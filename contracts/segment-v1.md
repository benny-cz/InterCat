# InterCat derived segment v1

Status: **frozen and implemented**, at minor 1 (§11). `observation-v1` and the additive `source-fields-v1` table are
defined here. Entity binding revisions, relation revisions and aggregate tiles reuse the container and are not defined
here.

This contract freezes what §20.1 calls the minimum store: immutable fixed-width little-endian columns, null
bitmaps, variable-data chunks, dictionaries, a raw-record locator and time-block metadata. It owns nothing
inside `journal-v1`, which §18.1 freezes, and nothing about how a generation is published, which
`contracts/store-v1.md` freezes. It owns the bytes of one segment and one dictionary, and what a reader may
conclude from them.

## 1. What a segment is

One segment is an immutable, time-ordered, columnar slice of one derived table of one capture. It is
published as a dependency of exactly one generation and is never rewritten: a later derivation publishes new
segments (R1, I15).

A segment carries source-derived facts only. There is deliberately no column for a resolved process
instance, a channel, a relation or a derived metric, because R1 forbids an observation column that a later
correlation revision could edit behind a reader. Those are separate derivations in their own dependencies.

Files are named by the generation that publishes them:

| Name | What it is |
|---|---|
| `seg-<generation:D10>-<ordinal:D4>.icats` | One `observation-v1` segment. |
| `fld-<generation:D10>-<ordinal:D4>.icats` | One `source-fields-v1` segment. |
| `dict-<generation:D10>-<dictionaryId:D4>.icatd` | One dictionary, named by the id its segments reference. |
| `journal-<generation:D10>.icatj` | The admitted `journal-v1` evidence the generation derives from. |

A segment references a dictionary by id, and the id is in the dictionary's file name, so a reader resolves a
segment's dictionaries from the manifest without a side index.

## 2. Bounds

Every bound is a refusal or a flush, never a truncation (R8).

| Bound | Value |
|---|---|
| Rows per segment, tunable target | 250,000 |
| Staged bytes per segment, tunable target | 64 MiB |
| Rows per segment, hard | 4,000,000 |
| Bytes per segment, hard | 256 MiB |
| Rows per time block | 8,192 |
| Time blocks per segment | 512 |
| Dictionary entries | 65,536 |
| Dictionary value bytes | 8 MiB |
| One text value, UTF-8 bytes | 4,096 |
| Variable chunk bytes | 64 MiB |

A writer that would pass a tunable target flushes; one that would pass a hard bound refuses. A reader that
meets a declared value past a bound refuses the file rather than reading part of it.

## 3. Layout

```text
segment file:
  [0, 128)                       header
  [128, 128 + 48*columns)        column directory
  ... + 32*timeBlocks            time-block directory
  <8-aligned column blocks>      values, then null bitmap, per column in directory order
  <variable chunk>               present only when a text column is chunk-encoded
  [length-32, length)            SHA-256 over every preceding byte
```

All scalars are little-endian. A 16-byte identifier is stored big-endian, so its bytes read in the order the
identifier is printed.

### Header, 128 bytes

| Offset | Width | Field |
|---|---|---|
| 0 | 8 | magic, `ICATSEG1` |
| 8 | 2 | format major, 1 |
| 10 | 2 | format minor, 1 (§11) |
| 12 | 4 | required feature bits; a non-zero bit this reader does not implement refuses the file |
| 16 | 16 | segment id |
| 32 | 16 | capture id |
| 48 | 16 | clock id — the clock every native reading in this segment is on (I8) |
| 64 | 4 | derivation version, the normalizer contract version (§24) |
| 68 | 4 | row count, at least 1 |
| 72 | 8 | minimum native reading |
| 80 | 8 | maximum native reading |
| 88 | 2 | column count |
| 90 | 2 | time-block count |
| 92 | 4 | timestamp encoding (`EN-TimestampEncoding`) |
| 96 | 4 | column directory offset, always 128 |
| 100 | 4 | time-block directory offset |
| 104 | 4 | variable chunk offset |
| 108 | 4 | variable chunk length |
| 112 | 4 | file length |
| 116 | 4 | table id: 1 `observation-v1`, 2 `source-fields-v1` |
| 120 | 4 | CRC-32C over bytes `[0, 120)` |
| 124 | 4 | CRC-32C over the column and time-block directories, bytes `[128, time-block directory offset + 32*timeBlocks)`; zero at minor 0 |

