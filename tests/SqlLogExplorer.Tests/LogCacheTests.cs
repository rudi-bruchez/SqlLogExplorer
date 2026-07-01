using Microsoft.Data.Sqlite;
using SqlLogExplorer.Data;
using Xunit;

namespace SqlLogExplorer.Tests;

public class LogCacheTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db");

    [Fact]
    public async Task CreateAsync_CreatesLogRecordsTableAndIndexes()
    {
        var path = TempDb();
        await using var cache = await LogCache.CreateAsync(path);

        await using var cmd = cache.Connection.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table','index') AND name IN " +
            "('LogRecords','IX_LogRecords_AllocUnitName','IX_LogRecords_Operation','IX_LogRecords_Alloc_Op');";
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(4, count);
    }

    [Fact]
    public async Task Dispose_DeletesDatabaseFile()
    {
        var path = TempDb();
        await using (var cache = await LogCache.CreateAsync(path))
        {
            Assert.True(File.Exists(path));
        }
        Assert.False(File.Exists(path));
    }
}
