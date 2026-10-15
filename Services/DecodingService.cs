using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlLogExplorer.Data;
using SqlLogExplorer.Decoding;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Services;

/// <summary>Inspection d'un enregistrement sur le cache : image décodée, chaîne de la ligne, rejeu (spec03 §5 à §7).
/// Ne lit pas le serveur, sauf le schéma par schemaLookup ; l'ancre 3 (ligne courante) est une action à part.</summary>
public sealed class DecodingService
{
    private const string Insert = "LOP_INSERT_ROWS";
    private const string Delete = "LOP_DELETE_ROWS";
    private const string NoImage = "no full image of this row available";

    private static readonly HashSet<string> DataContexts = ["LCX_HEAP", "LCX_CLUSTERED", "LCX_MARK_AS_GHOST"];

    // Transactions système qui déplacent des lignes (spec03 §5) : seule SplitPage a été mesurée (fixture split).
    private static readonly HashSet<string> SystemTransactions = ["SplitPage"];

    private readonly LogCache _cache;
    private readonly LogQuery _query;
    private readonly Func<long, CancellationToken, Task<TableSchema?>>? _schemaLookup;
    private readonly bool _isLive;

    /// <summary>schemaLookup null : pas de connexion de schéma (hors ligne) ; il renvoie null pour une unité d'allocation inconnue de la base.</summary>
    public DecodingService(LogCache cache, Func<long, CancellationToken, Task<TableSchema?>>? schemaLookup, bool isLive)
    {
        _cache = cache;
        _query = new LogQuery(cache.Connection);
        _schemaLookup = schemaLookup;
        _isLive = isLive;
    }

    /// <summary>La chaîne et les enregistrements des transactions qui y figurent.</summary>
    private sealed record RowChain(IReadOnlyList<LogRecord> Records, IReadOnlyList<LogRecord> Transactions);

    public async Task<Inspection> InspectAsync(LogRecord record, CancellationToken ct = default)
    {
        record = record with { Lsn = Lsn.Normalize(record.Lsn) };
        var own = record.TransactionId is null ? [] : await _query.GetTransactionRecordsAsync([record.TransactionId], ct);
        var outcome = OutcomeOf(record, RowReplayer.Classify(own));
        var isRow = record.Operation is Insert or Delete;
        var isModify = record.Operation is "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS";
        if (!isRow && !isModify)
            return new Inspection(record, null, null, null, [], null, outcome, false, null, null, null, false);

        var (schema, schemaMessage) = await SchemaAsync(record, ct);
        // Admission (spec03 §5) : contexte de données et, quand le schéma est là, unité IN_ROW_DATA d'index 0 ou 1 ; le type d'enregistrement est vu par RowDecoder.
        var admitted = record.Context is not null && DataContexts.Contains(record.Context) && (schema is null || schema.IsDataRows);
        var data = admitted ? schema : null;

        if (isRow)
        {
            var row = RowDecoder.Decode(record.RowLogContents0, data);
            var label = own.Any(r => r.Operation == "LOP_BEGIN_XACT" && r.TransactionName is not null && SystemTransactions.Contains(r.TransactionName))
                ? "row moved by the engine" : null;
            string? warning = null;
            if (data is not null && await ChainAsync(record, data, ct) is { } c)
            {
                // Une révocation ADR avant l'image : ses octets annulés sont restés dans la page (spec03 §7.3).
                var reverts = RowReplayer.AdrReverts(c.Transactions);
                if (c.Records.Any(r => string.CompareOrdinal(r.Lsn, record.Lsn) < 0 && r.TransactionId is not null && reverts.Contains(r.TransactionId)))
                    warning = "may hold values undone by a partial rollback";
            }
            return new Inspection(record, row, null, null, [], null, outcome, false, schemaMessage, label, warning, false);
        }

        if (data is null)
            return new Inspection(record, null, null, null, [], null, outcome, false, schemaMessage, null, null, false);

        // Une compensation n'a pas de clé : elle est traitée par transaction, jamais rejouée seule (spec03 §7.1).
        if (record.IsCompensation)
            return new Inspection(record, null, null, null, FragmentChanges(record, data), null, outcome, false, schemaMessage, null, null, false);

        var chain = await ChainAsync(record, data, ct);
        if (chain is null)
            return new Inspection(record, null, null, null, FragmentChanges(record, data), null, outcome, false, schemaMessage, null, "key not decodable", false);

        var tx = chain.Transactions;
        var result = RowReplayer.Replay(chain.Records, record.Lsn, RowReplayer.Classify(tx), RowReplayer.AdrReverts(tx), RowReplayer.FirstUndoLsns(tx));
        var before = result.Before is null ? null : RowDecoder.Decode(result.Before, data);
        var after = result.After is null ? null : RowDecoder.Decode(result.After, data);
        var changes = before is not null && after is not null ? Compare(before, after) : FragmentChanges(record, data);
        // Ancres 1 et 2 sans image : la ligne courante peut servir, en direct, sur une table clustered (spec03 §7.2).
        // Pas pour une transaction partiellement annulée : l'enregistrement termine la chaîne (spec03 §7.3), l'ancre 3 ne peut pas aboutir.
        var canRebuild = _isLive && data.IndexId == 1 && result.Status == NoImage && outcome != TxOutcome.PartlyRolledBack;
        return new Inspection(record, null, before, after, changes, result.Status, outcome, result.IncludesUnknownOutcome, schemaMessage, null, null, canRebuild);
    }

