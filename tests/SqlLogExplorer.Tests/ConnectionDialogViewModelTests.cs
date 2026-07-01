using SqlLogExplorer.Models;
using SqlLogExplorer.ViewModels;
using Xunit;

namespace SqlLogExplorer.Tests;

public class ConnectionDialogViewModelTests
{
    [Fact]
    public void BuildConnectionString_ReflectsFields()
    {
        var vm = new ConnectionDialogViewModel
        {
            Server = "srv", Auth = SqlAuthMode.SqlLogin, UserName = "dba", Password = "p",
            SelectedDatabase = "AdventureWorks", Encrypt = EncryptMode.Mandatory, TrustServerCertificate = true,
        };

        var cs = vm.BuildConnectionString();

        Assert.Contains("Data Source=srv", cs);
        Assert.Contains("User ID=dba", cs);
        Assert.Contains("Initial Catalog=AdventureWorks", cs);
    }
}
