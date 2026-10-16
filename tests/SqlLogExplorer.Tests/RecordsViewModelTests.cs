using SqlLogExplorer.Data;
using SqlLogExplorer.ViewModels;

namespace SqlLogExplorer.Tests;

public class RecordsViewModelTests
{
    private static async Task<(LogCache Cache, List<string> Lsns)> CacheAsync()
    {
        var f = FixtureSet.Load("2022", "modify");
        var cache = await LogCache.CreateAsync(Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db"));
        await cache.InsertBatchAsync(f.Log);
        return (cache, f.Log.Select(r => r.Lsn).Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task LoadAsync_FillsTheFirstPageInLsnOrder()
    {
        var (cache, lsns) = await CacheAsync();
        await using var _ = cache;
        Assert.Equal(315, lsns.Count);
        var vm = new RecordsViewModel(new LogQuery(cache.Connection));

        await vm.LoadAsync();

        Assert.Equal(0, vm.PageIndex);
        Assert.Equal(lsns.Take(200), vm.Records.Select(r => r.Lsn));
        Assert.False(vm.PreviousPageCommand.CanExecute(null));
        Assert.True(vm.NextPageCommand.CanExecute(null));
    }

    [Fact]
    public async Task NextPage_MovesOn_AndPreviousPageComesBack()
    {
        var (cache, lsns) = await CacheAsync();
        await using var _ = cache;
        var vm = new RecordsViewModel(new LogQuery(cache.Connection));
        await vm.LoadAsync();
        vm.SelectedRecord = vm.Records[0];

        await vm.NextPageCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.PageIndex);
        Assert.Equal(lsns.Skip(200), vm.Records.Select(r => r.Lsn));
        Assert.Null(vm.SelectedRecord);
        Assert.False(vm.NextPageCommand.CanExecute(null));
        Assert.True(vm.PreviousPageCommand.CanExecute(null));

        await vm.PreviousPageCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.PageIndex);
        Assert.Equal(lsns.Take(200), vm.Records.Select(r => r.Lsn));
    }
}

public class RecordsViewModelTransactionTests
{
    private static async Task<(LogCache, RecordsViewModel)> RefreshedAsync()
    {
        var cache = await LogQueryTests.SeedTransactionsAsync();
        var vm = new RecordsViewModel(new LogQuery(cache.Connection));
        await vm.RefreshAsync();
        return (cache, vm);
    }

    [Fact]
    public async Task RefreshAsync_ListsTransactions_AndShowsAllRecords()
    {
        var (cache, vm) = await RefreshedAsync();
        await using var _ = cache;

        Assert.Equal(3, vm.Transactions.Count);
        Assert.Equal(8, vm.Records.Count);
    }

    [Fact]
    public async Task SelectingATransaction_ShowsOnlyItsRecords_AndShowAllComesBack()
    {
        var (cache, vm) = await RefreshedAsync();
        await using var _ = cache;

        vm.SelectedTransaction = vm.Transactions[1];
        await vm.Pending;

        Assert.Equal(new[] { "LOP_BEGIN_XACT", "LOP_DELETE_ROWS", "LOP_ABORT_XACT" }, vm.Records.Select(r => r.Operation));

        await vm.ShowAllCommand.ExecuteAsync(null);

        Assert.Null(vm.SelectedTransaction);
        Assert.Equal(8, vm.Records.Count);
    }

    [Fact]
    public async Task SearchingAnObject_FiltersTransactionsAndRecords()
    {
        var (cache, vm) = await RefreshedAsync();
        await using var _ = cache;

        vm.ObjectFilter = "Orders";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal("0000:000003e9", Assert.Single(vm.Transactions).TransactionId);
        Assert.Equal("LOP_DELETE_ROWS", Assert.Single(vm.Records).Operation);

        // Une transaction choisie montre toutes ses opérations, begin et commit compris.
        vm.SelectedTransaction = vm.Transactions[0];
        await vm.Pending;
        Assert.Equal(3, vm.Records.Count);
    }

    [Fact]
    public async Task SearchingAPage_ShowsWhatHappenedOnIt()
    {
        var (cache, vm) = await RefreshedAsync();
        await using var _ = cache;

        vm.PageFilter = "1:336";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Null(vm.SearchError);
        Assert.Equal(new[] { "0000:000003e8", "0000:000003ea" }, vm.Transactions.Select(t => t.TransactionId));
        Assert.Equal(new[] { "LOP_INSERT_ROWS", "LOP_MODIFY_ROW" }, vm.Records.Select(r => r.Operation));
    }

    [Fact]
    public async Task AnUnreadablePage_IsReported_AndLeavesTheListAsItWas()
    {
        var (cache, vm) = await RefreshedAsync();
        await using var _ = cache;

        vm.PageFilter = "336";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.NotNull(vm.SearchError);
        Assert.Equal(3, vm.Transactions.Count);
        Assert.Equal(8, vm.Records.Count);
    }
}