    /// <summary>La chaîne de la ligne de l'enregistrement, celle que l'inspection rejoue ; vide sans schéma ou sans clé décodable.</summary>
    public async Task<IReadOnlyList<LogRecord>> GetRowChainAsync(LogRecord record, CancellationToken ct = default)
    {
        record = record with { Lsn = Lsn.Normalize(record.Lsn) };
        var (schema, _) = await SchemaAsync(record, ct);
        if (schema is not { IsDataRows: true } || record.IsCompensation) return [];
        return (await ChainAsync(record, schema, ct))?.Records ?? [];
    }

    private async Task<(TableSchema? Schema, string? Message)> SchemaAsync(LogRecord record, CancellationToken ct)
    {
        if (_schemaLookup is null) return (null, "a schema connection is required");
        var schema = record.AllocUnitId is { } au ? await _schemaLookup(au, ct) : null;
        return schema is null ? (null, "allocation unit not found in the connected database") : (schema, null);
    }

    private static TxOutcome? OutcomeOf(LogRecord record, IReadOnlyDictionary<string, TxOutcome> outcomes) =>
        record.TransactionId is null ? null : outcomes.GetValueOrDefault(record.TransactionId, TxOutcome.Unknown);

    /// <summary>null : pas de clé décodable (table clustered) ou pas d'adresse (tas).</summary>
    private async Task<RowChain?> ChainAsync(LogRecord record, TableSchema schema, CancellationToken ct)
    {
        if (schema.IndexId == 0)
        {
            if (record.PageId is null || record.SlotId is null) return null;
            var all = await _query.GetHeapChainAsync(schema.AllocUnitId, record.PageId, record.SlotId.Value, ct);
            var tx = await TransactionsAsync(all, record, ct);
            return new RowChain(HeapLife(all, record, RowReplayer.Classify(tx)), tx);
        }

        await KeyAllocationUnitAsync(schema, ct);
        var key = KeyOf(record, schema);
        if (key is null) return null;
        var chain = await _query.GetChainAsync(schema.AllocUnitId, key, ct);
        return new RowChain(chain, await TransactionsAsync(chain, record, ct));
    }

    private Task<IReadOnlyList<LogRecord>> TransactionsAsync(IReadOnlyList<LogRecord> chain, LogRecord record, CancellationToken ct) =>
        _query.GetTransactionRecordsAsync(
            chain.Append(record).Where(r => r.TransactionId is not null).Select(r => r.TransactionId!).ToHashSet(), ct);

    private static string? KeyOf(LogRecord r, TableSchema schema) => r.Operation is Insert or Delete
        ? KeyDecoder.FromRow(r.RowLogContents0, schema)
        : KeyDecoder.FromIndexRecord(r.RowLogContents2, schema);

    /// <summary>Pose RowKey sur les enregistrements de données de l'unité qui n'en ont pas encore, ceux d'un import récent compris (spec03 §7.1).</summary>
    private async Task KeyAllocationUnitAsync(TableSchema schema, CancellationToken ct)
    {
        var keys = new List<(string Lsn, string? RowKey)>();
        foreach (var r in await _query.GetUnkeyedDataRecordsAsync(schema.AllocUnitId, ct))
            if (KeyOf(r, schema) is { } key) keys.Add((r.Lsn, key));
        if (keys.Count > 0) await _cache.SetRowKeysAsync(keys, ct);
    }

