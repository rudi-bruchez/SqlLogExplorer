using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace SqlLogExplorer.Data;

/// <summary>Un seul utilisateur à la fois de la connexion du cache : l'inspection tourne hors du thread UI pendant que la grille
/// peut paginer, et une SqliteConnection n'est pas sûre entre threads. Chaque méthode de LogQuery et de LogCache y entre.</summary>
internal static class ConnectionGate
{
    private static readonly ConditionalWeakTable<SqliteConnection, SemaphoreSlim> Gates = new();

    public static async Task<IDisposable> EnterAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        var gate = Gates.GetValue(connection, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Exit(gate);
    }

    private sealed class Exit(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
