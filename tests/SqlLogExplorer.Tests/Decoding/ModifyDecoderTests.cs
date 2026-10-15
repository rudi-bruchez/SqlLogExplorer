using SqlLogExplorer.Decoding;
using SqlLogExplorer.Models;
using Xunit.Abstractions;

namespace SqlLogExplorer.Tests.Decoding;

public class ModifyDecoderTests(ITestOutputHelper output)
{
    private static byte[] Hex(string h) => Convert.FromHexString(h.Replace(" ", ""));

    // spec03 §2 : t(id int, a int, b varchar(20), c bit, d bit), ligne (1, 10, 'abc', 1, 0).
    private static readonly byte[] SpecRow = Hex("30000D00010000000A000000F105000001001700616263");
    private static readonly byte[] AbcdefRow = Hex("30000D00010000000A000000F10500000100 1A00616263646566");

    private static LogRecord ModifyRow(byte[]? before, byte[]? after, int offset, int size, string? description = null) =>
        new("00000001:00000001:0001", "LOP_MODIFY_ROW", "LCX_CLUSTERED", "0000:00000001", "dbo.t", before, after)
        { OffsetInRow = offset, ModifySize = size, Description = description };

    public static TheoryData<string> Versions => new() { "2017", "2019", "2022", "2025" };

    // ---- Cas de spec03 §2 ----

    [Fact]
    public void FixedColumn_ForwardAndBackward()
    {
        var f = Assert.Single(ModifyDecoder.Decode(ModifyRow(Hex("0A"), Hex("0B"), 8, 1))!);

        var after = ModifyDecoder.ApplyForward(SpecRow, f);
        var expected = (byte[])SpecRow.Clone(); expected[8] = 0x0B;
        Assert.Equal(expected, after);
        Assert.Equal(SpecRow, ModifyDecoder.ApplyBackward(after, f));
    }

    [Fact]
    public void VariableColumn_GrowsTheRowThenShrinksItBack()
    {
        var f = Assert.Single(ModifyDecoder.Decode(ModifyRow(Hex("1700616263"), Hex("1A00616263646566"), 18, 5))!);

        var grown = ModifyDecoder.ApplyForward(SpecRow, f);
        Assert.Equal(SpecRow.Length + 3, grown.Length);
        Assert.Equal(AbcdefRow, grown);
        Assert.Equal(SpecRow, ModifyDecoder.ApplyBackward(grown, f));
    }

    [Fact]
    public void CompensationModifyRow_IsAppliedForwardUnchecked()
    {
        var f = Assert.Single(ModifyDecoder.Decode(ModifyRow([], Hex("1700616263"), 18, 8, "COMPENSATION"))!);

        Assert.Equal(8, f.ForwardLength);
        Assert.Equal(SpecRow, ModifyDecoder.ApplyForward(AbcdefRow, f));
    }

    [Fact]
    public void CompensationModifyRow_CannotBeApplied_Backward()
    {
        var f = Assert.Single(ModifyDecoder.Decode(ModifyRow([], Hex("1700616263"), 18, 8, "COMPENSATION"))!);

        Assert.Throws<ReplayCheckException>(() => ModifyDecoder.ApplyBackward(SpecRow, f));
    }

    [Fact]
    public void ForwardFragment_WhoseBeforeBytesAreNotInTheRow_Throws()
    {
        var f = Assert.Single(ModifyDecoder.Decode(ModifyRow(Hex("0C"), Hex("0B"), 8, 1))!);

        Assert.Throws<ReplayCheckException>(() => ModifyDecoder.ApplyForward(SpecRow, f));
    }

    [Fact]
    public void BackwardFragment_WhoseAfterBytesAreNotInTheRow_Throws()
    {
        var f = Assert.Single(ModifyDecoder.Decode(ModifyRow(Hex("0A"), Hex("0B"), 8, 1))!);

        Assert.Throws<ReplayCheckException>(() => ModifyDecoder.ApplyBackward(SpecRow, f));
    }

