using SqlLogExplorer.Backends;
using SqlLogExplorer.Data;
using SqlLogExplorer.Decoding;
using SqlLogExplorer.Models;
using SqlLogExplorer.Services;
using SqlLogExplorer.ViewModels;

namespace SqlLogExplorer.Tests;

public class InspectorViewModelTests
{
    private static async Task<LogCache> CacheAsync(Fixture f)
    {
        var cache = await LogCache.CreateAsync(Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db"));
        await cache.InsertBatchAsync(f.Log);
        return cache;
    }

    private static Func<long, CancellationToken, Task<TableSchema?>> Lookup(Fixture f) =>
        (au, _) => Task.FromResult(f.Schemas.TryGetValue(au, out var s) ? s : null);

    private static LogRecord FirstModify(Fixture f) =>
        f.ForTable("dbo.t").Records.First(r => r.Operation is "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS" && !r.IsCompensation);

    private static byte[][] NonEmptyContents(LogRecord r) =>
        new[] { r.RowLogContents0, r.RowLogContents1, r.RowLogContents2, r.RowLogContents3, r.RowLogContents4, r.RowLogContents5 }
            .Where(b => b is { Length: > 0 }).Cast<byte[]>().ToArray();

    [Fact]
    public async Task Modify_FillsValuesWithTheChangedColumn_AndTheReplayStatus()
    {
        var f = FixtureSet.Load("2022", "modify");
        await using var cache = await CacheAsync(f);
        var vm = new InspectorViewModel(new DecodingService(cache, Lookup(f), isLive: false), null);
        var modify = FirstModify(f);

        await vm.InspectAsync(modify);

        Assert.Equal(5, vm.Values.Count);
        Assert.Equal("a", Assert.Single(vm.Values, v => v.Changed == true).Column);
        Assert.StartsWith("rebuilt", vm.ReplayStatus);
        Assert.Null(vm.SchemaMessage);
        Assert.False(vm.CanRebuildFromCurrentRow);
        Assert.Contains(modify.Operation, vm.Title);
        // Un modify n'a pas de contenu décodé : un morceau de hex brut par contenu non vide.
        var contents = NonEmptyContents(modify);
        Assert.Equal(contents.Length, vm.Bytes.Count);
        Assert.All(vm.Bytes, b => Assert.Null(b.Role));
        Assert.Equal(contents.Select(Hex), vm.Bytes.Select(b => b.Hex.Replace(" ", "")));
    }

    [Fact]
    public async Task Insert_BytesHaveOneChunkPerDecodedField()
    {
        var f = FixtureSet.Load("2022", "modify");
        await using var cache = await CacheAsync(f);
        var vm = new InspectorViewModel(new DecodingService(cache, Lookup(f), isLive: false), null);
        var (schema, records) = f.ForTable("dbo.t");
        var insert = records.First(r => r.Operation == "LOP_INSERT_ROWS");

        await vm.InspectAsync(insert);

        var fields = RowDecoder.Decode(insert.RowLogContents0, schema).Fields;
        var decoded = vm.Bytes.Where(b => b.ContentName == "RowLog Contents 0").ToList();
        Assert.Equal(fields.Count, decoded.Count);
        Assert.Equal(fields.Select(x => (FieldRole?)x.Role), decoded.Select(b => b.Role));
        Assert.Equal(fields.Select(x => x.Offset), decoded.Select(b => b.Offset));
        // Deux colonnes bit partagent un octet : deux champs au même offset, donc pas de concaténation, mais chaque morceau porte ses octets.
        Assert.Equal(fields.Select(x => Hex(insert.RowLogContents0![x.Offset..(x.Offset + x.Length)])), decoded.Select(b => b.Hex.Replace(" ", "")));
        Assert.Contains(decoded, b => b.Role == FieldRole.FixedColumn && b.Tooltip.Contains("column: a") && b.Tooltip.Contains("value: 10"));
        Assert.All(vm.Bytes.Except(decoded), b => Assert.Null(b.Role));
        Assert.Equal(NonEmptyContents(insert).Length - 1, vm.Bytes.Count - decoded.Count);
        Assert.Equal(5, vm.Values.Count);
        Assert.Null(vm.ReplayStatus);
    }

    [Fact]
    public async Task WithoutSchema_ValuesSayASchemaConnectionIsRequired_AndBytesKeepTheStructure()
    {
        var f = FixtureSet.Load("2022", "modify");
        await using var cache = await CacheAsync(f);
        var vm = new InspectorViewModel(new DecodingService(cache, null, isLive: false), null);
        var records = f.ForTable("dbo.t").Records;

        await vm.InspectAsync(FirstModify(f));

        Assert.Equal("a schema connection is required", vm.SchemaMessage);
        Assert.Empty(vm.Values);
        Assert.Null(vm.ReplayStatus);
        Assert.False(vm.CanRebuildFromCurrentRow);
        Assert.NotEmpty(vm.Bytes);

        await vm.InspectAsync(records.First(r => r.Operation == "LOP_INSERT_ROWS"));

        Assert.Equal("a schema connection is required", vm.SchemaMessage);
        Assert.Contains(vm.Bytes, b => b.Role == FieldRole.NullBitmap);
    }

    [Fact]
    public async Task InspectAsync_ShowsAFailureInsteadOfThrowing()
    {
        var f = FixtureSet.Load("2022", "modify");
        await using var cache = await CacheAsync(f);
        Func<long, CancellationToken, Task<TableSchema?>> failing = (_, _) => throw new InvalidOperationException("server unreachable");
        var vm = new InspectorViewModel(new DecodingService(cache, failing, isLive: false), null);
        var modify = FirstModify(f);

        await vm.InspectAsync(modify);

        Assert.Contains("server unreachable", vm.Error);
        Assert.Equal(NonEmptyContents(modify).Length, vm.Bytes.Count);
        Assert.False(vm.CanRebuildFromCurrentRow);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConnectSchemaSource_ReplacesTheInspector_AndTheSameRecordDecodes(bool privileged)
    {
        var f = FixtureSet.Load("2022", "modify");
        var cache = await CacheAsync(f);
        string? connected = null;
        await using var vm = new MainWindowViewModel(cs =>
        {
            connected = cs;
            return new SchemaSource(Lookup(f), _ => Task.FromResult(privileged));
        });
        await vm.ShowAsync(cache, liveConnectionString: null);
        var modify = FirstModify(f);

        Assert.True(vm.CanConnectSchemaSource);
        vm.Records!.SelectedRecord = vm.Records.Records.Single(r => r.Lsn == modify.Lsn);
        await vm.Selection;
        Assert.Equal("a schema connection is required", vm.Inspector!.SchemaMessage);
        Assert.False(vm.Inspector.CanRebuildFromCurrentRow);
        var before = vm.Inspector;

        await vm.ConnectSchemaSourceAsync("Server=offline;Database=db");

        Assert.Equal("Server=offline;Database=db", connected);
        Assert.NotSame(before, vm.Inspector);
        Assert.Null(vm.Inspector!.SchemaMessage);
        Assert.StartsWith("rebuilt", vm.Inspector.ReplayStatus);
        Assert.Equal("a", Assert.Single(vm.Inspector.Values, v => v.Changed == true).Column);
        Assert.False(vm.Inspector.CanRebuildFromCurrentRow);
        Assert.Equal(privileged, !vm.StatusMessage!.StartsWith("Warning"));
    }

    // ---- Valeur inconnue et NULL SQL : une cellule vide n'est pas un NULL (spec03 §6.1, §9) ----

    [Fact]
    public async Task FragmentOnlyModify_LeavesUnknownValuesBlank()
    {
        var f = FixtureSet.Load("2022", "modify");
        await using var cache = await CacheAsync(f);
        var vm = new InspectorViewModel(new DecodingService(cache, Lookup(f), isLive: false), null);
        // Compensation : le fragment n'a pas d'avant, la colonne a est nommée sans valeurs.
        var compensation = f.Log.Single(r => r.Lsn == "0000002A:000001E8:0003");

        await vm.InspectAsync(compensation);

        var a = Assert.Single(vm.Values, v => v.Column == "a");
        Assert.True(a.Changed);
        Assert.Equal("", InspectorViewModel.ValueText(a.Before));
        Assert.Equal("", InspectorViewModel.ValueText(a.After));
    }

    [Fact]
    public async Task Insert_LeavesBeforeBlank_AndARebuiltNullShowsNull()
    {
        var f = FixtureSet.Load("2022", "modify");
        await using var cache = await CacheAsync(f);
        var vm = new InspectorViewModel(new DecodingService(cache, Lookup(f), isLive: false), null);
        var records = f.ForTable("dbo.t").Records;

        await vm.InspectAsync(records.First(r => r.Operation == "LOP_INSERT_ROWS"));

        Assert.All(vm.Values, v => Assert.Equal("", InspectorViewModel.ValueText(v.Before)));
        Assert.Equal("10", InspectorViewModel.ValueText(vm.Values.Single(v => v.Column == "a").After));

        // modify.sql : b = NULL, cinquième modify de dbo.t, rejoué depuis l'insert.
        var mods = records.Where(r => r.Operation is "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS" && !r.IsCompensation).ToList();
        await vm.InspectAsync(mods[4]);

        var b = vm.Values.Single(v => v.Column == "b");
        Assert.StartsWith("rebuilt", vm.ReplayStatus);
        Assert.Equal("abcdef", InspectorViewModel.ValueText(b.Before));
        Assert.Equal("NULL", InspectorViewModel.ValueText(b.After));
    }

    [Fact]
    public async Task ASlowInspection_DoesNotOverwriteANewerSelection()
    {
        var f = FixtureSet.Load("2022", "modify");
        await using var cache = await CacheAsync(f);
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var calls = 0;
        Func<long, CancellationToken, Task<TableSchema?>> lookup = async (au, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { started.SetResult(); await gate.Task; }   // seule la première recherche attend
            return f.Schemas.TryGetValue(au, out var s) ? s : null;
        };
        var vm = new InspectorViewModel(new DecodingService(cache, lookup, isLive: false), null);
        var insert = f.ForTable("dbo.t").Records.First(r => r.Operation == "LOP_INSERT_ROWS");

        var slow = vm.InspectAsync(FirstModify(f));
        // L'inspection tourne hors du thread appelant : sans cette attente, celle de l'insert pouvait faire la première recherche,
        // attendre la barrière qui ne s'ouvre qu'après elle, et bloquer le test (blocages mesurés pendant la revue de mutation).
        await started.Task;
        await vm.InspectAsync(insert);
        gate.SetResult();
        await slow;

        Assert.Equal($"LOP_INSERT_ROWS at {insert.Lsn}, transaction committed", vm.Title);
        Assert.Null(vm.ReplayStatus);
        Assert.Contains(vm.Bytes, b => b.Role is not null);
    }

    [Fact]
    public async Task EmptyImage_HasNoNotePointingAtBytes()
    {
        var f = FixtureSet.Load("2022", "modify");
        await using var cache = await CacheAsync(f);
        var vm = new InspectorViewModel(new DecodingService(cache, null, isLive: false), null);

        await vm.InspectAsync(f.Log.Single(r => r.Lsn == "0000002A:00000260:0003"));

        Assert.Empty(vm.Bytes);
        Assert.True(vm.Notes is null || !vm.Notes.Contains("Bytes"), vm.Notes);
    }

    // ---- Utilisabilité (revue finale, mineures) ----

    [Fact]
    public async Task Inspection_DoesNotRunOnTheSelectingThread()
    {
        // Les appels asynchrones de SQLite se terminent de façon synchrone : sans Task.Run, une recherche lente fige le thread qui sélectionne.
        var f = FixtureSet.Load("2022", "modify");
        await using var cache = await CacheAsync(f);
        using var gate = new ManualResetEventSlim();
        Func<long, CancellationToken, Task<TableSchema?>> blocking = (au, _) =>
        {
            gate.Wait(TimeSpan.FromSeconds(2));
            return Task.FromResult(f.Schemas.TryGetValue(au, out var s) ? s : null);
        };
        var vm = new InspectorViewModel(new DecodingService(cache, blocking, isLive: false), null);

        var inspection = vm.InspectAsync(FirstModify(f));
        var returnedAtOnce = !inspection.IsCompleted;
        gate.Set();
        await inspection;

        Assert.True(returnedAtOnce);
        Assert.StartsWith("rebuilt", vm.ReplayStatus);
    }

    [Fact]
    public async Task AnOfflineReimport_KeepsTheSchemaSource()
    {
        var f = FixtureSet.Load("2022", "modify");
        await using var vm = new MainWindowViewModel(_ => new SchemaSource(Lookup(f), _ => Task.FromResult(true)));
        await vm.ShowAsync(await CacheAsync(f), liveConnectionString: null);
        await vm.ConnectSchemaSourceAsync("Server=offline;Database=db");

        await vm.ShowAsync(await CacheAsync(f), liveConnectionString: null);   // ce que fait un nouvel import hors ligne
        var modify = FirstModify(f);
        vm.Records!.SelectedRecord = vm.Records.Records.Single(r => r.Lsn == modify.Lsn);
        await vm.Selection;

        Assert.Null(vm.Inspector!.SchemaMessage);
        Assert.StartsWith("rebuilt", vm.Inspector.ReplayStatus);
        Assert.True(vm.CanConnectSchemaSource);
    }

    // ---- Lignes de propagation de l'inspecteur (revue de mutation) ----

    [Fact]
    public async Task AChainThroughATransactionOfUnknownOutcome_IsNoted()
    {
        var f = FixtureSet.Load("2022", "modify");
        var modify = FirstModify(f);
        var cache = await LogCache.CreateAsync(Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db"));
        await using var _ = cache;
        await cache.InsertBatchAsync(f.Log.Where(r => !(r.TransactionId == modify.TransactionId && r.Operation == "LOP_COMMIT_XACT")).ToList());
        var vm = new InspectorViewModel(new DecodingService(cache, Lookup(f), isLive: false), null);

        await vm.InspectAsync(modify);

        Assert.StartsWith("rebuilt", vm.ReplayStatus);
        Assert.Equal("includes changes of unknown outcome", vm.Notes);
    }

    // Un serveur qui refuse la connexion : le lecteur existe, mais rien ne peut y être lu.
    private static CurrentRowReader UnreachableReader() => new("Server=127.0.0.1,1;Connect Timeout=1;Encrypt=False;Database=db");

    [Fact]
    public async Task AReaderThatCannotTellItsDatabase_DoesNotOfferTheRebuild()
    {
        var f = FixtureSet.Load("2022", "schema_change");
        await using var cache = await CacheAsync(f);
        var service = new DecodingService(cache, Lookup(f), isLive: true);
        var update = f.ForTable("dbo.rb").Records.Single(r => r.Operation is "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS" && !r.IsCompensation);
        Assert.True((await service.InspectAsync(update)).CanRebuildFromCurrentRow);
        var vm = new InspectorViewModel(service, UnreachableReader());

        await vm.InspectAsync(update);

        Assert.False(vm.CanRebuildFromCurrentRow);
        Assert.Null(vm.Error);
    }

    [Fact]
    public async Task RebuildFromCurrentRow_RunsTheServiceAndShowsItsStatus()
    {
        // Un tas : le service refuse avant toute lecture du serveur, le lecteur injoignable n'est jamais appelé.
        var f = FixtureSet.Load("2022", "heap");
        await using var cache = await CacheAsync(f);
        var vm = new InspectorViewModel(new DecodingService(cache, Lookup(f), isLive: true), UnreachableReader());
        var update = f.ForTable("dbo.h").Records.First(r => r.Operation is "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS" && !r.IsCompensation);
        await vm.InspectAsync(update);
        Assert.StartsWith("rebuilt", vm.ReplayStatus);

        await vm.RebuildFromCurrentRowCommand.ExecuteAsync(null);

        Assert.Equal("not available for a heap", vm.ReplayStatus);
        Assert.Null(vm.Error);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);
}
