# InterCat implementation status

Updated: 2026-10-08 · Plan revision: 432 · Branch: `main`

This is the **current resume point**, not a running transcript. Update the backlog and open-work tables in place after
each slice, then add only a short latest-change note. The complete pre-revision-104 chronology, measurements, and old
verification ledger are preserved in [the historical status](history/IMPLEMENTATION-STATUS-through-revision-103.md),
and revisions 104–229 in [the second historical log](history/IMPLEMENTATION-STATUS-revisions-104-to-229.md).
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
broker kept, including what the broker recorded after the crash. Since revision 157, a live generation's derivation
extends the previous one's, and since revision 158 the overview and minimap come from each segment's in-memory tiles,
S4's first level. Since revision 162 a finished session reopens from its derivation checkpoint instead of deriving
its processes and relationships from every record. Since revision 163 its first view comes from a persisted overview
too, and opens no segment: 4M and 10M rows both reopen in about 0.17 s, down from 1.1 s and 2.2–2.4 s.
Since revision 166 the ranked table ranks groups and processes by each process's own records, kept in the checkpoint,
where it ranked by paired TCP alone and showed a real capture as zeros.
L4's duration bars are drawn for the operations derived so far: an RPC channel's calls since revision 181 and a
process's HTTP exchanges since revision 290, as density past 4,000 in view. Other mechanisms' wait on derived
operations.
M3's exit gate is met for its measured scope ([the M3 exit review](reviews/M3-exit-review.md), revision 252): RPC over
ALPC without duplicate volume, content truncation and encryption states, and per-build coverage published; pipe and
shared-section topology remain explicitly unavailable and move to M7 and M9. M4 began with its workspace (revision
253); M4 and M5 are not complete. All three of
§11.3's sharing
presets exist: a metadata-only **report**, a reopenable redacted **session package**, and an exact, unredacted
**original evidence package**. The communication graph is a bounded §6.3 projection with a relationship-first §19.4 layout, qualified on
two real sessions, one sparse and one dense. Processes with no relationship are counted in one parked node, and groups
collapse only under budget pressure. Hubs draw as stars with components apart, executable groups read as file names,
and positions survive descents and live publications. A bounded metadata search now finds groups, process instances and
channels by name, PID or endpoint; endpoint strings can match but are omitted from result snippets. Graph marks now explain their scope and evidence on hover; nodes
can be dragged into pinned positions or pinned from the keyboard, and an explicit re-layout preserves those user constraints.
Below the machine rung the graph draws only the rung's neighbourhood, and one **Rest of the machine** node counts every
other process. Double-clicking a relationship's edge, or Enter on its row in the relationship table, opens its channel,
or an RPC relationship's linked calls. The timeline draws in colour the records E would
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
| IC-005/006 feasibility | Measured: TCP and UDP `TrafficVisualization` over IPv4 and, since revision 174, IPv6 loopback; pipe `Unsupported`; RPC `ExperimentalEvidence`; since revision 187, ALPC measured in the lab through a private system logger, linking RPC client calls to the calls that served them (ADR-034); since revision 223 captured by the product's opt-in RPC peers profile (ADR-035), whose links name each call's other end (revision 225) and draw RPC peers (revision 227) | Sections and wider mechanisms are unqualified; ALPC stays opt-in (Moderate), and no RPC byte claims. |
| IC-011 journal | Complete for validated sources | New source/content adapters need their own evidence. |
| IC-012 profiles | Metadata Explore, Focused TCP and RPC peers enforceable; a Content request for WinINet's HTTP exchanges (ADR-037), and the content fixture, keep content through `icat record` only, scoped to named processes; other Content requests preview only | A validated content-capable source, its payload-specific scope and impact proof before enabling Content; broader profiles remain. |
| IC-013 canonical import | ETL import into verified session implemented | Completed-import reuse/catalogue, normalizer-upgrade generations, ETL/journal overlap disclosure. |
| IC-014 broker | Authenticated pipe, protected root, durable ownership/recovery, live evidence and live preview counts, ordinary CLI/Desktop client implemented; parent-owner parser blocker repaired and CLI/Desktop Explore exercised on the affected host; a crashed client's capture qualified to stop at lease expiry, finalized and leak-free, and its session finished by the next launch from the follow's ticket (`live-follow-v1`, qualified on real ETW), and a crashed `icat capture`'s by `icat follow <session>`; a connection bounded by request rate rather than a total, so an owner keeps it for a 24-hour capture; since revision 394 tests hold P18, P19 and P20 on any host: what the broker can reach, ownership by the authenticated identity alone, and no intrusive peer resolution | Installer pre-creation, retail-build matrix and remaining broker release qualification. |
| IC-015 metrics/entities | Source-observation metrics, process/executable grouping, TCP/UDP relations, peer/channel lower bounds; since revision 156 the relation index counts records by their other end, and a relation's untimed records, as it derives, so the overview reads no row's relation; since revision 157 a generation's instances and relations extend the previous generation's, exactly, or are derived in full; since revision 162 a finished session publishes their state as a derivation checkpoint, which a reopen builds both from (`derivation-checkpoint-v1`), and since revision 163 its whole-session overview counts beside it (`overview-index-v1`), so a reopen opens no segment, with each overview column's bytes per mechanism since revision 289 (minor 2), and each process's and TCP channel end's whole-session bytes before any policy since revision 292 (minor 3); since revision 166 each instance's own records per mechanism (`process-activity-v1`, entities-v1 §4a), extended between generations and kept in the checkpoint's format 1.1, rank the ranked table; since revision 173 IPv6 ends relate (`transport-endpoint-relation-v4`); since revision 178 RPC calls are derived as operations (`rpc-call-operation-v1`), and since revision 183 counted on the logical-operations basis (`metrics-v1` §8a); since revision 373 `icat operations`, `icat exchanges` and `icat channels` say what the capture covered of what they list, from its ledger, and no source is said uncollected from its records' absence (R21); since revision 374 `icat processes` states the coverage `icat metric` states of the same answer, and never that a process none of whose bytes was measured sent nothing (R21, P1); since revision 379 its table says a process's bytes are unmeasured, or that it made no sends or receives, as the window's rows do (R21, R18); since revision 380 its call, exchange and peer tables say untimed, not reached, unmeasured or none where they printed a dash (R21, R18); since revision 376 a binding's reason reads in one set of words, and direct evidence only's exclusions are said as what they are (R5, R22); since revision 377 `icat session` says the coverage ledger's facts in its coverage reasons' words (R5); since revision 378 `icat metric` names its basis, byte domain and accounting in the window's words (R5); since revision 404 a grouped answer groups by terminal session, a process whose lifecycle records name none unattributed by that reason (`metrics-v1` §6, `icat metric --group-by session`) | Canonical transfer owner, operation durations and operations beyond RPC calls (counted since revision 183), resource topology (process parents and children are shown since revision 211), relations beyond TCP and UDP, full coverage epoch publication. |
| IC-015a segments | Complete observation/source-field tables; since minor 1, every byte a reader interprets has a checksum of its own, and a published segment's reader reads each column when it is first asked for; since revision 161 the reader cache charges what a reader holds and trims readers to session time and mechanism past its budget; since revision 172 `observation-v2` holds IPv6 endpoint addresses, written only for a segment that has one | Compression and derived scale structures are later work. |
| IC-016 store | Complete M1 commit/recovery/lease/explicit-retention scope; a lease confirms measured dependencies from one directory listing; a viewer opens a session from one listing and hashes its segments, dictionaries and journals after the first view, falling back to the last-known-good, stated, when a file changed; queries share verified immutable segment readers, safe across threads, admitted within 256 MiB of published payload per store, pruned to what the selected generation names; a viewer holds one store per session, a capture's writer included, and keeps readers only for the session it shows; a writer removes superseded manifests as it publishes, and a reader waits out that removal; since revision 162 an index is published as a generation of its own (`CommitIndex`), carried by no additive generation and released with the segments it describes; since revision 234 kept content is a `Content` dependency beside the journal (`content-v1`), carried like a journal and released with its journal chunk, and since revision 306 on its own too, by a `Content` retention once its capture has finished; since revision 315 every later generation carries the latest journal and content release | Rolling retention policy and cross-process pin quota. |
| IC-016a checkpoint | Not started; revision 162's derivation checkpoint holds the state it would take a still-live subset of, but is released with the segments a retention releases | Live entity/endpoint state and open-operation censoring at eviction boundary. |
| IC-017 Desktop projection | Real overview, channel/evidence ladder, bounded metadata search, layout scheduling, live follow, interval/zoom/minimap with wheel and keyboard, exact L0 mechanism lanes, L1 process-owner lanes, L2 source-direction rows and L3 channel-end lanes banded by direction, with shared scale, own coverage, hover/time selection, persistent table/step focus and keyboard/wheel scrolling, exact bounded query data carried through live publications, the visible range as the default scope with a scope lock, and a bounded §6.3 graph with relationship-first layout, semantic hover, manual pinning/re-layout, quiet folding, minimal group collapse, table-shared selection, anchored carried layout, per-rung neighbourhoods with a context node, §6.7's edge double-click and back/forward history that restores each rung's interval, a per-rung timeline focus that counts what E reads, a selection highlighted in the timeline by its own exact count (§6.4) and a Ctrl+click multi-selection that Enter turns into a filter (§6.7), a labelled live edge that previews unpublished records within §12's steady-state budget (P26 asserted), a designed waiting state before a capture's first publication, a launch-time offer to finish a session a crashed viewer left, and the saved sessions listed while none is open; since revision 189 the machine and group rungs rank by records or by bytes sent or received (§6.1's metric selector), since revision 190 by RPC calls made or served, since revision 196 by bytes sent and received and by RPC errors, since revision 199 by median RPC call and serve time, and since revision 200 by peers, each listed by its basis since revision 201, and read per second over the ranked interval since revision 209; a large group keeps its process lanes when zoomed, counted coarser, since revision 210; the machine rung's lanes plot what a byte ranking measures since revision 284, a group's process lanes since revision 285, and a process's direction rows since revision 286; since revision 287 every chosen state (a selected row, a toggle or box that is on, a tab, a focused field, a combo box's list) is drawn from the tokens in every mode, never in the platform's accent colour; since revision 323 each relationship names the rule and version that derived it, and a TCP one the key of each connection it rests on, in the table's evidence tooltip, its screen-reader name and the edge's card (R4), and since revision 324 an RPC one its own key, which opens the calls its links join; since revision 325 the relationship chosen in the table is explained beneath it, for a keyboard as for a pointer, and since revision 327 Enter or a double click on it opens it as a double click on its edge does, an RPC one at the calls its links join; since revision 329 a click on an edge chooses its relationship, which its halo, the inspector and the timeline's highlight then show, and since revision 330 the inspector's evidence card and E follow it too, at every rung since revision 345; since revision 335 a selected process states the rule that bound its records and how strongly, a reused PID's later holder with how many the evidence policy left out (§6.8), since revision 336 a chosen paired channel the rule that paired its ends, whether the capture saw it open and close, and its key, since revision 337 a chosen group how it was formed and what its total leaves out, and since revision 338 a reused PID's candidates are counted one action from the explanation that names them, every read following, with the rail saying so until one click returns to correlated evidence, and since revision 339 a session opened from an investigation keeps that choice there with its pins and ranking (`workspace-v13`); since revision 340 a reused PID's holders are numbered wherever a process is named ("PID 100 #2"), as `icat processes` numbers them, and since revision 341 an edge's card names each process end by name and PID, as the relationship table does; since revision 342 the legend keys the lanes' heights and offers each lane its own scale (§6.2's normalization scope), which an investigation keeps since revision 343 (`workspace-v14`); since revision 344 either main pane fills the column by an explicit command (§6.1); since revision 348 a row chosen among a process's rows is what the evidence card counts, the timeline highlights and E lists; since revision 350 the timeline states its time base, session time since the capture's recorded wall-clock start; since revision 352 Esc cancels a drag in progress before it ascends; since revision 353 the card, with nothing selected, names the records E lists; since revision 354 a reused PID's later holder's empty rung says its records were left out as candidates and offers to count them there; since revision 355 a channel's own rung counts the channel on the card, as E lists it; since revision 356 an aggregate chosen in the graph is what the card counts and E lists, its processes' records; since revision 358 a double click follows only a plain click, so a press where a pan, brush or cancelled drag began zooms or opens nothing; since revision 359 E from an aggregate names it; since revision 360 an investigation keeps the window's panes, the split and the pane filling the column, for every session opened from it (`workspace-v15`); since revision 362 the inspector states beneath its time scope what the capture covered there, mechanism by mechanism (R21); since revision 367 an export, the window's and `icat export`'s, states the same of its scope (R21, R18), and since revision 407 how its processes were grouped, `icat export --group-by session` reaching a session's rung as the window does (R18); since revision 368 a record without a size says why in the rail and the inspector (R3, R5); since revision 370 a channel's own rung names the channel in the inspector, as its card counts it (I5); since revision 371 a selected record states its own coverage (R21); since revision 372 an export's CSV, on every line, and the redacted sharing report state their scope's coverage too (R21); since revision 373 the channel browser says what the capture covered of TCP, announced as the reason an empty list holds none (R21); since revision 375 `icat overview` states the coverage the inspector states beneath its time scope (R18); since revision 382 a one-sided connection's bytes, on its rung and in `icat channels`, and a selection's in the inspector say a direction no transfer measured as unmeasured and one with no record as such, never 0 B (R21); since revision 385 `icat processes` explains each instance's binding in the inspector's words, and what the evidence policy left out of a reused PID's later holder (R18, R21); since revision 386 `icat channels` says how each channel was paired, as the inspector does (R18); since revision 387 a process's lane can be pinned at the top of its group's lanes, by `P` with the timeline focused or the timeline's own button, marked with a pin's head and kept across the session's publications (§6.2); since revision 388 an investigation keeps them for the session, in the order pinned (`workspace-v16`); since revision 389 the timeline draws only the rows in view, so a 200-lane group's frame costs what a group of a few lanes' does (§6.2); since revision 390 a search typed at a group marks the lanes it finds and scrolls its hit's into view (§6.2); since revision 405 the machine rung groups processes by executable or by the terminal session their records name, regrouping the overview it projected, each session named in `icat metric --group-by session`'s words (§6.3), which an investigation keeps for the session since revision 406 (`workspace-v17`); since revision 409 a timeline cell chosen by a click, a step or the interval table is explained beneath the time scope, and a click on a lane counted coarser than the view chooses its own cell (§6.8, R13), and since revision 411 E from it lists exactly its records, behind a filter chip (§6.4); since revision 416 InterCat's own processes are labelled wherever a process is described, since revision 417 one command sets them aside from the rows, the graph and the channels, counting what it set aside (§19.5), and since revision 419 an investigation keeps that choice (`workspace-v18`); since revision 420 a group past the 200-lane bound draws its pinned and busiest members in lanes of their own and folds the rest into one exact lane beneath them, from which a pin brings any of them out (§6.2); since revision 421 a Wall clock toggle reads every instant the view states on the wall clock the capture's machine read, the axis naming its day and offset (§6.2), which an investigation keeps since revision 422 (`workspace-v19`), and since revision 423 a time typed into the search is gone to, its cell made the analysis interval (§6.7), and a zoom draws every lane at its own resolution, a mechanism with nothing in view among them; since revision 426 a moment gone to gives the timeline the keyboard, which says its analysis interval; since revision 427 the interval table states a wall-clock window whole at the smallest window, and keys no mechanism for an interval with no record; since revision 429 a relationship table listing none says why in its place; since revision 431 the timeline's axis ticks round instants of the ladder between its ends, in session time or on the wall clock; since revision 432 export, a redacted report and a re-layout are offered only where they act, saying why not | Resource topology once derived. L4 lanes beyond RPC calls (drawn since revision 181) and HTTP exchanges (since revision 290), and byte composition once IC-015 derives operations that carry a length. Deeper levels of the overview pyramid (S4; its top level is persisted since revision 163) and exact live cadence at 1M rows and beyond. A real screen-reader pass on Windows (the automation tree is audited headlessly since revision 131). |
| IC-018 query identity | Metrics identity frozen; CLI/Desktop export scopes share projection, and since revision 326 an evidence rung's pages and its export read its scope through one mapping, an RPC or HTTP key included; since revision 410 a timeline lane's narrowing to one mechanism, source direction or channel end too, with an identity of its own (§6.4); since revision 424 `icat evidence --from` places a moment by the reader the window's search places it with, and since revision 425 every `--interval` takes a time with its unit, as the window states one | Full UI query identity, generation-aware numeric cache/cursors and coherent bundle publication. |
| §11.3 sharing | All three presets, CLI and Desktop: the metadata-only report (`intercat-share-report-v1`), the reopenable redacted session package (`redacted-session-v1`) and the unredacted original evidence package (`original-evidence-package-v1`); since revision 372 the report states its scope's coverage, mechanism by mechanism, in words a leak test pins (R21); since revision 397 a redacted package can hold an interval of its source with its processes' lifecycle records, its coverage stated for the interval only (`redacted-session-v1` §11), which the window offers from its time scope since revision 399 | A redacted package of a whole investigation, which is not defined: a package's pseudonyms hold only within it. |
| M3 IPC breadth and content | Exit gate met for its measured scope ([review](reviews/M3-exit-review.md), revision 252) | Pipe and shared-section topology unavailable (to M7/M9); RPC over TCP, HTTP/2, compressed responses and asynchronous WinINet unmeasured; timing profile unavailable. |
| M4 multi-machine | In progress: since revision 253 an investigation's workspace (ADR-038) names separately valid sessions by identity, one member per capture, each resolved against where it was last found with the reason, never writing to one; a live capture's host identity includes its installation's. Since revision 254 (`workspace-v2`, ADR-039) a person aligns members to one member's clock with stated bounds, and `icat workspace compare` orders two members' instants only beyond their combined uncertainty. Since revision 255 (ADR-040) a live capture records its clock against the wall clock and its boot, and since revision 256 (`workspace-v3`) a workspace aligns one boot's captures exactly and others through their recorded wall clocks; since revision 257 the Desktop shows an investigation in a window of its own that lists, relinks, adds and opens its sessions; since revision 258 (ADR-041) `icat workspace correlate` proposes candidate joins between captures, since revision 259 the investigation window aligns sessions and lists candidates, since revision 260 (`workspace-v4`) a person's decisions about candidates are kept revisions, flagged when the alignments change, since revision 261 the window draws the merged time, a lane per session, since revision 262 two captures of one host that ran at once are flagged, since revision 263 (ADR-042) an investigation is packaged with its sessions, since revision 264 (`workspace-v5`) two instants a person reads in both measure the clocks' rate, since revision 265 (`workspace-v6`) a session is aligned through another, since revision 266 (`workspace-v7`) a person confirms two host identities are one host, since revision 267 the window compares two instants, since revision 268 it zooms the merged time and opens a column's records, since revision 269 (`workspace-v8`) candidates mirror through known address translations, since revision 270 (`workspace-v9`) a person keeps notes, pinned on the merged time, since revision 271 (`workspace-v10`) saved views of it, since revision 276 a capture that ends before its last publication, as a killed broker's does, still names its boot and start's wall clock to align by, since revision 277 candidate joins and the merged time name the generation of each capture they read (I16), since revision 347 its window lists what it keeps of each session's view, as `icat workspace show` does, since revision 381 its merged time hatches, and says, where a session's capture saw nothing (R21), since revision 383 its candidate joins say what each compared capture covered of TCP and UDP, and since revision 384 `icat workspace timeline` draws its merged time as the window does (R18), and since revision 428 its axis ticks round instants of the 1–2–5 ladder, each named where it is drawn, and its words say times in the digits their span needs; since revision 430 its Align dialog opens on the alignment in force | A known two-host exchange (needs a second host), alignment from shared markers. |
| M5 release | Open; since revision 391 `icat support` writes §20.6's support bundle, with nothing a record holds (`support-bundle-v1`, P16), and since revision 392 the window saves it for the session it shows. Since revision 393 a session's size, bytes per record and §12.1 tier, and a recording's time left under its limits, are stated in the window, `icat session` and `icat capture` (S5). Since revision 395 `icat demo` writes the gate's demo investigation of two hosts, generated and saying so wherever it is read (`demo-investigation-v1`), and since revision 396 the window's Explore the demo makes it once beside the sessions folder and opens it | Full scale/reliability/accessibility/installer/build matrix and release gates; the eviction point of a rolling retention policy, once there is one (S5). |

## Recent slices

- **Revision 432 — an action is offered only where it acts (§6.8, R15):**
  - Export, a redacted report and a re-layout are disabled while nothing is there for them, each saying why in its
    tooltip; Forward's face is one word, so the session's title keeps its room at the smallest window.

- **Revision 431 — the timeline's axis ticks round instants between its ends (§6.2):**
  - In session time the multiples of a ladder width, on the wall clock the round times of day the capture's machine
    read, each named where it is drawn and clear of the ends and of the time base; above ten seconds the ladder is
    §6.2's named one, so minutes tick whole minutes, in the investigation's axis too.

- **Revision 430 — the Align dialog starts from the alignment in force (§8.2, §8.4):**
  - A session aligned already opens the dialog on its method, its reference and what was stated, written back as
    stated, beneath the alignment it would replace; and an empty list of translations or saved views says so in its
    place.

- **Revision 429 — an empty relationship table says why (R15, R21); every workspace test runs on one thread:**
  - In place of no rows the relationship table says that no record pairs two of the session's processes and where a
    connection with its other end outside the session is listed, or that the rung's processes have none while the
    others are at the machine rung, or that InterCat's own were set aside with theirs; and every test of the window's
    model runs on the single-threaded context, which a test holds.

- **Revision 428 — the investigation's axis names round instants where it draws them (§8.2, §6.2):**
  - The merged time's axis labels its ends as the session's timeline does and ticks round instants of the 1–2–5 ladder
    between them, where it rounded quarters of its interval; the window says a lane's records from its first to its
    last, and a column, the interval shown, a saved view and a column opened in the digits their span needs; and it
    keeps two whole sessions in view at its smallest.

- **Revision 427 — the interval table fits the wall clock (§6.2, R15):**
  - A wall-clock window no longer runs under the count beside it, a window across days wraps within its column, the
    bytes trim to the table's width, and an interval with no record keys no mechanism, by glyph or by name. A folder
    written again with another session - a package made anew at the same name - is opened afresh, never read through
    the store the viewer kept for the session it held.

- **Revision 426 — the keyboard stays where a moment gone to is (§6.7, R15):**
  - Enter on a moment gives the timeline the keyboard, so `[` and `]` step on from the cell it chose; the timeline
    says the analysis interval beside the range in view, and the search's status is a polite live region.

- **Revision 425 — an `--interval` in seconds (R18):**
  - `icat evidence`, `timeline`, `export`, `package --redacted` and `workspace timeline` take each bound of
    `--interval` as a tick count or a time with its unit (`1.5s:2.5s`, `250ms`), taken outward to whole ticks, as the
    window states an interval and `icat metric` took one.

- **Revision 424 — `icat evidence --from` a moment (R18):**
  - `icat evidence --from` reads a moment as the window's search reads one - a time of day from the machine's logs, or
    session time - and lists the rows from it on, within `--interval` where one is asked for, naming where it fell; a
    moment the session cannot place is refused in the search's words (`evidence-page-v5`).

- **Revision 423 — go to a time typed into the search (§6.2, §6.7), and zoom every lane:**
  - A time typed into the search - a time of day from the machine's own logs, with its date or offset where needed, or
    session time - is offered first among its hits; Enter centres the timeline on it at the span it had and makes the
    cell holding it the analysis interval as a step would, the selected lane's explained in the inspector. A time the
    session cannot place is said with why, ahead of any names it also matches.
  - A zoom keeps a lane for every mechanism the overview draws, empty where none of its records falls: the timeline
    drew a zoom's lanes only when it held every one, so a span where any mechanism was quiet stayed at the overview's
    resolution. `icat timeline --mechanism` counts a quiet mechanism's columns with their coverage, and names it as
    the window does.

- **Revision 422 — an investigation keeps a session's times read on the wall clock (§6.2, §26.3, R22):**
  - The Wall clock choice lasted only while the window showed the session. An investigation now keeps it for each
    session opened from it (`workspace-v19`), puts it back from the first view and says so, as `icat workspace show`
    does.

- **Revision 421 — read a session's instants on the wall clock its machine kept (§6.2 time base, R18):**
  - Where the capture recorded its clock calibration, a Wall clock toggle reads every instant the view states - the
    axis, the cards, the time scope, the interval table, the evidence and the inspector's record - on the wall clock
    its machine read, in this computer's zone with its offset, so the timeline can be matched against that machine's
    own logs; `icat timeline` and `icat evidence` take `--wall-clock`.

- **Revision 420 — a group past the lane bound folds its least busy members into one lane (§6.2 collapse groups):**
  - A group with more members than the 200-lane bound drew no process lane, only its aggregate row. Its pinned and
    busiest members now draw 199 lanes of their own and the rest are counted exactly in one folded lane beneath them,
    named, explained, found by a search and listed by E as the set it is; a pin brings any of them out.

- **Revision 419 — an investigation keeps InterCat's own processes set aside (§19.5, §26.3, R22):**
  - Setting InterCat's own processes aside lasted only while the window showed the session. An investigation now keeps
    the choice for each session opened from it (`workspace-v18`), puts it back from the first view and says so, as
    `icat workspace show` does.

- **Revision 418 — a collector no process is, said where its PID is held (§19.5, R5, R18):**
  - A collector the capture named but no instance was - its creation time unread, or carried by no lifecycle record -
    was said nowhere a person looks. The inspector's and `icat processes`' explanation of a process holding its PID,
    the rail's tooltip and `icat overview` now say which, and why none is it.

- **Revision 417 — InterCat's own processes can be set aside (§19.5, §6.8, R18):**
  - The rail named InterCat's own processes but offered no way to leave them out of a view. One command now sets them
    aside from the rows, the graph and the channels, counting them and their records, which the timeline and the
    evidence still count; a ranking's totals, a process's lineage, an export and `icat export --set-aside-collectors`
    follow it.

- **Revision 416 — InterCat's own processes are labelled (§19.5, R5, R18):**
  - A capture named its collectors, but no view said which processes they were. The instance whose PID and creation
    time a collector names is now labelled in its row, the inspector, its graph card and `icat processes`; another
    holder of its PID is not.

- **Revision 415 — the broker names the client that asked it to record (§19.5):**
  - A broker's capture named the broker alone. The PID its control pipe names for its client, diagnostic only, now
    reaches the capture, which names it with its creation time as its `Client` collector.

- **Revision 414 — a capture names the processes that collected it (§19.5):**
  - Nothing in a session said which processes were InterCat's own. A live capture now publishes its broker, or `icat
    record`, by PID and creation time with its first generation (`collector-identities-v1`), which later generations
    carry, a follower mirrors, a redacted package leaves out and `icat session` states.

- **Revision 413 — one command restores the default view (§6.8, R5):**
  - A changed ranking, rate, lane scale, grouping or evidence policy was shown only by its own control, and only the
    policy could be put back in one action. The rail now says how many settings differ from their defaults, naming
    each in its tooltip, beside Restore defaults, which puts them all back.

- **Revision 412 — an operation's records are kept and read alone (P25, §12.1 S3):**
  - Each page or zoom of an RPC channel's, call's or relationship's records, or of HTTP exchanges', gathered them anew
    and tested every row beside them. The generation now keeps them by segment, and a page or a zoom reads their rows
    alone: a busy channel's zoom fell from 123-167 ms to 4-9 ms.

- **Revision 411 — E from a chosen cell lists exactly its records (§6.4, R18):**
  - E from a chosen cell listed its rung's records within it. It now lists the cell's own, a lane's narrowing shown as
    a filter chip, as `icat evidence` lists them.

- **Revision 410 — evidence narrowed to a lane's records (§6.4, R18):**
  - A cell's exact records had no scope: E from it listed its rung's. An evidence page, its timeline focus and `icat
    evidence` now narrow to one mechanism's, source direction's or channel end's records (`evidence-page-v4`); the
    window's E from a chosen cell follows.

- **Revision 409 — a timeline cell explains itself (§6.8, R13):**
  - A click chose the machine column beneath a cell, never a coarser lane's own, and the inspector explained no cell.
    A click now chooses the lane's cell, and the inspector says beneath the time scope how it was counted.

- **Revision 408 — paired calls and grouped exchanges are read without their segments (P25, §12.1 S3):**
  - Each query of a generation's RPC calls or HTTP exchanges opened every segment, though they were already paired.
    They now open none, and one call's evidence only the segment holding it: at 1M records it fell from 421-656 ms to
    21-29 ms.

- **Revision 407 — `icat export --group-by session` (R18, §6.3):**
  - A session's rung exported from the window alone. `icat export` now groups as the window does before reading a row
    key, so `--at session:1` reaches it, and the file is the window's byte for byte; an export names its grouping.

- **Revision 406 — an investigation keeps a session's grouping (§6.1, §26.3, `workspace-v17`):**
  - A grouping by terminal session lasted only while the window showed the session. The investigation it was opened
    from now keeps it beside the pins, puts it back from the first view, and says so where it lists what it keeps.

- **Revision 405 — the window groups by executable or terminal session (§6.3's lane grouping):**
  - The window grouped processes by executable only. A Group by selector at the machine rung now regroups the ranked
    rows, the graph and a group's rung by the terminal session each process's records name, from what was projected,
    and a live capture keeps the choice; keeping it in the investigation is next.

- **Revision 404 — group by terminal session (§6.3, `metrics-v1` §6):**
  - Only process, executable, mechanism and peer groupings were derived. A grouped answer now groups by the terminal
    session each process's lifecycle records name, a process naming none unattributed by that reason, and `icat metric
    --group-by session` ranks them; the window's grouping control is next.

- **Revision 403 — a process's connections read only the segments their lifetimes meet (P25, §12.1 S3):**
  - Its connection list counted every segment's rows. It now opens only the segments a listed connection's first to
    last readings meet: a process with one brief connection at 1M rows fell from 127-221 ms to 8-64 ms.

- **Revision 402 — an evidence page opens only the segments it reads (P25, §12.1 S3):**
  - A page opened every segment first, and a time scope's page walked every row of the session. It now opens a segment
    only when its merge reaches it and stops past its scope: a 1% scope's page at 1M rows fell from 171-244 ms to
    10-22 ms.

- **Revision 401 — a brush reads only the segments it meets (P25, §12.1 S3):**
  - A brush's counts, its lanes' bytes and a time scope's byte and peer rankings opened every segment of a reopened
    session. They now take the checkpoint's instances and channels and read only the segments the interval meets: a
    1% brush's byte ranking at 1M rows fell from 99-133 ms to 10-23 ms.

- **Revision 400 — a zoom reads only the segments it meets (P25, §12.1 S3/S4):**
  - The first zoom of a reopened session opened every segment and built every segment's tiles. It now reads each
    segment's header span and opens only those its interval meets: a 1% zoom at 1M rows fell from 86-237 ms to 3-17 ms.

- **Revision 399 — the window shares a redacted package of its time scope (§11.3):**
  - Only `icat package --interval` made one. "Share redacted session…" now offers the inspector's time scope first,
    saying what the package will hold in its readers' words, and a session over the row bound only an interval.

- **Revision 398 — the ranked list keeps the rail's room once a session is shown:**
  - At the smallest window the capture card left the list under two rows. Its first-run sentence and demo button now
    show only until a session is shown, the demo stays in the Investigation menu, and empty lines take no room.

- **Revision 397 — a redacted package of an interval (§11.3):**
  - A session over ten million rows could not be shared redacted. `icat package --redacted --interval` holds an
    interval's records and every lifecycle record of their PIDs, so processes keep their names and bindings; its
    coverage speaks only for the interval, and over its whole time is unknown, in every reader.

- **Revision 396 — the window opens the demo (M5):**
  - Only `icat demo` made it. Explore the demo, beside the saved sessions, now makes it once in InterCat's own folder
    beside the sessions folder and opens its investigation, saying what it is; later clicks open the same demo. Its
    investigation file now takes its name last, so a stopped write is never opened as a demo.

- **Revision 395 — `icat demo` writes the demo investigation (M5):**
  - M5's gate ships a reproducible demo of two hosts and several transports. `icat demo` writes one: two sessions
    that exchange TCP and UDP, aligned, with two candidate joins and every uncollected mechanism listed. Everything
    that reads it says InterCat generated it and nothing in it was captured.

- **Revision 394 — tests hold P18, P19 and P20:**
  - The contract index declared them uncovered. What the broker can reach, ownership by the authenticated identity
    alone, and no intrusive way of resolving a peer are now each held by a test that runs on any host.

- **Revision 393 — a session's growth is disclosed (§12.1 S5):**
  - Nothing said how large a session was or how long a recording had left. The window, `icat session` and
    `icat capture` now state its size as its manifest measured it, its bytes per record and tier, and while it
    records, when its length, journal or the disk's reserve stops it. Sizes on disk read in binary units throughout.

- **Revision 392 — the window saves a support bundle (§20.6, P16):**
  - Only `icat support` made one. "Save support bundle…" now asks first, saying what the bundle holds and leaves
    out in the command line's words, then saves the session shown by its folder's name. The window runs no
    capability probe, so its bundle holds no report and says `icat support` adds one.

- **Revision 391 — `icat support` writes a support bundle with nothing a record holds (§20.6, P16):**
  - Nothing made §20.6's support bundle. `icat support` writes versions, the machine's runtime and capability
    report, and each session's files, rows by mechanism, coverage and loss counters, clock and recording, from an
    allowlist: no content, endpoint, command line, raw record, name or path. It lists all that before writing.

- **Revision 390 — a search finds a group's lanes (§6.2):**
  - At a group of a hundred lanes, a search found a process only to open it. Typed at a group's rung, it now marks
    the lanes it finds - all of them, not only the 50 hits the rail lists - counts them in the caption, and scrolls
    its selected hit's lane into view, selecting nothing.

- **Revision 389 — the timeline draws only the rows in view (§6.2):**
  - Every repaint drew all of a group's lanes, up to 200, though the pane shows a few: a 200-lane frame took three
    times a small group's and allocated twenty times as much. Only the rows in view are drawn now, and a row scrolled
    or grown into view is drawn as it comes.

- **Revision 388 — an investigation keeps a session's pinned lanes (§6.1, §26.3, `workspace-v16`):**
  - Pinned lanes lasted only while the session was open. The investigation it was opened from now keeps them in the
    order pinned and puts them back when it is opened from there again; its window, the notice and `icat workspace
    show` say so in the same words.

- **Revision 387 — pin a process's lane at the top of its group's timeline (§6.2):**
  - A group's lanes were drawn only in the ranked table's order, so two instances of a large group being compared
    could sit dozens of lanes apart. `P` with the timeline focused, or the header's **Pin lane (P)**, now pins the
    selected process's lane first, marked with a pin's head, until it is unpinned; a later publication keeps it.

- **Revision 386 — `icat channels` says how each channel was paired (R18):**
  - It listed a channel's key, endpoints and records alone. Beneath each it now says the inspector's words: the rule
    and how strongly it paired the ends, whether the capture saw it open and close, and the coverage.

- **Revision 385 — `icat processes` says how each instance's records were bound (R18, R21):**
  - A reused PID's later holder listed only its lifecycle records, read as a quiet process. The inspector's words for
    how a process's records were bound, and what the policy left out, now serve `icat processes` too, beneath its
    table and in `--pid`'s detail, and its document counts each instance's candidates and withheld records.

- **Revision 384 — `icat workspace timeline` draws the merged time as the window does (R18):**
  - The investigation's merged time had no command-line form. `timeline` says each lane in the window's words, now
    shared, lists the columns that hold records and the runs its capture did not cover, and writes every column's
    records and coverage in `workspace-timeline-v1`.

- **Revision 383 — candidate joins say what each compared capture covered (R21):**
  - A capture that lost records, never collected UDP or has no ledger can hold no mirror of a connection, yet no
    candidate read as no other end. The window says each shortfall in the caution ink, and that every capture covered
    TCP and UDP where they did; `icat workspace correlate` and its document (`workspace-correlation-v6`) say it too.

- **Revision 382 — a connection's and a selection's bytes say unmeasured or no record, never 0 B (R21, R18):**
  - A connection whose sends stated no size read "0 B sent", and `icat workspace correlate` printed and wrote 0.
    Each direction now reads its measured sum, "2 sends unmeasured" or "no receive recorded", in the window, `icat
    channels` and `correlate`, whose document (`workspace-correlation-v5`) counts transfers and nulls an unmeasured sum.

- **Revision 381 — the investigation's merged time shows and says where a capture saw nothing (R21):**
  - Its lanes drew only records, so a column a capture lost, or never reached, read as a quiet one. Each run of such
    columns is now hatched as one band, as the session's timeline hatches it, and the cursor's column, each lane's
    sentence and a column of none opened say what its capture covered there.

- **Revision 380 — `icat`'s call, exchange and peer tables say what an empty cell means (R21, R18):**
  - Untimed durations, a server group no client call reached and a peer's unmeasured bytes all printed "-". They now
    read "untimed", "not reached" or "no ALPC", and "unmeasured" or "none", and an instance none of whose bytes
    was measured lists the ends it exchanged records with instead of none.

- **Revision 379 — `icat processes` says bytes are unmeasured, or that a process made none (R21, R18):**
  - Its table printed "-" for a process whose sends recorded no size and for one that sent nothing. It now says
    "unmeasured", "no sends" or "no receives" as the window's rows do, and its JSON counts each instance's measured
    and unmeasured records, even where no send of the session measured a size.

- **Revision 378 — `icat metric` names what it counts in the window's words (R5):**
  - It printed "Source observations", "TransportObserved", "SendSide - …" and a sides table of enumeration names.
    One mapping each for a basis, a byte domain and an accounting now serves the ranking selector, a record's size and
    `icat metric`: "source observations", "carried by the transport", "sender-accounted".

- **Revision 377 — `icat session` says the ledger's facts in its coverage reasons' words (R5):**
  - It split enumeration names into "Etl import", "Source session 0 records" and "Unrequested provider". It now says
    "ETL import", "the session reported 0 lost events, …" in layer order, zeros included, and "a provider the capture
    did not request", through one mapping the coverage reasons share.

- **Revision 376 — why a record binds to no process reads in one set of words (R5, R22):**
  - The window and `icat` worded a binding's reason apart, and `icat operations` and `exchanges` printed
    "NotAdmittedByPolicy". One mapping now serves them all, and calls such a binding one the evidence policy does not
    admit, not a reused PID's candidate, which under direct evidence only it is not; the metric caveat says so too.

- **Revision 375 — `icat overview` says what the capture covered, as the inspector does (R18, R21):**
  - It said only "Coverage ledger: published". It now says each mechanism's coverage over the session in the words
    the window states beneath its time scope, "Coverage: covered for TCP · no other mechanism collected", or that a
    generation without a ledger judged nothing.

- **Revision 374 — `icat processes` says what the capture covered, never that a process sent nothing (R21, P1):**
  - It stated no coverage beside its counts, and said "It sent and received no transport bytes" of a process whose
    bytes no record measured. It now says the coverage as `icat metric` does of the same answer, in words and JSON, and
    that no transport byte of such a process was measured, which is not proof it sent or received none.

- **Revision 373 — a listing says what the capture covered of what it lists (R21):**
  - `icat operations` said a session without RPC call records "did not include the RPC source", and one without ALPC
    records "collected no ALPC", from the records' absence alone. It, `icat exchanges`, `icat channels` and the channel
    browser now read RPC's and ALPC's, HTTP's or TCP's coverage from the ledger and say it, "RPC was not collected over
    the session: no admitted descriptor records it.", in their JSON and in words; an empty browser announces it.

- **Revision 372 — an export's CSV and the redacted sharing report state their scope's coverage (R21):**
  - A CSV of a range the capture saw nothing in was a header alone, and a sharing report said nothing of what the
    capture covered, which its recipient cannot look up. Every CSV line now ends with `row_present` and
    `scope_coverage`, an empty scope writes one line that keeps its context, and the sharing report lists each
    mechanism's coverage and says it in the inspector's words, its reasons admitted after their own leak test.

- **Revision 371 — a selected record states its own coverage (R21):**
  - A record's fields said nothing of whether the capture could have missed records beside it. They now end with its
    mechanism's coverage where it was read, "partial gap, not extrapolated: the session reported 1 lost event…", read
    from the ledger each evidence page now carries; a generation without one says it judged nothing.

- **Revision 370 — a channel's own rung names the channel in the inspector, as its card counts it (§6.4, I5):**
  - Opened with Enter, a channel's inspector went back to "client.exe" over a card headed "This channel", with the
    process's binding and lineage. It now names the paired channel, RPC channel or HTTP exchanges as their row did
    where they were chosen, says how a paired channel was paired, and keeps doing so through a newer publication; a
    process chosen there, as in the graph, is described instead.

- **Revision 369 — a direction and an operation's state read in one set of words (R5):**
  - A channel row said "both directions" where the direction rows said "Bidirectional", and the rows named a code no
    version knows "No data direction". The tour's operations read "Send Outbound · 2 s" and "OpenAtBoundary". Every
    direction now reads "bidirectional", "unknown direction" and so on, an unknown code by its number, and the tour's
    operations "Send · outbound · 2 s", "open at capture end", in the words a real RPC call's state uses.

- **Revision 368 — a record's kind, a missing size's reason and a capability read in words (R5, R3):**
  - The window called every missing size "size not exposed", whatever withheld it; it now says why, "size redacted",
    "size lost with its event", in the rail, the spoken name and the inspector. `icat session`, `metric` and
    `capabilities` printed "RequestStart", "unknown (NotExposed)", "Transport", "DisabledByProfile" and
    "TrafficVisualization"; they read the same mappings as the window (`ObservationText`, `CapabilityText`), and §23
    says names are what the command line accepts and JSON carries.

- **Revision 367 — an export states its scope's coverage, as the inspector does (R21, R18):**
  - A ranked export of a range the capture delivered nothing in listed every row at 0 observations, and an evidence
    export of it called itself complete with no record, neither saying the capture saw nothing there. Each export's
    JSON context now lists every mechanism's coverage over its scope with the fact behind it, and its caveats end with
    the words beneath the inspector's time scope, which `icat export` prints; the window's files stay `icat export`'s.

- **Revision 366 — the lane-scale window test reads its pixels with the pointer off the timeline (§13.6):**
  - It rested the pointer on the machine row for a card, then resized the window; whether the card was still drawn
    afterwards depended on other window tests sharing the UI thread, and the card then covered the bar a pixel check
    read. Three copies run together failed 2 rounds out of 15; with the pointer taken off the timeline before each
    read they passed 45 out of 45. Revision 357's wait for the timeline's detail, which could not have helped, is gone.

- **Revision 365 — the command line's tables name mechanisms and states as the window does (R5):**
  - `icat timeline`, `session`, `capabilities` and a content request's preview printed "Tcp", "PartialGap" and
    "ProcessLifecycle, ThreadLifecycle, Udp"; they now read "TCP", "partial gap, not extrapolated" and "process
    lifecycle, thread lifecycle, UDP", from the one mapping the window's rows and hover cards read
    (`CoverageStateText`, `MechanismText`). `icat session` names the epoch of each line of losses.

- **Revision 364 — a coverage state is said in words wherever a caveat states one (R5):**
  - Notes and caveats printed enumeration names, "RPC's capture coverage over this scope is UnknownCoverage", and
    `icat metric` "Tcp covered; … 19 not collected". They now say one sentence form, "RPC's coverage over this scope
    is unknown: …", and `icat metric` states its coverage as the inspector does; a mechanism is named through one
    mapping, `MechanismText`.

- **Revision 363 — `icat evidence` states its page's coverage (R21, R18):**
  - A page with no record in a capture gap said "Rows on page 0" and sent the reader to the coverage ledger. Each
    page now carries each mechanism's coverage over its time scope (`evidence-page-v3`) and says it beneath its fields
    in the inspector's words, which both now take from one place, `CoverageText`.

- **Revision 362 — the inspector states its time scope's coverage (R21, §6.5):**
  - Brushed where the capture delivered no reading, the card read "No own record admitted · 0 paired TCP
    observations", with only the whole session's coverage beside it. Beneath the range it now says what the capture
    covered there, mechanism by mechanism, with the fact behind any state short of covered, in the caution ink where
    it falls short: "Coverage unknown: outside the readings the capture's sources delivered, so a count of none here
    is not proof of inactivity". A range's coverage comes with its counts, and is said to be read until they are.

- **Revision 361 — I18's reader test lets go of the guard from a thread of its own (§13.6):**
  - Its writer let go of the guard from a thread-pool continuation, which runs only once a worker is free. Load
    alone never delayed it in 45 runs; holding the pool's workers made the reader give up every time, with the
    message revision 360's Release run failed with. A thread of its own lets go on time under the same hold, and
    §13.6 now says a test acts that way.

- **Revision 360 — an investigation keeps the window's panes (§6.1, §26.3, `workspace-v15`):**
  - The split dragged between the graph and the timeline, and the pane Expand or F11 let fill the column, lasted only
    while the window was open. An investigation now keeps them once, for every session opened from it, puts them back
    when one is, and says so in its window and in `icat workspace show`. A split leaves each pane its least height, so
    only Expand hides one.

- **Revision 359 — E from an aggregate names it (§6.4):**
  - Its filter read "4 selected processes"; it now reads "Records of: Rest of the machine", over "Records owned by the 4
    processes of Rest of the machine", counted as the shown generation holds them.

- **Revision 358 — a double click follows only a plain click (§6.7):**
  - A second press where a pan, a brush or a cancelled drag began zoomed the timeline, and in the graph opened the node
    whose drag was given up. The timeline and the graph now count a double click only when its first press was a plain
    click.

- **Revision 357 — a count of one reads in the singular (§6.8):**
  - "1 records", "1 across 1 PIDs", "1 stops in scope are paired", "1 of them report" and their kind in the window, the
    command line, the metric caveats and the overview's disclosures now agree with their count, through one helper,
    `CountText`, every layer uses. Plural wording is unchanged.

- **Revision 356 — an aggregate chosen in the graph is its processes (§6.4, I5):**
  - A quiet, undrawn-members or context node chosen in the graph: the timeline highlighted its members' records, the
    card counted its relationships, and E listed the rung's own records. The card now counts its processes' own
    records, relationships and bytes, as for the same processes chosen together, and E lists exactly their records.

- **Revision 355 — a channel's own rung counts the channel on the card (§6.4, I5):**
  - Opened from a process's rows, a channel's, RPC channel's or HTTP exchanges' rung counted the channel in its level
    line and E listed its records, but the card went back to the process. It now reads "This channel", "This RPC
    channel" or "These HTTP exchanges" with what the row counted, a brush included; a relationship or both ends chosen
    there are counted instead, as E then lists them.

- **Revision 354 — a later holder's empty rung says why, and counts its candidates there (§6.8):**
  - A reused PID's later holder's rung, counting correlated evidence only, listed no row and said its one-sided and
    other-mechanism records were "still one step away", though E lists only its creation. It now says its records
    were left out as candidates, with "Count them as candidates" beside E's button. Its RPC, HTTP and one-sided rows,
    the call and peer rankings and the lanes are now tested under each policy. The call rankings' note no longer says
    "1 more completed calls belong".

- **Revision 353 — with nothing selected, the card names what E lists (§6.4, I5):**
  - The card read "Selected process: No evidence selected" while E lists the rung's own records. It now reads
    "Records E lists: Every admitted record in this session", or a group's instances, over the scope the rows count,
    in the words the evidence rung then uses.

- **Revision 352 — Esc cancels a drag in progress (§6.7):**
  - Esc ascended mid-drag, so a pan, brush or node drag begun by mistake could only be finished. Esc now cancels it
    where it began: a pan or a minimap move gives the view back, a brush or a node's drag is dropped. With no drag,
    Esc ascends as before.

- **Revision 351 — `icat timeline` states its time base too (§6.2):**
  - Its text output gains a "Time base" field in the window's words; its JSON is unchanged. The plan's §6.7 row for
    Esc, which contradicted §3.2 and the window, now says Esc ascends a rung and clears the selection at the machine
    rung.

- **Revision 350 — the timeline states its time base (§6.2):**
  - The axis read "10.0 µs" to "19.1 µs" with nothing to say what those count from. Between them it now says "session
    time since 09/29/2026 12:00:00 UTC", the moment the capture's recorded wall clock puts session time 0, in the
    reader's zone with its offset; an import, which recorded none, says "session time". So does its spoken status.

- **Revision 349 — the live preview on each lane's own scale, tested (§6.2, §12):**
  - Revision 342 drew the live edge's bars against each lane's own peak, untested. A window test now reads from the
    pixels that a preview stays on the published scale, that each lane's own scale lifts a quiet lane's preview to its
    row's top, and that beside byte lanes the preview keeps its records scale.

- **Revision 348 — a row chosen among a process's rows is what E lists (§6.4, I5):**
  - The inspector described a chosen channel, connection, RPC channel or HTTP exchanges, but the card counted the
    process and E listed its records, though the pairing's explanation said E lists the channel's. The card now counts
    the row's records, the timeline highlights them and E lists them; clicking the process's node takes it back.

- **Revision 347 — an investigation's window says what it keeps of each session (§26.3):**
  - `icat workspace show` listed each member's pins, ranking, evidence policy and lane scale; the window that opens
    members listed none of them. Each member's row now says what opening it puts back, "Opens with 1 node pinned on
    its graph and its rows ranked by bytes sent per second, as it was left here.", and changes as the main window
    writes it. The command, the notice and the row say it in one set of words.

- **Revision 346 — E lists a chosen set's records (§6.7):**
  - With several processes chosen, E listed one of them while the card counted the set. E now lists the set's records,
    as Enter does.

- **Revision 345 — E lists what the inspector counts at every rung (§6.4, I5):**
  - At a group's rung the card above "Show source records (E)" counted a chosen process or relationship while E listed
    every member's records. E now follows a chosen relationship at any rung, and a process chosen among a group's members.

- **Revision 344 — either main pane fills the column by command (§6.1):**
  - Each pane's header has an Expand toggle, and F11 expands the pane holding the keyboard; the same command gives both
    their places back at the split the person dragged. §6.1's layout rules now describe the stacked panes as built.

- **Revision 343 — an investigation keeps each lane's own scale (§26.3, `workspace-v14`):**
  - A session opened from an investigation keeps whether each timeline lane has its own scale there, with its pins,
    ranking and evidence policy, and gets it back when opened from it again.

- **Revision 342 — each lane on its own scale, one click away (§6.2, §6.8):**
  - Beside a busy lane, a quiet one drew only its floor. The legend now keys the heights ("records per second, one scale
    for every lane") beside a Per lane toggle that reads each lane against its own busiest bar, at every rung with lanes.

- **Revision 341 — an edge's card names its ends by name and PID (§6.3, R5):**
  - A relationship between two instances of one executable read "chrome.exe ↔ chrome.exe" in its card. Its ends are
    now named as the relationship table and the inspector name them, "Browser · PID 8204 ↔ API host · PID 5120".

- **Revision 340 — a reused PID's holders read apart (§7.2, R5):**
  - Two processes that held one PID in turn read alike in the ranked table, the timeline and the graph. Each is now
    numbered as `icat processes` numbers it, "PID 100 #1" and "PID 100 #2", wherever the window names it.

- **Revision 339 — an investigation keeps each session's evidence policy (§6.8, §26.3, `workspace-v13`):**
  - Counting a reused PID's candidates lasted only while the session was open, so reopening it from its investigation
    showed other totals with nothing to say why. A session opened from an investigation now keeps the choice there
    with its pins and ranking, and is counted under it again, from its first view, when opened from it again.

- **Revision 338 — a reused PID's candidates are counted one action away (§6.8, R22):**
  - Where the inspector says a reused PID's later holder's records were left out of a total, "Count them as
    candidates" now counts them in every count, ranking, timeline and record list of the session, keeping the view.
    While they count, the rail says so, and one click goes back to correlated evidence.

- **Revision 337 — a group says how it was formed (§6.8):**
  - A chosen executable group now says how its members were grouped and the rule that bound their records. When a
    reused PID's later holder is among them, it also says how many records its total leaves out.

- **Revision 336 — a channel says how it was paired (§6.8, R4):**
  - A paired TCP channel chosen among a process's rows now says, in the inspector, which rule paired its two ends and
    how strongly, whether the capture saw it open and close, and the key E lists its records by.

- **Revision 335 — a process says how its records were counted (§6.8, R22):**
  - A reused PID's later holder counts only its creation and exit by default: any other record naming its PID could be
    a late record of an earlier holder. Its row read as a quiet process. For any selected process the inspector now
    says which rule bound its records and how strongly, and for such a holder how many the evidence policy left out.

- **Revision 334 — status lines announce what they say (R15):**
  - Every window's status line, a comparison's answer and the capture headline are polite live regions now, so a
    screen reader announces a save, a refusal or a finished search as it happens. The audits require it.

- **Revision 333 — a control is named by what it shows (R15, WCAG 2.5.3):**
  - About fifty controls were spoken as descriptions that left out their own label, which a voice user could not say.
    Each is now named by its label, with the description kept as help text, and the audits require it.

- **Revision 332 — every window beside the main one is heard (R15):**
  - The screen-reader audit now covers the content, raw record and channel windows, the investigation window's pages
    and dialogs, and every prompt. It found the raw record window's record unnamed, and names it.

- **Revision 331 — R11's allocation test measures every thread (R11):**
  - The warm-aggregate test failed once by reading a worker's 18 KB tally as a per-row cost. It measured only the
    calling thread while a query counts its segments on pool threads. It now measures every thread's allocations.

- **Revision 330 — the evidence card and E follow a chosen relationship (§6.4):**
  - With a relationship chosen, the inspector's evidence card counts its records and what was sent across it, where it
    said no evidence was selected. At the machine rung, E lists its records: its channel's, or its linked calls.

- **Revision 329 — a click on an edge chooses its relationship (§6.7, §6.4):**
  - A click on an edge now chooses the relationship it stands for, as its row in the table does. The relationship is the
    selection, no longer its source process: its edge is haloed, the inspector describes it, and the timeline highlights
    its channel's records or its linked calls.

- **Revision 328 — Enter shows a saved view, as a double click does (§8.4, R15):**
  - Enter on a saved view shows it, and Enter in the name box saves the view shown. A view saved on another time
    reference says why it cannot be shown, where it used to do nothing.

- **Revision 327 — a relationship opens from the table as from its edge (§6.7, R15):**
  - Enter or a double click on a relationship in the table now opens it as a double click on its edge does, and the
    rung it opens takes the keyboard. An RPC relationship, from either, opens the calls its links join rather than
    stopping at its source process.

- **Revision 326 — an export holds the records its rung lists, an RPC or HTTP rung's too (§6.4):**
  - An export from an RPC channel's or call's records, or from HTTP exchanges', read the whole session rather than the
    scope it named. An evidence rung's pages and its export now read its scope through one mapping, and a scope that
    cannot be read is refused by the read itself.

- **Revision 325 — a relationship chosen in the table is explained beneath it (§6.2):**
  - The rule and evidence a relationship's tooltip names now also stand in a line beneath the table for the one chosen
    there, so a person moving through the table by keyboard reads them too.

- **Revision 324 — an RPC relationship opens the calls its links join (R4 asserted):**
  - An RPC relationship's own key now opens every call its links join, at both ends, under the evidence policy that
    counts it, and `icat evidence --channel` takes it. R4 is covered: every relationship carries its rule, version,
    strength and evidence keys.

- **Revision 323 — a relationship names the rule that made it and what it rests on (R4):**
  - Each relationship now carries its rule, by identity and version, and a TCP one the key of each connection it rests
    on, which opens that connection's records. The relationships table's tooltip, its screen-reader name and the edge's
    hover card say so. An RPC relationship's links are still kept only as counts, so R4 stays open for them.

- **Revision 322 — every prompt is as tall as what it says (§6.8):**
  - Five prompts held a fixed height: one had nine pixels to spare for a long path, and another left 122 empty below
    its buttons. Each now sizes to its words, and the closing question keeps recording on Escape. A test draws all seven
    prompts with a long path, refusing text cut at any edge.

- **Revision 321 — the investigation window keeps its pages at its smallest size (§6.8):**
  - At 680 by 460 the timeline had no height and its chosen column no width, and the lists kept a row or less. Three
    dialogs cut off a long session name, one pushing its instant out of the window. The minimum is now 680 by 600. The
    timeline's tools wrap, its chart and words scroll together, its axis labels keep apart, and pickers trim with a
    tooltip.

- **Revision 320 — the secondary windows cut off no text (§6.8):**
  - The channel browser's rows ran a long process name past the list, losing a channel's endpoints and record count;
    they wrap now, and the content, raw-record and channel windows are tested at their minimum size.

- **Revision 319 — no text in the main window is cut off (§6.8):**
  - An empty rung's step read "Show source records (E", and the rail's card headings lost letters at its narrowest.
    Both wrap now, and a test walks every rung at the minimum window, at both rail widths, refusing text cut off by its
    box or a card, or trimmed with no tooltip to complete it.

- **Revision 318 — text a person reads names the product, not the plan (§20.4):**
  - "Captured before revision 255", "the M0 plan", "this milestone" and "(IC-005)" reached the align dialog, `icat
    session`, `icat exchanges`, the capability report and the source catalog. They now say what they mean, and a test
    refuses a string in the product that cites a revision, a milestone or a backlog item; the build's qualification
    tools are the stated exception.

- **Revision 317 — shown bytes keep room in the smallest content viewer (§3.7):**
  - At its minimum size the viewer's facts left a shown message two lines of hex. While the bytes are shown, the facts
    now scroll and give up as much room as leaves the bytes ten lines; before then, and in a taller window, they keep
    all they need.

- **Revision 316 — I10, P9 and P5 asserted (§13.5):**
  - Three rules listed as uncovered now have tests named for them: a member's own durations are unchanged by every
    alignment revision (I10), no order is stated within the combined uncertainty, even at its edge (P9), and two records
    at one instant with one size count twice (P5). R4's stale reason now says what a relationship still lacks.

- **Revision 315 — a release outlives the generation that made it (IC-016, store-v1 §8):**
  - A later generation, such as a checkpoint, left a retention record behind: a record's content said only that a
    retention "may have" released it, and re-derivation looked possible until it had read every segment. Every later
    generation now carries the latest journal and content release with the generation that made it, and a manifest
    that carries none is unchanged.

- **Revision 314 — a damaged session says what changed, once (§20.4, store-v1 §6):**
  - One changed byte was refused with four 64-digit digests and store jargon, repeated for the generation kept to fall
    back to. A reason now names the file and what is wrong with it - "does not match what generation 2 records (its
    SHA-256 begins d678662e9244, not 2d7f44f53556)", its length, or missing - and a refusal says once that the fallback
    needs the same file. A workspace member that fell back is no longer said to be a copy put in its place.

- **Revision 313 — a report's values line up (§20.4):**
  - A label longer than the 22-character column pushed its value past the others; twelve labels in seven commands did.
    Fields written one after another are now one group, whose values start two spaces past its longest label, and the
    group is written out before whatever follows it.

- **Revision 312 — a wrong input is named for what it is (§20.4):**
  - A folder that holds no session was refused with 2, 3, 4, or a warning and 1, by command; every command now refuses
    it in revision 310's sentence and with 2, as naming a folder that does not exist is. `icat export` reported a damaged
    session as a capability failure and now reports it as corrupted input, as every reader does. `import` and `verify`
    given a folder now say it is one, and `verify` says a session is verified whenever it is opened.

- **Revision 311 — a hand-edited file that cannot be read says where to look (§26.3):**
  - A workspace or settings file gave the JSON parser's words, or named an internal type; it now gives the line and
    character where its text stops being JSON, the fields it lacks or holds that this version does not know, or the
    path of the value that does not fit.

- **Revision 310 — a reader writes nothing into a folder that holds no session (store-v1 §8):**
  - `icat session ~/Documents`, and the Desktop's open, left an evidence-lease lock in the folder; a reader now makes
    a missing guard only where a session pointer is, and every reader refuses a folder with none in one person's
    sentence.

- **Revision 309 — `icat --help` names every form of every command (§20.4, R18):**
  - The summary had fallen behind: `--part`'s old rule, no `retain --release-content` or `package --original`, five
    of fourteen workspace subcommands. A test now holds it to each command's own help, and `icat capture` says it
    needs Windows elsewhere rather than calling itself unknown.

- **Revision 308 — the rail's empty state is §3.1's (R15):**
  - With no session shown, the saved sessions stand where the ranked table will be, under none of its chrome; the
    empty rung's card shows only while a capture is on its way; and the rail's actions scroll within what the list
    leaves them, so no card runs past the minimum window and the ranked rows keep a row or two in view.

- **Revision 307 — a deep rung fits the minimum window (R15):**
  - Back names the rung it returns to by its kind, "Back to Channel (Esc)", with the rung in full as its tooltip and
    help, so the title keeps its words; the rail's badge wraps under its heading rather than over it; and the
    timeline's peak rate stands above the plot, never over a bar.

- **Revision 306 — kept content released on its own (IC-016, content-v1 §2):**
  - `icat retain <session> --release-content` measures, and with `--confirm --reason` performs, a release of every
    content chunk: a `Content` retention keeps every journal, row and derived file, and is refused while the capture has
    not finished. Readers say the content was released, and when and why, rather than that none was kept; `icat
    session` states the generation's retention record, which it never did.

- **Revision 305 — a part that is not whole is shown with its gaps in place (M8, P2):**
  - The viewer's toggle and `icat content --part --reveal` show such a part buffer by buffer, each under a heading,
    with each gap a `--` line of its own: buffers never recorded, the rest of a cut buffer, a buffer kept without its
    bytes, each length stated only where a record states it. It is chosen by buffer, shown in hex alone at most 64 KiB
    at once, and never saved as one; `content-view-v1`'s part lists its gaps.

- **Revision 304 — a reader's refusal of a value reads as its own words (§20.4):**
  - `icat evidence --cursor abc` said "… restart from the first page. (Parameter 'cursor')", .NET's name for the
    code's variable; the command line now prints a reader's refusal without it, in each place it reports one.

- **Revision 303 — every machine-readable answer names its contract (§20.4, R18):**
  - `icat profiles --json` and `icat metric --matrix --json` printed bare arrays that named no contract; they are now
    `capture-profile-catalog-v1` and `metric-matrix-v1`. A test reads every read command's `--json` and refuses
    anything but one object naming its contract.

- **Revision 302 — a refused argument says what it was (§20.4):**
  - An operand that starts with a dash, such as a note, was refused as an unknown option with no word of `--`; it now
    says to write it after `--`, and an operand past the last a command takes is an unexpected argument.

- **Revision 301 — an investigation keeps each session's ranking (§26.3, `workspace-v12`):**
  - A session opened from an investigation now gets back what its rows were ranked by, and whether per second, as
    it gets back its pins; a layout that keeps only a ranking is kept, and one that pins nothing and ranks by records
    is removed. `icat workspace show` states each layout's pins and ranking (`workspace-resolution-v14`).

- **Revision 300 — an option keeps its value wherever it is written (§20.4):**
  - 22 commands read their directory before their options, so `icat metric --metric observations <session>` looked
    for a session named "observations". Options are now read first everywhere, and the reader refuses an option
    read after an operand, so a regression fails at once. A negative number is an operand
    (`workspace view <ws> early -5 10`), and `--` begins operands that start with a dash.

- **Revision 299 — a refused invocation keeps stdout for the answer (§20.4):**
  - Every command printed its help to stdout after refusing an invocation, so `icat ... --json` handed a script
    usage text as its answer. What explains a refusal or failure now goes to stderr with it; `--help` still
    answers on stdout.
  - `InterCat.Cli.Tests` runs `icat` in-process and reads both streams and the exit code: the first tests of the
    command line itself, covering this and revisions 297–298.

- **Revision 298 — a refused metric request says what would complete it (`metrics-v1` §2):**
  - `icat metric --metric bytes-sent` was refused for naming no byte domain without saying which option names one.
    A refusal about one part of a request now names that part and the values it takes there, and `icat metric`
    says the option: "Name it with --byte-domain: TransportObserved or CompletedIo.", or "Leave out --side." where
    the metric fixes it or has none.

- **Revision 297 — an interval's length is a tick count (I3, `metrics-v1` §5):**
  - `icat timeline` and `icat metric --metric rate` failed with an overflow over an interval whose end is more than
    2^63 - 1 ticks after its start, and `icat export` described one in the wrong unit. A time range now refuses such
    bounds where it is made; the four commands that read `--interval` say it is too long, and a saved view or a
    persisted overview extent that wide is refused in words.
  - A clock interval converted from a presentation interval can still reach further: past the clock's range, or on a
    3 GHz counter. It is held as the widest range centred on the capture's epoch, which holds every reading a session
    admits.

- **Revision 296 — an interval past the clock's range answers (`metrics-v1` §5):**
  - An interval ending beyond every instant the clock can read failed every ranking and the interval count with an
    overflow. Its bound now lies after every reading, so the interval holds the readings within it. `icat metric`
    takes such a bound the same way, and refuses only one that leaves the interval holding nothing, saying so.

- **Revision 295 — a PID is written as the identity it is (§6.1, §6.3):**
  - The inspector wrote "PID 8,204" where every other pane writes "PID 8204", and a nameless process's node read
    "PID 100" over "PID 100". Both now read as the rest do.

- **Revision 294 — a placeholder too long for its field ends in an ellipsis (§6.8):**
  - Cut at the field's edge, the rail's search box read "Search names or PID (Ctrl+I"; every placeholder is now held
    to its field. The search box's tooltip says it whole, and Ctrl+F is its accelerator.

- **Revision 293 — the current rung's crumb fits the trail (§3.2):**
  - At the minimum width a channel's crumb was wider than the trail beside the header's actions, so the trail lost
    its start ("nel: RPC calls to svcctl…"). It is now narrowed to the trail and ends in an ellipsis.

- **Revision 292 — a finished session's first byte view reads no segment (`overview-index-v1` minor 3, §6.1):**
  - Its checkpoint publication keeps each process's and TCP channel end's whole-session bytes before any evidence
    policy, summed in the pass that sums the lanes' bytes, and a reader applies the policy as a read would.
  - The ranked table and the graph under a byte ranking answer from them, equal to a read under every policy; an
    interval, a live session and one with more than 20,000 TCP channels are read as before.

- **Revision 291 — the warm aggregates' allocation test runs alone (R11):**
  - It failed now and then under the whole suite. Tests running beside it pushed its sessions' derivations out of
    the four the process's derivation cache keeps, and its warm queries then derived them again. It now runs with
    the cache's other tests, after the parallel ones.

- **Revision 290 — a process's HTTP exchanges in the timeline (§6.2, `http-exchanges-v1`):**
  - At the exchanges' rung each exchange is a bar from its first buffer to its response's end, in the HTTP hue when
    recorded whole and faint when not; past 4,000 in view, density columns with that share faint on top.
  - A bar's card states the exchange; a click selects its row. The RPC call lane's code now serves both lanes.
  - R11: the paint test measures each pane exactly, in one drawing context, to 8 B a frame where it allowed 32 beside
    an estimate. That found each operation lane's note formatted on every frame and the graph's byte weighting
    allocating its lambdas' locals on every read, at every rung; both now allocate nothing.
  - A toggle's high-contrast edge is read once its press has eased back; read part way, it blended into its ground
    and failed the test now and then in Release.

- **Revision 289 — the persisted overview keeps each lane's bytes (`overview-index-v1` minor 2, §6.2):**
  - A finished session's checkpoint publication sums each overview column's bytes per mechanism, and the machine
    rung's lanes under a byte ranking are answered from them, opening no segment; other columns are read as before.
  - The ranked table's per-process bytes are still read, so a reopen's first byte view reads once where it read twice.

- **Revision 288 — a background read is answered after the step that asked for it (§6.4):**
  - A read that had finished before it was awaited was applied inside the setter or publication that started it,
    skipping the "reading" or "updating" state it had just published; three tests of that state failed whenever the
    read won, 8 runs in 8 under load for one. Every evidence read now answers on a later turn; 0 in 8 since.

- **Revision 287 — every chosen state from the tokens, in every mode (§6.1, §6.6):**
  - A selected row, a toggle or box that is on, a tab, a focused field, the text selected in it and a combo box's open
    list were drawn in the platform's accent colour, which no report measures: a selected row's muted ink read 3.7:1.
    Each now takes the tokens, a selected row ringed in the accent on the elevated face, and the control theme's
    accent is the action fill, so the platform's colour reaches no control.
  - A multi-selection's bar is drawn inside its row, which no longer moves; the per-second toggle's label is centred;
    in high contrast the toggle, the closed combo boxes and the check box are edged as buttons are.

- **Revision 286 — under a byte ranking a process's direction rows plot bytes (§6.2):**
  - Each source-direction row plots the process's own records' bytes of that direction per second, and the machine row
    every record's, in one pass; every rung the ranking orders now plots what it measures.

- **Revision 285 — under a byte ranking a group's process lanes plot bytes (§6.2):**
  - Each process lane plots its own records' bytes per second, and the machine row above them every record's, read
    in one pass over the columns the lanes were counted in; a process's and a channel's rungs still count records.

- **Revision 284 — under a byte ranking the machine rung's lanes plot bytes (§6.2):**
  - Each mechanism lane plots what the ranking measures per second, read in one pass for every lane over the columns
    it draws. A column whose records recorded no size is cross-hatched, never zero, and keyed in the legend; a lane
    with no sized record says "no sends" beneath its name, and other rungs' lanes count records and say so.

- **Revision 283 — `icat session` states the recording a whole session's rates divide by (`metrics-v1` §7):**
  - Beside its clock calibration, in the reader's culture, and in its JSON as native bounds and seconds; a capture that
    recorded no stop states none.

- **Revision 282 — `icat workspace show` states each session's layout (§26.3):**
  - Its text listed only what its JSON held of the pins an investigation keeps; it now says how many nodes each
    session has pinned, which the Desktop puts back. Revision 280 was checked live on a dense capture.

- **Revision 281 — M4's exit review (§14, M4):**
  - Everything M4 implements is done, and three of its four exit checks hold by test; the known two-host exchange
    waits on a second machine, and the review says how to run it.

- **Revision 280 — an investigation keeps the pins placed on its sessions' graphs (§26.3, `workspace-v11`):**
  - A session opened from an investigation keeps its pinned nodes there, one layout per member replaced as it changes,
    and gets them back when opened from it again; the status says so. `show --json` lists the layouts.

- **Revision 279 — an investigation an earlier version wrote keeps reading (`workspace-v10`, R22):**
  - Each kind of fact was refused in any version before the newest, so a v5 file with two anchors, a v9 file with
    notes and the like stopped reading once a later version appeared. Each is now refused only before its own version.

- **Revision 278 — `icat workspace show`'s overlaps name the generations they read (I16, `workspace-resolution-v12`):**
  - The last of an investigation's results to name its snapshot vector; one of unplaced sessions reads none, and
    says only that their overlap is unknown.

- **Revision 277 — an investigation's results name the generations they read (I16, §10.4, M4):**
  - Candidate joins and the merged time state their snapshot vector: each capture's one generation and its manifest's
    digest (`workspace-correlation-v4`); a timeline read that a recording member straddles is taken again.

- **Revision 276 — a capture names its boot and start from its first publication (`clock-calibration-v1` §1, M4):**
  - Its start's calibration is published with its first chunk that is not its last, and its whole one replaces it
    at the end, so a capture that ends early, as a killed broker's does, can still be aligned by its boot or wall clock.
  - A follower mirrors each calibration as it appears; `icat session` states a lone sample as the start alone.

- **Revision 275 — a live capture's coverage speaks for its whole recording (`coverage-v2` §2, R21):**
  - A capture that delivered through its stop records its epoch and stop readings in its ledger, and speaks for every
    reading between: a quiet start or end is covered, and a whole-recording rate states coverage, not "unknown".
  - Older ledgers, imports, and a capture whose delivery had to be ended first stay bounded by what they delivered.

- **Revision 274 — the whole session's time scope is its recording (§5.2, `metrics-v1` §7):**
  - What revision 272's live UI check found: the inspector stated the span of the records beside a rate over the
    recording, and the per-second choice still said a whole session states no interval. Both now name the recording.

- **Revision 273 — a capture delivers its last second (`coverage-v1` §3, R8):**
  - A stop now stops its ETW session before it ends delivery: ETW hands the session's remaining buffers to the pump,
    which returns once it has delivered them. Ending delivery first had discarded them, counted by no loss counter.
  - The stop's own answer gives the final counters: the events the session lost, and the real-time buffers lost.

- **Revision 272 — a whole session's rates divide by its recording (`metrics-v1` §7, `clock-calibration-v1`):**
  - A capture's recording runs from its epoch to the stop reading its clock calibration records, widened to hold every
    timed record. Per second at the whole session, and `icat metric`'s rate without `--interval`, divide by it.
  - The stop reading is now taken as the capture asks its session to stop, not once it has drained, a second later.

- **Revision 271 — saved views of the merged time (§8.4, `workspace-v10`):**
  - A named interval of the investigation's time, kept as revisions in its reference's clock and never shown on another;
    `icat workspace view` and the timeline's Views… save, show and remove them.

- **Revision 270 — notes on an investigation (§8.4, `workspace-v9`):**
  - Kept as revisions, about the whole or pinned at a session's instant, which the timeline marks and places; `icat
    workspace note` and the window's Notes tab add, reword, remove and show them. Timeline reads no longer drop requests.

- **Revision 269 — candidate joins mirror through known address translations (§8.3, ADR-041, `workspace-v8`):**
  - A person states that an endpoint one capture sees is one the other holds; candidates mirror through it, never for
    loopback, and say they rest on it. `icat workspace translate` and the window's Known translations… make it.

- **Revision 268 — the merged time zooms, and a column opens in InterCat (§8.2):**
  - Buttons, the wheel and + / - zoom around a column cursor the keys move; Enter opens that column's session in the
    main window, zoomed there with its interval selected - only what the session holds of it, and never an empty one.

- **Revision 267 — the investigation window compares two instants (§8.2):**
  - Compare instants… on the Timeline tab places an instant of each of two sessions in the investigation's time, or says
    why it has none, and states their order only as their alignments allow - as `icat workspace compare` does.

- **Revision 266 — a person confirms two host identities are one host (§8.3, ADR-038, `workspace-v7`):**
  - Kept as revisions, transitive while in force, never evidence: captures of confirmed identities are compared as one
    host's - overlaps, loopback candidates - and say so. `icat workspace same-host` and the window's One host… make it.

- **Revision 265 — a session is aligned through another (§8.2, ADR-039, `workspace-v6`):**
  - A member may be aligned to any placed member and is placed through both; two members aligned through one compare as
    that shared alignment allows - its drift between them and their roundings, never twice its bound - so two captures
    of one boot compare to the nanosecond. Decisions record every alignment on both chains.

- **Revision 264 — two instants measure the clocks' rate (§8.2, ADR-039, `workspace-v5`):**
  - A person's alignment may take a second instant; the line through both is the mapping, its bound growing beyond them
    and a stated wander growing from the nearer. The timeline places lanes through the mapping, as uncertain as their
    widest instant. Version 4 files still read, their join decisions included, which the bump had nearly refused.

- **Revision 263 — an investigation is packaged with its sessions (§8.4, ADR-042):**
  - One new folder: the investigation's file, and beside it an original evidence package of each chosen session, named
    relative to the file. A session not where it was last found, or not chosen, stays a reference to relink.
    `icat workspace package` and the window's Package… make it, saying first what it exposes.

- **Revision 262 — two captures of one host that ran at once are flagged, never merged (§8.4):**
  - Every pair of one host identity is compared by its extents in the investigation's time: ran at once, may have,
    unknown, or - two boots seeming to overlap - contradictory, which says an alignment is wrong. `show` and the window
    say each in words. Live, two concurrent captures read "ran at once for 3.0 s" once aligned by their boot.

- **Revision 261 — the merged time (§8.2, M4):**
  - The investigation window's Timeline tab draws each placed session as a lane on the investigation's own axis, its
    records counted over shared columns mapped back into its own time, and states each lane in words: its records,
    where they fall, and how sure that is. Live, two captures aligned by boot drew at 0-3.0 s and 5.93-8.92 s.

- **Revision 260 — a person's decision about a join is a revision, never evidence (ADR-041, `workspace-v4`):**
  - `icat workspace join <n> --accept | --reject | --withdraw`, and a button each in the window, record a decision with
    the alignments in force; a candidate says what a person decided, and "to review" once either alignment changed. A
    decision whose pair is no candidate now is said, never dropped.
  - §21.1's scenario is asserted: B's receive 0.2 ms before A's send within 3 ms is ambiguous, never a latency.

- **Revision 259 — aligning and candidate joins in the investigation window (ADR-039, ADR-041):**
  - An Align dialog offers the three ways - by boot, by wall clocks with a stated agreement and drift, by an instant read
    in both - and says in words what is missing or refused; Withdraw keeps the revision. Candidate joins list on request,
    each with its evidence, none established. One parser reads durations, seconds and rates for both front ends.
  - Live, the dialog aligned two captures by their boot; a byte statement "received 0 B in 0" now says no receive.

- **Revision 258 — a join across captures is a candidate, never established (ADR-041):**
  - `icat workspace correlate` compares every member's one-sided connections: mirrored endpoints of one protocol whose
    lifetimes overlap in the investigation's time within their uncertainty, or cannot be compared, which is said;
    loopback only within one host. Each candidate lists its evidence, bytes both ways, and its alternatives.
  - One machine cannot capture an exchange's ends apart, so the rule is proven on synthetic sessions; live it proposed
    nothing between two real concurrent captures, in 0.4 s. A known two-host exchange needs a second host.

- **Revision 257 — an investigation in the Desktop (ADR-038):**
  - The start page's Investigation button opens or starts one, and an `.icat-workspace` on the command line opens it,
    in a window of its own: each session where it stands, its host, its time in words and why it is not present.
  - Enter or Open shows a session in InterCat's window; Relink and Add sessions keep the investigation whole, each
    refusal said. Live, its rows read to a screen reader as sentences, and Open showed a member in the main window.

- **Revision 256 — a workspace aligns captures by what they recorded (ADR-039 decision 7, `workspace-v3`):**
  - `icat workspace align --same-boot` aligns two captures that recorded one boot's token exactly through their epochs:
    live, the second's start fell 5.0632029 s into the first, as their 50,632,029 ticks say; another boot, no
    calibration or no boot token is refused.
  - `--wall-clock` anchors on the two samples taken closest in wall-clock time, bounded by their acquisition, the wall
    clocks' agreement a person must state, and a stated drift; live, its anchor agreed with the exact one to the tick.

- **Revision 255 — a capture records its clock against the wall clock, and its boot (ADR-040, `clock-calibration-v1`):**
  - At start and stop a live capture pairs the performance counter with the precise wall clock, each pair bracketed so
    its acquisition uncertainty is measured (±200 ns here), and names its boot by a random token kept in a volatile
    registry key Windows deletes on restart; the boot count, which a clone shares, is kept only for people.
  - It is capture evidence of its own kind (code 10), published with the last chunk, carried by later generations and
    followers, never released, never in a redacted package; `icat session` shows it and the wall clock's rate.

- **Revision 254 — an alignment is a bounded statement, and an unknown bound orders nothing (M4, ADR-039):**
  - §8.2's model in `InterCat.Domain`: an affine clock mapping with named contributions - bounds added, measured parts in
    quadrature, one unknown making the whole unknown - and an order between two clocks' instants stated only beyond the
    pair's uncertainty. §8.2's pair formula counted the systematic part twice; it now names each side's two parts.
  - `icat workspace align` records a person's statement that a member's instant is the time reference's, within a bound
    and an optional drift bound, as a kept revision of the file (`workspace-v2`, which reads version 1); `compare`
    states an order, an ambiguity, or nothing - not even the difference - with the reason. A stated bound is rounded up.

- **Revision 253 — an investigation references sessions it never changes (M4, ADR-038, `workspace-v1`):**
  - `icat workspace new | add | show | relink | alias` keeps an `.icat-workspace` file naming separately valid sessions
    by identity: the session, the capture its journal records, the generation selected and its digest, and its source
    clock's host, clock and epoch. Showing it opens each session as a viewer does and writes nothing; each member is
    present, advanced, replaced, missing, different or unreadable, with the reason, and only a relink to the member
    itself selects what is there.
  - A store's source identity names no capture - a broker capture's is its plan's digest, an import's its file's path -
    so members are keyed by the journal's capture identity: a copy, or a second import of one file, is refused, and two
    captures under one plan are two members.
  - A live capture's host identity was its machine's name and build alone, which P6 forbids: it is now derived from the
    installation's machine GUID with the name and build, so two machines of one name are two hosts. Captures recorded
    before keep their identities.

- **Revision 252 — the M3 exit review (`docs/reviews/M3-exit-review.md`):**
  - Each item of M3's exit gate checked against evidence that names it: RPC over ALPC adds no volume and counts calls,
    not the records beneath them; pipes and sections are unavailable and say so; content truncation and HTTPS's
    plaintext state are measured and stated; per-build coverage is published. The gate is met for its measured scope,
    and M4, multi-machine investigation, is next.

- **Revision 251 — per-build coverage, published and stated (M3's exit gate, §13.5, P27):**
  - `icat capabilities` said every mechanism was "Unsupported - no capture has measured it", TCP and HTTP included, on
    the very build their fixtures measured: the probe measures nothing, and the fixture index held its tiers only in
    prose. Each fixture now names its mechanism and each environment entry its tier, which a test holds to the prose.
  - The latest evidence of each mechanism on each build is projected into a file every InterCat build embeds and a
    page, `docs/PER-BUILD-COVERAGE.md`, both checked against the index. The capability report states a tier measured on
    its own build - TCP and UDP TrafficVisualization, RPC and HTTP ExperimentalEvidence, named pipes Unsupported - and
    names another build's evidence as another's.

- **Revision 250 — a redacted package keeps an HTTP exchange's shape (`redacted-session-v1` §4, I22):**
  - A live pass over sharing a fresh content session found the redacted package sound - no content file, no reference
    to one, the export carrying no byte, the original package stating the 287,837 kept bytes it holds - but its HTTP
    records grouped into no exchange: the policy withheld the fields no rule named, and `icat exchanges` then blamed the
    capture.
  - A buffer's place and ends are kept now, and its exchange number becomes a pseudonym of its own namespace, never a
    source value, so the package's exchanges group as the source's: 16 of 16 whole, the same bytes and median, none of
    the content. The policy's list of kept fields also names the ALPC message id it always kept.

- **Revision 249 — every evidence rung's timeline counts its own records apart (R21, §6.2):**
  - A one-sided connection's records, an HTTP exchange's buffers and an RPC channel's or call's records are the focus of
    the timeline beside their evidence, as a process's or a paired channel's were: a connection by its channel number,
    the others by the very records their evidence reads (`RecordsOf`), so the two never disagree.

- **Revision 248 — a process's connections to other hosts on its rung (`relations-v1` §5b):**
  - A live pass over a real 30-second Explore capture found chrome.exe, with 460 TCP and UDP records, saying "Nothing at
    this level": the rung listed paired channels only, and on a real machine most connections go to other hosts, whose
    end no record holds.
  - Such a connection is a row of its process's rung now - "→ 3.72.134.85:443", TCP or UDP from its own endpoint, how
    the capture saw it open and close, and the bytes its sends and receives measured - ranked with the paired channels
    beside it, and Enter opens its records. `icat channels --process <id> --one-sided` lists the same (R18).

- **Revision 247 — a process's HTTP exchanges on its rung (M8, `http-exchanges-v1`):**
  - A content session's process rung lists its HTTP exchanges as a row, where it said "Nothing at this level"; Enter
    lists the exchanges, each leading with how long it took and saying what was recorded of its request and response
    heads and bodies; Enter on one opens its buffers, where C shows a buffer and its whole part. `icat exchanges` lists
    the same (R18).
  - An exchange is a use of its client's number (`http-exchange-v1`): a new use opens at a request head flagged first,
    or at a buffer that repeats a place the use holds. The live session showed why nothing else may end one: WinINet
    raises a request body's closing buffer after the response has ended, and a first rule read 96 exchanges as 191.
  - `EvidenceScope.RpcKey` is `OperationKey` now: it names an RPC channel or call, or a process's HTTP exchanges or
    one exchange, and an evidence cursor names the rule its key reads by.

- **Revision 246 — a small session's persisted overview is read, not refused (`overview-index-v1` §3):**
  - A live pass over a fresh content session found `icat overview` calling its persisted overview "not readable: a
    count of 1,061 is more than the 77 bytes left can hold". The reader checked the minimap's and the timeline's
    column widths as counts of the fields after them; a few records over a long extent fill few of many columns, so
    every small session's overview was refused and counted from its segments instead, and a small RPC peers session
    lost revision 229's persisted links. The widths are now compared as widths.
  - The same pass found a content session's busiest process "Nothing at this level" on its rung, its HTTP exchanges
    reachable only as a flat list of buffers: next.

- **Revision 245 — a content capture's scope held to the named processes themselves (ADR-037):**
  - A process filter holds process IDs, so a named ID given to a new process mid-capture would have brought that
    process's HTTP messages into scope. Each session now holds every process its filter names open while it lives,
    which keeps Windows from giving the ID to any other process; a process not running, or already exited, is refused.
  - `icat record` names each process it keeps content from, with its image and start, refuses one that is not running
    before anything starts, and states what it collects before it records, as a preview does (§11.1).

- **Revision 244 — WinINet exchanges at once, chunked responses, and an exchange's number told apart in time
  (ADR-037, FX-HTTP-003):**
  - Eight clients in one process keep their exchanges apart: each keeps one number, its parts numbered and flagged,
    and every part matched, in the probe and through `icat record`. A chunked response body is kept as its client read
    it, without the chunk framing, which only its head names.
  - WinINet numbers a process's exchanges from 1, so one number can name two exchanges in a session - a process ID
    used again, or WinINet loaded again. The part query now tells two uses of a number apart in time and never merges
    them; before, their buffers sorted as 0, 0, 1, 1 and passed as one whole part (R22).

- **Revision 243 — HTTPS through WinINet's capture, measured (ADR-037, FX-HTTP-002):**
  - Over TLS the capture holds the plaintext: all parts matched the bytes the server decrypted and encrypted, and no
    buffer began as a TLS record does. Nothing in a record says whether its exchange was encrypted. The impact stays
    Low (a median 0.74 CPU pp).
  - A WinINet content request now says, before it records, that an HTTPS exchange is kept as its plaintext, headers,
    cookies and authorization included. The viewer and `icat content` state an application payload's encryption fact:
    the message as the application held it, above any encryption of its connection.
  - The request's inspection disclosure and the preview contract no longer deny what revisions 239 and 242 do: a
    WinINet request can start, and joining a part's buffers, copying and saving are deliberate actions of a person.

- **Revision 242 — an HTTP part reassembled from its buffers (M8's first step, `content-v1` §4):**
  - A WinINet buffer's record says which buffer of which part it is - a request or response head or body - and
    whether the part's buffers, from the one flagged first to the one flagged last, were all recorded and kept whole.
  - Only such a part is shown, copied or saved as one: the viewer's toggle, `icat content --part`. A part missing or
    cutting a buffer names what it lacks and is shown one buffer at a time (I21, P2).

- **Revision 241 — M3's content fixtures in the traceability matrix (§13.5):**
  - FX-CONTENT-001 and FX-HTTP-001 join `fixtures/index.json` with committed evidence: truth logs of lengths and
    SHA-256, never bytes, and a fresh `icat record` measurement of each, counters only.
  - The WinINet tests now carry the contract items they assert - R17, P15, P28 - and a recording test keeps HTTP
    buffers with their exchange, place and ends (I21). P15 and P28, uncovered until now, are covered.

- **Revision 240 — a covered capture's quiet interval reads as quiet (R21, §6.6):**
  - The machine timeline, a focus's timeline and the overview judged a bucket with nothing observed as unknown, so a
    scoped capture's evidence rung hatched nearly every interval and its status counted them as coverage-unknown.
  - An empty bucket now takes the capture's own coverage there, as lanes and direction rows do since revisions 165,
    197 and 206: quiet where it covered, a gap where it lost records, unknown past its readings or in an import.

- **Revision 239 — a capture keeps HTTP content from the processes it names (ADR-037, `content-v1` §5.1):**
  - WinINet's capture measured Low: a median 0.71 CPU pp and 2.5% of the workload's time over seven pairs. A bounded
    Content request for it compiles to `scoped-content-request-v1`, its provider enabled for the named processes alone.
  - A source that cannot select channels is requested with `*`, every channel of the named processes; `icat record
    --profile content` records it, and the broker previews such a request and never starts it.
  - Live: 32 exchanges, 229 buffers kept whole; all 128 parts matched the wire's SHA-256, all bound to the client.

- **Revision 238 — HTTP records bind to their client (ADR-037, ADR-030, `process-binding-v4`):**
  - A new mechanism, `Http`, and WinINet's capture source in the catalog: its four events are HTTP messages sent and
    received, their exchange, buffer order and ends kept as source fields 16 to 18, their length measured.
  - `process-binding-v4` binds an HTTP record to the client that raised it; a v2 or v3 checkpoint is read as v4's
    wherever every record named its owner. A scoped content policy now names the sources it keeps content of.
  - No profile captures the source yet: its admission is revision 239.

- **Revision 237 — M3's content-capable source, measured (ADR-037, §11.2):**
  - WinINet's own capture provider records each request and response head and body of an HTTP exchange. A lab probe
    measured it against FX-HTTP-001, whose loopback server logs every part it received and sent by length and SHA-256.
  - Four runs of 16 and 64 requests: every part matched the wire; session id, sequence number and flags state the
    exchange, buffer order and boundaries; the client raised every record; a process filter kept a decoy out.
  - Admitted to no profile yet; the admission - scope required, owner binding, overhead - is the next slice.

- **Revision 236 — a person inspects kept content (§3.7, §11.2, ADR-036, `content-v1` §4):**
  - C, or "Inspect its content", opens a record's content: its facts first - what it is, its source and declared
    encoding, which bytes were kept and which are missing, not reassembled, the policy - and its bytes only on request.
  - A typed range shows at most 64 KiB as inert hex with ASCII, and declared text with controls made visible; Copy as
    hex, Save writes the bytes as they are. Without consent nothing is shown. `icat content` does the same.
  - The inspector's size says what it measures in words ("604 B of the application's own message").

- **Revision 235 — a capture keeps content, from InterCat's own fixture (ADR-036, `content-v1` §5, §11.2):**
  - FX-CONTENT-001 raises `InterCat-Fixture-Content`, an EventSource whose layout the catalog reads from its type;
    `icat record --profile content-fixture` admits its messages under `scoped-content-fixture-v1`, copies at most
    4,096 bytes a record in the callback, writes them as a chunk with each publication, and stops at 16 MiB.
  - Every other source keeps metadata only. The broker neither offers nor records the fixture. Its payload names its
    own process, so ADR-030's header binding stays RPC's alone. `icat session` states a session's content in sum.
  - Live: 24 and 8,000 messages matched the truth's lengths and SHA-256; the second stopped at the limit.

- **Revision 234 — kept content is restricted evidence beside the journal (ADR-036, `content-v1`, §11.2):**
  - A `Content` chunk (store code 9) holds a journal chunk's records' kept content: each fragment's classification,
    direction, declared encoding, lengths and how it was cut, under the chunk's record limit and inspection consent.
  - Carried by every later generation, released only with its journal chunk; the inspector and `icat raw` state it,
    exports never read it, the original package discloses it and a redacted package leaves it behind. No capture
    writes one yet.

- **Revision 233 — a record says why it holds no content (§3.7, §11):**
  - The evidence inspector, the original record's window and `icat raw` say what a record holds of its message: none,
    since its source carries endpoints and a size, a call's interface and procedure, or a message id; and which
    source could hold the bytes when one is known ("A packet capture could hold them").
  - The statement stands apart from the retained event body, which is the event's own fields, not the message.

- **Revision 232 — the inspector reads a chosen channel or call in full (§6.7):**
  - A chosen RPC call, RPC channel or channel row, which the rail cuts short, heads the inspector: its name, its
    whole detail - who served a call, or why no one is known to have - and what Enter and O open.
  - A process, group or aggregate chosen afterwards takes its place; the process's lineage steps aside meanwhile.

- **Revision 231 — Esc lands on the call it came up from (§3.2):**
  - Climbing from a call's records to its channel reads the channel's calls through the page that holds the call and
    selects it, up to the 5,000th call; before, the channel came back at its first page with nothing selected.
  - A brush that still holds the selected call keeps it listed and selected. Live, O and a served call's menu went
    from a client call to the call that served it and back, each Esc landing on the call it left.

- **Revision 230 — open the call at an RPC call's other end:**
  - On a channel's rung, O or a linked call's row menu opens the call its link names: the ladder runs through that
    process to its own channel for the interface and lands on the call's records; Esc climbs one rung at a time.
  - A call no link reached offers neither; a linked call's spoken name says O opens its other end.

- Revisions 104–229 are in [the historical log](history/IMPLEMENTATION-STATUS-revisions-104-to-229.md), whole and in order.

## Open work, dependency order

0. Done: what revision 165's live test found. Revision 166 ranks L0 and L1 by each process's own records, and revision
   167 gives process and thread lifecycle a palette family of their own. Revision 197 fixed what a live pass on a dense
   capture found: coverage and bytes stated where they are known, and channel and relationship rows that read apart.
1. Keep large sessions inside their budgets. Revision 129 did this for the default 10-minute capture.
   - Revision 156 took the per-row relation lookups out of the overview.
   - Revision 157 extends each live generation's derivation from the previous one's.
   - Revision 158 serves the overview, zoomed detail and the minimap from each segment's tiles, built once per reader
     (S4's first level, in memory). At 1M rows a live generation now projects in 8 ms, and the same generation again in
     1 ms.
   - Revision 162 reopens a finished session from its derivation checkpoint rather than deriving from every record.
     A 10M-row session reopens in 0.62–0.65 s, down from 2.2–2.4 s, and holds 223 MiB, down from 787 MiB.
   - Revision 163 persists the whole-session overview beside it, S4's top level, and a finished session's first view
     opens no segment: 4M and 10M rows both reopen in about 0.17 s, holding 37 MiB.
   - Revision 164 qualified both on real ETW and in the window. A 10-minute capture ends by publishing them, and its
     saved session reopens without opening a segment. The window opens a 1M-row session from them in 27 ms to laid
     out.
   - Revision 168 measured §12's query gates at 1M and 10M rows (`ScaleGateTests`). The reopen, the L0 timeline and
     the whole-session ranking meet them with room; the window opens a 10M-row session in 57 ms to laid out.
   - Revision 169 met every one of them at 10M rows on 16 threads: a brushed ranking in 197 ms, a group's lanes in
     20 ms, first evidence pages in 35 ms. **Next:**
     1. A brush's first touch of a segment derives its bindings: most of what remains of its p95. Publishing the
        bindings, or per-tile owner and channel counts, with the checkpoint would take it away, and is what 100M rows,
        the scale tier, needs anyway (S4's pyramid).
     2. Done in revision 210 as far as the lanes go: past the 20,000-cell bound a group's lanes are counted in the
        columns it allows (§12's 2,000 × 40 query answers with lanes at 500 columns). Lanes at every view column for
        large groups wait on §6.2's density regime.
     3. The working set still ends near 1.2 GB at 10M rows, where 284 MiB stays reachable.
   - A live session still counts its overview from tiles until its writer finishes.
   - A zoom still builds a segment's tiles from its rows when first drawn. Persisting them is the pyramid's next
     level. Since revision 400 it opens and tiles only the segments its interval meets, by the readings their headers
     declare: a hundredth of a reopened 1M-row session in twenty segments drew in 3-17 ms, from 86-237 ms. Since
     revision 401 a brush's counts, its lanes' bytes and a time scope's byte and peer rankings do too, with the
     checkpoint's instances and channels; since revision 402 an evidence page opens only the segments it reads and
     stops past its time scope, and since revision 403 a process's connections read only the segments their lifetimes
     meet. Since revision 408 the call ranking and a process's RPC and HTTP listings read the calls and exchanges once
     paired or grouped, opening no segment, and a call's or an exchange's evidence opens only the segments holding its
     records. Since revision 412 the generation keeps an operation's records by segment, and a page or a zoom of them
     reads their rows alone: a busy channel's zoom takes 4 to 9 ms after the first, where it took 123 to 167. An RPC
     capture's first brush, ranking or listing still pairs its calls over every segment, since a call's request and
     response can lie in different ones, and nothing persists the pairs (P25). A search reads the overview, never a
     segment.
   - A focused count still reads its rows (§10.3: a filter is not what tiles hold).
   - Since revision 204 metric queries (`icat metric`, `icat processes`) take the checkpoint's derivation. What remains of
     a query's time at 1M rows is opening and verifying the store (0.86 s) and binding each row.
   - Revision 206's interval bytes read the listed rows once, when the table is shown: about 70 ms at 1M rows.
   - Viewer memory is bounded by the reader cache since revision 161 (S2), and a reopen fills it only as views read.
   - **Measured next step:** a warm query re-reads and re-verifies every segment the reader cache cannot hold. In a
     sampled trace at 1M rows under the old 64 MiB budget, opening segments took 13.7% of the samples, 8.7% of them the
     whole-file SHA-256, and column checksums took 8.8%, nearly all on a column's first read after an open. Revision
     148 raised the budget to 256 MiB, so a million rows (169 MiB) now fit, but the cliff returns near 1.5M rows. The
     structural fix is **column-granular reads**, in three steps:
     1. Done in revision 149: segment-v1 minor 1 gives the directories and the variable chunk checksums of their own,
        so a reader can check every byte it interprets without hashing the whole file. Revision 150 made each check
        about 18 times cheaper. A miss now costs mostly the whole-file read and its SHA-256.
     2. Done in revision 151: a published minor-1 segment opens by its header, directories and time column, and reads
        every other column from the file on its first read. A minor-0 segment is still read whole. Mapping files instead
        would fight retention, since Windows will not delete a mapped file.
     3. Done in revision 161: the cache charges what a reader holds. When a lease ends over budget, readers give back
        every column but session time and mechanism until the charge fits. A live generation at 3M rows went from 64
        to 9 ms, and a 4M-row session keeps all 16 of its readers where it kept 6.

     Decided in revision 152: a viewer no longer hashes a session before its first view (store-v1 §6). It hashes
     afterwards, and falls back and says so when a file changed. The command line and writers still hash at open.
2. Run a real screen reader (Narrator and NVDA) over the Desktop on Windows. Revision 131 audited the automation tree
   headlessly; it cannot hear what a screen reader says. Revision 214's live pass found that Avalonia's menu items
   expose no UI Automation Invoke pattern: the keyboard reaches them, but voice control may not, so check it there. A
   timeline status that changes with the view is read when asked for, not announced; check whether a zoom needs a live
   announcement. Since revision 426 the search's status is a polite live region, and the timeline's status names its
   analysis interval. Collapsing a group's lanes is done: pinning in revisions 387 and 388, drawing only the rows in
   view in revision 389, finding lanes by search in revision 390, and folding a group's least busy members past the
   200-lane bound into one exact lane in revision 420. L4's operation lanes, with duration bars and byte projections
   where a derivation supports them, wait on item 3's operations; L5 keeps its marks.
3. Continue M1's IC-015 operation/topology derivations and IC-016a checkpoint without inventing unsupported
   mechanism facts. Then resume the remaining milestone and retail-build gates from the plan.
   - **RPC operations (M3).** Revision 176 measured RPC's capture cost (Low), revision 177 binds an RPC record to the
     process that raised it (ADR-030), revision 178 pairs its calls (ADR-031, `icat operations`), revision 179 puts a
     process's RPC channels and their calls on the ladder, revision 180 admits RPC to Explore, 32-bit callers included,
     revision 181 draws a channel's calls in the timeline, and revision 183 counts them on the logical-operations
     metric basis (ADR-032). Next, in order:
     1. Done in revision 186: durations of client calls and server executions by cohort and statistic (ADR-033).
     2. Extend the calls between live generations, then keep them with the derivation checkpoint, when a real capture
        needs it. Revision 184 made pairing 3.7 times cheaper; a live session with its RPC rung open still pairs every
        call again at each generation (1.7 s at the end of a 10M-record session with 2,000,000 calls, tens of
        milliseconds at this workstation's real rate), and a reopen's first RPC rung pairs them all (1.8 s there, 0.33 s
        at 200,000 calls). Exactness needs every key a late record touches re-paired, and the grouping still reads every
        call.
     3. Resolve an RPC call's other end through ALPC (ADR-034) as an opt-in profile - collection alone measured Moderate
        in revision 188, so never Explore - in order: done in revision 218, ADR-035 for a private, uniquely named, owned
        system logger in the capture's one session, checked in the product's session conditions; done in revisions
        220 and 221, admission for ALPC's classic send and receive by opcode, from the class's registered layout; done
        in revision 222, the owned session's kernel flags, first; done in revision 223, the opt-in RPC peers profile
        that admits them and the ALPC record's normalization, with a ledger that names a classic descriptor by opcode;
        done in revision 224, its cost through the product, which states its sources' Moderate; done in revision 225,
        the relation rule (`rpc-call-peer-v1`, operations-v1 §5c) in `icat operations`; done in revision 226, other ends
        on the RPC rung; done in revision 227, RPC edges in the graph for a session that collected ALPC, counting call
        records and never ALPC's (§5.1, M3's exit gate); done in revision 228, peers counted from the links
        (metrics-v1 §8a); done in revision 229, the links kept with the persisted overview, so an RPC peers session's
        first view opens no segment either; done in revision 230, the call at a call's other end opened from its row.
     4. Name more RPC interfaces. Only interfaces a protocol specification names get a name; a real session's busiest
        (COM's `00000134-…`, `e60c73e6-…`, `00000136-…`, `00000132-…`, the DHCP client's) are named by none, so a name
        for them needs another source of truth than a guess.
     5. Done in revision 185 for the call lane: a view denser than its budget is drawn as density. The other lanes'
        §6.2 density regime remains (item 5).
   - **Content (§11, M3).** Revision 233 says per record why it holds none and which source could; revision 234 keeps
     content beside the journal (ADR-036, `content-v1`); revision 235 captures it from InterCat's own fixture through
     the `content-fixture` profile; revision 236 shows it in the bounded hex and text viewer (§3.7); revision 237
     measures M3's content-capable source, WinINet's capture provider (ADR-037); revision 238 puts it in the catalog and
     binds its records to their client (`process-binding-v4`); revision 239 admits it under a Content request, through
     `icat record`, scoped to named processes; revision 241 puts FX-HTTP-001 and FX-CONTENT-001 in the fixture index.
     Revision 242 reassembles a part from its buffers for a person (M8's first step); revision 243 measures HTTPS
     through it (FX-HTTP-002): kept as its plaintext, which a request says first; revision 244 measures exchanges at once
     and chunked responses (FX-HTTP-003), and tells a reused exchange number's uses apart; revision 245 holds a
     request's scope to the named processes themselves; revision 247 puts a process's HTTP exchanges on its rung, each
     with its parts (`http-exchanges-v1`, `icat exchanges`); revision 290 draws them as a lane in the timeline, as an
     RPC channel's calls are; revision 305 shows a part that is not whole with its gaps in place; revision 306
     releases kept content on its own. Later: HTTP/2 and compressed responses through it; a follower that mirrors
     content, so a broker capture could keep it; and a fixture decoder (§11.2's `DecodedFields`).

   - **One-sided connections (§7.1).** Revision 248 lists a process's connections no record's other end holds on its
     rung, and revision 249 counts its records apart in the timeline. Later: naming the other host where a source
     names it (a DNS name is content or another source's, never guessed).
   - **IPv6 beyond loopback.** Revisions 172–174 store, relate, redact, show and capture IPv6 endpoints, measured on
     `::1` (FX-TCP-002, FX-UDP-002; ADR-029). Still unmeasured: two-host IPv6 traffic, link-local addresses on several
     interfaces (a record carries no zone index, so two interfaces' equal addresses are one address to a relation), and
     IPv6 multicast.
   - **IC-016a** waits on a retention that releases observation segments: today only a journal prefix is released, and
     every derived segment, with every identity, stays.
4. §11.3's redacted packages above 10,000,000 rows: done in revision 397 from the command line, `icat package --redacted
   --interval`, which keeps every lifecycle record of the PIDs its interval's records belong to, so they keep their
   names and bindings, and whose coverage speaks only for the interval; done in revision 399 in the window, whose
   "Share redacted session…" offers its time scope. Revision 217 raised the whole package's bound from 1,000,000 by
   compacting the field join and verification, measured at 10M rows with a field each under a 1 GiB heap.
5. Interaction follow-ups with no dependents:
   - Per second over a whole session: done in revision 272. A whole session's rates divide by its recording, from its
     epoch to the stop its clock calibration records, widened to hold every timed record; an import, a redacted package
     and a capture before revision 255 record no stop and keep none. What it leaves open:
     - Coverage over the recording: done in revision 275, whose live epochs speak for the readings they recorded
       between (`coverage-v2` §2). A capture recorded before it stays bounded by what it delivered.
     - Its live check found that a capture never delivered its last second or so, uncounted as loss: fixed in revision
       273, whose stop stops the session before it ends delivery.
   - Qualify the **Other processes** remainder on real data when a naturally eligible capture exists. It is a budget
     fallback, covered synthetically; the dense capture never needs it.
   - Pins that survive reopening: done in revision 280 for a session opened from an investigation, which keeps them
     (`workspace-v11`); a session opened on its own keeps them while it is open. Sort, the ranking and whether it
     reads per second: done in revision 301, kept beside the pins (`workspace-v12`). Its evidence policy: done in
     revision 339 (`workspace-v13`). Each lane's own scale: done in revision 343 (`workspace-v14`). Since revision 347 the investigation's window lists
     what it keeps of each session, in the words `icat workspace show` and the notice use. The window's panes, its split
     and the pane filling the column: done in revision 360, once for every session of the investigation
     (`workspace-v15`). Pinned lanes: a control since revision 387, kept beside the pins since revision 388
     (`workspace-v16`). Lane grouping: a control since revision 405, by executable or by terminal session, kept beside
     the pins since revision 406 (`workspace-v17`), which `icat workspace show` states, and exported by `icat export
     --group-by session` since revision 407 as the window exports it. §19.5's view filter, InterCat's own processes
     set aside, is kept since revision 419 (`workspace-v18`), and the time base since revision 422 (`workspace-v19`).
     §6.1's column widths have no control in the window yet, so nothing of them is there to keep.
   - §6.7's table is complete since revision 160's multi-selection. What it leaves open:
     - a set of channels, which a timeline focus cannot name;
     - a lane view of an arbitrary set, which would need the graph to expand several groups at once;
     - (done in revision 205: the set's rows are marked in the ranked table itself.)
     Indexed/progressive search belongs to the later M4 scale gate.
   - §6.2's minimum drawn width (5 px) and pointer snapping belong with the density regime, where a column is one
     device pixel. Revision 154 recorded why they wait for it: today every bar's column, at least 5 px, is its pointer
     target, and snapping would take an empty neighbouring interval away from the pointer.
   - R11 beyond paint and aggregation: since revision 155 a test holds the window's four aggregate queries to no
     allocation per row, and since revision 291 it runs alone, where no other test can empty the derivation cache.
     The admission and decode loops keep IC-019's Windows allocation measurements and have no test that runs here. A
     pan still formats and lays out the ticks it draws, which §19.4 allows.
   - Theme modes (§6.1, §26.2, §26.3): light and dark follow the operating system since revision 138, and its
     high-contrast setting since revision 140; since revision 146 the user can choose one, kept in `app-settings-v1`.
     Revision 147 restated menus, tool tips, scroll bars and list selection in high contrast. Revision 287 draws every
     chosen state from the tokens in every mode, and sets the control theme's accent to the action fill, so the
     platform's accent colour reaches no control.
     - On Windows, check each of the system's high-contrast themes, and whether Avalonia reports light or dark for
       each as expected; and, with a vivid accent colour set, that it shows nowhere in the windows.
   - §6.6's unmeasured encoding is drawn since revision 207 where the graph plots bytes: a relationship or node none of
     whose sends measured a size is an open cross-hatched band or disc, keyed in the legend while drawn. Since revision
     284 the timeline's byte lanes draw it too, as an open cross-hatched cell at the lane's occupied floor. The ranked
     and interval tables state it in words.
   - §6.1's metric selector ranks the machine and group rungs by records, bytes sent or received (revision 189), or RPC
     calls made or served (revision 190), and a process's channels by its own bytes on each (revision 192), bytes sent and
     received and RPC errors (revision 196), RPC call and serve time (revision 199), and peers (revision 200); each count or
     sum reads per second over the ranked interval since revision 209. Since revision 284 the machine rung's mechanism
     lanes plot what a byte ranking measures, per second (§6.2), read for the columns they draw: the overview's once,
     and a zoomed view's own when it rests. Since revision 285 a group's process lanes do too, and since revision 286 a
     process's direction rows, each with the machine row above them. A channel's end lanes, which no ranking orders,
     count records. Since revision 289 the persisted overview keeps each column's bytes per mechanism, so a finished
     session's machine-rung lanes plot bytes without a read (overview-index-v1 minor 2), and since revision 292 each
     process's and TCP channel end's whole-session bytes before any policy (minor 3), so its first byte view - lanes,
     ranked rows, channel rows and graph - reads no segment. A brushed or zoomed interval's bytes, and a live session's,
     are still read when asked for. Revision 206 reads the interval table's bytes when it is shown. Each metric is
     listed under its basis, which stays beside the selector, since revision 201.
   - Done in revision 206: a real session's interval table reads each listed interval's bytes when shown, which revision
     197's live pass found it had none of. (The relationship table's scope, the pass's other finding, is revision 198's.)
   - Done in revision 203: TCP connection events no longer admit the source's always-zero size field as bytes.
   - §6.8's `Explain`: a relationship states the rule, version, evidence keys and strength behind its edge (revisions
     323–330), a process the rule and strength that bound its records, with what the evidence policy left out
     (revision 335; `icat processes` says it in the same words since revision 385), and a paired channel the rule that
     paired its ends, whether the capture saw it open and close, and its key (revision 336; `icat channels` says it in
     the same words since revision 386), and a group how it was formed and what its total leaves out (revision 337). A
     timeline cell's hover card and the interval table state its count and coverage, and since revision 409 a cell
     chosen by a click, a step or the interval table is explained beneath the inspector's time scope: which of its
     lane's records it counts and by which rule and version, what its bar plots, the column it was counted in and the
     capture's coverage over it; a click on a lane counted coarser than the view chooses that lane's own cell (R13).
     Since revision 411 E from it lists exactly its records, through the narrowing revision 410 gave a scope, its
     timeline focus and `icat evidence`: one mechanism's, one source direction's or one channel end's records, or a
     group lane's process's, behind a filter chip whose removal widens it; the machine row's cell, which draws the
     rung's records over the machine's, opens the rung's. Since revision 348 a channel, connection, RPC channel or
     HTTP exchanges chosen among a process's rows is what the evidence card counts and E lists, and since revision 355
     its own rung's card counts it too, as E lists it from there. Since revision 370 the inspector there names it too,
     as its row did where it was chosen, unless a process is chosen there. Since revision 356 an aggregate chosen in
     the graph is its processes there: the card counts their records and E lists them through a set filter, which for
     the context node standing for the rest of the machine names nearly every process. Since revision 359 that filter
     names the aggregate.
   - §6.1's collapse floor: done in revision 344. Either main pane fills the column by its Expand toggle or F11, and
     the same command restores the split the person dragged. Since revision 360 an investigation keeps the split and the
     expanded pane for every session opened from it (§26.3, `workspace-v15`), and a split leaves each pane its least
     height, so only that command hides one.
   - §6.2's time base: done in revision 350. The axis says it counts session time, and since when by the wall clock
     the capture recorded, in the reader's zone with its offset; `icat timeline` says the same since revision 351.
     Since revision 421 a Wall clock toggle reads every instant the view states on the wall clock the capture's
     machine read, placed through its calibration, the axis naming its day, both where a view crosses one, in the
     reader's zone with its offset; `icat timeline` and `icat evidence` take `--wall-clock`. Since revision 422 an
     investigation keeps the choice for each session opened from it (`workspace-v19`). Since revision 423 a time typed
     into the search - a time of day from the machine's logs, or session time - is gone to, the cell holding it made
     the analysis interval as a step would, and one the session cannot place said with why. Since revision 424 `icat
     evidence --from` lists the rows from a moment the same reader places (R18). Since revision 431 the axis ticks round
     instants of the ladder between its ends, on the wall clock the times of day it read. The host's own zone waits on
     a capture that records it.
   - §6.2's normalization scope: done in revision 342. The legend keys the lanes' heights and offers each lane its own
     scale, which a newer publication keeps, and since revision 343 an investigation too (§26.3, `workspace-v14`). The
     live preview's bars follow it, tested in the window since revision 349.
   - §6.8's evidence policy: since revision 338 a reused PID's candidates are counted one action from the explanation
     that names what they left out, and the rail says so while they count. Direct evidence only, and conflicting bindings
     too, stay with `icat`'s `--evidence-policy`. A session opened on its own keeps the choice while it is open; since
     revision 339 one opened from an investigation keeps it there with its pins and ranking, and is counted under it
     from its first view when opened from it again (§26.3, `workspace-v13`). Since revision 354 a later holder's
     empty rung says its records were left out as candidates and offers to count them there. Every read a process's
     rows, the call and peer rankings and the lanes make is tested under each policy.
   - §6.8's defaults: done in revision 413. Each setting's own control shows its value, and the rail says how many of
     them - the ranking and its rate, the lanes' scale, the grouping, the evidence policy and, since revision 417,
     whether InterCat's own processes are set aside - differ from their first-run defaults, beside a Restore defaults command that puts them all back as their controls would; the line's
     tooltip names each in the words `icat workspace show` and an investigation's notice use. The theme and the panes
     are the window's, not the view's, and stay as chosen.
   - §19.5's self-observation: since revision 414 a live capture names the process collecting it - its broker, or
     `icat record` - by PID and creation time (`collector-identities-v1`, dependency kind 11), carried, mirrored and
     never released, which `icat session` states; since revision 415 a broker names the client that asked it to record
     too, by the PID its control pipe names; and since revision 416 the instance a collector is - its PID and the
     creation time its lifecycle records carry (`collector-binding-v1`) - is labelled in its row, the inspector, its
     graph card and `icat processes`; and since revision 417 one command in the rail sets them aside from the rows,
     the graph and the channels, with the relationships and channels they are an end of, counting them and their
     records, which the timeline and the evidence still count. It is named among the settings changed from their
     defaults; a ranking's totals over the rows shown, a process's lineage and an export follow it, and `icat export
     --set-aside-collectors` builds the same file (R18). Exploration shows them by default (plan revision 417 says
     why). Since revision 418 a collector the capture names that no instance is - its creation time unread, or carried
     by no lifecycle record of its PID - is said where a process holding its PID is explained, in the inspector and
     `icat processes`, in the rail's tooltip and in `icat overview`. Since revision 419 an investigation keeps the
     choice for each session opened from it (`workspace-v18`). Done.
   - A count agrees with its noun and verb wherever a person reads it: done in revision 357, after revision 354's
     call rankings, for the window, the command line, the metric caveats and the overview's disclosures, through
     `CountText`. A new sentence that states a count should say it through it.
   - A scope's coverage beside its counts (R21): done in revision 362 for the inspector, which states beneath its time
     scope what the capture covered there, mechanism by mechanism, read with the scope's counts, in revision 363 for
     `icat evidence`, whose pages carry it (`evidence-page-v3`) in the same words (`CoverageText`), and in revision 367
     for exports, the window's and `icat export`'s, whose JSON context lists it and whose caveats say it, and in
     revision 371 for a selected record, whose fields end with its mechanism's coverage where it was read; `icat
     evidence` states it among every mechanism's over an interval of one reading. Revision 372 did it for the
     exports' CSV, on every line and on the one line a scope with no row writes, and for the redacted sharing report,
     whose allowlist now admits the coverage's fixed-template words after their own leak test. Revision 373 did it for
     the listings of RPC calls, HTTP exchanges and paired channels, `icat`'s and the channel browser's, which read it
     from the ledger and no longer call a source uncollected because none of its records is there. Revision 374 did it
     for `icat processes`, in the words `icat metric` uses of the same grouped answer, and revision 375 for `icat
     overview`, in the inspector's. Since revision 379 `icat processes` says a process's bytes are unmeasured, or that
     it made no sends or receives, as the window's ranked rows do, where it printed "-" for both, and since revision
     380 `icat`'s call, exchange and peer tables say "untimed", "not reached", "unmeasured" or "none" for a cell
     that printed "-". Since revision 381 the investigation's merged time hatches each run of a session's columns its
     capture did not cover, as the session's timeline does, and its cursor, its lanes' sentences and a column of none
     opened say so. Since revision 382 a one-sided connection's bytes, and a selection's in the inspector, say a
     direction none of whose transfers measured a size as unmeasured, and one with no record as such, never 0 B or
     "nothing received", as `icat workspace correlate` does in its words and its document. Since revision 383 candidate
     joins say what each compared capture covered of TCP and UDP, in the caution ink where one fell short, so no
     candidate is not read as no other end.
   - One display mapping per dimension (R5): done in revision 364 for a mechanism's names (`MechanismText`) and for
     its coverage in a sentence (`SessionCoverage.Sentence`), which every caveat and note now says, in revision 365
     for a coverage state's label and value (`CoverageStateText`), which the window's rows and hover cards and the
     command line's tables read, and in revision 368 for a record's kind and layer, the reason a field holds no
     value, and a capability's state, tier and overhead (`ObservationText`, `CapabilityText`). JSON documents keep
     the enumerations' names for a tool, as §23 now says. Revision 369 did the same for a direction and an operation's
     state (`ObservationText.DirectionOf`, `OperationText.State`), which a channel row, the timeline's direction rows
     and the tour's operations read. Revision 376 did it for why a record binds to no process (`BindingText`), which
     the window's evidence rows and `icat`'s processes, metric, operations and exchanges read, and revision 377 for a
     coverage ledger's acquisition, losses and omissions (`CoverageLedgerText`), which `icat session` and every coverage
     reason read. Revision 378 did it for a measurement's basis and byte domain (`MeasurementText`) and an accounting
     (`MetricCompatibility.Label`), which the ranking selector, a record's size and `icat metric` read; `icat measure`
     and `verify` name a computed tier through `CapabilityText`, untested here since both run only on Windows.
6. M4, multi-machine investigation. Revision 253 made its persistence: the workspace file, its members by identity and
   their resolution, and host names. Revision 254 added §8.2's model and its manual mode: a person aligns members to one
   member's clock with stated bounds, and an order across members is stated only beyond their uncertainty
   (`workspace-v2`). Next, in order:
   - done in revision 256: one boot's captures aligned exactly and others through their wall clocks; since revision
     276 a capture that ends before its last publication still records its start's calibration, its boot and wall
     clock; alignment from shared markers waits on cross-host correlation;
   - done in revisions 264 and 265 (`workspace-v5`, `workspace-v6`): a rate from two separated anchors, and aligning
     through another aligned member, whose alignment two members share and a comparison counts only by its drift;
   - done in revision 266 (`workspace-v7`): a person's confirmation that two host identities are one host, versioned;
   - cross-host correlation: candidates since revision 258 (ADR-041), listed in the window since 259, decided as kept
     revisions since 260, known address translations since 269, each compared capture's coverage since 383; next, a
     real two-host exchange, which needs a second host;
     partial overlap between two captures of one host is flagged since revision 262;
   - the Desktop's investigation: its members, states, relinks, additions and opening one (revision 257), aligning,
     withdrawing and candidate joins (revision 259), decisions (260), the merged time (261), comparing two instants
     (267), zooming it and opening a column's records (268), notes (270), saved views (271), and its coverage (381);
     the merged time on the command line, `icat workspace timeline` (384);
   - done in revision 263: an investigation packaged with its sessions (§8.4, ADR-042); a redacted package of a whole
     investigation is not defined, since a redacted package's pseudonyms hold only within it;
   - done in revisions 277 and 278: candidate joins, the merged time and `icat workspace show`'s overlaps name their
     snapshot vector (I16); a decision records the alignments it was made under, not the generations.
   - **Exit review (revision 281).** Every item M4 implements is done: the workspace file (253), host and boot identity
     (253, 255, 256, 266), calibration and the alignment UI (255, 259, 264, 265), merged time navigation (261, 267,
     268, 271), snapshot vectors (277, 278), candidate joins with their evidence (258, 260, 269), one member per
     capture, so no capture is counted twice (253), and packaging and relinking (257, 263). Of its exit gate, three
     of four hold, each by a test: injected clock uncertainty is exposed ("a session aligned at two instants is placed
     at the rate they measure, its lane uncertain by its wander"), an order is stated only beyond it ("a workspace
     orders instants across members only as far as their alignments allow"), and a manual alignment persists and
     reopens and changes no timestamp ("a manual alignment is an annotation, kept and reopened as recorded"). The
     fourth, **a known two-host exchange from separately captured traces**, needs two machines: record one known TCP
     exchange between them - a client on one, its server on the other - with `icat record` on both (the test
     workloads are loopback only), bring both sessions to one machine, `icat
     workspace new` and `add` them, align them by their wall clocks (`align <b> <a> --wall-clock --sync <duration>
     --drift-ppm <rate>`), and `correlate`: the exchange must be the one candidate, its lifetimes overlapping and its
     bytes the same on both sides. Correlation is tested only synthetically until then, since one host cannot split
     an exchange (TCP is not process-scoped at capture).

## Verification and cautions

- Revision 432 was built and tested in the same Linux container: Debug and Release each ran **1,823 tests**, passing
  **1,727 with 4 skipped**; the 92 failures are revision 361's baseline less the two legibility tests revisions 428
  and 432 made pass, and every one of them needs Windows. In a real window before any session, export, the redacted
  report and re-layout are disabled and say to open or record a session first, the tooltip shown on the disabled
  button; with a session open each is offered and says what it does; and at the rung of a process with no channel,
  export and the report say the rung lists no row. They caught each of 5 mutations; a sixth, which dropped one of the
  places a descent announces that the rung's rows changed, is equivalent, since the ranking's own change announces it
  too. The full runs first failed the test that holds every rule to a test or a declaration, for the new test's
  missing entry in the fixture index; with it added, that project passed in Debug and Release, and the counts above
  include it.

- Revision 431 was built and tested in the same Linux container: Debug and Release each ran **1,822 tests**, passing
  **1,725 with 4 skipped**; the 93 failures are the ones revision 428 left. In a real window of a calibrated session,
  in session time and on the wall clock, at the default and the smallest size, every tick's label is centred on its
  rule, clear of the next, of both ends and of the time base, and names the instant it is drawn at: a round session
  time, or the time of day the wall clock read there to the tick; at the default size each base ticks at least two.
  The clock's ticks are checked apart: multiples of the width in session time, negative ones included, and on a wall
  clock 10 ppm fast the round times of day it reads, a width under a second named to the digits it needs, and on one
  150 ppm fast a round second whose instant maps back a tick early is still named whole; the named ladder is checked
  from 4 s to a day and a half. A repaint of the unchanged timeline still allocates nothing. They caught each of 9
  mutations.

- Revision 430 was built and tested in the same Linux container: Debug and Release each ran **1,820 tests**, passing
  **1,723 with 4 skipped**; the 93 failures are the ones revision 428 left. In a real window the demo's server,
  aligned by its wall clocks, opens the Align dialog on them, its reference and the agreement and drift stated,
  beneath the alignment in force, and aligning from it states the same alignment again; a session aligned at two
  instants opens on both instants, their bound and the drift, and one aligned through another session on that session.
  A duration, an instant and a drift written back read as stated in three cultures. An empty list of translations or
  views says so. They caught each of 11 mutations.

- Revision 429 was built and tested in the same Linux container: Debug and Release each ran **1,818 tests**, passing
  **1,721 with 4 skipped**; the 93 failures are the ones revision 428 left. In a real window, a session whose one
  connection runs to another host shows the relationship table's line in place of its rows, in words and to a screen
  reader; a session listing two relationships shows none, until a descent to a process with no relationship shows it,
  and a return to the machine rung hides it again. The window's model says the same at a focused rung, and with
  InterCat's own processes set aside. With every test of the Desktop project on the single-threaded context, the
  project passed three runs in a row in Debug, and the test that holds the rule failed when one test was turned back
  to awaiting on the pool. They caught each of 6 mutations.

- Revision 428 was built and tested in the same Linux container: Debug and Release each ran **1,816 tests**, passing
  **1,719 with 4 skipped**; the 93 failures are revision 361's baseline less the investigation window's legibility
  test, which passes now that its smallest height keeps two whole sessions. Over three spans of an investigation - its
  whole 3 s, about 98 ms and 3.1 µs of it - and three widths, the axis names its ends as the session's timeline names
  its own, and every tick between them falls on one width of the 1–2–5 ladder, a width apart, named to that width's
  digits and centred on the instant it names; the existing test still finds no two labels run together at any width
  from 280 to 1,200 pixels. The column readout, a column opened, the views dialog and `icat workspace timeline`'s lane
  sentence are checked in their words, a lane of one record said at its instant. They caught each of 11 mutations. The
  Debug run failed one more test, which awaited a count on a pool thread while the workspace's own continuations ran
  beside it, as revision 427 found of four others: it, and the three others of its file that awaited alike, now run on
  the single-threaded context, and the Desktop tests then passed whole in Debug and Release.

- Revision 427 was built and tested in the same Linux container: Debug and Release each ran **1,815 tests**, passing
  **1,717 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. In a real window of the
  smallest size, read on the wall clock, each interval row's window is laid out whole within its column and ends
  before its count, and its bytes end inside the table; an interval with no record draws no glyph and is read without
  a mechanism, as the row builder's test checks of an empty and a TCP interval; and the registry of open sessions
  opens afresh a folder written with another session and one emptied, where it handed out the store kept for the
  session each held. They caught each of 7 mutations. The first full Debug run failed a test that gathered the view
  model's change names in a list its continuations appended to off the test's thread while the test read it, and a
  repeat failed an export test the same way; those two, and two more written alike, now run on the single-threaded
  context the other view-model tests use, where eight runs in a row passed. A Release run then failed, once, to open a
  package just made, which a test before it had made and removed at the same name in the same second: that is the
  registry fault above, and the full Debug and Release runs after its fix are the ones counted here.

- Revision 426 was built and tested in the same Linux container: Debug and Release each ran **1,812 tests**, passing
  **1,714 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. In a real window, Ctrl+F, a
  time and Enter leave the keyboard on the timeline, whose status for a screen reader names the analysis interval it
  chose beside the range in view; `]` then steps on from that cell in the selected TCP lane, and the search's status
  is a polite live region. The timeline's status with nothing chosen is unchanged, as the accessibility audit and the
  keyboard tests check. They caught each of 4 mutations.

- Revision 425 was built and tested in the same Linux container: Debug and Release each ran **1,812 tests**, passing
  **1,714 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its test counts `icat timeline
  --interval 0s:6us` and `0ms:0.006ms` in the buckets `0:60` counts, takes `150ns:250ns` outward to the ticks `[1, 3)`
  and `-1us:1µs` to `[-10, 10)`, and refuses a bound with no unit, an unknown unit, a lone bound and an end not after
  its start, each saying what an interval takes. They caught each of 8 mutations.

- Revision 424 was built and tested in the same Linux container: Debug and Release each ran **1,811 tests**, passing
  **1,713 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its test reads `icat evidence
  --from` a time of day typed in this computer's zone, listing the rows from it on and naming where it fell on the
  wall clock and in session time; places session time alike, its JSON carrying the instant in ticks and as the wall
  clock read it; lists from the later of the moment and `--interval`'s start, and refuses a moment after the
  interval's end; and refuses, in the search's words, a moment outside the session, a time of day in a session with no
  wall clock, and text that is no moment. They caught each of 11 mutations.

- Revision 423 was built and tested in the same Linux container: Debug and Release each ran **1,810 tests**, passing
  **1,712 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests read as a moment a
  time of day, with its date or offset, and a number of seconds with a fraction or a unit, and not a name, an endpoint
  or a bare number; place session time in its unit and a time of day on the wall clock in the reader's zone or the
  offset it names, on the day it names or the session's one day it falls on; refuse, with why, a moment outside the
  session, on two of its days, skipped or repeated by a summer-time change, malformed, or a time of day in a session
  with no wall clock; and read a wall-clock reading back through the calibration's rate to the instant it was read at.
  In the view model a time of day typed into the search is offered first, named on the wall clock and in session time
  and read aloud as a time; going to it centres a zoomed timeline on it at its span and makes the column holding it
  the analysis interval at the machine rung, the selected TCP lane's cell, explained, with a lane selected, and a
  coarser process lane's own cell at a group's rung; a later moment gone to first is the one chosen; one the session
  cannot place is said ahead of the names it matches, session time is gone to in a session with no wall clock, and a
  bare number still finds a PID. A zoom keeps the lanes asked for, empty and judged by their own coverage, so the
  window's zoom draws every lane and the ranked table agrees with the cell's count; a zoom carried into a publication
  that adds a lane explains no cell until its own arrives; `icat timeline --mechanism udp` counts each quiet column
  with its coverage. In a real window Ctrl+F, a time and Enter centre the zoomed timeline and explain the TCP cell
  holding it, one of the zoom's own. They caught each of 27 mutations.

- Revision 422 was built and tested in the same Linux container: Debug and Release each ran **1,798 tests**, passing
  **1,700 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests keep a member's
  layout reading its times on the wall clock, in its own words and after every other setting, remove it when it reads
  session time again, refuse a version 18 file that reads the wall clock, and read one that does not as reading
  session time before writing it as version 19. In a real window a session opened from its investigation and read on
  the wall clock keeps the choice there, which the investigation's window states; opened on its own it reads session
  time, opened from the investigation again it reads the wall clock from its first view, its toggle on, and says so,
  and read in session time again the investigation keeps nothing of it. `icat workspace show --json` writes
  `workspace-resolution-v22` with each layout's `wallClock`. They caught each of 12 mutations.

- Revision 421 was built and tested in the same Linux container: Debug and Release each ran **1,796 tests**, passing
  **1,698 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. The first full run failed one
  more test in both, the architecture test that keeps the plan's revisions out of what a person reads: the refusal for
  a session with no wall clock named the revision that began recording one. It was reworded, and the architecture
  tests and the time base's tests were run again in both configurations, passing. Its tests place a capture's wall
  clock through its calibration - exactly at its start and stop readings and on their line between, at the capture's
  own rate from a start alone, and nowhere for an import - and read instants, ranges and records on it as times of day
  in a zone with its offset, to the digits their span needs, cut rather than rounded, dated across midnight and with
  each end's offset across a summer-time change, while session time reads as it did. In the view model the choice
  moves the time scope, a card, the interval table's rows, an RPC call's and an HTTP exchange's row, the evidence's
  scope and rows and the inspector's record to the wall clock and back, keeping the record chosen, is named as a
  changed setting and is kept across a later publication; a session that recorded none never reads it. In a real
  window the toggle shows only for a calibrated session and moves the axis's start, its time base and its spoken
  words, and Restore defaults reads session time again. `icat timeline --wall-clock` and `icat evidence --wall-clock`
  state the same and refuse a session that recorded none. They caught each of 35 mutations.

- Revision 420 was built and tested in the same Linux container: Debug and Release each ran **1,786 tests**, passing
  **1,688 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its query tests count the
  lanes a view names each on its own and the rest in one folded lane, partitioning the group's records column by
  column, ignore a named process the group does not hold, fold a smaller group's unnamed members too, and refuse more
  lanes than the bound. In the view model a group of 230 draws its pinned lane first and its busiest after it, folds
  the 31 least busy and says so, counts every record once across the lanes and the fold, names the fold on its card
  and in its cell's explanation, and E lists exactly the folded members' records in that cell; a search counts the
  members it finds among the folded, and a folded member selected and pinned is counted in a lane of its own at the
  top, folding again when unpinned. A group of 12 folds nothing. In a real window the folded row is drawn beneath the
  lanes, where the pointer finds its bucket and a click chooses and explains its cell; a click on its name selects no
  process, and the pin control, offered for a folded member, gives it a lane. They caught each of 23 mutations.

- Revision 419 was built and tested in the same Linux container: Debug and Release each ran **1,782 tests**, passing
  **1,684 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests keep a member's
  layout setting InterCat's own processes aside, in its own words and on its own, remove it when they are shown again,
  refuse a version 17 file that sets them aside, and read one that does not as showing them before writing it as
  version 18; the words come last in what a layout keeps. In a real window a session opened from its investigation and
  set aside keeps the choice there, which the investigation's window states; opened on its own it shows them, opened
  from the investigation again it sets them aside from its first view and says so, and shown again the investigation
  keeps nothing of it. `icat workspace show --json` writes `workspace-resolution-v21` with each layout's
  `collectorsAside`. They caught each of 12 mutations.

- Revision 418 was built and tested in the same Linux container: Debug and Release each ran **1,780 tests**, passing
  **1,682 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests publish the capture
  whose client's creation time was not read and whose recorder names the broker's PID with a creation time no instance
  carries: the process holding the client's PID is explained as not taken for it, each holder of the broker's PID, the
  broker among them, as not the recorder, and no other process is; a process explained without the capture's
  collectors, or of a capture naming none, is explained as before. In the window's model the inspector says it of the
  process holding the client's PID, which bears no label, and the rail's tooltip ends with both sentences; `icat
  overview` names the broker's instance and why the other two are none under Collected by, and labels the broker among
  its busiest processes; `icat processes` explains as the inspector does. They caught each of 14 mutations.

- Revision 417 was built and tested in the same Linux container: Debug and Release each ran **1,777 tests**, passing
  **1,679 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests set the tour's
  collectors aside, which takes their relationships, channels, operations and marks and a group left with none, and
  the broker of a published capture, which takes its connection to its client while the timeline and the records no
  process holds stay whole; a snapshot naming none is itself. An export counts what it set aside in its caveat, JSON,
  CSV and redacted report, which names no process; an evidence export lists every record; and a group sharing its
  executable with the window that asked for the capture ranks without it, its calls' median and its peers. The peer
  and call measures keep each instance's own and leave the set-aside ones out of every group and the machine. In the
  window's model the rail names the broker and counts it once set aside, the setting is named as changed, a ranking by
  peers counts the client alone, and a lineage names a parent or child set aside; in the window, the command sets them
  aside, clears a selection of one without saying it was lost, shows them again, Restore defaults shows them, and
  another session opens with them shown. `icat export --set-aside-collectors` writes the same file and says when there
  was none to set aside. Four tests that took a CSV's coverage for its last column now find it before the set-aside
  counts. They caught each of 41 mutations; one written so that it no longer built was rewritten until it did.

- Revision 416 was built and tested in the same Linux container: Debug and Release each ran **1,766 tests**, passing
  **1,668 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests publish a capture
  whose broker sends to its client and then exits, its PID taken by another process: the broker is labelled in its
  caption and explanation, the later holder of its PID is not, and the client, whose creation time was not read, stays
  unfound, as does a collector naming the broker's PID with a creation time no instance carries; a capture naming none
  labels nothing. In the window's model the broker's row, the inspector's caption and explanation and its graph card
  name it, and the later holder is described as any process is; `icat processes` names it beside its executable, says
  how it was counted and names its role in its document, and `icat overview --json` writes the bundle with which
  instance each collector is - which, written first as a map keyed by instance, could not be written at all, so it is
  a list. They caught each of 16 mutations; one written so that it no longer built was rewritten until it did.

- Revision 415 was built and tested in the same Linux container: Debug and Release each ran **1,763 tests**, passing
  **1,665 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests start a capture
  through a connection whose pipe named its client's PID, which reaches the capture's runtime, once however often the
  request is replayed; refuse a PID that is not positive; and have a broker name itself and the client it was given,
  by PID and creation time, or itself alone. Reading the pipe's PID, and the broker's own runtime, run only on Windows
  and are untested here. They caught each of 4 mutations.

- Revision 414 was built and tested in the same Linux container: Debug and Release each ran **1,762 tests**, passing
  **1,664 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests round-trip a
  capture's collectors and refuse what the contract refuses; read them back, carried by re-derivation, never released
  and one to a generation; record a capture through a scripted trace host that names its broker and a client,
  published under its first generation's name, carried to its last, mirrored once by a follower, kept by a capture
  rewound to before its last publication and mirrored again once that follow resumes; publish them with a capture's
  only generation when it publishes once; refuse another capture's collectors and collectors no file could hold before
  a capture starts; read a collector's PID and creation time from its process, or leave the time out where no process
  holds the PID; keep them out of a redacted package, whose leak scan looks for their digest; and have `icat session`
  name each one, or say nothing of a session naming none. The broker's own naming of itself runs only on Windows and
  is untested here. They caught each of 18 mutations, one after a test was added for the leak scan's needle.

- Revision 413 was built and tested in the same Linux container: Debug and Release each ran **1,756 tests**, passing
  **1,658 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Debug's first run took a build
  of the window tests made before their last test was added, so that project was rebuilt and run again in Debug, its
  two failures the baseline's. Its tests name each setting a workspace changes from its default, in an investigation's
  words and as it changes, per second only under a ranking with a rate, and none in the tour; in the window, a ranking
  by bytes sent per second, each lane on its own scale, grouping by terminal session and candidates counted read "4
  settings changed" beside Restore defaults on one line, the four named in its tooltip and to a screen reader, and the
  command puts all four back, keeping the time and the selected process, after which it has nothing to restore; and in
  a session opened from its investigation, restoring keeps only its pin there, the session opening again at the
  defaults. A screenshot of the rail at 1,080 by 700 showed its ranked rows beneath the line, which a first draft
  listing every setting pushed out of view. They caught each of 11 mutations.

- Revision 412 was built and tested in the same Linux container: Debug and Release each ran **1,753 tests**, passing
  **1,655 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests page through an RPC
  channel whose process's calls alternate with another's in segments of ten records: each page of ten reads 11 rows
  and the last 10, listing what one segment of the same records lists. The records gathered for the first page are the
  ones the generation keeps, which a zoom of the channel counts as the one segment's zoom does; kept while among the
  last four operations asked for, a policy's apart from another's, they are gathered anew after four others. An
  exchange's buffers are kept too, and an operation's records are held by segment, each once, rows ascending. A probe,
  removed before committing, measured the zooms and pages on a reopened session of 500,000 calls in Release, against
  the same probe at revision 411. They caught each of 15 mutations; one written so that it no longer built was
  rewritten until it did.

- Revision 411 was built and tested in the same Linux container: Debug and Release each ran **1,751 tests**, passing
  **1,653 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests choose a TCP lane's
  cell, whose card names its records and whose E lists exactly them behind a "Mechanism: TCP" chip, the evidence
  rung's timeline counting them, and the chip removed, every record of the interval; a process's outbound row's cell,
  a channel end's and a group's process lane's, each listing exactly its records; the machine row's, which lists the
  rung's own; and a brushed range with a lane selected, which narrows nothing. Each list is the one `icat evidence`
  reads with the same narrowing. In the window, a click on a UDP lane's cell, then E, lists its records behind a
  "Mechanism: UDP" chip. They caught each of 10 mutations.

- Revision 410 was built and tested in the same Linux container: Debug and Release each ran **1,748 tests**, passing
  **1,650 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests read a session of a
  client and a server over one paired connection, the client also sending datagrams: a page narrowed to UDP lists
  exactly the UDP records, one narrowed to the client's outbound records exactly those, and the channel's two ends
  part its records, each end's bound to the process holding it; a timeline focus narrowed the same way counts each set
  exactly. A narrowed page's cursor continues it and restarts an unnarrowed or otherwise narrowed query, and an end
  without a paired channel, a third end and a mechanism or direction that is none are refused. A rung's lane filters
  narrow the scope its entity filter names, in words, and an end of a process's records is a problem stated. `icat
  evidence --mechanism udp`, `--direction inbound` and `--channel <key> --end 1` list those records,
  `evidence-page-v4` names them, and a name that is none or a third end is refused. They caught each of 19 mutations.

- Revision 409 was built and tested in the same Linux container: Debug and Release each ran **1,744 tests**, passing
  **1,646 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests choose cells of
  every kind of lane: a TCP lane's in the overview and in a zoomed view, under a record and a byte ranking; a process
  lane's in a group of forty counted in 500 columns where the view drew 1,000, which becomes the analysis interval
  whole and names its owner and the binding rule, and another process's empty one; a direction row's and a channel
  end's, which name the binding and the pairing rule; and the machine row above them. A brushed range explains none,
  another interval, lane, row, end, process or rung chosen takes over, the tour explains none, and a publication keeps
  the lane. In the window, a click on a coarser lane's cell in a group of a hundred lanes chooses that cell, where it
  chose the machine column beneath, its card then says it is the analysis interval, and the inspector's text beneath
  the time scope is the view model's. They caught each of 19 mutations.

- Revision 408 was built and tested in the same Linux container: Debug and Release each ran **1,740 tests**, passing
  **1,642 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests cut a capture's
  linked RPC calls into three segments and a process's WinINet buffers into four, each beside a twin holding them in
  one. Once the calls are paired, the call ranking of a time scope, the client's RPC channels and their spans open no
  segment, a page of the scope's one call opens the two holding it and its other end, its evidence the one holding its
  two records, and a zoom of its channel the two the zoom meets, each answering as the twin does. Once the exchanges
  are grouped, a process's exchanges, a time scope's and their spans open none, and an exchange's evidence the one
  segment holding its three buffers. A probe of 500,000 calls in twenty-one segments, deleted after measuring, gave
  the times the plan states. They caught each of 12 mutations.

- Revision 407 was built and tested in the same Linux container: Debug and Release each ran **1,738 tests**, passing
  **1,640 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests export a session of
  five processes in terminal sessions 0, 1 and 2 and one naming none, grouped by session and ranked by peers, from the
  window and through `SessionExport`: the two files are the same, byte for byte, in JSON and CSV, at the machine rung,
  whose rows are session 1, session 0, session 2 and the unrecorded processes, at terminal session 1's rung, and at
  its evidence, the records of its two processes. The JSON names `"groupedBy": "session"` and says how the groups were
  formed among its caveats. `icat export --group-by session --at session:1` writes that rung and reports "Grouped by
  terminal session"; without the option the key names no row, `--group-by host` is refused, and so is grouping by
  session a session whose records name no terminal session. They caught each of 11 mutations.

- Revision 406 was built and tested in the same Linux container: Debug and Release each ran **1,736 tests**, passing
  **1,638 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests keep a member's
  grouping by terminal session in its layout by name, and remove a layout that groups by executable and keeps nothing
  else; refuse a grouping no view offers when kept, and a file that names one, names executables, or groups in a
  version 16 file; and read a version 16 file as one grouping by executable, written again as version 17. `icat
  workspace show` states "its processes grouped by terminal session" before the ranking, and its document carries the
  grouping (`workspace-resolution-v20`). In a real window a session opened from its investigation and grouped by
  session keeps the grouping there, which the investigation's window says; opened on its own it is grouped by
  executable, and opened from the investigation again it is grouped by session from its first view, its notice saying
  what was put back; grouped by executable again, the investigation keeps nothing of it. They caught each of 14
  mutations, among them a grouping that keeps nothing, executables kept by name, an earlier version's grouping read,
  the window keeping no grouping, and the kept grouping not put back.

- Revision 405 was built and tested in the same Linux container: Debug and Release each ran **1,734 tests**, passing
  **1,636 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests regroup a session of
  five processes - client.exe in terminal sessions 1 and 2, the server in session 1, the service in session 0 and a
  tool naming none - by session: the machine rung lists sessions 0, 1 and 2 and the unrecorded processes apart, each
  its members' records together, to the executables' total; a session's rung lists its processes; and a session naming
  none offers executables alone. Peers and RPC call times by session match `icat metric --group-by session` over
  thirty random sessions and an RPC fixture. Thirty workers in session 1 fold into one graph node whose card names the
  session, and the inspector, a search hit and the evidence step name sessions. In a real window the selector stands
  in line beneath Rank by at the machine rung only, regroups the rail keeping its selection, ranking and brush, holds
  for a later publication, falls back to executables for one naming no session and starts by executable for another
  session. They caught each of 26 mutations, among them peers or call times keyed by executable only, the unrecorded
  processes listed first, the selector offered at a group's rung, and a regroup that kept the rung below, which a
  first run missed and a test of regrouping from a session's rung now catches.

- Revision 404 was built and tested in the same Linux container: Debug and Release each ran **1,727 tests**, passing
  **1,629 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests group a session of
  four processes - two naming terminal session 1, one session 0 and one none - by session: records and bytes sent fall
  under each process's session and add up with the unattributed rows to the total, the process naming none is
  unattributed as `SessionUnknown` and no executable reason appears, peers count three for session 1 and one for
  session 0, RPC calls started count four and two, and a session naming no session at all is not grouped by one.
  `icat metric --group-by session` ranks "Terminal session 1" and "Terminal session 0" and states the rest, and its
  JSON carries each group's kind and session. They caught each of 9 mutations, among them a process naming no session
  placed in a session, sessions grouped by process, and `icat` naming a session group by its kind.

- Revision 403 was built and tested in the same Linux container: Debug and Release each ran **1,724 tests**, passing
  **1,626 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its test reopens a six-segment
  session from its checkpoint and lists three processes' connections to other hosts: one whose connection ends at the
  fourth segment's first record opens the third and the fourth, one whose connection starts at the fourth's last
  record opens the last three, one holding a short connection inside a long one opens the four the long one meets,
  and within a scope a process opens only the segments both meet - one, or none when its connection lies elsewhere -
  each listing what one segment of the same records lists, and every process's together opening all six. On a
  reopened session of 1,000,000 rows in 20 segments, the connections of a process whose one connection spans a
  hundredth of it took 8-64 ms, opening 1 segment, where they took 127-221 ms opening 20. They caught each of 6
  mutations, among them a lifetime that ends at a segment's first reading or starts at its last, and lifetimes left
  unmerged.

- Revision 402 was built and tested in the same Linux container: Debug and Release each ran **1,723 tests**, passing
  **1,625 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its test reopens a six-segment
  session from its checkpoint: a first page of 150 records with their owners opens the two segments holding them and
  the 151st, reading 151 rows; the next opens neither the first segment, which ends before its cursor, nor any after
  the 151st's; a page's identity read from the segments' headers is the one their open readers give; and a page of a
  time scope opens only the segments it meets and reads only its rows - 90 of a hundredth of the session, its owner's
  45 and its channel's 90 among them, 10 across a boundary in two segments, none past every record - listing what one
  segment of the same records lists. On a reopened session of 1,000,000 rows in 20 segments a page of a hundredth of
  it took 10-22 ms (171-244 ms before) and its owner's page 9-15 ms (151-196 ms), each opening 1 segment where it
  opened 20; a first or second page opened 1 too. They caught each of 9 mutations, among them a page that reads rows
  before or past its scope, opens a segment before its cursor or outside its scope, and misreads a header's capture or
  derivation.

- Revision 401 was built and tested in the same Linux container: Debug and Release each ran **1,722 tests**, passing
  **1,623 with 4 skipped**; 94 failures are the ones revision 361's baseline lists. The 95th, `I14: a finished session
  keeps each overview column's bytes per mechanism…`, gave two sends session times their readings could not give, so
  the lanes' read of the extent met no segment holding them; they now have their readings' times, and the Application
  tests then passed all 323 in both. Its tests reopen a six-segment session from its checkpoint and find a brush's
  counts, its lanes' and scopes' bytes, a focus and a time scope's byte and peer rankings opening only the segments the
  interval meets - one, two across a boundary, one ending at a segment's first record, two starting at one's last, none
  past every record, all six for the whole - whether the channels or the instances are asked for first, and answering
  as one segment of the same records does; a session without a checkpoint derives from every segment once, and a
  later brush opens only what it meets; an RPC capture's first brush pairs its calls over all five of its segments,
  and a later one opens only the two its interval meets. On a reopened session of 1,000,000 rows in 20 segments a
  brush over a hundredth of it opened 1 segment where it opened 20: its counts took 11-64 ms (39-105 ms before), its
  bytes 5-26 ms (20-126 ms), a process's bytes 9-24 ms (33-53 ms), the byte ranking 10-23 ms (99-133 ms) and the peer
  ranking 12-42 ms (70-175 ms). They caught each of 14 mutations; the one that took the instances from every segment
  instead of the checkpoint first survived, since the first read asked for the channels, which bring the instances
  with them, and each brush is now read both ways.

