using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
namespace SqlLogExplorer.Backends;

/// <summary>Crée/démarre/arrête l'instance LocalDB dédiée (Windows uniquement, spec §3.1).</summary>
public sealed class LocalDbInstanceManager
{
    private readonly string _instanceName;
    private bool _startedByUs;

    public LocalDbInstanceManager(string instanceName = "SqlLogExplorerInstance")
        => _instanceName = instanceName;

    public string ConnectionString =>
        $"Server=(localdb)\\{_instanceName};Integrated Security=true;TrustServerCertificate=true;";

    public async Task EnsureStartedAsync(CancellationToken ct = default)
    {
        // create est idempotent côté effet : ignore l'échec si l'instance existe déjà.
        await RunAsync(LocalDbCommands.Create(_instanceName), throwOnError: false, ct);
        await RunAsync(LocalDbCommands.Start(_instanceName), throwOnError: true, ct);
        _startedByUs = true;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_startedByUs) return;
        await RunAsync(LocalDbCommands.Stop(_instanceName), throwOnError: false, ct);
        _startedByUs = false;
    }

    private static async Task RunAsync(string arguments, bool throwOnError, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("sqllocaldb", arguments)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Impossible de démarrer sqllocaldb.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15)); // Strict 15s timeout

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch { /* ignore */ }
            throw new TimeoutException($"Le processus sqllocaldb {arguments} a expiré.");
        }

        if (throwOnError && process.ExitCode != 0)
        {
            var err = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"sqllocaldb {arguments} a échoué : {err}");
        }
    }
}