The segment id is derived, not minted: it is a UUIDv8 over the capture, the clock, the derivation, the
segment's ordinal, its row count and its native interval. For table 2 the table id is also in the segment-id
preimage; table 1 keeps its original preimage for byte compatibility. Rebuilding a generation from the same
evidence therefore names the same segment.

### Column directory entry, 48 bytes

| Offset | Width | Field |
|---|---|---|
| 0 | 2 | column id (§5) |
| 2 | 1 | logical type |
| 3 | 1 | physical encoding |
| 4 | 1 | nullability, 0 or 1 |
| 5 | 1 | reserved, zero |
| 6 | 2 | dictionary id, 0 when none |
| 8 | 4 | row count, equal to the header's |
| 12 | 4 | value offset |
| 16 | 4 | value length |
| 20 | 4 | null bitmap offset, 0 when not nullable |
| 24 | 4 | null bitmap length |
| 28 | 4 | known count — rows with a value |
| 32 | 4 | unknown count — rows without one |
| 36 | 4 | CRC-32C over the values |
| 40 | 4 | CRC-32C over the null bitmap |
| 44 | 4 | a chunk-encoded column: CRC-32C over the variable chunk; any other column, or minor 0: reserved, zero |

Logical types: 1 `Unsigned8`, 2 `Unsigned16`, 3 `Unsigned32`, 4 `Unsigned64`, 5 `Signed32`, 6 `Signed64`,
7 `Guid16`, 8 `Text`.

Physical encodings: 1 `Plain` — the values themselves, fixed width, directly mappable; 2 `Dictionary` — a
`uint32` code per row into the dictionary this column names; 3 `VariableReference` — a `uint32` offset and a
`uint32` length per row into the variable chunk.

A nullable column stores a slot for every row, so it stays directly mappable. **A slot with no value holds
zeros and is never read as a value**: its meaning comes from the null bitmap, which is LSB-first, one bit
per row, with every bit past the row count clear. The known and unknown counts must agree with the bitmap
exactly, because an availability counter that disagrees with the data makes a denominator a guess (§10.2).

### Time-block directory entry, 32 bytes

| Offset | Width | Field |
|---|---|---|
| 0 | 4 | first row |
| 4 | 4 | row count, at least 1 |
| 8 | 8 | minimum native reading in the block |
| 16 | 8 | maximum native reading in the block |
| 24 | 8 | reserved, zero |

Blocks partition the rows in order: the first block starts at row 0, each continues the one before it, and
together they cover every row exactly once.

## 4. Order

Rows are sorted by native reading, then by the raw-record locator — stream, epoch, ordinal — then by the
fact key. The locator is unique inside a capture (I1), so the order is total and the file is a function of
its row set rather than of the order the rows arrived. The tie-breaks are what make the sort repeatable;
they are deliberately not a claim about which of two indistinguishable records happened first.

Two rows that compare equal share one observation identity. A writer refuses them rather than publishing a
fact that would be counted twice (I2, I5).

For `source-fields-v1`, the source-field code follows the fact key as the last sort key. Two rows for one
observation and one field compare equal and are refused; one observation can carry several distinct fields.

The ordering is chronological, not acquisition order. Acquisition order is in the `RawRecordOrdinal` and
`JournalRecordIndex` columns and stays distinguishable from it (I7).

## 5. `observation-v1` columns

Every code is a contract (R5) and is never reused. A reader that meets an unknown code in a required column
refuses the segment.

