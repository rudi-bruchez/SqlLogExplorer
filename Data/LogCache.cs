using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Data;

/// <summary>Cache SQLite local d'un import (spec §4). Une instance = une base.</summary>
public sealed class LogCache : IAsyncDisposable, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _databasePath;

    private LogCache(SqliteConnection connection, string databasePath)
    {
        _connection = connection;
        _databasePath = databasePath;
    }

    public SqliteConnection Connection => _connection;

    public static async Task<LogCache> CreateAsync(string databasePath, CancellationToken ct = default)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(ct);
        await InitializeSchemaAsync(connection, ct);
        return new LogCache(connection, databasePath);
    }

    private static async Task InitializeSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        // Bulk-load tuning (spec §4) : la base est jetable, on privilégie le débit sur la durabilité.
        const string sql = """
            PRAGMA journal_mode = MEMORY;
            PRAGMA synchronous  = OFF;
            PRAGMA temp_store   = MEMORY;

            CREATE TABLE IF NOT EXISTS LogRecords (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                LSN             TEXT NOT NULL UNIQUE,
                Operation       TEXT NOT NULL,
                Context         TEXT,
                TransactionId   TEXT,
                AllocUnitName   TEXT,
                RowLogContents0 BLOB,
                RowLogContents1 BLOB,
                AllocUnitId     INTEGER,
                PartitionId     INTEGER,
                PageId          TEXT,
                SlotId          INTEGER,
                OffsetInRow     INTEGER,
                ModifySize      INTEGER,
                NumElements     INTEGER,
                Description     TEXT,
                TransactionName TEXT,
                RowLogContents2 BLOB,
                RowLogContents3 BLOB,
                RowLogContents4 BLOB,
                RowLogContents5 BLOB,
                LogRecordBytes  BLOB,
                BeginTime       TEXT,
                EndTime         TEXT,
                RowKey          TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_LogRecords_AllocUnitName ON LogRecords(AllocUnitName);
            CREATE INDEX IF NOT EXISTS IX_LogRecords_TransactionId ON LogRecords(TransactionId);
            CREATE INDEX IF NOT EXISTS IX_LogRecords_Operation     ON LogRecords(Operation);
            CREATE INDEX IF NOT EXISTS IX_LogRecords_Alloc_Op      ON LogRecords(AllocUnitName, Operation);
            CREATE INDEX IF NOT EXISTS IX_LogRecords_Alloc_Key_Lsn       ON LogRecords(AllocUnitId, RowKey, LSN);
            CREATE INDEX IF NOT EXISTS IX_LogRecords_Alloc_Page_Slot_Lsn ON LogRecords(AllocUnitId, PageId, SlotId, LSN);
            """;
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return 0;
        using var gate = await ConnectionGate.EnterAsync(_connection, ct);

        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        // OR IGNORE : deux .trn qui se recouvrent ne doivent pas dupliquer une modification (spec03 §3).
        cmd.CommandText = """
            INSERT OR IGNORE INTO LogRecords
                (LSN, Operation, Context, TransactionId, AllocUnitName, RowLogContents0, RowLogContents1,
                 AllocUnitId, PartitionId, PageId, SlotId, OffsetInRow, ModifySize, NumElements,
                 Description, TransactionName, RowLogContents2, RowLogContents3, RowLogContents4, RowLogContents5, LogRecordBytes,
                 BeginTime, EndTime)
            VALUES
                ($lsn, $op, $ctx, $txid, $alloc, $rlc0, $rlc1,
                 $auid, $pid, $page, $slot, $off, $msize, $nel,
                 $desc, $txname, $rlc2, $rlc3, $rlc4, $rlc5, $lrb,
                 $begin, $end);
            """;

        var pLsn    = AddParam(cmd, "$lsn");
        var pOp     = AddParam(cmd, "$op");
        var pCtx    = AddParam(cmd, "$ctx");
        var pTxId   = AddParam(cmd, "$txid");
        var pAlloc  = AddParam(cmd, "$alloc");
        var pRlc0   = AddParam(cmd, "$rlc0");
        var pRlc1   = AddParam(cmd, "$rlc1");
        var pAuId   = AddParam(cmd, "$auid");
        var pPid    = AddParam(cmd, "$pid");
        var pPage   = AddParam(cmd, "$page");
        var pSlot   = AddParam(cmd, "$slot");
        var pOff    = AddParam(cmd, "$off");
        var pMSize  = AddParam(cmd, "$msize");
        var pNel    = AddParam(cmd, "$nel");
        var pDesc   = AddParam(cmd, "$desc");
        var pTxName = AddParam(cmd, "$txname");
        var pRlc2   = AddParam(cmd, "$rlc2");
        var pRlc3   = AddParam(cmd, "$rlc3");
        var pRlc4   = AddParam(cmd, "$rlc4");
        var pRlc5   = AddParam(cmd, "$rlc5");
        var pLrb    = AddParam(cmd, "$lrb");
        var pBegin  = AddParam(cmd, "$begin");
        var pEnd    = AddParam(cmd, "$end");

        long inserted = 0;
        cmd.Prepare();
        foreach (var r in records)
        {
            ct.ThrowIfCancellationRequested();
            pLsn.Value    = Lsn.Normalize(r.Lsn);
            pOp.Value     = r.Operation;
            pCtx.Value    = (object?)r.Context ?? DBNull.Value;
            pTxId.Value   = (object?)r.TransactionId ?? DBNull.Value;
            pAlloc.Value  = (object?)r.AllocUnitName ?? DBNull.Value;
            pRlc0.Value   = (object?)r.RowLogContents0 ?? DBNull.Value;
            pRlc1.Value   = (object?)r.RowLogContents1 ?? DBNull.Value;
            pAuId.Value   = (object?)r.AllocUnitId ?? DBNull.Value;
            pPid.Value    = (object?)r.PartitionId ?? DBNull.Value;
            pPage.Value   = (object?)r.PageId ?? DBNull.Value;
            pSlot.Value   = (object?)r.SlotId ?? DBNull.Value;
            pOff.Value    = (object?)r.OffsetInRow ?? DBNull.Value;
            pMSize.Value  = (object?)r.ModifySize ?? DBNull.Value;
            pNel.Value    = (object?)r.NumElements ?? DBNull.Value;
            pDesc.Value   = (object?)r.Description ?? DBNull.Value;
            pTxName.Value = (object?)r.TransactionName ?? DBNull.Value;
            pRlc2.Value   = (object?)r.RowLogContents2 ?? DBNull.Value;
            pRlc3.Value   = (object?)r.RowLogContents3 ?? DBNull.Value;
            pRlc4.Value   = (object?)r.RowLogContents4 ?? DBNull.Value;
            pRlc5.Value   = (object?)r.RowLogContents5 ?? DBNull.Value;
            pLrb.Value    = (object?)r.LogRecordBytes ?? DBNull.Value;
            pBegin.Value  = (object?)r.BeginTime ?? DBNull.Value;
            pEnd.Value    = (object?)r.EndTime ?? DBNull.Value;
            inserted += cmd.ExecuteNonQuery(); // 0 pour une ligne ignorée.
        }

        await tx.CommitAsync(ct);
        return inserted;
    }

    /// <summary>Clés canoniques calculées par DecodingService (spec03 §7.1), indexées pour les chaînes.</summary>
    public async Task SetRowKeysAsync(IReadOnlyList<(string Lsn, string? RowKey)> keys, CancellationToken ct = default)
    {
        using var gate = await ConnectionGate.EnterAsync(_connection, ct);
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE LogRecords SET RowKey = $key WHERE LSN = $lsn;";
        var pKey = AddParam(cmd, "$key");
        var pLsn = AddParam(cmd, "$lsn");
        cmd.Prepare();
        foreach (var (lsn, key) in keys)
        {
            pLsn.Value = Lsn.Normalize(lsn);
            pKey.Value = (object?)key ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        await tx.CommitAsync(ct);
    }

    private static SqliteParameter AddParam(SqliteCommand cmd, string name)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }

    public async ValueTask DisposeAsync()
    {
        // Une inspection en cours finit sa requête avant la fermeture.
        using (await ConnectionGate.EnterAsync(_connection)) { }
        await _connection.DisposeAsync();
        DeleteFile();
    }

    public void Dispose()
    {
        _connection.Dispose();
        DeleteFile();
    }

    // La base est temporaire : on supprime le fichier après fermeture (spec §4, cache lifecycle).
    // ClearPool libère le handle conservé par le pool de connexions avant la suppression.
    private void DeleteFile()
    {
        SqliteConnection.ClearPool(_connection);
        try
        {
            if (File.Exists(_databasePath)) File.Delete(_databasePath);
            var shm = _databasePath + "-shm";
            if (File.Exists(shm)) File.Delete(shm);
            var wal = _databasePath + "-wal";
            if (File.Exists(wal)) File.Delete(wal);
        }
        catch (Exception) { /* best-effort : un handle résiduel ne doit pas faire planter la fermeture. */ }
    }
}
