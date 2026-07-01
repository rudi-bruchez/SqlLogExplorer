using Microsoft.Data.SqlClient;
using SqlLogExplorer.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SqlLogExplorer.Backends;

/// <summary>
/// Résout une fenêtre de dates en plage LSN via un pré-scan des bornes de transaction (spec §3.1).
/// Ne lit que les enregistrements begin/commit/abort du log ACTIF (fenêtre limitée).
/// </summary>
public static class LiveLsnResolver
{
    private const string BoundaryOps =
        "[Operation] IN ('LOP_BEGIN_XACT','LOP_COMMIT_XACT','LOP_ABORT_XACT')";

    // COALESCE([End Time],[Begin Time]) : begin porte Begin Time, commit/abort portent End Time.
    public static string BuildRangeQuery() => $"""
        SELECT MIN([Current LSN]) AS StartLsn, MAX([Current LSN]) AS EndLsn
        FROM sys.fn_dblog(NULL, NULL)
        WHERE {BoundaryOps}
          AND (@start IS NULL OR CONVERT(datetime, COALESCE([End Time],[Begin Time]), 121) >= @start)
          AND (@end   IS NULL OR CONVERT(datetime, COALESCE([End Time],[Begin Time]), 121) <= @end);
        """;

    public static string BuildEarliestTimeQuery() => $"""
        SELECT MIN(CONVERT(datetime, COALESCE([End Time],[Begin Time]), 121))
        FROM sys.fn_dblog(NULL, NULL)
        WHERE {BoundaryOps};
        """;

    public static async Task<LsnRange?> ResolveAsync(
        SqlConnection cn, DateTime? start, DateTime? end, CancellationToken ct = default)
    {
        if (start is null && end is null) return null; // aucune fenêtre ⇒ log actif complet.

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = BuildRangeQuery();
        cmd.CommandTimeout = 0;
        cmd.Parameters.Add(new SqlParameter("@start", (object?)start ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@end",   (object?)end   ?? DBNull.Value));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(0) || reader.IsDBNull(1))
            return null; // rien dans la fenêtre.
        return new LsnRange(reader.GetString(0), reader.GetString(1));
    }

    public static async Task<DateTime?> GetEarliestLogTimeAsync(SqlConnection cn, CancellationToken ct = default)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = BuildEarliestTimeQuery();
        cmd.CommandTimeout = 0;
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DateTime dt ? dt : null;
    }
}
