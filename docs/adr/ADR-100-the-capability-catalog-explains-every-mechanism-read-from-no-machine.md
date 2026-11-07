# ADR-100: The capability catalog explains every mechanism, read from no machine

- Status: accepted for M6's capability catalog; `icat capabilities --catalog` prints it and `--find` searches it (revision 1019)
- Date: 2026-11-07
- Decision owners: InterCat maintainers
- Relates to: ADR-003, ADR-077, ADR-085, ADR-086, plan §4.1, §14.2, M6, `capability-report-v1`, P27

## Context

M6 asks for a searchable capability catalog explaining unsupported mechanisms, and §4.1 for visible unsupported or
experimental entries until a mechanism is validated. `icat capabilities` probes one machine: it lists the mechanisms a
registered source covers, a profile leaves unrequested or a fixture measured on that machine's build, each with its state
and tier, and leaves out a mechanism none of those names (ADR-085). A person asking why InterCat shows no DDE, clipboard
or QUIC found nothing, and the summary and reason the report computes for each mechanism were in its JSON alone.

## Decision

1. **The catalog is InterCat's own, read from no machine.** It lists every mechanism EN-Mechanism names but the unknown
   one: the sources the catalog holds for it, those no profile requests for it, the tier fixtures measured on each build
   with the fixture and its date, and in words where it stands and why, citing the decision that set it. A machine's
   report still lists only what that machine and its build's fixtures say (P27).
2. **An unsupported mechanism is explained, never left out.** One no source records says what keeps it out: a source
   that recorded nothing of it (ADR-003, ADR-085), events that name nothing (ADR-077), records that cannot tell it from
   another (ADR-086), no adapter, or instrumentation outside driverless capture.
3. **A search keeps the mechanisms whose words hold its text** - name, sources, fixtures, builds, tiers by name or in
   words, and explanation - ignoring case, and says how many of all it kept; none kept is said.
4. **It is printed, never written as a machine's report.** `--output` with the catalog is refused, and `--json` writes
   it as `capability-catalog-v1`, naming the search and how many mechanisms the catalog holds.

## Consequences

- Every explanation cites only decisions the repository records, which a test holds.
- The window has no view of the catalog yet; `icat capabilities --catalog` is where a person finds it.
- 16 mutations of the path are caught.