| Code | Column | Type | Null | Meaning |
|---|---|---|---|---|
| 1 | `RawStreamId` | u32 | no | Raw-record locator: the capture stream. |
| 2 | `RawSourceEpoch` | u32 | no | Raw-record locator: the source epoch. |
| 3 | `RawRecordOrdinal` | u64 | no | Raw-record locator: the acquisition ordinal (I1, I7). |
| 4 | `JournalRecordIndex` | u64 | yes | Position in the capture's journal, in stored order; across a live recording's chunks, in the order they were recorded (`contracts/store-v1.md` §7). |
| 5 | `FactKeyHigh` | u64 | no | Deterministic fact key, high half (I2). |
| 6 | `FactKeyLow` | u64 | no | Deterministic fact key, low half. |
| 7 | `SchemaCode` | u32 | no | Dictionary code: provider, event, version and fingerprint. |
| 8 | `Opcode` | u8 | no | The descriptor's opcode as delivered. |
| 9 | `NativeTicks` | i64 | no | The source clock reading in its original encoding (I8). |
| 10 | `SessionRelativeTicks` | i64 | yes | The derived session-relative instant; null when quarantined. |
| 11 | `HeaderProcessId` | i32 | no | The event header's process. Context, never the owner (§4.1). |
| 12 | `HeaderThreadId` | i32 | no | The event header's thread. |
| 13 | `ProcessorNumber` | u16 | no | Which processor delivered the record (§18.4). |
| 14 | `ActivityId` | guid | yes | Header activity id; null when the source carried none. |
| 15 | `RelatedActivityId` | guid | yes | Header related activity id. |
| 16 | `Mechanism` | u8 | no | `EN-Mechanism`. |
| 17 | `Layer` | u8 | no | `EN-Layer`, from the descriptor's source contract (I11). |
| 18 | `ObservationKind` | u8 | no | `EN-ObservationKind`. |
| 19 | `Direction` | u8 | no | `EN-Direction`. |
| 20 | `OwnerProcessId` | i32 | yes | The owner the record's own payload names. |
| 21 | `ResourceName` | text | yes | The bounded resource name as delivered. |
| 22 | `SourceIdentifier` | guid | yes | A 16-byte identifier the descriptor carried. |
| 23 | `EndpointAddressFamily` | u8 | yes | 4 or 6; what the address columns' bits mean. |
| 24 | `SourceEndpointAddress` | u32 | yes | The endpoint the source names as the origin. Which end that is depends on the descriptor: a TCP record names its owner's own endpoint, a UDP receive the datagram's sender (ADR-019). |
| 25 | `SourceEndpointPort` | u16 | yes | Its port. |
| 26 | `DestinationEndpointAddress` | u32 | yes | The endpoint the source names as the destination. |
| 27 | `DestinationEndpointPort` | u16 | yes | Its port. |
| 28 | `ByteValue` | i64 | yes | The byte measurement; null when the record carried none (R3). |
| 29 | `ByteDomain` | u8 | yes | `EN-ByteDomain`. Exactly one (I6). |
| 30 | `AccountingSide` | u8 | yes | `EN-AccountingSide`. |
| 31 | `MeasurementUnit` | u8 | yes | The unit of the byte value, carried rather than assumed (R2). |
| 32 | `ByteAvailability` | u8 | no | `EN-FieldAvailability`: why the value is absent when it is. |
| 33 | `StatusCode` | i64 | yes | The status the source reported. |
| 34 | `StatusAvailability` | u8 | no | Why the status is absent when it is. |
| 35 | `AttributionQuality` | u8 | no | `EN-QualityLevel`, attribution dimension. |
| 36 | `CorrelationQuality` | u8 | no | Correlation dimension. |
| 37 | `MeasurementQuality` | u8 | no | Measurement dimension. |
| 38 | `TimingQuality` | u8 | no | Timing dimension. |
| 39 | `Markers` | u16 | no | Row markers (§7). |

The four quality columns are never combined. A single confidence number would imply a calibration the
product does not have (P11, R2).

No column holds a status *domain*: §7.3 names one but §23 assigns it no enumeration, so a status is stored
as the code the source reported and nothing claims which domain it is in. That enumeration is owed.

### `source-fields-v1` (table 2)

This table carries the source correlation and object fields that do not fit `observation-v1`. Each row names
one observation by its raw locator and fact key, carries that observation's native reading, and states one
`EN-SourceField` meaning. A descriptor that does not admit a field creates no row for it; a declared field
whose record supplied no value creates a row with a null and an availability reason. The observation table's
39 columns and bytes do not change. A generation predating this table names no `fld-` dependency.

