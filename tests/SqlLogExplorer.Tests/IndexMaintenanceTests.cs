using SqlLogExplorer.Data;
using SqlLogExplorer.Models;
using SqlLogExplorer.Services;

namespace SqlLogExplorer.Tests;

public class IndexMaintenanceTests
{
    private const long OneMb = 1_048_576;

    // Une transaction synthétique : first et last deviennent des LSN, l'unité éventuelle reçoit tous les octets.
    private static TransactionFacts Tx(string id, string? name, int? spid, string? parent, int first, int last,
        bool begin = true, bool end = true, long bytes = 100, long? unit = null)
        => new(id, name, spid, parent, Lsn(first), Lsn(last),
            begin ? Time(first) : null, end ? Time(last) : null, begin, end, 1, bytes,
            unit is long u ? [new UnitBytes(u, "", bytes)] : []);

    private static string Lsn(int n) => $"00000001:{n:X8}:0001";

    private static string Time(int n) => $"2026/11/19 10:{n / 60:D2}:{n % 60:D2}:000";

    private static IReadOnlyList<string> Ids(MaintenanceOperation op) => op.Transactions.Select(t => t.TransactionId).ToList();

    [Fact]
    public void OfflineRebuild_IsOneOperationWithNestedBuild()
    {
        var ops = IndexMaintenance.Group([
            Tx("A", "ALTER INDEX", 60, null, 1, 10, bytes: 10),
            Tx("B", "offline index build", 60, "A", 2, 8, bytes: 200),
            Tx("C", "BulkExtentAlloc", 60, "A", 3, 4, bytes: 3000),
        ]);

        var op = Assert.Single(ops);
        Assert.Equal(MaintenanceKind.RebuildOffline, op.Kind);
        Assert.Equal(3, op.Transactions.Count);
        Assert.Equal(3210, op.Bytes);
        Assert.Equal(3, op.Records);
        Assert.Equal(60, op.Spid);
        Assert.Equal(Lsn(1), op.FirstLsn);
        Assert.Equal(Lsn(10), op.LastLsn);
        Assert.Equal(Time(1), op.Begin);
        Assert.Equal(Time(10), op.End);
        Assert.False(op.Partial);
        Assert.False(op.InUserTransaction);
        Assert.Empty(op.Objects);
    }

    [Fact]
    public void OnlineRebuild_TakesSameSpidTopLevelInSpan()
    {
        var ops = IndexMaintenance.Group([
            Tx("A", "ALTER INDEX", 60, null, 1, 20),
            Tx("B", "ONLINE_INDEX_DDL", 60, "A", 2, 3),
            Tx("C", "OnlineIndexInsertTxn", 60, null, 5, 7),
            Tx("D", "OnlineIndexInsertTxn", 61, null, 6, 8),
        ]);

        var op = Assert.Single(ops);
        Assert.Equal(MaintenanceKind.RebuildOnline, op.Kind);
        Assert.Equal(["A", "B", "C"], Ids(op));
        Assert.DoesNotContain("D", Ids(op));
        Assert.Equal(300, op.Bytes);
    }

    [Fact]
    public void OnlineRebuild_DetectedFromNestedDdlOnly()
    {
        var ops = IndexMaintenance.Group([
            Tx("A", "ALTER INDEX", 60, null, 1, 10),
            Tx("B", "ONLINE_INDEX_DDL", 60, "A", 2, 3),
        ]);

        Assert.Equal(MaintenanceKind.RebuildOnline, Assert.Single(ops).Kind);
    }

    [Fact]
    public void AlterIndexSetOrDisable_NotListed()
    {
        var ops = IndexMaintenance.Group([
            Tx("A", "ALTER INDEX", 60, null, 1, 10),
            Tx("B", "UpdateQPStats", 60, "A", 2, 3),
        ]);

        Assert.Empty(ops);
    }

    [Fact]
    public void TwoLevelNesting_ReachesTopLevel()
    {
        var ops = IndexMaintenance.Group([
            Tx("A", "ALTER INDEX", 60, null, 1, 10),
            Tx("B", "offline index build", 60, "A", 2, 9),
            Tx("C", "CopyRows", 60, "B", 3, 8),
        ]);

        var op = Assert.Single(ops);
        Assert.Equal(MaintenanceKind.RebuildOffline, op.Kind);
        Assert.Equal(3, op.Transactions.Count);
    }

