using Avalonia.Controls;
using Avalonia.Interactivity;
using SqlLogExplorer.Models;
using SqlLogExplorer.ViewModels;
using System;

namespace SqlLogExplorer.Views;

public partial class ConnectionDialog : Window
{
    public ConnectionDialog()
    {
        InitializeComponent();
    }

    private async void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            btn.IsEnabled = false;
        }
        
        try
        {
            if (DataContext is ConnectionDialogViewModel vm)
            {
                vm.StatusMessage = "Connecting to resolve LSN range...";
                
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                // Retrieve the LsnRange
                var range = await vm.ResolveRangeAsync(cts.Token);
                
                // Close and return the connection string and LsnRange
                Close((ConnectionString: vm.BuildConnectionString(), Range: range));
            }
        }
        catch (OperationCanceledException)
        {
            if (DataContext is ConnectionDialogViewModel vm)
            {
                vm.StatusMessage = "Failed to resolve LSN range: Connection timed out.";
            }
        }
        catch (Exception ex)
        {
            if (DataContext is ConnectionDialogViewModel vm)
            {
                vm.StatusMessage = $"Failed to resolve LSN range: {ex.Message}";
            }
        }
        finally
        {
            if (sender is Button b)
            {
                b.IsEnabled = true;
            }
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
