# Functional Specification 03: RowLog Contents Decoder

Status: design approved in conversation on 2026-10-15, revised the same day after a five-reader review (agy and codex with two prompts each, one Claude reader), all of whom ran their checks on SQL Server 2017, 2019, 2022 and 2025. To be reviewed again before planning.

Supersedes the decoding parts of spec01 (§5, §7.3, §7.4) and moves `UPDATE` reconstruction from post-MVP into this scope.

This is part A of a two-part effort. Part B (a later spec) will read raw `.ldf` and `.trn` files without SQL Server and will reuse the row decoder built here unchanged.

---

## 1. Purpose

The importer already stores `RowLog Contents 0` and `RowLog Contents 1` in the SQLite cache (`Data/LogCache.cs`), but nothing interprets them. This spec turns those bytes into something a person can read, for three uses that carry equal weight:

1. Recovery: what did this row contain before it was deleted or overwritten.
2. Audit: what changed, before and after, column by column.
3. Teaching: what each byte of a row means (status bits, fixed data, null bitmap, offset array, variable data).

Uses 1 and 3 drive one structural decision: the decoder does not return values only, it returns a list of annotated fields, each carrying its byte range, its role and its decoded value. The values view and the annotated hex view are two readings of the same result.

### 1.1 In scope

- Full row images of `LOP_INSERT_ROWS` and `LOP_DELETE_ROWS` on data rows (heap, index id 0, and clustered index leaf, index id 1).
- Deltas of `LOP_MODIFY_ROW` and `LOP_MODIFY_COLUMNS` on the same rows, attributed to the columns they touch.
- Reconstruction of the full row before and after an update, from three anchors (§7): the insert of the row, the delete of the row, and, in live mode, the current row.
- Rows carrying a row-versioning tag (RCSI, snapshot isolation): the tag is recognized and annotated.
- Types: `tinyint`, `smallint`, `int`, `bigint`, `bit`, `decimal`, `numeric`, `money`, `smallmoney`, `real`, `float`, `date`, `time`, `datetime`, `smalldatetime`, `datetime2`, `datetimeoffset`, `uniqueidentifier`, `char`, `varchar`, `nchar`, `nvarchar`, in-row `binary` and `varbinary`.
- SQL Server 2017, 2019, 2022 and 2025.

### 1.2 Out of scope

- `ROW` and `PAGE` compression, `sparse` columns, columnstore.
- LOB and row-overflow values: the in-row pointer is annotated as a pointer, the value is not followed.
- Nonclustered index records: shown as raw bytes only.
- Heap rows that become forwarded: replay stops at the forwarding stub (§7.4).
- Rollbacks to a savepoint inside a transaction that then commits (§7.3).
- The payload that Accelerated Database Recovery (ADR) may append to a row, beyond what the R&D task of §6.2 establishes: bytes not understood are shown raw, they never stop the decoding of the columns before them.

An unsupported type is never an error: its bytes are shown in hex with the label "type not decoded".

### 1.3 Prerequisites

- `fn_dblog` requires sysadmin (measured on 2022 and 2025: a login with `VIEW SERVER STATE` only gets error 229). The live mode therefore always runs as sysadmin, and the catalog reads of §4 and the page reads of §7.2 need nothing more.
- Offline, the schema comes from a connection to a restored copy of the same database, or to the source database itself. `PartitionId` and `AllocUnitId` are internal identifiers: they differ between two databases created by the same script, and they change when an index is rebuilt. `fn_dump_dblog` returns an empty `AllocUnitName` (measured on all four versions), so there is no name to fall back on. A record whose `AllocUnitId` is not found in the connected database is shown with its structure only (§5, "without a schema").

---

## 2. Observations

First probe on 2026-10-15 on `mcr.microsoft.com/mssql/server:2022-latest`, then reproduced by the review panel on 2017, 2019, 2022 and 2025. Tables: `t(id int not null primary key, a int null, b varchar(20) null, c bit, d bit)` and `u(id int not null primary key, a int null, x char(50) null, b varchar(20) null, c bit, d bit)`. Unless stated, the bytes are identical on the four versions.

