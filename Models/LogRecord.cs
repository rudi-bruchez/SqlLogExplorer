namespace SqlLogExplorer.Models;

/// <summary>Un enregistrement brut du journal (RowLog Contents non décodés).</summary>
public sealed record LogRecord(
    string Lsn,
    string Operation,
    string? Context,
    string? TransactionId,
    string? AllocUnitName,
    byte[]? RowLogContents0,
    byte[]? RowLogContents1);
