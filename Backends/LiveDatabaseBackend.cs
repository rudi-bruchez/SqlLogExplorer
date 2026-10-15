using System;
using System.Collections.Generic;
using System.Data;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Backends;

/// <summary>
/// Backend Live DB : exécute <c>sys.fn_dblog</c> sur une base de données en direct
/// et streame le résultat via un <see cref="SqlDataReader"/> en accès séquentiel.
/// </summary>
public sealed class LiveDatabaseBackend : ILogParserBackend
{
    private readonly LsnRange? _range;

    /// <summary>Une fenêtre temporelle résolue en plage LSN (spec §3.1) borne fn_dblog ; null = log actif complet.</summary>
    public LiveDatabaseBackend(LsnRange? range = null) => _range = range;

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async IAsyncEnumerable<LogRecord> ParseLogAsync(
        IReadOnlyList<string> targets, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Live DB : une seule cible = la chaîne de connexion (fn_dblog lit le log actif).
        if (targets.Count != 1)
            throw new ArgumentException("Live DB attend exactement une chaîne de connexion.", nameof(targets));

        await using var connection = new SqlConnection(targets[0]);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        // fn_dblog(NULL,NULL) = log actif complet ; fn_dblog(@start,@end) = borné à la fenêtre résolue (spec §3.1).
        var bounds = _range is null ? "NULL, NULL" : "@start, @end";
        cmd.CommandText = $"""
            SELECT {LogColumns.SelectList}
            FROM sys.fn_dblog({bounds});
            """;
        if (_range is not null)
        {
            cmd.Parameters.Add(new SqlParameter("@start", Lsn.ToFnDblogArgument(_range.Start)));
            cmd.Parameters.Add(new SqlParameter("@end", Lsn.ToFnDblogArgument(_range.End)));
        }
        cmd.CommandTimeout = 0;

        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct))
        {
            yield return LogColumns.Read(reader);
        }
    }

    public Task CleanupAsync(CancellationToken ct = default) => Task.CompletedTask;
}
