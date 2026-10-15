# Functional Specification 03: RowLog Contents Decoder

Status: design approved in conversation on 2026-10-15, to be reviewed before planning.
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

- Full row images of `LOP_INSERT_ROWS` and `LOP_DELETE_ROWS` on data rows (heap and clustered index leaf).
- Deltas of `LOP_MODIFY_ROW` and `LOP_MODIFY_COLUMNS` on the same rows, attributed to the columns they touch.
- Reconstruction of the full row before and after an update, by replaying the log already loaded in the cache (§7).
- Types: `tinyint`, `smallint`, `int`, `bigint`, `bit`, `decimal`, `numeric`, `money`, `smallmoney`, `real`, `float`, `date`, `time`, `datetime`, `smalldatetime`, `datetime2`, `datetimeoffset`, `uniqueidentifier`, `char`, `varchar`, `nchar`, `nvarchar`, in-row `binary` and `varbinary`.
- SQL Server 2017, 2019, 2022 and 2025.

### 1.2 Out of scope

- `ROW` and `PAGE` compression, `sparse` columns, columnstore.
- LOB and row-overflow values: the in-row pointer is annotated as a pointer, the value is not followed.
- Nonclustered index records (`LCX_INDEX_LEAF`, `LCX_INDEX_INTERIOR`): shown as raw bytes only.
- Schema changes between the time the log was written and the time the schema is read (detected where possible, §8, never corrected).
- Reconstructing a row backwards from the current page with `DBCC PAGE` (needs sysadmin and a page that has not moved; deferred).

An unsupported type is never an error: its bytes are shown in hex with the label "type not decoded".

---

## 2. Observations from a probe

Measured on 2026-10-15 on the `mcr.microsoft.com/mssql/server:2022-latest` image, with a table `t(id int not null primary key, a int null, b varchar(20) null, c bit, d bit)` and a table `u(id int not null primary key, a int null, x char(50) null, b varchar(20) null, c bit, d bit)`. These are observations to build fixtures on, not guarantees for the other versions.

- `fn_dblog` exposes, among others: `AllocUnitId`, `PartitionId` (no space), `Page ID` (format `0001:00000150`), `Slot ID`, `Offset in Row`, `Modify Size`, `Num Elements`, `RowLog Contents 0` to `RowLog Contents 5`, `Rowbits First Bit`, `Rowbits Bit Count`, `Rowbits Bit Value`, and `Log Record` (the whole binary log record).
- `LOP_INSERT_ROWS` on `t` with `(1, 10, 'abc', 1, 0)`: `RowLog Contents 0` = `0x30000D00010000000A0000000505000001001700616263`. Status bits `0x30`, `0x00`; fixed data ends at `0x000D`; `id` = 1, `a` = 10; one byte holding the two bits = `0x05`; column count 5; null bitmap `0x00`; one variable column ending at offset `0x0017`; data `abc`.
- The bit byte is `0x05` for `c = 1, d = 0`, and `0xF2` then `0xF3` on `u` when `c` goes from 0 to 1. Bits that belong to no column are not zero. The decoder must mask the declared bits and ignore the others.
- `LOP_MODIFY_ROW` changing `a` from 10 to 11: `Offset in Row` = 8, `Modify Size` = 1, `RowLog Contents 0` = `0x0A`, `RowLog Contents 1` = `0x0B`. SQL Server logs only the bytes that differ, so a fragment can be smaller than the column.
- `LOP_MODIFY_ROW` changing `b` from `abc` to `abcdef`: `Offset in Row` = 18, `Modify Size` = 5, before = `0x1700616263`, after = `0x1A00616263646566`. The fragment spans the last offset-array entry and the variable data, and the after fragment is longer than `Modify Size`.
- `LOP_DELETE_ROWS` arrives with context `LCX_MARK_AS_GHOST` and the full row image in `RowLog Contents 0`.
- `LOP_MODIFY_COLUMNS` on `u` (`a` 10 to 99, `b` `abc` to `zz`): `Num Elements` = 8, `RowLog Contents 0` = `0x0800080044004400`, `RowLog Contents 1` = `0x01000500`, `RowLog Contents 4` = `0x0A00000063000000490061626300000048007A7A`. The before and after values of `a` are visible in `RowLog Contents 4`; the meaning of the other bytes is not established yet (§6.2).
- The same query also returns `LOP_MODIFY_ROW` records with context `LCX_PFS` on allocation pages. Those must never reach the row decoder.

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
| `RowLog Contents 2` to `4` | `RowLogContents2` to `4` | BLOB |

A new index `(PageId, SlotId)` serves the replay (§7). The cache is a disposable file rebuilt at each import, so there is no migration: the `CREATE TABLE` changes.

Whether `fn_dump_dblog` exposes the same column names as `fn_dblog` on all four versions is to be checked by an integration test before the backends depend on it.

---

## 4. Components