- Revision 400 was built and tested in the same Linux container: Debug and Release each ran **1,720 tests**, passing
  **1,622 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its first run failed `§6.2: a
  zoomed mechanism lane hovers…` in both: its rows stretched their session times 500,000-fold and not their readings,
  so the zoom met no segment; it and seven other window tests now stretch both. Its test opens a six-segment session
  afresh for each zoom and finds it opened only the segments its interval meets - one, two across a boundary, one
  ending at a segment's first row, two starting at one's last, none past every row, all six for the whole - counting
  every row in it as a read of every segment does, and a focused zoom tiling only its met segment. Measured on a
  reopened session of 1,000,000 rows in 20 segments, a zoom to a hundredth of it took 3-17 ms after the first pass,
  opening 1 segment, where it took 86-237 ms opening 20. They caught each of 6 mutations; the one that tiled every
  segment of a focused zoom first survived, changing no count, and the test now checks which segments were tiled.

- Revision 399 was built and tested in the same Linux container: Debug and Release each ran **1,719 tests**, passing
  **1,621 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its window tests show the share
  prompt with a time scope, offered first and said in the package's own sentence, its whole-session choice sharing all
  of it and its scope choice the scope; without a scope, no choice; over the row bound, the whole session refused, what
  is kept hidden and the reason stated until a scope is chosen; and the window writing a package of its time scope
  whose policy and saved result state it. Both new forms of the prompt joined the legibility and accessibility audits.
  They caught each of 11 mutations; the progress text of the first read is not checked.