    [Fact]
    public void Reorganize_OneOperationPerIndex()
    {
        var ops = IndexMaintenance.Group([
            Tx("U0", "UPDATE", 70, null, 1, 1),
            Tx("R1", "ReorderPages", 70, null, 2, 3, unit: 1),
            Tx("S1", "SwapPage", 70, "R1", 3, 3, unit: 1),
            Tx("R2", "ReorderPages", 70, null, 4, 5, unit: 1),
            Tx("R3", "ReorderPages", 70, null, 6, 7, unit: 1),
            Tx("R4", "ReorderPages", 70, null, 8, 9, unit: 2),
            Tx("S4", "SwapPage", 70, "R4", 9, 9, unit: 2),
            Tx("R5", "ReorderPages", 70, null, 10, 11, unit: 2),
            Tx("U1", "UPDATE", 70, null, 12, 12),
        ]);

        Assert.Equal(2, ops.Count);
        Assert.All(ops, op => Assert.Equal(MaintenanceKind.Reorganize, op.Kind));
        Assert.Equal(["R1", "S1", "R2", "R3"], Ids(ops[0]));
        Assert.Equal(["R4", "S4", "R5"], Ids(ops[1]));
        Assert.All(ops, op => Assert.False(op.Partial));
    }

    [Fact]
    public void Reorganize_MoveBlobThenPages_SameOperation()
    {
        var ops = IndexMaintenance.Group([
            Tx("M1", "MoveBlob", 70, null, 1, 2, unit: 5),
            Tx("M2", "MoveBlob", 70, null, 3, 4, unit: 5),
            Tx("R1", "ReorderPages", 70, null, 5, 6, unit: 1),
            Tx("R2", "ReorderPages", 70, null, 7, 8, unit: 1),
            Tx("B1", "RebalanceSpaces", 70, null, 9, 10, unit: 2),
        ]);

        Assert.Equal(2, ops.Count);
        Assert.Equal(["M1", "M2", "R1", "R2"], Ids(ops[0]));
        Assert.Equal(["B1"], Ids(ops[1]));
    }

    [Fact]
    public void Reorganize_MoveBlobThenFamilyWithoutUnitThenPages_SameOperation()
    {
        var ops = IndexMaintenance.Group([
            Tx("M1", "MoveBlob", 70, null, 1, 2, unit: 5),
            Tx("R1", "ReorderPages", 70, null, 3, 4),
            Tx("R2", "ReorderPages", 70, null, 5, 6, unit: 1),
        ]);

        Assert.Equal(["M1", "R1", "R2"], Ids(Assert.Single(ops)));
    }

    [Fact]
    public void Reorganize_FamilyWithoutUnit_DoesNotCut()
    {
        var middle = IndexMaintenance.Group([
            Tx("R1", "ReorderPages", 70, null, 1, 2, unit: 1),
            Tx("R2", "ReorderPages", 70, null, 3, 4),
            Tx("R3", "ReorderPages", 70, null, 5, 6, unit: 1),
        ]);
        var first = IndexMaintenance.Group([
            Tx("R1", "ReorderPages", 70, null, 1, 2),
            Tx("R2", "ReorderPages", 70, null, 3, 4, unit: 1),
            Tx("R3", "ReorderPages", 70, null, 5, 6, unit: 1),
        ]);

        Assert.Equal(3, Assert.Single(middle).Transactions.Count);
        Assert.Equal(3, Assert.Single(first).Transactions.Count);
    }

    [Fact]
    public void Reorganize_TwoTablesOneAfterOther_TwoOperations()
    {
        var ops = IndexMaintenance.Group([
            Tx("R1", "ReorderPages", 70, null, 1, 2, unit: 1),
            Tx("R2", "ReorderPages", 70, null, 3, 4, unit: 1),
            Tx("X1", "UPDATE", 80, null, 5, 5),
            Tx("X2", "QDS base transaction", 90, null, 6, 6),
            Tx("R3", "ReorderPages", 70, null, 7, 8, unit: 3),
            Tx("R4", "ReorderPages", 70, null, 9, 10, unit: 3),
        ]);

        Assert.Equal(2, ops.Count);
        Assert.Equal(["R1", "R2"], Ids(ops[0]));
        Assert.Equal(["R3", "R4"], Ids(ops[1]));
    }

