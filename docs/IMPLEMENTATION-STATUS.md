# InterCat implementation status

Updated: 2026-09-26 · Plan revision: 149 · Branch: `main`

This is the **current resume point**, not a running transcript. Update the backlog and open-work tables in place after
each slice, then add only a short latest-change note. The complete pre-revision-104 chronology, measurements, and old
verification ledger are preserved in [the historical status](history/IMPLEMENTATION-STATUS-through-revision-103.md).
The product contract and milestone gates remain in [the implementation plan](design/INTERCAT-IMPLEMENTATION-PLAN.md).

## Where the product stands

M0 and its IC-010a capture-impact follow-on are complete on the measured development build. M1's journal, profiles,
physical store, commit/recovery, and leases exist, but canonical import reuse, operation/entity checkpointing, and
mechanism breadth remain. M2 has a real broker-driven Explore and saved-session Desktop flow, a navigable real
overview/evidence ladder, live publication, health, interval ranking, minimap and zoomed timeline, and interactive
L0 mechanism lanes, exact L1 process-owner lanes, L2 source-direction rows and L3 channel-end lanes banded by
direction. While recording, a labelled **live edge** previews records not yet published, which brings real-ETW
event-to-visible to p95 0.72–0.77 s and meets §12's steady-state budget. Exact results still arrive about 2.6 s after
an event (p95). The default 10-minute Explore capture runs to its bound on real ETW and saves whole. It meets every
§12 budget to its end: at 105,944 records, projection p95 81 ms, event-to-visible p95 0.85 s, exact p95 2.7 s, and
118 MiB of viewer memory at the end, 22.5 MiB of it cached segments. A crashed viewer's capture is stopped and
finalized by its lease without a leaked trace, and the next launch offers to finish its session from the evidence the
broker kept, including what the broker recorded after the crash. Projection still grows with the session, so large
sessions wait on S4 and incremental derivation.
L4 lanes wait on derived operations, and the operation view is open.
M3–M5 are not complete. Two of §11.3's three sharing
presets exist: a metadata-only **report** and a reopenable redacted **session package**. The original evidence package
does not. The communication graph is a bounded §6.3 projection with a relationship-first §19.4 layout, qualified on
two real sessions, one sparse and one dense. Processes with no relationship are counted in one parked node, and groups
collapse only under budget pressure. Hubs draw as stars with components apart, executable groups read as file names,
and positions survive descents and live publications. A bounded metadata search now finds groups, process instances and
channels by name, PID or endpoint; endpoint strings can match but are omitted from result snippets. Graph marks now explain their scope and evidence on hover; nodes
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
| IC-014 broker | Authenticated pipe, protected root, durable ownership/recovery, live evidence and live preview counts, ordinary CLI/Desktop client implemented; parent-owner parser blocker repaired and CLI/Desktop Explore exercised on the affected host; a crashed client's capture qualified to stop at lease expiry, finalized and leak-free, and its session finished by the next launch from the follow's ticket (`live-follow-v1`, qualified on real ETW); a connection bounded by request rate rather than a total, so an owner keeps it for a 24-hour capture | Installer pre-creation, retail-build matrix and remaining broker release qualification. |
| IC-015 metrics/entities | Source-observation metrics, process/executable grouping, TCP/UDP relations, peer/channel lower bounds | Canonical transfer owner, operations/topology, IPv6/non-TCP relations, full coverage epoch publication. |
| IC-015a segments | Complete observation/source-field tables; since minor 1, every byte a reader interprets has a checksum of its own | Column-granular reads (open work item 1). Compression and derived scale structures are later work. |
| IC-016 store | Complete M1 commit/recovery/lease/explicit-retention scope; a lease confirms hashed dependencies from one directory listing; queries share verified immutable segment readers, safe across threads, within 256 MiB of payload per store, pruned to what the selected generation names; a viewer holds one store per session, a capture's writer included, and keeps readers only for the session it shows; a writer removes superseded manifests as it publishes, and a reader waits out that removal | Rolling retention policy and cross-process pin quota. |
| IC-016a checkpoint | Not started | Live entity/endpoint state and open-operation censoring at eviction boundary. |
| IC-017 Desktop projection | Real overview, channel/evidence ladder, bounded metadata search, layout scheduling, live follow, interval/zoom/minimap with wheel and keyboard, exact L0 mechanism lanes, L1 process-owner lanes, L2 source-direction rows and L3 channel-end lanes banded by direction, with shared scale, own coverage, hover/time selection, persistent table/step focus and keyboard/wheel scrolling, exact bounded query data carried through live publications, the visible range as the default scope with a scope lock, and a bounded §6.3 graph with relationship-first layout, semantic hover, manual pinning/re-layout, quiet folding, minimal group collapse, table-shared selection, anchored carried layout, per-rung neighbourhoods with a context node, §6.7's edge double-click and back/forward history that restores each rung's interval, a per-rung timeline focus that counts what E reads, a labelled live edge that previews unpublished records within §12's steady-state budget (P26 asserted), a designed waiting state before a capture's first publication, a launch-time offer to finish a session a crashed viewer left, and the saved sessions listed while none is open | L4 operation lanes and byte composition once IC-015 derives operations. The persisted overview pyramid (S4) and exact live cadence at 1M rows and beyond. A real screen-reader pass on Windows (the automation tree is audited headlessly since revision 131), and pin/collapse/search for lanes as scale requires. |
| IC-018 query identity | Metrics identity frozen; CLI/Desktop export scopes share projection | Full UI query identity, generation-aware numeric cache/cursors and coherent bundle publication. |
| §11.3 sharing | Metadata-only report (`intercat-share-report-v1`) and reopenable redacted session package (`redacted-session-v1`) implemented, CLI and Desktop | Original evidence package preset; packages above 1,000,000 rows (interval-scoped package or streamed pseudonym tables). |
| M3–M5 release | Open | Multi-machine/workspace, full scale/reliability/accessibility/installer/build matrix and release gates. |

## Recent slices

- **Revision 149 — a segment checks every byte a reader interprets (segment-v1 minor 1, §20.1):**
  - **Why:** column-granular reads (open work item 1) load a column when it is first read, not the whole file. At
    minor 0 the column and time-block directories and the variable chunk were covered only by the whole-file trailer,
    so a reader could not check them without hashing the file.
  - **Format** ([`segment-v1`](../contracts/segment-v1.md) §3, §9, §11): minor 1 fills two words that minor 0
    reserved and wrote as zero. The header's word at 124 holds the CRC-32C over both directories. A chunk-encoded
    column's entry word at 44 holds the CRC-32C over the variable chunk. The trailer is still written. Dictionaries
    stay at minor 0, because each of their parts already had a checksum.
  - **Reader:** checks the directories' checksum at open, before it interprets an entry, and when it lists a segment's
    dictionaries. It checks the chunk's checksum on the first read of a chunk-encoded column. A minor-0 segment's zero
    words are never taken for checksums; its trailer covers those bytes, as before.
  - **Checked across builds:** revision 148's build and this one read a sparse ETL session imported by either build
    with identical row digests (852 observations, 3,104 fields). Both also read a chunk-encoded minor-1 segment
    identically. Existing sessions keep their minor-0 segments until a later derivation or compaction replaces them.
    The same rows now make different segment bytes, so a refactor proof across this revision compares rows, not
    file digests.
  - **Tests:** +5 in `SegmentV1Tests`:
    - the words hold the checksums;
    - a damaged directory is refused by its checksum even with the trailer rewritten, both at open and when listing
      dictionaries (two cases: a time block's reserved word, which nothing else reads, and a column's value offset);
    - a damaged chunk is refused on its column's first read, while other columns are still served;
    - a minor-0 file opens, and its trailer finds damage in the chunk.

    Five reader mutations each failed a test: no directory check at open; none when listing dictionaries; no chunk
    check; and either check applied to minor 0.
- **Revision 148 — a million rows fit the reader cache (§20.1, §12, S2):**
  - **Found:** the 1M-row benchmark session holds 169 MiB of segment payload. The 64 MiB cache kept one segment of
    four, so every warm projection, timeline detail and focused count re-read and re-hashed the rest. A sampled trace
    put that at the largest share of warm time.
  - **Measured before choosing**, warm medians by budget on that session: projection 315 → 127 ms, timeline detail
    158 → 29 ms, group focus 329 → 145 ms at 256 MiB. Without the synthetic rows a run-once method keeps alive, the
    product's own heap is the cached readers plus small derivations.
  - **Changed:** the admission budget is 256 MiB, half of §12's 512 MiB analysis budget. Since revision 142 a viewer
    keeps readers for the session it shows only, and a capture holds them once.
  - **Window level** (`bench/results/interaction-latency-20260926T173620Z`, 1M rows): group level change 681 → 350
    ms, process 315 → 95 ms, channel 414 → 139 ms, brush to ranking 337 → 182 ms, open 1,374 → 1,204 ms. Canvas pan,
    zoom and hover are unchanged.
  - **Still open:** the cliff returns near 1.5M rows. Column-granular reads are the structural fix (open work item 1).
  - **Tests:** unchanged (a tunable); every cache test runs against the constant or its own budget.
