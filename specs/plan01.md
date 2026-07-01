# Plan d'implémentation 01 — Tranche « Import & Quantification » (MVP)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Charger **un ou plusieurs fichiers `.trn`** (chaîne de backups) via `sys.fn_dump_dblog` (backend LocalDB) OU se connecter à une base de données en direct via `sys.fn_dblog` (Live Database) — avec une **boîte de dialogue de connexion façon SSMS** et un **filtre par fenêtre temporelle** (dates → plage LSN) —, streamer les enregistrements dans un cache SQLite local, et afficher la **quantification des opérations par type et par objet** dans l'UI Avalonia.

**Architecture :** Backend `ILogParserBackend` (LocalDB / LiveDB) → flux `IAsyncEnumerable<LogRecord>` → `LogImportService` (batchs) → cache `LogCache` (SQLite) → lectures `LogQuery` (pagination + agrégats) → ViewModels Avalonia. Aucune dépendance au décodage binaire (`RowLog Contents` stockés bruts, décodés dans un plan ultérieur).

**Tech Stack :** C# 14 / .NET 10, Avalonia UI 12.0.5, CommunityToolkit.Mvvm 8.4.1, Microsoft.Data.Sqlite, Microsoft.Data.SqlClient, xUnit.

**Périmètre de CE plan (référence spec01.md) :** §2 (couches persistance + présentation), §3.1 (Live Database — **dialog de connexion + fenêtre temporelle dates→LSN** — et LocalDB), §3.4 (contraintes), §4 (cache + §4.3 quantification), §6.1 Option A (fenêtre de connexion), §6.2 (vue Statistiques). **Hors périmètre (plans suivants) :** décodeur binaire §5, backend Docker §3.2, mode serveur distant §3.3, inspecteur détaillé §6.1 #2 (grille + inspecteur).

## Global Constraints

