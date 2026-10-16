using System;
using System.Buffers.Binary;
using System.Data.SqlTypes;
using System.Globalization;
using System.Text;

namespace SqlLogExplorer.Decoding;

/// <summary>Valeur temporelle décodée (date, time, datetime, smalldatetime, datetime2, datetimeoffset) et son texte, celui que SQL Server
/// affiche : ISO, fraction à l'échelle de la colonne. Le texte sert à l'affichage comme à la comparaison : le type .NET seul ne dit
/// ni l'échelle ni le type SQL (datetime, datetime2 et smalldatetime donnent tous un DateTime).</summary>
public sealed record SqlTemporal(object Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>Octets d'une colonne vers une valeur .NET, selon le format disque de SQL Server (spec03 §1.1, §4).</summary>
public static class TypeDecoder
{
    static TypeDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly DateTime Epoch1900 = new(1900, 1, 1);

    public static bool IsSupported(int systemTypeId) => systemTypeId is
        48 or 52 or 56 or 127 or 104 or 106 or 108 or 60 or 122 or 59 or 62 or
        40 or 41 or 61 or 58 or 42 or 43 or 36 or 175 or 167 or 239 or 231 or 173 or 165;

    /// <summary>Lève toujours ArgumentException (ou une sous-classe) sur une entrée invalide : longueur fausse ou valeur hors limites.</summary>
    public static object? Decode(SchemaColumn c, ReadOnlySpan<byte> b)
    {
        CheckLength(c, b.Length);
        try
        {
            var value = DecodeValue(c, b);
            return c.SystemTypeId is 40 or 41 or 42 or 43 or 58 or 61 ? new SqlTemporal(value!, TemporalText(c, value!)) : value;
        }
        catch (Exception e) when (e is OverflowException or NotSupportedException)
        {
            throw new ArgumentException($"value out of range for system type {c.SystemTypeId}: {e.Message}", e);
        }
    }

    private static void CheckLength(SchemaColumn c, int actual)
    {
        var expected = c.SystemTypeId switch
        {
            48 or 104 => 1,
            52 => 2,
            56 or 59 or 122 or 58 => 4,
            127 or 62 or 60 or 61 => 8,
            40 => 3,
            36 => 16,
            106 or 108 => c.Precision switch
            {
                >= 1 and <= 9 => 5, >= 10 and <= 19 => 9, >= 20 and <= 28 => 13, >= 29 and <= 38 => 17,
                _ => throw new ArgumentException($"invalid precision {c.Precision} for decimal"),
            },
            41 => TimeLength(CheckedScale(c)),
            42 => TimeLength(CheckedScale(c)) + 3,
            43 => TimeLength(CheckedScale(c)) + 5,
            _ => actual, // chaînes et binaires : toute longueur convient ; type non pris en charge : octets bruts
        };
        if (actual != expected) throw new ArgumentException($"expected {expected} bytes for system type {c.SystemTypeId}, got {actual}");
        if (c.SystemTypeId == 104 && c.LeafBitPosition is < 0 or > 7) throw new ArgumentException($"invalid bit position {c.LeafBitPosition}");
    }

    private static int CheckedScale(SchemaColumn c) =>
        c.Scale is >= 0 and <= 7 ? c.Scale : throw new ArgumentException($"invalid scale {c.Scale} for system type {c.SystemTypeId}");

    private static object? DecodeValue(SchemaColumn c, ReadOnlySpan<byte> b) => c.SystemTypeId switch
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

    private static string TemporalText(SchemaColumn c, object value)
    {
        var fraction = c.Scale == 0 ? "" : "." + new string('f', c.Scale);
        return value switch
        {
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly t => t.ToString("HH:mm:ss" + fraction, CultureInfo.InvariantCulture),
            DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss" + fraction + " zzz", CultureInfo.InvariantCulture),
            // datetime : des 1/300 s, que SQL Server montre arrondies à la milliseconde (.997 pour 299/300) ; jamais 1000, 299/300 donne 997.
            DateTime d when c.SystemTypeId == 61 => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                + "." + ((int)Math.Round(d.Ticks % TimeSpan.TicksPerSecond / (double)TimeSpan.TicksPerMillisecond)).ToString("000", CultureInfo.InvariantCulture),
            DateTime d when c.SystemTypeId == 58 => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss" + fraction, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
    }

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

    // L'heure passe par TimeOnly, comme pour time : 24 h ou plus lève au lieu de glisser au jour suivant.
    private static DateTime DateTime2(ReadOnlySpan<byte> b, int scale)
    {
        var t = TimeLength(scale);
        return DateOnly.FromDayNumber(UInt24(b[t..])).ToDateTime(Time(b[..t], scale));
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
