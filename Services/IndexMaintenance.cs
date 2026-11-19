using System;
using System.Collections.Generic;
using System.Linq;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Services;

/// <summary>Regroupe les transactions du cache en reconstructions et réorganisations d'index (spec04 §4).</summary>
public static class IndexMaintenance
{
    private const string AlterIndex = "ALTER INDEX";
    private const string UserTransaction = "user_transaction";
    private const string OfflineBuild = "offline index build";
    private const string MoveBlob = "MoveBlob";

    private static readonly HashSet<string> OnlineBuildNames = new(StringComparer.Ordinal) { "ONLINE_INDEX_DDL", "OnlineIndexInsertTxn" };

    private static readonly HashSet<string> ReorganizeNames = new(StringComparer.Ordinal)
    {
        "ReorderPages", "RebalanceSpaces", "MoveBlob", "SwapPage", "MoveRowsLeft", "AllocUnlinkedReorgPage", "DeallocUnlinkedReorgPage",
    };

    /// <summary>Une transaction de premier niveau avec toutes ses descendantes, et l'unité d'allocation qui y a le plus d'octets.</summary>
    private sealed class Family(TransactionFacts top, List<TransactionFacts> members)
    {
        public TransactionFacts Top { get; } = top;
        public List<TransactionFacts> Members { get; } = members;
        public int Spid => Top.Spid!.Value;
        public long? Unit { get; } = MainUnit(members);
        public bool Taken { get; set; }

        public bool Contains(Func<string, bool> name) => Members.Any(m => m.Name is string n && name(n));
    }