- **Revision 147 — the rest of the chrome, legible in high contrast (§6.1, §6.6):**
  - **Gap:** revision 140 restated only buttons and text boxes. Menus, tool tips, scroll bars and list selection kept
    the control theme's faint look in high contrast, and revision 146's Theme menu is how a user now reaches it.
  - **Menus:** the elevated face inside a divider edge. The item under the pointer or pressed is the measured action
    pair, the canvas on the accent. A line that cannot be chosen, such as where the settings are kept, keeps the muted
    ink instead of a faint grey.
  - **Tool tips:** body ink on the elevated face inside a divider edge.
  - **Scroll bars:** a divider-toned thumb on the bare ground, the accent under the pointer.
  - **List selection:** a highlighted row lies on the elevated face, where every ink a row carries is measured, muted
    included. A fill cannot both stand 3:1 from the ground and keep muted ink at 4.5:1, so a selected row also draws a
    two-pixel accent edge, from a style no resource key reaches. Every row keeps a transparent edge of the same width,
    so selecting one moves nothing.
  - **Tests:** one window test in both high-contrast modes (+1). It checks the selected row's accent edge and elevated
    fill, that the row's text does not move, the menu, tool tip and scroll-bar tokens, and that an ordinary mode drops
    the edge. It failed with the edge style removed; the existing test already checks every new key is handed back.
- **Revision 146 — the application's own settings, starting with the theme (§26.3, §26.2, §6.1):**
  - **Gap:** §26.3 names three settings scopes and none existed, so the theme could only follow the operating system.
    The open work listed storing the mode "once §26.3's per-user configuration exists".
  - **File** ([`app-settings-v1`](../contracts/app-settings-v1.md)): `%APPDATA%\InterCat\settings.json`, versioned and
    hand-editable, with `themeMode` its first key.
    - A key this build does not know is kept and reported; a value it cannot read is reported and left in place.
    - A choice replaces only its own key, written through a staged copy.
    - An unreadable file is kept aside before a choice is written over it.
    - A file another version wrote is neither applied nor overwritten.
  - **Window:** the header's **Theme** button opens a menu. It offers following the system, dark, light, and high
    contrast dark or light, with a check on the current choice, states where the settings are kept, and lists whatever
    reading the file reported. A choice applies at once. Only the application reads the user's file; a test names its
    own.
  - **Tests:** four store tests and one window test (+5). The window test clicks a choice as the menu delivers it,
    reads the file back, returns to the system's mode, and keeps the header title at least 100 px wide at 1080×700.
- **Revision 145 — the saved sessions, one gesture away (§3.1 step 1):**
  - **Gap:** every capture saves a session in `%LOCALAPPDATA%\InterCat\Sessions`, but reopening one meant knowing
    that path. *Open saved session*'s picker started wherever the platform chose, and the plan named no way back to a
    capture.
  - **List:** while no session is open, the rail lists the eight newest where the ranked table will be. Each row gives
    when it was saved, its records, its size, and *not finished saving* when a follow ticket is beside it. A directory
    that is not a readable session, or has a torn pointer, is left out rather than guessed at.
  - **Cost:** listing reads each session's current manifest and its segments' 128-byte headers, never hashing a
    session; opening one still verifies it in full.
  - **Keyboard:** Enter opens the row the keyboard is on. A list item takes focus from Tab without being selected, and
    the window's Enter otherwise meant a descent. A double click opens a row too.
  - **Around it:** a running capture hides the list with the other actions that wait for it to end, a finished capture
    re-lists it, and the picker now starts in the sessions folder. `Moments.When` now serves both the recent list and
    the unfinished-capture card.
  - **Tests:** one presenter and one window test (+2). The window test found the Enter path, and that a list item
    rather than the list takes focus.
- **Scale baseline** (`bench/results/interaction-latency-20260926T165252Z`, measured at revision 144): at 1M rows, open
  1,374 ms (was 1,550), group level change 681 ms (799), brush to ranking 337 ms (342), every window in budget. A
  sampled trace of the warm paths shows where the rest goes (open work item 1).
- **Revision 144 — superseded manifests go as a session publishes (store-v1 §9, §20.1, S2):**
  - **Found:** every generation's manifest lists every dependency it names, and `store-v1` kept each superseded one
    as an orphan until asked. A live session therefore held manifest bytes growing with the square of its length: 297
    manifests and 16 MB after the default 10-minute capture, and far more at the broker's 1,024-chunk cap.
  - **Which ones:** a writer that publishes, by commit or retention, follows the chain of previous generations back from
    the current one. It removes the manifests on that chain that no pointer names. A manifest an interrupted
    publication left was never current and no chain reaches it, so it stays an orphan for recovery to judge. Only
    manifests go: no dependency, pointer, lock or staging file.
  - **Found on the way:** removing by generation number, the first version, deleted exactly such an interrupted
    publication's manifest, and an existing I15 test caught it.
  - **When:** only while the writer can take the evidence guard exclusively, which no reader anywhere holds.
    Otherwise a later publication removes the backlog at once. Removal never fails a publication.
  - **Readers wait:** a reader meeting that exclusive hold used to fail its lease, which would have ended a live capture
    as interrupted. It now waits up to 1 s, and does so before taking its store's lock, so the store's other threads
    are not held up.
  - **Real ETW** (`bench/results/first-feedback-20260926T163447Z-10min-bounded`): the bounded 10-minute capture met
    every budget. It ended with **1 manifest (68 KB)** in the session and **2 (121 KB)** in the broker's evidence, and
    37 MB in all. Projection p95 was 67 ms, event-to-visible p95 815 ms and exact p95 2.71 s. The follower read the
    evidence throughout while the broker removed manifests, and nothing failed. The crashed-viewer scenario passed again
    on the final build.
  - **Measurement caution:** follow p95 read 147 ms against revision 142's 87 ms. That came from minutes 1–2 (187 and
    181 ms), while light file searches ran beside the capture. Minutes 3–10 read 71–86 ms, as revision 142's 68–94 ms.
  - **Tests:** a live session keeping only its pointers' manifests, a reader in another process deferring removal, and
    a reader waiting out a removal, with its bound (+3). Two fixtures rewound evidence to `manifest-0000000001.json`,
    which removal can take; one failed in a Debug run. They now rewind to the retained previous generation. The waiting
    test fails with the wait set to zero.
- **Revision 143 — the next launch finishes a crashed viewer's session (§3.1 step 6, ADR-027):**
  - **Gap:** §3.1 promises that a viewer that crashes loses no evidence and that the next launch offers to finish its
    session. The broker kept the evidence, but nothing recorded where it was, so the session stayed as far as the viewer
    had followed it.
  - **Ticket** ([`live-follow-v1`](../contracts/live-follow-v1.md)): while it follows, the runner holds
    `<session>.follow.json` beside the session. It names the evidence and the owner lease's expiry, and each renewal
    rewrites the expiry. A held ticket is never taken for an interrupted one. The runner removes it once every chunk the
    closed capture published is in the session.
  - **What a launch finds**, reading only. A capture has *settled* 60 s after its lease expiry: the broker stops a
    capture within seconds of the lapse and finalizes it within a few more.
    - **Finishable:** finalized, with chunks missing.
    - **Still stopping:** unfinalized and unsettled; the card looks again every 5 s.
    - **Ended unfinalized:** settled without finality, as when the broker or the machine stopped first.
    - **Evidence gone**, and **complete**, whose ticket is removed without a word.
  - **Finish:** takes the ticket, so two launches cannot finish one capture at once. The ordinary follower then derives
    the rest and refuses other evidence. Progress is reported every 16 chunks. The ticket goes once the session holds
    finality, or every chunk of a capture that settled unfinalized. The session then opens through the finish's own
    store, so nothing is hashed twice.
  - **Window:** an **Unfinished capture** card sits above the Explore card and never takes first run's focus. It hides
    while a capture runs. **Forget** removes only the ticket. While a finish runs, its button stops it, keeping what was
    derived. Only the application looks in the user's session folder; tests and headless windows never do.
  - **Found by looking:** at 1080×700 the first layout clipped **Open saved session** at the window's edge. The card's
    actions now share a row, and the window test asserts every rail action lies inside the rail.
  - **Real ETW** (`bench/results/crashed-viewer-20260926T162109Z`, a new `crashed-viewer` scenario): a Desktop capture
    runner was terminated after following 6 chunks. Its ticket held one renewal. The broker stopped and finalized the
    capture 29.7 s later. The finish derived all 22 chunks, 3,393 records, in 1.6 s, and they re-derive identically. The
    16 chunks after the crash were recorded during the lease window, and only the finish recovers them.
  - **Tests:** 17 journal (5 facts, 12 decision cases), 8 wording and 2 window tests (+27). Mutations fail them: a
    ticket offered for a session it is not beside, and an unsettled capture read as ended.
