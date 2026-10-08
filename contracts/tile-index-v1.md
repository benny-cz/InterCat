# InterCat tile index v1

Status: **implemented** in plan revision 456, minor 1 in revision 457, minor 2 in revision 459 and minor 3 in revision
460; a group's lanes count from it since revision 458, a process's direction rows since revision 459, and a channel's
ends since revision 460. It holds §12.1 S4's deeper levels, beneath the persisted overview
(`contracts/overview-index-v1.md`), which is the top level. For each observation segment of a generation, it keeps:

- the segment's timeline tiles at decimal levels, each with what its records' owners and channels bind to, tallied;
- the readings, mechanisms, directions, ends and bindings of the records of its finest tiles, beside them.

A zoom of a finished session counts each segment from these. It counts a tile whole wherever its records fall in one
column, and reads a tile's children, or a finest tile's records, only where a column boundary falls among them. A brush
counts a segment the same way at its two ends, adding a tile its interval holds whole from the tile's tallies. An owner
focus - a group's or one process's - counts its records, lanes and directions the same way per column, and so does a
channel's focus its records and its two ends' lanes. So none opens a segment, and each reads in proportion to what it
draws rather than to the session (S3).

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

Beside the finest tiles, the index keeps each timed record's reading, mechanism, source direction (`EN-Direction`), end
and bindings, tile by tile in row order.

A record's **end** is which end of its connection or flow it was made at, as `TransportRelationIndex.EndsOf` reads it
from the record's own endpoints: the **first**, whose own endpoint sorts first, which is a paired channel's first end;
the **second**; or **none**, for a record with no end of its own - another mechanism, or an incomplete endpoint. A record
bound to a channel has an end.

### Bindings

A record's bindings are what a brush reads of them (`contracts/entities-v1.md`, `SessionIntervalQuery`), as the
derivation the generation's checkpoint holds made them (`contracts/derivation-checkpoint-v1.md`):

- its **owner key**: 0 when its owner binds to no instance; otherwise eight times its instance's place among the
  derivation's instances plus one, plus the binding's strength (`EN-RelationStrength`), all an evidence policy's
  admission reads;
- its **channel key**: 0 when it binds to no channel; otherwise its channel's number plus one.

The index states that derivation (§3, the directory), and a brush counts its bindings only under the same (§4).

### Tallies

Each tile, at every level, can keep its timed records' bindings tallied, in two parts kept apart:

- its **owner tallies**: for each owner key other than 0, each mechanism and each direction, how many of its records
  have all three;
- its **channel tallies**: for each channel key other than 0, each mechanism, each direction and each end, how many of
  its records have all four, by the channel's number.

