# ADR-048: Evidence states what its chunk releases gave up in all, and a follow goes on across them

- Status: accepted; revision 446
- Date: 2026-10-08
- Amends: ADR-044 (decision 4, what a follower refuses), ADR-027 (what a follower refuses)
- Relates to: §9, §20.2, R16, S5, S6, IC-016a, ADR-024, ADR-043, ADR-045, `contracts/store-v1.md` §8,
  `contracts/live-follow-v1.md` §4

## Context

A rolling capture keeps the newest stretch of session time in the session a person watches, the follower's derived
session (ADR-045). The broker's evidence still keeps every chunk it recorded until the capture stops, so its journal
quota stops a rolling capture as soon as it would stop one that keeps everything: the evidence, not the watched session,
bounds how long a capture can roll.

The evidence's oldest chunks can be released as any recording's are, whole and by its own writer (ADR-024). A follow
refused evidence that had (ADR-044 decision 4), because it no longer said where the capture's journal began: the follower
numbers each record by counting every record before it, and a follower opened afresh counts them by reading the chunks
its session released back from the evidence. Once the evidence released them too, nothing said how many records went
before the chunks that remain, or where among the capture's chunks those lie.

ADR-044 rejected recording in the derived manifest how many chunks it released: a manifest carries only the latest
release of each kind, and a count could disagree with the chunks, which their bytes cannot.

## Decision

1. **A chunk release states what its recording gave up in all.** Its record carries `recording: { chunks, records }`:
   how many of the recording's oldest chunks, and how many records they held, this release and every chunk release
   before it gave up - the previous chunk release's statement plus its own, or its own alone when it is the first.
   Because each statement is cumulative, the latest release, which every later generation carries, says it all.
2. **Only what was counted is stated.** A chunk release after one that stated nothing - published before this
   decision - or after a journal prefix rewrite or an interval release, both of which give records up without counting
   whole chunks, states nothing; so does a rewrite itself. Only a journal-prefix record states it, of at least one chunk
   and of no fewer records than it gave up itself. The statement is written, and appended to the manifest's canonical
   text, only when present, so every other record is the file and the digest it always was.
3. **A follow places its chunks by their bytes, then by the statement.** The evidence holds the capture's chunks from the
   one after those it states it released. The derived session's chunks must still be a run of the capture's, byte for
   byte: its oldest chunk is found among the evidence's, or, where the evidence released the run's start, the evidence's
   oldest chunk among the derived session's, which the statement must allow. Every chunk both hold is compared. The
   follow then counts the capture's chunks whole - those the evidence released among them - and a fresh follower takes
   the journal index of the evidence's oldest chunk from the records the evidence states it gave up, reads back only the
   chunks the evidence still holds before the derived session's, and counts the derived session's own chunks that the
   evidence released among the records already stated.
4. **What a follow still refuses.** Evidence that released chunks without stating so, as before; a derived session that
   holds none of the chunks the evidence keeps, which nothing can place; and a follow into an empty session of evidence
   that released its first chunks, which would begin after records the session never held and states no release of.
5. **The broker releases only what its follow gave up (next).** The capture's owner tells the broker, as it renews its
   lease, the oldest evidence chunk its derived session still holds; the broker's recorder releases the chunks before it
   between two publications, by its own writer, never the chunk its committed boundary names, so the derived session
   always holds a chunk the evidence keeps. A capture's journal quota then bounds what its evidence holds rather than
   everything it recorded.

## Consequences

- A follow, the same follower or a fresh one, goes on across the evidence's releases and numbers every record as the
  capture did; a crashed follow's card counts the capture's chunks whole, and its finish goes on from the evidence that
  remains.
- The streams of chunks both sessions released are no longer there to check a later chunk's ordinals against; a later
  chunk is still checked against every chunk either session holds.
- A recording released before this decision, or after a rewrite or an interval release, keeps refusing a follow, as all
  released evidence did.
- The derived session's own releases state no count: a follow places them by the evidence's chunks, as ADR-044 decided.

## Alternatives considered

- **A count per release, summed by a reader.** Rejected: a manifest carries only the latest release of each kind, so the
  earlier counts would be gone; the cumulative statement is the same fact without that loss.
- **Keeping the released chunks' digests in the evidence.** Rejected: a follow needs where the remaining chunks lie and
  how many records went before them, not the bytes that went, and the list would grow with the capture the release is
  meant to bound.
- **Letting the evidence release chunks its follow still holds.** Possible under decision 3, and accepted from a person's
  own `icat retain`, but the broker's release waits for the follow (decision 5): a follow that falls behind would
  otherwise lose chunks it never mirrored.