- **Revision 142 — verified segment readers reused across queries and generations (§20.1, §12; S2 support, not S4):**
  - **Found:** `SessionStore` already memoized dependency hashes, but each projection still reopened every immutable
    segment, reread its bytes and dictionaries, and reran their integrity checks.
  - **Cache:** each open store admits at most 64 MiB of published segment and dictionary payload of verified readers. A
    hit needs the manifest to name the exact segment and every exact dictionary, so a carried segment survives live
    generations without being reopened. The figure bounds payload, not CLR memory; §12 still owns the 512 MiB
    analysis/cache budget.
  - **Retention:** selecting a generation prunes readers it no longer reaches, so released evidence is not kept resident.
    A query already holding a reader keeps it. An older lease finishing its open after a prune is not admitted. Compaction
    and journal re-derivation stay uncached, so they do not evict the interactive working set.
  - **Consumers:** overview, timeline, interval, channel, evidence and raw queries, metrics, `icat session` and redacted
    package verification. The last two took `Current` and opened files without a lease; both now lease the generation.
  - **Written without an SDK:** the slice arrived uncompiled. It built cleanly on the pinned SDK and its four tests passed.
    Review found four defects the tests could not see:
    - **Shared readers were not thread-safe.** A reader checks each column's checksum on first read, and it recorded
      that in a `HashSet` *before* checking. Each query used to own its readers; now the runner's projection and the
      window's queries share them. Concurrent marks could corrupt the set, and a second thread could read a column the
      first was still checking. With a damaged column, 278 of 320 concurrent reads were served its bytes. A lock-free
      flag now marks a column only after it passes; a lost race repeats the check and never skips it.
    - **Kept sessions kept their readers.** The Desktop keeps four sessions' stores, so returning to one hashes nothing
      again, and each could hold 64 MiB of readers: 256 MiB of §12's 512 after viewing four large sessions. Only the
      session opened last now keeps readers; the others keep only what they verified. `SharedSessionStores` became a
      facade over a testable `SessionStoreRegistry`.
    - **A capture was cached twice.** The runner derives into a writer store while the window read the same session
      through a reader store, so both cached the working set and both hashed every new file. The writer store is now
      the session's shared store.
    - **LRU fails the scans it serves.** Until S4 every projection and query reads every segment in the same order.
      Once a session's payload passed 64 MiB, a recency policy would evict each reader just before the next scan read
      it: no hits, full churn. The cache now admits what fits and bypasses the rest; pruning frees room.
  - **Measured on the saved 10-minute session** (Release, medians):

    | Measure | Uncached | Cached |
    |---|---|---|
    | Open every segment | 16.9 ms, 16.4 MiB allocated | 0.04 ms, none |
    | Whole projection, no derivation cached | 106 ms, 34.9 MiB | 56 ms, 18.5 MiB |

    The overview and every derivation are digest-identical either way. The 16 MiB now stays resident instead.
  - **Real ETW** (`bench/results/first-feedback-20260926T153533Z-10min-bounded`): the bounded 10-minute capture met every
    budget with 44% more records than revision 129's run.

    | Measure | Revision 129 | Revision 142 |
    |---|---|---|
    | Records | 73,660 | 105,944 |
    | Projection p50 / p95 | 53 / 85 ms | 44 / 81 ms |
    | Event-to-visible p95 | 849 ms | 846 ms |
    | Event-to-exact p95 | 2.69 s | 2.69 s |
    | Viewer private memory at the end | 76 MiB | 118 MiB |

    The session held 22.5 MiB of segment payload, which the cache now keeps resident; the rest of the growth is the
    larger session. The run predates the admission change, which acts only above 64 MiB.
  - **Tests:** the four cache tests, an I15 concurrency test, concurrent opens, a scan larger than the cache, the
    registry, and the capture's store as the shared one (+9). Each new test failed against its mutation: the I15 test in
    every run against the mark-first order, the scan test against eviction, the registry and capture tests without the
    release, and the capture test without the adoption.
- **Revision 141 — a repaint allocates nothing of its own (R11, §19.4):**
  - **Measured first:** one repaint of each pane against an empty drawing context, on every rung of a real session.
    Before: the timeline 58–125 KB, the graph 13–15 KB, the minimap 11 KB, and the hover layer 44–51 KB with a card
    shown. After: none of them allocates anything of its own.
  - **The drawing layer's costs:**
    - a formatted text allocates about 2 KB per draw, while a cached text layout draws for free; every pane's labels
      now use one cache of layouts;
    - a dashed pen allocates a dash effect per stroke, on the render thread too; the graph's dashes are now cached
      geometry under a solid pen, and they look the same.
  - **Our own costs:**
    - removed: a new brush per bar, pens and translucent brushes per frame, LINQ over buckets and edges, labels
      formatted per frame, and the view model's selection and pin sets rebuilt on every read;
    - two subtler ones: a lambda's captured locals are allocated where its method begins, even when a memo returns
      first, and unoptimised code builds a span of constants from a field handle on every call.
  - **Checked:** the test failed against a per-frame pen (1,200 B), a per-frame brush (592 B), a card memo that
    always missed (1,472 B) and a per-frame dictionary (184 B). A LINQ maximum over a list allocates nothing on .NET 10,
    so an allocation test does not see it. The graph's dashes and rings were compared with the layer's own dashing
    in rendered frames.
  - **Tests:** one UI test (+1). R11 moves to covered for the paint loops; admission and decode loops keep IC-019's
    Windows measurements.
- **Revision 140 — a verified high-contrast set, dark and light, following the operating system (§6.1, §6.6, §26.2):**
  - **Modes:** the platform's high-contrast preference selects a high-contrast set, dark or light as its scheme
    reports. Tests pin dark as before.
  - **Tokens (theme 1.2.0):** each high-contrast form has its own surfaces, families and status tokens, measured to
    7:1 for ink and 4.5:1 for fills. The families were searched within a window around each role colour, and every
    pair of them keeps apart (at least 20.8 CIE76, where dark and light reach 17.1). The report now enforces 15 for
    any pair in every mode, since the legend sets all families side by side.
  - **Edges:**
    - A divider token of its own: the elevated tone in dark and light, as before, and a visible line of at least 3:1
      in high contrast.
    - Cards take it as an edge, giving up a pixel of padding, so the ordinary modes move nothing.
    - In high contrast, the control theme's button and text-box keys are restated from the tokens: divider edges, the
      accent under the pointer, the measured action pair when pressed, and a divider-toned label when disabled. An
      ordinary mode hands those keys back.
  - **Checked:** each test failed against a mutation: a contrast-blind platform mapping, a 6.2:1 ink, a 1.54:1
    divider, no card edge, the old divider token, and no restated chrome, which left a button's edge the theme's
    faint #303030.
  - **Tests:** one contract test and two UI tests (+3). The theme-mode and status-token tests now run in all four modes.
- **Revision 139 — conditions and actions have tokens of their own (§6.6, R14, P24):**
  - **Found:** the coverage hatch and the words of a warning were RPC's amber, and the summary was amber even when
    coverage was complete. The live dot was ALPC's mint, and a paused one RPC's amber. The evidence-quality key
    coloured *direct* in ALPC mint behind TCP's glyph and *candidate* in RPC amber behind UDP's.
  - **Primary action (found):** the unused primary style put white text on the TCP fill, measuring 2.79:1 in dark and
    4.18:1 in light.
  - **Legend (found):** "outline = unmeasured" keyed an encoding nothing draws. The only outlined marks are L3
    records with no data direction.
  - **Tokens:** theme 1.1.0 adds a caution ink and an action fill (rest, pointer-over, pressed) with its ink, per
    mode. The report measures them: the action ink clears at least 5.9:1 on every fill state (light pointer-over is
    the lowest), and caution sits at least 39 CIE76 from every family's fill and ink.
  - **Drawn:**
    - The hatch and the warning words take caution. The coverage summary does only while coverage is limited.
    - The live dot is the accent while the view follows, and caution while it is held, paused or unavailable.
    - Start and Stop are primary, and keep their own pointer-over and pressed fills.
    - The key draws each strength with the graph's own edge routine in body ink, and the legend keys the hatch with
      a swatch drawn by the hatch routine.
  - **Checked:** each test was run against the old behaviour and failed:
    - hatch in RPC ink, a summary always caution, the old dot colours;
    - theme's grey on hover, the old palette values;
    - a correlated dash drawn dotted.
  - **Tests:** two contract tests and three UI tests (+5); the pixel reader is shared with the theme-mode test.
