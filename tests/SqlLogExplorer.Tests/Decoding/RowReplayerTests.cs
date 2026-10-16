using SqlLogExplorer.Decoding;
using SqlLogExplorer.Models;
using Xunit.Abstractions;

namespace SqlLogExplorer.Tests.Decoding;

public class RowReplayerTests(ITestOutputHelper output)
{
    private static byte[] Hex(string h) => Convert.FromHexString(h.Replace(" ", ""));

    // spec03 §2 : t(id int, a int, b varchar(20), c bit, d bit), ligne (1, 10, 'abc', 1, 0).
    private static readonly byte[] SpecRow = Hex("30000D00010000000A000000F105000001001700616263");
    private static readonly byte[] AbcdefRow = Hex("30000D00010000000A000000F10500000100 1A00616263646566");

    private static byte[] WithA(byte[] row, byte a) { var r = (byte[])row.Clone(); r[8] = a; return r; }

    private static string L(int n) => $"00000001:00000001:{n:X4}";
    private static string Tx(string name) => $"0000:{name}";

    private static LogRecord Insert(int n, string tx, byte[] image) =>
        new(L(n), "LOP_INSERT_ROWS", "LCX_CLUSTERED", Tx(tx), "dbo.t", image, null);

    private static LogRecord Delete(int n, string tx, byte[] image) =>
        new(L(n), "LOP_DELETE_ROWS", "LCX_CLUSTERED", Tx(tx), "dbo.t", image, null);

    private static LogRecord Modify(int n, string? tx, byte[] before, byte[] after, int offset, int size) =>
        new(L(n), "LOP_MODIFY_ROW", "LCX_CLUSTERED", tx is null ? null : Tx(tx), "dbo.t", before, after) { OffsetInRow = offset, ModifySize = size };

    // a : 10 -> 11, puis b : 'abc' -> 'abcdef'
    private static LogRecord SetA(int n, string tx, byte from = 0x0A, byte to = 0x0B) => Modify(n, tx, [from], [to], 8, 1);
    private static LogRecord GrowB(int n, string tx) => Modify(n, tx, Hex("1700616263"), Hex("1A00616263646566"), 18, 5);

    private static readonly IReadOnlyDictionary<string, TxOutcome> AllCommitted = new Dictionary<string, TxOutcome>
    {
        [Tx("A")] = TxOutcome.Committed, [Tx("B")] = TxOutcome.Committed, [Tx("C")] = TxOutcome.Committed, [Tx("D")] = TxOutcome.Committed,
    };
    private static readonly IReadOnlySet<string> NoReverts = new HashSet<string>();

    private static ReplayResult Replay(IReadOnlyList<LogRecord> chain, int selected, IReadOnlyDictionary<string, TxOutcome>? outcomes = null,
        IReadOnlySet<string>? reverts = null, byte[]? currentRow = null, IReadOnlyDictionary<string, string>? firstUndoLsns = null) =>
        RowReplayer.Replay(chain, L(selected), outcomes ?? AllCommitted, reverts ?? NoReverts, firstUndoLsns ?? new Dictionary<string, string>(), currentRow);

    private static IReadOnlyDictionary<string, TxOutcome> With(params (string Tx, TxOutcome Outcome)[] more)
    {
        var d = new Dictionary<string, TxOutcome>(AllCommitted);
        foreach (var (tx, o) in more) d[Tx(tx)] = o;
        return d;
    }

    // ---- Cas faits à la main ----

    [Fact]
    public void Insert_ThenTwoModifies_RebuildsForwards()
    {
        var chain = new[] { Insert(1, "A", SpecRow), SetA(2, "B"), GrowB(3, "C") };

        var r = Replay(chain, 3);

        Assert.Equal(WithA(SpecRow, 0x0B), r.Before);
        Assert.Equal(WithA(AbcdefRow, 0x0B), r.After);
        Assert.Equal(AnchorKind.Insert, r.Anchor);
        Assert.Equal(L(1), r.AnchorLsn);
        Assert.Equal($"rebuilt forwards from INSERT at LSN {L(1)}", r.Status);
        Assert.False(r.IncludesUnknownOutcome);
    }

