# RowLog Contents Decoder Implementation Plan

> For agentic workers: REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Goal: Decode the binary `RowLog Contents` of `fn_dblog` / `fn_dump_dblog` into annotated rows, attribute update fragments to columns, and rebuild a row before and after an update by replaying the cached log (spec03).

Architecture: A pure `Decoding/` folder (no SQL client, no UI) holds the schema model, type decoders, row walk, key extraction, modify fragments and the replay engine. `Backends/` gains the schema reader and the current-row reader, `Data/` the extended cache, `Services/DecodingService` orchestrates one inspection, and the Avalonia UI gains a Records tab with an inspector. Fixtures captured from real SQL Server 2017, 2019, 2022 and 2025 drive the tests.

Tech Stack: .NET 10, C# (nullable on), Avalonia 12 + CommunityToolkit.Mvvm, Microsoft.Data.SqlClient 7, Microsoft.Data.Sqlite 10, xUnit 2.9, podman with `mcr.microsoft.com/mssql/server:{2017,2019,2022,2025}-latest`.

Spec: `specs/spec03.md` (version 5). Read it before your task; section numbers below refer to it.

## Global Constraints

- The main project has no `ImplicitUsings`: every file in it lists its `using` directives. The test project has `ImplicitUsings` on.
- File-scoped namespaces; code comments in French, like the existing code; every user-facing string in English (spec01 §6).
- No new NuGet package. `System.Data.SqlTypes.SqlDecimal` and `System.Text.CodePagesEncodingProvider` are in the shared framework.
- Nothing under `Decoding/` references `Microsoft.Data.SqlClient`, `Microsoft.Data.Sqlite`, Avalonia, or anything under `Backends/`, `Data/`, `Services/`, `ViewModels/`.
- The decoder never throws towards the UI (spec §8): malformed input yields fields with `FieldRole.Malformed` and a `Problem` text.
- LSNs are stored uppercase (`Lsn.Normalize`) and passed to `fn_dblog` only in decimal form (`Lsn.ToFnDblogArgument`), spec §2 and §3.
- Every ordering of log records uses `LSN`, never `Id` (spec §3).
- Integration tests use `[IntegrationFact]` (skipped unless `SQLLOGEXPLORER_RUN_INTEGRATION=1`) and read the server from `SQLLOGEXPLORER_TEST_SQL`, a connection string to `master` as `sa`, produced by `tests/fixtures/sqlserver.sh` (Task 1). Start the server and run the tests in ONE shell invocation: shell state does not survive between tool calls, and a test run without the variables skips every integration test and still prints success.
- Commit messages: a subject line, then prose explaining why. No `Co-Authored-By`, no `Generated with`, no attribution trailer of any kind.
- Never `git checkout` or `git restore` a file that holds uncommitted work.

## Review Focus

1. A selected record whose allocation unit no longer exists in the connected database (index rebuilt since, or offline schema from another database): the inspector must show the structure without a schema, never an exception. Test in Task 11.
2. A `.trn` imported twice, or two `.trn` files imported in reverse order: no duplicate record, grid and chains in LSN order. Tests in Task 1 and Task 11.
3. Truncated or empty `RowLog Contents` (compensation delete with `0x`, a 3-byte blob): the row decoder returns malformed fields, no exception. Test in Task 6.
4. A column whose type is not in spec §1.1 (`xml`, `sql_variant`, `geography`): shown as hex with "type not decoded". Test in Task 5.
5. LSNs from 2017 (lowercase) mixed with LSNs from 2022 in one cache: one ordering. Test in Task 1.

---

## File structure

| Path | Responsibility | Task |
|---|---|---|
| `Models/LogRecord.cs` | the record, extended with the §3 columns | 1 |
| `Models/Lsn.cs` | LSN normalization and the decimal form for `fn_dblog` | 1 |
| `Backends/LogColumns.cs` | the shared `SELECT` list and `SqlDataReader` mapping for both backends | 1 |
| `Backends/LiveDatabaseBackend.cs`, `Backends/LocalDbBackend.cs`, `Backends/FnDumpDblogQuery.cs` | use `LogColumns` | 1 |
| `Data/LogCache.cs` | new columns, unique LSN, `RowKey`, new indexes | 1 |
| `Data/LogQuery.cs` | read new columns, order by LSN, chain and transaction queries | 1, 10 |
| `Backends/Sql/TableSchema.sql` | the one catalog query for a `TableSchema`, embedded resource | 2 |
| `Decoding/TableSchema.cs` | `TableSchema`, `SchemaColumn` | 2 |
| `Backends/SchemaProvider.cs` | `AllocUnitId` to `TableSchema`, privilege check | 2 |
| `tests/fixtures/sqlserver.sh` | start and stop a test SQL Server under podman | 3 |
| `tests/fixtures/capture.sh`, `tests/fixtures/capture.sql`, `tests/fixtures/scenarios/*.sql` | freeze fixtures from the four versions | 3 |
| `tests/SqlLogExplorer.Tests/Fixtures/<version>/<scenario>.json` | the frozen fixtures | 3 |
| `tests/SqlLogExplorer.Tests/FixtureSet.cs` | load a fixture as `LogRecord`s and `TableSchema`s | 3 |
| `docs/research/rowlog-layouts.md` | the R&D note: index record and `LOP_MODIFY_COLUMNS` layouts | 4 |
| `Decoding/DecodedRow.cs` | `FieldRole`, `DecodedField`, `DecodedColumn`, `DecodedRow` | 5 |
| `Decoding/TypeDecoder.cs` | bytes of one column to a value | 5 |
| `Decoding/RowDecoder.cs` | the row walk of spec §5 | 6 |
| `Decoding/KeyDecoder.cs` | canonical clustered key, spec §7.1 | 7 |
| `Decoding/ModifyDecoder.cs` | modify record to fragments, spec §6 | 8 |
| `Decoding/RowReplayer.cs` | transaction classification and replay, spec §7.3, §7.4 | 9 |
| `Services/DecodingService.cs` | one inspection: decode, key, chain, anchors 1 and 2, attribution | 10, 11 |
| `Backends/CurrentRowReader.cs` | anchor 3, spec §7.2 | 12 |
| `ViewModels/RecordsViewModel.cs`, `ViewModels/InspectorViewModel.cs`, `Views/MainWindow.axaml` | Records tab and inspector, spec §9 | 13 |

---

### Task 1: Import extension

Spec §3. Extends what is imported, fixes LSN handling, and makes the cache ready for replay.

Files:
- Create: `Models/Lsn.cs`, `Backends/LogColumns.cs`, `tests/fixtures/sqlserver.sh`
- Modify: `Models/LogRecord.cs`, `Backends/LiveDatabaseBackend.cs`, `Backends/LocalDbBackend.cs`, `Backends/FnDumpDblogQuery.cs`, `Data/LogCache.cs`, `Data/LogQuery.cs`
- Test: `tests/SqlLogExplorer.Tests/LsnTests.cs`, `tests/SqlLogExplorer.Tests/LogCacheTests.cs`, `tests/SqlLogExplorer.Tests/FnDumpDblogQueryTests.cs`, `tests/SqlLogExplorer.Tests/LogQueryTests.cs`, `tests/SqlLogExplorer.Tests/Integration/LogColumnsIntegrationTests.cs`

Interfaces:
- Produces: `LogRecord` with the new optional members below; `Lsn.Normalize(string)`, `Lsn.ToFnDblogArgument(string)`; `LogColumns.SelectList`, `LogColumns.Read(SqlDataReader)`; `LogCache.SetRowKeysAsync(IReadOnlyList<(string Lsn, string? RowKey)>, CancellationToken)`.

- [ ] Step 1: Write the failing tests

`tests/SqlLogExplorer.Tests/LsnTests.cs`:

```csharp
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Tests;

public class LsnTests
{
    [Fact]
    public void Normalize_UppercasesLowercaseLsn()
        => Assert.Equal("0000002A:00000470:0013", Lsn.Normalize("0000002a:00000470:0013"));

    [Fact]
    public void ToFnDblogArgument_ConvertsEachPartToDecimal()
        => Assert.Equal("42:1136:19", Lsn.ToFnDblogArgument("0000002A:00000470:0013"));

    [Fact]
    public void ToFnDblogArgument_AcceptsLowercase()
        => Assert.Equal("39:144:5", Lsn.ToFnDblogArgument("00000027:00000090:0005"));

    [Fact]
    public void ToFnDblogArgument_RejectsMalformedLsn()
        => Assert.Throws<FormatException>(() => Lsn.ToFnDblogArgument("not-an-lsn"));
}
```

Add to `LogCacheTests`:

```csharp
    [Fact]
    public async Task InsertBatchAsync_IgnoresDuplicateLsnAndNormalizesCase()
    {
        await using var cache = await LogCache.CreateAsync(TempDb());
        var a = new Models.LogRecord("0000002a:00000010:0001", "LOP_INSERT_ROWS", "LCX_CLUSTERED", "0000:00000001", null, new byte[] { 0x10 }, null);
        var b = a with { Lsn = "0000002A:00000010:0001" };

        await cache.InsertBatchAsync(new[] { a });
        await cache.InsertBatchAsync(new[] { b });

        await using var cmd = cache.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), MIN(LSN) FROM LogRecords;";
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        Assert.Equal(1L, r.GetInt64(0));
        Assert.Equal("0000002A:00000010:0001", r.GetString(1));
    }

    [Fact]
    public async Task InsertBatchAsync_RoundTripsReplayColumns()
    {
        await using var cache = await LogCache.CreateAsync(TempDb());
        var rec = new Models.LogRecord("0000002A:00000010:0002", "LOP_MODIFY_ROW", "LCX_CLUSTERED", "0000:00000002", "dbo.t",
            new byte[] { 0x0A }, new byte[] { 0x0B })
        {
            AllocUnitId = 72057594052476928, PartitionId = 72057594045726720, PageId = "0001:00000150", SlotId = 3,
            OffsetInRow = 8, ModifySize = 1, NumElements = 6, Description = "COMPENSATION", TransactionName = "UPDATE",
            RowLogContents2 = new byte[] { 0x16, 0x01 }, RowLogContents3 = new byte[] { 0x02 },
            RowLogContents4 = new byte[] { 0x03 }, RowLogContents5 = new byte[] { 0x04 },
        };
        await cache.InsertBatchAsync(new[] { rec });

        var page = await new Data.LogQuery(cache.Connection).GetPageAsync(0, 10);

        Assert.Equal(rec, page.Single(), new LogRecordComparer());
    }

    [Fact]
    public async Task SetRowKeysAsync_StoresKeysByLsn()
    {
        await using var cache = await LogCache.CreateAsync(TempDb());
        var rec = new Models.LogRecord("0000002A:00000010:0003", "LOP_INSERT_ROWS", "LCX_CLUSTERED", null, null, null, null);
        await cache.InsertBatchAsync(new[] { rec });

        await cache.SetRowKeysAsync(new[] { ("0000002A:00000010:0003", (string?)"V01000000;U0") });

        await using var cmd = cache.Connection.CreateCommand();
        cmd.CommandText = "SELECT RowKey FROM LogRecords;";
        Assert.Equal("V01000000;U0", (string?)await cmd.ExecuteScalarAsync());
    }
```

`LogRecordComparer` compares every member, byte arrays by content. Add it to `tests/SqlLogExplorer.Tests/LogRecordComparer.cs`:

```csharp
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Tests;

/// <summary>Égalité membre à membre de LogRecord, tableaux d'octets comparés par contenu.</summary>
public sealed class LogRecordComparer : IEqualityComparer<LogRecord>
{
    public bool Equals(LogRecord? x, LogRecord? y)
    {
        if (x is null || y is null) return x is null && y is null;
        static bool B(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
        return x.Lsn == y.Lsn && x.Operation == y.Operation && x.Context == y.Context
            && x.TransactionId == y.TransactionId && x.AllocUnitName == y.AllocUnitName
            && B(x.RowLogContents0, y.RowLogContents0) && B(x.RowLogContents1, y.RowLogContents1)
            && x.AllocUnitId == y.AllocUnitId && x.PartitionId == y.PartitionId && x.PageId == y.PageId
            && x.SlotId == y.SlotId && x.OffsetInRow == y.OffsetInRow && x.ModifySize == y.ModifySize
            && x.NumElements == y.NumElements && x.Description == y.Description && x.TransactionName == y.TransactionName
            && B(x.RowLogContents2, y.RowLogContents2) && B(x.RowLogContents3, y.RowLogContents3)
            && B(x.RowLogContents4, y.RowLogContents4) && B(x.RowLogContents5, y.RowLogContents5);
    }

    public int GetHashCode(LogRecord obj) => obj.Lsn.GetHashCode();
}
```

Add to `LogQueryTests` a test that two records inserted in reverse LSN order come back from `GetPageAsync` in LSN order, the first stored lowercase:

```csharp
    [Fact]
    public async Task GetPageAsync_OrdersByLsnNotInsertionOrder()
    {
        await using var cache = await LogCache.CreateAsync(Path.Combine(Path.GetTempPath(), $"sqllogexp_{Guid.NewGuid():N}.db"));
        await cache.InsertBatchAsync(new[]
        {
            new Models.LogRecord("0000002C:00000010:0001", "LOP_INSERT_ROWS", null, null, null, null, null),
            new Models.LogRecord("0000002b:00000010:0001", "LOP_INSERT_ROWS", null, null, null, null, null),
        });

        var page = await new LogQuery(cache.Connection).GetPageAsync(0, 10);

        Assert.Equal(new[] { "0000002B:00000010:0001", "0000002C:00000010:0001" }, page.Select(r => r.Lsn));
    }
```

Replace the column assertions of `FnDumpDblogQueryTests.Build_ContainsRequiredColumnsAndFunction` with one per column of `LogColumns.SelectList`:

```csharp
    [Theory]
    [InlineData("[Current LSN]")] [InlineData("[Transaction ID]")] [InlineData("[AllocUnitId]")]
    [InlineData("[PartitionId]")] [InlineData("[Page ID]")] [InlineData("[Slot ID]")]
    [InlineData("[Offset in Row]")] [InlineData("[Modify Size]")] [InlineData("[Num Elements]")]
    [InlineData("[Description]")] [InlineData("[Transaction Name]")]
    [InlineData("[RowLog Contents 0]")] [InlineData("[RowLog Contents 5]")]
    public void Build_SelectsColumn(string column)
        => Assert.Contains(column, FnDumpDblogQuery.Build("@path"));
```

Update `LogCacheTests.CreateAsync_CreatesLogRecordsTableAndIndexes` to also expect `IX_LogRecords_Alloc_Key_Lsn` and `IX_LogRecords_Alloc_Page_Slot_Lsn` (count 6).

The existing `LogQueryTests.GetPageAsync_RespectsLimitOffsetAndOrder` seeds LSNs `lsn0` to `lsn4` and expects `lsn2`: after this task they are stored uppercase, so change its expectations to `LSN2` and `LSN3`. That is the only existing test this task changes.

- [ ] Step 2: Run the tests to verify they fail

Run: `dotnet test tests/SqlLogExplorer.Tests --filter "FullyQualifiedName~LsnTests|FullyQualifiedName~LogCacheTests|FullyQualifiedName~FnDumpDblogQueryTests|FullyQualifiedName~LogQueryTests"`
Expected: build failure (`Lsn`, `AllocUnitId`, `SetRowKeysAsync` not defined).

- [ ] Step 3: Implement

`Models/LogRecord.cs`: keep the seven positional parameters (existing call sites stay valid) and add `init` properties.

```csharp
namespace SqlLogExplorer.Models;

/// <summary>Un enregistrement brut du journal (RowLog Contents non décodés), spec03 §3.</summary>
public sealed record LogRecord(
    string Lsn,
    string Operation,
    string? Context,
    string? TransactionId,
    string? AllocUnitName,
    byte[]? RowLogContents0,
    byte[]? RowLogContents1)
{
    public long? AllocUnitId { get; init; }
    public long? PartitionId { get; init; }
    public string? PageId { get; init; }
    public int? SlotId { get; init; }
    public int? OffsetInRow { get; init; }
    public int? ModifySize { get; init; }
    public int? NumElements { get; init; }
    public string? Description { get; init; }
    public string? TransactionName { get; init; }
    public byte[]? RowLogContents2 { get; init; }
    public byte[]? RowLogContents3 { get; init; }
    public byte[]? RowLogContents4 { get; init; }
    public byte[]? RowLogContents5 { get; init; }

    public bool IsCompensation => Description?.Contains("COMPENSATION", System.StringComparison.Ordinal) == true;
}
```

`Models/Lsn.cs`:

```csharp
using System;
using System.Globalization;

namespace SqlLogExplorer.Models;

/// <summary>LSN au format texte de fn_dblog (vlf:block:slot en hexadécimal), spec03 §2 et §3.</summary>
public static class Lsn
{
    /// <summary>2017 et 2019 rendent l'hexadécimal en minuscules : on stocke en majuscules pour un tri textuel unique.</summary>
    public static string Normalize(string lsn) => lsn.ToUpperInvariant();

    /// <summary>fn_dblog refuse la forme hexadécimale à deux-points (Msg 9005) ; il accepte la forme décimale a:b:c.</summary>
    public static string ToFnDblogArgument(string lsn)
    {
        var parts = lsn.Split(':');
        if (parts.Length != 3) throw new FormatException($"Invalid LSN '{lsn}'.");
        return string.Join(':', Array.ConvertAll(parts,
            p => long.Parse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)));
    }
}
```

`long.Parse` with `HexNumber` throws `FormatException` on `"not-an-lsn"`'s single part? It has no colon so the length check throws first; keep both.

`Backends/LogColumns.cs`:

```csharp
using System.Data;
using Microsoft.Data.SqlClient;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Backends;

/// <summary>Colonnes de fn_dblog / fn_dump_dblog importées (spec03 §3) et leur lecture, communes aux deux backends.</summary>
public static class LogColumns
{
    public const string SelectList = """
        [Current LSN], [Operation], [Context], [Transaction ID], [AllocUnitName],
        [RowLog Contents 0], [RowLog Contents 1],
        [AllocUnitId], [PartitionId], [Page ID], [Slot ID], [Offset in Row], [Modify Size], [Num Elements],
        [Description], [Transaction Name],
        [RowLog Contents 2], [RowLog Contents 3], [RowLog Contents 4], [RowLog Contents 5]
        """;

    /// <summary>Lecture dans l'ordre du SELECT (accès séquentiel). Les types sont ceux que fn_dblog renvoie
    /// (bigint, int, smallint, spec03 §2) : on lit par Convert pour ne pas dépendre de la largeur exacte.</summary>
    public static LogRecord Read(SqlDataReader r)
    {
        static byte[]? Bytes(SqlDataReader r, int i) => r.IsDBNull(i) ? null : (byte[])r.GetValue(i);
        static string? Text(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        static long? Long(SqlDataReader r, int i) => r.IsDBNull(i) ? null : System.Convert.ToInt64(r.GetValue(i));
        static int? Int(SqlDataReader r, int i) => r.IsDBNull(i) ? null : System.Convert.ToInt32(r.GetValue(i));

        return new LogRecord(Lsn.Normalize(r.GetString(0)), r.GetString(1), Text(r, 2), Text(r, 3), Text(r, 4), Bytes(r, 5), Bytes(r, 6))
        {
            AllocUnitId = Long(r, 7), PartitionId = Long(r, 8), PageId = Text(r, 9), SlotId = Int(r, 10),
            OffsetInRow = Int(r, 11), ModifySize = Int(r, 12), NumElements = Int(r, 13),
            Description = Text(r, 14), TransactionName = Text(r, 15),
            RowLogContents2 = Bytes(r, 16), RowLogContents3 = Bytes(r, 17), RowLogContents4 = Bytes(r, 18), RowLogContents5 = Bytes(r, 19),
        };
    }
}
```

`FnDumpDblogQuery.Build` returns `$"SELECT {LogColumns.SelectList} FROM sys.fn_dump_dblog(NULL, NULL, N'DISK', 1, {pathParameterName}, {defaults});"`. `LiveDatabaseBackend` selects `LogColumns.SelectList` from `sys.fn_dblog({bounds})` and yields `LogColumns.Read(reader)`; when `_range` is set, its parameters become `Lsn.ToFnDblogArgument(_range.Start)` and `Lsn.ToFnDblogArgument(_range.End)` (the existing code passes the hexadecimal form, which fails with Msg 9005 as soon as the LSN holds a letter). `LocalDbBackend` yields `LogColumns.Read(reader)`.

`Data/LogCache.cs`: the table gains the columns of spec §3 and `RowKey TEXT`; `LSN TEXT NOT NULL UNIQUE`; indexes `IX_LogRecords_Alloc_Key_Lsn ON LogRecords(AllocUnitId, RowKey, LSN)` and `IX_LogRecords_Alloc_Page_Slot_Lsn ON LogRecords(AllocUnitId, PageId, SlotId, LSN)`. The insert becomes `INSERT OR IGNORE`, binds every new column, and stores `Lsn.Normalize(r.Lsn)`. `InsertBatchAsync` still returns the number of rows actually inserted (`ExecuteNonQuery` returns 0 for an ignored row). Add:

```csharp
    /// <summary>Clés canoniques calculées par DecodingService (spec03 §7.1), indexées pour les chaînes.</summary>
    public async Task SetRowKeysAsync(IReadOnlyList<(string Lsn, string? RowKey)> keys, CancellationToken ct = default)
    {
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE LogRecords SET RowKey = $key WHERE LSN = $lsn;";
        var pKey = AddParam(cmd, "$key");
        var pLsn = AddParam(cmd, "$lsn");
        cmd.Prepare();
        foreach (var (lsn, key) in keys)
        {
            pLsn.Value = lsn;
            pKey.Value = (object?)key ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        await tx.CommitAsync(ct);
    }
```

`Data/LogQuery.cs`: `GetPageAsync` selects every column and orders `BY LSN`; extract the row mapping into `private static LogRecord ReadRecord(SqliteDataReader r)` so Task 10 reuses it.

- [ ] Step 4: Run the tests to verify they pass

Run the same command. Expected: 31 tests passed (LsnTests 4, LogCacheTests 6, FnDumpDblogQueryTests 14 = 13 theory rows + 1 fact, LogQueryTests 7). If your count differs, recount against the tree and explain the difference before going on: a lower or a higher number both mean the filter or the work is wrong.

- [ ] Step 5: Break and confirm

Break each of these, one at a time, print the mutated line with `grep -n`, run the filter, and restore:
- `Lsn.Normalize` returns its input unchanged: `InsertBatchAsync_IgnoresDuplicateLsnAndNormalizesCase` and `GetPageAsync_OrdersByLsnNotInsertionOrder` must fail;
- `INSERT OR IGNORE` back to `INSERT`: the duplicate test must fail (unique constraint);
- `ORDER BY LSN` back to `ORDER BY Id`: the ordering test must fail;
- `LogColumns.Read` maps `[Num Elements]` from index 12: `LogColumnsIntegrationTests` (Step 5b) must fail.

Reporting that one break did not fail the test you expected is the successful outcome of this step, not a failure.

- [ ] Step 5b: `sqlserver.sh` and the reader's integration test

`tests/fixtures/sqlserver.sh`:

```bash
#!/usr/bin/env bash
# Démarre ou arrête un SQL Server de test sous podman (spec03 §10.1).
# up <2017|2019|2022|2025> <nom> : attend que le serveur réponde, puis imprime les lignes export
#   SQLLOGEXPLORER_RUN_INTEGRATION et SQLLOGEXPLORER_TEST_SQL (à évaluer dans la MÊME invocation du shell).
# down <nom> : supprime le conteneur.
set -euo pipefail
PASSWORD='Fixture#2026x'
sqlcmd_path() { if [ "$1" = 2017 ]; then echo "/opt/mssql-tools/bin/sqlcmd"; else echo "/opt/mssql-tools18/bin/sqlcmd -C"; fi; }
case "${1:-}" in
  up)
    version=$2 name=$3
    podman run -d --name "$name" -p 127.0.0.1::1433 -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$PASSWORD" \
      "mcr.microsoft.com/mssql/server:${version}-latest" >/dev/null
    for _ in $(seq 1 90); do
      podman exec "$name" $(sqlcmd_path "$version") -S localhost -U sa -P "$PASSWORD" -Q 'SELECT 1' >/dev/null 2>&1 && break
      sleep 2
    done
    port=$(podman port "$name" 1433 | head -1 | cut -d: -f2)
    echo "export SQLLOGEXPLORER_RUN_INTEGRATION=1"
    echo "export SQLLOGEXPLORER_TEST_SQL='Server=127.0.0.1,$port;User ID=sa;Password=$PASSWORD;TrustServerCertificate=True;Database=master'" ;;
  down) podman rm -f -t 2 "$2" >/dev/null ;;
  *) echo "usage: $0 up <version> <name> | down <name>" >&2; exit 2 ;;
esac
```

`tests/SqlLogExplorer.Tests/Integration/LogColumnsIntegrationTests.cs` creates a database `sle_cols_<guid>` on `SQLLOGEXPLORER_TEST_SQL`, runs `CREATE TABLE dbo.t(id int NOT NULL PRIMARY KEY, a int NULL); INSERT dbo.t VALUES (1, 10); UPDATE dbo.t SET a = 11 WHERE id = 1;`, then reads that database's log through `new LiveDatabaseBackend().ParseLogAsync(new[] { <connection string to that database> })`, and asserts that the `LOP_MODIFY_ROW` whose `AllocUnitName` starts with `dbo.t` has `OffsetInRow == 8`, `ModifySize == 1`, `NumElements` not null, `RowLogContents0 == [0x0A]`, `RowLogContents1 == [0x0B]`, a non-null `RowLogContents2`, a non-null `AllocUnitId`, and an `Lsn` equal to its uppercase form. It also reads with a range, `new LiveDatabaseBackend(new LsnRange(first, last))` where `first` and `last` are LSNs read in the first pass and at least one of them holds a letter, and asserts it returns records (the hexadecimal form fails with Msg 9005; the decimal form does not). It drops the database in a `finally`.

Run, setup and tests in ONE invocation:

```bash
eval "$(bash tests/fixtures/sqlserver.sh up 2022 sle-it-t1)" && dotnet test tests/SqlLogExplorer.Tests --filter "FullyQualifiedName~LogColumnsIntegrationTests"; bash tests/fixtures/sqlserver.sh down sle-it-t1
```

Expected: 1 passed, 0 skipped. A skipped test means the variables did not reach the test process.

- [ ] Step 6: Full suite and commit

Run: `dotnet test tests/SqlLogExplorer.Tests`. Expected: all pass, integration tests skipped.

```bash
git add tests/fixtures/sqlserver.sh tests/SqlLogExplorer.Tests/Integration/LogColumnsIntegrationTests.cs Models/LogRecord.cs Models/Lsn.cs Backends/LogColumns.cs Backends/LiveDatabaseBackend.cs Backends/LocalDbBackend.cs Backends/FnDumpDblogQuery.cs Data/LogCache.cs Data/LogQuery.cs tests/SqlLogExplorer.Tests/LsnTests.cs tests/SqlLogExplorer.Tests/LogCacheTests.cs tests/SqlLogExplorer.Tests/LogQueryTests.cs tests/SqlLogExplorer.Tests/FnDumpDblogQueryTests.cs tests/SqlLogExplorer.Tests/LogRecordComparer.cs
git commit -m "feat: import the log columns the decoder needs"
```

with a body explaining why (spec03 §3: replay needs page, slot, offsets, all six contents and the transaction name; LSNs come lowercase from 2017 and must be passed to fn_dblog in decimal form).

---

### Task 2: Table schema and SchemaProvider

Spec §4 (`TableSchema`, `SchemaProvider`), §1.3.

Files:
- Create: `Decoding/TableSchema.cs`, `Backends/Sql/TableSchema.sql`, `Backends/SchemaProvider.cs`
- Modify: `SqlLogExplorer.csproj` (embed the SQL file)
- Test: `tests/SqlLogExplorer.Tests/Decoding/TableSchemaTests.cs`, `tests/SqlLogExplorer.Tests/Integration/SchemaProviderIntegrationTests.cs`

Interfaces:
- Produces:
  - `sealed record SchemaColumn(int PartitionColumnId, string Name, int LeafOffset, int LeafNullBit, int LeafBitPosition, int MaxInrowLength, int SystemTypeId, int Precision, int Scale, int? CodePage, bool IsNullable, bool IsDropped, bool IsUniqueifier, bool HasDefault, string? DefaultValue, int? KeyOrdinal)` with `bool IsFixed => LeafOffset > 0` and `bool IsBit => SystemTypeId == 104`;
  - `sealed record TableSchema(long AllocUnitId, string SchemaName, string TableName, int IndexId, int AllocUnitType, IReadOnlyList<SchemaColumn> Columns)` with `string ObjectName => $"{SchemaName}.{TableName}"` (display only: a query quotes `SchemaName` and `TableName` separately), `bool IsDataRows => AllocUnitType == 1 && IndexId is 0 or 1`, `IEnumerable<SchemaColumn> KeyColumns` (not dropped, `KeyOrdinal` set, ordered by `KeyOrdinal`) and `SchemaColumn? Uniqueifier`;
  - `SchemaProvider(string connectionString)` with `Task<TableSchema?> GetAsync(long allocUnitId, CancellationToken ct = default)` (cached per allocation unit, `null` when unknown) and `Task<bool> IsPrivilegedAsync(CancellationToken ct = default)`;
  - `SchemaProvider.Query` (the embedded SQL text, also used by Task 3).

- [ ] Step 1: `Backends/Sql/TableSchema.sql`

```sql
-- Schéma physique d'une unité d'allocation (spec03 §4). @AllocUnitId NULL = toutes les unités utilisateur.
-- sys.columns en jointure externe : les colonnes supprimées et l'uniqueifier n'y ont pas de ligne.
SELECT au.allocation_unit_id                                   AS AllocUnitId,
       OBJECT_SCHEMA_NAME(p.object_id)                         AS SchemaName,
       OBJECT_NAME(p.object_id)                                AS TableName,
       p.index_id                                              AS IndexId,
       au.type                                                 AS AllocUnitType,
       pc.partition_column_id                                  AS PartitionColumnId,
       c.name                                                  AS ColumnName,
       pc.leaf_offset                                          AS LeafOffset,
       pc.leaf_null_bit                                        AS LeafNullBit,
       pc.leaf_bit_position                                    AS LeafBitPosition,
       pc.max_inrow_length                                     AS MaxInrowLength,
       pc.system_type_id                                       AS SystemTypeId,
       pc.precision                                            AS [Precision],
       pc.scale                                                AS Scale,
       CONVERT(int, COLLATIONPROPERTY(pc.collation_name, 'CodePage')) AS CodePage,
       pc.is_nullable                                          AS IsNullable,
       pc.is_dropped                                           AS IsDropped,
       pc.is_uniqueifier                                       AS IsUniqueifier,
       pc.has_default                                          AS HasDefault,
       CONVERT(nvarchar(4000), pc.default_value)               AS DefaultValue,
       ic.key_ordinal                                          AS KeyOrdinal
FROM sys.allocation_units AS au
JOIN sys.partitions AS p ON p.partition_id = au.container_id
JOIN sys.system_internals_partition_columns AS pc ON pc.partition_id = p.partition_id
LEFT JOIN sys.columns AS c
       ON c.object_id = p.object_id AND c.column_id = pc.partition_column_id AND pc.is_dropped = 0
LEFT JOIN sys.index_columns AS ic
       ON ic.object_id = p.object_id AND ic.index_id = 1 AND p.index_id = 1
      AND ic.column_id = pc.partition_column_id AND ic.key_ordinal > 0 AND pc.is_dropped = 0
WHERE OBJECTPROPERTY(p.object_id, 'IsMsShipped') = 0
  AND au.type IN (1, 3)
  AND (@AllocUnitId IS NULL OR au.allocation_unit_id = @AllocUnitId)
ORDER BY au.allocation_unit_id, pc.leaf_null_bit;
```

Embed it: in `SqlLogExplorer.csproj`, `<ItemGroup><EmbeddedResource Include="Backends\Sql\*.sql" /></ItemGroup>`.

- [ ] Step 2: Write the failing unit tests

`tests/SqlLogExplorer.Tests/Decoding/TableSchemaTests.cs`:

```csharp
using SqlLogExplorer.Decoding;

namespace SqlLogExplorer.Tests.Decoding;

public class TableSchemaTests
{
    internal static SchemaColumn Col(int id, string name, int leafOffset, int nullBit, int systemType, int maxLen,
        int? keyOrdinal = null, bool dropped = false, bool uniqueifier = false, int bitPos = 0)
        => new(id, name, leafOffset, nullBit, bitPos, maxLen, systemType, 0, 0, null, true, dropped, uniqueifier, false, null, keyOrdinal);

    [Fact]
    public void KeyColumns_AreOrderedByKeyOrdinalAndExcludeDroppedAndUniqueifier()
    {
        var schema = new TableSchema(1, "dbo", "k", 1, 1, new[]
        {
            Col(1, "k1", -1, 1, 167, 10, keyOrdinal: 2),
            Col(2, "k2", 4, 2, 56, 4, keyOrdinal: 1),
            Col(67108865, "dropped#67108865", 8, 3, 56, 4, dropped: true),
            Col(3, "uniqueifier", -2, 4, 56, 4, uniqueifier: true),
        });

        Assert.Equal(new[] { "k2", "k1" }, schema.KeyColumns.Select(c => c.Name));
        Assert.Equal("uniqueifier", schema.Uniqueifier?.Name);
    }

    [Theory]
    [InlineData(1, 0, true)] [InlineData(1, 1, true)] [InlineData(1, 2, false)] [InlineData(3, 1, false)]
    public void IsDataRows_OnlyInRowDataOfHeapOrClusteredIndex(int auType, int indexId, bool expected)
        => Assert.Equal(expected, new TableSchema(1, "dbo", "t", indexId, auType, Array.Empty<SchemaColumn>()).IsDataRows);
}
```

Run: `dotnet test tests/SqlLogExplorer.Tests --filter "FullyQualifiedName~TableSchemaTests"`. Expected: build failure.

- [ ] Step 3: Implement `Decoding/TableSchema.cs` and `Backends/SchemaProvider.cs`

```csharp
using System.Collections.Generic;
using System.Linq;

namespace SqlLogExplorer.Decoding;

/// <summary>Une colonne physique d'une partition (sys.system_internals_partition_columns), spec03 §4.</summary>
public sealed record SchemaColumn(
    int PartitionColumnId, string Name, int LeafOffset, int LeafNullBit, int LeafBitPosition, int MaxInrowLength,
    int SystemTypeId, int Precision, int Scale, int? CodePage, bool IsNullable, bool IsDropped, bool IsUniqueifier,
    bool HasDefault, string? DefaultValue, int? KeyOrdinal)
{
    /// <summary>leaf_offset positif = colonne fixe ; négatif = rang d'une colonne variable (-1, -2…).</summary>
    public bool IsFixed => LeafOffset > 0;
    public bool IsBit => SystemTypeId == 104;
}

/// <summary>Schéma d'une unité d'allocation, colonnes dans l'ordre de leaf_null_bit (spec03 §4, §5).</summary>
public sealed record TableSchema(long AllocUnitId, string SchemaName, string TableName, int IndexId, int AllocUnitType, IReadOnlyList<SchemaColumn> Columns)
{
    public string ObjectName => $"{SchemaName}.{TableName}";

    public bool IsDataRows => AllocUnitType == 1 && IndexId is 0 or 1;

    public IEnumerable<SchemaColumn> KeyColumns =>
        Columns.Where(c => !c.IsDropped && c.KeyOrdinal is not null).OrderBy(c => c.KeyOrdinal);

    public SchemaColumn? Uniqueifier => Columns.FirstOrDefault(c => c.IsUniqueifier && !c.IsDropped);
}
```