    /// <summary>Vie d'une ligne de tas à son adresse : du dernier insert jusqu'au premier delete autour de l'enregistrement (spec03 §7.1).
    /// Un enregistrement d'une autre transaction annulée ne borne rien : son effet net est nul.</summary>
    private static IReadOnlyList<LogRecord> HeapLife(IReadOnlyList<LogRecord> all, LogRecord selected, IReadOnlyDictionary<string, TxOutcome> outcomes)
    {
        var i = all.ToList().FindIndex(r => r.Lsn == selected.Lsn);
        if (i < 0) return [];
        bool Bounds(LogRecord r) => r.TransactionId == selected.TransactionId
            || r.TransactionId is null || outcomes.GetValueOrDefault(r.TransactionId, TxOutcome.Unknown) != TxOutcome.RolledBack;

        var start = 0;
        if (all[i].Operation == Insert) start = i;
        else
            for (var j = i - 1; j >= 0; j--)
            {
                if (!Bounds(all[j])) continue;
                if (all[j].Operation == Insert) { start = j; break; }
                if (all[j].Operation == Delete) { start = j + 1; break; }
            }

        var end = all.Count;
        if (all[i].Operation == Delete) end = i + 1;
        else
            for (var j = i + 1; j < all.Count; j++)
            {
                if (!Bounds(all[j])) continue;
                if (all[j].Operation == Delete) { end = j + 1; break; }
                if (all[j].Operation == Insert) { end = j; break; }
            }
        return all.Skip(start).Take(end - start).ToList();
    }

    /// <summary>Colonne par colonne, par nom ; valeurs comparées en texte invariant, pour qu'un défaut lu au catalogue ("7") égale l'entier 7.</summary>
    private static IReadOnlyList<ColumnChange> Compare(DecodedRow before, DecodedRow after)
    {
        var afterByName = after.Columns.ToDictionary(c => c.Name);
        var changes = new List<ColumnChange>();
        foreach (var b in before.Columns)
        {
            var a = afterByName.GetValueOrDefault(b.Name);
            changes.Add(new ColumnChange(b.Name, b.Value, a?.Value, a is null || Text(b.Value) != Text(a.Value)));
        }
        var seen = before.Columns.Select(c => c.Name).ToHashSet();
        foreach (var a in after.Columns.Where(c => !seen.Contains(c.Name)))
            changes.Add(new ColumnChange(a.Name, null, a.Value, true));
        return changes;
    }

    // Un binaire se compare par ses octets : Convert.ToString d'un byte[] ne rend que le nom du type.
    private static string? Text(object? value) => value switch
    {
        null => null,
        byte[] bytes => Convert.ToHexString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>Sans ligne complète (spec03 §6.1) : un fragment tout entier dans une colonne fixe ou dans un octet de bits nomme la colonne ;
    /// il en donne les valeurs quand il la couvre entière (pour un octet de bits, les bits qui ont changé).</summary>
    private static IReadOnlyList<ColumnChange> FragmentChanges(LogRecord record, TableSchema schema)
    {
        var changes = new List<ColumnChange>();
        foreach (var f in ModifyDecoder.Decode(record) ?? [])
        {
            // Une compensation ne journalise pas l'avant : la longueur remplacée tient lieu.
            var length = f.Before.Length > 0 ? f.Before.Length : f.ForwardLength;
            if (length == 0) continue;
            var fixedColumns = schema.Columns.Where(c => c.IsFixed && !c.IsDropped).ToList();

            var bits = fixedColumns.Where(c => c.IsBit && c.LeafOffset == f.Offset).ToList();
            if (bits.Count > 0)
            {
                if (length != 1) continue;
                foreach (var c in bits)
                {
                    bool? b = f.Before.Length == 1 ? (f.Before[0] >> c.LeafBitPosition & 1) == 1 : null;
                    bool? a = f.After.Length == 1 ? (f.After[0] >> c.LeafBitPosition & 1) == 1 : null;
                    if (b is not null && b == a) continue;
                    changes.Add(new ColumnChange(c.Name, b, a, true));
                }
                continue;
            }

            var column = fixedColumns.FirstOrDefault(c => !c.IsBit && c.LeafOffset <= f.Offset && f.Offset + length <= c.LeafOffset + c.MaxInrowLength);
            if (column is null) continue;
            var whole = f.Offset == column.LeafOffset && length == column.MaxInrowLength;
            changes.Add(new ColumnChange(column.Name,
                whole && f.Before.Length == length ? SafeValue(column, f.Before) : null,
                whole && f.After.Length == length ? SafeValue(column, f.After) : null,
                true));
        }
        return changes;
    }

    private static object? SafeValue(SchemaColumn c, byte[] bytes)
    {
        try { return TypeDecoder.Decode(c, bytes); }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { return null; }
    }
}
