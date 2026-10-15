using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlLogExplorer.Backends;
using SqlLogExplorer.Decoding;
using SqlLogExplorer.Models;
using SqlLogExplorer.Services;

namespace SqlLogExplorer.ViewModels;

/// <summary>Un morceau du vidage hexadécimal : un champ décodé (Role non null) ou un contenu non décodé en hex brut (spec03 §9).</summary>
public sealed record ByteChunk(string ContentName, int Offset, string Hex, FieldRole? Role, string Tooltip);

/// <summary>Inspecteur de détail d'un enregistrement, onglets "Values" et "Bytes" (spec03 §9).
/// Ne lève jamais vers l'interface (spec03 §8) : un échec est affiché dans Error.</summary>
public sealed partial class InspectorViewModel : ViewModelBase
{
    private readonly DecodingService _service;
    private readonly CurrentRowReader? _reader;
    private bool? _readerAvailable;   // l'ADR de la base ne change pas pendant la session
    private LogRecord? _record;
    private int _generation;          // une sélection plus récente l'emporte sur une inspection encore en cours

    public InspectorViewModel(DecodingService service, CurrentRowReader? reader)
    {
        _service = service;
        _reader = reader;
    }

    [ObservableProperty] private string _title = "No record selected";
    [ObservableProperty] private string? _replayStatus;
    [ObservableProperty] private string? _schemaMessage;
    [ObservableProperty] private string? _notes;
    [ObservableProperty] private string? _error;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildFromCurrentRowCommand))]
    private bool _canRebuildFromCurrentRow;

    public ObservableCollection<ColumnChange> Values { get; } = new();
    public ObservableCollection<ByteChunk> Bytes { get; } = new();

    public async Task InspectAsync(LogRecord? record)
    {
        var generation = ++_generation;
        _record = record;
        Clear();
        if (record is null) return;
        Title = $"{record.Operation} at {record.Lsn}";
        FillBytes(record, null);
        try
        {
            var inspection = await _service.InspectAsync(record);
            var canRebuild = inspection.CanRebuildFromCurrentRow && await ReaderAvailableAsync();
            if (generation == _generation) Show(inspection, canRebuild);
        }
        catch (Exception ex)
        {
            if (generation == _generation) Error = $"Decoding failed: {ex.Message}";
        }
    }

    /// <summary>Ancre 3 (spec03 §7.2) : action explicite, jamais lancée à la sélection. L'échec d'une étape est affiché avec son message.</summary>
    [RelayCommand(CanExecute = nameof(CanRebuildFromCurrentRow))]
    private async Task RebuildFromCurrentRowAsync()
    {
        if (_record is not { } record || _reader is null) return;
        var generation = ++_generation;
        CanRebuildFromCurrentRow = false;
        Error = null;
        ReplayStatus = "reading the current row…";
        try
        {
            var inspection = await _service.RebuildFromCurrentRowAsync(record, _reader);
            if (generation == _generation) Show(inspection, false);
        }
        catch (Exception ex)
        {
            if (generation == _generation)
            {
                ReplayStatus = null;
                Error = $"Rebuild from the current row failed: {ex.Message}";
            }
        }
    }

    private async Task<bool> ReaderAvailableAsync()
    {
        if (_reader is null) return false;
        if (_readerAvailable is null)
        {
            try { _readerAvailable = await _reader.IsAvailableForDatabaseAsync(); }
            catch (Exception) { return false; }   // un serveur injoignable n'empêche pas l'inspection, il retire seulement le bouton
        }
        return _readerAvailable.Value;
    }

    private void Clear()
    {
        Title = "No record selected";
        ReplayStatus = SchemaMessage = Notes = Error = null;
        CanRebuildFromCurrentRow = false;
        Values.Clear();
        Bytes.Clear();
    }

    private void Show(Inspection x, bool canRebuild)
    {
        var record = x.Record;
        Title = x.Outcome is { } o ? $"{record.Operation} at {record.Lsn}, transaction {OutcomeText(o)}" : $"{record.Operation} at {record.Lsn}";
        ReplayStatus = x.ReplayStatus;
        SchemaMessage = x.SchemaMessage;
        Error = null;
        Notes = string.Join("; ", new[]
        {
            x.Label, x.Warning, x.IncludesUnknownOutcome ? "includes changes of unknown outcome" : null,
            x.Row?.HasProblem == true ? "the row image has problems, see Bytes" : null,
        }.Where(s => s is not null)) is { Length: > 0 } n ? n : null;
        CanRebuildFromCurrentRow = canRebuild;

        Values.Clear();
        // Un insert montre la ligne après, un delete la ligne avant ; un modify, ses colonnes avant et après.
        if (x.Row is { } row && x.SchemaMessage is null)
            foreach (var c in row.Columns)
                Values.Add(record.Operation == "LOP_DELETE_ROWS" ? new ColumnChange(c.Name, c.Value, null, false) : new ColumnChange(c.Name, null, c.Value, false));
        else
            foreach (var c in x.Changes) Values.Add(c);

        Bytes.Clear();
        FillBytes(record, x.Row);
    }

    /// <summary>Chaque contenu non vide : par champ quand c'est l'image décodée (RowLog Contents 0 d'un insert ou d'un delete), sinon en hex brut.</summary>
    private void FillBytes(LogRecord r, DecodedRow? row)
    {
        var contents = new[] { r.RowLogContents0, r.RowLogContents1, r.RowLogContents2, r.RowLogContents3, r.RowLogContents4, r.RowLogContents5 };
        for (var i = 0; i < contents.Length; i++)
        {
            if (contents[i] is not { Length: > 0 } bytes) continue;
            var name = $"RowLog Contents {i}";
            if (i == 0 && row is not null)
                foreach (var f in row.Fields)
                    Bytes.Add(new ByteChunk(name, f.Offset, Hex(bytes, f.Offset, f.Length), f.Role, Tooltip(name, f)));
            else
                Bytes.Add(new ByteChunk(name, 0, Hex(bytes, 0, bytes.Length), null, $"{name}, {bytes.Length} bytes, not decoded"));
        }
    }

    // Un champ malformé peut annoncer une longueur au-delà du contenu : on borne pour ne jamais lever.
    private static string Hex(byte[] bytes, int offset, int length)
    {
        offset = Math.Clamp(offset, 0, bytes.Length);
        length = Math.Clamp(length, 0, bytes.Length - offset);
        return length == 0 ? "" : BitConverter.ToString(bytes, offset, length).Replace('-', ' ');
    }

    private static string Tooltip(string content, DecodedField f)
    {
        var lines = new List<string> { $"role: {f.Role}" };
        if (f.Column is not null) lines.Add($"column: {f.Column}");
        if (f.Value is not null) lines.Add($"value: {ValueText(f.Value)}");
        if (f.Problem is not null) lines.Add($"problem: {f.Problem}");
        lines.Add($"{content}, offset {f.Offset}, {f.Length} bytes");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Texte d'une valeur décodée : NULL SQL, binaire en hex, le reste en culture invariante.</summary>
    public static string ValueText(object? value) => value switch
    {
        null => "NULL",
        byte[] b => "0x" + Convert.ToHexString(b),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static string OutcomeText(TxOutcome o) => o switch
    {
        TxOutcome.Committed => "committed",
        TxOutcome.RolledBack => "rolled back",
        TxOutcome.PartlyRolledBack => "partly rolled back",
        _ => "of unknown outcome",
    };
}