- Revision 398 was built and tested in the same Linux container: Debug and Release each ran **1,717 tests**, passing
  **1,619 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists, and the multi-selection test
  revision 397's Release run failed passed in both. Measured at 1080 × 700 with a session of three processes, the card
  wanted 323 px of the 311 it may have and left the list 120; it now wants 280 and leaves 163, two rows and the start
  of a third, and at 760 px 223. Its window test opens a session at that size and finds the card's first-run sentence,
  demo button and empty freshness line hidden, the card unscrolled, the three rows in view, and the Investigation
  menu's Explore the demo opening the demo beside the session; before a session the sentence and button show and an
  empty path takes no room. They caught each of 8 mutations.

- Revision 397 was built and tested in the same Linux container: Debug ran **1,716 tests**, passing **1,618 with 4
  skipped**, and its 94 failures are the ones revision 361's baseline lists; Release ran the same and failed one more,
  `R15: Ctrl+click and Ctrl+Space build a multi-selection…`, which then passed 3 of 3 alone. Its third ranked row was
  not realized: since revision 396's Explore the demo button, the rail's card at 1080 × 700 with a session open leaves
  the ranked list its 120 px floor, two rows, so the test depends on timing. Revision 398 gives the list its room back.
  Revision 397's tests make an interval package of the rich source and find its seven rows - the interval's three and
  four lifecycle records of their PIDs - its instances and bindings the source's, its parent named and not linked, its
  names pseudonyms, its ledger clipped to the interval, dropped or renumbered as the interval meets its epochs, its
  coverage the source's within it and unknown outside and over its whole time, its policy's interval and one
  sentence in every reader, and the byte scan's needles a whole package's; measure an interval's preview, its first
  tick held and its end not; refuse an empty interval and an impossible policy interval; pin the native readings an
  interval holds either side of zero on a 10 MHz and a 1 GHz clock; and read `icat package --interval`, `icat
  session` and the window saying it. They caught each of 27 mutations.

