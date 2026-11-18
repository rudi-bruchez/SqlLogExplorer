# Functional Specification 04: Index maintenance and log volume

Status: draft of 2026-11-18, written after the probes of §2, awaiting approval and the five-reader review.

## 1. Purpose

A customer's transaction log grew, and the person investigating wants to show which index maintenance caused it. The import already holds every log record; this spec adds the size of each record and groups the records of each `ALTER INDEX ... REBUILD` and `ALTER INDEX ... REORGANIZE` into one line: what was maintained, when, how many log records, how many bytes of log, and which share of the imported log that is.

### 1.1 In scope

- `REBUILD` offline and `ONLINE = ON`, `REORGANIZE`, on one index or `ALL`.
- Live import (`fn_dblog`) and backup import (`fn_dump_dblog`).
- SQL Server 2017, 2019, 2022 and 2025 (the probes ran on 2022 and 2025, §2; the integration test of §7 runs on the four).

### 1.2 Out of scope

- `Log Reserve`, the space reserved for a rollback. It weighs on the size of the active log during a long transaction, but it is not log written; a later column if the investigation needs it.
- The size of the `.ldf` file and its growth events, and the volume written between two log backups.
- `ALTER INDEX` run inside an explicit user transaction: the begin record is then named `user_transaction`, and rule 1 of §4 does not see it. Its system transactions are still caught by rule 2 when they are reorganize transactions.
- `DBCC DBREINDEX`, `DBCC INDEXDEFRAG`, `CREATE INDEX ... WITH (DROP_EXISTING = ON)`, resumable rebuilds: not probed.
- Resolving an allocation unit to a name when `AllocUnitName` is empty (§5).

## 2. Observations

Probed on 2026-11-18 on `sql2022` (2022 CU26) and `sql2025` (2025 CU7), identical results. Database in `FULL` recovery after a full backup, table `T(id uniqueidentifier PRIMARY KEY CLUSTERED, n int, pad char(400))` of 100 000 rows plus an index `IX_n(n)`, a log backup before each statement, then the records of `fn_dblog` that were not there before, grouped by the `Transaction Name` of their `LOP_BEGIN_XACT`.

| Statement | Where the bytes are (`SUM([Log Record Length])`) |
|---|---|
| `REBUILD` of `ALL`, offline | one transaction `ALTER INDEX`: 48.9 MB of 49.3; then `BulkExtentAlloc`, `offline index build` |
| `REBUILD` of `ALL`, `ONLINE = ON` | `OnlineIndexInsertTxn` 76.5 MB, `SplitPage` 8.1 MB, `BulkExtentAlloc` 3.5 MB; `ALTER INDEX` itself 39 KB |
| `REORGANIZE` of `ALL` | no `ALTER INDEX` transaction at all. `SwapPage` 55.1 MB, `AllocUnlinkedReorgPage` 2.5 MB, `DeallocUnlinkedReorgPage` 1.9 MB, `ReorderPages` 0.4 MB, about 30 000 transactions |

- Every transaction of a statement has the `SPID` of the session that ran it, on its `LOP_BEGIN_XACT`. Transactions of other sessions interleave: Query Store (`QDS base transaction`, `QDS batch`) on another SPID, `SetFileSize` on a background SPID.
- For `REBUILD`, offline and online, every transaction of the session begins after the `LOP_BEGIN_XACT` of `ALTER INDEX` and before its `LOP_COMMIT_XACT`.
- Many of these transactions are nested: `SplitPage`, `BulkExtentAlloc`, `SwapPage`, `MoveRowsLeft`, `AllocUnlinkedReorgPage` have a `Parent Transaction ID`. `OnlineIndexInsertTxn`, `ReorderPages`, `RebalanceSpaces` are top-level.
- `REORGANIZE` is a sequence of top-level `ReorderPages` and `RebalanceSpaces` transactions of the same SPID, each with its children.
- Online rebuild and reorganize also write many records outside any transaction (`Transaction ID` `0000:00000000`): 14 214 `LOP_MIGRATE_LOCKS` during the online rebuild, 10 860 during the reorganize, about 0.8 MB.
- An online rebuild writes 1.8 times the log of an offline rebuild of the same indexes, row by row.

## 3. Import extension

`LogColumns.SelectList`, `LogRecord` and the `LogRecords` table gain:

| fn_dblog column | Cache column | Type | Filled on |
|---|---|---|---|
| `Log Record Length` | `LogRecordLength` | INTEGER | every record |
| `SPID` | `Spid` | INTEGER | `LOP_BEGIN_XACT` |
| `Parent Transaction ID` | `ParentTransactionId` | TEXT | `LOP_BEGIN_XACT` of a nested transaction |

