# Index Maintenance and Log Volume Implementation Plan

> For agentic workers: REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Goal: Import the size, session and parent of each log record, group the transactions of each index rebuild and reorganize into operations, and show them with their log volume in a new "Index maintenance" tab (spec04).

Architecture: Three columns join the import (`LogColumns`, `LogRecord`, `LogCache`, `LogQuery`). `LogQuery` gives one fact per transaction; a pure static class `Services/IndexMaintenance.cs` applies rules 1 to 3 of spec04 §4 to those facts, then adds the records outside any transaction by LSN range; `StatisticsViewModel` exposes the result to a new tab.

Tech Stack: .NET 10, C# (nullable on), Avalonia 12 + CommunityToolkit.Mvvm, Microsoft.Data.SqlClient 7, Microsoft.Data.Sqlite 10, xUnit 2.9, podman with `mcr.microsoft.com/mssql/server:{2017,2019,2022,2025}-latest`.

Spec: `specs/spec04.md` (version 2). Read §2 to §7 before your task; section numbers below refer to it.

## Global Constraints

- The main project has no `ImplicitUsings`: every file in it lists its `using` directives. The test project has `ImplicitUsings` on.
- File-scoped namespaces; code comments in French, like the existing code; every user-facing string in English.
- No new NuGet package.
- LSNs in the cache are uppercase and fixed width (`Lsn.Normalize`): compare them with `string.CompareOrdinal`, never with the default comparer.
- The new columns are appended at the END of `LogColumns.SelectList` and of `LogQuery.SelectRecords`: both readers are positional.
- The reorganize names are exactly `ReorderPages, RebalanceSpaces, MoveBlob, SwapPage, MoveRowsLeft, AllocUnlinkedReorgPage, DeallocUnlinkedReorgPage` (§4 rule 2). The build names are `offline index build` (offline) and `ONLINE_INDEX_DDL`, `OnlineIndexInsertTxn` (online). Names compare ordinally, case included.
- The pseudo-transaction of records outside any transaction is `0000:00000000`.
- Integration tests use `[IntegrationFact]` and read `SQLLOGEXPLORER_TEST_SQL`, printed by `tests/fixtures/sqlserver.sh up <version> <name>`. Start the server and run the tests in ONE shell invocation (`eval "$(tests/fixtures/sqlserver.sh up 2022 sle-t)" && dotnet test ...; tests/fixtures/sqlserver.sh down sle-t`): shell state does not survive between tool calls, and a run without the variables skips every integration test and still prints success.
- Commit messages: a subject line, then prose explaining why. No `Co-Authored-By`, no `Generated with`, no attribution trailer of any kind. Stage files by explicit path.
- If what you measure contradicts this plan or the spec, stop and say so; do not bend the code to the text.

## Review Focus

1. An import where a reorganize family has no allocation unit at all (only page-allocation records without `AllocUnitId`): it must not cut the run. Test in Task 3 (`Reorganize_FamilyWithoutUnit_DoesNotCut`).
2. Two operations on two sessions running at the same time, whose spans overlap: the records outside transactions in the overlap are counted in neither, and the summary never exceeds 100 %. Test in Task 4 (`ExclusiveRanges_OverlapBelongsToNone`, `AnalyzeAsync_TotalCountsEachRecordOnce`).
3. An import with no index maintenance at all, or an empty cache: the tab shows an empty grid and "0 B (0 %)" with no division by zero. Test in Task 5 (`RefreshAsync_EmptyCache_ShowsZeroShare`).
4. A live import after a second rebuild of the same index, where `AllocUnitName` is `Unknown Alloc Unit`: the object is shown by its id. Test in Task 4 (`DisplayName_UnknownAllocUnit_ShowsId`).
5. A `user_transaction` that holds an ordinary `UPDATE` and no index work: not listed. Test in Task 3 (`UserTransaction_WithoutIndexWork_NotListed`).

---

## File structure

