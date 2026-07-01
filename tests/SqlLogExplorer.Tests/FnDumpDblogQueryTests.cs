using SqlLogExplorer.Backends;
using Xunit;

namespace SqlLogExplorer.Tests;

public class FnDumpDblogQueryTests
{
    [Fact]
    public void Build_ContainsRequiredColumnsAndFunction()
    {
        var sql = FnDumpDblogQuery.Build("@path");

        Assert.Contains("[Current LSN]", sql);
        Assert.Contains("[Transaction ID]", sql);
        Assert.Contains("[RowLog Contents 0]", sql);
        Assert.Contains("[RowLog Contents 1]", sql);
        Assert.Contains("sys.fn_dump_dblog(NULL, NULL, N'DISK', 1, @path,", sql);
    }

    [Fact]
    public void Build_EmitsRequestedNumberOfDefaults()
    {
        var sql = FnDumpDblogQuery.Build("@path", defaultParameterCount: 63);

        var count = sql.Split("DEFAULT").Length - 1;
        Assert.Equal(63, count);
    }
}
