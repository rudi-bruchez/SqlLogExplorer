using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace SqlLogExplorer.Decoding;

/// <summary>Parcours d'une ligne de données (spec03 §5). Ne lève jamais d'exception : les défauts deviennent des champs Malformed.</summary>
public static class RowDecoder
{
    /// <summary>Note d'une colonne absente de la ligne : sa valeur est le défaut du catalogue (ou NULL), pas un refus.</summary>
    public const string AddedAfterRow = "added after the row was written";

    public static DecodedRow Decode(byte[]? row, TableSchema? schema)
    {
        var fields = new List<DecodedField>();
        var columns = new List<DecodedColumn>();
        if (row is null || row.Length < 4)
        {
            fields.Add(new DecodedField(0, row?.Length ?? 0, FieldRole.Malformed, Problem: row is null || row.Length == 0 ? "no row image" : "row shorter than its header"));
            return new DecodedRow(row is { Length: > 0 } ? row[0] >> 1 & 7 : -1, fields, columns);
        }

        var a = row[0];
        var type = a >> 1 & 7;
        // Le bit 0 de A n'appartient à aucun champ du format FixedVar ; il est posé sur toutes les lignes compressées mesurées (ROW et PAGE).
        if ((a & 1) != 0)
        {
            fields.Add(new DecodedField(0, row.Length, FieldRole.Malformed, Problem: "not a FixedVar row (compressed rows are not decoded)"));
            return new DecodedRow(type, fields, columns);
        }
        if (schema is not null && IsCompressed(schema))
        {
            fields.Add(new DecodedField(0, row.Length, FieldRole.Malformed, Problem: "compressed rows are not decoded"));
            return new DecodedRow(type, fields, columns);
        }
        fields.Add(new DecodedField(0, 1, FieldRole.StatusA, Value: a));
        fields.Add(new DecodedField(1, 1, FieldRole.StatusB, Value: row[1]));
        if (type != 0)
        {
            fields.Add(new DecodedField(2, row.Length - 2, FieldRole.Unused));
            return new DecodedRow(type, fields, columns);
        }

        int fixedEnd = BinaryPrimitives.ReadUInt16LittleEndian(row.AsSpan(2));
        fields.Add(new DecodedField(2, 2, FieldRole.FixedEnd, Value: fixedEnd));
        if (fixedEnd < 4 || fixedEnd + 2 > row.Length)
            return Malformed(row, fields, columns, type, 4, "fixed-data end offset beyond the row");

        int n = BinaryPrimitives.ReadUInt16LittleEndian(row.AsSpan(fixedEnd));
        fields.Add(new DecodedField(fixedEnd, 2, FieldRole.ColumnCount, Value: n));
        var bitmapLength = (a & 0x10) != 0 ? (n + 7) / 8 : 0;
        var bitmapStart = fixedEnd + 2;
        if (bitmapStart + bitmapLength > row.Length)
            return Malformed(row, fields, columns, type, bitmapStart, "null bitmap beyond the row");
        if (bitmapLength > 0) fields.Add(new DecodedField(bitmapStart, bitmapLength, FieldRole.NullBitmap));
        bool IsNull(int nullBit) => bitmapLength > 0 && (row[bitmapStart + (nullBit - 1) / 8] >> ((nullBit - 1) % 8) & 1) == 1;

        // Zone variable (bit 5 de A), offsets masqués par 0x7FFF, bit 0x8000 = pointeur hors ligne.
        var pos = bitmapStart + bitmapLength;
        var ends = new List<(int End, bool OffRow)>();
        var varStart = pos;
        if ((a & 0x20) != 0)
        {
            if (pos + 2 > row.Length) return Malformed(row, fields, columns, type, pos, "variable column count beyond the row");
            int v = BinaryPrimitives.ReadUInt16LittleEndian(row.AsSpan(pos));
            fields.Add(new DecodedField(pos, 2, FieldRole.VariableCount, Value: v));
            varStart = pos + 2 + 2 * v;
            if (varStart > row.Length) return Malformed(row, fields, columns, type, pos + 2, "offset array beyond the row");
            var previous = varStart;
            for (var j = 0; j < v; j++)
            {
                int entry = BinaryPrimitives.ReadUInt16LittleEndian(row.AsSpan(pos + 2 + 2 * j));
                var end = entry & 0x7FFF;
                if (end < previous || end > row.Length)
                    return Malformed(row, fields, columns, type, pos + 2 + 2 * j, "variable end offset out of order or beyond the row");
                fields.Add(new DecodedField(pos + 2 + 2 * j, 2, FieldRole.VariableOffset, Value: end));
                ends.Add((end, (entry & 0x8000) != 0));
                previous = end;
            }
        }
        var dataEnd = ends.Count > 0 ? ends[^1].End : varStart;

        if (schema is null)
        {
            if (fixedEnd > 4) fields.Add(new DecodedField(4, fixedEnd - 4, FieldRole.FixedColumn));
            for (var j = 0; j < ends.Count; j++)
            {
                var start = j == 0 ? varStart : ends[j - 1].End;
                fields.Add(new DecodedField(start, ends[j].End - start, ends[j].OffRow ? FieldRole.LobPointer : FieldRole.VariableColumn));
            }
        }
        else
        {
            if (n > schema.Columns.Count)
                fields.Add(new DecodedField(fixedEnd, 2, FieldRole.Malformed, Problem: "column count greater than the schema's"));
            // spec03 §8 : la fin de la partie fixe doit être celle que le schéma prévoit pour les N colonnes présentes.
            var expectedEnd = schema.Columns.Where(c => c.IsFixed && c.LeafNullBit <= n)
                .Select(c => c.LeafOffset + (c.IsBit ? 1 : c.MaxInrowLength)).DefaultIfEmpty(4).Max();
            if (expectedEnd != fixedEnd)
                fields.Add(new DecodedField(2, 2, FieldRole.Malformed, Problem: $"fixed-data end offset {fixedEnd}, the schema predicts {expectedEnd}"));
            var claimed = new bool[fixedEnd];
            foreach (var c in schema.Columns)
                DecodeColumn(row, c, n, IsNull, fixedEnd, varStart, ends, fields, columns, claimed);
            for (var i = 4; i < fixedEnd; i++)
            {
                if (claimed[i]) continue;
                var j = i;
                while (j < fixedEnd && !claimed[j]) j++;
                fields.Add(new DecodedField(i, j - i, FieldRole.Unused));
                i = j;
            }
        }

        // Bit 6 de A : étiquette de version de 14 octets après les données variables ; au-delà, traîne inconnue (ADR).
        var tail = dataEnd;
        if ((a & 0x40) != 0)
        {
            if (tail + 14 > row.Length) fields.Add(new DecodedField(tail, row.Length - tail, FieldRole.Malformed, Problem: "versioning tag beyond the row"));
            else { fields.Add(new DecodedField(tail, 14, FieldRole.VersionTag)); tail += 14; }
        }
        if (tail < row.Length) fields.Add(new DecodedField(tail, row.Length - tail, FieldRole.UnknownTrailer));

        return new DecodedRow(type, fields.OrderBy(f => f.Offset).ToList(), columns);
    }