| Path | Responsibility | Task |
|---|---|---|
| `Models/LogRecord.cs` | three new properties | 1 |
| `Backends/LogColumns.cs` | three new columns read | 1 |
| `Data/LogCache.cs` | three new cache columns | 1 |
| `Data/LogQuery.cs` | new columns read; transaction facts, totals, outside sums | 1, 2 |
| `Models/IndexMaintenance.cs` | `TransactionFacts`, `UnitBytes`, `MaintenanceKind`, `MaintenanceObject`, `MaintenanceOperation`, `MaintenanceReport` | 2, 3, 4 |
| `Services/IndexMaintenance.cs` | rules 1 to 3, objects, exclusive ranges, `AnalyzeAsync` | 3, 4 |
| `ViewModels/StatisticsViewModel.cs`, `ViewModels/IndexMaintenanceRow.cs`, `Views/MainWindow.axaml` | the tab | 5 |
| `tests/SqlLogExplorer.Tests/Integration/IndexMaintenanceIntegrationTests.cs` | the four-version test of §7 | 6 |
| `docs/user-guide.md`, `CHANGELOG.md` | documentation, §8 | 7 |

---

### Task 1: Import Log Record Length, SPID and Parent Transaction ID

Files:
- Modify: `Models/LogRecord.cs`, `Backends/LogColumns.cs`, `Data/LogCache.cs`, `Data/LogQuery.cs` (`SelectRecords`, `ReadRecord`)
- Test: `tests/SqlLogExplorer.Tests/LogCacheTests.cs`, `tests/SqlLogExplorer.Tests/Integration/LogColumnsIntegrationTests.cs`

Interfaces:
- Produces: `LogRecord.LogRecordLength` (`int?`), `LogRecord.Spid` (`int?`), `LogRecord.ParentTransactionId` (`string?`), init-only like the others; cache columns `LogRecordLength INTEGER`, `Spid INTEGER`, `ParentTransactionId TEXT`.

- [ ] Step 1: Write the failing test `InsertBatchAsync_RoundTripsLengthSpidAndParent` in `LogCacheTests`: insert one `LOP_BEGIN_XACT` with `LogRecordLength = 132, Spid = 73, ParentTransactionId = "0000:0000056b"` and one record with all three null; read back with `new LogQuery(cache.Connection).GetPageAsync(0, 10)`; assert the three values on the first and three nulls on the second.

- [ ] Step 2: Run `dotnet test tests/SqlLogExplorer.Tests --filter "FullyQualifiedName~LogCacheTests.InsertBatchAsync_RoundTripsLengthSpidAndParent"`. Expected: build error (properties do not exist).

- [ ] Step 3: Add the properties to `LogRecord`; append `[Log Record Length], [SPID], [Parent Transaction ID]` to `LogColumns.SelectList` and read them at indices 23, 24, 25 with the existing `Int` and `Text` helpers; add the three columns to the `CREATE TABLE`, the `INSERT` and its parameters in `LogCache`; append them to `LogQuery.SelectRecords` and `ReadRecord` (indices 23 to 25). Store `ParentTransactionId` as returned, not normalized (§3).

- [ ] Step 4: Run the test of step 2. Expected: 1 passed.

- [ ] Step 5: Extend `LogColumnsIntegrationTests`: add the three columns to `Plain`, to `ReadPlainAsync` (read by name) and to the per-field asserts of `AssertMatchesPlain`; add `Assert.Contains(records, r => r.LogRecordLength is > 0)`, `Assert.Contains(records, r => r.Operation == "LOP_BEGIN_XACT" && r.Spid is not null)`, `Assert.All(records.Where(r => r.Operation != "LOP_BEGIN_XACT"), r => Assert.Null(r.Spid))` and `Assert.Contains(records, r => r.ParentTransactionId is not null)`. In `FnDumpDblog_CarriesTheSameLogRecordBytesAsFnDblog`, read all live records into a dictionary by LSN and assert, for every dumped record whose LSN is in it, equal `LogRecordLength`, `Spid`, `ParentTransactionId`; assert at least one dumped `LOP_BEGIN_XACT` has a non-null `Spid`.

- [ ] Step 6: Run the integration tests on 2022 in one invocation: `eval "$(tests/fixtures/sqlserver.sh up 2022 sle-t1)" && dotnet test tests/SqlLogExplorer.Tests --filter "FullyQualifiedName~LogColumnsIntegrationTests"; tests/fixtures/sqlserver.sh down sle-t1`. Expected: 2 passed, 0 skipped. Any skipped test means the variables were not set: the run did not test anything. Then break it: swap indices 24 and 25 in `LogColumns.Read`, rerun, see `ParseLogAsync_ReadsReplayColumnsAndAcceptsLsnRange` fail; restore. Seeing it fail is the success of this step.

