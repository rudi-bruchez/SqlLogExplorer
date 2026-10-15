using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SqlLogExplorer.Decoding;

/// <summary>
/// Clé clustered canonique d'une ligne (spec03 §7.1), lue dans l'image d'une ligne ou dans l'enregistrement d'index
/// de RowLog Contents 2 (docs/research/rowlog-layouts.md). Fonction pure : renvoie null quand la clé n'est pas décodable.
/// Format : par colonne de clé, dans l'ordre de la clé, "N;" si NULL sinon "V" + octets stockés en hexadécimal + ";"
/// (une colonne bit : "V00;" ou "V01;") ; puis "U" + l'uniqueifier si l'index en a un.
/// </summary>
public static class KeyDecoder
{
    public static string? FromRow(byte[]? row, TableSchema schema)
    {
        if (row is null || row.Length == 0) return null;
        var decoded = RowDecoder.Decode(row, schema);
        var sb = new StringBuilder();
        foreach (var c in schema.KeyColumns)
        {
            var field = decoded.Fields.FirstOrDefault(f => f.Column == c.Name && f.Role is FieldRole.FixedColumn or FieldRole.VariableColumn);
            var column = decoded.Columns.FirstOrDefault(x => x.Name == c.Name);
            if (field is null || column is null) return null;   // colonne illisible : Malformed, absente ou hors zone
            // NULL : valeur absente sans réserve ; un type non décodé garde une note mais ses octets restent comparables.
            if (column.Value is null && column.Note is null) AppendNull(sb);
            else if (c.IsBit) AppendBit(sb, column.Value is true);
            else AppendBytes(sb, row.AsSpan(field.Offset, field.Length));
        }
        var u = schema.Uniqueifier;
        if (u is not null)
        {
            // Premier enregistrement d'une clé : uniqueifier de longueur nulle ou absent, soit 0 (RowDecoder).
            var value = decoded.Columns.FirstOrDefault(x => x.Name == u.Name)?.Value;
            sb.Append('U').Append(value is int i ? i : 0);
        }
        return sb.ToString();
    }

    public static string? FromIndexRecord(byte[]? record, TableSchema schema)
    {
        if (record is null || record.Length < 1) return null;
        var keys = schema.KeyColumns.ToList();
        var u = schema.Uniqueifier;
        if (keys.Count == 0) return null;
        var count = keys.Count + (u is null ? 0 : 1);   // C : la liste de clés, uniqueifier compris

        // Statut A : enregistrement d'index (type 3), bitmap de NULL toujours présent, zone variable si une colonne l'est.
        var status = record[0];
        var variableCount = keys.Count(c => !c.IsFixed) + (u is null ? 0 : 1);
        if ((status >> 1 & 7) != 3 || (status & 0x10) == 0 || ((status & 0x20) != 0) != (variableCount > 0)) return null;

        // Partie fixe depuis l'octet 1, dans l'ordre de la clé ; les colonnes bit se partagent un octet par groupe de huit.
        var fixedAt = new Dictionary<SchemaColumn, (int Offset, int Bit)>();
        var pos = 1;
        var bits = 0;
        var groupAt = 0;
        foreach (var c in keys)
        {
            if (c.IsBit)
            {
                if (bits % 8 == 0) { groupAt = pos; pos++; }
                fixedAt[c] = (groupAt, bits % 8);
                bits++;
            }
            else if (c.IsFixed) { fixedAt[c] = (pos, -1); pos += c.MaxInrowLength; }
        }

        if (pos + 2 > record.Length || BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(pos)) != count) return null;
        var bitmapAt = pos + 2;
        var bitmapEnd = bitmapAt + (count + 7) / 8;
        if (bitmapEnd > record.Length) return null;
        bool IsNull(int i) => (record[bitmapAt + i / 8] >> (i % 8) & 1) == 1;

        // Zone variable : nombre, fins d'octets (depuis le début de l'enregistrement), puis les données.
        var ends = new List<int>();
        var dataStart = bitmapEnd;
        if (variableCount > 0)
        {
            if (bitmapEnd + 2 > record.Length || BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(bitmapEnd)) != variableCount) return null;
            dataStart = bitmapEnd + 2 + 2 * variableCount;
            if (dataStart > record.Length) return null;
            var previous = dataStart;
            for (var j = 0; j < variableCount; j++)
            {
                int end = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(bitmapEnd + 2 + 2 * j));
                if (end < previous || end > record.Length) return null;
                ends.Add(end);
                previous = end;
            }
        }

        var sb = new StringBuilder();
        var variableIndex = 0;
        for (var i = 0; i < keys.Count; i++)
        {
            var c = keys[i];
            ReadOnlySpan<byte> bytes;
            if (c.IsFixed || c.IsBit)
            {
                var (offset, bit) = fixedAt[c];
                var length = c.IsBit ? 1 : c.MaxInrowLength;
                if (offset + length > record.Length) return null;
                if (IsNull(i)) { AppendNull(sb); continue; }
                if (bit >= 0) { AppendBit(sb, (record[offset] >> bit & 1) == 1); continue; }   // bits étrangers de l'octet masqués
                bytes = record.AsSpan(offset, length);
            }
            else
            {
                bytes = Variable(record, ends, dataStart, variableIndex++);
                if (IsNull(i)) { AppendNull(sb); continue; }
            }
            AppendBytes(sb, bytes);
        }

        if (u is not null)
        {
            var bytes = Variable(record, ends, dataStart, variableIndex);
            if (bytes.Length != 4) return null;   // toujours 4 octets dans l'index, même pour la première ligne d'une clé
            sb.Append('U').Append(BinaryPrimitives.ReadInt32LittleEndian(bytes));
        }
        return sb.ToString();
    }

    private static ReadOnlySpan<byte> Variable(byte[] record, List<int> ends, int dataStart, int index)
        => record.AsSpan(index == 0 ? dataStart : ends[index - 1], ends[index] - (index == 0 ? dataStart : ends[index - 1]));

    private static void AppendNull(StringBuilder sb) => sb.Append("N;");
    private static void AppendBit(StringBuilder sb, bool on) => sb.Append(on ? "V01;" : "V00;");
    private static void AppendBytes(StringBuilder sb, ReadOnlySpan<byte> bytes) => sb.Append('V').Append(Convert.ToHexString(bytes)).Append(';');
}
