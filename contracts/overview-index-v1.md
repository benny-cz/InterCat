# InterCat overview index v1

Status: **implemented** in plan revision 163; minor 1 in revision 229, minor 2 in revision 289, minor 3 in revision
292, minor 4 in revision 445. It is the top level of §12.1 S4's pyramid: the whole-session overview a generation's first view draws, persisted
beside its derivation checkpoint (`contracts/derivation-checkpoint-v1.md`). With both, opening a finished session opens
no segment before the first view. Its processes and relationships come from the checkpoint, and its timeline, mechanism
lanes and minimap from these counts. Deeper levels are the per-segment tiles of revision 158, built from a segment's
rows when a view needs them.

Revision 170 renamed this contract from `overview-v1`, which the JSON bundle `icat overview --json` had carried since
revision 87 and still carries: that bundle is what a view shows, and this is a file a generation publishes. The file
itself names neither.

Like the checkpoint, an overview is a derived index. It holds counts, never evidence (R1), and the segments it covers
rebuild it (R20). One that is missing, stale or unreadable costs time, and when unreadable a caveat; it never changes
an answer.

## 1. What it holds

For the covered observation segments, what the overview projection counts from their rows:

- how many rows they hold, and how many of those have no usable session time;
- the extent: the earliest session time of a timed row, and one tick after the latest, when any row is timed. Since
  plan revision 445 a generation that states an interval release (store-v1 §8) begins its extent at the first tick
  wholly at or after the release's boundary, from which it keeps every record, when it holds timed rows on both sides
  of it: the rows kept from before the boundary, as the evidence of what came after, are its rows, but its timeline and
  minimap begin where every record is kept rather than draw the released stretch as one long gap (ADR-045);
- the **main columns**: the 64 overview buckets (`SessionOverviewProjector.MaximumTimelineBuckets`) dividing the extent
  by §10.3's `b(i) = t0 + floor(span·i/W)`, fewer when the extent spans fewer ticks, with each column's count per
  mechanism;
- the **minimap columns**: the aligned 1–2–5 columns covering the extent (`SessionMinimap.ColumnsFor`, at most 2,000),
  with each column's count.

Coverage is not held: the projection judges each column's coverage from the generation's coverage ledger, as it does
for counts it made itself. A column's bounds are held only as the rules above give them, so a reader that would place
columns otherwise does not use the counts (§3).

Since minor 1 (plan revision 229), when the generation's coverage ledger names a collected ALPC descriptor, it also
holds the **RPC links** of `contracts/operations-v1.md` §5c: for every pair of process instances a served call joins,
at each strength a link between them has, the call records at both ends of those links. A link is correlated at best
and as weak as the weaker binding of its two calls, and a link one of whose calls binds to no instance joins no pair.
They are held before any evidence policy, which the projection applies to them as it does to relations, so the graph's
RPC edges come from them and no call is paired or followed before the first view. A generation whose ledger names no
collected ALPC holds none.

Since minor 2 (plan revision 289) it also holds the **lane bytes**: for each main column and each mechanism it counts
records of there, what those records' transport-observed measurements sent under sender accounting, received under
receiver accounting, and stated on neither side, each with the contributions that measured it and those that declared
a size they did not record (`metrics-v1` §4, R3). They are what the machine rung's mechanism lanes plot under a byte
ranking (§6.2), so those lanes read no segment either; the same columns at any other width, or any other interval, are
read from the segments. A column and mechanism with no contribution holds no entry, and its bytes are none.

Since minor 3 (plan revision 292) it also holds the **process bytes**: what every record's transport-observed
measurements came to over the whole session, as the lane bytes count them, held before any evidence policy, which a
reader applies to them as the projection applies one to relations:

- for each process instance and each strength its records bind at - direct, correlated, candidate or conflicting, the
  strengths a policy can admit - their bytes;
- the bytes of records bound to no instance, or at a strength no policy admits;
- for each end of each TCP channel (`relations-v1`), whatever the channel's own strength, the bytes of the records the
  instance holding that end raised there, and the one strength they bind at. A process's records other than its
  lifecycle bind as strongly as its place among its PID's instances allows (`entities-v1` §4), so an end's records share
  one; a process connected to itself has its records counted at the first end, as a read counts them.

Under a policy, an instance's bytes at the strengths it admits are that process's, and the rest are unattributed; an
end's bytes are its holder's on that channel when the policy admits both the channel and their strength. That is
exactly what a read of every segment measures under the policy (`SessionByteRanking`), so the ranked table's
whole-session byte ranking and the graph sized by bytes read no segment either. An interval is still read. A session
with more than 20,000 TCP channels, or whose process bytes would take more than 12 MiB, keeps none, as does one where an
end's records bind at two strengths, which no binding rule gives today; it is then read as before.

Since minor 4 (plan revision 445) it also holds how many timed rows were read **before the extent** begins: those a
generation that released an interval kept from before its boundary. The main and minimap columns, with them, add up to
the timed rows. Every other overview holds zero.

## 2. Files and publication

The dependency kind is `Index` (store-v1 code 4), under the name:

```text
overview-<publishing generation:D10>.bin
```

The overview is published with the derivation checkpoint, in the same index publication (`CommitIndex`), by the same
writers. It follows the checkpoint's publication rules (derivation-checkpoint-v1 §2), including release with any
segment. A generation names at most one.

## 3. Bytes

