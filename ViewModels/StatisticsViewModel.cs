using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using SqlLogExplorer.Data;
using SqlLogExplorer.Models;
using SqlLogExplorer.Services;

namespace SqlLogExplorer.ViewModels;

/// <summary>Vue Quantification (spec §4.3 / §6.2) : agrégats par type et par objet.</summary>
public sealed class StatisticsViewModel : ViewModelBase
{
    private readonly LogQuery _query;

    public StatisticsViewModel(LogQuery query) => _query = query;

    public ObservableCollection<OperationCount> ByOperation { get; } = new();
    public ObservableCollection<ObjectCount> ByObject { get; } = new();

    // Pas nommée IndexMaintenance : le nom masquerait la classe statique du même nom dans le view model.
    public ObservableCollection<IndexMaintenanceRow> MaintenanceRows { get; } = new();

    private string _maintenanceSummary = "";
    public string MaintenanceSummary
    {
        get => _maintenanceSummary;
        private set => SetProperty(ref _maintenanceSummary, value);
    }

    public async Task RefreshAsync(LogFilter? filter = null)
    {
        var byOp = await _query.CountByOperationAsync(filter);
        var byObj = await _query.CountByObjectAsync(filter);

        ByOperation.Clear();
        foreach (var x in byOp) ByOperation.Add(x);

        ByObject.Clear();
        foreach (var x in byObj) ByObject.Add(x);

        // La maintenance d'index ignore le filtre : elle concerne tout l'import (spec04 §6).
        MaintenanceRows.Clear();
        try
        {
            // Hors du thread UI : les appels asynchrones de Microsoft.Data.Sqlite sont synchrones, et l'analyse lit tout le cache.
            // ConnectionGate sérialise chaque requête sur la connexion, celles des autres onglets comprises. Les collections
            // se remplissent après l'await, sur le contexte de l'appelant.
            var report = await Task.Run(() => IndexMaintenance.AnalyzeAsync(_query));
            double Share(long bytes) => report.TotalBytes == 0 ? 0 : 100.0 * bytes / report.TotalBytes;
            foreach (var op in report.Operations) MaintenanceRows.Add(new IndexMaintenanceRow(op, Share(op.Bytes)));
            MaintenanceSummary = $"Imported log record bytes: {IndexMaintenanceRow.FormatBytes(report.TotalBytes)}. " +
                $"Index maintenance: {IndexMaintenanceRow.FormatBytes(report.MaintenanceBytes)} " +
                $"({IndexMaintenanceRow.FormatShare(Share(report.MaintenanceBytes))}).";
        }
        catch (Exception ex)
        {
            // Un échec de cet onglet ne doit pas faire perdre l'import ni les autres onglets.
            MaintenanceRows.Clear();
            MaintenanceSummary = $"Index maintenance could not be computed: {ex.Message}";
        }
    }
}
