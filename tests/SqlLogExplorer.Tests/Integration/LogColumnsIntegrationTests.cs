using Microsoft.Data.SqlClient;
using SqlLogExplorer.Backends;
using SqlLogExplorer.Models;
using Xunit;

namespace SqlLogExplorer.Tests.Integration;

public class LogColumnsIntegrationTests
{
    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var cn = new SqlConnection(connectionString);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<LogRecord>> ReadAsync(LiveDatabaseBackend backend, string connectionString)
    {
        var list = new List<LogRecord>();
        await foreach (var r in backend.ParseLogAsync(new[] { connectionString })) list.Add(r);
        return list;
    }

    private sealed record Plain(long? AllocUnitId, long? PartitionId, string? PageId, int? SlotId, int? OffsetInRow,
        int? ModifySize, int? NumElements, string? Description, string? TransactionName,
        byte[]? Rlc0, byte[]? Rlc1, byte[]? Rlc2, byte[]? Rlc3, byte[]? Rlc4, byte[]? Rlc5, string Operation, byte[]? LogRecord, string? BeginTime, string? EndTime);

    /// <summary>Relecture indépendante, colonnes lues par nom, pour épingler le mappage de LogColumns.Read.</summary>
    private static async Task<Dictionary<string, Plain>> ReadPlainAsync(string connectionString)
    {
        var map = new Dictionary<string, Plain>();
        await using var cn = new SqlConnection(connectionString);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT [Current LSN], [AllocUnitId], [PartitionId], [Page ID], [Slot ID], [Offset in Row], [Modify Size],
                   [Num Elements], [Description], [Transaction Name],
                   [RowLog Contents 0], [RowLog Contents 1], [RowLog Contents 2], [RowLog Contents 3],
                   [RowLog Contents 4], [RowLog Contents 5], [Operation], [Log Record], [Begin Time], [End Time]
            FROM sys.fn_dblog(NULL, NULL);
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        long? L(string n) => r[n] is DBNull ? null : Convert.ToInt64(r[n]);
        int? I(string n) => r[n] is DBNull ? null : Convert.ToInt32(r[n]);
        string? T(string n) => r[n] is DBNull ? null : (string)r[n];
        byte[]? B(string n) => r[n] is DBNull ? null : (byte[])r[n];
        while (await r.ReadAsync())
            map[Lsn.Normalize((string)r["Current LSN"])] = new Plain(L("AllocUnitId"), L("PartitionId"), T("Page ID"),
                I("Slot ID"), I("Offset in Row"), I("Modify Size"), I("Num Elements"), T("Description"), T("Transaction Name"),
                B("RowLog Contents 0"), B("RowLog Contents 1"), B("RowLog Contents 2"), B("RowLog Contents 3"),
                B("RowLog Contents 4"), B("RowLog Contents 5"), T("Operation")!, B("Log Record"), T("Begin Time"), T("End Time"));
        return map;
    }

