# Functional Specification 03: RowLog Contents Decoder

Status: design approved in conversation on 2026-10-15, revised twice the same day after two five-reader reviews (agy and codex with two prompts each, one Claude reader), all of whom ran their checks on SQL Server 2017, 2019, 2022 and 2025. Version 7, after a third panel, the plan's panel, the layout research of the plan's task 4 (`docs/research/rowlog-layouts.md`), and the review of the replay engine (§7.3, §7.4).

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
- Replaying backwards across a compensation record, and replaying a transaction that rolled back part of its work (§7.3).
- The current-row anchor on a heap or on a database with Accelerated Database Recovery (§7.2).
- Memory-optimized tables (In-Memory OLTP). Their changes reach `fn_dblog` only as opaque `LOP_HK` records, outside the operations §5 admits, so they are listed but never decoded. Their rows are readable through another undocumented function, `sys.fn_dblog_xtp` (`newrow_data`, `oldrow_key_data`), in a format of their own (§2): a later spec.
- Rows written before an `ALTER COLUMN` that changed a column's physical type or position: the current schema cannot describe them, and nothing in the row says so. Only `ADD COLUMN` is handled (§5).
- The rows an engine operation packs into one `LOP_INSERT_ROWS` (page split): only `RowLog Contents 0` is decoded, the others are shown raw (§5).
- The payload that Accelerated Database Recovery (ADR) appends after the versioning tag: shown raw as an unknown trailer (§5), it never stops the decoding of the columns before it.

An unsupported type is never an error: its bytes are shown in hex with the label "type not decoded".

### 1.3 Prerequisites

- `fn_dblog` requires sysadmin (measured on 2022 and 2025: a login with `VIEW SERVER STATE` only gets error 229). The live mode therefore always runs as sysadmin, and the catalog reads of §4 and the page reads of §7.2 need nothing more.
- Offline, the schema comes from a connection to a restored copy of the same database, or to the source database itself. `PartitionId` and `AllocUnitId` are internal identifiers: they differ between two databases created by the same script, and they change when an index is rebuilt. `fn_dump_dblog` returns an empty `AllocUnitName` (measured on all four versions), so there is no name to fall back on. A record whose `AllocUnitId` is not found in the connected database is shown with its structure only (§5, "without a schema").

---

## 2. Observations

First probe on 2026-10-15 on `mcr.microsoft.com/mssql/server:2022-latest`, then reproduced by two review panels on 2017, 2019, 2022 and 2025. Tables: `t(id int not null primary key, a int null, b varchar(20) null, c bit, d bit)` and `u(id int not null primary key, a int null, x char(50) null, b varchar(20) null, c bit, d bit)`. Unless stated, the bytes are identical on the four versions.

