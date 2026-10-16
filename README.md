# SqlLogExplorer

SqlLogExplorer is a desktop application for Windows, Linux and macOS that reads the transaction log of a SQL Server database and shows what each row operation did. It imports log records through `sys.fn_dblog` (live database) or `sys.fn_dump_dblog` (log backups), keeps them in a local SQLite cache, and decodes the binary `RowLog Contents` of inserts, deletes and updates into typed column values, before and after the change.

It is written for DBAs, for three uses:

- Recovery: what a row contained before it was deleted or overwritten.
- Audit: what an `UPDATE` changed, column by column, before and after.
- Learning the log format: an annotated hexdump colors each byte of a row image by its role (status bits, fixed data, null bitmap, offset array, variable data) and names the column and value it belongs to.

SqlLogExplorer is a preview. The log format is undocumented: every layout the decoder relies on was measured on SQL Server 2017, 2019, 2022 and 2025. When a reconstruction cannot be proven, the application refuses it with a message instead of showing a row that might be wrong.

## Documentation

- [User guide](docs/user-guide.md): the connection dialog, live and offline imports, the schema source, the Records tab with its transactions and searches, the statistics tabs, the inspector.
- [Decoding and reconstruction](docs/decoding.md): what is decoded, how an update is rebuilt, and what every refusal message means.
- [Changelog](CHANGELOG.md).

## Download and install