| Code | Column | Type | Null | Meaning |
|---|---|---|---|---|
| 1, 2, 3 | `RawStreamId`, `RawSourceEpoch`, `RawRecordOrdinal` | u32, u32, u64 | no | Locator of the observation carrying the field. |
| 5, 6 | `FactKeyHigh`, `FactKeyLow` | u64, u64 | no | Its fact key. |
| 9 | `NativeTicks` | i64 | no | The observation's source reading. |
| 40 | `SourceField` | u16 | no | `EN-SourceField` meaning; an unknown code refuses the row. |
| 41 | `FieldValue` | i64 | yes | Numeric source value, without interpretation or unit conversion. |
| 42 | `FieldText` | text | yes | Text source value, mutually exclusive with `FieldValue`. |
| 43 | `FieldAvailability` | u8 | no | `Present` with exactly one value; otherwise why neither value is present. |

Its fields, like the observations, are immutable source facts. A process binding may join them by locator
and fact key but never write its resolved instance into either table. The table uses the same checksums,
bitmaps, dictionary budget and variable-chunk fallback as table 1.

## 6. The measurement slot and the measurement

A byte measurement is two facts, not one, and the format keeps them apart (R2, R3, §5):

- **A declared slot.** The descriptor exposes a byte field. `ByteDomain`, `AccountingSide` and
  `MeasurementUnit` are present, whether or not this record supplied a value.
- **A value.** `ByteValue` is present and `ByteAvailability` is `Present`.

The three combinations a reader can meet:

| `ByteValue` | Labels | `ByteAvailability` | Means |
|---|---|---|---|
| present | present | `Present` | A measurement. Zero is an observed zero. |
| null | present | any other reason | An unknown **in a declared slot**. It is in the denominator. |
| null | null | `NotApplicable` | The descriptor has no byte field. It is in no denominator. |

Any other combination is refused. That is what makes a measurement-availability ratio mean something: the
denominator is the declared slots, so "12 MiB observed, measured on 63% of eligible contributions" is a
statement about availability and never about how much of the machine's traffic was captured.

A sum covers exactly one byte domain and one accounting side. Contributions in another domain or on another
side are excluded **and counted**, never added (I6, P3). Accumulation is checked, so a long high-volume
capture reports an overflow rather than wrapping into a smaller total that looks plausible (§10.2).

## 7. Row markers

`Markers` is a bit set. A bit outside this list refuses the row, so a marker written by a later minor
version cannot be read as unset.

| Bit | Marker |
|---|---|
| 0 | The resource name is a prefix; the source's was longer (I21). |
| 1 | The capture requested extended data for this record's provider, so an absence is a real absence. |
| 2 | At least one extended item the source carried is not in the evidence behind this row (I13). |
| 3 | The record's body was admitted under a policy that retained less than the source carried. |

## 8. Dictionaries

A dictionary's entries are sorted by their UTF-8 bytes and unique, so the file is a function of the value
set rather than of the order the values were met: two derivations over the same rows publish the same
dictionary bytes. A code is the entry's index. Absence is never a code — a column says so in its null
bitmap — so code 0 is an ordinary entry.

```text
dictionary file:
  [0, 64)                        header
  [64, 64 + 4*(entries+1))       value offsets, ascending, last equal to the value length
  <values>                       UTF-8, in code order
  [length-32, length)            SHA-256 over every preceding byte
```

| Offset | Width | Field |
|---|---|---|
| 0 | 8 | magic, `ICATDIC1` |
| 8 | 2 | format major, 1 |
| 10 | 2 | format minor, 0; segment minor 1 changed nothing here (§11) |
| 12 | 4 | required feature bits |
| 16 | 2 | dictionary id |
| 18 | 2 | kind: 1 `Utf8Text`, 2 `Schema` |
| 20 | 4 | entry count |
| 24 | 4 | offsets offset, always 64 |
| 28 | 4 | offsets length |
| 32 | 4 | values offset |
| 36 | 4 | values length |
| 40 | 4 | file length |
| 44 | 4 | CRC-32C over bytes `[0, 44)` |
| 48 | 4 | CRC-32C over the values |
| 52 | 4 | CRC-32C over the offsets |
| 56 | 8 | reserved, zero |

