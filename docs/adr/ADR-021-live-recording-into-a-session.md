# ADR-021: A live recording publishes one session generation when its capture stops

- Status: accepted for M1, with incremental publication owed to M2
- Date: 2026-09-23
- Decision owners: InterCat maintainers
- Relates to: §9.3 (capture pipeline), §20.1 (commit protocol), §20.6 (health ledger), IC-014, R8, R21, I7, I8,
  ADR-010, ADR-018, `contracts/store-v1.md`, `contracts/coverage-v1.md`

## Context

Every published session so far came from `icat import` of an ETL that another tool recorded. The owned ETW session,
the admission table, the envelope mapper, `journal-v1`, the normalizer and the derived-generation builder all existed.
The capture-comparison harness even wrote a live `journal-v1` file. But no path took a live capture into a session a
reader can open. The broker's `IBrokerCaptureRuntime` had only a test fake. So the M2 exploration slice had nothing
live to explore.

## Decision

1. **One runtime, two callers.** `LiveSessionRecorder` records an owned capture into a new session. `icat record` calls
   it directly from an elevated shell, and the broker's capture binding will wrap the same runtime.
2. **Records keep acquisition order.** One writer thread drains the admission queue. Each admitted record becomes a
   journal envelope with its acquisition ordinal and derives its rows exactly as an imported record does (I7). The
   normalizer plan and schema table precede the first record, so the session re-derives like an import.
3. **Publish once, when the capture stops.** The generation (journal, plan, segments, dictionaries and coverage
   ledger) is published through the §20.1 commit protocol after the stop. A recording interrupted before it stops leaves only
   staging files. Publishing while recording, so a viewer can follow a capture, is owed to M2. It needs the journal's
   committed boundary to advance under an open lease.
4. **One capture per session.** A recording is refused into a session that already has a generation, as an import is.
5. **Coverage is measured, never assumed.**
   - The live epoch's collected descriptors are those of providers whose enablement succeeded.
   - Its losses are the session's own counters: events the session lost, buffers its consumer lost, records the full
     queue dropped, and admitted records the writer never journaled.
   - A queue-dropped record is loss, not a delivery of its descriptor.
   - If a loss counter could not be read, the generation is published without a ledger and its coverage stays unknown.
     An unread counter is not a zero (R21).
6. **A refused clock blocks publication.** If a delivered reading turns out not to be on the clock the journal names,
   nothing is published, because every record would be presented against the wrong clock (I8).

## Consequences

- An 8-second Explore recording on this machine journaled 4,273 records - process lifecycle, TCP and UDP - with every
  loss counter at zero. It reopened through `icat session`, `icat processes` and `icat metric` like an imported one,
  and its ledger reported all three mechanisms covered.
- Implementing it found that the import's coverage ledger named its providers by GUID, because an import enables no
  provider. The shared tally now names them from the source catalog.
- The viewer still sees a recording only after it stops. The broker still needs its binding and a session root the
  viewer can read.

## Alternatives considered

- **Write the journal live and derive afterwards with `icat rederive`.** Rejected: it doubles the work and leaves no
  session to open until a second command runs, and the derivation code is the same either way.
- **Publish a generation every few seconds now.** Deferred rather than rejected: each generation re-names the whole
  journal under the current contract, so it needs a growing committed boundary first (§20.1, ADR-010).