    [Fact]
    public void Reorganize_BrokenByOtherTransactionOfSameSpid()
    {
        var ops = IndexMaintenance.Group([
            Tx("R1", "ReorderPages", 70, null, 1, 2, unit: 1),
            Tx("U1", "UPDATE", 70, null, 3, 4, unit: 1),
            Tx("R2", "ReorderPages", 70, null, 5, 6, unit: 1),
        ]);

        Assert.Equal(2, ops.Count);
        Assert.Equal(["R1"], Ids(ops[0]));
        Assert.Equal(["R2"], Ids(ops[1]));
    }

    [Fact]
    public void Reorganize_OtherSpidInterleaved_DoesNotBreak()
    {
        var ops = IndexMaintenance.Group([
            Tx("R1", "ReorderPages", 70, null, 1, 2, unit: 1),
            Tx("Q1", "QDS base transaction", 90, null, 3, 4),
            Tx("R2", "ReorderPages", 70, null, 5, 6, unit: 1),
        ]);

        Assert.Equal(["R1", "R2"], Ids(Assert.Single(ops)));
    }

    [Fact]
    public void Reorganize_OrphanSwapPage_StartsRun()
    {
        var ops = IndexMaintenance.Group([
            Tx("P", null, null, null, 1, 9, begin: false),
            Tx("S1", "SwapPage", 70, "P", 2, 3, unit: 1),
            Tx("R1", "ReorderPages", 70, null, 4, 5, unit: 1),
        ]);

        var op = Assert.Single(ops);
        Assert.Equal(MaintenanceKind.Reorganize, op.Kind);
        Assert.Equal(["S1", "R1"], Ids(op));
    }

    [Fact]
    public void UserTransaction_WithReorganize_IsMarked()
    {
        var ops = IndexMaintenance.Group([
            Tx("T", "user_transaction", 70, null, 1, 10),
            Tx("S1", "SwapPage", 70, "T", 2, 3, unit: 1),
            Tx("M1", "MoveRowsLeft", 70, "T", 4, 5, unit: 1),
        ]);

        var op = Assert.Single(ops);
        Assert.Equal(MaintenanceKind.Reorganize, op.Kind);
        Assert.True(op.InUserTransaction);
        Assert.Equal(["T", "S1", "M1"], Ids(op));
        Assert.False(op.Partial);
    }

    [Fact]
    public void UserTransaction_WithoutIndexWork_NotListed()
    {
        var ops = IndexMaintenance.Group([
            Tx("T", "user_transaction", 70, null, 1, 10),
            Tx("S1", "SplitPage", 70, "T", 2, 3, unit: 1),
        ]);

        Assert.Empty(ops);
    }

    [Fact]
    public void UserTransaction_OnlineRebuild_TakesTopLevelInsertTxn()
    {
        var ops = IndexMaintenance.Group([
            Tx("T", "user_transaction", 70, null, 1, 20),
            Tx("D", "ONLINE_INDEX_DDL", 70, "T", 2, 3),
            Tx("I", "OnlineIndexInsertTxn", 70, null, 5, 6, bytes: OneMb),
        ]);

        var op = Assert.Single(ops);
        Assert.Equal(MaintenanceKind.RebuildOnline, op.Kind);
        Assert.True(op.InUserTransaction);
        Assert.Contains("I", Ids(op));
        Assert.Equal(200 + OneMb, op.Bytes);
    }

