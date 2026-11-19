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

/// <summary>Nature d'une opération de maintenance d'index (spec04 §4).</summary>
public enum MaintenanceKind { RebuildOffline, RebuildOnline, Reorganize }

/// <summary>Un objet touché par une opération et ses octets de journal (spec04 §5).</summary>
public sealed record MaintenanceObject(long AllocUnitId, string Display, long Bytes);

/// <summary>Une reconstruction ou une réorganisation trouvée dans le journal : ses transactions (imbriquées comprises),
/// leurs enregistrements et leurs octets (spec04 §4). Partial : l'opération peut déborder de l'import.</summary>
public sealed record MaintenanceOperation(
    MaintenanceKind Kind,
    int Spid,
    string FirstLsn,
    string LastLsn,
    string? Begin,
    string? End,
    IReadOnlyList<TransactionFacts> Transactions,
    long Records,
    long Bytes,
    bool Partial,
    bool InUserTransaction)
{
    public IReadOnlyList<MaintenanceObject> Objects { get; init; } = [];
    public int OtherObjects { get; init; }
}

/// <summary>Le volume de journal de l'import, celui des opérations de maintenance et ces opérations, par octets décroissants (spec04 §6).</summary>
public sealed record MaintenanceReport(long TotalBytes, long MaintenanceBytes, IReadOnlyList<MaintenanceOperation> Operations);
