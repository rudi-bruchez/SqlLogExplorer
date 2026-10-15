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
        IReadOnlySet<string>? reverts = null, byte[]? currentRow = null) =>
        RowReplayer.Replay(chain, L(selected), outcomes ?? AllCommitted, reverts ?? NoReverts, currentRow);

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
        // Le delete est après l'enregistrement choisi ; l'enregistrement de la transaction P est encore après (la ligne est réinsérée).
        var chain = new[] { SetA(2, "B"), GrowB(3, "C"), Delete(4, "D", WithA(AbcdefRow, 0x0B)), Insert(5, "P", SpecRow) };

        var withRevert = Replay(chain, 3, outcomes, new HashSet<string> { Tx("P") });
        var withoutRevert = Replay(chain, 3, outcomes);

        Assert.Null(withRevert.After);
        Assert.Equal($"partial rollback at LSN {L(5)}, not replayed", withRevert.Status);
        Assert.Equal(AnchorKind.Delete, withoutRevert.Anchor);
        Assert.Equal(WithA(AbcdefRow, 0x0B), withoutRevert.After);
        Assert.Equal($"rebuilt backwards from DELETE at LSN {L(4)}", withoutRevert.Status);
    }

    [Fact]
    public void AdrRevertBeforeTheSelectedRecord_StopsTheReplayToo()
    {
        var outcomes = With(("P", TxOutcome.PartlyRolledBack));
        var chain = new[] { Insert(1, "A", SpecRow), SetA(2, "P"), GrowB(3, "C") };

        Assert.Equal($"partial rollback at LSN {L(2)}, not replayed", Replay(chain, 3, outcomes, new HashSet<string> { Tx("P") }).Status);
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
    public void AnInsertBeforeADelete_IsNotAnAnchorForARecordAfterTheDelete()
    {
        var chain = new[] { Insert(1, "A", SpecRow), Delete(2, "D", SpecRow), SetA(3, "B"), Delete(4, "D", WithA(SpecRow, 0x0B)) };

        var r = Replay(chain, 3);

        Assert.Equal(AnchorKind.Delete, r.Anchor);
        Assert.Equal(L(4), r.AnchorLsn);
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
    public void AdrReverts_HoldsTheNestAbortWithoutCompensation_NotTheOneWithBoth()
    {
        var records = new[]
        {
            Tr("3", "LOP_CTR_NEST_ABORT", "COMPENSATION;Start nest Id (2)"), Tr("3", "LOP_COMMIT_XACT"),
            Tr("4", "LOP_CTR_NEST_ABORT", "COMPENSATION;Start nest Id (2)"), Tr("4", "LOP_MODIFY_ROW", "COMPENSATION"), Tr("4", "LOP_COMMIT_XACT"),
            Tr("5", "LOP_COMMIT_XACT"),
            Tr("7", "LOP_CTR_NEST_ABORT", "COMPENSATION;Start nest Id (2)"), Tr("7", "LOP_LOCK_XACT", "COMPENSATION"), Tr("7", "LOP_COMMIT_XACT"),   // fixture ADR : le verrou compensé n'est pas une compensation de ligne
            Tr("6", "LOP_CTR_NEST_ABORT", "COMPENSATION;Start nest Id (2)"),   // jamais validée : inconnue, pas une révocation
        };

        var reverts = RowReplayer.AdrReverts(records);

        Assert.Equal([Tx("3"), Tx("7")], reverts.OrderBy(x => x));
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
            var r = RowReplayer.Replay(chain, modifies[i].Lsn, outcomes, reverts);
            Assert.True(r.After is not null, $"{version} modify #{i}: {r.Status}");
            Assert.Equal(expected[i], Values(r.After!, schema));
            Assert.Equal(TxOutcome.Committed, outcomes[modifies[i].TransactionId!]);
            Assert.Equal(AnchorKind.Insert, r.Anchor);
            Assert.False(r.IncludesUnknownOutcome);
        }

        // La transaction annulée : l'enregistrement choisi montre son effet avant l'annulation.
        Assert.Equal(TxOutcome.RolledBack, outcomes[modifies[6].TransactionId!]);
        var rolledBack = RowReplayer.Replay(chain, modifies[6].Lsn, outcomes, reverts);
        Assert.Equal("id=1, a=99, b=zz, c=True, d=True", Values(rolledBack.After!, schema));
        Assert.StartsWith("rebuilt forwards", rolledBack.Status);

        // Le point de sauvegarde : validée avec compensation, hors des révocations ADR.
        var savepoint = modifies[7].TransactionId!;
        Assert.Equal(savepoint, modifies[8].TransactionId);
        Assert.Equal(TxOutcome.PartlyRolledBack, outcomes[savepoint]);
        Assert.DoesNotContain(savepoint, reverts);
        foreach (var r in modifies.Skip(7))
            Assert.Equal($"partial rollback at LSN {modifies[7].Lsn}, not replayed", RowReplayer.Replay(chain, r.Lsn, outcomes, reverts).Status);
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
            Assert.Equal($"partial rollback at LSN {revert.Lsn}, not replayed", RowReplayer.Replay(chain, m.Lsn, outcomes, reverts).Status);
    }
}