    // Une reconstruction en ligne coupée par la fin de l'import : l'ouvrante (sans fin) et sa ONLINE_INDEX_DDL imbriquée
    // s'arrêtent au LSN 3, puis 100 OnlineIndexInsertTxn de premier niveau du même SPID suivent, et une transaction d'un autre SPID.
    private static IReadOnlyList<TransactionFacts> CutOnlineRebuild(string openerName, int spid)
    {
        var list = new List<TransactionFacts>
        {
            Tx("O", openerName, spid, null, 1, 3, end: false, bytes: 100),
            Tx("D", "ONLINE_INDEX_DDL", spid, "O", 2, 3, bytes: 2000),
        };
        for (var i = 0; i < 100; i++)
            list.Add(Tx($"I{i}", "OnlineIndexInsertTxn", spid, null, 10 + 2 * i, 11 + 2 * i, bytes: 100_000));
        list.Add(Tx("X", "UPDATE", spid + 1, null, 50, 51));
        return list;
    }

    [Fact]
    public void OnlineRebuild_CutByEndOfImport_TakesLaterInsertTxn()
    {
        var op = Assert.Single(IndexMaintenance.Group(CutOnlineRebuild("ALTER INDEX", 60)));

        Assert.Equal(MaintenanceKind.RebuildOnline, op.Kind);
        Assert.True(op.Partial);
        Assert.False(op.InUserTransaction);
        Assert.Equal(102, op.Transactions.Count);
        Assert.DoesNotContain("X", Ids(op));
        Assert.Equal(100 + 2000 + 100 * 100_000L, op.Bytes);
        Assert.Equal(Lsn(1), op.FirstLsn);
        Assert.Equal(Lsn(11 + 2 * 99), op.LastLsn);
        Assert.Null(op.End);
    }

    [Fact]
    public void OnlineRebuildInOpenUserTransaction_CutByEndOfImport_TakesLaterInsertTxn()
    {
        var op = Assert.Single(IndexMaintenance.Group(CutOnlineRebuild("user_transaction", 70)));

        Assert.Equal(MaintenanceKind.RebuildOnline, op.Kind);
        Assert.True(op.Partial);
        Assert.True(op.InUserTransaction);
        Assert.Equal(102, op.Transactions.Count);
        Assert.DoesNotContain("X", Ids(op));
        Assert.Equal(100 + 2000 + 100 * 100_000L, op.Bytes);
        Assert.Equal(Lsn(11 + 2 * 99), op.LastLsn);
        Assert.Null(op.End);
    }

    [Fact]
    public void CommittedRebuild_SpanStopsAtItsLastLsn()
    {
        // Validée, l'ouvrante garde son étendue : une OnlineIndexInsertTxn du même SPID après elle n'est pas prise.
        var ops = IndexMaintenance.Group([
            Tx("A", "ALTER INDEX", 60, null, 1, 3),
            Tx("D", "ONLINE_INDEX_DDL", 60, "A", 2, 3),
            Tx("I", "OnlineIndexInsertTxn", 60, null, 10, 11),
        ]);

        Assert.Equal(["A", "D"], Ids(Assert.Single(ops)));
    }

    [Fact]
    public void Partial_RebuildOrUserTransactionWithoutCommit()
    {
        IReadOnlyList<TransactionFacts> Rebuild(bool end) => [
            Tx("A", "ALTER INDEX", 60, null, 1, 10, end: end),
            Tx("B", "offline index build", 60, "A", 2, 8),
        ];
        IReadOnlyList<TransactionFacts> UserTransaction(bool end) => [
            Tx("T", "user_transaction", 70, null, 1, 10, end: end),
            Tx("S1", "SwapPage", 70, "T", 2, 3, unit: 1),
        ];

        Assert.True(Assert.Single(IndexMaintenance.Group(Rebuild(false))).Partial);
        Assert.True(Assert.Single(IndexMaintenance.Group(UserTransaction(false))).Partial);
        Assert.False(Assert.Single(IndexMaintenance.Group(Rebuild(true))).Partial);
        Assert.False(Assert.Single(IndexMaintenance.Group(UserTransaction(true))).Partial);
    }

    [Fact]
    public void PartialRebuild_HasNoEnd()
    {
        var ops = IndexMaintenance.Group([
            Tx("A", "ALTER INDEX", 60, null, 1, 10, end: false),
            Tx("B", "offline index build", 60, "A", 2, 8),
            Tx("C", "BulkExtentAlloc", 60, null, 3, 9),
        ]);

        var op = Assert.Single(ops);
        Assert.Equal(["A", "B", "C"], Ids(op));
        Assert.True(op.Partial);
        Assert.Null(op.End);
        Assert.Equal(Time(1), op.Begin);
    }