- Revision 396 was built and tested in the same Linux container: Debug and Release each ran **1,709 tests**, passing
  **1,611 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its window test keeps the demo's
  folder beside the sessions folder; offers the action before any capture, described as generated; makes the demo the
  first time, a second click meanwhile answered at once with nothing opened, and opens its investigation saying what
  it is; opens the same demo again rather than another; leaves a folder a stopped write left and takes the next; says
  where every demo folder is taken; and hides the action while a capture runs. They caught each of 13 mutations; one,
  the guard against a second click, first survived, since the racing writes then failed on one folder, and the test
  now checks that a second click is answered at once. That the investigation file takes its name last is not
  observable once a demo is whole; the test's stopped folder stands for what it prevents.

- Revision 395 was built and tested in the same Linux container: Debug and Release each ran **1,708 tests**, passing
  **1,610 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests make the demo and
  open both sessions whole - finalized, calibrated and checkpointed - with the client's browser and sync, the server's
  api, db and files and their loopback relationship, records placed where their readings put them, each mechanism's
  coverage with the server's two lost records, the wall-clock alignment of 0.75 s within its stated 2 ms, and the two
  candidate joins; make it twice to the same digests and refuse a used folder untouched; and read the window's strip,
  title, disclosure, saved sessions list and investigation window, `icat session`, `icat workspace show` and its
  JSON `demo` saying it was generated. They caught each of 17 mutations; one, the note `icat workspace show` adds,
  first survived, since the demo's own workspace note repeats the sentence, and the test now reads the line alone.