- `fn_dblog` and `fn_dump_dblog` expose the same columns on the four versions, and the call built by `FnDumpDblogQuery` (5 positional arguments, 63 `DEFAULT`) works on all four. The columns used here: `AllocUnitId`, `PartitionId` (both `bigint`), `Page ID` (format `0001:00000150`), `Slot ID` (`int`), `Offset in Row`, `Modify Size`, `Num Elements` (`smallint`), `RowLog Contents 0` to `RowLog Contents 5`, `Description`, `Transaction Name`, `Lock Information`, `Log Record` (the whole binary log record).
- `LOP_INSERT_ROWS` of `(1, 10, 'abc', 1, 0)` into `t`: `RowLog Contents 0` = `0x30000D00010000000A000000xx05000001001700616263`, where `xx` is the byte holding the two bits. Status bits `0x30`, `0x00`; fixed data ends at `0x000D`; `id` = 1, `a` = 10; column count 5; null bitmap `0x00`; one variable column ending at `0x0017`; data `abc`. `RowLog Contents 2` of the same insert is not a row: it is a 26-byte structure ending with the key-lock hash.
- The bit byte `xx` differs between versions and between runs (`0x01`, `0x05`, `0x09`, `0x11`, `0xCD` were all observed). The bits that belong to no column are not zero. `sys.system_internals_partition_columns.leaf_bit_position` gives each bit column its position (0 for `c`, 1 for `d`); the decoder masks that bit only. No fixture may expect a fixed value for the unused bits.
- A `NULL` fixed column of an inserted row is not zeroed: its bytes keep whatever was there (`0xE0E61322` observed for a `NULL int`). An `UPDATE` that sets a fixed column to `NULL` does zero its bytes. Only the null bitmap says it is `NULL`. This holds for a nullable key column too: the same `NULL` key column holds stale bytes in the insert image and zeros in the index record of the modifies. A `bit` key column shares its byte with other bit columns, whose bits change during the row's life.
- When no variable column is present (bit 5 of status A clear), the row ends after the null bitmap: there is no variable column count. `t_novar(id, n1 int null, v1 varchar(10) null)` with `(3, 30, NULL)` gives a 15-byte row `0x10000C00030000001E000000030004`.
- `LOP_MODIFY_ROW` changing `a` from 10 to 11: `Offset in Row` = 8, `Modify Size` = 1, `RowLog Contents 0` = `0x0A`, `RowLog Contents 1` = `0x0B`. Only the bytes that differ are logged, so a fragment can be one byte of a four-byte column.
- `LOP_MODIFY_ROW` changing `b` from `abc` to `abcdef`: `Offset in Row` = 18, `Modify Size` = 5, before = `0x1700616263`, after = `0x1A00616263646566`. Replacing `Modify Size` bytes at `Offset in Row` with the after bytes gives the row `DBCC PAGE` shows, including when the row grows or shrinks.
- `LOP_MODIFY_ROW` setting a variable column to `NULL`: `Offset in Row` = 0, `Modify Size` = the whole row, after = the whole new row. A fragment can span status bits, fixed data, null bitmap and variable area at once.
- On a clustered index, every modify record except compensation records carries the clustered key in `RowLog Contents 2`, as an index record. Its layout is not a data row's: one status byte, no status B, no fixed-data end offset; the fixed key columns follow at offset 1, then the column count, the null bitmap, and the variable key columns: `0x1601000000010000` for `id = 1`; `0x360700000002000001000F00616263` for `(k1 = 'abc', k2 = 7)`; `0x3602000002000B000E0041424344` and `0x3602000002000C000E0041424344` for the keys `('A', 'BCD')` and `('AB', 'CD')`; the uniqueifier of a non-unique clustered index is included (`...00000000` and `...01000000` for two rows with the same key). This holds when the statement took no row lock (`TABLOCKX`). A heap modify has an empty `RowLog Contents 2`.
- `Lock Information` is not a row identity. It records the locks the statement acquired: under `TABLOCK`, `TABLOCKX`, lock escalation or `ALLOW_ROW_LOCKS = OFF` it holds no `KEY` or `RID` lock at all (5663 of 6000 rows of one `INSERT ... SELECT` had none), and the 6-byte key hash collides for `('A', 'BCD')` and `('AB', 'CD')`.
- Slots move. Inserting `id` 1, 2, then 0 into a clustered table and then updating `id = 1` logs the update on slot 1, where the last insert was `id = 2`, and the before byte `0x0A` matches both rows. Ghost cleanup (`LOP_EXPUNGE_ROWS`) moves slots the other way. A heap slot freed by a delete is reused by the next insert.
- `LOP_DELETE_ROWS` arrives with context `LCX_MARK_AS_GHOST` and the full row image in `RowLog Contents 0`, both for the clustered index and for every nonclustered index of the table. A nonclustered index record has record type 3 in status A (`0x16`, `0x36`); a data row has type 0.
- `LOP_MODIFY_COLUMNS` (`UPDATE u SET a = 99, b = 'zz'`): `Offset in Row` = 0, `Modify Size` = 0, `Num Elements` = 8, `RowLog Contents 0` = `0x0800080044004400`, `RowLog Contents 1` = `0x01000500`, `RowLog Contents 4` = `0x0A00000063000000490061626300000048007A7A`, and `RowLog Contents 5` is filled. The same record type also appears for the second single-column update of a row under RCSI, on 2017 and 2022. Under ADR, a single-column update was logged as `LOP_MODIFY_ROW` in other runs: which form appears depends on more than ADR.
- A rolled-back update logs a compensation `LOP_MODIFY_ROW` with `Description` = `COMPENSATION`, an empty `RowLog Contents 0`, the restored bytes in `RowLog Contents 1` (5 bytes restoring an 8-byte fragment: `Modify Size` is the length replaced, not the length of the restored bytes), and an empty `RowLog Contents 2`: a compensation record carries no key. A compensation `LOP_MODIFY_COLUMNS` keeps its offsets in `RowLog Contents 0` and its lengths in `RowLog Contents 1`, has empty before elements and its restored bytes from `RowLog Contents 5` on (§6.2). A rolled-back insert logs a compensation `LOP_DELETE_ROWS` with an empty `RowLog Contents 0`; a rolled-back delete, a compensation `LOP_INSERT_ROWS` with the full image. The transaction ends with `LOP_ABORT_XACT`.
- Under ADR, `ROLLBACK TRAN <savepoint>` inside a transaction that commits logs `LOP_CTR_NEST_ABORT` and no compensation of the row at all, and `DBCC PAGE` then still shows the aborted bytes as a primary record (2022): the page is not the committed row. The undone bytes also reach the log: the next modification of the row is logged with the aborted value in its before bytes, and a later delete logs an image holding the aborted value (2019, 2022, 2025).
- With RCSI on, status A gains bit 6 (`0x70`) and a 14-byte versioning tag follows the variable data; a one-byte update is then logged as a fragment running to the end of the row (`Offset 8, Size 24`). Under ADR, a further payload follows the tag.
- `DBCC PAGE` reads the buffer pool: during an open transaction it shows the uncommitted bytes, while a `SELECT` under RCSI shows the committed row. A heap slot can hold a ghost record of a deleted row.
- A page split logs, in a system transaction, an `LOP_INSERT_ROWS` packing the moved rows into several `RowLog Contents` (one split of 239 rows gave lengths `15|15|15|15|3760|3744`), and an `LOP_DELETE_SPLIT` on the old page. Such inserts carry no row lock.
- `Current LSN` is lowercase hexadecimal on 2017 and 2019, uppercase on 2022 and 2025. Passed back to `fn_dblog` in that colon form, an LSN raises Msg 9005 as soon as it holds a letter (2019, 2022, 2025), and on 2017 can silently return the head of the log; the decimal form (`42:1136:19`, each part converted from hexadecimal) works. With a start LSN that has been truncated since, `fn_dblog` does not fail: it silently starts at the current head of the log.
- After `ALTER TABLE ... ADD n int NOT NULL DEFAULT 7`, rows written before the change keep their shorter layout, and a delete of such a row logs that shorter image. `has_default` and `default_value` are set for the added column. A dropped column keeps its place, `is_dropped = 1`, and a `partition_column_id` above 67108864: column order in the row follows `leaf_null_bit`, not `partition_column_id`.
- A memory-optimized table's changes appear in `fn_dblog` only as `LOP_HK` records, context `LCX_NULL`, without `AllocUnitId` or `AllocUnitName`, several operations per record (2022). `sys.fn_dblog_xtp` exposes them one by one: an insert of `(1, 10, 'abc')` gives `newrow_data` = `0x010000000A0000000000100013000000616263`, an update a full new image and the old key in `oldrow_key_data`, a delete the key alone. The row has no status bits, and `xtp_object_id` (2147483648) is an internal identifier, not the table's `object_id`.
- `ALTER INDEX ... REBUILD` changes both `partition_id` and `allocation_unit_id`. A `RESTORE` keeps them.

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
| `Transaction Name` | `TransactionName` | TEXT |
| `RowLog Contents 2` to `5` | `RowLogContents2` to `5` | BLOB |
| `Log Record`, for `LOP_MODIFY_COLUMNS` records only (`NULL` otherwise) | `LogRecordBytes` | BLOB |