    [Fact]
    public void Delete_AfterTwoModifies_RebuildsBackwards()
    {
        var chain = new[] { SetA(2, "B"), GrowB(3, "C"), Delete(4, "D", WithA(AbcdefRow, 0x0B)) };

        var r = Replay(chain, 3);

        Assert.Equal(WithA(SpecRow, 0x0B), r.Before);
        Assert.Equal(WithA(AbcdefRow, 0x0B), r.After);
        Assert.Equal(AnchorKind.Delete, r.Anchor);
        Assert.Equal(L(4), r.AnchorLsn);
        Assert.Equal($"rebuilt backwards from DELETE at LSN {L(4)}", r.Status);
    }

    [Fact]
    public void Delete_SelectedIsTheFirstModify_GivesTheInsertedRow()
    {
        var chain = new[] { SetA(2, "B"), GrowB(3, "C"), Delete(4, "D", WithA(AbcdefRow, 0x0B)) };

        var r = Replay(chain, 2);

        Assert.Equal(SpecRow, r.Before);
        Assert.Equal(WithA(SpecRow, 0x0B), r.After);
    }

    [Fact]
    public void RolledBackModify_BetweenCommittedOnes_IsSkippedForwards()
    {
        var outcomes = With(("R", TxOutcome.RolledBack));
        var withRollback = new[] { Insert(1, "A", SpecRow), SetA(2, "B"), SetA(3, "R", 0x0B, 0x63), GrowB(4, "C") };

        var r = Replay(withRollback, 4, outcomes);

        Assert.Equal(WithA(SpecRow, 0x0B), r.Before);
        Assert.Equal(WithA(AbcdefRow, 0x0B), r.After);
        var without = Replay([Insert(1, "A", SpecRow), SetA(2, "B"), GrowB(4, "C")], 4);
        Assert.Equal(without.Before, r.Before);
        Assert.Equal(without.After, r.After);
        Assert.Equal(without.Status, r.Status);
    }

    [Fact]
    public void RolledBackModify_BetweenCommittedOnes_IsSkippedBackwards()
    {
        var outcomes = With(("R", TxOutcome.RolledBack));
        var final = WithA(AbcdefRow, 0x0B);
        var withRollback = new[] { SetA(2, "B"), SetA(3, "R", 0x0B, 0x63), GrowB(4, "C"), Delete(5, "D", final) };

        var r = Replay(withRollback, 2, outcomes);

        Assert.Equal(SpecRow, r.Before);
        Assert.Equal(WithA(SpecRow, 0x0B), r.After);
        Assert.Equal(AnchorKind.Delete, r.Anchor);
    }

    [Fact]
    public void SelectedRecordInARolledBackTransaction_ShowsItsChangeBeforeTheUndo()
    {
        var outcomes = With(("R", TxOutcome.RolledBack));
        var chain = new[] { Insert(1, "A", SpecRow), SetA(2, "B"), SetA(3, "R", 0x0B, 0x63), GrowB(4, "C") };

        var r = Replay(chain, 3, outcomes);

        Assert.Equal(WithA(SpecRow, 0x0B), r.Before);
        Assert.Equal(WithA(SpecRow, 0x63), r.After);
        Assert.StartsWith("rebuilt forwards", r.Status);
    }

    [Fact]
    public void SelectedRecordInARolledBackTransaction_WithOnlyADeleteAnchor_AsksForForwards()
    {
        var outcomes = With(("R", TxOutcome.RolledBack));
        var chain = new[] { SetA(3, "R", 0x0A, 0x63), Delete(4, "D", SpecRow) };

        var r = Replay(chain, 3, outcomes);

        Assert.Null(r.After);
        Assert.Equal("rollback in the chain, rebuild forwards only", r.Status);
    }

    [Fact]
    public void OtherRecordsOfTheSelectedRolledBackTransaction_AreKeptUpToTheSelectedOne()
    {
        var outcomes = With(("R", TxOutcome.RolledBack));
        var chain = new[] { Insert(1, "A", SpecRow), SetA(2, "R"), SetA(3, "R", 0x0B, 0x63), SetA(4, "R", 0x63, 0x64) };

        var r = Replay(chain, 3, outcomes);

        Assert.Equal(WithA(SpecRow, 0x0B), r.Before);
        Assert.Equal(WithA(SpecRow, 0x63), r.After);
    }

    [Fact]
    public void PartlyRolledBackModify_BeforeTheSelectedRecord_StopsTheReplay()
    {
        var outcomes = With(("P", TxOutcome.PartlyRolledBack));
        var chain = new[] { Insert(1, "A", SpecRow), SetA(2, "P"), GrowB(3, "C") };

        var r = Replay(chain, 3, outcomes);

        Assert.Null(r.After);
        Assert.Equal($"partial rollback at LSN {L(2)}, not replayed", r.Status);
    }

