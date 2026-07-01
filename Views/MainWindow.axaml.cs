using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

    // MVP intermédiaire : chaîne de connexion brute. Remplacé par ConnectionDialog en Task 18.
    private async void OnAnalyzeLive(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            await vm.LoadLiveAsync(LiveDbConnectionString.Text ?? string.Empty);
    }

    // Libère le cache SQLite (connexion + fichier temporaire) à la fermeture de la fenêtre.
    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is MainWindowViewModel vm)
            await vm.DisposeAsync();
    }
}