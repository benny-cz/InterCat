# InterCat overview index v1

Status: **implemented** in plan revision 163. It is the top level of §12.1 S4's pyramid: the whole-session overview a
generation's first view draws, persisted beside its derivation checkpoint (`contracts/derivation-checkpoint-v1.md`).
With both, opening a finished session opens no segment before the first view. Its processes and relationships come
from the checkpoint, and its timeline, mechanism lanes and minimap from these counts. Deeper levels are the per-segment
tiles of revision 158, built from a segment's rows when a view needs them.

Revision 170 renamed this contract from `overview-v1`, which the JSON bundle `icat overview --json` had carried since
revision 87 and still carries: that bundle is what a view shows, and this is a file a generation publishes. The file
itself names neither.

Like the checkpoint, an overview is a derived index. It holds counts, never evidence (R1), and the segments it covers
rebuild it (R20). One that is missing, stale or unreadable costs time, and when unreadable a caveat; it never changes
an answer.

## 1. What it holds

For the covered observation segments, what the overview projection counts from their rows:

- how many rows they hold, and how many of those have no usable session time;
- the extent: the earliest session time of a timed row, and one tick after the latest, when any row is timed;
- the **main columns**: the 64 overview buckets (`SessionOverviewProjector.MaximumTimelineBuckets`) dividing the extent
  by §10.3's `b(i) = t0 + floor(span·i/W)`, fewer when the extent spans fewer ticks, with each column's count per
  mechanism;
- the **minimap columns**: the aligned 1–2–5 columns covering the extent (`SessionMinimap.ColumnsFor`, at most 2,000),
  with each column's count.

Coverage is not held: the projection judges each column's coverage from the generation's coverage ledger, as it does
for counts it made itself. A column's bounds are held only as the rules above give them, so a reader that would place
columns otherwise does not use the counts (§3).

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
overview   = "ICATOVRV" (8 ASCII bytes), major u16 = 1, minor u16 = 0,
             session guid, derivedGeneration i64 (>= 1),
             mainBound u16 (= 64), minimapBound u16 (= 2000),
             count u32, (name str8, length i64, digest str8)*   ; covered observation segments, ascending by name
             rows i64, untimed i64 (0 <= untimed <= rows),
             hasExtent u8 (0, 1),
             [extentStart i64, extentEnd i64 (> start),
              mainColumns u32,
              count u32, (column u32, mechanism u16, records i32 (> 0))*        ; ascending by (column, mechanism)
              minimapStart i64, minimapEnd i64, minimapColumns u32,
              count u32, (column u32, records i32 (> 0))*]                      ; ascending by column
```

A reader refuses an overview whose bytes do not hash to its recorded digest. It also refuses one where:

- the magic or version is not this build's, or the bounds are not this build's `MaximumTimelineBuckets` and minimap
  bound;
- the session is not the generation's;
- a count exceeds the bytes left, a name is not an owned file name, a digest is malformed, or an order above is broken;
- a column index is outside its columns, a mechanism is not one §23 defines, or bytes remain;
- the main column count, or the minimap span and column count, are not what this build derives from the extent;
- the main or minimap counts do not add up to the timed rows, or there is no extent while some row is timed.

A refused overview is not used, and the overview says why in one caveat. A reader uses an overview for a generation
only when it covers exactly the generation's observation segments, with the same names, lengths and digests. Counts
cannot be extended by further segments, as the extent and every column would move. Otherwise the projection counts
from the segments' tiles.

## 4. What is not defined at this version

- Deeper levels. Zoomed detail builds a segment's tiles from its rows when first drawn (revision 158). Persisting
  them, so a zoom into a long session reads only the tiles it draws, is the pyramid's next level.
- Focused counts, which filter rows by owner or channel and are not what these counts hold (§10.3).
- Byte sums. The overview counts records only, and so does this.