    [Fact]
    public void ForwardFragment_PastTheEndOfTheRow_Throws()
    {
        var f = new Fragment(40, Hex("0A"), Hex("0B"), 1);

        Assert.Throws<ReplayCheckException>(() => ModifyDecoder.ApplyForward(SpecRow, f));
    }

    [Fact]
    public void OtherOperations_AreNotModifyRecords()
    {
        Assert.Null(ModifyDecoder.Decode(new LogRecord("l", "LOP_INSERT_ROWS", null, null, null, SpecRow, null)));
    }

    // ---- LOP_MODIFY_COLUMNS ----

    private static LogRecord Record(string version, string scenario, string table, int index)
    {
        var (_, records) = FixtureSet.Load(version, scenario).ForTable(table);
        return records.Where(r => r.Operation == "LOP_MODIFY_COLUMNS").ElementAt(index);
    }

    [Theory, MemberData(nameof(Versions))]
    public void ModifyColumns_OfSpecRowU_DecodesIntoTheFragmentsOfTheNote(string version)   // a = 99, b = 'zz' : 8:1>1 68:5>4
    {
        var fragments = ModifyDecoder.Decode(Record(version, "modify", "dbo.u", 0))!;

        Assert.Equal(2, fragments.Count);
        Assert.Equal((8, 1, 1), (fragments[0].Offset, fragments[0].ForwardLength, fragments[0].After.Length));
        Assert.Equal((68, 5, 4), (fragments[1].Offset, fragments[1].ForwardLength, fragments[1].After.Length));
        Assert.Equal(fragments[0].ForwardLength, fragments[0].Before.Length);
        Assert.Equal(fragments[1].ForwardLength, fragments[1].Before.Length);
    }

    [Theory, MemberData(nameof(Versions))]
    public void ModifyColumns_WithoutLogRecord_IsUnknown(string version)
    {
        var r = Record(version, "modify", "dbo.u", 0) with { LogRecordBytes = null };

        Assert.Null(ModifyDecoder.Decode(r));
    }

    [Theory, MemberData(nameof(Versions))]
    public void ModifyColumns_WhoseElementsRunPastTheEnd_IsUnknown(string version)
    {
        var r = Record(version, "modify", "dbo.u", 0);
        var cut = r.LogRecordBytes![..^5];   // le dernier élément (4 octets) déborde

        Assert.Null(ModifyDecoder.Decode(r with { LogRecordBytes = cut }));
        Assert.Null(ModifyDecoder.Decode(r with { LogRecordBytes = cut[..10] }));
    }

    [Theory, MemberData(nameof(Versions))]
    public void ModifyColumns_WhoseCountIsNotNumElements_IsUnknown(string version)
    {
        var r = Record(version, "modify", "dbo.u", 0);

        Assert.Null(ModifyDecoder.Decode(r with { NumElements = r.NumElements + 2 }));
        Assert.Null(ModifyDecoder.Decode(r with { NumElements = null }));
    }

    // ---- Rejeu des fixtures : de l'image d'insert à l'image de delete ----

    private sealed class RowState(byte[] bytes) { public byte[] Bytes = bytes; public int Modifications; }

    private static readonly HashSet<string> DataOperations = ["LOP_INSERT_ROWS", "LOP_DELETE_ROWS", "LOP_MODIFY_ROW", "LOP_MODIFY_COLUMNS"];
    private static readonly HashSet<string> DataContexts = ["LCX_HEAP", "LCX_CLUSTERED", "LCX_MARK_AS_GHOST"];