- `fn_dblog` and `fn_dump_dblog` expose the same columns on the four versions, and the call built by `FnDumpDblogQuery` (5 positional arguments, 63 `DEFAULT`) works on all four. The columns used here: `AllocUnitId`, `PartitionId`, `Page ID` (format `0001:00000150`), `Slot ID`, `Offset in Row`, `Modify Size`, `Num Elements`, `RowLog Contents 0` to `RowLog Contents 5`, `Lock Information`, `Description`, `Log Record` (the whole binary log record).
- `LOP_INSERT_ROWS` of `(1, 10, 'abc', 1, 0)` into `t`: `RowLog Contents 0` = `0x30000D00010000000A000000xx05000001001700616263`, where `xx` is the byte holding the two bits. Status bits `0x30`, `0x00`; fixed data ends at `0x000D`; `id` = 1, `a` = 10; column count 5; null bitmap `0x00`; one variable column ending at `0x0017`; data `abc`.
- The bit byte `xx` is `0x01` on 2017 and 2025, `0x05` on 2022, and varies on 2019 between runs. The bits that belong to no column are not zero. `sys.system_internals_partition_columns.leaf_bit_position` gives each bit column its position (0 for `c`, 1 for `d`); the decoder masks that bit only.
- A `NULL` fixed column is not zeroed: its bytes keep whatever was there (`0xE0E61322` observed for a `NULL int`). Only the null bitmap says it is `NULL`.
- When no variable column is present (bit 5 of status A clear), the row ends after the null bitmap: there is no variable column count. `t_novar(id, n1 int null, v1 varchar(10) null)` with `(3, 30, NULL)` gives a 15-byte row `0x10000C00030000001E000000030004`.
- `LOP_MODIFY_ROW` changing `a` from 10 to 11: `Offset in Row` = 8, `Modify Size` = 1, `RowLog Contents 0` = `0x0A`, `RowLog Contents 1` = `0x0B`. Only the bytes that differ are logged, so a fragment can be one byte of a four-byte column.
- `LOP_MODIFY_ROW` changing `b` from `abc` to `abcdef`: `Offset in Row` = 18, `Modify Size` = 5, before = `0x1700616263`, after = `0x1A00616263646566`. Replacing `Modify Size` bytes at `Offset in Row` with the after bytes gives the row `DBCC PAGE` shows, including when the row grows or shrinks.
- `LOP_MODIFY_ROW` setting a variable column to `NULL`: `Offset in Row` = 0, `Modify Size` = the whole row, after = the whole new row. A fragment can span status bits, fixed data, null bitmap and variable area at once.
- On a clustered index, a `LOP_MODIFY_ROW` carries the clustered key in `RowLog Contents 2`, as an index record: `0x1601000000010000` for `id = 1`, `0x360700000002000001000F00616263` for the key `(k1 = 'abc', k2 = 7)`.
- `Lock Information` holds the key lock of the row: `KEY: 5:72057594045792256 (accf95516a96)` on both the insert and the update of the same row. The same hash ends `RowLog Contents 2` of the insert and `RowLog Contents 3` of the update. In live mode, `SELECT %%lockres%% FROM k` returns `(accf95516a96)` for that row. On a heap, the lock is `RID: 5:1:384:0` (file, page, slot).
- Slots move. Inserting `id` 1, 2, then 0 into a clustered table and then updating `id = 1` logs the update on slot 1, where the last insert was `id = 2`, and the before byte `0x0A` matches both rows. Ghost cleanup (`LOP_EXPUNGE_ROWS`) moves slots the other way.
- `LOP_DELETE_ROWS` arrives with context `LCX_MARK_AS_GHOST` and the full row image in `RowLog Contents 0`, both for the clustered index and for every nonclustered index of the table.
- `LOP_MODIFY_COLUMNS` (`UPDATE u SET a = 99, b = 'zz'`): `Offset in Row` = 0, `Modify Size` = 0, `Num Elements` = 8, `RowLog Contents 0` = `0x0800080044004400`, `RowLog Contents 1` = `0x01000500`, `RowLog Contents 4` = `0x0A00000063000000490061626300000048007A7A`, and `RowLog Contents 5` is filled. With ADR on (2022), even a single-column update is logged as `LOP_MODIFY_COLUMNS`.
- A rolled-back update logs a compensation `LOP_MODIFY_ROW` with an empty `RowLog Contents 0`, the restored bytes in `RowLog Contents 1`, and `Description` = `COMPENSATION`.
- With RCSI on, status A gains bit 6 (`0x70`) and a 14-byte versioning tag follows the variable data; a one-byte update is then logged as a fragment running to the end of the row (`Offset 8, Size 24`).
- A page split logs, in a system transaction, an `LOP_INSERT_ROWS` that can carry several moved rows (the second in `RowLog Contents 1`), and an `LOP_DELETE_SPLIT` on the old page.
- After `ALTER TABLE ... ADD n int NOT NULL DEFAULT 7`, rows written before the change keep their shorter layout, and a delete of such a row logs that shorter image.
- `ALTER INDEX ... REBUILD` changes both `partition_id` and `allocation_unit_id`.

