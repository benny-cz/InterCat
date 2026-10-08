# ADR-047: A follow mirrors the content kept of a chunk's records with the chunk, byte for byte

- Status: accepted; revision 444
- Date: 2026-10-08
- Relates to: §3.7, §9, §11, §11.2, I21, R16, ADR-027, ADR-036, ADR-037, ADR-043, ADR-044, `contracts/content-v1.md` §2,
  §5, `contracts/broker-v1.md` §2

## Context

A recording that keeps content publishes it beside the journal chunk whose records it belongs to, under that chunk's
generation number, and releasing the chunk releases it (ADR-036). An elevated recorder that publishes evidence only
leaves its rows to an ordinary process, which mirrors each chunk into a session of its own and derives them there
(ADR-027), so nothing decodes or queries with elevation (R16). That follower mirrored journal chunks alone and refused
evidence that named a content chunk. A content capture could therefore be recorded only by an elevated `icat record`
that derived its own rows, and never through the broker, whose captures are evidence only - so the window, which
captures through the broker, could not keep content at all.

A content chunk names its capture and no generation: its header holds the capture's identity, the policy, the record
limit and the inspection consent, and each fragment names its record by raw identity.

## Decision

1. **The content goes with its chunk.** A follow mirrors the content kept of a chunk's records in the generation that
   mirrors the chunk, under that generation's number, so it stays paired with the chunk in the derived session as it was
   in the evidence: a release that gives up the chunk gives up its content in the same retention record, and a rolling
   follow (ADR-045) gives up content with the records it releases.
2. **Byte for byte.** The mirrored chunk is the evidence's bytes unchanged, since none names a generation. It is checked
   against the length and digest the evidence's generation recorded, read whole - every fragment checked, and refused
   unless it is the capture's whose chunk it stands beside - and only then staged.
3. **Evidence that released its content is not followed.** As evidence that released its own records is refused (ADR-044),
   so is evidence that released its kept content: the derived session would hold records whose content went without
   saying so (I21). Content that changed after its generation was published is refused as a changed chunk is, and
   nothing is mirrored in that generation.
4. **An elevated recorder keeps content as evidence only.** `icat record --evidence-only` takes a content profile, and
   `icat follow` derives the session with its content.
5. **The broker does not prepare a content capture yet.** Its prepared plan admits the reviewed metadata-only body
   policy alone (`contracts/broker-v1.md` §2), and its digest covers no content policy's limits or inspection consent,
   so it previews a Content request and refuses to start one, now for that reason alone.

## Consequences

- What a capture kept and what its follow keeps are the same bytes, under the same digest, and each record of a followed
  session finds its content as the recording's own reader would.
- A content capture recorded with elevation is derived, queried and viewed without it.
- A follower that resumes, or goes on after its session released chunks, mirrors each later chunk's content with it;
  chunks it reads back from the evidence only to find where it stands copy nothing.
- The broker's own content capture - a prepared plan that admits the content policy and digests it, the content limit as
  a stop reason, and `icat capture`'s request - is the next step, and the window's after it.

## Alternatives considered

- **Rewriting the chunk under the derived generation.** Rejected: a chunk names no generation, so the exact bytes are
  already valid there, and a copy keeps the evidence's digest, which anyone can compare.
- **Mirroring content once the capture finishes.** Rejected: a followed session would show records without the content
  their capture kept for as long as it records, and the content would not be paired with its chunk for a release to give
  up together.
- **Following evidence that released its content by carrying the release.** Rejected for now: the release names the
  evidence's chunks by its generations, which the derived session does not hold under those names, so the record would
  have to be restated rather than carried. A person who no longer wants the content releases it from the derived session
  once its capture has finished.