and one column filled later: `RowKey` (TEXT, `NULL` at import, §7.1).

The integer columns are read with the type `fn_dblog` actually returns (`bigint`, `int`, `smallint`, §2), not assumed to be `int`.

A record already present in the cache (same `LSN`) is not inserted twice: two overlapping `.trn` files must not duplicate a modification, since the replay would apply it twice. `LSN` becomes unique and the insert ignores duplicates. Every ordering used by the replay is by `LSN`, never by `Id`: files can be imported in any order (spec01 §3.2). The importer stores `LSN` uppercase (§2: 2017 and 2019 return it lowercase); fixed-width uppercase hexadecimal sorts as text. An LSN passed back to `fn_dblog` is converted to the decimal form `a:b:c` (§2). The existing `LiveLsnResolver`, which passes LSNs to `fn_dblog(@start, @end)`, is checked against the same trap. The records grid orders by `LSN` too.

The cache gains an index on `(AllocUnitId, RowKey, LSN)` and one on `(AllocUnitId, PageId, SlotId, LSN)`.

The cache is a disposable file rebuilt at each import, so there is no migration: the `CREATE TABLE` changes.

---

## 4. Components

All decoding code lives in a `Decoding/` folder that references neither SQL client nor UI, so that part B can reuse it.

| Component | Responsibility | Depends on |
|---|---|---|
| `TableSchema` | One allocation unit: its type (`IN_ROW_DATA` or other), index id, and the physical columns of its partition ordered by `leaf_null_bit`: `partition_column_id`, name (synthetic for dropped columns and the uniqueifier), `leaf_offset`, `leaf_null_bit`, `leaf_bit_position`, `max_inrow_length`, `system_type_id`, precision, scale, code page, `is_nullable`, `is_dropped`, `is_uniqueifier`, `has_default`, `default_value`, and the clustered key ordinal of the column when it belongs to the key. | nothing |
| `SchemaProvider` (outside `Decoding/`) | `AllocUnitId` to `TableSchema`. Reads `sys.allocation_units`, `sys.partitions`, `sys.system_internals_partition_columns`, `sys.columns` (left join on `partition_column_id = column_id`: dropped columns and the uniqueifier have no row there), `sys.index_columns`, and `COLLATIONPROPERTY(collation_name, 'CodePage')`. Cached per allocation unit for the session. On an offline schema connection, checks that the login is sysadmin or `db_owner` of the database, and says so when it is not: a partly privileged login sees a filtered catalog without any error. The live connection is sysadmin anyway (§1.3). | SQL connection |
| `RowDecoder` | `byte[]` plus optional `TableSchema` to `DecodedRow` (§5). Pure function. | `TypeDecoders` |
| `KeyDecoder` | The clustered key of a row: from a data row image (through `RowDecoder`) and `TableSchema`, or from the index record in `RowLog Contents 2` of a modify record, whose own layout (§2) it walks itself: fixed key columns from offset 1 in key order, then column count, null bitmap, variable key columns. Returns the canonical key (§7.1). Pure function. | `RowDecoder` |
| `TypeDecoders` | Bytes of one column to a .NET value, per SQL type. `char` and `varchar` through `Encoding.GetEncoding(codePage)`, with `CodePagesEncodingProvider` registered at start-up; `nchar` and `nvarchar` as UTF-16LE whatever the collation. | nothing |
| `ModifyDecoder` | One modify record to a list of fragments `(offset, before bytes, after bytes)`: one fragment for `LOP_MODIFY_ROW`, several for `LOP_MODIFY_COLUMNS` (§6.2). Pure function. | nothing |
| `RowReplayer` | Rebuilds the full row before and after a modify record from an anchor (§7). | cache, `RowDecoder`, `KeyDecoder`, `ModifyDecoder` |
| `CurrentRowReader` (outside `Decoding/`) | Live mode: reads the current bytes of a row and the log written since the import (§7.2). | SQL connection |
| Records grid and details inspector (UI) | The main window has no grid of records yet; one is added, paged with the existing `LogQuery.GetPageAsync`. Selecting a record fills the inspector, two tabs "Values" and "Bytes" (§9). | `DecodedRow` |

