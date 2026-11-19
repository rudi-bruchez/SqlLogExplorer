using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SqlLogExplorer.Data;
using SqlLogExplorer.Models;
using SqlLogExplorer.ViewModels;
using Xunit;

namespace SqlLogExplorer.Tests;

public class StatisticsViewModelTests
{
    [Fact]
    public async Task RefreshAsync_PopulatesByOperationAndByObject()
    {
        await using var cache = await LogCache.CreateAsync(
            Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db"));
        await cache.InsertBatchAsync(new List<LogRecord>
        {
            new("l1", "LOP_INSERT_ROWS", null, null, "dbo.Clients", null, null),
            new("l2", "LOP_INSERT_ROWS", null, null, "dbo.Clients", null, null),
            new("l3", "LOP_DELETE_ROWS", null, null, "dbo.Orders",  null, null),
        });
        var vm = new StatisticsViewModel(new LogQuery(cache.Connection));

        await vm.RefreshAsync();

        Assert.Equal(2, vm.ByOperation.Count);
        Assert.Equal("LOP_INSERT_ROWS", vm.ByOperation[0].Operation);
        Assert.Equal(2, vm.ByOperation[0].Count);
        Assert.Equal(2, vm.ByObject.Count);
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(48_922_276L, "46.7 MB")]
    public void FormatBytes_Units(long bytes, string expected)
        => Assert.Equal(expected, IndexMaintenanceRow.FormatBytes(bytes));

    [Fact]
    public async Task RefreshAsync_EmptyCache_ShowsZeroShare()
    {
        await using var cache = await LogCache.CreateAsync(
            Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db"));
        var vm = new StatisticsViewModel(new LogQuery(cache.Connection));

        await vm.RefreshAsync();

        Assert.Empty(vm.MaintenanceRows);
        Assert.Contains("0 B (0.0 %)", vm.MaintenanceSummary);
    }

    [Fact]
    public async Task RefreshAsync_ListsAnOfflineRebuild()
    {
        await using var cache = await LogCache.CreateAsync(
            Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db"));
        LogRecord Rec(string lsn, string op, string? tx, int len) =>
            new(lsn, op, null, tx, null, null, null) { LogRecordLength = len };
        await cache.InsertBatchAsync(new List<LogRecord>
        {
            Rec("00000010:00000001:0001", "LOP_BEGIN_XACT", "0000:00000100", 100)
                with { TransactionName = "ALTER INDEX", Spid = 60, BeginTime = "2026/11/19 10:00:00:000" },
            Rec("00000010:00000002:0001", "LOP_BEGIN_XACT", "0000:00000101", 100)
                with { TransactionName = "offline index build", Spid = 60, ParentTransactionId = "0000:00000100",
                       BeginTime = "2026/11/19 10:00:01:000" },
            Rec("00000010:00000003:0001", "LOP_INSERT_ROWS", "0000:00000101", 900),
            Rec("00000010:00000004:0001", "LOP_COMMIT_XACT", "0000:00000101", 50)
                with { EndTime = "2026/11/19 10:00:02:000" },
            Rec("00000010:00000005:0001", "LOP_COMMIT_XACT", "0000:00000100", 50)
                with { EndTime = "2026/11/19 10:00:03:000" },
            Rec("00000010:00000006:0001", "LOP_BEGIN_XACT", "0000:00000200", 100)
                with { TransactionName = "INSERT", Spid = 61, BeginTime = "2026/11/19 10:00:04:000" },
            Rec("00000010:00000007:0001", "LOP_COMMIT_XACT", "0000:00000200", 50)
                with { EndTime = "2026/11/19 10:00:05:000" },
        });
        var vm = new StatisticsViewModel(new LogQuery(cache.Connection));

        await vm.RefreshAsync();

        var row = Assert.Single(vm.MaintenanceRows);
        Assert.Equal("Rebuild offline", row.Kind);
        Assert.InRange(row.Share, 0.0001, 100);
    }
}
