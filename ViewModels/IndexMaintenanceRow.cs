using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.ViewModels;

/// <summary>Une ligne de l'onglet Index maintenance : une opération et sa part du journal importé (spec04 §6).</summary>
/// <param name="Share">Part des octets de l'opération dans le journal importé, en pourcentage (0 à 100).</param>
public sealed record IndexMaintenanceRow(MaintenanceOperation Operation, double Share)
{
    public string Kind => Operation.Kind switch
    {
        MaintenanceKind.RebuildOffline => "Rebuild offline",
        MaintenanceKind.RebuildOnline => "Rebuild online",
        _ => "Reorganize",
    };

    public string Objects
    {
        get
        {
            var text = string.Join(", ", Operation.Objects.Select(o => o.Display));
            return Operation.OtherObjects > 0
                ? $"{text} and {Operation.OtherObjects.ToString(CultureInfo.InvariantCulture)} others"
                : text;
        }
    }

    public string? Begin => Operation.Begin;
    public string? End => Operation.End;
    public long Transactions => Operation.Transactions.Count;
    public long Records => Operation.Records;
    public long Bytes => Operation.Bytes;
    public string BytesText => FormatBytes(Operation.Bytes);
    public string ShareText => FormatShare(Share);

    public string Notes
    {
        get
        {
            var notes = new List<string>();
            if (Operation.Partial)
                notes.Add(Operation.Kind == MaintenanceKind.Reorganize && !Operation.InUserTransaction
                    ? "may extend beyond the import"
                    : "not committed in the import");
            if (Operation.InUserTransaction) notes.Add("in a user transaction");
            return string.Join("; ", notes);
        }
    }

    /// <summary>Un pourcentage à une décimale, culture invariante.</summary>
    public static string FormatShare(double percent) => percent.ToString("F1", CultureInfo.InvariantCulture) + " %";

    /// <summary>Taille lisible : octets sous 1024, sinon KB, MB, GB (base 1024, une décimale).</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        string[] units = ["KB", "MB", "GB"];
        double v = bytes;
        var i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < units.Length - 1);
        return v.ToString("F1", CultureInfo.InvariantCulture) + " " + units[i];
    }
}
