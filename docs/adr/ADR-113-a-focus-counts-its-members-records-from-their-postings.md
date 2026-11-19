# ADR-113: A focus counts its members' records from their postings

- Status: accepted for M5's scale tiers (§12.1 S3); the tile index posts each section's owners and channels since
  revision 1064 (minor 10), and an owner or a channel's focus counts from those postings since revision 1064; it
  measures each section's owners' records since revision 1065 (minor 11), and a group's byte lanes measure from them;
  a group's first count and byte lanes read their members' lists ahead since revision 1088, and walk behind that
  reading since revision 1089; a group's lists in a section are read on their own and kept as one part since revision
  1090
- Date: 2026-11-18
- Decision owners: InterCat maintainers
- Relates to: ADR-054, ADR-057, plan §12, §12.1 S3 and S4, `contracts/tile-index-v1.md`,
  `bench/results/scale-tiers-20261118T084118Z`, `bench/results/scale-tiers-20261118T112429Z`,
  `bench/results/scale-tiers-20261118T130340Z`, I4, I14, S3

## Context

Revision 1063 measured §12.1's tiers: warm, a 40-process group's lanes took 285 ms at T3's 95th percentile and a
connection's two ends 204 ms, against §12's 100 ms, both growing with the session though their columns did not. Counters
added for the investigation, and removed, showed why. A focus counted a tile whole from its owner or channel tallies
wherever the tile lay in one column, and those tallies hold every owner or channel the tile's records name: in the scale
gate's sessions, 400 processes taking turns record by record, about a thousand entries in a 100 ms tile. At T3 a quarter
of the session in 2,000 columns counted some 9,700 such tiles a query - 9.7 million entries, about 96 MB of tally parts,
past the 64 MiB tile cache, so the same parts were read and checked again by every query - and 2.7 million records where
a column boundary fell among tiles too narrow to keep tallies. No tile at these levels kept channel tallies, so a
channel's focus read every record around each boundary. The work tracked the records in view times the processes a tile
names, though the focus counts 40 processes, or one channel.

A first form of this decision posted each record's finest tile and read the one tile a boundary falls within to place
the records around it; measured, it brought T3's lanes to 146 ms and its ends to 90 ms, the tiles read at 2,000
boundaries a query costing most of what remained. Posting each record's reading needs no tile read at all, for two more
bytes a record.

## Decision

1. **Each section posts its owners' and channels' records** (tile index minor 10): for each owner key, mechanism and
   direction its timed records have, and for each channel, mechanism, end and direction, the reading of each such
   record less the section's earliest, in row order, so never decreasing - four bytes each, or eight where the section's
   readings span 2^32 ticks or more. Each part carries its checksums, as every other part does.
2. **An owner or a channel's focus counts each section from its postings**: the section's part of the interval is cut
   at every boundary of the view's columns and the lanes' within it; how many of a list's records lie before a cut is
   how many of its readings do, found by a binary search from the cut before's; a column's records are the difference
   between its two cuts' counts. A focus reads each section's directory and its members' lists, and no tally, record or
   level spanning the segments: in proportion to its columns and members, never to the records in view.
3. **Postings are checked as every part is** (I4): a list going backwards or past its section's latest reading, a
   directory of a width its section's span does not have, of a key, kind or count no record could have, or whose lists
   do not fill their part, refuses the index, which is then counted from the segments' rows.
4. **Tallies stay** for what counts every owner and channel at once - a brush, the whole timeline - and for an index of
   an earlier minor, which a checkpoint publishes again with postings (I14). A chunk's tiles, whose bindings a later
   derivation reads anew, post nothing, and a live session's focus counts them from their tallies, as before.
5. **Bytes are not posted at this minor**: a group's byte lanes and a process's byte direction rows count from the byte
   tallies, and are the next work.

## Consequences

- Measured again at T1 to T3 (`bench/results/scale-tiers-20261118T112429Z`, three runs of each tier in processes of
  their own, the machine 9 to 31% busy before them): a 40-process group's lanes took 44, 52 and 46 ms at the 95th
  percentile, from 68, 250 and 285, and a connection's two ends 26, 52 and 53, from 50, 161 and 204: within §12's
  100 ms at every tier, the lanes about as fast at T3 as at T1 (S3), which `ScaleTierTests` holds. Two things the
  measurement showed are part of the decision's form: a posted index's sections are counted one after another into
  the focus's one tally, for at a few binary searches a boundary each worker's own tally of every column and lane,
  merged, cost more than the counting; and a list is kept read as wide as its section holds it, four bytes a reading,
  so the tile cache holds a focus's lists rather than giving them up and reading them again. A group's byte lanes,
  still counted from byte tallies, took 45, 278 and 622 ms.
