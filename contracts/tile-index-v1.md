# InterCat tile index v1

Status: **implemented** in plan revision 456, minor 1 in revision 457, minor 2 in revision 459, minor 3 in revision 460,
minor 4 in revision 701, minor 5 in revision 704 and minor 6 in revision 706; a group's lanes count from it since
revision 458, a process's direction rows since revision 459, a channel's ends since revision 460, every query from a
level spanning the segments since revision 701, a zoom's byte lanes and an interval's bytes from its byte sums since
revision 704, and a brush's bytes by process and channel end from its byte tallies since revision 706. It holds
§12.1 S4's deeper levels, beneath the persisted overview (`contracts/overview-index-v1.md`), which is the top level. For
each observation segment of a generation, it keeps:

- the segment's timeline tiles at decimal levels, each with what its records' owners and channels bind to, tallied, and
  the bytes its records measured, summed and tallied by owner, channel and side;
- the readings, mechanisms, directions, ends, bindings and measures of the records of its finest tiles, beside them.

Where two or more segments hold timed records, it keeps a level spanning them as well: the whole generation's tiles, at
decimal levels, with their counts, tallies, byte sums and byte tallies but no records of their own.

A zoom of a finished session counts each segment from these. It counts a tile whole wherever its records fall in one
column, and reads a tile's children, or a finest tile's records, only where a column boundary falls among them. A zoom's
byte lanes add up a tile's byte sums the same way, and read its records' measures only where a boundary falls among
them. A brush counts a segment the same way at its two ends, adding a tile its interval holds whole from the tile's
tallies, and a brush ranked by bytes measures it so, adding such a tile's bytes from its byte tallies. An owner
focus - a group's or one process's - counts its records, lanes and directions the same way per column, and so does a
channel's focus its records and its two ends' lanes. Over a level spanning the segments, each counts that level's tiles
first, whole where it can, and the segments only beneath the ranges of its finest tiles it could not. So none opens a
segment, and each reads in proportion to what it draws rather than to the session, or to how many segments hold it
(S3).

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

Beside the finest tiles, the index keeps each timed record's reading, mechanism, source direction (`EN-Direction`), end,
bindings and, since minor 5, measure (§1, Bytes), tile by tile in row order.

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
bytes of the records beneath it but their measures, which only a sum of bytes needs (§3), so both together take at most
two thirds of them, and a tile keeps its tallies where minor 4 kept them. Where records take turns among
many processes and channels, a narrow tile's records each have an owner and a channel of their own, and its tallies
would hold an entry for nearly every record: a query then counts it from its children or its records, which cost it at
most three times what those tallies would have. A brush counts a tile from both parts, an owner focus from its owner
tallies, and a channel's focus from its channel tallies, so each reads only the part it counts, and neither part's size
keeps the other from being kept.

### Bytes

Since minor 5, a record's **measure** is what reading its row measures of its transport-observed bytes (`metrics-v1` §4,
`SegmentMeasurement.MeasureDomainByGroup`): none, for a record stating no transport-observed byte slot; its accounting
side (`EN-AccountingSide`), for one declaring a size it did not record; or its side and the bytes it measured.

Each tile, at every level, keeps its timed records' measures summed: for each mechanism and accounting side its records
state, how many measured their bytes, how many declared a size they did not record, and the bytes they measured. A wider
tile's sums are its children's. A tile keeps its sums however few its records, for they are at most four for each of its
mechanisms.

A segment keeps no bytes where reading its rows' bytes would refuse one: a transport-observed record measured in a unit
other than the segment's others are, or stating no side or unit §23 defines, or no reason for a value it lacks, or a
side's bytes past what a sum holds. Its section's tiles then keep no sums and its records no measures, and a query reads
its rows, which refuse as they do without an index. A checkpoint measures every timed segment's bytes as it publishes
(`contracts/overview-index-v1.md` minor 2), and publishes nothing where that refuses a row, so the sections of a tile
index it publishes keep their bytes; one keeping none is read as this says.

### Byte tallies

