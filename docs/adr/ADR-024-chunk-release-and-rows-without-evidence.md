# ADR-024: A live recording is released a chunk at a time, and re-derivation never drops rows it cannot rebuild

- Status: accepted for M1 and M2
- Date: 2026-09-23
- Decision owners: InterCat maintainers
- Amends: ADR-010 (what a journal-prefix release gives up), ADR-022 decision 5 (retention over chunks)
- Relates to: §20.1 (journal lifetime, rebuildable derivations), I15, I18, ADR-010, ADR-022, ADR-023,
  `contracts/store-v1.md`

## Context

ADR-010 made releasing a journal prefix an explicit action. The release publishes a shorter journal, keeps every
derived row, and gives up the ability to re-derive the released records. Two things were missing.

**Re-derivation after a release lost rows without saying so.** A re-derivation replays the retained journal and
publishes a replacement that carries none of the earlier rows. After a release, the rows of the released records
therefore vanished at the next `icat rederive`. The replacement's rows also counted their journal index from the
first retained record instead of the capture's first record. No test covered the sequence, and no refusal or note
disclosed it. This was a defect: a release ADR-010 presents as giving up a *capability* also gave up *data*, one
command later.

**A live recording could not be released at all.** ADR-022 left journal-prefix retention reading one journal, so it
refused a chunk sequence, naming it. A long recording could not shed its oldest evidence.

## Decision

1. **A chunk is the unit of release in a live recording.** A boundary releases each chunk that ends at or before it,
   counting records in stored order across the chunks the generation names. The release publishes a retention
   generation that stops naming those chunks. It rewrites nothing, and the committed boundary does not change.
   - Only a leading run of chunks is released, and never the chunk the boundary names. A chunk from the middle would
     leave a hole in the capture, and releasing the boundary's chunk would leave a boundary on evidence the
     generation no longer holds.
   - A release that would leave no admitted records is refused, as for a single journal. That includes releasing
     every chunk but an empty final one, which carried only the ledger.
   - Rewriting part of a chunk was rejected. The retained part would be published under the new generation's name,
     which sorts after every chunk, so its records would fall out of the order ADR-023's replay depends on.
2. **The retention record names every released chunk.** Its source digest is the digest of the released chunks'
   dependency lines, `name|length|digest` joined by a line feed, oldest first. Each line is exactly what the
   superseded manifest held, and the released bytes are gone, so these lines are what stays identifiable.
3. **Re-derivation is refused when it would drop rows it cannot rebuild.**
   - A journal-prefix retention generation still holds the rows of the records it released. `Assess` and the replay
     refuse it before reading any evidence, naming how many records were released.
   - A later generation no longer carries that record. For it, the replay compares the oldest record each stream's
     current rows derive from with the oldest record the retained journals hold. It refuses before publishing if
     any rows derive from records the journals no longer hold.
   - The refusal names ADR-024. `icat retain` states it before a release is confirmed, and `icat session` shows it as
     the reason re-derivation is not available.

## Consequences

- A long recording can shed its oldest evidence in whole chunks, cheaply and without rewriting. On the 5-chunk,
  1,288-record recording, a boundary at record 700 released the first chunk. That was 626 records and 183,169 B,
  removed from disk with nothing written. `icat session` then showed 4 chunks and re-derivation as not available,
  with the reason. `icat rederive --check` refused with the same explanation and exit code 3.
- After a release, a session cannot be re-derived until rows can be carried across a replacement. That needs the
  replacement to keep the released records' rows and their journal indexes, and to label a row with the derivation
  that produced it when that differs from the replay's. Both are deferred until a normalizer revision needs them.
  Today there is only `observation-v1`, so a re-derivation can only reproduce what the session already holds.
- The preview reads every chunk to count its records. That is a full read of the evidence, bounded by the chunk
  size.
- A retention published while a recording is still running changes the generation the recorder expects to publish
  next. The recorder's next commit then fails. Retention is safe on a finished recording. A rolling window over a
  running capture needs the broker to schedule both writers.

## Alternatives considered

- **Carry the released records' rows into the replacement.** Deferred, as above. It is the complete answer, but
  labelling carried rows correctly needs multi-derivation generations, which nothing produces yet.
- **Release part of a chunk by rewriting it.** Rejected, as in decision 1.
- **Refuse chunk sequences, as before.** Rejected. A recording with no way to shed old evidence cannot run for long.