`DecodedRow` is a list of `DecodedField(int Offset, int Length, FieldRole Role, string? Column, object? Value, string? Problem)`. `FieldRole` covers: status bits A, status bits B, fixed-data end offset, fixed column, column count, null bitmap, variable column count, variable offset entry, variable column, LOB pointer, versioning tag, unused bytes, unknown trailer, malformed.

---

## 5. Row decoding

The decoder applies to `RowLog Contents 0` of `LOP_INSERT_ROWS` and `LOP_DELETE_ROWS` whose context is `LCX_HEAP`, `LCX_CLUSTERED` or `LCX_MARK_AS_GHOST`, and whose record type (bits 1 to 3 of the first byte) is 0, a primary record. When the `AllocUnitId` resolves through `SchemaProvider`, it must also be an `IN_ROW_DATA` allocation unit of index id 0 or 1. Any other record, including a nonclustered index record (type 3) and a forwarded record (type 1), is shown as raw bytes with its record type named. This record-type rule is what keeps index records out when no schema is available (offline without a schema connection).

The other `RowLog Contents` of an insert or a delete are never decoded as rows: `RowLog Contents 2` of an ordinary insert is lock and index metadata (§2), and the rows an engine operation packs into one insert are out of scope (§1.2). A record whose transaction name (`LOP_BEGIN_XACT` of the same `Transaction ID`, when it is in the cache) is a system transaction name such as `SplitPage` is labelled "row moved by the engine", not shown as a user insert or delete. When the begin record is outside the window, no label is given.

Walk, in order:

1. Status bits A (1 byte) and status bits B (1 byte). In A: bits 1 to 3 are the record type, bit 4 says a null bitmap is present, bit 5 that variable columns are present, bit 6 that a versioning tag is present. The decoder follows the flags, not the schema.
2. Fixed-data end offset `F` (2 bytes, little-endian).
3. At `F`: column count `N` (2 bytes), then the null bitmap, `ceil(N / 8)` bytes. The bitmap is read before any fixed column, because a `NULL` fixed column keeps stale bytes (§2).
4. Columns are taken in `leaf_null_bit` order; the column of null bit `i` is present in the row when `i <= N`. Present fixed columns are read at their `leaf_offset`, skipped when their null bit is set. A `bit` column reads the byte at its `leaf_offset` and masks `leaf_bit_position`. Dropped columns are never read: their bytes are marked "dropped column", and a column added later may reuse the same offset. Bytes no column claims are marked unused.
5. If bit 5 of A is set: variable column count `V` (2 bytes) at `F + 2 + ceil(N / 8)`, then `V` end offsets of 2 bytes each. If bit 5 is clear, `V` is 0 and the row has no variable area.
6. Variable data. The first variable column starts at `F + 2 + ceil(N / 8) + 2 + 2V`; each next one starts at the previous end offset. An end offset is `entry & 0x7FFF`; the high bit `0x8000` marks an off-row pointer (LOB or row-overflow), annotated as such. A variable column is `NULL` when its null bit is set, whatever its length; a zero length with a clear null bit is an empty value. The order of variable columns is the order of their negative `leaf_offset` (-1, -2, ...).
7. If bit 6 of A is set: the 14 bytes that follow the variable data (or the null bitmap when `V` is 0) are the versioning tag. Any byte after the tag is an unknown trailer (ADR, §1.2).

Missing columns. A column whose `leaf_null_bit` is greater than `N` was added after the row was written: it takes its `default_value` when `has_default` is set, `NULL` otherwise, and is labelled "added after the row was written". Its `leaf_offset` is never read: it may point past the end of the row. Trailing variable columns may also be absent from the offset array, or the whole variable area may be absent (bit 5 clear): such a column is `NULL` when its null bit is set and an empty value otherwise (a row `(3, '', N'')` has no variable area at all). Neither case is malformed. A present variable column of zero length whose type has a fixed size (the uniqueifier of the first row of a key) is 0 for the uniqueifier; for any other column, bytes that do not fit the column's type make a malformed field, never an exception.

Without a schema, steps 1, 2, 3, 5, 6 and 7 still run: the "Bytes" view shows the structure with unnamed fields, and the fixed area as one block.

---

## 6. Modify decoding

### 6.1 `LOP_MODIFY_ROW`

One fragment: before = `RowLog Contents 0`, after = `RowLog Contents 1`, at `Offset in Row`; the before fragment is `Modify Size` bytes long, the after fragment has its own length. A compensation record (`Description` = `COMPENSATION`) has an empty before fragment: it replaces `Modify Size` bytes with its after bytes.

Attribution to columns is done on full rows whenever the replay (§7) provides them: decode the row before and the row after with `RowDecoder` and compare them column by column. This covers every shape of fragment, including a fragment starting at offset 0 that rewrites the whole row (a column set to `NULL`, §2).

Without a full row, the fragment is attributed from the schema alone, and only when it falls entirely inside one fixed column's bytes or one bit byte: the column is named, and its before and after values are given when the fragment covers the whole column (or, for a bit byte, the bits that changed). Any other fragment is shown as annotated bytes.

### 6.2 `LOP_MODIFY_COLUMNS`

Established on the four versions by the plan's task 4 and written in `docs/research/rowlog-layouts.md` (rule A), which is the reference for the decoder. In short: the binary `Log Record` has a header whose length is the `u16` at offset 2, then the element count (`Num Elements`), the element lengths (`u16`), and the elements, each starting on a multiple of 4; the padding is not reliably zero, on any version. With n = (count - 4) / 2 fragments: element 0 holds n pairs `(before offset, running offset)`, element 1 the n replaced lengths, element 2 the clustered key (§2), element 3 the lock information, and elements 4 + 2i and 5 + 2i the before and after bytes of fragment i. Fragment i replaces its replaced length at its running offset with its after bytes; fragments are applied in order and undone in reverse order. In a compensation record the before elements are empty.

`RowLog Contents 0` to `5` alone do not fix the length of the last after fragment: `RowLog Contents 4` and `5` run to the end of the record, padding included, and rows carrying an ADR payload give no other way to find it. The decoder therefore reads `Log Record`, which the import keeps for these records only (§3). A record without it (imported before this version) or whose elements do not add up is "layout unknown": the "Bytes" tab shows it raw, and the replay treats it as a break (§7.4).

---

## 7. Replay

Replay needs a schema (§4): without one, a modify record is shown as its fragment only.

### 7.1 Row identity

