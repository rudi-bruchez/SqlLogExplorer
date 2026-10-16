# Decoding and reconstruction

This page explains, for a DBA, what SqlLogExplorer decodes in a log record, how it rebuilds the full row around an `UPDATE`, and what each message means when it refuses. The format of `RowLog Contents` is undocumented: everything here was measured on SQL Server 2017, 2019, 2022 and 2025. The measurements behind it are in `specs/spec03.md` (section 2) and `docs/research/rowlog-layouts.md`; the code is in `Decoding/` and `Services/DecodingService.cs`.

The guiding rule: when the application cannot prove a value, it says so instead of showing a plausible guess.

## Records that are decoded

Four operations are decoded:

| Operation | What is decoded |
|---|---|
| `LOP_INSERT_ROWS` | The full row image in `RowLog Contents 0`: the row as inserted. |
| `LOP_DELETE_ROWS` | The full row image in `RowLog Contents 0`: the row as it was when deleted. |
| `LOP_MODIFY_ROW` | The changed bytes, and the full row before and after when it can be rebuilt. |
| `LOP_MODIFY_COLUMNS` | The same, for an update logged as several changes in one record. |

A record is decoded as a data row only when:

- its context is `LCX_HEAP`, `LCX_CLUSTERED` or `LCX_MARK_AS_GHOST`;
- when a schema is available, its allocation unit is the in-row data (`IN_ROW_DATA`) of a heap (index id 0) or of a clustered index (index id 1);
- the record type in the first status byte is 0, a primary record.

Everything else is shown as bytes only: nonclustered index records (a delete logs one image per index, all with context `LCX_MARK_AS_GHOST`), forwarded records, ghost records, and every other operation (`LOP_BEGIN_XACT`, `LOP_COMMIT_XACT`, `LOP_LOCK_XACT`, `LOP_HK` and so on). For an insert or a delete whose record type is not 0, the `Bytes` tab marks the two status bytes and shows the rest as `Unused`, and `Values` stays empty.

Only `RowLog Contents 0` of an insert or a delete is decoded as a row. `RowLog Contents 2` of an ordinary insert holds lock and index metadata, not a row.

## Row images

A row image is read in the order SQL Server lays it out (the FixedVar format):

1. Status byte A and status byte B. In A, bits 1 to 3 give the record type, bit 4 says a null bitmap is present, bit 5 that variable-length columns are present, bit 6 that a 14-byte versioning tag follows the data.
2. The offset of the end of the fixed-length data (2 bytes).
3. At that offset, the column count `N` (2 bytes) and the null bitmap (`ceil(N / 8)` bytes).
4. The fixed-length columns, each at its `leaf_offset` from `sys.system_internals_partition_columns`. A `bit` column reads one bit of its byte, at its `leaf_bit_position`.
5. When bit 5 is set: the variable column count (2 bytes), one 2-byte end offset per variable column, then the variable data. The high bit `0x8000` of an end offset marks an in-row pointer to an off-row value.
6. When bit 6 is set: the versioning tag (row versioning, for RCSI or snapshot isolation). Any byte after it is shown as an unknown trailer: Accelerated Database Recovery (ADR) appends one.

The decoder follows the flags of the row, not the schema. It reads the null bitmap before any fixed column, because a `NULL` fixed column of an inserted row is not zeroed: it keeps whatever bytes were there, and only the bitmap says it is `NULL`. Without a schema the structure is still walked: the `Bytes` tab shows the status bytes, offsets, bitmap and variable columns, with the fixed data as one unnamed block.

Some columns get a note in `Values` instead of, or next to, their value:

| Note | Meaning |
|---|---|
| `added after the row was written` | The column's null bit is beyond the row's column count: the column was added (`ALTER TABLE … ADD`) after this row was written. The value shown is the column's default from the catalog, or `NULL` without one. This is not an error. |
| `off-row value not followed` | The value is stored off-row (LOB or row-overflow). Only its in-row pointer is in the row, shown as `LobPointer` in `Bytes`. |
| `sparse column not decoded` | Sparse columns live in a separate sparse vector, which the decoder does not read. |
| `type not decoded` | The column's type is not in the decoded list below. Its raw bytes are shown in hexadecimal. |
| `<n> bytes do not fit the column type` | The bytes found for the column have the wrong length or an impossible value for its type (for example a `datetime` time of a day or more). The field is marked `Malformed` in `Bytes`. |

Dropped columns keep their place in the row. Their bytes are marked `DroppedColumn` and they are not listed in `Values`. The uniqueifier of a non-unique clustered index is listed as a column named `uniqueifier` (0 for the first row of a key).

## Data types

Decoded types:

| Family | Types |
|---|---|
| Integers | `tinyint`, `smallint`, `int`, `bigint`, `bit` |
| Exact numerics | `decimal`, `numeric`, `money`, `smallmoney` |
| Approximate numerics | `real`, `float` |
| Date and time | `date`, `time`, `datetime`, `smalldatetime`, `datetime2`, `datetimeoffset` |
| Strings | `char`, `varchar` (with the code page of the column's collation), `nchar`, `nvarchar` (UTF-16) |
| Binary | `binary`, `varbinary` |
| Other | `uniqueidentifier` |

`varchar(max)`, `nvarchar(max)` and `varbinary(max)` values are decoded when they are stored in the row, and noted `off-row value not followed` when they are not. Every other type (for example `rowversion`, `sql_variant`, `xml`, `hierarchyid`, the spatial types, CLR types) is shown as raw bytes with the note `type not decoded`. An unsupported type is never an error: the other columns of the row are decoded as usual.

Dates and times are shown in ISO form at the column's scale, as SQL Server displays them; a `datetime` shows its 1/300 s rounded to the millisecond.

## Row identity

To rebuild an update, the application needs the history of one row: its insert, its updates and its delete, in LSN order. Neither the page and slot nor `Lock Information` identify a row in a clustered index: slots move as rows are inserted and ghosts cleaned up, a page split moves rows to another page, a statement under `TABLOCK` logs no row lock, and the 6-byte lock hash collides (`('A', 'BCD')` and `('AB', 'CD')` share one).

Clustered table. A row is identified by its allocation unit and its clustered key, read from the log itself:

- from the row image of an insert or a delete;
- from `RowLog Contents 2` of an update, which carries the clustered key as an index record (every update except compensation records).

The key compares the stored bytes of each key column, in key order, with its null bit, then the uniqueifier. Bytes are compared, not values, so no collation rule is involved, and a key's stored bytes do not change during the life of the row. The bytes of a `NULL` key column are never compared.

The key of every record of a table is computed the first time a record of that table is inspected, which is why the first selection can take a few seconds on a large import.

Heap. A heap row has no key. It is identified by its allocation unit, page and slot, between an insert and a delete at that address: a slot freed by a delete is reused by the next insert, so a heap history never crosses an insert or a delete.

An update of a clustered key is logged as a delete and an insert: it ends one row and starts another.

## Updates

An update record is decoded into fragments: each says "at this offset, these bytes became those bytes".

- `LOP_MODIFY_ROW` carries one fragment: `Offset in Row`, the bytes before in `RowLog Contents 0`, the bytes after in `RowLog Contents 1`. Only the bytes that differ are logged, so a fragment can be one byte of a four-byte `int`. A fragment can also rewrite the whole row (setting a variable column to `NULL` does).
- `LOP_MODIFY_COLUMNS` carries several fragments, applied in order. Their exact lengths are only reliable in the binary `Log Record` column, which the import keeps for these records only (see `docs/research/rowlog-layouts.md`, rule A).

A fragment alone rarely names a column. Without the full row, `Values` names a column only when the fragment lies entirely inside one fixed-length column or one byte of `bit` columns, and gives its values only when the fragment covers the whole column, with the note `from the fragment alone, NULL state unknown`. With the full row before and after, every column is decoded on both sides and compared.

Columns are compared by name, as text: a default read from the catalog (`7`) equals the integer 7. When one side was not read (off-row, sparse), the column is marked changed only if its in-row bytes differ, and left unknown otherwise.

## Transaction outcome

Every record with a transaction id gets the outcome of its transaction, read from the records of that transaction in the cache:

| Outcome | Rule |
|---|---|
| rolled back | The transaction has an `LOP_ABORT_XACT`. |
| partly rolled back | It commits, and it holds a compensation record of a row operation (`Description` = `COMPENSATION` on an insert, delete or modify) or an `LOP_CTR_NEST_ABORT` (a rollback to a savepoint, or a statement rolled back inside the transaction). |
| committed | It has an `LOP_COMMIT_XACT` and none of the above. |
| of unknown outcome | Neither its commit nor its rollback is in the imported range. |

The outcome is shown in the inspector title.

## Reconstruction anchors

A row's history needs one full image of the row at a known point, its anchor. Three anchors exist:

1. INSERT. The latest `LOP_INSERT_ROWS` of the row before the selected update, with no delete in between. The replay runs forwards, applying each update up to the selected one. Status: `rebuilt forwards from INSERT at LSN <x>`.
2. DELETE. The first `LOP_DELETE_ROWS` of the row after the selected update, with no insert in between. Its image is the row after its last change; the replay runs backwards, undoing each update down to the selected one. Status: `rebuilt backwards from DELETE at LSN <x>`.
3. Current row. The row as it is now in the database, read on request with the `Rebuild from the current row` button. The replay runs backwards from now. Status: `rebuilt backwards from the current row`.

The first two are tried on every selection, from the cache only, forwards first. The third reads the server and runs only when you click; it is offered only in live mode, on a clustered table, in a database without ADR, and when anchors 1 and 2 found no image. On a heap, seeking a row by its physical address would scan the table. Under ADR, `DBCC PAGE` can show bytes of a rolled-back change as if they stood (measured on 2022). The steps of anchor 3 and its own messages are in the [user guide](user-guide.md#rebuild-from-the-current-row).

Each step of the replay checks that the row holds the bytes the fragment expects (the bytes before, forwards; the bytes after, backwards). That check is weak, since a fragment can be a single byte. It catches gross errors, not a wrong row: identity comes from the key, not from the check.

The rules for transactions:

- A rolled-back transaction is left out of the history: its net effect on the row is nothing. Compensation records are never replayed.
- When the selected update itself belongs to a rolled-back transaction, the history keeps that transaction's records up to the selected one, so you see what the change did before it was undone. Such a change can only be rebuilt forwards: nothing after it in the log shows the row as it left it.
- A record of a partly rolled-back transaction stops the replay: which of its changes survived cannot be told from the row's records alone.
- Under ADR, a rollback (to a savepoint, or of the whole transaction) can log no compensation for the row: the undone bytes stay in the page and reach the following records of the row. Any such transaction in the row's history stops every replay of that row, whatever its outcome.
- A delete separates two lives of a key. Under ADR, a key deleted then inserted again can be logged as an update of the ghost record, not as an insert, so nothing before the delete is ever replayed into the new life.
- Updates of a transaction of unknown outcome are applied, and the result is noted `includes changes of unknown outcome`.

When the replay fails in both directions, the more specific reason is shown; between two specific reasons, the forward one.

When the replay succeeds, `Values` shows the full row before and after. When it fails, `Values` falls back to the fragment alone, as described in [Updates](#updates).

## Refusal and status messages

These are the texts the inspector can show, quoted from the code.

### Replay status

Shown on the status line of an update.

| Message | Meaning |
|---|---|
| `rebuilt forwards from INSERT at LSN <x>` | Success, from anchor 1. |
| `rebuilt backwards from DELETE at LSN <x>` | Success, from anchor 2. |
| `rebuilt backwards from the current row` | Success, from anchor 3. |
| `no full image of this row available` | Neither an insert before nor a delete after the update is in the cache. Import a wider range, or, in live mode, use `Rebuild from the current row`. |
| `rollback in the chain, rebuild forwards only` | The selected update was rolled back, and no insert of the row is in the cache to rebuild it forwards. |
| `partial rollback before the selected record, not replayed` | The selected update belongs to a rolled-back transaction that had already undone part of its work (a savepoint) before it: replaying would apply changes that never stood. |
| `partial rollback at LSN <x>, not replayed` | The record at LSN x, in the row's history, belongs to a partly rolled-back transaction. |
| `ADR revert in the chain, not replayed` | A transaction in the row's history rolled back under ADR without compensation records: its undone bytes leaked into later records. |
| `row inserted again after a delete, no image before it` | Going backwards, the history reaches a delete: the selected update belongs to a new life of the key, and nothing gives its image before. |
| `row forwarded, not followed` | A heap row became a forwarding stub (moved to another page). The new location is not followed. |
| `modification not decodable at LSN <x>` | The record at LSN x has a layout the decoder does not know, for example an `LOP_MODIFY_COLUMNS` without its `Log Record` bytes, or an `LOP_MODIFY_ROW` without `Offset in Row`. |
| `chain broken at LSN <x>` | At LSN x, the row did not hold the bytes the fragment expected. The history is not consistent with the image, and nothing is shown. |
| `key not decodable` | Shown as a note on selection, or as the status after a rebuild from the current row: the clustered key (or heap address) of the update could not be read. |

### Notes

Shown in italics under the status line.

| Message | Meaning |
|---|---|
| `key not decodable` | See above. A partitioned non-unique clustered index whose partition column is not in the key gives this for every update (see [What is not supported](#what-is-not-supported)). |
| `row moved by the engine` | The insert or delete belongs to a `SplitPage` system transaction. Shown only when the transaction's begin record is in the cache. |
| `may hold values undone by a partial rollback` | A delete image logged after an ADR revert in the same life of the row: some of its values may have been rolled back. |
| `includes changes of unknown outcome` | The rebuilt row applies changes whose commit or rollback is outside the imported range. |
| `the row image has problems, see Bytes` | The image of an insert or a delete has a malformed field or an unreadable column. |

### Schema messages

Shown above the `Values` grid.

| Message | Meaning |
|---|---|
| `a schema connection is required` | Offline import without a schema source. Connect one with `Connect schema source…`. |
| `allocation unit not found in the connected database` | The record's `AllocUnitId` is not in the schema source: another database, a database where the index was rebuilt since, or a table dropped since. The schema query reads in-row and row-overflow allocation units only (`au.type IN (1, 3)` in `Backends/Sql/TableSchema.sql`), so a record of a LOB data allocation unit gets this message too. |

### Anchor 3 messages

Shown on the status line after `Rebuild from the current row`: `row locked by an open transaction`, `log truncated since the import, re-import to use the current row`, `current row is not this row`, `table rebuilt since this record, the current row is not comparable`, `the current row holds columns that cannot be decoded`, `key value cannot be sent in the database code page`, `key not decodable, the current row cannot be sought`, `not available on a database with Accelerated Database Recovery`. Their meanings are in the [user guide](user-guide.md#rebuild-from-the-current-row).

### Problems in the Bytes tab

Shown as `problem:` in the tooltip of a `Malformed` field, and visible in `Values` as a missing or noted value.

| Problem | Meaning |
|---|---|
| `no row image` | The record has no `RowLog Contents 0`. |
| `row shorter than its header` | Fewer than 4 bytes. |
| `not a FixedVar row (compressed rows are not decoded)` | Bit 0 of status byte A is set, as on every compressed row measured. |
| `compressed rows are not decoded` | The schema says the partition is compressed (`ROW` or `PAGE`). |
| `fixed-data end offset beyond the row` | The offset of the end of the fixed data points past the row. |
| `null bitmap beyond the row` | The null bitmap runs past the row. |
| `variable column count beyond the row` | The variable column count runs past the row. |
| `offset array beyond the row` | The variable offset array runs past the row. |
| `variable end offset out of order or beyond the row` | An end offset decreases or points past the row. |
| `column count greater than the schema's` | The row has more columns than the table: the schema probably comes from another database. |
| `fixed-data end offset <f>, the schema predicts <e>` | The fixed data does not have the size the schema gives for this column count: the schema does not describe this row. |
| `fixed column beyond the fixed-data end offset` | A fixed column lies past the end of the fixed data. |
| `versioning tag beyond the row` | Bit 6 announces a versioning tag that the row is too short to hold. |
| `<n> bytes do not fit the column type` | See the column notes above. |
| `sparse column not decoded` | See the column notes above. |

## What is not supported

| Not supported | Why, and what is shown |
|---|---|
| Compressed rows (`ROW`, `PAGE`) | A different row format. The row is one `Malformed` field with the problem `compressed rows are not decoded` or `not a FixedVar row (compressed rows are not decoded)`. |
| Sparse columns | They live in a sparse vector the decoder does not read. Noted `sparse column not decoded`; the other columns are decoded. Anchor 3 refuses such a row. |
| Off-row values (LOB, row-overflow) | Following the pointer would mean reading other pages. Noted `off-row value not followed`. |
| Nonclustered index records | Only data rows (heap and clustered index leaf) are decoded. Index records are shown as bytes. |
| Memory-optimized tables (In-Memory OLTP) | Their changes reach `fn_dblog` only as `LOP_HK` records, several operations per record, without an allocation unit. They are listed in the Records tab and counted in `By operation type`, but not decoded and not flagged. |
| A non-unique clustered index partitioned on a column outside the key | The key record of an update then carries the partition column as an extra entry, which the schema read by this version does not describe. Every update of such a table is noted `key not decodable` and is not rebuilt. |
| Rows written before an `ALTER COLUMN` that changed a column's type or position | The current schema cannot describe them, and nothing in the row says so. When the size of the fixed data differs, the problem `fixed-data end offset <f>, the schema predicts <e>` flags it. Only `ADD COLUMN` is handled. |
| Heap rows that become forwarded | The replay stops at the forwarding stub: `row forwarded, not followed`. |
| Rows packed into one insert by a page split | Only `RowLog Contents 0` of an insert is decoded; the other contents are shown raw. |
| The payload ADR appends after the versioning tag | Shown as an unknown trailer; it never stops the decoding of the columns before it. |
| Undoing a compensation record | A compensation does not log the bytes it replaced. Compensation records are left out of every replay; selected alone, they show their fragment only. |
| Rebuilding from the current row on a heap, under ADR, or offline | See [Reconstruction anchors](#reconstruction-anchors). |
| Raw `.ldf` files | Records are read through `fn_dblog` and `fn_dump_dblog` only. |

## Known issues

The decision ledger of the decoder (`docs/superpowers/plans/2026-10-15-rowlog-decoder-ledger.md`, item 20) lists cases left as they are in 0.1.0. None of them shows a wrong value silently, but they can mislead:

- A `smalldatetime` stored with an invalid byte pattern rolls over instead of being refused.
- Anchor 3 accepts a current row whose columns are off-row or of a type that is not decoded; those columns are shown as unknown.
- An offline schema source is kept after importing backups of another database.
- A column added with `ADD COLUMN` without rewriting the rows can make anchor 3 answer `current row is not this row`.
- When an off-row value moves, its in-row pointer changes, and the column is shown as changed although its value may be the same.
