# RowLog Contents layouts: the clustered key record and `LOP_MODIFY_COLUMNS`

Research note of task 4 (plan `2026-10-15-rowlog-decoder`, spec03 §6.2 and §11). It states the two undocumented layouts that `KeyDecoder` (task 7) and `ModifyDecoder` (task 8) implement. Measured on 2026-10-15 and 2026-10-16 on `mcr.microsoft.com/mssql/server:<2017|2019|2022|2025>-latest`.

## Material

- Frozen fixtures of task 3: `tests/SqlLogExplorer.Tests/Fixtures/<version>/{keys,modify,versioned,versioned_adr}.json`.
- New probes, in `docs/research/probes/`: `key_types.sql` (one table per key type), `key_bits.sql` (place and number of bit key columns), `modify_columns.sql` (no versioning), `modify_columns_rcsi.sql`, `modify_columns_rcsi_adr.sql`, `modify_columns_adr.sql` (ADR without RCSI). Each file ran on a fresh database `fx` (full recovery, backed up), with the options of its first line, and was captured with `tests/fixtures/capture.sql` (log, schema, `DBCC PAGE` of every data page at the end). ADR files ran on 2019, 2022 and 2025 only. Containers came from `tests/fixtures/sqlserver.sh`.
- Throwaway check scripts (Python), outside the repository. What they check is described with each result below.

In what follows, `u16(b, o)` is the little-endian 16-bit integer at offset `o` of `b`, and `pad4(n)` is `n` rounded up to a multiple of 4.

## The clustered key record in `RowLog Contents 2`

On a clustered index, every non-compensation modify record (`LOP_MODIFY_ROW` and `LOP_MODIFY_COLUMNS`) carries in `RowLog Contents 2` the clustered key of the row, as an index record. A heap modify and a compensation record have an empty `RowLog Contents 2`.

### Rule

Input: the bytes `K` of `RowLog Contents 2` and the `TableSchema` of the allocation unit.

1. Key list. The clustered key columns ordered by key ordinal, then the uniqueifier when the schema has one (`is_uniqueifier`). `C` is the length of that list. Null bit `i` and every other position below refer to this list, never to `leaf_null_bit` or `partition_column_id`.
2. `K[0]` is status A. Bits 1 to 3 hold 3 (index record), bit 4 is always set (null bitmap present), bit 5 is set when the list holds a variable column or the uniqueifier. Observed values: `0x16` and `0x36` only. There is no status B and no fixed-data end offset.
3. Fixed area, from offset 1. Walk the list in key order and lay out the fixed-length columns only:
   - a fixed column that is not `bit` takes `max_inrow_length` bytes, the same stored bytes as in the data row (no transformation for any type, including `DESC` keys);
   - `bit` key columns are packed: the first bit key column of each group of eight (in key order) allocates one byte at the current position, and the j-th bit key column of the key (0-based, counting bit key columns only) is bit `j % 8` of its group's byte;
   - variable columns and the uniqueifier take no room here.
4. Column count: `u16` equal to `C`.
5. Null bitmap: `ceil(C / 8)` bytes; bit `i` (least significant first) is the null bit of list entry `i`, so key ordinal `i + 1`, and the uniqueifier's bit is the last one, never set.
6. If bit 5 of status A is set: variable count `V` (`u16`), `V` end offsets (`u16`, from the start of `K`), then the data: the variable key columns in key order, then the uniqueifier. The uniqueifier is always 4 bytes, `00000000` for the first row of a key, although the data row of that first row has a zero-length uniqueifier or no variable area at all.
7. A `NULL` key column: a fixed column holds zeros, a variable column has zero length, a `bit` column has its bit clear. Its null bit is the only thing to read.
8. The bits of a key bit byte that belong to no key column are not reliable: `0x07` and `0xC7` (table `bk`, one bit key column) and `0x39` (table `kb`, two) were observed on 2019 and 2025 where the key bits alone give `0x01`. Mask each bit key column's own bit.

For the canonical key of spec §7.1, step 3 gives each fixed column's bytes, step 6 each variable column's bytes, step 5 the null bits, and the uniqueifier comes from step 6 as a 4-byte integer.

In every table measured, the data row of a clustered table stores its key columns first, in key order: for each fixed key column, its offset in `K` equals `leaf_offset - 3`, and each bit key column's position in the key byte equals its `leaf_bit_position`. The rule above does not depend on this, and it was not measured on a table whose columns were altered after the clustered index was built.