    [Fact]
    public void PartlyRolledBackModify_BetweenTheSelectedRecordAndTheDelete_StopsBackwards()
    {
        var outcomes = With(("P", TxOutcome.PartlyRolledBack));
        var chain = new[] { SetA(2, "B"), GrowB(3, "P"), Delete(4, "D", WithA(AbcdefRow, 0x0B)) };

        var r = Replay(chain, 2, outcomes);

        Assert.Equal($"partial rollback at LSN {L(3)}, not replayed", r.Status);
    }

    [Fact]
    public void SelectedRecordInAPartlyRolledBackTransaction_IsNotReplayed()
    {
        var outcomes = With(("P", TxOutcome.PartlyRolledBack));
        var chain = new[] { Insert(1, "A", SpecRow), SetA(2, "P") };

        Assert.Equal($"partial rollback at LSN {L(2)}, not replayed", Replay(chain, 2, outcomes).Status);
    }

    [Fact]
    public void ModifyWhoseBeforeBytesDoNotMatch_BreaksTheChain()
    {
        var chain = new[] { Insert(1, "A", SpecRow), SetA(2, "B", 0x0C, 0x0D) };

        var r = Replay(chain, 2);

        Assert.Null(r.After);
        Assert.Equal($"chain broken at LSN {L(2)}", r.Status);
    }

    [Fact]
    public void ModifyPastTheEndOfTheRow_BreaksTheChain()
    {
        var chain = new[] { Insert(1, "A", SpecRow), Modify(2, "B", [0x0A], [0x0B], 400, 1) };

        Assert.Equal($"chain broken at LSN {L(2)}", Replay(chain, 2).Status);
    }

    [Fact]
    public void AdrRevertAfterTheSelectedRecord_StopsEveryReplayOfTheRow()
    {
        var outcomes = With(("P", TxOutcome.PartlyRolledBack));
        // Le delete est après l'enregistrement choisi ; la révocation P, entre les deux, est dans la même vie de la clé.
        var chain = new[] { SetA(2, "B"), GrowB(3, "C"), SetA(4, "P", 0x0B, 0x0C), Delete(5, "D", WithA(AbcdefRow, 0x0C)) };

        var withRevert = Replay(chain, 3, outcomes, new HashSet<string> { Tx("P") });
        var withoutRevert = Replay(chain, 3, With(("P", TxOutcome.Committed)));

        Assert.Null(withRevert.After);
        Assert.Equal("ADR revert in the chain, not replayed", withRevert.Status);
        Assert.Equal(AnchorKind.Delete, withoutRevert.Anchor);
        Assert.Equal(WithA(AbcdefRow, 0x0B), withoutRevert.After);
        Assert.Equal($"rebuilt backwards from DELETE at LSN {L(5)}", withoutRevert.Status);
    }

    // Un delete sépare deux vies de la clé (§7.4) : une révocation dans l'une n'arrête pas le rejeu de l'autre.
    [Fact]
    public void AdrRevertInAnotherLifeOfTheKey_DoesNotStopTheReplay()
    {
        var outcomes = With(("P", TxOutcome.PartlyRolledBack));
        var reverts = new HashSet<string> { Tx("P") };

        // Révocation dans la vie suivante : la ligne est réinsérée par P après le delete.
        var after = new[] { SetA(2, "B"), GrowB(3, "C"), Delete(4, "D", WithA(AbcdefRow, 0x0B)), Insert(5, "P", SpecRow) };
        var r = Replay(after, 3, outcomes, reverts);
        Assert.Equal($"rebuilt backwards from DELETE at LSN {L(4)}", r.Status);
        Assert.Equal(WithA(AbcdefRow, 0x0B), r.After);

        // Révocation dans la vie précédente : la nouvelle vie se rejoue depuis son insert.
        var before = new[] { Insert(1, "A", SpecRow), SetA(2, "P"), Delete(3, "D", WithA(SpecRow, 0x0B)), Insert(4, "B", SpecRow), GrowB(5, "C") };
        var f = Replay(before, 5, outcomes, reverts);
        Assert.Equal($"rebuilt forwards from INSERT at LSN {L(4)}", f.Status);
        Assert.Equal(AbcdefRow, f.After);
    }

    [Fact]
    public void AdrRevertBeforeTheSelectedRecord_StopsTheReplayToo()
    {
        var outcomes = With(("P", TxOutcome.PartlyRolledBack));
        var chain = new[] { Insert(1, "A", SpecRow), SetA(2, "P"), GrowB(3, "C") };

        Assert.Equal("ADR revert in the chain, not replayed", Replay(chain, 3, outcomes, new HashSet<string> { Tx("P") }).Status);
    }

