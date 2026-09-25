# InterCat implementation status

Updated: 2026-09-25 · Plan revision: 114 · Branch: `main`

This is the **current resume point**, not a running transcript. Update the backlog and open-work tables in place after
each slice, then add only a short latest-change note. The complete pre-revision-104 chronology, measurements, and old
verification ledger are preserved in [the historical status](history/IMPLEMENTATION-STATUS-through-revision-103.md).
The product contract and milestone gates remain in [the implementation plan](design/INTERCAT-IMPLEMENTATION-PLAN.md).

## Where the product stands

M0 and its IC-010a capture-impact follow-on are complete on the measured development build. M1's journal, profiles,
physical store, commit/recovery, and leases exist, but canonical import reuse, operation/entity checkpointing, and
mechanism breadth remain. M2 has a real broker-driven Explore and saved-session Desktop flow, a navigable real
overview/evidence ladder, live publication, health, interval ranking, minimap and zoomed timeline; per-rung
timeline lanes, the operation view and the steady-state feedback budget are open. M3–M5 are not complete. Two of §11.3's three sharing
presets exist: a metadata-only **report** and a reopenable redacted **session package**. The original evidence package
does not. The communication graph is a bounded §6.3 projection with a relationship-first §19.4 layout, qualified on
two real sessions, one sparse and one dense. Processes with no relationship are counted in one parked node, and groups
collapse only under budget pressure. Hubs draw as stars with components apart, executable groups read as file names,
and positions survive descents and live publications. Graph marks now explain their scope and evidence on hover; nodes
can be dragged into pinned positions or pinned from the keyboard, and an explicit re-layout preserves those user constraints.
Below the machine rung the graph draws only the rung's neighbourhood, and one **Rest of the machine** node counts every
other process. Double-clicking a relationship's edge opens its channel. The timeline draws in colour the records E would
list for the rung, over every record in grey. A timeline bucket explains itself on hover, as graph marks do.

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
| IC-017 Desktop projection | Real overview, channel/evidence ladder, layout scheduling, live follow, interval/zoom/minimap with wheel and keyboard, and a bounded §6.3 graph with relationship-first layout, semantic hover, manual pinning/re-layout, quiet folding, minimal group collapse, table-shared selection, anchored carried layout, per-rung neighbourhoods with a context node, §6.7's edge double-click, and a per-rung timeline focus that counts what E reads | Per-rung timeline lanes, focus coverage, operation and byte composition. Persisted overview pyramid and bounded steady-state feedback. |
| IC-018 query identity | Metrics identity frozen; CLI/Desktop export scopes share projection | Full UI query identity, generation-aware numeric cache/cursors and coherent bundle publication. |
| §11.3 sharing | Metadata-only report (`intercat-share-report-v1`) and reopenable redacted session package (`redacted-session-v1`) implemented, CLI and Desktop | Original evidence package preset; packages above 1,000,000 rows (interval-scoped package or streamed pseudonym tables). |
| M3–M5 release | Open | Multi-machine/workspace, full scale/reliability/accessibility/installer/build matrix and release gates. |

## Recent slices

- **Revision 114 — the timeline's remaining §6.7 gestures:**
  - **New gestures:**
    - A double click zooms in by 2 at the pointer.
    - A pinch zooms against the viewport it began from, so a long gesture does not drift.
    - `Shift`+arrow pans one drawn bucket.
    - A horizontal wheel, or `Shift` with the wheel, pans a tenth of the span per notch.
  - **Changed:** `+`/`-` now zoom around the analysis interval rather than the viewport's centre.
  - **`[`/`]`:** at the evidence rung they select the previous or next record, which the timeline marks. Elsewhere they
    make the previous or next drawn bucket holding a record of the rung's focus the analysis interval, skipping empty
    buckets. In a zoomed view with nothing further, they page on.
  - The minimap's keyboard path shares these. The header hint and its tooltip name every gesture.
  - **Tests:** four UI tests covering stepping over empty buckets and back, zoom around the interval, one-bucket and
    sideways-wheel pans, double click, pinch without drift, and record stepping at the evidence rung.
  - **Still open in §6.7's table:** `Ctrl`+click multi-selection, `Alt`+`Right` forward history, `Ctrl`+`F` search, and
    the lane rows.