### Check

For each clustered table, the script decodes every `LOP_INSERT_ROWS` image of the table with the schema (spec §5), builds the record the rule above predicts, and compares it byte for byte with every distinct `RowLog Contents 2` of the table's modify records. Every one must equal a predicted record; bit key bytes are compared under the mask of step 8.

Result: 37 tables (the 7 clustered tables of `keys` that have modify records, `t`, `u`, `s`, `m`, `m2` of `modify`, `vr`, the 19 tables of the probe `key_types`, the 3 of `key_bits`, `mw` and `vr2`), on 2017, 2019, 2022 and 2025: every key record matches. Without the mask, `bk` fails on 2019 and 2025 and `kb` on 2019. The check falls when the rule is broken: ordering the key list by column id fails `ko`, `kn` and `kb` on the four versions; omitting the 4 zero bytes of a first uniqueifier fails `n`, `nk`, `ku`, `kn` and `kbn`.

### Cases

Bytes of 2022; they are identical on the four versions apart from the unused bits of step 8.

| Key | Row | `RowLog Contents 2` |
|---|---|---|
| `int` | 305419896 | `16 78563412 0100 00` |
| `bigint` | 1311768467463790320 | `16 f0debc9a78563412 0100 00` |
| `smallint` | -2 | `16 feff 0100 00` |
| `tinyint` | 200 | `16 c8 0100 00` |
| `decimal(10,3)` | -1234.567 | `16 00 87d6120000000000 0100 00` (sign byte 0 = negative, then 8 bytes) |
| `date` | 2026-10-15 | `16 3f4a0b 0100 00` |
| `datetime2(7)` | 2026-10-15T12:34:56.1234567 | `16 87ee977669 3f4a0b 0100 00` |
| `datetimeoffset(7)` | same, +02:00 | `16 871e0fb358 3f4a0b 7800 0100 00` |
| `uniqueidentifier` | 01234567-89AB-CDEF-0123-456789ABCDEF | `16 67452301ab89efcd0123456789abcdef 0100 00` |
| `char(5)` | 'ab' | `16 6162202020 0100 00` |
| `varchar(10)` | 'abc' | `36 0100 00 0100 0b00 616263` |
| `nchar(3)` | N'é' | `16 e90020002000 0100 00` |
| `nvarchar(10)` | N'éa' | `36 0100 00 0100 0c00 e9006100` |
| `binary(4)` | 0xABCD | `16 abcd0000 0100 00` |
| `varbinary(10)` | 0xABCDEF | `36 0100 00 0100 0b00 abcdef` |
| `(k1 varchar, k2 int)`, table `k` | ('abc', 7) | `36 07000000 0200 00 0100 0f00 616263`: fixed `k2` first although it is key ordinal 2 |
| `PRIMARY KEY (k2, k1)`, both `int`, table `ko` | (k1 2, k2 10) | `16 0a000000 02000000 0200 00`: key order, not column order |
| `PRIMARY KEY (k DESC)`, table `kd` | 2 | `16 02000000 0100 00`: same bytes as ascending |
| `PRIMARY KEY (k DESC, n DESC)`, `varchar`, `int`, table `kdv` | ('abd', 6) | `36 06000000 0200 00 0100 0f00 616264` |
| non-unique `int`, table `n` | 1, first and second row | `36 01000000 0200 00 0100 1000 00000000` and `... 01000000` |
| non-unique `varchar`, table `ku` | 'dup' three times | `36 0200 00 0200 0d00 1100 647570 00000000`, then `01000000`, `02000000` |
| `(k1 varchar, k2 int NULL)` non-unique, table `nk` | ('x', NULL) | `36 00000000 0300 02 0200 0f00 1300 78 00000000`: null bit 1 = `k2`, bytes zeroed |
| `(c int NULL, a varchar NULL, b int NULL)` non-unique, columns declared `a, b, c`, table `kn` | (c NULL, 'x', 1) | `36 00000000 01000000 0400 01 ...`: null bit 0 = `c` (key ordinal 1) |
| same | (1, NULL, 1) | `36 01000000 01000000 0400 02 0200 1200 1600 00000000`: null bit 1 = `a`, zero length |
| same | (1, 'x', NULL) | `36 01000000 00000000 0400 04 ...`: null bit 2 = `b` |
| `(id int, flag bit)`, table `bk` | (1, 1) | `16 01000000 01 0200 00`; `07` and `c7` instead of `01` on 2019 and 2025 |
| `(id, g bit, flag bit)`, table `kb` | (1, g 1, flag 0), (2, 0, 1), (3, 1, 1) | `16 01000000 01 0300 00`, `... 02 ...`, `... 03 ...`: `g` bit 0, `flag` bit 1, one byte |
| `(b1 bit, id int, b2 bit)`, table `kbb` | (0, 2, 1) | `16 02 02000000 0300 00`: one byte at offset 1 for both bits, `id` after it |
| `PRIMARY KEY (b2, b1)`, table `kbr` | (b1 1, b2 0) | `16 02 0200 00`: `b2` bit 0, `b1` bit 1 |
| `(id int, b bit NULL)` non-unique, table `kbn` | (1, NULL) | `36 01000000 00 0300 02 0100 1100 00000000`: bit clear, null bit 1 set |