    [Fact]
    public void UnknownOutcome_IsAppliedAndFlagged()
    {
        var outcomes = With(("U", TxOutcome.Unknown));
        var chain = new[] { Insert(1, "A", SpecRow), SetA(2, "U"), GrowB(3, "C") };

        var r = Replay(chain, 3, outcomes);

        Assert.Equal(WithA(AbcdefRow, 0x0B), r.After);
        Assert.True(r.IncludesUnknownOutcome);
        Assert.False(Replay(chain, 3, With(("U", TxOutcome.Committed))).IncludesUnknownOutcome);
    }

    [Fact]
    public void NoInsertNoDeleteNoCurrentRow_HasNoImage()
    {
        var r = Replay([SetA(2, "B"), GrowB(3, "C")], 3);

        Assert.Null(r.Before);
        Assert.Null(r.After);
        Assert.Equal(AnchorKind.None, r.Anchor);
        Assert.Equal("no full image of this row available", r.Status);
    }

    [Fact]
    public void CurrentRow_AnchorsTheReplayWhenThereIsNoDelete()
    {
        var chain = new[] { SetA(2, "B"), GrowB(3, "C") };

        var r = Replay(chain, 3, currentRow: WithA(AbcdefRow, 0x0B));

        Assert.Equal(AnchorKind.CurrentRow, r.Anchor);
        Assert.Null(r.AnchorLsn);
        Assert.Equal(WithA(SpecRow, 0x0B), r.Before);
        Assert.Equal(WithA(AbcdefRow, 0x0B), r.After);
        Assert.Equal("rebuilt backwards from the current row", r.Status);
    }

    [Fact]
    public void CurrentRow_GivesWayToTheDelete()
    {
        var chain = new[] { SetA(2, "B"), Delete(4, "D", WithA(SpecRow, 0x0B)) };

        Assert.Equal(AnchorKind.Delete, Replay(chain, 2, currentRow: SpecRow).Anchor);
    }

    [Fact]
    public void CurrentRow_IsNotUsedAcrossALaterInsert()
    {
        var chain = new[] { SetA(2, "B"), Insert(4, "D", SpecRow) };

        Assert.Equal("no full image of this row available", Replay(chain, 2, currentRow: SpecRow).Status);
    }

    [Fact]
    public void ModifyProducingAForwardingStub_EndsTheChain()
    {
        var chain = new[] { Insert(1, "A", SpecRow), Modify(2, "B", [0x30], [0x04], 0, 1) };

        var r = Replay(chain, 2);

        Assert.Null(r.After);
        Assert.Equal("row forwarded, not followed", r.Status);
    }

    [Fact]
    public void ModifyColumnsWithoutLogRecord_IsNotDecodable()
    {
        var undecodable = new LogRecord(L(2), "LOP_MODIFY_COLUMNS", "LCX_CLUSTERED", Tx("B"), "dbo.t", [1], [1]) { NumElements = 8 };

        var r = Replay([Insert(1, "A", SpecRow), undecodable], 2);

        Assert.Equal($"modification not decodable at LSN {L(2)}", r.Status);
    }

    [Fact]
    public void LastInsertIsTheAnchor_NotTheFirst()
    {
        var old = WithA(SpecRow, 0x05);
        var chain = new[] { Insert(1, "A", old), Insert(2, "A", SpecRow), SetA(3, "B") };

        var r = Replay(chain, 3);

        Assert.Equal(L(2), r.AnchorLsn);
        Assert.Equal(WithA(SpecRow, 0x0B), r.After);
    }

    [Fact]
    public void FirstRecordAfterADelete_HasNoImageBeforeIt()
    {
        // Sous ADR la clé revient par un modify sur le fantôme, pas par un insert (spec03 §7.4) : la vie précédente ne se rejoue pas dans la nouvelle.
        var chain = new[] { Insert(1, "A", SpecRow), Delete(2, "D", SpecRow), SetA(3, "B"), Delete(4, "D", WithA(SpecRow, 0x0B)) };

        var r = Replay(chain, 3);

        Assert.Null(r.Before);
        Assert.Null(r.After);
        Assert.Equal("row inserted again after a delete, no image before it", r.Status);
    }

