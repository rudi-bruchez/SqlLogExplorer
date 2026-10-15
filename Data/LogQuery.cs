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
            "SELECT LSN, Operation, Context, TransactionId, AllocUnitName, RowLogContents0, RowLogContents1, " +
            "AllocUnitId, PartitionId, PageId, SlotId, OffsetInRow, ModifySize, NumElements, " +
            "Description, TransactionName, RowLogContents2, RowLogContents3, RowLogContents4, RowLogContents5, LogRecordBytes " +
            $"FROM LogRecords{where} ORDER BY LSN LIMIT $limit OFFSET $offset;";
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var list = new List<LogRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(ReadRecord(reader));
        }
        return list;
    }

    /// <summary>Lit une ligne dans l'ordre de colonnes du SELECT de <see cref="GetPageAsync"/>.</summary>
    private static LogRecord ReadRecord(SqliteDataReader r)
    {
        static byte[]? Bytes(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : (byte[])r[i];
        static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        static long? Long(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
        static int? Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

        return new LogRecord(r.GetString(0), r.GetString(1), Text(r, 2), Text(r, 3), Text(r, 4), Bytes(r, 5), Bytes(r, 6))
        {
            AllocUnitId = Long(r, 7), PartitionId = Long(r, 8), PageId = Text(r, 9), SlotId = Int(r, 10),
            OffsetInRow = Int(r, 11), ModifySize = Int(r, 12), NumElements = Int(r, 13),
            Description = Text(r, 14), TransactionName = Text(r, 15),
            RowLogContents2 = Bytes(r, 16), RowLogContents3 = Bytes(r, 17), RowLogContents4 = Bytes(r, 18), RowLogContents5 = Bytes(r, 19),
            LogRecordBytes = Bytes(r, 20),
        };
    }

    public async Task<IReadOnlyList<OperationCount>> CountByOperationAsync(
        LogFilter? filter = null, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        var where = AppendFilter(cmd, filter);
        cmd.CommandText =
            $"SELECT Operation, COUNT(*) AS Nb FROM LogRecords{where} GROUP BY Operation ORDER BY Nb DESC;";

        var list = new List<OperationCount>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new OperationCount(reader.GetString(0), reader.GetInt64(1)));
        return list;
    }

    public async Task<IReadOnlyList<ObjectCount>> CountByObjectAsync(
        LogFilter? filter = null, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        var where = AppendFilter(cmd, filter);
        var and = where.Length == 0 ? " WHERE" : where + " AND";
        cmd.CommandText =
            $"SELECT AllocUnitName, COUNT(*) AS Nb FROM LogRecords{and} AllocUnitName IS NOT NULL " +
            "GROUP BY AllocUnitName ORDER BY Nb DESC;";

        var list = new List<ObjectCount>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new ObjectCount(reader.GetString(0), reader.GetInt64(1)));
        return list;
    }

    public async Task<IReadOnlyList<ObjectOperationCount>> CountByObjectAndOperationAsync(
        LogFilter? filter = null, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        var where = AppendFilter(cmd, filter);
        var and = where.Length == 0 ? " WHERE" : where + " AND";
        cmd.CommandText =
            $"SELECT AllocUnitName, Operation, COUNT(*) AS Nb FROM LogRecords{and} AllocUnitName IS NOT NULL " +
            "GROUP BY AllocUnitName, Operation ORDER BY AllocUnitName, Nb DESC;";

        var list = new List<ObjectOperationCount>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new ObjectOperationCount(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
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