- Cible : `net10.0`, `Nullable=enable`, `AvaloniaUseCompiledBindingsByDefault=true` (déjà dans `SqlLogExplorer.csproj`).
- Un seul projet applicatif `SqlLogExplorer` (namespace racine `SqlLogExplorer`) + un projet de test. **Ne pas** restructurer l'app existante.
- Cache : **SQLite uniquement** (pas de DuckDB). Un import = une base SQLite temporaire.
- Backend de CE plan : **LocalDB et LiveDatabase**. Les tests qui touchent LocalDB ou SQL Server sont des tests d'**intégration** gardés par la variable d'environnement `SQLLOGEXPLORER_RUN_INTEGRATION=1` ; ils sont ignorés par défaut.
- `RowLog Contents` conservés en `BLOB` bruts, jamais décodés dans ce plan.
- **Texte de l'UI en anglais** (libellés, watermarks, `StatusMessage`, titres) — spec §6. La prose et les commentaires du code peuvent rester en français.
- **Import hors thread UI et annulable** : l'extraction (bloquante) tourne via `Task.Run`, un bouton **Cancel** propage un `CancellationToken` jusqu'à `ImportAsync` (spec §3.4).
- **Cycle de vie du cache** : chaque `LogCache` supprime son fichier `.db` au `Dispose` ; le ViewModel libère l'ancien cache avant chaque import et à la fermeture (spec §4).
- **Fichiers offline : `.trn` uniquement, mais un ou plusieurs** (chaîne de backups). Un `.trn` = un appel `fn_dump_dblog` ; le backend itère sur la liste. L'ordre n'affecte pas les agrégats de ce plan. Chaque `.trn` est supposé complet et non-strippé (cf. spec §3.2). Les `.ldf` détachés restent post-MVP (spec §1.2).
- Schéma de cache exact : table `LogRecords(Id, LSN, Operation, Context, TransactionId, AllocUnitName, RowLogContents0, RowLogContents1)` (spec §4.1).
- Colonnes `fn_dump_dblog`/`fn_dblog` : `[Current LSN]`, `[Operation]`, `[Context]`, `[Transaction ID]`, `[AllocUnitName]`, `[RowLog Contents 0]`, `[RowLog Contents 1]`.
- **Fenêtre temporelle (Live only)** : `fn_dblog` prend des LSN, pas des dates. La plage est résolue par un **pré-scan** des bornes de transaction (`[Begin Time]`/`[End Time]` sur `LOP_BEGIN_XACT`/`LOP_COMMIT_XACT`/`LOP_ABORT_XACT`), poussée côté serveur en `fn_dblog(@start,@end)`, et limitée au **log actif** (spec §3.1). La plage est **injectée dans le constructeur de `LiveDatabaseBackend`** (l'interface `ILogParserBackend` ne change pas). LocalDB ignore toute fenêtre.
- **Dialog de connexion (Live only)** : boîte façon SSMS/ADS (spec §3.1). Le mot de passe « mémorisé » n'est **jamais** stocké en clair — au MVP, l'historique retient les connexions **sans** mot de passe ; le stockage sécurisé (DPAPI/Keychain/libsecret) est le seul mécanisme autorisé si « remember » est implémenté.

---

## Structure des fichiers (créés dans ce plan)

Dans le projet `SqlLogExplorer` :
- `Models/LogRecord.cs` — record immuable d'un enregistrement de log.
- `Models/LogFilter.cs` — critères de filtrage (table / opération / transaction).
- `Models/Statistics.cs` — records d'agrégats (`OperationCount`, `ObjectCount`, `ObjectOperationCount`).
- `Models/LsnRange.cs` — plage LSN optionnelle (bornes `fn_dblog`).
- `Models/ConnectionSettings.cs` — champs de la boîte de connexion (serveur, auth, base, chiffrement…).
- `Backends/SqlConnectionStringFactory.cs` — construction (pure) de la chaîne de connexion depuis `ConnectionSettings`.
- `Backends/LiveLsnResolver.cs` — pré-scan dates → plage LSN + heure du plus ancien enregistrement (spec §3.1).
- `ViewModels/ConnectionDialogViewModel.cs` — logique de la boîte de connexion (build chaîne, liste des bases, historique).
- `Views/ConnectionDialog.axaml` (+ `.axaml.cs`) — boîte de dialogue de connexion façon SSMS.
- `Data/LogCache.cs` — création du schéma SQLite + insertion par batch.
- `Data/LogQuery.cs` — lectures : pagination + agrégats §4.3.
- `Backends/ILogParserBackend.cs` — interface backend (spec §3).
- `Backends/FnDumpDblogQuery.cs` — construction (pure) de la requête `fn_dump_dblog`.
- `Backends/LocalDbCommands.cs` — construction (pure) des arguments `sqllocaldb`.
- `Backends/LocalDbInstanceManager.cs` — cycle de vie de l'instance LocalDB (exécution `sqllocaldb`).
- `Backends/LocalDbBackend.cs` — orchestration + streaming `SqlDataReader`.
- `Backends/LiveDatabaseBackend.cs` — exécution directe sur base live via `fn_dblog`.
- `Services/LogImportService.cs` — pipeline backend → cache (batchs, progression, annulation).
- `ViewModels/MainWindowViewModel.cs` — **modifié** : flux de chargement.
- `ViewModels/StatisticsViewModel.cs` — quantification (§6.2).
- `Views/MainWindow.axaml` (+ `.axaml.cs`) — **modifié** : barre de chargement + onglets.

Projet de test `tests/SqlLogExplorer.Tests/` :
- `SqlLogExplorer.Tests.csproj`, `IntegrationFact.cs`, `LogCacheTests.cs`, `LogQueryTests.cs`, `FnDumpDblogQueryTests.cs`, `LocalDbCommandsTests.cs`, `LogImportServiceTests.cs`, `SqlConnectionStringFactoryTests.cs`, `LiveLsnResolverTests.cs`, `Integration/LocalDbBackendIntegrationTests.cs`, `Integration/LiveDatabaseBackendIntegrationTests.cs`, `Integration/LiveLsnResolverIntegrationTests.cs`.

---

### Task 1 : Solution, projet de test, dépendances

**Files:**
- Create: `SqlLogExplorer.sln`
- Create: `tests/SqlLogExplorer.Tests/SqlLogExplorer.Tests.csproj`
- Create: `tests/SqlLogExplorer.Tests/IntegrationFact.cs`
- Modify: `SqlLogExplorer.csproj` (ajout packages + exclusion du dossier `tests`)

**Interfaces:**
- Consumes: rien.
- Produces: `IntegrationFactAttribute` — `[IntegrationFact]` remplace `[Fact]` et skippe si `SQLLOGEXPLORER_RUN_INTEGRATION != "1"`.

- [ ] **Step 1 : Ajouter les packages data à l'app**

Run :
```bash
cd "C:/Users/rudi.bruchez.ext/OneDrive - NEOEN/Sources/Rudi/SqlLogExplorer"
dotnet add SqlLogExplorer.csproj package Microsoft.Data.Sqlite
dotnet add SqlLogExplorer.csproj package Microsoft.Data.SqlClient
```
Expected: deux `PackageReference` ajoutées, restauration OK.

- [ ] **Step 2 : Exclure `tests/` des globs de l'app**

Le projet app est à la racine ; sans exclusion, il compilerait `tests/**/*.cs`. Dans `SqlLogExplorer.csproj`, à l'intérieur du premier `<PropertyGroup>`, ajouter :
```xml
    <DefaultItemExcludes>$(DefaultItemExcludes);tests\**</DefaultItemExcludes>
```

- [ ] **Step 3 : Créer le projet de test et la solution**

Run :
```bash
dotnet new xunit -o tests/SqlLogExplorer.Tests -f net10.0
dotnet add tests/SqlLogExplorer.Tests/SqlLogExplorer.Tests.csproj reference SqlLogExplorer.csproj
dotnet new sln -n SqlLogExplorer
dotnet sln add SqlLogExplorer.csproj tests/SqlLogExplorer.Tests/SqlLogExplorer.Tests.csproj
```

- [ ] **Step 4 : Créer l'attribut `[IntegrationFact]`**

`tests/SqlLogExplorer.Tests/IntegrationFact.cs` :
```csharp
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
```

- [ ] **Step 5 : Vérifier build + test**

Run : `dotnet test`
Expected : build OK des deux projets, `Passed! - Failed: 0` (1 test généré par le template, ou 0 après suppression). Supprimer `tests/SqlLogExplorer.Tests/UnitTest1.cs` s'il existe.

- [ ] **Step 6 : Commit**

```bash
git add -A
git commit -m "chore: add test project, sln, data packages, IntegrationFact"
```

---

### Task 2 : Modèles (LogRecord, LogFilter, agrégats)

**Files:**
- Create: `Models/LogRecord.cs`, `Models/LogFilter.cs`, `Models/Statistics.cs`, `Models/LsnRange.cs`
- Test: `tests/SqlLogExplorer.Tests/ModelsTests.cs`

**Interfaces:**
- Produces:
  - `LogRecord(string Lsn, string Operation, string? Context, string? TransactionId, string? AllocUnitName, byte[]? RowLogContents0, byte[]? RowLogContents1)`
  - `LogFilter(string? AllocUnitName = null, string? Operation = null, string? TransactionId = null)` avec `bool IsEmpty`.
  - `OperationCount(string Operation, long Count)`, `ObjectCount(string AllocUnitName, long Count)`, `ObjectOperationCount(string AllocUnitName, string Operation, long Count)`.
  - `LsnRange(string Start, string End)` — bornes LSN au format `fn_dblog` (`vlf:block:slot`) pour `fn_dblog(@start,@end)`.

- [ ] **Step 1 : Test de la sémantique `LogFilter.IsEmpty`**

`tests/SqlLogExplorer.Tests/ModelsTests.cs` :
```csharp
using SqlLogExplorer.Models;
using Xunit;

namespace SqlLogExplorer.Tests;

public class ModelsTests
{
    [Fact]
    public void EmptyFilter_IsEmpty_IsTrue()
    {
        Assert.True(new LogFilter().IsEmpty);
    }

    [Fact]
    public void FilterWithOperation_IsEmpty_IsFalse()
    {
        Assert.False(new LogFilter(Operation: "LOP_INSERT_ROWS").IsEmpty);
    }
}
```

- [ ] **Step 2 : Vérifier l'échec de compilation/test**

Run : `dotnet test --filter FullyQualifiedName~ModelsTests`
Expected : FAIL — `LogFilter`/`LogRecord` introuvables.

- [ ] **Step 3 : Créer les modèles**

`Models/LogRecord.cs` :
```csharp
namespace SqlLogExplorer.Models;

/// <summary>Un enregistrement brut du journal (RowLog Contents non décodés).</summary>
public sealed record LogRecord(
    string Lsn,
    string Operation,
    string? Context,
    string? TransactionId,
    string? AllocUnitName,
    byte[]? RowLogContents0,
    byte[]? RowLogContents1);
```

`Models/LogFilter.cs` :
```csharp
namespace SqlLogExplorer.Models;

/// <summary>Critères de filtrage appliqués aux lectures et aux agrégats (§4.3).</summary>
public sealed record LogFilter(
    string? AllocUnitName = null,
    string? Operation = null,
    string? TransactionId = null)
{
    public bool IsEmpty =>
        string.IsNullOrEmpty(AllocUnitName)
        && string.IsNullOrEmpty(Operation)
        && string.IsNullOrEmpty(TransactionId);
}
```

`Models/Statistics.cs` :
```csharp
namespace SqlLogExplorer.Models;

public sealed record OperationCount(string Operation, long Count);
public sealed record ObjectCount(string AllocUnitName, long Count);
public sealed record ObjectOperationCount(string AllocUnitName, string Operation, long Count);
```

`Models/LsnRange.cs` :
```csharp
namespace SqlLogExplorer.Models;

/// <summary>Bornes LSN au format fn_dblog (vlf:block:slot) passées à fn_dblog(@start,@end) (spec §3.1).</summary>
public sealed record LsnRange(string Start, string End);
```

- [ ] **Step 4 : Vérifier le succès**

Run : `dotnet test --filter FullyQualifiedName~ModelsTests`
Expected : PASS (2 tests).

- [ ] **Step 5 : Commit**

```bash
git add Models tests/SqlLogExplorer.Tests/ModelsTests.cs
git commit -m "feat: add LogRecord, LogFilter, statistics models"
```

---

### Task 3 : LogCache — création du schéma SQLite

**Files:**
- Create: `Data/LogCache.cs`
- Test: `tests/SqlLogExplorer.Tests/LogCacheTests.cs`

**Interfaces:**
- Produces:
  - `static Task<LogCache> LogCache.CreateAsync(string databasePath, CancellationToken ct = default)`
  - `SqliteConnection LogCache.Connection { get; }`
  - `LogCache : IAsyncDisposable, IDisposable`
- Consumes: rien.

- [ ] **Step 1 : Test — la table et les index existent après création**

`tests/SqlLogExplorer.Tests/LogCacheTests.cs` :
```csharp
using Microsoft.Data.Sqlite;
using SqlLogExplorer.Data;
using Xunit;

namespace SqlLogExplorer.Tests;

public class LogCacheTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db");

    [Fact]
    public async Task CreateAsync_CreatesLogRecordsTableAndIndexes()
    {
        var path = TempDb();
        await using var cache = await LogCache.CreateAsync(path);

        await using var cmd = cache.Connection.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table','index') AND name IN " +
            "('LogRecords','IX_LogRecords_AllocUnitName','IX_LogRecords_Operation','IX_LogRecords_Alloc_Op');";
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(4, count);
    }
}
```

- [ ] **Step 2 : Vérifier l'échec**

Run : `dotnet test --filter FullyQualifiedName~LogCacheTests`
Expected : FAIL — `LogCache` introuvable.

- [ ] **Step 3 : Implémenter `LogCache` (schéma seulement)**

`Data/LogCache.cs` :
```csharp
using Microsoft.Data.Sqlite;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Data;

/// <summary>Cache SQLite local d'un import (spec §4). Une instance = une base.</summary>
public sealed class LogCache : IAsyncDisposable, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _databasePath;

    private LogCache(SqliteConnection connection, string databasePath)
    {
        _connection = connection;
        _databasePath = databasePath;
    }

    public SqliteConnection Connection => _connection;

    public static async Task<LogCache> CreateAsync(string databasePath, CancellationToken ct = default)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(ct);
        await InitializeSchemaAsync(connection, ct);
        return new LogCache(connection, databasePath);
    }

    private static async Task InitializeSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        // Bulk-load tuning (spec §4) : la base est jetable, on privilégie le débit sur la durabilité.
        const string sql = """
            PRAGMA journal_mode = MEMORY;
            PRAGMA synchronous  = OFF;
            PRAGMA temp_store   = MEMORY;

            CREATE TABLE IF NOT EXISTS LogRecords (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                LSN             TEXT NOT NULL,
                Operation       TEXT NOT NULL,
                Context         TEXT,
                TransactionId   TEXT,
                AllocUnitName   TEXT,
                RowLogContents0 BLOB,
                RowLogContents1 BLOB
            );
            CREATE INDEX IF NOT EXISTS IX_LogRecords_AllocUnitName ON LogRecords(AllocUnitName);
            CREATE INDEX IF NOT EXISTS IX_LogRecords_TransactionId ON LogRecords(TransactionId);
            CREATE INDEX IF NOT EXISTS IX_LogRecords_Operation     ON LogRecords(Operation);
            CREATE INDEX IF NOT EXISTS IX_LogRecords_Alloc_Op      ON LogRecords(AllocUnitName, Operation);
            """;
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        DeleteFile();
    }

    public void Dispose()
    {
        _connection.Dispose();
        DeleteFile();
    }

    // La base est temporaire : on supprime le fichier après fermeture (spec §4, cache lifecycle).
    // ClearPool libère le handle conservé par le pool de connexions avant la suppression.
    private void DeleteFile()
    {
        SqliteConnection.ClearPool(_connection);
        try
        {
            if (File.Exists(_databasePath)) File.Delete(_databasePath);
        }
        catch (IOException) { /* best-effort : un handle résiduel ne doit pas faire planter la fermeture. */ }
    }
}
```

- [ ] **Step 4 : Vérifier le succès (schéma + suppression du fichier au Dispose)**

Ajouter à `LogCacheTests` :
```csharp
    [Fact]
    public async Task Dispose_DeletesDatabaseFile()
    {
        var path = TempDb();
        await using (var cache = await LogCache.CreateAsync(path))
        {
            Assert.True(File.Exists(path));
        }
        Assert.False(File.Exists(path));
    }
```

Run : `dotnet test --filter FullyQualifiedName~LogCacheTests`
Expected : PASS.

- [ ] **Step 5 : Commit**

```bash
git add Data/LogCache.cs tests/SqlLogExplorer.Tests/LogCacheTests.cs
git commit -m "feat: LogCache creates SQLite schema and indexes"
```

---

### Task 4 : LogCache — insertion par batch

**Files:**
- Modify: `Data/LogCache.cs`
- Test: `tests/SqlLogExplorer.Tests/LogCacheTests.cs`

**Interfaces:**
- Produces: `Task<long> LogCache.InsertBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken ct = default)` → nombre inséré ; insertion dans une transaction unique.

- [ ] **Step 1 : Test — round-trip d'un batch, y compris NULL et BLOB**

Ajouter à `LogCacheTests` :
```csharp
    [Fact]
    public async Task InsertBatchAsync_PersistsRowsWithNullsAndBlobs()
    {
        await using var cache = await LogCache.CreateAsync(TempDb());
        var records = new List<Models.LogRecord>
        {
            new("00000021:000000b4:0002", "LOP_INSERT_ROWS", "LCX_HEAP", "0000:0000abcd", "dbo.Clients",
                new byte[] { 0x10, 0x00 }, null),
            new("00000021:000000b4:0003", "LOP_DELETE_ROWS", null, null, null, null, null),
        };

        var inserted = await cache.InsertBatchAsync(records);

        Assert.Equal(2, inserted);
        await using var cmd = cache.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM LogRecords WHERE AllocUnitName IS NULL;";
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
    }
```

- [ ] **Step 2 : Vérifier l'échec**

Run : `dotnet test --filter FullyQualifiedName~LogCacheTests`
Expected : FAIL — `InsertBatchAsync` introuvable.

- [ ] **Step 3 : Implémenter `InsertBatchAsync`**

Ajouter dans `LogCache` (avant `DisposeAsync`) :
```csharp
    public async Task<long> InsertBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return 0;

        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO LogRecords (LSN, Operation, Context, TransactionId, AllocUnitName, RowLogContents0, RowLogContents1)
            VALUES ($lsn, $op, $ctx, $txid, $alloc, $rlc0, $rlc1);
            """;

        var pLsn   = AddParam(cmd, "$lsn");
        var pOp    = AddParam(cmd, "$op");
        var pCtx   = AddParam(cmd, "$ctx");
        var pTxId  = AddParam(cmd, "$txid");
        var pAlloc = AddParam(cmd, "$alloc");
        var pRlc0  = AddParam(cmd, "$rlc0");
        var pRlc1  = AddParam(cmd, "$rlc1");

        long inserted = 0;
        foreach (var r in records)
        {
            ct.ThrowIfCancellationRequested();
            pLsn.Value   = r.Lsn;
            pOp.Value    = r.Operation;
            pCtx.Value   = (object?)r.Context ?? DBNull.Value;
            pTxId.Value  = (object?)r.TransactionId ?? DBNull.Value;
            pAlloc.Value = (object?)r.AllocUnitName ?? DBNull.Value;
            pRlc0.Value  = (object?)r.RowLogContents0 ?? DBNull.Value;
            pRlc1.Value  = (object?)r.RowLogContents1 ?? DBNull.Value;
            inserted += await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return inserted;
    }

    private static SqliteParameter AddParam(SqliteCommand cmd, string name)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }
```

- [ ] **Step 4 : Vérifier le succès**

Run : `dotnet test --filter FullyQualifiedName~LogCacheTests`
Expected : PASS (3 tests).

- [ ] **Step 5 : Commit**

```bash
git add Data/LogCache.cs tests/SqlLogExplorer.Tests/LogCacheTests.cs
git commit -m "feat: LogCache batch insert in a single transaction"
```

---

### Task 5 : LogQuery — pagination filtrée

**Files:**
- Create: `Data/LogQuery.cs`
- Test: `tests/SqlLogExplorer.Tests/LogQueryTests.cs`

**Interfaces:**
- Consumes: `LogCache.Connection`, `LogRecord`, `LogFilter`.
- Produces:
  - `LogQuery(SqliteConnection connection)`
  - `Task<IReadOnlyList<LogRecord>> GetPageAsync(int offset, int limit, LogFilter? filter = null, CancellationToken ct = default)` (tri par `Id`).

- [ ] **Step 1 : Test — pagination + filtre par opération**

`tests/SqlLogExplorer.Tests/LogQueryTests.cs` :
```csharp
using Microsoft.Data.Sqlite;
using SqlLogExplorer.Data;
using SqlLogExplorer.Models;
using Xunit;

namespace SqlLogExplorer.Tests;

public class LogQueryTests
{
    private static async Task<LogCache> SeedAsync()
    {
        var cache = await LogCache.CreateAsync(Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db"));
        var records = new List<LogRecord>();
        for (int i = 0; i < 5; i++)
            records.Add(new($"lsn{i}", "LOP_INSERT_ROWS", "LCX_HEAP", $"tx{i}", "dbo.Clients", null, null));
        for (int i = 0; i < 3; i++)
            records.Add(new($"lsnd{i}", "LOP_DELETE_ROWS", "LCX_HEAP", $"txd{i}", "dbo.Orders", null, null));
        await cache.InsertBatchAsync(records);
        return cache;
    }

    [Fact]
    public async Task GetPageAsync_RespectsLimitOffsetAndOrder()
    {
        await using var cache = await SeedAsync();
        var query = new LogQuery(cache.Connection);

        var page = await query.GetPageAsync(offset: 2, limit: 2);

        Assert.Equal(2, page.Count);
        Assert.Equal("lsn2", page[0].Lsn);
        Assert.Equal("lsn3", page[1].Lsn);
    }

    [Fact]
    public async Task GetPageAsync_FiltersByOperation()
    {
        await using var cache = await SeedAsync();
        var query = new LogQuery(cache.Connection);

        var page = await query.GetPageAsync(0, 100, new LogFilter(Operation: "LOP_DELETE_ROWS"));

        Assert.Equal(3, page.Count);
        Assert.All(page, r => Assert.Equal("LOP_DELETE_ROWS", r.Operation));
    }
}
```

- [ ] **Step 2 : Vérifier l'échec**

Run : `dotnet test --filter FullyQualifiedName~LogQueryTests`
Expected : FAIL — `LogQuery` introuvable.

- [ ] **Step 3 : Implémenter `LogQuery.GetPageAsync` + le constructeur de filtre**

`Data/LogQuery.cs` :
```csharp
using Microsoft.Data.Sqlite;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Data;

/// <summary>Lectures sur le cache : pagination virtuelle et agrégats (§4.3, §6).</summary>
public sealed class LogQuery
{
    private readonly SqliteConnection _connection;

    public LogQuery(SqliteConnection connection) => _connection = connection;

    public async Task<IReadOnlyList<LogRecord>> GetPageAsync(
        int offset, int limit, LogFilter? filter = null, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        var where = AppendFilter(cmd, filter);
        cmd.CommandText =
            "SELECT LSN, Operation, Context, TransactionId, AllocUnitName, RowLogContents0, RowLogContents1 " +
            $"FROM LogRecords{where} ORDER BY Id LIMIT $limit OFFSET $offset;";
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var list = new List<LogRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new LogRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : (byte[])reader[5],
                reader.IsDBNull(6) ? null : (byte[])reader[6]));
        }
        return list;
    }

    /// <summary>Ajoute les paramètres de filtre à <paramref name="cmd"/> et renvoie la clause WHERE (préfixée d'un espace) ou "".</summary>
    internal static string AppendFilter(SqliteCommand cmd, LogFilter? filter)
    {
        if (filter is null || filter.IsEmpty) return string.Empty;

        var clauses = new List<string>();
        if (!string.IsNullOrEmpty(filter.AllocUnitName))
        {
            clauses.Add("AllocUnitName LIKE $f_alloc");
            cmd.Parameters.AddWithValue("$f_alloc", $"%{filter.AllocUnitName}%");
        }
        if (!string.IsNullOrEmpty(filter.Operation))
        {
            clauses.Add("Operation = $f_op");
            cmd.Parameters.AddWithValue("$f_op", filter.Operation);
        }
        if (!string.IsNullOrEmpty(filter.TransactionId))
        {
            clauses.Add("TransactionId = $f_txid");
            cmd.Parameters.AddWithValue("$f_txid", filter.TransactionId);
        }
        return clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses);
    }
}
```

- [ ] **Step 4 : Vérifier le succès**

Run : `dotnet test --filter FullyQualifiedName~LogQueryTests`
Expected : PASS (2 tests).

- [ ] **Step 5 : Commit**

```bash
git add Data/LogQuery.cs tests/SqlLogExplorer.Tests/LogQueryTests.cs
git commit -m "feat: LogQuery paginated filtered read"
```

---

### Task 6 : LogQuery — agrégat par type d'opération

**Files:**
- Modify: `Data/LogQuery.cs`
- Test: `tests/SqlLogExplorer.Tests/LogQueryTests.cs`

**Interfaces:**
- Produces: `Task<IReadOnlyList<OperationCount>> CountByOperationAsync(LogFilter? filter = null, CancellationToken ct = default)` — trié par comptage décroissant.

- [ ] **Step 1 : Test — comptage par opération, tri décroissant + filtre**

Ajouter à `LogQueryTests` :
```csharp
    [Fact]
    public async Task CountByOperationAsync_GroupsAndOrdersDescending()
    {
        await using var cache = await SeedAsync();
        var query = new LogQuery(cache.Connection);

        var result = await query.CountByOperationAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal("LOP_INSERT_ROWS", result[0].Operation);
        Assert.Equal(5, result[0].Count);
        Assert.Equal("LOP_DELETE_ROWS", result[1].Operation);
        Assert.Equal(3, result[1].Count);
    }

    [Fact]
    public async Task CountByOperationAsync_HonorsFilter()
    {
        await using var cache = await SeedAsync();
        var query = new LogQuery(cache.Connection);

        var result = await query.CountByOperationAsync(new LogFilter(AllocUnitName: "Orders"));

        Assert.Single(result);
        Assert.Equal("LOP_DELETE_ROWS", result[0].Operation);
    }
```

- [ ] **Step 2 : Vérifier l'échec**

Run : `dotnet test --filter FullyQualifiedName~LogQueryTests`
Expected : FAIL — `CountByOperationAsync` introuvable.

- [ ] **Step 3 : Implémenter `CountByOperationAsync`**

Ajouter dans `LogQuery` :
```csharp
    public async Task<IReadOnlyList<OperationCount>> CountByOperationAsync(
        LogFilter? filter = null, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        var where = AppendFilter(cmd, filter);
        cmd.CommandText =
            $"SELECT Operation, COUNT(*) AS Nb FROM LogRecords{where} GROUP BY Operation ORDER BY Nb DESC;";

        var list = new List<OperationCount>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new OperationCount(reader.GetString(0), reader.GetInt64(1)));
        return list;
    }
```

- [ ] **Step 4 : Vérifier le succès**

Run : `dotnet test --filter FullyQualifiedName~LogQueryTests`
Expected : PASS.

- [ ] **Step 5 : Commit**

```bash
git add Data/LogQuery.cs tests/SqlLogExplorer.Tests/LogQueryTests.cs
git commit -m "feat: LogQuery aggregate count by operation"
```

---

### Task 7 : LogQuery — agrégats par objet et croisé objet × opération

**Files:**
- Modify: `Data/LogQuery.cs`
- Test: `tests/SqlLogExplorer.Tests/LogQueryTests.cs`

**Interfaces:**
- Produces:
  - `Task<IReadOnlyList<ObjectCount>> CountByObjectAsync(LogFilter? filter = null, CancellationToken ct = default)` — trié décroissant, exclut `AllocUnitName IS NULL`.
  - `Task<IReadOnlyList<ObjectOperationCount>> CountByObjectAndOperationAsync(LogFilter? filter = null, CancellationToken ct = default)`.

- [ ] **Step 1 : Test — par objet et matrice croisée**

Ajouter à `LogQueryTests` :
```csharp
    [Fact]
    public async Task CountByObjectAsync_GroupsByAllocUnit()
    {
        await using var cache = await SeedAsync();
        var query = new LogQuery(cache.Connection);

        var result = await query.CountByObjectAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal("dbo.Clients", result[0].AllocUnitName);
        Assert.Equal(5, result[0].Count);
    }

    [Fact]
    public async Task CountByObjectAndOperationAsync_ReturnsMatrix()
    {
        await using var cache = await SeedAsync();
        var query = new LogQuery(cache.Connection);

        var result = await query.CountByObjectAndOperationAsync();

        Assert.Contains(result, x => x.AllocUnitName == "dbo.Clients" && x.Operation == "LOP_INSERT_ROWS" && x.Count == 5);
        Assert.Contains(result, x => x.AllocUnitName == "dbo.Orders"  && x.Operation == "LOP_DELETE_ROWS" && x.Count == 3);
    }
```

- [ ] **Step 2 : Vérifier l'échec**

Run : `dotnet test --filter FullyQualifiedName~LogQueryTests`
Expected : FAIL — méthodes introuvables.

- [ ] **Step 3 : Implémenter les deux méthodes**

Ajouter dans `LogQuery` :
```csharp
    public async Task<IReadOnlyList<ObjectCount>> CountByObjectAsync(
        LogFilter? filter = null, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        var where = AppendFilter(cmd, filter);
        var and = where.Length == 0 ? " WHERE" : where + " AND";
        cmd.CommandText =
            $"SELECT AllocUnitName, COUNT(*) AS Nb FROM LogRecords{and} AllocUnitName IS NOT NULL " +
            "GROUP BY AllocUnitName ORDER BY Nb DESC;";

        var list = new List<ObjectCount>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new ObjectCount(reader.GetString(0), reader.GetInt64(1)));
        return list;
    }

    public async Task<IReadOnlyList<ObjectOperationCount>> CountByObjectAndOperationAsync(
        LogFilter? filter = null, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        var where = AppendFilter(cmd, filter);
        var and = where.Length == 0 ? " WHERE" : where + " AND";
        cmd.CommandText =
            $"SELECT AllocUnitName, Operation, COUNT(*) AS Nb FROM LogRecords{and} AllocUnitName IS NOT NULL " +
            "GROUP BY AllocUnitName, Operation ORDER BY AllocUnitName, Nb DESC;";

        var list = new List<ObjectOperationCount>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new ObjectOperationCount(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        return list;
    }
```

- [ ] **Step 4 : Vérifier le succès**

Run : `dotnet test --filter FullyQualifiedName~LogQueryTests`
Expected : PASS.

- [ ] **Step 5 : Commit**

```bash
git add Data/LogQuery.cs tests/SqlLogExplorer.Tests/LogQueryTests.cs
git commit -m "feat: LogQuery aggregates by object and object x operation"
```

---

### Task 8 : Interface backend + requête `fn_dump_dblog`

**Files:**
- Create: `Backends/ILogParserBackend.cs`, `Backends/FnDumpDblogQuery.cs`
- Test: `tests/SqlLogExplorer.Tests/FnDumpDblogQueryTests.cs`

**Interfaces:**
- Produces:
  - `interface ILogParserBackend { Task InitializeAsync(CancellationToken ct = default); IAsyncEnumerable<LogRecord> ParseLogAsync(IReadOnlyList<string> targets, CancellationToken ct = default); Task CleanupAsync(CancellationToken ct = default); }` — `targets` = liste de chemins `.trn` (LocalDB, un appel par fichier) ou liste à un élément contenant la chaîne de connexion (Live DB).
  - `static class FnDumpDblogQuery { const int DefaultParameterCount = 63; static string Build(string pathParameterName, int defaultParameterCount = DefaultParameterCount); }`

- [ ] **Step 1 : Test — la requête contient les colonnes attendues et le bon nombre de DEFAULT**

`tests/SqlLogExplorer.Tests/FnDumpDblogQueryTests.cs` :
```csharp
using SqlLogExplorer.Backends;
using Xunit;

namespace SqlLogExplorer.Tests;

public class FnDumpDblogQueryTests
{
    [Fact]
    public void Build_ContainsRequiredColumnsAndFunction()
    {
        var sql = FnDumpDblogQuery.Build("@path");

        Assert.Contains("[Current LSN]", sql);
        Assert.Contains("[Transaction ID]", sql);
        Assert.Contains("[RowLog Contents 0]", sql);
        Assert.Contains("[RowLog Contents 1]", sql);
        Assert.Contains("sys.fn_dump_dblog(NULL, NULL, N'DISK', 1, @path,", sql);
    }

    [Fact]
    public void Build_EmitsRequestedNumberOfDefaults()
    {
        var sql = FnDumpDblogQuery.Build("@path", defaultParameterCount: 63);

        var count = sql.Split("DEFAULT").Length - 1;
        Assert.Equal(63, count);
    }
}
```

- [ ] **Step 2 : Vérifier l'échec**

Run : `dotnet test --filter FullyQualifiedName~FnDumpDblogQueryTests`
Expected : FAIL — types introuvables.

- [ ] **Step 3 : Créer l'interface et le constructeur de requête**

`Backends/ILogParserBackend.cs` :
```csharp
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Backends;

/// <summary>Extrait les enregistrements bruts d'un fichier de log (spec §3).</summary>
public interface ILogParserBackend
{
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Streame les enregistrements des cibles fournies.
    /// LocalDB : une entrée = un fichier .trn (un appel fn_dump_dblog par fichier).
    /// Live DB : une seule entrée = la chaîne de connexion.
    /// </summary>
    IAsyncEnumerable<LogRecord> ParseLogAsync(IReadOnlyList<string> targets, CancellationToken ct = default);

    Task CleanupAsync(CancellationToken ct = default);
}
```

`Backends/FnDumpDblogQuery.cs` :
```csharp
namespace SqlLogExplorer.Backends;

/// <summary>
/// Construit l'appel à <c>sys.fn_dump_dblog</c> (spec §3.4). La fonction prend
/// 5 paramètres positionnels puis une longue liste de DEFAULT ; le nombre exact
/// varie selon la version de SQL Server et doit être validé par un test d'intégration.
/// </summary>
public static class FnDumpDblogQuery
{
    /// <summary>5 positionnels + 63 DEFAULT = 68 paramètres (SQL Server 2019/2022).</summary>
    public const int DefaultParameterCount = 63;

    public static string Build(string pathParameterName, int defaultParameterCount = DefaultParameterCount)
    {
        var defaults = string.Join(", ", Enumerable.Repeat("DEFAULT", defaultParameterCount));
        return $"""
            SELECT [Current LSN]        AS Lsn,
                   [Operation]          AS Operation,
                   [Context]            AS Context,
                   [Transaction ID]     AS TransactionId,
                   [AllocUnitName]      AS AllocUnitName,
                   [RowLog Contents 0]  AS RowLogContents0,
                   [RowLog Contents 1]  AS RowLogContents1
            FROM sys.fn_dump_dblog(NULL, NULL, N'DISK', 1, {pathParameterName}, {defaults});
            """;
    }
}
```

- [ ] **Step 4 : Vérifier le succès**

Run : `dotnet test --filter FullyQualifiedName~FnDumpDblogQueryTests`
Expected : PASS (2 tests).

- [ ] **Step 5 : Commit**

```bash
git add Backends/ILogParserBackend.cs Backends/FnDumpDblogQuery.cs tests/SqlLogExplorer.Tests/FnDumpDblogQueryTests.cs
git commit -m "feat: ILogParserBackend interface and fn_dump_dblog query builder"
```

---

### Task 9 : Cycle de vie de l'instance LocalDB

**Files:**
- Create: `Backends/LocalDbCommands.cs`, `Backends/LocalDbInstanceManager.cs`
- Test: `tests/SqlLogExplorer.Tests/LocalDbCommandsTests.cs`

**Interfaces:**
- Produces:
  - `static class LocalDbCommands { static string Create(string instance); static string Start(string instance); static string Stop(string instance); }`
  - `sealed class LocalDbInstanceManager { LocalDbInstanceManager(string instanceName = "SqlLogExplorerInstance"); Task EnsureStartedAsync(CancellationToken ct = default); Task StopAsync(CancellationToken ct = default); string ConnectionString { get; } }`

- [ ] **Step 1 : Test (pur) — construction des arguments `sqllocaldb`**

`tests/SqlLogExplorer.Tests/LocalDbCommandsTests.cs` :
```csharp
using SqlLogExplorer.Backends;
using Xunit;

namespace SqlLogExplorer.Tests;

public class LocalDbCommandsTests
{
    [Fact]
    public void Create_QuotesInstanceName()
        => Assert.Equal("create \"SqlLogExplorerInstance\"", LocalDbCommands.Create("SqlLogExplorerInstance"));

    [Fact]
    public void Start_QuotesInstanceName()
        => Assert.Equal("start \"SqlLogExplorerInstance\"", LocalDbCommands.Start("SqlLogExplorerInstance"));
}
```

- [ ] **Step 2 : Vérifier l'échec**

Run : `dotnet test --filter FullyQualifiedName~LocalDbCommandsTests`
Expected : FAIL — `LocalDbCommands` introuvable.

- [ ] **Step 3 : Implémenter `LocalDbCommands` et `LocalDbInstanceManager`**

`Backends/LocalDbCommands.cs` :
```csharp
namespace SqlLogExplorer.Backends;

/// <summary>Construit les arguments de l'utilitaire <c>sqllocaldb</c>.</summary>
public static class LocalDbCommands
{
    public static string Create(string instance) => $"create \"{instance}\"";
    public static string Start(string instance)  => $"start \"{instance}\"";
    public static string Stop(string instance)   => $"stop \"{instance}\"";
}
```

`Backends/LocalDbInstanceManager.cs` :
```csharp
using System.Diagnostics;

namespace SqlLogExplorer.Backends;

/// <summary>Crée/démarre/arrête l'instance LocalDB dédiée (Windows uniquement, spec §3.1).</summary>
public sealed class LocalDbInstanceManager
{
    private readonly string _instanceName;
    private bool _startedByUs;

    public LocalDbInstanceManager(string instanceName = "SqlLogExplorerInstance")
        => _instanceName = instanceName;

    public string ConnectionString =>
        $"Server=(localdb)\\{_instanceName};Integrated Security=true;TrustServerCertificate=true;";

    public async Task EnsureStartedAsync(CancellationToken ct = default)
    {
        // create est idempotent côté effet : ignore l'échec si l'instance existe déjà.
        await RunAsync(LocalDbCommands.Create(_instanceName), throwOnError: false, ct);
        await RunAsync(LocalDbCommands.Start(_instanceName), throwOnError: true, ct);
        _startedByUs = true;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_startedByUs) return;
        await RunAsync(LocalDbCommands.Stop(_instanceName), throwOnError: false, ct);
        _startedByUs = false;
    }

    private static async Task RunAsync(string arguments, bool throwOnError, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("sqllocaldb", arguments)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Impossible de démarrer sqllocaldb.");
        await process.WaitForExitAsync(ct);
        if (throwOnError && process.ExitCode != 0)
        {
            var err = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"sqllocaldb {arguments} a échoué : {err}");
        }
    }
}
```

- [ ] **Step 4 : Vérifier le succès (tests unitaires purs)**

Run : `dotnet test --filter FullyQualifiedName~LocalDbCommandsTests`
Expected : PASS (2 tests). `LocalDbInstanceManager` (qui lance un process) est couvert en intégration en Task 10.

- [ ] **Step 5 : Commit**

```bash
git add Backends/LocalDbCommands.cs Backends/LocalDbInstanceManager.cs tests/SqlLogExplorer.Tests/LocalDbCommandsTests.cs
git commit -m "feat: LocalDB instance lifecycle (commands + manager)"
```

---

### Task 10 : Backend LocalDB — streaming `fn_dump_dblog`

**Files:**
- Create: `Backends/LocalDbBackend.cs`
- Test: `tests/SqlLogExplorer.Tests/Integration/LocalDbBackendIntegrationTests.cs`

**Interfaces:**
- Consumes: `ILogParserBackend`, `FnDumpDblogQuery`, `LocalDbInstanceManager`, `LogRecord`.
- Produces: `sealed class LocalDbBackend : ILogParserBackend` avec ctor `LocalDbBackend(LocalDbInstanceManager? instanceManager = null)`.

- [ ] **Step 1 : Test d'intégration (gardé) — parser un `.trn` de test**

> Prérequis (documentés dans le test) : Windows + LocalDB installé ; un fichier `.trn` de test dont le chemin est fourni via `SQLLOGEXPLORER_TEST_TRN`. Générer ce fichier une fois avec le script `tests/fixtures/make_test_trn.sql` (créé en Step 5).

`tests/SqlLogExplorer.Tests/Integration/LocalDbBackendIntegrationTests.cs` :
```csharp
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
```

- [ ] **Step 2 : Vérifier l'échec de compilation**

Run : `dotnet build`
Expected : FAIL — `LocalDbBackend` introuvable.

- [ ] **Step 3 : Implémenter `LocalDbBackend`**

`Backends/LocalDbBackend.cs` :
```csharp
using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Backends;

/// <summary>
/// Backend Windows : exécute <c>sys.fn_dump_dblog</c> sur une instance LocalDB jetable
/// et streame le résultat via un <see cref="SqlDataReader"/> en accès séquentiel (spec §3.1).
/// </summary>
public sealed class LocalDbBackend : ILogParserBackend
{
    private readonly LocalDbInstanceManager _instanceManager;
    private int _defaultParameterCount = FnDumpDblogQuery.DefaultParameterCount;

    public LocalDbBackend(LocalDbInstanceManager? instanceManager = null)
        => _instanceManager = instanceManager ?? new LocalDbInstanceManager();

    public Task InitializeAsync(CancellationToken ct = default)
        => _instanceManager.EnsureStartedAsync(ct);

    public async IAsyncEnumerable<LogRecord> ParseLogAsync(
        IReadOnlyList<string> targets, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_instanceManager.ConnectionString);
        await connection.OpenAsync(ct);

        // Une chaîne de backups = un appel fn_dump_dblog par fichier (spec §3.2).
        // On concatène les flux ; l'ordre n'affecte pas les agrégats de ce plan.
        foreach (var path in targets)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = FnDumpDblogQuery.Build("@path", _defaultParameterCount);
            cmd.CommandTimeout = 0; // fn_dump_dblog est lent (spec §3.4) : pas de timeout.
            cmd.Parameters.Add(new SqlParameter("@path", path));

            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            while (await reader.ReadAsync(ct))
            {
                // Accès séquentiel : lire les colonnes dans l'ordre du SELECT (0..6).
                yield return new LogRecord(
                    Lsn:             reader.GetString(0),
                    Operation:       reader.GetString(1),
                    Context:         reader.IsDBNull(2) ? null : reader.GetString(2),
                    TransactionId:   reader.IsDBNull(3) ? null : reader.GetString(3),
                    AllocUnitName:   reader.IsDBNull(4) ? null : reader.GetString(4),
                    RowLogContents0: reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5),
                    RowLogContents1: reader.IsDBNull(6) ? null : (byte[])reader.GetValue(6));
            }
        }
    }

    public Task CleanupAsync(CancellationToken ct = default)
        => _instanceManager.StopAsync(ct);
}
```

- [ ] **Step 4 : Vérifier build + tests non-intégration verts**

Run : `dotnet test`
Expected : PASS ; le test d'intégration est `Skipped` (sauf si `SQLLOGEXPLORER_RUN_INTEGRATION=1`).

- [ ] **Step 5 : Ajouter le script de fixture `.trn`**

`tests/fixtures/make_test_trn.sql` :
```sql
-- Génère un .trn de test minimal pour LocalDbBackendIntegrationTests.
-- À exécuter une fois sur (localdb)\SqlLogExplorerInstance. Adapter les chemins.
CREATE DATABASE SqlLogExplorer_TestSrc;
GO
ALTER DATABASE SqlLogExplorer_TestSrc SET RECOVERY FULL;
GO
BACKUP DATABASE SqlLogExplorer_TestSrc TO DISK = N'C:\Temp\SqlLogExplorer_TestSrc_full.bak';
GO
USE SqlLogExplorer_TestSrc;
CREATE TABLE dbo.Clients (Id INT NOT NULL PRIMARY KEY, Nom VARCHAR(50) NOT NULL);
INSERT INTO dbo.Clients (Id, Nom) VALUES (1, 'Alice'), (2, 'Bob');
DELETE FROM dbo.Clients WHERE Id = 2;
GO
-- Le .trn à passer via SQLLOGEXPLORER_TEST_TRN :
BACKUP LOG SqlLogExplorer_TestSrc TO DISK = N'C:\Temp\SqlLogExplorer_TestSrc.trn';
GO
```

> Note d'implémentation : si le test d'intégration échoue avec une erreur d'arité de `fn_dump_dblog`, ajuster `_defaultParameterCount` (la valeur exacte dépend de la version de SQL Server, cf. spec §3.4).

- [ ] **Step 6 : Commit**

```bash
git add Backends/LocalDbBackend.cs tests/SqlLogExplorer.Tests/Integration/LocalDbBackendIntegrationTests.cs tests/fixtures/make_test_trn.sql
git commit -m "feat: LocalDbBackend streams fn_dump_dblog via SqlDataReader"
```

---

### Task 11 : Backend Live Database — streaming fn_dblog

**Files:**
- Create: `Backends/LiveDatabaseBackend.cs`
- Test: `tests/SqlLogExplorer.Tests/Integration/LiveDatabaseBackendIntegrationTests.cs`

**Interfaces:**
- Consumes: `ILogParserBackend`, `LogRecord`.
- Produces: `sealed class LiveDatabaseBackend : ILogParserBackend` avec ctor `LiveDatabaseBackend(LsnRange? range = null)` — `range` borne `fn_dblog(@start,@end)` ; `null` = log actif complet.

- [ ] **Step 1 : Test d'intégration (gardé)**

`tests/SqlLogExplorer.Tests/Integration/LiveDatabaseBackendIntegrationTests.cs` :
```csharp
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
```

- [ ] **Step 2 : Vérifier l'échec de compilation**

Run : `dotnet build`
Expected : FAIL — `LiveDatabaseBackend` introuvable.

- [ ] **Step 3 : Implémenter `LiveDatabaseBackend`**

`Backends/LiveDatabaseBackend.cs` :
```csharp
using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Backends;

/// <summary>
/// Backend Live DB : exécute <c>sys.fn_dblog</c> sur une base de données en direct
/// et streame le résultat via un <see cref="SqlDataReader"/> en accès séquentiel.
/// </summary>
public sealed class LiveDatabaseBackend : ILogParserBackend
{
    private readonly LsnRange? _range;

    /// <summary>Une fenêtre temporelle résolue en plage LSN (spec §3.1) borne fn_dblog ; null = log actif complet.</summary>
    public LiveDatabaseBackend(LsnRange? range = null) => _range = range;

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async IAsyncEnumerable<LogRecord> ParseLogAsync(
        IReadOnlyList<string> targets, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Live DB : une seule cible = la chaîne de connexion (fn_dblog lit le log actif).
        if (targets.Count != 1)
            throw new ArgumentException("Live DB attend exactement une chaîne de connexion.", nameof(targets));

        await using var connection = new SqlConnection(targets[0]);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        // fn_dblog(NULL,NULL) = log actif complet ; fn_dblog(@start,@end) = borné à la fenêtre résolue (spec §3.1).
        var bounds = _range is null ? "NULL, NULL" : "@start, @end";
        cmd.CommandText = $"""
            SELECT [Current LSN],
                   [Operation],
                   [Context],
                   [Transaction ID],
                   [AllocUnitName],
                   [RowLog Contents 0],
                   [RowLog Contents 1]
            FROM sys.fn_dblog({bounds});
            """;
        if (_range is not null)
        {
            cmd.Parameters.Add(new SqlParameter("@start", _range.Start));
            cmd.Parameters.Add(new SqlParameter("@end", _range.End));
        }
        cmd.CommandTimeout = 0;

        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct))
        {
            yield return new LogRecord(
                Lsn:             reader.GetString(0),
                Operation:       reader.GetString(1),
                Context:         reader.IsDBNull(2) ? null : reader.GetString(2),
                TransactionId:   reader.IsDBNull(3) ? null : reader.GetString(3),
                AllocUnitName:   reader.IsDBNull(4) ? null : reader.GetString(4),
                RowLogContents0: reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5),
                RowLogContents1: reader.IsDBNull(6) ? null : (byte[])reader.GetValue(6));
        }
    }

    public Task CleanupAsync(CancellationToken ct = default) => Task.CompletedTask;
}
```

- [ ] **Step 4 : Commit**

```bash
git add Backends/LiveDatabaseBackend.cs tests/SqlLogExplorer.Tests/Integration/LiveDatabaseBackendIntegrationTests.cs
git commit -m "feat: LiveDatabaseBackend streams fn_dblog"
```

---

### Task 12 : Pipeline d'import (backend → cache)

**Files:**
- Create: `Services/LogImportService.cs`
- Test: `tests/SqlLogExplorer.Tests/LogImportServiceTests.cs`

**Interfaces:**
- Consumes: `ILogParserBackend`, `LogCache`.
- Produces: `sealed class LogImportService { LogImportService(ILogParserBackend backend, LogCache cache, int batchSize = 1000); Task<long> ImportAsync(IReadOnlyList<string> targets, IProgress<long>? progress = null, CancellationToken ct = default); }`

- [ ] **Step 1 : Test — import via un backend factice, progression + total**

`tests/SqlLogExplorer.Tests/LogImportServiceTests.cs` :
```csharp
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
                await Task.Yield();
                yield return new LogRecord($"lsn{i}", "LOP_INSERT_ROWS", "LCX_HEAP", $"tx{i}", "dbo.T", null, null);
            }
        }
    }

    [Fact]
    public async Task ImportAsync_PersistsAllRecordsAndReportsProgress()
    {
        await using var cache = await LogCache.CreateAsync(
            Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db"));
        var service = new LogImportService(new FakeBackend(2500), cache, batchSize: 1000);
        var reports = new List<long>();

        var total = await service.ImportAsync(new[] { "ignored.trn" }, new Progress<long>(reports.Add));

        Assert.Equal(2500, total);
        var query = new LogQuery(cache.Connection);
        var page = await query.GetPageAsync(0, 5000);
        Assert.Equal(2500, page.Count);
    }
}
```

- [ ] **Step 2 : Vérifier l'échec**

Run : `dotnet test --filter FullyQualifiedName~LogImportServiceTests`
Expected : FAIL — `LogImportService` introuvable.

- [ ] **Step 3 : Implémenter `LogImportService`**

`Services/LogImportService.cs` :
```csharp
using SqlLogExplorer.Backends;
using SqlLogExplorer.Data;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Services;

/// <summary>Streame un backend vers le cache par batchs, avec progression et annulation (spec §3.4).</summary>
public sealed class LogImportService
{
    private readonly ILogParserBackend _backend;
    private readonly LogCache _cache;
    private readonly int _batchSize;

    public LogImportService(ILogParserBackend backend, LogCache cache, int batchSize = 1000)
    {
        _backend = backend;
        _cache = cache;
        _batchSize = batchSize;
    }

    /// <summary>Renvoie le nombre total d'enregistrements importés. <paramref name="progress"/> reçoit le cumul après chaque batch.</summary>
    public async Task<long> ImportAsync(IReadOnlyList<string> targets, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        await _backend.InitializeAsync(ct);
        try
        {
            var batch = new List<LogRecord>(_batchSize);
            long total = 0;

            await foreach (var record in _backend.ParseLogAsync(targets, ct).WithCancellation(ct))
            {
                batch.Add(record);
                if (batch.Count >= _batchSize)
                {
                    total += await _cache.InsertBatchAsync(batch, ct);
                    batch.Clear();
                    progress?.Report(total);
                }
            }

            if (batch.Count > 0)
            {
                total += await _cache.InsertBatchAsync(batch, ct);
                progress?.Report(total);
            }

            return total;
        }
        finally
        {
            await _backend.CleanupAsync(ct);
        }
    }
}
```

- [ ] **Step 4 : Vérifier le succès**

Run : `dotnet test --filter FullyQualifiedName~LogImportServiceTests`
Expected : PASS.

- [ ] **Step 5 : Commit**

```bash
git add Services/LogImportService.cs tests/SqlLogExplorer.Tests/LogImportServiceTests.cs
git commit -m "feat: LogImportService batches backend stream into cache"
```

---

### Task 13 : ViewModels — chargement + quantification

**Files:**
- Create: `ViewModels/StatisticsViewModel.cs`
- Modify: `ViewModels/MainWindowViewModel.cs`
- Test: `tests/SqlLogExplorer.Tests/StatisticsViewModelTests.cs`

**Interfaces:**
- Consumes: `LogCache`, `LogQuery`, `LogImportService`, `LocalDbBackend`, `LiveDatabaseBackend`.
- Produces:
  - `StatisticsViewModel` : `ObservableCollection<OperationCount> ByOperation`, `ObservableCollection<ObjectCount> ByObject`, `Task RefreshAsync(LogFilter? filter = null)`.
  - `MainWindowViewModel` (implémente `IAsyncDisposable`) : `[RelayCommand] Task LoadOfflineAsync(IReadOnlyList<string> filePaths)`, `Task LoadLiveAsync(string connectionString, LsnRange? range = null)` (méthode publique, appelée après ConnectionDialog — Task 17/18), `[RelayCommand] void Cancel()`, propriétés `bool IsLoading`, `long ImportedCount`, `string? StatusMessage`, `StatisticsViewModel Statistics`. L'import s'exécute hors thread UI (`Task.Run`) et est annulable (`CancellationTokenSource`) ; le cache précédent est libéré avant chaque import et à la fermeture.

- [ ] **Step 1 : Test — `StatisticsViewModel.RefreshAsync` remplit les collections**

`tests/SqlLogExplorer.Tests/StatisticsViewModelTests.cs` :
```csharp
using SqlLogExplorer.Data;
using SqlLogExplorer.Models;
using SqlLogExplorer.ViewModels;
using Xunit;

namespace SqlLogExplorer.Tests;

public class StatisticsViewModelTests
{
    [Fact]
    public async Task RefreshAsync_PopulatesByOperationAndByObject()
    {
        await using var cache = await LogCache.CreateAsync(
            Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db"));
        await cache.InsertBatchAsync(new List<LogRecord>
        {
            new("l1", "LOP_INSERT_ROWS", null, null, "dbo.Clients", null, null),
            new("l2", "LOP_INSERT_ROWS", null, null, "dbo.Clients", null, null),
            new("l3", "LOP_DELETE_ROWS", null, null, "dbo.Orders",  null, null),
        });
        var vm = new StatisticsViewModel(new LogQuery(cache.Connection));

        await vm.RefreshAsync();

        Assert.Equal(2, vm.ByOperation.Count);
        Assert.Equal("LOP_INSERT_ROWS", vm.ByOperation[0].Operation);
        Assert.Equal(2, vm.ByOperation[0].Count);
        Assert.Equal(2, vm.ByObject.Count);
    }
}
```

- [ ] **Step 2 : Vérifier l'échec**

Run : `dotnet test --filter FullyQualifiedName~StatisticsViewModelTests`
Expected : FAIL — `StatisticsViewModel` introuvable.

- [ ] **Step 3 : Implémenter `StatisticsViewModel`**

`ViewModels/StatisticsViewModel.cs` :
```csharp
using System.Collections.ObjectModel;
using SqlLogExplorer.Data;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.ViewModels;

/// <summary>Vue Quantification (spec §4.3 / §6.2) : agrégats par type et par objet.</summary>
public sealed class StatisticsViewModel : ViewModelBase
{
    private readonly LogQuery _query;

    public StatisticsViewModel(LogQuery query) => _query = query;

    public ObservableCollection<OperationCount> ByOperation { get; } = new();
    public ObservableCollection<ObjectCount> ByObject { get; } = new();

    public async Task RefreshAsync(LogFilter? filter = null)
    {
        var byOp = await _query.CountByOperationAsync(filter);
        var byObj = await _query.CountByObjectAsync(filter);

        ByOperation.Clear();
        foreach (var x in byOp) ByOperation.Add(x);

        ByObject.Clear();
        foreach (var x in byObj) ByObject.Add(x);
    }
}
```

- [ ] **Step 4 : Implémenter le flux de chargement dans `MainWindowViewModel`**

Remplacer le contenu de `ViewModels/MainWindowViewModel.cs` par :
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlLogExplorer.Backends;
using SqlLogExplorer.Data;
using SqlLogExplorer.Models;
using SqlLogExplorer.Services;

namespace SqlLogExplorer.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private long _importedCount;
    [ObservableProperty] private string? _statusMessage = "Select a .trn file or enter a Live DB connection.";

    private LogCache? _cache;
    private CancellationTokenSource? _cts;

    public StatisticsViewModel? Statistics { get; private set; }

    [RelayCommand]
    private Task LoadOfflineAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths is null || filePaths.Count == 0) return Task.CompletedTask;
        return ExecuteLoadAsync(filePaths, new LocalDbBackend());
    }

    /// <summary>Lance une analyse Live depuis une chaîne de connexion et une fenêtre LSN optionnelle
    /// (fournies par ConnectionDialog, spec §3.1). Méthode publique appelée après la fermeture du dialog.</summary>
    public Task LoadLiveAsync(string connectionString, LsnRange? range = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return Task.CompletedTask;
        return ExecuteLoadAsync(new[] { connectionString }, new LiveDatabaseBackend(range));
    }

    /// <summary>Annule l'import en cours (bouton Cancel, spec §3.4).</summary>
    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task ExecuteLoadAsync(IReadOnlyList<string> targets, ILogParserBackend backend)
    {
        if (IsLoading) return; // pas d'imports concurrents.
        IsLoading = true;
        ImportedCount = 0;
        StatusMessage = "Analyzing…";

        // Libère le cache précédent (ferme la connexion + supprime le fichier temporaire).
        await DisposeCacheAsync();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db");
            _cache = await LogCache.CreateAsync(dbPath, ct);
            var service = new LogImportService(backend, _cache);
            var progress = new Progress<long>(n => ImportedCount = n);

            // fn_dump_dblog / fn_dblog + insertions SQLite sont bloquants (spec §3.4) :
            // on exécute l'import hors du thread UI pour garder l'interface fluide.
            var total = await Task.Run(() => service.ImportAsync(targets, progress, ct), ct);

            // Retour sur le thread UI (aucun ConfigureAwait(false)) : maj des ObservableCollection sûre.
            Statistics = new StatisticsViewModel(new LogQuery(_cache.Connection));
            await Statistics.RefreshAsync();
            OnPropertyChanged(nameof(Statistics));

            StatusMessage = $"{total} records imported.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Analysis cancelled.";
            await DisposeCacheAsync(); // cache partiel inutilisable : on le supprime.
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed: {ex.Message}";
            await DisposeCacheAsync();
        }
        finally
        {
            IsLoading = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task DisposeCacheAsync()
    {
        if (_cache is not null)
        {
            await _cache.DisposeAsync();
            _cache = null;
        }
    }

    public async ValueTask DisposeAsync() => await DisposeCacheAsync();
}
```

> **Note threading (Avalonia).** `Progress<long>` capture le `SynchronizationContext` du thread UI à sa construction et y reposte ses rappels ; la continuation après `await Task.Run(...)` revient elle aussi sur le thread UI (pas de `ConfigureAwait(false)`), donc les mutations d'`ObservableCollection` dans `RefreshAsync` restent thread-safe.

- [ ] **Step 5 : Vérifier le succès**

Run : `dotnet test --filter FullyQualifiedName~StatisticsViewModelTests`
Expected : PASS. Puis `dotnet build` de l'app : OK.

- [ ] **Step 6 : Commit**

```bash
git add ViewModels tests/SqlLogExplorer.Tests/StatisticsViewModelTests.cs
git commit -m "feat: main load flow and quantification view-model"
```

---

### Task 14 : Vue Avalonia — chargement + onglet Statistiques

**Files:**
- Modify: `Views/MainWindow.axaml`
- Test: manuel (vérification visuelle) — Avalonia n'est pas testé en TDD ici.

**Interfaces:**
- Consumes: `MainWindowViewModel` (commande `LoadOfflineCommand`, méthode `LoadLiveAsync` via code-behind, `CancelCommand`, `IsLoading`, `StatusMessage`, `Statistics.ByOperation`, `Statistics.ByObject`).

- [ ] **Step 1 : Remplacer le corps de `MainWindow.axaml`**

Remplacer le `<TextBlock .../>` (ligne 18) par :
```xml
    <DockPanel Margin="12">
        <StackPanel DockPanel.Dock="Top" Orientation="Vertical" Spacing="8" Margin="0,0,0,12">
            <StackPanel Orientation="Horizontal" Spacing="8">
                <Button Content="Open .trn file(s) (Offline)…" Click="OnOpenFile"/>
                <TextBlock Text=" OR " VerticalAlignment="Center" FontWeight="Bold"/>
                <TextBox x:Name="LiveDbConnectionString" Watermark="SQL Server connection string" Width="300" VerticalAlignment="Center"/>
                <Button Content="Analyze Live DB" Click="OnAnalyzeLive"/>
                <Button Content="Cancel" Command="{Binding CancelCommand}" IsVisible="{Binding IsLoading}"/>
            </StackPanel>
            <TextBlock Text="Requires sysadmin or VIEW SERVER STATE / db_owner on the target SQL Server."
                       FontStyle="Italic" Opacity="0.7"/>
            <StackPanel Orientation="Horizontal" Spacing="8">
                <TextBlock Text="{Binding StatusMessage}" VerticalAlignment="Center"/>
                <ProgressBar IsIndeterminate="True" Width="120" IsVisible="{Binding IsLoading}" VerticalAlignment="Center"/>
            </StackPanel>
        </StackPanel>

        <TabControl>
            <TabItem Header="By operation type">
                <DataGrid ItemsSource="{Binding Statistics.ByOperation}" IsReadOnly="True"
                          AutoGenerateColumns="False">
                    <DataGrid.Columns>
                        <DataGridTextColumn Header="Operation" Binding="{Binding Operation}" Width="*"/>
                        <DataGridTextColumn Header="Count" Binding="{Binding Count}" Width="120"/>
                    </DataGrid.Columns>
                </DataGrid>
            </TabItem>
            <TabItem Header="By object">
                <DataGrid ItemsSource="{Binding Statistics.ByObject}" IsReadOnly="True"
                          AutoGenerateColumns="False">
                    <DataGrid.Columns>
                        <DataGridTextColumn Header="Object (AllocUnitName)" Binding="{Binding AllocUnitName}" Width="*"/>
                        <DataGridTextColumn Header="Count" Binding="{Binding Count}" Width="120"/>
                    </DataGrid.Columns>
                </DataGrid>
            </TabItem>
        </TabControl>
    </DockPanel>