- A posted index is larger by four bytes a record for its owner's list and four for its channel's: about eight bytes a
  record of the scale gate's 342, 2.3%.
- `contracts/tile-index-v1.md` defines minor 10: the section header's two more parts, their layout, what reading them
  refuses, and how a focus counts from them.

## Addendum: a group's byte lanes measure its members from their measures (revision 1065)

Revision 1064 left a group's byte lanes counting from byte tallies, which hold every owner a tile's records name, as
owner tallies do: 45, 278 and 622 ms at T1's, T2's and T3's 95th percentile. Since minor 11 a section keeping bytes
keeps its owners' measures beside their postings: for each owner list, each record's measure as its record holds it,
and before every 64th record and past the last a block of what the list's records before it measured - their sent,
received and other bytes, how many measured each and how many declared a size they did not record - with where the
next record's measure begins. A group's byte lanes cut each section's part of the interval at their columns'
boundaries, find how many of a member's records lie before each cut from its postings, as its lanes' counts do, and
take what those records measured from the block before that place and at most 63 measures after it: a lane column's
bytes are the difference between its two cuts'. The machine row adds up the tiles' byte sums, as a zoom's byte lanes
do, every mechanism one lane, so the lanes read no byte tally; a section keeping no bytes has its rows read, as
before.

Two more things the measurement showed are part of the decision's form. A 40-process group's postings and measures
over T3's 72 million records, with its sections' directories, are charged about 90 MiB, which a tile cache of 64 MiB
gave up and read again at every view, T3's quarters taking up to 470 ms: since this revision the cache keeps 128 MiB,
a tunable within §12's 512 MiB for analysis and caches, and a T3 session's steady state held 278 MiB private where it
held 237. And each list's measures are walked in one loop compiled optimized at once: decoded by a method a record,
which ran unoptimized until the runtime had seen enough of it, they put T1's first quarters at 200 ms. Measured again
(`bench/results/scale-tiers-20261118T130340Z`, the machine 22 to 41% busy before the runs), the byte lanes took 39, 54 and
59 ms, within §12's 100 ms at every tier, which `ScaleTierTests` now holds, beside a group's lanes at 32, 25 and
34 ms and a connection's ends at 29, 14 and 13. The measures cost four bytes a record of the scale gate's, 354
where they were 350. A session past the measured T3, toward its 100 GiB, holds more of a group's records than the
cache keeps, and its lanes read some lists again at each view; the tiers measured hold them. Measures that are damaged,
measure other lists than the postings hold, state sums or places their records do not have, or hold a measure no
record can have refuse the index, which is then measured from the segments' rows (I4); an index of minor 10 is
measured from its byte tallies until a checkpoint publishes it again (I14). A process's byte direction rows still
count from byte tallies. 18 mutations are caught.

## Addendum: a group's first count reads its members' lists ahead (revision 1088)

Warm, a group's lanes and byte lanes answer within §12's budget at every tier; the first time a process asks them of
a session it has just opened, they did not at T2 and T3 (`docs/reviews/M5-declared-performance-tier.md`). Probes
written for the investigation, and removed, put the cold cost of a 40-process group's first timeline at T2 in two
parts: code running for the first time, and its members' lists read one at a time - some 12,000 reads of about a
kilobyte across 120 sections, each checked, decoded and kept on the thread that walks the sections - which, the code
compiled, took about 150 ms where the reads alone took 41 to 57.

Since this revision a group's focus and its byte lanes read ahead what their walk will read: where at least two
sections the interval meets would read their owners' directory - or their measures' - from the file, those sections
are read on several workers, each its directory and its members' lists in runs, one read of up to 4 MiB covering
every member's list the run reaches, into a pooled buffer, each list checked and decoded as before and a run's lists
kept in one hold of the cache; with measures, theirs too. Lists are decoded by copying them whole and checking them
in one pass. The walk that follows, one section after another into one tally, finds them kept and counts what it
did: nothing is counted ahead. A part the read-ahead cannot read is left to the walk, which reads it again and
refuses it as before, so a refusal names the part a walk in section order meets first, whatever order the workers
read in. The format is unchanged.