Since minor 6, each tile, at every level, also keeps its timed records' measures tallied: for each owner key, channel key
and accounting side its records state - 0 among the keys, for a record bound to no instance or to no channel - how many
measured their bytes, how many declared a size they did not record, and the bytes they measured. A wider tile's byte
tallies are its children's, summed. A section keeps them wherever it keeps its byte sums, whatever their size: where a
tile's records each have an owner and a channel of their own, they hold an entry for nearly each, and cost a brush
about what reading its records would.

They hold what a brush ranked by bytes measures (`SessionByteRanking`): each record's bytes counted for the instance its
owner binds to, where the evidence policy admits the binding's strength, which its owner key carries; and, where the
brush draws its channel, for that channel's end at that instance. A record whose owner the policy does not admit is
counted as unattributed, as reading its row counts it.

### Segments that keep no tiles

A segment whose timed readings ever go backwards keeps no tiles, and a zoom reads its rows. So does one with no timed
record, which a zoom has nothing to count of. Each says so.

### A level spanning the segments

Since minor 4, where two or more of a generation's segments hold a timed record and none's timed readings go backwards,
the index keeps a level spanning them all, in a section of its own:

- its finest tile `j` holds every segment's timed records in `[j·W, (j+1)·W)`, and only a tile that holds a record is
  kept;
- `W` is the narrowest power of ten no narrower than any segment's finest tiles that spans the generation's timed
  readings, from the earliest to the latest, in at most 2,048 tiles and in at most one tile for each 256 timed records;
- each level above is ten times wider, as a segment's are, up to the first that holds at most 32 tiles.

A tile keeps its earliest and latest reading, how many of its records are of each mechanism the level's records are of,
and its owner and channel tallies, as a segment's tile does, each part kept where it takes at most a third of the bytes
of the records beneath it in the segments' sections, their measures left out; and since minor 5, where every section it
spans keeps its bytes and no sum passes what it holds, its byte sums; and since minor 6, where it keeps its byte sums
and its finest tiles' byte tallies fit the same bound as their tallies (below), its byte tallies. It keeps no records: a
query counts a finest tile it cannot count whole from the sections whose timed readings meet the tile's. The section also states, for each segment's section holding a timed record, its earliest and
latest reading and how many timed records it holds, which find the sections a range meets without reading their headers.

Every tile of a segment is aligned at a power of ten no wider than `W`, so its tile of width `W` - its own, or one summed
above its top - is exactly what it holds of the spanning tile with the same readings, and the spanning tile is the sum of
those, as reading every record would make it. While the level is written, its finest tiles hold at most 2,097,152 tally
entries, about 24 MB; past that bound they are made ten times wider, which sums each process's and channel's entries,
until they fit. Where they do not fit even once a tile is as wide as the readings' span, or a count passes what a tile's
word holds, the index keeps no such level, and every query counts its segments' sections as minor 3 did. Its finest
tiles' byte tallies are held to the same 2,097,152 entries once the tiles are as wide as their tallies make them, but
the tiles are not made wider for them: past the bound, or where a sum passes what it holds, the level keeps no byte
tallies, and a brush ranked by bytes measures its interval from the segments' sections.

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
directory states a derivation other than the checkpoint's. An index of minor 3 (revision 460's, which keeps no level
spanning the segments), minor 4 (revision 701's, which keeps no byte sums) or minor 5 (revision 704's, which keeps no
byte tallies) is read - its records counted from its segments' sections or the level spanning them, a zoom's byte lanes
added from its byte sums where it keeps them, and a brush's bytes measured from the segments' rows - but is not
current either. Its writer's next publication, or `icat checkpoint`, publishes all four again.

At 1,000,000 records of the scale gate's generator in four segments, a tile index took 7.7% of the segments' bytes,
13.5 bytes a record, and 7.6% at 10,000,000 in forty: what revision 459's took, which kept no ends, since a record's
kind - its mechanism, end and direction - takes the one byte its mechanism alone took. Its records take turns among 200
connections, record by record, so only its two widest levels keep tallies (§1). Revision 456's index, which kept no
bindings, took 2.9% of the same, and revision 457's, which kept no directions, 6.2%. Had each part been kept wherever it
took at most half the records' bytes, as tallies were before minor 3, narrower tiles would have kept tallies with an
entry for nearly every record, which spare no reading, and the index would have taken 12.8%.