- [ ] Step 7: Run the whole suite: `dotnet test tests/SqlLogExplorer.Tests`. Expected: no failure.

- [ ] Step 8: Commit `Models/LogRecord.cs Backends/LogColumns.cs Data/LogCache.cs Data/LogQuery.cs tests/SqlLogExplorer.Tests/LogCacheTests.cs tests/SqlLogExplorer.Tests/Integration/LogColumnsIntegrationTests.cs` with subject `feat: import the length, session and parent of each log record`.

### Task 2: Transaction facts and totals from the cache

Files:
- Create: `Models/IndexMaintenance.cs`
- Modify: `Data/LogQuery.cs`
- Test: `tests/SqlLogExplorer.Tests/LogQueryTests.cs`

Interfaces:
- Consumes: the cache columns of Task 1.
- Produces, in `Models/IndexMaintenance.cs` (namespace `SqlLogExplorer.Models`):
  - `public sealed record UnitBytes(long AllocUnitId, string? AllocUnitName, long Bytes);`
  - `public sealed record TransactionFacts(string TransactionId, string? Name, int? Spid, string? ParentTransactionId, string FirstLsn, string LastLsn, string? BeginTime, string? EndTime, bool HasBegin, bool HasEnd, long Records, long Bytes, IReadOnlyList<UnitBytes> Units);`
- Produces, in `LogQuery`:
  - `Task<IReadOnlyList<TransactionFacts>> GetTransactionFactsAsync(CancellationToken ct = default)`: every `TransactionId` not null and not `0000:00000000`, ordered by `FirstLsn`. `Name`, `Spid`, `ParentTransactionId`, `BeginTime` from its `LOP_BEGIN_XACT`; `EndTime` from its commit or abort; `HasEnd` true when a `LOP_COMMIT_XACT` or `LOP_ABORT_XACT` is there; `Bytes` is `SUM(COALESCE(LogRecordLength, 0))`; `Units` one entry per non-null `AllocUnitId` of its records with their bytes, ordered by bytes descending, and the `MAX(AllocUnitName)` seen for it. Two SQL statements (one `GROUP BY TransactionId`, one `GROUP BY TransactionId, AllocUnitId`) joined in memory.
  - `Task<long> GetTotalLogBytesAsync(CancellationToken ct = default)`: `SUM(COALESCE(LogRecordLength, 0))` of the whole cache, 0 when empty.
  - `Task<(long Records, long Bytes)> SumOutsideTransactionsAsync(string fromLsn, string toLsn, CancellationToken ct = default)`: records of `TransactionId = '0000:00000000'` with `LSN BETWEEN` the two bounds, both included.

- [ ] Step 1: Write the failing tests in `LogQueryTests`, on a cache seeded with: transaction `0000:00000010` (begin `00000001:00000010:0001` named `ALTER INDEX`, SPID 60, length 100; a `LOP_FORMAT_PAGE` of 300 bytes on unit 7 named `dbo.T.IX_n`; a commit with `EndTime`), transaction `0000:00000011` nested (parent `0000:00000010`, no commit, 50 bytes on unit 9 with empty name), a record of `0000:00000000` of 40 bytes between them, and one of 20 bytes after.
  - `GetTransactionFactsAsync_OneFactPerTransaction`: 2 facts; the first has `Name == "ALTER INDEX"`, `Spid == 60`, `HasBegin`, `HasEnd`, `Bytes == 400 + commit length`, `Units[0] == new UnitBytes(7, "dbo.T.IX_n", 300)`; the second has `ParentTransactionId == "0000:00000010"`, `HasEnd == false`.
  - `GetTotalLogBytesAsync_SumsEveryRecord`: equals the sum of all seeded lengths; on an empty cache, 0.
  - `SumOutsideTransactionsAsync_BoundsIncluded`: the range from the first to the last LSN of transaction `0000:00000010` gives `(1, 40)`; a range ending exactly on the 20-byte record includes it.

