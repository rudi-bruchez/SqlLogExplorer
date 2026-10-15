using SqlLogExplorer.Backends;
using SqlLogExplorer.Decoding;
using static SqlLogExplorer.Tests.Decoding.TableSchemaTests;

namespace SqlLogExplorer.Tests;

public class CurrentRowParsingTests
{
    public static TheoryData<string> Versions => new() { "2017", "2019", "2022", "2025" };

    [Theory, MemberData(nameof(Versions))]
    public void ParseSlotDump_RebuildsTheRowOfDboU(string version)
    {
        var f = FixtureSet.Load(version, "modify");
        var (schema, records) = f.ForTable("dbo.u");
        // La dernière modification de dbo.u donne la page et le slot de la ligne id 2.
        var last = records[^1];
        var lines = f.Pages.Where(p => p.Style == 1 && p.PageId == last.PageId).Select(p => (p.ParentObject, p.Value));

        var row = CurrentRowReader.ParseSlotDump(lines, last.SlotId!.Value);

        Assert.NotNull(row);
        var decoded = RowDecoder.Decode(row, schema);
        Assert.False(decoded.HasProblem);
        Assert.Equal(2, decoded.Columns.Single(c => c.Name == "id").Value);
        Assert.Equal(100, decoded.Columns.Single(c => c.Name == "a").Value);
        Assert.Equal("zz", decoded.Columns.Single(c => c.Name == "b").Value);
    }

    [Fact]
    public void ParseSlotDump_ReadsTheHexAreaOnly_AndTruncatesToTheLength()
    {
        // Mesuré sur 2022 : la partie ASCII peut ressembler à de l'hexadécimal ("abc"), elle n'est jamais lue.
        var lines = new[]
        {
            ("Slot 0, Offset 0x60, Length 22, DumpStyle BYTE", "0000000000000000:   30000c00 07000000 02000000 03000001 00160061  0..................a"),
            ("Slot 0, Offset 0x60, Length 22, DumpStyle BYTE", "0000000000000014:   6263                                          bc"),
        };

        var row = CurrentRowReader.ParseSlotDump(lines, 0);

        Assert.Equal("30000C00070000000200000003000001001600616263", Convert.ToHexString(row!));
    }

    [Fact]
    public void ParseSlotDump_IgnoresOtherSlots_AndReturnsNullForAMissingSlot()
    {
        var lines = new[]
        {
            ("Slot 1, Offset 0x60, Length 4, DumpStyle BYTE", "0000000000000000:   01020304                                      ...."),
            ("Slot 10, Offset 0x70, Length 4, DumpStyle BYTE", "0000000000000000:   0a0b0c0d                                      ...."),
        };

        Assert.Equal("01020304", Convert.ToHexString(CurrentRowReader.ParseSlotDump(lines, 1)!));
        Assert.Equal("0A0B0C0D", Convert.ToHexString(CurrentRowReader.ParseSlotDump(lines, 10)!));
        Assert.Null(CurrentRowReader.ParseSlotDump(lines, 0));
    }

    // ---- Valeurs de la clé lues dans l'enregistrement d'index (mêmes octets que KeyDecoderTests) ----

    private static byte[] Hex(string h) => Convert.FromHexString(h.Replace(" ", ""));

    private static TableSchema Schema(string name, params SchemaColumn[] columns) => new(1, "dbo", name, 1, 1, columns);

    private static string Values(IReadOnlyList<(SchemaColumn Column, object? Value)>? values) =>
        values is null ? "<null>" : string.Join(", ", values.Select(v => $"{v.Column.Name}={v.Value ?? "NULL"}"));

    [Fact]
    public void ValuesFromIndexRecord_KeyOrder_VariableAndFixed()
    {
        var k = Schema("k", Col(1, "k1", -1, 1, 167, 10, keyOrdinal: 1), Col(2, "k2", 4, 2, 56, 4, keyOrdinal: 2), Col(3, "v", 8, 3, 56, 4));
        Assert.Equal("k1=abc, k2=7", Values(KeyDecoder.ValuesFromIndexRecord(Hex("360700000002000001000F00616263"), k)));
    }