    // Dans une partition compressée (ROW ou PAGE), toutes les colonnes ont un leaf_offset négatif, y compris celles d'un type de longueur fixe.
    private static bool IsCompressed(TableSchema schema) =>
        schema.Columns.Any(c => !c.IsDropped && !c.IsUniqueifier && c.LeafOffset < 0 && !IsVariableType(c.SystemTypeId));

    // varchar, nvarchar, varbinary, text, ntext, image, sql_variant, xml, types CLR.
    private static bool IsVariableType(int systemTypeId) => systemTypeId is 167 or 231 or 165 or 35 or 99 or 34 or 98 or 241 or 240;

    private static void DecodeColumn(byte[] row, SchemaColumn c, int n, Func<int, bool> isNull, int fixedEnd, int varStart,
        List<(int End, bool OffRow)> ends, List<DecodedField> fields, List<DecodedColumn> columns, bool[] claimed)
    {
        if (c.LeafOffset == 0)   // colonne éparse (SPARSE) : ses octets sont dans le vecteur épars, que ce décodeur ne lit pas
        {
            if (!c.IsDropped)
            {
                columns.Add(new DecodedColumn(c.Name, null, "sparse column not decoded"));
                fields.Add(new DecodedField(row.Length, 0, FieldRole.Malformed, c.Name, Problem: "sparse column not decoded"));
            }
            return;
        }

        if (c.LeafNullBit > n)   // colonne ajoutée après l'écriture de la ligne : son leaf_offset n'est jamais lu
        {
            if (!c.IsDropped)
                columns.Add(new DecodedColumn(c.Name, c.HasDefault ? c.DefaultValue : null, AddedAfterRow));
            return;
        }

        if (c.IsFixed)
        {
            var length = c.IsBit ? 1 : c.MaxInrowLength;
            if (c.LeafOffset + length > fixedEnd)
            {
                fields.Add(new DecodedField(c.LeafOffset, Math.Max(0, fixedEnd - c.LeafOffset), FieldRole.Malformed, c.Name,
                    Problem: "fixed column beyond the fixed-data end offset"));
                return;
            }
            for (var i = c.LeafOffset; i < c.LeafOffset + length; i++) claimed[i] = true;
            if (c.IsDropped) { fields.Add(new DecodedField(c.LeafOffset, length, FieldRole.DroppedColumn, c.Name)); return; }
            Add(row.AsSpan(c.LeafOffset, length), c.LeafOffset, FieldRole.FixedColumn);
            return;
        }

        var j = -c.LeafOffset - 1;
        if (j >= ends.Count)   // absente du tableau d'offsets (ou zone variable absente) : NULL si le bit le dit, sinon vide
        {
            if (c.IsDropped) return;
            var (absent, absentProblem) = isNull(c.LeafNullBit) ? (null, null) : SafeDecode(c, ReadOnlySpan<byte>.Empty);
            columns.Add(new DecodedColumn(c.Name, absent, absentProblem));
            if (absentProblem is not null) fields.Add(new DecodedField(row.Length, 0, FieldRole.Malformed, c.Name, Problem: absentProblem));
            return;
        }
        var start = j == 0 ? varStart : ends[j - 1].End;
        var len = ends[j].End - start;
        if (c.IsDropped) { fields.Add(new DecodedField(start, len, FieldRole.DroppedColumn, c.Name)); return; }
        if (ends[j].OffRow && !isNull(c.LeafNullBit))
        {
            fields.Add(new DecodedField(start, len, FieldRole.LobPointer, c.Name));
            columns.Add(new DecodedColumn(c.Name, null, "off-row value not followed"));
            return;
        }
        Add(row.AsSpan(start, len), start, FieldRole.VariableColumn);

        void Add(ReadOnlySpan<byte> bytes, int offset, FieldRole role)
        {
            if (isNull(c.LeafNullBit))
            {
                fields.Add(new DecodedField(offset, bytes.Length, role, c.Name));
                columns.Add(new DecodedColumn(c.Name, null));
                return;
            }
            var (value, problem) = SafeDecode(c, bytes);
            fields.Add(new DecodedField(offset, bytes.Length, problem is null ? role : FieldRole.Malformed, c.Name, value, problem));
            columns.Add(new DecodedColumn(c.Name, value, problem ?? (TypeDecoder.IsSupported(c.SystemTypeId) ? null : "type not decoded")));
        }
    }

    // L'uniqueifier de la première ligne d'une clé est une colonne variable de longueur nulle : il vaut 0 (spec03 §5).
    private static (object? Value, string? Problem) SafeDecode(SchemaColumn c, ReadOnlySpan<byte> bytes)
    {
        if (c.IsUniqueifier && bytes.Length == 0) return (0, null);
        try { return (TypeDecoder.Decode(c, bytes), null); }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException)
        {
            return (null, $"{bytes.Length} bytes do not fit the column type");
        }
    }

    private static DecodedRow Malformed(byte[] row, List<DecodedField> fields, List<DecodedColumn> columns, int type, int from, string problem)
    {
        fields.Add(new DecodedField(from, row.Length - from, FieldRole.Malformed, Problem: problem));
        return new DecodedRow(type, fields, columns);
    }
}
