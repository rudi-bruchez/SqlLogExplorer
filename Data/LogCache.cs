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
                LSN             TEXT NOT NULL,
                Operation       TEXT NOT NULL,
                Context         TEXT,
                TransactionId   TEXT,
                AllocUnitName   TEXT,
                RowLogContents0 BLOB,
                RowLogContents1 BLOB
            );
            CREATE INDEX IF NOT EXISTS IX_LogRecords_AllocUnitName ON LogRecords(AllocUnitName);
            CREATE INDEX IF NOT EXISTS IX_LogRecords_TransactionId ON LogRecords(TransactionId);
            CREATE INDEX IF NOT EXISTS IX_LogRecords_Operation     ON LogRecords(Operation);
            CREATE INDEX IF NOT EXISTS IX_LogRecords_Alloc_Op      ON LogRecords(AllocUnitName, Operation);
            """;
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return 0;

        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO LogRecords (LSN, Operation, Context, TransactionId, AllocUnitName, RowLogContents0, RowLogContents1)
            VALUES ($lsn, $op, $ctx, $txid, $alloc, $rlc0, $rlc1);
            """;

        var pLsn   = AddParam(cmd, "$lsn");
        var pOp    = AddParam(cmd, "$op");
        var pCtx   = AddParam(cmd, "$ctx");
        var pTxId  = AddParam(cmd, "$txid");
        var pAlloc = AddParam(cmd, "$alloc");
        var pRlc0  = AddParam(cmd, "$rlc0");
        var pRlc1  = AddParam(cmd, "$rlc1");

        long inserted = 0;
        foreach (var r in records)
        {
            ct.ThrowIfCancellationRequested();
            pLsn.Value   = r.Lsn;
            pOp.Value    = r.Operation;
            pCtx.Value   = (object?)r.Context ?? DBNull.Value;
            pTxId.Value  = (object?)r.TransactionId ?? DBNull.Value;
            pAlloc.Value = (object?)r.AllocUnitName ?? DBNull.Value;
            pRlc0.Value  = (object?)r.RowLogContents0 ?? DBNull.Value;
            pRlc1.Value  = (object?)r.RowLogContents1 ?? DBNull.Value;
            inserted += await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return inserted;
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
        }
        catch (IOException) { /* best-effort : un handle résiduel ne doit pas faire planter la fermeture. */ }
    }
}
