using SqlLogExplorer.Decoding;
using SqlLogExplorer.Models;
using static SqlLogExplorer.Tests.Decoding.TableSchemaTests;

namespace SqlLogExplorer.Tests.Decoding;

public class RowDecoderTests
{
    // spec03 §2 : t(id int not null primary key, a int null, b varchar(20) null, c bit, d bit), ligne (1, 10, 'abc', 1, 0).
    private static readonly TableSchema T = new(1, "dbo", "t", 1, 1, new[]
    {
        Col(1, "id", 4, 1, 56, 4, keyOrdinal: 1), Col(2, "a", 8, 2, 56, 4), Col(3, "b", -1, 3, 167, 20) with { CodePage = 1252 },
        Col(4, "c", 12, 4, 104, 1, bitPos: 0), Col(5, "d", 12, 5, 104, 1, bitPos: 1),
    });

    private static byte[] Hex(string h) => Convert.FromHexString(h);
    private static object? V(DecodedRow r, string col) => r.Columns.Single(c => c.Name == col).Value;

    [Fact]
    public void SpecRow_DecodesEveryColumn_AndMasksUnusedBits()
    {
        var row = RowDecoder.Decode(Hex("30000D00010000000A000000F105000001001700616263"), T);   // 0xF1 : bits parasites

        Assert.Equal(0, row.RecordType);
        Assert.False(row.HasProblem);
        Assert.Equal(1, V(row, "id")); Assert.Equal(10, V(row, "a")); Assert.Equal("abc", V(row, "b"));
        Assert.Equal(true, V(row, "c")); Assert.Equal(false, V(row, "d"));
    }

    [Fact]
    public void NoVariableColumnPresent_RowEndsAfterBitmap()   // spec03 §2, t_novar (3, 30, NULL)
    {
        var s = new TableSchema(1, "dbo", "t_novar", 1, 1, new[] { Col(1, "id", 4, 1, 56, 4), Col(2, "n1", 8, 2, 56, 4), Col(3, "v1", -1, 3, 167, 10) });
        var row = RowDecoder.Decode(Hex("10000C00030000001E000000030004"), s);

        Assert.False(row.HasProblem);
        Assert.Equal(30, V(row, "n1")); Assert.Null(V(row, "v1"));
    }

    [Fact]
    public void NullFixedColumn_IgnoresStaleBytes()
    {
        var s = new TableSchema(1, "dbo", "x", 1, 1, new[] { Col(1, "id", 4, 1, 56, 4), Col(2, "n1", 8, 2, 56, 4) });
        var row = RowDecoder.Decode(Hex("10000C0004000000E0E61322020002"), s);   // bit 2 du bitmap = n1 NULL

        Assert.Null(V(row, "n1"));
    }

    [Fact]
    public void ColumnAddedAfterRow_TakesDefault_AndIsNotMalformed()
    {
        var s = new TableSchema(1, "dbo", "ac", 1, 1, new[]
        {
            Col(1, "id", 4, 1, 56, 4), Col(2, "a", 8, 2, 56, 4),
            Col(3, "n", 12, 3, 56, 4) with { HasDefault = true, DefaultValue = "7", IsNullable = false },
        });
        var row = RowDecoder.Decode(Hex("10000C00010000000A000000020000"), s);   // N = 2

        Assert.False(row.HasProblem);
        var n = row.Columns.Single(c => c.Name == "n");
        Assert.Equal("7", n.Value); Assert.Equal("added after the row was written", n.Note);
    }

    [Fact]
    public void OffRowPointer_IsAnnotatedNotDecoded()
    {
        var s = new TableSchema(1, "dbo", "o", 1, 1, new[] { Col(1, "id", 4, 1, 56, 4), Col(2, "m", -1, 2, 167, 8000) });
        var pointer = new string('0', 48);   // 24 octets de pointeur : données de 15 à 39, fin 0x27 | 0x8000
        var row = RowDecoder.Decode(Hex("30000800010000000200000100" + "2780" + pointer), s);

        Assert.False(row.HasProblem);
        Assert.Equal("off-row value not followed", row.Columns.Single(c => c.Name == "m").Note);
        Assert.Contains(row.Fields, f => f.Role == FieldRole.LobPointer);
    }