- [ ] Step 2: Run `dotnet test tests/SqlLogExplorer.Tests --filter "FullyQualifiedName~LogQueryTests"`. Expected: build error.

- [ ] Step 3: Implement the records and the three methods. Take the gate like the other queries (`using var gate = await ConnectionGate.EnterAsync(_connection, ct);`).

- [ ] Step 4: Run the filter of step 2. Expected: all `LogQueryTests` pass, 3 more than before this task.

- [ ] Step 5: Commit `Models/IndexMaintenance.cs Data/LogQuery.cs tests/SqlLogExplorer.Tests/LogQueryTests.cs` with subject `feat: per-transaction facts for the index maintenance view`.

### Task 3: Grouping rules 1 to 3

Files:
- Create: `Services/IndexMaintenance.cs` (namespace `SqlLogExplorer.Services`, `public static class IndexMaintenance`)
- Modify: `Models/IndexMaintenance.cs`
- Test: `tests/SqlLogExplorer.Tests/IndexMaintenanceTests.cs`

Interfaces:
- Consumes: `TransactionFacts`, `UnitBytes` (Task 2).
- Produces:
  - `public enum MaintenanceKind { RebuildOffline, RebuildOnline, Reorganize }`
  - `public sealed record MaintenanceObject(long AllocUnitId, string Display, long Bytes);` (filled by Task 4; Task 3 leaves `Objects` empty)
  - `public sealed record MaintenanceOperation(MaintenanceKind Kind, int Spid, string FirstLsn, string LastLsn, string? Begin, string? End, IReadOnlyList<TransactionFacts> Transactions, long Records, long Bytes, bool Partial, bool InUserTransaction) { public IReadOnlyList<MaintenanceObject> Objects { get; init; } = []; public int OtherObjects { get; init; } }`
  - `public static IReadOnlyList<MaintenanceOperation> IndexMaintenance.Group(IReadOnlyList<TransactionFacts> transactions)`, operations ordered by `FirstLsn`. `Records` and `Bytes` are the sums over `Transactions` (all members, nested included); `FirstLsn`/`LastLsn` the min and max over them; `Begin` the `BeginTime` of the first top-level member, `End` the greatest `EndTime` of the top-level members.

The algorithm (spec §4), in this order:
1. Top-level ancestor: follow `ParentTransactionId` up through transactions that exist in the input AND have `HasBegin`; guard against a cycle with a visited set. A family is a top-level transaction with all transactions whose ancestor it is. A family whose top has no `HasBegin` (hence no SPID) enters no rule.
2. A family's allocation unit: the `AllocUnitId` with the most bytes summed over all its members' `Units`, or none.
3. Families per SPID, ordered by the top's `FirstLsn`.
4. Rule 1: for each top named `ALTER INDEX`, take every family of the same SPID whose top `FirstLsn` lies in [top `FirstLsn`, top `LastLsn`]. Kind online if any member of the taken families is named `ONLINE_INDEX_DDL` or `OnlineIndexInsertTxn`, else offline if one is named `offline index build`, else no operation and the families stay free. Partial when the `ALTER INDEX` has no `HasEnd`.
5. Rule 3: each free family whose top is named `user_transaction` and whose members include a build name or a reorganize name is one operation; kind online, then offline, then reorganize, in that precedence; `InUserTransaction = true`; partial when the top has no `HasEnd`.
6. Rule 2, per SPID, walking ALL its families in order (taken ones included, as breakers): a family is eligible when it is free and its top name is a reorganize name. A run starts at an eligible family and continues while the next family is eligible and its unit does not cut: it does not cut when it has no unit, when it equals the run's current unit, or when the previous family's top is `MoveBlob` and this one's is not; any other unit cuts, ending the run and starting a new one at this family. The run's current unit is the last non-null unit seen. Partial when the run's first family is the SPID's first family, or its last family is the SPID's last family, or the last family's top has no `HasEnd`.

