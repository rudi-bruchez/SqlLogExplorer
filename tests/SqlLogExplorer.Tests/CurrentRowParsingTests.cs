using SqlLogExplorer.Backends;
using SqlLogExplorer.Decoding;
using static SqlLogExplorer.Tests.Decoding.TableSchemaTests;

namespace SqlLogExplorer.Tests;

public class CurrentRowParsingTests
{
    public static TheoryData<string> Versions => new() { "2017", "2019", "2022", "2025" };

    [Fact]
    public void KeyMatches_RejectsAMalformedSlot_EvenForANullKey()
    {
        var s = new TableSchema(1, "dbo", "n", 1, 1, new[] { Col(1, "k", 4, 1, 56, 4, keyOrdinal: 1), Col(2, "v", 8, 2, 56, 4) });
        var k = s.Columns[0];

        Assert.True(CurrentRowReader.KeyMatches(Convert.FromHexString("10000C0000000000010000000200" + "01"), s, [(k, null)]));   // k NULL
        Assert.False(CurrentRowReader.KeyMatches(Convert.FromHexString("10000C0001"), s, [(k, null)]));                        // ligne tronquée
    }

    [Fact]
    public void KeyMatches_RejectsARecordThatIsNotPrimary_EvenForANullKey()
    {
        var s = new TableSchema(1, "dbo", "n", 1, 1, new[] { Col(1, "k", 4, 1, 56, 4, keyOrdinal: 1), Col(2, "v", 8, 2, 56, 4) });

        // Talon de renvoi (type 2) : décodé sans colonne ni problème, la colonne k y est absente comme une valeur NULL.
        Assert.False(CurrentRowReader.KeyMatches(Convert.FromHexString("040001000000010000"), s, [(s.Columns[0], null)]));
    }

    [Fact]
    public void ASlotWithASparseColumn_IsUndecodable_NotAnotherRow()
    {
        // Ligne réelle de SQL Server 2022 (RowDecoderTests) : (id 1, a 10, s1 et s2 éparses, v 'b1'), clé id.
        var s = new TableSchema(1, "dbo", "sp", 1, 1, new[]
        {
            Col(3, "s1", 0, 0, 56, 4), Col(4, "s2", 0, 0, 56, 4),
            Col(1, "id", 4, 1, 56, 4, keyOrdinal: 1), Col(2, "a", 8, 2, 56, 4), Col(5, "v", -1, 3, 167, 5) with { CodePage = 1252 },
        });
        var id = s.Columns.Single(c => c.Name == "id");
        var row = Convert.FromHexString("30000C00010000000A000000030000010015006231");

        Assert.Equal(new CurrentRowResult(null, CurrentRowReader.Undecodable), CurrentRowReader.FromSlot(row, s, [(id, 1)]));
        Assert.Equal("the current row holds columns that cannot be decoded", CurrentRowReader.Undecodable);
        Assert.Null(CurrentRowReader.FromSlot(row, s, [(id, 2)]));                                       // une autre clé : pas cette ligne
        Assert.Null(CurrentRowReader.FromSlot(Convert.FromHexString("30000C0001"), s, [(id, 1)]));        // tronquée : pas cette ligne
    }

    [Fact]
    public void ASlotWithTheKey_IsTheRow()
    {
        var s = new TableSchema(1, "dbo", "n", 1, 1, new[] { Col(1, "k", 4, 1, 56, 4, keyOrdinal: 1), Col(2, "v", 8, 2, 56, 4) });
        var row = Convert.FromHexString("10000C00070000000100000002000000");

        Assert.Equal(row, CurrentRowReader.FromSlot(row, s, [(s.Columns[0], 7)])!.Row);
        Assert.Null(CurrentRowReader.FromSlot(null, s, [(s.Columns[0], 7)]));
    }

    [Fact]
    public void Sendable_InAnUnknownDatabaseCodePage_IsFalse_NotAnException()
    {
        Assert.False(CurrentRowReader.Sendable("abc", 1252, 12345));
        Assert.True(CurrentRowReader.Sendable("abc", 1252, 1252));
    }

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