    [Fact]
    public void VersionTag_FollowsVariableData_TrailerAfterIt()
    {
        var tag = new string('A', 28);
        var row = RowDecoder.Decode(Hex("70000D00010000000A000000010500000100170061626300" [..46] + tag + "FFFF"), T);

        Assert.Contains(row.Fields, f => f.Role == FieldRole.VersionTag && f.Offset == 23 && f.Length == 14);
        Assert.Contains(row.Fields, f => f.Role == FieldRole.UnknownTrailer && f.Offset == 37 && f.Length == 2);
        Assert.Equal("abc", V(row, "b"));
    }

    [Theory]
    [InlineData("")] [InlineData("30")] [InlineData("300040")] [InlineData("30000D00010000000A000000010500000100FF00616263")]
    public void MalformedInput_NeverThrows(string hex)
    {
        var row = RowDecoder.Decode(Hex(hex), T);
        Assert.True(row.HasProblem);
    }

    [Fact]
    public void NullOrEmptyRow_IsMalformed_NotAnException()
    {
        Assert.True(RowDecoder.Decode(null, T).HasProblem);
        Assert.True(RowDecoder.Decode(Array.Empty<byte>(), T).HasProblem);
    }

    [Fact]
    public void NoVariableArea_ClearNullBit_IsEmptyNotNull()   // ligne (3, '') : pas de zone variable, bit NULL à 0
    {
        var s = new TableSchema(1, "dbo", "e", 1, 1, new[] { Col(1, "id", 4, 1, 56, 4), Col(2, "vc", -1, 2, 167, 10) with { CodePage = 1252 } });
        var row = RowDecoder.Decode(Hex("1000080003000000020000"), s);

        Assert.False(row.HasProblem);
        Assert.Equal("", V(row, "vc"));
    }

    [Fact]
    public void FixedEndDifferentFromSchema_IsMalformed()   // spec03 §8 : F = 14 alors que le schéma prévoit 13
    {
        var row = RowDecoder.Decode(Hex("30000E00010000000A0000000100" + "0500000100180061626300"[..20]), T);

        Assert.Contains(row.Fields, f => f.Role == FieldRole.Malformed && f.Problem!.Contains("schema predicts 13"));
    }

    [Fact]
    public void IndexRecord_IsShownRaw()   // spec03 §5 : type 3, aucune valeur décodée
    {
        var row = RowDecoder.Decode(Hex("360100000002000001001100616C696365"), T);

        Assert.Equal(3, row.RecordType);
        Assert.Empty(row.Columns);
        Assert.Equal("index record", row.RecordTypeName);
    }

    [Fact]
    public void WithoutSchema_StructureOnly()
    {
        var row = RowDecoder.Decode(Hex("30000D00010000000A000000010500000100170061626300"[..46]), null);

        Assert.Empty(row.Columns);
        Assert.Contains(row.Fields, f => f.Role == FieldRole.VariableOffset);
        Assert.Contains(row.Fields, f => f.Role == FieldRole.FixedColumn && f.Offset == 4 && f.Length == 9);
    }

    [Fact]
    public void SparseColumn_DoesNotThrow_IsFlagged_AndNeighboursStillDecode()   // ligne réelle de SQL Server 2022 : (1, 10, NULL, NULL, 'b1')
    {
        var s = new TableSchema(1, "dbo", "sp", 1, 1, new[]
        {
            Col(3, "s1", 0, 0, 56, 4), Col(4, "s2", 0, 0, 56, 4),
            Col(1, "id", 4, 1, 56, 4), Col(2, "a", 8, 2, 56, 4), Col(5, "v", -1, 3, 167, 5) with { CodePage = 1252 },
        });
        var row = RowDecoder.Decode(Hex("30000C00010000000A000000030000010015006231"), s);

        Assert.True(row.HasProblem);
        Assert.Equal(1, V(row, "id")); Assert.Equal(10, V(row, "a")); Assert.Equal("b1", V(row, "v"));
        Assert.All(new[] { "s1", "s2" }, n =>
        {
            Assert.Null(V(row, n));
            Assert.Equal("sparse column not decoded", row.Columns.Single(c => c.Name == n).Note);
        });
    }

