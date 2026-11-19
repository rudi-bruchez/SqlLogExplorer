using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlLogExplorer.Backends;
using SqlLogExplorer.Data;
using SqlLogExplorer.Models;
using SqlLogExplorer.Services;
using Xunit;
using Xunit.Abstractions;

namespace SqlLogExplorer.Tests.Integration;

/// <summary>Chaque instruction de maintenance d'index, ses opérations dans le rapport, en direct et depuis une sauvegarde du journal (spec04 §8).</summary>
public class IndexMaintenanceIntegrationTests(ITestOutputHelper output)
{
    private const string DataDir = "/var/opt/mssql/data";

    /// <summary>Une instruction : ses deux LSN (bruts, tels que fn_dblog les rend), les unités IN_ROW_DATA lues après elle et sa référence.</summary>
    private sealed record Statement(string Name, string Before, string After, IReadOnlyList<long> Units, long Reference);

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var cn = new SqlConnection(connectionString);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqlConnection cn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task<string> MaxLsnAsync(SqlConnection cn)
        => (string)(await ScalarAsync(cn, "SELECT MAX([Current LSN]) FROM sys.fn_dblog(NULL, NULL);"))!;

    /// <summary>Les unités IN_ROW_DATA des index d'une table (tous, ou celui nommé).</summary>
    private static async Task<IReadOnlyList<long>> UnitsAsync(SqlConnection cn, string table, string? index = null)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT au.allocation_unit_id
            FROM sys.allocation_units AS au
            JOIN sys.partitions AS p ON au.container_id = p.hobt_id
            JOIN sys.indexes AS i ON i.object_id = p.object_id AND i.index_id = p.index_id
            WHERE au.type_desc = N'IN_ROW_DATA' AND p.object_id = OBJECT_ID(@table) AND (@index IS NULL OR i.name = @index);
            """;
        cmd.Parameters.AddWithValue("@table", table);
        cmd.Parameters.AddWithValue("@index", (object?)index ?? DBNull.Value);
        var list = new List<long>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetInt64(0));
        return list;
    }

    /// <summary>Référence : octets des transactions dont le LOP_BEGIN_XACT a la session, entre les deux LSN, plus les enregistrements
    /// hors transaction compris entre le premier et le dernier LSN de ces transactions. Bornes en comparaison de texte, jamais
    /// fn_dblog(@before, @after) (Msg 9005, spec03 §2).</summary>
    private static async Task<long> ReferenceAsync(SqlConnection cn, string before, string after, int spid)
        => Convert.ToInt64(await ScalarAsync(cn, """
            SELECT [Current LSN] AS lsn, [Transaction ID] AS tid, [Operation] AS op, [SPID] AS spid, [Log Record Length] AS len
            INTO #w FROM sys.fn_dblog(NULL, NULL) WHERE [Current LSN] > @before AND [Current LSN] <= @after;
            SELECT * INTO #s FROM #w WHERE tid IN (SELECT tid FROM #w WHERE op = 'LOP_BEGIN_XACT' AND spid = @spid);
            -- Hors transaction : seulement dans l'étendue des transactions de la session. Le nettoyage des enregistrements
            -- fantômes laissés par le DML d'une autre session (LOP_EXPUNGE_ROWS) tombe dans la fenêtre avant l'instruction ;
            -- il appartient à ce DML, et spec04 §4 ne le donne à aucune opération.
            SELECT COALESCE((SELECT SUM(CAST(len AS bigint)) FROM #s), 0)
                 + COALESCE((SELECT SUM(CAST(len AS bigint)) FROM #w WHERE tid = '0000:00000000'
                             AND lsn >= (SELECT MIN(lsn) FROM #s) AND lsn <= (SELECT MAX(lsn) FROM #s)), 0);
            DROP TABLE #s;
            DROP TABLE #w;
            """, ("@before", before), ("@after", after), ("@spid", spid)), CultureInfo.InvariantCulture);

    private static async Task<MaintenanceReport> AnalyzeAsync(IAsyncEnumerable<LogRecord> records, string cachePath)
    {
        await using var cache = await LogCache.CreateAsync(cachePath);
        var batch = new List<LogRecord>(5000);
        await foreach (var r in records)
        {
            batch.Add(r);
            if (batch.Count == 5000)
            {
                await cache.InsertBatchAsync(batch);
                batch.Clear();
            }
        }
        await cache.InsertBatchAsync(batch);
        return await IndexMaintenance.AnalyzeAsync(new LogQuery(cache.Connection));
    }

    private static async IAsyncEnumerable<LogRecord> DumpAsync(string master, string trn)
    {
        await using var cn = new SqlConnection(master);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = FnDumpDblogQuery.Build("@path");
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("@path", trn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) yield return LogColumns.Read(r);
    }

    private static bool Between(string lsn, string before, string after)
    {
        var l = Lsn.Normalize(lsn);
        return string.CompareOrdinal(l, Lsn.Normalize(before)) > 0 && string.CompareOrdinal(l, Lsn.Normalize(after)) <= 0;
    }

    private void Check(string import, MaintenanceReport report, IReadOnlyList<Statement> statements, int spid, int otherSpid)
    {
        output.WriteLine($"[{import}] {report.Operations.Count} operations, total {report.TotalBytes} B, maintenance {report.MaintenanceBytes} B");
        var expected = new Dictionary<string, (int Count, MaintenanceKind? Kind)>(StringComparer.Ordinal)
        {
            ["S1"] = (2, MaintenanceKind.Reorganize),
            ["S2"] = (2, MaintenanceKind.Reorganize),
            ["S3"] = (0, null),
            ["S4"] = (1, MaintenanceKind.RebuildOffline),
            ["S5"] = (1, MaintenanceKind.RebuildOnline),
        };

        var failures = new List<string>();
        foreach (var s in statements)
        {
            var ops = report.Operations.Where(o => Between(o.FirstLsn, s.Before, s.After)).ToList();
            var bytes = ops.Sum(o => o.Bytes);
            var ratio = s.Reference == 0 ? double.NaN : 100.0 * bytes / s.Reference;
            var firsts = ops.Where(o => o.Objects.Count > 0).Select(o => o.Objects[0].AllocUnitId).ToHashSet();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[{import}] {s.Name}: {ops.Count} op(s) [{string.Join(", ", ops.Select(o => $"{o.Kind} spid {o.Spid} {o.Bytes} B first {(o.Objects.Count > 0 ? o.Objects[0].Display : "-")}"))}], " +
                $"operations {bytes} B, reference {s.Reference} B, ratio {ratio:F2} %, units read after [{string.Join(", ", s.Units)}]"));

            var (count, kind) = expected[s.Name];
            if (ops.Count != count) failures.Add($"{import} {s.Name}: {ops.Count} operations, {count} expected");
            if (kind is MaintenanceKind k && ops.Any(o => o.Kind != k)) failures.Add($"{import} {s.Name}: a kind other than {k}");
            if (ops.Count > 0 && !(ratio >= 95.0 && ratio <= 101.0))
                failures.Add(string.Create(CultureInfo.InvariantCulture, $"{import} {s.Name}: ratio {ratio:F2} % outside 95..101 %"));

            var units = s.Units.ToHashSet();
            switch (s.Name)
            {
                case "S1" or "S2" when !firsts.SetEquals(units):
                    failures.Add($"{import} {s.Name}: first objects [{string.Join(", ", firsts)}] differ from [{string.Join(", ", units)}]");
                    break;
                // Une opération sans objet ne doit pas passer le contrôle d'inclusion à vide.
                case "S5" when ops.Any(o => o.Objects.Count == 0) || !firsts.IsSubsetOf(units):
                    failures.Add($"{import} {s.Name}: first objects [{string.Join(", ", firsts)}] of {ops.Count} operation(s) not all in [{string.Join(", ", units)}]");
                    break;
                case "S4" when ops.Count == 1 && !units.IsSubsetOf(ops[0].Objects.Select(o => o.AllocUnitId)):
                    failures.Add($"{import} {s.Name}: objects [{string.Join(", ", ops[0].Objects.Select(o => o.AllocUnitId))}] lack one of [{string.Join(", ", units)}]");
                    break;
            }
        }

        var outside = report.Operations.Count(o => !statements.Any(s => Between(o.FirstLsn, s.Before, s.After)));
        output.WriteLine($"[{import}] operations outside the statements: {outside}");
        if (report.Operations.Any(o => o.Spid == otherSpid)) failures.Add($"{import}: an operation has the second connection's SPID {otherSpid}");
        if (report.Operations.Any(o => o.Spid != spid)) output.WriteLine($"[{import}] note: an operation has a SPID other than {spid}");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [IntegrationFact]
    public async Task Operations_MatchEachStatement_LiveAndFromBackup()
    {
        var master = Environment.GetEnvironmentVariable("SQLLOGEXPLORER_TEST_SQL");
        Assert.False(string.IsNullOrEmpty(master), "Définir SQLLOGEXPLORER_TEST_SQL (voir tests/fixtures/sqlserver.sh).");

        var db = $"sle_im_{Guid.NewGuid():N}";
        var dbConn = new SqlConnectionStringBuilder(master!) { InitialCatalog = db, Pooling = false }.ConnectionString;
        var bak = $"{DataDir}/{db}.bak";
        var trn = $"{DataDir}/{db}.trn";
        var liveCache = Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db");
        var dumpCache = Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db");
        var cutCache = Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db");
        await ExecAsync(master!, $"CREATE DATABASE [{db}];");
        try
        {
            await ExecAsync(master!, $"ALTER DATABASE [{db}] SET RECOVERY FULL; BACKUP DATABASE [{db}] TO DISK = N'{bak}' WITH INIT;");

            // Clés id et n aléatoires, insérées par petits lots : un n constant ou séquentiel laisse IX_n sous 10 % (mesuré).
            const string fill = """
                DECLARE @i int = 0;
                WHILE @i < 100
                BEGIN
                    INSERT dbo.{0} (n) SELECT TOP (200) ABS(CHECKSUM(NEWID())) FROM sys.all_objects;
                    SET @i += 1;
                END;
                """;
            await ExecAsync(dbConn, """
                CREATE TABLE dbo.T (id uniqueidentifier DEFAULT NEWID() PRIMARY KEY, n int, pad char(400) DEFAULT 'x');
                CREATE INDEX IX_n ON dbo.T (n);
                CREATE TABLE dbo.A (id uniqueidentifier DEFAULT NEWID() PRIMARY KEY, n int, pad char(400) DEFAULT 'x');
                CREATE TABLE dbo.B (id uniqueidentifier DEFAULT NEWID() PRIMARY KEY, n int, pad char(400) DEFAULT 'x');
                """);
            foreach (var table in new[] { "T", "A", "B" })
                await ExecAsync(dbConn, string.Format(CultureInfo.InvariantCulture, fill, table));

            await using var cn = new SqlConnection(dbConn);
            await cn.OpenAsync();
            var spid = Convert.ToInt32(await ScalarAsync(cn, "SELECT @@SPID;"), CultureInfo.InvariantCulture);

            await using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT OBJECT_NAME(s.object_id) + '.' + i.name, s.avg_fragmentation_in_percent
                    FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') AS s
                    JOIN sys.indexes AS i ON i.object_id = s.object_id AND i.index_id = s.index_id
                    WHERE s.alloc_unit_type_desc = N'IN_ROW_DATA' AND s.object_id IN (OBJECT_ID('dbo.T'), OBJECT_ID('dbo.A'), OBJECT_ID('dbo.B'));
                    """;
                var fragmentation = new List<(string Index, double Percent)>();
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) fragmentation.Add((r.GetString(0), r.GetDouble(1)));
                foreach (var (index, percent) in fragmentation)
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"fragmentation {index}: {percent:F1} %"));
                Assert.Equal(4, fragmentation.Count);
                Assert.All(fragmentation, f => Assert.True(f.Percent > 30, $"{f.Index} {f.Percent} %"));
            }

            var statements = new List<Statement>();
            async Task RunAsync(string name, string sql, Func<Task<IReadOnlyList<long>>> units, Func<Task>? during = null)
            {
                var before = await MaxLsnAsync(cn);
                await using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 0;
                    await cmd.ExecuteNonQueryAsync();
                }
                if (during is not null) await during();
                var after = await MaxLsnAsync(cn);
                statements.Add(new Statement(name, before, after, await units(), 0));
            }

