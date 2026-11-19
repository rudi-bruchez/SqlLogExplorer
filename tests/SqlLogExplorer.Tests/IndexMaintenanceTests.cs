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
}
