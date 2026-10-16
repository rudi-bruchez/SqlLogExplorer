# User guide

This guide follows the application as it presents itself: the main window, the connection dialog, the two ways to import records, the schema source, the tabs, and the inspector. Labels are quoted as they appear on screen. For what the decoder does with the bytes, and what each refusal means, see [Decoding and reconstruction](decoding.md).

## Main window

The top of the window holds the import buttons:

- `Open .trn file(s) (Offline)…` imports log backups.
- `Connect (Live DB)…` imports from a live database.
- `Cancel` appears while an import runs and stops it.
- `Connect schema source…` appears after an offline import (see [Schema source](#schema-source)).

Below the buttons, a status line reports what is happening: `Select a .trn file or enter a Live DB connection.` at start, `Analyzing…` with a progress bar during an import, then `<n> records imported.`, `Analysis cancelled.` or `Failed: <message>`.

The rest of the window is three tabs: `Records`, `By operation type` and `By object`.

One import is held at a time. Starting a new import, or closing the window, discards the previous one. The records are kept in a temporary SQLite file (`sqllogexp_<guid>.db` in the system temporary folder), deleted at that moment. A cancelled or failed import is discarded too.

## Connection dialog

`Connect (Live DB)…` and `Connect schema source…` open the same dialog, titled `Connect to Server`. It has two tabs, `Connection` and `History`. The `History` tab shows a `Recent servers:` list, which this version does not fill: it is always empty.

The `Connection` tab:

| Field | Content |
|---|---|
| `Server:` | Server name or IP address, with an instance name or a port as SQL Server clients accept them. |
| `Authentication:` | `Windows` (integrated security) or `SqlLogin`. `SqlLogin` shows `User name:` and `Password:`. |
| `Encryption:` | `Mandatory` (default) or `Optional`. |
| `Trust server certificate` | Checked by default. |
| `Connect` | Connects to the server without a database and lists its databases. The status line then says `<n> databases found.` or `Connection failed: <message>`. |
| `Database:` | The database to analyze, from the list `Connect` filled. |
| `Time Window (Optional):` | `Start:` and `End:`, each a date and a time. Live import only: the dialog hides it when it opens for a schema source. |

`OK` checks the choice and closes the dialog; `Cancel` closes it without doing anything. Without a database, `OK` reports `Failed to resolve LSN range: Please select a database first.`

## Live import

1. Click `Connect (Live DB)…`.
2. Fill the server, authentication and encryption, then click `Connect`.
3. Choose the database in `Database:`.
4. Optionally set the time window (next section).
5. Click `OK`.

The application then runs `SELECT … FROM sys.fn_dblog(…)` on the database and streams the records into the cache. The login must be `sysadmin`: `fn_dblog` refuses anything less. `fn_dblog` reads the log the database still holds; records truncated since (by a log backup, or by a checkpoint in the simple recovery model) are only in log backups.

In live mode, the analyzed database also serves as the schema source, and the inspector can read the current row of a table (see [Rebuild from the current row](#rebuild-from-the-current-row)).

### Time window

Without a time window, the whole log `fn_dblog` returns is imported.

With a window, clicking `OK` first resolves it into an LSN range, with the status `Connecting to resolve LSN range...`:

- A date without a time means midnight of that day. A time without a date is ignored. Either bound can be left empty.
- The range runs from the first to the last `LOP_BEGIN_XACT`, `LOP_COMMIT_XACT` or `LOP_ABORT_XACT` record whose time falls inside the window. The time of a begin record is its `Begin Time`, that of a commit or an abort its `End Time`. These are the server's clock, so the window is in the server's local time.
- Only records inside that range are imported. A transaction that began before the window and committed inside it is imported from its commit record on, without its earlier changes. Widen the window to include the start of the transactions you want to rebuild.
- If no begin, commit or abort record falls inside the window, the window is dropped and the whole log is imported.

Resolving the window reads the begin, commit and abort records of the whole log once, so on a large log the dialog can take a while before it closes. Opening the connection for it is given 10 seconds; past that, the dialog says `Failed to resolve LSN range: Connection timed out.` Any other failure is shown as `Failed to resolve LSN range: <message>`. The dialog stays open in both cases.

## Offline import of log backups

Offline import reads `.trn` log backups through `sys.fn_dump_dblog`. It needs Windows and SQL Server Express LocalDB (the `sqllocaldb` command). The button is visible on every platform, but on Linux and macOS the import fails because `sqllocaldb` cannot be started.

1. Click `Open .trn file(s) (Offline)…`.
2. Select one or more files in the file dialog (filter `SQL Server transaction-log backup`, `*.trn`). Select several files to import a chain of log backups.
3. The application creates and starts a LocalDB instance named `SqlLogExplorerInstance`, reads each file in turn, then stops the instance.

The files can be selected in any order: records are always ordered by LSN. A record whose LSN is already in the cache is skipped, so overlapping backups do not duplicate anything, and the count in `<n> records imported.` is the number of records actually added.

`fn_dump_dblog` returns an empty `AllocUnitName`, so after an offline import the `AllocUnitName` column of the Records grid is empty and the `By object` tab has no table names. The records carry their `AllocUnitId` instead, which the schema source resolves.

## Schema source

Decoding values needs the physical schema of each table: column offsets, types, null bits, the clustered key. It is read from the catalog of a database by allocation unit id (`AllocUnitId`).

In live mode the analyzed database is the schema source. Offline, there is none until you connect one: the inspector shows the structure of the bytes, and `Values` says `a schema connection is required`.

To connect a schema source after an offline import:

1. Click `Connect schema source…`.
2. In the dialog, connect to the server and choose a database that holds the same allocation units as the backups: a restored copy of the database, or the source database itself. A database created by the same script is not enough: allocation unit ids differ between two databases, and change when an index is rebuilt. A restore keeps them.
3. Click `OK`.

The status line then says one of:

- `Schema source connected.`
- `Warning: the login is neither sysadmin nor db_owner of this database; the catalog is filtered and some tables may not decode.`
- `Schema connection failed: <message>`

The selected record is inspected again with the new schema. A record whose allocation unit is not in the connected database shows `allocation unit not found in the connected database` in `Values`. The schema source is kept for the next offline import, and dropped by a live import.

## Records tab

The `Records` tab lists the cached records in LSN order, 200 per page, with the columns `LSN`, `Operation`, `Context`, `AllocUnitName` and `TransactionId`. `Previous` and `Next` move between pages; `Page <n>` shows the current one. The text next to them says what the grid shows: `All records`, the records of a search, or one transaction. The grid has no sort.

LSNs are shown in the `fn_dblog` form `vlf:block:slot`, in uppercase hexadecimal (SQL Server 2017 and 2019 return lowercase; the application normalizes them).

Selecting a record fills the inspector, below the grid. A splitter between the two can be dragged.

### Transactions

The panel on the left of the tab lists the transactions of the cache in LSN order, with the columns `Begin`, `End`, `Outcome`, `Ops`, `Name` and `TransactionId`:

- `Begin` is the `Begin Time` of the `LOP_BEGIN_XACT` record and `End` the `End Time` of the `LOP_COMMIT_XACT` or `LOP_ABORT_XACT` record, as SQL Server returns them, in the local time of the server.
- `Outcome` is `Committed`, `Rolled back`, or `Open` when the imported log holds neither a commit nor an abort for the transaction (it was still running, or it ends after the imported part of the log).
- `Ops` counts the imported records of the transaction, its begin and commit included.
- `Name` is the transaction name SQL Server records: `user_transaction` for an explicit `BEGIN TRAN`, otherwise the statement, such as `INSERT` or `UPDATE`.

A transaction that began before the imported part of the log has no `Begin` and no `Name`. The records outside any transaction (`TransactionId` `0000:00000000`, such as checkpoints) are not listed.

Selecting a transaction shows only its records in the grid, from the first page. `Show all records` clears the selection.

### Searching by object or page

The two boxes above the transaction list restrict the list and the grid. Type a value and press `Enter` or click `Search`; empty both boxes and search again to see everything.

- `Object` keeps the records whose `AllocUnitName` contains the text, such as `Orders` or `dbo.Orders.PK_Orders`. An offline import has no `AllocUnitName` (see above), so this search finds nothing there.
- `Page` keeps the records whose `Page ID` is that page, written `file:page` either in decimal, as `DBCC PAGE` and `sys.dm_db_page_info` take it (`1:336`), or as `fn_dblog` shows it (`0001:00000150`). A value read neither way is reported in red, and the list and grid stay as they were.

With both boxes filled, a record must match both. The transaction list then shows every transaction that has at least one matching record, and its `Ops` still counts all its records. Selecting one of them shows all its operations, not only those on the object or page, so that its begin and commit stay in view.

A page search follows the `Page ID` column only. Records that concern a page without naming it there, such as the allocation bitmaps (PFS, GAM, IAM) updated when the page is allocated, do not appear.

## Statistics tabs

`By operation type` counts the cached records per `Operation`, most frequent first (columns `Operation` and `Count`).

`By object` counts them per `AllocUnitName`, most frequent first (columns `Object (AllocUnitName)` and `Count`). Records without an allocation unit name are left out, and an offline import has none (see above).

Both are computed once, at the end of an import.

## Inspector

The inspector shows the selected record. Its top part holds:

- A title, `<Operation> at <LSN>`, followed by the outcome of the record's transaction when it has one: `, transaction committed`, `rolled back`, `partly rolled back` or `of unknown outcome`. The outcome is read from the records of the same transaction in the cache; a transaction whose commit or rollback is outside the imported range is of unknown outcome.
- For an update, a status line saying how the row was rebuilt, or why it was not (for example `rebuilt forwards from INSERT at LSN <x>` or `no full image of this row available`), and, when it applies, the `Rebuild from the current row` button.
- Notes, in italics, separated by semicolons.
- An error, in red, when decoding or a rebuild failed.

Below are two tabs, `Values` and `Bytes`.

Only `LOP_INSERT_ROWS`, `LOP_DELETE_ROWS`, `LOP_MODIFY_ROW` and `LOP_MODIFY_COLUMNS` are decoded. Any other record shows its title and its raw bytes.

The first record selected in a table can take a few seconds on a large import: the application computes the key of every record of that table then (about 8 seconds for 300,000 records was measured during development). Later selections in the same table are fast. A selection made while another inspection runs waits for it, and only the latest selection is shown.

### Notes

The notes line can hold:

| Note | Meaning |
|---|---|
| `row moved by the engine` | The insert or delete belongs to a `SplitPage` system transaction: the engine moved the row during a page split, no user statement changed it. Shown only when the transaction's begin record is in the cache. |
| `may hold values undone by a partial rollback` | A delete image logged after an ADR revert in the same life of the row: the image may hold values that were rolled back. |
| `key not decodable` | The update's clustered key (or the heap row's address) could not be read, so the row's history cannot be found. |
| `includes changes of unknown outcome` | The rebuilt row applies changes of a transaction whose outcome is unknown. |
| `the row image has problems, see Bytes` | The row image of an insert or a delete has malformed fields, or a column the decoder could not read (bytes that do not fit the type, a sparse column); the tooltips of the `Bytes` tab say which. |

### Values

For an insert, `Values` lists the columns of the inserted row in the `After` column. For a delete, the deleted row in the `Before` column. The grid columns are `Column`, `Before` and `After`.

For an update whose row was rebuilt, `Values` lists every column of the row before and after the change, and highlights in yellow the rows of the columns that changed. A column whose change cannot be told (a value that was not read on one side) is not highlighted.

For an update whose row could not be rebuilt, `Values` falls back to the change alone: it names a column only when the logged bytes fall entirely inside one fixed-length column or one byte of `bit` columns, and gives its values only when the bytes cover the whole column. Such values are followed by `(from the fragment alone, NULL state unknown)`: without the full row, the null bitmap is not known, and the column may in fact be `NULL`.

How values are written:

| Display | Meaning |
|---|---|
| empty cell | No value on this side (the other side of an insert or a delete), or not known. |
| `NULL` | SQL `NULL`. |
| `0x…` | A `binary` or `varbinary` value, or the raw bytes of a type that is not decoded. |
| `True`, `False` | A `bit` value. |
| Dates and times | ISO form as SQL Server shows it, `yyyy-MM-dd HH:mm:ss` with the fraction at the column's scale. |
| `value (note)` | A value with an explanation, such as `(added after the row was written)` or `(type not decoded)`. |
| `(note)` | A value that was not read, for example `(off-row value not followed)` or `(sparse column not decoded)`. |

A message in italics above the grid replaces the values when there is no schema: `a schema connection is required`, or `allocation unit not found in the connected database`.

### Bytes

`Bytes` shows every non-empty `RowLog Contents` of the record as hexadecimal blocks. Hovering a block shows a tooltip.

For an insert or a delete with a decodable row image, `RowLog Contents 0` is split into fields, one block per field, colored by role. The tooltip gives the role, the column, the decoded value, a problem when there is one, and the position (`RowLog Contents 0, offset <o>, <n> bytes`). Without a schema the structure is still shown, with the fixed data as one unnamed block.

| Color | Roles (as named in the tooltip) |
|---|---|
| gray | `StatusA`, `StatusB`: the two status bytes |
| tan | `FixedEnd`, `ColumnCount`, `VariableCount`: the offset of the end of the fixed data, the column count, the variable column count |
| lilac | `NullBitmap` |
| yellow | `VariableOffset`: one entry of the variable column offset array |
| green | `FixedColumn` |
| blue | `VariableColumn` |
| cyan | `LobPointer` (an in-row pointer to an off-row value), `VersionTag` (the 14-byte row versioning tag) |
| red | `Malformed`: bytes that do not fit the expected structure, with the problem in the tooltip |
| light gray | `Unused`, `DroppedColumn`, `UnknownTrailer` (bytes after the versioning tag, for example under ADR) |

Every other content (the other `RowLog Contents` of an insert or a delete, all contents of an update) is one pale block with the tooltip `RowLog Contents <i>, <n> bytes, not decoded`.

### Rebuild from the current row

When an update cannot be rebuilt from the log alone (status `no full image of this row available`), the inspector can use the row as it is now in the database. The `Rebuild from the current row` button appears only when all of these hold:

- the import is live (not offline);
- the table has a clustered index (not a heap);
- the database does not have Accelerated Database Recovery on;
- the record's transaction is not partly rolled back.

Nothing is read from the server until you click. The click sets the status to `reading the current row…`, then, on one connection, in one `SERIALIZABLE` transaction with a lock timeout of 5 seconds:

1. seeks the row on the key read from the log, and holds it;
2. reads the log written since the last record of the cache with `fn_dblog`, and adds it to the cache;
3. reads the row's page with `DBCC PAGE`;
4. commits, and checks that the bytes are the row whose key was sought.

The row is then replayed backwards, from now to the selected record. On success, the status reads `rebuilt backwards from the current row`. If the row was deleted after the selected record, the delete now in the cache is used instead, and the status reads `rebuilt backwards from DELETE at LSN <x>`.

The records added in step 2 appear in the Records grid when it loads a page again. The statistics tabs are not updated.

When the current row cannot be used, the status says why:

| Status | Meaning |
|---|---|
| `row locked by an open transaction` | Another session holds a lock on the row: waiting 5 seconds was not enough. Retry once that transaction has ended. |
| `log truncated since the import, re-import to use the current row` | The log was truncated (by a log backup, for example) after the import, so the changes in between cannot be read. Import again. |
| `current row is not this row` | No row with that key exists now, or the row found does not carry the same key. |
| `table rebuilt since this record, the current row is not comparable` | The page belongs to another allocation unit: the index or the table was rebuilt or recreated. |
| `the current row holds columns that cannot be decoded` | The current row has a column the decoder cannot read, such as a sparse column. |
| `key value cannot be sent in the database code page` | A `char` or `varchar` key value cannot be expressed in the database's default code page, so seeking it could find another row. Nothing is sought. |
| `key not decodable, the current row cannot be sought` | The key could not be read from the update record, or one of its columns is of a type that is not decoded. |
| `not available on a database with Accelerated Database Recovery` | ADR was turned on since the button was shown. |

The rebuilt row then follows the same rules as any other reconstruction, so the status can also be one of the replay refusals listed in [Decoding and reconstruction](decoding.md#refusal-and-status-messages).

When a step fails with a SQL Server error, the error line reads `Rebuild from the current row failed: Current row, step '<step>' failed: <message>`, where `<step>` is `seek`, `log suffix` or `page read`. Any other failure, such as a lost connection, reads `Rebuild from the current row failed: <message>`. Reading `fn_dblog` and `DBCC PAGE` requires `sysadmin`.

After a rebuild attempt, the button is hidden for that record. Select another record, then this one again, to show it.

### Errors

A failure while inspecting a record is shown in red as `Decoding failed: <message>`. The rest of the application keeps working.