Clustered table: a row is identified by `(AllocUnitId, key)`, where the key is the canonical clustered key read from the log itself by `KeyDecoder`: from the row image of an insert or a delete, from `RowLog Contents 2` of a modify record (§2). The canonical key is, for each key column in key order, its null bit and, when it is not `NULL`, its stored bytes with their length; a `bit` key column contributes only its own bit (`leaf_bit_position`); then the uniqueifier as a 4-byte integer, 0 when the data row has none. The bytes of a `NULL` key column are never compared: they are stale in a data row and zero in an index record (§2). Stored bytes are compared, not values: a key's stored bytes do not change for the life of the row, and comparing values would need collation rules.

Neither page and slot nor `Lock Information` are an identity (§2): slots move, a row moves to another page on a split, a statement can take no row lock, and the lock hash collides.

Heap: a row has no key. It is identified by `(AllocUnitId, PageId, SlotId)` between an insert and a delete at that address: a slot freed by a delete is reused (§2), so a chain on a heap never crosses an `LOP_DELETE_ROWS` or an `LOP_INSERT_ROWS` at its address.

`RowKey` is filled per allocation unit before each chain is built, for every data record of that allocation unit whose `RowKey` is still `NULL` (records added by §7.2 included): `KeyDecoder` reads its key and stores it as hexadecimal text. Compensation records carry no key (§2) and are handled by transaction (§7.3), not by key. Any other record whose key cannot be decoded takes part in no chain, which is reported for the selected record ("key not decodable").

A clustered key update is logged as a delete and an insert: it ends one row and starts another, which is what it is.

### 7.2 Anchors

A chain needs one full image of the row at a known LSN. Three anchors, tried in this order:

1. Insert: the latest `LOP_INSERT_ROWS` of the row before the selected record. Replay runs forwards.
2. Delete: the first `LOP_DELETE_ROWS` of the row after the selected record. Its image is the row after its last modification. Replay runs backwards.
3. Current row: the row as it is now in the database. Replay runs backwards, from now to the selected record. Live mode only, clustered tables only (a heap has no key to seek on, and a heap predicate on `%%physloc%%` scans the table), and not on a database where `sys.databases.is_accelerated_database_recovery_on` is 1 (the page can hold aborted bytes, §2).

Anchor 3 reads the server, so it is not tried on selection: the inspector offers "Rebuild from the current row" as an explicit action. Each run executes these steps on one connection, inside one transaction at `SERIALIZABLE` isolation with `SET LOCK_TIMEOUT 5000`, and any failed step ends it with a message naming the step:

1. Seek the row on its decoded key and hold it: `SELECT sys.fn_PhysLocFormatter(%%physloc%%) FROM <table> WITH (ROWLOCK, HOLDLOCK) WHERE <predicate>`, where each key column gives `<column> = <value>`, or `<column> IS NULL` when its null bit is set. From here to the commit, no writer can change the row, and a writer already holding it makes the seek time out ("row locked by an open transaction"), so uncommitted bytes of an open transaction are never read. A non-unique clustered index returns one row per uniqueifier; each slot is read in step 4 and the one whose key matches is kept.
2. Read the log written since the last record of the cache: `fn_dblog('<a:b:c>', NULL)` with that LSN in decimal form (§2). The first record returned must be that last record; otherwise the log was truncated since (a log backup) and the anchor is unavailable ("log truncated since the import, re-import to use the current row"). The records read are added to the cache like any import. Because the row is held since step 1, every modification of the row is now in the cache.
3. If the cache holds a delete of the row after the selected record, anchor 2 applies instead.
4. `DBCC PAGE (<db>, <file>, <page>, 1) WITH TABLERESULTS`, and read the memory dump of the slot.
5. Commit, then check that the bytes are a primary record (type 0) whose canonical key equals the row's key. Otherwise the anchor is unavailable ("current row is not this row").

### 7.3 Applying a modification

Transactions are classified from the cache:

- rolled back: the transaction has an `LOP_ABORT_XACT`;
- partly rolled back: it commits, and holds a compensation of a row operation (`LOP_INSERT_ROWS`, `LOP_DELETE_ROWS`, `LOP_MODIFY_ROW`, `LOP_MODIFY_COLUMNS` with `Description` = `COMPENSATION`; under ADR, `LOP_CTR_NEST_ABORT` and `LOP_LOCK_XACT` also carry that description and are not row compensations) or an `LOP_CTR_NEST_ABORT` (a rollback to a savepoint, or a statement rolled back inside the transaction; under ADR there is no compensation record to apply, §2);
- committed: it has an `LOP_COMMIT_XACT` and none of the above;
- unknown: neither its commit nor its abort is in the window.