- **Revision 113 — timeline hover (§6.2):**
  - **Hover:** a timeline bucket under the pointer is outlined and explained, and hover never selects or brushes. A
    press removes the card.
  - **Card contents:** the bucket's half-open interval and record count; the count's basis, unit, domain and
    accounting; the rung's focus count inside it; its rate against the busiest visible bar; what is unmeasured, which
    for a records timeline is only records with no session time; bytes; coverage; its resolution and generation; and
    whether a click would make it the analysis interval.
  - **Hover layer:** one window-level layer, which takes no input, now draws both the graph's and the timeline's cards,
    using a shared painter. At the minimum window a complete card is taller than the timeline pane and would otherwise
    be clipped. `GraphHoverCard` became `HoverCard`.
  - **Tests:** the card contract on a real session's focused rung, and a UI test at 1080×700. It checks that the card
    lies whole inside the window, that hover selects nothing, and that the axis gutter shows no card.
- **Revision 112 — the timeline follows the rung (§3.2, §6.2):**
  - **Focus:** from L1 down, the timeline draws in colour the records E reads from the rung:
    - L1: the records the group's members own.
    - L2: the records the instance owns.
    - L3: the records of the channel's two ends.
    - L5: its evidence scope.
  - **Context:** every record stays behind as grey context on one rate scale. The caption names the focus.
  - **Counting:** `SessionTimelineQuery.Focused` counts the focus in the same pass and on the same columns as the whole
    timeline, with the evidence rung's own row rules and policy. At the whole extent it uses the overview's columns.
  - **Interval table:** each window's focus count stands beside its own count.
  - **Failure:** a focus the generation cannot resolve is named with its reason, and every record is drawn in its hue.
  - **Live refresh:** a new publication shows the previous zoomed detail and focus counts until its own arrive, so a
    focused timeline does not blink every publication.
  - **Fixed:** revision 111's reset of the ranked table's scroll ran after the list's layout. On the sparse import it
    once left an opened `svchost.exe` scrolled mid-list. The offset now returns to the top before layout, and the
    real-session test asserts it.
  - **Tests:**
    - Each focus bucket equals the records the evidence query returns in its interval.
    - The per-rung Desktop flow, unresolvable focus, live carry, and the keyboard path of the scroll reset.
- **Revision 111 — the graph follows the rung (§3.2, §6.3, §6.7):**
  - **Neighbourhoods:**
    - L1 draws the opened group's members and their peers.
    - L2 draws the instance and its peers.
    - L3 and L4 draw the channel's two participants.
    - The evidence rung keeps the graph it was reached from.
    - Every other process is counted in one outline-only **Rest of the machine** node. It is parked below the drawing,
      and its faded edges move nothing.
  - **Edge double-click:** a single-relationship edge descends through its group and source process to its channel.
    If the relationship has several channels, it stops at the process, whose rows list them. An aggregate edge opens
    nothing. The edge's hover card says which of these a double-click will do.
  - **Found on real data and fixed:**
    - The context node held the most observations, so it set the node-size scale. It is now drawn at the aggregate
      floor, and it is last in keyboard order.
    - Its tangled transparent rims are replaced by a dashed stack.
    - The header claimed "and its peers" for a focus that had none.
    - The header called 98 folded processes "drawn". It now says "drawn as 1 node".
    - A new rung's ranked table opened at the scroll offset of the rung it left.
  - **Tests:** neighbourhood projection, per-rung drawing, edge double-click, aggregate-edge refusal, context
    scale/keyboard/wording, focus summaries, and the table's scroll reset. The scroll-reset test fails without its fix.