Measured through the scale gate on the reference machine, each tier's session in a process of its own, the machine in
use and 25 to 42% busy before the runs: a 40-process group's first timeline took 134 and 153 ms at T2, from 291 and
297, and 201 at T3, from 500; its first byte lanes 94 and 108 at T2, from 243 and 255, and 219 at T3, from 445; at
T1, 73 and 51 ms. A process's first evidence page did not change, and could not: asked again of a session the process
had cleared, its code compiled, it took 14 to 18 ms at T2, against 140 to 180 the first time, almost all of it code
compiled at its first use. InterCat's assemblies compiled ahead of time (ReadyToRun), in an experiment on a copy of
the build, brought T2's first evidence page to 100 to 117 ms, its group timeline to 66 to 126 and its byte lanes to
76 to 89; whether the package ships them so is a release decision, recorded where it is taken. T3's first
questions remain over §12's budgets either way: a 40-process group there reads some 29,000 lists. A test holds that a
group's lists are read ahead in a read of each section's directory and one of its members' lists and of their
measures, that the count and byte lanes then read no part and count and measure what reading each list alone does,
and that a damaged list read ahead is refused by the count; 6 mutations are caught.

## Addendum: a group's count walks behind its lists as they are read (revision 1089)

Precompiled as the package ships it since this revision (ADR-116), a 40-process group's first timeline at T2 still took
about 100 to 120 ms cold: a probe written for the investigation, and removed, found the read-ahead taking 40 to 53 ms on
its workers, and the walk, one section after another into the one tally, 17 to 34 more, begun only once every section had
been read. Since this revision the workers take the sections in the order the walk counts them, each the next not yet
taken, and the walk waits for a section only until its directory and lists - and with measures, theirs - are kept, so it
counts each section while the workers read the ones after it. A section no worker has taken when the walk reaches it the
walk reads ahead itself, so it never waits on a worker yet to start. Nothing else changes: the walk still counts every
section in order into the one tally, a part the read-ahead cannot read is left to it and refused as before (I4), and
anything else reading a section throws is thrown where the walk reaches that section, as a walk reading it would have.
A test holds, at one worker and at four, that each section's parts are read once, the count reading none alone, and
that it counts and measures what reading each list alone does. With ADR-116's precompiled code, a group's first timeline took 80 to 85 ms
at T2 and its byte lanes 68 to 79, in each of three runs (`bench/results/scale-tiers-20261119T164559Z`), and T2 is
declared; at T3 they took 118 and 151 ms at the median, where some 29,000 lists are read.

## Addendum: a group's lists are read on their own, a section's as one part (revision 1090)

At T3, its code precompiled, a 40-process group's first timeline still took 118 ms cold at the median and its byte lanes
151. A probe, removed, found the read-ahead reading 166 MiB for some 29 MiB of the group's lists - a run spanned every
list between the first member's and the last's, the other processes' among them - and the count allocating some 85 MiB,
each of 29,000 lists an array of its own, which the collections that followed copied as they aged: 28 to 37 ms of
pauses in each first answer.

Since this revision a group's lists in a section are read and kept together as one part. They are read in runs of
lists lying no more than a page apart, so a group whose processes interleave with others' reads its own lists and
not the lists between them, into one buffer, where each list is checked as one read alone is - its checksum, its
readings never going backwards nor past the section's latest - and the whole kept and charged as one, the cache
finding it again only for exactly the lists it holds. Their measures are read and kept alike, each list's read where
it lies in its buffer and named only where refused. The count counts each list where it lies. A list a single
process's or channel's question reads is read and kept alone, as before.

A group's byte lanes measure their sections on several workers behind their reading - one for every 32 sections, at
most a quarter of the processors and six - each into a tally of its own merged at the end, a refusal being the one a
measure in section order meets first; and their reading starts before the machine's own byte row is measured, beside
it. Its count was tried so too and was slower - 80 ms at T3's median on six workers, 74 on one - each worker holding
a tally of every column and lane, so the count walks its sections alone, as before.

Through the scale gate, the machine in use and 22 to 55% busy (`bench/results/scale-tiers-20261119T181920Z`): at T3 a
group's first timeline took 86 to 100 ms, 91 at the median, allocating 51 MiB, and its byte lanes 94 to 106, 104 at the
median, 4 over the budget; at T2 they took 42 to 51 and 48 to 51. Tests hold that a group's lists are read on their
own, the bytes read exactly its members' lists and measures where four kilobytes and more lie between members, and
counted to what the section's tiles count; that a pass in order surfaces the failure a serial pass meets first; and
the refusals of a damaged or backwards list, as before. 8 mutations are caught.
