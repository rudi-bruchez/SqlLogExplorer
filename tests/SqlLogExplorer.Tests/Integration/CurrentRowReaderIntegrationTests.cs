using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SqlLogExplorer.Backends;
using SqlLogExplorer.Data;
using SqlLogExplorer.Decoding;
using SqlLogExplorer.Models;
using SqlLogExplorer.Services;

namespace SqlLogExplorer.Tests.Integration;

/// <summary>Ancre 3 (spec03 §7.2) contre un vrai serveur : une base neuve par test, en FULL après une sauvegarde complète.</summary>
public class CurrentRowReaderIntegrationTests
{
    private const string FromCurrentRow = "rebuilt backwards from the current row";

    private sealed class Database : IAsyncDisposable
    {
        private readonly string _master;
        public string Name { get; }
        public string ConnectionString { get; }

        private Database(string master, string name)
        {
            _master = master;
            Name = name;
            ConnectionString = new SqlConnectionStringBuilder(master) { InitialCatalog = name }.ConnectionString;
        }

        public static async Task<Database> CreateAsync(bool adr = false)
        {
            var master = Environment.GetEnvironmentVariable("SQLLOGEXPLORER_TEST_SQL");
            Assert.False(string.IsNullOrEmpty(master), "Définir SQLLOGEXPLORER_TEST_SQL.");
            var db = new Database(master, "sle_t12_" + Guid.NewGuid().ToString("N"));
            await ExecAsync(master, $"CREATE DATABASE [{db.Name}];");
            await ExecAsync(master, $"ALTER DATABASE [{db.Name}] SET RECOVERY FULL;");
            if (adr) await ExecAsync(master, $"ALTER DATABASE [{db.Name}] SET ACCELERATED_DATABASE_RECOVERY = ON;");
            // Sans sauvegarde complète, une base FULL reste en troncature automatique.
            await ExecAsync(master, $"BACKUP DATABASE [{db.Name}] TO DISK = N'/var/opt/mssql/data/{db.Name}.bak' WITH INIT;");
            return db;
        }

        public Task ExecAsync(string sql) => ExecAsync(ConnectionString, sql);

        public async Task<object?> ScalarAsync(string sql)
        {
            await using var cn = new SqlConnection(ConnectionString);
            await cn.OpenAsync();
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = sql;
            return await cmd.ExecuteScalarAsync();
        }

        /// <summary>Le dernier LSN du journal, normalisé.</summary>
        public async Task<string> LastLsnAsync() => Lsn.Normalize((string)(await ScalarAsync("SELECT MAX([Current LSN]) FROM sys.fn_dblog(NULL, NULL);"))!);

        public async Task<long> AllocUnitAsync(string table) => Convert.ToInt64(await ScalarAsync(
            $"SELECT au.allocation_unit_id FROM sys.allocation_units au JOIN sys.partitions p ON p.partition_id = au.container_id WHERE p.object_id = OBJECT_ID('{table}') AND p.index_id IN (0, 1) AND au.type = 1;"));

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await ExecAsync(_master, $"ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}];");
        }

        private static async Task ExecAsync(string connectionString, string sql)
        {
            await using var cn = new SqlConnection(connectionString);
            await cn.OpenAsync();
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Un import qui commence après l'insert de la ligne (ni ancre 1 ni ancre 2 dans le cache), et le modify choisi.</summary>
    private sealed record Scenario(LogCache Cache, DecodingService Service, LogRecord Selected, TableSchema Schema) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Cache.DisposeAsync();
    }

