# InterCat implementation status

Updated: 2026-09-24 · Plan revision: 104 · Branch: `main`

This is the **current resume point**, not a running transcript. Update the backlog and open-work tables in place after
each slice, then add only a short latest-change note. The complete pre-revision-104 chronology, measurements, and old
verification ledger are preserved in [the historical status](history/IMPLEMENTATION-STATUS-through-revision-103.md).
The product contract and milestone gates remain in [the implementation plan](design/INTERCAT-IMPLEMENTATION-PLAN.md).

## Where the product stands

M0 and its IC-010a capture-impact follow-on are complete on the measured development build. M1's journal, profiles,
physical store, commit/recovery, and leases exist, but canonical import reuse, operation/entity checkpointing, and
mechanism breadth remain. M2 has a real broker-driven Explore and saved-session Desktop flow, a navigable real
overview/evidence ladder, live publication, health, interval ranking, minimap and zoomed timeline; the full per-rung
graph/operation view and steady-state feedback budget are open. M3–M5 are not complete. A sharing **report** now exists;
a reopenable redacted **session package** does not.

The ordinary user's path is `icat capture` or Desktop Explore through the broker; `icat import` builds a session from
ETL. `icat session`, `processes`, `metric`, `overview`, `timeline`, `evidence`, `raw`, `retain`, and `export` inspect or
manage it. `icat export --share-redacted` and Desktop's separate “Share redacted report” create allowlisted,
pseudonymized JSON/CSV. Ordinary detailed `intercat-export-v1` retains sensitive names and raw locators and must not
be described as safe to share.

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
| IC-017 Desktop projection | Real overview, channel/evidence ladder, layout scheduling, live follow, interval/zoom/minimap implemented | Per-rung eligible graph/operation/byte composition, persisted overview pyramid, bounded steady-state feedback. |
| IC-018 query identity | Metrics identity frozen; CLI/Desktop export scopes share projection | Full UI query identity, generation-aware numeric cache/cursors and coherent bundle publication. |
| §11.3 sharing | Metadata-only pseudonymized report in JSON/CSV implemented | Reopenable redacted normalized session package, fresh dictionaries/indices and leakage audit; original evidence package preset. |
| M3–M5 release | Open | Multi-machine/workspace, full scale/reliability/accessibility/installer/build matrix and release gates. |

## Latest coherent slice — redacted sharing report and status reset

- `RedactedShareExport` creates a separate `intercat-share-report-v1` contract by serializing **only** explicit fields.
  It omits original session/capture IDs, raw record locators, provider IDs, names, PIDs, addresses, ports, source
  files, free-text context and content. Random relationship tokens are consistent inside one report, not across
  reports; JSON and CSV carry the policy, retained/omitted lists and a not-anonymous warning. Empty CSV scopes carry
  a metadata row marked `row_present=false`. Policy: [sharing-report redaction](design/SHARING-REPORT-REDACTION.md).
- `icat export --share-redacted` and the Desktop's separate share action use that same renderer. Desktop shows a
  pre-save disclosure; both paths stage UTF-8 beside the destination and publish only a complete file.
- Adversarial JSON/CSV tests use sensitive sentinels in context and source fields, check relationship preservation,
  per-report token rotation, empty scopes, and Desktop/headless scope parity. The ordinary detailed export stays
  byte-for-byte unchanged. **This is not the §11.3 redacted normalized session package.**
- The previous 2,040-line chronological status was moved to `docs/history/`; this file is the concise, current ledger.

## Open work, dependency order

1. Complete and test §11.3's **reopenable redacted normalized session package** with a new identity and provenance.
   It must rebuild dictionaries/indices and scan annotations/references; it must contain neither unredacted ETL nor
   any locator that resolves to original payload (I22). Do not simply zip or relabel a detailed export.
2. Make the Desktop's per-rung graph, numeric ranking, timeline, evidence and coverage one eligible generation/scope
   bundle. Eliminate the whole-machine graph at a channel rung. Include operation/byte projections where derivations
   actually support them, and say unavailable otherwise.
3. Persist an overview pyramid and incremental tiles (§10.2/S4); bound query/layout/paint costs and retest the
   missed steady-state latency target on real ETW. Add minimap wheel zoom and keyboard parity.
4. Continue M1's IC-015 operation/topology derivations and IC-016a checkpoint without inventing unsupported
   mechanism facts. Then resume the remaining milestone and retail-build gates from the plan.

## Verification and cautions

- Current tests: **854 passed in Debug and Release**, zero failures (+7 over revision 103). The CLI export help
  advertises the new flag; the Application and Desktop tests cover redaction, relationships and headless scope parity.
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
