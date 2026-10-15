using SqlLogExplorer.Backends;
using Xunit;

namespace SqlLogExplorer.Tests;

public class FnDumpDblogQueryTests
{
    [Theory]
    [InlineData("[Current LSN]")] [InlineData("[Transaction ID]")] [InlineData("[AllocUnitId]")]
    [InlineData("[PartitionId]")] [InlineData("[Page ID]")] [InlineData("[Slot ID]")]
    [InlineData("[Offset in Row]")] [InlineData("[Modify Size]")] [InlineData("[Num Elements]")]
    [InlineData("[Description]")] [InlineData("[Transaction Name]")]
    [InlineData("[RowLog Contents 0]")] [InlineData("[RowLog Contents 5]")]
    public void Build_SelectsColumn(string column)
        => Assert.Contains(column, FnDumpDblogQuery.Build("@path"));

    [Fact]
    public void Build_EmitsRequestedNumberOfDefaults()
    {
        var sql = FnDumpDblogQuery.Build("@path", defaultParameterCount: 63);

        var count = sql.Split("DEFAULT").Length - 1;
        Assert.Equal(63, count);
    }
}