    [Fact]
    public void CompressedTable_IsRejectedWholesale_NotDecodedAsWrongValues()   // ROW : (-8388096, NULL) s'écrit 010204000200000000
    {
        var s = new TableSchema(1, "dbo", "cs", 1, 1, new[] { Col(1, "id", -1, 1, 56, 4, keyOrdinal: 1), Col(2, "s", -2, 2, 167, 10) with { CodePage = 1252 } });
        var row = RowDecoder.Decode(Hex("010204000200000000"), s);

        Assert.True(row.HasProblem);
        Assert.Empty(row.Columns);
        Assert.Contains(row.Fields, f => f.Role == FieldRole.Malformed && f.Offset == 0 && f.Length == 9 && f.Problem!.Contains("compressed rows are not decoded"));
    }

    [Theory]   // lignes réelles de SQL Server 2022, table (a varchar(10), b varchar(10)) compressée ROW ou PAGE : ('abc','de') et (NULL,'x')
    [InlineData("010234616263646500")] [InlineData("010220786263646500")]
    public void CompressedRowOfVariableOnlyTable_IsRejected_WithOrWithoutSchema(string hex)
    {
        var s = new TableSchema(1, "dbo", "vr", 0, 1, new[]
        {
            Col(1, "a", -1, 1, 167, 10) with { CodePage = 1252 }, Col(2, "b", -2, 2, 167, 10) with { CodePage = 1252 },
        });

        foreach (var schema in new[] { s, null })
        {
            var row = RowDecoder.Decode(Hex(hex), schema);
            Assert.True(row.HasProblem);
            Assert.Empty(row.Columns);
            Assert.Contains(row.Fields, f => f.Role == FieldRole.Malformed && f.Offset == 0 && f.Length == 9 && f.Problem!.StartsWith("not a FixedVar row"));
        }
    }

    [Fact]
    public void VariableColumnsAndUniqueifier_WithNegativeOffsets_AreNotMistakenForCompression()
    {
        var s = new TableSchema(1, "dbo", "v", 1, 1, new[]
        {
            Col(1, "u", -1, 1, 56, 4, uniqueifier: true), Col(2, "v1", -2, 2, 167, 5) with { CodePage = 1252 }, Col(3, "x", -3, 3, 56, 4, dropped: true),
        });
        var row = RowDecoder.Decode(Hex("30000400030000" + "00" + "0300" + "0000" + "0000"), s);   // seule la décision de compression compte ici

        Assert.DoesNotContain(row.Fields, f => f.Problem?.Contains("compressed rows are not decoded") == true);
    }

    [Fact]
    public void CompressedTable_RowWhoseFirstByteHasBit0Clear_IsStillRejected()
    {
        // L'octet 0x10 passe la garde du bit 0 : seul le schéma (leaf_offset négatif d'un int) dit que la table est compressée.
        var s = new TableSchema(1, "dbo", "cs", 1, 1, new[] { Col(1, "id", -1, 1, 56, 4, keyOrdinal: 1), Col(2, "a", -2, 2, 56, 4) });
        var row = RowDecoder.Decode(Hex("10000C00010000000A000000020000"), s);

        Assert.Empty(row.Columns);
        Assert.Contains(row.Fields, f => f.Role == FieldRole.Malformed && f.Offset == 0 && f.Problem == "compressed rows are not decoded");
    }

    // ---- Gardes de la structure : chacune a son entrée (revue de mutation) ----

