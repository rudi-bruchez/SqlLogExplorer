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

    [Fact]
    public async Task RefreshAsync_AnalysisFails_KeepsOtherTabsAndSaysWhy()
    {
        await using var cache = await LogCache.CreateAsync(
            Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db"));
        await cache.InsertBatchAsync(new List<LogRecord>
        {
            new("l1", "LOP_INSERT_ROWS", null, null, "dbo.Clients", null, null),
        });
        // Les requêtes des onglets par type et par objet n'utilisent pas ParentTransactionId ; celle des faits de transaction,
        // si : seule l'analyse de maintenance échoue.
        await using (var cmd = cache.Connection.CreateCommand())
        {
            cmd.CommandText = "ALTER TABLE LogRecords DROP COLUMN ParentTransactionId;";
            await cmd.ExecuteNonQueryAsync();
        }
        var vm = new StatisticsViewModel(new LogQuery(cache.Connection));

        await vm.RefreshAsync();

        Assert.Single(vm.ByOperation);
        Assert.Single(vm.ByObject);
        Assert.Empty(vm.MaintenanceRows);
        Assert.StartsWith("Index maintenance could not be computed: ", vm.MaintenanceSummary);
        Assert.Contains("ParentTransactionId", vm.MaintenanceSummary);
    }

    private static IndexMaintenanceRow Row(MaintenanceKind kind, bool partial, bool inUserTransaction,
        IReadOnlyList<MaintenanceObject>? objects = null, int others = 0)
        => new(new MaintenanceOperation(kind, 60, "00000001:00000001:0001", "00000001:00000002:0001", null, null, [], 1, 100,
            partial, inUserTransaction) { Objects = objects ?? [], OtherObjects = others }, 1.0);

    [Theory]
    [InlineData(MaintenanceKind.Reorganize, false, false, "")]
    [InlineData(MaintenanceKind.Reorganize, true, false, "may extend beyond the import")]
    [InlineData(MaintenanceKind.RebuildOnline, true, false, "not committed in the import")]
    [InlineData(MaintenanceKind.RebuildOffline, true, false, "not committed in the import")]
    [InlineData(MaintenanceKind.RebuildOnline, false, true, "in a user transaction")]
    [InlineData(MaintenanceKind.Reorganize, false, true, "in a user transaction")]
    [InlineData(MaintenanceKind.RebuildOnline, true, true, "not committed in the import; in a user transaction")]
    [InlineData(MaintenanceKind.Reorganize, true, true, "not committed in the import; in a user transaction")]
    public void Notes_EachBranch(MaintenanceKind kind, bool partial, bool inUserTransaction, string expected)
        => Assert.Equal(expected, Row(kind, partial, inUserTransaction).Notes);

    [Fact]
    public void Objects_ListsNamesThenOthers()
    {
        MaintenanceObject[] objects = [new(1, "dbo.T.PK", 90), new(2, "dbo.T.IX_n", 5)];

        Assert.Equal("dbo.T.PK, dbo.T.IX_n and 3 others", Row(MaintenanceKind.Reorganize, false, false, objects, 3).Objects);
        Assert.Equal("dbo.T.PK, dbo.T.IX_n", Row(MaintenanceKind.Reorganize, false, false, objects).Objects);
    }
}
