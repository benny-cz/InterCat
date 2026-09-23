# ADR-023: Re-derivation replays a live recording's journal chunks as one capture

- Status: accepted for M1 and M2
- Date: 2026-09-23
- Decision owners: InterCat maintainers
- Amends: ADR-022 decisions 2 and 5 (a row's journal index; re-derivation over chunks)
- Relates to: §18.4 (replay), §20.1 (rebuildable derivations), I1, I2, I7, I8, ADR-010, ADR-022,
  `contracts/store-v1.md`, `contracts/segment-v1.md`, `contracts/journal-v1.md`

## Context

ADR-022 made a live recording a sequence of journal chunks but left re-derivation reading one journal. On a chunked
session it refused, naming the chunks. §20.1 says derivations "can be rebuilt when their source and required schemas
remain retained", and a chunked recording retains both. The refusal therefore denied every live recording the rebuild
§20.1 promises, so a recording that outlived a normalizer change would stay on the old derivation.

ADR-022 also made a row's journal index count within its chunk. Index 5 then existed once per chunk, so the index shown
beside a metric's evidence no longer located a record in the capture. A replay of the whole sequence could reproduce it
only by knowing which generation had derived each row.

## Decision

1. **A row's journal index counts across chunks.** The index counts its record across the capture's chunks, in the
   order they were recorded. It locates a record in the capture's evidence on its own, and a replay of the chunk sequence
   derives the same index the recording derived (I2, I7).
2. **Replay reads every chunk in recorded order.** Re-derivation replays every chunk in name order. Each chunk is named
   for the generation that published it, so name order is the order they were recorded. It publishes one replacement
   generation that carries every chunk, the normalizer plan and the coverage ledger unchanged, and replaces only derived
   files.
3. **The replay proves the chunks are one recording before it publishes anything.**
   - The committed boundary names the newest chunk, and a chunk recorded after it is refused.
   - Every chunk's length and digest match its dependency.
   - Every chunk names the first chunk's capture and clock, and its schema table satisfies the retained plan (I1, I8).
   - Within each stream and epoch, every record's ordinal must exceed the highest ordinal of the chunks before it. A
     chunk replayed twice or out of order would repeat raw identities, so it is refused (I1).
   - The boundary chunk replays exactly the boundary's committed record count.

   Any failure publishes nothing, and the current generation stays published.
4. **`icat rederive` names the chunks.** It shows the chunk count in its progress and result, and adds `journalChunks`
   to its `rederive-v1` and `rederive-check-v1` documents.

## Consequences

- A live recording is rebuildable like an import. The chunked recording from ADR-022's verification had 5 chunks and
  1,288 records. `icat rederive --check` replayed all five chunks, including the last one, which held no records and
  carried only the ledger. `icat rederive` then published a replacement generation. Its `observations` answer grouped
  by mechanism matched the recorded one in value, coverage and contributions, and it read 1 segment instead of 4. The
  replacement also re-verifies.
- A replacement lays the replayed rows into bounded segments. This compacts a recording's per-chunk segments, and it
  is the only compaction available until §20.1's compaction targets are implemented. The chunk files themselves stay.
- Sessions recorded by the ADR-022 build indexed rows within each chunk. Their rows stay readable, and re-deriving one
  publishes the capture-wide index. Only display reads the index.
- Journal-prefix retention still reads one journal, and it still refuses a chunk sequence, naming it.

## Alternatives considered

- **Keep per-chunk indexes and record each row's chunk.** Rejected. It adds a column for a position that one
  capture-wide count already gives, and a reader would need two numbers to find a record.
- **Merge the chunks into one journal when re-deriving.** Rejected. It would rewrite admitted evidence, and
  re-derivation only ever replaces derived files.
