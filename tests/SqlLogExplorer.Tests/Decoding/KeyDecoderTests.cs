using SqlLogExplorer.Decoding;
using static SqlLogExplorer.Tests.Decoding.TableSchemaTests;

namespace SqlLogExplorer.Tests.Decoding;

public class KeyDecoderTests
{
    private static byte[] Hex(string h) => Convert.FromHexString(h.Replace(" ", ""));

    private static TableSchema Schema(string name, params SchemaColumn[] columns) => new(1, "dbo", name, 1, 1, columns);

    // spec03 §2 : s(id int primary key, a int).
    private static readonly TableSchema S = Schema("s", Col(1, "id", 4, 1, 56, 4, keyOrdinal: 1), Col(2, "a", 8, 2, 56, 4));

    // k(k1 varchar(10), k2 int, v int), clé (k1, k2).
    private static readonly TableSchema K = Schema("k",
        Col(1, "k1", -1, 1, 167, 10, keyOrdinal: 1), Col(2, "k2", 4, 2, 56, 4, keyOrdinal: 2), Col(3, "v", 8, 3, 56, 4));

    // c(k1 varchar(10), k2 varchar(10), v int), clé (k1, k2).
    private static readonly TableSchema C = Schema("c",
        Col(1, "k1", -1, 1, 167, 10, keyOrdinal: 1), Col(2, "k2", -2, 2, 167, 10, keyOrdinal: 2), Col(3, "v", 4, 3, 56, 4));

    // n(id int, v int), index clustered non unique sur id : uniqueifier variable en tête.
    private static readonly TableSchema N = Schema("n",
        Col(1, "uniqueifier", -1, 1, 56, 4, uniqueifier: true), Col(2, "id", 4, 2, 56, 4, keyOrdinal: 1), Col(3, "v", 8, 3, 56, 4));

    // nk(k1 varchar(5), k2 int NULL, v int), clé non unique (k1, k2).
    private static readonly TableSchema NK = Schema("nk",
        Col(1, "uniqueifier", -1, 1, 56, 4, uniqueifier: true), Col(2, "k1", -2, 2, 167, 5, keyOrdinal: 1),
        Col(3, "k2", 4, 3, 56, 4, keyOrdinal: 2), Col(4, "v", 8, 4, 56, 4));

    // bk(id int, flag bit, c bit, d bit, v int), clé (id, flag) : flag est le bit 0 de l'octet à 8 dans la ligne.
    private static readonly TableSchema BK = Schema("bk",
        Col(1, "id", 4, 1, 56, 4, keyOrdinal: 1), Col(2, "flag", 8, 2, 104, 1, keyOrdinal: 2, bitPos: 0),
        Col(3, "c", 8, 3, 104, 1, bitPos: 1), Col(4, "d", 8, 4, 104, 1, bitPos: 2), Col(5, "v", 9, 5, 56, 4));

    // ---- Cas de spec03 §2 ----

    [Fact]
    public void Int_InsertRowAndModifyKey_GiveTheSameKey()
    {
        Assert.Equal("V01000000;", KeyDecoder.FromRow(Hex("10000C00010000000A000000020000"), S));
        Assert.Equal("V01000000;", KeyDecoder.FromIndexRecord(Hex("1601000000010000"), S));
    }

    [Fact]
    public void FixedKeyColumnComesFirstInTheIndexRecord_ButKeyOrderIsKept()
    {
        Assert.Equal("V616263;V07000000;", KeyDecoder.FromRow(Hex("30000C00070000000100000003000001001600616263"), K));
        Assert.Equal("V616263;V07000000;", KeyDecoder.FromIndexRecord(Hex("360700000002000001000F00616263"), K));
    }

    [Fact]
    public void ConcatenatedVariableKeys_StayDistinct()   // ('A','BCD') contre ('AB','CD')
    {
        var a = KeyDecoder.FromIndexRecord(Hex("3602000002000B000E0041424344"), C);
        var b = KeyDecoder.FromIndexRecord(Hex("3602000002000C000E0041424344"), C);

        Assert.Equal("V41;V424344;", a);
        Assert.Equal("V4142;V4344;", b);
        Assert.Equal(a, KeyDecoder.FromRow(Hex("300008000100000003000002001200150041424344"), C));
        Assert.Equal(b, KeyDecoder.FromRow(Hex("300008000200000003000002001300150041424344"), C));
    }

