namespace SqlLogExplorer.Models;

public sealed record OperationCount(string Operation, long Count);
public sealed record ObjectCount(string AllocUnitName, long Count);
/// <summary>Une transaction du cache. Issue : Committed, Rolled back, ou Open sans commit ni abort dans le journal importé.</summary>
public sealed record TransactionSummary(
    string TransactionId, string? Name, string? BeginTime, string? EndTime, string Outcome, long OperationCount);
public sealed record ObjectOperationCount(string AllocUnitName, string Operation, long Count);
