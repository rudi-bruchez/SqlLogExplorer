using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlLogExplorer.Backends;
using SqlLogExplorer.Data;
using SqlLogExplorer.Models;
using SqlLogExplorer.Services;

namespace SqlLogExplorer.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private long _importedCount;
    [ObservableProperty] private string? _statusMessage = "Select a .trn file or enter a Live DB connection.";

    private LogCache? _cache;
    private CancellationTokenSource? _cts;

    public StatisticsViewModel? Statistics { get; private set; }

    [RelayCommand]
    private Task LoadOfflineAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths is null || filePaths.Count == 0) return Task.CompletedTask;
        return ExecuteLoadAsync(filePaths, new LocalDbBackend());
    }

    /// <summary>Lance une analyse Live depuis une chaîne de connexion et une fenêtre LSN optionnelle
    /// (fournies par ConnectionDialog, spec §3.1). Méthode publique appelée après la fermeture du dialog.</summary>
    public Task LoadLiveAsync(string connectionString, LsnRange? range = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return Task.CompletedTask;
        return ExecuteLoadAsync(new[] { connectionString }, new LiveDatabaseBackend(range));
    }

    /// <summary>Annule l'import en cours (bouton Cancel, spec §3.4).</summary>
    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task ExecuteLoadAsync(IReadOnlyList<string> targets, ILogParserBackend backend)
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
            _cache = await LogCache.CreateAsync(dbPath, ct);
            var service = new LogImportService(backend, _cache);
            var progress = new Progress<long>(n => ImportedCount = n);

            // fn_dump_dblog / fn_dblog + insertions SQLite sont bloquants (spec §3.4) :
            // on exécute l'import hors du thread UI pour garder l'interface fluide.
            var total = await Task.Run(() => service.ImportAsync(targets, progress, ct), ct);

            // Retour sur le thread UI (aucun ConfigureAwait(false)) : maj des ObservableCollection sûre.
            Statistics = new StatisticsViewModel(new LogQuery(_cache.Connection));
            await Statistics.RefreshAsync();
            OnPropertyChanged(nameof(Statistics));

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

    private async Task DisposeCacheAsync()
    {
        if (_cache is not null)
        {
            await _cache.DisposeAsync();
            _cache = null;
        }
    }

    public async ValueTask DisposeAsync() => await DisposeCacheAsync();
}