    private static void AssertMatchesPlain(IReadOnlyList<LogRecord> records, Dictionary<string, Plain> plain)
    {
        static void Bytes(byte[]? e, byte[]? a, string what)
        {
            if (e is null) Assert.True(a is null, what);
            else Assert.True(a is not null && e.AsSpan().SequenceEqual(a), what);
        }
        foreach (var r in records)
        {
            var p = plain[r.Lsn];
            var w = r.Lsn;
            Assert.True(p.AllocUnitId == r.AllocUnitId, $"{w} AllocUnitId");
            Assert.True(p.PartitionId == r.PartitionId, $"{w} PartitionId");
            Assert.True(p.PageId == r.PageId, $"{w} PageId");
            Assert.True(p.SlotId == r.SlotId, $"{w} SlotId");
            Assert.True(p.OffsetInRow == r.OffsetInRow, $"{w} OffsetInRow");
            Assert.True(p.ModifySize == r.ModifySize, $"{w} ModifySize");
            Assert.True(p.NumElements == r.NumElements, $"{w} NumElements");
            Assert.True(p.Description == r.Description, $"{w} Description");
            Assert.True(p.TransactionName == r.TransactionName, $"{w} TransactionName");
            Assert.True(p.BeginTime == r.BeginTime, $"{w} BeginTime");
            Assert.True(p.EndTime == r.EndTime, $"{w} EndTime");
            Bytes(p.Rlc0, r.RowLogContents0, $"{w} RLC0"); Bytes(p.Rlc1, r.RowLogContents1, $"{w} RLC1");
            Bytes(p.Rlc2, r.RowLogContents2, $"{w} RLC2"); Bytes(p.Rlc3, r.RowLogContents3, $"{w} RLC3");
            Bytes(p.Rlc4, r.RowLogContents4, $"{w} RLC4"); Bytes(p.Rlc5, r.RowLogContents5, $"{w} RLC5");
            // Le Log Record entier n'est gardé que pour LOP_MODIFY_COLUMNS (spec03 §3).
            if (r.Operation == "LOP_MODIFY_COLUMNS")
            {
                Assert.NotNull(p.LogRecord);
                Bytes(p.LogRecord, r.LogRecordBytes, $"{w} LogRecord");
            }
            else Assert.True(r.LogRecordBytes is null, $"{w} LogRecordBytes doit être null pour {r.Operation}");
        }
        Assert.Contains(records, r => r.Operation == "LOP_MODIFY_COLUMNS" && r.LogRecordBytes is { Length: > 0 });
        // Au moins une ligne renseigne chaque champ nullable : un mappage « toujours null » ne passe pas.
        Assert.Contains(records, r => r.AllocUnitId is not null);
        Assert.Contains(records, r => r.PartitionId is not null);
        Assert.Contains(records, r => r.PageId is not null);
        Assert.Contains(records, r => r.SlotId is not null);
        Assert.Contains(records, r => r.OffsetInRow is not null);
        Assert.Contains(records, r => r.ModifySize is not null);
        Assert.Contains(records, r => r.NumElements is not null);
        Assert.Contains(records, r => r.Description is not null);
        Assert.Contains(records, r => r.TransactionName is not null);
        Assert.Contains(records, r => r.Operation == "LOP_BEGIN_XACT" && r.BeginTime is not null);
        Assert.Contains(records, r => r.Operation == "LOP_COMMIT_XACT" && r.EndTime is not null);
        Assert.Contains(records, r => r.RowLogContents2 is not null);
        Assert.Contains(records, r => r.RowLogContents3 is not null);
        Assert.Contains(records, r => r.RowLogContents4 is not null);
        Assert.Contains(records, r => r.RowLogContents5 is not null);
    }

