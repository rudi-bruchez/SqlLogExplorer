using System.Collections.Generic;

namespace SqlLogExplorer.Models;

/// <summary>Octets de journal écrits sur une unité d'allocation par une transaction (spec04).</summary>
public sealed record UnitBytes(long AllocUnitId, string? AllocUnitName, long Bytes);

/// <summary>Ce que le cache sait d'une transaction : son début, sa fin, son volume de journal et sa répartition par unité d'allocation (spec04).</summary>
public sealed record TransactionFacts(
    string TransactionId,
    string? Name,
    int? Spid,
    string? ParentTransactionId,
    string FirstLsn,
    string LastLsn,
    string? BeginTime,
    string? EndTime,
    bool HasBegin,
    bool HasEnd,
    long Records,
    long Bytes,
    IReadOnlyList<UnitBytes> Units);
