using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlLogExplorer.Backends;
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

        return Replayed(record, data, chain, outcome, schemaMessage, null);
    }

    /// <summary>Rejoue la chaîne, avec la ligne courante pour ancre 3 quand elle est fournie, et en fait l'inspection.</summary>
    private Inspection Replayed(LogRecord record, TableSchema data, RowChain chain, TxOutcome? outcome, string? schemaMessage, byte[]? currentRow)
    {
        var tx = chain.Transactions;
        var result = RowReplayer.Replay(chain.Records, record.Lsn, RowReplayer.Classify(tx), RowReplayer.AdrReverts(tx), RowReplayer.FirstUndoLsns(tx), currentRow);
        var before = result.Before is null ? null : RowDecoder.Decode(result.Before, data);
        var after = result.After is null ? null : RowDecoder.Decode(result.After, data);
        var changes = before is not null && after is not null ? Compare(before, after, result.Before, result.After) : FragmentChanges(record, data);
        // Ancres 1 et 2 sans image : la ligne courante peut servir, en direct, sur une table clustered (spec03 §7.2).
        // Pas pour une transaction partiellement annulée : l'enregistrement termine la chaîne (spec03 §7.3), l'ancre 3 ne peut pas aboutir.
        var canRebuild = _isLive && data.IndexId == 1 && result.Status == NoImage && outcome != TxOutcome.PartlyRolledBack;
        return new Inspection(record, null, before, after, changes, result.Status, outcome, result.IncludesUnknownOutcome, schemaMessage, null, null, canRebuild);
    }

    /// <summary>Ancre 3 (spec03 §7.2), action explicite de l'inspecteur : la ligne courante, lue sous verrou avec la suite du journal,
    /// rejouée à rebours jusqu'à l'enregistrement choisi. Les règles du rejeu valent comme pour les autres ancres (spec03 §7.3, §7.4).
    /// L'échec d'une instruction lève une exception qui nomme l'étape (spec03 §8).</summary>
    public async Task<Inspection> RebuildFromCurrentRowAsync(LogRecord record, CurrentRowReader reader, CancellationToken ct = default)
    {
        record = record with { Lsn = Lsn.Normalize(record.Lsn) };
        var inspection = await InspectAsync(record, ct);
        // Un insert ou un delete est sa propre image ; une compensation n'a pas de clé (spec03 §7.1).
        if (record.Operation is not ("LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS") || record.IsCompensation) return inspection;
        var (schema, _) = await SchemaAsync(record, ct);
        if (schema is not { IsDataRows: true }) return inspection;

        Inspection Unavailable(Inspection i, string status) => i with { Before = null, After = null, ReplayStatus = status, CanRebuildFromCurrentRow = false };
        if (schema.IndexId == 0) return Unavailable(inspection, "not available for a heap");
        if (!await reader.IsAvailableForDatabaseAsync(ct)) return Unavailable(inspection, "not available on a database with Accelerated Database Recovery");
        var key = record.RowLogContents2 is { } index ? KeyDecoder.ValuesFromIndexRecord(index, schema) : null;
        if (key is null) return Unavailable(inspection, "key not decodable, the current row cannot be sought");
        var lastLsn = await _query.GetLastLsnAsync(ct) ?? record.Lsn;

        // La suite du journal entre dans le cache comme un import ; une suppression de la ligne après l'enregistrement choisi,
        // hors transaction annulée (son effet net est nul), donne l'ancre 2.
        async Task<bool> AcceptSuffix(IReadOnlyList<LogRecord> suffix, CancellationToken c)
        {
            await _cache.InsertBatchAsync(suffix, c);
            var chain = await ChainAsync(record, schema, c);
            if (chain is null) return true;
            var outcomes = RowReplayer.Classify(chain.Transactions);
            return !chain.Records.Any(r => r.Operation == Delete && string.CompareOrdinal(r.Lsn, record.Lsn) > 0
                && (r.TransactionId is null || outcomes.GetValueOrDefault(r.TransactionId, TxOutcome.Unknown) != TxOutcome.RolledBack));
        }

        var read = await reader.ReadAsync(schema, key, lastLsn, AcceptSuffix, ct);
        if (read.Unavailable == CurrentRowReader.Deleted) return await InspectAsync(record, ct);
        if (read.Row is null) return Unavailable(await InspectAsync(record, ct), read.Unavailable ?? CurrentRowReader.NotThisRow);

        var rowChain = await ChainAsync(record, schema, ct);
        if (rowChain is null) return Unavailable(inspection, "key not decodable");
        return Replayed(record, schema, rowChain, OutcomeOf(record, RowReplayer.Classify(rowChain.Transactions)), null, read.Row);
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

    /// <summary>La valeur d'une colonne décodée telle que l'inspecteur la montre : avec sa note quand RowDecoder en a posé une.</summary>
    public static object? ValueOf(DecodedColumn c) =>
        c.Note is null ? c.Value : new NotedValue(c.Value, c.Value is not null || c.Note == RowDecoder.AddedAfterRow, c.Note);

    /// <summary>Colonne par colonne, par nom ; valeurs comparées en texte invariant, pour qu'un défaut lu au catalogue ("7") égale l'entier 7.
    /// Une valeur non lue d'un côté : changée si ses octets dans la ligne diffèrent de ceux de l'autre côté (deux pointeurs hors ligne
    /// différents), inconnu sinon ; des octets égaux ne prouvent rien, une valeur hors ligne change sans que son pointeur bouge.</summary>
    internal static IReadOnlyList<ColumnChange> Compare(DecodedRow before, DecodedRow after, byte[]? beforeRow, byte[]? afterRow)
    {
        var afterByName = after.Columns.ToDictionary(c => c.Name);
        var changes = new List<ColumnChange>();
        foreach (var b in before.Columns)
        {
            var a = afterByName.GetValueOrDefault(b.Name);
            if (a is null) { changes.Add(new ColumnChange(b.Name, ValueOf(b), null, true)); continue; }
            var (bv, av) = (ValueOf(b), ValueOf(a));
            bool? changed = bv is NotedValue { HasValue: false } || av is NotedValue { HasValue: false }
                ? Stored(before, beforeRow, b.Name) is { } x && Stored(after, afterRow, a.Name) is { } y && x.Role == y.Role && !x.Bytes.SequenceEqual(y.Bytes) ? true : null
                : Text(bv) != Text(av);
            changes.Add(new ColumnChange(b.Name, bv, av, changed));
        }
        var seen = before.Columns.Select(c => c.Name).ToHashSet();
        foreach (var a in after.Columns.Where(c => !seen.Contains(c.Name)))
            changes.Add(new ColumnChange(a.Name, null, ValueOf(a), true));
        return changes;
    }

    /// <summary>Les octets d'une colonne dans la ligne et leur rôle ; null quand la ligne n'en porte pas.</summary>
    private static (FieldRole Role, byte[] Bytes)? Stored(DecodedRow decoded, byte[]? row, string column)
    {
        var f = decoded.Fields.FirstOrDefault(f => f.Column == column && f.Role is FieldRole.FixedColumn or FieldRole.VariableColumn or FieldRole.LobPointer or FieldRole.Malformed);
        if (row is null || f is null || f.Offset + f.Length > row.Length) return null;
        return (f.Role, row[f.Offset..(f.Offset + f.Length)]);
    }

    // Un binaire se compare par ses octets : Convert.ToString d'un byte[] ne rend que le nom du type.
    private static string? Text(object? value) => value switch
    {
        null => null,
        NotedValue n => Text(n.Value),
        byte[] bytes => Convert.ToHexString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private const string FromFragment = "from the fragment alone, NULL state unknown";

    /// <summary>Sans ligne complète (spec03 §6.1) : un fragment tout entier dans une colonne fixe ou sur un octet de bits nomme la colonne.
    /// Il en donne les valeurs seulement quand l'avant et l'après couvrent exactement ses octets (un octet de chaque côté pour un octet
    /// de bits, et alors les seuls bits qui ont changé), avec une note : sans la ligne, le bitmap de NULL peut les démentir.</summary>
    internal static IReadOnlyList<ColumnChange> FragmentChanges(LogRecord record, TableSchema schema)
    {
        var changes = new List<ColumnChange>();
        var fixedColumns = schema.Columns.Where(c => c.IsFixed && !c.IsDropped).ToList();
        foreach (var f in ModifyDecoder.Decode(record) ?? [])
        {
            // Une compensation ne journalise pas l'avant : la longueur remplacée tient lieu.
            var length = f.Before.Length > 0 ? f.Before.Length : f.ForwardLength;
            if (length == 0) continue;

            var bits = fixedColumns.Where(c => c.IsBit && c.LeafOffset == f.Offset).ToList();
            if (bits.Count > 0)
            {
                // L'octet est touché sans qu'on sache lequel de ses bits a changé.
                if (f.Before.Length != 1 || f.After.Length != 1)
                {
                    changes.AddRange(bits.Select(c => new ColumnChange(c.Name, null, null, null)));
                    continue;
                }
                foreach (var c in bits)
                {
                    var b = (f.Before[0] >> c.LeafBitPosition & 1) == 1;
                    var a = (f.After[0] >> c.LeafBitPosition & 1) == 1;
                    if (b != a) changes.Add(new ColumnChange(c.Name, FragmentValue(b), FragmentValue(a), true));
                }
                continue;
            }

            var column = fixedColumns.FirstOrDefault(c => !c.IsBit && c.LeafOffset <= f.Offset && f.Offset + length <= c.LeafOffset + c.MaxInrowLength);
            if (column is null) continue;
            var exact = f.Offset == column.LeafOffset && f.Before.Length == column.MaxInrowLength && f.After.Length == column.MaxInrowLength;
            changes.Add(new ColumnChange(column.Name,
                exact ? FragmentValue(SafeValue(column, f.Before)) : null,
                exact ? FragmentValue(SafeValue(column, f.After)) : null,
                true));
        }
        return changes;
    }

    private static NotedValue? FragmentValue(object? value) => value is null ? null : new NotedValue(value, true, FromFragment);

    private static object? SafeValue(SchemaColumn c, byte[] bytes)
    {
        try { return TypeDecoder.Decode(c, bytes); }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { return null; }
    }
}
