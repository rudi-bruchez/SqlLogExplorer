using System.Collections.Generic;
using System.Linq;

namespace SqlLogExplorer.Decoding;

/// <summary>Une colonne physique d'une partition (sys.system_internals_partition_columns), spec03 §4.</summary>
public sealed record SchemaColumn(
    int PartitionColumnId, string Name, int LeafOffset, int LeafNullBit, int LeafBitPosition, int MaxInrowLength,
    int SystemTypeId, int Precision, int Scale, int? CodePage, bool IsNullable, bool IsDropped, bool IsUniqueifier,
    bool HasDefault, string? DefaultValue, int? KeyOrdinal)
{
    /// <summary>leaf_offset positif = colonne fixe ; négatif = rang d'une colonne variable (-1, -2…).</summary>
    public bool IsFixed => LeafOffset > 0;
    public bool IsBit => SystemTypeId == 104;
}

/// <summary>Schéma d'une unité d'allocation, colonnes dans l'ordre de leaf_null_bit (spec03 §4, §5).</summary>
public sealed record TableSchema(long AllocUnitId, string SchemaName, string TableName, int IndexId, int AllocUnitType, IReadOnlyList<SchemaColumn> Columns)
{
    /// <summary>Affichage seulement : une requête cite SchemaName et TableName séparément.</summary>
    public string ObjectName => $"{SchemaName}.{TableName}";

    public bool IsDataRows => AllocUnitType == 1 && IndexId is 0 or 1;

    public IEnumerable<SchemaColumn> KeyColumns =>
        Columns.Where(c => !c.IsDropped && c.KeyOrdinal is not null).OrderBy(c => c.KeyOrdinal);

    public SchemaColumn? Uniqueifier => Columns.FirstOrDefault(c => c.IsUniqueifier && !c.IsDropped);
}