    [Fact]
    public void SecondRecordAfterADelete_ReplaysBackwardsWithinTheNewLife()
    {
        var chain = new[] { Insert(1, "A", SpecRow), Delete(2, "D", SpecRow), SetA(3, "B"), GrowB(4, "C"), Delete(5, "D", WithA(AbcdefRow, 0x0B)) };

        var r = Replay(chain, 4);

        Assert.Equal(WithA(SpecRow, 0x0B), r.Before);
        Assert.Equal(WithA(AbcdefRow, 0x0B), r.After);
        Assert.Equal(L(5), r.AnchorLsn);
    }

    [Fact]
    public void FirstRecordAfterADelete_IsNotReplayedFromTheCurrentRowEither()
    {
        var chain = new[] { Insert(1, "A", SpecRow), Delete(2, "D", SpecRow), SetA(3, "B") };

        Assert.Equal("row inserted again after a delete, no image before it", Replay(chain, 3, currentRow: WithA(SpecRow, 0x0B)).Status);
    }

    [Fact]
    public void UndoingIntoAGhostImage_MeansTheKeyWasInsertedAgain()
    {
        // Le delete précédent est hors des enregistrements passés : seul l'octet d'état de l'avant (fantôme, type 6) le trahit.
        var chain = new[] { Modify(2, "B", [0x7C], [0x30], 0, 1), Delete(3, "D", SpecRow) };

        var r = Replay(chain, 2);

        Assert.Null(r.Before);
        Assert.Equal("row inserted again after a delete, no image before it", r.Status);
    }

    [Fact]
    public void RolledBackSelectedRecord_AfterASavepointUndo_IsNotReplayed()
    {
        var outcomes = With(("R", TxOutcome.RolledBack));
        var chain = new[] { Insert(1, "A", SpecRow), SetA(2, "R"), SetA(3, "R", 0x0B, 0x0C), GrowB(4, "R") };
        var undoAfterSecond = new Dictionary<string, string> { [Tx("R")] = L(3) + "0" };   // la compensation vient après le LSN 3 mais avant le 4
        var undoAtTheEnd = new Dictionary<string, string> { [Tx("R")] = L(9) };

        var before = Replay(chain, 4, outcomes, firstUndoLsns: undoAfterSecond);
        var after = Replay(chain, 4, outcomes, firstUndoLsns: undoAtTheEnd);

        Assert.Null(before.After);
        Assert.Equal("partial rollback before the selected record, not replayed", before.Status);
        Assert.StartsWith("rebuilt forwards", after.Status);
    }

    [Fact]
    public void RolledBackSelectedRecord_BeforeTheSavepointUndo_IsStillReplayed()
    {
        var outcomes = With(("R", TxOutcome.RolledBack));
        var chain = new[] { Insert(1, "A", SpecRow), SetA(2, "R"), SetA(3, "R", 0x0B, 0x0C) };

        var r = Replay(chain, 2, outcomes, firstUndoLsns: new Dictionary<string, string> { [Tx("R")] = L(3) });

        Assert.Equal(WithA(SpecRow, 0x0B), r.After);
    }

    [Fact]
    public void TransactionIdNullOrAbsentFromOutcomes_IsUnknown()
    {
        var chain = new[] { Insert(1, "A", SpecRow), SetA(2, "Z"), Modify(3, null, [0x0B], [0x0C], 8, 1) };

        var r = Replay(chain, 3);

        Assert.Equal(WithA(SpecRow, 0x0C), r.After);
        Assert.True(r.IncludesUnknownOutcome);
    }

    // ---- Classify et AdrReverts ----

    private static LogRecord Tr(string tx, string operation, string? description = null) =>
        new(L(9), operation, "LCX_NULL", Tx(tx), null, null, null) { Description = description };

    [Fact]
    public void Classify_SortsTransactionsByTheirRecords()
    {
        var records = new[]
        {
            Tr("1", "LOP_ABORT_XACT"),
            Tr("2", "LOP_MODIFY_ROW", "COMPENSATION"), Tr("2", "LOP_COMMIT_XACT"),
            Tr("3", "LOP_CTR_NEST_ABORT", "COMPENSATION;Start nest Id (2) ;End nest Id (4)"), Tr("3", "LOP_COMMIT_XACT"),
            Tr("4", "LOP_COMMIT_XACT"),
            Tr("5", "LOP_MODIFY_ROW"),
            Tr("6", "LOP_MODIFY_ROW", "COMPENSATION"), Tr("6", "LOP_ABORT_XACT"),
        };

        var outcomes = RowReplayer.Classify(records);

        Assert.Equal(TxOutcome.RolledBack, outcomes[Tx("1")]);
        Assert.Equal(TxOutcome.PartlyRolledBack, outcomes[Tx("2")]);
        Assert.Equal(TxOutcome.PartlyRolledBack, outcomes[Tx("3")]);
        Assert.Equal(TxOutcome.Committed, outcomes[Tx("4")]);
        Assert.Equal(TxOutcome.Unknown, outcomes[Tx("5")]);
        Assert.Equal(TxOutcome.RolledBack, outcomes[Tx("6")]);
    }