- **Revision 110 — graph interaction contract (§6.2, §6.3, §19.4), verified:**
  - **Hover semantics:** node and edge cards state the exact applied half-open scope, source basis, metric/unit/domain,
    accounting applicability, observation value, byte availability at the granularity the source actually supports, coverage,
    and the visible size/thickness scale. They do not fabricate an operation interval, byte precision, or transfer direction.
  - **Direct manipulation:** dragging a node pins its stable graph key; `P` pins/unpins the selected node without requiring
    precise pointer input; `L` explicitly re-lays out the graph while retaining pins. Pins carry across rung changes and
    later publications of the same open session. Reopen persistence remains gated on `.icat-workspace` (§26.3), so the UI
    does not claim it yet.
  - **Crowded labels:** label placement searches deterministic outward and diagonal slots before omission; selection/focus
    still receives an in-pane fallback rather than becoming nameless. Hover-card text wraps within a bounded card instead
    of silently truncating the semantic contract.
  - **Plan repair:** §19.4 no longer mandates a spatial index at the hard 200-node/500-edge drawing bound; a measured
    bounded geometry scan is allowed, with indexing required if scale or input-budget measurements justify it. The plan's
    former claim that pins survive reopen was reconciled with §26.3: reopen persistence begins only with workspace-state
    persistence. The semantic-channel table now also limits arrowheads to relations whose derivation actually supports
    direction; paired-TCP display order is not promoted into initiator/sender evidence.
  - **Tests added:** hard pin constraints through re-layout/publication, crowded-hub label fallback, hover-contract wording
    and half-open range formatting, plus keyboard-only pin/re-layout reachability.
  - **Verification on the pinned SDK:** the slice was written partly without an SDK. On .NET 10.0.401 it compiled
    cleanly, and every new test passed.
    - The ledger lacked the new R15 test, so the traceability check failed.
    - A hover-test expectation still used the old scope wording.
    - On a comma-decimal machine, `[0,0, 24,0) s` read ambiguously, so half-open ranges now part their bounds with the
      culture's list separator (`[0,0; 24,0) s`).
    - "Known bytes: 7,24 MB known" became "Bytes: 7,24 MB known".
    - An older R3 defect surfaced in a rendered frame: a synthetic process with no measured bytes read "0,00 MB known"
      and now reads "bytes unknown".
    - A headless drag test now proves that a click only selects and a drag pins exactly at the drop point.
    - On the dense real capture, the wider label search names the hubs. Only the queue hub, ringed by 27 workers, stays
      unlabelled; it is named on hover and selection.
- **Revision 109 — relationship-first layout on a dense real capture (§19.4):**
  - **Why:** `tools/Record-DenseGraphCapture.ps1` recorded a busy machine with `icat record`: nine loopback hubs and
    their clients under 13 executable names, plus a 27-process worker pool, for 621 processes and 56 relationships.
    On it, revision 108's one-full-width-band-per-executable layout drew a tangle. Relationships almost always cross
    executables, so every hub sat in another strip than its clients.
  - **Layout:** `GraphLayout` now parks edgeless nodes as a left-aligned footer and relaxes related nodes by a bounded,
    deterministic Fruchterman-Reingold pass:
    - Each component relaxes on its own forces, so a hub and its peers form a star.
    - Components repel only at short range, and a gravity weaker across the pane's width packs them side by side.
    - Discs are kept apart by §6.3's radii, now shared through `GraphEncoding`.
    - New nodes are seeded beside a placed neighbour.
  - **Stability:** a node drawn before is anchored and moves at most about 0.09 of the pane's height per layout.
    Anchoring eases off only when a layout adds many nodes that are also a large share of it, such as opening a group.
  - **View:** discs shrink uniformly, to no less than half, in a pane shorter than the design height; labels never
    cover a node; aggregates' count labels come before busy processes' names.
  - **Result:** nine separate stars with no crossing edges. `worker.exe` collapsed at the machine rung and opened into
    a 27-leaf star. The old band tests were replaced by relationship, parking and settling contracts.