    /// <summary>Rejoue tous les enregistrements de données des tables utilisateur, lignes suivies par page et emplacement (le journal n'est pas filtré par transaction : l'effet net d'une annulation est nul).</summary>
    private (int ModifyColumns, int ModifyRows, int Compared, int ComparedAfterModify) Replay(string version, string scenario)
    {
        var fixture = FixtureSet.Load(version, scenario);
        var userUnits = fixture.Schemas.Values.Where(s => s.IsDataRows).Select(s => s.AllocUnitId).ToHashSet();
        var pages = new Dictionary<(long, string), List<RowState>>();
        int modifyColumns = 0, modifyRows = 0, compared = 0, comparedAfterModify = 0;

        foreach (var r in fixture.Log
            .Where(r => r.AllocUnitId is { } au && userUnits.Contains(au) && DataOperations.Contains(r.Operation)
                        && r.Context is not null && DataContexts.Contains(r.Context) && r.PageId is not null && r.SlotId is not null)
            .OrderBy(r => r.Lsn, StringComparer.Ordinal))
        {
            var rows = pages.TryGetValue((r.AllocUnitId!.Value, r.PageId!), out var l) ? l : pages[(r.AllocUnitId.Value, r.PageId!)] = [];
            var slot = r.SlotId!.Value;
            var at = $"{version} {scenario} {r.Operation} {r.Lsn} page {r.PageId} slot {slot}";

            switch (r.Operation)
            {
                case "LOP_INSERT_ROWS" when r.Context == "LCX_MARK_AS_GHOST":   // annulation d'un delete : la ligne fantôme redevient normale
                    rows[slot].Bytes = r.RowLogContents0!;
                    break;
                case "LOP_INSERT_ROWS":
                    rows.Insert(slot, new RowState(r.RowLogContents0!));
                    break;
                case "LOP_DELETE_ROWS":
                    if (r.RowLogContents0 is { Length: > 0 } image)
                    {
                        Assert.True(image.SequenceEqual(rows[slot].Bytes), $"Delete image differs from the replayed row: {at}\n{Convert.ToHexString(image)}\n{Convert.ToHexString(rows[slot].Bytes)}");
                        compared++;
                        if (rows[slot].Modifications > 0) comparedAfterModify++;
                    }
                    if (r.Context != "LCX_MARK_AS_GHOST") rows.RemoveAt(slot);   // un delete physique libère l'emplacement ; un fantôme le garde
                    break;
                default:
                    var fragments = ModifyDecoder.Decode(r) ?? throw new InvalidOperationException($"Layout unknown: {at}");
                    Assert.NotEmpty(fragments);
                    if (r.Operation == "LOP_MODIFY_COLUMNS") modifyColumns++; else modifyRows++;
                    var before = rows[slot].Bytes;
                    var row = before;
                    foreach (var f in fragments) row = ModifyDecoder.ApplyForward(row, f);   // lève si les octets d'avant manquent
                    if (!r.IsCompensation)
                    {
                        var back = row;
                        foreach (var f in Enumerable.Reverse(fragments)) back = ModifyDecoder.ApplyBackward(back, f);
                        Assert.True(before.SequenceEqual(back), $"Undo does not give back the row: {at}");
                    }
                    rows[slot].Bytes = row;
                    rows[slot].Modifications++;
                    break;
            }
        }

        output.WriteLine($"{version} {scenario}: {modifyColumns} LOP_MODIFY_COLUMNS, {modifyRows} LOP_MODIFY_ROW, {compared} delete images compared ({comparedAfterModify} after at least one modification)");
        return (modifyColumns, modifyRows, compared, comparedAfterModify);
    }

    [Theory, MemberData(nameof(Versions))]
    public void Fixtures_ReplayFromInsertImageReachesTheDeleteImage(string version)
    {
        var scenarios = version == "2017" ? new[] { "modify", "versioned" } : ["modify", "versioned", "versioned_adr"];
        var total = scenarios.Select(s => Replay(version, s)).Aggregate((a, b) =>
            (a.ModifyColumns + b.ModifyColumns, a.ModifyRows + b.ModifyRows, a.Compared + b.Compared, a.ComparedAfterModify + b.ComparedAfterModify));

        output.WriteLine($"{version} total: {total}");
        // Les comptes des fixtures : un écart veut dire que le rejeu ne couvre plus ce qu'il couvrait.
        Assert.Equal(version == "2017" ? (6, 21, 3, 2) : (8, 23, 4, 3), total);
    }
}
