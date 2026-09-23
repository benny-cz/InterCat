# ADR-022: A live recording publishes as it records, one journal chunk per generation

- Status: accepted for M1 and M2
- Date: 2026-09-23
- Decision owners: InterCat maintainers
- Relates to: §19.3 (live publication cadence), §20.1 (commit protocol, journal lifetime), IC-014, IC-017, R21, I7,
  I8, ADR-010, ADR-021, `contracts/store-v1.md`, `contracts/journal-v1.md`

## Context

ADR-021 published a live recording once, when its capture stopped. A viewer could therefore see nothing until the
capture was over, and M2's live overview needs to follow a capture as it runs. §20.1 already says a generation names
its evidence by a committed boundary - which journal, how many bytes and records of it were durable, and that prefix's
digest - instead of implying the whole file.

Two ways to publish while recording fit that sentence.

## Decision

A live recording publishes as it records by completing **journal chunks**.

1. At every publication interval that admitted records, the writer completes the current chunk. A chunk is a complete
   `journal-v1` file: header, clock, schema table, batches and a terminal frame. The writer then publishes a generation
   carrying every earlier chunk and segment, adding the new chunk and the segments derived from it, and begins the
   next chunk.
2. Record ordinals continue across chunks, so raw-record identities stay unique within the capture. A row's journal
   index counts within the chunk its segment's generation published.
3. The committed boundary names the newest chunk. The normalizer plan is published once, with the first generation.
4. The coverage ledger is published only with the last generation, when the capture stops. An epoch still running has
   not stated what it lost, so the generations before it carry no ledger and their coverage is unknown (R21).
5. Re-derivation and journal-prefix retention keep working on one journal. For several they refuse, naming the chunks,
   instead of dropping the other chunks' rows.

## Consequences

- A reader follows a recording by acquiring the current generation again. During a 12-second recording published every
  3 s, `icat metric` read generation 1 with 626 observations and then generation 2 with 961. The recording ended at 5
  generations holding 1,288 records, all mechanisms covered and nothing lost.
- Every published file stays immutable and verifies exactly as before. A reader never shares a file with a writer, and
  every journal ends in a terminal frame.
- Costs taken on knowingly:
  - One journal file and one set of segments per publication. §20.1's compaction targets exist for this and are not yet
    implemented.
  - Every commit re-measures every dependency, which is milliseconds at current sizes but grows with the session.
  - Re-derivation and retention do not yet read a chunk sequence.

## Alternatives considered

- **One growing journal, each generation naming a longer prefix.** Deferred. A published file would change after
  publication, every dependency check would have to measure a prefix rather than the file, a reader would have to share
  the file with its writer, and a prefix would end without a terminal frame. Each of those weakens a rule the store now
  proves. Chunks give the same following behaviour and keep every rule.
- **Publish a snapshot ledger with every generation.** Rejected. A snapshot published while the capture runs would claim
  coverage that a later unreadable counter could invalidate, and a carried ledger cannot be withdrawn.
