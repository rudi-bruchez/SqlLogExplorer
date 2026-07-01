using System.Collections.ObjectModel;
using System.Threading.Tasks;
using SqlLogExplorer.Data;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.ViewModels;

/// <summary>Vue Quantification (spec §4.3 / §6.2) : agrégats par type et par objet.</summary>
public sealed class StatisticsViewModel : ViewModelBase
{
    private readonly LogQuery _query;

    public StatisticsViewModel(LogQuery query) => _query = query;

    public ObservableCollection<OperationCount> ByOperation { get; } = new();
    public ObservableCollection<ObjectCount> ByObject { get; } = new();

    public async Task RefreshAsync(LogFilter? filter = null)
    {
        var byOp = await _query.CountByOperationAsync(filter);
        var byObj = await _query.CountByObjectAsync(filter);

        ByOperation.Clear();
        foreach (var x in byOp) ByOperation.Add(x);

        ByObject.Clear();
        foreach (var x in byObj) ByObject.Add(x);
    }
}
