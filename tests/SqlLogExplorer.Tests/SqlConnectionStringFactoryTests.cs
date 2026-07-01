using SqlLogExplorer.Backends;
using SqlLogExplorer.Models;
using Xunit;

namespace SqlLogExplorer.Tests;

public class SqlConnectionStringFactoryTests
{
    [Fact]
    public void Build_WindowsAuth_UsesIntegratedSecurity()
    {
        var s = new ConnectionSettings("srv", SqlAuthMode.Windows, null, null, "master",
            EncryptMode.Mandatory, TrustServerCertificate: true);
        var cs = SqlConnectionStringFactory.Build(s);

        Assert.Contains("Data Source=srv", cs);
        Assert.Contains("Integrated Security=True", cs);
        Assert.Contains("Initial Catalog=master", cs);
        Assert.Contains("Trust Server Certificate=True", cs);
        Assert.DoesNotContain("Password", cs);
    }

    [Fact]
    public void Build_SqlLogin_IncludesUserAndPassword()
    {
        var s = new ConnectionSettings("srv", SqlAuthMode.SqlLogin, "dba", "p@ss", null,
            EncryptMode.Optional, TrustServerCertificate: false);
        var cs = SqlConnectionStringFactory.Build(s);

        Assert.Contains("User ID=dba", cs);
        Assert.Contains("p@ss", cs);
        Assert.DoesNotContain("Integrated Security=True", cs);
    }
}
