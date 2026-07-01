using Microsoft.Data.SqlClient;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Backends;

/// <summary>Construit une chaîne de connexion ADO.NET à partir des champs de la boîte (spec §3.1).</summary>
public static class SqlConnectionStringFactory
{
    public static string Build(ConnectionSettings s)
    {
        var b = new SqlConnectionStringBuilder { DataSource = s.Server };
        if (!string.IsNullOrEmpty(s.Database)) b.InitialCatalog = s.Database;

        if (s.Auth == SqlAuthMode.Windows)
        {
            b.IntegratedSecurity = true;
        }
        else
        {
            b.UserID = s.UserName ?? string.Empty;
            b.Password = s.Password ?? string.Empty;
        }

        b.Encrypt = s.Encrypt == EncryptMode.Mandatory
            ? SqlConnectionEncryptOption.Mandatory
            : SqlConnectionEncryptOption.Optional;
        b.TrustServerCertificate = s.TrustServerCertificate;
        return b.ConnectionString;
    }
}