`Backends/SchemaProvider.cs` reads the embedded resource once (`typeof(SchemaProvider).Assembly.GetManifestResourceStream("SqlLogExplorer.Backends.Sql.TableSchema.sql")`), runs it with `@AllocUnitId`, groups rows into one `TableSchema` (name `dropped#<PartitionColumnId>` when `ColumnName` is null and `IsDropped`, `uniqueifier` when `IsUniqueifier`), and caches results in a `Dictionary<long, TableSchema?>` (an unknown allocation unit is cached as `null`). Several result columns are `tinyint` or `smallint` (`IndexId`, `AllocUnitType`, `SystemTypeId`, `Precision`, `Scale`, `LeafNullBit`, `LeafBitPosition`, `KeyOrdinal`...): read every integer with `Convert.ToInt32(reader.GetValue(i))`, never `GetInt32`, which throws on them. `IsPrivilegedAsync` runs `SELECT CONVERT(bit, ISNULL(IS_SRVROLEMEMBER('sysadmin'), 0) | ISNULL(IS_ROLEMEMBER('db_owner'), 0));`.

Run the unit tests. Expected: 5 pass (1 fact, 4 theory rows).

- [ ] Step 4: Integration test

`tests/SqlLogExplorer.Tests/Integration/SchemaProviderIntegrationTests.cs` creates a database `sle_schema_<guid>` on `SQLLOGEXPLORER_TEST_SQL`, runs

```sql
CREATE TABLE dbo.t(id int NOT NULL PRIMARY KEY, a int NULL, b varchar(20) NULL, c bit, d bit);
CREATE TABLE dbo.d(id int NOT NULL PRIMARY KEY, x int NULL, y int NULL);
ALTER TABLE dbo.d DROP COLUMN x;
CREATE TABLE dbo.n(k int NOT NULL, v int NULL);
CREATE CLUSTERED INDEX cx ON dbo.n(k);
```

and asserts, for the in-row allocation unit of each table (`SELECT allocation_unit_id FROM sys.allocation_units au JOIN sys.partitions p ON p.partition_id = au.container_id WHERE p.object_id = OBJECT_ID('dbo.t') AND au.type = 1`):
- `t`: 5 columns in null-bit order `id, a, b, c, d`; `c.LeafBitPosition == 0`, `d.LeafBitPosition == 1`, same `LeafOffset` for `c` and `d`; `b.LeafOffset == -1`; `id.KeyOrdinal == 1`; `b.CodePage` not null;
- `d`: 3 columns, the dropped one named `dropped#<id>` with `IsDropped`;
- `n`: an `IsUniqueifier` column, `k.KeyOrdinal == 1`;
- `GetAsync(123)` returns `null`;
- `IsPrivilegedAsync()` is true for `sa`.

It drops the database in a `finally`.

Run (setup and test in ONE invocation):

```bash
eval "$(bash tests/fixtures/sqlserver.sh up 2022 sle-it-t2)" && dotnet test tests/SqlLogExplorer.Tests --filter "FullyQualifiedName~SchemaProviderIntegrationTests"; bash tests/fixtures/sqlserver.sh down sle-it-t2
```

Expected: 1 test passed, 0 skipped. A skipped test means the variables did not reach the test process.

- [ ] Step 5: Break and confirm

One at a time, printing the mutated line: change the `LEFT JOIN sys.columns` to `JOIN` (the dropped-column assertion must fail); read `IndexId` with `GetInt32` instead of `Convert.ToInt32` (the test must fail with an invalid cast, which proves the conversion rule is exercised).

- [ ] Step 6: Commit

```bash
git add Decoding/TableSchema.cs Backends/Sql/TableSchema.sql Backends/SchemaProvider.cs SqlLogExplorer.csproj tests/SqlLogExplorer.Tests/Decoding/TableSchemaTests.cs tests/SqlLogExplorer.Tests/Integration/SchemaProviderIntegrationTests.cs
git commit -m "feat: read the physical schema of an allocation unit"
```

---

### Task 3: Fixtures from four SQL Server versions

Spec §10.1, §10.2. Produces the frozen data every decoding test uses. The expected values are the ones the scenarios insert; they are written in the scenario files and repeated in the tests, never read back from the decoder.

Files:
- Create: `tests/fixtures/capture.sh`, `tests/fixtures/capture.sql`, `tests/fixtures/scenarios/*.sql` (list below), `tests/SqlLogExplorer.Tests/FixtureSet.cs`, `tests/SqlLogExplorer.Tests/Fixtures/<version>/<scenario>.json`
- Modify: `tests/SqlLogExplorer.Tests/SqlLogExplorer.Tests.csproj` (copy fixtures to output)
- Test: `tests/SqlLogExplorer.Tests/FixtureSetTests.cs`, `tests/SqlLogExplorer.Tests/Integration/CaptureIntegrationTests.cs`

Interfaces:
- Consumes: `tests/fixtures/sqlserver.sh` (Task 1), `Backends/Sql/TableSchema.sql` (Task 2).
- Produces: `FixtureSet.Versions` (`"2017", "2019", "2022", "2025"`); `FixtureSet.Load(string version, string scenario)` returning `Fixture(string Version, IReadOnlyList<LogRecord> Log, IReadOnlyDictionary<long, TableSchema> Schemas, IReadOnlyList<PageDumpLine> Pages)`; `Fixture.ForTable(string objectName)` returning the in-row `TableSchema` of that table and its data records (`LOP_INSERT_ROWS`, `LOP_DELETE_ROWS`, `LOP_MODIFY_ROW`, `LOP_MODIFY_COLUMNS`) in LSN order; `PageDumpLine(int Style, string PageId, string ParentObject, string Object, string Field, string Value)`.

- [ ] Step 1: `tests/fixtures/sqlserver.sh` exists since Task 1; use it for every container this task starts by hand.

- [ ] Step 2: Scenarios

One file per scenario under `tests/fixtures/scenarios/`. The first line may declare database options: `-- options: RCSI`, `-- options: ADR`, `-- options: RCSI ADR` (ADR only on 2019 and later; `capture.sh` skips an `ADR` scenario on 2017 and writes no fixture for it). Each scenario runs in a fresh database `fx` in `FULL` recovery after a full backup, so the log is not truncated under it. Values are chosen to be recognizable in hex.

`types.sql`:

```sql
CREATE TABLE dbo.types (
  id int NOT NULL PRIMARY KEY,
  ti tinyint, si smallint, bi bigint, b1 bit, b2 bit, b3 bit,
  d9 decimal(9,2), d19 decimal(19,4), d28 decimal(28,6), d38 decimal(38,10), nu numeric(5,1),
  mo money, sm smallmoney, re real, fl float,
  dt date, t0 time(0), t3 time(3), t7 time(7), dtm datetime, sdt smalldatetime,
  d2_0 datetime2(0), d2_3 datetime2(3), d2_7 datetime2(7), dto datetimeoffset(7),
  g uniqueidentifier, ch char(5), vc varchar(20), nch nchar(3), nvc nvarchar(20),
  bn binary(4), vb varbinary(10), x xml);
INSERT dbo.types VALUES (1, 200, -12345, 1234567890123, 1, 0, 1,
  1234567.89, 123456789012345.6789, 1234567890123456789012.123456, 1234567890123456789012345678.0123456789, -1234.5,
  922337203685477.5807, -214748.3648, 3.5, -2.25,
  '2026-10-15', '13:14:15', '13:14:15.123', '13:14:15.1234567', '2026-10-15T13:14:15.123', '2026-10-15T13:14:00',
  '2026-10-15T13:14:15', '2026-10-15T13:14:15.123', '2026-10-15T13:14:15.1234567', '2026-10-15T13:14:15.1234567+02:00',
  '6F9619FF-8B86-D011-B42D-00C04FC964FF', 'ab', 'élan', N'日本', N'Grüße', 0xDEADBEEF, 0x0102, '<a/>');
INSERT dbo.types (id) VALUES (2);
INSERT dbo.types (id, vc, nvc) VALUES (3, '', N'');
```

`nulls.sql` (stale bytes of a `NULL` fixed column, no variable column present, empty versus `NULL`, trailing `NULL`):

```sql
CREATE TABLE dbo.nulls (id int NOT NULL PRIMARY KEY, n1 int NULL, v1 varchar(10) NULL, v2 varchar(10) NULL, v3 varchar(10) NULL);
INSERT dbo.nulls VALUES (1, 30, NULL, NULL, NULL);
INSERT dbo.nulls VALUES (2, NULL, 'V1', NULL, 'V3');
INSERT dbo.nulls VALUES (3, NULL, 'V1', '', 'V3');
INSERT dbo.nulls VALUES (4, 7, 'V1', NULL, NULL);
UPDATE dbo.nulls SET n1 = NULL WHERE id = 1;
INSERT dbo.nulls VALUES (5, NULL, NULL, NULL, NULL);
```

`modify.sql` (the spec §2 tables and every replay case on a clustered table):

```sql
CREATE TABLE dbo.t (id int NOT NULL PRIMARY KEY, a int NULL, b varchar(20) NULL, c bit, d bit);
INSERT dbo.t VALUES (1, 10, 'abc', 1, 0);
UPDATE dbo.t SET a = 11 WHERE id = 1;
UPDATE dbo.t SET b = 'abcdef' WHERE id = 1;
UPDATE dbo.t SET d = 1 WHERE id = 1;
UPDATE dbo.t WITH (TABLOCKX) SET a = 20 WHERE id = 1;
UPDATE dbo.t SET b = NULL WHERE id = 1;
UPDATE dbo.t SET b = 'zz' WHERE id = 1;
BEGIN TRAN; UPDATE dbo.t SET a = 99 WHERE id = 1; ROLLBACK;
BEGIN TRAN; SAVE TRAN s; UPDATE dbo.t SET a = 98 WHERE id = 1; ROLLBACK TRAN s; UPDATE dbo.t SET a = 21 WHERE id = 1; COMMIT;
DELETE dbo.t WHERE id = 1;
CREATE TABLE dbo.u (id int NOT NULL PRIMARY KEY, a int NULL, x char(50) NULL, b varchar(20) NULL, c bit, d bit);
INSERT dbo.u VALUES (2, 10, 'x', 'abc', 0, 1);
UPDATE dbo.u SET a = 99, b = 'zz' WHERE id = 2;
UPDATE dbo.u SET a = 100 WHERE id = 2;
CREATE TABLE dbo.s (id int NOT NULL PRIMARY KEY, a int NULL);
INSERT dbo.s VALUES (1, 10); INSERT dbo.s VALUES (2, 10); INSERT dbo.s VALUES (0, 5);
UPDATE dbo.s SET a = 11 WHERE id = 1;
BEGIN TRAN; INSERT dbo.s VALUES (7, 70); ROLLBACK;
BEGIN TRAN; DELETE dbo.s WHERE id = 2; ROLLBACK;
```

`keys.sql`:

```sql
CREATE TABLE dbo.k (k1 varchar(10) NOT NULL, k2 int NOT NULL, a int NULL, PRIMARY KEY (k1, k2));
INSERT dbo.k VALUES ('abc', 7, 1);
UPDATE dbo.k SET a = 2 WHERE k1 = 'abc';
CREATE TABLE dbo.c (k1 varchar(10) NOT NULL, k2 varchar(10) NOT NULL, v int NULL, PRIMARY KEY (k1, k2));
INSERT dbo.c VALUES ('A', 'BCD', 1), ('AB', 'CD', 2);
UPDATE dbo.c WITH (TABLOCKX) SET v = v + 10;
CREATE TABLE dbo.n (k int NOT NULL, v int NULL);
CREATE CLUSTERED INDEX cx ON dbo.n (k);
INSERT dbo.n VALUES (1, 1), (1, 2);
UPDATE dbo.n SET v = v + 10;
CREATE TABLE dbo.nk (k1 varchar(5) NOT NULL, k2 int NULL, v int NULL);
CREATE CLUSTERED INDEX cx ON dbo.nk (k1, k2);
INSERT dbo.nk VALUES ('x', NULL, 1), ('x', 0, 2);
UPDATE dbo.nk SET v = v + 10;
CREATE TABLE dbo.bk (id int NOT NULL, flag bit NOT NULL, c bit NULL, d bit NULL, v int NULL, CONSTRAINT pk_bk PRIMARY KEY (id, flag));
INSERT dbo.bk VALUES (1, 1, 0, 0, 1);
UPDATE dbo.bk SET c = 1, d = 1 WHERE id = 1;
UPDATE dbo.bk SET v = 2 WHERE id = 1;
CREATE TABLE dbo.ix (id int NOT NULL PRIMARY KEY, a int NULL, name varchar(20) NULL);
CREATE INDEX ix_name ON dbo.ix (name);
INSERT dbo.ix VALUES (1, 100, 'alice');
DELETE dbo.ix WHERE id = 1;
```

`schema_change.sql`:

```sql
CREATE TABLE dbo.ac (id int NOT NULL PRIMARY KEY, a int NULL);
INSERT dbo.ac VALUES (1, 10);
ALTER TABLE dbo.ac ADD n int NOT NULL CONSTRAINT df_ac_n DEFAULT 7;
INSERT dbo.ac (id, a) VALUES (2, 20);
DELETE dbo.ac WHERE id = 1;
CREATE TABLE dbo.dc (id int NOT NULL PRIMARY KEY, x int NULL, y int NULL);
ALTER TABLE dbo.dc DROP COLUMN x;
INSERT dbo.dc VALUES (1, 5);
EXEC dbo.SnapshotSchema;
CREATE TABLE dbo.rb (id int NOT NULL PRIMARY KEY, v int NULL);
INSERT dbo.rb VALUES (1, 10);
EXEC dbo.SnapshotSchema;
ALTER INDEX ALL ON dbo.rb REBUILD;
UPDATE dbo.rb SET v = 11 WHERE id = 1;
```

`heap.sql`:

```sql
CREATE TABLE dbo.h (id int NOT NULL, v int NOT NULL, pad char(50) NOT NULL DEFAULT 'p', big varchar(7000) NULL);
INSERT dbo.h (id, v) VALUES (1, 10);
UPDATE dbo.h SET v = 11 WHERE id = 1;
DELETE dbo.h WHERE id = 1;
INSERT dbo.h (id, v) VALUES (2, 20);
INSERT dbo.h (id, v) SELECT TOP (100) 100 + ROW_NUMBER() OVER (ORDER BY (SELECT 1)), 1 FROM sys.all_objects;
UPDATE dbo.h SET big = REPLICATE('x', 7000) WHERE id = 101;
```

`split.sql`:

```sql
CREATE TABLE dbo.sp (id int NOT NULL PRIMARY KEY, a int NULL, data char(1000) NOT NULL DEFAULT 'd');
INSERT dbo.sp (id, a) SELECT v * 2, v FROM (VALUES (1),(2),(3),(4),(5),(6),(7)) AS x(v);
INSERT dbo.sp (id, a) VALUES (3, 3);
UPDATE dbo.sp SET a = 40 WHERE id = 4;
CREATE TABLE dbo.ov (id int NOT NULL PRIMARY KEY, v1 varchar(5000) NULL, v2 varchar(5000) NULL, m varchar(max) NULL);
INSERT dbo.ov VALUES (1, REPLICATE('c', 5000), REPLICATE('d', 5000), REPLICATE(CONVERT(varchar(max), 'm'), 10000));
```

`collations.sql` (UTF-8 skipped on 2017 by `capture.sh`, which drops the `dbo.u8` lines there: lines tagged `-- 2019+`):

```sql
CREATE TABLE dbo.co (id int NOT NULL PRIMARY KEY, gr varchar(10) COLLATE Greek_CI_AS, ja varchar(10) COLLATE Japanese_CI_AS);
INSERT dbo.co VALUES (1, N'αβγ', N'日本語');
CREATE TABLE dbo.u8 (id int NOT NULL PRIMARY KEY, s varchar(20) COLLATE Latin1_General_100_CI_AS_SC_UTF8); -- 2019+
INSERT dbo.u8 VALUES (1, N'Grüße 日本'); -- 2019+
```

`versioned.sql` with `-- options: RCSI`, and `versioned_adr.sql` with `-- options: RCSI ADR`, both:

```sql
CREATE TABLE dbo.vr (id int NOT NULL PRIMARY KEY, a int NULL, b varchar(20) NULL);
INSERT dbo.vr VALUES (1, 10, 'abc');
UPDATE dbo.vr SET a = 11 WHERE id = 1;
UPDATE dbo.vr SET a = 12 WHERE id = 1;
UPDATE dbo.vr SET b = 'abcd' WHERE id = 1;
BEGIN TRAN; SAVE TRAN s; UPDATE dbo.vr SET a = 99 WHERE id = 1; ROLLBACK TRAN s; COMMIT;
DELETE dbo.vr WHERE id = 1;
```

- [ ] Step 3: `tests/fixtures/capture.sql`

Runs in `fx` after a scenario, with the sqlcmd variable `$(StartLsn)` set to the last LSN before the scenario (decimal form). It returns ONE row, ONE `nvarchar(max)` column holding the JSON document:

```sql
SET NOCOUNT ON;
DECLARE @log nvarchar(max) = (
  SELECT [Current LSN] AS Lsn, [Operation] AS Operation, [Context] AS Context, [Transaction ID] AS TransactionId,
         [AllocUnitName] AS AllocUnitName, [AllocUnitId] AS AllocUnitId, [PartitionId] AS PartitionId,
         [Page ID] AS PageId, [Slot ID] AS SlotId, [Offset in Row] AS OffsetInRow, [Modify Size] AS ModifySize,
         [Num Elements] AS NumElements, [Description] AS Description, [Transaction Name] AS TransactionName,
         [RowLog Contents 0] AS Rlc0, [RowLog Contents 1] AS Rlc1, [RowLog Contents 2] AS Rlc2,
         [RowLog Contents 3] AS Rlc3, [RowLog Contents 4] AS Rlc4, [RowLog Contents 5] AS Rlc5,
         [Lock Information] AS LockInformation, [Log Record] AS LogRecord
  FROM sys.fn_dblog('$(StartLsn)', NULL)
  WHERE ISNULL([AllocUnitName], N'') NOT LIKE N'sys.%' AND ISNULL([AllocUnitName], N'') NOT LIKE N'dbo.SchemaSnapshot%'
  FOR JSON PATH, INCLUDE_NULL_VALUES);
EXEC dbo.SnapshotSchema;
DECLARE @schema nvarchar(max) = (SELECT DISTINCT * FROM dbo.SchemaSnapshot FOR JSON PATH, INCLUDE_NULL_VALUES);
CREATE TABLE #page (ParentObject nvarchar(255), Object nvarchar(255), Field nvarchar(255), Value nvarchar(max));
CREATE TABLE #pages (Style int, PageId varchar(20), ParentObject nvarchar(255), Object nvarchar(255), Field nvarchar(255), Value nvarchar(max));
DECLARE @f int, @p int, @sql nvarchar(400);
DECLARE pc CURSOR LOCAL FAST_FORWARD FOR
  SELECT allocated_page_file_id, allocated_page_page_id
  FROM sys.dm_db_database_page_allocations(DB_ID(), NULL, NULL, NULL, 'DETAILED')
  WHERE is_allocated = 1 AND page_type_desc = 'DATA_PAGE'
    AND OBJECTPROPERTY(object_id, 'IsMsShipped') = 0 AND object_id <> OBJECT_ID('dbo.SchemaSnapshot');
OPEN pc; FETCH NEXT FROM pc INTO @f, @p;
WHILE @@FETCH_STATUS = 0
BEGIN
  DELETE #page; SET @sql = N'DBCC PAGE (fx, ' + CAST(@f AS nvarchar(10)) + N', ' + CAST(@p AS nvarchar(10)) + N', 1) WITH TABLERESULTS, NO_INFOMSGS';
  INSERT #page EXEC (@sql);
  INSERT #pages SELECT 1, FORMAT(@f, '0000') + ':' + FORMAT(@p, 'X8'), ParentObject, Object, Field, Value FROM #page;
  DELETE #page; SET @sql = REPLACE(@sql, N', 1) WITH', N', 3) WITH');
  INSERT #page EXEC (@sql);
  INSERT #pages SELECT 3, FORMAT(@f, '0000') + ':' + FORMAT(@p, 'X8'), ParentObject, Object, Field, Value FROM #page;
  FETCH NEXT FROM pc INTO @f, @p;
END
CLOSE pc; DEALLOCATE pc;
SELECT (SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(40)) AS ProductVersion,
               JSON_QUERY(@log) AS Log, JSON_QUERY(@schema) AS [Schema],
               JSON_QUERY((SELECT * FROM #pages FOR JSON PATH)) AS Pages
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
```

`FOR JSON` encodes `varbinary` as base64. If `JSON_QUERY` of an empty array produces `null`, `FixtureSet` treats `null` as empty.

- [ ] Step 4: `tests/fixtures/capture.sh`

```bash
#!/usr/bin/env bash
# Fige les fixtures (spec03 §10.1) : pour chaque version et chaque scénario, base fx neuve, scénario, capture JSON.
# Usage : capture.sh [version…]   (défaut : 2017 2019 2022 2025)
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out="${SLE_FIXTURE_OUT:-$here/../SqlLogExplorer.Tests/Fixtures}"
PASSWORD='Fixture#2026x'
if [ $# -eq 0 ]; then set -- 2017 2019 2022 2025; fi
versions=("$@")
snapshot_proc=$(sed 's/@AllocUnitId IS NULL OR //' "$here/../../Backends/Sql/TableSchema.sql" | sed 's/AND (au.allocation_unit_id = @AllocUnitId)//')
for v in "${versions[@]}"; do
  name="sle-capture-$v-$$"
  if [ "$v" = 2017 ]; then sq=(/opt/mssql-tools/bin/sqlcmd); else sq=(/opt/mssql-tools18/bin/sqlcmd -C); fi
  # -y 0 (pas de troncature des nvarchar(max)) est incompatible avec -h et -W : il ne sert qu'à la capture JSON.
  run() { podman exec -i "$name" "${sq[@]}" -S localhost -U sa -P "$PASSWORD" -b -I "$@"; }
  podman run -d --name "$name" -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$PASSWORD" "mcr.microsoft.com/mssql/server:${v}-latest" >/dev/null
  trap 'podman rm -f -t 2 "$name" >/dev/null' EXIT
  until run -Q 'SELECT 1' >/dev/null 2>&1; do sleep 2; done
  mkdir -p "$out/$v"
  for f in "$here"/scenarios/*.sql; do
    scenario=$(basename "$f" .sql)
    opts=$(head -1 "$f" | sed -n 's/^-- options: //p')
    if [ "$v" = 2017 ] && [[ "$opts" == *ADR* ]]; then continue; fi
    run -Q "IF DB_ID('fx') IS NOT NULL BEGIN ALTER DATABASE fx SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE fx; END; CREATE DATABASE fx; ALTER DATABASE fx SET RECOVERY FULL; BACKUP DATABASE fx TO DISK = '/var/opt/mssql/data/fx.bak' WITH INIT;" >/dev/null
    [[ "$opts" == *RCSI* ]] && run -Q "ALTER DATABASE fx SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;" >/dev/null
    [[ "$opts" == *ADR* ]] && run -Q "ALTER DATABASE fx SET ACCELERATED_DATABASE_RECOVERY = ON;" >/dev/null
    { echo "CREATE TABLE dbo.SchemaSnapshot (AllocUnitId bigint, SchemaName sysname, TableName sysname, IndexId int, AllocUnitType int, PartitionColumnId int, ColumnName sysname NULL, LeafOffset int, LeafNullBit int, LeafBitPosition int, MaxInrowLength int, SystemTypeId int, [Precision] int, Scale int, CodePage int NULL, IsNullable bit, IsDropped bit, IsUniqueifier bit, HasDefault bit, DefaultValue nvarchar(4000) NULL, KeyOrdinal int NULL);"
      echo "GO"
      echo "CREATE PROCEDURE dbo.SnapshotSchema AS INSERT dbo.SchemaSnapshot $snapshot_proc"
      echo "GO"; } | run -d fx -i /dev/stdin >/dev/null
    start=$(run -d fx -h -1 -W -Q "SET NOCOUNT ON; SELECT MAX([Current LSN]) FROM sys.fn_dblog(NULL, NULL);" | tr -d '\r' | head -1)
    start_dec=$(python3 -I -c "import sys; print(':'.join(str(int(p,16)) for p in sys.argv[1].split(':')))" "$start")
    if [ "$v" = 2017 ]; then grep -v -- '-- 2019+' "$f"; else cat "$f"; fi | run -d fx -i /dev/stdin >/dev/null
    run -d fx -y 0 -v StartLsn="$start_dec" -i /dev/stdin < "$here/capture.sql" | tr -d '\r' | grep '^{' > "$out/$v/$scenario.json"
    python3 -I -c "import json,sys; json.load(open(sys.argv[1]))" "$out/$v/$scenario.json"
  done
  podman rm -f -t 2 "$name" >/dev/null; trap - EXIT
done
```

The `snapshot_proc` line strips the `@AllocUnitId` filter from `TableSchema.sql` so the procedure snapshots every user allocation unit; `INSERT dbo.SchemaSnapshot <select>` relies on the column order of `TableSchema.sql`, which the `CREATE TABLE` above repeats. `SnapshotSchema` runs at the end of each capture and wherever a scenario calls it (before a rebuild), so the allocation units a rebuild retires keep their schema; `SELECT DISTINCT` removes repeats.

This script is the place where the brief is most likely to be wrong: it was written without being run. If a line fails, fix it, and report every change you had to make and why.

- [ ] Step 5: `FixtureSet` and its test

