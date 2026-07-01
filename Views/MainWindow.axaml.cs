using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SqlLogExplorer.Models;
using SqlLogExplorer.ViewModels;

namespace SqlLogExplorer.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private async void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select one or more transaction-log backup files",
            AllowMultiple = true, // une chaîne de .trn (spec §3.2) ; l'ordre n'affecte pas les agrégats.
            FileTypeFilter =
            [
                // MVP : .trn uniquement (les .ldf sont post-MVP, cf. spec §1.2).
                new FilePickerFileType("SQL Server transaction-log backup") { Patterns = ["*.trn"] },
            ],
        });

        if (files.Count > 0 && DataContext is MainWindowViewModel vm)
        {
            var paths = files
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrEmpty(p))
                .Cast<string>()
                .ToList();
            if (paths.Count > 0)
                await vm.LoadOfflineCommand.ExecuteAsync(paths);
        }
    }

    private async void OnConnectLive(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var dialog = new Views.ConnectionDialog { DataContext = new ConnectionDialogViewModel() };
        var result = await dialog.ShowDialog<(string ConnectionString, LsnRange? Range)?>(this);
        if (result is { } r && !string.IsNullOrWhiteSpace(r.ConnectionString))
            await vm.LoadLiveAsync(r.ConnectionString, r.Range);
    }

    // Libère le cache SQLite (connexion + fichier temporaire) à la fermeture de la fenêtre.
    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is MainWindowViewModel vm)
            await vm.DisposeAsync();
    }
}