    [Fact]
    public void Uniqueifier_AlwaysFourBytesInTheIndex_ZeroOrAbsentInTheFirstDataRow()
    {
        Assert.Equal("V01000000;U0", KeyDecoder.FromIndexRecord(Hex("36010000000200000100100000000000"), N));
        Assert.Equal("V01000000;U1", KeyDecoder.FromIndexRecord(Hex("36010000000200000100100001000000"), N));
        Assert.Equal("V01000000;U0", KeyDecoder.FromRow(Hex("10000C000100000001000000030000"), N));   // pas de zone variable
        Assert.Equal("V01000000;U1", KeyDecoder.FromRow(Hex("30000C0001000000020000000300000100170001000000"), N));
    }

    // ---- Cas de docs/research/rowlog-layouts.md ----

    [Fact]
    public void NullKeyColumn_IgnoresStaleBytes_AndDiffersFromZero()
    {
        // nk : ('x', NULL) dont la ligne garde 0F000000 dans k2, et ('x', 0).
        var nullRow = KeyDecoder.FromRow(Hex("30000C000F0000000100000004000402001500160078"), NK);
        var zeroRow = KeyDecoder.FromRow(Hex("30000C00000000000200000004000002001500160078"), NK);
        var nullKey = KeyDecoder.FromIndexRecord(Hex("36 00000000 0300 02 0200 0F00 1300 78 00000000"), NK);

        Assert.Equal("V78;N;U0", nullRow);
        Assert.Equal("V78;V00000000;U0", zeroRow);
        Assert.Equal(nullRow, nullKey);
        Assert.Equal(zeroRow, KeyDecoder.FromIndexRecord(Hex("36 00000000 0300 00 0200 0F00 1300 78 00000000"), NK));
    }

    [Fact]
    public void BitKey_UsesItsOwnBit_WhateverTheOtherBitsOfTheByte()
    {
        // Ligne : 0x09 = flag 1 (bit 0) et un bit étranger. Index : 01, 07 et C7 mesurés pour flag 1 ; C6 pour flag 0.
        Assert.Equal("V01000000;V01;", KeyDecoder.FromRow(Hex("10000D00 01000000 09 01000000 0500 00"), BK));
        foreach (var b in new[] { "01", "07", "C7" })
            Assert.Equal("V01000000;V01;", KeyDecoder.FromIndexRecord(Hex("16 01000000 " + b + " 0200 00"), BK));
        Assert.Equal("V01000000;V00;", KeyDecoder.FromIndexRecord(Hex("16 01000000 C6 0200 00"), BK));
    }

    [Fact]
    public void TwoBitKeys_ShareOneByte_InKeyOrder()
    {
        var kb = Schema("kb", Col(1, "id", 4, 1, 56, 4, keyOrdinal: 1), Col(2, "g", 8, 2, 104, 1, keyOrdinal: 2, bitPos: 0), Col(3, "flag", 8, 3, 104, 1, keyOrdinal: 3, bitPos: 1));
        Assert.Equal("V01000000;V01;V00;", KeyDecoder.FromIndexRecord(Hex("16 01000000 39 0300 00"), kb));   // 0x39 : bits 0 seulement
        Assert.Equal("V02000000;V00;V01;", KeyDecoder.FromIndexRecord(Hex("16 02000000 02 0300 00"), kb));
        Assert.Equal("V03000000;V01;V01;", KeyDecoder.FromIndexRecord(Hex("16 03000000 03 0300 00"), kb));

        // (b1 bit, id int, b2 bit) : l'octet des deux bits est au premier bit, id après lui.
        var kbb = Schema("kbb", Col(1, "b1", 8, 1, 104, 1, keyOrdinal: 1, bitPos: 0), Col(2, "id", 4, 2, 56, 4, keyOrdinal: 2), Col(3, "b2", 8, 3, 104, 1, keyOrdinal: 3, bitPos: 1));
        Assert.Equal("V00;V02000000;V01;", KeyDecoder.FromIndexRecord(Hex("16 02 02000000 0300 00"), kbb));

        // PRIMARY KEY (b2, b1) : b2 est le bit 0 du groupe.
        var kbr = Schema("kbr", Col(1, "b1", 8, 1, 104, 1, keyOrdinal: 2, bitPos: 0), Col(2, "b2", 8, 2, 104, 1, keyOrdinal: 1, bitPos: 1));
        Assert.Equal("V00;V01;", KeyDecoder.FromIndexRecord(Hex("16 02 0200 00"), kbr));
    }