Read with `Convert`, like the other integer columns. The cache is rebuilt at each import (spec03 §3), so there is no migration. Spec03 §2 states that `fn_dump_dblog` has the same columns as `fn_dblog`; the integration test of §7 checks these three on a backup.

## 4. Grouping

A pure function, `IndexMaintenance.Group`, outside `Decoding/`, takes the transactions of the cache and returns the operations. One SQL query (`LogQuery`) gives, per `TransactionId` other than `0000:00000000`: name, SPID and parent (from `LOP_BEGIN_XACT`), first and last LSN, `BeginTime`, `EndTime`, whether a begin, a commit or an abort record is in the cache, record count, `SUM(LogRecordLength)`, and the allocation unit names of its records with their bytes.

A nested transaction belongs to the operation of its parent, found by `ParentTransactionId`, whatever its own name. Rules 1 and 2 only look at top-level transactions; a nested transaction whose parent is not in the cache is treated as top-level.

Rule 1, rebuild. Each top-level transaction named `ALTER INDEX` opens an operation whose span runs from its first LSN to its last LSN (its commit or abort). The operation takes every top-level transaction of the same SPID whose first LSN is in the span. It is online when one of its transactions is named `OnlineIndexInsertTxn` or `ONLINE_INDEX_DDL`, offline otherwise.

Rule 2, reorganize. The top-level transactions of each SPID not taken by rule 1, in LSN order, are cut into runs: a run is a maximal sequence of consecutive transactions of that SPID whose names are all in `{ReorderPages, RebalanceSpaces, SwapPage, MoveRowsLeft, AllocUnlinkedReorgPage, DeallocUnlinkedReorgPage}`. Any other transaction of the same SPID ends the run, which also keeps a reused SPID from joining two sessions. Each run is one reorganize operation.

Records outside any transaction whose LSN is in the span of an operation (first to last LSN of its transactions) are added to it by a second query per operation. They cannot be tied to a session; on a busy server they can include a little of someone else's work, which the view says (§6).

An operation is partial when its `ALTER INDEX` has no commit or abort in the cache (the import ends in the middle), or, for rule 2, when its first transaction is a nested one whose parent is not in the cache (the import starts in the middle). A rebuild whose `ALTER INDEX` begin is before the import starts is not recognized: its online transactions are not reorganize names, so they are left out rather than mislabelled. The view says that only operations whose start is in the import are listed.

## 5. Object names

Each operation lists the allocation units of its records, by bytes, without the system ones (`AllocUnitName` starting with `sys.`): `dbo.T.PK__T__3213E83F`, `dbo.T.IX_n`. A backup import has empty `AllocUnitName` (spec03 §1.3); the operation then shows `AllocUnitId` values. Resolving them through the schema connection is left for later: a rebuild changes the allocation unit id (spec03 §2), so the id of an old rebuild may not exist any more in the connected database.

## 6. View

A tab "Index maintenance" next to "By operation type" and "By object", filled at the same time as the statistics, on the whole import (the records filter does not apply, as for the other statistics tabs).

A line above the grid: the total log bytes imported, and the bytes and share of all index maintenance found.

Columns: Kind (`Rebuild offline`, `Rebuild online`, `Reorganize`), Objects, Begin, End, Transactions, Records, Log bytes (shown in KB/MB/GB, sorted on the number), Share of imported log (percent), Partial. Sorted by log bytes, descending.

A note under the grid: "Records written outside a transaction during an operation are counted with it. Only operations whose start is in the import are listed."

## 7. Tests

- Unit tests of `IndexMaintenance.Group` on synthetic transactions: offline rebuild, online rebuild, reorganize, `ALL` (several objects in one operation), another SPID interleaved (Query Store), a reorganize run broken by an ordinary transaction of the same SPID, a nested transaction found by its parent, an `ALTER INDEX` without commit (partial), an import starting in the middle of a rebuild (nothing listed).
- `LogCacheTests`: the three columns round-trip.
- Integration test on the test containers, the four versions: the table of §2 (smaller), each statement, import live and from a log backup, and check the kind, the object and that the operation holds at least 90 % of the bytes written by its session's transactions during the statement.

## 8. Documentation

`docs/user-guide.md` gains a section on the tab: what it counts, what it does not (§1.2), and how to read the share column for a customer. `CHANGELOG.md` gains an entry.