    [Fact]
    public void ValuesFromIndexRecord_NullColumns_UniqueifierAndBits()
    {
        // kn(a varchar NULL, b int NULL, c int NULL), clé non unique (c, a, b).
        var kn = Schema("kn", Col(1, "uniqueifier", -1, 1, 56, 4, uniqueifier: true), Col(2, "a", -2, 2, 167, 10, keyOrdinal: 2),
            Col(3, "b", 4, 3, 56, 4, keyOrdinal: 3), Col(4, "c", 8, 4, 56, 4, keyOrdinal: 1));
        Assert.Equal("c=NULL, a=x, b=1, uniqueifier=0", Values(KeyDecoder.ValuesFromIndexRecord(Hex("36 00000000 01000000 0400 01 0200 1300 1700 78 00000000"), kn)));
        Assert.Equal("c=1, a=x, b=NULL, uniqueifier=0", Values(KeyDecoder.ValuesFromIndexRecord(Hex("36 01000000 00000000 0400 04 0200 1300 1700 78 00000000"), kn)));

        // ku(k varchar) non unique : uniqueifier 2.
        var ku = Schema("ku", Col(1, "uniqueifier", -1, 1, 56, 4, uniqueifier: true), Col(2, "k", -2, 2, 167, 10, keyOrdinal: 1), Col(3, "v", 4, 3, 56, 4));
        Assert.Equal("k=dup, uniqueifier=2", Values(KeyDecoder.ValuesFromIndexRecord(Hex("36 0200 00 0200 0d00 1100 647570 02000000"), ku)));

        // (id, b1, b2) : les bits d'un octet partagé, bits étrangers masqués (0x39).
        var kb = Schema("kb", Col(1, "id", 4, 1, 56, 4, keyOrdinal: 1), Col(2, "b1", 8, 2, 104, 1, keyOrdinal: 2, bitPos: 0),
            Col(3, "b2", 8, 3, 104, 1, keyOrdinal: 3, bitPos: 1));
        Assert.Equal("id=1, b1=True, b2=False", Values(KeyDecoder.ValuesFromIndexRecord(Hex("16 01000000 39 0300 00"), kb)));
    }

    [Fact]
    public void ValuesFromIndexRecord_TrailingEmptyVariable_IsEmptyNotNull()
    {
        var eb = Schema("eb", Col(1, "k", -1, 1, 167, 10, keyOrdinal: 1), Col(2, "j", -2, 2, 167, 10, keyOrdinal: 2), Col(3, "v", 4, 3, 56, 4));
        var values = KeyDecoder.ValuesFromIndexRecord(Hex("360200000100090061"), eb);
        Assert.Equal("a", values![0].Value);
        Assert.Equal("", values[1].Value);
    }

    [Fact]
    public void ValuesFromIndexRecord_Undecodable_IsNull()
    {
        // Partitionnée hors de la clé, non unique : non pris en charge, comme FromIndexRecord.
        var pt = Schema("pt", Col(1, "uniqueifier", -1, 1, 56, 4, uniqueifier: true), Col(2, "id", 4, 2, 56, 4, keyOrdinal: 1),
            Col(3, "reg", 8, 3, 56, 4), Col(4, "v", 12, 4, 56, 4));
        Assert.Null(KeyDecoder.ValuesFromIndexRecord(Hex("3601000000050000000300000100140000000000"), pt));
        // Type de clé non pris en charge par TypeDecoder (hierarchyid, 240) : pas de valeur à chercher.
        var h = Schema("h", Col(1, "id", -1, 1, 240, 892, keyOrdinal: 1));
        Assert.Null(KeyDecoder.ValuesFromIndexRecord(Hex("36 0100 00 0100 0900 58"), h));
    }
}