    [Fact]
    public void AdrReverts_HoldsNestAbortOrWholeRollbackWithoutRowCompensation()
    {
        var records = new[]
        {
            Tr("3", "LOP_CTR_NEST_ABORT", "COMPENSATION;Start nest Id (2)"), Tr("3", "LOP_COMMIT_XACT"),
            Tr("4", "LOP_CTR_NEST_ABORT", "COMPENSATION;Start nest Id (2)"), Tr("4", "LOP_MODIFY_ROW", "COMPENSATION"), Tr("4", "LOP_COMMIT_XACT"),
            Tr("5", "LOP_COMMIT_XACT"),
            Tr("7", "LOP_CTR_NEST_ABORT", "COMPENSATION;Start nest Id (2)"), Tr("7", "LOP_LOCK_XACT", "COMPENSATION"), Tr("7", "LOP_COMMIT_XACT"),   // fixture ADR : le verrou compensé n'est pas une compensation de ligne
            Tr("6", "LOP_CTR_NEST_ABORT", "COMPENSATION;Start nest Id (2)"),   // jamais validée : l'issue ne compte pas
            Tr("8", "LOP_MODIFY_ROW"), Tr("8", "LOP_ABORT_XACT"),               // annulée en entier sans compensation de ligne (ADR)
            Tr("9", "LOP_MODIFY_ROW"), Tr("9", "LOP_MODIFY_ROW", "COMPENSATION"), Tr("9", "LOP_ABORT_XACT"),   // annulée avec compensation : pas ADR
        };

        var reverts = RowReplayer.AdrReverts(records);

        Assert.Equal([Tx("3"), Tx("6"), Tx("7"), Tx("8")], reverts.OrderBy(x => x));
    }

    [Fact]
    public void FirstUndoLsns_IsTheEarliestRowCompensationOrNestAbort()
    {
        var records = new[]
        {
            Tr("1", "LOP_MODIFY_ROW", "COMPENSATION") with { Lsn = L(7) }, Tr("1", "LOP_CTR_NEST_ABORT", "COMPENSATION") with { Lsn = L(5) },
            Tr("1", "LOP_LOCK_XACT", "COMPENSATION") with { Lsn = L(2) },
            Tr("2", "LOP_COMMIT_XACT"),
        };

        var lsns = RowReplayer.FirstUndoLsns(records);

        Assert.Equal(L(5), lsns[Tx("1")]);
        Assert.False(lsns.ContainsKey(Tx("2")));
    }

    // ---- Fixtures ----

    public static TheoryData<string> Versions => new() { "2017", "2019", "2022", "2025" };
    public static TheoryData<string> AdrVersions => new() { "2019", "2022", "2025" };

    private static string Values(byte[] row, TableSchema schema) =>
        string.Join(", ", RowDecoder.Decode(row, schema).Columns.Select(c => $"{c.Name}={c.Value ?? "NULL"}"));

