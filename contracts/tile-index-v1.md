# InterCat tile index v1

Status: **implemented** in plan revision 456, minor 1 in revision 457. It holds §12.1 S4's deeper levels, beneath the
persisted overview (`contracts/overview-index-v1.md`), which is the top level. For each observation segment of a
generation, it keeps:

- the segment's timeline tiles at decimal levels, each with what its records' owners and channels bind to, tallied;
- the readings, mechanisms and bindings of the records of its finest tiles, beside them.

A zoom of a finished session counts each segment from these. It counts a tile whole wherever its records fall in one
column, and reads a tile's children, or a finest tile's records, only where a column boundary falls among them. A brush
counts a segment the same way at its two ends, adding a tile its interval holds whole from the tile's tallies. So
neither opens a segment, and each reads in proportion to what it draws rather than to the session (S3).

Like the overview, a tile index is a derived index. It holds counts, readings and bindings, never evidence (R1), and
the segments it describes, with the derivation the checkpoint holds, rebuild it (R20). One that is missing, stale or
unreadable costs time and never changes an answer.

## 1. What it holds

A record's reading here is its session time in presentation ticks: its nanoseconds over 100, truncated toward zero, as
every interval reads a row's time. A record with no usable session time is in no tile.

### Finest tiles

For each segment, its finest tiles are the ones revision 158 builds in memory (`SegmentTimeTiles`):

- tile `j` holds the timed records in `[j·w, (j+1)·w)`;
- `w` is the narrowest power of ten that spans the segment's timed readings in at most 20,480 tiles;
- only a tile that holds a record is kept.

A tile keeps its earliest and latest reading, and how many of its records are of each mechanism the segment's timed
records are of.

### Wider levels

Each level above is ten times wider. It groups the tiles below that share `floor(j/10)`. Its earliest and latest
reading are its first and last child's, and its counts are their sums. Levels go up to the first that holds at most 32
tiles.

### Records

Beside the finest tiles, the index keeps each timed record's reading, mechanism and bindings, tile by tile in row order.

### Bindings

A record's bindings are what a brush reads of them (`contracts/entities-v1.md`, `SessionIntervalQuery`), as the
derivation the generation's checkpoint holds made them (`contracts/derivation-checkpoint-v1.md`):

- its **owner key**: 0 when its owner binds to no instance; otherwise eight times its instance's place among the
  derivation's instances plus one, plus the binding's strength (`EN-RelationStrength`), all an evidence policy's
  admission reads;
- its **channel key**: 0 when it binds to no channel; otherwise its channel's number plus one.

The index states that derivation (§3, the directory), and a brush counts its bindings only under the same (§4).

### Tallies

Each tile, at every level, can keep its timed records' bindings tallied:

- for each owner key other than 0, and each mechanism, how many of its records have both;
- for each channel key other than 0, how many of its records have it, by the channel's number.

A wider tile's tallies are its children's, summed. A tile keeps them only where they take at most half the bytes of the
records beneath it (§3). Where records take turns among many processes and channels, a narrow tile's records each have
an owner and a channel of their own, and its tallies would take as many bytes as its records: a brush then counts it
from its children or its records, which cost it at most twice what its tallies would have.

### Segments that keep no tiles

A segment whose timed readings ever go backwards keeps no tiles, and a zoom reads its rows. So does one with no timed
record, which a zoom has nothing to count of. Each says so.

## 2. Files and publication

The dependency kind is `Index` (code 4 in `contracts/store-v1.md`), so a reader built before this contract accepts a
generation that names one and ignores it. The file name is:

```text
tiles-<publishing generation:D10>.bin
```

A generation names at most one tile index. It is published with the following, in the generation that publishes them
(`contracts/derivation-checkpoint-v1.md` §2) and by the same writers:

- the derivation checkpoint;
- the persisted overview;
- the operation index.

Those writers are an import, the end of a live follow, a finished recording, a compaction, a re-derivation and `icat
checkpoint`. Later publications treat a tile index as they treat the checkpoint:

- an additive generation does not carry it;
- a compaction or a retention that releases a segment releases it with the segments.