- **Revision 138 — the Desktop follows the operating system's light or dark setting (§6.1, §6.6, §26.2):**
  - **Runtime mode:** the app applies the platform's light or dark variant at start and again whenever the platform
    reports a change. Tests pin dark in code, not through an environment variable, which §26.3 would count as a hidden
    fourth settings scope.
  - **Canvases:** the graph, timeline, minimap and hover layer built their brushes once from dark tokens. Each now
    keeps one brush set per mode, built once and reused every frame (R11), and draws with the current mode's. The
    graph's cached label text is keyed by mode too, because it carries its brush.
  - **Legend (found):** it computed each family's fill and ink and bound neither, so the chip glyph and the name were
    drawn in body text and the legend keyed no hue. The glyph now takes the family's fill and the name its ink (§6.6).
  - **Checked:** a UI test switches to light and back. Each time it samples the graph pane's ground and a node's centre
    from the rendered frame, and reads the legend's colours. With the graph pinned to dark, the node reads #152b3d
    where light's #e8edf3 is due.
  - **Still open:** a verified high-contrast token set, the mode as a stored application setting (§26.3), and a
    coverage/warning token of its own: the hatch and warning text borrow the RPC family's amber ink.
  - **Tests:** one UI test (+1).
- **Revision 137 — P17, P21 and P23 asserted; the theme gap stated (§13.5, §6.1):**
  - **Ledger:** P21 and P23 were uncovered "until IC-017", which is well under way, and P17 "until M5", although
    redacted packages shipped in revision 106. Each now names tests:
    - P21: a brush superseded mid-count never applies, and the ranking, relationship table, graph and export all
      answer the one scope on screen; a closed workspace applies nothing counted for it. With the supersession check
      removed, the obsolete count overwrites the newer one ("40" for "20").
    - P23: while a count is in flight the previous answer stays on screen, marked pending, and a descent happens at
      once.
    - P17: a redacted package carries none of its source's evidence: no journal, segment, dictionary, index or plan
      of it, by digest or by content.
  - **Theme gap:** §6.1 asks for dark, light and high-contrast token sets with no view hard-coding a colour. Dark and
    light exist and are verified, but the Desktop always runs dark, and no high-contrast set exists. §26.2 named no
    default, so it now says to follow the operating system. The work is listed under open work.
  - **Tests:** four (+4), all in the ledger.
- **Revision 136 — a live capture keeps the scope's counts on screen (§6.4, R7):**
  - **Found:** each publication is a new workspace, which counts its scope afresh. Until it had, a brushed or (since
    revision 134) zoomed ranking, graph and tables blinked back to whole-session numbers under "Ranking within …",
    once per publication, for the length of the count.
  - **Fixed:** the previous publication's counts stand in, marked "… the previous publication's counts until then",
    and this generation's own replace them, never merged, as the timeline's carried detail does. A zoomed view's range
    is carried too, so the next publication starts counting it before the view is bound.
  - **Never claimed:** a stand-in leaves the export's interval empty, and an export asked for meanwhile waits for this
    generation's own counts. It refuses rather than wait on a count that no longer runs.
  - **Checked:** without the carry, the new workspace is not ranked within the interval at all.
  - **Test harness:** revision 132's forward test failed once in Release. A plain xUnit test has no dispatcher, so a
    count's continuation ran on a pool thread beside the navigation it followed and overwrote its projection. The app
    never does this, because the UI dispatcher serializes both. `SingleThreadedContext` now runs such real-session tests
    on one thread with a message loop, as the dispatcher would.
  - **Also:** the channel browser's tooltip says its records follow the ranking's time scope, a brush or else the
    zoomed range, not only a brush.
  - **Tests:** one UI test for a brush, the export and a zoomed view across two publications (+1).
- **Revision 135 — the minimum window during a capture (§6.8, §3.2):** found by looking at the evidence rung while
  recording at 1080×700.
  - **Card:** it kept its two idle actions, disabled, and its introduction, so the ranked list had room for about one
    record. While a capture starts, records or finishes, the card now holds only its state, stop and pause. The list
    shows three records at that size.
  - **Chips:** a chip showed only its value, so a channel's filter and the evidence scope of the same channel read as
    one filter shown twice. Each chip now names what it narrows, as a crumb does: "Group:", "Process:", "Channel:",
    "Records of:".
  - **Wrapping:** the chips could not wrap, and the fourth, longer now, ended at 1,239 px of a 1,080 px window. They
    wrap within the bar.
  - **Words (R5):** a channel's ranked row read "Tcp · paired endpoints", the enumeration's own name, where the
    legend, lanes and tables say TCP. It now uses the one display mapping, and a known direction reads as a word.
  - **Cut text:** a ranked row's name and detail, a crumb, and a filter chip each show their whole text in a tooltip.
  - **Tests:**
    - Revision 134's legibility theory now also checks the card and every chip at both sizes. With the old chip
      panel it fails at "1239 of 1080 px".
    - An Application test for the channel row's words, in the ledger under R5.
    - "I22: a cancelled package leaves neither the package nor its stage behind" looked for any stage in the shared
      temporary folder, so an interrupted run of another packaging test failed it. It now looks for its own.
- **Revision 134 — the visible range is the default scope, with a scope lock (§6.4):**
  - **Gap:** §6.4 makes the viewport the graph and ranking scope when nothing is brushed, with a scope lock and the
    effective range always shown. Revision 98 left all three open, and this list had dropped them.
  - **Scope:** with nothing brushed, the timeline's settled viewport is the scope of a published session. Every rung,
    the graph, the tables, the inspector and `E` count only what is drawn. A brush wins however the view moves;
    clearing it hands the scope back to the view, and fitting returns to the whole session.
  - **Stated:** the rail reads "Ranking within the visible …" while it counts and "Ranked within the visible …" after.
    It now shows while the first count loads, where before it appeared only once a count had applied. The
    inspector's time scope reads "Visible …".
  - **Lock:** **Keep this range** makes the visible range the analysis interval, drawn on the axis and cleared like
    any brush, so zooming and panning to look around leave the counts where they are.
  - **Kept apart:** a rung's own time never takes the view's range, so a descent does not freeze a zoom into the
    ladder. `E` reads the scope on screen, so it lists exactly the records behind the counts (I5).
  - **Found by looking:** rendered at the minimum window, three things read badly:
    - Revision 132's Forward button read "Forward to Process: PID 200 (Alt+Ri", and the header's title shrank to
      "Sessi…". Its face is now "Forward (Alt+Right)"; its tooltip and accessible name say which rung.
    - Every ranked row's name had 55 px at every window size, because its coverage words shared the count's
      column in the 250 px rail: "PID 200" read "PID …". Coverage now follows the detail on the line beneath.
    - "Retry exploring (Ctrl+R)" was cut at the rail's edge. The capture card's buttons are one size smaller.
  - **Tests:**
    - One Desktop test: visible scope, brush precedence, fit, `E` and the lock.
    - One UI test: a settled zoom re-ranks, and the button keeps the range while the view pans.
    - One layout theory at both supported sizes: a row's name and the header's title keep their room. With the old
      layout it fails at 55 px.
- **Revision 133 — hover answers for what is under the pointer now (R13, P22, §6.2):**
  - **Found:** each pane re-read its hover only when the pointer moved. The timeline kept the instant it hovered and
    the graph the node. So a keyboard zoom or pan, a resize, a lane scroll, a moved pane or a newer generation under a
    resting pointer left the card describing what used to be there. While recording, the live edge re-scales the plot
    at 4 Hz, so the timeline's card went stale almost at once.
  - **Fixed:**
    - The timeline keeps where the pointer rests, relative to the window, and works out what it hovers on every read.
    - The graph re-answers its hover on every layout, pin, resize and generation change.
    - The graph drew no background, so Avalonia only saw the pointer over ink. That dropped the hover when a node
      moved away, but not when another node arrived under the pointer. It also meant a press on empty graph space
      never reached the pane, so the pane never took the keyboard focus its arrow keys, P and L need. The whole pane
      now answers the pointer.
  - **Traceability:**
    - The ledger filed 16 tests under R13 (hit testing through a data-space index), and none tested hit testing. 13
      were §3.2 ladder tests and 3 graph or workspace tests; they now carry §3.2, §6.3, §6.8 and §19.4 names.
    - R13 now names a new test. The unpainted gap after a bar is still that bucket's time, and a click there selects the
      bucket's exact interval.
    - P22 moves from uncovered to two tests.
    - R11's reason, "paint loops do not exist before IC-017", was stale: both drawn panes allocate and use LINQ every
      frame.
  - **Checked:** with the old views both P22 tests and the focus test fail. The R13 test passes, since it records a
    property that already held.
  - **Tests:** four UI tests (+4); the R13 and P22 ones are in the ledger.
