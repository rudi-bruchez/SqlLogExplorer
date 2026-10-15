using System.Text.Json;
using SqlLogExplorer.Decoding;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Tests;

/// <summary>Une ligne de DBCC PAGE WITH TABLERESULTS (style 1 ou 3), figée avec le journal.</summary>
public sealed record PageDumpLine(int Style, string PageId, string ParentObject, string Object, string Field, string Value);

/// <summary>Journal, schémas et pages d'un scénario capturé sur une version de SQL Server (spec03 §10.1).</summary>
public sealed record Fixture(string Version, IReadOnlyList<LogRecord> Log, IReadOnlyDictionary<long, TableSchema> Schemas, IReadOnlyList<PageDumpLine> Pages)
{
    private static readonly HashSet<string> DataOperations = ["LOP_INSERT_ROWS", "LOP_DELETE_ROWS", "LOP_MODIFY_ROW", "LOP_MODIFY_COLUMNS"];

    // Le journal porte aussi des enregistrements LCX_PFS qui citent l'AllocUnitId de la table (spec03 §5).
    private static readonly HashSet<string> DataContexts = ["LCX_HEAP", "LCX_CLUSTERED", "LCX_MARK_AS_GHOST"];

    /// <summary>Schéma en ligne de la table (l'unité d'allocation la plus récente du journal) et ses enregistrements de données dans l'ordre des LSN.</summary>
    public (TableSchema Schema, IReadOnlyList<LogRecord> Records) ForTable(string objectName)
    {
        var inLog = Log.Where(r => r.AllocUnitId is not null).Select(r => r.AllocUnitId!.Value).ToHashSet();
        var schema = Schemas.Values
            .Where(s => s.ObjectName == objectName && s.IsDataRows && inLog.Contains(s.AllocUnitId))
            .OrderByDescending(s => s.AllocUnitId)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Table '{objectName}' absente du journal de la fixture {Version}.");
        var records = Log
            .Where(r => r.AllocUnitId == schema.AllocUnitId && DataOperations.Contains(r.Operation)
                        && r.Context is not null && DataContexts.Contains(r.Context))
            .OrderBy(r => r.Lsn, StringComparer.Ordinal)
            .ToList();
        return (schema, records);
    }
}

public static class FixtureSet
{
    public static readonly IReadOnlyList<string> Versions = ["2017", "2019", "2022", "2025"];

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static Fixture Load(string version, string scenario)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", version, scenario + ".json");
        var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException($"Fixture vide : {path}");

        // Les identifiants de page du journal sont en minuscules, ceux de DBCC PAGE en majuscules : on stocke en majuscules, comme les LSN.
        // Un tableau JSON vide passé par JSON_QUERY peut devenir null : on le traite comme vide.
        var log = (doc.Log ?? []).Select(l => new LogRecord(
            SqlLogExplorer.Models.Lsn.Normalize(l.Lsn), l.Operation, l.Context, l.TransactionId, l.AllocUnitName, l.Rlc0, l.Rlc1)
        {
            AllocUnitId = l.AllocUnitId, PartitionId = l.PartitionId, PageId = l.PageId?.ToUpperInvariant(), SlotId = l.SlotId,
            OffsetInRow = l.OffsetInRow, ModifySize = l.ModifySize, NumElements = l.NumElements,
            Description = l.Description, TransactionName = l.TransactionName,
            RowLogContents2 = l.Rlc2, RowLogContents3 = l.Rlc3, RowLogContents4 = l.Rlc4, RowLogContents5 = l.Rlc5,
        }).ToList();

        var schemas = (doc.Schema ?? []).GroupBy(c => c.AllocUnitId).ToDictionary(g => g.Key, g =>
        {
            var first = g.First();
            var columns = g.OrderBy(c => c.LeafNullBit).ThenBy(c => c.PartitionColumnId).Select(c =>
            {
                var name = c.ColumnName
                    ?? (c.IsUniqueifier ? "uniqueifier" : c.IsDropped ? $"dropped#{c.PartitionColumnId}" : $"column#{c.PartitionColumnId}");
                return new SchemaColumn(c.PartitionColumnId, name, c.LeafOffset, c.LeafNullBit, c.LeafBitPosition, c.MaxInrowLength,
                    c.SystemTypeId, c.Precision, c.Scale, c.CodePage, c.IsNullable, c.IsDropped, c.IsUniqueifier,
                    c.HasDefault, c.DefaultValue, c.KeyOrdinal);
            }).ToList();
            return new TableSchema(g.Key, first.SchemaName, first.TableName, first.IndexId, first.AllocUnitType, columns);
        });

        var pages = (doc.Pages ?? []).Select(p => new PageDumpLine(p.Style, p.PageId.ToUpperInvariant(), p.ParentObject ?? "", p.Object ?? "", p.Field ?? "", p.Value ?? "")).ToList();
        return new Fixture(version, log, schemas, pages);
    }

    private sealed class Document
    {
        public string? ProductVersion { get; set; }
        public List<LogRow>? Log { get; set; }
        public List<SchemaRow>? Schema { get; set; }
        public List<PageRow>? Pages { get; set; }
    }

    private sealed class LogRow
    {
        public string Lsn { get; set; } = "";
        public string Operation { get; set; } = "";
        public string? Context { get; set; }
        public string? TransactionId { get; set; }
        public string? AllocUnitName { get; set; }
        public long? AllocUnitId { get; set; }
        public long? PartitionId { get; set; }
        public string? PageId { get; set; }
        public int? SlotId { get; set; }
        public int? OffsetInRow { get; set; }
        public int? ModifySize { get; set; }
        public int? NumElements { get; set; }
        public string? Description { get; set; }
        public string? TransactionName { get; set; }
        public byte[]? Rlc0 { get; set; }
        public byte[]? Rlc1 { get; set; }
        public byte[]? Rlc2 { get; set; }
        public byte[]? Rlc3 { get; set; }
        public byte[]? Rlc4 { get; set; }
        public byte[]? Rlc5 { get; set; }
    }

    private sealed class SchemaRow
    {
        public long AllocUnitId { get; set; }
        public string SchemaName { get; set; } = "";
        public string TableName { get; set; } = "";
        public int IndexId { get; set; }
        public int AllocUnitType { get; set; }
        public int PartitionColumnId { get; set; }
        public string? ColumnName { get; set; }
        public int LeafOffset { get; set; }
        public int LeafNullBit { get; set; }
        public int LeafBitPosition { get; set; }
        public int MaxInrowLength { get; set; }
        public int SystemTypeId { get; set; }
        public int Precision { get; set; }
        public int Scale { get; set; }
        public int? CodePage { get; set; }
        public bool IsNullable { get; set; }
        public bool IsDropped { get; set; }
        public bool IsUniqueifier { get; set; }
        public bool HasDefault { get; set; }
        public string? DefaultValue { get; set; }
        public int? KeyOrdinal { get; set; }
    }

    private sealed class PageRow
    {
        public int Style { get; set; }
        public string PageId { get; set; } = "";
        public string? ParentObject { get; set; }
        public string? Object { get; set; }
        public string? Field { get; set; }
        public string? Value { get; set; }
    }
}