---

## 3. Import extension

`LogRecord`, both backends (`LiveDatabaseBackend`, `FnDumpDblogQuery`) and the `LogRecords` table gain these columns:

| fn_dblog column | Cache column | Type |
|---|---|---|
| `AllocUnitId` | `AllocUnitId` | INTEGER |
| `PartitionId` | `PartitionId` | INTEGER |
| `Page ID` | `PageId` | TEXT |
| `Slot ID` | `SlotId` | INTEGER |
| `Offset in Row` | `OffsetInRow` | INTEGER |
| `Modify Size` | `ModifySize` | INTEGER |
| `Num Elements` | `NumElements` | INTEGER |
| `Description` | `Description` | TEXT |
| `RowLog Contents 2` to `5` | `RowLogContents2` to `5` | BLOB |
| `Lock Information` | `RowLock` | TEXT |

`RowLock` is not the whole `Lock Information` text: the importer keeps only the row lock, `KEY (hash)` for a clustered row or `RID file:page:slot` for a heap row, and `NULL` when there is none. It is the identity of the row for the replay (§7). The cache gains an index on `(AllocUnitId, RowLock, Id)`, which serves the replay without a sort.

The integer columns are read with the type `fn_dblog` actually returns (some are `smallint`), not assumed to be `int`; the existing `LiveDatabaseBackend` must be checked for the same mistake.

A record already present in the cache (same `LSN`) is not inserted twice: two overlapping `.trn` files must not duplicate a modification, since the replay would apply it twice. `LSN` becomes unique and the insert ignores duplicates.

The cache is a disposable file rebuilt at each import, so there is no migration: the `CREATE TABLE` changes.

---

## 4. Components

All decoding code lives in a `Decoding/` folder that references neither SQL client nor UI, so that part B can reuse it.

| Component | Responsibility | Depends on |
|---|---|---|
| `TableSchema` | One allocation unit: its type (`IN_ROW_DATA` or other), index id, and the physical columns of its partition: `partition_column_id`, name (synthetic for dropped columns and the uniqueifier), `leaf_offset`, `leaf_null_bit`, `leaf_bit_position`, `max_inrow_length`, type, precision, scale, code page, `is_nullable`, `is_dropped`, `is_uniqueifier`, `has_default`, `default_value`, and whether the column is part of the clustered key and at which position. | nothing |
| `SchemaProvider` (outside `Decoding/`) | `AllocUnitId` to `TableSchema`. Reads `sys.allocation_units`, `sys.partitions`, `sys.system_internals_partition_columns`, `sys.columns` (left join: dropped columns and the uniqueifier have no row there), `sys.index_columns`, and `COLLATIONPROPERTY(collation_name, 'CodePage')`. Cached per allocation unit for the session. | SQL connection |
| `RowDecoder` | `byte[]` plus optional `TableSchema` to `DecodedRow`. Pure function. Also decodes an index record (the clustered key carried by a modify record, §2) against the key columns. | `TypeDecoders` |
| `TypeDecoders` | Bytes of one column to a .NET value, per SQL type. Text through `Encoding.GetEncoding(codePage)`, with `CodePagesEncodingProvider` registered at start-up. | nothing |
| `ModifyDecoder` | One modify record to a list of fragments `(offset, before bytes, after bytes)`: one fragment for `LOP_MODIFY_ROW`, several for `LOP_MODIFY_COLUMNS` (§6.2). Pure function. | nothing |
| `RowReplayer` | Rebuilds the full row before and after a modify record from an anchor (§7). | cache, `RowDecoder`, `ModifyDecoder` |
| `CurrentRowReader` (outside `Decoding/`) | Live mode: reads the current bytes of a row (§7.2). | SQL connection |
| Details inspector (UI) | Two tabs for the selected record, "Values" and "Bytes" (§9). | `DecodedRow` |