### Unknown

- More than eight bit key columns: the second byte is presumed to sit at the position of the ninth bit key column, by analogy with data rows; not measured.
- Key types of spec §1.1 not probed: `numeric`, `money`, `smallmoney`, `real`, `float`, `time`, `datetime`, `smalldatetime`. They are fixed-length and are expected to follow step 3; not measured.
- A clustered index whose data rows do not store the key columns first (for instance after `ALTER TABLE` on a key column, or a column added then made part of the key): the rule from the key list does not depend on it, but it was not measured.
- Partitioned tables and keys near the 900 or 1700 byte limits: not measured.

## `LOP_MODIFY_COLUMNS`

### Structure of the log record

Measured on every row operation of the fixtures (insert, delete, modify row, modify columns, on the four versions): the binary `Log Record` column has a fixed header whose length `H` is `u16(Log Record, 2)` (62 on every record measured), then at offset `H` the element count `E` (equal to `Num Elements`), then `E` lengths of `u16`. The elements follow from `pad4(H + 2 + 2E)`, each one starting on a multiple of 4: element `k + 1` starts at `pad4(start_k + length_k)`. The bytes between an element's end and the next multiple of 4 are padding. They are not reliably zero: in `LOP_MODIFY_COLUMNS` records, leftover bytes appeared on 2025 (`0ba35f7a`, `150057b6` after the one-byte elements `0b` and `15`); in other operations, nonzero padding appeared on all four versions (13 to 76 elements per version in the fixtures).

`fn_dblog` exposes the elements this way, for `LOP_MODIFY_COLUMNS` (checked on all 177 records):

- `RowLog Contents 0` to `3` are elements 0 to 3 exactly;
- `RowLog Contents 4` is everything from the start of element 4 to the end of the record (elements 4, 5, 6, ... with their padding), and is empty when element 4 is empty;
- `RowLog Contents 5` is everything from the start of element 5 to the end, empty when element 5 is empty;
- `Offset in Row` and `Modify Size` are 0, `Num Elements` = `E` = 4 + 2n, where n is the number of fragments.

Meaning of the elements, for n fragments numbered i = 0 to n - 1:

| Element | Content |
|---|---|
| 0 (`RowLog Contents 0`) | n pairs of `u16`: `ob_i`, `oa_i`. `ob_i` is the fragment's offset in the row before the record, `oa_i` its offset in the row once fragments 0 to i - 1 are applied. `oa_0 = ob_0`. |
| 1 (`RowLog Contents 1`) | n `u16`: `lb_i`, the length replaced by fragment i (the `Modify Size` of fragment i). |
| 2 (`RowLog Contents 2`) | clustered key record (above); empty on a heap and in a compensation record. |
| 3 (`RowLog Contents 3`) | lock information, the same bytes as `RowLog Contents 2` of the row's insert (26 bytes on the clustered tables, 20 on the heap `mh`); empty in a compensation record. |
| 4 + 2i | before bytes of fragment i, `lb_i` long; empty in a compensation record. |
| 5 + 2i | after bytes of fragment i. |

A fragment holds only bytes that differ, as in `LOP_MODIFY_ROW` (one byte of a four-byte `int`), except where a length change moves bytes: then it runs from the first byte that differs to the end of the moved region (a variable offset array and the data that follows it, a versioning tag).

### Rule A, from `Log Record`

