namespace SqlLogExplorer.Models;

/// <summary>Critères de filtrage appliqués aux lectures et aux agrégats (§4.3).</summary>
public sealed record LogFilter(
    string? AllocUnitName = null,
    string? Operation = null,
    string? TransactionId = null)
{
    public bool IsEmpty =>
        string.IsNullOrEmpty(AllocUnitName)
        && string.IsNullOrEmpty(Operation)
        && string.IsNullOrEmpty(TransactionId);
}