- [ ] Step 1: Write the failing tests. Add a small builder in the test file (`static TransactionFacts Tx(string id, string? name, int? spid, string? parent, int first, int last, bool begin = true, bool end = true, long bytes = 100, long? unit = null)`, where `first`/`last` become LSNs `$"00000001:{n:X8}:0001"`). The tests, each asserting the count of operations and the listed properties:
  - `OfflineRebuild_IsOneOperationWithNestedBuild`: `ALTER INDEX` (1..10) with nested `offline index build` and nested `BulkExtentAlloc` → 1, `RebuildOffline`, 3 transactions, bytes the sum.
  - `OnlineRebuild_TakesSameSpidTopLevelInSpan`: `ALTER INDEX` (1..20), nested `ONLINE_INDEX_DDL`, top-level `OnlineIndexInsertTxn` at 5 same SPID, top-level `OnlineIndexInsertTxn` at 6 other SPID → 1 operation, `RebuildOnline`, the other SPID's transaction not in `Transactions`.
  - `OnlineRebuild_DetectedFromNestedDdlOnly`: `ALTER INDEX` with only nested `ONLINE_INDEX_DDL` → `RebuildOnline`.
  - `AlterIndexSetOrDisable_NotListed`: `ALTER INDEX` with no build member → 0.
  - `TwoLevelNesting_ReachesTopLevel`: `CopyRows` under `offline index build` under `ALTER INDEX` → 1 operation, 3 transactions.
  - `Reorganize_OneOperationPerIndex`: SPID 70, `ReorderPages` ×3 on unit 1 then ×2 on unit 2, with nested `SwapPage` → 2 operations.
  - `Reorganize_MoveBlobThenPages_SameOperation`: `MoveBlob` ×2 on unit 5, `ReorderPages` ×2 on unit 1, `RebalanceSpaces` on unit 2 → 2 operations: the first with 4 families, the second with 1.
  - `Reorganize_FamilyWithoutUnit_DoesNotCut`: unit 1, no unit, unit 1 → 1.
  - `Reorganize_BrokenByOtherTransactionOfSameSpid`: `ReorderPages`, `UPDATE`, `ReorderPages` all on unit 1 → 2.
  - `Reorganize_OtherSpidInterleaved_DoesNotBreak`: `QDS base transaction` of SPID 90 between two `ReorderPages` of SPID 70 on unit 1 → 1, 2 families.
  - `Reorganize_OrphanSwapPage_StartsRun`: a `SwapPage` whose parent has no begin record → its own family, eligible.
  - `UserTransaction_WithReorganize_IsMarked`: `user_transaction` with nested `SwapPage`, `MoveRowsLeft` → 1, `Reorganize`, `InUserTransaction`.
  - `UserTransaction_WithoutIndexWork_NotListed`: `user_transaction` with nested `SplitPage` only → 0.
  - `Partial_RebuildOrUserTransactionWithoutCommit`: `ALTER INDEX` with `end: false` → `Partial`; `user_transaction` with nested `SwapPage` and `end: false` → `Partial`; the same two with a commit → not partial.
  - `Partial_ReorganizeAtStartAndEndOfImport`: SPID 70 with only `ReorderPages` families → partial; the same preceded and followed by an `UPDATE` of SPID 70 → not partial.
  - `RebuildStartedBeforeImport_NotListed`: a transaction with `begin: false` carrying 1 MB and top-level `OnlineIndexInsertTxn` of SPID 60 → 0.

- [ ] Step 2: Run `dotnet test tests/SqlLogExplorer.Tests --filter "FullyQualifiedName~IndexMaintenanceTests"`. Expected: build error.

- [ ] Step 3: Implement the records of the Interfaces block and `Group` as the algorithm above says.

- [ ] Step 4: Run the filter of step 2. Expected: exactly 16 passed. Another number means the filter or the work is wrong.

- [ ] Step 5: Break it to see the tests bite: drop the unit cut in rule 2, rerun, see `Reorganize_OneOperationPerIndex` fail; drop the `MoveBlob` exception, see `Reorganize_MoveBlobThenPages_SameOperation` fail; restore both. Two red tests here are the success of this step.

- [ ] Step 6: Commit `Models/IndexMaintenance.cs Services/IndexMaintenance.cs tests/SqlLogExplorer.Tests/IndexMaintenanceTests.cs` with subject `feat: group rebuild and reorganize transactions into operations`.

