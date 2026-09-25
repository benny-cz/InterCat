# InterCat implementation status

Updated: 2026-09-25 · Plan revision: 108 · Branch: `main`

This is the **current resume point**, not a running transcript. Update the backlog and open-work tables in place after
each slice, then add only a short latest-change note. The complete pre-revision-104 chronology, measurements, and old
verification ledger are preserved in [the historical status](history/IMPLEMENTATION-STATUS-through-revision-103.md).
The product contract and milestone gates remain in [the implementation plan](design/INTERCAT-IMPLEMENTATION-PLAN.md).

## Where the product stands

M0 and its IC-010a capture-impact follow-on are complete on the measured development build. M1's journal, profiles,
physical store, commit/recovery, and leases exist, but canonical import reuse, operation/entity checkpointing, and
mechanism breadth remain. M2 has a real broker-driven Explore and saved-session Desktop flow, a navigable real
overview/evidence ladder, live publication, health, interval ranking, minimap and zoomed timeline; the full per-rung
graph/operation view and steady-state feedback budget are open. M3–M5 are not complete. Two of §11.3's three sharing
presets exist: a metadata-only **report** and a reopenable redacted **session package**. The original evidence package
does not. The communication graph is a bounded §6.3 projection, qualified on a real 534-process import: processes with
no relationship are counted in one node, groups collapse only under budget pressure, executable groups read as file
names, and positions survive descents and live publications.

The ordinary user's path is `icat capture` or Desktop Explore through the broker; `icat import` builds a session from
ETL. `icat session`, `processes`, `metric`, `overview`, `timeline`, `evidence`, `raw`, `retain`, and `export` inspect or
manage it. `icat export --share-redacted` and Desktop's “Share redacted report” create allowlisted, pseudonymized
JSON/CSV. `icat package --redacted` and Desktop's “Share redacted session…” create a new session directory that reopens
everywhere, with pseudonymous names/IDs/addresses and synthetic records ([`redacted-session-v1`](../contracts/redacted-session-v1.md)).
Ordinary detailed `intercat-export-v1` retains sensitive names and raw locators and must not be described as safe to share.

## Backlog status (update this table, not the archive)

| Item | Current state | Remaining acceptance gap |
|---|---|---|
| IC-001–004, 007–010a (M0) | Complete for measured M0 scope | Qualify other supported retail builds and mechanisms as their gates require. |
| IC-005/006 feasibility | Measured: TCP `TrafficVisualization`; pipe `Unsupported`; RPC `ExperimentalEvidence` | Sections/ALPC and wider mechanisms are unqualified; no inferred peer or RPC byte claims. |
| IC-011 journal | Complete for validated sources | New source/content adapters need their own evidence. |
| IC-012 profiles | Metadata Explore and Focused TCP enforceable; Content request preview refuses start | Payload-specific scope, body policy and impact proof before enabling Content; broader profiles remain. |
| IC-013 canonical import | ETL import into verified session implemented | Completed-import reuse/catalogue, normalizer-upgrade generations, ETL/journal overlap disclosure. |
| IC-014 broker | Authenticated pipe, protected root, durable ownership/recovery, live evidence, ordinary CLI/Desktop client implemented and exercised | Installer pre-creation, retail-build matrix and remaining broker release qualification. |
| IC-015 metrics/entities | Source-observation metrics, process/executable grouping, TCP/UDP relations, peer/channel lower bounds | Canonical transfer owner, operations/topology, IPv6/non-TCP relations, full coverage epoch publication. |
| IC-015a segments | Complete observation/source-field tables | Compression and derived scale structures are later work. |
| IC-016 store | Complete M1 commit/recovery/lease/explicit-retention scope | Rolling retention policy and cross-process pin quota. |
| IC-016a checkpoint | Not started | Live entity/endpoint state and open-operation censoring at eviction boundary. |
| IC-017 Desktop projection | Real overview, channel/evidence ladder, layout scheduling, live follow, interval/zoom/minimap with wheel and keyboard, and a bounded §6.3 graph qualified on real data (quiet fold, group collapse, table-shared selection, carried layout) | Stable group regions for many drawn groups; a densely related real capture; per-rung eligible graph/operation/byte composition, persisted overview pyramid, bounded steady-state feedback. |
| IC-018 query identity | Metrics identity frozen; CLI/Desktop export scopes share projection | Full UI query identity, generation-aware numeric cache/cursors and coherent bundle publication. |
| §11.3 sharing | Metadata-only report (`intercat-share-report-v1`) and reopenable redacted session package (`redacted-session-v1`) implemented, CLI and Desktop | Original evidence package preset; packages above 1,000,000 rows (interval-scoped package or streamed pseudonym tables). |
| M3–M5 release | Open | Multi-machine/workspace, full scale/reliability/accessibility/installer/build matrix and release gates. |