            await RunAsync("S1", "ALTER INDEX ALL ON dbo.T REORGANIZE;", () => UnitsAsync(cn, "dbo.T"));
            await RunAsync("S2", "ALTER INDEX ALL ON dbo.A REORGANIZE; ALTER INDEX ALL ON dbo.B REORGANIZE;",
                async () => [.. await UnitsAsync(cn, "dbo.A"), .. await UnitsAsync(cn, "dbo.B")]);
            await RunAsync("S3", "ALTER INDEX IX_n ON dbo.T SET (ALLOW_PAGE_LOCKS = ON);", () => UnitsAsync(cn, "dbo.T", "IX_n"));
            await RunAsync("S4", "ALTER INDEX ALL ON dbo.T REBUILD;", () => UnitsAsync(cn, "dbo.T"));

            // S5 : une seconde connexion met à jour dbo.T (donc IX_n) en boucle tant que la reconstruction en ligne tourne.
            var stop = new CancellationTokenSource();
            var started = new TaskCompletionSource();
            var otherSpid = 0;
            var updates = 0;
            var updater = Task.Run(async () =>
            {
                await using var other = new SqlConnection(dbConn);
                await other.OpenAsync();
                otherSpid = Convert.ToInt32(await ScalarAsync(other, "SELECT @@SPID;"), CultureInfo.InvariantCulture);
                while (!stop.IsCancellationRequested)
                {
                    await ScalarAsync(other, "UPDATE TOP (50) dbo.T SET n = n + 1;");
                    updates++;
                    started.TrySetResult();
                }
            });
            try
            {
                await Task.WhenAny(started.Task, updater);
                await RunAsync("S5", "ALTER INDEX IX_n ON dbo.T REBUILD WITH (ONLINE = ON);", () => UnitsAsync(cn, "dbo.T", "IX_n"),
                    async () =>
                    {
                        stop.Cancel();
                        await updater;
                    });
            }
            finally
            {
                stop.Cancel();
                // Observée ici aussi quand S5 échoue : l'erreur de la boucle est imprimée sans masquer celle du test.
                try { await updater; }
                catch (Exception e) { output.WriteLine($"updater: {e.Message}"); }
            }
            var s5 = statements[^1];
            var interleaved = Convert.ToInt32(await ScalarAsync(cn, """
                SELECT COUNT(*) FROM sys.fn_dblog(NULL, NULL)
                WHERE [Current LSN] > @before AND [Current LSN] <= @after AND [Operation] = 'LOP_BEGIN_XACT' AND [SPID] = @other;
                """, ("@before", s5.Before), ("@after", s5.After), ("@other", otherSpid)), CultureInfo.InvariantCulture);
            output.WriteLine($"session {spid}, second connection {otherSpid}, {updates} updates, {interleaved} of its transactions begin in S5");
            Assert.NotEqual(spid, otherSpid);
            Assert.True(updates > 0);
            Assert.True(interleaved > 0, "No transaction of the second connection begins inside S5's window.");