```

- [ ] **Step 2 : Ajouter le package `Avalonia.Controls.DataGrid` et le thème**

Run :
```bash
dotnet add SqlLogExplorer.csproj package Avalonia.Controls.DataGrid --version 12.0.5
```
Dans `App.axaml`, ajouter dans `<Application.Styles>` (après le thème Fluent) :
```xml
        <StyleInclude Source="avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml"/>
```

- [ ] **Step 3 : Gérer l'ouverture de fichier dans le code-behind**

Remplacer `Views/MainWindow.axaml.cs` par :
```csharp
using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SqlLogExplorer.ViewModels;

namespace SqlLogExplorer.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private async void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select one or more transaction-log backup files",
            AllowMultiple = true, // une chaîne de .trn (spec §3.2) ; l'ordre n'affecte pas les agrégats.
            FileTypeFilter =
            [
                // MVP : .trn uniquement (les .ldf sont post-MVP, cf. spec §1.2).
                new FilePickerFileType("SQL Server transaction-log backup") { Patterns = ["*.trn"] },
            ],
        });

        if (files.Count > 0 && DataContext is MainWindowViewModel vm)
        {
            var paths = files
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrEmpty(p))
                .Cast<string>()
                .ToList();
            if (paths.Count > 0)
                await vm.LoadOfflineCommand.ExecuteAsync(paths);
        }
    }

    // MVP intermédiaire : chaîne de connexion brute. Remplacé par ConnectionDialog en Task 18.
    private async void OnAnalyzeLive(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            await vm.LoadLiveAsync(LiveDbConnectionString.Text ?? string.Empty);
    }

    // Libère le cache SQLite (connexion + fichier temporaire) à la fermeture de la fenêtre.
    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is MainWindowViewModel vm)
            await vm.DisposeAsync();
    }
}
```

- [ ] **Step 4 : Vérifier build + lancement manuel**

Run : `dotnet build`
Expected : build OK.
Vérification manuelle (Windows + LocalDB + des `.trn`) : `dotnet run` → cliquer « Open .trn file(s) (Offline)… » → **sélectionner un ou plusieurs fichiers** → la barre indéterminée s'affiche **sans figer l'UI** (import sur thread de fond), le bouton **Cancel** apparaît, puis les deux onglets se remplissent avec les comptages **cumulés** par opération et par objet (les comptages d'une chaîne = somme des fichiers). Vérifier qu'un clic sur **Cancel** pendant l'import affiche « Analysis cancelled. » et n'aboutit pas à un cache figé. En l'absence de LocalDB, le `StatusMessage` affiche l'échec sans planter. Après fermeture de la fenêtre, vérifier qu'aucun `sqllogexp_*.db` ne subsiste dans le dossier temp. Test similaire possible en rentrant une chaîne de connexion locale dans la TextBox Live DB.

- [ ] **Step 5 : Commit**

```bash
git add Views App.axaml SqlLogExplorer.csproj
git commit -m "feat: Avalonia UI for file load and quantification tabs"
```

---

### Task 15 : Boîte de connexion — construction (pure) de la chaîne

**Files:**
- Create: `Models/ConnectionSettings.cs`, `Backends/SqlConnectionStringFactory.cs`
- Test: `tests/SqlLogExplorer.Tests/SqlConnectionStringFactoryTests.cs`

**Interfaces:**
- Produces:
  - `enum SqlAuthMode { Windows, SqlLogin }`, `enum EncryptMode { Optional, Mandatory }`
  - `ConnectionSettings(string Server, SqlAuthMode Auth, string? UserName, string? Password, string? Database, EncryptMode Encrypt, bool TrustServerCertificate)`
  - `static class SqlConnectionStringFactory { static string Build(ConnectionSettings s); }`

- [ ] **Step 1 : Test (pur) — Windows vs SQL login, chiffrement, base**

`tests/SqlLogExplorer.Tests/SqlConnectionStringFactoryTests.cs` :
```csharp
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
```

- [ ] **Step 2 : Vérifier l'échec** — `dotnet test --filter FullyQualifiedName~SqlConnectionStringFactoryTests` → FAIL (types introuvables).

- [ ] **Step 3 : Implémenter**

`Models/ConnectionSettings.cs` :
```csharp
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
```

`Backends/SqlConnectionStringFactory.cs` :
```csharp
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
```

- [ ] **Step 4 : Vérifier le succès** — `dotnet test --filter FullyQualifiedName~SqlConnectionStringFactoryTests` → PASS.
- [ ] **Step 5 : Commit** — `git add Models/ConnectionSettings.cs Backends/SqlConnectionStringFactory.cs tests/... && git commit -m "feat: connection settings + connection string factory"`

---

### Task 16 : Résolveur fenêtre temporelle → plage LSN (dates → LSN)

**Files:**
- Create: `Backends/LiveLsnResolver.cs`
- Test: `tests/SqlLogExplorer.Tests/LiveLsnResolverTests.cs` (pur), `tests/SqlLogExplorer.Tests/Integration/LiveLsnResolverIntegrationTests.cs` (gardé)

**Interfaces:**
- Produces:
  - `static class LiveLsnResolver`
    - `static string BuildRangeQuery()` (pur) — SQL qui renvoie `MIN`/`MAX [Current LSN]` sur les bornes de transaction dans la fenêtre.
    - `static string BuildEarliestTimeQuery()` (pur).
    - `Task<LsnRange?> ResolveAsync(SqlConnection cn, DateTime? start, DateTime? end, CancellationToken ct = default)` — `null` si aucune borne (⇒ log actif complet).
    - `Task<DateTime?> GetEarliestLogTimeAsync(SqlConnection cn, CancellationToken ct = default)`.

> **Fondement (spec §3.1).** `fn_dblog` renvoie `[Current LSN]` **déjà** au format `vlf:block:slot` hex à largeur fixe et zéro-paddé → le tri lexicographique = tri LSN, donc `MIN`/`MAX([Current LSN])` donnent des bornes valides directement passables à `fn_dblog(@start,@end)` (aucune conversion numérique nécessaire). Seuls `LOP_BEGIN_XACT` / `LOP_COMMIT_XACT` / `LOP_ABORT_XACT` portent une heure (`[Begin Time]` / `[End Time]`) → on filtre sur ces opérations. Le format d'heure de `fn_dblog` (`yyyy/mm/dd hh:mm:ss:mmm`) est **version-dépendant** : la conversion est validée par le test d'intégration.

- [ ] **Step 1 : Test (pur) — les requêtes contiennent bornes d'opérations et colonnes attendues**

`tests/SqlLogExplorer.Tests/LiveLsnResolverTests.cs` :
```csharp
using SqlLogExplorer.Backends;
using Xunit;

