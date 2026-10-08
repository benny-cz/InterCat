# ADR-044: A follow goes on across releases of the session it derives into

- Status: accepted; revision 438; decision 4 amended by ADR-048 (revision 446)
- Date: 2026-10-08
- Amends: ADR-027 (what a follower refuses)
- Relates to: §9, §20.2, R16, S5, S6, IC-016a, ADR-024, ADR-027, ADR-043, `contracts/store-v1.md` §8

## Context

Rolling retention (§20.2) has to bound the session a person watches while a capture records. In a broker capture that
is the follower's derived session (ADR-027). The broker's evidence session holds journal chunks only, so an interval
release (ADR-043), which needs rows, applies to the derived session. Only that session's own writer, the follower's
process, may publish the release, and only between two mirrors: a release published beneath another writer fails that
writer's next publication (ADR-024).

The follower matched its chunks to the evidence's by position: the derived session's n-th chunk had to be the
evidence's n-th. A release of the derived session's oldest chunks broke that in three ways:

- the same follower refused to go on;
- a follower opened afresh counted only the records of the chunks the session still held, so it numbered every later
  record from the wrong journal index;
- callers decided that a follow was complete by comparing the chunks the derived session holds with the evidence's, and
  the Desktop's live edge compared them with the broker's chunk numbers.

## Decision

1. **The derived session's chunks are a run of the evidence's, found by their bytes.** The follower locates the derived
   session's oldest chunk among the evidence's by length and digest, and every later chunk must follow it there, byte
   for byte. A run that starts after the evidence's first chunk is accepted only when the derived session states a
   release, by interval or of a journal prefix. Otherwise the session was changed by hand, and the follow is refused as
   other evidence is.
2. **A follow counts the capture's chunks.** A step's derived chunk count is the number of evidence chunks the derived
   session has followed: those it holds and those its releases gave up. "Every chunk followed" still reads as the
   derived count equal to the evidence's, and the live edge previews the chunks after the ones followed.
   `LiveSessionFollower.Followed` says the same from the two manifests alone, for a crashed follow's card.
3. **A follower opened afresh reads the released chunks back from the evidence.** They are still there, since the
   evidence keeps every chunk from its first. Each is checked by length and digest as a mirror checks it. Its records
   continue the capture's journal index and every stream's highest ordinal, so the next chunk's rows are numbered as the
   capture numbered them.
4. **Evidence that released its own records is refused, explicitly.** It no longer says where the capture's journal
   began, so a follow, even into an empty session, would number what it derives from the wrong record. ADR-027 refused
   it only where a mirrored chunk differed.

## Consequences

- Rolling retention of the watched session can run in the follower's process, between mirrors, with nothing else
  changed. That policy is its own revision.
- The broker's evidence still grows until its quota stops the capture. Releasing it while a viewer follows remains to
  be coordinated (ADR-027), and under this decision a follower refuses evidence that released records.
- A follower opened afresh on a session that released chunks reads the evidence's copies of them once, as many bytes as
  the session gave up.

## Alternatives considered

- **Record in the derived manifest how many chunks it released.** Rejected: a manifest carries only the latest release
  of each kind, and a count could disagree with the chunks, which the bytes cannot.
- **Take the next journal index from the rows.** Rejected: the rows a release gave up carry no index, and the next index
  is the evidence's to say.