- **Revision 132 — forward history, and an ascent's lost brush (§3.2, §6.4, §6.7):**
  - **Forward:** `Alt`+`Right` re-enters the rung the latest ascent or crumb left, exactly as it was left, including
    a filter taken off there. A **Forward to …** button does the same (R15). It sits left of Back, so Back never
    moves under a pointer that keeps clicking it. The header hint names the key while there is somewhere to go.
  - **Rules:** the ladder keeps the rungs left, nearest first. The list is always a way down from the current rung,
    one valid descent at a time, so it can never hold more than the rungs below. A descent elsewhere, or a filter taken
    off, ends it, as a new page does in a browser. Enter on the exact row forward names keeps the rest of the way.
  - **Time:** each rung remembers the analysis interval the user had on it when they left it. Every way back (Esc,
    `Alt`+`Left`, a crumb, forward) restores it through the selection coordinator. A rung left unbrushed comes back
    unbrushed.
  - **Fixed:** an ascent wrote the interval field directly, bypassing the selection coordinator. Brush the machine
    rung, narrow the brush on a group, then press Esc: no brush showed, yet the ranking still read "Ranked within
    1.0 – 5.0 µs" with the group's counts, and the next selection event could bring that stale brush back. Esc now
    restores the machine's brush and counts inside it.
  - **Publications:** the navigation memento carries forward history, with viewports rebased to the new extent. It
    stops before the first rung the new generation no longer has, with no notice about rungs nobody is looking at.
  - **Plan:**
    - §3.2 and §6.4 now say what back and forward do, and that "time" is the interval a rung was left with.
    - §6.7's `[`/`]` row said L2–L5 lanes did not exist, but L2 rows and L3 ends step.
    - Its "lane rows" open item was already done.
  - **Tests:** 10 new test methods: 5 property theories over random ladders (4 seeds each), 4 Desktop tests and 1 UI
    test for the keys, the button and Back's position. Five mutations of the ladder each fail the properties, and the
    old ascent fails both brush tests.
- **Revision 131 — what a screen reader hears (§6.5, R15):**
  - **Audit:** a headless test walks the window's UI Automation tree at the machine, group, process and channel rungs
    with the tables shown. Every focusable element and list item needs a name, no name may be a record dump, and every
    list item must speak its row's sentence.
  - **Found and fixed:** every ranked, relationship, interval and search row was read aloud as its C# record,
    `RungRow { Key = executable:unknown, Label = … }`. Avalonia names an item from its container, then from a template
    that is one text block, then from `ToString()`; the templates set the name on an inner grid, which never reaches the
    item. `AccessibleItems` now names each container from its row's `IAccessibleRow.AccessibleName`. Lane options'
    `ToString()` is their label, since a combo box reads its value from it (it read `TimelineLaneOption { … }`).
  - **Canvases:** the graph, timeline and minimap were focusable with no control type. They are now custom controls
    with a role word, help text naming their keyboard path and table, and their caption as status, read on request.
    The pane splitter is named.
  - **Wording:** "1 observations" → "1 observation"; "coverage unknown coverage" → "coverage: unknown"; "12 · 3 in focus
    observations" → "12 observations, 3 in focus"; search kinds read as words, not capitals.
  - **Checked:** with the container naming switched off, the audit fails on exactly the old record dumps.
  - **Tests:** two UI tests (+2), both in the traceability ledger under R15.
- **Revision 130 — the waiting capture states itself, and P26 is asserted (§6.2, §6.8, §19.3):**
  - **Waiting state:** a capture that records but has published nothing yet read "No capture is running" beside a
    recording health strip. The header now reads "Recording · first view pending", and the empty rung and disclosure
    say when the first view comes. Stopping with nothing published says so; the words for no capture return after it.
  - **No time axis without time:** an empty workspace's placeholder extent read "Time scope: All 0.1 µs" and drew a
    0.0–0.1 µs axis. It now reads "No time recorded yet", and the timeline draws no axis.
  - **A saved view gives way only once a capture records:** starting keeps it, so a declined approval loses nothing.
    `BeginCapture`'s reset became `ForgetDisplayedSession` so a test can follow the same path.
  - **P26 asserted:** with a live preview drawn, the ranking, the interval table and CSV and JSON exports are exactly
    what they were. `]` stepping ends on a published bucket, and a brush dragged into the edge stops at the published
    extent. The ledger now covers P26.
  - **Build:** `JournalProbe` built one `JsonSerializerOptions` per call, which the 10.0.1xx analyzers refuse (CA1869).
  - **Tests:** two UI tests (+2).
- **Revision 129 — a long capture stays inside its budgets (§12, §19.3, ADR-025, broker-v1 §5.3):**
  - **Found by profiling** revision 128's 10-minute session, and each fixed exactly:
    - **Leases:** they re-opened all 292 chunks each time (33 ms). They now confirm hashed files from one directory
      listing (1 ms), and a lease on an unchanged generation reuses its verified manifest.
    - **Process fields:** process derivation read all 97,897 source-field rows through the inspection path (42 ms).
    - **Endpoints:** relation derivation and the projector decoded each row's endpoints up to four times.
  - **Runner:** follow and projection run as one background step at a time. Status, the preview and lease renewal keep
    their own 250 ms cadence (`LiveDerivation`).
  - **Broker defect found by the run:** a connection closed after 4,096 commands, so a 4 Hz owner lost its capture
    after 17 minutes. `broker-v1` now bounds a connection by rate: 256 at once, 64 a second, answered late rather than
    refused.
  - **Verified equal:** old and new builds give identical instance, relation, per-row binding and overview digests on
    two real sessions.
  - **Result** (`bench/results/first-feedback-20260925T215018Z-10min-bounded`): every budget met, 73,660 records.

    | Measure | Revision 128 | Revision 129 |
    |---|---|---|
    | Projection p95 | 264 ms | 85 ms |
    | Follow p95 | 250 ms | 72 ms |
    | Event-to-visible p95 | 966 ms | 849 ms |
    | Event-to-exact p95 | 2.99 s | 2.69 s |
    | Viewer private memory at the end | 153 MiB | 76 MiB |

    The 31 records never previewed were journaled as the quota closed the capture, and were exact within 2.4 s.
  - **Also:** a follower refuses other evidence as other evidence before comparing chunk counts, which also fixes a
    Debug test that failed when that capture published fewer chunks. The harness records a broker that outlives its
    capture instead of crashing on it.
  - **Tests:** two store, two Desktop and two broker tests (+6). The broker qualification passed
    (`bench/results/broker-qualification-20260925T214822Z`).
- **Revision 128 — M2's live-session gate qualified on real ETW, and what a long capture costs (§3.1, §12, M2 gate):**
  - **Bounded session:** `first-feedback --seconds 600 --bounded` runs the default Explore capture until the broker ends
    it at its 600 s quota. Schema v3 samples viewer and broker memory every 10 s and reports one-minute trend windows.
    The collector keeps only measurements, never overviews, so memory is the runner's own.
  - **Result** (`bench/results/first-feedback-20260925T205636Z-10min-bounded`): saved whole, 88,800 records, no drops
    or loss, no leaked ETW session.

    | Measure | Whole run | Last minute |
    |---|---|---|
    | Event-to-visible p95 / p99 | 966 / 1,173 ms | 1,031 / 1,171 ms |
    | Event-to-exact p95 | 2.99 s | 3.10 s |
    | Projection p95 | 264 ms (budget 250) | 266 ms |
    | Viewer private memory | 50 MiB after 1 min | 153 MiB at the end |

    The broker stayed flat at 42–44 MiB private. Projection grows about 2.5 ms per thousand records, and the preview
    waits behind it in the runner's one loop.
  - **Crashed viewer:** `run`'s new abandoned-client scenario drops the pipe mid-capture without a stop. The broker
    stopped the capture at lease expiry 32.1 s later, finalized it fully, left no ETW session and idle-exited cleanly
    (`bench/results/broker-qualification-20260925T210750Z`; every other scenario passed too).
  - **Plan:** §3.1 step 6 now says the next launch offers to finish a crashed viewer's session from the evidence the
    broker kept. The follower already resumes from its last mirrored chunk; the Desktop does not offer it yet.
  - **Also:** the broker's maintenance diagnostic no longer repeats its own kind ("Maintenance: Maintenance: …").
  - **Tests:** unchanged; the measurements are the qualification tool's.
