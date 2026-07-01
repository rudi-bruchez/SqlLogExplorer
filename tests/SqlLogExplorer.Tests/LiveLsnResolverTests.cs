using SqlLogExplorer.Backends;
using Xunit;

namespace SqlLogExplorer.Tests;

public class LiveLsnResolverTests
{
    [Fact]
    public void BuildRangeQuery_FiltersOnTransactionBoundaryOps()
    {
        var sql = LiveLsnResolver.BuildRangeQuery();
        Assert.Contains("fn_dblog(NULL, NULL)", sql);
        Assert.Contains("LOP_BEGIN_XACT", sql);
        Assert.Contains("LOP_COMMIT_XACT", sql);
        Assert.Contains("MIN([Current LSN])", sql);
        Assert.Contains("MAX([Current LSN])", sql);
    }
}