All decoding code lives in a `Decoding/` folder that references neither SQL client nor UI, so that part B can reuse it.

| Component | Responsibility | Depends on |
|---|---|---|
| `TableSchema` | Columns of one partition: name, `partition_column_id`, `leaf_offset`, `leaf_null_bit`, `max_inrow_length`, type, precision, scale, collation, `is_nullable`, `is_dropped`, `is_uniqueifier`. | nothing |
| `SchemaProvider` (outside `Decoding/`) | `PartitionId` to `TableSchema`, read from `sys.system_internals_partition_columns` joined to `sys.partitions` and `sys.columns`. Cached per partition for the session. | SQL connection |
| `RowDecoder` | `byte[]` plus optional `TableSchema` to `DecodedRow`. Pure function. | `TypeDecoders` |
| `TypeDecoders` | Bytes of one column to a .NET value, per SQL type. | nothing |
| `DeltaDecoder` | A modify record plus `TableSchema` to `DecodedDelta`: the columns the fragment touches, with before and after values when they can be decoded, annotated bytes otherwise. | `RowDecoder` |
| `RowReplayer` | Rebuilds the full row before and after a modify record from the cache (§7). | cache, `RowDecoder`, `DeltaDecoder` |
| Details inspector (UI) | Two tabs for the selected record, "Values" and "Bytes" (§9). | `DecodedRow`, `DecodedDelta` |

`DecodedRow` is a list of `DecodedField(int Offset, int Length, FieldRole Role, string? Column, object? Value, string? Problem)`. `FieldRole` covers: status bits A, status bits B, fixed-data end offset, fixed column, column count, null bitmap, variable column count, variable offset entry, variable column, LOB pointer, unused bytes, malformed.

Joining `sys.system_internals_partition_columns` to `sys.columns` is not a plain `partition_column_id = column_id` join in general (dropped columns, uniqueifier, index key reordering). The plan must establish the join against fixtures, including a table with a dropped column and a non-unique clustered index.

---

## 5. Row decoding

The decoder applies to records whose operation is `LOP_INSERT_ROWS` or `LOP_DELETE_ROWS` and whose context is `LCX_HEAP`, `LCX_CLUSTERED` or `LCX_MARK_AS_GHOST`, and whose `AllocUnitId` belongs to an `IN_ROW_DATA` allocation unit. Any other record is shown as raw bytes.

Walk, in order, each step producing one or more fields:

1. Status bits A (1 byte) and status bits B (1 byte). Bit 4 of A says whether a null bitmap is present, bit 5 whether variable columns are present. The decoder follows the flags, not the schema.
2. Fixed-data end offset (2 bytes, little-endian).
3. Fixed columns, at their `leaf_offset`. Several `bit` columns share one byte; which catalog column gives each one's bit position is to be established on fixtures, and the decoder masks that bit only. Bytes that no column claims are marked unused.
4. Column count (2 bytes), then a null bitmap of `ceil(count / 8)` bytes.
5. Variable column count (2 bytes), then one 2-byte end offset per variable column. The high bit of an entry marks an off-row (LOB or row-overflow) pointer.
6. Variable data, each column from the previous end offset to its own.

Trailing variable columns that are `NULL` are absent from the offset array; the decoder reports them as `NULL` when the schema has more variable columns than the row. A row whose column count differs from the schema's is decoded as far as it goes, and the difference is reported (§8).

Without a schema, steps 1, 2, 4, 5 and 6 still run: the "Bytes" view shows the structure with unnamed fields.

Text decoding uses the code page of the column collation for `char` and `varchar`, UTF-16LE for `nchar` and `nvarchar`. A UTF-8 collation (`_UTF8`) on `char`/`varchar` decodes as UTF-8.

---

## 6. Delta decoding

### 6.1 `LOP_MODIFY_ROW`

`RowLog Contents 0` holds the bytes before, `RowLog Contents 1` the bytes after, starting at `Offset in Row`. The decoder maps the byte range `[Offset in Row, Offset in Row + Modify Size)` onto the row layout:

- Fully inside one fixed column: the column is named, and its before and after values are decoded when the fragment covers the whole column. When it covers only part of it (observed: 1 byte of an `int`), the value can only be decoded with the rest of the row, which the replay provides (§7); without replay, the fragment is shown as bytes of that column.
- Inside a bit byte: the columns whose bits changed are named, with their values.
- Over the offset array or the variable data: the decoder needs the row layout to say which column it is, which again comes from the replay; without it, the fragment is shown as annotated bytes ("variable area, offset 18").

### 6.2 `LOP_MODIFY_COLUMNS`

The layout of this record is not documented and is not established by the probe. What the probe shows (§2) is consistent with: `RowLog Contents 0` an array of 2-byte offsets, `RowLog Contents 1` an array of 2-byte lengths, `RowLog Contents 4` the before and after fragments concatenated. This is a hypothesis.