A `Schema` entry is `<provider GUID in `D` form>|<event id>|<version>|<fingerprint>`. The table travels with
the segment, because the machine reading it may not have the recording machine's manifests (§18.3).

**The dictionary budget has a fallback, not just a refusal** (§10.2). A text column whose segment has more
distinct values than a dictionary holds is stored in the variable chunk instead. The fallback is per column
and per segment: a segment can hold one chunk-encoded column and one dictionary-coded one. A column no row
has a value in needs no dictionary and is chunk-encoded with an empty chunk, so a generation never publishes
a dictionary that resolves nothing.

## 9. What a reader verifies, and when

At open, before a row is served:

- the header's magic, major version and required feature bits;
- the header's own CRC-32C;
- from minor 1, the directories' CRC-32C, before an entry is interpreted;
- the SHA-256 trailer over the whole file;
- every declared extent against the file's real length, and against the row count and each column's width;
- each column's known and unknown counts against its row count;
- every time block continues the block before it and together they cover every row;
- the time column itself: it is read, checked to be non-decreasing, and compared with the header's minimum
  and maximum and with every time block's. The ordering and the interval are not trusted because everything
  above them — a viewport's boundary arithmetic, a block skip, a range scan — rests on them.

On first read of any other column: its CRC-32C, and its null bitmap's; from minor 1, for a chunk-encoded column,
the variable chunk's too. A caller that reads two columns pays for two rather than for the whole file. The
per-column checksum is what distinguishes one damaged column from a damaged file; the trailer alone can only say
that something changed.

From minor 1, every byte a reader interprets is under a CRC-32C of its own: the header, both directories, each
column's values and null bitmap, and the variable chunk. Only alignment padding and the trailer are not. That is
what lets a reader check exactly the bytes it reads, rather than hash a whole file to trust one column of it. The
trailer is still written and still pins the whole file, and a store's manifest records its length and digest
(`contracts/store-v1.md` §3).

A dictionary-coded column whose dictionary was not supplied refuses rather than returning the code rendered
as a value. A row whose enumeration carries a code §23 does not define refuses. An empty segment is never
published at all: nothing observed is coverage, which the coverage ledger states, and a zero-row segment
would let absence be read as data (R21, P1).

## 10. Not defined at this version

- Entity binding revisions, relation revisions, operation revisions. They reuse the container under their
  own table id and column set.
- Compression. Every column at this version is uncompressed and directly mappable; §20.1's bounded decode
  buffers apply when a compressed encoding is added.
- Aggregate tiles and the multiresolution overview pyramid of §10.2.

Compaction and evidence leases, listed here while this format was frozen, are now `contracts/store-v1.md` §8: a
compaction rewrites rows unchanged, raw-record locator included, and a reader holds its generation under a lease. Neither
changed a byte of this format.

## 11. Minor versions

A major version is a layout: a reader refuses one it does not implement (§9). A minor version only fills words an
earlier minor reserved and wrote as zero; it never moves a field or changes what one means. So a reader reads any
minor of its major, and checks the words the segment's own minor declares. A reader that predates a minor never
reads the words it filled, and reads the file as it always did.

| Minor | Written by | Adds |
|---|---|---|
| 0 | revisions up to 148 | The header's CRC-32C, each column's CRC-32C and its null bitmap's, and the trailer. The directories and the variable chunk rest on the trailer alone. |
| 1 | revision 149 on | The directories' CRC-32C in the header's word at 124, and the variable chunk's in each chunk-encoded column's word at 44. |

A reader never infers a checksum from a minor-0 segment's zero words: minor 0 carries none, and its trailer is
checked at open. Sessions recorded before revision 149 keep their minor-0 segments until a later derivation or
compaction publishes new ones. Dictionaries stay at minor 0: their header, offsets and values each carried a
checksum from the start.