    [Fact]
    public void NullableBitKey_IsNullByItsBitmapBit()
    {
        var kbn = Schema("kbn", Col(1, "uniqueifier", -1, 1, 56, 4, uniqueifier: true), Col(2, "id", 4, 2, 56, 4, keyOrdinal: 1), Col(3, "b", 8, 3, 104, 1, keyOrdinal: 2));
        Assert.Equal("V01000000;N;U0", KeyDecoder.FromIndexRecord(Hex("36 01000000 00 0300 02 0100 1100 00000000"), kbn));
    }

    [Fact]
    public void KeyOrderIsNotColumnOrder_AndDescIsStoredAsAscending()
    {
        // ko(k1 int, k2 int, v int), PRIMARY KEY (k2, k1) : k2 est stocké en premier.
        var ko = Schema("ko", Col(1, "k2", 4, 1, 56, 4, keyOrdinal: 1), Col(2, "k1", 8, 2, 56, 4, keyOrdinal: 2), Col(3, "v", 12, 3, 56, 4));
        Assert.Equal("V0A000000;V02000000;", KeyDecoder.FromIndexRecord(Hex("16 0a000000 02000000 0200 00"), ko));
        Assert.Equal("V0A000000;V02000000;", KeyDecoder.FromRow(Hex("10001000 0A000000 02000000 03000000 0300 00"), ko));

        var kd = Schema("kd", Col(1, "k", 4, 1, 56, 4, keyOrdinal: 1), Col(2, "v", 8, 2, 56, 4));
        Assert.Equal("V02000000;", KeyDecoder.FromIndexRecord(Hex("16 02000000 0100 00"), kd));

        var kdv = Schema("kdv", Col(1, "k", -1, 1, 167, 10, keyOrdinal: 1), Col(2, "n", 4, 2, 56, 4, keyOrdinal: 2));
        Assert.Equal("V616264;V06000000;", KeyDecoder.FromIndexRecord(Hex("36 06000000 0200 00 0100 0f00 616264"), kdv));
    }

    [Fact]
    public void NullableKeys_FollowKeyOrdinalNotColumnOrder()
    {
        // kn(a varchar NULL, b int NULL, c int NULL), clé non unique (c, a, b) : bit 0 = c, 1 = a, 2 = b, 3 = uniqueifier.
        var kn = Schema("kn", Col(1, "uniqueifier", -1, 1, 56, 4, uniqueifier: true), Col(2, "a", -2, 2, 167, 5, keyOrdinal: 2),
            Col(3, "b", 8, 3, 56, 4, keyOrdinal: 3), Col(4, "c", 4, 4, 56, 4, keyOrdinal: 1));
        Assert.Equal("N;V78;V01000000;U0", KeyDecoder.FromIndexRecord(Hex("36 00000000 01000000 0400 01 0200 1300 1700 78 00000000"), kn));
        Assert.Equal("V01000000;N;V01000000;U0", KeyDecoder.FromIndexRecord(Hex("36 01000000 01000000 0400 02 0200 1200 1600 00000000"), kn));
        Assert.Equal("V01000000;V78;N;U0", KeyDecoder.FromIndexRecord(Hex("36 01000000 00000000 0400 04 0200 1300 1700 78 00000000"), kn));
    }

