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
            "('LogRecords','IX_LogRecords_AllocUnitName','IX_LogRecords_Operation','IX_LogRecords_Alloc_Op'," +
            "'IX_LogRecords_Alloc_Key_Lsn','IX_LogRecords_Alloc_Page_Slot_Lsn');";
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(6, count);
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

    [Fact]
    public async Task InsertBatchAsync_PersistsRowsWithNullsAndBlobs()
    {
        await using var cache = await LogCache.CreateAsync(TempDb());
        var records = new List<Models.LogRecord>
        {
            new("00000021:000000b4:0002", "LOP_INSERT_ROWS", "LCX_HEAP", "0000:0000abcd", "dbo.Clients",
                new byte[] { 0x10, 0x00 }, null),
            new("00000021:000000b4:0003", "LOP_DELETE_ROWS", null, null, null, null, null),
        };

        var inserted = await cache.InsertBatchAsync(records);

        Assert.Equal(2, inserted);
        await using var cmd = cache.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM LogRecords WHERE AllocUnitName IS NULL;";
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task InsertBatchAsync_IgnoresDuplicateLsnAndNormalizesCase()
    {
        await using var cache = await LogCache.CreateAsync(TempDb());
        var a = new Models.LogRecord("0000002a:00000010:0001", "LOP_INSERT_ROWS", "LCX_CLUSTERED", "0000:00000001", null, new byte[] { 0x10 }, null);
        var b = a with { Lsn = "0000002A:00000010:0001" };

        await cache.InsertBatchAsync(new[] { a });
        await cache.InsertBatchAsync(new[] { b });

        await using var cmd = cache.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), MIN(LSN) FROM LogRecords;";
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        Assert.Equal(1L, r.GetInt64(0));
        Assert.Equal("0000002A:00000010:0001", r.GetString(1));
    }

    [Fact]
    public async Task InsertBatchAsync_RoundTripsReplayColumns()
    {
        await using var cache = await LogCache.CreateAsync(TempDb());
        var rec = new Models.LogRecord("0000002A:00000010:0002", "LOP_MODIFY_ROW", "LCX_CLUSTERED", "0000:00000002", "dbo.t",
            new byte[] { 0x0A }, new byte[] { 0x0B })
        {
            AllocUnitId = 72057594052476928, PartitionId = 72057594045726720, PageId = "0001:00000150", SlotId = 3,
            OffsetInRow = 8, ModifySize = 1, NumElements = 6, Description = "COMPENSATION", TransactionName = "UPDATE",
            RowLogContents2 = new byte[] { 0x16, 0x01 }, RowLogContents3 = new byte[] { 0x02 },
            RowLogContents4 = new byte[] { 0x03 }, RowLogContents5 = new byte[] { 0x04 },
        };
        await cache.InsertBatchAsync(new[] { rec });

        var page = await new Data.LogQuery(cache.Connection).GetPageAsync(0, 10);

        Assert.Equal(rec, page.Single(), new LogRecordComparer());
    }

    [Fact]
    public async Task SetRowKeysAsync_StoresKeysByLsn()
    {
        await using var cache = await LogCache.CreateAsync(TempDb());
        var rec = new Models.LogRecord("0000002A:00000010:0003", "LOP_INSERT_ROWS", "LCX_CLUSTERED", null, null, null, null);
        await cache.InsertBatchAsync(new[] { rec });

        await cache.SetRowKeysAsync(new[] { ("0000002A:00000010:0003", (string?)"V01000000;U0") });

        await using var cmd = cache.Connection.CreateCommand();
        cmd.CommandText = "SELECT RowKey FROM LogRecords;";
        Assert.Equal("V01000000;U0", (string?)await cmd.ExecuteScalarAsync());
    }
}