namespace SqlLogExplorer.Tests;

public class LiveLsnResolverTests
{
    [Fact]
    public void BuildRangeQuery_FiltersOnTransactionBoundaryOps()
    {
        var sql = LiveLsnResolver.BuildRangeQuery();
        Assert.Contains("fn_dblog(NULL, NULL)", sql);
        Assert.Contains("LOP_BEGIN_XACT", sql);
        Assert.Contains("LOP_COMMIT_XACT", sql);
        Assert.Contains("MIN([Current LSN])", sql);
        Assert.Contains("MAX([Current LSN])", sql);
    }
}
```

- [ ] **Step 2 : Vérifier l'échec** — FAIL (type introuvable).

- [ ] **Step 3 : Implémenter `LiveLsnResolver`**

`Backends/LiveLsnResolver.cs` :
```csharp
using Microsoft.Data.SqlClient;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Backends;

/// <summary>
/// Résout une fenêtre de dates en plage LSN via un pré-scan des bornes de transaction (spec §3.1).
/// Ne lit que les enregistrements begin/commit/abort du log ACTIF (fenêtre limitée).
/// </summary>
public static class LiveLsnResolver
{
    private const string BoundaryOps =
        "[Operation] IN ('LOP_BEGIN_XACT','LOP_COMMIT_XACT','LOP_ABORT_XACT')";

