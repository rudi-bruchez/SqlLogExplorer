using System.Collections.Generic;
using System.Linq;
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
        using var gate = await ConnectionGate.EnterAsync(_connection, ct);
        await using var cmd = _connection.CreateCommand();
        var where = AppendFilter(cmd, filter);
        cmd.CommandText = $"{SelectRecords}{where} ORDER BY LSN LIMIT $limit OFFSET $offset;";
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        return await ReadAllAsync(cmd, ct);
    }

    private const string SelectRecords =
        "SELECT LSN, Operation, Context, TransactionId, AllocUnitName, RowLogContents0, RowLogContents1, " +
        "AllocUnitId, PartitionId, PageId, SlotId, OffsetInRow, ModifySize, NumElements, " +
        "Description, TransactionName, RowLogContents2, RowLogContents3, RowLogContents4, RowLogContents5, LogRecordBytes, " +
        "BeginTime, EndTime FROM LogRecords";

    // Enregistrements de données (spec03 §5) : le journal porte aussi des LCX_PFS et des LCX_INDEX_* qui citent l'AllocUnitId de la table.
    private const string DataRecords =
        "Operation IN ('LOP_INSERT_ROWS', 'LOP_DELETE_ROWS', 'LOP_MODIFY_ROW', 'LOP_MODIFY_COLUMNS') " +
        "AND Context IN ('LCX_HEAP', 'LCX_CLUSTERED', 'LCX_MARK_AS_GHOST')";

    // Une compensation n'a pas de clé et n'entre dans aucune chaîne : le rejeu la traite par transaction (spec03 §7.1, §7.3).
    private const string NotCompensation = "(Description IS NULL OR instr(Description, 'COMPENSATION') = 0)";

    /// <summary>Enregistrements de données de l'unité d'allocation dont RowKey est encore NULL, compensations exclues (spec03 §7.1).</summary>
    public async Task<IReadOnlyList<LogRecord>> GetUnkeyedDataRecordsAsync(long allocUnitId, CancellationToken ct = default)
    {
        using var gate = await ConnectionGate.EnterAsync(_connection, ct);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"{SelectRecords} WHERE AllocUnitId = $au AND RowKey IS NULL AND {DataRecords} AND {NotCompensation} ORDER BY LSN;";
        cmd.Parameters.AddWithValue("$au", allocUnitId);
        return await ReadAllAsync(cmd, ct);
    }

    /// <summary>Chaîne d'une ligne d'une table clustered : ses enregistrements de données, dans l'ordre des LSN (spec03 §7.1).</summary>
    public async Task<IReadOnlyList<LogRecord>> GetChainAsync(long allocUnitId, string rowKey, CancellationToken ct = default)
    {
        using var gate = await ConnectionGate.EnterAsync(_connection, ct);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"{SelectRecords} WHERE AllocUnitId = $au AND RowKey = $key AND {DataRecords} ORDER BY LSN;";
        cmd.Parameters.AddWithValue("$au", allocUnitId);
        cmd.Parameters.AddWithValue("$key", rowKey);
        return await ReadAllAsync(cmd, ct);
    }

    /// <summary>Tous les enregistrements de données d'une adresse de tas, compensations exclues, dans l'ordre des LSN ;
    /// l'appelant y découpe la vie de la ligne entre un insert et un delete (spec03 §7.1).</summary>
    public async Task<IReadOnlyList<LogRecord>> GetHeapChainAsync(long allocUnitId, string pageId, int slotId, CancellationToken ct = default)
    {
        using var gate = await ConnectionGate.EnterAsync(_connection, ct);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"{SelectRecords} WHERE AllocUnitId = $au AND PageId = $page AND SlotId = $slot AND {DataRecords} AND {NotCompensation} ORDER BY LSN;";
        cmd.Parameters.AddWithValue("$au", allocUnitId);
        cmd.Parameters.AddWithValue("$page", pageId);
        cmd.Parameters.AddWithValue("$slot", slotId);
        return await ReadAllAsync(cmd, ct);
    }

    /// <summary>Début, validation, annulation, LOP_CTR_NEST_ABORT et compensations de ces transactions, dans l'ordre des LSN :
    /// ce que lisent RowReplayer.Classify, AdrReverts et FirstUndoLsns (spec03 §7.3), et le nom de la transaction (§5).</summary>
    public async Task<IReadOnlyList<LogRecord>> GetTransactionRecordsAsync(IReadOnlyCollection<string> transactionIds, CancellationToken ct = default)
    {
        using var gate = await ConnectionGate.EnterAsync(_connection, ct);
        var list = new List<LogRecord>();
        // Par paquets : le nombre de paramètres d'une requête SQLite est borné.
        foreach (var chunk in transactionIds.Distinct().Chunk(500))
        {
            await using var cmd = _connection.CreateCommand();
            var names = new List<string>();
            for (var i = 0; i < chunk.Length; i++)
            {
                names.Add($"$tx{i}");
                cmd.Parameters.AddWithValue($"$tx{i}", chunk[i]);
            }
            cmd.CommandText =
                $"{SelectRecords} WHERE TransactionId IN ({string.Join(", ", names)}) " +
                "AND (Operation IN ('LOP_BEGIN_XACT', 'LOP_COMMIT_XACT', 'LOP_ABORT_XACT', 'LOP_CTR_NEST_ABORT') " +
                "OR instr(Description, 'COMPENSATION') > 0) ORDER BY LSN;";
            list.AddRange(await ReadAllAsync(cmd, ct));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Lsn, b.Lsn));
        return list;
    }

    /// <summary>Les transactions du cache dans l'ordre de leur premier LSN, sans la pseudo-transaction 0000:00000000 des
    /// enregistrements hors transaction (checkpoints). Avec <paramref name="touching"/>, seulement celles dont un
    /// enregistrement y répond (un objet, une page), comptées en entier.</summary>
    public async Task<IReadOnlyList<TransactionSummary>> GetTransactionsAsync(LogFilter? touching = null, CancellationToken ct = default)
    {
        using var gate = await ConnectionGate.EnterAsync(_connection, ct);
        await using var cmd = _connection.CreateCommand();
        var where = AppendFilter(cmd, touching);
        var touches = where.Length == 0 ? string.Empty : $" AND TransactionId IN (SELECT TransactionId FROM LogRecords{where})";
        cmd.CommandText =
            "SELECT TransactionId, MAX(CASE WHEN Operation = 'LOP_BEGIN_XACT' THEN TransactionName END), " +
            "MAX(BeginTime), MAX(EndTime), " +
            "MAX(Operation = 'LOP_COMMIT_XACT'), MAX(Operation = 'LOP_ABORT_XACT'), COUNT(*) " +
            "FROM LogRecords WHERE TransactionId IS NOT NULL AND TransactionId <> '0000:00000000'" + touches +
            " GROUP BY TransactionId ORDER BY MIN(LSN);";

        var list = new List<TransactionSummary>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var outcome = r.GetBoolean(4) ? "Committed" : r.GetBoolean(5) ? "Rolled back" : "Open";
            list.Add(new TransactionSummary(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), outcome, r.GetInt64(6)));
        }
        return list;
    }

    /// <summary>Le plus grand LSN du cache, d'où l'ancre 3 reprend la lecture du journal (spec03 §7.2) ; null pour un cache vide.</summary>
    public async Task<string?> GetLastLsnAsync(CancellationToken ct = default)
    {
        using var gate = await ConnectionGate.EnterAsync(_connection, ct);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(LSN) FROM LogRecords;";
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<IReadOnlyList<LogRecord>> ReadAllAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<LogRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadRecord(reader));
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
            LogRecordBytes = Bytes(r, 20), BeginTime = Text(r, 21), EndTime = Text(r, 22),
        };
    }

    public async Task<IReadOnlyList<OperationCount>> CountByOperationAsync(
        LogFilter? filter = null, CancellationToken ct = default)
    {
        using var gate = await ConnectionGate.EnterAsync(_connection, ct);
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
        using var gate = await ConnectionGate.EnterAsync(_connection, ct);
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
        using var gate = await ConnectionGate.EnterAsync(_connection, ct);
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
        if (!string.IsNullOrEmpty(filter.PageId))
        {
            clauses.Add("PageId = $f_page");
            cmd.Parameters.AddWithValue("$f_page", filter.PageId);
        }
        if (!string.IsNullOrEmpty(filter.TransactionId))
        {
            clauses.Add("TransactionId = $f_txid");
            cmd.Parameters.AddWithValue("$f_txid", filter.TransactionId);
        }
        return clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses);
    }
}
