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
    public async Task AQuery_WaitsWhileAnotherUserHoldsTheConnection()
    {
        // L'inspection tourne hors du thread UI : la grille ne doit pas utiliser la connexion en même temps.
        await using var cache = await SeedAsync();
        var held = await ConnectionGate.EnterAsync(cache.Connection);

        var query = new LogQuery(cache.Connection).GetLastLsnAsync();
        var waited = !query.IsCompleted;
        held.Dispose();

        Assert.True(waited);
        Assert.NotNull(await query);
    }

    [Fact]
    public async Task GetPageAsync_RespectsLimitOffsetAndOrder()
    {
        await using var cache = await SeedAsync();
        var query = new LogQuery(cache.Connection);

        var page = await query.GetPageAsync(offset: 2, limit: 2);

        Assert.Equal(2, page.Count);
        Assert.Equal("LSN2", page[0].Lsn);
        Assert.Equal("LSN3", page[1].Lsn);
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

    [Fact]
    public async Task GetPageAsync_OrdersByLsnNotInsertionOrder()
    {
        await using var cache = await LogCache.CreateAsync(Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db"));
        await cache.InsertBatchAsync(new[]
        {
            new Models.LogRecord("0000002C:00000010:0001", "LOP_INSERT_ROWS", null, null, null, null, null),
            new Models.LogRecord("0000002b:00000010:0001", "LOP_INSERT_ROWS", null, null, null, null, null),
        });

        var page = await new LogQuery(cache.Connection).GetPageAsync(0, 10);

        Assert.Equal(new[] { "0000002B:00000010:0001", "0000002C:00000010:0001" }, page.Select(r => r.Lsn));
    }
    // Trois transactions et la pseudo-transaction des checkpoints ; heures au format de fn_dblog.
    internal static async Task<LogCache> SeedTransactionsAsync()
    {
        var cache = await LogCache.CreateAsync(Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db"));
        await cache.InsertBatchAsync(new LogRecord[]
        {
            new("00000001:00000001:0001", "LOP_BEGIN_CKPT", "LCX_NULL", "0000:00000000", null, null, null),
            new("00000001:00000002:0001", "LOP_BEGIN_XACT", "LCX_NULL", "0000:000003e8", null, null, null)
                { TransactionName = "user_transaction", BeginTime = "2026/10/16 09:00:00:100" },
            new("00000001:00000003:0001", "LOP_INSERT_ROWS", "LCX_CLUSTERED", "0000:000003e8", "dbo.Clients.PK_Clients", null, null)
                { PageId = "0001:00000150" },
            new("00000001:00000004:0001", "LOP_COMMIT_XACT", "LCX_NULL", "0000:000003e8", null, null, null)
                { EndTime = "2026/10/16 09:00:01:200" },
            new("00000001:00000005:0001", "LOP_BEGIN_XACT", "LCX_NULL", "0000:000003e9", null, null, null)
                { TransactionName = "DELETE", BeginTime = "2026/10/16 09:05:00:000" },
            new("00000001:00000006:0001", "LOP_DELETE_ROWS", "LCX_HEAP", "0000:000003e9", "dbo.Orders", null, null)
                { PageId = "0001:000000ed" },
            new("00000001:00000007:0001", "LOP_ABORT_XACT", "LCX_NULL", "0000:000003e9", null, null, null)
                { EndTime = "2026/10/16 09:05:02:000" },
            // Commencée avant la fenêtre importée, pas encore terminée.
            new("00000001:00000008:0001", "LOP_MODIFY_ROW", "LCX_CLUSTERED", "0000:000003ea", "dbo.Clients.PK_Clients", null, null)
                { PageId = "0001:00000150" },
        });
        return cache;
    }

    [Fact]
    public async Task GetTransactionsAsync_SummarisesEachTransactionInLsnOrder()
    {
        await using var cache = await SeedTransactionsAsync();

        var txs = await new LogQuery(cache.Connection).GetTransactionsAsync();

        Assert.Equal(new[]
        {
            new TransactionSummary("0000:000003e8", "user_transaction", "2026/10/16 09:00:00:100", "2026/10/16 09:00:01:200", "Committed", 3),
            new TransactionSummary("0000:000003e9", "DELETE", "2026/10/16 09:05:00:000", "2026/10/16 09:05:02:000", "Rolled back", 3),
            new TransactionSummary("0000:000003ea", null, null, null, "Open", 1),
        }, txs);
    }

    [Fact]
    public async Task GetTransactionsAsync_ObjectFilterKeepsTheTransactionsThatTouchIt()
    {
        await using var cache = await SeedTransactionsAsync();

        var txs = await new LogQuery(cache.Connection).GetTransactionsAsync(new LogFilter(AllocUnitName: "Clients"));

        Assert.Equal(new[] { "0000:000003e8", "0000:000003ea" }, txs.Select(t => t.TransactionId));
        Assert.Equal(3, txs[0].OperationCount); // toute la transaction, pas seulement ses lignes sur l'objet
    }

    [Fact]
    public async Task GetPageAsync_ReadsBackBeginAndEndTime()
    {
        await using var cache = await SeedTransactionsAsync();

        var page = await new LogQuery(cache.Connection).GetPageAsync(0, 10, new LogFilter(TransactionId: "0000:000003e8"));

        Assert.Equal("2026/10/16 09:00:00:100", page[0].BeginTime);
        Assert.Equal("2026/10/16 09:00:01:200", page[2].EndTime);
    }

    [Fact]
    public async Task GetTransactionsAsync_PageFilterKeepsTheTransactionsThatTouchThePage()
    {
        await using var cache = await SeedTransactionsAsync();

        var txs = await new LogQuery(cache.Connection).GetTransactionsAsync(new LogFilter(PageId: "0001:000000ed"));

        Assert.Equal("0000:000003e9", Assert.Single(txs).TransactionId);
    }

    [Theory]
    [InlineData("1:336", "0001:00000150")]          // DBCC PAGE, décimal
    [InlineData(" 1 : 336 ", "0001:00000150")]
    [InlineData("0001:00000150", "0001:00000150")]  // fn_dblog, hexadécimal
    [InlineData("0001:000000ED", "0001:000000ed")]
    [InlineData("3:0", "0003:00000000")]
    [InlineData("336", null)]
    [InlineData("1:", null)]
    [InlineData("x:336", null)]
    [InlineData("0001:0000015g", null)]
    [InlineData("1:-5", null)]
    public void NormalizePageId_AcceptsDbccAndFnDblogForms(string input, string? expected)
        => Assert.Equal(expected, LogFilter.NormalizePageId(input));
}