    [Fact]
    public void Partial_ReorganizeAtStartAndEndOfImport()
    {
        IReadOnlyList<TransactionFacts> Reorganize(bool lastEnd) => [
            Tx("R1", "ReorderPages", 70, null, 2, 3, unit: 1),
            Tx("R2", "ReorderPages", 70, null, 4, 5, unit: 1, end: lastEnd),
        ];
        IReadOnlyList<TransactionFacts> Framed(bool lastEnd) => [
            Tx("U0", "UPDATE", 70, null, 1, 1),
            .. Reorganize(lastEnd),
            Tx("U1", "UPDATE", 70, null, 6, 6),
        ];

        Assert.True(Assert.Single(IndexMaintenance.Group(Reorganize(true))).Partial);
        Assert.False(Assert.Single(IndexMaintenance.Group(Framed(true))).Partial);
        Assert.True(Assert.Single(IndexMaintenance.Group(Framed(false))).Partial);
    }

    [Fact]
    public void RebuildStartedBeforeImport_NotListed()
    {
        var rebuild = IndexMaintenance.Group([
            Tx("A", null, null, null, 1, 10, begin: false, bytes: OneMb),
            Tx("I", "OnlineIndexInsertTxn", 60, null, 3, 4),
        ]);
        var userTransaction = IndexMaintenance.Group([
            Tx("T", "user_transaction", 70, null, 1, 10, begin: false),
            Tx("S1", "SwapPage", 70, "T", 2, 3, unit: 1),
        ]);

        Assert.Empty(rebuild);
        var op = Assert.Single(userTransaction);
        Assert.Equal(MaintenanceKind.Reorganize, op.Kind);
        Assert.True(op.Partial);
        Assert.False(op.InUserTransaction);
        Assert.Equal(["S1"], Ids(op));
    }

    // Une opération synthétique dont la seule transaction a les unités données.
    private static MaintenanceOperation OpWithUnits(long bytes, params UnitBytes[] units)
    {
        var tx = new TransactionFacts("T", "ReorderPages", 70, null, Lsn(1), Lsn(2), null, null, true, true, 1, bytes, units);
        return new MaintenanceOperation(MaintenanceKind.Reorganize, 70, Lsn(1), Lsn(2), null, null, [tx], 1, bytes, false, false);
    }

    [Fact]
    public void DisplayName_UnknownAllocUnit_ShowsId()
    {
        Assert.Equal("72057594052608000", IndexMaintenance.DisplayName(72057594052608000, "Unknown Alloc Unit"));
        Assert.Equal("5", IndexMaintenance.DisplayName(5, ""));
        Assert.Equal("dbo.T.IX_n", IndexMaintenance.DisplayName(5, "dbo.T.IX_n"));
    }

    [Fact]
    public void Objects_SystemUnitsLeftOutAndOnePercentThreshold()
    {
        var op = OpWithUnits(1000,
            new UnitBytes(1, "dbo.T.PK", 900), new UnitBytes(2, "dbo.T.IX_n", 95),
            new UnitBytes(3, "sys.sysrowsets", 3), new UnitBytes(77, "", 2));

        var (objects, others) = IndexMaintenance.Objects(op);

        Assert.Equal(["dbo.T.PK", "dbo.T.IX_n"], objects.Select(o => o.Display));
        Assert.Equal([900L, 95L], objects.Select(o => o.Bytes));
        Assert.Equal(1, others);
    }

    [Fact]
    public void Objects_SameNameShownOnce()
    {
        var op = OpWithUnits(900, new UnitBytes(5, "dbo.L.PK", 600), new UnitBytes(6, "dbo.L.PK", 300));

        var (objects, others) = IndexMaintenance.Objects(op);

        var obj = Assert.Single(objects);
        Assert.Equal("dbo.L.PK", obj.Display);
        Assert.Equal(900, obj.Bytes);
        Assert.Equal(5, obj.AllocUnitId);
        Assert.Equal(0, others);
    }