Little-endian throughout, with derivation-checkpoint-v1's `str8` and `guid`. An overview is at most 16 MiB.

```text
overview   = "ICATOVRV" (8 ASCII bytes), major u16 = 1, minor u16 = 4,
             session guid, derivedGeneration i64 (>= 1),
             mainBound u16 (= 64), minimapBound u16 (= 2000),
             count u32, (name str8, length i64, digest str8)*   ; covered observation segments, ascending by name
             rows i64, untimed i64 (0 <= untimed <= rows),
             hasExtent u8 (0, 1),
             [extentStart i64, extentEnd i64 (> start, by at most 2^63 - 1),
              mainColumns u32,
              count u32, (column u32, mechanism u16, records i32 (> 0))*        ; ascending by (column, mechanism)
              minimapStart i64, minimapEnd i64, minimapColumns u32,
              count u32, (column u32, records i32 (> 0))*],                     ; ascending by column
             hasRpcLinks u8 (0, 1),                                             ; minor 1 and later
             [count u32, (first guid, second guid, strength u8, records i64 (> 0))*]
                  ; first before second as their "N" forms order; ascending by (first, second, strength)
             hasLaneBytes u8 (0, 1),                                            ; minor 2 and later
             [count u32, (column u32, mechanism u16,
                          sentBytes i64, sentMeasured i64, sentUnmeasured i64,
                          receivedBytes i64, receivedMeasured i64, receivedUnmeasured i64,
                          otherBytes i64, otherMeasured i64, otherUnmeasured i64)*]
                  ; every value >= 0; ascending by (column, mechanism)
             hasProcessBytes u8 (0, 1),                                         ; minor 3 and later
             [unbound bytes,
              count u32, (instance guid, strength u8, bytes)*
                  ; ascending by (instance "N" form, strength)
              count u32, (channel str8, holder guid, strength u8, bytes)*]
                  ; ascending by (channel, holder "N" form)
             beforeExtent i64 (0 <= beforeExtent <= rows - untimed)           ; minor 4 and later

bytes      = sentBytes i64, sentMeasured i64, sentUnmeasured i64,
             receivedBytes i64, receivedMeasured i64, receivedUnmeasured i64,
             otherBytes i64, otherMeasured i64, otherUnmeasured i64             ; every value >= 0
```

A minor-0 overview ends after its minimap, or after `hasExtent` when it is 0, and holds no RPC links; a minor-1 overview
ends after them and holds no lane bytes; a minor-2 overview ends after those and holds no process bytes; a minor-3
overview ends after those and counts no row before its extent. Each is read as before. A link's strength is `EN-RelationStrength`'s
`Correlated`, `Candidate` or `Conflicting`.

A reader refuses an overview whose bytes do not hash to its recorded digest. It also refuses one where:

- the magic or version is not this build's, or the bounds are not this build's `MaximumTimelineBuckets` and minimap
  bound;
- the session is not the generation's;
- a count exceeds the bytes left, a name is not an owned file name, a digest is malformed, or an order above is broken.
  `mainColumns` and `minimapColumns` are widths, not counts: a few records over a long extent fill few of many columns,
  and a width bounds nothing about the bytes that follow (revision 246 stopped refusing such an overview);
- a column index is outside its columns, a mechanism is not one §23 defines, or bytes remain;
- the main column count, or the minimap span and column count, are not what this build derives from the extent;
- the extent is empty or wider than a tick count holds (its end more than 2^63 - 1 ticks after its start);
- the main or minimap counts, with the rows before the extent, do not add up to the timed rows; there is no extent
  while some row is timed; or more rows are counted before the extent than are timed;
- an RPC link names an empty identity or the same instance twice, puts its pair or its links out of order, has a
  strength a link cannot have, holds no record, or there are more than 1,000,000 of them;
- a lane's bytes lie in a column outside the main columns, or in one that counts no record of their mechanism (an
  overview with no extent has no columns, so it holds none); are out of order; hold a value below zero, or bytes on a
  side no contribution measured; or hold no contribution at all;
- a process's or channel end's bytes name no instance, channel or holder, or a strength no policy admits; are out of
  order or held twice; hold a value below zero, or bytes on a side no contribution measured; or hold no contribution at
  all. The unbound bytes may hold none, and are otherwise held to the same.

A reader uses the process bytes only for the whole session, and only when the derivations it builds from the
checkpoint hold every instance they name and every channel end, at one of its channel's two holders; otherwise it reads
the segments.

A refused overview is not used, and the overview says why in one caveat. A reader uses an overview for a generation
only when it covers exactly the generation's observation segments, with the same names, lengths and digests, and,
for a generation that states an interval release, only when its extent does not begin before the release's boundary:
one counted as overviews were before minor 4, from a row kept from before it, is counted again. Counts
cannot be extended by further segments, as the extent and every column would move. Otherwise the projection counts
from the segments' tiles.

## 4. What is not defined at this version

- Deeper levels. Zoomed detail builds a segment's tiles from its rows when first drawn (revision 158). Persisting
  them, so a zoom into a long session reads only the tiles it draws, is the pyramid's next level.
- Focused counts, which filter rows by owner or channel and are not what these counts hold (§10.3).
- Byte sums beyond the main columns' lanes and the whole session's processes and TCP channel ends: the minimap's, a
  datagram flow's, and any interval's, which are read from the segments.
- RPC links within an interval. A brush still follows the calls it holds from the segments.