A generation that names the other three but no readable tile index of each of its segments is not current. That
includes every generation published before revision 456, a minor-0 index (revision 456's, which kept no bindings), and
one whose directory states a derivation other than the checkpoint's. Its writer's next publication, or `icat
checkpoint`, publishes all four again.

At 1,000,000 records of the scale gate's generator in four segments, a tile index took 6.2% of the segments' bytes,
10.9 bytes a record, and 6.1% at 10,000,000 in forty. Its records take turns among 200 connections, record by record,
so its narrower tiles keep no tallies (§1). Revision 456's index, which kept no bindings, took 2.9% of the same.

## 3. Bytes

Little-endian throughout. `str8` is an unsigned byte count followed by that many UTF-8 bytes. `guid` is 16 bytes in
.NET's `Guid` byte order. `CRC-32C` is the Castagnoli checksum journal-v1 uses. A file is:

```text
header        48 bytes
sections      one per segment, each starting where the one before ends
directory     where the last section ends
footer        the last 32 bytes
```

### Header

| Offset | Size | Field |
|---|---|---|
| 0 | 8 | magic `ICATTILE` |
| 8 | 2 | major, 1 |
| 10 | 2 | minor, 1 |
| 12 | 4 | reserved, zero |
| 16 | 16 | session (`guid`) |
| 32 | 8 | the generation whose segments it describes |
| 40 | 4 | reserved, zero |
| 44 | 4 | CRC-32C over bytes `[0, 44)` |

### Footer

| Offset | Size | Field |
|---|---|---|
| 0 | 8 | where the directory begins |
| 8 | 4 | the directory's length |
| 12 | 4 | how many sections |
| 16 | 4 | CRC-32C over the directory |
| 20 | 8 | reserved, zero |
| 28 | 4 | CRC-32C over the footer's bytes `[0, 28)` |

### Directory

The derivation the bindings were made under, then one entry per section, in ordinal order of segment name:

```text
str8 binding rule    str8 relation rule    i32 instances    i32 channels    32 bytes fingerprint
str8 segment name    i64 segment length    str8 segment digest    i64 section offset    i64 section length    (per section)
```

The rules are `ProcessInstanceIndex.BindingRule` and `TransportRelationIndex.RelationRule` of the build that wrote
the index. The fingerprint is SHA-256 over:

- each rule's UTF-8 bytes followed by a zero byte, the binding rule first;
- the number of instances as an `i32`, then each instance's identity as a `guid`, in the derivation's order;
- the number of channels as an `i32`;
- the number of paired relations as an `i32`, then for each, in the derivation's order, its channel number as an
  `i32` and its stable key's UTF-8 bytes followed by a zero byte;
- the number of unpaired connections as an `i32`, then each one's channel number and stable key the same way.

Two derivations with the same rules, instances and channels in the same order bind every record of the same segments
alike, so one's bindings count for the other.

### Sections

A section begins with its header:

```text
i32 rows    i32 untimed rows    u8 flags (bit 0: ordered, bit 1: timed; no other)
i64 earliest reading    i64 latest reading (both 0 when nothing is timed)    i64 finest tile width
u8 m: mechanisms    m × u16 mechanism codes, increasing
u8 L: levels        L × (i32 tiles, i64 where the level begins, from the section's start)
i64 where the records begin, from the section's start    i64 the records' length
i64 where the tallies begin, from the section's start    i64 the tallies' length
u32 CRC-32C over the header's bytes before it
```

A section that keeps no tiles has no mechanisms, no levels, no records and no tallies, and a width of 1. The tallies
end where the section does.

### Levels

The levels follow the header, the finest first, each beginning where the one before ends. A level is its tiles in
time order, in blocks of 32; the last block may hold fewer. A block is:

- its tiles;
- an `i32`: for the finest level, where its last tile's records end within the records, before their checksum; for a
  wider level, zero;
- an `i32`: where its tiles' tallies end within the tallies, before their checksum;
- a CRC-32C over the tiles and those two words.

A tile is:

```text
i64 earliest reading    i64 latest reading    i32 link    i32 children    i32 tallies    m × i32 count, one per mechanism
```

For a finest tile, `link` is where its first record begins within the records, and `children` is zero. For a wider
tile, `link` is the position of its first child in the level below, and `children` is how many it has: 1 to 10, the
next tiles there. `tallies` is where the tile's tallies begin within the tallies.

### Records

The records follow the levels. For each finest tile, in order, they hold its records in row order, then a CRC-32C over
those records' bytes, so a column boundary or a brush's end reads and checks only the tile it falls among. A record
is:

- its reading's increase over the record before it in its tile, as unsigned LEB128 (for a tile's first record the
  increase is over the tile's earliest reading, so it is zero);
- a `u8`: its mechanism's place among the section's mechanisms;
- its owner key, then its channel key, each as unsigned LEB128.

### Tallies

The tallies follow the records, level by level from the finest. For each block of a level they hold that block's
tiles' tallies, in time order, then a CRC-32C over those bytes. A tile's tallies are, each number as unsigned LEB128:

- how many owner entries, plus one, then each entry in order of owner key and then of mechanism: its key's increase
  over the entry before's (the first entry's over zero, so its key), its mechanism's place among the section's
  mechanisms as a `u8`, and how many records;
