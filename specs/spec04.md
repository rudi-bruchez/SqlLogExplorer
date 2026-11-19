# Functional Specification 04: Index maintenance and log volume

Status: version 2 of 2026-11-18, after the five-reader review of version 1 (§9). Awaiting approval.

## 1. Purpose

A customer's transaction log grew, and the person investigating wants to show which index maintenance caused it. The import already holds every log record; this spec adds the size of each record and groups the records of each `ALTER INDEX ... REBUILD` and of each index reorganized by `ALTER INDEX ... REORGANIZE` into one line: what was maintained, when, how many log records, how many bytes of log records, and which share of the imported log that is.

### 1.1 In scope

- `REBUILD` offline and `ONLINE = ON`, `REORGANIZE` (with its default `LOB_COMPACTION = ON`), on one index or `ALL`, on rowstore indexes.
- The same statements inside an explicit user transaction (rule 3 of §4).
- Live import (`fn_dblog`) and backup import (`fn_dump_dblog`).
- SQL Server 2017, 2019, 2022 and 2025. The probes of §2 ran on 2022 and 2025, and the review reproduced them on 2017; the integration test of §7 runs on the four.

### 1.2 Out of scope

- The physical log written. The measure is `SUM([Log Record Length])`, the bytes of the log records. The log actually written is larger, because of log block headers and padding: measured by the review against `sys.dm_db_log_stats` and the size of the next log backup, 6 % more for an offline rebuild, 3 % for an online rebuild, 16 % for a reorganize (2022 and 2025). The view says so (§6). A per-operation physical figure is not defined: other sessions write into the same log blocks.
- `Log Reserve`, the space reserved for a rollback, and the size of the `.ldf` file or its growth events.
- `ALTER TABLE ... REBUILD` (a transaction named `ALTER TABLE`, 8.6 MB for 20 000 heap rows on 2025), `CREATE INDEX ... WITH (DROP_EXISTING = ON)`, `DBCC DBREINDEX`, `DBCC INDEXDEFRAG`, resumable rebuilds, columnstore indexes: not probed, not recognized.
- `DeferredAllocUnitDrop::Process`, the release of the old tree after a rebuild: it runs on a background SPID after the commit and is not attributed (0.34 % of an offline rebuild of 79 MB on 2022).
- Resolving an allocation unit id to a name when the import has none (§5).

## 2. Observations

Probed on 2026-11-18 on `sql2022` (2022 CU26) and `sql2025` (2025 CU7), identical results; the review reproduced the transaction names and their nesting on 2017. Database in `FULL` recovery after a full backup, table `T(id uniqueidentifier PRIMARY KEY CLUSTERED, n int, pad char(400))` of 100 000 rows plus an index `IX_n(n)`, a log backup before each statement, then the records of `fn_dblog` that were not there before, grouped by the `Transaction Name` of their `LOP_BEGIN_XACT`. A single `INSERT ... SELECT` leaves the clustered index almost unfragmented (0.018 %, measured by the review): a test that needs a reorganize to do work fragments the table first and checks `avg_fragmentation_in_percent` (§7).

| Statement | Where the bytes are (`SUM([Log Record Length])`) |
|---|---|
| `REBUILD` of `ALL`, offline | one transaction `ALTER INDEX`: 48.9 MB of 49.3; then `BulkExtentAlloc`, `offline index build` |
| `REBUILD` of `ALL`, `ONLINE = ON` | `OnlineIndexInsertTxn` 76.5 MB, `SplitPage` 8.1 MB, `BulkExtentAlloc` 3.5 MB; `ALTER INDEX` itself 39 KB |
| `REORGANIZE` of `ALL` | no `ALTER INDEX` transaction at all. `SwapPage` 55.1 MB, `AllocUnlinkedReorgPage` 2.5 MB, `DeallocUnlinkedReorgPage` 1.9 MB, `ReorderPages` 0.4 MB, about 30 000 transactions |
| `REORGANIZE` with LOB data (`varchar(max)`, review, 2022) | top-level `MoveBlob` 9.3 MB, 74 % of the statement; then `SwapPage` 2.1 MB |