`DecodedRow` is a list of `DecodedField(int Offset, int Length, FieldRole Role, string? Column, object? Value, string? Problem)`. `FieldRole` covers: status bits A, status bits B, fixed-data end offset, fixed column, column count, null bitmap, variable column count, variable offset entry, variable column, LOB pointer, versioning tag, unused bytes, unknown trailer, malformed.

---

## 5. Row decoding

The decoder applies to `LOP_INSERT_ROWS` and `LOP_DELETE_ROWS` whose context is `LCX_HEAP`, `LCX_CLUSTERED` or `LCX_MARK_AS_GHOST`, and whose `AllocUnitId` resolves, through `SchemaProvider`, to an `IN_ROW_DATA` allocation unit of index id 0 or 1. Without that resolution (no schema, unknown allocation unit), the record is decoded "without a schema" (below). Records of index id 2 and above are shown as raw bytes.

An `LOP_INSERT_ROWS` can carry more than one row (page split, §2): every non-empty `RowLog Contents` of such a record is a row image and is decoded. A record logged inside a system transaction (its transaction begins with `LOP_BEGIN_XACT` named `SplitPage` or another system name) is labelled "row moved by the engine", not shown as a user insert or delete.

Walk, in order:

1. Status bits A (1 byte) and status bits B (1 byte). In A: bits 1 to 3 are the record type (0 = primary record, 1 = forwarded record, 2 = forwarding stub; any other type is shown raw), bit 4 says a null bitmap is present, bit 5 that variable columns are present, bit 6 that a versioning tag is present. The decoder follows the flags, not the schema.
2. Fixed-data end offset `F` (2 bytes, little-endian).
3. At `F`: column count `N` (2 bytes), then the null bitmap, `ceil(N / 8)` bytes. The bitmap is read before any fixed column, because a `NULL` fixed column keeps stale bytes (§2).
4. Fixed columns, at their `leaf_offset`, skipped when their null bit is set. A `bit` column reads the byte at its `leaf_offset` and masks `leaf_bit_position`. Bytes no column claims are marked unused.
5. If bit 5 of A is set: variable column count `V` (2 bytes) at `F + 2 + ceil(N / 8)`, then `V` end offsets of 2 bytes each. If bit 5 is clear, `V` is 0 and the row has no variable area.
6. Variable data. The first variable column starts at `F + 2 + ceil(N / 8) + 2 + 2V`; each next one starts at the previous end offset. An end offset is `entry & 0x7FFF`; the high bit `0x8000` marks an off-row pointer (LOB or row-overflow), annotated as such. A variable column is `NULL` when its null bit is set, whatever its length; a zero length with a clear null bit is an empty value. The order of variable columns is the order of their negative `leaf_offset` (-1, -2, ...).
7. If bit 6 of A is set: the last 14 bytes are the versioning tag. Bytes after the variable data and before the tag, if any, are an unknown trailer (ADR, §1.2).

Missing columns. When `N` is smaller than the schema's column count, the columns beyond `N` were added after the row was written: each takes its `default_value` when `has_default` is set, `NULL` otherwise, and is labelled "added after the row was written". Trailing variable columns that are `NULL` may also be absent from the offset array. Neither case is malformed.

Without a schema, steps 1, 2, 3, 5, 6 and 7 still run: the "Bytes" view shows the structure with unnamed fields, and the fixed area as one block.

---

## 6. Modify decoding

### 6.1 `LOP_MODIFY_ROW`

One fragment: before = `RowLog Contents 0`, after = `RowLog Contents 1`, at `Offset in Row`; the before fragment is `Modify Size` bytes long, the after fragment has its own length.

Attribution to columns is done on full rows whenever the replay (§7) provides them: decode the row before and the row after with `RowDecoder` and compare them column by column. This covers every shape of fragment, including a fragment starting at offset 0 that rewrites the whole row (a column set to `NULL`, §2).

Without a full row, the fragment is attributed from the schema alone, and only when it falls entirely inside one fixed column's bytes or one bit byte: the column is named, and its before and after values are given when the fragment covers the whole column (or, for a bit byte, the bits that changed). Any other fragment is shown as annotated bytes.