A wider tile's tallies are its children's, summed. A tile keeps each part only where it takes at most a third of the
bytes of the records beneath it (§3), so both together take at most two thirds of them. Where records take turns among
many processes and channels, a narrow tile's records each have an owner and a channel of their own, and its tallies
would hold an entry for nearly every record: a query then counts it from its children or its records, which cost it at
most three times what those tallies would have. A brush counts a tile from both parts, an owner focus from its owner
tallies, and a channel's focus from its channel tallies, so each reads only the part it counts, and neither part's size
keeps the other from being kept.

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
includes every generation published before revision 456, an index of minor 0 (revision 456's, which kept no bindings),
minor 1 (revision 457's, which kept no directions) or minor 2 (revision 459's, which kept no ends), and one whose
directory states a derivation other than the checkpoint's. Its writer's next publication, or `icat checkpoint`,
publishes all four again.

At 1,000,000 records of the scale gate's generator in four segments, a tile index took 7.7% of the segments' bytes,
13.5 bytes a record, and 7.6% at 10,000,000 in forty: what revision 459's took, which kept no ends, since a record's
kind - its mechanism, end and direction - takes the one byte its mechanism alone took. Its records take turns among 200
connections, record by record, so only its two widest levels keep tallies (§1). Revision 456's index, which kept no
bindings, took 2.9% of the same, and revision 457's, which kept no directions, 6.2%. Had each part been kept wherever it
took at most half the records' bytes, as tallies were before minor 3, narrower tiles would have kept tallies with an
entry for nearly every record, which spare no reading, and the index would have taken 12.8%.

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
| 10 | 2 | minor, 3 |
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
i64 where the records begin, from the section's start           i64 the records' length
i64 where the owner tallies begin, from the section's start     i64 the owner tallies' length
i64 where the channel tallies begin, from the section's start   i64 the channel tallies' length
u32 CRC-32C over the header's bytes before it
```

A section that keeps no tiles has no mechanisms, no levels, no records and no tallies, and a width of 1. The owner
tallies begin where the records end, the channel tallies where the owner tallies end, and the channel tallies end where
the section does.

### Levels

The levels follow the header, the finest first, each beginning where the one before ends. A level is its tiles in
time order, in blocks of 32; the last block may hold fewer. A block is:

- its tiles;
- an `i32`: for the finest level, where its last tile's records end within the records, before their checksum; for a
  wider level, zero;
- an `i32`: where its tiles' owner tallies end within the owner tallies, before their checksum;
- an `i32`: where its tiles' channel tallies end within the channel tallies, before their checksum;
- a CRC-32C over the tiles and those three words.

A tile is:

```text
i64 earliest reading    i64 latest reading    i32 link    i32 children    i32 owner tallies    i32 channel tallies
m × i32 count, one per mechanism
```

For a finest tile, `link` is where its first record begins within the records, and `children` is zero. For a wider
tile, `link` is the position of its first child in the level below, and `children` is how many it has: 1 to 10, the
next tiles there. `owner tallies` and `channel tallies` are where the tile's owner and channel tallies begin within
them.

### Records

The records follow the levels. For each finest tile, in order, they hold its records in row order, then a CRC-32C over
those records' bytes, so a column boundary or a brush's end reads and checks only the tile it falls among. A record
is:

- its reading's increase over the record before it in its tile, as unsigned LEB128 (for a tile's first record the
  increase is over the tile's earliest reading, so it is zero);
- its **kind**, as unsigned LEB128: its mechanism's place among the section's mechanisms times 32, plus its end times 8
  (0 for none, 1 for the first, 2 for the second), plus its direction code - its `EN-Direction` code, or 7 for a code
  of 7 or more. While a section's mechanisms are four or fewer, every kind takes one byte;
- its owner key, then its channel key, each as unsigned LEB128.

### Tallies

The owner tallies follow the records, and the channel tallies follow them, each level by level from the finest. For
each block of a level, each holds that block's tiles' tallies of its part, in time order, then a CRC-32C over those
bytes. Each number is unsigned LEB128. A tile's owner tallies are how many entries, plus one, then each entry in order
of owner key, then of mechanism, then of direction:

- its key's increase over the entry before's (the first entry's over zero, so its key);
- its mechanism's place among the section's mechanisms times 8, plus its direction code;
- how many records.

A tile's channel tallies are how many entries, plus one, then each entry in order of channel number, then of
mechanism, then of end, then of direction:

- its number's increase over the entry before's (the first entry's over zero, so its number);
- its kind, as a record's is;
- how many records.

Either part of a tile's tallies that so written would take more than a third of the bytes of the records beneath it -
its own, or its children's - is not kept, and is a single zero instead.

## 4. Reading

A tile index carries its own checksums, so it is read by the block and never hashed whole first (store-v1 §6):

- its header, footer and directory are checked when it is opened;
- a section's header when a zoom or a brush first needs it;
- a block when a zoom or a brush reads it, and a block's owner or channel tallies when a query counts one of its tiles
  from them;
- a finest tile's records when a column boundary or a brush's end falls among them. They are read with the records of
  the tiles after them in their block, so a count that reads a block's every tile reads its records once, and each
  tile's are checked as they are counted.

A reader keeps up to 16,384 blocks decoded for the queries after it, about 25 MB: every block of a 10M-record session's
tiles, which a group's lanes at 2,000 columns read most of.

### What is refused

A reader refuses an index, or a part of it, that:

- does not begin with the magic;
- is another major, or minor 0, 1 or 2, which keep no bindings, no directions or no ends;
- describes another session;
- states more instances or channels than a binding packs: 16,777,214 and 134,217,726;
- fails a checksum;
- lists a segment twice or out of order, or places a section outside the sections or leaves bytes between them;
- has a section header that does not lay out its section:
  - levels that do not begin where the one before ends;
  - a level of no tile;
  - more than 20,481 finest tiles;
  - a level below the top of at most 32 tiles, or a top level of more;
  - records longer than 20 bytes for each timed row and 4 for each finest tile;
  - owner tallies that do not begin where the records end, channel tallies that do not begin where the owner tallies
    end, or channel tallies that do not end where the section does;
  - flags or mechanisms it does not define;
- holds a tile that:
  - holds no record;
  - begins after it ends;
  - does not follow the tile before it in time;
- holds a finest tile whose records do not follow the one before it's and its checksum;
- holds a wider tile whose children are not the next ones, or lie outside the level below;
- holds tiles whose owner or channel tallies do not follow one another, or a block whose tallies lie outside the
  section's;
- holds records that are not their tile's:
  - its first record is not at the tile's earliest reading;
  - a record lies past the tile's latest;
  - the last record is not at the latest;
  - their count is not the tile's;
  - a record's kind names a mechanism not among the section's or an end past the second, or its owner key is 1 to 7,
    or names an instance or a channel beyond the stated derivation's;
- holds tallies that are not their tile's: entries out of order, an owner or channel beyond the stated derivation's, a
  mechanism not among the section's, a kind no record can have, a count of zero or above the tile's records, owner or
  channel counts that sum above them, or bytes after its last entry.

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
- a tile all of whose records lie in it and that keeps both its owner and its channel tallies adds them: its counts'
  sum observed, each owner entry whose strength the policy admits to its instance and mechanism, and each channel entry
  the brush draws to that channel;
- any other tile gives way to its children, or, at the finest level, to its records, each counted as its row would be.

That is exactly what reading every row counts. A brush reads, for each segment its interval meets, its top block and,
at each of its two ends, at most two blocks per level with their tallies and one tile's records - and, beneath a tile
that keeps no tallies, the records that cost at most three times what they would have. An index whose
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
ms (§12's budget is 250 ms). Since minor 3, whose brush reads a tile's owner and channel tallies, the latter by end and
direction, it took 14 ms the first time and 14 to 15 ms at the 95th percentile.

### An owner focus

An owner focus - a group's, or one process's - counts the records whose owner binds to one of its members, where the
evidence policy admits the binding, of one mechanism and in one source direction where it names them: in the view's
columns, by mechanism; for a group, in each member's lane - or the lane its folded members share - in the lanes'
columns; and for one process, in its row of each direction. Past §6.2's cell budget a group's lanes have columns of
their own over the same interval, fewer and wider, whose boundaries need not be the view's. An owner focus counts from
the index under the same conditions as a brush, and each segment from its top level down:

- a tile none of whose records lies in the columns' interval is passed over;
- a tile all of whose records fall in one column of the view's and one of the lanes', and that keeps owner tallies,
  adds each owner entry of a member whose strength the policy admits there, of the mechanism and direction the focus
  names, to the focus, to that member's lane and to its direction's row;
- any other tile gives way to its children, or, at the finest level, to its records, each counted as its row would be.

That is exactly what reading every row counts. A direction code that no direction is refuses the index for a count by
direction, as reading the row refuses it.

Each segment's section is counted by a worker of its own, side by side, as a focus that reads rows counts its segments.
At 10,000,000 records of the scale gate's generator in forty segments, on a 4-core machine, a 40-process group's lanes
at the window's 256 columns took 52 to 61 ms the first time, against 850 ms reading its rows, and 22 to 25 ms at the
median after; at 2,000 columns, 52 to 77 ms the first time and 67 to 101 ms at the 95th percentile, over three runs of
the scale gate. The busiest process's direction rows at 256 columns took 156 ms the first time, against 2.5 s reading
its rows, and about 45 ms after, against about 30. Reading no segment, the session's working set at the end of the gate
was 312 to 323 MB, against 1.35 GB.

### A channel's focus

A channel's focus - a paired TCP channel's, or a connection's whose other end the capture does not hold - counts the
records bound to its channel, of one mechanism, in one source direction and, for a paired channel, at one end where it
names them: in the view's columns, by mechanism; and for a paired channel, at each of its two ends, every record made
there and its outbound and inbound records apart. It counts from the index under the same conditions as a brush, and
each segment from its top level down:

- a tile none of whose records lies in the columns' interval is passed over;
- a tile all of whose records fall in one column, and that keeps channel tallies, adds each entry of the channel there,
  of the mechanism, direction and end the focus names, to the focus and to its end's lanes;
- any other tile gives way to its children, or, at the finest level, to its records, each counted as its row would be.

That is exactly what reading every row counts. A record of a paired channel whose ends are counted that names no end
of it refuses the index, as reading its row refuses it, and so does a direction code that no direction is. A focus
narrowed to such a code reads its rows.

At 10,000,000 records of the scale gate's generator in forty segments, on a 4-core machine, the busiest connection's two
ends at the window's 256 columns took 192 ms the first time, against 2.9 s reading its rows, and 22 to 84 ms after,
against 64 to 316 ms; the session's working set was 151 MB, against 859 MB. The scale gate, which measures them after
its other queries, took 55 to 87 ms the first time and 27 to 36 ms at the 95th percentile, over three runs.

## 5. What is not defined at this version

- The focus of an operation, or of a channel and owners at once, whose columns count what the tallies do not hold apart
  (§10.3): an operation's records, a channel's records of some processes. Such a focus reads its rows, from the
  segments the interval meets. So, at this version, does a focus on the whole session's records of one mechanism or one
  source direction: a tile's counts hold the first, and no tally the second.
- Byte sums, which a zoom's lanes read from the segments.
- The tiles of a live generation, which publishes no checkpoint until its writer finishes. Its zooms build each
  segment's tiles from its rows.
- A level spanning segments. A wide zoom over many segments reads each one's top block, and each section's header when
  first opened.
