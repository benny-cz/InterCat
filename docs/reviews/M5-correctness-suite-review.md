# M5 review — the correctness suite (§13.2)

Date: 2026-11-18, at plan revision 1069
Scope reviewed: the sixteen properties of §13.2 and its closing sentence - scan-based references compared with optimized
indices and tiles on small randomized traces, and property tests that stress adversarial identities and intervals.
Method: for each property, the tests named for its contracts in `fixtures/index.json`'s ledger were read for the
property's required assertion, not only its contract's name, and a property is covered only where a test body makes the
assertion. A test named for a contract can assert something else of it, which the review says where it found one.

## Verdicts

| Property | Tests making its assertion | Verdict |
|---|---|---|
| Conservation (I4, I5) | `SessionTimelineTests` (each focused column equals the evidence records in it, the focus's sum the evidence count), `EvidenceNarrowingTests` (a narrowed focus sums to its narrowed page), `SegmentPassTests` (random multi-segment captures under every policy: lanes and pages each equal a row scan) | Covered for focused scopes; the whole timeline against unscoped evidence holds through each equalling a row scan |
| Weighted conservation (R3, I6) | `SegmentV1Tests` (a send-side sum is exactly its known rows, requested I/O apart and counted, a lost size unknown), `SessionMetricsTests` (requested and completed stay apart; an unknown size is unknown, never zero) | Covered |
| Layer accounting (I11, §5.3) | `SegmentV1Tests` (an application-layer RPC generation leaves the transport byte sum unchanged), `SessionMetricsTests` (endpoint activity and send side apart) | Partly: no test lays an RPC annotation over ALPC evidence, and canonical-owner totals are not implemented, so only their absence is asserted |
| Identity (R22, I12) | `ProcessInstanceTests` (a reused PID is two instances), `RelationTests` (a reused port is a second connection), `SynchronizationHandleGateTests` (a reused object address after its last close is a new object) | Partly: no test reuses a thread ID; RPC peers pair a server thread within a bounded window after the receive, not by a thread's identity |
| Reproducibility (I2, I14) | `ObservationNormalizerV1Tests` (a journal re-derives byte-identical segments), `CanonicalImportTests` (a re-import reuses its identity), `ProcessInstanceTests` and `RelationCensusProperties` (the same instances and relations however records are segmented or ordered) | Partly: no test varies the worker count of a segment pass, which runs at the processor count |
| Late evidence (I1, I17) | `LateEvidenceTests` (a later generation resolves a peer under a new identity while a leased one answers as it did, its records, instances and dependencies unchanged) | Covered |
| Boundaries (I3, I4, §10.3) | `ColumnProperties` (since this revision: the timeline's own column math at spans up to the widest a reading allows), `SegmentTimeTilesTests` (equal, backwards and untimed readings; tile counts equal a row scan), `SessionOverviewTests` (the final observation inside the fitted extent) | Covered |
| Intervals (I20) | `SessionRpcCallsTests` (an overlap count across columns, an open call to the interval's end, each call on one side of a split), `OperationMetricsTests` (started, completed and failed counts per interval against an independent oracle over 40 seeds) | Partly: clipped occupancy is not implemented, so §21.1's occupancy scenario has no test |
| Navigation (R10, R13, §6.7) | `NavigationProperties` (the pointer's time through a zoom, zoom and pan restored unless clamped, and since this revision mixed sequences of zooms and pans at the timeline's 10 ms minimum span), `EvidenceRungWindowTests` (a pinch zooms against the viewport it began from) | Covered; the product clamps to no overscroll, which §6.2's allowance holds |
| Async publication (R6, R7, I15, I16) | `EvidenceRungTests` (a second brush landing first: ranking, table, graph and export answer only it), `GraphLayoutSchedulerTests` (a late layout of an older graph is discarded), `InvestigationWindowTests` (an abandoned merged-time zoom is never drawn) | Partly: no test races a delayed query against a filter change or an alignment revision |
| Clock safety (I9, I10, §8.2) | `InvestigationWorkspaceTests` and `WorkspaceCorrelationTests` (within the combined uncertainty an order is ambiguous and no latency is said; a member's own duration unchanged through every alignment), `ClockAlignmentTests` | Covered |
| Content safety (R17, I21, I22) | `ContentViewTests` (bytes read only when revealed, refused when inspection is off, gone after release), `RedactedSessionPackageTests` (no source name, identity, address, digest or body byte in a package), `ContentSdkFixtureTests` | Covered; the R17 test of `JournalProbeTests` holds the journal probe tool, not the product |
| Coverage (R21, I13) | `LiveSessionRecorderTests` (loss injected at the source, the consumer and decode said at its own layer, the mechanism partial), `SessionCoverageTests` (delivered, quiet, uncollected and legacy apart), `ScopeCoverageTests` (a count of none is not proof of inactivity) | Covered |
| Encoding (R14) | `ThemeContractTests` (contrast in every mode; adjacent family fills separable under three colour-vision simulations; every family a glyph), `EvidencePatternTests`, and one test each for direction, unmeasured and coverage hatching | Partly: no one test walks every §6.6 meaning to its redundant channel |
| Layout determinism (§19.4) | `GraphLayoutTests` (one identity, fresh and refreshed, on 1, 2 and 8 threads twice: the same energy and positions) | Covered; the layout reads no clock |
| Specification identity (R18, §10.5) | `QueryIdentityTests` (the golden corpus's bytes and hashes; two spellings one identity; eleven variants all distinct) | Partly: the corpus runs through the specification alone, the CLI and the window agreeing on single requests |

The closing sentence holds: scan-based references are compared on randomized traces with the segment time tiles, the
persisted tile index in each of its parts - counts, tallies, byte sums and tallies, postings and the evidence pages read
from them - the live chunk tiles, the parallel segment passes and the incremental derivations. `RandomCaptures` and
`RelationCensusProperties` stress PID exit and reuse, shared ports and late records; navigation and the timeline's
columns are generated over adversarial extents.

## Changed by this revision

- The boundary and conservation properties (I3, I4, I5) held a bucket class nothing in the product used. They now hold
  the timeline's own columns - the column a reading falls in, and each column's interval - over extents as wide as a
  reading allows, where a 64-bit product of a span and a column count would wrap; the unused class is removed.
- A golden file was written by its own test whenever it was missing, so deleting it made the test pass. The query
  corpus (FX-QUERY-001) and the journal (FX-JOURNAL-002) are now regenerated only when asked for by name, and that run
  fails with what changed, as §13.6 asks.
- Navigation's properties gained mixed sequences of zooms and pans, at one reading's minimum span and at the timeline's.

## Open

1. Clipped occupancy (I20): not implemented, so §21.1's 0.5 s to 2.5 s scenario - starts 1/0/0, completions 0/0/1,
   overlap 1/1/1, occupancy 0.5/1/0.5 - has no test.
2. Async publication: a delayed query raced against a filter change and an alignment revision.
3. Reproducibility: a segment pass's results at one worker and at many.
4. Layer accounting: an RPC annotation over ALPC evidence; canonical-owner totals stated as not implemented.
5. Identity: a reused thread ID within RPC peer pairing.
6. Specification identity: the golden corpus through the CLI and the window.
7. Encoding: every §6.6 meaning to its redundant channel in one test.