Since minor 4 the same million records' index took 10.0% of the segments' bytes, 17.5 bytes a record: its level spanning
the four segments took 3.9 MB of the 17 MB, 600 finest tiles a tenth of a second wide, each holding about 1,700 records
and keeping both parts of its tallies, and the two levels above them. The level's share falls as the session grows: it
has at most 2,048 finest tiles whatever the records' count. In 400 segments of 2,500 records, whose narrower sections
keep fewer tallies and more headers, the index took 47.7% of the segments' bytes, the spanning level the same 3.9 MB.

Since minor 5 the same million records' index took 12.0% of the segments' bytes, 21.1 bytes a record: each record's
measure takes about three bytes, and a byte for a record measuring none, and the tiles' byte sums 463 KiB, 14 KiB of
them the spanning level's. Its tallies are kept where minor 4 kept them. Had they been weighed against the records'
bytes with their measures, tiles of a few hundred records would have kept them too, and the index would have taken
15.4%. In 400 segments of 2,500 records the index took 58.7% of the segments' bytes, 9.6 MB of its 100 MB the byte sums
of their narrow tiles, each of which keeps its mechanisms' sides however few its records.

Since minor 6 the same million records' index took 12.7% of the segments' bytes, 22.3 bytes a record: its byte tallies
took 1.2 MB. Each finest tile of a few hundred records sent and received by a few dozen processes over their
connections holds an entry for each process, channel and side among them, and a wider tile those of its children,
merged.

## 3. Bytes

Little-endian throughout. `str8` is an unsigned byte count followed by that many UTF-8 bytes. `guid` is 16 bytes in
.NET's `Guid` byte order. `CRC-32C` is the Castagnoli checksum journal-v1 uses. A file is:

```text
header        48 bytes
sections      one per segment, each starting where the one before ends
spanning      the spanning section, where there is one, where the last segment's section ends
directory     where the last section ends
footer        the last 32 bytes
```

### Header

| Offset | Size | Field |
|---|---|---|
| 0 | 8 | magic `ICATTILE` |
| 8 | 2 | major, 1 |
| 10 | 2 | minor, 6 |
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
| 12 | 4 | how many segments' sections |
| 16 | 4 | CRC-32C over the directory |
| 20 | 8 | reserved, zero |
| 28 | 4 | CRC-32C over the footer's bytes `[0, 28)` |

### Directory

The derivation the bindings were made under, then one entry per segment's section, in ordinal order of segment name,
then where the spanning section lies:

```text
str8 binding rule    str8 relation rule    i32 instances    i32 channels    32 bytes fingerprint
str8 segment name    i64 segment length    str8 segment digest    i64 section offset    i64 section length    (per section)
i64 spanning section offset    i64 spanning section length    (both 0 where there is none)
```