## Recent completed slices

- **Revision 108 — the graph qualified on real data (§6.3, §6.4, §19.4):** revision 107 arrived uncompiled
  (five CA1859 and five xUnit2031 errors) and, on the real 534-process import (one admitted relationship), drew 197
  isolated circles, one edge and colliding `\Device\HarddiskVolume8\…` labels. Now:
  - **Quiet fold, first in the fallback order:** processes with no relationship in the published scope fold into one
    edgeless **No relationships** node, or into an opened group's **Other members**. A lone quiet process stays
    itself; the remainder never absorbs the quiet node; the focused process is never folded.
  - **Cheaper, minimal fallbacks:** group collapse counts only individually drawn members. The node budget is met by
    an exact count and the edge budget by bisection over the collapse order. Folds are monotone, so the shortest
    fitting prefix is found in O(log n) drawings instead of one per group.
  - **Readable names:** `ExecutableNames` labels a group by file name plus just enough folders to disambiguate
    (`git.exe (cmd)`); the path moved to `ProcessGroup.Detail`, which the inspector shows on its own line.
  - **Shared selection (§6.4):** a group row rings its drawn nodes, and a broken ring marks aggregates that hold part
    of a selection. The inspector says where each member is drawn, E scopes evidence to the group, and a selected
    aggregate survives a live publication.
  - **Layout (§19.4):** a fresh workspace seeds on the lattice, not the overview's placeholder circle, so aggregates
    no longer land on edges. A layout memory returns reappearing nodes to their place; positions carry across
    publications of one session.
  - **Presentation:** labels are single-line, ellipsized and placed without overlap. Pane headers give their summary a
    full-width line. The header counts relationships "among N of them".
  - **Tests:** an opt-in real-session UI test (`INTERCAT_REAL_SESSION`) replaced the throwaway local one, and a flaky
    redaction test no longer fails when a random token contains "8080".
  - **Plan:** the §19.4 claim that metric-ordered bands are stable across refreshes was corrected.
- **Revision 107 — bounded communication-graph projection (§6.3):** `GraphProjection` keeps the drawing under the
  200-node / 500-edge budget before layout, keeps every process in exactly one drawn node, preserves source
  relationship identities through aggregate edges, re-counts rather than re-clusters under a brush, and fails the graph
  closed without taking down the tables. The 512/4,096 layout caps remain a hard safety bound.
- **Revision 106 — reopenable redacted session package (§11.3, I22):** `RedactedSessionPackage` builds a new session
  under [`redacted-session-v1`](../contracts/redacted-session-v1.md) with fresh identities, synthetic journal records,
  bijective pseudonyms with meaningful fixed points, an allowlist for source fields and a retention-protected
  `RedactionPolicy` dependency. A package is published only after it is reopened and verified value by value.
  Entry points: `icat package --redacted [--check]` and Desktop “Share redacted session…”.
- **Revisions 104–105:** the metadata-only `intercat-share-report-v1` ([policy](design/SHARING-REPORT-REDACTION.md));
  minimap wheel and keyboard navigation.

## Open work, dependency order