    [Theory, MemberData(nameof(Versions))]
    public void Fixture_Modify_ReplayedRowsMatchTheScenario(string version)
    {
        var fixture = FixtureSet.Load(version, "modify");
        var (schema, all) = fixture.ForTable("dbo.t");
        var chain = all.Where(r => !r.IsCompensation).ToList();
        var outcomes = RowReplayer.Classify(fixture.Log);
        var reverts = RowReplayer.AdrReverts(fixture.Log);
        var modifies = chain.Where(r => r.Operation is "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS").ToList();

        Assert.Empty(reverts);
        Assert.Equal(9, modifies.Count);

        // modify.sql, dans l'ordre : a = 11, b = 'abcdef', d = 1, TABLOCKX a = 20, b = NULL, b = 'zz', a = 99 annulé, a = 98 et a = 21 sous point de sauvegarde.
        string[] expected =
        [
            "id=1, a=11, b=abc, c=True, d=False",
            "id=1, a=11, b=abcdef, c=True, d=False",
            "id=1, a=11, b=abcdef, c=True, d=True",
            "id=1, a=20, b=abcdef, c=True, d=True",
            "id=1, a=20, b=NULL, c=True, d=True",
            "id=1, a=20, b=zz, c=True, d=True",
        ];
        for (var i = 0; i < expected.Length; i++)
        {
            var r = RowReplayer.Replay(chain, modifies[i].Lsn, outcomes, reverts, RowReplayer.FirstUndoLsns(fixture.Log));
            Assert.True(r.After is not null, $"{version} modify #{i}: {r.Status}");
            Assert.Equal(expected[i], Values(r.After!, schema));
            Assert.Equal(TxOutcome.Committed, outcomes[modifies[i].TransactionId!]);
            Assert.Equal(AnchorKind.Insert, r.Anchor);
            Assert.False(r.IncludesUnknownOutcome);
        }

        // La transaction annulée : l'enregistrement choisi montre son effet avant l'annulation.
        Assert.Equal(TxOutcome.RolledBack, outcomes[modifies[6].TransactionId!]);
        var rolledBack = RowReplayer.Replay(chain, modifies[6].Lsn, outcomes, reverts, RowReplayer.FirstUndoLsns(fixture.Log));
        Assert.Equal("id=1, a=99, b=zz, c=True, d=True", Values(rolledBack.After!, schema));
        Assert.StartsWith("rebuilt forwards", rolledBack.Status);

        // Le point de sauvegarde : validée avec compensation, hors des révocations ADR.
        var savepoint = modifies[7].TransactionId!;
        Assert.Equal(savepoint, modifies[8].TransactionId);
        Assert.Equal(TxOutcome.PartlyRolledBack, outcomes[savepoint]);
        Assert.DoesNotContain(savepoint, reverts);
        foreach (var r in modifies.Skip(7))
            Assert.Equal($"partial rollback at LSN {modifies[7].Lsn}, not replayed", RowReplayer.Replay(chain, r.Lsn, outcomes, reverts, RowReplayer.FirstUndoLsns(fixture.Log)).Status);
    }

    [Theory, MemberData(nameof(AdrVersions))]
    public void Fixture_VersionedAdr_EveryModifyOfTheRowStopsAtTheRevert(string version)
    {
        var fixture = FixtureSet.Load(version, "versioned_adr");
        var (_, all) = fixture.ForTable("dbo.vr");
        var chain = all.Where(r => !r.IsCompensation).ToList();
        var outcomes = RowReplayer.Classify(fixture.Log);
        var reverts = RowReplayer.AdrReverts(fixture.Log);
        var modifies = chain.Where(r => r.Operation is "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS").ToList();

        output.WriteLine(string.Join("\n", modifies.Select(m => $"{m.Lsn} {m.Operation} {m.TransactionId} {outcomes.GetValueOrDefault(m.TransactionId!)} adr={reverts.Contains(m.TransactionId!)}")));
        Assert.Equal(4, modifies.Count);
        var revert = modifies.Single(r => reverts.Contains(r.TransactionId!));
        Assert.Equal(TxOutcome.PartlyRolledBack, outcomes[revert.TransactionId!]);
        output.WriteLine($"{version}: revert at {revert.Lsn} ({revert.Operation})");

        foreach (var m in modifies)
            Assert.Equal("ADR revert in the chain, not replayed", RowReplayer.Replay(chain, m.Lsn, outcomes, reverts, RowReplayer.FirstUndoLsns(fixture.Log)).Status);
    }

    // ---- Chaînes réelles de la revue (2022, capturées avec et sans ADR, une ligne chacune) ----

    private static ReplayResult Probe(Fixture f, IReadOnlyList<LogRecord> chain, string lsn) =>
        RowReplayer.Replay(chain, lsn, RowReplayer.Classify(f.Log), RowReplayer.AdrReverts(f.Log), RowReplayer.FirstUndoLsns(f.Log));

    private static List<LogRecord> Modifies(IEnumerable<LogRecord> chain) =>
        chain.Where(r => r.Operation is "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS").ToList();