    [Fact]
    public void UniqueifierAfterAVariableKey_CountsUp()
    {
        var ku = Schema("ku", Col(1, "uniqueifier", -1, 1, 56, 4, uniqueifier: true), Col(2, "k", -2, 2, 167, 10, keyOrdinal: 1));
        Assert.Equal("V647570;U0", KeyDecoder.FromIndexRecord(Hex("36 0200 00 0200 0d00 1100 647570 00000000"), ku));
        Assert.Equal("V647570;U1", KeyDecoder.FromIndexRecord(Hex("36 0200 00 0200 0d00 1100 647570 01000000"), ku));
        Assert.Equal("V647570;U2", KeyDecoder.FromIndexRecord(Hex("36 0200 00 0200 0d00 1100 647570 02000000"), ku));
    }

    [Fact]
    public void EmptyVariableKey_IsNotNull()
    {
        var kv = Schema("kv", Col(1, "k", -1, 1, 167, 10, keyOrdinal: 1));
        Assert.Equal("V;", KeyDecoder.FromIndexRecord(Hex("36 0100 00 0100 0800"), kv));
        Assert.Equal("N;", KeyDecoder.FromIndexRecord(Hex("36 0100 01 0100 0800"), kv));
    }

    // ---- Clé variable vide ou NULL en fin de ligne : absente du tableau d'offsets (octets mesurés sur SQL Server 2022) ----

    [Fact]
    public void TrailingEmptyOrNullVariableKey_RowAndIndexRecordAgree()
    {
        // ea(k varchar(10) primary key, v int) : ('', 1). L'index n'a ni zone variable (statut 16) ni octets.
        var ea = Schema("ea", Col(1, "k", -1, 1, 167, 10, keyOrdinal: 1), Col(2, "v", 4, 2, 56, 4));
        Assert.Equal("V;", KeyDecoder.FromRow(Hex("1000080001000000020000"), ea));
        Assert.Equal("V;", KeyDecoder.FromIndexRecord(Hex("16010000"), ea));

        // eb(k, j varchar(10), v int), clé (k, j) : ('a', '') ; j, vide et dernier, manque aux deux.
        var eb = Schema("eb", Col(1, "k", -1, 1, 167, 10, keyOrdinal: 1), Col(2, "j", -2, 2, 167, 10, keyOrdinal: 2), Col(3, "v", 4, 3, 56, 4));
        Assert.Equal("V61;V;", KeyDecoder.FromRow(Hex("30000800010000000300000100100061"), eb));
        Assert.Equal("V61;V;", KeyDecoder.FromIndexRecord(Hex("360200000100090061"), eb));

        // en(i int, s varchar(10) NULL, v int), index clustered non unique (i, s) : (0, NULL).
        var en = Schema("en", Col(1, "uniqueifier", -1, 1, 56, 4, uniqueifier: true), Col(2, "i", 4, 2, 56, 4, keyOrdinal: 1),
            Col(3, "s", -2, 3, 167, 10, keyOrdinal: 2), Col(4, "v", 8, 4, 56, 4));
        Assert.Equal("V00000000;N;U0", KeyDecoder.FromRow(Hex("10000C000000000001000000040004"), en));
        Assert.Equal("V00000000;N;U0", KeyDecoder.FromIndexRecord(Hex("360000000003000202000E00120000000000"), en));
    }

    [Fact]
    public void NonUniqueIndexPartitionedOutsideTheKey_IsNotSupported()
    {
        // pt(id int, reg int, v int) partitionnée sur reg, index non unique (id) : l'enregistrement porte reg comme
        // entrée de clé supplémentaire (3 colonnes) que le schéma ne signale pas. Mesuré sur 2022 ; null attendu.
        var pt = Schema("pt", Col(1, "uniqueifier", -1, 1, 56, 4, uniqueifier: true), Col(2, "id", 4, 2, 56, 4, keyOrdinal: 1),
            Col(3, "reg", 8, 3, 56, 4), Col(4, "v", 12, 4, 56, 4));
        Assert.Null(KeyDecoder.FromIndexRecord(Hex("3601000000050000000300000100140000000000"), pt));
    }

    // ---- Entrées illisibles ----