### 6.2 `LOP_MODIFY_COLUMNS`

The layout is not documented. What the panel measured: `Offset in Row` and `Modify Size` are 0; `RowLog Contents 0` looks like pairs of 2-byte offsets; `RowLog Contents 1` like 2-byte lengths; `RowLog Contents 4` like before and after fragments interleaved, aligned on 4 bytes on some versions and not others; `RowLog Contents 5` holds part of the same data. None of this is established.

Because ADR turns every update into an `LOP_MODIFY_COLUMNS` (§2), this record cannot be left out. The plan starts with an R&D task whose output is a written note of the layout, established on fixtures covering: one fixed column under ADR; two fixed columns; a fixed and a variable column; two variable columns; a change in length; a column set to `NULL` and back; with and without RCSI; on the four versions. The note is a deliverable of its own, reviewed before `ModifyDecoder` decodes this record.

If the layout cannot be established for a version, `ModifyDecoder` returns "layout unknown" for that record on that version: the "Bytes" tab shows it raw, and the replay treats it as a break (§7.3).

---

## 7. Replay

### 7.1 Row identity

A row is identified by `(AllocUnitId, RowLock)`: the key-lock hash for a clustered row, the RID for a heap row (§3). Page and slot are not an identity: slots move inside a page (§2), and a row moves to another page on a split while keeping its key and therefore its hash.

A record without `RowLock` (a system transaction moving rows, for instance) does not take part in a chain. The hash is 6 bytes, so two keys can collide; the before-bytes check of §7.3 and the key check of §7.2 are what catch it.

### 7.2 Anchors

A chain needs one full image of the row at a known LSN. Three anchors, tried in this order:

1. Insert: the latest `LOP_INSERT_ROWS` of the row before the selected record. Replay runs forwards.
2. Delete: the first `LOP_DELETE_ROWS` of the row after the selected record. Its image is the row after its last modification. Replay runs backwards.
3. Current row (live mode only): the row as it is now in the database. Replay runs backwards, from now to the selected record.

The current row is read as follows:
- Decode the clustered key from `RowLog Contents 2` of the selected modify record (§2), with the key columns of `TableSchema`.
- Run a seek on that key, `SELECT %%lockres%%, sys.fn_PhysLocFormatter(%%physloc%%) FROM <table> WHERE <key> = <values>`, and check that `%%lockres%%` equals the row's hash. A mismatch, or no row, means "current row not found": the row was deleted since, or the key could not be decoded. No table scan is ever issued.
- Read the slot bytes with `DBCC PAGE (<db>, <file>, <page>, 1) WITH TABLERESULTS`, from the memory dump lines of that slot.
- Every modification of the row after the window end is fetched from `fn_dblog` for that `RowLock`, so that the backward replay crosses them too.

For a heap, the RID gives file, page and slot directly; the key steps are skipped.

### 7.3 Applying a modification

Records of a transaction that was rolled back (it has an `LOP_ABORT_XACT` in the cache) are removed from the chain, the original modifications and their compensation records together, since their net effect is nothing. A compensation record (`Description` = `COMPENSATION`) inside a transaction that commits (rollback to a savepoint) ends the chain with "unsupported: partial rollback".

Forwards, for each fragment: check that the current row holds the before bytes at the fragment's offset, then replace `len(before)` bytes at that offset with the after bytes.

Backwards: check that the current row holds the after bytes at the offset, then replace `len(after)` bytes with the before bytes. For `LOP_MODIFY_COLUMNS`, the fragments are undone in reverse order.

When a check fails, replay stops and reports "chain broken at LSN x". The check is weak, because only the bytes that differ are logged (one byte, sometimes one bit). It catches gross errors, not an unlucky collision. That is why identity comes from the row lock and not from the check.

### 7.4 Ends of a chain

- `LOP_DELETE_ROWS` of the row ends it forwards; `LOP_INSERT_ROWS` ends it backwards.
- A heap modification whose after bytes are a forwarding stub (record type 2, §5) ends it with "row forwarded, not followed".
- A modify record whose layout is unknown (§6.2) ends it with "modification not decodable at LSN x".
- A change of `AllocUnitId` (index rebuild) ends it: the hash is per allocation unit.

When no anchor is found, the result is "no full image of this row available", and the view falls back to the fragment alone (§6.1).

---

## 8. Errors

