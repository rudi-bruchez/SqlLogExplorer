
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlLogExplorer.Backends;
using SqlLogExplorer.Data;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Services;

/// <summary>Streame un backend vers le cache par batchs, avec progression et annulation (spec §3.4).</summary>
public sealed class LogImportService
{
    private readonly ILogParserBackend _backend;
    private readonly LogCache _cache;
    private readonly int _batchSize;

    public LogImportService(ILogParserBackend backend, LogCache cache, int batchSize = 1000)
    {
        _backend = backend;
        _cache = cache;
        _batchSize = batchSize;
    }

    /// <summary>Renvoie le nombre total d'enregistrements importés. <paramref name="progress"/> reçoit le cumul après chaque batch.</summary>
    public async Task<long> ImportAsync(IReadOnlyList<string> targets, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        await _backend.InitializeAsync(ct);
        try
        {
            var batch = new List<LogRecord>(_batchSize);
            long total = 0;

            await foreach (var record in _backend.ParseLogAsync(targets, ct).WithCancellation(ct))
            {
                batch.Add(record);
                if (batch.Count >= _batchSize)
                {
                    total += await _cache.InsertBatchAsync(batch, ct);
                    batch.Clear();
                    progress?.Report(total);
                }
            }

            if (batch.Count > 0)
            {
                total += await _cache.InsertBatchAsync(batch, ct);
                progress?.Report(total);
            }

            return total;
        }
        finally
        {
            await _backend.CleanupAsync(CancellationToken.None);
        }
    }
}
