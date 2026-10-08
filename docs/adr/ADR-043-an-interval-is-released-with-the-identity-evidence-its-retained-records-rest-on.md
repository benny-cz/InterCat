# ADR-043: An interval is released with the identity evidence its retained records rest on

- Status: accepted; revision 434, decision 6 revision 435
- Date: 2026-10-08
- Amends: ADR-024 (what a release of a recording's oldest chunks gives up)
- Relates to: §12.1 S6, §20.2, IC-016a, I15, I18, I20, ADR-010, ADR-013, ADR-016, ADR-024, ADR-026,
  `contracts/store-v1.md` §8, `contracts/entities-v1.md`, `contracts/relations-v1.md`

## Context

A journal-prefix or chunk release (ADR-010, ADR-024) gives up records and keeps every row derived from them, so a
session that sheds evidence still grows by its rows: a long recording cannot run within a bound. §20.2 asks that
rolling retention release an old interval, and S6 that it never break a reference.

What a later record means rests on records before it. A process instance is derived from its PID's lifecycle records
alone: its lifetime, its epoch - how many instances of the PID came before it - its key and its name; a PID no
lifecycle record names has one instance keyed by the first record that names it (`entities-v1` §3). A connection's
incarnations are divided by its connects, accepts and disconnects, its key is anchored on its first record, and its
holder is decided by every record of it (`relations-v1` §3-§5b). Releasing those with their interval would re-identify
what stays without saying so: a still-running process would become a provisional one under another key, a reused PID's
later holder would read as its first and bind more strongly, a connection would lose its witnessed open and its key,
and an incarnation two processes held would read as held by one.

§20.2 also says to "preserve supporting lifecycle evidence separately when exact provenance is required", and that a
checkpoint must not turn a missing start into an observed one.

## Decision

1. **The unit of release is the journal's, and a boundary is a session time.** A release before an instant gives up
   the longest leading run of the journal's units - a recording's chunks, or a single journal's batches - each of whose
   rows reads before it. A row with no session time holds no unit back. The newest unit holding records is always kept,
   and with it the chunk the committed boundary names. What a release achieves is stated as its own boundary, one
   nanosecond after the latest row it gave up: every record read at or after it is retained. Before it the session
   holds only the rows kept as evidence (decision 3) and records delivered with later ones.
2. **Rows go with their records.** Every row derived from a released record goes, but for decision 3's. A row is told to
   its unit by its record's number; a journal not stored in the order its records were numbered is refused, since the
   numbers would not say. Rows whose records an earlier journal release gave up are the oldest, and go first.
3. **Identity evidence is kept as evidence, not summarized.** For each process a row that stays belongs to: every
   lifecycle record of its PID, or, for a PID no lifecycle record names, its first record; and the same for each parent
   its instances link to, and theirs. For each connection end such a row names, and its mirror, which pairing reads:
   its connects, accepts and disconnects, and in each incarnation its first record and the first record of each owner,
   bound instance and strength no staying row has. A kept row stays as a retained one does, so its own process and
   connection are kept in turn, until a round keeps nothing more. Their source fields go with them. Every row that stays
   then binds to the same instance at the same strength, has the same other end and channel key, and every instance it
   names is the instance it was - which a test asserts over random recordings.
4. **Derived files holding a row given up are rewritten.** A publication - the segments and dictionaries one generation
   published - holding one is replaced by one holding its other rows, unchanged; any other is carried. Every index goes
   with a replaced segment. The coverage ledger, clock calibration, plan, finalization marker and collector identities
   are carried. Kept content goes with its chunk; beside a single journal, whose first batches would be rewritten from
   under it, it is released on its own first.
5. **The record states the interval.** A new kind, `Interval`, lists every file it released - journal units, their
   content, the derived files replaced, the indexes - with their bytes, the journal records given up and the digest of
   the files' dependency lines, and states its boundary, the rows given up and the rows kept. Every later generation
   carries the latest one, as it carries a journal-prefix release. Re-derivation is refused while kept rows remain,
   since their records are gone.
6. **An operation open across the boundary keeps its records** (revision 435, I20). An RPC call one of whose records
   stays keeps the other, and an ambiguous run of one keeps every record of it, so the call pairs, times and names its
   interface as before, and every later record of its key pairs as it did: a call whose records all go opened and
   closed its key's walk. A completed client call that stays keeps what its other end is read from - every ALPC send on
   its thread in its window, every receive of its one send's message id before it stopped, and the first server call the
   receiving thread began after the receive - and a server call that stays keeps every client call that reached it, so
   one two calls reached stays neither's. An HTTP exchange one of whose buffers stays keeps every buffer, and a use of a
   number opened only because it repeated a buffer of the use before keeps that use, whose going would join it to an
   earlier one. These rows are kept within the same rounds as decision 3's, so an operation's process and a kept row's
   operation are kept in turn. An operation is then complete rather than censored at a retention boundary; one whose
   start the capture never saw is still stated as such.

## Alternatives considered

- **A summary checkpoint the derivations start from**, holding the still-live instances and endpoint state at the
  boundary. Rejected for now: each derivation would need a second entry point seeded with state, and a summary cannot
  be checked against the evidence it summarizes. Kept rows are evidence the derivations already read, so every derived
  fact of a retained record is exactly what the whole capture gave it. Revision 162's derivation checkpoint, the
  natural source of such a summary, is released with the segments it describes.
- **Release rows by time and keep their records.** Rejected: rows would be gone while their records stayed, and a
  re-derivation would bring them back.
- **Rewrite part of a unit.** Rejected, as in ADR-024.
- **Keep every row of a process or connection still present after the boundary.** Rejected: a long-lived connection's
  traffic would never go.

## Consequences

- A long session sheds rows with its records. What it keeps is bounded by the processes and connections that stay - a
  few rows each - not by their traffic.
- A connection open across the boundary keeps its key, lifetime, holders and witnessed open; its records and bytes are
  what stays of it, and its first and last readings are those of the rows that stay.
- Not yet decided, and each its own revision: readers stating the interval as released rather than quiet; the
  command line's release; and a rolling policy, which must run inside the recorder, since a release published beneath
  a running recorder fails its next publication (ADR-024).
- An RPC call, link or exchange straddling the boundary is kept whole, so its records before the boundary are counted
  wherever its own rule counts it; a long call keeps the ALPC records of its window.
- A release reads every row several times and derives the processes and connections of the whole generation: an
  explicit action's cost. A rolling policy needs the keeping extended between generations rather than derived again.
- A process's kept first record may carry content, which goes with its chunk.
