using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlLogExplorer.Backends;
using SqlLogExplorer.Data;
using SqlLogExplorer.Decoding;
using SqlLogExplorer.Models;
using SqlLogExplorer.Services;

namespace SqlLogExplorer.ViewModels;

/// <summary>Source de schéma d'une chaîne de connexion : la recherche par unité d'allocation et le contrôle des droits (spec03 §4).</summary>
internal sealed record SchemaSource(Func<long, CancellationToken, Task<TableSchema?>> Lookup, Func<CancellationToken, Task<bool>> IsPrivileged);

public partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private long _importedCount;
    [ObservableProperty] private string? _statusMessage = "Select a .trn file or enter a Live DB connection.";

    [ObservableProperty] private RecordsViewModel? _records;
    [ObservableProperty] private InspectorViewModel? _inspector;
    [ObservableProperty] private bool _canConnectSchemaSource;

    private readonly Func<string, SchemaSource> _schemaSource;
    private LogCache? _cache;
    private string? _liveConnectionString;   // la base analysée en direct : source du schéma et de la ligne courante
    private SchemaSource? _offlineSchemaSource;   // hors ligne, la source connectée : un nouvel import la garde
    private CancellationTokenSource? _cts;

    public MainWindowViewModel() : this(cs =>
    {
        var provider = new SchemaProvider(cs);
        return new SchemaSource(provider.GetAsync, provider.IsPrivilegedAsync);
    })
    { }

    /// <summary>Les tests injectent ici une recherche de schéma servie par les fixtures.</summary>
    internal MainWindowViewModel(Func<string, SchemaSource> schemaSource) => _schemaSource = schemaSource;

    public StatisticsViewModel? Statistics { get; private set; }

    /// <summary>L'inspection de la dernière sélection, que les tests attendent.</summary>
    internal Task Selection { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private Task LoadOfflineAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths is null || filePaths.Count == 0) return Task.CompletedTask;
        return ExecuteLoadAsync(filePaths, new LocalDbBackend(), null);
    }

    /// <summary>Lance une analyse Live depuis une chaîne de connexion et une fenêtre LSN optionnelle
    /// (fournies par ConnectionDialog, spec §3.1). Méthode publique appelée après la fermeture du dialog.</summary>
    public Task LoadLiveAsync(string connectionString, LsnRange? range = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return Task.CompletedTask;
        return ExecuteLoadAsync(new[] { connectionString }, new LiveDatabaseBackend(range), connectionString);
    }

    /// <summary>Annule l'import en cours (bouton Cancel, spec §3.4).</summary>
    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task ExecuteLoadAsync(IReadOnlyList<string> targets, ILogParserBackend backend, string? liveConnectionString)
    {
        if (IsLoading) return; // pas d'imports concurrents.
        IsLoading = true;
        ImportedCount = 0;
        StatusMessage = "Analyzing…";

        // Libère le cache précédent (ferme la connexion + supprime le fichier temporaire).
        await DisposeCacheAsync();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db");
            var cache = await LogCache.CreateAsync(dbPath, ct);
            _cache = cache;
            var service = new LogImportService(backend, cache);
            var progress = new Progress<long>(n => ImportedCount = n);

            // fn_dump_dblog / fn_dblog + insertions SQLite sont bloquants (spec §3.4) :
            // on exécute l'import hors du thread UI pour garder l'interface fluide.
            var total = await Task.Run(() => service.ImportAsync(targets, progress, ct), ct);

            // Retour sur le thread UI (aucun ConfigureAwait(false)) : maj des ObservableCollection sûre.
            await ShowAsync(cache, liveConnectionString);

            StatusMessage = $"{total} records imported.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Analysis cancelled.";
            await DisposeCacheAsync(); // cache partiel inutilisable : on le supprime.
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed: {ex.Message}";
            await DisposeCacheAsync();
        }
        finally
        {
            IsLoading = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Après un import : statistiques, grille et inspecteur sur le cache. En direct, le schéma et la ligne courante
    /// viennent de la base analysée ; hors ligne, pas de schéma tant qu'aucune source n'est connectée (spec03 §9).</summary>
    internal async Task ShowAsync(LogCache cache, string? liveConnectionString)
    {
        _cache = cache;
        _liveConnectionString = liveConnectionString;

        Statistics = new StatisticsViewModel(new LogQuery(cache.Connection));
        await Statistics.RefreshAsync();
        OnPropertyChanged(nameof(Statistics));

        if (liveConnectionString is not null) _offlineSchemaSource = null;
        Inspector = liveConnectionString is null
            ? new InspectorViewModel(new DecodingService(cache, _offlineSchemaSource?.Lookup, isLive: false), null)
            : new InspectorViewModel(new DecodingService(cache, _schemaSource(liveConnectionString).Lookup, isLive: true),
                new CurrentRowReader(liveConnectionString));
        CanConnectSchemaSource = liveConnectionString is null;

        if (Records is not null) Records.PropertyChanged -= OnRecordsChanged;
        Records = new RecordsViewModel(new LogQuery(cache.Connection));
        Records.PropertyChanged += OnRecordsChanged;
        await Records.LoadAsync();
    }

    private void OnRecordsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecordsViewModel.SelectedRecord) && Inspector is not null)
            Selection = Inspector.InspectAsync(Records?.SelectedRecord);   // InspectAsync ne lève pas (spec03 §8)
    }

    /// <summary>Hors ligne : connecte une base pour son catalogue (spec01 §5.1). Un DecodingService fixe sa recherche de schéma
    /// à la construction : on le remplace, avec l'inspecteur, puis on inspecte de nouveau la sélection.</summary>
    public async Task ConnectSchemaSourceAsync(string connectionString)
    {
        // En direct, la base analysée sert déjà le schéma.
        if (_cache is null || _liveConnectionString is not null || string.IsNullOrWhiteSpace(connectionString)) return;
        var source = _schemaSource(connectionString);
        bool privileged;
        try { privileged = await source.IsPrivileged(CancellationToken.None); }
        catch (Exception ex)
        {
            StatusMessage = $"Schema connection failed: {ex.Message}";
            return;
        }

        _offlineSchemaSource = source;
        Inspector = new InspectorViewModel(new DecodingService(_cache, source.Lookup, isLive: false), null);
        StatusMessage = privileged
            ? "Schema source connected."
            : "Warning: the login is neither sysadmin nor db_owner of this database; the catalog is filtered and some tables may not decode.";
        Selection = Inspector.InspectAsync(Records?.SelectedRecord);
        await Selection;
    }

    private async Task DisposeCacheAsync()
    {
        // La grille et l'inspecteur lisent le cache : ils partent avec lui.
        if (Records is not null) Records.PropertyChanged -= OnRecordsChanged;
        Records = null;
        Inspector = null;
        CanConnectSchemaSource = false;
        _liveConnectionString = null;
        if (_cache is not null)
        {
            await _cache.DisposeAsync();
            _cache = null;
        }
    }

    public async ValueTask DisposeAsync() => await DisposeCacheAsync();
}
