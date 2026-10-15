using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlLogExplorer.Data;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.ViewModels;

/// <summary>Grille des enregistrements du cache, paginée par LogQuery.GetPageAsync, dans l'ordre des LSN (spec03 §9).</summary>
public sealed partial class RecordsViewModel : ViewModelBase
{
    private readonly LogQuery _query;
    private readonly int _pageSize;

    public RecordsViewModel(LogQuery query, int pageSize = 200)
    {
        _query = query;
        _pageSize = pageSize;
    }

    public ObservableCollection<LogRecord> Records { get; } = new();

    [ObservableProperty] private LogRecord? _selectedRecord;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyPropertyChangedFor(nameof(PageNumber))]
    private int _pageIndex;

    /// <summary>Numéro affiché, à partir de 1.</summary>
    public int PageNumber => PageIndex + 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    private bool _hasNextPage;

    /// <summary>Charge la page courante ; un enregistrement de plus que la page dit s'il en existe une suivante.</summary>
    public async Task LoadAsync()
    {
        var page = await _query.GetPageAsync(PageIndex * _pageSize, _pageSize + 1);
        SelectedRecord = null;
        Records.Clear();
        foreach (var r in page.Take(_pageSize)) Records.Add(r);
        HasNextPage = page.Count > _pageSize;
    }

    [RelayCommand(CanExecute = nameof(HasNextPage))]
    private Task NextPageAsync()
    {
        PageIndex++;
        return LoadAsync();
    }

    private bool CanGoBack() => PageIndex > 0;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task PreviousPageAsync()
    {
        PageIndex--;
        return LoadAsync();
    }
}
