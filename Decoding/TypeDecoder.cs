using System;
using System.Buffers.Binary;
using System.Data.SqlTypes;
using System.Text;

namespace SqlLogExplorer.Decoding;

/// <summary>Octets d'une colonne vers une valeur .NET, selon le format disque de SQL Server (spec03 §1.1, §4).</summary>
public static class TypeDecoder
{
    static TypeDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly DateTime Epoch1900 = new(1900, 1, 1);

    public static bool IsSupported(int systemTypeId) => systemTypeId is
        48 or 52 or 56 or 127 or 104 or 106 or 108 or 60 or 122 or 59 or 62 or
        40 or 41 or 61 or 58 or 42 or 43 or 36 or 175 or 167 or 239 or 231 or 173 or 165;

    public static object? Decode(SchemaColumn c, ReadOnlySpan<byte> b) => c.SystemTypeId switch
    {
        48 => b[0],
        52 => BinaryPrimitives.ReadInt16LittleEndian(b),
        56 => BinaryPrimitives.ReadInt32LittleEndian(b),
        127 => BinaryPrimitives.ReadInt64LittleEndian(b),
        104 => (b[0] >> c.LeafBitPosition & 1) == 1,
        106 or 108 => Decimal(c, b),
        60 => BinaryPrimitives.ReadInt64LittleEndian(b) / 10000m,
        122 => BinaryPrimitives.ReadInt32LittleEndian(b) / 10000m,
        59 => BinaryPrimitives.ReadSingleLittleEndian(b),
        62 => BinaryPrimitives.ReadDoubleLittleEndian(b),
        40 => DateOnly.FromDayNumber(UInt24(b)),
        41 => Time(b, c.Scale),
        61 => Epoch1900.AddDays(BinaryPrimitives.ReadInt32LittleEndian(b[4..]))
                       .AddTicks((long)Math.Round(BinaryPrimitives.ReadUInt32LittleEndian(b) * (TimeSpan.TicksPerSecond / 300.0))),
        58 => Epoch1900.AddDays(BinaryPrimitives.ReadUInt16LittleEndian(b[2..])).AddMinutes(BinaryPrimitives.ReadUInt16LittleEndian(b)),
        42 => DateTime2(b, c.Scale),
        43 => DateTimeOffset(b, c.Scale),
        36 => new Guid(b),
        175 or 167 => Encoding.GetEncoding(c.CodePage ?? 1252).GetString(b),
        239 or 231 => Encoding.Unicode.GetString(b),
        173 or 165 => b.ToArray(),
        _ => b.ToArray(),
    };

    // decimal/numeric : 1 octet de signe (1 = positif), puis l'entier non signé sur 4, 8, 12 ou 16 octets.
    private static SqlDecimal Decimal(SchemaColumn c, ReadOnlySpan<byte> b)
    {
        var data = new int[4];
        for (var i = 0; i < (b.Length - 1) / 4; i++) data[i] = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(1 + 4 * i, 4));
        return new SqlDecimal((byte)c.Precision, (byte)c.Scale, b[0] == 1, data);
    }

    private static int UInt24(ReadOnlySpan<byte> b) => b[0] | b[1] << 8 | b[2] << 16;

    // time(n) : 3 à 5 octets, unités de 10^-n seconde.
    private static long TimeTicks(ReadOnlySpan<byte> b, int scale)
    {
        long units = 0;
        for (var i = b.Length - 1; i >= 0; i--) units = units << 8 | b[i];
        for (var i = scale; i < 7; i++) units *= 10;
        return units;
    }

    private static int TimeLength(int scale) => scale <= 2 ? 3 : scale <= 4 ? 4 : 5;

    private static TimeOnly Time(ReadOnlySpan<byte> b, int scale) => new(TimeTicks(b, scale));

    private static DateTime DateTime2(ReadOnlySpan<byte> b, int scale)
    {
        var t = TimeLength(scale);
        return DateOnly.FromDayNumber(UInt24(b[t..])).ToDateTime(TimeOnly.MinValue).AddTicks(TimeTicks(b[..t], scale));
    }

    // datetimeoffset : heure UTC, date UTC, puis décalage en minutes (int16).
    private static DateTimeOffset DateTimeOffset(ReadOnlySpan<byte> b, int scale)
    {
        var t = TimeLength(scale);
        var utc = DateTime2(b[..(t + 3)], scale);
        var offset = TimeSpan.FromMinutes(BinaryPrimitives.ReadInt16LittleEndian(b[(t + 3)..]));
        return new DateTimeOffset(utc + offset, offset);
    }
}
