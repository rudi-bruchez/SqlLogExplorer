# Changelog

All notable changes to this project are documented in this file. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- A transaction list on the left of the `Records` tab, with each transaction's begin and end time, outcome (`Committed`, `Rolled back` or `Open`), number of records and name. Selecting a transaction shows only its records in the grid; `Show all records` clears it.
- A search by object and by page above the list. `Object` matches part of the `AllocUnitName`; `Page` takes `file:page` in decimal (`1:336`, as `DBCC PAGE` takes it) or in the `fn_dblog` form (`0001:00000150`). Both the transaction list and the grid follow the search.
- The import now reads the `Begin Time` and `End Time` columns of `fn_dblog` and `fn_dump_dblog`.

## [0.1.1] - 2026-10-16

Fixes the connection dialog, whose buttons could not be reached.

### Fixed

- The `OK` and `Cancel` buttons of the connection dialog were cut off at the bottom of the window when SQL authentication or the time window made the form taller than the window, so a database could be selected and never confirmed. They now stay in a bar at the bottom, the fields scroll above it, and the window can be resized.

## [0.1.0] - 2026-10-16

First release. SqlLogExplorer reads the transaction log of a SQL Server database through `sys.fn_dblog`, or of log backups through `sys.fn_dump_dblog`, keeps the records in a local SQLite cache, and decodes the binary `RowLog Contents` of row operations into typed column values, before and after each change. It is a preview: the log format is undocumented, and every layout the decoder relies on was measured on SQL Server 2017, 2019, 2022 and 2025.

### Added

- Live analysis: connect to a SQL Server, pick a database and a time window, and import the matching records of `fn_dblog`. The window is resolved to an LSN range first, so only that part of the log is read.
- Offline analysis of `.trn` log backups, one file or a chain, through `fn_dump_dblog` on a LocalDB instance (Windows only).
- A Records tab that pages through the cached records in LSN order, and a statistics view counting operations by type and by object.
- Row decoding for `LOP_INSERT_ROWS`, `LOP_DELETE_ROWS`, `LOP_MODIFY_ROW` and `LOP_MODIFY_COLUMNS` on clustered and heap tables, for the common fixed and variable types, with the clustered key decoded to identify the row.
- An inspector showing each column before and after the change, with an annotated hexdump of the bytes each value comes from.
- UPDATE reconstruction from three anchors: the delta itself, a replay from the nearest INSERT or DELETE image of the same row, and, on explicit request, the current row read from the database with `DBCC PAGE`.
- Decoding of offline backups: the table schemas are read from a server you connect as the schema source, which holds the same database.

### Limits

- A reconstruction that cannot be proven is refused with a message rather than shown. This covers savepoint rollbacks, ADR reverts without row compensation, and a key reused after a delete.
- Not decoded: compressed rows and sparse columns (each reported by name), partitioned non-unique clustered indexes (reported as `key not decodable`), and In-Memory OLTP tables, whose `LOP_HK` records are imported but not decoded.
- Offline import of `.trn` files needs LocalDB, so it works on Windows only; the menu entry is visible on Linux and macOS and fails there.
- Importing with `fn_dblog` and reading the current row with `DBCC PAGE` require `sysadmin`. Reading the current row also requires a clustered table without ADR, and it holds a SERIALIZABLE transaction on the analyzed database for a few hundred milliseconds.
- Raw `.ldf` files are not read.

[Unreleased]: https://github.com/rudi-bruchez/SqlLogExplorer/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/rudi-bruchez/SqlLogExplorer/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/rudi-bruchez/SqlLogExplorer/releases/tag/v0.1.0
