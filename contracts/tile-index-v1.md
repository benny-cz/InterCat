# InterCat tile index v1

Status: **implemented** in plan revision 456. It holds §12.1 S4's deeper levels, beneath the persisted overview
(`contracts/overview-index-v1.md`), which is the top level. For each observation segment of a generation, it keeps:

- the segment's timeline tiles at decimal levels;
- the readings and mechanisms of the records of its finest tiles, beside them.

A zoom of a finished session counts each segment from these. It counts a tile whole wherever its records fall in one
column, and reads a tile's children, or a finest tile's records, only where a column boundary falls among them. So it
opens no segment, and reads in proportion to what it draws rather than to the session (S3).

Like the overview, a tile index is a derived index. It holds counts and readings, never evidence (R1), and the segments
it describes rebuild it (R20). One that is missing, stale or unreadable costs time and never changes an answer.

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

Beside the finest tiles, the index keeps each timed record's reading and mechanism, tile by tile in row order.

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
includes every generation published before revision 456. Its writer's next publication, or `icat checkpoint`,
publishes all four again.

At 1,000,000 records in four segments, a tile index took 2.0% of the segments' bytes, 3.6 bytes a record.

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
| 10 | 2 | minor, 0 |
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

One entry per section, in ordinal order of segment name. Each entry is:

```text
str8 segment name    i64 segment length    str8 segment digest    i64 section offset    i64 section length
```

### Sections

A section begins with its header:

```text
i32 rows    i32 untimed rows    u8 flags (bit 0: ordered, bit 1: timed; no other)
i64 earliest reading    i64 latest reading (both 0 when nothing is timed)    i64 finest tile width
u8 m: mechanisms    m × u16 mechanism codes, increasing
u8 L: levels        L × (i32 tiles, i64 where the level begins, from the section's start)
i64 where the records begin, from the section's start    i64 the records' length
u32 CRC-32C over the header's bytes before it
```

A section that keeps no tiles has no mechanisms, no levels and no records, and a width of 1.

### Levels

The levels follow the header, the finest first, each beginning where the one before ends. A level is its tiles in
time order, in blocks of 32; the last block may hold fewer. A block is:

- its tiles;
- an `i32`: for the finest level, where its tiles' records end within the records, before their checksum; for a
  wider level, zero;
- a CRC-32C over the tiles and that word.

A tile is:

```text
i64 earliest reading    i64 latest reading    i32 link    i32 children    m × i32 count, one per mechanism
```

For a finest tile, `link` is where its first record begins within the records, and `children` is zero. For a wider
tile, `link` is the position of its first child in the level below, and `children` is how many it has: 1 to 10, the
next tiles there.

### Records

The records follow the levels. For each block of finest tiles they hold that block's tiles' records, in row order, then
a CRC-32C over those records' bytes. A record is:

- its reading's increase over the record before it in its tile, as unsigned LEB128 (for a tile's first record the
  increase is over the tile's earliest reading, so it is zero);
- a `u8`: its mechanism's place among the section's mechanisms.

## 4. Reading

A tile index carries its own checksums, so it is read by the block and never hashed whole first (store-v1 §6):

- its header, footer and directory are checked when it is opened;
- a section's header when a zoom first needs it;
- a block when a zoom reads it;
- a block of records when a column boundary falls among one of its tiles.

### What is refused

A reader refuses an index, or a part of it, that:

- does not begin with the magic;
- is another major;
- describes another session;
- fails a checksum;
- lists a segment twice or out of order, or places a section outside the sections or leaves bytes between them;
- has a section header that does not lay out its section:
  - levels that do not begin where the one before ends;
  - a level of no tile;
  - more than 20,481 finest tiles;
  - a level below the top of at most 32 tiles, or a top level of more;
  - records longer than 11 bytes for each timed row and 4 for each block;
  - flags or mechanisms it does not define;
- holds a tile that:
  - holds no record;
  - begins after it ends;
  - does not follow the tile before it in time;
- holds a finest tile whose records do not follow the one before it's;
- holds a wider tile whose children are not the next ones, or lie outside the level below;
- holds records that are not their tile's:
  - its first record is not at the tile's earliest reading;
  - a record lies past the tile's latest;
  - the last record is not at the latest;
  - their count is not the tile's.

A zoom that meets any of these stops reading the index for the generation, says why, and counts from the segments. The
window opens a session without hashing a file that checks itself. The command line opens a session hashing every
file, and so opens the generation before one whose tile index changed (store-v1 §6).

### Counting

A zoom counts from the index only when it describes every observation segment the generation names, by name, length and
digest. It counts each segment from its top level down:

- a tile none of whose records lies in the interval is passed over;
- a tile all of whose records fall in one column adds its counts there;
- any other tile gives way to its children, or, at the finest level, to its records, each counted in the column holding
  its reading.

That is exactly what reading every row counts (I4, §10.3's exact boundary fragments). A zoom reads, for each segment
its interval meets, its top block and, for each column boundary among its records, at most one block per level and
one block of records.

At 1,000,000 records, a reopened session's first zoom took the following, against the 56, 28 and 22 ms that counting
from the segments' own tiles took:

| Zoom | Time |
|---|---|
| The whole extent in 1,000 columns | 16.5 ms |
| Half of it | 10.3 ms |
| A hundredth in 256 columns | 1.1 ms |

## 5. What is not defined at this version

- Focused counts, which filter rows by owner, direction or channel, and are not what tiles hold (§10.3). A focus reads
  its rows, from the segments the interval meets.
- Byte sums, which a zoom's lanes read from the segments.
- The tiles of a live generation, which publishes no checkpoint until its writer finishes. Its zooms build each
  segment's tiles from its rows.
- A level spanning segments. A wide zoom over many segments reads each one's top block, and each section's header when
  first opened.
