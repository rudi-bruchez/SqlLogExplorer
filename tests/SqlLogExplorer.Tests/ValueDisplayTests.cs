using System.Buffers.Binary;
using SqlLogExplorer.Decoding;
using SqlLogExplorer.Services;
using SqlLogExplorer.ViewModels;
using static SqlLogExplorer.Tests.Decoding.TableSchemaTests;

namespace SqlLogExplorer.Tests;

/// <summary>Texte des valeurs décodées et détection des changements (spec03 §9) : le texte affiché est celui qui sert à comparer.</summary>
public class ValueDisplayTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b);
    private static string Text(SchemaColumn c, string hex) => InspectorViewModel.ValueText(TypeDecoder.Decode(c, Convert.FromHexString(hex)));

    private static SchemaColumn Temporal(int type, int length, int scale) => Col(1, "c", 4, 1, type, length) with { Scale = scale, Precision = scale };

    // time(7) : 5 octets d'unités de 100 ns ; datetime2(7) : l'heure puis 3 octets de jours depuis l'an 1 ;
    // datetimeoffset(7) : heure et date UTC, puis le décalage en minutes ; datetime : 1/300 s puis jours depuis 1900.
    private static string Time7(long ticks) => Hex(BitConverter.GetBytes(ticks))[..10];
    private static string Day(DateOnly d) => Hex(BitConverter.GetBytes(d.DayNumber))[..6];
    private static string Offset(short minutes) => Hex(BitConverter.GetBytes(minutes));
    private static string Datetime(DateOnly d, int ticks300) =>
        Hex(BitConverter.GetBytes(ticks300)) + Hex(BitConverter.GetBytes(d.DayNumber - new DateOnly(1900, 1, 1).DayNumber));

    private static readonly DateOnly Day0 = new(2026, 10, 15);
    private static readonly long Ten = new TimeOnly(10, 0).Ticks;

    [Fact]
    public void TemporalValues_ShowTheirScaleInIsoOrder()
    {
        Assert.Equal("2026-10-15", Text(Temporal(40, 3, 0), Day(Day0)));
        Assert.Equal("13:14:15", Text(Temporal(41, 3, 0), "27BA00"));
        Assert.Equal("10:00:30.7654321", Text(Temporal(41, 5, 7), Time7(Ten + 307654321)));
        Assert.Equal("2026-10-15 10:00:00.1000000", Text(Temporal(42, 8, 7), Time7(Ten + 1000000) + Day(Day0)));
        Assert.Equal("2026-10-15 10:00:00.100", Text(Temporal(42, 7, 3), Hex(BitConverter.GetBytes(36000100))[..8] + Day(Day0)));
        Assert.Equal("2026-10-15 12:00:00.1000000 +02:00", Text(Temporal(43, 10, 7), Time7(Ten + 1000000) + Day(Day0) + Offset(120)));
        Assert.Equal("2026-10-15 13:14:00", Text(Temporal(58, 4, 0), "1A03" + Hex(BitConverter.GetBytes((ushort)(Day0.DayNumber - new DateOnly(1900, 1, 1).DayNumber)))));
    }

    // datetime : SQL Server affiche les 1/300 s arrondis à la milliseconde, .000, .003 ou .007.
    [Theory]
    [InlineData(299, "2026-10-15 10:00:00.997")]
    [InlineData(1, "2026-10-15 10:00:00.003")]
    [InlineData(2, "2026-10-15 10:00:00.007")]
    [InlineData(0, "2026-10-15 10:00:00.000")]
    public void Datetime_ShowsMillisecondsAsSqlServerDoes(int extra, string expected) =>
        Assert.Equal(expected, Text(Temporal(61, 8, 0), Datetime(Day0, 36000 * 300 + extra)));

    // id int, t7 time(7), d2 datetime2(7), dto datetimeoffset(7), dt datetime : une ligne FixedVar sans colonne variable.
    private static readonly TableSchema Times = new(1, "dbo", "times", 1, 1, new[]
    {
        Col(1, "id", 4, 1, 56, 4, keyOrdinal: 1), Col(2, "t7", 8, 2, 41, 5) with { Scale = 7 }, Col(3, "d2", 13, 3, 42, 8) with { Scale = 7 },
        Col(4, "dto", 21, 4, 43, 10) with { Scale = 7 }, Col(5, "dt", 31, 5, 61, 8),
    });

    private static DecodedRow Row(long fraction, int ticks300) => RowDecoder.Decode(Convert.FromHexString(
        "1000" + "2700" + "01000000" + Time7(Ten + fraction) + Time7(Ten + fraction) + Day(Day0)
        + Time7(Ten + fraction) + Day(Day0) + Offset(0) + Datetime(Day0, 36000 * 300 + ticks300) + "0500" + "00"), Times);

    [Fact]
    public void AChangeBelowTheSecond_IsAChange_WithDistinctTexts()
    {
        var before = Row(1000000, 0);
        var after = Row(2000000, 1);   // 0,1 s de plus ; 3 ms de plus pour le datetime
        Assert.False(before.HasProblem);

        var changes = DecodingService.Compare(before, after);

        Assert.Equal(["t7", "d2", "dto", "dt"], changes.Where(c => c.Changed).Select(c => c.Column));
        Assert.All(changes.Where(c => c.Column != "id"), c => Assert.NotEqual(InspectorViewModel.ValueText(c.Before), InspectorViewModel.ValueText(c.After)));
    }
}
