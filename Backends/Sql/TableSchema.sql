-- Schéma physique d'une unité d'allocation (spec03 §4). @AllocUnitId NULL = toutes les unités utilisateur.
-- sys.columns en jointure externe : les colonnes supprimées et l'uniqueifier n'y ont pas de ligne.
SELECT au.allocation_unit_id                                   AS AllocUnitId,
       OBJECT_SCHEMA_NAME(p.object_id)                         AS SchemaName,
       OBJECT_NAME(p.object_id)                                AS TableName,
       p.index_id                                              AS IndexId,
       au.type                                                 AS AllocUnitType,
       pc.partition_column_id                                  AS PartitionColumnId,
       c.name                                                  AS ColumnName,
       pc.leaf_offset                                          AS LeafOffset,
       pc.leaf_null_bit                                        AS LeafNullBit,
       pc.leaf_bit_position                                    AS LeafBitPosition,
       pc.max_inrow_length                                     AS MaxInrowLength,
       pc.system_type_id                                       AS SystemTypeId,
       pc.precision                                            AS [Precision],
       pc.scale                                                AS Scale,
       CONVERT(int, COLLATIONPROPERTY(pc.collation_name, 'CodePage')) AS CodePage,
       pc.is_nullable                                          AS IsNullable,
       pc.is_dropped                                           AS IsDropped,
       pc.is_uniqueifier                                       AS IsUniqueifier,
       pc.has_default                                          AS HasDefault,
       CONVERT(nvarchar(4000), pc.default_value)               AS DefaultValue,
       ic.key_ordinal                                          AS KeyOrdinal
FROM sys.allocation_units AS au
JOIN sys.partitions AS p ON p.partition_id = au.container_id
JOIN sys.system_internals_partition_columns AS pc ON pc.partition_id = p.partition_id
LEFT JOIN sys.columns AS c
       ON c.object_id = p.object_id AND c.column_id = pc.partition_column_id AND pc.is_dropped = 0
LEFT JOIN sys.index_columns AS ic
       ON ic.object_id = p.object_id AND ic.index_id = 1 AND p.index_id = 1
      AND ic.column_id = pc.partition_column_id AND ic.key_ordinal > 0 AND pc.is_dropped = 0
WHERE OBJECTPROPERTY(p.object_id, 'IsMsShipped') = 0
  AND au.type IN (1, 3)
  AND (@AllocUnitId IS NULL OR au.allocation_unit_id = @AllocUnitId)
ORDER BY au.allocation_unit_id, pc.leaf_null_bit;