- **Revision 127 — §6.8's latency windows measured, and one fixed (§6.8, M2 gate):**
  - **Benchmark:** an opt-in benchmark (`INTERCAT_LATENCY_OUTPUT`) drives the real window over 100,000 and 1,000,000
    synthetic records and the saved Explore session. It times canvas cost per pan, zoom and hover, recorded without
    rasterizing. It also times level changes, a brush and a search from gesture to answer.
  - **Found and fixed:** each generation's workspace opened a fresh store, and a fresh store hashes every file its
    generation names. That cost about 0.4 s at a million records before the first answer of each live publication and
    reopen. `SharedSessionStores` now gives a session's workspaces one verified store.
  - **Result at 1M records** (`bench/results/interaction-latency-20260925T204410Z`):

    | Window | Measured |
    |---|---|
    | Pan canvas p95 | 9.7 ms |
    | Zoom canvas p95 | 7.8 ms |
    | Hover p95 | 26 ms |
    | Group level change | 799 ms, was 1,328 ms |
    | Brush to ranking | 342 ms |

    Every window is within budget. The real session answers within 51 ms. Opening at 1M records still takes 1.55 s.
  - **Also fixed:** the opt-in real-session report timed the L1 group count at the end of its checks rather than on
    arrival. The corrected times are 261 ms for the group, about 196 ms for L2 and 72 ms for L3, instead of the
    reported 1.17 s.
  - **Tests:** one Desktop test of the shared store (+1); the benchmark is opt-in and reports skipped without its
    variable.
- **Revision 126 — the live edge, and §12's steady-state budget met (§6.2, §12, §19.3):**
  - **Drawing:** while the view follows a recording, the time after the last published record takes the plot's right
    edge beyond a dashed rule. It carries the broker preview's chunks after those shown, in 100 ms bins at half
    strength, on the published scale where legible. At L0 each lane shows its own mechanism, and the label names a
    mechanism with no lane yet. A focused rung shows it only in its machine row.
  - **Behaviour:** hover explains a bin as a preview; a press selects nothing. A paused, held or zoomed view hides it,
    and the publication holding its chunk retires it. The Desktop runner passes a changed preview at 4 Hz.
  - **Wire:** field 28 carries the capture's journaled records. A preview therefore holds exactly journal indexes
    `[journaled − counted, journaled)`.
  - **Measured:** `first-feedback` v2 times every record to its first preview and its first overview. Three 15 s
    real-ETW runs met every budget:
    - event-to-visible p95 717–766 ms, p99 799–924 ms;
    - every record previewed;
    - exact still p95 2.6 s.
  - **Tests:** a Desktop conversion test and a 1080×700 window test (+2).
- **Revision 125 — broker live preview, and what live projection costs (§12, §19.3, broker-v1 §5.9):**
  - **Measured first:** the steady-state miss is not what a timeline pyramid would fix. On synthetic TCP sessions, a
    new generation's overview took 0.33 s at 100,000 rows and 1.0 s at 1,000,000. A plain pass over the time and
    mechanism columns was 24 ms per million rows. The rest was per-generation process and relation derivation
    (115 and 212 ms), per-row relation lookups and per-column tallies. §12 now records this: S4 is needed for
    reopen at 100 GiB, and live cadence at scale needs incremental IC-015 derivation.
  - **Preview:** `GetStatus` fields 21–27 now carry every journaled record counted by chunk, mechanism and 100 ms bin.
    They cover the chunk being written and the four published before it. The recorder's single writer thread counts,
    not the ETW callback. The counts are bounded to 256 bins a chunk and 2,000 a status, and their totals always add up.
  - **Safety:** counts only; a status whose counts do not add up, or name an impossible chunk, key, mechanism or span,
    is refused.
  - **Tests:** tally bounds, a recording read mid-capture across chunk publications, wire round trip and refusals,
    the dispatcher only while recording, and a real runtime preview through the codec (+4).
  - **Qualification:** real-ETW broker qualification passed (`bench/results/broker-qualification-20260925T163744Z`).
  - Revision 126 draws it and measures it.
- **Revision 124 — L3 channel-end lanes banded by direction (§3.2, §6.2, §6.6):**
  - **Lanes:** the channel rung draws one lane per end under the machine-context row. Each lane is named by the
    process holding the end and the end's own endpoint.
  - **Ends:** a record's end is decided by its own endpoint, so a process connected to itself still has two ends. The
    ends partition the channel bucket by bucket.
  - **Bands:** outbound records rise above an end's midline and inbound fall below, on the shared scale; ↑/↓ label the
    sides in place. A disconnect or other undirected record is a neutral midline mark.
  - **Coverage:** an end can hold only the channel's mechanism, so an empty bucket is judged on that mechanism's
    coverage, as an L0 lane's is. Real data showed why: every quiet bucket of each end had been hatched as unknown.
  - **Interaction:** hover gives the end, the bucket's split and where each part is drawn, and the channel's count. An
    end's name, or the tables' end selector, scopes the interval table and `[`/`]`. The choice survives a same-session
    publication and is forgotten on another channel.
  - **Refactor:** the L1–L3 lanes now share one row set in `TimelineView` for hit tests, hover, points, label clicks and
    stepping, instead of a copy per kind.
  - **Real check:** python.exe PID 90424's loopback channel split into two ends of the same process, :36703 (out 1 ·
    in 2 · 4 in all) and :36704 (out 2 · in 1 · 4 in all), counted in 59–78 ms.
  - **Tests:** one Application, one Desktop and one UI test (+3); the opt-in real-session test now qualifies L3.
- **Revision 123 — L2 source-direction rows (§3.2, §6.2, §6.6, §23):** An instance's focus now splits into one row
  per `EN-Direction` code (Outbound, Inbound, Bidirectional, Unknown direction, No data direction). The rows sit under
  a machine-context row and are counted in the same leased pass as the focus. They partition the instance's records
  bucket by bucket, and each row judges its own coverage. All five rows are always drawn, so none moves under a zoom.
  An empty row reads "· none". Hover gives the row and a separate *Direction:* line. A row follows the source
  catalog's direction, so a connection attempt counts as outbound; the card says it does not tell who initiated the
  conversation. A row's name, or the tables' direction selector, scopes the interval table and `[`/`]`. The choice
  survives a same-session publication. Found while qualifying it, and fixed:
  - Hover lines were cut at two wrapped lines, which dropped an L1 or L2 basis line's accounting clause. They now
    wrap to three.
  - A process with no witnessed executable read "PID 812 · PID 812" in captions, search hits, owner rows and cards.
    `ProcessNode.NameWithPid` now names it once.
  - Plan §23 said a connect has no data direction while the catalog marks it `Outbound`. It now states which field
    means what, and the §3.2 table no longer asks for "one lane per process instance" at a one-instance rung.

  Real check: on the saved Explore session, python.exe PID 90424 splits into Outbound 32 · Inbound 84 · No data
  direction 5, counted in 181 ms at 1080×700. Tests: two Application, one Desktop and one UI test (+4), and the opt-in
  real-session test now qualifies L2. **943 passed, 1 skipped** in both Debug and Release.
- **Revision 122 — interactive L1 process-owner lanes (§3.2, §6.2, §6.7):** A group now draws one row per
  canonical owner, plus a separate machine-context row, on one shared visible rate scale with each owner's own
  coverage. Hover explains exact owner, interval, count and scale; clicking a label selects that process, including
  offscreen rows reached from the ranked table or graph. The interval table lists the selected owner's buckets;
  Group totals restores the aggregate. Bracket keys step through that owner's occupied buckets. A 96-row real
  Explore group passed the opt-in scroll, hover and selection check; a headless 1080×700 case exercises context,
  table, label, stepping and zoom. **939 passed, 1 skipped** in serial Debug and Release runs.
- **Revision 121 — exact bounded L1 owner-lane data (§3.2, §6.2):** A multi-instance group focus now counts each
  admitted record into its canonical owner's row in the existing leased timeline scan. Rows partition the exact
  focus on identical columns; each row judges coverage from its own observed mechanisms, including an explicit
  unknown for an empty bucket. The query allocates no more than **200 lanes / 20,000 cells**; over-budget groups
  keep their exact aggregate and a refusal reason rather than dropping marks. Desktop carries rows only with their
  focus across a same-session live publication. A real saved Explore group of **96 svchost.exe instances** produced
  96 exact owner rows and 137 focused records (105 ms in the opt-in UI run). Rendering and interaction followed in
  revision 122. One new Application budget test; **938 passed, 1 skipped** in serial Debug and Release runs.
- **Revision 120 — keyboard/table access to L0 lanes (§6.2, §6.5, §6.7):** A lane name can now be clicked to focus
  it; the table's keyboard-accessible selector chooses the same focus. The interval table then lists that lane's exact
  buckets and coverage, including zoomed detail, while graph and ranking stay unfiltered. `[`/`]` step only through
  occupied buckets of the focused lane; “All mechanisms” restores machine stepping. Focus survives a same-session
  publication by mechanism identity; a vanished lane is explicitly reported and falls back to All mechanisms.
  Descending releases a long L0 scroller's minimum height; ascending restores it.
  One new headless UI test plus expanded zoom/layout checks. **937 passed, 1 skipped** in Debug and Release; the
  opt-in UI test against the user's saved Explore session passed separately in Release.