1. **The graph on a busy real machine.** Revision 108's real import has one admitted relationship. Group collapse,
   edge-budget bisection and the **Other processes** remainder are exercised only by synthetic tests. Two steps:
   - Record a real capture with many loopback-related processes, such as several TestWorkloads pairs beside ordinary
     applications, and qualify it with the opt-in UI test.
   - Replace full-width group bands with stable group regions (§19.4's stated limitation). Today a newly drawn group
     shifts every band, and dozens of groups make thin strips.
2. Make the Desktop's per-rung graph, numeric ranking, timeline, evidence and coverage one eligible generation/scope
   bundle. Eliminate the whole-machine graph at a channel rung. Include operation/byte projections where derivations
   actually support them, and say unavailable otherwise.
3. Persist an overview pyramid and incremental tiles (§10.2/S4); bound query/layout/paint costs and retest the
   missed steady-state latency target on real ETW.
4. Continue M1's IC-015 operation/topology derivations and IC-016a checkpoint without inventing unsupported
   mechanism facts. Then resume the remaining milestone and retail-build gates from the plan.
5. §11.3's third preset, the explicitly unredacted original evidence package, and redacted packages above 1,000,000
   rows.

## Verification and cautions

- Last executed clean baseline (revision 108): **897 passed, 1 skipped, in Debug and Release**, zero failures (+22 over
  revision 106's 875).
  - Application gained 17: projection, quiet fold, edge-budget bisection, remainder isolation and executable names.
  - Desktop gained 6: group selection, partial selection, carried layout, ascend and memento.
  - The skip is the opt-in real-session UI test. Run it with `INTERCAT_REAL_SESSION=<session dir>`; without the
    variable it reports skipped, never passed.
  - The ledger in `fixtures/index.json` lists the new R7/R8/R13 tests.
- Revision 108 real-data check: `icat import` of the local
  `bench/results/capture-comparison-20260921-baseline/etl/diagnostic-evidence.etl` reproduced 852 observations,
  3,104 source fields and 534 processes (134 executable groups, one admitted relationship). The opt-in test passed in
  Debug and Release, and its frames were inspected at 1456×939 and 1080×700:
  - The machine rung draws 3 nodes: the related pair and **No relationships · 532**.
  - Selecting `svchost.exe` names its 98 processes and its path.
  - Opening it draws 4 nodes, with **Other members · 98**.
  - A brush keeps the drawing's structure.
  - Headless cold timings: overview 271 ms, graph projection 32 ms, layout 13 ms, group open plus layout 47 ms.
  Revision 107 alone drew 197 nodes and 1 edge there.
- Recent real evidence: `bench/results/first-feedback-20260924T140714Z-minimap` (live projection p50 16–32 ms,
  p95 21–129 ms; first overview 0.9–1.1 s after first record). The event-to-visible steady-state budget still
  missed in the prior run (`first-feedback-20260924T132726Z-live-counters`: p95 2.6–3.2 s).
- On a 94,694-record session, a one-pass evidence export took 2.9 s instead of 83.5 s through 474 page reads;
  the output was identical. This is not a whole-product scale qualification.
- The broker/source measurements are on one pre-release Windows build; do not generalize capture overhead or
  capability tier to retail builds. The report is pseudonymized, **not anonymous**: times, counts and workload
  shapes may identify a machine. The detailed export is sensitive. Screen-reader audit is still open.
- Keep the user-owned untracked `Zip-GitFiles.ps1` and `InterCat.zip` untouched and uncommitted. Before each slice,
  inspect `git status` and these tables. After each coherent slice, build and run the full suite in both
  configurations: the analyzers treat warnings as errors, and revision 107 shows that an uncompiled slice hides failures.
  Then update this file and the plan if needed, commit and push `main`.

## Key reference contracts

`contracts/journal-v1.md`, `store-v1.md`, `segment-v1.md`, `metrics-v1.md`, `entities-v1.md`,
`query-identity-v1.md`; ADR-008, ADR-010, ADR-012, ADR-013, ADR-023–028; the complete historical ledger linked above.
