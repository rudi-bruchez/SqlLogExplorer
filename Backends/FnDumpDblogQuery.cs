using System.Linq;

namespace SqlLogExplorer.Backends;

/// <summary>
/// Construit l'appel à <c>sys.fn_dump_dblog</c> (spec §3.4). La fonction prend
/// 5 paramètres positionnels puis une longue liste de DEFAULT ; le nombre exact
/// varie selon la version de SQL Server et doit être validé par un test d'intégration.
/// </summary>
public static class FnDumpDblogQuery
{
    /// <summary>5 positionnels + 63 DEFAULT = 68 paramètres (SQL Server 2019/2022).</summary>
    public const int DefaultParameterCount = 63;

    public static string Build(string pathParameterName, int defaultParameterCount = DefaultParameterCount)
    {
        var defaults = string.Join(", ", Enumerable.Repeat("DEFAULT", defaultParameterCount));
        return $"""
            SELECT [Current LSN]        AS Lsn,
                   [Operation]          AS Operation,
                   [Context]            AS Context,
                   [Transaction ID]     AS TransactionId,
                   [AllocUnitName]      AS AllocUnitName,
                   [RowLog Contents 0]  AS RowLogContents0,
                   [RowLog Contents 1]  AS RowLogContents1
            FROM sys.fn_dump_dblog(NULL, NULL, N'DISK', 1, {pathParameterName}, {defaults});
            """;
    }
}
