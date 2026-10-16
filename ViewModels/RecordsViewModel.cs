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

    /// <summary>Transactions du cache, restreintes à celles qui touchent <see cref="ObjectFilter"/> s'il est renseigné.</summary>
    public ObservableCollection<TransactionSummary> Transactions { get; } = new();

    /// <summary>Partie du nom d'objet (AllocUnitName) cherchée ; appliquée par <see cref="SearchCommand"/>.</summary>
    [ObservableProperty] private string? _objectFilter;

    /// <summary>Page cherchée, 1:336 (DBCC PAGE) ou 0001:00000150 (fn_dblog) ; appliquée par <see cref="SearchCommand"/>.</summary>
    [ObservableProperty] private string? _pageFilter;

    /// <summary>Saisie de page illisible à la dernière recherche ; la liste et la grille restent alors inchangées.</summary>
    [ObservableProperty] private string? _searchError;

    /// <summary>Le filtre de la liste et de la grille, tel qu'appliqué à la dernière recherche.</summary>
    private LogFilter _applied = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterDescription))]
    private TransactionSummary? _selectedTransaction;

    /// <summary>Le chargement lancé par le dernier changement de transaction, que les tests attendent.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    private bool _refreshing;

    public string FilterDescription =>
        SelectedTransaction is { } t ? $"Transaction {t.TransactionId}"
        : _applied.IsEmpty ? "All records"
        : "Records on " + string.Join(", ", new[]
            {
                _applied.AllocUnitName is { } o ? $"objects matching '{o}'" : null,
                _applied.PageId is { } p ? $"page {p}" : null,
            }.Where(s => s is not null));

    // Une transaction choisie montre toutes ses opérations, begin et commit compris, même hors de l'objet cherché.
    private LogFilter Filter => SelectedTransaction is { } t
        ? new LogFilter(TransactionId: t.TransactionId)
        : _applied;

    partial void OnSelectedTransactionChanged(TransactionSummary? value)
    {
        if (_refreshing) return;
        PageIndex = 0;
        Pending = LoadAsync();
    }

    /// <summary>Recharge la liste des transactions selon l'objet et la page cherchés, puis la première page de la grille.</summary>
    public async Task RefreshAsync()
    {
        string? page = null;
        if (!string.IsNullOrWhiteSpace(PageFilter) && (page = LogFilter.NormalizePageId(PageFilter)) is null)
        {
            SearchError = $"Page '{PageFilter.Trim()}' not understood: use file:page, as 1:336 or 0001:00000150.";
            return;
        }
        SearchError = null;
        _applied = new LogFilter(AllocUnitName: string.IsNullOrWhiteSpace(ObjectFilter) ? null : ObjectFilter.Trim(), PageId: page);
        var txs = await _query.GetTransactionsAsync(_applied);
        _refreshing = true;
        try
        {
            SelectedTransaction = null;
            Transactions.Clear();
            foreach (var t in txs) Transactions.Add(t);
        }
        finally { _refreshing = false; }
        OnPropertyChanged(nameof(FilterDescription));
        PageIndex = 0;
        await LoadAsync();
    }

    [RelayCommand]
    private Task SearchAsync() => RefreshAsync();

    [RelayCommand]
    private Task ShowAllAsync()
    {
        SelectedTransaction = null;
        return Pending;
    }

    /// <summary>Charge la page courante ; un enregistrement de plus que la page dit s'il en existe une suivante.</summary>
    public async Task LoadAsync()
    {
        var page = await _query.GetPageAsync(PageIndex * _pageSize, _pageSize + 1, Filter);
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
