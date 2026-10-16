using System.Globalization;

namespace SqlLogExplorer.Models;

/// <summary>Critères de filtrage appliqués aux lectures et aux agrégats (§4.3).</summary>
public sealed record LogFilter(
    string? AllocUnitName = null,
    string? Operation = null,
    string? TransactionId = null,
    string? PageId = null)
{
    public bool IsEmpty =>
        string.IsNullOrEmpty(AllocUnitName)
        && string.IsNullOrEmpty(Operation)
        && string.IsNullOrEmpty(TransactionId)
        && string.IsNullOrEmpty(PageId);

    /// <summary>Une page saisie sous la forme de fn_dblog (0001:00000150, page sur huit chiffres hexadécimaux) ou de
    /// DBCC PAGE (1:336, décimal), ramenée à la forme du cache ; null si elle ne se lit ni comme l'une ni comme l'autre.</summary>
    public static string? NormalizePageId(string input)
    {
        var parts = input.Split(':');
        if (parts.Length != 2) return null;
        var (file, page) = (parts[0].Trim(), parts[1].Trim());
        var style = page.Length == 8 ? NumberStyles.AllowHexSpecifier : NumberStyles.None;
        return uint.TryParse(file, style, CultureInfo.InvariantCulture, out var f) && f <= 0xFFFF
            && uint.TryParse(page, style, CultureInfo.InvariantCulture, out var p)
            ? $"{f:x4}:{p:x8}"
            : null;
    }
}
