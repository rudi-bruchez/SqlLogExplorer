using SqlLogExplorer.Models;

namespace SqlLogExplorer.Tests;

/// <summary>Égalité membre à membre de LogRecord, tableaux d'octets comparés par contenu.</summary>
public sealed class LogRecordComparer : IEqualityComparer<LogRecord>
{
    public bool Equals(LogRecord? x, LogRecord? y)
    {
        if (x is null || y is null) return x is null && y is null;
        static bool B(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
        return x.Lsn == y.Lsn && x.Operation == y.Operation && x.Context == y.Context
            && x.TransactionId == y.TransactionId && x.AllocUnitName == y.AllocUnitName
            && B(x.RowLogContents0, y.RowLogContents0) && B(x.RowLogContents1, y.RowLogContents1)
            && x.AllocUnitId == y.AllocUnitId && x.PartitionId == y.PartitionId && x.PageId == y.PageId
            && x.SlotId == y.SlotId && x.OffsetInRow == y.OffsetInRow && x.ModifySize == y.ModifySize
            && x.NumElements == y.NumElements && x.Description == y.Description && x.TransactionName == y.TransactionName
            && B(x.RowLogContents2, y.RowLogContents2) && B(x.RowLogContents3, y.RowLogContents3)
            && B(x.RowLogContents4, y.RowLogContents4) && B(x.RowLogContents5, y.RowLogContents5)
            && B(x.LogRecordBytes, y.LogRecordBytes);
    }

    public int GetHashCode(LogRecord obj) => obj.Lsn.GetHashCode();
}
