using SqlLogExplorer.Models;
using Xunit;

namespace SqlLogExplorer.Tests;

public class ModelsTests
{
    [Fact]
    public void EmptyFilter_IsEmpty_IsTrue()
    {
        Assert.True(new LogFilter().IsEmpty);
    }

    [Fact]
    public void FilterWithOperation_IsEmpty_IsFalse()
    {
        Assert.False(new LogFilter(Operation: "LOP_INSERT_ROWS").IsEmpty);
    }
}