    private static async Task<Scenario> ImportAfterInsertAsync(Database db, string table, string update)
    {
        var start = await db.LastLsnAsync();
        await db.ExecAsync(update);
        var end = await db.LastLsnAsync();

        var records = new List<LogRecord>();
        await foreach (var r in new LiveDatabaseBackend(new LsnRange(start, end)).ParseLogAsync([db.ConnectionString])) records.Add(r);
        var cache = await LogCache.CreateAsync(Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db"));
        await cache.InsertBatchAsync(records);

        var provider = new SchemaProvider(db.ConnectionString);
        var au = await db.AllocUnitAsync(table);
        var schema = (await provider.GetAsync(au))!;
        var selected = records.Last(r => r.AllocUnitId == au && r.Operation == "LOP_MODIFY_ROW" && !r.IsCompensation);
        var service = new DecodingService(cache, (id, ct) => provider.GetAsync(id, ct), isLive: true);
        return new Scenario(cache, service, selected, schema);
    }

    private static string Values(DecodedRow? row) =>
        row is null ? "<null>" : string.Join(", ", row.Columns.Select(c => $"{c.Name}={c.Value ?? "NULL"}"));

    private static Task<bool> Accept(IReadOnlyList<LogRecord> records, CancellationToken ct) => Task.FromResult(true);

    [IntegrationFact]
    public async Task UpdateAfterTheImport_IsCrossed()
    {
        await using var db = await Database.CreateAsync();
        await db.ExecAsync("CREATE TABLE dbo.t(id int NOT NULL PRIMARY KEY, a int NULL, b varchar(20) NULL); INSERT dbo.t VALUES (1, 10, 'abc');");
        await using var s = await ImportAfterInsertAsync(db, "dbo.t", "UPDATE dbo.t SET a = 11 WHERE id = 1;");

        var before = await s.Service.InspectAsync(s.Selected);
        Assert.Equal("no full image of this row available", before.ReplayStatus);
        Assert.True(before.CanRebuildFromCurrentRow);

        // Après l'import, une autre connexion modifie la ligne : la suite du journal doit la défaire.
        await db.ExecAsync("UPDATE dbo.t SET a = 12, b = 'zzzz' WHERE id = 1;");
        var x = await s.Service.RebuildFromCurrentRowAsync(s.Selected, new CurrentRowReader(db.ConnectionString));

        Assert.Equal(FromCurrentRow, x.ReplayStatus);
        Assert.Equal("id=1, a=10, b=abc", Values(x.Before));
        Assert.Equal("id=1, a=11, b=abc", Values(x.After));
    }

    [IntegrationFact]
    public async Task ADeleteSinceTheImport_GivesTheDeleteAnchor()
    {
        // La clé supprimée puis insérée de nouveau : la recherche trouve la nouvelle vie, la suite porte le delete de l'ancienne.
        await using var db = await Database.CreateAsync();
        await db.ExecAsync("CREATE TABLE dbo.t(id int NOT NULL PRIMARY KEY, a int NULL); INSERT dbo.t VALUES (1, 10);");
        await using var s = await ImportAfterInsertAsync(db, "dbo.t", "UPDATE dbo.t SET a = 11 WHERE id = 1;");
        await db.ExecAsync("DELETE dbo.t WHERE id = 1; INSERT dbo.t VALUES (1, 50);");

        var x = await s.Service.RebuildFromCurrentRowAsync(s.Selected, new CurrentRowReader(db.ConnectionString));

        Assert.StartsWith("rebuilt backwards from DELETE", x.ReplayStatus);
        Assert.Equal("id=1, a=10", Values(x.Before));
        Assert.Equal("id=1, a=11", Values(x.After));
    }

    [IntegrationFact]
    public async Task TheRowIsHeldFromTheSeek_WhileTheSuffixIsAccepted()
    {
        await using var db = await Database.CreateAsync();
        await db.ExecAsync("CREATE TABLE dbo.t(id int NOT NULL PRIMARY KEY, a int NULL); INSERT dbo.t VALUES (1, 10);");
        var schema = (await new SchemaProvider(db.ConnectionString).GetAsync(await db.AllocUnitAsync("dbo.t")))!;
        var id = schema.Columns.Single(c => c.Name == "id");
        var last = await db.LastLsnAsync();

        int? error = null;
        async Task<bool> TryToUpdate(IReadOnlyList<LogRecord> records, CancellationToken ct)
        {
            await using var cn = new SqlConnection(db.ConnectionString);
            await cn.OpenAsync(ct);
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = "SET LOCK_TIMEOUT 1000; UPDATE dbo.t SET a = 50 WHERE id = 1;";
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (SqlException e) { error = e.Number; }
            return true;
        }

        var result = await new CurrentRowReader(db.ConnectionString).ReadAsync(schema, [(id, 1)], last, TryToUpdate);

        Assert.Equal(1222, error);
        Assert.Null(result.Unavailable);
        Assert.Equal(10, RowDecoder.Decode(result.Row, schema).Columns.Single(c => c.Name == "a").Value);
    }

    [IntegrationFact]
    public async Task TheSuffixIsReadAfterTheSeek_SoAWriterTheSeekWaitedForIsInIt()
    {
        // Un écrivain tient la ligne et valide pendant que la recherche l'attend : sa validation doit être dans la suite.
        // Lue avant la recherche, la suite aurait déjà son modify (journalisé avant la validation) mais pas son LOP_COMMIT_XACT.
        await using var db = await Database.CreateAsync();
        await db.ExecAsync("CREATE TABLE dbo.t(id int NOT NULL PRIMARY KEY, a int NULL); INSERT dbo.t VALUES (1, 10);");
        var au = await db.AllocUnitAsync("dbo.t");
        var schema = (await new SchemaProvider(db.ConnectionString).GetAsync(au))!;
        var id = schema.Columns.Single(c => c.Name == "id");
        var last = await db.LastLsnAsync();

        await using var holder = new SqlConnection(db.ConnectionString);
        await holder.OpenAsync();
        var tx = (SqlTransaction)await holder.BeginTransactionAsync();
        short spid;
        await using (var cmd = holder.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE dbo.t SET a = 11 WHERE id = 1; SELECT CONVERT(smallint, @@SPID);";
            spid = (short)(await cmd.ExecuteScalarAsync())!;
        }
        // Valider seulement quand la recherche est vue bloquée par l'écrivain : ni trop tôt (test vide), ni après le délai de 5 s.
        var commit = Task.Run(async () =>
        {
            for (var i = 0; i < 400; i++)
            {
                if (Convert.ToInt32(await db.ScalarAsync($"SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = {spid};")) > 0)
                {
                    await tx.CommitAsync();
                    return true;
                }
                await Task.Delay(10);
            }
            await tx.CommitAsync();
            return false;
        });

        IReadOnlyList<LogRecord> suffix = [];
        var result = await new CurrentRowReader(db.ConnectionString).ReadAsync(schema, [(id, 1)], last,
            (r, ct) => { suffix = r; return Task.FromResult(true); });
        Assert.True(await commit, "la recherche n'a jamais été vue bloquée par l'écrivain");

        Assert.Null(result.Unavailable);
        var modify = Assert.Single(suffix, r => r.AllocUnitId == au && r.Operation == "LOP_MODIFY_ROW");
        Assert.Contains(suffix, r => r.Operation == "LOP_COMMIT_XACT" && r.TransactionId == modify.TransactionId);
        Assert.Equal(11, RowDecoder.Decode(result.Row, schema).Columns.Single(c => c.Name == "a").Value);
    }

    [IntegrationFact]
    public async Task AnOpenUpdateOfTheRow_ReturnsRowLocked()
    {
        await using var db = await Database.CreateAsync();
        await db.ExecAsync("CREATE TABLE dbo.t(id int NOT NULL PRIMARY KEY, a int NULL); INSERT dbo.t VALUES (1, 10);");
        var schema = (await new SchemaProvider(db.ConnectionString).GetAsync(await db.AllocUnitAsync("dbo.t")))!;
        var id = schema.Columns.Single(c => c.Name == "id");
        var last = await db.LastLsnAsync();

        await using var holder = new SqlConnection(db.ConnectionString);
        await holder.OpenAsync();
        await using var tx = (SqlTransaction)await holder.BeginTransactionAsync();
        await using (var cmd = holder.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE dbo.t SET a = 11 WHERE id = 1;";
            await cmd.ExecuteNonQueryAsync();
        }

        var watch = Stopwatch.StartNew();
        var result = await new CurrentRowReader(db.ConnectionString).ReadAsync(schema, [(id, 1)], last, Accept);
        watch.Stop();
        await tx.RollbackAsync();

        Assert.Equal("row locked by an open transaction", result.Unavailable);
        Assert.Null(result.Row);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{watch.Elapsed}");
    }

    [IntegrationFact]
    public async Task ALogTruncatedSinceTheImport_IsReported()
    {
        await using var db = await Database.CreateAsync();
        await db.ExecAsync("CREATE TABLE dbo.t(id int NOT NULL PRIMARY KEY, a int NULL); INSERT dbo.t VALUES (1, 10); CREATE TABLE dbo.f(id int IDENTITY PRIMARY KEY, pad char(2000) NOT NULL);");
        var schema = (await new SchemaProvider(db.ConnectionString).GetAsync(await db.AllocUnitAsync("dbo.t")))!;
        var id = schema.Columns.Single(c => c.Name == "id");
        var last = await db.LastLsnAsync();

        // Produire du journal jusqu'à ce que le LSN du cache soit vraiment sorti du journal actif.
        var truncated = false;
        for (var round = 0; round < 20 && !truncated; round++)
        {
            await db.ExecAsync("INSERT dbo.f(pad) SELECT TOP (1000) 'x' FROM sys.all_objects a CROSS JOIN sys.all_objects b;");
            await db.ExecAsync($"BACKUP LOG [{db.Name}] TO DISK = N'/var/opt/mssql/data/{db.Name}_{round}.trn' WITH INIT; CHECKPOINT;");
            var min = Lsn.Normalize((string)(await db.ScalarAsync("SELECT MIN([Current LSN]) FROM sys.fn_dblog(NULL, NULL);"))!);
            truncated = string.CompareOrdinal(min, last) > 0;
        }
        Assert.True(truncated, "le journal n'a pas été tronqué en 20 tours");

        var called = false;
        var result = await new CurrentRowReader(db.ConnectionString).ReadAsync(schema, [(id, 1)], last,
            (r, ct) => { called = true; return Task.FromResult(true); });

        Assert.Equal("log truncated since the import, re-import to use the current row", result.Unavailable);
        Assert.Null(result.Row);
        Assert.False(called);
    }

    [IntegrationFact]
    public async Task AnAdrDatabase_IsNotAvailable()
    {
        await using var adr = await Database.CreateAsync(adr: true);
        Assert.False(await new CurrentRowReader(adr.ConnectionString).IsAvailableForDatabaseAsync());

        await adr.ExecAsync("CREATE TABLE dbo.t(id int NOT NULL PRIMARY KEY, a int NULL); INSERT dbo.t VALUES (1, 10);");
        await using var s = await ImportAfterInsertAsync(adr, "dbo.t", "UPDATE dbo.t SET a = 11 WHERE id = 1;");
        var x = await s.Service.RebuildFromCurrentRowAsync(s.Selected, new CurrentRowReader(adr.ConnectionString));
        Assert.Equal("not available on a database with Accelerated Database Recovery", x.ReplayStatus);
        Assert.Null(x.Before);

        await using var plain = await Database.CreateAsync();
        Assert.True(await new CurrentRowReader(plain.ConnectionString).IsAvailableForDatabaseAsync());
    }

    [IntegrationFact]
    public async Task ANullableKeyHoldingNull_IsFound()
    {
        await using var db = await Database.CreateAsync();
        await db.ExecAsync("CREATE TABLE dbo.n(k int NULL, v int NOT NULL); CREATE UNIQUE CLUSTERED INDEX cx ON dbo.n(k); INSERT dbo.n VALUES (NULL, 1), (3, 1);");
        await using var s = await ImportAfterInsertAsync(db, "dbo.n", "UPDATE dbo.n SET v = 2 WHERE k IS NULL;");

        var x = await s.Service.RebuildFromCurrentRowAsync(s.Selected, new CurrentRowReader(db.ConnectionString));

        Assert.Equal(FromCurrentRow, x.ReplayStatus);
        Assert.Equal("k=NULL, v=1", Values(x.Before));
        Assert.Equal("k=NULL, v=2", Values(x.After));
    }

    [IntegrationFact]
    public async Task ANonUniqueKey_KeepsTheSlotOfItsUniqueifier()
    {
        await using var db = await Database.CreateAsync();
        await db.ExecAsync("CREATE TABLE dbo.d(k int NOT NULL, v int NOT NULL); CREATE CLUSTERED INDEX cx ON dbo.d(k); INSERT dbo.d VALUES (5, 1), (5, 2), (5, 3);");
        await using var s = await ImportAfterInsertAsync(db, "dbo.d", "UPDATE dbo.d SET v = 12 WHERE v = 2;");

        var x = await s.Service.RebuildFromCurrentRowAsync(s.Selected, new CurrentRowReader(db.ConnectionString));

        Assert.Equal(FromCurrentRow, x.ReplayStatus);
        Assert.Equal(2, x.Before!.Columns.Single(c => c.Name == "v").Value);
        Assert.Equal(12, x.After!.Columns.Single(c => c.Name == "v").Value);
        Assert.Equal(1, x.After.Columns.Single(c => c.Name == "uniqueifier").Value);
    }

    [IntegrationFact]
    public async Task ATableOutsideDbo_IsFound()
    {
        await using var db = await Database.CreateAsync();
        await db.ExecAsync("CREATE SCHEMA s;");
        await db.ExecAsync("CREATE TABLE s.t(id int NOT NULL PRIMARY KEY, a int NULL); INSERT s.t VALUES (1, 10);");
        await using var s = await ImportAfterInsertAsync(db, "s.t", "UPDATE s.t SET a = 11 WHERE id = 1;");

        var x = await s.Service.RebuildFromCurrentRowAsync(s.Selected, new CurrentRowReader(db.ConnectionString));

        Assert.Equal(FromCurrentRow, x.ReplayStatus);
        Assert.Equal("id=1, a=10", Values(x.Before));
        Assert.Equal("id=1, a=11", Values(x.After));
    }

    [IntegrationFact]
    public async Task NamesThatNeedQuoting_AreFound()
    {
        // Un crochet fermant dans le schéma, un point dans la table : sans citation de chaque nom, la requête vise un autre objet.
        await using var db = await Database.CreateAsync();
        await db.ExecAsync("CREATE SCHEMA [o]]s];");
        await db.ExecAsync("CREATE TABLE [o]]s].[t.y](id int NOT NULL PRIMARY KEY, a int NULL); INSERT [o]]s].[t.y] VALUES (1, 10);");
        await using var s = await ImportAfterInsertAsync(db, "[o]]s].[t.y]", "UPDATE [o]]s].[t.y] SET a = 11 WHERE id = 1;");

        var x = await s.Service.RebuildFromCurrentRowAsync(s.Selected, new CurrentRowReader(db.ConnectionString));

        Assert.Equal(FromCurrentRow, x.ReplayStatus);
        Assert.Equal("id=1, a=10", Values(x.Before));
    }

    [IntegrationFact]
    public async Task ThePoolDoesNotInheritSerializable()
    {
        // Un nom d'application propre au test : le pool de cette chaîne ne contient que ce que la lecture y aurait rendu.
        await using var db = await Database.CreateAsync();
        await db.ExecAsync("CREATE TABLE dbo.t(id int NOT NULL PRIMARY KEY, a int NULL); INSERT dbo.t VALUES (1, 10);");
        var cs = new SqlConnectionStringBuilder(db.ConnectionString) { ApplicationName = "sle-pool-" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var schema = (await new SchemaProvider(cs).GetAsync(await db.AllocUnitAsync("dbo.t")))!;
        var id = schema.Columns.Single(c => c.Name == "id");

        var result = await new CurrentRowReader(cs).ReadAsync(schema, [(id, 1)], await db.LastLsnAsync(), Accept);
        Assert.Null(result.Unavailable);

        await using var next = new SqlConnection(cs);
        await next.OpenAsync();
        await using var cmd = next.CreateCommand();
        cmd.CommandText = "SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID;";
        Assert.Equal(2, Convert.ToInt32(await cmd.ExecuteScalarAsync()));   // READ COMMITTED
    }

    [IntegrationFact]
    public async Task AVarcharKeyOutsideTheDatabaseCodePage_IsNotSought()
    {
        // Base en SQL_Latin1_General_CP1_CI_AS (1252) : une clé grecque ou UTF-8 deviendrait « ??? » dans un paramètre varchar.
        await using var db = await Database.CreateAsync();
        await db.ExecAsync("""
            CREATE TABLE dbo.g(k varchar(10) COLLATE Greek_CI_AS NOT NULL PRIMARY KEY, a int NULL);
            CREATE TABLE dbo.u8(k varchar(10) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL PRIMARY KEY, a int NULL);
            CREATE TABLE dbo.ga(k varchar(10) COLLATE Greek_CI_AS NOT NULL PRIMARY KEY, a int NULL);
            INSERT dbo.g VALUES (N'αβγ', 10), ('???', 10);
            INSERT dbo.u8 VALUES (N'жё', 10);
            INSERT dbo.ga VALUES ('abc', 10);
            """);

        foreach (var (table, update) in new[] { ("dbo.g", "UPDATE dbo.g SET a = 11 WHERE k = N'αβγ';"), ("dbo.u8", "UPDATE dbo.u8 SET a = 11 WHERE k = N'жё';") })
        {
            await using var s = await ImportAfterInsertAsync(db, table, update);
            var x = await s.Service.RebuildFromCurrentRowAsync(s.Selected, new CurrentRowReader(db.ConnectionString));
            Assert.Equal("key value cannot be sent in the database code page", x.ReplayStatus);
            Assert.Null(x.Before);
        }

        // Témoin : une clé ASCII d'une colonne grecque passe par la page de la base et se retrouve.
        await using var ok = await ImportAfterInsertAsync(db, "dbo.ga", "UPDATE dbo.ga SET a = 11 WHERE k = 'abc';");
        var y = await ok.Service.RebuildFromCurrentRowAsync(ok.Selected, new CurrentRowReader(db.ConnectionString));
        Assert.Equal(FromCurrentRow, y.ReplayStatus);
        Assert.Equal("k=abc, a=10", Values(y.Before));
    }
}