    [Fact]
    public void UnreadableInput_ReturnsNull()
    {
        Assert.Null(KeyDecoder.FromIndexRecord(null, S));
        Assert.Null(KeyDecoder.FromIndexRecord([], S));
        Assert.Null(KeyDecoder.FromRow(null, S));
        Assert.Null(KeyDecoder.FromRow([], S));
        Assert.Null(KeyDecoder.FromIndexRecord(Hex("16 01000000 0100"), S));                           // bitmap manquant
        Assert.Null(KeyDecoder.FromIndexRecord(Hex("16 01000000"), S));                                // fin dans le nombre de colonnes
        Assert.Null(KeyDecoder.FromIndexRecord(Hex("16 01000000 0200 00"), S));                        // nombre de colonnes faux
        Assert.Null(KeyDecoder.FromIndexRecord(Hex("10 01000000 0100 00"), S));                        // pas un enregistrement d'index
        Assert.Null(KeyDecoder.FromIndexRecord(Hex("36 01000000 0100 00"), S));                        // zone variable annoncée, aucune colonne variable
        Assert.Null(KeyDecoder.FromIndexRecord(Hex("36 0100 00 0100 0f00 616263"), K));                // nombre de colonnes faux pour k
        Assert.Null(KeyDecoder.FromIndexRecord(Hex("36 07000000 0200 00 0100 0f00 6162"), K));         // fin de variable au-delà de l'enregistrement
        Assert.Null(KeyDecoder.FromIndexRecord(Hex("36 01000000 0200 00 0100 1000 000000"), N));       // uniqueifier de 3 octets
        Assert.Null(KeyDecoder.FromRow(Hex("10000C0001"), S));                                         // ligne tronquée
    }

    // ---- Colonne de clé non lue : pas de clé, plutôt qu'une clé fabriquée (spec03 §7.1) ----

    [Fact]
    public void KeyColumnOrUniqueifierWithANote_GivesNoKey()
    {
        // k1 hors ligne (fin 0x16 | 0x8000) : l'ancien code l'encodait comme une chaîne vide.
        Assert.Null(KeyDecoder.FromRow(Hex("30000C00070000000100000003000001001680616263"), K));
        // k1 dans une page de code inconnue : ses octets ne vont pas au type.
        var kBadCodePage = K with { Columns = K.Columns.Select(c => c.Name == "k1" ? c with { CodePage = 12345 } : c).ToList() };
        Assert.Null(KeyDecoder.FromRow(Hex("30000C00070000000100000003000001001600616263"), kBadCodePage));
        // Uniqueifier de 3 octets dans la ligne : l'ancien code en faisait U0.
        Assert.Null(KeyDecoder.FromRow(Hex("30000C00010000000200000003000001001600010000"), N));
    }

    // ---- Fixtures : journaux réels de SQL Server 2017 à 2025 ----

    public static TheoryData<string> Versions => new() { "2017", "2019", "2022", "2025" };

    private static (string? Key, string Op)[] Keys(string version, string scenario, string table, out TableSchema schema, bool modifies)
    {
        var (s, records) = FixtureSet.Load(version, scenario).ForTable(table);
        schema = s;
        Assert.Equal(1, s.IndexId);
        return records
            .Where(r => !r.IsCompensation && (modifies   // une compensation de delete n'a pas d'image, une d'insert répète une clé
                ? r.Operation is "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS"
                : r.Operation is "LOP_INSERT_ROWS" or "LOP_DELETE_ROWS"))
            .Select(r => (modifies ? KeyDecoder.FromIndexRecord(r.RowLogContents2, s) : KeyDecoder.FromRow(r.RowLogContents0, s), r.Operation))
            .ToArray();
    }