A segment's name is never empty. Minor 3's directory ends after its sections' entries.

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
i64 where the byte sums begin, from the section's start         i64 the byte sums' length
i64 where the byte tallies begin, from the section's start      i64 the byte tallies' length
u32 CRC-32C over the header's bytes before it
```

A section that keeps no tiles has no mechanisms, no levels, no records, no tallies, no byte sums and no byte tallies,
and a width of 1. The owner tallies begin where the records end, the channel tallies where the owner tallies end, the
byte sums where the channel tallies end and the byte tallies where the byte sums end, and the byte tallies end where the
section does; a section keeping no bytes has neither, and one keeping byte sums keeps byte tallies. Minor 5's header
ends after the byte sums' length, and its byte sums end where the section does; minor 3's and 4's end after the channel
tallies' length, and their channel tallies end where the section does.

### Levels

The levels follow the header, the finest first, each beginning where the one before ends. A level is its tiles in
time order, in blocks of 32; the last block may hold fewer. A block is:

- its tiles;
- since minor 6, an `i32`: where its tiles' byte tallies end within the byte tallies, before their checksum; zero where
  the section keeps none;
- since minor 5, an `i32`: where its tiles' byte sums end within the byte sums, before their checksum; zero where the
  section keeps none;
- an `i32`: for the finest level, where its last tile's records end within the records, before their checksum; for a
  wider level, zero;
- an `i32`: where its tiles' owner tallies end within the owner tallies, before their checksum;
- an `i32`: where its tiles' channel tallies end within the channel tallies, before their checksum;
- a CRC-32C over the tiles and those words.

A tile is:

```text
i64 earliest reading    i64 latest reading    i32 link    i32 children    i32 owner tallies    i32 channel tallies
m × i32 count, one per mechanism
i32 byte sums (since minor 5)    i32 byte tallies (since minor 6)
```

For a finest tile, `link` is where its first record begins within the records, and `children` is zero. For a wider
tile, `link` is the position of its first child in the level below, and `children` is how many it has: 1 to 10, the
next tiles there. `owner tallies`, `channel tallies`, `byte sums` and `byte tallies` are where the tile's owner
tallies, channel tallies, byte sums and byte tallies begin within them; `byte sums` and `byte tallies` are zero where
the section keeps none.

### Records

The records follow the levels. For each finest tile, in order, they hold its records in row order, then a CRC-32C over
those records' bytes, so a column boundary or a brush's end reads and checks only the tile it falls among. A record
is:

- its reading's increase over the record before it in its tile, as unsigned LEB128 (for a tile's first record the
  increase is over the tile's earliest reading, so it is zero);
- its **kind**, as unsigned LEB128: its mechanism's place among the section's mechanisms times 32, plus its end times 8
  (0 for none, 1 for the first, 2 for the second), plus its direction code - its `EN-Direction` code, or 7 for a code
  of 7 or more. While a section's mechanisms are four or fewer, every kind takes one byte;
- its owner key, then its channel key, each as unsigned LEB128;
- since minor 5, its measure, as unsigned LEB128: 0 for none; its accounting side's code, 1 to 4, for a size it declared
  and did not record; that code plus 4 for bytes it measured, followed by them as unsigned LEB128. Every record of a
  section keeping no bytes has a measure of 0.

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
its own, or its children's - but their measures is not kept, and is a single zero instead.

### Byte sums

Since minor 5 the byte sums follow the channel tallies, level by level from the finest, as the tallies do: for each
block of a level, that block's tiles' sums in time order, then a CRC-32C over those bytes. Each number is unsigned
LEB128. A tile's sums are how many entries, then each entry in order of mechanism, then of side:

- its mechanism's place among the section's mechanisms times 4, plus its accounting side's code less one;
- how many of its records measured their bytes;
- how many declared a size they did not record;
- the bytes they measured.

A section keeping no bytes has no byte sums.

### Byte tallies

Since minor 6 the byte tallies follow the byte sums, level by level from the finest, as the sums do: for each block of
a level, that block's tiles' byte tallies in time order, then a CRC-32C over those bytes. Each number is unsigned
LEB128. A tile's byte tallies are how many entries, plus one, then each entry in order of owner key, then of channel
key, then of side:

- its owner key's increase over the entry before's (the first entry's over zero, so its key);
- its channel key - for an entry of the same owner key as the one before, its increase over that entry's - times 4,
  plus its accounting side's code less one;
- how many of its records measured their bytes;
- how many declared a size they did not record;
- the bytes they measured.

A tile keeping none is a single zero, and a query measures it from its children or its records. A section keeping no
bytes has no byte tallies.

### The spanning section

The spanning section begins with its header:

```text
i32 sections: how many segments' sections the directory lists    i32 extents    i64 timed records it spans
i64 earliest reading    i64 latest reading    i64 finest tile width
u8 m: mechanisms    m × u16 mechanism codes, increasing
u8 L: levels        L × (i32 tiles, i64 where the level begins, from the section's start)
i64 where the owner tallies begin, from the section's start     i64 the owner tallies' length
i64 where the channel tallies begin, from the section's start   i64 the channel tallies' length
i64 where the byte sums begin, from the section's start         i64 the byte sums' length
i64 where the byte tallies begin, from the section's start      i64 the byte tallies' length
u32 CRC-32C over the header's bytes before it
```

Minor 5's header ends after the byte sums' length, and minor 4's after the channel tallies'. Its extents follow: for each segment's section holding a timed record, in the directory's order, its place in the
directory as an `i32`, its earliest and latest reading and how many timed records it holds as `i64`s, 28 bytes in all;
then a CRC-32C over them. Its levels follow the extents, and its owner and channel tallies, byte sums and byte tallies
its levels, laid out as a segment's section lays out its own, but that it keeps no records: a finest tile's `link` and
`children` are zero, and every block's word for where its records end is zero. The byte tallies end where the section
does. A level keeping no bytes has neither byte sums nor byte tallies, and one keeping byte sums may keep no byte
tallies (§1); minor 5's byte sums end where the section does, and minor 4's channel tallies.

## 4. Reading

A tile index carries its own checksums, so it is read by the block and never hashed whole first (store-v1 §6):

- its header, footer and directory are checked when it is opened;
- a section's header when a zoom or a brush first needs it, and the spanning section's header, extents and top block
  when a query first counts it;
- a block when a zoom or a brush reads it, and a block's owner or channel tallies, its byte sums or its byte tallies,
  when a query counts one of its tiles from them;
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
  - records longer than 31 bytes for each timed row and 4 for each finest tile;
  - owner tallies that do not begin where the records end, channel tallies that do not begin where the owner tallies
    end, byte sums that do not begin where the channel tallies end, or byte tallies that do not begin where the byte
    sums end or end where the section does;
  - since minor 6, byte sums without byte tallies, or byte tallies without byte sums;
  - flags or mechanisms it does not define;
- holds a tile that:
  - holds no record;
  - begins after it ends;
  - does not follow the tile before it in time;
- holds a finest tile whose records do not follow the one before it's and its checksum;
- holds a wider tile whose children are not the next ones, or lie outside the level below;
- holds tiles whose owner or channel tallies do not follow one another, or a block whose tallies lie outside the
  section's;
- holds tiles whose byte sums do not follow one another, or a block whose byte sums lie outside the section's, or, in
  a section keeping no bytes, a tile or a block placing any; and the same of byte tallies;
- holds records that are not their tile's:
  - its first record is not at the tile's earliest reading;
  - a record lies past the tile's latest;
  - the last record is not at the latest;
  - their count is not the tile's;
  - a record's kind names a mechanism not among the section's or an end past the second, or its owner key is 1 to 7,
    or names an instance or a channel beyond the stated derivation's;
  - a record's measure is above 8, or other than 0 in a section keeping no bytes, or measured more bytes than a sum
    holds;
- holds tallies that are not their tile's: entries out of order, an owner or channel beyond the stated derivation's, a
  mechanism not among the section's, a kind no record can have, a count of zero or above the tile's records, owner or
  channel counts that sum above them, or bytes after its last entry;
- holds byte sums that are not their tile's: more entries than four for each of the section's mechanisms, entries out
  of order, a mechanism not among the section's, an entry no record measured or declared, more of a mechanism's records
  than the tile holds of it, bytes no record measured or more than a sum holds, or bytes after its last entry;
- holds byte tallies that are not their tile's: entries out of order, an owner key of 1 to 7, an owner or channel
  beyond the stated derivation's, an entry no record measured or declared, more records measured or declared than the
  tile holds, in an entry or in all, bytes no record measured or more than a sum holds, or bytes after its last entry;
- places a spanning section other than where the last segment's section ends, or ends its sections other than where
  the directory begins;
- has a spanning section whose header does not lay it out:
  - sections other than the directory's, fewer than two extents or more than the sections, or fewer timed records
    than extents;
  - an earliest reading after its latest, a width no power of ten, no mechanism or no level;
  - levels laid out other than as a section's are, or more than 2,049 finest tiles;
  - owner tallies that do not begin where its levels end, byte sums that do not begin where its channel tallies end,
    byte tallies that do not begin where its byte sums end or end where the section does, or byte tallies without byte
    sums;
- states extents out of the directory's order or beyond its sections, outside the readings it spans, of no record, or
  that do not add up to its timed records, its earliest reading and its latest;
- has a top level whose counts do not add up to the timed records it spans;
- holds a spanning finest tile with records or children, or whose readings do not lie within one tile of its width,
  after the one before it's;
- states the extent of a section whose own header says another earliest or latest reading or another count of timed
  records, or says it keeps no tiles: found when a range the spanning section leaves reads that section.

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

### Byte lanes

A zoom's byte lanes - each mechanism's lane under a byte ranking (§6.2) - and an interval's bytes, of every record or of
one mechanism's, add up what reading every row measures (`SessionIntervalByteQuery`): in each column, by the accounting
side each record states, the bytes its records measured, how many did, and how many declared a size they did not
record (R3). They add from the index only when it is of minor 5 and describes every observation segment the generation
names, as a zoom counts, and add each segment from its top level down:

- a tile none of whose records lies in the interval is passed over;
- a tile all of whose records fall in one column adds its sums there, each mechanism's to its lane;
- any other tile gives way to its children, or, at the finest level, to its records, each added in the column holding
  its reading.

A section keeping no bytes, or no tiles, has its segment's rows read. Over a level spanning the segments that keeps
their bytes, the lanes add its tiles first, and the sections beneath only the ranges it leaves, as a zoom counts them.
That is exactly what reading every row measures.

At 1,000,000 records of the scale gate's generator, a reopened session's byte lanes in the window's 2,000 columns took
the following, the first time and then four more, against reading the segments' rows:

| Byte lanes | From the tiles | From the segments |
|---|---|---|
| 4 segments: the whole extent | 37.8 ms, then 3.6 to 10.9 ms | 585 ms, then 88 to 99 ms |
| 4 segments: a third of it | 5.4 ms, then 4.7 to 8.0 ms | 90 ms, then 46 to 71 ms |
| 400 segments: the whole extent | 19.0 ms, then 8.4 to 19.7 ms | 978 ms, then 141 to 406 ms |
| 400 segments: a third of it | 6.2 ms, then 13.9 to 21.7 ms | 146 ms, then 93 to 135 ms |

### Brushing

A brush counts the records inside its interval: every one observed, each by the instance its owner binds to where the
evidence policy admits the binding, and each of a paired TCP or UDP channel the policy admits by that channel. It counts from
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

### A brush ranked by bytes

A brush ranked by bytes measures what reading every row measures (`SessionByteRanking`, `metrics-v1` §4): inside its
interval, each process's transport-observed bytes by accounting side, how many of its records measured them and how
many declared a size they did not record; the same at each end of each paired channel the evidence policy draws; and of
the records whose owner the policy binds to no instance. It measures from the index only when the index is of minor 6
and describes every observation segment the generation names, and states the derivation the brush binds with, as a
brush counts. It then measures each segment from its top level down:

- a tile none of whose records lies in the interval is passed over;
- a tile all of whose records lie in it and that keeps byte tallies adds them: each entry to the instance its owner key
  binds where the policy admits the binding's strength, and otherwise to the records no instance takes; and, where the
  brush draws the entry's channel, to that channel's end at that instance;
- any other tile gives way to its children, or, at the finest level, to its records, each measured as its row would be.

A brush's bounds place a record by the first native reading at or after each (`metrics-v1` §5), and a record with no
usable session time still has a native reading in it. A tile places a record by its tick, its nanoseconds over 100
truncated toward zero, which is the same place but for a record before the capture's epoch in the tick a bound falls in.
So a segment is measured from its rows where its section keeps no byte tallies, where it holds an untimed record, and,
for a brush starting at or before the epoch, where its section holds a reading there and its rows a record before the
epoch; such a segment is opened only where its native readings meet the brush's. Over a level spanning the segments
that keeps byte tallies, where no segment is measured from its rows, a brush measures the level's tiles first and the
sections beneath only the ranges it leaves, as a brush counts them. That is exactly what reading every row measures.

At 10,000,000 records of the scale gate's generator in forty segments, a reopened session's ten brushes ranked by
bytes, from a half to a fifth of the extent, took 3.0 ms the first time and 1.9 to 12.5 ms after, against 1,336 ms the
first time and 29 to 513 ms after reading their segments' rows (§12's budget is 250 ms); at 1,000,000 records in four
segments, 53.9 ms the first time and 1.9 to 31.0 ms after, against 437 ms and 19 to 226 ms.

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

A channel's focus - a paired TCP or UDP channel's, or a connection's whose other end the capture does not hold - counts the
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

### Over the level spanning the segments

A zoom, a brush and a focus count from the level spanning the segments under the conditions each counts from their
sections, and only where the index's sections are exactly the generation's observation segments. Each counts the level
from its top down, as it counts a segment's section:

- a tile none of whose records lies in the interval is passed over;
- a tile it would count whole in a segment's section adds its counts or its tallies: a zoom's falling in one column; a
  brush's lying in its interval and keeping both parts of its tallies; a focus's falling in one column of the focus and
  of its lanes and keeping the part of its tallies it counts;
- any other tile gives way to its children. A finest tile leaves its range to the sections beneath it: from its earliest
  reading, or the interval's start where that is later, to just past its latest, or the interval's end where that is
  earlier.

Ranges with no tile counted whole between them are one, for no record lies between two tiles. Each range is counted
from the segments' sections whose extents meet it, each counting only its records in the range, as the section counts
an interval (§4). Every timed record lies in one finest tile, so the tiles counted whole and the ranges left count each
record in the interval once: exactly what reading every row counts.

A brush so reads the level's top block and, at each of its two ends, a block per level with its tallies and the
sections beneath one finest tile; a zoom or a focus, the sections beneath one finest tile for each column boundary that
falls among a tile's records. None reads the header of a section no range meets.

At 1,000,000 records of the scale gate's generator, a reopened session took the following, against the same records'
minor 3 index, which counts each segment's section. A brush of a third of the extent is timed six times over, a whole
session's group of 40 processes and its busiest connection's two ends four times; the first time, over a cold cache, is
the slowest.

| Query | 400 segments of 2,500, minor 3 | 400 segments, minor 4 | 4 segments of 250,000, minor 3 | 4 segments, minor 4 |
|---|---|---|---|---|
| A brush of a third | 152 to 195 ms | 5.1 to 7.3 ms | 3.3 to 32.5 ms | 3.5 to 28.2 ms |
| The whole extent in 2,000 columns | 5.4 to 13.7 ms | 5.0 to 22.2 ms | 3.4 to 35.9 ms | 3.8 to 39.9 ms |
| A group's lanes at 256 columns | 398 to 504 ms | 207 to 239 ms | 45 to 79 ms | 49 to 112 ms |
| Its busiest connection's ends at 256 columns | 414 to 460 ms | 168 to 231 ms | 37 to 59 ms | 41 to 67 ms |
| A group's lanes at 2,000 columns | 407 to 488 ms | 426 to 519 ms | 27 to 123 ms | 59 to 94 ms |

A brush no longer grows with the segments. A focus at the window's 256 columns reads half what it did: about two fifths of
the finest spanning tiles lie across a column boundary and are counted from the sections, whose tiles of a few hundred
records keep too few tallies to spare their records. At 2,000 columns every finest tile, a tenth of a second wide, lies
across a boundary, and a focus counts the sections as before (§5).

## 5. What is not defined at this version

- The focus of an operation, or of a channel and owners at once, whose columns count what the tallies do not hold apart
  (§10.3): an operation's records, a channel's records of some processes. Such a focus reads its rows, from the
  segments the interval meets. So does a focus on the whole session's records of one source direction, which no tally
  holds apart. A focus on its records of one mechanism alone is what the zoom's own counts hold of it, and since
  revision 461 is taken from them.
- The bytes of an owner's or a channel's records apart in columns - a group's lanes or a process's direction rows under
  a byte ranking, a channel's bytes - which the byte tallies hold by tile but no focus counts from at this version. Each
  reads its rows from the segments its interval meets.
- The tiles of a live generation, which publishes no checkpoint until its writer finishes. Its zooms build each
  segment's tiles from its rows.
- Spanning tiles narrower than a focus's columns where they are many. The level's finest tiles are at most 2,048, so a
  focus at more columns than that, or over a part of the session holding fewer, finds each lying across a column
  boundary and counts the sections beneath, as before minor 4.
- A level spanning segments one of which keeps no tiles, its timed readings going backwards. Every query then counts the
  segments' sections, and that one's rows.