    /// <summary>Les opérations trouvées, triées par premier LSN.</summary>
    public static IReadOnlyList<MaintenanceOperation> Group(IReadOnlyList<TransactionFacts> transactions)
    {
        // Familles par SPID, dans l'ordre du premier LSN de leur transaction de premier niveau.
        var bySpid = Families(transactions)
            .GroupBy(f => f.Spid)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.Top.FirstLsn, StringComparer.Ordinal).ToList());
        var operations = new List<MaintenanceOperation>();

        // Règle 1 : ALTER INDEX et les familles du même SPID qui commencent dans son étendue.
        foreach (var families in bySpid.Values)
            foreach (var family in families.Where(f => !f.Taken && IsNamed(f.Top, AlterIndex)))
            {
                var taken = InSpan(families, family);
                // Sans construction (SET, DISABLE), l'ALTER INDEX n'est pas une maintenance et les familles restent libres.
                if (BuildKind(taken) is not MaintenanceKind kind) continue;
                Take(taken);
                operations.Add(Operation(kind, taken, partial: !family.Top.HasEnd, inUserTransaction: false));
            }

        // Règle 3 : user_transaction qui contient une construction d'index ou une réorganisation.
        foreach (var families in bySpid.Values)
            foreach (var family in families.Where(f => !f.Taken && IsNamed(f.Top, UserTransaction)
                         && f.Contains(n => n == OfflineBuild || OnlineBuildNames.Contains(n) || ReorganizeNames.Contains(n))))
            {
                var taken = InSpan(families, family);
                Take(taken);
                var kind = BuildKind(taken) ?? MaintenanceKind.Reorganize;
                operations.Add(Operation(kind, taken, partial: !family.Top.HasEnd, inUserTransaction: true));
            }

        // Règle 2 : séries de réorganisation, toutes familles du SPID parcourues (les prises coupent la série).
        foreach (var families in bySpid.Values)
            foreach (var run in Runs(families))
            {
                var last = run[^1];
                var partial = run[0] == families[0] || last == families[^1] || !last.Top.HasEnd;
                operations.Add(Operation(MaintenanceKind.Reorganize, run, partial, inUserTransaction: false));
            }

        return operations.OrderBy(o => o.FirstLsn, StringComparer.Ordinal).ToList();
    }

    /// <summary>Les familles dont le premier niveau a un enregistrement de début (donc un SPID) ; les autres n'entrent dans aucune règle.</summary>
    private static IEnumerable<Family> Families(IReadOnlyList<TransactionFacts> transactions)
    {
        var byId = new Dictionary<string, TransactionFacts>(StringComparer.Ordinal);
        foreach (var t in transactions) byId[t.TransactionId] = t;

        var members = new Dictionary<string, List<TransactionFacts>>(StringComparer.Ordinal);
        foreach (var t in transactions)
        {
            var topId = TopOf(t, byId).TransactionId;
            if (!members.TryGetValue(topId, out var list)) members[topId] = list = [];
            list.Add(t);
        }

        foreach (var (topId, list) in members)
        {
            var top = byId[topId];
            if (!top.HasBegin || top.Spid is null) continue;
            yield return new Family(top, list.OrderBy(m => m.FirstLsn, StringComparer.Ordinal).ToList());
        }
    }

    /// <summary>L'ancêtre de premier niveau : on remonte tant que le parent est dans le cache avec son début.</summary>
    private static TransactionFacts TopOf(TransactionFacts transaction, Dictionary<string, TransactionFacts> byId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { transaction.TransactionId };
        var current = transaction;
        while (current.ParentTransactionId is string parentId
               && byId.TryGetValue(parentId, out var parent) && parent.HasBegin
               && visited.Add(parent.TransactionId))
            current = parent;
        return current;
    }

    /// <summary>L'unité d'allocation qui a le plus d'octets dans la famille, la plus petite en cas d'égalité.</summary>
    private static long? MainUnit(List<TransactionFacts> members)
    {
        var bytes = members.SelectMany(m => m.Units).GroupBy(u => u.AllocUnitId).Select(g => (Id: g.Key, Bytes: g.Sum(u => u.Bytes)));
        return bytes.OrderByDescending(u => u.Bytes).ThenBy(u => u.Id).Select(u => (long?)u.Id).FirstOrDefault();
    }

    /// <summary>Les familles libres du SPID dont le premier LSN est dans l'étendue de celle qui ouvre l'opération, elle comprise.</summary>
    private static List<Family> InSpan(List<Family> families, Family opener)
        => families.Where(f => !f.Taken
            && string.CompareOrdinal(f.Top.FirstLsn, opener.Top.FirstLsn) >= 0
            && string.CompareOrdinal(f.Top.FirstLsn, opener.Top.LastLsn) <= 0).ToList();

    private static void Take(List<Family> families)
    {
        foreach (var f in families) f.Taken = true;
    }

    /// <summary>En ligne si une transaction en ligne est présente, sinon hors ligne si une construction l'est, sinon rien.</summary>
    private static MaintenanceKind? BuildKind(List<Family> families)
        => families.Any(f => f.Contains(OnlineBuildNames.Contains)) ? MaintenanceKind.RebuildOnline
            : families.Any(f => f.Contains(n => n == OfflineBuild)) ? MaintenanceKind.RebuildOffline
            : null;

    /// <summary>Découpe les familles d'un SPID en séries de réorganisation (règle 2).</summary>
    private static List<List<Family>> Runs(List<Family> families)
    {
        var runs = new List<List<Family>>();
        List<Family>? run = null;
        long? unit = null;
        for (var i = 0; i < families.Count; i++)
        {
            var family = families[i];
            if (family.Taken || !IsReorganize(family.Top))
            {
                run = null;
                continue;
            }

            var cuts = run is not null && family.Unit is long u && unit is long current && u != current
                && !(IsNamed(families[i - 1].Top, MoveBlob) && !IsNamed(family.Top, MoveBlob));
            if (run is null || cuts)
            {
                run = [];
                runs.Add(run);
                unit = null;
            }
            run.Add(family);
            if (family.Unit is not null) unit = family.Unit;
        }
        return runs;
    }

    private static bool IsReorganize(TransactionFacts t) => t.Name is string n && ReorganizeNames.Contains(n);

    private static bool IsNamed(TransactionFacts t, string name) => string.Equals(t.Name, name, StringComparison.Ordinal);

    private static MaintenanceOperation Operation(MaintenanceKind kind, List<Family> families, bool partial, bool inUserTransaction)
    {
        var members = families.SelectMany(f => f.Members).OrderBy(m => m.FirstLsn, StringComparer.Ordinal).ToList();
        var tops = families.Select(f => f.Top).OrderBy(t => t.FirstLsn, StringComparer.Ordinal).ToList();
        return new MaintenanceOperation(
            kind,
            families[0].Spid,
            members.Select(m => m.FirstLsn).Min(StringComparer.Ordinal)!,
            members.Select(m => m.LastLsn).Max(StringComparer.Ordinal)!,
            tops[0].BeginTime,
            tops.Select(t => t.EndTime).Where(e => e is not null).Max(StringComparer.Ordinal),
            members,
            members.Sum(m => m.Records),
            members.Sum(m => m.Bytes),
            partial,
            inUserTransaction);
    }
}
