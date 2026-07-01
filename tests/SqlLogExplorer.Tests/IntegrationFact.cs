using Xunit;

namespace SqlLogExplorer.Tests;

/// <summary>Fact d'intégration : exécuté seulement si SQLLOGEXPLORER_RUN_INTEGRATION=1.</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SQLLOGEXPLORER_RUN_INTEGRATION") != "1")
        {
            Skip = "Test d'intégration désactivé (SQLLOGEXPLORER_RUN_INTEGRATION != 1).";
        }
    }
}