    // COALESCE([End Time],[Begin Time]) : begin porte Begin Time, commit/abort portent End Time.
    public static string BuildRangeQuery() => $"""
        SELECT MIN([Current LSN]) AS StartLsn, MAX([Current LSN]) AS EndLsn
        FROM sys.fn_dblog(NULL, NULL)
        WHERE {BoundaryOps}
          AND (@start IS NULL OR CONVERT(datetime, COALESCE([End Time],[Begin Time]), 121) >= @start)
          AND (@end   IS NULL OR CONVERT(datetime, COALESCE([End Time],[Begin Time]), 121) <= @end);
        """;

    public static string BuildEarliestTimeQuery() => $"""
        SELECT MIN(CONVERT(datetime, COALESCE([End Time],[Begin Time]), 121))
        FROM sys.fn_dblog(NULL, NULL)
        WHERE {BoundaryOps};
        """;

    public static async Task<LsnRange?> ResolveAsync(
        SqlConnection cn, DateTime? start, DateTime? end, CancellationToken ct = default)
    {
        if (start is null && end is null) return null; // aucune fenêtre ⇒ log actif complet.

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = BuildRangeQuery();
        cmd.CommandTimeout = 0;
        cmd.Parameters.Add(new SqlParameter("@start", (object?)start ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@end",   (object?)end   ?? DBNull.Value));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(0) || reader.IsDBNull(1))
            return null; // rien dans la fenêtre.
        return new LsnRange(reader.GetString(0), reader.GetString(1));
    }

    public static async Task<DateTime?> GetEarliestLogTimeAsync(SqlConnection cn, CancellationToken ct = default)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = BuildEarliestTimeQuery();
        cmd.CommandTimeout = 0;
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DateTime dt ? dt : null;
    }
}
```

- [ ] **Step 4 : Vérifier le succès (pur)** — `dotnet test --filter FullyQualifiedName~LiveLsnResolverTests` → PASS.

- [ ] **Step 5 : Test d'intégration (gardé) — résoudre une plage plausible**

`tests/SqlLogExplorer.Tests/Integration/LiveLsnResolverIntegrationTests.cs` :
```csharp
using Microsoft.Data.SqlClient;
using SqlLogExplorer.Backends;
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
```

> Note : si la conversion d'heure échoue selon la version SQL, ajuster le style `CONVERT` (le format `fn_dblog` peut varier ; cf. spec §3.1).

- [ ] **Step 6 : Commit** — `git add Backends/LiveLsnResolver.cs tests/... && git commit -m "feat: resolve date window to LSN range via fn_dblog pre-scan"`

---

### Task 17 : ViewModel + vue de la boîte de connexion

**Files:**
- Create: `ViewModels/ConnectionDialogViewModel.cs`, `Views/ConnectionDialog.axaml` (+ `.axaml.cs`)
- Test: `tests/SqlLogExplorer.Tests/ConnectionDialogViewModelTests.cs`

**Interfaces:**
- Consumes: `SqlConnectionStringFactory`, `LiveLsnResolver`, `ConnectionSettings`, `LsnRange`.
- Produces:
  - `ConnectionDialogViewModel` : `[ObservableProperty]` Server, `SqlAuthMode Auth`, UserName, Password, `EncryptMode Encrypt`, `bool TrustServerCertificate`, `DateTimeOffset? StartTime`, `DateTimeOffset? EndTime` ; `ObservableCollection<string> Databases`, `string? SelectedDatabase` ; `string BuildConnectionString()` ; `[RelayCommand] Task ListDatabasesAsync()` (peuple `Databases` via `SELECT name FROM sys.databases ORDER BY name`) ; `Task<LsnRange?> ResolveRangeAsync()`.

- [ ] **Step 1 : Test — `BuildConnectionString` délègue à la factory**

`tests/SqlLogExplorer.Tests/ConnectionDialogViewModelTests.cs` :
```csharp
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
```

- [ ] **Step 2 : Vérifier l'échec** — FAIL (type introuvable).

- [ ] **Step 3 : Implémenter `ConnectionDialogViewModel`**

`ViewModels/ConnectionDialogViewModel.cs` :
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.SqlClient;
using SqlLogExplorer.Backends;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.ViewModels;

/// <summary>Logique de la boîte de connexion façon SSMS (spec §3.1 / §6.1 Option A).</summary>
public partial class ConnectionDialogViewModel : ViewModelBase
{
    [ObservableProperty] private string _server = string.Empty;
    [ObservableProperty] private SqlAuthMode _auth = SqlAuthMode.Windows;
    [ObservableProperty] private string? _userName;
    [ObservableProperty] private string? _password;
    [ObservableProperty] private EncryptMode _encrypt = EncryptMode.Mandatory;
    [ObservableProperty] private bool _trustServerCertificate = true;
    [ObservableProperty] private string? _selectedDatabase;
    [ObservableProperty] private DateTimeOffset? _startTime;
    [ObservableProperty] private DateTimeOffset? _endTime;
    [ObservableProperty] private string? _statusMessage;

    public ObservableCollection<string> Databases { get; } = new();

    private ConnectionSettings ToSettings() => new(
        Server, Auth, UserName, Password, SelectedDatabase, Encrypt, TrustServerCertificate);

    public string BuildConnectionString() => SqlConnectionStringFactory.Build(ToSettings());

    /// <summary>Connecte au serveur (sans base précise) et liste les bases (spec §6.1 : dropdown peuplé après connexion).</summary>
    [RelayCommand]
    public async Task ListDatabasesAsync()
    {
        try
        {
            var probe = SqlConnectionStringFactory.Build(ToSettings() with { Database = null });
            await using var cn = new SqlConnection(probe);
            await cn.OpenAsync();
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sys.databases ORDER BY name;";
            await using var reader = await cmd.ExecuteReaderAsync();
            Databases.Clear();
            while (await reader.ReadAsync()) Databases.Add(reader.GetString(0));
            StatusMessage = $"{Databases.Count} databases found.";
        }
        catch (Exception ex) { StatusMessage = $"Connection failed: {ex.Message}"; }
    }

    /// <summary>Résout la fenêtre temporelle en plage LSN (null = log actif complet).</summary>
    public async Task<LsnRange?> ResolveRangeAsync()
    {
        if (StartTime is null && EndTime is null) return null;
        await using var cn = new SqlConnection(BuildConnectionString());
        await cn.OpenAsync();
        return await LiveLsnResolver.ResolveAsync(cn, StartTime?.DateTime, EndTime?.DateTime);
    }
}
```

- [ ] **Step 4 : Vérifier le succès** — `dotnet test --filter FullyQualifiedName~ConnectionDialogViewModelTests` → PASS.

- [ ] **Step 5 : Créer la vue `Views/ConnectionDialog.axaml`** (vérif manuelle)

Fenêtre modale reproduisant `specs/01.connection.png` : `TextBox` Server ; `ComboBox` Authentication (`Windows` / `SQL Server`) ; `TextBox` User / `TextBox` (PasswordChar) Password, visibles seulement si `Auth == SqlLogin` ; `ComboBox` Encrypt (`Optional`/`Mandatory`) ; `CheckBox` Trust Server Certificate ; bouton **Connect** (`ListDatabasesCommand`) ; `ComboBox` Database (`ItemsSource={Binding Databases}` `SelectedItem={Binding SelectedDatabase}`) ; deux `CalendarDatePicker`+heure pour Start/End (fenêtre temporelle) ; `TextBlock` `StatusMessage` ; boutons **OK** / **Cancel**. Onglet avancé « Connection string » (facultatif au MVP). Tout le texte en anglais (spec §6). L'onglet « historique » peut être minimal (liste des serveurs récents, **sans mot de passe**).

`Views/ConnectionDialog.axaml.cs` : sur **OK**, résout la plage (`await vm.ResolveRangeAsync()`) puis ferme en renvoyant `(vm.BuildConnectionString(), range)` à l'appelant.

- [ ] **Step 6 : Commit** — `git add ViewModels/ConnectionDialogViewModel.cs Views/ConnectionDialog.axaml* tests/... && git commit -m "feat: SSMS-style connection dialog with database listing and time window"`

---

### Task 18 : Câbler le dialog + la fenêtre temporelle dans la fenêtre principale

**Files:**
- Modify: `Views/MainWindow.axaml` (+ `.axaml.cs`)
- Test: manuel.

**Interfaces:**
- Consumes: `ConnectionDialogViewModel`, `ConnectionDialog`, `MainWindowViewModel.LoadLiveAsync(string, LsnRange?)`.

- [ ] **Step 1 : Remplacer le TextBox + bouton « Analyze Live DB » par un bouton d'ouverture du dialog**

Dans `MainWindow.axaml`, remplacer :
```xml
                <TextBox x:Name="LiveDbConnectionString" Watermark="SQL Server connection string" Width="300" VerticalAlignment="Center"/>
                <Button Content="Analyze Live DB" Click="OnAnalyzeLive"/>
```
par :
```xml
                <Button Content="Connect (Live DB)…" Click="OnConnectLive"/>
```

- [ ] **Step 2 : Ouvrir le dialog dans le code-behind et lancer l'analyse**

Remplacer `OnAnalyzeLive` par :
```csharp
    private async void OnConnectLive(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var dialog = new Views.ConnectionDialog { DataContext = new ConnectionDialogViewModel() };
        var result = await dialog.ShowDialog<(string ConnectionString, LsnRange? Range)?>(this);
        if (result is { } r && !string.IsNullOrWhiteSpace(r.ConnectionString))
            await vm.LoadLiveAsync(r.ConnectionString, r.Range);
    }
```
(et retirer le champ `LiveDbConnectionString` devenu inutile ; ajouter `using SqlLogExplorer.Models;`).

- [ ] **Step 3 : Vérification manuelle**

`dotnet run` → « Connect (Live DB)… » → la boîte façon SSMS s'ouvre → renseigner serveur/auth → **Connect** peuple la liste des bases → choisir une base → (optionnel) fixer une fenêtre Start/End → **OK** : l'analyse Live démarre, hors thread UI, annulable, et les onglets de quantification se remplissent. Sans fenêtre temporelle, `fn_dblog(NULL,NULL)` ; avec, `fn_dblog(@start,@end)` sur la plage résolue.

- [ ] **Step 4 : Commit** — `git add Views/MainWindow.axaml* && git commit -m "feat: wire SSMS-style connection dialog and time window into main flow"`

---

## Self-Review (couverture spec ↔ plan)

- **§2 couches persistance/présentation** → Tasks 3–7 (cache+lectures), 13–14 (présentation). ✔
- **§3.1 backend LocalDB / Live Database** → Tasks 9–10 (LocalDB), Task 11 (Live DB). ✔
- **§3.1 dialog de connexion façon SSMS** (chaîne de connexion, auth, chiffrement, dropdown base peuplé après connexion, historique sans mot de passe) → Tasks 15 (factory), 17 (VM + vue), 18 (câblage). ✔
- **§3.1 fenêtre temporelle dates→LSN** (pré-scan des bornes de transaction, `fn_dblog(@start,@end)`, injection ctor `LiveDatabaseBackend`) → Tasks 2 (`LsnRange`), 16 (résolveur), 11 (backend), 17/18 (UI). ✔
- **§3.2 offline multi-`.trn`** (un appel `fn_dump_dblog` par fichier, backend itère sur `IReadOnlyList<string> targets`, FilePicker `AllowMultiple`) → Tasks 8, 10, 12, 13, 14. ✔
- **§3.4 contraintes** (`fn_dump_dblog` lent → `CommandTimeout=0`, batchs, annulation ; privilèges/instance jetable) → Tasks 10–12 ; **annulation + import hors thread UI + bouton Cancel** → Tasks 13–14 ; **hint privilèges** dans l'UI → Task 14. ✔
- **§4.1 schéma cache** → Task 3 (schéma exact). ✔
- **§4.2 index** (`AllocUnitName`, `TransactionId`, `Operation`, composite) → Task 3. ✔
- **§4 bulk-load tuning** (PRAGMA `journal_mode`/`synchronous`/`temp_store`) + **cycle de vie cache** (suppression du fichier au Dispose) → Task 3 (+ dispose côté ViewModel Task 13, fenêtre Task 14). ✔
- **§4.3 quantification** (par type, par objet, croisé, respect des filtres) → Tasks 6–7 (+ filtre Task 5). ✔
- **§6.2 vue Statistiques** → Tasks 13–14. ✔
- **Hors périmètre assumé** : §5 décodeur binaire complet, §3.2 Docker (ajout ultérieur), §3.3 serveur distant, §6.1 inspecteur détaillé, **`.ldf` détaché** (post-MVP : attach + `fn_dblog`), **dropdown base Live DB** (§6.1), **normalisation `AllocUnitName`** index→table → plans suivants. Le drill-down « clic agrégat → filtre grille » (§6.2) est reporté au plan qui introduit la grille détaillée.

**Placeholder scan :** aucun TODO/TBD ; chaque étape de code fournit le code complet. ✔
**Type consistency :** `LogRecord`, `LogFilter`, `OperationCount`/`ObjectCount`/`ObjectOperationCount`, `LsnRange`, `ConnectionSettings`, `LogCache.InsertBatchAsync`, `LogQuery.*Async`, `ILogParserBackend` (inchangée), `LiveDatabaseBackend(LsnRange?)`, `LogImportService.ImportAsync`, `LocalDbInstanceManager.ConnectionString`, `SqlConnectionStringFactory.Build`, `LiveLsnResolver.ResolveAsync`, `ConnectionDialogViewModel`, `MainWindowViewModel.LoadLiveAsync(string, LsnRange?)` — signatures identiques entre tâches productrices et consommatrices. ✔
