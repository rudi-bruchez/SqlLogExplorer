using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
