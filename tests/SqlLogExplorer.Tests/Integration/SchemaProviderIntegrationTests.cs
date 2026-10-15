using Microsoft.Data.SqlClient;
using SqlLogExplorer.Backends;

namespace SqlLogExplorer.Tests.Integration;

public class SchemaProviderIntegrationTests
{
    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var cn = new SqlConnection(connectionString);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> AllocUnitAsync(string connectionString, string table)
    {
        await using var cn = new SqlConnection(connectionString);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = $"SELECT allocation_unit_id FROM sys.allocation_units au JOIN sys.partitions p ON p.partition_id = au.container_id WHERE p.object_id = OBJECT_ID('{table}') AND au.type = 1";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [IntegrationFact]
    public async Task GetAsync_ReadsPhysicalColumnsOfDroppedBitKeyAndUniqueifierCases()
    {
        var master = Environment.GetEnvironmentVariable("SQLLOGEXPLORER_TEST_SQL");
        Assert.False(string.IsNullOrEmpty(master), "Définir SQLLOGEXPLORER_TEST_SQL.");
        var db = "sle_schema_" + Guid.NewGuid().ToString("N");
        var cs = new SqlConnectionStringBuilder(master) { InitialCatalog = db }.ConnectionString;

        await ExecAsync(master, $"CREATE DATABASE [{db}];");
        try
        {
            await ExecAsync(cs, """
                CREATE TABLE dbo.t(id int NOT NULL PRIMARY KEY, a int NULL, b varchar(20) NULL, c bit, d bit);
                CREATE TABLE dbo.d(id int NOT NULL PRIMARY KEY, x int NULL, y int NULL);
                ALTER TABLE dbo.d DROP COLUMN x;
                CREATE TABLE dbo.n(k int NOT NULL, v int NULL);
                CREATE CLUSTERED INDEX cx ON dbo.n(k);
                """);
            var provider = new SchemaProvider(cs);

            var t = await provider.GetAsync(await AllocUnitAsync(cs, "dbo.t"));
            Assert.NotNull(t);
            Assert.Equal(new[] { "id", "a", "b", "c", "d" }, t.Columns.Select(c => c.Name));
            var c = t.Columns[3]; var d = t.Columns[4];
            Assert.Equal(0, c.LeafBitPosition);
            Assert.Equal(1, d.LeafBitPosition);
            Assert.Equal(c.LeafOffset, d.LeafOffset);
            Assert.Equal(-1, t.Columns[2].LeafOffset);
            Assert.Equal(1, t.Columns[0].KeyOrdinal);
            Assert.NotNull(t.Columns[2].CodePage);

            var dropped = await provider.GetAsync(await AllocUnitAsync(cs, "dbo.d"));
            Assert.NotNull(dropped);
            Assert.Equal(3, dropped.Columns.Count);
            var gone = Assert.Single(dropped.Columns, x => x.IsDropped);
            Assert.Equal($"dropped#{gone.PartitionColumnId}", gone.Name);

            var n = await provider.GetAsync(await AllocUnitAsync(cs, "dbo.n"));
            Assert.NotNull(n);
            Assert.Contains(n.Columns, x => x.IsUniqueifier);
            Assert.Equal(1, n.Columns.Single(x => x.Name == "k").KeyOrdinal);

            Assert.Null(await provider.GetAsync(123));
            Assert.True(await provider.IsPrivilegedAsync());
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecAsync(master, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}];");
        }
    }

    [IntegrationFact]
    public async Task GetAsync_ReadsDefaultValuesWithoutLoss()
    {
        var master = Environment.GetEnvironmentVariable("SQLLOGEXPLORER_TEST_SQL");
        Assert.False(string.IsNullOrEmpty(master), "Définir SQLLOGEXPLORER_TEST_SQL.");
        var db = "sle_schema_" + Guid.NewGuid().ToString("N");
        var cs = new SqlConnectionStringBuilder(master) { InitialCatalog = db }.ConnectionString;

        await ExecAsync(master, $"CREATE DATABASE [{db}];");
        try
        {
            // Une ligne existe avant l'ADD COLUMN : c'est le cas où la valeur par défaut sert à décoder (spec03 §5).
            await ExecAsync(cs, """
                CREATE TABLE dbo.t(id int NOT NULL);
                INSERT dbo.t VALUES (1);
                ALTER TABLE dbo.t ADD
                    m money NOT NULL DEFAULT 12.3456,
                    d2 datetime2(7) NOT NULL DEFAULT '2024-01-02 03:04:05.1234567',
                    dto datetimeoffset NOT NULL DEFAULT '2024-01-02 03:04:05.1234567 +02:00',
                    vb varbinary(10) NOT NULL DEFAULT 0x0102,
                    r real NOT NULL DEFAULT 1.5e-10,
                    n nvarchar(10) NOT NULL DEFAULT N'αβγ',
                    i int NOT NULL DEFAULT 7;
                """);
            var t = await new SchemaProvider(cs).GetAsync(await AllocUnitAsync(cs, "dbo.t"));
            Assert.NotNull(t);

            var expected = new Dictionary<string, string>
            {
                ["m"] = "12.3456",
                ["d2"] = "2024-01-02T03:04:05.1234567",
                ["dto"] = "2024-01-02T03:04:05.1234567+02:00",
                ["vb"] = "0x0102",
                ["r"] = "1.4999999853326784e-010",
                ["n"] = "αβγ",
                ["i"] = "7",
            };
            foreach (var (name, value) in expected)
            {
                var col = t.Columns.Single(x => x.Name == name);
                Assert.True(col.HasDefault, name);
                Assert.Equal(value, col.DefaultValue);
            }
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecAsync(master, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}];");
        }
    }
}
