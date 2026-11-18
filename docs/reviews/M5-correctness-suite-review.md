# M5 review — the correctness suite (§13.2)

Date: 2026-11-18, at plan revision 1069; the Intervals verdict updated at revision 1070 and Async publication's at 1071 and 1072
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
| Intervals (I20) | `OperationColumnsTests` (since revision 1070, §21.1's operation from 0.5 s to 2.5 s: started 1/0/0 and completed 0/0/1 by the metrics, running 1/1/1 and occupying 0.5/1/0.5 s in the timeline's density columns; one stopping where a column begins occupies none of it), `OperationColumnProperties` (each column's overlap, flagged and untimed counts and clipped occupancy against a scan of generated operations, open ones to the interval's end), `SessionRpcCallsTests` (an overlap count across columns, an open call to the interval's end, each call on one side of a split, a channel's clipped call time), `OperationMetricsTests` (started, completed and failed counts per interval against an independent oracle over 40 seeds) | Covered since revision 1070 |
| Navigation (R10, R13, §6.7) | `NavigationProperties` (the pointer's time through a zoom, zoom and pan restored unless clamped, and since this revision mixed sequences of zooms and pans at the timeline's 10 ms minimum span), `EvidenceRungWindowTests` (a pinch zooms against the viewport it began from) | Covered; the product clamps to no overscroll, which §6.2's allowance holds |
| Async publication (R6, R7, I15, I16) | `AsyncPublicationTests` (since revision 1071: a brush's count, a process's channels and timeline, and E's records, each read and then held until after a later brush, a filter added or removed, or the rung left, compared whole with the same gestures answered in time), `EvidenceRungTests` (a second brush landing first: ranking, table, graph and export answer only it), `GraphLayoutSchedulerTests` (a late layout of an older graph is discarded), `InvestigationWindowTests` (an abandoned merged-time zoom is never drawn; since revision 1072, a merged timeline placed before an alignment was withdrawn is read again under the alignment the rows show, never drawn) | Covered since revision 1072 |
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

## Changed by revision 1069

- The boundary and conservation properties (I3, I4, I5) held a bucket class nothing in the product used. They now hold
  the timeline's own columns - the column a reading falls in, and each column's interval - over extents as wide as a
  reading allows, where a 64-bit product of a span and a column count would wrap; the unused class is removed.
- A golden file was written by its own test whenever it was missing, so deleting it made the test pass. The query
  corpus (FX-QUERY-001) and the journal (FX-JOURNAL-002) are now regenerated only when asked for by name, and that run
  fails with what changed, as §13.6 asks.
- Navigation's properties gained mixed sequences of zooms and pans, at one reading's minimum span and at the timeline's.

## Changed by revision 1070

- Clipped occupancy (I20) is implemented where the timeline counts operations into columns. A lane's density columns of
  an RPC channel's calls and of a process's HTTP exchanges hold, beside each column's overlap count, the time its
  operations ran inside it, each one's clipped to the column - a call open at capture end's to the interval's end - and
  count apart the calls no start and stop timed; a column's card says both. §21.1's scenario is a test, and a property
  compares every column with a scan of generated operations. Ten properties are now covered and six partly.

## Changed by revision 1071

- A late answer is raced against the gestures after it. Every read the window awaits passes a hold a test can arm by
  the step that asked, which keeps the answer once read until it is let go; a brush's count, a process's channels and
  timeline, and E's records are each held past a later brush, a filter added or removed, or the rung left, and the
  workspace is compared whole with one given the same gestures answered in time. E's records and a process's channels
  write a late answer into the request it answered, never the one on screen, so their races hold by construction; the
  count's and the focused timeline's guards hold theirs, and a mutation of either is caught.

## Changed by revision 1072

- A merged timeline names the alignment revision it was placed under, and a drawing is one reading of the
  investigation's file - its lanes, their sentences, the overlaps and the notes had been read from up to three readings,
  in the window and in `icat workspace timeline` alike. The window reads again a timeline placed under an earlier
  alignment than the rows it shows; a test holds one past a withdrawal and finds it is never drawn. Eleven properties are
  now covered and five partly.

## Open

1. Reproducibility: a segment pass's results at one worker and at many.
2. Layer accounting: an RPC annotation over ALPC evidence; canonical-owner totals stated as not implemented.
3. Identity: a reused thread ID within RPC peer pairing.
4. Specification identity: the golden corpus through the CLI and the window.
5. Encoding: every §6.6 meaning to its redundant channel in one test.