A rolled-back transaction's records, compensation records included, are left out of every chain: their net effect on the row is nothing. When the selected record itself belongs to a rolled-back transaction, the chain is built without the other rolled-back transactions and stops at the selected record, so the user sees what the change did before it was undone, labelled "rolled back". If that transaction also holds, before the selected record, a compensation of a row operation or an `LOP_CTR_NEST_ABORT` (a savepoint rolled back first), the result is "partial rollback before the selected record, not replayed": the records it undid would otherwise be replayed as if they had stood.

A record of a partly rolled-back transaction ends the chain at that record ("partial rollback at LSN x, not replayed"): which of its changes survived cannot be told from the row's records alone. Under ADR the undone bytes also leak into later records of the row (§2). An ADR revert is a transaction that holds an `LOP_CTR_NEST_ABORT`, or that is rolled back whole, and logs no compensation of a row operation for the row: its undone bytes stay in the page and reach later records. Whatever its outcome, committed or rolled back, when one of its records is anywhere in the row's chain, before or after the selected record, every replay of that row stops with the status "ADR revert in the chain, not replayed", and a delete image of the row is shown with the warning "may hold values undone by a partial rollback". Without ADR the compensation records restore the bytes, and stopping at the record is enough.

Records of a transaction of unknown outcome are applied and the result is labelled "includes changes of unknown outcome".

Forwards, for each fragment: check that the current row holds the before bytes at the fragment's offset, then replace `len(before)` bytes at that offset with the after bytes.

Backwards: check that the current row holds the after bytes at the offset, then replace `len(after)` bytes with the before bytes. For `LOP_MODIFY_COLUMNS`, the fragments are undone in reverse order.

When a check fails, replay stops and reports "chain broken at LSN x". The check is weak, because only the bytes that differ are logged (one byte, sometimes one bit). It catches gross errors, not a wrong row. That is why identity comes from the key and not from the check.

Every modify record shown, selected or replayed, carries the classification of its transaction.

### 7.4 Ends of a chain

- `LOP_DELETE_ROWS` of the row ends it forwards; `LOP_INSERT_ROWS` ends it backwards. A delete also separates two lives of a key: a key deleted and inserted again can be logged, under ADR, as an `LOP_MODIFY_ROW` on the ghost record rather than an insert, so the first record of the key after a delete is the start of a new life. Backwards, the chain stops there with "row inserted again after a delete, no image before it", and no record before that delete is ever replayed into the new life. A page split's moved row is logged as an insert (§2): it ends a backward chain at the split, with a correct image, so the result stays correct and only starts later.
- A heap modification whose after bytes are a forwarding stub (record type 2) ends it with "row forwarded, not followed".
- A modify record whose layout is unknown (§6.2) ends it with "modification not decodable at LSN x".
- A record of a partly rolled-back transaction ends it (§7.3).

A change of `AllocUnitId` (index rebuild) separates two chains of the same key: a rebuild in the window splits the history, and records from before the rebuild no longer resolve against a live catalog (§1.3).

When no anchor is found, the result is "no full image of this row available", and the view falls back to the fragment alone (§6.1).

---

## 8. Errors

The decoder never throws towards the UI. It always returns a `DecodedRow`, possibly containing fields with role malformed and a `Problem` text:

- row shorter than an offset it announces;
- an end offset (after masking `0x7FFF`) beyond the end of the row, or decreasing;
- column count greater than the schema's (a schema from another database);
- fixed-data end offset different from what the schema predicts for this column count.

A column count smaller than the schema's is not an error (§5). These problems are visible in both tabs. Errors surfaced as errors: a failure to connect when reading the schema, a login that is neither sysadmin nor `db_owner` (§4), and a failure of a statement of §7.2, with the statement that failed.

---

## 9. User interface

Decoding happens on demand, when a record is selected in the grid, not during import, so import speed is unchanged. The grid is a new tab "Records" next to the statistics tabs, paged with `LogQuery.GetPageAsync`, ordered by `LSN`.

The details inspector (spec01 §6.1) has two tabs:

- "Values": for an insert or a delete, the decoded columns. For a modify, the touched columns with before and after, the outcome of its transaction, and, when the replay succeeded, the full row before and after with the changed columns highlighted. The replay status is shown in one line ("rebuilt forwards from INSERT at LSN x", "rebuilt backwards from DELETE at LSN x", "rebuilt backwards from the current row", "chain broken at LSN x", "no full image of this row available"). In live mode, the button "Rebuild from the current row" (§7.2) is shown when anchors 1 and 2 failed.
- "Bytes": the hex dump of every non-empty `RowLog Contents` of the record, each field of a decoded row in its own color; hovering a field shows its role, column and value. Contents that are not decoded are shown as plain hex.