### Task 4: Objects, records outside transactions, and the report

Files:
- Modify: `Services/IndexMaintenance.cs`, `Models/IndexMaintenance.cs`
- Test: `tests/SqlLogExplorer.Tests/IndexMaintenanceTests.cs`

Interfaces:
- Consumes: `Group` (Task 3), `LogQuery.GetTransactionFactsAsync`, `GetTotalLogBytesAsync`, `SumOutsideTransactionsAsync` (Task 2).
- Produces:
  - `public static string IndexMaintenance.DisplayName(long allocUnitId, string? allocUnitName)`: the name, unless it is null, empty or `Unknown Alloc Unit`, then the id in invariant decimal.
  - `public static (IReadOnlyList<MaintenanceObject> Objects, int Others) IndexMaintenance.Objects(MaintenanceOperation op)`: the members' units summed by id, units whose name starts with `sys.` left out, those with at least 1 % of `op.Bytes` listed by bytes descending, the rest counted in `Others`.
  - `public static IReadOnlyList<IReadOnlyList<(string From, string To)>> IndexMaintenance.ExclusiveRanges(IReadOnlyList<(string First, string Last)> spans)`: for each span, the parts of it covered by no other span, as inclusive `(From, To)` pairs whose bounds are bounds of the input spans. LSNs are opaque strings ordered by `string.CompareOrdinal`.
  - `public sealed record MaintenanceReport(long TotalBytes, long MaintenanceBytes, IReadOnlyList<MaintenanceOperation> Operations);`
  - `public static async Task<MaintenanceReport> IndexMaintenance.AnalyzeAsync(LogQuery query, CancellationToken ct = default)`: facts → `Group` → for each operation, `Objects`/`OtherObjects` set, and for each of its exclusive ranges the `SumOutsideTransactionsAsync` result added to `Records` and `Bytes` (with `with`); operations sorted by `Bytes` descending; `MaintenanceBytes` the sum of their bytes.

Why the bounds need no care: every bound is the LSN of a transaction record, and a record outside transactions never has the LSN of another record, so whether a part includes its bounds never changes a sum.

- [ ] Step 1: Write the failing tests:
  - `DisplayName_UnknownAllocUnit_ShowsId`: `(72057594052608000, "Unknown Alloc Unit")` → `"72057594052608000"`; `(5, "")` → `"5"`; `(5, "dbo.T.IX_n")` → `"dbo.T.IX_n"`.
  - `Objects_SystemUnitsLeftOutAndOnePercentThreshold`: units `dbo.T.PK` 900, `dbo.T.IX_n` 95, `sys.sysrowsets` 3, id 77 (empty name) 2, op bytes 1000 → 2 objects in that order, `Others == 1`.
  - `ExclusiveRanges_DisjointSpansKeepThemselves`, `ExclusiveRanges_OverlapBelongsToNone` (spans A..D and C..F give A..C and D..F), `ExclusiveRanges_NestedSpanOwnsNothing`.
  - `AnalyzeAsync_TotalCountsEachRecordOnce`: a cache with two overlapping reorganize runs on SPIDs 70 and 71 and three outside records (one in each exclusive part, one in the overlap) → each operation gets its one record; `MaintenanceBytes <= TotalBytes`; the overlap record in neither.

- [ ] Step 2: Run `dotnet test tests/SqlLogExplorer.Tests --filter "FullyQualifiedName~IndexMaintenanceTests"`. Expected: build error.

- [ ] Step 3: Implement.

- [ ] Step 4: Run the filter of step 2. Expected: exactly 22 passed (16 of Task 3 and 6 new).

- [ ] Step 5: Break it: make `ExclusiveRanges` return each span whole; see `ExclusiveRanges_OverlapBelongsToNone` and `AnalyzeAsync_TotalCountsEachRecordOnce` fail; restore.

- [ ] Step 6: Commit `Models/IndexMaintenance.cs Services/IndexMaintenance.cs tests/SqlLogExplorer.Tests/IndexMaintenanceTests.cs` with subject `feat: objects and records outside transactions of each maintenance operation`.

### Task 5: The Index maintenance tab