    public static TheoryData<string, string, string> ClusteredTables
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var v in new[] { "2017", "2019", "2022", "2025" })
            {
                foreach (var t in new[] { "k", "c", "n", "nk", "bk", "ko", "kd" }) data.Add(v, "keys", "dbo." + t);
                foreach (var t in new[] { "s", "t", "u", "m", "m2" }) data.Add(v, "modify", "dbo." + t);
            }
            return data;
        }
    }

    [Theory, MemberData(nameof(ClusteredTables))]
    public void Fixtures_EveryModifyKey_IsTheKeyOfAnInsertedRow_AndDistinctRowsDiffer(string version, string scenario, string table)
    {
        var rows = Keys(version, scenario, table, out _, modifies: false);
        var modifies = Keys(version, scenario, table, out _, modifies: true);

        Assert.All(rows, r => Assert.NotNull(r.Key));
        Assert.All(modifies, m => Assert.NotNull(m.Key));
        var inserted = rows.Where(r => r.Op == "LOP_INSERT_ROWS").Select(r => r.Key!).ToList();
        Assert.Equal(inserted.Count, inserted.Distinct().Count());   // aucune des fixtures ne réinsère une clé
        Assert.All(rows.Where(r => r.Op == "LOP_DELETE_ROWS"), d => Assert.Contains(d.Key, inserted));
        Assert.All(modifies, m => Assert.Contains(m.Key, inserted));
    }

    [Theory, MemberData(nameof(Versions))]
    public void Fixtures_Nk_NullAndZeroAreTwoKeys_AndTheNullRowKeepsItsKey(string version)
    {
        var rows = Keys(version, "keys", "dbo.nk", out _, modifies: false);
        var modifies = Keys(version, "keys", "dbo.nk", out _, modifies: true);

        Assert.Equal(2, rows.Length);
        Assert.Equal("V78;N;U0", rows[0].Key);   // ('x', NULL), dont les octets de k2 sont périmés
        Assert.Equal("V78;V00000000;U0", rows[1].Key);
        Assert.Equal(new[] { "V78;N;U0", "V78;V00000000;U0" }, modifies.Select(m => m.Key).Order(StringComparer.Ordinal));
    }

    [Theory, MemberData(nameof(Versions))]
    public void Fixtures_Bk_KeyIsStableAcrossTheUpdatesOfCAndD(string version)
    {
        var rows = Keys(version, "keys", "dbo.bk", out _, modifies: false);
        var modifies = Keys(version, "keys", "dbo.bk", out _, modifies: true);

        Assert.Single(rows);
        Assert.Equal("V01000000;V01;", rows[0].Key);
        Assert.NotEmpty(modifies);
        Assert.All(modifies, m => Assert.Equal(rows[0].Key, m.Key));
    }

    [Theory, MemberData(nameof(Versions))]
    public void Fixtures_C_ConcatenatedKeysDiffer(string version)
    {
        var rows = Keys(version, "keys", "dbo.c", out _, modifies: false);

        Assert.Equal(new[] { "V41;V424344;", "V4142;V4344;" }, rows.Select(r => r.Key));
    }

    [Theory, MemberData(nameof(Versions))]
    public void Fixtures_N_UniqueifierSeparatesTheSecondRow(string version)
    {
        var rows = Keys(version, "keys", "dbo.n", out _, modifies: false);
        var modifies = Keys(version, "keys", "dbo.n", out _, modifies: true);

        Assert.Equal(new[] { "V01000000;U0", "V01000000;U1" }, rows.Select(r => r.Key));
        Assert.Equal(new[] { "V01000000;U0", "V01000000;U1" }, modifies.Select(m => m.Key).Order(StringComparer.Ordinal));
    }

    [Theory, MemberData(nameof(Versions))]
    public void Fixtures_KeyOrderAndDesc(string version)
    {
        Assert.Equal(new[] { "V14000000;V01000000;", "V0A000000;V02000000;" }, Keys(version, "keys", "dbo.ko", out _, modifies: false).Select(r => r.Key));
        Assert.Equal(new[] { "V01000000;", "V02000000;" }, Keys(version, "keys", "dbo.kd", out _, modifies: false).Select(r => r.Key));
    }

    [Theory, MemberData(nameof(Versions))]
    public void Fixtures_CompensationRecordsCarryNoKey(string version)
    {
        var (schema, records) = FixtureSet.Load(version, "modify").ForTable("dbo.t");
        var compensations = records.Where(r => r.IsCompensation && r.Operation is "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS").ToList();

        Assert.NotEmpty(compensations);
        Assert.All(compensations, r => Assert.Null(KeyDecoder.FromIndexRecord(r.RowLogContents2, schema)));
    }
}
