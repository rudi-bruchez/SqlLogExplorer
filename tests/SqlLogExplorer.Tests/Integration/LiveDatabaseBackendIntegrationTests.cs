using SqlLogExplorer.Backends;
using Xunit;

namespace SqlLogExplorer.Tests.Integration;

public class LiveDatabaseBackendIntegrationTests
{
    [IntegrationFact]
    public async Task ParseLogAsync_YieldsRecordsFromLiveDb()
    {
        var connString = Environment.GetEnvironmentVariable("SQLLOGEXPLORER_TEST_LIVEDB");
        Assert.False(string.IsNullOrEmpty(connString), "Définir SQLLOGEXPLORER_TEST_LIVEDB vers une base live (ex: master).");

        var backend = new LiveDatabaseBackend();
        await backend.InitializeAsync();
        try
        {
            var count = 0;
            await foreach (var record in backend.ParseLogAsync(new[] { connString! }))
            {
                Assert.False(string.IsNullOrEmpty(record.Lsn));
                Assert.False(string.IsNullOrEmpty(record.Operation));
                if (++count >= 10) break;
            }
            // master a toujours de l'activité, ou on a pu lire des logs.
        }
        finally
        {
            await backend.CleanupAsync();
        }
    }
}
