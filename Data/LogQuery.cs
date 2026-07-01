using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Data;

/// <summary>Lectures sur le cache : pagination virtuelle et agrégats (§4.3, §6).</summary>
public sealed class LogQuery
{
    private readonly SqliteConnection _connection;

    public LogQuery(SqliteConnection connection) => _connection = connection;

    public async Task<IReadOnlyList<LogRecord>> GetPageAsync(
        int offset, int limit, LogFilter? filter = null, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        var where = AppendFilter(cmd, filter);
        cmd.CommandText =
            "SELECT LSN, Operation, Context, TransactionId, AllocUnitName, RowLogContents0, RowLogContents1 " +
            $"FROM LogRecords{where} ORDER BY Id LIMIT $limit OFFSET $offset;";
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var list = new List<LogRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new LogRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : (byte[])reader[5],
                reader.IsDBNull(6) ? null : (byte[])reader[6]));
        }
        return list;
    }

    /// <summary>Ajoute les paramètres de filtre à <paramref name="cmd"/> et renvoie la clause WHERE (préfixée d'un espace) ou "".</summary>
    internal static string AppendFilter(SqliteCommand cmd, LogFilter? filter)
    {
        if (filter is null || filter.IsEmpty) return string.Empty;

        var clauses = new List<string>();
        if (!string.IsNullOrEmpty(filter.AllocUnitName))
        {
            clauses.Add("AllocUnitName LIKE $f_alloc");
            cmd.Parameters.AddWithValue("$f_alloc", $"%{filter.AllocUnitName}%");
        }
        if (!string.IsNullOrEmpty(filter.Operation))
        {
            clauses.Add("Operation = $f_op");
            cmd.Parameters.AddWithValue("$f_op", filter.Operation);
        }
        if (!string.IsNullOrEmpty(filter.TransactionId))
        {
            clauses.Add("TransactionId = $f_txid");
            cmd.Parameters.AddWithValue("$f_txid", filter.TransactionId);
        }
        return clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses);
    }
}
