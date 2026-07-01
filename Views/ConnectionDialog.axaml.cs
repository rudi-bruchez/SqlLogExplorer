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
        if (DataContext is ConnectionDialogViewModel vm)
        {
            try
            {
                vm.StatusMessage = "Connecting to resolve LSN range...";
                
                // Retrieve the LsnRange
                var range = await vm.ResolveRangeAsync();
                
                // Close and return the connection string and LsnRange
                Close((ConnectionString: vm.BuildConnectionString(), Range: range));
            }
            catch (Exception ex)
            {
                vm.StatusMessage = $"Failed to resolve LSN range: {ex.Message}";
            }
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