- Revision 394 was built and tested in the same Linux container: Debug and Release each ran **1,704 tests**, passing
  **1,606 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its fitness tests read the
  projects and sources the broker is built from, and its coordinator test runs the broker's ownership checks with an
  in-memory store, so all three run on any host. They caught each of 10 mutations: the broker referencing the
  application layer, the recorder taking a UI package, the broker naming the ETL replay, a second ETL file reader,
  a memory read, broader process access, an unchecked pipe server, status given to anyone, an owner without its logon
  session, and a session name valid without its secret. A mutation that stopped uppercasing the owner's SID survived
  as equivalent: owners already match ignoring case.

- Revision 393 was built and tested in the same Linux container: Debug and Release each ran **1,701 tests**, passing
  **1,603 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests measure a published
  session's size as the sum of its files' own lengths on disk, with its journal's part, bytes per record and tier, and
  the same in the projected overview; hold the tier bounds at 2, 20 and 100 GiB; and walk a recording capture's limits:
  its length first, its journal first at six times the rate, a nearly full volume first with the session on another,
  a volume that could not be read, a 24-hour limit read in hours and a journal limit in days, a minute of growth after
  the generation measured it, and a session not yet grown measurably. A window test reads the line for a saved
  session, for a recording half a minute from its limit in caution ink and for a stopping capture, and forgets it when
  a new capture starts; a command line test holds `icat session`'s `On disk` and JSON `size` to the window's words.
  They caught each of 19 mutations. Five checkpoint tests compared two generations' overviews whole; they now leave
  out the size each generation measured of its own files, as they leave out its manifest.