In offline mode without a schema connection, "Bytes" still shows the structure and "Values" says that a schema connection is required. User-facing text is in English (spec01 §6).

---

## 10. Tests

### 10.1 Frozen fixtures

A script starts SQL Server 2017, 2019, 2022 and 2025 under podman (images already present on the development machine), creates tables covering every type of §1.1, inserts known values, updates and deletes them, and freezes, per version, as JSON under `tests/`:

- the `fn_dblog` rows involved, with every column of §3 and `Log Record`;
- the `TableSchema` of each allocation unit;
- for some rows, the output of `DBCC PAGE (…, 1)` and `DBCC PAGE (…, 3)`.

`Log Record` is not used by part A. It is captured now because it is the raw material of part B.

Unit tests decode the fixtures without SQL Server. Expected values are the values the script inserted, written in the test, never an output of the decoder copied back; bytes no column claims are never compared (§2). `DBCC PAGE (…, 3)` gives SQL Server's own split of a row into columns, compared with the fields of `RowDecoder`; `DBCC PAGE (…, 1)` gives the bytes the replay must reach.

The script runs as an integration test (`IntegrationFact`, `SQLLOGEXPLORER_RUN_INTEGRATION=1`) and removes its containers when it ends, including on failure.

### 10.2 Cases

Rows:
- isolated `NULL`, a `NULL` fixed column with stale bytes, a row with every nullable column `NULL`;
- a row with no variable column present (bit 5 clear);
- several `bit` columns in one byte;
- empty string versus `NULL`, in a non-trailing and a trailing variable column;
- `time` and `datetime2` at precisions 0, 3 and 7; `datetimeoffset`;
- `decimal` at each storage size (5, 9, 13, 17 bytes);
- non-ASCII Unicode; `varchar` under a non-Latin collation; a `_UTF8` collation (2019 and later, it does not exist in 2017);
- a dropped column; a column added with a default after rows were written, then deleted; a non-unique clustered index (uniqueifier); a heap;
- a `varchar(max)` value pushed off-row, and a row-overflow `varchar(5000)` pair;
- a table with a nonclustered index, whose index records must not reach the row decoder, with and without a schema;
- RCSI on (versioning tag); ADR on (2022 and 2025), tag then trailer.

Keys:
- an `int` key, a composite `varchar` + `int` key, the colliding pair `('A', 'BCD')` and `('AB', 'CD')`, a non-unique clustered key, each key type of §1.1 that can be a key;
- the same key read from an insert image and from a modify's `RowLog Contents 2` must compare equal.

Replay:
- forwards: insert, several updates on fixed, bit and variable columns, a column set to `NULL`;
- a nullable key column holding `NULL`, the pair `('x', NULL)` and `('x', 0)`, and a `bit` key column next to non-key bits that change;
- a partial rollback (savepoint) with and without ADR; a rolled-back insert and a rolled-back delete; the selected record inside a rolled-back transaction;
- LSNs from 2017 (lowercase) and 2022 (uppercase) in one ordering test, and the decimal LSN form passed to `fn_dblog`;
- an update under `TABLOCKX` between two ordinary updates, which must be in the chain;
- backwards from a delete; backwards from the current row, with modifications after the import;
- the current row after a log backup taken since the import (anchor unavailable), with an open transaction on the row (anchor unavailable), with an update committed by another session just before the action (it must be in the chain), and on an ADR database (anchor not offered);
- slots that move: insert 1, 2, then 0, then update 1, which must rebuild `id = 1`;
- a page split between the insert and the update;
- a rolled-back update in the middle of a chain, forwards and backwards;
- an index rebuild in the middle of a chain;
- a heap slot reused by another row after a delete; a heap row that becomes forwarded;
- the same `.trn` imported twice; two `.trn` files imported in reverse order.

---

## 11. Open questions

1. The `LOP_MODIFY_COLUMNS` layout (§6.2), and whether it is the same on the four versions.
2. The ADR trailer: what it contains and whether it is worth decoding beyond "unknown trailer".
3. The exact layout of the index record in `RowLog Contents 2` for every key type of §1.1 (measured for `int`, `varchar`, `varchar` + `int`, non-unique `int`).
4. The system transaction names that move rows (`SplitPage` was measured; others to list from the fixtures).
