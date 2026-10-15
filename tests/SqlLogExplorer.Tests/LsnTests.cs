using SqlLogExplorer.Models;

namespace SqlLogExplorer.Tests;

public class LsnTests
{
    [Fact]
    public void Normalize_UppercasesLowercaseLsn()
        => Assert.Equal("0000002A:00000470:0013", Lsn.Normalize("0000002a:00000470:0013"));

    [Fact]
    public void ToFnDblogArgument_ConvertsEachPartToDecimal()
        => Assert.Equal("42:1136:19", Lsn.ToFnDblogArgument("0000002A:00000470:0013"));

    [Fact]
    public void ToFnDblogArgument_AcceptsLowercase()
        => Assert.Equal("39:144:5", Lsn.ToFnDblogArgument("00000027:00000090:0005"));

    [Fact]
    public void ToFnDblogArgument_RejectsMalformedLsn()
        => Assert.Throws<FormatException>(() => Lsn.ToFnDblogArgument("not-an-lsn"));
}
