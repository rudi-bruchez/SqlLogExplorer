using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.SqlClient;
using SqlLogExplorer.Backends;
using SqlLogExplorer.Models;
using System;
using System.Threading.Tasks;

namespace SqlLogExplorer.ViewModels;

/// <summary>Logique de la boîte de connexion façon SSMS (spec §3.1 / §6.1 Option A).</summary>
public partial class ConnectionDialogViewModel : ViewModelBase
{
    [ObservableProperty] private string _server = string.Empty;
    [ObservableProperty] private SqlAuthMode _auth = SqlAuthMode.Windows;

    partial void OnAuthChanged(SqlAuthMode value)
    {
        OnPropertyChanged(nameof(IsSqlLoginVisible));
    }

    public bool IsSqlLoginVisible => Auth == SqlAuthMode.SqlLogin;

    [ObservableProperty] private string? _userName;
    [ObservableProperty] private string? _password;
    [ObservableProperty] private EncryptMode _encrypt = EncryptMode.Mandatory;
    [ObservableProperty] private bool _trustServerCertificate = true;
    [ObservableProperty] private string? _selectedDatabase;
    [ObservableProperty] private DateTimeOffset? _startTime;
    [ObservableProperty] private DateTimeOffset? _endTime;
    [ObservableProperty] private TimeSpan? _startTimeOfDay;
    [ObservableProperty] private TimeSpan? _endTimeOfDay;
    [ObservableProperty] private string? _statusMessage;

    public ObservableCollection<string> Databases { get; } = new();
    public ObservableCollection<string> RecentServers { get; } = new();

    public SqlAuthMode[] AuthModes { get; } = new[] { SqlAuthMode.Windows, SqlAuthMode.SqlLogin };
    public EncryptMode[] EncryptModes { get; } = new[] { EncryptMode.Optional, EncryptMode.Mandatory };

    private ConnectionSettings ToSettings() => new(
        Server, Auth, UserName, Password, SelectedDatabase, Encrypt, TrustServerCertificate);

    public string BuildConnectionString() => SqlConnectionStringFactory.Build(ToSettings());

    /// <summary>Connecte au serveur (sans base précise) et liste les bases (spec §6.1 : dropdown peuplé après connexion).</summary>
    [RelayCommand]
    public async Task ListDatabasesAsync()
    {
        try
        {
            var probe = SqlConnectionStringFactory.Build(ToSettings() with { Database = null });
            await using var cn = new SqlConnection(probe);
            await cn.OpenAsync();
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sys.databases ORDER BY name;";
            await using var reader = await cmd.ExecuteReaderAsync();
            Databases.Clear();
            while (await reader.ReadAsync()) Databases.Add(reader.GetString(0));
            StatusMessage = $"{Databases.Count} databases found.";
        }
        catch (Exception ex) { StatusMessage = $"Connection failed: {ex.Message}"; }
    }

    public DateTime? GetResolvedStartTime()
    {
        if (StartTime is null) return null;
        var date = StartTime.Value.Date;
        if (StartTimeOfDay.HasValue) date = date.Add(StartTimeOfDay.Value);
        return date;
    }

    public DateTime? GetResolvedEndTime()
    {
        if (EndTime is null) return null;
        var date = EndTime.Value.Date;
        if (EndTimeOfDay.HasValue) date = date.Add(EndTimeOfDay.Value);
        return date;
    }

    /// <summary>Résout la fenêtre temporelle en plage LSN (null = log actif complet).</summary>
    public async Task<LsnRange?> ResolveRangeAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedDatabase))
            throw new InvalidOperationException("Please select a database first.");

        var resolvedStart = GetResolvedStartTime();
        var resolvedEnd = GetResolvedEndTime();
        if (resolvedStart is null && resolvedEnd is null) return null;
        await using var cn = new SqlConnection(BuildConnectionString());
        await cn.OpenAsync();
        return await LiveLsnResolver.ResolveAsync(cn, resolvedStart, resolvedEnd);
    }
}
