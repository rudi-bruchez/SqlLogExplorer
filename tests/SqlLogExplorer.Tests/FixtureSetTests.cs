namespace SqlLogExplorer.Tests;

public class FixtureSetTests
{
    public static TheoryData<string> Versions => new() { "2017", "2019", "2022", "2025" };

    [Theory, MemberData(nameof(Versions))]
    public void Modify_FirstInsertOfT_IsTheSpecRow(string version)
    {
        var (schema, records) = FixtureSet.Load(version, "modify").ForTable("dbo.t");
        var insert = records.First(r => r.Operation == "LOP_INSERT_ROWS");

        // spec03 §2 : 0x30000D00 01000000 0A000000 xx 0500 00 0100 1700 616263, xx = octet des bits (non comparé).
        var expected = Convert.FromHexString("30000D00010000000A000000000500000100170061626300".Replace(" ", ""))[..23];
        Assert.Equal(expected[..12], insert.RowLogContents0![..12]);
        Assert.Equal(expected[13..], insert.RowLogContents0![13..]);
        Assert.Equal(5, schema.Columns.Count);
    }
}