    [Fact]
    public void ColumnCountGreaterThanTheSchemas_IsMalformed()   // N = 6 pour les 5 colonnes de t, partie fixe juste
    {
        var row = RowDecoder.Decode(Hex("30000D00010000000A000000F106000001001700616263"), T);

        Assert.Contains(row.Fields, f => f.Role == FieldRole.Malformed && f.Offset == 13 && f.Problem == "column count greater than the schema's");
    }

    [Fact]
    public void VersioningBitWithFewerThanFourteenBytes_IsMalformed()   // bit 6 de A posé, 2 octets après les données variables
    {
        var row = RowDecoder.Decode(Hex("70000D00010000000A000000F105000001001700616263" + "AABB"), T);

        Assert.Contains(row.Fields, f => f.Role == FieldRole.Malformed && f.Offset == 23 && f.Length == 2 && f.Problem == "versioning tag beyond the row");
        Assert.DoesNotContain(row.Fields, f => f.Role == FieldRole.VersionTag);
    }

    [Fact]
    public void FixedEndBeforeAColumnsEnd_LeavesThatColumnUnread()   // F = 12 : c et d, à l'octet 12, sont au-delà
    {
        var row = RowDecoder.Decode(Hex("30000C00010000000A00000005000001001600616263"), T);

        Assert.Contains(row.Fields, f => f.Role == FieldRole.Malformed && f.Column == "c" && f.Problem == "fixed column beyond the fixed-data end offset");
        Assert.DoesNotContain(row.Columns, c => c.Name is "c" or "d");
        Assert.Equal(10, V(row, "a"));
    }

    [Fact]
    public void ColumnBytesTheTypeRefuses_GiveANote_NotAnException()   // b dans une page de code inconnue : TypeDecoder lève
    {
        var s = T with { Columns = T.Columns.Select(c => c.Name == "b" ? c with { CodePage = 12345 } : c).ToList() };
        var row = RowDecoder.Decode(Hex("30000D00010000000A000000F105000001001700616263"), s);

        var b = row.Columns.Single(c => c.Name == "b");
        Assert.Null(b.Value);
        Assert.Equal("3 bytes do not fit the column type", b.Note);
        Assert.Contains(row.Fields, f => f.Role == FieldRole.Malformed && f.Column == "b" && f.Offset == 20);
        Assert.Equal(10, V(row, "a"));
    }

    // ---- Fixtures : journaux réels de SQL Server 2017 à 2025 (spec03 §10.1) ----

    public static TheoryData<string> Versions => new() { "2017", "2019", "2022", "2025" };