- Revision 392 was built and tested in the same Linux container: Debug and Release each ran **1,694 tests**, passing
  **1,596 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its window test saves a bundle
  of a session whose records name a user's folder, a tool and two endpoints: the question before it says what the
  bundle holds and leaves out in `icat support`'s words and that a bundle made there holds no capability report, and
  the bundle holds the session by its folder's name with its rows, a null capability report and none of those names,
  endpoints or the folder above it. The question joins the prompt audits, and Escape chooses no file. They caught
  each of 9 mutations, the command line's own capability report and string enums among them.

- Revision 391 was built and tested in the same Linux container: Debug and Release each ran **1,692 tests**, passing
  **1,594 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests write a bundle of a
  session whose records name a user's folder, a tool, two endpoints and a host, with a ledger that lost 3 records: it
  holds the version, the runtime, the capability report, the session by its folder's name with its rows by mechanism
  and coverage in `icat session`'s words, its ledger's loss, clock and files, and none of those names, endpoints or
  folders above it. `--check` lists what a bundle holds and leaves out and reads nothing; a bundle must be told where
  to go and replaces nothing unless told to; a folder holding no session, one that does not verify and one that is
  gone are each said to be, by the folder's name. They caught each of 11 mutations.

- Revision 390 was built and tested in the same Linux container: Debug and Release each ran **1,688 tests**, passing
  **1,590 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests check that the
  search's new predicate finds exactly the processes its hits list for nine queries, PID prefixes and blanks included;
  at a group of 60 lanes a PID marks one lane and names it to show, the executable's name marks all 60 though the
  rail lists 50 hits, a hit chosen in the rail is the lane shown, another group's process and a search that finds
  none here mark nothing and say so, and lanes counted after the search is typed are marked as they arrive. In the
  smallest window a PID typed at a group of 120 scrolls its lane, far below, into view, marked in the strongest ink,
  selecting nothing, and Escape clears the marks. They caught each of 11 mutations.

- Revision 389 was built and tested in the same Linux container: Debug and Release each ran **1,684 tests**, passing
  **1,586 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its test opens a group of 120
  instances in the smallest window: at the top only the machine row and the lanes in view are drawn, with the first
  lane's send in its hue; scrolled to the bottom, the last lane's is drawn and the first rows are not; back at the top,
  the timeline filling the column draws the lanes the taller view shows. A throwaway headless measurement in Release,
  removed before the commit, found a 200-lane frame at 73 ms and 10 MB allocated before and 24 ms and 0.44 MB after, as
  a group of eight lanes takes. The test caught each of 6 mutations; the paint-allocation test still finds no
  allocation of the timeline's own.

- Revision 388 was built and tested in the same Linux container: Debug and Release each ran **1,683 tests**, passing
  **1,585 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests keep two lanes, the
  later-sorting first, in that order, beside a pin and a ranking, and remove a layout pinning nothing; refuse a lane
  pinned twice, by the empty identity or past 1,024, from the code and from a hand-edited file, and a version 15 file
  that pins one; read a version 15 file as pinning none, and write it as version 16. `icat workspace show` says the
  lane with the node and the ranking and lists it in `workspace-resolution-v18`. In the window, a session opened from
  an investigation keeps two pinned lanes there, its row says so, opened on its own it pins none, opened again they are
  drawn first in the order pinned and its notice says what was put back, and unpinned the investigation keeps nothing of
  it. They caught each of 16 mutations.

- Revision 387 was built and tested in the same Linux container: Debug and Release each ran **1,681 tests**, passing
  **1,583 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests open a group of four
  instances sending four messages down to one and pin the least busy lane: it is drawn first, a second pin goes
  beneath it, unpinned it goes back last, the ranked table keeps its order, and another group's caption counts none;
  under a byte ranking a pin and the same view asked again read no bytes anew. In the window, `P` with the timeline
  focused pins the lane and not the graph's node, the button unpins it, the pin's head is drawn in the accent after
  the pinned lane's name and on no other, a lane pinned from the bottom of thirty stays in view at the top, and the
  next publication keeps it pinned. They caught each of 17 mutations.

- Revision 386 was built and tested in the same Linux container: Debug and Release each ran **1,675 tests**, passing
  **1,577 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its test runs `icat channels`
  over the fixture's one paired channel, which says beneath its line that its ends were paired by the transport rule,
  correlated, open before the capture and after it, coverage unknown, in the words the inspector's own tests read
  with the key E lists. They caught each of 7 mutations.

- Revision 385 was built and tested in the same Linux container: Debug and Release each ran **1,674 tests**, passing
  **1,576 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its test runs `icat processes`
  over PID 100 created, exited and created again, whose second holder's two sends bind only as candidates: the list
  says the inspector's sentence of the later holder - 2 records left out - and the option that counts them, and none
  of the first; `--pid 100` says each holder's, the first "the first of 2 processes"; and the document counts its
  holders, candidates and withheld records under the default policy and under candidates, where the hint is gone. The
  inspector's own tests read the moved words unchanged. They caught each of 16 mutations.

- Revision 384 was built and tested in the same Linux container: Debug and Release each ran **1,673 tests**, passing
  **1,575 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its test aligns a session
  whose ledger covered its first readings, delivered none after them and then lost 3 events with one 50 µs later that
  has no ledger and a third aligned to nothing, under a note, and runs `icat workspace timeline`: each sentence is the
  window's, the first column with records is a row and an empty one is not, and the runs its capture did not cover
  read "columns 40–120 unknown; columns 121–160 partial gap"; its document carries each lane's 160 columns with the
  window's coverage, the offset lane's own ticks 500 before the investigation's, the unplaced lane's gap, and the
  note, 20 columns over an interval as asked, and wrong columns, a wrong interval or an option of another verb are
  refused in words. A unit test names a single column's run and longer ones. The window's own tests read the moved
  words unchanged. They caught each of 24 mutations.

- Revision 383 was built and tested in the same Linux container: Debug and Release each ran **1,671 tests**, passing
  **1,573 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its Application test compares a
  client whose capture collected TCP alone and lost 2 events with a server that has no ledger: each mechanism's state,
  in member order, and the three sentences that say a partial gap, UDP not collected, and both unknown; then two
  captures that covered both, a reduced fidelity stated by hand, shortfalls several captures share, said once of them
  all, and no capture. `icat workspace correlate` over two hosts' captures with no candidate states each one's
  coverage, that no candidate is no proof, the sentences, and each state with its fact in
  `workspace-correlation-v6`; the window says two ledger-less captures' unknown coverage in one sentence in the caution
  ink, and hides it where two captures covered both, whose notes say so, until a third without a ledger joins them.
  They caught each of 27 mutations.

- Revision 382 was built and tested in the same Linux container: Debug and Release each ran **1,668 tests**, passing
  **1,570 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. A unit test says a connection's
  bytes in each case - every send unmeasured and no receive, one of each, none either way, a measured zero, and a
  German culture's separators - and another the window's directional words, a count of one agreeing with its noun;
  the rung's fixture says its receive of no size in parentheses and its datagram flow's missing receive, the window's
  candidate end "no receive recorded", and the inspector "no send recorded" and "no receive recorded". `icat workspace
  correlate` over a client whose one send stated no size and two captures of its server, which received it with none,
  says "1 send unmeasured" and "1 receive unmeasured", never ": 0 B sent", and "not the only match: 1 other
  candidate", and writes each end's counts and its null sums in `workspace-correlation-v5`. They caught each of
  22 mutations.

- Revision 381 was built and tested in the same Linux container: Debug and Release each ran **1,665 tests**, passing
  **1,567 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its window test aligns a session
  whose ledger covered its first readings, delivered none after them and then lost 3 events, beside one with no
  ledger: the middle of each run of columns is drawn plain, as a thin strip at the lane's foot, or hatched through the
  lane, read from the rendered frame in the caution ink; each run is one band; and the cursor, a column of none opened
  and the lane's sentence say "covered", "unknown" and "partial gap". A second test says a column of none in each
  state's words. They caught each of 22 mutations: the hatch not drawn, a covered column hatched, the strip and the
  band swapped or moved, the guards, a run split or cut to its first column, the column's coverage unsaid, Enter offered
  on a column of none, each state's words, the status read without coverage, the intro, and the lane's sentence
  without its counts, out of order or never "all".

- Revision 380 was built and tested in the same Linux container: Debug and Release each ran **1,663 tests**, passing
  **1,565 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests run `icat operations`
  over a server call that never stopped, without ALPC and beside linked calls that followed it, whose group reads
  "untimed" three times and "no ALPC" or "not reached"; `icat exchanges` over an exchange whose response end was not
  recorded, "untimed" under "executable not witnessed · 4242"; and `icat processes --pid` over an unsized send's
  unresolved end, "unmeasured" and "none", a receiver's sender, "none" and "64 B", and a process with no transport
  record, which says so. They caught each of 11 mutations once a peer the instance sent nothing to was pinned, which the
  first run missed: each word restored to a dash, the ALPC flag inverted, the process named "PID", the answer with
  nothing measured left unread, the records unread, and the old note.

- Revision 379 was built and tested in the same Linux container: Debug and Release each ran **1,662 tests**, passing
  **1,564 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its test runs `icat processes`
  over a sender of 64 bytes, its receiver and a process whose one send recorded no size, whose rows read "64 B" and
  "no receives", "no sends" and "64 B", and "unmeasured" and "no receives", its JSON counting the last one's
  measured and unmeasured records; and over a session whose only send recorded no size, where it is still unmeasured.
  It caught each of 5 mutations: never unmeasured, none printed as "-", the answer with nothing measured left unread,
  the counts swapped, and the sends read from the receives.