The plan starts this part with an R&D task whose output is a short written note of the layout, established on fixtures covering: two fixed columns, one fixed and one variable, two variable columns, a change in length, and the four versions. Code that decodes `LOP_MODIFY_COLUMNS` is written only after that note exists. If the layout cannot be established, these records are shown as annotated raw bytes and the spec says so.

---

## 7. Replay

For a modify record on (`PageId`, `SlotId`):

1. Read from the cache the records on the same page and slot with an LSN lower than or equal to the selected one, in LSN order.
2. Find the latest full image: an `LOP_INSERT_ROWS`. The row is now known.
3. Apply each following modify record in order. Before applying, check that the before bytes of the record equal the current bytes of the row at its offset. Apply by replacing the `Modify Size` bytes at `Offset in Row` with the after bytes, whose length may differ (§2: the row grows when a variable column grows).
4. Stop at the selected record. The row before and after it are the two results.

When the check in step 3 fails, replay stops and reports "chain broken at LSN x". This is what happens when a slot is reused after a delete, or when the page was reorganized or split: the replay never shows a row it cannot vouch for. When no full image is found, the result is "original image outside the loaded window" and the view falls back to the delta alone (§6).

A delete (`LOP_DELETE_ROWS`) ends a chain. An insert on the same slot after it starts a new one.

Replay also feeds §6.1: a fragment that covers part of a column, or the variable area, is decoded on the reconstructed row.

---

## 8. Errors

The decoder never throws towards the UI. It always returns a `DecodedRow`, possibly containing fields with role malformed and a `Problem` text:

- row shorter than an offset it announces;
- offset array entry beyond the end of the row, or decreasing;
- column count different from the schema's, which usually means the schema changed after the log was written;
- fixed-data end offset different from what the schema predicts.

These problems are visible in both tabs. The only error surfaced as an error is a failure to connect when reading the schema.

---

## 9. User interface

Decoding happens on demand, when a record is selected in the grid, not during import, so import speed is unchanged.

The details inspector (spec01 §6.1) has two tabs:

- "Values": for an insert or a delete, the decoded columns. For a modify, the touched columns with before and after, and, when the replay succeeded, the full row before and after with the changed columns highlighted. The replay status is shown in one line ("rebuilt from INSERT at LSN x", "chain broken at LSN x", "original image outside the loaded window").
- "Bytes": the hex dump of `RowLog Contents 0` (and `1` for a modify), each field in its own color, hovering a field shows its role, column and value.

In offline mode without a schema connection, "Bytes" still shows the structure and "Values" says that a schema connection is required. User-facing text is in English (spec01 §6).

---

## 10. Tests

### 10.1 Frozen fixtures

A script starts SQL Server 2017, 2019, 2022 and 2025 under podman (images already present on the development machine), creates tables covering every type of §1.1, inserts known values, updates and deletes them, and freezes, per version, as JSON under `tests/`:

- the `fn_dblog` rows involved, with every column of §3 and `Log Record`;
- the `TableSchema` rows of each partition;
- for some rows, the output of `DBCC PAGE (…, 3)`.

`Log Record` is not used by part A. It is captured now because it is the raw material of part B, and fixtures with known content cost nothing to extend today.

Unit tests decode the fixtures without SQL Server. Expected values are the values the script inserted, written in the test, never an output of the decoder copied back. The `DBCC PAGE` output gives SQL Server's own split of the row into columns, which the test compares with the fields of `RowDecoder`.

The script runs as an integration test (`IntegrationFact`, `SQLLOGEXPLORER_RUN_INTEGRATION=1`) and removes its containers when it ends, including on failure.

### 10.2 Cases

- isolated `NULL`, row with every nullable column `NULL`;
- several `bit` columns in one byte, and the non-zero unused bits observed in §2;
- empty string versus `NULL`;
- trailing `NULL` variable columns absent from the offset array;
- `time` and `datetime2` at precisions 0, 3 and 7; `datetimeoffset`;
- `decimal` at each storage size (5, 9, 13, 17 bytes);
- non-ASCII Unicode; `varchar` under a non-Latin collation; a `_UTF8` collation;
- a dropped column; a non-unique clustered index (uniqueifier); a heap;
- a `varchar(max)` value pushed off-row, to check the pointer is recognized and not decoded as data;
- replay: a full chain (insert, several updates, delete), a chain whose insert is outside the window, a slot reused after a delete that must report "chain broken".

---

## 11. Open questions

1. Permissions: `sys.system_internals_partition_columns` needs `VIEW DEFINITION` at least. The plan should check what a non-sysadmin login with only `VIEW SERVER STATE` (what `fn_dblog` needs) can read, and what the UI says when the schema is not readable.
2. `fn_dump_dblog` column names on the four versions (§3).
3. `LOP_MODIFY_COLUMNS` layout (§6.2).
4. Whether `Slot ID` is stable enough across a page split for the replay to be worth more than its failure rate; the fixtures should include one split to measure it.
