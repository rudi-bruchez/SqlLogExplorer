using System.Data.SqlTypes;
using SqlLogExplorer.Decoding;
using static SqlLogExplorer.Tests.Decoding.TableSchemaTests;

namespace SqlLogExplorer.Tests.Decoding;

public class TypeDecoderTests
{
    // Une valeur temporelle est rendue avec son texte (SqlTemporal) : ces tests en vérifient la valeur .NET.
    private static object? D(int type, string hex, int precision = 0, int scale = 0, int? codePage = null)
        => Unwrap(TypeDecoder.Decode(Col(1, "c", 4, 1, type, hex.Length / 2) with { Precision = precision, Scale = scale, CodePage = codePage },
                              Convert.FromHexString(hex)));

    private static object? Unwrap(object? value) => value is SqlTemporal t ? t.Value : value;

    [Fact] public void Int() => Assert.Equal(10, D(56, "0A000000"));
    [Fact] public void NegativeSmallint() => Assert.Equal((short)-12345, D(52, "C7CF"));
    [Fact] public void Tinyint() => Assert.Equal((byte)200, D(48, "C8"));
    [Fact] public void Bigint() => Assert.Equal(1234567890123L, D(127, "CB04FB711F010000"));
    [Fact] public void Real() => Assert.Equal(3.5f, D(59, "00006040"));
    [Fact] public void Float() => Assert.Equal(-2.25, D(62, "00000000000002C0"));
    [Fact] public void Money() => Assert.Equal(922337203685477.5807m, D(60, "FFFFFFFFFFFFFF7F"));
    [Fact] public void Smallmoney() => Assert.Equal(-214748.3648m, D(122, "00000080"));
    [Fact] public void Decimal9() => Assert.Equal(SqlDecimal.Parse("1234567.89"), D(106, "01" + "15CD5B07", 9, 2));
    [Fact] public void NegativeNumeric() => Assert.Equal(SqlDecimal.Parse("-1234.5"), D(108, "00" + "39300000", 5, 1));
    [Fact] public void Date() => Assert.Equal(new DateOnly(2026, 10, 15), D(40, DateHex(new DateOnly(2026, 10, 15))));
    [Fact] public void Time0() => Assert.Equal(new TimeOnly(13, 14, 15), D(41, "27BA00", 0, 0));
    [Fact] public void Time7() => Assert.Equal(new TimeOnly(13, 14, 15).Add(TimeSpan.FromTicks(1234567)),
        D(41, Ticks5Hex(new TimeOnly(13, 14, 15).Ticks + 1234567), 16, 7));
    // datetime : 4 octets de 1/300 s (14296537 = 13:14:15 + 37/300 s), puis 4 octets de jours depuis 1900 ; .123 est stocké .1233333.
    [Fact] public void Datetime() => Assert.Equal(new DateTime(2026, 10, 15, 13, 14, 15).AddTicks((long)Math.Round(37 * (TimeSpan.TicksPerSecond / 300.0))),
        D(61, "D925DA00" + DaysHex1900(new DateTime(2026, 10, 15))));
    [Fact] public void Smalldatetime() => Assert.Equal(new DateTime(2026, 10, 15, 13, 14, 0),
        D(58, "1A03" + Days16Hex1900(new DateTime(2026, 10, 15))));
    [Fact] public void Uniqueidentifier() => Assert.Equal(Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF"),
        D(36, Convert.ToHexString(Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF").ToByteArray())));
    [Fact] public void VarcharLatin1() => Assert.Equal("élan", D(167, "E96C616E", codePage: 1252));
    [Fact] public void Nvarchar() => Assert.Equal("Grüße", D(231, Convert.ToHexString(System.Text.Encoding.Unicode.GetBytes("Grüße"))));
    [Fact] public void VarcharUtf8() => Assert.Equal("Grüße", D(167, Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("Grüße")), codePage: 65001));
    [Fact] public void Varbinary() => Assert.Equal(new byte[] { 1, 2 }, D(165, "0102"));
    [Fact] public void Xml_IsNotSupported()
    {
        Assert.False(TypeDecoder.IsSupported(241));
        Assert.Equal(new byte[] { 0xAB }, D(241, "AB"));
    }

    [Theory]
    [InlineData(48, 0, 0, 0)] [InlineData(48, 0, 0, 2)]
    [InlineData(52, 0, 0, 1)] [InlineData(52, 0, 0, 3)]
    [InlineData(56, 0, 0, 3)] [InlineData(56, 0, 0, 8)]
    [InlineData(127, 0, 0, 7)] [InlineData(127, 0, 0, 4)]
    [InlineData(104, 0, 0, 0)] [InlineData(104, 0, 0, 2)]
    [InlineData(59, 0, 0, 8)] [InlineData(62, 0, 0, 4)]
    [InlineData(60, 0, 0, 4)] [InlineData(122, 0, 0, 8)]
    [InlineData(40, 0, 0, 4)] [InlineData(40, 0, 0, 2)]
    [InlineData(61, 0, 0, 4)] [InlineData(58, 0, 0, 8)]
    [InlineData(36, 0, 0, 15)]
    [InlineData(106, 9, 2, 0)] [InlineData(106, 9, 2, 3)] [InlineData(108, 19, 4, 5)] [InlineData(108, 38, 10, 13)]
    [InlineData(41, 0, 0, 0)] [InlineData(41, 0, 0, 4)] [InlineData(41, 7, 7, 4)]
    [InlineData(42, 0, 0, 5)] [InlineData(42, 7, 7, 7)]
    [InlineData(43, 0, 0, 6)] [InlineData(43, 7, 7, 9)]
    public void WrongLength_ThrowsArgumentException(int type, int precision, int scale, int length)
    {
        var column = Col(1, "c", 4, 1, type, length) with { Precision = precision, Scale = scale };
        var e = Assert.ThrowsAny<ArgumentException>(() => TypeDecoder.Decode(column, new byte[length]));
        Assert.Contains("expected", e.Message);
    }

    // Longueurs bonnes, valeurs impossibles : toujours ArgumentException, jamais Overflow ou NotSupported.
    [Fact] public void DateAfterYear9999_Throws() => Assert.ThrowsAny<ArgumentException>(() => D(40, "DBB937")); // 3652059
    [Fact] public void TimeOf24Hours_Throws() => Assert.ThrowsAny<ArgumentException>(() => D(41, "805101", 0, 0)); // 86400 s
    [Fact] public void Datetime2AfterYear9999_Throws() => Assert.ThrowsAny<ArgumentException>(() => D(42, "000000" + "DBB937", 0, 0));
    [Fact] public void DecimalMagnitudeAbove38Digits_Throws() => Assert.ThrowsAny<ArgumentException>(() => D(108, "01" + string.Concat(Enumerable.Repeat("FFFFFFFF", 4)), 38, 0));
    [Fact] public void UnknownCodePage_Throws() => Assert.ThrowsAny<ArgumentException>(() => D(167, "41", codePage: 12345));

    private static string DateHex(DateOnly d) => Convert.ToHexString(BitConverter.GetBytes(d.DayNumber))[..6];
    private static string Ticks5Hex(long ticks) => Convert.ToHexString(BitConverter.GetBytes(ticks))[..10];
    private static string DaysHex1900(DateTime d) => Convert.ToHexString(BitConverter.GetBytes((int)(d.Date - new DateTime(1900, 1, 1)).TotalDays));
    private static string Days16Hex1900(DateTime d) => Convert.ToHexString(BitConverter.GetBytes((ushort)(d.Date - new DateTime(1900, 1, 1)).TotalDays));

    // Valeurs de la première ligne de dbo.types (tests/fixtures/scenarios/types.sql).
    internal static readonly (string Column, object Expected)[] FirstRow =
    [
        ("ti", (byte)200), ("si", (short)-12345), ("bi", 1234567890123L),
        ("d9", SqlDecimal.Parse("1234567.89")), ("d19", SqlDecimal.Parse("123456789012345.6789")),
        ("d28", SqlDecimal.Parse("1234567890123456789012.123456")), ("d38", SqlDecimal.Parse("1234567890123456789012345678.0123456789")),
        ("nu", SqlDecimal.Parse("-1234.5")), ("mo", 922337203685477.5807m), ("sm", -214748.3648m), ("re", 3.5f), ("fl", -2.25),
        ("dt", new DateOnly(2026, 10, 15)),
        ("t0", new TimeOnly(13, 14, 15)), ("t3", new TimeOnly(13, 14, 15, 123)), ("t7", new TimeOnly(13, 14, 15).Add(TimeSpan.FromTicks(1234567))),
        ("dtm", new DateTime(2026, 10, 15, 13, 14, 15).AddTicks((long)Math.Round(37 * (TimeSpan.TicksPerSecond / 300.0)))),
        ("sdt", new DateTime(2026, 10, 15, 13, 14, 0)),
        ("d2_0", new DateTime(2026, 10, 15, 13, 14, 15)), ("d2_3", new DateTime(2026, 10, 15, 13, 14, 15, 123)),
        ("d2_7", new DateTime(2026, 10, 15, 13, 14, 15).AddTicks(1234567)),
        ("dto", new DateTimeOffset(new DateTime(2026, 10, 15, 13, 14, 15).AddTicks(1234567), TimeSpan.FromHours(2))),
        ("g", Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF")), ("ch", "ab   "), ("bn", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
    ];

    [Theory]
    [InlineData("2017")] [InlineData("2019")] [InlineData("2022")] [InlineData("2025")]
    public void FixedColumns_OfTheFirstInsert_MatchTheInsertedValues(string version)
    {
        var (schema, records) = FixtureSet.Load(version, "types").ForTable("dbo.types");
        var row = records.First(r => r.Operation == "LOP_INSERT_ROWS").RowLogContents0!;
        foreach (var (column, expected) in FirstRow)
        {
            var c = schema.Columns.Single(x => x.Name == column);
            var actual = Unwrap(TypeDecoder.Decode(c, row.AsSpan(c.LeafOffset, c.MaxInrowLength)));
            Assert.True(expected.Equals(actual) || (expected is byte[] e && actual is byte[] a && e.SequenceEqual(a)),
                $"{version} {column} : attendu {expected}, obtenu {actual}");
        }
    }
}
