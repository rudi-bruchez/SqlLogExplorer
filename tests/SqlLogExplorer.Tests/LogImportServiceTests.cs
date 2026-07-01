using System.Runtime.CompilerServices;
using SqlLogExplorer.Backends;
using SqlLogExplorer.Data;
using SqlLogExplorer.Models;
using SqlLogExplorer.Services;
using Xunit;

namespace SqlLogExplorer.Tests;

public class LogImportServiceTests
{
    private sealed class FakeBackend(int recordCount) : ILogParserBackend
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CleanupAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<LogRecord> ParseLogAsync(
            IReadOnlyList<string> targets, [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (int i = 0; i < recordCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return new LogRecord($"lsn{i}", "LOP_INSERT_ROWS", "LCX_HEAP", $"tx{i}", "dbo.T", null, null);
            }
        }
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    [Fact]
    public async Task ImportAsync_PersistsAllRecordsAndReportsProgress()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db");
        try
        {
            await using var cache = await LogCache.CreateAsync(dbPath);
            var service = new LogImportService(new FakeBackend(2500), cache, batchSize: 1000);
            var reports = new List<long>();

            var total = await service.ImportAsync(new[] { "ignored.trn" }, new SyncProgress<long>(reports.Add));

            Assert.Equal(2500, total);
            var query = new LogQuery(cache.Connection);
            var page = await query.GetPageAsync(0, 5000);
            Assert.Equal(2500, page.Count);

            Assert.Equal(new[] { 1000L, 2000L, 2500L }, reports);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }
}