            for (var i = 0; i < statements.Count; i++)
                statements[i] = statements[i] with { Reference = await ReferenceAsync(cn, statements[i].Before, statements[i].After, spid) };

            var live = await AnalyzeAsync(new LiveDatabaseBackend().ParseLogAsync(new[] { dbConn }), liveCache);
            Check("live", live, statements, spid, otherSpid);

            // Avant la sauvegarde du journal, qui le tronque : un import en direct coupé pendant la reconstruction en ligne.
            await CheckCutRebuildAsync(cn, dbConn, s5, spid, cutCache);

            await ExecAsync(master!, $"BACKUP LOG [{db}] TO DISK = N'{trn}' WITH INIT;");
            var dumped = await AnalyzeAsync(DumpAsync(master!, trn), dumpCache);
            Check("backup", dumped, statements, spid, otherSpid);
        }
        finally
        {
            try { await ExecAsync(master!, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}];"); }
            catch (SqlException e) { output.WriteLine($"cleanup {db}: {e.Message}"); }
            foreach (var path in new[] { bak, trn })
            {
                try { await ExecAsync(master!, $"EXEC master.sys.xp_delete_file 0, N'{path}';"); }
                catch (SqlException e) { output.WriteLine($"cleanup {path}: {e.Message}"); }
            }
            SqliteCleanup(liveCache);
            SqliteCleanup(dumpCache);
            SqliteCleanup(cutCache);
        }
    }

    private static async Task<IReadOnlyList<string>> LsnsAsync(SqlConnection cn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>Import en direct arrêté au début d'une OnlineIndexInsertTxn de la session vers le milieu de S5 : l'ALTER INDEX n'a
    /// pas de validation dans l'import, son étendue va jusqu'à la dernière famille de la session (spec04 §4, règle 1).</summary>
    private async Task CheckCutRebuildAsync(SqlConnection cn, string dbConn, Statement s5, int spid, string cachePath)
    {
        var inserts = await LsnsAsync(cn, """
            SELECT [Current LSN] FROM sys.fn_dblog(NULL, NULL)
            WHERE [Current LSN] > @before AND [Current LSN] <= @after AND [Operation] = 'LOP_BEGIN_XACT'
              AND [Transaction Name] = 'OnlineIndexInsertTxn' AND [SPID] = @spid
            ORDER BY [Current LSN];
            """, ("@before", s5.Before), ("@after", s5.After), ("@spid", spid));
        var alter = await LsnsAsync(cn, """
            SELECT l.[Current LSN] FROM sys.fn_dblog(NULL, NULL) AS l
            WHERE l.[Transaction ID] IN (SELECT b.[Transaction ID] FROM sys.fn_dblog(NULL, NULL) AS b
                                         WHERE b.[Current LSN] > @before AND b.[Current LSN] <= @after
                                           AND b.[Operation] = 'LOP_BEGIN_XACT' AND b.[Transaction Name] = 'ALTER INDEX'
                                           AND b.[SPID] = @spid)
            ORDER BY l.[Current LSN];
            """, ("@before", s5.Before), ("@after", s5.After), ("@spid", spid));
        var start = await LsnsAsync(cn, "SELECT MIN([Current LSN]) FROM sys.fn_dblog(NULL, NULL);");
        Assert.NotEmpty(inserts);
        Assert.NotEmpty(alter);

        var cut = inserts[inserts.Count / 2];
        // Le dernier enregistrement de l'ALTER INDEX dans l'import coupé, et les OnlineIndexInsertTxn qui commencent après lui :
        // celles que l'ancienne étendue (jusqu'au dernier LSN de l'ALTER INDEX) laissait sans opération.
        // Bruts pour T-SQL, normalisés pour les comparaisons ordinales.
        var cutKey = Lsn.Normalize(cut);
        var alterLast = alter.Select(Lsn.Normalize).Where(l => string.CompareOrdinal(l, cutKey) < 0).Max(StringComparer.Ordinal)!;
        var afterAlter = inserts.Select(Lsn.Normalize).Count(l => string.CompareOrdinal(l, alterLast) > 0 && string.CompareOrdinal(l, cutKey) <= 0);
        var reference = await ReferenceAsync(cn, s5.Before, cut, spid);

        var report = await AnalyzeAsync(new LiveDatabaseBackend(new LsnRange(start[0], cut)).ParseLogAsync(new[] { dbConn }), cachePath);
        var ops = report.Operations.Where(o => Between(o.FirstLsn, s5.Before, cut)).ToList();
        var bytes = ops.Sum(o => o.Bytes);
        var ratio = reference == 0 ? double.NaN : 100.0 * bytes / reference;
        // Part des octets dans les transactions qui commencent après le dernier enregistrement de l'ALTER INDEX : sans elle,
        // la coupe ne distinguerait pas l'étendue amendée de l'ancienne.
        var late = ops.SelectMany(o => o.Transactions).Where(t => string.CompareOrdinal(t.FirstLsn, alterLast) > 0).Sum(t => t.Bytes);
        var lateShare = bytes == 0 ? double.NaN : 100.0 * late / bytes;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[cut] S5 cut at {cut} ({inserts.Count / 2 + 1} of {inserts.Count} OnlineIndexInsertTxn), ALTER INDEX last record before the cut {alterLast} " +
            $"({alter.Count} records in all), {afterAlter} OnlineIndexInsertTxn begin after it, holding {late} B ({lateShare:F2} % of the operation)"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[cut] S5: {ops.Count} op(s) [{string.Join(", ", ops.Select(o => $"{o.Kind} spid {o.Spid} {o.Bytes} B {o.Transactions.Count} tx partial {o.Partial}"))}], " +
            $"operations {bytes} B, reference {reference} B, ratio {ratio:F2} %"));

        Assert.True(afterAlter > 0, "No OnlineIndexInsertTxn begins after the ALTER INDEX's last record before the cut.");
        Assert.True(lateShare > 50, "The cut leaves too few bytes after the ALTER INDEX's last record to tell the spans apart.");
        var op = Assert.Single(ops);
        Assert.Equal(MaintenanceKind.RebuildOnline, op.Kind);
        Assert.True(op.Partial);
        Assert.InRange(ratio, 95.0, 101.0);
    }

    private static void SqliteCleanup(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(path)) File.Delete(path);
    }
}
