using Microsoft.Data.SqlClient;
using SqlLogExplorer.Backends;
using System;
using System.Threading.Tasks;
using Xunit;

namespace SqlLogExplorer.Tests.Integration;

public class LiveLsnResolverIntegrationTests
{
    [IntegrationFact]
    public async Task ResolveAsync_ReturnsBoundsWithinActiveLog()
    {
        var cs = Environment.GetEnvironmentVariable("SQLLOGEXPLORER_TEST_LIVEDB");
        Assert.False(string.IsNullOrEmpty(cs), "Définir SQLLOGEXPLORER_TEST_LIVEDB.");

        await using var cn = new SqlConnection(cs);
        await cn.OpenAsync();

        var earliest = await LiveLsnResolver.GetEarliestLogTimeAsync(cn);
        var range = await LiveLsnResolver.ResolveAsync(cn, earliest, DateTime.Now);

        // Peut être null si le log actif ne contient aucune transaction bornée ; sinon bornes non vides.
        if (range is not null)
        {
            Assert.False(string.IsNullOrEmpty(range.Start));
            Assert.False(string.IsNullOrEmpty(range.End));
        }
    }
}
