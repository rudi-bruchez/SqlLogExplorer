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