- Revision 378 was built and tested in the same Linux container: Debug and Release each ran **1,661 tests**, passing
  **1,563 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests read each basis's,
  byte domain's and accounting's words, an unknown one by its number and an accounting's meaning after its label; a
  record's size in the capacity domain set apart by a comma, and in another domain followed by which bytes it counted;
  and run `icat metric` for bytes sent, whose basis, byte domain, accounting, contributions heading and sides read in
  the window's words and never by an enumeration's name. They caught each of 12 mutations once the capacity size was
  pinned, which the first run missed: each word changed or an unknown one guessed, each raw name restored in `icat
  metric`, the meaning without its label, and the selector's heading not set in capitals. The computed tier `icat
  measure` and `verify` print runs only on Windows and is untested here.

- Revision 377 was built and tested in the same Linux container: Debug and Release each ran **1,658 tests**, passing
  **1,560 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests read each
  acquisition's, loss layer's and omission's words, an imported file's losses as the file's, a reported zero said, and
  an unknown value by its number, and that a coverage reason says a loss in the same words; and run `icat session` over
  a live capture, whose losses it lists in layer order though its ledger keeps them in another, and over an imported
  file with a provider the capture did not request, none of it split from an enumeration's name. They caught each of 9
  mutations: the acronym split or a word capitalized, the file's and the session's losses swapped, a layer's words
  changed or an unknown one guessed, each raw name in `icat session`, and the losses left unordered.

- Revision 376 was built and tested in the same Linux container: Debug and Release each ran **1,657 tests**, passing
  **1,559 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests read every binding
  reason's words, each its own and none an enumeration's name, an unknown one by its number, and the window's evidence
  row in them; run `icat operations`, `exchanges`, `processes` and `metric --group-by process` under direct evidence
  only over an RPC call and an HTTP exchange no lifecycle record names, each saying why in those words; and judge the
  caveats: two later-instance sends of a reused PID are its candidates under the default policy, while direct evidence
  only says all three sends, and all six calls of an RPC capture, bind by where their readings fall. They caught each of
  10 mutations: the old narrow words, an unknown reason by its name, each layer's raw name, both caveats' branches, and
  the caveat's agreement.

- Revision 375 was built and tested in the same Linux container: Debug and Release each ran **1,653 tests**, passing
  **1,555 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its test runs `icat overview`
  over a capture that collected TCP alone and over a generation without a ledger: each report states the words the
  window's snapshot of the same overview reads beneath its time scope, "Coverage: covered for TCP · no other mechanism
  collected" and "Coverage unknown: …". It caught both of 2 mutations: the line dropped, and the words of one
  mechanism alone.

- Revision 374 was built and tested in the same Linux container: Debug and Release each ran **1,652 tests**, passing
  **1,554 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its test runs `icat processes`
  over a capture that collected TCP alone and over a generation without a ledger: each states the coverage line
  `icat metric --group-by process` states of the same answer, word for word, its JSON carries TCP's state and reason,
  and a process whose one send's size its source did not expose is said to have no transport byte measured, never to
  have sent none. It caught each of 6 mutations: the coverage line dropped or not passed, the JSON's coverage null or
  its states the mechanisms' names, the ledger ignored, and the old zero claim.

- Revision 373 was built and tested in the same Linux container: Debug and Release each ran **1,651 tests**, passing
  **1,553 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests run `icat operations`,
  `icat exchanges` and `icat channels` over a capture that collected TCP alone, which say RPC and HTTP were not
  collected and that the capture did not collect ALPC, and TCP's coverage with its fact, in words and in their JSON;
  over one that collected ALPC and was delivered none, which says the generation holds no ALPC record and that ALPC was
  covered; and over a generation without a ledger, which says nothing was judged and never "did not". A page of
  channels carries TCP's coverage as the rule judges it; the channel browser announces it as the reason an empty list
  holds none, without saying it twice, and states it beside the caveat under a list; a call's other end with no ALPC
  record to follow, and the metric that needs one, no longer say what the capture collected. They caught each of 16
  mutations: the ALPC branch inverted or given RPC's state, the RPC sentence dropped or given ALPC's, the empty note's
  old claim, the mechanisms' order, HTTP's and TCP's sentences dropped or another mechanism's, the page's coverage
  changed, the browser's status without its reason, its caveat without the coverage or saying it twice, and the old
  words in the other end and the metric.

- Revision 372 was built and tested in the same Linux container: Debug and Release each ran **1,648 tests**, passing
  **1,550 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests write each export's
  CSV with rows and without: every line ends with `row_present` and the scope's coverage, after every column it had, a
  scope with no row keeps its context, its ranking and its coverage on one line, and coverage nothing judged is an empty
  cell. A sharing report is given coverage judged by the rule from ledgers whose one provider is named with a secret,
  through every reason the rule can give, whose words are pinned: neither the name nor the id reaches its JSON or CSV,
  ranked or evidence, with rows or none, and both state each mechanism's coverage and the inspector's words for it.
  Through `SessionExport`, the window and `icat export`, an empty CSV of a range the capture delivered nothing in says
  so on its one line, and a sharing report of it, the window's or `icat export --share-redacted`'s, says what the card
  says. Two ranking-export tests that pinned the old line endings now read their cells by column name. They caught
  each of 18 mutations: a trailing column renamed, `row_present` always true, the coverage cell empty, an empty scope's
  line dropped, short or without its ranking, the sharing report's reasons, states or summary dropped or emptied, its
  CSV cells or headers changed, and `icat export --share-redacted` not printing the coverage.

- Revision 371 was built and tested in the same Linux container: Debug and Release each ran **1,644 tests**, passing
  **1,546 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests read a page of two
  sends, one in each of a capture's epochs, the second of which lost an event: each record's coverage is its epoch's,
  a mechanism its epoch did not collect is not collected, a reading past every delivered one - even the last tick
  there is - is unknown rather than failing, and a generation without a ledger judged nothing. In the window, the
  same two sends' Coverage fields say "covered: …" and "partial gap, not extrapolated: …", and a ledgerless
  generation's says so. They caught each of 6 mutations: a record's coverage the whole session's or another
  mechanism's, a reading at the last tick unbounded, a page keeping no ledger, the window keeping no page to read it
  from, and the coverage said without its state.

- Revision 370 was built and tested in the same Linux container: Debug and Release each ran **1,642 tests**, passing
  **1,544 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests open a paired
  channel, an RPC channel and a process's HTTP exchanges on their own rungs, where the inspector's title and subtitle
  are those it gave the row where it was chosen and a paired channel's explanation is its pairing, the process's
  lineage hidden; restore a newer publication there, which describes the channel too; choose a process there, which is
  then described with its lineage and binding; and hear the subtitle change when an RPC channel's calls are read. Two
  tests had pinned the old behaviour, the inspector naming the process on a channel's rung, and now read the new. They
  caught each of 13 mutations:
  - the title or subtitle not the opened channel's, a process chosen there ignored, and the process the rung was
    reached with taken for a choice;
  - the reached-with process not kept on navigating, not carried in the memento, or forgotten by a newer publication;
  - an RPC channel's, HTTP exchanges' or paired channel's row not rebuilt, the pairing not explained, the process's
    lineage shown over the channel, and the window not told when the RPC channel's row is read.

- Revision 369 was built and tested in the same Linux container: Debug and Release each ran **1,642 tests**, passing
  **1,544 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests read every direction
  and operation state in words, a code no version knows by its number, an operation's state as a real call's for the
  two states both name, a channel row in every direction, the window's direction-row options, and the tour's operations
  on its HTTPS, RPC and shared-section channels: what each did, which way and when, and how it ended. They caught each
  of 14 mutations:
  - bidirectional said as "both directions", an unstated direction said to carry no data, an unknown code guessed as
    no data direction, and no data direction said as "none";
  - an operation open at capture end, without its start, or failed said otherwise, and an unknown state by its
    enumeration;
  - a channel's or an operation's direction, or an operation's state, sized or not, by its enumeration, and a title or
    a direction row's label not raised.

- Revision 368 was built and tested in the same Linux container: Debug and Release each ran **1,639 tests**, passing
  **1,541 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests read every kind,
  layer, field availability, capability state, tier and overhead in words, and a code no version knows by its
  number; a record without a size under each availability, in the rail, its spoken name and the inspector's Size
  field; and `icat session`'s summary and rows, `icat metric`'s evidence rows and projection, and `icat capabilities`'
  sources and mechanisms, read column by column whatever this machine reports, so the check holds on a Windows build
  whose TCP has a measured tier and a fixture. It replaces revision 365's pattern for that row, which held only where
  every mechanism is unsupported. They caught each of 26 mutations:
  - a kind run together or by its enumeration, an unknown kind not named by its number, and a layer, a state, a tier
    or an overhead by its enumeration;
  - a redacted or a lost event's size said otherwise, every missing size said to be withheld, a size claimed present
    said to be present, and one that does not apply given a reason;
  - the inspector's Size field repeating "size", in the record text and in the window, and an unknown kind's title;
  - `icat session`'s kind, missing size or layers, `icat metric`'s kind, missing size, mechanism or projected layer,
    and `icat capabilities`' states, tier or overhead by their enumerations.

- Revision 367 was built and tested in the same Linux container: Debug and Release each ran **1,636 tests**, passing
  **1,538 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests export a capture that
  delivered readings from 0 to 20 and from 40 to 60: over the whole session, every mechanism's state and fact in the
  JSON and the inspector's words last among the caveats; ranked within the gap, every row at none and every mechanism
  unknown; the gap's records, none and complete, said the same; and a generation without a ledger. In the window, the
  whole session's export, a brush over the gap and the records of a covered range each end with the words the
  inspector states for that scope, and each is `icat export`'s file byte for byte. `icat export`'s report prints the
  words beneath its fields; the check that a report's last fields are written as its command ends moved to `icat
  checkpoint`, whose report still ends with them. They caught each of 12 mutations:
  - the coverage words left out, or put before the other caveats;
  - a ranked or an evidence context keeping no coverage, and an evidence export saying none;
  - the JSON listing no mechanism, or a state without its fact;
  - `icat export` ranking an interval, or reading its records, with the whole session's coverage, and the window doing
    either;
  - `icat export` printing no coverage beneath its report.

- Revision 366 was built and tested in the same Linux container: Debug and Release each ran **1,634 tests**, passing
  **1,536 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Run alone, the lane-scale test
  passed 12 runs pinned to one processor shared with six busy loops and 60 runs in one process, and five runs of the
  whole window suite beside three other suites looping never failed it. Three copies of it running together, in a probe
  since removed, failed 2 rounds out of 15 in two runs, each at the check after the descent to a process's direction
  rows, with the pointer still on the timeline and a hover card over the bar it read; with the pointer taken off first,
  three copies passed 45 rounds out of 45. Leaving the pointer where it was now fails the test at its first check after a
  card was read, since the test says first that no card is drawn.

- Revision 365 was built and tested in the same Linux container: Debug and Release each ran **1,634 tests**, Debug
  passing **1,536 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests read every
  mechanism's name for a lane and for a sentence and every state's value and label, and the command line's tables over a
  capture with two epochs: `icat timeline`'s buckets; `icat session`'s summary, coverage table, list of what was not
  collected, each epoch's losses and rows; `icat capabilities`' mechanisms; and a content request's preview, none naming
  an enumeration. A test that had pinned "mostly Named pipe" in the inspector now reads "named pipe". They caught each
  of 18 mutations:
  - a partial gap's value without its qualifier, unknown's value and label exchanged, and a label taking another
    state's value;
  - a named pipe keeping its capital in a sentence, and an abbreviation lowered;
  - the window's rows, and a process's explanation, stating the enumeration;
  - by their enumeration: the timeline table's mechanism or coverage, the session's summary, coverage table, list of
    what was not collected or rows, the capabilities table's mechanism or coverage, and the content request's mechanism;
  - each epoch's losses unlabelled.

  Release passed 1,535: it also failed revision 342's lane-scale window test once, at its pixel check after a descent,
  the second recorded failure and the first since revision 357 made it wait for the timeline's detail after each
  descent. It passed six runs alone and a rerun of the whole Release window suite, so it is intermittent and not this
  slice's; revision 366 found its cause, a hover card left over the bar it read, and took it away.

- Revision 364 was built and tested in the same Linux container: Debug and Release each ran **1,632 tests**,
  passing **1,534 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests read the
  sentence for each coverage state, a lifecycle named in a sentence, and the caveat an answer over RPC calls gives under
  an imported ledger; and `icat metric` for every mechanism, for TCP over a gap and over the session, and for a
  generation without a ledger, its projection named as a lane is. Two tests had pinned the enumeration's names, "is
  UnknownCoverage" in a ranking note and "is PartialGap" in a rate's caveat, and now read the words. The call-time and
  errors rankings' notes, which no test read, are read now. They caught each of 19 mutations, two of them once those
  notes were read:
  - four states said as their enumeration, a partial gap not said to be unextrapolated, a sentence starting in lower
    case, and one without the fact behind it;
  - a lifecycle named as its lane in a sentence, and TCP by its enumeration;
  - the operations, rate and export caveats, and each of the three ranking notes, naming the state's enumeration;
  - `icat metric` stating one mechanism as a scope, its session said as the selected interval, nothing judged where no
    ledger was published, and its projection naming the enumeration.

- Revision 363 was built and tested in the same Linux container: Debug and Release each ran **1,630 tests**,
  passing **1,532 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its command-line
  test reads `icat evidence` over a capture that delivered readings from 0 to 20 and from 40 to 60: the whole session
  and a range within an epoch say TCP was covered, a range between them lists no row and says its coverage is unknown,
  its JSON names each mechanism's state and reason, and a generation without a ledger says it judged nothing. The
  inspector's tests read the same words from `CoverageText`, whose own tests moved with them. They caught each of 16
  mutations:
  - a page without its coverage or stating the whole session's for its interval, the coverage left unsaid, the
    document still named `evidence-page-v2`, and the caveat still sending readers to the ledger;
  - the whole session judged as a range no reading falls in, and such a range given the whole session's coverage;
  - where the words moved: the worst state named first, what was not collected left unsaid, three names joined by
    "and" alone, nothing collected not short, covered mechanisms split by their counts, unknown for different reasons
    said as one, and process lifecycle named as its lane;
  - the card describing nothing, and its limit judged on nothing.

- Revision 362 was built and tested in the same Linux container: Debug and Release each ran **1,629 tests**,
  passing **1,531 with 4 skipped**; the 94 failures are the ones revision 361's baseline lists. Its tests brush a
  session whose capture delivered readings from 0 to 20 and from 40 to 60: within each epoch, between them, as the
  visible range and over the whole session, with E opened where nothing was delivered. They carry a stand-in into the
  publication that brings the ledger, refuse a count, show a session with no source to count from, and read the words
  for an import's quiet UDP, an undecodable TCP record, three delivering mechanisms, a coarse clock's sub-tick brush and
  a state this version never derives. In the window they read the line beneath the range, its ink, and its wrap at the
  smallest window. They caught each of 34 mutations; two first failed to build, as constant properties the analyzers
  flag, and were caught in forms that read the view's state:
  - an interval stated with the whole session's coverage, or one no reading falls in given it, or the overview's or the
    interval's coverage not carried into the workspace;
  - unknown for different reasons said as one, an uncollected mechanism named, what was not collected left unsaid or
    said where everything was, covered mechanisms split by their counts, the worst state named first, a partial gap not
    said to be unextrapolated, an unknown state without its fact, and nothing collected or nothing judged read
    otherwise;
  - a gap, a partial gap or nothing collected not short, or nothing judged short;
  - process lifecycle named as its lane, three names joined by "and" alone, and one name listed as the last of several;
  - a range said counted before it is, the previous range's coverage carried to a new one, and a range no source
    counts, or a failed count, said to be read;
  - no ledger, or a stand-in, shown as a limit, a limit claimed before the range is counted, and an empty statement
    shown;
  - E's empty rung pointing at the health strip;
  - the window not told when the counts arrive or the scope changes, the limit not in the caution ink, and the line
    hidden or bound to the range.

- Revision 361 was built and tested in a new Linux container. Its baseline at revision 360 ran **1,623 tests** in Debug
  and Release, each passing **1,525 with 4 skipped**. Its 94 failures are the Windows and font-metric ones revision 293
  leaves, with one font metric more than the last container's 93: here the header's title at 1080×700 has 121 px of the
  150 its test asks, and the investigation window at its least height 162 of the 165 px its first two sessions need.
  Revision 361's runs fail the same 94 in both configurations. Its test passed 8 runs out of 8 alone. In a probe since
  removed, holding the pool, the old form failed 4 runs out of 4 and the new one passed 4 out of 4. It caught two of
  three mutations of the wait it guards: a reader that does not wait, and a wait shorter than the 200 ms removal; a wait
  without a bound hung it until it was killed at 120 s.

- Revision 360 was built and tested in the same Linux container: Debug and Release each ran **1,623 tests**. Debug
  passed **1,526 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Release
  passed 1,525: it also failed I18's "a reader waits out a writer's removal instead of failing its lease" once, a
  storage test this slice does not touch, whose reader gave up after its 1 s wait. Run alone it passed 8 times out of 8.
  Its writer is a thread-pool continuation that releases the guard after 200 ms, so the likeliest cause is that the
  whole suite's load delayed it past the reader's wait. Revision 361 found that load alone did not, but a held pool
  did, and releases the guard from a thread of the test's own. Its tests keep, replace, remove and refuse an
  investigation's panes, read a version 14 file and state them in `icat workspace show`.
  In the window they drag the splitter, step it with a key and let the timeline fill the column. They open a session on
  its own, then one from the investigation, and drag each pane to its least height. At its least height the
  investigation's window keeps two sessions in view with the panes said beneath them. They caught each of 22 mutations:
  - the share kept unrounded, a split alone keeping nothing, or a share of 0 or 1 taken;
  - a version 14 file keeping panes or refused, a file's panes keeping nothing, or not checked at all;
  - the split said of the graph while the graph fills the column, or said as 0%;
  - `icat workspace show` leaving the panes out of its document or its words;
  - a pane filling the column, a drag let go or a key on the splitter not kept, or a drag kept while held;
  - the kept panes not put back, the notice leaving them out, or the investigation's window not told, or never
    reading them;
  - a split hiding a pane, a hidden pane keeping its least height, or the split read from the hidden rows.

- Revision 359 was built and tested in the same Linux container: Debug and Release each ran **1,620 tests**, passing
  **1,523 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests read an
  aggregate's filter chip and scope after E at three rungs, and a named set whose membership a newer generation shrank.
  They caught each of five mutations:
  - a named set reading as selected processes;
  - the resolver forgetting the name, or reading every set as named;
  - the aggregate's filter showing a count;
  - one process of an aggregate said in the plural.

- Revision 358 was built and tested in the same Linux container: Debug and Release each ran **1,620 tests**, passing
  **1,523 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its window test
  double-clicks plainly, then presses where a pan, a brush and an Esc-cancelled press began, and in the graph where a
  cancelled node drag began. It caught each of six mutations:
  - the timeline zooming on any second press, or forgetting a plain click;
  - a cancelled press keeping the click before it;
  - the graph opening on any second press;
  - a cancelled press in the graph counting as a click, whether its release or its cancel was mutated.

  The release mutant's first form did not build, since a field assigned and never read is an error; its second form was
  caught. Writing the test found that a cancelled press still counts toward the platform's click count, so the press
  after it is a double click's second press and the one after that a third, which makes no double click. The test
  clicks elsewhere first, so its last two presses are a double click of their own. Revision 352's finding that "two
  presses at one point make a double click, which zooms, however the first ended" now holds only for a plain first
  click.

- Revision 357 was built and tested in the same Linux container: Debug and Release each ran **1,619 tests**, passing
  **1,522 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests read a
  count of one in the window's error ranking, in `icat processes`, `channels`, `timeline` and `overview`, in the peer
  and RPC metric caveats and in the overview's disclosure. They caught each of twelve mutations:
  - one counted as many;
  - the verb agreeing with many;
  - one record outside the capture, or the only record of a process, said in the plural;
  - one unpaired failure "report", and one unknown status "they";
  - one TCP row without a peer "have", one PID "of them", and a one-record connection "records";
  - one record of neither side "state", one call with no status "are counted", and one row held by no process
    "name".

  Two existing tests had pinned the slips themselves, "1 of them report a failure status" and "The other 1 name no
  owner"; they now read the corrected words. A first Release run also failed revision 342's lane-scale window test once,
  at a pixel check after a descent: 1 in 188 recorded suite runs. It passed 14 reruns, and a run that stopped there drew
  no hover card and counted the lanes on the same columns, so the cause is not confirmed. The test now asks for the
  timeline's detail at once after each descent, and waits for it before reading lanes or pixels, removing the one
  asynchronous redraw that could land mid-check. A second Debug run failed revision 355's channel-rung card test, which
  read the card just after choosing the channel, while its bytes could still be "reading bytes…", and compared that
  with the card after the descent, by then read. It now waits for the selection's bytes before each reading, and passed
  ten runs in each configuration; the third pair of runs is the one above.

- Revision 356 was built and tested in the same Linux container: Debug and Release each ran **1,617 tests**, passing
  **1,520 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its test chooses
  the quiet node at the machine rung and the context node at a group's rung and on a channel's rung, and compares the
  card, the timeline's highlight and what E lists. It caught each of five mutations: the card counting no member of an
  aggregate, an aggregate counted as one process, E listing the rung for an aggregate, an aggregate reading no bytes,
  and a channel rung's card ignoring an aggregate chosen there.

- Revision 355 was built and tested in the same Linux container: Debug and Release each ran **1,616 tests**, passing
  **1,519 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests, which
  open a paired channel, an RPC channel and HTTP exchanges from a process's rows and compare the card with what E then
  lists, caught each of eleven mutations:
  - an RPC channel's, HTTP exchanges' or paired channel's rung counting its process;
  - both ends chosen there counted as the channel;
  - an RPC channel counted by its calls rather than its call records;
  - an RPC channel headed as a paired one;
  - the card not told when the calls are read, which only the window's test sees;
  - the summary, or the heading, staying on the process;
  - a brushed channel counted over the whole session;
  - the HTTP bytes leaving out what was received.

  A first form of the tests read the card just after the way back from E, while the RPC channel's rung was reading its
  calls again: the card then says "Reading calls…", as its level line does.

- Revision 354 was built and tested in the same Linux container: Debug and Release each ran **1,614 tests**, passing
  **1,517 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of twenty mutations. Thirteen had one of the evidence source's reads that revision 338 left untested read
  correlated evidence only, whatever the window counts:
  - a process's RPC channels, its calls, the calls through one, and the calls' spans;
  - its HTTP exchanges as a row, as a page, and as spans;
  - its one-sided connections;
  - the call and peer rankings;
  - an interval's bytes, a group's process lanes and a process's direction rows.

  The other seven: the empty rung saying a later holder's records are one step away; the rung offering nothing; the
  offer not raised when the rung changes; the reason saying one record in the plural; each call ranking saying one call
  in the plural; and the rung's button counting correlated evidence. The rung's first "offers nothing" mutant did not
  build, since a property that reads nothing can be static, which the analyzers refuse; its second form was caught.
  Writing the test found the empty rung's misleading reason and the call rankings' "1 more completed calls belong".
  No read was found ignoring the policy.

- Revision 353 was built and tested in the same Linux container: Debug and Release each ran **1,611 tests**, passing
  **1,514 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its test, which
  reads the card with nothing selected at the machine and group rungs and over a chosen interval, then presses E, caught
  each of seven mutations: an empty card naming nothing, the records named over the rung rather than the scope the rows
  count, a selected process named as the rung's records, the evidence rung naming its records as E's, the tour naming
  its rung's records, the heading staying on the process, and the summary staying empty. An existing test had pinned
  the old heading, "Selected process", at the machine rung with nothing selected; it now reads the records E lists.

- Revision 352 was built and tested in the same Linux container: Debug and Release each ran **1,610 tests**, passing
  **1,513 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its test, which
  drives each gesture with Esc mid-drag, caught each of eight mutations: a cancelled pan keeping its view, the timeline
  cancelling nothing, a cancelled brush applied on release, a cancelled minimap move keeping its view, the minimap
  taking the view a press began from after the press moved it, a cancelled node drag still pinned, Esc ascending
  mid-drag, and Esc both cancelling and ascending. A first form of the minimap's second mutant did not compile, since a
  field never written is an error; its second was caught. Writing the test found that two presses at one point make a
  double click, which zooms, however the first ended; the test's gestures each begin at a point of their own.
- Revision 351 was built and tested in the same Linux container: Debug and Release each ran **1,609 tests**, passing
  **1,512 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its test caught
  each of three mutations of `icat timeline`: no start read, no time base stated, and the start read of no
  calibration. A first form of the last did not compile, since a constant null pattern never matches; its second was
  caught. The plan's §6.7 change is to its words alone.
- Revision 350 was built and tested in the same Linux container: Debug and Release each ran **1,608 tests**, passing
  **1,511 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. The repaint
  allocation test still finds nothing allocated, the time base formatted once per session. Its tests caught each of
  twelve mutations: no start read, the first sample's reading taken for session time 0, another clock's calibration
  read, the overview or the snapshot carrying no start, a negative offset read as positive, the reader's zone ignored,
  the offset left unsaid, the axis saying no time base or only session time, a narrow axis saying nothing rather than
  session time, and the spoken status leaving the time base out. Writing it found nothing else: at the smallest window
  the axis has 510 px, room for the whole statement.
- Revision 349 was built and tested in the same Linux container: Debug and Release each ran **1,605 tests**, passing
  **1,508 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its test, which
  reads the live edge's bars from the pixels drawn, caught each of five mutations of the drawing: a lane's preview
  ignoring its own peak, beside byte lanes reading their byte peaks or their bytes scale, one scale reading each lane's
  peak, and a preview read against its own busiest bin instead of the published bars' peak. The last was missed at
  first, since every bar the test read stood under its row's top either way; previewed sends at about half the
  published rate now stand under it on one scale, where their own scale would lift them to it.
- Revision 348 was built and tested in the same Linux container: Debug and Release each ran **1,604 tests**, passing
  **1,507 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of nineteen mutations. Eleven concerned the chosen row: one chosen at the wrong rung, a connection not chosen, the
  tour following too, an RPC channel or HTTP exchanges named a channel or highlighted as one, an RPC channel claiming
  bytes, HTTP exchanges stating none, a connection not saying its other end is unheld, and a channel counting its row
  rather than its two ends. Eight concerned the view: the highlight, the card's heading or count, or E keeping the
  process; E's filter calling the exchanges "this"; choosing the process again changing nothing; the card not restated
  as a row is chosen; and a chosen channel's bytes left unread. Three were missed at first - the tour, the restated
  card and the unread bytes - and each now has a test: the tour's card, the change the window binds to, and a channel
  chosen under a brush, whose bytes no other description had read. Writing them found that choosing the process of
  its own rung again, as a click on its node does, changed nothing while a row was described, so the card could not
  be brought back to it.
- Revision 347 was built and tested in the same Linux container: Debug and Release each ran **1,602 tests**, passing
  **1,505 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of nineteen mutations. Seven were in the shared words: a series joined with ", and", per second, one node, a
  reused PID's candidates or each lane's own scale left unsaid, a default ranking said, and a layout's pins counted as
  none. One was `icat workspace show` saying nothing of a layout. The other eleven were in the windows: the notice
  putting back nothing; no investigation window told of a write; a write told during a reading not kept, not put back,
  or put back forever; the selection lost; another investigation's window told; the row's line hidden or left out of
  its spoken name; a reading listing no kept layout; and the line saying what is kept without what it means.
- Revision 346 was built and tested in the same Linux container: Debug and Release each ran **1,601 tests**, passing
  **1,504 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its test caught the
  one mutation that matters, E ignoring the chosen set. A guard for a chosen relationship beside a set was dropped as
  dead: choosing either lets the other go.
- Revision 345 was built and tested in the same Linux container: Debug and Release each ran **1,600 tests**, passing
  **1,503 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its test caught
  each of three mutations: a chosen relationship followed only at the machine rung, a chosen process followed only
  there, and the filter's reason always naming the machine rung. E with several processes chosen still listed the one
  the keyboard was on, while the card counted the set; revision 346 has it list the set, as Enter does.
- Revision 344 was built and tested in the same Linux container: Debug and Release each ran **1,599 tests**, passing
  **1,502 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its test caught
  each of ten mutations: the graph's toggle expanding nothing, the other pane left shown, the expanded pane keeping half
  the column, a restore forgetting the split, the splitter left in place, F11 expanding the other pane, F11 restoring
  only from the pane, a text box swallowing F11, the timeline's toggle keeping its old state, and F11 with the keyboard
  in neither pane expanding the graph. The split's mutant first failed to build, since a field written and never read is
  an error; its second form was caught. Writing it found that F11 pressed from the search box never reached the
  window's shortcuts, which a text box keeps for itself; F11 edits no text, so it is handled before them.
- Revision 343 was built and tested in the same Linux container: Debug and Release each ran **1,598 tests**, passing
  **1,501 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of thirteen mutations: a layout that keeps only the lane scale keeping nothing, a layout dropping it, a version 13
  file allowed to name it, a version 13 file refused, `icat workspace show` leaving it unsaid or keeping its old
  resolution version, and, in the window, the kept lane scale not read, written as shared, not seen when it alone
  changed, not kept on a toggle, not put back, left out of the notice, or the notice naming the old three settings.
- Revision 342 was built and tested in the same Linux container: Debug and Release each ran **1,598 tests**, passing
  **1,501 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of twenty-two mutations. Ten were in the view model: the legend keeping its old scope, always saying one scale or
  never naming bytes, no lane view offering the toggle, the key staying where no lanes are drawn, the bytes keeping the
  records' key, a card always saying shared, a publication or a restore forgetting the choice, and the machine row's
  card naming the scale it no longer shares. Twelve were in the timeline: the choice inverted, a mechanism lane reading
  the shared peak, a mechanism, process, direction or end row drawn on the shared scale, a row reading no peak, an end
  reading its outbound band alone, a process lane reading no byte peak, the axis stating one peak, a card reading the
  shared peak, and the peaks never growing past sixteen rows. The drawing mutants are caught by the pixels the window
  draws: a quiet lane's busiest bar reaches its row's top only on its own scale. The live preview's bars follow each
  lane's own scale too, with no test of their own for it. The repaint allocation test now measures every rung with each
  lane on its own scale as well, and finds nothing allocated.
- Revision 341 was built and tested in the same Linux container: Debug and Release each ran **1,597 tests**, passing
  **1,500 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of four mutations: a card naming a process by its label alone, a card naming a process no executable named
  twice ("PID 100 · PID 100 #2"), an edge naming its ends by label, and a node's card naming its process by label.
- Revision 340 was built and tested in the same Linux container: Debug and Release each ran **1,597 tests**, passing
  **1,500 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of thirteen mutations: two holders left unnumbered, every PID numbered, a caption dropping the number whether
  named by its executable or by its PID, a row, the inspector or a lane dropping it, a graph node projected, re-counted
  or drawn without its holder, a node's PID line or its card's title ignoring it, and E scoping to the bare executable
  name. That last one came to light while the slice was written: E at the machine rung named its scope by executable
  alone. The test of that path used a process no executable named, whose caption is its bare PID, so it could not
  tell the two apart; a new test names one.
- Revision 339 was built and tested in the same Linux container: Debug and Release each ran **1,593 tests**, passing
  **1,496 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of fifteen mutations. Six were in the layout: correlated evidence kept by name, a layout that keeps only a policy
  keeping nothing, a layout dropping its policy, one keeping a policy no view offers, a version 12 file allowed to name
  a policy, and a file naming a policy no view offers read. One refused a version 12 file. Six were in the window: an
  investigation's policy not put back, a layout written without its policy, a changed policy alone not written, a
  chosen policy not kept, the notice silent on candidates, and three things put back joined with "and" twice. Two
  were in `icat workspace show`: the policy left unsaid, and the resolution keeping its old version. Writing the slice
  found that a layout could name a policy the window cannot show: direct evidence only would have been put back with
  no word in the rail, since the rail speaks only of candidates. A layout now keeps only candidates, and a file naming
  another policy is refused.
- Revision 338 was built and tested in the same Linux container: Debug and Release each ran **1,593 tests**, passing
  **1,496 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of seventeen mutations. Five had one of the evidence source's reads (a brushed count, a byte ranking, the timeline's
  focus, a page of evidence, an export's records) read correlated evidence only. The others covered a live publication
  ignoring the window's policy, the workspace reading under the default whatever it shows, a choice re-projecting under
  the policy shown, candidates offered for any selection, and a process's or group's explanation silent on counted
  candidates. They also covered an export not saying so, the rail always saying so, the offer's words left unwrapped, a
  first holder's records counted as candidates, a scope read ignoring its policy, and a session opened keeping the last
  one's policy. The live publication's first mutant did not build, since a constructor parameter left unread is an
  error; its second form was caught. The offer's label first ran past the inspector's card, cut at "candidat", which
  only a person or a test of that moment sees. It wraps now, and the window's test holds every text of the offer and of
  the counted view whole, at the test's size and the smallest. A process's RPC, HTTP and one-sided connection reads,
  the call and peer rankings, and the lanes pass the policy too, with no test of their own for it.
- Revision 337 was built and tested in the same Linux container: Debug ran **1,589 tests**, passing **1,492 with 4
  skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Release passed 1,491: one more test
  failed. "§3.2: a rung's timeline counts the records E reads from it" asserts the moment before a count lands,
  "Counting records owned by…", on a test thread with no message loop. There the view model's answer resumes on a pool
  thread, which can apply it before the test's next statement, and in Release it once had: one failure in 146 recorded
  runs. That test, and a sibling that asserts the same moment, now run on one thread with a message loop, as the app's
  dispatcher runs them and the file's other tests of that moment already did, so a count lands only when the test
  awaits it. Both passed in ten runs of their class in each configuration, and the Desktop project's 139 tests in
  each. The revision's tests caught each of five mutations: a named executable read as unwitnessed, nothing left out
  said, every member counted as a later holder, the tour's groups explained, and a grouping headed as a pairing.
- Revision 336 was built and tested in the same Linux container: Debug and Release each ran **1,588 tests**, passing
  **1,491 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of ten mutations: a channel that names no rule, a candidate pairing read as correlated, an unseen opening and an
  unseen closing each read as seen, a half-seen lifetime read backwards, a correlated pairing read as a candidate, a
  pairing headed as a count, a channel no rule paired explained, a new selection not restated, and the heading fixed in
  the markup. The heading's first mutant did not build, since a constant heading reads no instance data, and its
  second form was caught. Only the two lifetimes that the capture saw whole or not at all had any test before; a test
  now holds all four.
- Revision 335 was built and tested in the same Linux container: Debug and Release each ran **1,584 tests**, passing
  **1,487 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of ten mutations: a withheld count taken from what the policy admits, every process its PID's first holder, a
  PID's holders counted over every PID, no withheld count carried, a first holder explained as a later one, an
  eleventh read as "11st", a holder that withheld nothing said to leave records out, the explanation shown with
  nothing selected, a new selection not restated, and a group explained as a process. A stray control byte had stood
  for the "e" of "entities-v1" since revision 211, in a doc comment, the plan and the archived status; it is gone.
- Revision 334 was built and tested in the same Linux container: Debug and Release each ran **1,580 tests**, passing
  **1,483 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. The audits caught
  each of four mutations: the investigation window's status left silent, the content window with no status announced,
  the capture headline left silent, and a comparison's answer not announced. Avalonia 11.3.22's own assemblies show
  that what the audits require reaches a screen reader on Windows. A text block's automation peer raises a name change
  with each change of its text, and the Windows provider raises UI Automation's live-region event for any element whose
  name changes while its live setting is not off. Hearing it with Narrator and NVDA is still owed (open work item 2).
- Revision 333 was built and tested in the same Linux container: Debug and Release each ran **1,580 tests**, passing
  **1,483 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. The audits caught
  two of four mutations: a main-window button and a secondary window's button named by their old descriptions. The
  other two survived because they changed nothing. They removed names this revision had given a package choice and
  the empty rung's step, and both already speak the text they show. Those two names are gone.
- Revision 332 was built and tested in the same Linux container: Debug and Release each ran **1,580 tests**, passing
  **1,483 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. The audits caught
  each of three mutations, one name removed from each family of window: the raw record, a saved view's name box, and
  the content window's first byte. Two names first given to the raw record window were taken back. Its button already
  speaks what it shows, and a name on its status would have replaced what the status says.
- Revision 331 was built and tested in the same Linux container: Debug and Release each ran **1,577 tests**, passing
  **1,480 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. The allocation test
  passed in each of six runs of its project and in both suites. Two mutations checked that it still finds what it is for.
  One added a byte per row to the interval count's segment pass, the other to the focused count's, and each failed it.
  A probe of 600 isolated interval counts at 10,000 rows found 168,352 bytes on the calling thread every time; the
  failing run's 149,976 was the same count with its worker's tally allocated on a pool thread. It also found that a warm
  focused count over 100 instances allocates about 1.75 MB per call, in tallies sized by its lanes and columns, not by
  its rows. R11 allows that; the measurement says what it is.
- Revision 330 was built and tested in the same Linux container: Debug and Release each ran **1,577 tests**. Release
  passed **1,480 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Debug failed
  one more: R11's warm-aggregate allocation test measured the interval count at 0.9 B per row. That is an Application
  query this revision does not touch, and the test passed in all six re-runs and in every other suite run since revision
  325. Revision 331 root-causes it. This revision's tests caught each of seven mutations. E ignoring a chosen
  relationship, or reading neither its linked calls nor its one channel, was caught. So was an evidence card that still
  names a process, counts nothing, or states bytes for calls that carry none, and a choice that reads no bytes.
- Revision 329 was built and tested in the same Linux container: Debug and Release each ran **1,576 tests**, passing
  **1,479 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of fifteen mutations. A click on an edge that chooses nothing was caught, as was an aggregate edge's click that
  chooses one of its relationships. So was a choice that leaves the process selected, goes unhaloed, goes undescribed, or
  highlights neither a channel's records nor an RPC relationship's calls. A choice that a brush, or the list's passing
  null, lets go was caught. So was one that a process, a ranked row, a cleared selection or a new rung keeps. Last came
  a relationship let go, or unchosen in the table, that keeps its halo or its channel's highlight. Three needed a second
  pass. Two mutations did not build in their first form. A ranked row at the machine rung is a group, which already lets
  the relationship go, so the test now chooses an RPC channel's row. The channel's highlight was cleared on every path
  but the table's, which now goes through the same rule and is tested.
- Revision 328 was built and tested in the same Linux container: Debug and Release each ran **1,575 tests**, passing
  **1,478 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its test caught
  four of five mutations. It caught Enter on a view left unhandled, Enter in the name box that does not save, and a view
  of another time reference that shows nothing in silence or closes the dialog. The fifth survived because it changed
  nothing. It removed a line that chose the view with the keyboard before showing it, and the list has always chosen
  that view itself by the time the key reaches the window. The line was dead, and is gone.
- Revision 327 was built and tested in the same Linux container: Debug and Release each ran **1,574 tests**, passing
  **1,477 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of fourteen mutations. An RPC relationship that stops at its source process was caught, as were one that opens
  that process's records, and one that opens a rung without the session's records. So was a one-channel relationship
  that stops at its process. Wording was covered too: a card that says an RPC edge opens its source process, a row or
  line that does not say what Enter opens, and a count of every channel rather than the relationship's. On the window,
  the tests caught Enter left unhandled or acting only on a chosen row, an opened rung that does not take the keyboard,
  a double click that opens nothing, and a row with no key. The first form of the first mutation did not build.
- Revision 326 was built and tested in the same Linux container: Debug and Release each ran **1,573 tests**, passing
  **1,476 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of eight mutations. An export or a page that drops the RPC or HTTP key was caught, as was an export that drops
  the interval, the owners or the channel. So were a scope that cannot be read but is read as the whole session, and
  the desktop export reading as it did before. The eighth, an export whose records lose the owners the rung resolved,
  survived the first pass. The test now compares each exported record's owner with the rung's, and catches it.
- Revision 325 was built and tested in the same Linux container: Debug and Release each ran **1,571 tests**, passing
  **1,474 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of five mutations: a line that says nothing, one always shown, and one never shown or hidden. So were an
  explanation that keeps the first choice's words when the choice moves, and one that explains the table's first
  relationship rather than the one chosen.
- Revision 324 was built and tested in the same Linux container: Debug and Release each ran **1,570 tests**, passing
  **1,473 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of ten mutations. They were an RPC relationship resting on nothing, and a key that opens every link whatever its
  pair or ignores the evidence policy. Others opened only the client call, or read a key the other way round. The
  command line read an RPC key as a TCP channel or called it one, and the row and card counted the key as a channel.
  Last, a relationship that no admitted link joins read as an empty page. The reversed key took a second form: the
  first did not build.
- Revision 323 was built and tested in the same Linux container: Debug and Release each ran **1,568 tests**, passing
  **1,471 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of fourteen mutations. They were a TCP or the tour's relationship naming another rule or resting on nothing, its
  evidence out of order, and an RPC one naming the transport rule. Equality that ignores the evidence, or compares its
  list by reference, was caught, as was a version written with a leading zero. So were a tooltip naming every channel,
  a card, row or screen-reader name that names no rule, and an evidence column with no tooltip.
- Revision 322 was built and tested in the same Linux container: Debug and Release each ran **1,554 tests**, passing
  **1,457 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of eight mutations: each of the five prompts back at its fixed height, the investigation's package result at one,
  and the closing question with no Escape or no focused answer. With the prompts' own words no fixed height cut text off
  in this container, so the test also holds each prompt to the height its words take; squeezed to 150 pixels, the
  redacted report's prompt had its last paragraph and both buttons refused as cut off by the window's foot.
- Revision 321 was built and tested in the same Linux container: Debug and Release each ran **1,551 tests**, passing
  **1,454 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of eighteen mutations, from a minimum back at 460 pixels and the lanes in words outside the chart's scroll to
  colliding axis labels, an unbounded lane label, pickers that cut a name with no tooltip, and "1 records". The first
  pass missed two, a readout given one pixel and a toolbar that does not wrap. They showed the legibility test blind to a
  wrapped text kept short and to a text wholly past an edge, which it now refuses; revisions 319's and 320's mutations
  stay caught.
- Revision 320 was built and tested in the same Linux container: Debug and Release each ran **1,549 tests**, passing
  **1,452 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its test caught
  both mutations: channel rows that never wrap, and a list that scrolls them sideways instead.
- Revision 319 was built and tested in the same Linux container: Debug and Release each ran **1,548 tests**, passing
  **1,451 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its test caught
  each of three mutations: the empty rung's step back to words that cannot wrap, headings that never wrap, and a
  trimmed hint left with no tooltip.
- Revision 318 was built and tested in the same Linux container: Debug and Release each ran **1,547 tests**, passing
  **1,450 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its test caught
  each of five mutations putting the plan back into product text: a revision, an interpolated revision, a milestone
  plan, a backlog item and "this milestone".
- Revision 317 was built and tested in the same Linux container: Debug and Release each ran **1,546 tests**, passing
  **1,449 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its test caught
  five of six mutations: the facts never capped, too little room kept for the bytes, no refit as the byte view resizes,
  room never given back to a taller window, and facts that cannot scroll. The sixth capped the facts before the bytes
  show; it cannot differ, since the fit runs only when the byte view resizes and the view is never hidden again once
  shown.
- Revision 316 was built and tested in the same Linux container: Debug and Release each ran **1,545 tests**, passing
  **1,448 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of five mutations:
  - one member's instants compared through its alignment;
  - an order stated at the uncertainty's edge;
  - an unknown comparison given a difference, on either branch that finds it unknown;
  - an ambiguous one worded as an order.

  The branch through a shared member whose rate is not stated first survived every workspace test, until P9's test
  aligned two members through one. P5's test guards a prohibition no code implements, so it has no mutation.
- Revision 315 was built and tested in the same Linux container: Debug and Release each ran **1,542 tests**, passing
  **1,445 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of seventeen mutations:
  - a commit or a retention that carries nothing, or carries without the previous generation's own release;
  - a derived-files release carried, or an earlier release kept beside a later one of its kind;
  - readers that state only a generation's own release: a record's content, re-derivation's refusal and `icat session`'s
    report;
  - the list written when absent, or left out of the digest;
  - a manifest accepted that is empty, carries a later generation's release, two of a kind, or an entry with no record;
  - re-derivation's old words, and `icat session` naming no generation or none carried.
- Revision 314 was built and tested in the same Linux container: Debug ran **1,541 tests**, passing **1,444 with 4
  skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Release failed one more, "I1:
  re-derivation refuses journal chunks that do not continue one another". Its second burst of records waited a fixed
  600 ms for the first chunk to publish, and under the whole suite's load publishing took longer. It passed alone five
  times out of five. It now waits for the first publication itself, through the scripted host's `PauseUntil`, and
  passes in both configurations; its project passes in Release with only its three baseline failures. The tests
  caught nineteen of twenty mutations:
  - the digest given whole or by eleven digits, the lengths swapped, the generation left unnamed, a missing file
    called unreadable;
  - a shared file said twice, no fallback said to fail, a problem filed under its manifest, the files left unsaid;
  - the old digest and length wording;
  - `rederive`'s, `metric`'s, the Desktop's and the workspace's reasons back in parentheses, and the workspace calling
    a fallback a copy;
  - the package's and the builder's old words.

  The twentieth, the contents check's own length wording, is reachable only when a file changes between a lease and
  its hashing, because the lease checks lengths first and falls back.
- Revision 313 was built and tested in the same Linux container: Debug and Release each ran **1,537 tests**, passing
  **1,440 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of fifteen mutations: no flush when a command ends, a group's column that ignores its labels or keeps one space
  past the longest, each field in its own column, a change of writer kept in one group, a group never cleared, and each
  of nine other writes that did not write out the group before it.
- Revision 312 was built and tested in the same Linux container: Debug and Release each ran **1,534 tests**, passing
  **1,437 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of thirty mutations:
  - the shared refusal's code changed, or its handler removed;
  - the refusal caught by any of seven readers' own handlers;
  - `export`'s old handler back, or damage reported as a capability failure;
  - either of the store's throws back to the old exception;
  - `session`'s note or its "Acquired" line as before;
  - `verify`'s folder message removed or inverted, and `import`'s, `measure`'s and the workspace's old words;
  - ten commands' old codes.

  The last, for `retain`, first survived until the test gave its journal release a folder too.
- Revision 311 was built and tested in the same Linux container: Debug and Release each ran **1,533 tests**, passing
  **1,436 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of seven mutations: lines counted from 0, the missing or unknown fields or the value's path left unsaid, a
  non-object read as fields, and the workspace's or settings' parser words back.
- Revision 310 was built and tested in the same Linux container: Debug and Release each ran **1,532 tests**, passing
  **1,435 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of four mutations:
  - the guard made in any folder again, which the storage, command-line and window tests each caught;
  - an older session refused its guard;
  - the old words for a folder with a guard and no pointer;
  - `processes` keeping its own words.

  The third first survived until the storage test covered a folder whose writer made the guard and stopped before
  publishing.
- Revision 309 was built and tested in the same Linux container: Debug and Release each ran **1,530 tests**, passing
  **1,433 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its test caught
  six of seven mutations: a workspace subcommand, `package --original` or `retain --release-content` dropped from the
  summary, its pointer to each command's help removed, an entry with no synopsis, and `capture` unknown again
  elsewhere. The seventh left `measure udp` only described, which the summary still names as a form in
  `<tcp|udp|pipe|rpc>`, so nothing was lost.
- Revision 308 was built and tested in the same Linux container: Debug and Release each ran **1,529 tests**, passing
  **1,432 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of nine mutations:
  - the ranked header shown, or flagged, with no session;
  - the empty rung's card always shown, or never, with no session;
  - its heading unchanged with no session;
  - the actions unbounded, or keeping the list no room, or no floor of their own;
  - the saved sessions shown beside a session.

  The empty-state test alone misses the two bound mutations, since hiding the header already leaves the cards room
  there; the test of a session beside the cards catches them. Two existing tests opened the empty window to measure
  its search box, which no longer shows there, and now open a session's.
- Revision 307 was built and tested in the same Linux container: Debug and Release each ran **1,526 tests**, passing
  **1,429 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of six mutations: Back naming the whole rung again, its tooltip or help left out, the machine rung's help
  reworded, the badge laid over the heading, and the peak rate drawn over the plot again. The baseline's header test
  at 1080 by 700 still fails here as before: it measures the machine rung's buttons in the Linux fallback font.
- Revision 306 was built and tested in the same Linux container: Debug and Release each ran **1,524 tests**, passing
  **1,427 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of 19 mutations. They covered:
  - each refusal skipped: no content, an unfinished capture, a count taken in another generation;
  - the content kept named, its records not counted, its digest unchecked;
  - a journal prefix still refused after the release;
  - the measurement's counts, its finished flag and an unreadable chunk;
  - readers that drop the release's when and why, or call an HTTP record metadata-only;
  - the command line's exit code, its confirmation and its notes;
  - `icat session` hiding the record.

  An existing test asserted the old wording for content released with its journal chunk, and now asserts the new one.
  A chunk whose bytes changed is counted unreadable, and the release then publishes nothing, since the generation no
  longer verifies.
- Revision 305 was built and tested in the same Linux container: Debug and Release each ran **1,521 tests**, passing
  **1,424 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of 37 mutations. They covered:
  - gaps before, between or after the recorded buffers left out or misnumbered;
  - a cut tail or an omitted buffer read as kept, and an unknown length read as zero;
  - bytes read unasked or from the wrong place;
  - the view's choice of buffers, its bound and where it stops;
  - the viewer's toggle, save, text view, labels, copy, colours and spoken names;
  - the command line's exit code, gap list and choice of buffers.

  Three first failed to build, since `if (false)` is unreachable code here; written otherwise, each was caught. A
  rendered frame of the view was checked by eye for its headings, gap colour and alignment.
- Revision 304 was built and tested in the same Linux container: Debug and Release each ran **1,517 tests**, passing
  **1,420 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its test caught
  both mutations: the suffix kept, and `evidence` printing the exception's message again.
- Revision 303 was built and tested in the same Linux container: Debug and Release each ran **1,517 tests**, passing
  **1,420 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its test caught
  each of three mutations: the matrix or the catalog printed as an array again, and the matrix naming no contract.
  Before the change it failed on the catalog, the first array it read.
- Revision 302 was built and tested in the same Linux container: Debug and Release each ran **1,516 tests**, passing
  **1,419 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of four mutations: the `--` hint left out, any argument with a dash taken as an option's name, an extra operand
  called an option, and `workspace` wording its own refusal.
- Revision 301 was built and tested in the same Linux container: Debug and Release each ran **1,516 tests**, passing
  **1,419 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  nine of eleven mutations: the ranking dropped from a layout, per second keeping nothing, a version-11 file allowed a
  ranking, `Records` allowed by name, the ranking not put back on opening, a ranking change skipped when the pins are
  unchanged, the notice or `icat workspace show` saying nothing of the ranking, and `show`'s contract left at v13.
  The two left are equivalent: removing either `RankBy` or `PerSecond` from what the window watches changes nothing,
  since every ranking change raises both.
- Revision 300 was built and tested in the same Linux container: Debug and Release each ran **1,515 tests**, passing
  **1,418 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of eight mutations: the reader's guard against an option read after an operand removed, a negative number
  read as an option, `--` ignored, a verb taken from anywhere but the front, operands after `--` left unreported,
  and `metric`, `measure` or `follow` reading an operand before an option. Every command's arguments are now read
  once by the tests, so a command that reads them out of order fails there. By hand, `channels`, `processes`,
  `metric`, `session`, `timeline`, `evidence`, `operations` and `exchanges` answered with their options written
  before the session, where most had looked for a session named after an option's value.
- Revision 299 was built and tested in the same Linux container: Debug and Release each ran **1,512 tests**, passing
  **1,415 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. The three new
  command-line tests caught each of thirteen mutations: help after a refusal on stdout (an unknown command,
  `timeline`), `icat metric`'s remedy on stdout, `Explain` not redirecting; a tick interval or a metric interval
  constructed past a tick count, a metric bound past the clock's range refused, `evidence` or `export` refusing one
  without saying why; and a remedy whose "Name it with" and "takes" were swapped, a numerator remedy said as a
  value list, choices joined without "or", or no remedy at all. A sweep of every command's refusal, run with stdout
  and stderr apart, found stdout empty in each, where 27 of 43 had written help or remedies there; `--help` still
  answers on stdout.
- Revision 298 was built and tested in the same Linux container: Debug and Release each ran **1,509 tests**, passing
  **1,412 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of fifteen mutations: a domain, side, layer, numerator or duration refusal naming no part, one offering every
  domain or interval rather than those its request takes, one offering a value where the part is right only left out,
  one offering none where a value is wanted, and a numerator list headed as metrics. No test covers the command line,
  so `icat metric` was run against a session through each refusal: a missing domain, side, numerator or duration
  interval says "Name it with" its option and the values it takes, a wrong domain or side says what the option takes
  there, and a side, domain, layer or numerator the metric fixes or has none of says to leave it out.
- Revision 297 was built and tested in the same Linux container: Debug and Release each ran **1,509 tests**, passing
  **1,412 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests caught
  each of fourteen mutations: a range taking any span or `TryCreate` ignoring it; the held part not centred, reaching
  past its end or not given; each of the ranking, bucket, capture-column and recording conversions constructing its
  range (thrown) or declining a wide one (holding nothing, or its coverage unknown); and the overview and saved-view
  checks left out. No test covers the command line, so it was run against a session: `timeline`, `evidence`, `export`
  and `metric` refuse `-9223372036854775808:9223372036854775807`, `-9223372036854775808:0` and
  `-1:9223372036854775807`, saying the interval is longer than any, where `timeline` and `metric --metric rate` had
  failed with an overflow. `metric` over `-1000000000000000s:1000000000000000s` answers its 64 B over the range centred
  on the epoch, and an export or `timeline --bytes` over `-100000000000000000:100000000000000000` answers in full.
- Revision 296 was built and tested in the same Linux container: Debug and Release each ran **1,506 tests**, passing
  **1,409 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its test failed
  with the bound's overflow thrown, as every ranking and the interval count had thrown it, and with an interval past
  the range answered as holding nothing. No test covers the command line, so `icat metric` was run against a session:
  "0s:90000000000s" answered its 64 B sent where it had refused, a start before the clock's range answered, and one
  after it was refused, saying the interval holds none.
- Revision 295 was built and tested in the same Linux container: Debug and Release each ran **1,505 tests**, passing
  **1,408 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its tests failed
  with the PID written as a quantity ("PID 1,960") and with a nameless node's PID drawn twice.
- Revision 294 was built and tested in the same Linux container: Debug and Release each ran **1,504 tests**, passing
  **1,407 with 4 skipped**; the 93 failures are the Windows and font-metric ones revision 293 leaves. Its new test
  failed without the placeholder held to its field, its text then measured whole at 363 px in a 102 px field, and
  without the ellipsis.
- Revision 293 was built and tested in the same Linux container: Debug and Release each ran **1,503 tests**, passing
  **1,406 with 4 skipped**. 93 failed, the Windows and font-metric ones revision 287 names but one: the IPv6
  channel's test of its crumbs, counted among the font-metric failures since revision 287, passes. Its current crumb
  had been cut on the left, the defect this revision fixes, which the wider fallback font exposed. The new test
  failed without the fix, its current crumb starting 49 px left of the trail's edge.
- Revision 292 was built and tested in the same Linux container: Debug and Release each ran **1,502 tests**, passing
  **1,404 with 4 skipped**; the 94 failures are exactly the Windows and font-metric ones revision 287 names. Its 4 new
  tests were checked by reverting what they test: admitting every strength or drawing every channel end whatever the
  policy, answering an interval from the kept bytes, writing the unbound bytes as none, keeping a datagram flow's ends,
  dropping a channel's second end, giving unbound records to a process, keeping nothing, reading a strength no policy
  admits or an end held twice, using bytes that name an instance the checkpoint does not hold, and a viewer that reads
  anyway each failed them. At a million rows a reopened session's whole-session byte ranking answered in 26-30 ms
  where a read took 162-182 ms, and publishing took 475-521 ms with the process bytes or without them.
- Revision 291 was built and tested in the same Linux container: Debug and Release each ran **1,498 tests**, passing
  **1,400 with 4 skipped**; the 94 failures are exactly the Windows and font-metric ones revision 287 names. Beside a
  test that empties the derivation cache for twenty seconds, the warm aggregates' allocation test failed 3 runs of 3
  as it stood, measuring a focused count's 16.2 B per row and an interval count's 28.2, and passed 3 of 3 once it ran
  alone; ten runs of its assembly beside three others, as the suite runs them, passed before the change too, as 46 of
  48 suite runs had. An array the size of a segment allocated in an interval count still fails it, at 4.0 B per row.
- Revision 290 was built and tested in the same Linux container: Debug and Release each ran **1,498 tests**. Release
  passed **1,400, with 4 skipped**, and its 94 failures are exactly the Windows and font-metric ones revision 287
  names. Debug passed 1,399: beyond those 94, the warm aggregates' allocation test (R11) failed once, its overview
  projection measuring 22.9 B per row, as it had in revisions 269 and 287 and in code this revision does not touch; it
  passed 6 runs of 6 alone and 6 of 6 under four busy cores, and revision 291 takes up why. Its 4 new tests were
  checked by reverting what they test: an exchange ended at its last buffer, one not recorded whole uncounted or drawn
  solid, the lane never read, a click that selects nothing, the faint share overlaid, no card, and density that only
  an RPC lane hovers each failed them; so did each lane's note formatted on every read, the graph's lambdas allocated
  on every read, and a 24-byte object on every frame, which the old 32-byte tolerance passed. Read before its press
  had eased back, the toggle measured 99.3-99.7% of its size in 3 runs of 3; read once it had, its test passed 6 runs
  of 6 in Debug and in Release.
- Revision 289 was built and tested in the same Linux container: Debug and Release each ran **1,494 tests**, passing
  **1,396 with 4 skipped** once the fixture index named the 2 new I4 and I14 tests, whose traceability check the
  first runs failed; the 94 other failures are the Windows and font-metric ones revision 287 names. Its 3 new tests
  were checked by reverting what they test: an overview published without its lane bytes, kept bytes answering
  another width, a lane answered from another mechanism's cells, and a reader that admits a cell where nothing is
  counted, one that contradicts itself, or two out of order each failed them.
- Revision 288 was built and tested in the same Linux container: Debug and Release each ran **1,491 tests** twice, and
  all four runs passed **1,393, with 4 skipped**; the 94 failures are exactly the Windows and font-metric ones revision
  287 names, and nothing failed beyond them. Under four busy cores, the three tests of a read's state before it arrives
  had failed 8 runs in 8 and 6 in 8 before it, and failed none of 8 each after. Its 2 new tests were checked by
  reverting what they test: removing the forced yield, the asking thread, or the rule at one read each failed them.
- Revision 287 was built and tested in a Linux container without Windows, on the .NET 10 SDK there: Debug and Release
  each ran **1,489 tests** twice. Release passed **1,391, with 4 skipped**, and its 94 failures are exactly those
  revision 286 has in that container: the 86 CaptureBroker tests and 6 capture tests that call Windows APIs, and 2
  layout tests whose text Linux measures in a wider fallback font for Segoe UI. Beyond them, 4 tests failed once
  each across the runs: 3 assert a byte read's state before it arrives, and the read sometimes arrived first, which
  revision 288 makes deterministic; 1 measures warm aggregates' allocation (R11). The 6 new tests were each checked
  by reverting what they test: 15 of 17 reverts failed them, and the other 2 drew the same colours, since the action
  fill, the pinned accent and the accent ink are one value in every mode.
- Revision 286 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,483 tests: 1,479
  passed, 4 skipped**, zero failures. Live, in the Release app on a 20-second dense capture, deleted after: a web-ui.exe
  process's rows under Bytes sent read "by source direction · bytes sent per second", its sends in the Outbound row and
  "no sends" beneath Inbound, and three posted wheel notches read the rows anew over 5.0–15.4 s.
- Revision 285 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,481 tests: 1,477
  passed, 4 skipped**, zero failures, each of the 12 test assemblies reporting; the run was then stopped for low memory
  before it printed its exit codes, and the Debug suite passed again, exit 0, in a clean worktree before the push.
  Live, in the Release app on a 20-second dense capture, deleted after: web-ui.exe's
  seven process lanes under Bytes sent read "7 process lanes · bytes sent per second", the machine row "Machine · all
  sends" at 860 KB/s, and three posted wheel notches counted the lanes anew and read their bytes over 5.1–15.4 s.
- Revision 284 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,478 tests: 1,474
  passed, 4 skipped**, zero failures. Live, in the Release app on a 20-second dense capture, deleted after: choosing
  Bytes sent read "Bytes sent per second by mechanism · 4 lanes" within 150 ms, TCP's lane took its byte profile at
  871 KB/s, the Process and RPC lanes read "no sends", and three posted wheel notches read the zoomed view's own
  columns, 1.2 MB/s over 5.3–15.5 s. A posted pointer does not rest over a window the cursor is not on, so the card
  was checked headlessly.
- Revision 283 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,474 tests: 1,470
  passed, 4 skipped**, zero failures. Live, on a 2-second scratch capture, deleted after: "Recording 2,203468 s, from
  capture start to its stop"; first "2.203468", beside "0,0 ppm", until the text took the reader's culture. Also live,
  revision 277's texts in the Release app's investigation window, on two captures of one boot: the candidates read
  "Compared session afc4d716 at generation 2, session ef5bfab6 at generation 2", and each lane "Read at its
  generation 2".
- Revision 282 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,474 tests: 1,470
  passed, 4 skipped**, zero failures. Live, a scratch investigation of a 2-second capture with a layout written by hand,
  as the contract allows, deleted after: `show` read "Session 454c14cc: 2 nodes pinned on its graph, put back when it
  is opened from this investigation."
- Revision 281 changed documents only; its code is revision 280's, whose Debug suite ran again in a clean worktree
  before the push.
- Revision 280 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,474 tests: 1,470
  passed, 4 skipped**, zero failures. Its window test drives the real main and investigation windows headlessly: a pin
  placed on a member opened from the investigation is written there, is absent when the session is opened on its own,
  and is back when it is opened from the investigation again. Checked live after revision 281 on a 20-second dense
  capture in a scratch investigation, deleted after: the Release app opened from the investigation said where its
  pins are kept, a ranked group selected and P posted to its window wrote one pin to the file, and a relaunch opened
  from the investigation said it "put back 1".
- Revision 279 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,472 tests: 1,468
  passed, 4 skipped**, zero failures. Its test failed before the fix with "a workspace-v5 file holds no alignment with
  a second anchor", for a file as revision 264 wrote it.
- Revision 278 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,471 tests: 1,467
  passed, 4 skipped**, zero failures. Live, two captures of this host in a scratch investigation, deleted after: before
  an alignment `show` stated their overlap unknown, and printed "Read from ." until that line was made to state only
  sessions it read; aligned by their one boot, its `--json` names both captures' generations for its overlaps.
- Revision 277 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,471 tests: 1,467
  passed, 4 skipped**, zero failures. Live, two 2-second `icat record` captures in a scratch investigation, deleted
  after: `icat workspace correlate` reads "44b1ac8e at generation 2; 9c931b72 at generation 2", and its `--json` names
  each capture's generation and manifest digest.
- Revision 276 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,470 tests: 1,466
  passed, 4 skipped**, zero failures. The broker qualification passed on real ETW
  (`bench/results/broker-qualification-20260929T063157Z`): each finished capture published a calibration of one sample
  with its first generation and of two with its last, which replaced it, and the capture whose broker it killed still
  holds its start's calibration.
- Revision 275 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,469 tests: 1,465
  passed, 4 skipped**, zero failures. Live, a 4-second `icat record` into the scratchpad, deleted after, published a
  `coverage-v2` ledger recording from its epoch to its stop; its whole-recording rate states "Process lifecycle
  covered; Udp covered", where the revision-273 capture of the same shape still states unknown coverage, and its first
  timeline column is covered. The broker qualification passed on real ETW
  (`bench/results/broker-qualification-20260929T061601Z`) once the Release CLI it launches was rebuilt: a stale one
  refused the new ledger.
- Revision 274 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,466 tests: 1,462
  passed, 4 skipped**, zero failures. Live, the Release app opened a scratch capture and an imported ETL, deleted after,
  driven through UI Automation and read by PrintWindow: per second read "Records · per second over the whole
  recording, 4,2 s" and "13 KB on 98 sends · per second over the whole recording, 4,2 s" beside a time scope of
  "All · 4,178 s recorded", and the import "Per second needs an interval: brush one or zoom" beside "All 2,618 s".
- Revision 273 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,466 tests: 1,462
  passed, 4 skipped**, zero failures. Live, a 4-second `icat record` into the scratchpad with a loopback sender running
  past its stop now ends its records 31 ms before its 4.18 s recording ends, not 1.2 s: 137 UDP records, not 101, and
  a ledger that reports nothing lost. The broker qualification passed on real ETW into the scratchpad
  (`bench/results/broker-qualification-20260929T054405Z`), and no InterCat session was left.
- Revision 272 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,465 tests: 1,461
  passed, 4 skipped**, zero failures. Live, a 4-second `icat record` into the scratchpad, deleted after, answered `icat
  metric --metric rate` over "the whole recording, [-0.002778 s, 4.173207 s)", a record before the epoch widening it.
  Before the stop reading moved, a capture of the same length recorded 5.07 s. A loopback sender running past the stop
  showed its records ending 1.2 s before it: the tail-delivery defect above.
- Revision 271 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,461 tests: 1,457
  passed, 4 skipped**, zero failures. Live, `icat workspace view` on a scratch investigation of an imported ETL and a
  2-second capture, deleted after, refused a view before any alignment, saved one after, listed and removed it, and
  refused an end before its start in a sentence.
- Revision 270 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,459 tests: 1,455
  passed, 4 skipped**, zero failures. Live, `icat workspace note` on a scratch investigation of an imported ETL, deleted
  after, added a note about it and one pinned at an instant, listed both with their place, reworded and removed one, and
  refused a removed note and a note given both --at and --remove.
- Revision 269 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,456 tests: 1,452
  passed, 4 skipped**, zero failures. Live, `icat workspace translate` on a scratch investigation of an imported ETL,
  deleted after, stated and withdrew a translation, wrote an IPv6 endpoint canonically and refused a loopback address;
  one machine cannot capture both sides of a translation, so the candidate it yields is proven on synthetic sessions.
  Its worktree check then failed once, on R11's allocation test under the whole suite's load, which passed alone five
  times and in both suites; that test now takes the least of three warm runs, and the change was checked the same way.
- Revision 268 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,454 tests: 1,450
  passed, 4 skipped**, zero failures. Live, keys posted to the Release Desktop's investigation window moved its column
  cursor to a 3-second capture's last column and opened it: the main window showed that capture zoomed to it, its one
  record selected. The first try opened an empty stretch before the capture; that was fixed, and the chart given its
  automation peer.
- Revision 267 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,453 tests: 1,449
  passed, 4 skipped**, zero failures. Live, the Release Desktop's Compare instants…, driven by UI Automation on a
  3-second `icat record` capture aligned to an imported ETL within 2 ms, in scratch and deleted after, placed both
  instants and said their order was ambiguous within ±2.1 ms.
- Revision 266 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,452 tests: 1,448
  passed, 4 skipped**, zero failures. Live, an imported ETL recorded on this workstation and a 3-second `icat record`
  capture of it, in scratch and deleted after, read as two hosts until `same-host` confirmed them one; `show` listed
  each with the other, a second confirmation and an unfounded withdrawal were refused, and the withdrawal was kept.
- Revision 265 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,448 tests: 1,444
  passed, 4 skipped**, zero failures. Live, of two concurrent `icat record` captures in scratch, deleted after, one was
  aligned by a stated instant to an imported ETL and the other exactly to it by their boot: the two compared to the
  nanosecond and ran at once for 871 ms, and the Release Desktop drew the second placed through both alignments.
- Revision 264 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,444 tests: 1,440
  passed, 4 skipped**, zero failures. Live, a 3-second `icat record` capture aligned at two instants of an imported ETL,
  in scratch and deleted after, measured +200 ppm; `compare` and the Release Desktop's timeline placed it through that
  rate, the lane within ±1.5 ms, as two instants 2 s apart, each within ±1 ms, allow.
- Revision 263 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,440 tests: 1,436
  passed, 4 skipped**, zero failures. Live, an investigation of a 3-second `icat record` capture and an imported ETL, in
  scratch and deleted after, was packaged from the CLI and from the Release Desktop, whose folder picker was filled by
  message; moved elsewhere, the package reopened with both sessions present and its alignment and host name kept.
- Revision 262 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,435 tests: 1,431
  passed, 4 skipped**, zero failures. Live, two concurrent `icat record` captures in scratch, deleted after, read as an
  unknown overlap until aligned by their boot and then as having run at once for 3.0 s. Their boot token was new since
  the previous day's captures: the machine had restarted, and the volatile key had gone with it.
- Revision 261 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,432 tests: 1,428
  passed, 4 skipped**, zero failures. Live, the Release Desktop drew two scratch captures of this machine, aligned by
  their boot, as lanes at 0 to 3.0 s and 5.93 to 8.92 s of the investigation's time, where their shared counter puts
  them. The captures were deleted after.
- Revision 260 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,430 tests: 1,426
  passed, 4 skipped**, zero failures. The Release `icat workspace join` refused, in words, a candidate that does not
  exist, a number that is none, and no or two decisions; decisions themselves are proven on synthetic sessions, since
  this machine cannot capture an exchange's two ends apart.
- Revision 259 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,428 tests: 1,424
  passed, 4 skipped**, zero failures. Live, the Release Desktop opened an investigation of two scratch captures from the
  command line; UI Automation chose the second session, opened Align, chose "by their boot" and aligned it exactly
  (4.9344874 s), and the Candidate joins tab found none, as two captures of one machine must. Captures deleted after.
- Revision 258 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,426 tests: 1,422
  passed, 4 skipped**, zero failures. Live, `icat workspace correlate` over two concurrent explore captures of this
  machine, aligned by their boot, proposed no candidate in 0.4 s, as it must: every connection there is paired within
  one capture or one-sided the same way in both. A focused capture's `--pid` does not split an exchange's ends here.
- Revision 257 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,424 tests: 1,420
  passed, 4 skipped**, zero failures. Live, the Release Desktop opened an investigation of two scratch captures, aligned
  by their boot, from the command line in its own window; UI Automation read both rows as sentences, and Open showed
  the second in the main window ("Saved session open"). The captures were deleted after.
- Revision 256 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,422 tests: 1,418
  passed, 4 skipped**, zero failures. Live, on two `icat record` captures in scratch, deleted after: aligned by their
  shared boot token, the second's start fell 5.0632029 s into the first - their epochs' 50,632,029 ticks - and instants
  100 ns apart were ordered exactly; aligned by their wall clocks under a stated 5 ms and 20 ppm, the anchor matched the
  exact one to the tick, and instants 6.8 ms apart were ordered beyond ±5.1 ms.
- Revision 255 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,420 tests: 1,416
  passed, 4 skipped**, zero failures. Live, two `icat record` captures in scratch, deleted after, each recorded a clock
  calibration: two samples of ±200 ns, the wall clock 0.0 ppm (±0.1) against the counter, and one boot token for
  both, which the first minted into the volatile key `HKLM\SOFTWARE\InterCat.Boot` on this workstation; Windows deletes
  it at the next restart. No test mints a token.
- Revision 254 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,413 tests: 1,409
  passed, 4 skipped**, zero failures. Live, on two `icat record` captures in scratch, deleted after: aligned by their
  recorded epochs on this machine's one QPC counter (48,342,261 ticks apart), `compare` stated instants 26.1 µs apart as
  ordered beyond ±1.0 µs, the anchor itself as ambiguous, and 6.3 s apart as ordered; with no drift bound, an instant
  1.0 s from the anchor as no order; withdrawn, nothing. A session's files hashed the same before and after.
- Revision 253 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,407 tests: 1,403
  passed, 4 skipped**, zero failures. Live, with the Release CLI on captures recorded into scratch and deleted after: a
  copy of a member was refused; a moved member read Missing and was relinked; a rederived one read Advanced, then
  Present once relinked; an older copy put back read Replaced; the workspace's own folder was refused as no session;
  and every file of a session hashed the same before and after `show`. Two fresh captures recorded one host identity,
  derived from the installation, where this machine's earlier captures recorded the name-based one.
- Revision 252 changes documents only: no code changed since revision 251's suites (1,399 tests: 1,395 passed, 4
  skipped, in Debug and Release); the architecture tests ran again and passed. Every test the M3 exit review cites by
  name exists.
- Revision 251 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,399 tests: 1,395
  passed, 4 skipped**, zero failures. Live: the Release `icat capabilities` on this workstation's build states TCP and
  UDP TrafficVisualization (FX-TCP-002, FX-UDP-002), RPC and HTTP ExperimentalEvidence (FX-RPC-001, FX-HTTP-003) and
  named pipes Unsupported (FX-PIPE-001), each as committed fixture evidence, where it had said Unsupported for all.
- Revision 250 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,396 tests: 1,392
  passed, 4 skipped**, zero failures. Live, on a fresh 16-exchange content session in scratch: its redacted package held
  no content file, `icat exchanges` on it grouped 16 of 16 exchanges whole with the source's bytes and median under
  pseudonymous numbers, the export carried no byte, and the original package's check stated its 287,837 kept bytes. The
  session and its packages were deleted.
- Revision 249 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,395 tests: 1,391
  passed, 4 skipped**, zero failures. Live, on a real 20-second Explore capture recorded into scratch (4,158 records,
  nothing lost): Enter on one of chrome.exe's connections opened its records with the timeline reading "Records of TCP
  to 3.72.134.85:443 in colour, the rest of the machine in grey". The capture was deleted.
- Revision 248 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,393 tests: 1,389
  passed, 4 skipped**, zero failures. Live, on a real 30-second Explore capture recorded into scratch (4,013 records,
  nothing lost): chrome.exe's rung, which had said "Nothing at this level", listed 25 connections holding 459 of its
  460 records, `icat channels --one-sided` the same, and an RPC-heavy svchost.exe's rung read as before. The capture
  was deleted.
- Revision 247 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,391 tests: 1,387
  passed, 4 skipped**, zero failures. Live, on the fresh 96-exchange content session: `icat exchanges` listed 96
  exchanges, all recorded whole, from 580 buffers; the Release window's process rung showed them as a row, Enter
  listed them with their parts, Enter on one opened its buffers, and C opened a buffer's content. The capture was
  deleted.
- Revision 246 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,388 tests: 1,384
  passed, 4 skipped**, zero failures. Live: `icat overview` of the fresh 986-record content session that had refused
  its persisted overview now reads it; the session's summary, byte metrics (929,043 B sent and 870,465 B received,
  the 1,799,508 B kept) and the window's machine, process and evidence rungs were read. The capture was deleted.
- Revision 245 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,387 tests: 1,383
  passed, 4 skipped**, zero failures. Live, from the elevated shell into scratch: `icat record` named the workload by
  image and start, stated what it collects, and kept 97 HTTP records of 16 exchanges with nothing lost while holding
  the workload open; a request naming a process ID nobody has was refused before any session or directory existed.
  The capture was deleted.
- Revision 244 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,386 tests: 1,382
  passed, 4 skipped**, zero failures. Live, from the elevated shell into scratch: the probe matched every part of 256
  and of 2,048 exchanges eight at once, whose numbers ran 1 to 2,048, and of 64 chunked responses; `icat record` kept
  FX-HTTP-003's 256 exchanges, all 1,024 parts matched the server's bodies, and `icat content --part --save` wrote a
  matching 98,304-byte chunked body. The captures were deleted.
- Revision 243 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,385 tests: 1,381
  passed, 4 skipped**, zero failures. Live, from the elevated shell into scratch: the probe matched all 64 parts of 16
  TLS exchanges and measured the impact Low over TLS; `icat record` kept FX-HTTP-002's 32 exchanges, all 128 parts
  matched the server's decrypted bytes, and `icat content --part --save` wrote a matching 262,144-byte body; the
  Release window's viewer showed the Encryption fact and the whole part. The captures were deleted.
- Revision 242 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,384 tests: 1,380
  passed, 4 skipped**, zero failures. Live, a fresh FX-HTTP-001 capture: `icat content --part --save` wrote a
  262,144-byte response body reassembled from 17 buffers that matched the server's SHA-256, and the Release window's
  toggle showed the same part and went back. The capture was deleted.
- Revision 241 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,382 tests: 1,378
  passed, 4 skipped**, zero failures. Both fixtures were recorded again with `icat record` from the elevated shell into
  scratch: FX-CONTENT-001's 24 messages matched the truth (23 whole by SHA-256, one cut), and FX-HTTP-001's 128 parts
  matched the wire, nothing lost. Only truth logs and counters are committed; the captures were deleted.
- Revision 240 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,381 tests: 1,377
  passed, 4 skipped**, zero failures; two tests that pinned the old reading now assert the capture's. Live, a 12-second
  content-fixture capture opened in the Release window: the process's evidence rung drew no gap, and its status read
  "Coverage reported for 64 intervals" where revision 239's read "58 coverage-unknown intervals". Scratch was deleted.
- Revision 239 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,381 tests: 1,377
  passed, 4 skipped**, zero failures. Live, elevated, into scratch: `icat record --profile content` scoped to
  FX-HTTP-001's process kept 229 buffers whole; all 128 parts matched the wire; `icat content` and the Release
  window's viewer read them, and a response head saved through the native dialog matched its SHA-256. No ETW session
  was left; scratch was deleted.
- Revision 238 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,380 tests: 1,376
  passed, 4 skipped**, zero failures. They include WinINet's four events compiled against its registered layout, a
  content policy that keeps only the sources it names, and v3 checkpoints read as v4's where every record named its
  owner. No capture ran: no profile admits the source yet.
- Revision 237 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,377 tests: 1,373
  passed, 4 skipped**, zero failures. The probe ran four times, elevated, into scratch: 16 and 64 requests, bodies to
  96 and 256 KiB; every part of every exchange matched, nothing was lost, and a concurrent decoy's 64 exchanges left no
  record. No ETW session was left; scratch was deleted. `bench/results/wininet-capture-feasibility-20260928T102426Z`
  holds counters only.
- Revision 236 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,377 tests: 1,373
  passed, 4 skipped**, zero failures. Live on the Release build, on a fixture capture recorded into scratch: C opened
  the viewer on the reveal button; the reveal moved the keyboard to the first hex line; a range and a refused range
  read as typed; Esc returned to the record; a message saved through the native dialog, and one by `icat content
  --save`, matched the truth's SHA-256. The clipboard was left alone (Copy is covered headlessly); scratch was deleted.
- Revision 235 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,369 tests: 1,365
  passed, 4 skipped**, zero failures. Live, elevated, into scratch: 24 fixture messages kept 23 whole and 1 cut to
  4,096 bytes; 8,000 unpaced ones stopped the capture at 16,773,163 bytes (2,151 whole, 3,015 cut, 2,834 omitted).
  Every length, direction and whole message's SHA-256 matched the truth, nothing was lost, no message text was in any
  other file, no ETW session was left, and both sessions were deleted.
- Revision 234 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,364 tests: 1,360
  passed, 4 skipped**, zero failures; nine of them hold the chunk format and its refusals, pages and the original
  record stating kept content, consent withholding bytes, release with the journal chunk, and both sharing presets.
- Revision 233 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,355 tests: 1,351
  passed, 4 skipped**, zero failures; three of them hold each source's statement, the original record's and the
  inspector's. `icat raw` stated it for a lifecycle and a TCP record of a scratch import of the sparse ETL, deleted.
- Revision 232 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,353 tests: 1,349
  passed, 4 skipped**, zero failures; two of them hold how a chosen channel and call are described, and what replaces
  them. The Release Desktop's inspector read a client and a served call of a real elevated `icat record --profile
  rpc-peers` session in full; the session and its renders were then deleted.
- Revision 231 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,352 tests: 1,348
  passed, 4 skipped**, zero failures; two of them read a channel through a call and land Esc on calls of its first
  and second pages and under a brush. A real elevated `icat record --profile rpc-peers` session was driven in the
  Release Desktop with posted keys and clicks: O, Esc, a served call's menu and Esc each landed on the call named.
- Revision 230 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,350 tests: 1,346
  passed, 4 skipped**, zero failures; one of them opens a linked call's other end, reads its records, and climbs to
  the host's channel, whose calls name their caller.
- Revisions 104–229: [the historical log](history/IMPLEMENTATION-STATUS-revisions-104-to-229.md).
- Two Claude sessions pushed to `main` in parallel on 2026-09-25/26. A Linux container session built a duplicate live
  edge while a Windows session shipped revisions 126–129. The duplicate was discarded, and only its additive parts
  became revision 130. Fetch `origin/main` before starting a slice and again before pushing.
- The broker/source measurements are on one pre-release Windows build; do not generalize capture overhead or
  capability tier to retail builds. The report is pseudonymized, **not anonymous**: times, counts and workload
  shapes may identify a machine. The detailed export is sensitive. Screen-reader audit is still open.
- Keep user-owned untracked files (currently `Zip-GitFiles.ps1`) untouched and uncommitted. Before each slice,
  inspect `git status` and these tables. After each coherent slice, build and run the full suite in both
  configurations: the analyzers treat warnings as errors, and revision 107 shows that an uncompiled slice hides failures.
  Then update this file and the plan if needed, commit and push `main`.

## Key reference contracts

`contracts/journal-v1.md`, `store-v1.md`, `segment-v1.md`, `metrics-v1.md`, `entities-v1.md`, `operations-v1.md`, `derivation-checkpoint-v1.md`, `overview-index-v1.md`,
`query-identity-v1.md`, `live-follow-v1.md`, `app-settings-v1.md`, `workspace-v19.md`, `support-bundle-v1.md`, `demo-investigation-v1.md`, `clock-calibration-v1.md`, `coverage-v2.md`; ADR-008, ADR-010, ADR-012, ADR-013, ADR-023–031, ADR-038–041; the complete historical ledger linked above.