Files:
- Create: `ViewModels/IndexMaintenanceRow.cs`
- Modify: `ViewModels/StatisticsViewModel.cs`, `Views/MainWindow.axaml`
- Test: `tests/SqlLogExplorer.Tests/StatisticsViewModelTests.cs`

Interfaces:
- Consumes: `IndexMaintenance.AnalyzeAsync`, `MaintenanceReport`, `MaintenanceOperation` (Task 4).
- Produces:
  - `public sealed record IndexMaintenanceRow(MaintenanceOperation Operation, double Share)` with get-only properties `string Kind` (`Rebuild offline`, `Rebuild online`, `Reorganize`), `string Objects` (displays joined by `, `, then `and n others` when `OtherObjects > 0`), `string? Begin`, `string? End`, `long Transactions`, `long Records`, `long Bytes`, `string BytesText`, `string ShareText` (percent, one decimal, invariant), `string Notes` (`may extend beyond the import` when partial, `in a user transaction`, joined by `; `).
  - `public static string IndexMaintenanceRow.FormatBytes(long bytes)`: below 1024 `"n B"`, then KB, MB, GB with one decimal, base 1024, invariant culture.
  - `StatisticsViewModel.IndexMaintenance` (`ObservableCollection<IndexMaintenanceRow>`) and `StatisticsViewModel.IndexMaintenanceSummary` (`string`, observable), filled by `RefreshAsync`: `"Imported log records: {total}. Index maintenance: {bytes} ({share})."`, share 0 when the total is 0.

- [ ] Step 1: Write the failing tests: `FormatBytes_Units` (`0 → "0 B"`, `1023 → "1023 B"`, `1536 → "1.5 KB"`, `48_922_276 → "46.7 MB"`); `RefreshAsync_EmptyCache_ShowsZeroShare` (empty grid, summary contains `"0 B (0.0 %)"`); `RefreshAsync_ListsAnOfflineRebuild` (a cache seeded with an `ALTER INDEX` family and its nested `offline index build` → one row, `Kind == "Rebuild offline"`, `Share` between 0 and 100).

- [ ] Step 2: Run `dotnet test tests/SqlLogExplorer.Tests --filter "FullyQualifiedName~StatisticsViewModelTests"`. Expected: build error.

- [ ] Step 3: Implement the row and the view model; in `Views/MainWindow.axaml` add, after the `By object` tab, a `TabItem Header="Index maintenance"` holding a `DockPanel`: the summary `TextBlock` on top, the note of spec §6 at the bottom (verbatim: "Bytes are those of the log records; the log file receives 3 to 16 % more. Records written outside a transaction during an operation are counted with it. Consecutive reorganizes of the same index appear as one line. A rebuild that started before the import is not listed."), and a read-only `DataGrid` with the columns Kind, Objects, Begin, End, Transactions, Records, Log bytes (`Binding="{Binding BytesText}" SortMemberPath="Bytes"`), Share (`SortMemberPath="Share"`), Notes.

- [ ] Step 4: Run the filter of step 2. Expected: all pass, 3 more than before. Then the whole suite: no failure.

- [ ] Step 5: Run the app on a cache with an operation (`dotnet run`, live import of a test database where you ran a rebuild) and look at the tab: the columns fit, sorting by Log bytes sorts by number. Say what you saw.

- [ ] Step 6: Commit `ViewModels/IndexMaintenanceRow.cs ViewModels/StatisticsViewModel.cs Views/MainWindow.axaml tests/SqlLogExplorer.Tests/StatisticsViewModelTests.cs` with subject `feat: an Index maintenance tab with the log volume of each operation`.

### Task 6: Integration test on the four versions

Files:
- Create: `tests/SqlLogExplorer.Tests/Integration/IndexMaintenanceIntegrationTests.cs`

Interfaces:
- Consumes: `LiveDatabaseBackend`, `FnDumpDblogQuery.Build`, `LogColumns.Read`, `LogCache`, `LogQuery`, `IndexMaintenance.AnalyzeAsync`.