The decoder never throws towards the UI. It always returns a `DecodedRow`, possibly containing fields with role malformed and a `Problem` text:

- row shorter than an offset it announces;
- an end offset (after masking `0x7FFF`) beyond the end of the row, or decreasing;
- column count greater than the schema's (a column dropped or a schema from another database);
- fixed-data end offset different from what the schema predicts for this column count.

A column count smaller than the schema's is not an error (§5). These problems are visible in both tabs. Errors surfaced as errors: a failure to connect when reading the schema, and a permission failure, with the statement that failed.

---

## 9. User interface

Decoding happens on demand, when a record is selected in the grid, not during import, so import speed is unchanged.

The details inspector (spec01 §6.1) has two tabs:

- "Values": for an insert or a delete, the decoded columns. For a modify, the touched columns with before and after, and, when the replay succeeded, the full row before and after with the changed columns highlighted. The replay status is shown in one line ("rebuilt forwards from INSERT at LSN x", "rebuilt backwards from the current row", "chain broken at LSN x", "no full image of this row available").
- "Bytes": the hex dump of every non-empty `RowLog Contents` of the record, each field in its own color; hovering a field shows its role, column and value.

In offline mode without a schema connection, "Bytes" still shows the structure and "Values" says that a schema connection is required. User-facing text is in English (spec01 §6).

---

## 10. Tests

### 10.1 Frozen fixtures

A script starts SQL Server 2017, 2019, 2022 and 2025 under podman (images already present on the development machine), creates tables covering every type of §1.1, inserts known values, updates and deletes them, and freezes, per version, as JSON under `tests/`:

- the `fn_dblog` rows involved, with every column of §3 and `Log Record`;
- the `TableSchema` of each allocation unit;
- for some rows, the output of `DBCC PAGE (…, 1)` and `DBCC PAGE (…, 3)`.

`Log Record` is not used by part A. It is captured now because it is the raw material of part B.

Unit tests decode the fixtures without SQL Server. Expected values are the values the script inserted, written in the test, never an output of the decoder copied back. `DBCC PAGE (…, 3)` gives SQL Server's own split of a row into columns, compared with the fields of `RowDecoder`; `DBCC PAGE (…, 1)` gives the bytes the replay must reach.

The script runs as an integration test (`IntegrationFact`, `SQLLOGEXPLORER_RUN_INTEGRATION=1`) and removes its containers when it ends, including on failure.

### 10.2 Cases

Rows:
- isolated `NULL`, a `NULL` fixed column with stale bytes, a row with every nullable column `NULL`;
- a row with no variable column present (bit 5 clear);
- several `bit` columns in one byte, with the non-zero unused bits of §2;
- empty string versus `NULL`, in a non-trailing and a trailing variable column;
- `time` and `datetime2` at precisions 0, 3 and 7; `datetimeoffset`;
- `decimal` at each storage size (5, 9, 13, 17 bytes);
- non-ASCII Unicode; `varchar` under a non-Latin collation; a `_UTF8` collation (2019 and later, it does not exist in 2017);
- a dropped column; a column added with a default after rows were written; a non-unique clustered index (uniqueifier); a heap;
- a `varchar(max)` value pushed off-row, and a row-overflow `varchar(5000)` pair;
- a table with a nonclustered index, whose index deletes must not reach the row decoder;
- RCSI on (versioning tag); ADR on (2022 and 2025).

Replay:
- forwards: insert, several updates on fixed, bit and variable columns, a column set to `NULL`;
- backwards from a delete; backwards from the current row, with modifications after the window end;
- slots that move: insert 1, 2, then 0, then update 1, which must rebuild `id = 1`;
- a page split between the insert and the update;
- a rolled-back update in the middle of a chain; a rollback to a savepoint;
- an index rebuild in the middle of a chain;
- a heap row that becomes forwarded;
- the same `.trn` imported twice.

---

## 11. Open questions

1. The `LOP_MODIFY_COLUMNS` layout (§6.2), and whether it is the same on the four versions.
2. The ADR trailer: what it contains and whether it is worth decoding beyond "unknown trailer".
3. Whether `%%lockres%%` and the log hash agree for every key type in §1.1 (measured for `int` and `varchar` + `int` only).
4. The system transaction names that move rows (`SplitPage` was measured; ghost cleanup and others to list from the fixtures).
