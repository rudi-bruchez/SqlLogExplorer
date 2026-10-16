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

    [Fact]
    public async Task ASchemaSource_NeverResolvesARange()
    {
        // Une source de schéma n'a pas besoin de fn_dblog, que ses connexions sans sysadmin ne peuvent pas lire :
        // aucune connexion n'est ouverte, même avec une fenêtre de temps remplie (serveur injoignable ici).
        var vm = new ConnectionDialogViewModel
        {
            Server = "127.0.0.1,1", Auth = SqlAuthMode.SqlLogin, UserName = "u", Password = "p", SelectedDatabase = "db",
            StartTime = new System.DateTimeOffset(2023, 1, 1, 0, 0, 0, System.TimeSpan.Zero), IsSchemaSource = true,
        };

        using var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(5));
        Assert.Null(await vm.ResolveRangeAsync(cts.Token));
    }

    [Fact]
    public void GetResolvedStartTime_CombinesDateAndTime()
    {
        var vm = new ConnectionDialogViewModel
        {
            StartTime = new System.DateTimeOffset(2023, 1, 1, 0, 0, 0, System.TimeSpan.Zero),
            StartTimeOfDay = new System.TimeSpan(14, 30, 0)
        };

        var resolved = vm.GetResolvedStartTime();
        Assert.Equal(new System.DateTime(2023, 1, 1, 14, 30, 0), resolved);
    }

    [Fact]
    public void GetResolvedEndTime_CombinesDateAndTime()
    {
        var vm = new ConnectionDialogViewModel
        {
            EndTime = new System.DateTimeOffset(2023, 1, 2, 0, 0, 0, System.TimeSpan.Zero),
            EndTimeOfDay = new System.TimeSpan(16, 45, 0)
        };

        var resolved = vm.GetResolvedEndTime();
        Assert.Equal(new System.DateTime(2023, 1, 2, 16, 45, 0), resolved);
    }
}