    public static TheoryData<string, string> VersionsAndScenarios
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var v in FixtureSet.Versions)
                foreach (var s in new[] { "collations", "heap", "keys", "modify", "nulls", "schema_change", "split", "types", "versioned", "versioned_adr" })
                    if (!(v == "2017" && s == "versioned_adr")) data.Add(v, s);   // ADR apparaît avec SQL Server 2019
            return data;
        }
    }

    private static List<(LogRecord Record, DecodedRow Row)> Rows(string version, string scenario, string table, string operation = "LOP_INSERT_ROWS")
    {
        var (schema, records) = FixtureSet.Load(version, scenario).ForTable(table);
        return records.Where(r => r.Operation == operation).Select(r => (r, RowDecoder.Decode(r.RowLogContents0, schema))).ToList();
    }

    [Theory, MemberData(nameof(Versions))]
    public void Types_FirstRow_EveryColumnMatchesTheInsertedValue(string version)
    {
        var row = Rows(version, "types", "dbo.types")[0].Row;

        Assert.False(row.HasProblem);
        Assert.Equal(34, row.Columns.Count);   // id + 33 colonnes de types.sql
        Assert.Equal(1, V(row, "id"));
        foreach (var (column, expected) in TypeDecoderTests.FirstRow)
        {
            var actual = V(row, column) is SqlTemporal t ? t.Value : V(row, column);
            Assert.True(expected.Equals(actual) || (expected is byte[] e && actual is byte[] a && e.SequenceEqual(a)),
                $"{version} {column} : attendu {expected}, obtenu {actual}");
        }
        Assert.Equal(true, V(row, "b1")); Assert.Equal(false, V(row, "b2")); Assert.Equal(true, V(row, "b3"));
        Assert.Equal("élan", V(row, "vc")); Assert.Equal("日本 ", V(row, "nch")); Assert.Equal("Grüße", V(row, "nvc"));
        Assert.Equal(new byte[] { 1, 2 }, V(row, "vb"));
        var x = row.Columns.Single(c => c.Name == "x");
        Assert.Equal("type not decoded", x.Note);
        Assert.Equal(Hex("DFFF01B004F0016100EF000001F801F7")[..4], ((byte[])x.Value!)[..4]);   // les octets bruts du XML binaire
    }

    [Theory, MemberData(nameof(Versions))]
    public void Types_SecondRow_EveryColumnButIdIsNull_ThirdRowHasEmptyStrings(string version)
    {
        var rows = Rows(version, "types", "dbo.types");

        Assert.False(rows[1].Row.HasProblem);
        Assert.All(rows[1].Row.Columns.Where(c => c.Name != "id"), c => Assert.Null(c.Value));
        Assert.Equal(2, V(rows[1].Row, "id"));

        Assert.False(rows[2].Row.HasProblem);
        Assert.Equal("", V(rows[2].Row, "vc")); Assert.Equal("", V(rows[2].Row, "nvc"));
        Assert.Null(V(rows[2].Row, "vb")); Assert.Null(V(rows[2].Row, "ch"));
    }

    [Theory, MemberData(nameof(Versions))]
    public void Nulls_FiveRows_MatchTheInsertedValues(string version)
    {
        var rows = Rows(version, "nulls", "dbo.nulls").Select(r => r.Row).ToList();
        var expected = new (int Id, int? N1, string? V1, string? V2, string? V3)[]
        {
            (1, 30, null, null, null), (2, null, "V1", null, "V3"), (3, null, "V1", "", "V3"), (4, 7, "V1", null, null), (5, null, null, null, null),
        };

        Assert.Equal(5, rows.Count);
        for (var i = 0; i < 5; i++)
        {
            Assert.False(rows[i].HasProblem);
            Assert.Equal(expected[i].Id, V(rows[i], "id")); Assert.Equal(expected[i].N1, V(rows[i], "n1"));
            Assert.Equal(expected[i].V1, V(rows[i], "v1")); Assert.Equal(expected[i].V2, V(rows[i], "v2")); Assert.Equal(expected[i].V3, V(rows[i], "v3"));
        }
    }

    [Theory, MemberData(nameof(Versions))]
    public void SchemaChange_AddedColumnTakesItsDefault_DroppedColumnYieldsNothing(string version)
    {
        var delete = Rows(version, "schema_change", "dbo.ac", "LOP_DELETE_ROWS").Single().Row;
        var n = delete.Columns.Single(c => c.Name == "n");
        Assert.False(delete.HasProblem);
        Assert.Equal(1, V(delete, "id")); Assert.Equal(10, V(delete, "a"));
        Assert.Equal("7", n.Value); Assert.Equal("added after the row was written", n.Note);

        var dc = Rows(version, "schema_change", "dbo.dc").Single().Row;
        Assert.False(dc.HasProblem);
        Assert.Equal(new[] { "id", "y" }, dc.Columns.Select(c => c.Name));
        Assert.Equal(5, V(dc, "y"));
        Assert.Contains(dc.Fields, f => f.Role == FieldRole.DroppedColumn);
    }

    [Theory, MemberData(nameof(Versions))]
    public void Keys_NonclusteredIndexDelete_YieldsNoColumn(string version)
    {
        var fixture = FixtureSet.Load(version, "keys");
        var schema = fixture.Schemas.Values.Single(s => s.ObjectName == "dbo.ix" && s.IndexId == 2 && fixture.Log.Any(r => r.AllocUnitId == s.AllocUnitId));
        var record = fixture.Log.Single(r => r.AllocUnitId == schema.AllocUnitId && r.Operation == "LOP_DELETE_ROWS" && r.Context is "LCX_HEAP" or "LCX_CLUSTERED" or "LCX_MARK_AS_GHOST");

        foreach (var s in new[] { schema, null })
        {
            var row = RowDecoder.Decode(record.RowLogContents0, s);
            Assert.Equal(3, row.RecordType);
            Assert.Empty(row.Columns);
        }
        Assert.False(schema.IsDataRows);
    }

    [Theory, MemberData(nameof(Versions))]
    public void Keys_FirstRowOfNk_HasAZeroLengthUniqueifier(string version)
    {
        var row = Rows(version, "keys", "dbo.nk")[0].Row;

        Assert.False(row.HasProblem);
        Assert.Equal(0, V(row, "uniqueifier")); Assert.Equal("x", V(row, "k1")); Assert.Null(V(row, "k2")); Assert.Equal(1, V(row, "v"));
    }

    [Theory, MemberData(nameof(Versions))]
    public void Keys_KeyOrderDiffersFromStorageOrder_ColumnsFollowTheirNullBits(string version)
    {
        var ko = Rows(version, "keys", "dbo.ko").Select(r => r.Row).ToList();
        Assert.Equal(new[] { 20, 10 }, ko.Select(r => (int)V(r, "k2")!));
        Assert.Equal(new[] { 1, 2 }, ko.Select(r => (int)V(r, "k1")!));
        Assert.Equal(new[] { 1, 2 }, Rows(version, "keys", "dbo.kd").Select(r => (int)V(r.Row, "k")!));
    }

    [Theory, MemberData(nameof(Versions))]
    public void Collations_CodePagesDecodeTheirText(string version)
    {
        var row = Rows(version, "collations", "dbo.co").Single().Row;
        Assert.False(row.HasProblem);
        Assert.Equal("αβγ", V(row, "gr")); Assert.Equal("日本語", V(row, "ja"));

        if (version == "2017") return;   // les collations UTF-8 apparaissent avec SQL Server 2019
        Assert.Equal("Grüße 日本", V(Rows(version, "collations", "dbo.u8").Single().Row, "s"));
    }

    [Theory]
    [InlineData("2017", "versioned")] [InlineData("2019", "versioned")] [InlineData("2022", "versioned")] [InlineData("2025", "versioned")]
    [InlineData("2019", "versioned_adr")] [InlineData("2022", "versioned_adr")] [InlineData("2025", "versioned_adr")]
    public void Versioned_InsertCarriesAVersionTag(string version, string scenario)
    {
        var row = Rows(version, scenario, "dbo.vr")[0].Row;

        Assert.False(row.HasProblem);
        Assert.Equal(10, V(row, "a")); Assert.Equal("abc", V(row, "b"));
        Assert.Contains(row.Fields, f => f.Role == FieldRole.VersionTag && f.Length == 14);
    }

    [Theory, MemberData(nameof(Versions))]
    public void Split_OverflowRow_KeepsV1InRowAndAnnotatesTheTwoPointers(string version)
    {
        var row = Rows(version, "split", "dbo.ov").Single().Row;

        Assert.False(row.HasProblem);
        Assert.Equal(new string('c', 5000), V(row, "v1"));
        foreach (var name in new[] { "v2", "m" })
        {
            var column = row.Columns.Single(c => c.Name == name);
            Assert.Null(column.Value); Assert.Equal("off-row value not followed", column.Note);
        }
        // Mesuré sur les quatre versions : v2 est un pointeur de dépassement de ligne (24 octets), m un pointeur LOB (36 octets).
        Assert.Equal(24, row.Fields.Single(f => f.Role == FieldRole.LobPointer && f.Column == "v2").Length);
        Assert.Equal(36, row.Fields.Single(f => f.Role == FieldRole.LobPointer && f.Column == "m").Length);
    }

    [Theory, MemberData(nameof(VersionsAndScenarios))]
    public void Sweep_EveryDataRecordOfEveryFixture_DecodesWithoutProblem(string version, string scenario)
    {
        var fixture = FixtureSet.Load(version, scenario);
        var decoded = 0;
        foreach (var record in fixture.Log)
        {
            if (record.Operation is not ("LOP_INSERT_ROWS" or "LOP_DELETE_ROWS")) continue;
            if (record.Context is not ("LCX_HEAP" or "LCX_CLUSTERED" or "LCX_MARK_AS_GHOST")) continue;
            if (record.AllocUnitId is not { } id || !fixture.Schemas.TryGetValue(id, out var schema) || !schema.IsDataRows) continue;

            var row = RowDecoder.Decode(record.RowLogContents0, schema);
            decoded++;
            var compensation = record.RowLogContents0 is null or { Length: 0 };   // le délestage d'une transaction annulée n'a pas d'image
            Assert.True(compensation == row.HasProblem, $"{version} {scenario} {record.Lsn} {schema.ObjectName} : {string.Join("; ", row.Fields.Select(f => f.Problem).Where(p => p is not null))}");
            if (compensation) continue;

            // Les champs se suivent sans trou ni chevauchement et couvrent toute la ligne.
            // Seules les colonnes bit partagent un octet.
            var next = 0;
            DecodedField? previous = null;
            foreach (var f in row.Fields)
            {
                var sharesBitByte = previous is { Length: 1 } && f.Length == 1 && f.Offset == previous.Offset && schema.Columns.Any(c => c.IsBit && c.Name == f.Column);
                if (!sharesBitByte)
                {
                    Assert.True(f.Offset == next, $"{version} {scenario} {record.Lsn} {schema.ObjectName} : champ {f.Role} en {f.Offset}, attendu {next}");
                    next += f.Length;
                }
                previous = f;
            }
            Assert.Equal(record.RowLogContents0!.Length, next);
        }
        Assert.True(decoded > 0, $"{version} {scenario} : aucun enregistrement de données");
    }

    [Theory, MemberData(nameof(Versions))]
    public void PageDump_FieldsOfDboU_MatchTheDecodedOffsetsAndLengths(string version)
    {
        var fixture = FixtureSet.Load(version, "modify");
        var (schema, records) = fixture.ForTable("dbo.u");
        var pageId = records.First(r => r.Operation == "LOP_INSERT_ROWS").PageId;

        // Les octets de la ligne, depuis le vidage mémoire (DBCC PAGE style 1) : « 0000000000000014:   20202020 … ».
        var dump = fixture.Pages.Where(p => p.Style == 1 && p.PageId == pageId && p.ParentObject.StartsWith("Slot 0,") && p.Object.StartsWith("Memory Dump"));
        var length = int.Parse(System.Text.RegularExpressions.Regex.Match(dump.First().ParentObject, @"Length (\d+)").Groups[1].Value);
        var bytes = Hex(string.Concat(dump.Select(p => p.Value.Substring(20, 44).Replace(" ", ""))))[..length];
        var row = RowDecoder.Decode(bytes, schema);
        Assert.False(row.HasProblem);

        var columns = fixture.Pages.Where(p => p.Style == 3 && p.PageId == pageId && p.ParentObject.StartsWith("Slot 0 Offset") && p.Object.StartsWith("Slot 0 Column")).ToList();
        Assert.Equal(6, columns.Count);
        foreach (var line in columns)
        {
            var m = System.Text.RegularExpressions.Regex.Match(line.Object, @"Offset 0x([0-9a-f]+) Length (\d+)");
            var field = row.Fields.Single(f => f.Column == line.Field);
            Assert.True(field.Offset == Convert.ToInt32(m.Groups[1].Value, 16) && field.Length == int.Parse(m.Groups[2].Value),
                $"{version} {line.Field} : DBCC PAGE {line.Object}, décodé {field.Offset}+{field.Length}");
        }
    }
}