1. Read `H`, `E`, the lengths and the elements as above; n = (E - 4) / 2.
2. Fragment i is `(offset = oa_i, before = element 4 + 2i, after = element 5 + 2i, replaced = lb_i)`, with `oa_i` and `lb_i` from elements 0 and 1.
3. Forwards, for i = 0 to n - 1, on the current row: when `before` is not empty, check that the row holds `before` at `offset`; then replace `replaced` bytes at `offset` with `after`.
4. Backwards, for i = n - 1 down to 0: check that the row holds `after` at `offset`, then replace `len(after)` bytes with `before`. A compensation record has no before bytes and cannot be undone (spec §1.2 already leaves that out).
5. A compensation record (`Description` = `COMPENSATION`) follows the same rule: its before elements are empty, `lb_i` still gives the length replaced, and its `ob_i`, `oa_i` are the offsets in the row as it is before and during the compensation.

Applying the fragments in reverse order at `ob_i` on the row before gives the same result as step 3.

### Rule B, from `RowLog Contents 0`, `1`, `4` and `5` only

These are the columns the cache holds under spec §3. They determine every fragment except the length of the last after element.

1. n = `len(RowLog Contents 0)` / 4; `ob_i`, `oa_i`, `lb_i` as above.
2. Before length `bl_i` = `lb_i`, or 0 in a compensation record.
3. After length of every fragment but the last: `la_i = lb_i + (oa_(i+1) - ob_(i+1)) - (oa_i - ob_i)`.
4. Stream `S` = `RowLog Contents 4` when it is not empty. Otherwise `S` = `RowLog Contents 5` and element 4 is empty (every compensation record; a non-compensation record whose first fragment inserts bytes would also have it, not observed).
5. Walk `S` from `p = 0`: for each fragment, before = `S[p, p + bl_i)` (skipped for i = 0 when `S` is `RowLog Contents 5`), `p = pad4(p + bl_i)`; after = `S[p, p + la_i)`, `p = pad4(p + la_i)`.
6. Last fragment: with `r = len(S) - p`, its after length is one of `max(0, r - 3)` to `r`. Since padding is not reliably zero, the bytes cannot decide it. With the row before the record available, take the candidate for which the row after has the length its own structure gives (spec §5 walk: end of the variable data, or end of the null bitmap, plus 14 for a versioning tag). When no candidate or several qualify, the record's layout is unknown (spec §6.2).

Rule B fails on rows that carry an ADR payload after the versioning tag (ADR without RCSI), whose length the row structure does not give, and it cannot give the last after fragment of a record shown alone, without a row.

### Check

For each fixture and probe, the script replays every row from its `LOP_INSERT_ROWS` (slots tracked through inserts), applying `LOP_MODIFY_ROW` as spec §6.1 says and `LOP_MODIFY_COLUMNS` with the rule under test. At each `LOP_MODIFY_COLUMNS` it checks that the before bytes of every fragment are in the row (step 3) and that undoing the fragments (step 4) gives back the row before. At the end of each chain, the replayed row is compared byte for byte with the next full image: the `LOP_DELETE_ROWS` image of the row when there is one, otherwise the row `DBCC PAGE` shows after the scenario. A `LOP_MODIFY_COLUMNS` passes when all of these hold for its chain. All transactions are replayed, rolled-back ones and their compensation records included, since their net effect is nothing.

The check falls when the rule is broken: applying fragments at `ob_i` instead of `oa_i` fails 17 records per version in the probes; taking `la_i = lb_i` for every fragment fails the `m2` record whose variable columns grow, on the four versions.

Results: 177 records, 48 distinct cases (33 records on 2017, 48 on each of 2019, 2022 and 2025).

- Rule A: every record passes, on the four versions.
- Rule B: every record passes except the 5 cases of ADR without RCSI, which fail on 2019, 2022 and 2025 (the replayed row keeps 3 bytes of padding at its end, and the delete image does not match).

In the table, each version cell reads `rule A / rule B`. Fragments read `offset:replaced>after length`. Where two shapes are listed, they differ between versions (ADR payload).

