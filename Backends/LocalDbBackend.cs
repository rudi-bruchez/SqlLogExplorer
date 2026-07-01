using System.Collections.Generic;
using System.Data;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Backends;

/// <summary>
/// Backend Windows : exécute <c>sys.fn_dump_dblog</c> sur une instance LocalDB jetable
/// et streame le résultat via un <see cref="SqlDataReader"/> en accès séquentiel (spec §3.1).
/// </summary>
public sealed class LocalDbBackend : ILogParserBackend
{
    private readonly LocalDbInstanceManager _instanceManager;
    private int _defaultParameterCount = FnDumpDblogQuery.DefaultParameterCount;

    public LocalDbBackend(LocalDbInstanceManager? instanceManager = null)
        => _instanceManager = instanceManager ?? new LocalDbInstanceManager();

    public Task InitializeAsync(CancellationToken ct = default)
        => _instanceManager.EnsureStartedAsync(ct);

    public async IAsyncEnumerable<LogRecord> ParseLogAsync(
        IReadOnlyList<string> targets, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_instanceManager.ConnectionString);
        await connection.OpenAsync(ct);

        // Une chaîne de backups = un appel fn_dump_dblog par fichier (spec §3.2).
        // On concatène les flux ; l'ordre n'affecte pas les agrégats de ce plan.
        foreach (var path in targets)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = FnDumpDblogQuery.Build("@path", _defaultParameterCount);
            cmd.CommandTimeout = 0; // fn_dump_dblog est lent (spec §3.4) : pas de timeout.
            cmd.Parameters.Add(new SqlParameter("@path", path));

            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            while (await reader.ReadAsync(ct))
            {
                // Accès séquentiel : lire les colonnes dans l'ordre du SELECT (0..6).
                yield return new LogRecord(
                    Lsn:             reader.GetString(0),
                    Operation:       reader.GetString(1),
                    Context:         reader.IsDBNull(2) ? null : reader.GetString(2),
                    TransactionId:   reader.IsDBNull(3) ? null : reader.GetString(3),
                    AllocUnitName:   reader.IsDBNull(4) ? null : reader.GetString(4),
                    RowLogContents0: reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5),
                    RowLogContents1: reader.IsDBNull(6) ? null : (byte[])reader.GetValue(6));
            }
        }
    }

    public Task CleanupAsync(CancellationToken ct = default)
        => _instanceManager.StopAsync(ct);
}
