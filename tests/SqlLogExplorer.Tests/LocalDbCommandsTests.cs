using SqlLogExplorer.Backends;
using Xunit;

namespace SqlLogExplorer.Tests;

public class LocalDbCommandsTests
{
    [Fact]
    public void Create_QuotesInstanceName()
        => Assert.Equal("create \"SqlLogExplorerInstance\"", LocalDbCommands.Create("SqlLogExplorerInstance"));

    [Fact]
    public void Start_QuotesInstanceName()
        => Assert.Equal("start \"SqlLogExplorerInstance\"", LocalDbCommands.Start("SqlLogExplorerInstance"));
}
