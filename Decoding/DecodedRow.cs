using System.Collections.Generic;
using System.Linq;

namespace SqlLogExplorer.Decoding;

/// <summary>Rôle d'une plage d'octets dans un enregistrement de ligne (spec03 §4).</summary>
public enum FieldRole
{
    StatusA, StatusB, FixedEnd, FixedColumn, ColumnCount, NullBitmap, VariableCount, VariableOffset,
    VariableColumn, LobPointer, VersionTag, Unused, DroppedColumn, UnknownTrailer, Malformed,
}

public sealed record DecodedField(int Offset, int Length, FieldRole Role, string? Column = null, object? Value = null, string? Problem = null);

/// <summary>Valeur null = NULL SQL.</summary>
public sealed record DecodedColumn(string Name, object? Value, string? Note = null);

public sealed record DecodedRow(int RecordType, IReadOnlyList<DecodedField> Fields, IReadOnlyList<DecodedColumn> Columns)
{
    public bool HasProblem => Fields.Any(f => f.Role == FieldRole.Malformed || f.Problem is not null);

    public string RecordTypeName => RecordType switch
    {
        0 => "primary record", 1 => "forwarded record", 2 => "forwarding stub",
        3 => "index record", 4 => "blob fragment", 5 => "ghost index record", 6 => "ghost data record", 7 => "ghost version record",
        _ => "unknown",
    };
}