Releases are published on the [GitHub Releases page](https://github.com/rudi-bruchez/SqlLogExplorer/releases). Each release carries one archive per platform, named `sqllogexplorer_<version>_<os>_<arch>`, where `<os>` is `linux`, `osx` or `win` and `<arch>` is `x64` or `arm64`. The archives are self-contained: no .NET runtime needs to be installed.

Each release also carries `sqllogexplorer_<version>_checksums.txt`, the SHA-256 sums of the archives. Download it next to the archive, then check the archive:

```sh
sha256sum -c --ignore-missing sqllogexplorer_0.2.0_checksums.txt          # Linux
shasum -a 256 -c --ignore-missing sqllogexplorer_0.2.0_checksums.txt      # macOS
```

On Windows, the output of `(Get-FileHash <archive> -Algorithm SHA256).Hash` in PowerShell must match the line of that archive in the checksums file (case aside).

Extract the archive into a folder of your choice and run the `SqlLogExplorer` executable (`SqlLogExplorer.exe` on Windows).

The macOS build is not signed, so Gatekeeper blocks it after a download. Clear the quarantine attribute on the extracted folder once:

```sh
xattr -dr com.apple.quarantine <folder>
```

## Prerequisites and permissions

| Task | What it needs |
|---|---|
| Live import (`fn_dblog`) | A login that is `sysadmin` on the server. |
| Rebuild from the current row (`DBCC PAGE`) | `sysadmin`, live mode only. |
| Reading table schemas (needed to decode values) | `sysadmin`, or `db_owner` of the database. In live mode the analyzed database serves; offline, a schema source connection. |
| Offline import of `.trn` files (`fn_dump_dblog`) | Windows with SQL Server Express LocalDB installed (the `sqllocaldb` command on the `PATH`). |

The schema is read from `sys.system_internals_partition_columns` and the related catalog views. A login that is neither `sysadmin` nor `db_owner` sees a filtered catalog without any error, so the application warns when it connects a schema source with such a login.

The offline mode creates and starts a LocalDB instance named `SqlLogExplorerInstance` for the duration of an import, then stops it.

## Quick start

Live analysis of a database:

1. Click `Connect (Live DB)…`.
2. Enter the server, choose the authentication and the encryption, then click `Connect` to list the databases.
3. Choose the database. Optionally set a start and an end in `Time Window (Optional):` to import only part of the log.
4. Click `OK`. The records are imported and shown in the `Records` tab.
5. To follow one transaction, select it in the list on the left; to see what happened to a table or a page, type its name or `file:page` in the boxes above the list.
6. Select an `LOP_INSERT_ROWS`, `LOP_DELETE_ROWS`, `LOP_MODIFY_ROW` or `LOP_MODIFY_COLUMNS` record. The inspector below the grid shows the decoded columns in `Values` and the annotated bytes in `Bytes`.

Offline analysis of log backups (Windows):

1. Click `Open .trn file(s) (Offline)…` and select one or more `.trn` files.
2. When the import is done, click `Connect schema source…` and connect to a database that holds the same tables: a restored copy of the database, or the source database itself.
3. Select records as above.

The [user guide](docs/user-guide.md) describes each step and every message in detail.

## Supported versions

Every layout the decoder relies on was measured on SQL Server 2017, 2019, 2022 and 2025, and the test fixtures under `tests/SqlLogExplorer.Tests/Fixtures/` come from these four versions. Other versions are not tested.

## Limits

- Only the log `fn_dblog` still returns is read live: records already truncated from the log (after a log backup, or a checkpoint in the simple recovery model) are only available from log backups.
- Raw `.ldf` files are not read. Offline analysis reads `.trn` log backups only, and only on Windows.
- Decoded: `LOP_INSERT_ROWS`, `LOP_DELETE_ROWS`, `LOP_MODIFY_ROW` and `LOP_MODIFY_COLUMNS` on heaps and clustered indexes. Nonclustered index records and other operations are listed but not decoded.
- Not decoded: compressed rows (`ROW` or `PAGE`), sparse columns, values stored off-row (LOB and row-overflow), memory-optimized tables (In-Memory OLTP), and some data types. The details are in [Decoding and reconstruction](docs/decoding.md).
- A reconstruction that cannot be proven is refused: rollbacks to a savepoint, Accelerated Database Recovery (ADR) reverts, a key reused after a delete, a heap row moved to another page, and a few other cases, each with its own message.
- Rebuilding from the current row works in live mode only, on a clustered table, in a database without ADR. It runs a `SERIALIZABLE` transaction on the analyzed database for the time of the read (a few hundred milliseconds in testing), and waits at most 5 seconds for a row lock.
- The Records tab pages through the records in LSN order. It can be restricted to one transaction, an object or a page, and has no other filter.

## Build from source

The project targets .NET 10. With the .NET 10 SDK installed:

```sh
dotnet build
dotnet test tests/SqlLogExplorer.Tests
```

The unit tests decode frozen fixtures and need no SQL Server. The integration tests are skipped unless `SQLLOGEXPLORER_RUN_INTEGRATION=1`, and then read these variables:

| Variable | Used by | Value |
|---|---|---|
| `SQLLOGEXPLORER_RUN_INTEGRATION` | every integration test | `1` to run them |
| `SQLLOGEXPLORER_TEST_SQL` | log columns, schema, current row tests | connection string to `master` of a test server, as `sysadmin` |
| `SQLLOGEXPLORER_TEST_LIVEDB` | live backend and LSN window tests | connection string to a live database |
| `SQLLOGEXPLORER_TEST_TRN` | LocalDB backend test (Windows) | path of a `.trn` file; `tests/fixtures/make_test_trn.sql` makes one |

`tests/fixtures/sqlserver.sh` starts a test SQL Server under podman and prints the two `export` lines for `SQLLOGEXPLORER_RUN_INTEGRATION` and `SQLLOGEXPLORER_TEST_SQL`. Evaluate them and run the tests in the same shell invocation:

```sh
eval "$(tests/fixtures/sqlserver.sh up 2022 sle-test)" && dotnet test tests/SqlLogExplorer.Tests; tests/fixtures/sqlserver.sh down sle-test
```

`tests/fixtures/capture.sh` regenerates the fixtures (it starts SQL Server 2017, 2019, 2022 and 2025 under podman). One integration test runs it for 2022, so the integration run also needs podman and the `mcr.microsoft.com/mssql/server:2022-latest` image.

## License

MIT, see [LICENSE](LICENSE).