- Every transaction of a statement has the `SPID` of the session that ran it, on its `LOP_BEGIN_XACT` and only there. Transactions of other sessions interleave: Query Store on the same or another SPID, `SetFileSize` and `DeferredAllocUnitDrop::Process` on background SPIDs, concurrent DML of other sessions (excluded by the SPID, verified by the review on online rebuild and reorganize).
- For `REBUILD`, offline and online, every transaction of the session begins after the `LOP_BEGIN_XACT` of `ALTER INDEX` and before its `LOP_COMMIT_XACT`.
- Many transactions are nested: `SplitPage`, `BulkExtentAlloc`, `SwapPage`, `MoveRowsLeft`, `AllocUnlinkedReorgPage`, `offline index build`, `ONLINE_INDEX_DDL` have a `Parent Transaction ID`, in the same text form as `Transaction ID`. `OnlineIndexInsertTxn`, `ReorderPages`, `RebalanceSpaces`, `MoveBlob` are top-level. Nesting goes two levels deep inside a rebuild: `CopyRows` under `offline index build` under `ALTER INDEX`, and Query Store's `QDS batch` under `QDS base transaction` under `ALTER INDEX` (review, 2022).
- A rebuild always has a nested `offline index build` (offline) or `ONLINE_INDEX_DDL` (online), even on a three-row table. `ALTER INDEX ... SET (...)` and `ALTER INDEX ... DISABLE` also log a top-level transaction named `ALTER INDEX`, without either.
- `REORGANIZE` is a sequence of top-level `ReorderPages`, `RebalanceSpaces` and `MoveBlob` transactions of the same SPID, each with its children, one index after the other. Two `REORGANIZE` statements in a row on the same session leave nothing between them that tells them apart, and two connections opened one after the other can receive the same SPID (review, 2025).
- In a reorganize of `ALL` on a table with a `varchar(max)` column and a nonclustered index (2022, 99 % fragmentation), the top-level families come in this order: 320 `MoveBlob` on the clustered index's `LOB_DATA` unit, then 1 973 `ReorderPages`/`RebalanceSpaces` on its `IN_ROW_DATA` unit, then 203 on the nonclustered index. The allocation unit with the most bytes in a family was never a system one. Both units of the clustered index have the same `AllocUnitName`; `PartitionId` on the records is not usable to group them (0, `NULL` and unrelated values).
- Inside `BEGIN TRAN ... COMMIT`, the begin record is named `user_transaction` and the reorganize transactions are all nested under it.
- Online rebuild and reorganize also write many records outside any transaction (`Transaction ID` `0000:00000000`), mostly `LOP_MIGRATE_LOCKS`: about 0.8 MB for each statement of the table above.
- An online rebuild writes 1.8 times the log of an offline rebuild of the same indexes (1.77 to 1.87 on 2017, 2022, 2025).
- `AllocUnitName` is resolved by `fn_dblog` when it is read. A live import taken after a second rebuild of the same index shows the first rebuild's records as `Unknown Alloc Unit`, and an online rebuild read right after it ran already has 15 % of its bytes under that name (review, 2022 and 2025). A backup import has an empty `AllocUnitName` everywhere but an `AllocUnitId`.

## 3. Import extension

`LogColumns.SelectList`, `LogRecord` and the `LogRecords` table gain:

| fn_dblog column | Cache column | Type | Filled on |
|---|---|---|---|
| `Log Record Length` | `LogRecordLength` | INTEGER | every record |
| `SPID` | `Spid` | INTEGER | `LOP_BEGIN_XACT` |
| `Parent Transaction ID` | `ParentTransactionId` | TEXT | `LOP_BEGIN_XACT` of a nested transaction |

Read with `Convert`, like the other integer columns. `ParentTransactionId` is stored as returned, like `TransactionId`: both come from the same function in the same form, and the parent is found by plain equality. The cache is rebuilt at each import (spec03 §3), so there is no migration. The review checked on 2017, 2022 and 2025 that `fn_dump_dblog`, through the call `FnDumpDblogQuery` builds, returns the same values for these three columns as `fn_dblog`; the integration test of §7 keeps that check.

## 4. Grouping

A pure function, `IndexMaintenance.Group`, outside `Decoding/`, takes the transactions of the cache and returns the operations. One SQL query (`LogQuery`) gives, per `TransactionId` other than `0000:00000000`: name, SPID and parent (from `LOP_BEGIN_XACT`), first and last LSN, `BeginTime`, `EndTime`, whether a begin, a commit or an abort record is in the cache, record count, `SUM(LogRecordLength)`, and its bytes per `AllocUnitId` with the `AllocUnitName` seen for it.

Every transaction belongs to its top-level ancestor, found by following `ParentTransactionId` up; a transaction whose parent has no begin record in the cache (its parent began before the import, so it has neither name nor SPID) is its own top-level ancestor. A top-level transaction without a begin record has no SPID and enters no rule. The rules below look at top-level transactions, each counted with all its descendants ("the family" below). The allocation unit of a family is the one with the most bytes in it.