- **Revision 108 — the graph qualified on real data (§6.3, §6.4):**
  - **Origin:** revision 107 arrived uncompiled. On a real 534-process import with one relationship it drew 197
    isolated circles and device-path labels.
  - **Quiet fold:** processes with no relationship now fold first, into **No relationships** or an opened group's
    **Other members**.
  - **Minimal collapse:** the node budget is met by an exact count and the edge budget by bisection.
  - **Names:** executable groups read as disambiguated file names (`ExecutableNames`), with the path in the inspector.
  - **Selection:** a group row rings its drawn nodes, and E scopes evidence to it.
  - **Stability:** positions carry across publications and rungs.
  - **Tests:** an opt-in real-session UI test (`INTERCAT_REAL_SESSION`) was added, and a flaky redaction test fixed.
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

1. Give each rung its own timeline lanes (§3.2's timeline column): a lane per group, instance, or channel end, within
   §6.2's mark budget. Coverage should be judged on the focus's own mechanisms. Include operation/byte projections
   where derivations actually support them, and state unavailable where they do not. The focus overlay from revision
   112 is the single-lane step toward this.
2. Persist an overview pyramid and incremental tiles (§10.2/S4); bound query/layout/paint costs and retest the
   missed steady-state latency target on real ETW.
3. Continue M1's IC-015 operation/topology derivations and IC-016a checkpoint without inventing unsupported
   mechanism facts. Then resume the remaining milestone and retail-build gates from the plan.
4. §11.3's third preset, the explicitly unredacted original evidence package, and redacted packages above 1,000,000
   rows.
5. Interaction follow-ups with no dependents:
   - Qualify the **Other processes** remainder on real data when a naturally eligible capture exists. It is a budget
     fallback, covered synthetically; the dense capture never needs it.
   - Pins that survive reopening, once §26.3's workspace persistence exists.
   - §6.7's remaining rows: `Ctrl`+click multi-selection as an explicit predicate, forward navigation history
     (`Alt`+`Right`), and `Ctrl`+`F` search.

## Verification and cautions

- Last executed clean baseline (revision 114): **921 passed, 1 skipped, in Debug and Release**, zero failures.
  - Revision 108 gained 22 over revision 106's 875: 17 in Application and 6 in Desktop.
  - Revision 109 replaced the band test with relationship, parking and settling contracts (+2).
  - Revision 110 added hover, pin, label-slot, half-open, keyboard, drag and R3 tests.
  - Revision 111 added one Application, three Desktop and one UI test (+5).
  - Revision 112 added one Application and three Desktop tests (+4).
  - Revision 113 added one Desktop and one UI hover test (+2).
  - Revision 114 added four UI gesture tests (+4).
  - The skip is the opt-in real-session UI test. Run it with `INTERCAT_REAL_SESSION=<session dir>`; without the
    variable it reports skipped, never passed.
- Revision 112 real check: both sessions passed in Release, and the frames were inspected.
  - Dense session: opening `worker.exe` colours 2,866 of 17,350 records, all inside the workers' burst early in the
    session, counted 35 ms after the group opened.
  - Sparse import: `svchost.exe` owns 98 of 852 records, counted in 8 ms.
- Revision 111 real check: both sessions passed the opt-in test in Release, and the frames were inspected.
  - Dense session: opening `worker.exe` now draws 29 nodes and 27 edges, a 27-worker star around its `queue.exe` hub.
    The header reads "worker.exe and its peers: 28 processes drawn · 593 more in Rest of the machine". The context node
    holds 593 processes and 29 relationships, all among themselves. Group open plus layout takes 57 ms.
  - Sparse import: opening `svchost.exe` reads "svchost.exe: 98 processes drawn as 1 node · 436 more in Rest of the
    machine". Group open plus layout takes 56 ms.
- Revision 109 dense real check: `tools/Record-DenseGraphCapture.ps1` recorded a local-only session of 621 processes,
  56 relationships and 146 groups. The opt-in test passed in Release, and its frames were inspected at 1456×939 and
  1080×700:
  - The machine rung draws 40 nodes and 31 edges, with `worker.exe` (27 processes) collapsed and
    **No relationships · 556**.
  - Opening `worker.exe` draws 66 nodes and 56 edges.
  - Headless cold timings: overview 443 ms, graph projection 34 ms, layout 33 ms, group open plus layout 53 ms.
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