- how many channel entries, then each entry in order of channel number: its number's increase over the entry before's
  (the first entry's over zero), and how many records.

A tile whose tallies so written would take more than half the bytes of the records beneath it - its own, or its
children's - keeps none, and its tallies are a single zero instead.

## 4. Reading

A tile index carries its own checksums, so it is read by the block and never hashed whole first (store-v1 §6):

- its header, footer and directory are checked when it is opened;
- a section's header when a zoom or a brush first needs it;
- a block when a zoom or a brush reads it, and a block's tallies when a brush counts one of its tiles from them;
- a finest tile's records when a column boundary or a brush's end falls among them.

### What is refused

A reader refuses an index, or a part of it, that:

- does not begin with the magic;
- is another major, or minor 0, which keeps no bindings;
- describes another session;
- states more instances or channels than a binding packs: 16,777,214 and 134,217,726;
- fails a checksum;
- lists a segment twice or out of order, or places a section outside the sections or leaves bytes between them;
- has a section header that does not lay out its section:
  - levels that do not begin where the one before ends;
  - a level of no tile;
  - more than 20,481 finest tiles;
  - a level below the top of at most 32 tiles, or a top level of more;
  - records longer than 19 bytes for each timed row and 4 for each finest tile;
  - tallies that do not end where the section does;
  - flags or mechanisms it does not define;
- holds a tile that:
  - holds no record;
  - begins after it ends;
  - does not follow the tile before it in time;
- holds a finest tile whose records do not follow the one before it's and its checksum;
- holds a wider tile whose children are not the next ones, or lie outside the level below;
- holds tiles whose tallies do not follow one another, or a block whose tallies lie outside the section's;
- holds records that are not their tile's:
  - its first record is not at the tile's earliest reading;
  - a record lies past the tile's latest;
  - the last record is not at the latest;
  - their count is not the tile's;
  - a record's mechanism is not among the section's, or its owner key is 1 to 7, or names an instance or a channel
    beyond the stated derivation's;
- holds tallies that are not their tile's: entries out of order, an owner or channel beyond the stated derivation's, a
  mechanism not among the section's, a count of zero or above the tile's records, owner or channel counts that sum
  above them, or bytes after its last entry.

A zoom or a brush that meets any of these stops reading the index for the generation, says why, and counts from the
segments. The window opens a session without hashing a file that checks itself. The command line opens a session
hashing every file, and so opens the generation before one whose tile index changed (store-v1 §6).

### Counting

A zoom counts from the index only when it describes every observation segment the generation names, by name, length and
digest. It counts each segment from its top level down:

- a tile none of whose records lies in the interval is passed over;
- a tile all of whose records fall in one column adds its counts there;
- any other tile gives way to its children, or, at the finest level, to its records, each counted in the column holding
  its reading.

That is exactly what reading every row counts (I4, §10.3's exact boundary fragments). A zoom reads, for each segment
its interval meets, its top block and, for each column boundary among its records, at most one block per level and
one tile's records.

At 1,000,000 records, a reopened session's first zoom took the following in revision 456, against the 56, 28 and 22 ms
that counting from the segments' own tiles took:

| Zoom | Time |
|---|---|
| The whole extent in 1,000 columns | 16.5 ms |
| Half of it | 10.3 ms |
| A hundredth in 256 columns | 1.1 ms |

### Brushing

A brush counts the records inside its interval: every one observed, each by the instance its owner binds to where the
evidence policy admits the binding, and each of a paired TCP channel the policy admits by that channel. It counts from
the index only when the index describes every observation segment the generation names, as a zoom does, and states the
derivation the brush binds with: the same rules, instance and channel counts and fingerprint. It then counts each
segment from its top level down:

- a tile none of whose records lies in the interval is passed over;
- a tile all of whose records lie in it and that keeps tallies adds them: its counts' sum observed, each owner entry
  whose strength the policy admits to its instance and mechanism, and each channel entry the brush draws to that
  channel;
- any other tile gives way to its children, or, at the finest level, to its records, each counted as its row would be.

That is exactly what reading every row counts. A brush reads, for each segment its interval meets, its top block and,
at each of its two ends, at most two blocks per level with their tallies and one tile's records - and, beneath a tile
that keeps no tallies, the records that cost at most twice what they would have. An index whose
derivation is not the brush's is not counted by the brush, which reads the rows of the segments its interval meets, and
the generation's writer publishes the index again (§2).

At 1,000,000 records of the scale gate's generator in four segments, a reopened session's brushes took the following,
the first and then the median of fifteen, against counting from the segments' rows:

| Brush | From the tiles | From the segments |
|---|---|---|
| The whole extent | 36.7 ms, then 3.5 ms | 586 ms, then 19.8 ms |
| Half of it | 4.9 ms, then 4.1 ms | 217 ms, then 14.0 ms |
| A hundredth | 3.7 ms, then 3.1 ms | 177 ms, then 8.6 ms |

At 10,000,000 records in forty segments, the scale gate's brushed ranking, which counts its interval and then ranks
the graph and the table from the counts, took 6.3 ms the first time and 9.5 ms at the 95th percentile, from 631 and 273
ms (§12's budget is 250 ms).

## 5. What is not defined at this version

- A focused timeline's columns, which count one owner's, direction's or channel's records per column and are not what
  tiles hold (§10.3). A focus reads its rows, from the segments the interval meets.
- Byte sums, which a zoom's lanes read from the segments.
- The tiles of a live generation, which publishes no checkpoint until its writer finishes. Its zooms build each
  segment's tiles from its rows.
- A level spanning segments. A wide zoom over many segments reads each one's top block, and each section's header when
  first opened.