    [Fact]
    public void ExclusiveRanges_DisjointSpansKeepThemselves()
    {
        var ranges = IndexMaintenance.ExclusiveRanges([("A", "B"), ("C", "D")]);

        Assert.Equal([("A", "B")], ranges[0]);
        Assert.Equal([("C", "D")], ranges[1]);
    }

    [Fact]
    public void ExclusiveRanges_OverlapBelongsToNone()
    {
        var ranges = IndexMaintenance.ExclusiveRanges([("A", "D"), ("C", "F")]);

        Assert.Equal([("A", "C")], ranges[0]);
        Assert.Equal([("D", "F")], ranges[1]);
    }

    [Fact]
    public void ExclusiveRanges_NestedSpanOwnsNothing()
    {
        var ranges = IndexMaintenance.ExclusiveRanges([("A", "F"), ("B", "C")]);

        Assert.Equal([("A", "B"), ("C", "F")], ranges[0]);
        Assert.Empty(ranges[1]);
    }

    [Fact]
    public async Task AnalyzeAsync_TotalCountsEachRecordOnce()
    {
        // Deux réorganisations qui se chevauchent (SPID 70 de 0x10 à 0x30, SPID 71 de 0x20 à 0x40) et trois
        // enregistrements hors transaction : un dans chaque partie exclusive, un dans le chevauchement.
        static LogRecord Begin(int lsn, string tx, int spid) =>
            new($"00000001:{lsn:X8}:0001", "LOP_BEGIN_XACT", "LCX_NULL", tx, null, null, null)
                { TransactionName = "ReorderPages", Spid = spid, LogRecordLength = 100, BeginTime = "2026/11/19 10:00:00:000" };
        static LogRecord Work(int lsn, string tx, long unit, string name) =>
            new($"00000001:{lsn:X8}:0001", "LOP_MODIFY_ROW", "LCX_INDEX_LEAF", tx, name, null, null)
                { AllocUnitId = unit, LogRecordLength = 1000 };
        static LogRecord Commit(int lsn, string tx) =>
            new($"00000001:{lsn:X8}:0001", "LOP_COMMIT_XACT", "LCX_NULL", tx, null, null, null)
                { LogRecordLength = 50, EndTime = "2026/11/19 10:00:01:000" };
        static LogRecord Outside(int lsn, int length) =>
            new($"00000001:{lsn:X8}:0001", "LOP_MIGRATE_LOCKS", "LCX_NULL", "0000:00000000", null, null, null) { LogRecordLength = length };

        await using var cache = await LogCache.CreateAsync(Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db"));
        await cache.InsertBatchAsync(new List<LogRecord>
        {
            Begin(0x10, "0000:00000100", 70),
            Work(0x12, "0000:00000100", 1, "dbo.A.PK"),
            Outside(0x15, 40),
            Begin(0x20, "0000:00000200", 71),
            Work(0x22, "0000:00000200", 2, "dbo.B.PK"),
            Outside(0x25, 60),
            Commit(0x30, "0000:00000100"),
            Outside(0x35, 80),
            Commit(0x40, "0000:00000200"),
        });

        var report = await IndexMaintenance.AnalyzeAsync(new LogQuery(cache.Connection));

        Assert.Equal(2, report.Operations.Count);
        var a = Assert.Single(report.Operations, o => o.Spid == 70);
        var b = Assert.Single(report.Operations, o => o.Spid == 71);
        Assert.Equal(4, a.Records);
        Assert.Equal(1150 + 40, a.Bytes);
        Assert.Equal(4, b.Records);
        Assert.Equal(1150 + 80, b.Bytes);
        // Triées par octets décroissants.
        Assert.Same(b, report.Operations[0]);
        Assert.Equal("dbo.A.PK", Assert.Single(a.Objects).Display);
        Assert.Equal(2 * 1150 + 40 + 60 + 80, report.TotalBytes);
        Assert.Equal(a.Bytes + b.Bytes, report.MaintenanceBytes);
        Assert.True(report.MaintenanceBytes <= report.TotalBytes);
        // L'enregistrement du chevauchement n'est compté dans aucune opération.
        Assert.Equal(60, report.TotalBytes - report.MaintenanceBytes);
    }
}