| Case | Table (file) | Fragments | 2017 | 2019 | 2022 | 2025 |
|---|---|---|---|---|---|---|
| `a = 99, b = 'zz'` (fixed + variable, length change) | `u` (modify) | 8:1>1 68:5>4 | pass/pass | pass/pass | pass/pass | pass/pass |
| `a, b` (two fixed) | `m2` (modify) | 8:1>1 62:1>1 | pass/pass | pass/pass | pass/pass | pass/pass |
| `c, d` (two variable, both grow) | `m2` (modify) | 71:8>9 142:0>1 | pass/pass | pass/pass | pass/pass | pass/pass |
| RCSI, `a = 12` (one column) | `vr` (versioned) | 8:1>1 28:3>3 | pass/pass | pass/pass | pass/pass | pass/pass |
| RCSI, `a = 99` under a savepoint | `vr` (versioned) | 8:1>1 29:3>3 | pass/pass | pass/pass | pass/pass | pass/pass |
| RCSI, its compensation | `vr` (versioned) | 8:1>1 29:3>3 | pass/pass | pass/pass | pass/pass | pass/pass |
| `a, b, e` (three fixed) | `mw` (probe) | 8:1>1 62:1>1 66:1>1 | pass/pass | pass/pass | pass/pass | pass/pass |
| `c = 'cccc', d = 'ee'` (two variable, length change) | `mw` (probe) | 126:8>10 196:2>2 | pass/pass | pass/pass | pass/pass | pass/pass |
| `a, c = 'c'` (fixed + variable, shrinks) | `mw` (probe) | 8:1>1 126:10>7 | pass/pass | pass/pass | pass/pass | pass/pass |
| `a = NULL, c = NULL` | `mw` (probe) | 8:1>1 122:11>10 | pass/pass | pass/pass | pass/pass | pass/pass |
| `a = 13, c = 'back'` (back from `NULL`) | `mw` (probe) | 8:1>1 122:10>14 | pass/pass | pass/pass | pass/pass | pass/pass |
| `b = NULL, e = NULL` (two fixed) | `mw` (probe) | 62:1>1 66:1>1 122:1>1 | pass/pass | pass/pass | pass/pass | pass/pass |
| `b = 22, e = 32` (back from `NULL`) | `mw` (probe) | 62:1>1 66:1>1 122:1>1 | pass/pass | pass/pass | pass/pass | pass/pass |
| `a, b, e, c, d` (five columns) | `mw` (probe) | 8:1>1 62:5>5 126:10>7 193:2>1 | pass/pass | pass/pass | pass/pass | pass/pass |
| `d = NULL, a = 7` | `mw` (probe) | 8:1>1 123:8>6 191:1>0 | pass/pass | pass/pass | pass/pass | pass/pass |
| `d = 'dback', a = 8` (back from `NULL`) | `mw` (probe) | 8:1>1 123:6>8 193:0>5 | pass/pass | pass/pass | pass/pass | pass/pass |
| `c, d` rolled back, forward record | `mw` (probe) | 126:7>19 205:5>1 | pass/pass | pass/pass | pass/pass | pass/pass |
| `c, d` rolled back, compensation (lengths change) | `mw` (probe) | 126:19>7 193:1>5 | pass/pass | pass/pass | pass/pass | pass/pass |
| `a, b` rolled back, forward record | `mw` (probe) | 8:2>2 62:2>2 | pass/pass | pass/pass | pass/pass | pass/pass |
| `a, b` rolled back, compensation | `mw` (probe) | 8:2>2 62:2>2 | pass/pass | pass/pass | pass/pass | pass/pass |
| `a, d` under a savepoint, forward record | `mw` (probe) | 8:2>2 130:1>1 193:5>9 | pass/pass | pass/pass | pass/pass | pass/pass |
| `a, d` under a savepoint, compensation | `mw` (probe) | 8:2>2 130:1>1 193:9>5 | pass/pass | pass/pass | pass/pass | pass/pass |
| `x, y` (two `char(50)`) | `mw` (probe) | 13:1>1 71:1>1 | pass/pass | pass/pass | pass/pass | pass/pass |
| heap, `a, b` | `mh` (probe) | 8:1>1 62:1>1 | pass/pass | pass/pass | pass/pass | pass/pass |
| heap, `a, c = 'cccc'` | `mh` (probe) | 8:1>1 71:4>6 | pass/pass | pass/pass | pass/pass | pass/pass |
| RCSI, `a = 11` (one column, first update) | `vr2` (probe) | 8:1>1 26:9>9 | pass/pass | pass/pass | pass/pass | pass/pass |
| RCSI, `a = 12` | `vr2` (probe) | 8:1>1 32:3>3 | pass/pass | pass/pass | pass/pass | pass/pass |
| RCSI, `b = NULL` | `vr2` (probe) | 0:1>1 18:17>10 | pass/pass | pass/pass | pass/pass | pass/pass |
| RCSI, `b = 'back'` (back from `NULL`) | `vr2` (probe) | 0:1>1 18:10>18 | pass/pass | pass/pass | pass/pass | pass/pass |
| RCSI, `a, c` | `vr2` (probe) | 8:5>5 30:3>3 | pass/pass | pass/pass | pass/pass | pass/pass |
| RCSI, `a = 99` rolled back, forward | `vr2` (probe) | 8:1>1 30:3>3 | pass/pass | pass/pass | pass/pass | pass/pass |
| RCSI, `a = 99` rolled back, compensation | `vr2` (probe) | 8:1>1 30:3>3 | pass/pass | pass/pass | pass/pass | pass/pass |
| RCSI, `c = 32` | `vr2` (probe) | 12:1>1 30:3>3 | pass/pass | pass/pass | pass/pass | pass/pass |
| RCSI + ADR, `a = 12` (one column) | `vr` (versioned_adr) | 8:1>1 28:3>3 | n/a | pass/pass | pass/pass | pass/pass |
| RCSI + ADR, `a = 99` under a savepoint (no compensation) | `vr` (versioned_adr) | 8:1>1 29:3>3 | n/a | pass/pass | pass/pass | pass/pass |
| RCSI + ADR, `a = 11` (one column) | `vr2` (probe) | 8:1>1 26:9>9 | n/a | pass/pass | pass/pass | pass/pass |
| RCSI + ADR, `a = 12` | `vr2` (probe) | 8:1>1 32:3>3 | n/a | pass/pass | pass/pass | pass/pass |
| RCSI + ADR, `b = NULL` | `vr2` (probe) | 0:1>1 18:17>10 | n/a | pass/pass | pass/pass | pass/pass |
| RCSI + ADR, `b = 'back'` (back from `NULL`) | `vr2` (probe) | 0:1>1 18:10>18 | n/a | pass/pass | pass/pass | pass/pass |
| RCSI + ADR, `a, c` | `vr2` (probe) | 8:5>5 30:3>3 | n/a | pass/pass | pass/pass | pass/pass |
| RCSI + ADR, `a = 99` rolled back, forward | `vr2` (probe) | 8:1>1 30:3>3 | n/a | pass/pass | pass/pass | pass/pass |
| RCSI + ADR, `a = 99` rolled back, compensation | `vr2` (probe) | 8:1>1 30:3>3 | n/a | pass/pass | pass/pass | pass/pass |
| RCSI + ADR, `c = 32` | `vr2` (probe) | 12:1>1 30:3>3 | n/a | pass/pass | pass/pass | pass/pass |
| ADR, `a = 11` (one column) | `vr2` (probe) | 8:1>1 30:10>31 | n/a | pass/FAIL | pass/FAIL | pass/FAIL |
| ADR, `a = 12` (one column, three fragments) | `vr2` (probe) | 8:1>1 34:1>1 56:5>5, or 8:1>1 34:1>1 60:1>1 | n/a | pass/FAIL | pass/FAIL | pass/FAIL |
| ADR, `a = 13` (back from `NULL`) | `vr2` (probe) | 8:27>27 60:11>11 | n/a | pass/FAIL | pass/FAIL | pass/FAIL |
| ADR, `b = NULL` | `vr2` (probe) | 0:1>1 18:53>60 | n/a | pass/FAIL | pass/FAIL | pass/FAIL |
| ADR, `b = 'back'` (back from `NULL`) | `vr2` (probe) | 0:1>1 18:60>61 | n/a | pass/FAIL | pass/FAIL | pass/FAIL |

