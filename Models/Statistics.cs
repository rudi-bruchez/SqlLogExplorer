namespace SqlLogExplorer.Models;

public sealed record OperationCount(string Operation, long Count);
public sealed record ObjectCount(string AllocUnitName, long Count);
public sealed record ObjectOperationCount(string AllocUnitName, string Operation, long Count);
