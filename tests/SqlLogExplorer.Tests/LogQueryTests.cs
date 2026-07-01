using Microsoft.Data.Sqlite;
using SqlLogExplorer.Data;
using SqlLogExplorer.Models;
using Xunit;

namespace SqlLogExplorer.Tests;

public class LogQueryTests
{
    private static async Task<LogCache> SeedAsync()
    {
        var cache = await LogCache.CreateAsync(Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db"));
        var records = new List<LogRecord>();
        for (int i = 0; i < 5; i++)
            records.Add(new($"lsn{i}", "LOP_INSERT_ROWS", "LCX_HEAP", $"tx{i}", "dbo.Clients", null, null));
        for (int i = 0; i < 3; i++)
            records.Add(new($"lsnd{i}", "LOP_DELETE_ROWS", "LCX_HEAP", $"txd{i}", "dbo.Orders", null, null));
        await cache.InsertBatchAsync(records);
        return cache;
    }

    [Fact]
    public async Task GetPageAsync_RespectsLimitOffsetAndOrder()
    {
        await using var cache = await SeedAsync();
        var query = new LogQuery(cache.Connection);

        var page = await query.GetPageAsync(offset: 2, limit: 2);

        Assert.Equal(2, page.Count);
        Assert.Equal("lsn2", page[0].Lsn);
        Assert.Equal("lsn3", page[1].Lsn);
    }

    [Fact]
    public async Task GetPageAsync_FiltersByOperation()
    {
        await using var cache = await SeedAsync();
        var query = new LogQuery(cache.Connection);

        var page = await query.GetPageAsync(0, 100, new LogFilter(Operation: "LOP_DELETE_ROWS"));

        Assert.Equal(3, page.Count);
        Assert.All(page, r => Assert.Equal("LOP_DELETE_ROWS", r.Operation));
    }

    [Fact]
    public async Task CountByOperationAsync_GroupsAndOrdersDescending()
    {
        await using var cache = await SeedAsync();
        var query = new LogQuery(cache.Connection);

        var result = await query.CountByOperationAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal("LOP_INSERT_ROWS", result[0].Operation);
        Assert.Equal(5, result[0].Count);
        Assert.Equal("LOP_DELETE_ROWS", result[1].Operation);
        Assert.Equal(3, result[1].Count);
    }

    [Fact]
    public async Task CountByOperationAsync_HonorsFilter()
    {
        await using var cache = await SeedAsync();
        var query = new LogQuery(cache.Connection);

        var result = await query.CountByOperationAsync(new LogFilter(AllocUnitName: "Orders"));

        Assert.Single(result);
        Assert.Equal("LOP_DELETE_ROWS", result[0].Operation);
    }

    [Fact]
    public async Task CountByObjectAsync_GroupsByAllocUnit()
    {
        await using var cache = await SeedAsync();
        var query = new LogQuery(cache.Connection);

        var result = await query.CountByObjectAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal("dbo.Clients", result[0].AllocUnitName);
        Assert.Equal(5, result[0].Count);
    }

    [Fact]
    public async Task CountByObjectAndOperationAsync_ReturnsMatrix()
    {
        await using var cache = await SeedAsync();
        var query = new LogQuery(cache.Connection);

        var result = await query.CountByObjectAndOperationAsync();

        Assert.Contains(result, x => x.AllocUnitName == "dbo.Clients" && x.Operation == "LOP_INSERT_ROWS" && x.Count == 5);
        Assert.Contains(result, x => x.AllocUnitName == "dbo.Orders"  && x.Operation == "LOP_DELETE_ROWS" && x.Count == 3);
    }
}