Which statements produce `LOP_MODIFY_COLUMNS` rather than `LOP_MODIFY_ROW` was the same on the four versions for every probe. In `mw` (no versioning) every update of the probe, single column ones included when the changed bytes are far apart, gave `LOP_MODIFY_COLUMNS`. Under RCSI, `a = NULL`, `a = 13`, `b = 'a longer value'`, `b = 's'` and the rolled-back `b` update gave `LOP_MODIFY_ROW`. Under ADR without RCSI, the first single-column update (`a = 11`) already gave `LOP_MODIFY_COLUMNS`.

Record bytes of some cases (2022):

| Case | `RowLog Contents 0` | `RowLog Contents 1` | `RowLog Contents 4` |
|---|---|---|---|
| `u`: `a = 99, b = 'zz'` | `0800 0800 4400 4400` | `0100 0500` | `0a000000 63000000 4900616263 000000 48007a7a` |
| `m2`: `c, d` grow | `4700 4700 8d00 8e00` | `0800 0000` | `4f008b008d006363 50008c008f00636363 000000 64000000` (element 6 empty) |
| `mw`: five columns | `0800 0800 3e00 3e00 7e00 7e00 c400 c100` | `0100 0500 0a00 0200` | `0d000000 01000000 1600000020 000000 ...` |
| `mw`: `c, d` compensation | `7e00 7e00 cd00 c100` | `1300 0100` | empty; `RowLog Contents 5` holds `8500c100c60078 00 646261636b 000000` (element 6 empty) |
| `vr2` RCSI: `b = NULL` | `0000 0000 1200 1200` | `0100 1100` | `70000000 50000000 0001001a00616263080000000900030091 000000 04080000000900040092 0000` |
| `m2` on 2025: `a, b` | `0800 0800 3e00 3e00` | `0100 0100` | `0a000000 0b a35f7a 14000000 15 0057b6`: leftover padding |

