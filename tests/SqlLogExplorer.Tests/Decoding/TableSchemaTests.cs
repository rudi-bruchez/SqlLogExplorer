using SqlLogExplorer.Decoding;

namespace SqlLogExplorer.Tests.Decoding;

public class TableSchemaTests
{
    internal static SchemaColumn Col(int id, string name, int leafOffset, int nullBit, int systemType, int maxLen,
        int? keyOrdinal = null, bool dropped = false, bool uniqueifier = false, int bitPos = 0)
        => new(id, name, leafOffset, nullBit, bitPos, maxLen, systemType, 0, 0, null, true, dropped, uniqueifier, false, null, keyOrdinal);

    [Fact]
    public void KeyColumns_AreOrderedByKeyOrdinalAndExcludeDroppedAndUniqueifier()
    {
        var schema = new TableSchema(1, "dbo", "k", 1, 1, new[]
        {
            Col(1, "k1", -1, 1, 167, 10, keyOrdinal: 2),
            Col(2, "k2", 4, 2, 56, 4, keyOrdinal: 1),
            Col(67108865, "dropped#67108865", 8, 3, 56, 4, dropped: true),
            Col(3, "uniqueifier", -2, 4, 56, 4, uniqueifier: true),
        });

        Assert.Equal(new[] { "k2", "k1" }, schema.KeyColumns.Select(c => c.Name));
        Assert.Equal("uniqueifier", schema.Uniqueifier?.Name);
    }

    [Theory]
    [InlineData(1, 0, true)] [InlineData(1, 1, true)] [InlineData(1, 2, false)] [InlineData(3, 1, false)]
    public void IsDataRows_OnlyInRowDataOfHeapOrClusteredIndex(int auType, int indexId, bool expected)
        => Assert.Equal(expected, new TableSchema(1, "dbo", "t", indexId, auType, Array.Empty<SchemaColumn>()).IsDataRows);
}
