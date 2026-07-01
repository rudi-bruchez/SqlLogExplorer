using SqlLogExplorer.Backends;
using Xunit;

namespace SqlLogExplorer.Tests.Integration;

public class LocalDbBackendIntegrationTests
{
    [IntegrationFact]
    public async Task ParseLogAsync_YieldsRecordsFromTrn()
    {
        var trn = Environment.GetEnvironmentVariable("SQLLOGEXPLORER_TEST_TRN");
        Assert.False(string.IsNullOrEmpty(trn), "Définir SQLLOGEXPLORER_TEST_TRN vers un .trn de test.");

        var backend = new LocalDbBackend();
        await backend.InitializeAsync();
        try
        {
            var count = 0;
            await foreach (var record in backend.ParseLogAsync(new[] { trn! }))
            {
                Assert.False(string.IsNullOrEmpty(record.Lsn));
                Assert.False(string.IsNullOrEmpty(record.Operation));
                if (++count >= 10) break;
            }
            Assert.True(count > 0, "Aucun enregistrement lu depuis le .trn.");
        }
        finally
        {
            await backend.CleanupAsync();
        }
    }
}
