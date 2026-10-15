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

    [Fact]
    public void Modify2025_PageOfEveryURecord_IsFoundInThePageDump()
    {
        // Le journal rend les identifiants de page en minuscules, DBCC PAGE (via FORMAT) en majuscules : 0001:000001a0.
        var fixture = FixtureSet.Load("2025", "modify");
        var (_, records) = fixture.ForTable("dbo.u");
        Assert.NotEmpty(records);
        Assert.Contains(records, r => r.PageId!.Any(char.IsLetter) && r.PageId!.Substring(5).Any(char.IsLetter));
        foreach (var record in records)
            Assert.Contains(fixture.Pages, p => p.PageId == record.PageId);
    }

    [Theory, MemberData(nameof(Versions))]
    public void Modify_LogRecordBytes_OnlyOnModifyColumns(string version)
    {
        var log = FixtureSet.Load(version, "modify").Log;
        var modifyColumns = log.Where(r => r.Operation == "LOP_MODIFY_COLUMNS").ToList();
        Assert.NotEmpty(modifyColumns);
        foreach (var r in modifyColumns)
        {
            Assert.NotNull(r.LogRecordBytes);
            Assert.NotEmpty(r.LogRecordBytes!);
            // spec03 §6.2 : le u16 à l'offset 2 de l'en-tête est la longueur fixe, 62 pour LOP_MODIFY_COLUMNS.
            Assert.Equal(62, BitConverter.ToUInt16(r.LogRecordBytes!, 2));
        }
        Assert.All(log.Where(r => r.Operation != "LOP_MODIFY_COLUMNS"), r => Assert.Null(r.LogRecordBytes));
    }
}
