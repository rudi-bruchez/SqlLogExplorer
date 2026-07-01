namespace SqlLogExplorer.Models;

public enum SqlAuthMode { Windows, SqlLogin }
public enum EncryptMode { Optional, Mandatory }

/// <summary>Champs de la boîte de connexion (spec §3.1 / §6.1 Option A).</summary>
public sealed record ConnectionSettings(
    string Server,
    SqlAuthMode Auth,
    string? UserName,
    string? Password,
    string? Database,
    EncryptMode Encrypt,
    bool TrustServerCertificate);