Rule 1, rebuild. Each top-level transaction named `ALTER INDEX` opens a candidate whose span runs from its first LSN to its last LSN. The candidate takes every top-level family of the same SPID whose first LSN is in the span. It is a rebuild online when its families contain a transaction named `ONLINE_INDEX_DDL` or `OnlineIndexInsertTxn`, a rebuild offline when they contain `offline index build`, and otherwise it is not an index maintenance operation (`SET`, `DISABLE`) and is not listed.

Rule 2, reorganize. The top-level families of each SPID not taken by rule 1 nor rule 3, in LSN order, are cut into runs: a run is a maximal sequence of consecutive families of that SPID whose top-level names are in `{ReorderPages, RebalanceSpaces, MoveBlob, SwapPage, MoveRowsLeft, AllocUnlinkedReorgPage, DeallocUnlinkedReorgPage}` (the last four when their parent is missing). Another transaction of the same SPID ends the run, and so does a change of the family's allocation unit, with two exceptions: a family without any allocation unit does not cut, and the change from a `MoveBlob` family to a family of another name does not cut, because the LOB phase of an index (its `LOB_DATA` allocation unit) comes just before the page phase of the same index (its `IN_ROW_DATA` unit, §2). A reorganize of `ALL` therefore gives one operation per index, which is what the investigation wants to read. Two reorganizes of the same index one after the other, on one session or on two sessions given the same SPID, stay one operation; this is said in the view (§6).

Rule 3, inside a user transaction. A top-level transaction named `user_transaction` whose descendants contain `offline index build`, `ONLINE_INDEX_DDL`, or a reorganize name of rule 2 is one operation of the matching kind, marked "in a user transaction". Like rule 1, it also takes every top-level family of the same SPID whose first LSN is in its span: an online rebuild inside `BEGIN TRAN` writes its `OnlineIndexInsertTxn` transactions at the top level, 99.5 % of its bytes (plan review, 2022). Its bytes are those of all these families, which can include other work done in the same transaction; the mark says so.

Records outside any transaction. A record of `0000:00000000` whose LSN is in the span (first to last LSN of its families) of exactly one operation is added to it; one in the span of several operations, or of none, is added to none. This keeps the total of §6 from counting a record twice when operations overlap (concurrent sessions). These records cannot be tied to a session: in the span of an online rebuild running beside a second session that updated and checkpointed in a loop, they were 983 KB, 2.1 % of the window, checkpoint records included (review, 2025).

Partial. An operation of rule 1 or 3 is partial when its `ALTER INDEX` or `user_transaction` has no commit or abort record in the cache: its begin record dates its start. An operation of rule 2 is partial when its first family is the first top-level transaction of its SPID in the import (it may have started before), or when its last family has no commit or abort record or is the last top-level transaction of its SPID in the import (it may go on after); the view reads "may extend beyond the import". A rebuild whose `ALTER INDEX` begin record is before the import is not listed: its online transactions are not reorganize names and its offline work is inside the transaction that has no begin; the view says so.

## 5. Object names

An operation lists its allocation units by bytes: those holding at least 1 % of the operation's bytes, then "and n others". Each is shown by the `AllocUnitName` seen for its id, unless that name is empty, `Unknown Alloc Unit` or starts with `sys.`; with no usable name, by `AllocUnitId`. Units shown by the same name (the `LOB_DATA` and `IN_ROW_DATA` units of one index) are one object, their bytes added. System allocation units are left out by name when there is one; in a backup import they cannot be told apart, and the 1 % threshold is what keeps them from filling the column (19 of the 24 units of an online rebuild were system ones, review).

Resolving ids to names through the schema connection is left for later: a rebuild changes the allocation unit id (spec03 §2), so the id of an old rebuild may not exist any more in the connected database.

## 6. View

A tab "Index maintenance" next to "By operation type" and "By object", filled at the same time as the statistics, on the whole import (the records filter does not apply, as for the other statistics tabs).

A line above the grid: the total log record bytes imported, and the bytes and share of all the operations found, counted once each (§4).

Columns: Kind (`Rebuild offline`, `Rebuild online`, `Reorganize`), Objects, Begin, End, Transactions, Records, Log bytes (shown in KB/MB/GB, sorted on the number), Share of imported log (percent), Notes (`partial`, `in a user transaction`). Sorted by log bytes, descending.

A note under the grid: "Bytes are those of the log records; the log file receives 3 to 16 % more. Records written outside a transaction during an operation are counted with it. Consecutive reorganizes of the same index appear as one line. A rebuild that started before the import is not listed."