### Unknown

- A record with more than four fragments (12 elements was the largest measured), and whether `RowLog Contents 4` still reaches the end of the record then: presumed, not measured.
- A non-compensation record whose first fragment has an empty before element (pure insertion at the first offset), and a record where elements 4 and 5 are both empty: then rule B has no column holding element 6 onwards. Not observed.
- `fn_dump_dblog` was not measured: the fixtures come from `fn_dblog`. Spec §2 says the two functions expose the same columns; that `Log Record` holds the same bytes from a `.trn` file is presumed.
- The content of the ADR payload after the versioning tag (spec §11 question 2) remains unknown; rule A does not need it.
- Why the engine chooses `LOP_MODIFY_COLUMNS` over `LOP_MODIFY_ROW`: not established, and not needed by the decoder.

## Contradictions with the spec

1. Spec §6.2 says `RowLog Contents 4` is "aligned on 4 bytes on some versions and not others". Measured: the elements are aligned on 4 bytes on all four versions. What varies is the content of the padding, zero or leftover bytes, leftover in `LOP_MODIFY_COLUMNS` on 2025 and in other operations on all four versions. Reading `RowLog Contents 4` as an unpadded concatenation is what gives the impression of a varying alignment.
2. Spec §2 says a compensation `LOP_MODIFY_ROW` or `LOP_MODIFY_COLUMNS` has an empty `RowLog Contents 0` and its restored bytes in `RowLog Contents 1`. That holds for `LOP_MODIFY_ROW` only. A compensation `LOP_MODIFY_COLUMNS` has its offset pairs in `RowLog Contents 0`, its replaced lengths in `RowLog Contents 1`, empty `RowLog Contents 2`, `3` and `4`, and its restored bytes in `RowLog Contents 5` (elements 5, 7, ... with empty before elements between them).
3. Spec §3 imports `RowLog Contents 0` to `5` but not `Log Record`. These columns do not determine the length of the last after fragment of a `LOP_MODIFY_COLUMNS` (rule B, step 6): the decoder can then decode a record only with the row before it, and not at all on rows carrying an ADR payload (5 cases out of 48, failing on every version that has ADR). Importing `Log Record` for `LOP_MODIFY_COLUMNS` records (for instance `CASE WHEN [Operation] = N'LOP_MODIFY_COLUMNS' THEN [Log Record] END`) makes rule A available, which passes every case on every version. This is a change to spec §3 and to task 2's import, for the controller to decide.
4. Spec §2 says a `NULL` fixed column is not zeroed and keeps whatever was there. That holds for an inserted row. An `UPDATE` that sets a fixed column to `NULL` zeroes its bytes: `a = NULL` logs `0c` to `00` at offset 8, `b = NULL, e = NULL` logs `15` to `00` and `1f` to `00` (table `mw`, four versions). The decoder's behaviour does not change, since the null bitmap decides, but no fixture should expect stale bytes after such an update.

Nothing measured contradicts spec §2 on the key record (fixed key columns from offset 1, then count, null bitmap, variable key columns, uniqueifier included), nor spec §7.1 (zeros in a `NULL` key column of the index record, stale bytes in the data row).
