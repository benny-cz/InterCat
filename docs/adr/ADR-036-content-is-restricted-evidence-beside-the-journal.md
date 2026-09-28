# ADR-036: Content is restricted evidence kept beside the journal, never inside its metadata

- Status: accepted; the store and its readers are revision 234, the capture path revision 235, and the viewer 236
- Date: 2026-09-28
- Relates to: §3.7, §10.1, §11, §18.2, I13, I21, I22, `contracts/content-v1.md`, `contracts/journal-v1.md`,
  `contracts/store-v1.md`, `contracts/capture-profile-preview-v1.md`, ADR-010

## Context

§11 makes opt-in content inspection part of the first release, and M3's exit gate asks for content truncation and
encryption states. Revision 233 says per record why none is held; nothing holds any.

- **The journal keeps metadata.** A journal-v1 record's body is the metadata projection its descriptor admits, under
  `metadata-only-admitted-projection-v1`, which forbids retaining the source's bytes. Every row is re-derived from those
  projections (R20). A content body in its place would take away what rows are derived from; content and projection in
  one body would make every metadata reader parse content to find its fields.
- **The store's layout already separates them.** §10.1 names `content/`, optional restricted payload chunks, beside
  `sources/`, the raw evidence.
- **The request side exists** (`capture-profile-preview-v1`): a source's validated content contract, process and
  channel selectors, a per-record and a session limit, stop-at-limit, and separate inspection consent. No store,
  admission or viewer does.

## Decision

1. **Content is evidence of its own, beside the journal.** A record's content is at most one fragment in a `content-v1`
   chunk, store dependency kind `Content` (code 9), linked to the record by its raw identity. The journal keeps the
   record's metadata projection unchanged. Metadata readers, the normalizer, segments, indexes and every session without
   content are untouched, and content never has to be parsed to read metadata.
2. **A fragment says what I21 asks.** Its classification (`EN-ContentClassification`); its direction, the record's; the
   encoding its source declares - binary, UTF-8 or UTF-16LE - and never one guessed from the bytes; its offset in its
   message when the source states one; the message's original length when the source exposes it; the bytes kept, and how
   they were cut: whole, truncated by the per-record limit, or omitted by the session limit. Missing bytes are stated as
   the ranges those lengths leave, never padded, and a prefix is never shown as a whole message.
3. **The request's limits bind the chunk.** A chunk names the per-record limit and the admission policy it was kept
   under, and a reader refuses a fragment longer than that limit. At the session limit the capture stops
   (`stop-at-limit`); a record admitted before the stop took effect keeps its fragment's lengths without bytes,
   omitted by the session limit (I13).
4. **Inspection consent travels with the content.** A chunk names the consent it was kept under. Under `disabled` a
   reader states that a record's content was kept and its lengths, and shows none of it; under `hex-text` it shows a
   bounded, inert preview a person asked for. Neither authorizes search, decoding, reassembly or export.
5. **Content reaches nothing else.** Search, rankings, metrics, the share report, the detailed export, logs, telemetry
   and crash diagnostics read no fragment. A redacted package keeps no content and no reference to any (I22). The
   original evidence package carries the chunks as the evidence they are and states how many fragments and bytes it
   carries before it saves.
6. **A chunk lives and dies with its journal chunk.** It is published in the generation that publishes the journal
   chunk whose records it holds content for, under that generation's number, and every later generation carries it as it
   carries a journal: additive publication, index publication, re-derivation and compaction. Releasing a recording's
   journal chunk releases its content chunk in the same retention record. A release that rewrites one journal's prefix is
   refused while content is kept, because it would leave content its records no longer have; a release of content
   alone, keeping the metadata, is later work with its own retention kind. A follower of an evidence session refuses
   content until it mirrors it.
7. **A chunk checks itself.** Its header and every fragment carry a CRC-32C, so a viewer lists a chunk at open without
   hashing it, as it lists a journal, and checks a fragment when it reads it.

## Consequences

- Content can be kept, shared as original evidence, and released with its journal chunk, without one metadata-only
  reader, rule or file changing. The share report and the redacted package state that they hold none.
- The capture path (revision 235) is InterCat's own fixture provider, raised by its FX-CONTENT-001 workload and recorded
  by `icat record --profile content-fixture` only, never through the broker: its messages are admitted under the one
  reviewed scoped content policy, copied bounded in the callback, and written as a chunk with each publication. Every
  other source of the same capture keeps metadata only. The fixture names its own process in its payload, so no
  application provider's event header is read as an owner (ADR-030).
- The viewer (revision 236) states a fragment's facts before any byte, and shows its bytes only when asked and allowed:
  a typed range, bounded and inert, which a person may copy as hex or save as it is (`contracts/content-v1.md` §4).
- Content from an imported file (§11.1's inspection of what an ETL already holds) is a later adapter: it will write
  chunks under its own policy, with the inspection warning §11.1 asks for.
