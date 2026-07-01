using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Backends;

/// <summary>Extrait les enregistrements bruts d'un fichier de log (spec §3).</summary>
public interface ILogParserBackend
{
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Streame les enregistrements des cibles fournies.
    /// LocalDB : une entrée = un fichier .trn (un appel fn_dump_dblog par fichier).
    /// Live DB : une seule entrée = la chaîne de connexion.
    /// </summary>
    IAsyncEnumerable<LogRecord> ParseLogAsync(IReadOnlyList<string> targets, CancellationToken ct = default);

    Task CleanupAsync(CancellationToken ct = default);
}
