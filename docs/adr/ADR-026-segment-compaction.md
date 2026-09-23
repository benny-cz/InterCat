# ADR-026: Small publications are coalesced into bounded segments, keeping every row

- Status: accepted for M1 and M2
- Date: 2026-09-23
- Decision owners: InterCat maintainers
- Relates to: §19.3 (live publication cadence), §20.1 (compaction targets, commit sequence), I1, I2, I18, ADR-010,
  ADR-022, ADR-024, ADR-025, `contracts/store-v1.md`, `contracts/segment-v1.md`

## Context

A live recording publishes a journal chunk and a few derived files at every interval (ADR-022). §20.1 sets
compaction targets so that a long live session stays reopenable within budget:

- compaction becomes mandatory above 64 live segments per time block and 512 per session;
- outputs are at least 8 MiB or 64,000 rows;
- serving the initial viewport after reopen takes no more than 128 segment opens.

Nothing implemented them. A one-hour recording at the default 5-second interval would name about 720 observation
segments, with a field segment and dictionaries beside each. Every commit and lease would open all of them
(ADR-025), and every query would open all of them too.

## Decision

1. **A publication unit is what one generation derived:** its observation and field segments and its dictionaries.
   A segment resolves its dictionaries through its own generation, so a unit's dictionaries serve only its segments
   and go with them, with no reference counting. A unit is small while its observation rows and bytes are both below
   the output targets, 64,000 rows and 8 MiB.
2. **Runs of two or more consecutive small units are coalesced.** Their rows are rewritten into new segments of a new
   generation, one run at a time, so no output spans the time of a unit it did not coalesce. Every row moves
   unchanged: its raw-record locator, its journal index and its values. Every observation therefore keeps its
   identity (I1, I2), and a row count checked against the plan guards the move.
3. **The compaction generation is published through the retention path.**
   - It releases the replaced files with a `DerivedFiles` retention record whose reason states that their rows were
     coalesced and kept.
   - It carries every journal, the plan, the ledger and the committed boundary unchanged.
   - A file a reader's lease holds stays until that reader lets go (I18).
   - Only segments and dictionaries can be replaced; the store refuses anything else.
4. **A live recording compacts itself.**
   - When 64 small publications have accumulated, the writer takes one step between chunks: the oldest run, cut at one
     segment's worth of rows (250,000 by default), so the writer is held up for a bounded time.
   - When the capture stops, every run left is coalesced, so a finished recording opens from as few segments as its
     rows need.
   - A compaction that fails leaves the recording published and whole, so it is reported rather than ending the
     capture.
   - `icat compact [--check]` coalesces any session, and is lossless, so it needs no confirmation.
5. **"Per time block" is the recorder's count of small publications.** §20.1's per-time-block bound is read as how
   many small publications have accumulated. A chunked recording's segments cover consecutive, barely overlapping
   intervals, so small segments per time block and small publications since the last compaction are the same count.

## Consequences

- The real 5-chunk recording compacted from 4 observation segments to 1, and released 16 files (445,309 B). Its
  `observations` answer by mechanism was identical afterwards, and it still re-derives.
- A benchmark session of 120 publications and 2.4 million rows compacted from 120 observation segments to 10, and
  from 360 files to 140. Compaction ran at about 200,000 rows per second (12.1 s in process; `icat compact` took
  16.4 s, including its verified open). A grouped metric over the session fell from 2.8-3.1 s to 2.6 s. The rest is
  the fresh open's hashing and the row scan, which compaction does not change.
- A live step rewrites at most 250,000 rows, about 1.25 s. The admission queue holds 65,536 records, and a chunk stays
  small only below about 12,800 records per second at the default interval. The queue therefore absorbs a step with
  room to spare. Above that rate every chunk already reaches the targets, and no step runs.
- Compaction reads rows through the materializing reader. A column-level copy would be faster, and is left for when
  the budget needs it.
- Journal chunks are not coalesced. They are admitted evidence, and their names encode recorded order (ADR-023). A
  long recording still names one journal file per publication.

## Alternatives considered

- **Replace every derived file, as re-derivation does.** Rejected. It rewrites the whole session each time, where
  compaction rewrites only the small publications.
- **Leave replaced files as orphans for a later sweep.** Rejected. Disk use would double until someone swept it.
  Releasing them through the retention path respects leases and keeps disk use flat.
- **A new retention kind for compaction.** Deferred. It would change the manifest format, and older readers refuse an
  unknown kind. The `DerivedFiles` record with its stated reason is accurate: files were released and their rows
  moved.