    [IntegrationFact]
    public async Task ParseLogAsync_ReadsReplayColumnsAndAcceptsLsnRange()
    {
        var master = Environment.GetEnvironmentVariable("SQLLOGEXPLORER_TEST_SQL");
        Assert.False(string.IsNullOrEmpty(master), "Définir SQLLOGEXPLORER_TEST_SQL (voir tests/fixtures/sqlserver.sh).");

        var db = $"sle_cols_{Guid.NewGuid():N}";
        var dbConn = new SqlConnectionStringBuilder(master!) { InitialCatalog = db, Pooling = false }.ConnectionString;
        await ExecAsync(master!, $"CREATE DATABASE [{db}];");
        try
        {
            await ExecAsync(master!, $"ALTER DATABASE [{db}] SET RECOVERY FULL;");
            await ExecAsync(dbConn,
                "CREATE TABLE dbo.t(id int NOT NULL PRIMARY KEY, a int NULL); INSERT dbo.t VALUES (1, 10); UPDATE dbo.t SET a = 11 WHERE id = 1;");
            // Mise à jour de deux colonnes non contiguës : journalisée en LOP_MODIFY_COLUMNS sur les quatre versions.
            await ExecAsync(dbConn,
                "CREATE TABLE dbo.m2 (id int NOT NULL PRIMARY KEY, a int NULL, x char(50) NULL, b int NULL); " +
                "INSERT dbo.m2 VALUES (1, 1, 'x', 2); UPDATE dbo.m2 SET a = 11, b = 21;");

            var all = await ReadAsync(new LiveDatabaseBackend(), dbConn);
            AssertMatchesPlain(all, await ReadPlainAsync(dbConn));

            // Le brief ne filtrait que sur l'unité d'allocation : la table porte aussi deux LOP_MODIFY_ROW
            // aux offsets 340 (mise à jour de la page d'allocation), d'où le filtre sur l'offset de l'UPDATE.
            var modify = all.Single(r => r.Operation == "LOP_MODIFY_ROW" && r.OffsetInRow == 8
                && r.AllocUnitName is not null && r.AllocUnitName.StartsWith("dbo.t", StringComparison.Ordinal));
            Assert.Equal(8, modify.OffsetInRow);
            Assert.Equal(1, modify.ModifySize);
            Assert.NotNull(modify.NumElements);
            Assert.Equal(new byte[] { 0x0A }, modify.RowLogContents0);
            Assert.Equal(new byte[] { 0x0B }, modify.RowLogContents1);
            Assert.NotNull(modify.RowLogContents2);
            Assert.NotNull(modify.AllocUnitId);
            Assert.Equal(modify.Lsn.ToUpperInvariant(), modify.Lsn);

            // Plage : au moins une borne contient une lettre hexadécimale (Msg 9005 avec l'ancienne forme).
            var first = all.First(r => r.Lsn.Any(char.IsLetter)).Lsn;
            var last = all[^1].Lsn;
            Assert.True((first + last).Any(char.IsLetter));
            var ranged = await ReadAsync(new LiveDatabaseBackend(new LsnRange(first, last)), dbConn);
            Assert.NotEmpty(ranged);
            AssertMatchesPlain(ranged, await ReadPlainAsync(dbConn));
        }
        finally
        {
            await ExecAsync(master!, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}];");
        }
    }

    [IntegrationFact]
    public async Task FnDumpDblog_CarriesTheSameLogRecordBytesAsFnDblog()
    {
        var master = Environment.GetEnvironmentVariable("SQLLOGEXPLORER_TEST_SQL");
        Assert.False(string.IsNullOrEmpty(master), "Définir SQLLOGEXPLORER_TEST_SQL (voir tests/fixtures/sqlserver.sh).");

        var db = $"sle_dump_{Guid.NewGuid():N}";
        var dbConn = new SqlConnectionStringBuilder(master!) { InitialCatalog = db, Pooling = false }.ConnectionString;
        var trn = $"/var/opt/mssql/data/{db}.trn";
        await ExecAsync(master!, $"CREATE DATABASE [{db}];");
        try
        {
            await ExecAsync(master!, $"ALTER DATABASE [{db}] SET RECOVERY FULL; BACKUP DATABASE [{db}] TO DISK = N'/var/opt/mssql/data/{db}.bak';");
            await ExecAsync(dbConn,
                "CREATE TABLE dbo.m2 (id int NOT NULL PRIMARY KEY, a int NULL, x char(50) NULL, b int NULL); " +
                "INSERT dbo.m2 VALUES (1, 1, 'x', 2); UPDATE dbo.m2 SET a = 11, b = 21;");

            var live = (await ReadAsync(new LiveDatabaseBackend(), dbConn))
                .Where(r => r.Operation == "LOP_MODIFY_COLUMNS").ToDictionary(r => r.Lsn);
            Assert.NotEmpty(live);

            await ExecAsync(master!, $"BACKUP LOG [{db}] TO DISK = N'{trn}';");

            var dumped = new List<LogRecord>();
            await using (var cn = new SqlConnection(master!))
            {
                await cn.OpenAsync();
                await using var cmd = cn.CreateCommand();
                cmd.CommandText = FnDumpDblogQuery.Build("@path");
                cmd.Parameters.AddWithValue("@path", trn);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) dumped.Add(LogColumns.Read(r));
            }

            var dumpedModify = dumped.Where(r => r.Operation == "LOP_MODIFY_COLUMNS").ToList();
            Assert.NotEmpty(dumpedModify);
            foreach (var d in dumpedModify)
            {
                Assert.NotNull(d.LogRecordBytes);
                Assert.Contains(d.Lsn, live.Keys);
                Assert.True(live[d.Lsn].LogRecordBytes!.AsSpan().SequenceEqual(d.LogRecordBytes!), $"{d.Lsn} LogRecordBytes");
            }
            Assert.All(dumped.Where(r => r.Operation != "LOP_MODIFY_COLUMNS"), r => Assert.Null(r.LogRecordBytes));
        }
        finally
        {
            await ExecAsync(master!, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}];");
        }
    }
}