    [Fact]
    public void Real_RolledBackTransactionThatUndidASavepoint_WithoutAdr()
    {
        // BEGIN TRAN; a = 30; SAVE TRAN s; a = 31; ROLLBACK TRAN s; b = 'z'; ROLLBACK; puis b = 'final' (validé) et un delete.
        var f = FixtureSet.Load("2022", "rollback_savepoint_plain");
        var (schema, all) = f.ForTable("dbo.p");
        var chain = all.Where(r => !r.IsCompensation).ToList();
        var mods = Modifies(chain);
        Assert.Equal(4, mods.Count);

        var a30 = Probe(f, chain, mods[0].Lsn);
        var a31 = Probe(f, chain, mods[1].Lsn);
        var z = Probe(f, chain, mods[2].Lsn);
        var final = Probe(f, chain, mods[3].Lsn);

        Assert.Equal("id=2, a=30, b=b", Values(a30.After!, schema));
        Assert.Equal("id=2, a=30, b=b", Values(a31.Before!, schema));
        Assert.Equal("id=2, a=31, b=b", Values(a31.After!, schema));
        Assert.Null(z.Before);
        Assert.Null(z.After);
        Assert.Equal("partial rollback before the selected record, not replayed", z.Status);
        Assert.Equal("id=2, a=20, b=b", Values(final.Before!, schema));
        Assert.Equal("id=2, a=20, b=final", Values(final.After!, schema));
    }

    [Fact]
    public void Real_WholeRollbackAfterASavepointUndo_UnderAdr_PoisonsEveryRecordOfTheRow()
    {
        // Même scénario sous ADR : aucune compensation de ligne, les octets annulés fuient dans le modify validé suivant.
        var f = FixtureSet.Load("2022", "rollback_savepoint_adr");
        var (_, all) = f.ForTable("dbo.p");
        var chain = all.Where(r => !r.IsCompensation).ToList();
        var mods = Modifies(chain);
        Assert.Equal(4, mods.Count);
        var outcomes = RowReplayer.Classify(f.Log);

        var aborted = mods[2].TransactionId!;
        Assert.Equal(TxOutcome.RolledBack, outcomes[aborted]);
        Assert.Contains(aborted, RowReplayer.AdrReverts(f.Log));
        Assert.Equal(TxOutcome.Committed, outcomes[mods[3].TransactionId!]);
        foreach (var m in mods)
            Assert.Equal("ADR revert in the chain, not replayed", Probe(f, chain, m.Lsn).Status);
    }

    [Fact]
    public void Real_KeyInsertedAgainUnderAdr_IsALogModifyOnTheGhost()
    {
        // INSERT 7; a = 71; DELETE; INSERT 7 (modify sur le fantôme sous ADR); a = 701; b = 'g2'; DELETE; INSERT 7 (idem); b = 'g4'.
        var f = FixtureSet.Load("2022", "reinsert_adr");
        var (schema, all) = f.ForTable("dbo.p");
        var chain = all.Where(r => !r.IsCompensation).ToList();
        var mods = Modifies(chain);
        Assert.Equal(6, mods.Count);

        var reinserted = Probe(f, chain, mods[1].Lsn);
        Assert.Null(reinserted.Before);
        Assert.Equal("row inserted again after a delete, no image before it", reinserted.Status);

        var second = Probe(f, chain, mods[2].Lsn);
        Assert.Equal("id=7, a=700, b=gg", Values(second.Before!, schema));
        Assert.Equal("id=7, a=701, b=gg", Values(second.After!, schema));

        var firstLife = Probe(f, chain, mods[0].Lsn);
        Assert.Equal("id=7, a=70, b=g", Values(firstLife.Before!, schema));
        Assert.StartsWith("rebuilt forwards", firstLife.Status);

        Assert.Equal("no full image of this row available", Probe(f, chain, mods[4].Lsn).Status);
        Assert.Equal("no full image of this row available", Probe(f, chain, mods[5].Lsn).Status);
    }

    [Fact]
    public void Real_ReinsertModify_WhoseDeleteIsOutsideTheWindow_IsStillRecognised()
    {
        var f = FixtureSet.Load("2022", "reinsert_adr");
        var (_, all) = f.ForTable("dbo.p");
        var mods = Modifies(all.Where(r => !r.IsCompensation));
        var chain = all.Where(r => !r.IsCompensation && string.CompareOrdinal(r.Lsn, mods[1].Lsn) >= 0).ToList();   // le delete 0x230:0002 n'y est plus

        Assert.DoesNotContain(chain, r => r.Operation == "LOP_DELETE_ROWS" && string.CompareOrdinal(r.Lsn, mods[1].Lsn) < 0);
        var r = Probe(f, chain, mods[1].Lsn);

        Assert.Null(r.Before);
        Assert.Equal("row inserted again after a delete, no image before it", r.Status);
        Assert.Equal(AnchorKind.None, r.Anchor);
    }
}