## 7. Tests

Unit tests of `IndexMaintenance.Group` on synthetic transactions, one per rule and per case:

- offline rebuild, online rebuild, `ALL` (several objects in one operation);
- `ALTER INDEX ... SET` and `DISABLE`: nothing listed;
- reorganize of `ALL` on two indexes: two operations; two reorganizes of two tables one after the other on one SPID: two operations; a run broken by an ordinary transaction of the same SPID;
- `MoveBlob` at the top of a reorganize: the LOB phase and the page phase of one index give one operation, and the next index another;
- a family without allocation unit inside a run;
- reorganize inside `user_transaction`: one operation, marked;
- another SPID interleaved (Query Store, DML);
- a nested transaction two levels deep, a nested transaction whose parent is missing;
- a record outside a transaction inside one span, inside two overlapping spans (counted in none), the total line not above the sum of distinct bytes;
- partial at the start and at the end of the import, for each rule; an import starting in the middle of a rebuild: nothing listed;
- names: `Unknown Alloc Unit`, empty name, `sys.` names, the 1 % threshold.

`LogCacheTests`: the three columns round-trip.

Integration test on the four versions, live and from a log backup. The table of §2, smaller, fragmented by inserting in many small batches of random keys and checked by `avg_fragmentation_in_percent > 30` before the reorganize statements. The script records the session's `@@SPID` and the LSN before and after each statement, and runs: an offline rebuild of `ALL`, an online rebuild of one index, `SET`, a reorganize of `ALL`, then two reorganizes of two tables one after the other, with DML from a second session during the online rebuild. It asserts the number of operations of each statement (one per index for a reorganize of `ALL`), the kind and the objects of each, and that the bytes of the operations of one statement, together, lie between 95 % and 101 % of a reference measured by the script itself: the bytes of the transactions of that SPID whose begin record is between the statement's two LSNs (descendants included), plus the records outside any transaction between those two LSNs. The upper bound is what catches a merge with the next statement.

## 8. Documentation

`docs/user-guide.md` gains a section on the tab: what it counts, what it does not (§1.2), the gap with the physical log, and how to read the share column for a customer. `CHANGELOG.md` gains an entry.

## 9. Review of version 1

Five readers on 2026-11-18: Vibe (Mistral Large, directive prompt), Vibe (GLM, neutral), codex twice (GPT-6.2-Sol, directive and neutral), a Claude subagent (neutral). What changed:

- Rule 2 merged consecutive reorganizes into one line (codex twice, Claude, GLM): runs are now cut at a change of allocation unit, and the remaining merge is said in the view. The cut was itself measured before it went in: it would have split an index's LOB phase from its page phase, hence the `MoveBlob` exception.
- A reorganize cut by the start of the import was shown as complete (codex twice): the partial rule now looks at the first and last transaction of the SPID in the import.
- `ALTER INDEX ... SET` and `DISABLE` were listed as offline rebuilds (Claude, then verified on 2022): the kind now comes from the nested build transactions.
- `MoveBlob` was missing from the reorganize names and held 74 % of a LOB reorganize (GLM).
- A reorganize inside a user transaction was not caught, contrary to version 1's §1.2 (GLM): rule 3.
- `SUM([Log Record Length])` is 3 to 16 % below the log written (Claude): stated in §1.2 and in the view.
- `Unknown Alloc Unit` in live imports, system units in backup imports, and the missing `AllocUnitId` in the query (Claude, codex neutral): §4 and §5.
- Records outside a transaction counted twice in overlapping spans (Claude, GLM): counted only in one.
- The §2 setup did not fragment the table, and the 90 % criterion of §7 could not fail on a merge (all readers): §2 and §7.
- `ALTER TABLE ... REBUILD` and `DeferredAllocUnitDrop::Process` (Claude, GLM): out of scope, said.
- Nesting two levels deep inside a rebuild (Mistral): §4 follows the parent chain up to the top-level ancestor.

The review of the plan (2026-11-19) changed two rules: rule 3 takes the top-level families in its span, after an online rebuild inside `BEGIN TRAN` was found to keep 0.5 % of its bytes; §5 shows the units of one index once.

Rejected: `FnDumpDblogQuery` would fail on all four versions with 63 `DEFAULT` arguments and need 62 (Mistral). Measured again on 2022 CU26 and 2025 CU7 on 2026-11-18: 63 returns the rows, 62 fails with Msg 313 (insufficient arguments), 64 with Msg 8144 (too many). codex and Claude also read backups through that call.
