namespace SqlLogExplorer.Models;

/// <summary>Un enregistrement brut du journal (RowLog Contents non décodés), spec03 §3.</summary>
public sealed record LogRecord(
    string Lsn,
    string Operation,
    string? Context,
    string? TransactionId,
    string? AllocUnitName,
    byte[]? RowLogContents0,
    byte[]? RowLogContents1)
{
    public long? AllocUnitId { get; init; }
    public long? PartitionId { get; init; }
    public string? PageId { get; init; }
    public int? SlotId { get; init; }
    public int? OffsetInRow { get; init; }
    public int? ModifySize { get; init; }
    public int? NumElements { get; init; }
    public string? Description { get; init; }
    public string? TransactionName { get; init; }
    public byte[]? RowLogContents2 { get; init; }
    public byte[]? RowLogContents3 { get; init; }
    public byte[]? RowLogContents4 { get; init; }
    public byte[]? RowLogContents5 { get; init; }

    /// <summary>Le Log Record binaire entier, gardé pour LOP_MODIFY_COLUMNS seulement (spec03 §3, §6.2).</summary>
    public byte[]? LogRecordBytes { get; init; }

    public bool IsCompensation => Description?.Contains("COMPENSATION", System.StringComparison.Ordinal) == true;
}
