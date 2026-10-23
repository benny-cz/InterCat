# ADR-054: A finished generation publishes indexes that answer exactly what its segments would, and the caches beneath them are budgeted

- Status: accepted; revision 726. It records what the derivation checkpoint (revision 162), the persisted overview
  (revision 163), the tile index (revisions 456 to 460, 701 to 708) and the operation index (revisions 440 and 441)
  decide together, and the budgets of the in-memory caches beneath them
- Date: 2026-10-22
- Relates to: §10.2, §10.3, §12.1 S1-S4, §19.3, §20.1, §20.2, R8, R20, P25, `contracts/derivation-checkpoint-v1.md`,
  `contracts/overview-index-v1.md`, `contracts/tile-index-v1.md`, `contracts/operation-index-v1.md`

## Context

A session's evidence is its journal and the segments derived from it, and every answer the window or `icat` gives can
be computed from the segments alone. Computing it each time breaks the scale invariants: reopening a session would
read every record before the first view (S1), a zoom or a brush would read every row in range (S4), and the first RPC
or HTTP listing would pair every call record of every segment (S3, P25). §10.2 asks for indexes and a multiresolution
pyramid, for common summaries rather than a cube of every dimension, and for budgets on what is kept in memory. Four
contracts and two caches implement that; no record said how they fit together, or why each is exact rather than
approximate.

## Decision

1. **Four indexes, published together.** A finished generation names at most one of each, published in one index
   publication by the same writers - an import, the end of a live follow, a finished recording, a compaction, a
   re-derivation and `icat checkpoint`:
   - the **derivation checkpoint**: the process instances, their activity and the transport relations derived from named
     segments, so a reopen derives nothing it covers (S1);
   - the **overview**: the whole-session timeline, mechanism lanes and minimap counts, the pyramid's top level, so the
     first view opens no segment (S4);
   - the **tile index**: each segment's timeline tiles at decimal levels, with what their records' owners and channels
     bind to, tallies and byte sums where they spare reading, and a level spanning the segments, so a zoom, a brush, a
     group's lanes and a channel's ends open no segment (S4);
   - the **operation index**: RPC calls with their other ends and HTTP exchanges, so the first operation view pairs
     nothing (S3).
2. **An index is exact or unused.** A reader uses an index only when it is current for the generation - covering the
   segments it names under the derivation the checkpoint states - and then gets exactly what deriving from every segment
   gives; tests hold each to that. Otherwise the reader derives from the segments, slower and never different. An index
   of an older minor is read for what it keeps, and the rest is measured from rows.
3. **Indexes are evidence's dependents, never evidence.** They are the store's `Index` dependency kind, which a reader
   that does not know them ignores; an additive generation does not carry them, and a compaction or a retention that
   releases a segment releases them. Each can be rebuilt from retained evidence (R20). Each file is bounded - a
   checkpoint at 512 MiB, an operation index at 1 GiB, a tile index's directory at 64 MiB - and one past its bound is
   refused rather than read in part.
4. **Common summaries, not a cube.** Tiles keep counts by mechanism and record kind, owner and channel bindings, and
   tallies and byte sums by owner, channel and direction only where keeping them spares reading rows; nothing is
   precomputed across host, process, peer, resource, operation and time together. A question the indexes do not hold is
   answered by a bounded scan.
5. **In-memory caches are bounded.** A store instance's segment readers are cached against a byte budget, 256 MiB by
   default, charged with what each reader holds - the columns and chunks it read, the dictionaries it decoded and the
   bindings derived from its rows - trimmed back to the few columns every projection reads, and bypassed once full; a
   reader a query holds is never taken from it. The derivations of the four most recently used generations are kept
   process-wide, by count: a derivation holds no file, so keeping it never delays a release. Since revision 751 a tile
   index keeps what its queries read of it - blocks decoded, and their tallies, byte sums and records - against a
   byte budget, 64 MiB by default, which the tiles of every chunk a live session's generations name share, giving up
   a part chosen at random once full; before, each chunk's tiles kept 64 blocks, fewer than a chunk of a busy
   machine's traffic holds, and every focus read them all again.

## Why

- **Exact, not approximate.** A count that differs between a reopened session and one derived in full would be a
  defect nobody could locate; requiring each index to equal the full derivation makes an index a matter of speed only,
  and lets a test hold it.
- **Published together.** One writer publishing all four keeps them of one derivation; a generation that names some but
  not a readable tile index of each segment is not current, so a partial set is never mistaken for a whole one.
- **Tallies only where they pay.** At a million records, keeping a tile's tallies wherever they took at most half its
  records' bytes would have taken 12.8% of the segments' bytes, where keeping them only where they spare reading took
  7.7% (`tile-index-v1` §2); today's index, with byte sums and byte tallies, takes 12.7%, about 22 bytes a record.
- **The derivation cache by count.** A derivation grows with a session's processes and relations, not its records: at 10
  million records of 400 processes, everything the heap retained beyond the reader cache was about 35 MiB. The reader
  cache, which grows with records, is the one a byte budget must bound.

## Consequences

- A finished session's first view, zooms, brushes and first operation views open no segment; a live generation derives
  incrementally from the generation before it, and its indexes come with the publication that ends it.
- §19.3's rule that a cache is budgeted by bytes, not entry count alone, has one exception, the derivation cache, and
  its basis is a measurement at 400 processes. A machine of thousands of processes and tens of thousands of relations is
  unmeasured; if its derivations prove large, the cache needs a byte budget too. The scale tier that would measure it
  waits on M5's disk.
- At 100 million records, faster scans are not enough, and what a brush and a group count must come from the pyramid;
  that tier is unmeasured for the same reason.

## Alternatives considered

- **Approximate summaries (sketches) for large sessions.** Rejected: they cannot be exported as exact results (§19.3),
  and a preview the window labels as approximate would still differ from `icat`.
- **A cube of every dimension.** Rejected by §10.2: its size is the product of the dimensions' cardinalities.
- **Indexes inside the segments.** Rejected: a segment is immutable evidence of its rows; an index changes when its
  derivation changes, and is released and rebuilt without touching the rows.
- **A byte budget for the derivation cache now.** Not taken: estimating an object graph's bytes is a guess where the
  reader cache's charge is a count of the bytes it read, and the measured derivations are small.