One test, `Operations_MatchEachStatement_LiveAndFromBackup`, on a fresh database in `FULL` recovery after a full backup, in this order (one connection for the statements, so one SPID):
1. Tables `T(id uniqueidentifier DEFAULT NEWID() PRIMARY KEY, n int, pad char(400) DEFAULT 'x')` with `IX_n(n)`, `A` and `B` with the same shape and no `IX_n`, each filled with 100 batches of 200 rows of random keys; assert `avg_fragmentation_in_percent > 30` for the four indexes (`sys.dm_db_index_physical_stats`, `LIMITED`).
2. For each statement, read `MAX([Current LSN])` of `fn_dblog` before and after, and, after it, the `IN_ROW_DATA` allocation unit ids of the indexes it touched (`sys.allocation_units` joined to `sys.partitions` on `container_id = hobt_id`). Statements: S1 `ALTER INDEX ALL ON dbo.T REORGANIZE`; S2 `ALTER INDEX ALL ON dbo.A REORGANIZE; ALTER INDEX ALL ON dbo.B REORGANIZE` (one batch); S3 `ALTER INDEX IX_n ON dbo.T SET (ALLOW_PAGE_LOCKS = ON)`; S4 `ALTER INDEX ALL ON dbo.T REBUILD`; S5 `ALTER INDEX IX_n ON dbo.T REBUILD WITH (ONLINE = ON)` while a second connection updates `dbo.A` in a loop until S5 returns.
3. Reference per statement, computed by T-SQL on `fn_dblog` between its two LSNs: bytes of the transactions whose `LOP_BEGIN_XACT` has the statements' SPID, plus records of `0000:00000000`.
4. Live import: `LiveDatabaseBackend.ParseLogAsync` into a `LogCache`, then `AnalyzeAsync`. Then `BACKUP LOG` to a file, import it through `FnDumpDblogQuery.Build` and `LogColumns.Read` into another cache, `AnalyzeAsync` again.
5. For each of the two reports, operations matched to statements by `FirstLsn` between the statement's two LSNs, both passed through `Lsn.Normalize` first (2017 and 2019 return lowercase, the cache holds uppercase): S1 gives 2 `Reorganize` operations, S2 gives 2, S3 gives 0, S4 gives 1 `RebuildOffline`, S5 gives 1 `RebuildOnline`; the first objects of the operations of a statement, as a set, equal the ids read after it for S1 and S2, and belong to them for S4 and S5; the bytes of the operations of each statement, together, lie between 95 % and 101 % of its reference; no operation of the whole report has the second connection's SPID.

- [ ] Step 1: Write the test, dropping the database and deleting the backup files in `finally`.

- [ ] Step 2: Run it on the four versions, each in one invocation: `for v in 2017 2019 2022 2025; do eval "$(tests/fixtures/sqlserver.sh up $v sle-im$v)" && dotnet test tests/SqlLogExplorer.Tests --filter "FullyQualifiedName~IndexMaintenanceIntegrationTests"; tests/fixtures/sqlserver.sh down sle-im$v; done`, one version at a time (memory). Expected: 1 passed, 0 skipped, on each. If a version contradicts a rule of spec04, stop and report the measurement; do not adjust the thresholds.

- [ ] Step 3: Break it: in `Group`, remove the unit cut of rule 2; rerun on 2022; see the S2 count and the 101 % bound fail; restore.

- [ ] Step 4: Commit the test file with subject `test: index maintenance operations against each statement on four versions`.

### Task 7: Documentation

Files:
- Modify: `docs/user-guide.md`, `CHANGELOG.md`

- [ ] Step 1: In `docs/user-guide.md`, change the sentence "The rest of the window is three tabs" to four tabs, and add a section "Index maintenance tab" after "Statistics tabs": what one line is (one rebuild statement, one index of a reorganize), the columns, the summary line, the limits of spec04 §1.2 and §6 note (log record bytes 3 to 16 % below the log written; consecutive reorganizes of one index merged; rebuilds started before the import not listed; `ALTER TABLE ... REBUILD` and columnstore not recognized; in an offline import, objects shown by allocation unit id), and how to read the share column for a customer. Style: no bold, no em dash or en dash.

- [ ] Step 2: In `CHANGELOG.md`, under `[Unreleased]`, an `Added` entry for the tab and the three imported columns, and a `Limits` entry with the same limits in one line each.

- [ ] Step 3: Commit both files with subject `docs: the Index maintenance tab`.