- **Revision 119 — draw and interact with L0 mechanism lanes (§3.2, §6.2):** The machine timeline now uses the
  overview's exact per-mechanism bucket series. Rows share a visible rate scale but retain their own coverage;
  unknown coverage does not paint an entire observed bar as lost. Hover names the mechanism, exact interval, count,
  coverage and scale; click selects that interval. Zoomed detail replaces the coarse columns for every lane together,
  with a complete-overview fallback for partial detail. The label gutter scrolls long lane lists by wheel, and
  Up/Down/Page Up/Page Down work from the focused timeline without changing time. Headless tests cover separate lane
  hits, zoomed lane detail, selection and scrolling at 1080×700. The user's finalized Explore session also passed the
  opt-in real-session lane hover check. **936 passed, 1 skipped** in both Debug and Release.
- **Revision 118 — qualify the repaired Desktop Explore and clarify live coverage (§3.1, §6.3):** The user opened
  the rebuilt Desktop, captured, stopped/saved and closed successfully. Its final generation held **12,976 source
  observations**, a coverage ledger and **two admitted graph relationships**; the opt-in real-session UI test passed.
  At the earlier live screenshot, the graph had no paired relationship and the coverage ledger was not yet final;
  that did not mean the timeline's records were lost. The legend now calls hatching a gap *or unknown coverage*, live
  zero-loss wording states final coverage is pending, and a zero-edge graph header points to observed timeline data.
  One Desktop regression added and existing UI assertions updated; **933 passed, 1 skipped** in both Debug and Release.
- **Revision 117 — repair the actual Start exploring blocker (§3.1, IC-014):** An elevated five-second CLI
  Explore attempt had failed with broker exit 3. Direct broker stderr identified the cause: a standard ProgramData
  ACE rendered with `DCLCRPCR`, which the strict broker-root SDDL parser did not need to understand when checking
  only the parent owner. The parent check now requests and parses the owner alone; strict ACE and label validation
  of the *broker root* is unchanged. A second elevated five-second Explore attempt on that host finalized with all
  milestones and 549 derived records. The Desktop now puts a persistent error and retry immediately below Start,
  with a bounded scrollable long reason and tooltip; generic launcher advice no longer asks for directory deletion.
  Both Debug client outputs contain the fixed broker. One security regression and three headless UI cases added;
  **932 passed, 1 skipped** in both Debug and Release. Desktop click-through followed in revision 118.
- **Revision 116 — exact L0 lane-ready timeline data (§3.2, §6.2):** The overview's existing leased timeline
  scan now publishes one bucket series per observed mechanism, and zoomed detail does the same. Lane counts partition
  every whole-timeline bucket without a second segment scan. The existing overview separately counts rows without
  session time; they cannot be placed in any lane. Coverage
  is queried for each lane's own mechanism even in an empty bucket; the whole timeline's dominant hue is never used to
  infer a lane's coverage. The workspace carries these immutable series through interval re-ranking. Rendering and
  lane interaction followed in revision 119. One new Application test; **928 passed, 1 skipped** in both
  Debug and Release.
- **Revision 115 — bounded, privacy-aware Desktop search (§6.7):** Ctrl+F focuses an always-visible search field;
  groups, processes and channels rank by exact/prefix/substring metadata match, and a bounded list states the full
  match count. Enter or double-click follows the ladder to a hit; Esc clears search. Search survives same-session live
  publication. A stale hit cannot change the ladder, and opening a whole-session hit clears a brush that could hide it.
  Endpoint strings may match but are not shown in search snippets (P16). The search uses the current in-memory snapshot,
  not an indexed M4 full-content search. Three Application, two Desktop and one UI tests added; **927 passed, 1 skipped**
  in both Debug and Release.
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

1. Keep large sessions inside their budgets. Revision 129 did this for the default 10-minute capture; projection still
   grows about 0.8 ms per thousand records.
   - S4's persisted overview pyramid (P25).
   - IC-015's incremental process and relation derivation, so exact live results keep a sub-second cadence and viewer
     memory stops growing with the session (S2).
   Revision 142 removes repeat segment reads and checks within one open store, but the projection still scans the rows;
   re-run the bounded 10-minute first-feedback and revision 127's latency benchmark, then the 1M and 10M-row gates, as
   S4 and incremental derivation land.
   - **Measured next step:** a warm query re-reads and re-verifies every segment the reader cache cannot hold. In a
     sampled trace at 1M rows under the old 64 MiB budget, opening segments took 13.7% of the samples, 8.7% of them the
     whole-file SHA-256, and column checksums took 8.8%, nearly all on a column's first read after an open. Revision
     148 raised the budget to 256 MiB, so a million rows (169 MiB) now fit, but the cliff returns near 1.5M rows. The
     structural fix is **column-granular reads**, in three steps:
     1. Done in revision 149: segment-v1 minor 1 gives the directories and the variable chunk checksums of their own,
        so a reader can check every byte it interprets without hashing the whole file.
     2. A reader that reads the header, the directories and the time column at open, and every other column from the
        file on its first read. A minor-0 segment keeps today's whole-file open. Mapping files instead would fight
        retention, since Windows will not delete a mapped file.
     3. The cache admits a reader by the bytes it holds, not by its file's length.

     Then decide whether a fresh store must still hash every segment it names at open (store-v1 §3). S1's open budget
     at scale turns on it.
2. Run a real screen reader (Narrator and NVDA) over the Desktop on Windows. Revision 131 audited the automation tree
   headlessly; it cannot hear what a screen reader says. Then add pin/collapse/search for lanes as the observed lane
   count requires. L4's operation lanes, with duration bars and byte projections where a derivation supports them,
   wait on item 3's operations; L5 keeps its marks.
3. Continue M1's IC-015 operation/topology derivations and IC-016a checkpoint without inventing unsupported
   mechanism facts. Then resume the remaining milestone and retail-build gates from the plan.
4. §11.3's third preset, the explicitly unredacted original evidence package, and redacted packages above 1,000,000
   rows.
