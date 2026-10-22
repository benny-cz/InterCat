# ADR-051: A fixture is named for its area and numbered for good; one index declares every fixture, and a fixture covers exactly the contracts its tests are named for

- Status: accepted; revision 723. It records the scheme `fixtures/index.json` has followed since M0, corrects where
  §13.5 described another, and adds the checks that hold it
- Date: 2026-10-22
- Relates to: §13.1, §13.5, §13.6, §14.2, §21.2 item 12, P27, `fixtures/index.json`,
  `tests/InterCat.Architecture.Tests/FixtureTraceabilityTests.cs`

## Context

§13.5 named fixtures `FX-<mechanism>-<nnn>`, with a code from `EN-Mechanism` and `PLT` for platform, clock and
multi-host scenarios. It had each fixture declare its identity, coverage, truth, expected result, environment and
provenance beside its data, and asked for two derived checks: every rule and invariant named by at least one passing
fixture or test, and every milestone gate naming only fixtures that exist.

What grew differs from each of those. Of fourteen fixtures, nine are named for a mechanism - TCP, UDP, PIPE, RPC, HTTP -
and five for what their evidence is for: IDENTITY, JOURNAL, QUERY and CONTENT, which no mechanism code names. Their
declarations are entries in one file, `fixtures/index.json`, beside a ledger of the tests that assert each contract;
a fixture's own directory holds its data alone. The ledger's check ran, and it counts tests. The milestone gates of §14
name criteria, never a fixture, so the gate check had nothing to read; what ran in its place checked that each entry's
truth log, expected artifact and tests exist.

Nothing checked a fixture's `covers`, the contracts it says it asserts, and writing this record found them adrift.
Ten of fourteen fixtures made 18 claims that none of their listed tests asserts - FX-TCP-001 claimed R9, R21 and P27,
FX-CONTENT-001 R17 and P2 - and one listed a test for a contract it did not claim. Tests that read a fixture's committed
evidence were missing from its list: FX-TCP-001's lacked the test that re-evaluates its evidence to its tier, and all
four transport fixtures' lacked `icat verify`'s tests, which rebuild runs from that evidence. And bench results of
September named `FX-IMPORT-001`, a local import probe the index never declared.

## Decision

1. **A name is an area and a number.** `FX-<AREA>-<nnn>`: AREA is an upper-case code - a mechanism's short code (TCP,
   UDP, PIPE, RPC, ALPC, HTTP) where the evidence is one mechanism's, the area it serves where no one mechanism is its
   subject (IDENTITY, JOURNAL, QUERY, CONTENT), and PLT for platform, clock and multi-host scenarios - and nnn three
   digits, counted from 001 within the area. A number is permanent: a fixture that is superseded, or a number given to
   evidence that is not kept, is listed under `retired` with the revision and the reason, and the number is never given
   to another fixture.
2. **One index declares every fixture.** `fixtures/index.json` holds an entry per fixture: `id`, `title`, `scenario`
   (its §13.1 scenario family), `milestone`, `mechanism` (its `EN-Mechanism` code, where it has one), `backlog`,
   `covers` (rules, invariants and prohibitions), `tests`, `truth` (generator, seed, log and why it is independent),
   `expected` (the artifact a test compares with, and what it holds), `environment` (each build it ran on, the date and
   the result) and `provenance` (adapter, whether it is shareable, and what it holds). `fixtures/<id>/` holds the data.
3. **Coverage is by tests.** A test names the contract it asserts by beginning its display name with the contract's
   ID (§13.6). The index's ledger lists, for each rule, invariant and prohibition, exactly the tests so named, or, where
   none is, the reason and the item that will cover it; every contract is in one or the other.
4. **A fixture covers exactly what its tests assert.** A fixture's `covers` is the set of contracts its listed tests are
   named for. A claim no listed test asserts fails the check, and so does a listed test whose contract is not claimed. A
   test named for a section or an ADR asserts no contract the ledger counts, so it is not listed.
5. **The checks** (`FixtureTraceabilityTests`):
   - The ledger, as decision 3.
   - Each entry resolves: a well-formed ID, held by one entry and by no retired number, with its directory; a directory
     under `fixtures/` for no entry fails too. Its truth log, expected artifact and tests exist; its milestone is one the
     plan defines (§14, §15) and its backlog items ones §14.1 lists; and its `covers` is as decision 4.
   - Every fixture ID that code, tests, tools, contracts, ADRs, capability reports or bench results name is one the index
     holds or has retired. The plan's examples of the form are exempt. This takes the gate check's place: a gate names
     criteria, and what names a fixture as evidence - a measured tier in the source catalog (P27), a contract, an ADR, a
     benchmark's input - is where a fixture that does not exist would mislead.

## Consequences

- A new fixture is its data under `fixtures/<id>/`, its entry, and the tests that read it, each named for the contract
  it asserts. A claim without such a test fails, as does a test that asserts a contract the entry leaves out.
- Revision 723 made the existing entries exact. It listed the tests that read a fixture but were not listed, and added a
  test that each transport fixture's committed evidence holds only loopback flows its truth names; together they back 10
  of the 18 claims. It removed the 8 that no test asserts - R9 from FX-TCP-001, I21 from FX-JOURNAL-002, R17 from
  FX-CONTENT-001, P15 from the three HTTP fixtures and R22 from the first two - and claimed the 7 contracts that listed
  tests assert, among them R18 and I14 for the transport fixtures `icat verify` reads. `FX-IMPORT-001` is retired with the
  reason: a local probe holding machine data, which bench results of 2026-09-22 and 2026-09-23 name as their input and
  which was never committed or declared.
- What the checks do not prove: that a listed test reads the fixture's own data. A test may assert, on synthetic input,
  behaviour the fixture's run demonstrated - the capture lifecycle tests listed for FX-TCP-001 do. And `environment`
  entries are written by hand from measurement runs.
- A renamed or removed test fails the ledger until the index follows it.

## Alternatives considered

- **A declaration beside each fixture's data**, as §13.5 first said. One file is read and checked whole; declarations
  spread over directories would need a walk to find what one file already lists, and a fixture whose data is not
  committed - machine data never is (§13.6) - would have nowhere to declare itself.
- **Mechanism codes only.** The journal, identity, query and content fixtures exercise no one mechanism; forcing one, or
  PLT, would hide what each is for.
- **Counting a fixture's covers as coverage of a contract.** Coverage is what a test asserts; a fixture without a test
  asserts nothing.
- **Deriving covers from the tests instead of storing it.** The stored list reads at a glance in the index and in a
  review, and the check keeps it from drifting.
