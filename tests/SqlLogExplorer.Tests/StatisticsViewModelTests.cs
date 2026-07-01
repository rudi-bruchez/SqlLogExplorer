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
}