5. Interaction follow-ups with no dependents:
   - `icat capture` writes no follow ticket, so only the Desktop offers to finish a crashed capture's session; a CLI
     user runs `icat follow` on the evidence again.
   - Qualify the **Other processes** remainder on real data when a naturally eligible capture exists. It is a budget
     fallback, covered synthetically; the dense capture never needs it.
   - Pins that survive reopening, once §26.3's workspace persistence exists.
   - §6.7's remaining row: `Ctrl`+click multi-selection as an explicit predicate. Indexed/progressive search belongs
     to the later M4 scale gate.
   - §6.2's minimum drawn width (5 px) and pointer snapping. At the minimum window the plot is 458 px, or 326–378 px
     beside lane labels, so the 64 overview columns are 5–7 px and their bars 3–5 px. They can still be pointed at,
     because a hit takes the whole column's time. Widening must stay cosmetic (R13).
   - R11 beyond paint: the aggregate, admission and decode loops have IC-019's Windows allocation measurements but no
     test that runs here. A pan still formats and lays out the ticks it draws, which §19.4 allows.
   - Theme modes (§6.1, §26.2, §26.3): light and dark follow the operating system since revision 138, and its
     high-contrast setting since revision 140; since revision 146 the user can choose one, kept in `app-settings-v1`.
     Revision 147 restated menus, tool tips, scroll bars and list selection in high contrast.
     - On Windows, check each of the system's high-contrast themes, and whether Avalonia reports light or dark for
       each as expected.
   - §6.6's unmeasured encoding (an open cross-hatch outline) is drawn nowhere, because no pane plots a value that can
     be unknown yet. Draw it, with its legend entry, when the first one does (bytes, or §6.2's heat cells).

## Verification and cautions

- Revision 149 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,066 tests: 1,064
  passed, 2 skipped**, zero failures.
- Revision 148 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,061 tests: 1,059
  passed, 2 skipped**, zero failures.
- Revision 147 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,061 tests: 1,059
  passed, 2 skipped**, zero failures.
- Revision 146 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,060 tests: 1,058
  passed, 2 skipped**, zero failures.
- Revision 145 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,055 tests: 1,053
  passed, 2 skipped**, zero failures.
- Revision 144 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,053 tests: 1,051
  passed, 2 skipped**, zero failures.
- Revision 143 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,050 tests: 1,048
  passed, 2 skipped**, zero failures. The crashed-viewer scenario passed on real ETW, and it leaked no ETW session.
- Revision 142 was written without an SDK and then built and tested on Windows with the pinned SDK 10.0.401: Debug and
  Release both ran **1,023 tests: 1,021 passed, 2 skipped**, zero failures. This is the first full clean run on Windows
  since revision 129, so the 86 CaptureBroker tests the Linux container could not run pass again.
- Revision 141 was built and tested in the same Linux container: Debug and Release both ran **1,014 tests: 926 passed, 2 skipped, 86 failed**, and the
  failures are again only the 86 CaptureBroker tests that need Windows.
- Revision 140 was built and tested in the same Linux container: Debug and Release both ran **1,013 tests: 925 passed, 2 skipped, 86 failed**, and the
  failures are again only the 86 CaptureBroker tests that need Windows.
- Revision 139 was built and tested in the same Linux container: Debug and Release both ran **1,010 tests: 922 passed, 2 skipped, 86 failed**, and the
  failures are again only the 86 CaptureBroker tests that need Windows.
- Revision 138 was built and tested in the same Linux container: Debug and Release both ran **1,005 tests: 917 passed, 2 skipped, 86 failed**, and the
  failures are again only the 86 CaptureBroker tests that need Windows.
- Revision 137 was built and tested in the same Linux container: Debug and Release both ran **1,004 tests: 916 passed, 2 skipped, 86 failed**, and the
  failures are again only the 86 CaptureBroker tests that need Windows.
- Revision 136 was built and tested in the same Linux container: Debug and Release both ran **1,000 tests: 912 passed, 2 skipped, 86 failed**, and the
  failures are again only the 86 CaptureBroker tests that need Windows.
- Revision 135 was built and tested in the same Linux container: Debug and Release both ran **999 tests: 911 passed, 2 skipped, 86 failed**, and the
  failures are again only the 86 CaptureBroker tests that need Windows.
- Revision 134 was built and tested in the same Linux container: Debug and Release both ran **998 tests: 910 passed, 2 skipped, 86 failed**, and the
  failures are again only the 86 CaptureBroker tests that need Windows.
- Revision 133 was built and tested in the same Linux container: Debug and Release both ran **994 tests: 906 passed, 2 skipped, 86 failed**, and the
  failures are again only the 86 CaptureBroker tests that need Windows.
- Revision 132 was built and tested in the same Linux container: Debug and Release both ran **990 tests: 902 passed, 2 skipped, 86 failed**, and the
  failures are again only the 86 CaptureBroker tests that need Windows.
- Revision 131 was built and tested in the same Linux container: Debug and Release both ran **965 tests: 877 passed, 2 skipped, 86 failed**, and the
  failures are again only the 86 CaptureBroker tests that need Windows.
- Revision 130 was built and tested in a Linux cloud container, not on Windows.
  - The pinned SDK 10.0.401 was unreachable there, so the build used Ubuntu's 10.0.112 through a local `global.json`
    override. The override is not committed.
  - Debug and Release both ran **963 tests: 875 passed, 2 skipped, 86 failed**. Every failure is one of the 86 CaptureBroker tests that need
    Windows (token authentication, `kernel32`, Windows paths); they fail the same way on revision 129 there.
  - Still owed on Windows: the full suite on the pinned SDK.
- Two Claude sessions pushed to `main` in parallel on 2026-09-25/26. A Linux container session built a duplicate live
  edge while a Windows session shipped revisions 126–129. The duplicate was discarded, and only its additive parts
  became revision 130. Fetch `origin/main` before starting a slice and again before pushing.
- Last executed clean baseline on Windows: revision 149, **1,064 passed, 2 skipped, in Debug and Release**. Before
  it, revision 148: 1,059 passed, 2 skipped; revision 129: 959 passed, 2 skipped. Revision 129 adds two store, two
  Desktop and two broker tests (+6). Its real-ETW measurements are
  `bench/results/first-feedback-20260925T215018Z-10min-bounded` and
  `bench/results/broker-qualification-20260925T214822Z`.
  - Run nothing else while a first-feedback run records: a concurrent build or suite would be measured as projection
    cost.
  - Once-per-publication loops run tier-0 code for their first few calls. A profiler should report steady state after
    warm-up, or it overstates those loops several times over (process derivation: 116 ms cold, 25 ms warm).
  - Revision 128 adds no tests; its real-ETW measurements are
    `bench/results/first-feedback-20260925T205636Z-10min-bounded` and
    `bench/results/broker-qualification-20260925T210750Z`.
  - Revision 127 adds one Desktop test (+1) and the opt-in latency benchmark (+1 skip); its measurement is
    `bench/results/interaction-latency-20260925T204410Z`.
  - Revision 126 adds one Desktop and one UI test (+2); real-ETW first-feedback met every budget
    (`bench/results/first-feedback-20260925T203141Z`).
  - Revision 125 adds two recorder, one wire and one theory case (+4); the real-ETW broker qualification passed.
  - Revision 124 adds one Application, one Desktop and one UI test (+3) and extends the opt-in real-session check to
    L3, which passed separately in Release on the saved Explore session.
  - Revision 123 adds two Application, one Desktop and one UI test (+4) and extends the opt-in real-session check to
    L2, which passed separately in Release on the saved Explore session.
  - Caution from revision 123: a filtered `dotnet test` whose build fails still runs the previous binary. Grep its
    output for `error` or build the test project first, as the opt-in run showed before its compile error was seen.
  - Revision 122 adds one UI case and extends the opt-in real-session check, which passed separately in Release.
  - Revision 121 adds one Application lane-budget test (+1), strengthens the focused query partition check and
    exercises 96 owner lanes and same-focus carry on the user's real saved Explore session. A Debug follower test
    failed once while Debug and Release suites ran concurrently; it passed alone and on a serial full Debug rerun.
  - Revision 120 adds one L0 lane-selection UI case (+1) and expands zoom/layout assertions. The opt-in real saved
    Explore session UI test passed separately in Release.
  - Revision 119 adds two lane UI cases and one zoomed-detail UI case (+3). The user's finalized Desktop
    Explore session passed the opt-in mechanism-lane hover check in a separate Release run.
  - Revision 118 added one Desktop zero-edge regression (+1) and updated UI legend/loss assertions. The user's
    finalized Desktop Explore session passed the opt-in real-session UI check in a separate Release run.
  - Revision 117 added one broker-security regression and three UI refusal-layout cases (+4). An initial Release
    run caught the new test missing from `fixtures/index.json`; traceability was updated and both suites reran clean.
  - Revision 116 added one Application mechanism-lane invariant test (+1).
  - Debug emitted transient MSBuild copy-retry warnings because an already-running `InterCat.Desktop.exe` (PID 4048)
    held its Debug DLLs open. The retries succeeded and all tests passed; that process was left untouched.
  - Revision 115 added three Application, two Desktop and one UI search tests (+6).
  - Revision 108 gained 22 over revision 106's 875: 17 in Application and 6 in Desktop.
  - Revision 109 replaced the band test with relationship, parking and settling contracts (+2).
  - Revision 110 added hover, pin, label-slot, half-open, keyboard, drag and R3 tests.
  - Revision 111 added one Application, three Desktop and one UI test (+5).
  - Revision 112 added one Application and three Desktop tests (+4).
  - Revision 113 added one Desktop and one UI hover test (+2).
  - Revision 114 added four UI gesture tests (+4).
  - The two skips are opt-in measurements that report skipped, never passed, without their variables:
    - the real-session UI test, run with `INTERCAT_REAL_SESSION=<session dir>`;
    - the §6.8 latency benchmark, run with `INTERCAT_LATENCY_OUTPUT=<new dir>`, which also measures the real session
      when that variable is set.
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
- Recent real evidence: `bench/results/first-feedback-20260925T203141Z` (schema v2).
  - First overview 0.97–1.36 s after the first record; derivation p95 174–250 ms and projection p95 22–134 ms.
  - Event-to-visible p95 717–766 ms through the live preview; exact p95 2.57–2.59 s.
  - Before the preview, event-to-visible missed at p95 2.6–3.2 s (`first-feedback-20260924T132726Z-live-counters`).
- On a 94,694-record session, a one-pass evidence export took 2.9 s instead of 83.5 s through 474 page reads;
  the output was identical. This is not a whole-product scale qualification.
- The broker/source measurements are on one pre-release Windows build; do not generalize capture overhead or
  capability tier to retail builds. The report is pseudonymized, **not anonymous**: times, counts and workload
  shapes may identify a machine. The detailed export is sensitive. Screen-reader audit is still open.
- Keep user-owned untracked files (currently `Zip-GitFiles.ps1`) untouched and uncommitted. Before each slice,
  inspect `git status` and these tables. After each coherent slice, build and run the full suite in both
  configurations: the analyzers treat warnings as errors, and revision 107 shows that an uncompiled slice hides failures.
  Then update this file and the plan if needed, commit and push `main`.

## Key reference contracts

`contracts/journal-v1.md`, `store-v1.md`, `segment-v1.md`, `metrics-v1.md`, `entities-v1.md`,
`query-identity-v1.md`, `live-follow-v1.md`, `app-settings-v1.md`; ADR-008, ADR-010, ADR-012, ADR-013, ADR-023–028; the complete historical ledger linked above.
