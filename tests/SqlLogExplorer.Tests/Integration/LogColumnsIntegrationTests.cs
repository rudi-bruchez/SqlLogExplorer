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

            var all = await ReadAsync(new LiveDatabaseBackend(), dbConn);

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
        }
        finally
        {
            await ExecAsync(master!, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}];");
        }
    }
}
