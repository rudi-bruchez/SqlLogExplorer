using SqlLogExplorer.Data;
using SqlLogExplorer.Decoding;
using SqlLogExplorer.Models;
using SqlLogExplorer.Services;
using Xunit;

namespace SqlLogExplorer.Tests;

public class DecodingServiceTests
{
    public static TheoryData<string> Versions => new() { "2017", "2019", "2022", "2025" };
    public static TheoryData<string> AdrVersions => new() { "2019", "2022", "2025" };

    private const string NoImage = "no full image of this row available";

    private static async Task<LogCache> CacheAsync(params IReadOnlyList<LogRecord>[] batches)
    {
        var cache = await LogCache.CreateAsync(Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db"));
        foreach (var batch in batches) await cache.InsertBatchAsync(batch);
        return cache;
    }

    private static Func<long, CancellationToken, Task<TableSchema?>> Lookup(Fixture f) =>
        (au, _) => Task.FromResult(f.Schemas.TryGetValue(au, out var s) ? s : null);

    private static string Values(DecodedRow? row) =>
        row is null ? "<null>" : string.Join(", ", row.Columns.Select(c => $"{c.Name}={c.Value ?? "NULL"}"));

    private static object? Value(DecodedRow? row, string column) => row!.Columns.Single(c => c.Name == column).Value;

    private static List<LogRecord> Modifies(IEnumerable<LogRecord> records) =>
        records.Where(r => r.Operation is "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS" && !r.IsCompensation).ToList();

    private static async Task<List<(string Lsn, string? Context, string Operation, bool Compensation, string? RowKey)>> KeysAsync(LogCache cache, long allocUnitId)
    {
        await using var cmd = cache.Connection.CreateCommand();
        cmd.CommandText = "SELECT LSN, Context, Operation, Description, RowKey FROM LogRecords WHERE AllocUnitId = $au ORDER BY LSN;";
        cmd.Parameters.AddWithValue("$au", allocUnitId);
        var list = new List<(string, string?, string, bool, string?)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2),
                !r.IsDBNull(3) && r.GetString(3).Contains("COMPENSATION"), r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    // ---- modify : dbo.t ----

    // modify.sql, dans l'ordre : a = 11, b = 'abcdef', d = 1, TABLOCKX a = 20, b = NULL, b = 'zz'.
    private static readonly string[] ExpectedT =
    [
        "id=1, a=11, b=abc, c=True, d=False",
        "id=1, a=11, b=abcdef, c=True, d=False",
        "id=1, a=11, b=abcdef, c=True, d=True",
        "id=1, a=20, b=abcdef, c=True, d=True",
        "id=1, a=20, b=NULL, c=True, d=True",
        "id=1, a=20, b=zz, c=True, d=True",
    ];

    [Theory, MemberData(nameof(Versions))]
    public async Task Modify_RebuildsEveryModifyOfDboT_AndKeysTheAllocationUnit(string version)
    {
        var f = FixtureSet.Load(version, "modify");
        var (schema, records) = f.ForTable("dbo.t");
        var mods = Modifies(records);
        Assert.Equal(9, mods.Count);
        await using var cache = await CacheAsync(f.Log);
        var service = new DecodingService(cache, Lookup(f), isLive: false);

        for (var i = 0; i < ExpectedT.Length; i++)
        {
            var x = await service.InspectAsync(mods[i]);
            Assert.True(x.After is not null, $"{version} modify #{i}: {x.ReplayStatus}");
            Assert.Equal(ExpectedT[i], Values(x.After));
            Assert.StartsWith("rebuilt", x.ReplayStatus);
            Assert.Equal(TxOutcome.Committed, x.Outcome);
            Assert.Null(x.Row);
            Assert.Null(x.SchemaMessage);
            Assert.False(x.CanRebuildFromCurrentRow);
        }

        // La première inspection a posé la clé de chaque enregistrement de données de dbo.t, sauf les compensations, et d'aucun LCX_PFS.
        var keys = await KeysAsync(cache, schema.AllocUnitId);
        string[] data = ["LCX_HEAP", "LCX_CLUSTERED", "LCX_MARK_AS_GHOST"];
        string[] ops = ["LOP_INSERT_ROWS", "LOP_DELETE_ROWS", "LOP_MODIFY_ROW", "LOP_MODIFY_COLUMNS"];
        var keyed = keys.Where(k => data.Contains(k.Context) && ops.Contains(k.Operation) && !k.Compensation).ToList();
        Assert.Equal(11, keyed.Count);   // un insert, neuf modify, un delete
        Assert.All(keyed, k => Assert.NotNull(k.RowKey));
        Assert.Single(keyed.Select(k => k.RowKey).Distinct());
        Assert.Contains(keys, k => k.Context == "LCX_PFS");
        Assert.All(keys.Where(k => k.Context == "LCX_PFS"), k => Assert.Null(k.RowKey));
        Assert.Contains(keys, k => k.Compensation);
        Assert.All(keys.Where(k => k.Compensation), k => Assert.Null(k.RowKey));
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task UnkeyedDataRecords_LeaveOutPfsRecordsAndCompensations(string version)
    {
        // KeyDecoder rejette déjà un LCX_PFS (pas de RowLog Contents 2) : le filtre de contexte se vérifie sur la requête elle-même.
        var f = FixtureSet.Load(version, "modify");
        var (schema, _) = f.ForTable("dbo.t");
        await using var cache = await CacheAsync(f.Log);

        var unkeyed = await new LogQuery(cache.Connection).GetUnkeyedDataRecordsAsync(schema.AllocUnitId);

        Assert.Contains(f.Log, r => r.AllocUnitId == schema.AllocUnitId && r.Context == "LCX_PFS" && r.Operation == "LOP_MODIFY_ROW");
        Assert.Equal(11, unkeyed.Count);
        Assert.All(unkeyed, r => Assert.Contains(r.Context, new[] { "LCX_HEAP", "LCX_CLUSTERED", "LCX_MARK_AS_GHOST" }));
        Assert.DoesNotContain(unkeyed, r => r.IsCompensation);
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task Modify_ChangesComeFromTheRebuiltRows(string version)
    {
        var f = FixtureSet.Load(version, "modify");
        var mods = Modifies(f.ForTable("dbo.t").Records);
        await using var cache = await CacheAsync(f.Log);

        var x = await new DecodingService(cache, Lookup(f), false).InspectAsync(mods[0]);

        Assert.Equal("id=1, a=10, b=abc, c=True, d=False", Values(x.Before));
        var changed = Assert.Single(x.Changes, c => c.Changed == true);
        Assert.Equal(new ColumnChange("a", 10, 11, true), changed);
        Assert.Equal(5, x.Changes.Count);
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task Modify_RolledBackAndSavepointTransactions(string version)
    {
        var f = FixtureSet.Load(version, "modify");
        var mods = Modifies(f.ForTable("dbo.t").Records);
        await using var cache = await CacheAsync(f.Log);
        var service = new DecodingService(cache, Lookup(f), false);

        var rolledBack = await service.InspectAsync(mods[6]);
        Assert.Equal(TxOutcome.RolledBack, rolledBack.Outcome);
        Assert.Equal("id=1, a=99, b=zz, c=True, d=True", Values(rolledBack.After));

        foreach (var m in mods.Skip(7))
        {
            var x = await service.InspectAsync(m);
            Assert.Equal($"partial rollback at LSN {mods[7].Lsn}, not replayed", x.ReplayStatus);
            Assert.Equal(TxOutcome.PartlyRolledBack, x.Outcome);
            Assert.Null(x.After);
        }
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task Modify_ImportedTwice_ChangesNoResult(string version)
    {
        var f = FixtureSet.Load(version, "modify");
        var mods = Modifies(f.ForTable("dbo.t").Records);
        await using var once = await CacheAsync(f.Log);
        await using var twice = await CacheAsync(f.Log, f.Log);
        var a = new DecodingService(once, Lookup(f), false);
        var b = new DecodingService(twice, Lookup(f), false);

        foreach (var m in mods)
        {
            var x = await a.InspectAsync(m);
            await twice.InsertBatchAsync(f.Log);   // un import qui recouvre le précédent, entre deux inspections
            var y = await b.InspectAsync(m);
            Assert.Equal(x.ReplayStatus, y.ReplayStatus);
            Assert.Equal(Values(x.After), Values(y.After));
            Assert.Equal(x.Outcome, y.Outcome);
        }
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task Modify_RecordsAddedAfterAFirstInspection_AreKeyedToo(string version)
    {
        var f = FixtureSet.Load(version, "modify");
        var mods = Modifies(f.ForTable("dbo.t").Records);
        var cut = mods[0].Lsn;
        var first = f.Log.Where(r => string.CompareOrdinal(r.Lsn, cut) <= 0).ToList();
        var rest = f.Log.Where(r => string.CompareOrdinal(r.Lsn, cut) > 0).ToList();
        await using var cache = await CacheAsync(first);
        var service = new DecodingService(cache, Lookup(f), false);

        Assert.Equal(ExpectedT[0], Values((await service.InspectAsync(mods[0])).After));
        await cache.InsertBatchAsync(rest);
        var later = await service.InspectAsync(mods[5]);

        Assert.Equal(ExpectedT[5], Values(later.After));
    }

    // ---- modify : dbo.s, des slots déplacés ----

    [Theory, MemberData(nameof(Versions))]
    public async Task Modify_KeyedChainIgnoresSlotsShiftedByAnotherInsert(string version)
    {
        // id = 0 inséré après 1 et 2 décale leurs slots : l'adresse du modify de id = 1 est celle où id = 2 a été inséré.
        var f = FixtureSet.Load(version, "modify");
        var (_, records) = f.ForTable("dbo.s");
        var update = Assert.Single(Modifies(records));
        await using var cache = await CacheAsync(f.Log);

        var x = await new DecodingService(cache, Lookup(f), false).InspectAsync(update);

        Assert.Equal("id=1, a=11", Values(x.After));
        Assert.Equal("id=1, a=10", Values(x.Before));
    }

    // ---- sans schéma ----

    [Theory, MemberData(nameof(Versions))]
    public async Task NoSchemaConnection_SaysSoWithoutThrowing(string version)
    {
        var f = FixtureSet.Load(version, "modify");
        var records = f.ForTable("dbo.t").Records;
        await using var cache = await CacheAsync(f.Log);
        var service = new DecodingService(cache, null, isLive: true);

        var x = await service.InspectAsync(Modifies(records)[0]);
        var insert = await service.InspectAsync(records.First(r => r.Operation == "LOP_INSERT_ROWS"));

        Assert.Null(x.Row);
        Assert.Null(x.Before);
        Assert.Null(x.After);
        Assert.Equal("a schema connection is required", x.SchemaMessage);
        Assert.Equal(TxOutcome.Committed, x.Outcome);
        Assert.False(x.CanRebuildFromCurrentRow);
        Assert.Equal("a schema connection is required", insert.SchemaMessage);
        Assert.NotNull(insert.Row);
        Assert.Empty(insert.Row!.Columns);
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task AllocationUnitUnknownToTheSchema_DecodesTheInsertWithoutColumns(string version)
    {
        var f = FixtureSet.Load(version, "modify");
        var records = f.ForTable("dbo.t").Records;
        await using var cache = await CacheAsync(f.Log);
        var service = new DecodingService(cache, (_, _) => Task.FromResult<TableSchema?>(null), false);

        var insert = await service.InspectAsync(records.First(r => r.Operation == "LOP_INSERT_ROWS"));
        var modify = await service.InspectAsync(Modifies(records)[0]);

        Assert.Equal("allocation unit not found in the connected database", insert.SchemaMessage);
        Assert.NotNull(insert.Row);
        Assert.Empty(insert.Row!.Columns);
        Assert.Contains(insert.Row.Fields, x => x.Role == FieldRole.FixedEnd);
        Assert.Equal("allocation unit not found in the connected database", modify.SchemaMessage);
        Assert.Null(modify.Row);
        Assert.Null(modify.Before);
        Assert.Null(modify.After);
        Assert.Empty(modify.Changes);
    }

    // ---- schema_change : reconstruction d'index ----

    [Theory, MemberData(nameof(Versions))]
    public async Task IndexRebuild_StartsANewChain(string version)
    {
        var f = FixtureSet.Load(version, "schema_change");
        var (schema, records) = f.ForTable("dbo.rb");
        var update = Assert.Single(Modifies(records));
        var oldInsert = f.Log.Single(r => r.Operation == "LOP_INSERT_ROWS" && r.Context == "LCX_CLUSTERED"
            && r.AllocUnitId != schema.AllocUnitId && f.Schemas.TryGetValue(r.AllocUnitId!.Value, out var s) && s.TableName == "rb");
        await using var cache = await CacheAsync(f.Log);
        var offline = new DecodingService(cache, Lookup(f), false);
        var live = new DecodingService(cache, Lookup(f), true);

        var x = await offline.InspectAsync(update);
        var chain = await offline.GetRowChainAsync(update);

        Assert.Equal(NoImage, x.ReplayStatus);
        Assert.DoesNotContain(chain, r => r.Lsn == oldInsert.Lsn);
        Assert.Contains(chain, r => r.Lsn == update.Lsn);
        Assert.False(x.CanRebuildFromCurrentRow);
        Assert.True((await live.InspectAsync(update)).CanRebuildFromCurrentRow);
        // Sans rejeu, le fragment seul : un octet dans l'entier v (spec03 §6.1).
        var v = Assert.Single(x.Changes);
        Assert.Equal("v", v.Column);
        Assert.True(v.Changed);
    }

    // ---- keys : enregistrement d'index non clustered ----

    [Theory, MemberData(nameof(Versions))]
    public async Task NonclusteredIndexDelete_IsNotDecodedAsARow(string version)
    {
        var f = FixtureSet.Load(version, "keys");
        await using var cache = await CacheAsync(f.Log);
        var service = new DecodingService(cache, Lookup(f), false);
        var deletes = f.Log.Where(r => r.Operation == "LOP_DELETE_ROWS" && r.AllocUnitName?.StartsWith("dbo.ix.") == true).ToList();
        var onIndex = deletes.Single(r => f.Schemas[r.AllocUnitId!.Value].IndexId == 2);
        var onTable = deletes.Single(r => f.Schemas[r.AllocUnitId!.Value].IndexId == 1);

        var x = await service.InspectAsync(onIndex);
        var y = await service.InspectAsync(onTable);

        Assert.NotNull(x.Row);
        Assert.Empty(x.Row!.Columns);
        Assert.Equal("id=1, a=100, name=alice", Values(y.Row));
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task Admission_ChecksTheAllocationUnit_NotOnlyTheRecordType(string version)
    {
        // Synthétique : l'enregistrement d'index ci-dessus est de type 3, que RowDecoder écarte de lui-même ; ici une ligne primaire (type 0)
        // servie avec le schéma d'un index non clustered, pour que seule la vérification IsDataRows la tienne à l'écart (spec03 §5).
        var f = FixtureSet.Load(version, "keys");
        var onTable = f.Log.Single(r => r.Operation == "LOP_DELETE_ROWS" && r.AllocUnitName?.StartsWith("dbo.ix.") == true
                                        && f.Schemas[r.AllocUnitId!.Value].IndexId == 1);
        var asIndex = f.Schemas[onTable.AllocUnitId!.Value] with { IndexId = 2 };
        await using var cache = await CacheAsync(f.Log);

        var x = await new DecodingService(cache, (_, _) => Task.FromResult<TableSchema?>(asIndex), false).InspectAsync(onTable);

        Assert.NotNull(x.Row);
        Assert.Equal(0, x.Row!.RecordType);
        Assert.Empty(x.Row.Columns);
    }

    // ---- heap ----

    [Theory, MemberData(nameof(Versions))]
    public async Task Heap_UpdateRebuildsFromItsInsert_AndTheSlotReuseIsAnotherRow(string version)
    {
        var f = FixtureSet.Load(version, "heap");
        var (_, records) = f.ForTable("dbo.h");
        var insert1 = records.First(r => r.Operation == "LOP_INSERT_ROWS");
        var update = Modifies(records).First();
        var insert2 = records.First(r => r.Operation == "LOP_INSERT_ROWS" && r.PageId == insert1.PageId && r.SlotId == insert1.SlotId
                                         && string.CompareOrdinal(r.Lsn, update.Lsn) > 0);
        await using var cache = await CacheAsync(f.Log);
        var service = new DecodingService(cache, Lookup(f), true);

        var x = await service.InspectAsync(update);
        var chain = await service.GetRowChainAsync(update);

        Assert.Equal(1, Value(x.After, "id"));
        Assert.Equal(11, Value(x.After, "v"));
        Assert.Equal(10, Value(x.Before, "v"));
        Assert.Equal($"rebuilt forwards from INSERT at LSN {insert1.Lsn}", x.ReplayStatus);
        Assert.False(x.CanRebuildFromCurrentRow);
        Assert.Contains(chain, r => r.Lsn == insert1.Lsn);
        Assert.DoesNotContain(chain, r => r.Lsn == insert2.Lsn);
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task Heap_ForwardedRow_IsNotFollowed(string version)
    {
        var f = FixtureSet.Load(version, "heap");
        var (_, records) = f.ForTable("dbo.h");
        // id = 101 : le modify dont l'après est un talon de renvoi (type 2).
        var update = Modifies(records).Single(r => r.RowLogContents1 is { Length: > 0 } after && (after[0] >> 1 & 7) == 2);
        await using var cache = await CacheAsync(f.Log);

        var x = await new DecodingService(cache, Lookup(f), false).InspectAsync(update);

        Assert.Equal("row forwarded, not followed", x.ReplayStatus);
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task Heap_WithoutAnImage_CannotUseTheCurrentRow(string version)
    {
        var f = FixtureSet.Load(version, "heap");
        var (_, records) = f.ForTable("dbo.h");
        var insert1 = records.First(r => r.Operation == "LOP_INSERT_ROWS");
        var update = Modifies(records).First();
        // Ni l'insert ni le delete de la ligne : aucune des deux ancres du cache.
        var delete1 = records.First(r => r.Operation == "LOP_DELETE_ROWS");
        await using var cache = await CacheAsync(f.Log.Where(r => r.Lsn != insert1.Lsn && r.Lsn != delete1.Lsn).ToList());

        var x = await new DecodingService(cache, Lookup(f), true).InspectAsync(update);

        Assert.Equal(NoImage, x.ReplayStatus);
        Assert.False(x.CanRebuildFromCurrentRow);
    }

    // ---- split ----

    [Theory, MemberData(nameof(Versions))]
    public async Task Split_UpdateAfterTheSplitIsRebuilt_AndTheMovedRowsAreLabelled(string version)
    {
        var f = FixtureSet.Load(version, "split");
        var (_, records) = f.ForTable("dbo.sp");
        var update = Assert.Single(Modifies(records));
        var splits = f.Log.Where(r => r.Operation == "LOP_BEGIN_XACT" && r.TransactionName == "SplitPage").Select(r => r.TransactionId).ToHashSet();
        var moved = records.Single(r => r.Operation == "LOP_INSERT_ROWS" && splits.Contains(r.TransactionId));
        var plain = records.First(r => r.Operation == "LOP_INSERT_ROWS");
        await using var cache = await CacheAsync(f.Log);
        var service = new DecodingService(cache, Lookup(f), false);

        var x = await service.InspectAsync(update);

        Assert.StartsWith("rebuilt", x.ReplayStatus);
        Assert.Equal(4, Value(x.After, "id"));
        Assert.Equal(40, Value(x.After, "a"));
        Assert.Equal(2, Value(x.Before, "a"));
        Assert.Equal("row moved by the engine", (await service.InspectAsync(moved)).Label);
        Assert.Null((await service.InspectAsync(plain)).Label);
    }

    // ---- versioned_adr ----

    [Theory, MemberData(nameof(AdrVersions))]
    public async Task VersionedAdr_DeleteImageCarriesTheWarning(string version)
    {
        var f = FixtureSet.Load(version, "versioned_adr");
        var (_, records) = f.ForTable("dbo.vr");
        var delete = records.Single(r => r.Operation == "LOP_DELETE_ROWS");
        var insert = records.Single(r => r.Operation == "LOP_INSERT_ROWS");
        await using var cache = await CacheAsync(f.Log);
        var service = new DecodingService(cache, Lookup(f), false);

        var x = await service.InspectAsync(delete);

        Assert.Equal("may hold values undone by a partial rollback", x.Warning);
        Assert.Equal(1, Value(x.Row, "id"));
        // L'insert précède la révocation : son image ne peut rien en tenir.
        Assert.Null((await service.InspectAsync(insert)).Warning);
    }

    // L'avertissement ne vaut que pour l'image d'un delete, et dans la vie de la clé qui tient la révocation (spec03 §7.3, §7.4).
    [Theory, MemberData(nameof(AdrVersions))]
    public async Task VersionedAdr_NextLifeOfTheKey_CarriesNoWarning(string version)
    {
        var f = FixtureSet.Load(version, "versioned_adr");
        var (_, records) = f.ForTable("dbo.vr");
        var delete = records.Single(r => r.Operation == "LOP_DELETE_ROWS");
        var insert = records.Single(r => r.Operation == "LOP_INSERT_ROWS");
        // La clé revient après le delete, puis repart : deux enregistrements d'une vie suivante, sans révocation.
        var again = insert with { Lsn = "FFFFFFFF:00000001:0001", TransactionId = null };
        var gone = delete with { Lsn = "FFFFFFFF:00000001:0002", TransactionId = null };
        await using var cache = await CacheAsync(f.Log, [again, gone]);
        var service = new DecodingService(cache, Lookup(f), false);

        Assert.Equal("may hold values undone by a partial rollback", (await service.InspectAsync(delete)).Warning);
        Assert.Null((await service.InspectAsync(again)).Warning);
        Assert.Null((await service.InspectAsync(gone)).Warning);
    }

    [Theory, MemberData(nameof(AdrVersions))]
    public async Task VersionedAdr_AnInsertAfterTheRevert_CarriesNoWarning(string version)
    {
        // Une ligne déplacée par un fractionnement est journalisée comme un insert (spec03 §2) : dans la même vie, après la révocation.
        // L'avertissement vise l'image d'un delete, pas celle d'un insert.
        var f = FixtureSet.Load(version, "versioned_adr");
        var (_, records) = f.ForTable("dbo.vr");
        var insert = records.Single(r => r.Operation == "LOP_INSERT_ROWS");
        var lastModify = records.Last(r => r.Operation is "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS");
        var moved = insert with { Lsn = lastModify.Lsn[..^4] + "FFFF", TransactionId = null };
        Assert.DoesNotContain(f.Log, r => r.Lsn == moved.Lsn);
        await using var cache = await CacheAsync(f.Log, [moved]);

        Assert.Null((await new DecodingService(cache, Lookup(f), false).InspectAsync(moved)).Warning);
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task PlainDelete_HasNoWarning(string version)
    {
        var f = FixtureSet.Load(version, "modify");
        var delete = f.ForTable("dbo.t").Records.Single(r => r.Operation == "LOP_DELETE_ROWS");
        await using var cache = await CacheAsync(f.Log);

        var x = await new DecodingService(cache, Lookup(f), false).InspectAsync(delete);

        Assert.Null(x.Warning);
        Assert.Equal("id=1, a=21, b=zz, c=True, d=True", Values(x.Row));
        Assert.Equal(TxOutcome.Committed, x.Outcome);
    }

    // ---- Review Focus : unités inconnues, doublons, ordre mélangé, cas limites ----

    [Theory, MemberData(nameof(Versions))]
    public async Task UnknownAllocationUnit_WithAShortImage_ReturnsAProblemRow(string version)
    {
        var f = FixtureSet.Load(version, "modify");
        var insert = f.ForTable("dbo.t").Records.First(r => r.Operation == "LOP_INSERT_ROWS");
        var odd = insert with { AllocUnitId = 424242, RowLogContents0 = [0x30, 0x00, 0x0D] };
        await using var cache = await CacheAsync(f.Log);

        var x = await new DecodingService(cache, Lookup(f), false).InspectAsync(odd);

        Assert.NotNull(x.Row);
        Assert.True(x.Row!.HasProblem);
        Assert.Equal("allocation unit not found in the connected database", x.SchemaMessage);
    }

    [Fact]
    public async Task TwoVersionsInOneCache_AreReturnedInLsnOrder_AndEveryInspectionReturns()
    {
        // Deux bases différentes : on ne vérifie que l'ordre et l'absence d'exception.
        var f17 = FixtureSet.Load("2017", "modify");
        var f22 = FixtureSet.Load("2022", "modify");
        await using var cache = await CacheAsync(f22.Log, f17.Log);
        var lookup = new Func<long, CancellationToken, Task<TableSchema?>>((au, _) =>
            Task.FromResult(f17.Schemas.TryGetValue(au, out var s) ? s : f22.Schemas.GetValueOrDefault(au)));
        var service = new DecodingService(cache, lookup, false);

        var page = await new LogQuery(cache.Connection).GetPageAsync(0, 100000);

        Assert.Equal(page.Select(r => r.Lsn).OrderBy(l => l, StringComparer.Ordinal).ToList(), page.Select(r => r.Lsn).ToList());
        Assert.True(page.Count >= f17.Log.Count);
        foreach (var r in page)
            Assert.NotNull(await service.InspectAsync(r));
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task SameFixtureImportedTwice_GivesIdenticalInspections(string version)
    {
        var f = FixtureSet.Load(version, "modify");
        await using var once = await CacheAsync(f.Log);
        await using var twice = await CacheAsync(f.Log, f.Log);
        var a = new DecodingService(once, Lookup(f), false);
        var b = new DecodingService(twice, Lookup(f), false);

        foreach (var r in f.Log.Where(r => r.Operation is "LOP_INSERT_ROWS" or "LOP_DELETE_ROWS" or "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS"))
        {
            var x = await a.InspectAsync(r);
            var y = await b.InspectAsync(r);
            Assert.Equal(Values(x.Row), Values(y.Row));
            Assert.Equal(Values(x.Before), Values(y.Before));
            Assert.Equal(Values(x.After), Values(y.After));
            Assert.Equal(x.ReplayStatus, y.ReplayStatus);
            Assert.Equal(x.Outcome, y.Outcome);
            Assert.Equal(x.Warning, y.Warning);
            Assert.Equal(x.Changes.Count, y.Changes.Count);
        }
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task Modify_WithATruncatedKey_ReportsKeyNotDecodable(string version)
    {
        var f = FixtureSet.Load(version, "modify");
        var mod = Modifies(f.ForTable("dbo.t").Records)[0];
        Assert.NotNull(mod.RowLogContents2);
        var broken = mod with { RowLogContents2 = mod.RowLogContents2![..1] };
        await using var cache = await CacheAsync(f.Log);

        var x = await new DecodingService(cache, Lookup(f), true).InspectAsync(broken);

        Assert.Equal("key not decodable", x.Warning);
        Assert.Null(x.Before);
        Assert.Null(x.After);
        Assert.Null(x.ReplayStatus);
        Assert.False(x.CanRebuildFromCurrentRow);
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task SelectedCompensationModify_IsNotReplayed(string version)
    {
        var f = FixtureSet.Load(version, "modify");
        var (_, records) = f.ForTable("dbo.t");
        var comp = records.First(r => r.Operation is "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS" && r.IsCompensation && r.Context is "LCX_CLUSTERED" or "LCX_HEAP");
        await using var cache = await CacheAsync(f.Log);

        var x = await new DecodingService(cache, Lookup(f), true).InspectAsync(comp);

        Assert.Null(x.Before);
        Assert.Null(x.After);
        Assert.Null(x.Row);
        Assert.Null(x.ReplayStatus);
        Assert.Null(x.Warning);
        Assert.False(x.CanRebuildFromCurrentRow);
        Assert.All(x.Changes, c => Assert.True(c.Changed));
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task FragmentOnASharedBitByte_NamesOnlyTheBitThatChanged(string version)
    {
        // modify.sql : d passe de 0 à 1 (c reste à 1) dans l'octet de bits que c et d partagent.
        var f = FixtureSet.Load(version, "modify");
        var (_, records) = f.ForTable("dbo.t");
        var mods = Modifies(records);
        // Sans l'insert ni le delete : pas d'ancre, donc le fragment seul.
        var anchors = records.Where(r => r.Operation is "LOP_INSERT_ROWS" or "LOP_DELETE_ROWS").Select(r => r.Lsn).ToHashSet();
        await using var cache = await CacheAsync(f.Log.Where(r => !anchors.Contains(r.Lsn)).ToList());

        var x = await new DecodingService(cache, Lookup(f), false).InspectAsync(mods[2]);

        Assert.Equal(NoImage, x.ReplayStatus);
        var change = Assert.Single(x.Changes);
        Assert.Equal(new ColumnChange("d", new NotedValue(false, true, FromFragment), new NotedValue(true, true, FromFragment), true), change);
    }

    // ---- Lignes de propagation du service (revue de mutation) ----

    [Fact]
    public async Task RolledBackTransactionThatUndidASavepoint_IsNotReplayed()
    {
        // BEGIN TRAN; a = 30; SAVE TRAN s; a = 31; ROLLBACK TRAN s; b = 'z'; ROLLBACK : b = 'z' suit la compensation du point de sauvegarde.
        var f = FixtureSet.Load("2022", "rollback_savepoint_plain");
        var mods = Modifies(f.ForTable("dbo.p").Records);
        await using var cache = await CacheAsync(f.Log);

        var x = await new DecodingService(cache, Lookup(f), false).InspectAsync(mods[2]);

        Assert.Equal(TxOutcome.RolledBack, x.Outcome);
        Assert.Equal("partial rollback before the selected record, not replayed", x.ReplayStatus);
        Assert.Null(x.After);
    }

    [Fact]
    public void HeapLife_IsNotBoundedByAnotherTransactionThatRolledBack()
    {
        static LogRecord R(int n, string operation, string tx) => new($"00000001:00000001:{n:X4}", operation, "LCX_HEAP", tx, "dbo.h", null, null);
        // Au même slot : un delete puis un insert d'une transaction X annulée encadrent le modify choisi ; leur effet net est nul.
        var all = new[] { R(1, "LOP_INSERT_ROWS", "A"), R(2, "LOP_DELETE_ROWS", "X"), R(3, "LOP_MODIFY_ROW", "B"), R(4, "LOP_INSERT_ROWS", "X"), R(5, "LOP_DELETE_ROWS", "C") };
        var outcomes = new Dictionary<string, TxOutcome>
        {
            ["A"] = TxOutcome.Committed, ["B"] = TxOutcome.Committed, ["C"] = TxOutcome.Committed, ["X"] = TxOutcome.RolledBack,
        };

        var life = DecodingService.HeapLife(all, all[2], outcomes);

        Assert.Equal(all.Select(r => r.Lsn), life.Select(r => r.Lsn));
    }

    [Fact]
    public async Task AChainThroughATransactionOfUnknownOutcome_SaysSo()
    {
        var f = FixtureSet.Load("2022", "modify");
        var mods = Modifies(f.ForTable("dbo.t").Records);
        var tx = mods[0].TransactionId;
        // Sans son commit, la transaction du premier modify n'a pas d'issue dans le journal.
        await using var cache = await CacheAsync(f.Log.Where(r => !(r.TransactionId == tx && r.Operation == "LOP_COMMIT_XACT")).ToList());

        var x = await new DecodingService(cache, Lookup(f), false).InspectAsync(mods[0]);

        Assert.Equal(TxOutcome.Unknown, x.Outcome);
        Assert.StartsWith("rebuilt", x.ReplayStatus);
        Assert.True(x.IncludesUnknownOutcome);
    }

    [Fact]
    public async Task ALowercaseLsn_IsNormalizedOnEntry()
    {
        var f = FixtureSet.Load("2022", "modify");
        var modify = Modifies(f.ForTable("dbo.t").Records)[0];
        var lower = modify with { Lsn = modify.Lsn.ToLowerInvariant() };
        Assert.NotEqual(modify.Lsn, lower.Lsn);
        var h = FixtureSet.Load("2022", "heap");
        var heapUpdate = Modifies(h.ForTable("dbo.h").Records).First();
        await using var cache = await CacheAsync(f.Log);
        await using var heapCache = await CacheAsync(h.Log);

        var x = await new DecodingService(cache, Lookup(f), false).InspectAsync(lower);
        var chain = await new DecodingService(heapCache, Lookup(h), false).GetRowChainAsync(heapUpdate with { Lsn = heapUpdate.Lsn.ToLowerInvariant() });

        Assert.Equal(ExpectedT[0], Values(x.After));
        Assert.Contains(chain, r => r.Lsn == heapUpdate.Lsn);
    }

    // ---- Fragment seul (spec03 §6.1) : sans ligne complète, le bitmap de NULL est inconnu ----

    private const string FromFragment = "from the fragment alone, NULL state unknown";

    // spec03 §2 : t(id int, a int, b varchar(20), c bit, d bit), c et d dans l'octet 12.
    private static readonly TableSchema T = new(1, "dbo", "t", 1, 1, new[]
    {
        Decoding.TableSchemaTests.Col(1, "id", 4, 1, 56, 4, keyOrdinal: 1), Decoding.TableSchemaTests.Col(2, "a", 8, 2, 56, 4),
        Decoding.TableSchemaTests.Col(3, "b", -1, 3, 167, 20) with { CodePage = 1252 },
        Decoding.TableSchemaTests.Col(4, "c", 12, 4, 104, 1, bitPos: 0), Decoding.TableSchemaTests.Col(5, "d", 12, 5, 104, 1, bitPos: 1),
    });

    private static LogRecord ModifyRow(int offset, string before, string after) =>
        new("00000001:00000001:0001", "LOP_MODIFY_ROW", "LCX_CLUSTERED", "0000:00000001", null, Convert.FromHexString(before), Convert.FromHexString(after))
        { OffsetInRow = offset };

    [Fact]
    public void Fragment_CoveringExactlyAColumn_GivesItsValues_WithTheirNote()
    {
        var a = Assert.Single(DecodingService.FragmentChanges(ModifyRow(8, "0A000000", "0B000000"), T));

        Assert.Equal(new ColumnChange("a", new NotedValue(10, true, FromFragment), new NotedValue(11, true, FromFragment), true), a);
    }

    [Theory]
    [InlineData(8, "0A000000", "0B0000000000")]   // fragment qui grandit sur un int
    [InlineData(12, "01", "0300")]                 // fragment qui grandit sur l'octet de bits
    [InlineData(12, "0102", "03")]                 // fragment qui rétrécit sur l'octet de bits
    public void Fragment_NotCoveringExactlyAColumnOnBothSides_NamesItWithoutValues(int offset, string before, string after)
    {
        var changes = DecodingService.FragmentChanges(ModifyRow(offset, before, after), T);

        Assert.NotEmpty(changes);
        Assert.All(changes, c => { Assert.Null(c.Before); Assert.Null(c.After); });
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task PartlyRolledBackModify_NeverOffersTheCurrentRow(string version)
    {
        var f = FixtureSet.Load(version, "modify");
        var (_, records) = f.ForTable("dbo.t");
        var mods = Modifies(records);
        var anchors = records.Where(r => r.Operation is "LOP_INSERT_ROWS" or "LOP_DELETE_ROWS").Select(r => r.Lsn).ToHashSet();
        // Avec et sans ancre dans le cache : dans les deux cas la chaîne s'arrête sur la transaction partiellement annulée.
        foreach (var log in new[] { f.Log, f.Log.Where(r => !anchors.Contains(r.Lsn)).ToList() })
        {
            await using var cache = await CacheAsync(log);
            var service = new DecodingService(cache, Lookup(f), true);
            foreach (var m in mods.Skip(7))
            {
                var x = await service.InspectAsync(m);
                Assert.Equal(TxOutcome.PartlyRolledBack, x.Outcome);
                Assert.False(x.CanRebuildFromCurrentRow, $"{version} {m.Lsn}: {x.ReplayStatus}");
            }
        }
    }
}