`tests/SqlLogExplorer.Tests/FixtureSet.cs` deserializes the JSON with `System.Text.Json` (case-insensitive), turns base64 into `byte[]`, normalizes `Lsn` with `Lsn.Normalize`, and builds `TableSchema` objects grouped by `AllocUnitId` (columns ordered by `LeafNullBit`, names as in `SchemaProvider`). `Fixture.ForTable("dbo.t")` returns the schema whose `ObjectName == "dbo.t"`, `IsDataRows` true, of the highest `AllocUnitId` present in the log for that object, and the data records of that allocation unit in LSN order. Only records whose `Context` is `LCX_HEAP`, `LCX_CLUSTERED` or `LCX_MARK_AS_GHOST` are data records (the log also holds `LCX_PFS` records carrying the table's `AllocUnitId`, spec §5). Copy the fixtures to the test output, recursively (they sit in version subfolders): `<None Include="Fixtures/**/*.json" CopyToOutputDirectory="PreserveNewest" />`.

`FixtureSetTests`:

```csharp
namespace SqlLogExplorer.Tests;

public class FixtureSetTests
{
    public static TheoryData<string> Versions => new() { "2017", "2019", "2022", "2025" };

    [Theory, MemberData(nameof(Versions))]
    public void Modify_FirstInsertOfT_IsTheSpecRow(string version)
    {
        var (schema, records) = FixtureSet.Load(version, "modify").ForTable("dbo.t");
        var insert = records.First(r => r.Operation == "LOP_INSERT_ROWS");

        // spec03 §2 : 0x30000D00 01000000 0A000000 xx 0500 00 0100 1700 616263, xx = octet des bits (non comparé).
        var expected = Convert.FromHexString("30000D00010000000A000000000500000100170061626300".Replace(" ", ""))[..23];
        Assert.Equal(expected[..12], insert.RowLogContents0![..12]);
        Assert.Equal(expected[13..], insert.RowLogContents0![13..]);
        Assert.Equal(5, schema.Columns.Count);
    }
}
```

- [ ] Step 6: Capture and run

First run `dotnet test tests/SqlLogExplorer.Tests --filter "FullyQualifiedName~FixtureSetTests"` before capturing: expected 4 failures, each a missing fixture file (this proves the test reads files from the output folder). Then run `bash tests/fixtures/capture.sh` (about ten minutes for four versions). Then `dotnet test tests/SqlLogExplorer.Tests --filter "FullyQualifiedName~FixtureSetTests"`. Expected: 4 passed.

Then check by hand, and write the result in your report, for one version: the `modify` fixture holds a `LOP_MODIFY_ROW` with `Offset in Row` 8 and contents `0x0A` / `0x0B`; the `versioned_adr` fixture exists for 2019, 2022 and 2025 and not 2017; the `schema_change` fixture holds two allocation units named `dbo.rb`.

- [ ] Step 7: Capture as an integration test

`tests/SqlLogExplorer.Tests/Integration/CaptureIntegrationTests.cs` runs `bash tests/fixtures/capture.sh 2022` into a temporary copy (through the environment variable `SLE_FIXTURE_OUT`) and asserts every scenario produced a JSON file whose `Log` array is non-empty. Run it with `SQLLOGEXPLORER_RUN_INTEGRATION=1` in the same invocation. Expected: 1 passed. (`SLE_FIXTURE_OUT` is already honoured by the script above.)

- [ ] Step 8: Commit

Commit the scripts, scenarios, `FixtureSet`, its tests, the csproj change and every fixture file:

```bash
git add tests/fixtures tests/SqlLogExplorer.Tests/FixtureSet.cs tests/SqlLogExplorer.Tests/FixtureSetTests.cs tests/SqlLogExplorer.Tests/Integration/CaptureIntegrationTests.cs tests/SqlLogExplorer.Tests/SqlLogExplorer.Tests.csproj tests/SqlLogExplorer.Tests/Fixtures
git commit -m "test: frozen log fixtures from SQL Server 2017 to 2025"
```

---

### Task 4: R&D note on the two undocumented layouts

Spec §6.2 and §11. This task writes no product code. Its output is `docs/research/rowlog-layouts.md`, which Tasks 7 and 8 implement. The controller reviews the note before Task 7 starts.

Files:
- Create: `docs/research/rowlog-layouts.md`, `docs/research/probes/*.sql` (the statements you ran)

- [ ] Step 1: Index record in `RowLog Contents 2`

From the `keys` and `modify` fixtures of the four versions, and new probes on a container from `sqlserver.sh` where the fixtures are not enough, establish for the clustered key carried by modify records:
- the order of fixed key columns from offset 1 (key ordinal or column id), with a key `(k2 int, k1 int)` declared in the opposite order of the columns;
- which null bit belongs to which key column;
- where the uniqueifier sits, and what it holds for the first row of a duplicate key;
- whether a `bit` key column is stored as a full byte or packed, and which bit;
- whether a descending key column is stored differently;
- what each key type of spec §1.1 looks like (one probe per type that can be a key: `int`, `bigint`, `smallint`, `tinyint`, `decimal`, `date`, `datetime2`, `datetimeoffset`, `uniqueidentifier`, `char`, `varchar`, `nchar`, `nvarchar`, `binary`, `varbinary`).

- [ ] Step 2: `LOP_MODIFY_COLUMNS`

From the `modify` and `versioned*` fixtures and new probes, establish the meaning of `RowLog Contents 0`, `1`, `3`, `4` and `5` and `Num Elements`, on the cases of spec §6.2: two fixed columns; a fixed and a variable column; two variable columns; a change in length; a column set to `NULL` and back; a single-column update under RCSI and under ADR; a compensation `LOP_MODIFY_COLUMNS`. The deliverable is a rule that turns one record into a list of `(offset, before bytes, after bytes)` fragments, and a check of that rule: applying the fragments to the row before (from `DBCC PAGE` or from the preceding records) gives the row after, on every case and every version.

- [ ] Step 3: Write the note

`docs/research/rowlog-layouts.md` has one section per layout, each with: the rule, stated so that code can follow it; a table of the cases checked, with the version, the record bytes and the result; and what remains unknown. When a case does not fit the rule, say so; a rule that covers three versions out of four is a valid result, and the decoder will answer "layout unknown" on the fourth (spec §6.2).

- [ ] Step 4: Commit

```bash
git add docs/research
git commit -m "docs: layouts of the clustered key record and LOP_MODIFY_COLUMNS"
```

Remove every container you started.

---

### Task 5: Decoded row model and type decoders

Spec §4 (`TypeDecoders`, `DecodedRow`).

Files:
- Create: `Decoding/DecodedRow.cs`, `Decoding/TypeDecoder.cs`
- Test: `tests/SqlLogExplorer.Tests/Decoding/TypeDecoderTests.cs`

Interfaces:
- Consumes: `SchemaColumn` (Task 2), `FixtureSet` (Task 3).
- Produces:

```csharp
public enum FieldRole { StatusA, StatusB, FixedEnd, FixedColumn, ColumnCount, NullBitmap, VariableCount, VariableOffset,
    VariableColumn, LobPointer, VersionTag, Unused, DroppedColumn, UnknownTrailer, Malformed }
public sealed record DecodedField(int Offset, int Length, FieldRole Role, string? Column = null, object? Value = null, string? Problem = null);
public sealed record DecodedColumn(string Name, object? Value, string? Note = null);   // Value null = SQL NULL
public sealed record DecodedRow(int RecordType, IReadOnlyList<DecodedField> Fields, IReadOnlyList<DecodedColumn> Columns)
{
    public bool HasProblem => Fields.Any(f => f.Role == FieldRole.Malformed || f.Problem is not null);
    public string RecordTypeName => RecordType switch { 0 => "primary record", 1 => "forwarded record", 2 => "forwarding stub",
        3 => "index record", 4 => "blob fragment", 5 => "ghost index record", 6 => "ghost data record", 7 => "ghost version record", _ => "unknown" };
}
public static class TypeDecoder
{
    public static bool IsSupported(int systemTypeId);
    public static object? Decode(SchemaColumn column, ReadOnlySpan<byte> bytes);   // unsupported type: returns the bytes as byte[]
}
```

- [ ] Step 1: Write the failing tests

`TypeDecoderTests` has two parts. First, hand-built bytes for each type (the encodings below are SQL Server's on-disk formats, spec §1.1):

```csharp
using System.Data.SqlTypes;
using SqlLogExplorer.Decoding;
using static SqlLogExplorer.Tests.Decoding.TableSchemaTests;

namespace SqlLogExplorer.Tests.Decoding;

public class TypeDecoderTests
{
    private static object? D(int type, string hex, int precision = 0, int scale = 0, int? codePage = null)
        => TypeDecoder.Decode(Col(1, "c", 4, 1, type, hex.Length / 2) with { Precision = precision, Scale = scale, CodePage = codePage },
                              Convert.FromHexString(hex));

    [Fact] public void Int() => Assert.Equal(10, D(56, "0A000000"));
    [Fact] public void NegativeSmallint() => Assert.Equal((short)-12345, D(52, "C7CF"));
    [Fact] public void Tinyint() => Assert.Equal((byte)200, D(48, "C8"));
    [Fact] public void Bigint() => Assert.Equal(1234567890123L, D(127, "CB04FB711F010000"));
    [Fact] public void Real() => Assert.Equal(3.5f, D(59, "00006040"));
    [Fact] public void Float() => Assert.Equal(-2.25, D(62, "00000000000002C0"));
    [Fact] public void Money() => Assert.Equal(922337203685477.5807m, D(60, "FFFFFFFFFFFFFF7F"));
    [Fact] public void Smallmoney() => Assert.Equal(-214748.3648m, D(122, "00000080"));
    [Fact] public void Decimal9() => Assert.Equal(SqlDecimal.Parse("1234567.89"), D(106, "01" + "15CD5B07", 9, 2));
    [Fact] public void NegativeNumeric() => Assert.Equal(SqlDecimal.Parse("-1234.5"), D(108, "00" + "39300000", 5, 1));
    [Fact] public void Date() => Assert.Equal(new DateOnly(2026, 10, 15), D(40, DateHex(new DateOnly(2026, 10, 15))));
    [Fact] public void Time0() => Assert.Equal(new TimeOnly(13, 14, 15), D(41, "27BA00", 0, 0));
    [Fact] public void Time7() => Assert.Equal(new TimeOnly(13, 14, 15).Add(TimeSpan.FromTicks(1234567)),
        D(41, Ticks5Hex(new TimeOnly(13, 14, 15).Ticks + 1234567), 16, 7));
    // datetime : 4 octets de 1/300 s (14296537 = 13:14:15 + 37/300 s), puis 4 octets de jours depuis 1900 ; .123 est stocké .1233333.
    [Fact] public void Datetime() => Assert.Equal(new DateTime(2026, 10, 15, 13, 14, 15).AddTicks((long)Math.Round(37 * (TimeSpan.TicksPerSecond / 300.0))),
        D(61, "D925DA00" + DaysHex1900(new DateTime(2026, 10, 15))));
    [Fact] public void Smalldatetime() => Assert.Equal(new DateTime(2026, 10, 15, 13, 14, 0),
        D(58, "1A03" + Days16Hex1900(new DateTime(2026, 10, 15))));
    [Fact] public void Uniqueidentifier() => Assert.Equal(Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF"),
        D(36, Convert.ToHexString(Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF").ToByteArray())));
    [Fact] public void VarcharLatin1() => Assert.Equal("élan", D(167, "E96C616E", codePage: 1252));
    [Fact] public void Nvarchar() => Assert.Equal("Grüße", D(231, Convert.ToHexString(System.Text.Encoding.Unicode.GetBytes("Grüße"))));
    [Fact] public void VarcharUtf8() => Assert.Equal("Grüße", D(167, Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("Grüße")), codePage: 65001));
    [Fact] public void Varbinary() => Assert.Equal(new byte[] { 1, 2 }, D(165, "0102"));
    [Fact] public void Xml_IsNotSupported()
    {
        Assert.False(TypeDecoder.IsSupported(241));
        Assert.Equal(new byte[] { 0xAB }, D(241, "AB"));
    }

    private static string DateHex(DateOnly d) => Convert.ToHexString(BitConverter.GetBytes(d.DayNumber))[..6];
    private static string Ticks5Hex(long ticks) => Convert.ToHexString(BitConverter.GetBytes(ticks))[..10];
    private static string DaysHex1900(DateTime d) => Convert.ToHexString(BitConverter.GetBytes((int)(d.Date - new DateTime(1900, 1, 1)).TotalDays));
    private static string Days16Hex1900(DateTime d) => Convert.ToHexString(BitConverter.GetBytes((ushort)(d.Date - new DateTime(1900, 1, 1)).TotalDays));
}
```

The literals in this brief were computed with a script, not read from SQL Server: if the fixture part of this test disagrees with one of them, the fixture wins, and your report says which literal was wrong.

Second, a fixture theory: for each version, decode every column of the first `dbo.types` insert of the `types` fixture with `RowDecoder` (Task 6)... `RowDecoder` does not exist yet, so in this task the fixture check reads the column bytes directly from `leaf_offset` / `max_inrow_length` for fixed columns only, and asserts the values inserted in `types.sql` for `ti, si, bi, d9, d19, d28, d38, nu, mo, sm, re, fl, dt, t0, t3, t7, dtm, sdt, d2_0, d2_3, d2_7, dto, g, ch, bn` (25 columns). Variable columns are checked in Task 6.

- [ ] Step 2: Run to verify they fail

Run: `dotnet test tests/SqlLogExplorer.Tests --filter "FullyQualifiedName~TypeDecoderTests"`. Expected: build failure.

- [ ] Step 3: Implement

`Decoding/DecodedRow.cs` as in the Interfaces block. `Decoding/TypeDecoder.cs`:

```csharp
using System;
using System.Buffers.Binary;
using System.Data.SqlTypes;
using System.Text;

namespace SqlLogExplorer.Decoding;

/// <summary>Octets d'une colonne vers une valeur .NET, selon le format disque de SQL Server (spec03 §1.1, §4).</summary>
public static class TypeDecoder
{
    static TypeDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly DateTime Epoch1900 = new(1900, 1, 1);

    public static bool IsSupported(int systemTypeId) => systemTypeId is
        48 or 52 or 56 or 127 or 104 or 106 or 108 or 60 or 122 or 59 or 62 or
        40 or 41 or 61 or 58 or 42 or 43 or 36 or 175 or 167 or 239 or 231 or 173 or 165;

    public static object? Decode(SchemaColumn c, ReadOnlySpan<byte> b) => c.SystemTypeId switch
    {
        48 => b[0],
        52 => BinaryPrimitives.ReadInt16LittleEndian(b),
        56 => BinaryPrimitives.ReadInt32LittleEndian(b),
        127 => BinaryPrimitives.ReadInt64LittleEndian(b),
        104 => (b[0] >> c.LeafBitPosition & 1) == 1,
        106 or 108 => Decimal(c, b),
        60 => BinaryPrimitives.ReadInt64LittleEndian(b) / 10000m,
        122 => BinaryPrimitives.ReadInt32LittleEndian(b) / 10000m,
        59 => BinaryPrimitives.ReadSingleLittleEndian(b),
        62 => BinaryPrimitives.ReadDoubleLittleEndian(b),
        40 => DateOnly.FromDayNumber(UInt24(b)),
        41 => Time(b, c.Scale),
        61 => Epoch1900.AddDays(BinaryPrimitives.ReadInt32LittleEndian(b[4..]))
                       .AddTicks((long)Math.Round(BinaryPrimitives.ReadUInt32LittleEndian(b) * (TimeSpan.TicksPerSecond / 300.0))),
        58 => Epoch1900.AddDays(BinaryPrimitives.ReadUInt16LittleEndian(b[2..])).AddMinutes(BinaryPrimitives.ReadUInt16LittleEndian(b)),
        42 => DateTime2(b, c.Scale),
        43 => DateTimeOffset(b, c.Scale),
        36 => new Guid(b),
        175 or 167 => Encoding.GetEncoding(c.CodePage ?? 1252).GetString(b),
        239 or 231 => Encoding.Unicode.GetString(b),
        173 or 165 => b.ToArray(),
        _ => b.ToArray(),
    };

    // decimal/numeric : 1 octet de signe (1 = positif), puis l'entier non signé sur 4, 8, 12 ou 16 octets.
    private static SqlDecimal Decimal(SchemaColumn c, ReadOnlySpan<byte> b)
    {
        var data = new int[4];
        for (var i = 0; i < (b.Length - 1) / 4; i++) data[i] = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(1 + 4 * i, 4));
        return new SqlDecimal((byte)c.Precision, (byte)c.Scale, b[0] == 1, data);
    }

    private static int UInt24(ReadOnlySpan<byte> b) => b[0] | b[1] << 8 | b[2] << 16;

    // time(n) : 3 à 5 octets, unités de 10^-n seconde.
    private static long TimeTicks(ReadOnlySpan<byte> b, int scale)
    {
        long units = 0;
        for (var i = b.Length - 1; i >= 0; i--) units = units << 8 | b[i];
        for (var i = scale; i < 7; i++) units *= 10;
        return units;
    }

    private static int TimeLength(int scale) => scale <= 2 ? 3 : scale <= 4 ? 4 : 5;

    private static TimeOnly Time(ReadOnlySpan<byte> b, int scale) => new(TimeTicks(b, scale));

    private static DateTime DateTime2(ReadOnlySpan<byte> b, int scale)
    {
        var t = TimeLength(scale);
        return DateOnly.FromDayNumber(UInt24(b[t..])).ToDateTime(TimeOnly.MinValue).AddTicks(TimeTicks(b[..t], scale));
    }

    // datetimeoffset : heure UTC, date UTC, puis décalage en minutes (int16).
    private static DateTimeOffset DateTimeOffset(ReadOnlySpan<byte> b, int scale)
    {
        var t = TimeLength(scale);
        var utc = DateTime2(b[..(t + 3)], scale);
        var offset = TimeSpan.FromMinutes(BinaryPrimitives.ReadInt16LittleEndian(b[(t + 3)..]));
        return new DateTimeOffset(utc + offset, offset);
    }
}
```

The `Time0` test passes `Precision = 0, Scale = 0`; `Time7` passes `Scale = 7`. `SchemaColumn.Scale` for `time`, `datetime2` and `datetimeoffset` holds the fractional precision in `sys.system_internals_partition_columns`; if the fixtures show otherwise, use what they show and report it.

- [ ] Step 4: Run to verify they pass

Same command. Expected: 21 facts + 4 fixture theory rows = 25 passed. Any other count means the filter or the work is wrong.

- [ ] Step 5: Break and confirm

One at a time, printing the mutated line: swap the two halves of `datetime` (days first); make `smallmoney` divide by 1000; ignore `c.CodePage` (always 1252). Each must make at least one named test fail; say which.

- [ ] Step 6: Commit

```bash
git add Decoding/DecodedRow.cs Decoding/TypeDecoder.cs tests/SqlLogExplorer.Tests/Decoding/TypeDecoderTests.cs
git commit -m "feat: decode SQL Server column bytes to values"
```

---

### Task 6: Row decoder

Spec §5 and §8.

Files:
- Create: `Decoding/RowDecoder.cs`
- Test: `tests/SqlLogExplorer.Tests/Decoding/RowDecoderTests.cs`

Interfaces:
- Consumes: `TableSchema`, `SchemaColumn` (Task 2), `DecodedRow`, `TypeDecoder` (Task 5), `FixtureSet` (Task 3).
- Produces: `public static class RowDecoder { public static DecodedRow Decode(byte[]? row, TableSchema? schema); }` (a `null` or empty row gives one malformed field "no row image": a compensation delete has empty contents). Each present column yields exactly one `DecodedColumn` in `leaf_null_bit` order (dropped columns yield none); each field of the row yields one or more `DecodedField`s. Column notes, used verbatim by later tasks: `"added after the row was written"`, `"off-row value not followed"`, `"type not decoded"`.

- [ ] Step 1: Write the failing tests

```csharp
using SqlLogExplorer.Decoding;
using static SqlLogExplorer.Tests.Decoding.TableSchemaTests;

namespace SqlLogExplorer.Tests.Decoding;

public class RowDecoderTests
{
    // spec03 §2 : t(id int not null primary key, a int null, b varchar(20) null, c bit, d bit), ligne (1, 10, 'abc', 1, 0).
    private static readonly TableSchema T = new(1, "dbo", "t", 1, 1, new[]
    {
        Col(1, "id", 4, 1, 56, 4, keyOrdinal: 1), Col(2, "a", 8, 2, 56, 4), Col(3, "b", -1, 3, 167, 20) with { CodePage = 1252 },
        Col(4, "c", 12, 4, 104, 1, bitPos: 0), Col(5, "d", 12, 5, 104, 1, bitPos: 1),
    });

    private static byte[] Hex(string h) => Convert.FromHexString(h);
    private static object? V(DecodedRow r, string col) => r.Columns.Single(c => c.Name == col).Value;

    [Fact]
    public void SpecRow_DecodesEveryColumn_AndMasksUnusedBits()
    {
        var row = RowDecoder.Decode(Hex("30000D00010000000A000000F105000001001700616263"), T);   // 0xF1 : bits parasites

        Assert.Equal(0, row.RecordType);
        Assert.False(row.HasProblem);
        Assert.Equal(1, V(row, "id")); Assert.Equal(10, V(row, "a")); Assert.Equal("abc", V(row, "b"));
        Assert.Equal(true, V(row, "c")); Assert.Equal(false, V(row, "d"));
    }

    [Fact]
    public void NoVariableColumnPresent_RowEndsAfterBitmap()   // spec03 §2, t_novar (3, 30, NULL)
    {
        var s = new TableSchema(1, "dbo", "t_novar", 1, 1, new[] { Col(1, "id", 4, 1, 56, 4), Col(2, "n1", 8, 2, 56, 4), Col(3, "v1", -1, 3, 167, 10) });
        var row = RowDecoder.Decode(Hex("10000C00030000001E000000030004"), s);

        Assert.False(row.HasProblem);
        Assert.Equal(30, V(row, "n1")); Assert.Null(V(row, "v1"));
    }

    [Fact]
    public void NullFixedColumn_IgnoresStaleBytes()
    {
        var s = new TableSchema(1, "dbo", "x", 1, 1, new[] { Col(1, "id", 4, 1, 56, 4), Col(2, "n1", 8, 2, 56, 4) });
        var row = RowDecoder.Decode(Hex("10000C0004000000E0E61322020002"), s);   // bit 2 du bitmap = n1 NULL

        Assert.Null(V(row, "n1"));
    }

    [Fact]
    public void ColumnAddedAfterRow_TakesDefault_AndIsNotMalformed()
    {
        var s = new TableSchema(1, "dbo", "ac", 1, 1, new[]
        {
            Col(1, "id", 4, 1, 56, 4), Col(2, "a", 8, 2, 56, 4),
            Col(3, "n", 12, 3, 56, 4) with { HasDefault = true, DefaultValue = "7", IsNullable = false },
        });
        var row = RowDecoder.Decode(Hex("10000C00010000000A000000020000"), s);   // N = 2

        Assert.False(row.HasProblem);
        var n = row.Columns.Single(c => c.Name == "n");
        Assert.Equal("7", n.Value); Assert.Equal("added after the row was written", n.Note);
    }

    [Fact]
    public void OffRowPointer_IsAnnotatedNotDecoded()
    {
        var s = new TableSchema(1, "dbo", "o", 1, 1, new[] { Col(1, "id", 4, 1, 56, 4), Col(2, "m", -1, 2, 167, 8000) });
        var pointer = new string('0', 48);   // 24 octets de pointeur : données de 15 à 39, fin 0x27 | 0x8000
        var row = RowDecoder.Decode(Hex("30000800010000000200000100" + "2780" + pointer), s);

        Assert.False(row.HasProblem);
        Assert.Equal("off-row value not followed", row.Columns.Single(c => c.Name == "m").Note);
        Assert.Contains(row.Fields, f => f.Role == FieldRole.LobPointer);
    }

    [Fact]
    public void VersionTag_FollowsVariableData_TrailerAfterIt()
    {
        var tag = new string('A', 28);
        var row = RowDecoder.Decode(Hex("70000D00010000000A000000010500000100170061626300" [..46] + tag + "FFFF"), T);

        Assert.Contains(row.Fields, f => f.Role == FieldRole.VersionTag && f.Offset == 23 && f.Length == 14);
        Assert.Contains(row.Fields, f => f.Role == FieldRole.UnknownTrailer && f.Offset == 37 && f.Length == 2);
        Assert.Equal("abc", V(row, "b"));
    }

    [Theory]
    [InlineData("")] [InlineData("30")] [InlineData("300040")] [InlineData("30000D00010000000A000000010500000100FF00616263")]
    public void MalformedInput_NeverThrows(string hex)
    {
        var row = RowDecoder.Decode(Hex(hex), T);
        Assert.True(row.HasProblem);
    }

    [Fact]
    public void NullOrEmptyRow_IsMalformed_NotAnException()
    {
        Assert.True(RowDecoder.Decode(null, T).HasProblem);
        Assert.True(RowDecoder.Decode(Array.Empty<byte>(), T).HasProblem);
    }

    [Fact]
    public void NoVariableArea_ClearNullBit_IsEmptyNotNull()   // ligne (3, '') : pas de zone variable, bit NULL à 0
    {
        var s = new TableSchema(1, "dbo", "e", 1, 1, new[] { Col(1, "id", 4, 1, 56, 4), Col(2, "vc", -1, 2, 167, 10) with { CodePage = 1252 } });
        var row = RowDecoder.Decode(Hex("1000080003000000020000"), s);

        Assert.False(row.HasProblem);
        Assert.Equal("", V(row, "vc"));
    }

    [Fact]
    public void FixedEndDifferentFromSchema_IsMalformed()   // spec03 §8 : F = 14 alors que le schéma prévoit 13
    {
        var row = RowDecoder.Decode(Hex("30000E00010000000A0000000100" + "0500000100180061626300"[..20]), T);

        Assert.Contains(row.Fields, f => f.Role == FieldRole.Malformed && f.Problem!.Contains("schema predicts 13"));
    }

    [Fact]
    public void IndexRecord_IsShownRaw()   // spec03 §5 : type 3, aucune valeur décodée
    {
        var row = RowDecoder.Decode(Hex("360100000002000001001100616C696365"), T);

        Assert.Equal(3, row.RecordType);
        Assert.Empty(row.Columns);
        Assert.Equal("index record", row.RecordTypeName);
    }

    [Fact]
    public void WithoutSchema_StructureOnly()
    {
        var row = RowDecoder.Decode(Hex("30000D00010000000A000000010500000100170061626300"[..46]), null);

        Assert.Empty(row.Columns);
        Assert.Contains(row.Fields, f => f.Role == FieldRole.VariableOffset);
        Assert.Contains(row.Fields, f => f.Role == FieldRole.FixedColumn && f.Offset == 4 && f.Length == 9);
    }
}
```

Add fixture theories over the four versions (`TheoryData` of versions as in `FixtureSetTests`), each asserting values written in the scenario file:
- `types`: row 1, every column of `types.sql` except `x` (25 fixed + `vc`, `nvc`, `vb`), `x` with note `"type not decoded"`; row 2, every column but `id` null; row 3, `vc == ""` and `nvc == ""` (empty, not null).
- `nulls`: rows 1 to 5 against the inserted values, row 3 `v2 == ""` and row 2 `v2 == null`.
- `schema_change`: the delete of `dbo.ac` id 1 decodes with `n == "7"` and the note; `dbo.dc` row 1 has columns `id, y` only (the dropped one yields no `DecodedColumn`).
- `keys`: the `LOP_DELETE_ROWS` of `dbo.ix` on the nonclustered index (`AllocUnitId` whose schema has `IndexId == 2`, or record type 3 when no schema) yields no column.
- `collations`: `gr == "αβγ"`, `ja == "日本語"`, and on 2019 and later `s == "Grüße 日本"`.
- `versioned` and `versioned_adr`: the insert decodes `a == 10`, `b == "abc"`, with a `VersionTag` field.
- `split`: the `dbo.ov` insert has two variable columns annotated `"off-row value not followed"`... only if they went off-row; read the fixture and assert what the bytes say, then say in your report what you found.

Add one sweep theory over every version and every scenario: every `LOP_INSERT_ROWS` and `LOP_DELETE_ROWS` data record (context `LCX_HEAP`, `LCX_CLUSTERED`, `LCX_MARK_AS_GHOST`, schema `IsDataRows`) decodes without exception, and `HasProblem` is false except for compensation records with empty contents. It must cover `dbo.nk` of the `keys` scenario, whose `('x', NULL)` row has a zero-length uniqueifier (`30000C000E0000000100000004000402001500160078`): the plan's first version of this decoder threw on it.

Also compare, for each version, the fields of the `dbo.t` row in the `modify` fixture's `DBCC PAGE (…, 3)` output (`Fixture.Pages`, `Style == 3`, lines whose `Field` is a column name; the offset and length are in the `Object` column, as `Slot 0 Column 2 Offset 0x8 Length 4 Length (physical) 4`) with the `DecodedField` offsets and lengths of the same row. The insert in the log and the row on the page differ (the page shows the row after its updates, or not at all after the delete): pick a table whose row is still on the page at capture time (`dbo.u` id 2) and its last image (replay is Task 9; for this check, decode the page row from the `Style == 1` memory dump instead of the log).

- [ ] Step 2: Run to verify they fail

Run: `dotnet test tests/SqlLogExplorer.Tests --filter "FullyQualifiedName~RowDecoderTests"`. Expected: build failure.

- [ ] Step 3: Implement `Decoding/RowDecoder.cs`

```csharp
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace SqlLogExplorer.Decoding;

/// <summary>Parcours d'une ligne de données (spec03 §5). Ne lève jamais d'exception : les défauts deviennent des champs Malformed.</summary>
public static class RowDecoder
{
    public static DecodedRow Decode(byte[]? row, TableSchema? schema)
    {
        var fields = new List<DecodedField>();
        var columns = new List<DecodedColumn>();
        if (row is null || row.Length < 4)
        {
            fields.Add(new DecodedField(0, row?.Length ?? 0, FieldRole.Malformed, Problem: row is null || row.Length == 0 ? "no row image" : "row shorter than its header"));
            return new DecodedRow(row is { Length: > 0 } ? row[0] >> 1 & 7 : -1, fields, columns);
        }

        var a = row[0];
        var type = a >> 1 & 7;
        fields.Add(new DecodedField(0, 1, FieldRole.StatusA, Value: a));
        fields.Add(new DecodedField(1, 1, FieldRole.StatusB, Value: row[1]));
        if (type != 0)
        {
            fields.Add(new DecodedField(2, row.Length - 2, FieldRole.Unused));
            return new DecodedRow(type, fields, columns);
        }

        int fixedEnd = BinaryPrimitives.ReadUInt16LittleEndian(row.AsSpan(2));
        fields.Add(new DecodedField(2, 2, FieldRole.FixedEnd, Value: fixedEnd));
        if (fixedEnd < 4 || fixedEnd + 2 > row.Length)
            return Malformed(row, fields, columns, type, 4, "fixed-data end offset beyond the row");

        int n = BinaryPrimitives.ReadUInt16LittleEndian(row.AsSpan(fixedEnd));
        fields.Add(new DecodedField(fixedEnd, 2, FieldRole.ColumnCount, Value: n));
        var bitmapLength = (a & 0x10) != 0 ? (n + 7) / 8 : 0;
        var bitmapStart = fixedEnd + 2;
        if (bitmapStart + bitmapLength > row.Length)
            return Malformed(row, fields, columns, type, bitmapStart, "null bitmap beyond the row");
        if (bitmapLength > 0) fields.Add(new DecodedField(bitmapStart, bitmapLength, FieldRole.NullBitmap));
        bool IsNull(int nullBit) => bitmapLength > 0 && (row[bitmapStart + (nullBit - 1) / 8] >> ((nullBit - 1) % 8) & 1) == 1;

        // Zone variable (bit 5 de A), offsets masqués par 0x7FFF, bit 0x8000 = pointeur hors ligne.
        var pos = bitmapStart + bitmapLength;
        var ends = new List<(int End, bool OffRow)>();
        var varStart = pos;
        if ((a & 0x20) != 0)
        {
            if (pos + 2 > row.Length) return Malformed(row, fields, columns, type, pos, "variable column count beyond the row");
            int v = BinaryPrimitives.ReadUInt16LittleEndian(row.AsSpan(pos));
            fields.Add(new DecodedField(pos, 2, FieldRole.VariableCount, Value: v));
            varStart = pos + 2 + 2 * v;
            if (varStart > row.Length) return Malformed(row, fields, columns, type, pos + 2, "offset array beyond the row");
            var previous = varStart;
            for (var j = 0; j < v; j++)
            {
                int entry = BinaryPrimitives.ReadUInt16LittleEndian(row.AsSpan(pos + 2 + 2 * j));
                var end = entry & 0x7FFF;
                if (end < previous || end > row.Length)
                    return Malformed(row, fields, columns, type, pos + 2 + 2 * j, "variable end offset out of order or beyond the row");
                fields.Add(new DecodedField(pos + 2 + 2 * j, 2, FieldRole.VariableOffset, Value: end));
                ends.Add((end, (entry & 0x8000) != 0));
                previous = end;
            }
        }
        var dataEnd = ends.Count > 0 ? ends[^1].End : varStart;

        if (schema is null)
        {
            if (fixedEnd > 4) fields.Add(new DecodedField(4, fixedEnd - 4, FieldRole.FixedColumn));
            for (var j = 0; j < ends.Count; j++)
            {
                var start = j == 0 ? varStart : ends[j - 1].End;
                fields.Add(new DecodedField(start, ends[j].End - start, ends[j].OffRow ? FieldRole.LobPointer : FieldRole.VariableColumn));
            }
        }
        else
        {
            if (n > schema.Columns.Count)
                fields.Add(new DecodedField(fixedEnd, 2, FieldRole.Malformed, Problem: "column count greater than the schema's"));
            // spec03 §8 : la fin de la partie fixe doit être celle que le schéma prévoit pour les N colonnes présentes.
            var expectedEnd = schema.Columns.Where(c => c.IsFixed && c.LeafNullBit <= n)
                .Select(c => c.LeafOffset + (c.IsBit ? 1 : c.MaxInrowLength)).DefaultIfEmpty(4).Max();
            if (expectedEnd != fixedEnd)
                fields.Add(new DecodedField(2, 2, FieldRole.Malformed, Problem: $"fixed-data end offset {fixedEnd}, the schema predicts {expectedEnd}"));
            var claimed = new bool[fixedEnd];
            foreach (var c in schema.Columns)
                DecodeColumn(row, c, n, IsNull, fixedEnd, varStart, ends, fields, columns, claimed);
            for (var i = 4; i < fixedEnd; i++)
            {
                if (claimed[i]) continue;
                var j = i;
                while (j < fixedEnd && !claimed[j]) j++;
                fields.Add(new DecodedField(i, j - i, FieldRole.Unused));
                i = j;
            }
        }

        // Bit 6 de A : étiquette de version de 14 octets après les données variables ; au-delà, traîne inconnue (ADR).
        var tail = dataEnd;
        if ((a & 0x40) != 0)
        {
            if (tail + 14 > row.Length) fields.Add(new DecodedField(tail, row.Length - tail, FieldRole.Malformed, Problem: "versioning tag beyond the row"));
            else { fields.Add(new DecodedField(tail, 14, FieldRole.VersionTag)); tail += 14; }
        }
        if (tail < row.Length) fields.Add(new DecodedField(tail, row.Length - tail, FieldRole.UnknownTrailer));

        return new DecodedRow(type, fields.OrderBy(f => f.Offset).ToList(), columns);
    }

    private static void DecodeColumn(byte[] row, SchemaColumn c, int n, Func<int, bool> isNull, int fixedEnd, int varStart,
        List<(int End, bool OffRow)> ends, List<DecodedField> fields, List<DecodedColumn> columns, bool[] claimed)
    {
        if (c.LeafNullBit > n)   // colonne ajoutée après l'écriture de la ligne : son leaf_offset n'est jamais lu
        {
            if (!c.IsDropped)
                columns.Add(new DecodedColumn(c.Name, c.HasDefault ? c.DefaultValue : null, "added after the row was written"));
            return;
        }

        if (c.IsFixed)
        {
            var length = c.IsBit ? 1 : c.MaxInrowLength;
            if (c.LeafOffset + length > fixedEnd)
            {
                fields.Add(new DecodedField(c.LeafOffset, Math.Max(0, fixedEnd - c.LeafOffset), FieldRole.Malformed, c.Name,
                    Problem: "fixed column beyond the fixed-data end offset"));
                return;
            }
            for (var i = c.LeafOffset; i < c.LeafOffset + length; i++) claimed[i] = true;
            if (c.IsDropped) { fields.Add(new DecodedField(c.LeafOffset, length, FieldRole.DroppedColumn, c.Name)); return; }
            Add(row.AsSpan(c.LeafOffset, length), c.LeafOffset, FieldRole.FixedColumn);
            return;
        }

        var j = -c.LeafOffset - 1;
        if (j >= ends.Count)   // absente du tableau d'offsets (ou zone variable absente) : NULL si le bit le dit, sinon vide
        {
            if (!c.IsDropped) columns.Add(new DecodedColumn(c.Name, isNull(c.LeafNullBit) ? null : SafeDecode(c, ReadOnlySpan<byte>.Empty).Value));
            return;
        }
        var start = j == 0 ? varStart : ends[j - 1].End;
        var len = ends[j].End - start;
        if (c.IsDropped) { fields.Add(new DecodedField(start, len, FieldRole.DroppedColumn, c.Name)); return; }
        if (ends[j].OffRow && !isNull(c.LeafNullBit))
        {
            fields.Add(new DecodedField(start, len, FieldRole.LobPointer, c.Name));
            columns.Add(new DecodedColumn(c.Name, null, "off-row value not followed"));
            return;
        }
        Add(row.AsSpan(start, len), start, FieldRole.VariableColumn);

        void Add(ReadOnlySpan<byte> bytes, int offset, FieldRole role)
        {
            if (isNull(c.LeafNullBit))
            {
                fields.Add(new DecodedField(offset, bytes.Length, role, c.Name));
                columns.Add(new DecodedColumn(c.Name, null));
                return;
            }
            var (value, problem) = SafeDecode(c, bytes);
            fields.Add(new DecodedField(offset, bytes.Length, problem is null ? role : FieldRole.Malformed, c.Name, value, problem));
            columns.Add(new DecodedColumn(c.Name, value, problem ?? (TypeDecoder.IsSupported(c.SystemTypeId) ? null : "type not decoded")));
        }
    }

    // L'uniqueifier de la première ligne d'une clé est une colonne variable de longueur nulle : il vaut 0 (spec03 §5).
    private static (object? Value, string? Problem) SafeDecode(SchemaColumn c, ReadOnlySpan<byte> bytes)
    {
        if (c.IsUniqueifier && bytes.Length == 0) return (0, null);
        try { return (TypeDecoder.Decode(c, bytes), null); }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException)
        {
            return (null, $"{bytes.Length} bytes do not fit the column type");
        }
    }

    private static DecodedRow Malformed(byte[] row, List<DecodedField> fields, List<DecodedColumn> columns, int type, int from, string problem)
    {
        fields.Add(new DecodedField(from, row.Length - from, FieldRole.Malformed, Problem: problem));
        return new DecodedRow(type, fields, columns);
    }
}
```

Two points to verify against the fixtures rather than trust: that `max_inrow_length` is the stored length of every fixed type (`decimal`, `time(n)`, `datetime2(n)`), and that the column-count check (`n > schema.Columns.Count`) holds with a uniqueifier. If either is wrong, fix the code and say so.

- [ ] Step 4: Run to verify they pass

Same command. Count: 11 facts, 4 malformed rows, plus the fixture theories you wrote (state the number). Any other count means the filter or the work is wrong.

- [ ] Step 5: Break and confirm

One at a time, printing the mutated line, and say which test failed:
- read the bitmap after the fixed columns (use `isNull` = always false): `NullFixedColumn_IgnoresStaleBytes` must fail;
- drop the `& 0x7FFF` mask: `OffRowPointer_IsAnnotatedNotDecoded` or a `split` fixture test must fail;
- guard on bit 4 instead of bit 5 for the variable area: `NoVariableColumnPresent_RowEndsAfterBitmap` must fail;
- remove the `c.LeafNullBit <= n` check: `ColumnAddedAfterRow_TakesDefault_AndIsNotMalformed` must fail;
- mask nothing in the bit decoder (`b[0] != 0`): `SpecRow_DecodesEveryColumn_AndMasksUnusedBits` must fail.

- [ ] Step 6: Commit

```bash
git add Decoding/RowDecoder.cs tests/SqlLogExplorer.Tests/Decoding/RowDecoderTests.cs
git commit -m "feat: walk a data row into annotated fields and columns"
```

---

### Task 7: Key decoder

Spec §7.1. Implements the index-record layout established in `docs/research/rowlog-layouts.md` (Task 4). Read that note first; where it contradicts this brief, the note wins and your report says where.

Files:
- Create: `Decoding/KeyDecoder.cs`
- Test: `tests/SqlLogExplorer.Tests/Decoding/KeyDecoderTests.cs`

Interfaces:
- Consumes: `RowDecoder`, `TableSchema`.
- Produces: `public static class KeyDecoder { public static string? FromRow(byte[] row, TableSchema schema); public static string? FromIndexRecord(byte[] record, TableSchema schema); }`. Both return the canonical key string, or `null` when it cannot be decoded. Format: for each key column in key order, `N;` when null, else `V<hex>;` (a `bit` key column: `V00;` or `V01;`, its own bit only); then `U<int>` for the uniqueifier (0 when absent), or nothing when the index has no uniqueifier.

- [ ] Step 1: Write the failing tests

Hand-built cases from spec §2: `dbo.s` (`id int`) insert `0x10000C00010000000A000000020000` and modify key `0x1601000000010000` both give `V01000000;`. `dbo.k` (`k1 varchar(10)`, `k2 int`, key `(k1, k2)`) insert row `0x30000C00070000000100000003000001001600616263` and modify key `0x360700000002000001000F00616263` both give `V616263;V07000000;`. `dbo.c` keys `0x3602000002000B000E0041424344` and `0x3602000002000C000E0041424344` give two different strings. `dbo.n` modify keys `0x36010000000200000100100000000000` and `0x36010000000200000100100001000000` give `V01000000;U0` and `V01000000;U1`. A `null` record or an empty one returns `null`.

Fixture theories over the four versions, on the `keys` and `modify` fixtures: for every clustered table, the key of each insert and delete image equals the key of every modify record (non-compensation) that the scenario applied to that row, and keys of distinct rows differ. In particular: `dbo.nk` rows `('x', NULL)` and `('x', 0)` give two keys, and the `NULL` row's insert key equals its modify keys although the insert holds stale bytes; `dbo.bk` key is stable across the updates of `c` and `d`; `dbo.c` `('A','BCD')` and `('AB','CD')` differ.

- [ ] Step 2: Run to verify they fail

Run: `dotnet test tests/SqlLogExplorer.Tests --filter "FullyQualifiedName~KeyDecoderTests"`. Expected: build failure.

- [ ] Step 3: Implement

`FromRow` decodes the row with `RowDecoder` (it needs the raw bytes of key columns, so take them from the `DecodedField`s whose `Column` is a key column, and the null state from the `DecodedColumn`); a `bit` key column contributes `V0n;` with its bit; the uniqueifier contributes `U` + its int32 value, 0 when its column is `NULL` or absent. `FromIndexRecord` walks the record per the note (Task 4); the spec §2 hypothesis is: status byte at 0, fixed key columns from offset 1 in key order with their `max_inrow_length`, then the column count (2 bytes), the null bitmap if bit 4 of the status byte is set, then if bit 5 is set the variable count, end offsets, and the variable key columns in key order, the uniqueifier among them. Any read beyond the record returns `null`.

- [ ] Step 4: Run to verify they pass, stating the count.

- [ ] Step 5: Break and confirm

One at a time: include the bytes of a `NULL` key column (the `dbo.nk` test must fail); stop masking the `bit` key column (the `dbo.bk` test must fail); drop the uniqueifier (the `dbo.n` test must fail); drop the null bit from the canonical form (the `('x', NULL)` versus `('x', 0)` test must fail).

- [ ] Step 6: Commit `Decoding/KeyDecoder.cs` and its tests: `feat: canonical clustered key from rows and modify records`.

---

### Task 8a: Import `Log Record` for `LOP_MODIFY_COLUMNS`

Spec §3 and §6.2 (version 6). The research of Task 4 showed that `RowLog Contents 0` to `5` do not determine the last after fragment of a `LOP_MODIFY_COLUMNS` record; the whole binary `Log Record` does. The import keeps it for those records only, so the cache does not double in size.

Files:
- Modify: `Models/LogRecord.cs`, `Backends/LogColumns.cs`, `Data/LogCache.cs`, `Data/LogQuery.cs`, `tests/SqlLogExplorer.Tests/FixtureSet.cs`, `tests/SqlLogExplorer.Tests/LogRecordComparer.cs`
- Test: `tests/SqlLogExplorer.Tests/LogCacheTests.cs`, `tests/SqlLogExplorer.Tests/Integration/LogColumnsIntegrationTests.cs`, `tests/SqlLogExplorer.Tests/FixtureSetTests.cs`

Interfaces:
- Produces: `public byte[]? LogRecordBytes { get; init; }` on `LogRecord`, after `RowLogContents5`; cache column `LogRecordBytes BLOB`; `LogColumns.SelectList` ends with `CASE WHEN [Operation] = N'LOP_MODIFY_COLUMNS' THEN [Log Record] END` read at index 20; `FixtureSet` fills `LogRecordBytes` from the fixture's `LogRecord` field for `LOP_MODIFY_COLUMNS` records only, and leaves it null otherwise (the fixtures already carry `LogRecord` for every record).

- [ ] Step 1: Failing tests. `LogCacheTests`: a record with `LogRecordBytes` round-trips through `InsertBatchAsync` and `LogQuery`; `LogRecordComparer` compares the new field. `FixtureSetTests`: on every version's `modify` fixture, every `LOP_MODIFY_COLUMNS` record has a non-empty `LogRecordBytes` whose `u16` at offset 2 is 62 and every other record has null. `LogColumnsIntegrationTests` (2022): the by-name comparison covers `[Log Record]` for `LOP_MODIFY_COLUMNS` rows (the scenario must produce one: add `CREATE TABLE dbo.m2 (id int NOT NULL PRIMARY KEY, a int NULL, x char(50) NULL, b int NULL)`, one row, `UPDATE dbo.m2 SET a = 11, b = 21`, which logs `LOP_MODIFY_COLUMNS` on the four versions) and asserts null for the other operations. Run them: they must fail to compile; say so.
- [ ] Step 2: Implement in the files listed.
- [ ] Step 3: Run `LogCacheTests`, `FixtureSetTests` (unit, exact counts), then the integration test with the server in ONE invocation: `eval "$(bash tests/fixtures/sqlserver.sh up 2022 sle-it-t8a)" && SQLLOGEXPLORER_RUN_INTEGRATION=1 dotnet test tests/SqlLogExplorer.Tests --filter "FullyQualifiedName~LogColumnsIntegrationTests"; bash tests/fixtures/sqlserver.sh down sle-it-t8a` (0 skipped). Then check `fn_dump_dblog` on the same server: back up the log of the test database to a file, read it with `FnDumpDblogQuery`, and assert that its `LOP_MODIFY_COLUMNS` records carry `LogRecordBytes` equal to the live ones (add this to the integration test if `FnDumpDblogQuery` can be driven from it, otherwise report the manual check with its output).
- [ ] Step 4: Break and confirm: drop the `CASE` (select `[Log Record]` for every row): the null assertion must fail; leave `LogRecordBytes` out of the cache insert: the round-trip test must fail.
- [ ] Step 5: Commit: `feat: keep the whole log record of LOP_MODIFY_COLUMNS`.

---

### Task 8: Modify decoder

Spec §6.1 and §6.2 (version 6). `LOP_MODIFY_COLUMNS` follows rule A of `docs/research/rowlog-layouts.md`, read from `LogRecord.LogRecordBytes` (Task 8a). Rule B of the note is not implemented.

Files:
- Create: `Decoding/ModifyDecoder.cs`
- Test: `tests/SqlLogExplorer.Tests/Decoding/ModifyDecoderTests.cs`

Interfaces:
- Consumes: `LogRecord` (Task 1). (`Decoding/` may reference `Models/`: `LogRecord` is a plain record.)
- Produces:

```csharp
/// <summary>Remplacer ForwardLength octets à Offset par After (en avant) ; After.Length octets par Before (en arrière).</summary>
public sealed record Fragment(int Offset, byte[] Before, byte[] After, int ForwardLength);
public static class ModifyDecoder
{
    /// <summary>null = disposition inconnue (spec03 §6.2) ; liste vide jamais renvoyée pour un enregistrement de modification.</summary>
    public static IReadOnlyList<Fragment>? Decode(LogRecord record);
    public static byte[] ApplyForward(byte[] row, Fragment f);    // throws ReplayCheckException when Before is not at Offset
    public static byte[] ApplyBackward(byte[] row, Fragment f);   // throws ReplayCheckException when After is not at Offset
}
public sealed class ReplayCheckException(string message) : Exception(message);
```

- [ ] Step 1: Write the failing tests

From spec §2: `a` 10 to 11 (`Offset 8, Size 1, 0x0A -> 0x0B`) applied forward to the spec row gives the row with `0x0B` at 8, and backward gives the original; `b` `abc` to `abcdef` (`Offset 18, Size 5, 0x1700616263 -> 0x1A00616263646566`) grows the row by 3 bytes forward and shrinks it back; a compensation `LOP_MODIFY_ROW` (`Description = "COMPENSATION"`, `Offset 18`, `ModifySize 8`, `Rlc0 = 0x`, `Rlc1 = 0x1700616263`) applied forward to the `abcdef` row gives the `abc` row, unchecked; applying backward a compensation fragment throws `ReplayCheckException`; a forward fragment whose before bytes are not in the row throws `ReplayCheckException`; the `dbo.u` record of spec §2 (`a = 99, b = 'zz'`) decodes, on the four versions, into the fragments `8:1>1` and `68:5>4` of the note; a fixture theory over the four versions replays every row of the `modify` and `versioned` scenarios (and `versioned_adr` on 2019+) from its insert image through its modify records in LSN order, applying every non-compensation fragment forward, every fragment's before check passes on the way, and the result equals the row's delete image when there is one (rows are tracked by page and slot from their insert, rolled-back transactions replayed with their compensation records, as the note's check does; the theory must cover at least one `LOP_MODIFY_COLUMNS` record per version, and states how many rows it compared to a delete image); undoing each record's fragments in reverse order gives back the row before; a `LOP_MODIFY_COLUMNS` with null `LogRecordBytes`, or whose element lengths run past its end, gives `null`.

- [ ] Step 2: Run to verify they fail. `dotnet test tests/SqlLogExplorer.Tests --filter "FullyQualifiedName~ModifyDecoderTests"`: build failure.

- [ ] Step 3: Implement

`LOP_MODIFY_ROW`: one fragment, `Offset = record.OffsetInRow ?? 0`, `Before = record.RowLogContents0 ?? []`, `After = record.RowLogContents1 ?? []`, `ForwardLength = record.IsCompensation ? record.ModifySize ?? 0 : Before.Length` (the `LogRecord` members are nullable, `Fragment`'s are not). Compensation fragments are decoded for the Bytes view and for `ApplyForward`'s own tests; the replay never applies them (spec §7.3 drops rolled-back transactions whole and stops at partial rollbacks). `ApplyForward` checks `row[Offset..Offset+Before.Length] == Before` unless `Before` is empty and the record was a compensation (an empty `Before` with `ForwardLength > 0` is a compensation: no check), then replaces `ForwardLength` bytes with `After`. `ApplyBackward` refuses a fragment with an empty `Before` and `ForwardLength > 0` (compensation: before bytes not logged), checks `After` at `Offset`, and replaces `After.Length` bytes with `Before`. `LOP_MODIFY_COLUMNS`: rule A of the note on `LogRecordBytes`: header length `u16` at offset 2, element count and lengths, elements on 4-byte boundaries, fragment i = `(Offset = running offset oa_i, Before = element 4 + 2i, After = element 5 + 2i, ForwardLength = lb_i)`. `null` when `LogRecordBytes` is null, when the count is not `Num Elements` or not 4 + 2n with n = len(element 0) / 4 = len(element 1) / 2, or when an element runs past the end. A compensation `LOP_MODIFY_COLUMNS` gives fragments with empty `Before`, like a compensation `LOP_MODIFY_ROW`.

- [ ] Step 4: Run to verify they pass, stating the count.
- [ ] Step 5: Break and confirm: remove the before check (the mismatch test must fail); use `Before.Length` for compensations (the compensation test must fail); for `LOP_MODIFY_COLUMNS`, swap before and after in one fragment (the fixture theory must fail).
- [ ] Step 6: Commit: `feat: modify records as applicable fragments`.

---

### Task 9: Replay engine

Spec §7.3, §7.4, and the anchor order of §7.2 (anchors 1 and 2 here, anchor 3 supplies an image in Task 12). Pure: it receives the chain and the transaction outcomes, it reads nothing.

Files:
- Create: `Decoding/RowReplayer.cs`
- Test: `tests/SqlLogExplorer.Tests/Decoding/RowReplayerTests.cs`

Interfaces:
- Consumes: `LogRecord`, `ModifyDecoder`, `Fragment`, `ReplayCheckException`.
- Produces:

```csharp
public enum TxOutcome { Committed, RolledBack, PartlyRolledBack, Unknown }
public enum AnchorKind { None, Insert, Delete, CurrentRow }
public sealed record ReplayResult(byte[]? Before, byte[]? After, AnchorKind Anchor, string? AnchorLsn, string Status, bool IncludesUnknownOutcome);
public static class RowReplayer
{
    /// <summary>chain : les enregistrements de données de cette ligne (insert, delete, modify), dans l'ordre des LSN ;
    /// les compensations n'y sont pas (elles n'ont pas de clé, spec03 §7.1). outcomes : par Transaction ID ;
    /// une transaction absente du dictionnaire, ou un TransactionId null, vaut Unknown.
    /// currentRow : image de l'ancre 3 (après le dernier enregistrement de chain), ou null.</summary>
    public static ReplayResult Replay(IReadOnlyList<LogRecord> chain, string selectedLsn,
        IReadOnlyDictionary<string, TxOutcome> outcomes, IReadOnlySet<string> adrReverts, byte[]? currentRow = null);

    /// <summary>Classe les transactions d'après leurs enregistrements (LOP_COMMIT_XACT, LOP_ABORT_XACT, compensations, LOP_CTR_NEST_ABORT).</summary>
    public static IReadOnlyDictionary<string, TxOutcome> Classify(IEnumerable<LogRecord> transactionRecords);

    /// <summary>Transactions partiellement annulées par une révocation ADR : LOP_CTR_NEST_ABORT sans aucun enregistrement de compensation (spec03 §7.3).</summary>
    public static IReadOnlySet<string> AdrReverts(IEnumerable<LogRecord> transactionRecords);
}
```

Status strings, exact (the UI shows them): `"rebuilt forwards from INSERT at LSN {lsn}"`, `"rebuilt backwards from DELETE at LSN {lsn}"`, `"rebuilt backwards from the current row"`, `"chain broken at LSN {lsn}"`, `"partial rollback at LSN {lsn}, not replayed"`, `"modification not decodable at LSN {lsn}"`, `"rollback in the chain, rebuild forwards only"`, `"row forwarded, not followed"`, `"no full image of this row available"`.

- [ ] Step 1: Write the failing tests

Build chains by hand from the spec §2 bytes, and from the `modify` fixture per version. Hand-built cases:
1. insert, modify `a` 10 to 11, modify `b` to `abcdef`; selected = second modify: `Before` holds `a = 11, b = 'abc'`, `After` holds `b = 'abcdef'`, anchor `Insert`;
2. same chain plus a delete whose image is the final row, with the insert removed: anchor `Delete`, same `Before` and `After`;
3. a modify of a transaction classified `RolledBack`, between two committed modifies: it is skipped forwards and backwards, and the result equals the chain without it;
4. selected = the rolled-back modify itself: `After` shows its change, status starts with `"rebuilt forwards"`;
5. a modify of a `PartlyRolledBack` transaction before the selected record: status `"partial rollback at LSN …, not replayed"`;
6. a modify whose before bytes do not match: `"chain broken at LSN …"`;
7. a record of a transaction in `adrReverts` placed AFTER the selected record, with a delete anchor after it: status `"partial rollback at LSN …, not replayed"` (spec §7.3: an ADR revert anywhere in the chain stops every replay of the row); the same chain with that transaction `PartlyRolledBack` but not in `adrReverts`: backward replay from the delete succeeds, since it never crosses the record;
8. an `Unknown` outcome modify in the chain: `IncludesUnknownOutcome` true;
9. no insert, no delete, no current row: `"no full image of this row available"`;
10. `currentRow` given and no delete: anchor `CurrentRow`;
11. a heap modify whose after bytes start with a forwarding-stub status byte (record type 2, `0x04`): `"row forwarded, not followed"`;
12. `Classify`: a transaction with `LOP_ABORT_XACT` is `RolledBack`; with `LOP_COMMIT_XACT` and a compensation record is `PartlyRolledBack`; with `LOP_COMMIT_XACT` and `LOP_CTR_NEST_ABORT` is `PartlyRolledBack`; with only `LOP_COMMIT_XACT` is `Committed`; with neither is `Unknown`. `AdrReverts` holds the transaction with `LOP_CTR_NEST_ABORT` and no compensation, not the one with both;
13. a record whose `TransactionId` is null or absent from `outcomes` is treated as `Unknown` (no exception).

Fixture theories over the four versions on `modify` (`dbo.t`, no ADR): for each `LOP_MODIFY_ROW` the scenario applied to row 1 before the savepoint transaction, the replayed `After` decodes, with `RowDecoder`, to the values the scenario set at that statement (write them in the test from `modify.sql`); the `TABLOCKX` update is in the chain; the savepoint transaction is `PartlyRolledBack`, not in `AdrReverts` (it has compensation records), and its records report `"partial rollback at LSN …, not replayed"`. On `versioned_adr` (2019 and later): the savepoint transaction is in `AdrReverts`, and every modify of `dbo.vr` id 1 reports `"partial rollback at LSN …, not replayed"`, including the ones before it.

- [ ] Step 2: Run to verify they fail. Build failure.
- [ ] Step 3: Implement

Follow spec §7.3 literally: if any record of the chain belongs to a transaction in `adrReverts`, stop at once with `"partial rollback at LSN {that record's LSN}, not replayed"`. Drop records of `RolledBack` transactions other than the selected record's; when the selected record's transaction is `RolledBack`, keep its records up to the selected one. Forward: the anchor is the last `LOP_INSERT_ROWS` before the selected record with no `LOP_DELETE_ROWS` between them; apply each following record up to the selected one with `ModifyDecoder.ApplyForward`, stopping on a `PartlyRolledBack` record, a `null` decode, or a `ReplayCheckException`. Backward: the anchor is the first `LOP_DELETE_ROWS` after the selected record with no insert between them, else `currentRow`; undo each record from the anchor back to the selected one with `ApplyBackward`, stopping the same way and on a compensation fragment. A row image whose first byte has record type 2 ends the chain with `"row forwarded, not followed"`. Try forward first, then backward; return the first success, else the most informative failure (a broken chain over "no full image").

- [ ] Step 4: Run to verify they pass, stating the count.
- [ ] Step 5: Break and confirm: keep rolled-back records in the chain (case 3 must fail); treat `PartlyRolledBack` as `Committed` (case 5 and the savepoint fixture must fail); ignore `adrReverts` (case 7 and the `versioned_adr` theory must fail); ignore the selected record's own transaction rule (case 4 must fail); choose the first insert instead of the last (write the test that catches it first if none does, and say so).
- [ ] Step 6: Commit: `feat: replay a row's chain forwards and backwards`.

---

### Task 10: Decoding service, cache side

Spec §7.1 (filling `RowKey`), §7.2 anchors 1 and 2, §6.1 attribution. Orchestrates one inspection over the cache. No UI.

Files:
- Create: `Services/DecodingService.cs`, `Services/Inspection.cs`
- Modify: `Data/LogQuery.cs`
- Test: `tests/SqlLogExplorer.Tests/DecodingServiceTests.cs`

Interfaces:
- Consumes: `LogCache.SetRowKeysAsync`, `LogQuery` (Task 1), `TableSchema` (Task 2), `RowDecoder`, `KeyDecoder`, `ModifyDecoder`, `RowReplayer`.
- Produces:

```csharp
public sealed record ColumnChange(string Column, object? Before, object? After, bool Changed);
public sealed record Inspection(
    LogRecord Record,
    DecodedRow? Row,                      // insert / delete image
    DecodedRow? Before, DecodedRow? After, // modify, when replay succeeded
    IReadOnlyList<ColumnChange> Changes,   // modify: from Before/After, else from the fragment alone (§6.1)
    string? ReplayStatus, TxOutcome? Outcome, bool IncludesUnknownOutcome,
    string? SchemaMessage,                 // "a schema connection is required", "allocation unit not found in the connected database"
    string? Label,                         // "row moved by the engine" (spec §5), else null
    string? Warning,                       // "may hold values undone by a partial rollback" (spec §7.3), "key not decodable" (spec §7.1)
    bool CanRebuildFromCurrentRow);
public sealed class DecodingService
{
    public DecodingService(LogCache cache, Func<long, CancellationToken, Task<TableSchema?>>? schemaLookup, bool isLive);
    public Task<Inspection> InspectAsync(LogRecord record, CancellationToken ct = default);
}
```

Data records are those whose `Context` is `LCX_HEAP`, `LCX_CLUSTERED` or `LCX_MARK_AS_GHOST` (the log also holds `LCX_PFS` records carrying the table's `AllocUnitId`; they must never be keyed nor enter a chain). System transaction names that move rows: `SplitPage` plus those listed in `docs/research/rowlog-layouts.md` (Task 4).

`LogQuery` gains: `Task<IReadOnlyList<LogRecord>> GetUnkeyedDataRecordsAsync(long allocUnitId, CancellationToken)` (data records only), `Task<IReadOnlyList<LogRecord>> GetChainAsync(long allocUnitId, string rowKey, CancellationToken)`, `Task<IReadOnlyList<LogRecord>> GetHeapChainAsync(long allocUnitId, string pageId, int slotId, CancellationToken)` (all ordered by LSN), and `Task<IReadOnlyList<LogRecord>> GetTransactionRecordsAsync(IReadOnlyCollection<string> transactionIds, CancellationToken)` (the begin, commit, abort, nest-abort and compensation records of those transactions). Chains contain data records only.

- [ ] Step 1: Write the failing tests

Load a fixture into a temporary `LogCache` (`FixtureSet.Load(v, "modify").Log` through `InsertBatchAsync`), with `schemaLookup` serving the fixture's schemas. Assert, per version:
- inspecting each `dbo.t` modify before the savepoint transaction gives `After` values as in `modify.sql` and `ReplayStatus` starting with `"rebuilt"`; inspecting a record of the savepoint transaction gives `"partial rollback at LSN …, not replayed"`; and the first inspection fills `RowKey` for every `dbo.t` data record (contexts above) except compensations, and for no `LCX_PFS` record;
- inspecting the `dbo.s` update of id 1 (after `id = 0` shifted slots) rebuilds `id = 1, a = 11`, never `id = 2`;
- inspecting a `dbo.t` modify with `schemaLookup` null gives `Row`/`Before`/`After` null, `SchemaMessage == "a schema connection is required"`, and no exception;
- with a lookup that returns null for the allocation unit: `SchemaMessage == "allocation unit not found in the connected database"` and the record's `RowLogContents0` still decoded without a schema in `Row` (insert) or nothing for a modify;
- `CanRebuildFromCurrentRow` is true only when `isLive`, the table is clustered (`IndexId == 1`), and anchors 1 and 2 failed;
- inspecting the `dbo.t` modify of the rolled-back transaction gives `Outcome == RolledBack`;
- importing the `modify` fixture twice (two `InsertBatchAsync` calls) changes no result.

And `schema_change`: the `dbo.rb` update after the rebuild has its own allocation unit and its chain does not include the insert before the rebuild (status `"no full image of this row available"`).

And `keys`: inspecting the delete of `dbo.ix` on the nonclustered index, with its schema available (`IndexId == 2`), yields `Row` with no column (spec §5 admission rule: context and `IsDataRows` are checked before decoding, record type alone is not enough).

And `heap`: inspecting the update of `id = 1` rebuilds `id = 1, v = 11` from its insert, and the insert of `id = 2` at the same address does not enter that chain; the update of `id = 101` whose after bytes are a forwarding stub reports `"row forwarded, not followed"` (if the fixture shows no forwarding on a version, assert what it shows and say so).

And `split`: the update of `dbo.sp` `id = 4` after the page split is rebuilt (the key does not change on a split), and the split's own `LOP_INSERT_ROWS` carries `Label == "row moved by the engine"` when its `LOP_BEGIN_XACT` named `SplitPage` is in the fixture.

And `versioned_adr` (2019 and later): the delete of `dbo.vr` id 1 carries `Warning == "may hold values undone by a partial rollback"`.

- [ ] Step 2: Run to verify they fail. Build failure.
- [ ] Step 3: Implement. Admission (spec §5): an insert or a delete is decoded as a data row only when its context is a data context and, when a schema is available, `schema.IsDataRows`; otherwise `Row = RowDecoder.Decode(Rlc0, null)` restricted to raw bytes (no columns). For a decoded insert or delete: `Row = RowDecoder.Decode(Rlc0, schema)`, `Label` from the transaction name, `Warning` when the row's chain holds a record of an ADR revert. For a modify: key the allocation unit before every chain lookup (`GetUnkeyedDataRecordsAsync`, `KeyDecoder.FromRow` for inserts and deletes, `KeyDecoder.FromIndexRecord(Rlc2)` for non-compensation modifies, then `SetRowKeysAsync`; heaps use `GetHeapChainAsync` and no key); a selected modify whose own key is `null` gets `Warning == "key not decodable"` and no replay. Read the chain (data records only, no compensation: spec §7.3 handles rollbacks by transaction), read the transaction records of every `TransactionId` in the chain, `Classify` and `AdrReverts` them, replay, decode `Before` and `After`, and compute `Changes` by comparing the two `DecodedRow.Columns` by name, values compared through `Convert.ToString(value, CultureInfo.InvariantCulture)` so that a default read from the catalog as the text `"7"` equals the integer 7 of a later row. Without replay, attribute the fragment from the schema alone (spec §6.1): a fragment entirely inside one fixed column or one bit byte names the column, and gives values when it covers the whole column.
- [ ] Step 4: Run to verify they pass, stating the count.
- [ ] Step 5: Break and confirm: key the chain on `(PageId, SlotId)` for a clustered table (the `dbo.s` test must fail); do not fill keys of records added after the first keying (write a test that inserts more records after a first inspection, then inspects again; it must fail under the break); drop the context filter of `GetUnkeyedDataRecordsAsync` (the `LCX_PFS` assertion must fail); drop the `IsDataRows` admission check (the `dbo.ix` test must fail).
- [ ] Step 6: Commit: `feat: inspect a log record with its rebuilt row`.

---

### Task 11: Decoding service, Review Focus cases

Pins the Review Focus lines 1, 2 and 5 at the service level.

Files:
- Modify: `tests/SqlLogExplorer.Tests/DecodingServiceTests.cs`

- [ ] Step 1: Tests
  - a record whose `AllocUnitId` is absent from the lookup, with a 3-byte `RowLogContents0`: `InspectAsync` returns, `Row.HasProblem` is true;
  - the `modify` fixture of 2017 and the one of 2022 imported in reverse order into one cache (different databases, so the test only checks ordering and absence of exception: `LogQuery.GetPageAsync` returns them in LSN order and every inspection returns);
  - the same fixture imported twice: identical inspections.
- [ ] Step 2: Run, fix what fails in `DecodingService` (not in the test), break the fix once to see the test fail, commit: `test: inspection survives unknown units, duplicates and mixed case`.

---

### Task 12: Current-row reader (anchor 3)

Spec §7.2, live mode, clustered tables, no ADR, modify records only (an insert or a delete is its own full image).

Files:
- Create: `Backends/CurrentRowReader.cs`
- Modify: `Decoding/KeyDecoder.cs` (add `ValuesFromIndexRecord`), `Services/DecodingService.cs` (add `RebuildFromCurrentRowAsync`)
- Test: `tests/SqlLogExplorer.Tests/CurrentRowParsingTests.cs`, `tests/SqlLogExplorer.Tests/Integration/CurrentRowReaderIntegrationTests.cs`

Interfaces:
- Consumes: `TableSchema`, `KeyDecoder`, `TypeDecoder`, `Lsn`, `LogColumns`, `DecodingService` (Task 10), `LogCache.InsertBatchAsync`.
- Produces:

```csharp
public sealed record CurrentRowResult(byte[]? Row, string? Unavailable);
public sealed class CurrentRowReader
{
    public CurrentRowReader(string connectionString);   // chaîne vers la base analysée
    public Task<bool> IsAvailableForDatabaseAsync(CancellationToken ct = default);   // false when ADR is on
    /// <summary>acceptSuffix reçoit les enregistrements lus depuis le dernier LSN du cache, pendant que la ligne est tenue ;
    /// il les ajoute au cache, les indexe, et rend false si la ligne a été supprimée après l'enregistrement choisi.</summary>
    public Task<CurrentRowResult> ReadAsync(TableSchema schema, IReadOnlyList<(SchemaColumn Column, object? Value)> key,
        string lastCachedLsn, Func<IReadOnlyList<LogRecord>, CancellationToken, Task<bool>> acceptSuffix, CancellationToken ct = default);
    internal static byte[]? ParseSlotDump(IEnumerable<(string ParentObject, string Value)> lines, int slot);
}
// KeyDecoder
public static IReadOnlyList<(SchemaColumn Column, object? Value)>? ValuesFromIndexRecord(byte[] record, TableSchema schema);
// DecodingService
public Task<Inspection> RebuildFromCurrentRowAsync(LogRecord record, CurrentRowReader reader, CancellationToken ct = default);
```

`ReadAsync` runs on one connection, inside one transaction at `SERIALIZABLE` isolation, after `SET LOCK_TIMEOUT 5000`, in this order (spec §7.2):
1. seek: `SELECT sys.fn_PhysLocFormatter(%%physloc%%) FROM [schema].[table] WITH (ROWLOCK, HOLDLOCK) WHERE ...`, with `SchemaName` and `TableName` quoted separately (`"[" + name.Replace("]", "]]") + "]"`), one parameter per key column, `[col] IS NULL` for a null key value; a lock timeout (error 1222) returns `"row locked by an open transaction"`, no row returns `"current row is not this row"`;
2. suffix: `SELECT <LogColumns.SelectList> FROM sys.fn_dblog(@start, NULL)` with `@start = Lsn.ToFnDblogArgument(lastCachedLsn)`; the first record's `Lsn` (normalized, `LogColumns.Read` does it) must equal `Lsn.Normalize(lastCachedLsn)`, else `"log truncated since the import, re-import to use the current row"`;
3. `acceptSuffix(records)`; `false` returns `"row deleted since: the DELETE anchor applies"`;
4. `DBCC PAGE (DB_NAME(), <file>, <page>, 1) WITH TABLERESULTS` for each address the seek returned;
5. commit; keep the slot whose bytes are a primary record (type 0) and whose `KeyDecoder.FromRow` equals the row's key, else `"current row is not this row"`.

`DBCC PAGE (…, 1) WITH TABLERESULTS` returns four columns, `ParentObject`, `Object`, `Field`, `VALUE`. A slot's memory dump lines have `ParentObject` = `Slot 0, Offset 0x60, Length 22, DumpStyle BYTE` and `VALUE` = `0000000000000000:   30000c00 07000000 02000000 03000001 00160061  0..................a` (measured on 2022): an offset, a colon, three spaces, a 44-character hex area of up to five 4-byte groups, two spaces, then an ASCII rendering that can itself look like hex (`bc`). `ParseSlotDump` takes the 44 characters after `":   "`, removes the spaces, concatenates the lines of the slot in order, and truncates to the `Length` of `ParentObject`. Never parse the ASCII part.

`RebuildFromCurrentRowAsync` checks `IsAvailableForDatabaseAsync` (`"not available on a database with Accelerated Database Recovery"`), the heap case (`"not available for a heap"`), decodes the key values from the selected record's `RowLogContents2`, passes an `acceptSuffix` that inserts the records into the cache, keys them, and returns `false` when the chain now holds a delete of the row after the selected record (the service then inspects again and anchor 2 applies), and finally replays with `currentRow`.

- [ ] Step 1: Write the failing tests

`CurrentRowParsingTests`: `ParseSlotDump` on the `Style == 1` lines of the `modify` fixture of each version returns, for the slot holding `dbo.u` id 2, bytes that `RowDecoder` decodes to `id = 2, a = 100, b = 'zz'`; and on the two literal lines above (slot 0, length 22) returns `30000c00070000000200000003000001001600616263` as bytes.

`CurrentRowReaderIntegrationTests` (one 2022 container from `sqlserver.sh`, a fresh database per test, `FULL` recovery after a full backup):
- an update made after the import is crossed: import, `UPDATE` from another connection, rebuild from the current row, the `Before` of the selected record is right;
- order of the steps: an `acceptSuffix` callback that tries, from a second connection with `SET LOCK_TIMEOUT 1000`, to update the same row must get error 1222 (the row is held since step 1);
- a session holding an open `UPDATE` on the row makes `ReadAsync` return `"row locked by an open transaction"` within ten seconds;
- truncation: after the import, generate log until truncation really happens (loop: insert 1000 rows into a filler table, `BACKUP LOG` to a file, `CHECKPOINT`, until `SELECT MIN([Current LSN]) FROM sys.fn_dblog(NULL, NULL)`, normalized, sorts after the cache's last LSN; at most 20 rounds, and the test fails if truncation never happens), then `ReadAsync` returns the truncation message;
- an ADR database: `IsAvailableForDatabaseAsync` is false;
- a nullable key holding `NULL` is found (`IS NULL` predicate);
- a non-unique clustered key picks the slot whose uniqueifier matches;
- a table in a schema other than `dbo` (`CREATE SCHEMA s; CREATE TABLE s.t ...`) is found.

Run them before implementing: `CurrentRowParsingTests` must fail to compile (`ParseSlotDump` missing); say so.

- [ ] Step 2: Implement `ValuesFromIndexRecord` (the index-record walk of Task 7, returning typed values through `TypeDecoder`), `CurrentRowReader`, and `RebuildFromCurrentRowAsync`.

- [ ] Step 3: Run, setup and tests in ONE invocation:

```bash
eval "$(bash tests/fixtures/sqlserver.sh up 2022 sle-it-t12)" && dotnet test tests/SqlLogExplorer.Tests --filter "FullyQualifiedName~CurrentRowParsingTests|FullyQualifiedName~CurrentRowReaderIntegrationTests"; bash tests/fixtures/sqlserver.sh down sle-it-t12
```

Expected: 0 skipped; state the count.

- [ ] Step 4: Break and confirm, printing each mutated line: run the seek after the suffix read (the order test must fail); skip the first-record check (the truncation test must fail); drop `IS NULL` (the nullable key test must fail); quote `ObjectName` as one name (the schema test must fail).

- [ ] Step 5: Commit: `feat: rebuild a row backwards from its current bytes`.

---

### Task 13: Records tab and inspector

Spec §9. Also adds the offline schema connection (spec01 §5.1, which the UI never got).

Files:
- Create: `ViewModels/RecordsViewModel.cs`, `ViewModels/InspectorViewModel.cs`
- Modify: `ViewModels/MainWindowViewModel.cs`, `Views/MainWindow.axaml`, `Views/MainWindow.axaml.cs`
- Test: `tests/SqlLogExplorer.Tests/RecordsViewModelTests.cs`, `tests/SqlLogExplorer.Tests/InspectorViewModelTests.cs`

Interfaces:
- `RecordsViewModel(LogQuery query, int pageSize = 200)`: `ObservableCollection<LogRecord> Records`, `LogRecord? SelectedRecord`, `int PageIndex`, commands `NextPage`, `PreviousPage`, `Task LoadAsync()`.
- `InspectorViewModel(DecodingService service, CurrentRowReader? reader)`: `Task InspectAsync(LogRecord)`, properties `string Title`, `ObservableCollection<ColumnChange> Values`, `ObservableCollection<ByteChunk> Bytes` (`sealed record ByteChunk(string ContentName, int Offset, string Hex, FieldRole? Role, string Tooltip)`), `string? ReplayStatus`, `string? SchemaMessage`, `bool CanRebuildFromCurrentRow`, command `RebuildFromCurrentRow`.
- `MainWindowViewModel` keeps the live connection string given to `LoadLiveAsync` (it does not today), and creates both view-models after an import: live, with a `SchemaProvider` and a `CurrentRowReader` on that connection string; offline, with no schema lookup. Offline it shows a "Connect schema source…" button opening the existing `ConnectionDialog`; `Task ConnectSchemaSourceAsync(string connectionString)` creates a `SchemaProvider`, shows a warning when `IsPrivilegedAsync` is false, and replaces the `DecodingService` and the `InspectorViewModel` (a `DecodingService` is immutable: its lookup is set at construction).
- `CanRebuildFromCurrentRow` is true only for a modify record, live, on a clustered table, when anchors 1 and 2 failed and `CurrentRowReader.IsAvailableForDatabaseAsync` is true.

- [ ] Step 1: View-model tests (they fail to compile before Step 2; say so) with a cache loaded from the `modify` fixture of 2022: `LoadAsync` fills 200 records max in LSN order; `NextPage` moves on; selecting the first `dbo.t` modify fills `Values` with `a` changed and `ReplayStatus` starting with `"rebuilt"`; `Bytes` has one chunk per `DecodedField` of each non-empty contents, plus plain hex chunks (`Role == null`) for contents not decoded; without a schema, `SchemaMessage == "a schema connection is required"`; after `ConnectSchemaSourceAsync` with a lookup serving the fixture's schemas (inject it through an internal constructor taking the lookup delegate), the same record decodes; `CanRebuildFromCurrentRow` is false offline.
- [ ] Step 2: Implement the view-models, then the view: a `TabItem Header="Records"` before the statistics tabs, holding a `DataGrid` (`LSN`, `Operation`, `Context`, `AllocUnitName`, `TransactionId`) bound to `Records.Records` and `Records.SelectedRecord`, previous/next buttons, and below it a `TabControl` with "Values" (`DataGrid` of `Column`, `Before`, `After`, changed rows highlighted) and "Bytes" (`ItemsControl` with a `WrapPanel`, each chunk a `Border` whose background comes from the role through a converter, `ToolTip.Tip` bound to `Tooltip`), the replay status line and the "Rebuild from the current row" button bound to `CanRebuildFromCurrentRow`.
- [ ] Step 3: Run the app against a 2022 container (`dotnet run`), import live, select a modify of a table you created, and take a screenshot of each tab; attach the paths in your report. If the session cannot show a window, say so; do not claim the UI works from the tests alone.
- [ ] Step 4: Full suite, then commit: `feat: records grid with a decoding inspector`.

---

## Self-review notes

- Spec coverage: §3 Task 1; §4 Tasks 2, 5 to 10, 12, 13; §5 Task 6; §6.1 Tasks 8, 10; §6.2 Tasks 4, 8; §7.1 Tasks 7, 10; §7.2 Tasks 9, 10, 12; §7.3 and §7.4 Task 9; §8 Task 6, 10; §9 Task 13; §10 Task 3 and every test step; §1.3 Tasks 2, 12.
- Out of scope per spec §1.2 and not planned: compression, LOB following, nonclustered decoding, forwarded rows, In-Memory OLTP.
