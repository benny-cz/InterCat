# InterCat implementation status

Updated: 2026-09-24 · Plan revision: 106 · Branch: `main`

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
does not. **A real session above 512 process instances cannot open in the Desktop overview yet** (see open work 1).

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
| IC-017 Desktop projection | Real overview, channel/evidence ladder, layout scheduling, live follow, interval/zoom/minimap with wheel and keyboard implemented | §6.3 cluster collapse (a real 534-process session is refused at the 512-node bound), per-rung eligible graph/operation/byte composition, persisted overview pyramid, bounded steady-state feedback. |
| IC-018 query identity | Metrics identity frozen; CLI/Desktop export scopes share projection | Full UI query identity, generation-aware numeric cache/cursors and coherent bundle publication. |
| §11.3 sharing | Metadata-only report (`intercat-share-report-v1`) and reopenable redacted session package (`redacted-session-v1`) implemented, CLI and Desktop | Original evidence package preset; packages above 1,000,000 rows (interval-scoped package or streamed pseudonym tables). |
| M3–M5 release | Open | Multi-machine/workspace, full scale/reliability/accessibility/installer/build matrix and release gates. |

## Recent completed slices

- **Revision 106 — reopenable redacted session package (§11.3, I22):** `RedactedSessionPackage` builds a new session
  under [`redacted-session-v1`](../contracts/redacted-session-v1.md): fresh session/capture/clock/host ids, one
  synthetic journal record per row, rebuilt rows, source fields and dictionaries, a pseudonymized coverage ledger, and
  a `RedactionPolicy` provenance dependency (store code 8) that retention refuses to release. Each namespace is a
  bijection with fixed points where a value means the same on every machine (address/port 0, loopback, PIDs 0/4/-1).
  Names are mapped a path component at a time, executables case-insensitively. Readings move to a whole-second epoch.
  Source fields follow an allowlist; FILETIMEs and text are `Redacted`. Public Microsoft providers keep their names.
  A package is built beside its destination and renamed only after verification: reopened, every value checked,
  source tallies/sums/fixed points/distinct counts reproduced, bytes searched for source identities and names.
  Entry points are `icat package --redacted [--check]` and Desktop “Share redacted session…”, with disclosure,
  cancellable progress and an offer to open the package for review. `icat session`, `raw`, `rederive` and the Desktop
  label a package and its synthetic records. The previously uncommitted report fix now emits 100-ns presentation ticks,
  matching the report's own interval unit. Stale plan lines (§6.4/§14 "redacted export open", §20.4 pre-M2 CLI) and
  `segment-v1` §10 were corrected.
- **Revision 105 — minimap navigation:** wheel zooms at its pointer, including after the pointer moves outside the
  current brush; the minimap is focusable and shares the timeline's arrow/Home/End/+/-/0 keyboard path. The same
  revision's storage audit showed a replacement derivation could not be a redacted package (revision 106 built one).
- **Revision 104 — redacted sharing report:** `intercat-share-report-v1`, an allowlisted pseudonymized JSON/CSV report
  ([policy](design/SHARING-REPORT-REDACTION.md)), from `icat export --share-redacted` and the Desktop.

## Open work, dependency order

1. **Let every real session open in the Desktop.** On a real import (534 process instances, 476 of them rundown), the
   overview refuses at the provisional 512-node bound, so the Desktop cannot open an ordinary capture. Implement
   §6.3's cluster collapse (groups over 25 members, lowest-metric first, member and edge counts shown) so the overview
   stays within the layout budget without omitting anyone, and qualify it on that session.
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

- Current tests: **875 passed in Debug and Release**, zero failures (+17 over revision 105): 12 package tests with
  mutation-checked fidelity and leak assertions, 2 storage provenance tests, 1 re-derivation refusal, 2 headless
  Desktop share/reopen tests. I22 moved from declared-uncovered to covered in `fixtures/index.json`.
- Revision 106 real-data check: a real imported session (852 rows, 3,104 source fields, 534 processes) packaged,
  verified and reopened. Process summary, 482 parent links, 6 executable groups and 534 per-process counts matched the
  source exactly; an independent byte scan found no source identity, and readings moved from 1.8e13 to 1.5e5 ticks.
  The same session is refused by the Desktop overview (534 > 512 nodes): open work 1.
- Recent real evidence: `bench/results/first-feedback-20260924T140714Z-minimap` (live projection p50 16–32 ms,
  p95 21–129 ms; first overview 0.9–1.1 s after first record). The event-to-visible steady-state budget still
  missed in the prior run (`first-feedback-20260924T132726Z-live-counters`: p95 2.6–3.2 s).
- On a 94,694-record session, a one-pass evidence export took 2.9 s instead of 83.5 s through 474 page reads;
  the output was identical. This is not a whole-product scale qualification.
- The broker/source measurements are on one pre-release Windows build; do not generalize capture overhead or
  capability tier to retail builds. The report is pseudonymized, **not anonymous**: times, counts and workload
  shapes may identify a machine. The detailed export is sensitive. Screen-reader audit is still open.
- Keep user-owned untracked `Zip-GitFiles.ps1` untouched. Before each slice, inspect `git status` and these tables;
  after each coherent slice, run proportional tests, update this file and the plan if needed, commit and push `main`.

## Key reference contracts

`contracts/journal-v1.md`, `store-v1.md`, `segment-v1.md`, `metrics-v1.md`, `entities-v1.md`,
`query-identity-v1.md`; ADR-008, ADR-010, ADR-012, ADR-013, ADR-023–028; the complete historical ledger linked above.
