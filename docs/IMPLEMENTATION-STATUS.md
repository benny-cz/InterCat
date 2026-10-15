# InterCat implementation status

Updated: 2026-10-15 · Plan revision: 554 · Branch: `main`

This is the **current resume point**, not a running transcript. Update the backlog and open-work tables in place after
each slice, then add only a short latest-change note. The complete pre-revision-104 chronology, measurements, and old
verification ledger are preserved in [the historical status](history/IMPLEMENTATION-STATUS-through-revision-103.md),
revisions 104–229 in [the second historical log](history/IMPLEMENTATION-STATUS-revisions-104-to-229.md), and
revisions 230–499 in [the third](history/IMPLEMENTATION-STATUS-revisions-230-to-499.md).
The product contract and milestone gates remain in [the implementation plan](design/INTERCAT-IMPLEMENTATION-PLAN.md).

## Where the product stands

M0 and its IC-010a capture-impact follow-on are complete on the measured development build. M1's journal, profiles,
physical store, commit/recovery, and leases exist, and since revisions 434 and 435 an interval release keeps the
identity evidence and the open operations of what it retains. Since revision 439 a follow keeps a rolling window of
session time, and since revision 442 a person's pin keeps a session's records from a moment through every release,
within the size it allows, and since revision 448 a rolling capture's broker releases what its follow gave up; canonical import reuse and mechanism breadth remain. M2 has a real broker-driven Explore and saved-session Desktop flow, a navigable real
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
Since revision 440 its RPC calls and their other ends are kept beside them, so its first call ranking, RPC listing or
brush pairs no call, and since revision 441 its HTTP exchanges, so its first HTTP listing groups no buffer. Since
revision 456 its zooms count from persisted tiles, since revision 457 its brushes from those tiles' tallies of what
their records bind to, since revision 458 a group's lanes, since revision 459 a process's direction rows and since
revision 460 a channel's two ends, so none opens a segment: at 10M rows a brushed ranking takes 15 ms at the 95th
percentile, a 40-process group's first lanes 52 ms, a process's first direction rows 156 ms, a connection's first ends
192 ms, and the session's working set ends near 320 MB.
Since revision 462 a zoomed view, and since revision 463 the whole session, is counted a column per device pixel and
drawn as §6.2's density: a lone record's mark is drawn 5 px wide, a point beside it snaps to it, and its card and a
click keep its own interval.
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
two real sessions, one sparse and one dense. Since revision 545 it relates processes by paired UDP flows beside TCP
connections, a pair's edges side by side. Processes with no relationship are counted in one parked node, since
revision 546 the less busy of those that only reach other hosts in another, past the eight busiest, and groups
collapse only under budget pressure. Since revision 550 the Other hosts node names the busiest addresses the drawn
processes' connections ran to. Hubs draw as stars with components apart, executable groups read as file names,
and positions survive descents and live publications. A bounded metadata search now finds groups, process instances and
channels by name, PID or endpoint, and since revision 551 processes by an address they reached, which since revision 553 the graph marks; endpoint strings and addresses can match but are omitted from result snippets. Graph marks now explain their scope and evidence on hover; nodes
can be dragged into pinned positions or pinned from the keyboard, and an explicit re-layout preserves those user constraints.
Below the machine rung the graph draws only the rung's neighbourhood, and one **Rest of the machine** node counts every
other process. Double-clicking a relationship's edge, or Enter on its row in the relationship table, opens its channel,
or an RPC relationship's linked calls. The timeline draws in colour the records E would
list for the rung, over every record in grey. A timeline cell explains itself on hover, as graph marks do, and once
chosen in the inspector, which lists its first records; since revision 471 the graph highlights what they belong to.
Since revision 493 the graph's nodes travel to new places and a new selection's ring settles onto its node, and since
revision 494 a timeline coming back to the newest generation is carried there, unless the platform reduces motion.

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
| IC-012 profiles | Metadata Explore, Focused TCP and RPC peers enforceable; a Content request for WinINet's HTTP exchanges (ADR-037), and the content fixture, keep content through `icat record` only, scoped to named processes, and since revision 444 as evidence only too, whose content `icat follow` mirrors (ADR-047); other Content requests preview only. Since revision 451 the broker prepares a content capture of its client's own processes alone, each named by its ID and start, its policy digested, and since revision 452 starts one, holding those processes while it records, its content limit a stop it states (ADR-049); since revision 453 `icat capture --profile content` asks for one, refusing a process whose ID passed to another before the broker pinned it, and since revision 454 the window, its processes chosen from the person's own and the broker's review shown before it starts; since revision 543 RPC peers records through the broker too, from the window's Keep and `icat capture --profile rpc-peers`, ALPC enabled by kernel flags with no provider request | HTTP/2 and compressed responses through WinINet's capture; broader profiles remain. |
| IC-013 canonical import | ETL import into verified session implemented | Completed-import reuse/catalogue, normalizer-upgrade generations, ETL/journal overlap disclosure. |
| IC-014 broker | Authenticated pipe, protected root, durable ownership/recovery, live evidence and live preview counts, ordinary CLI/Desktop client implemented; parent-owner parser blocker repaired and CLI/Desktop Explore exercised on the affected host; a crashed client's capture qualified to stop at lease expiry, finalized and leak-free, and its session finished by the next launch from the follow's ticket (`live-follow-v1`, qualified on real ETW), and a crashed `icat capture`'s by `icat follow <session>`; a connection bounded by request rate rather than a total, so an owner keeps it for a 24-hour capture; since revision 394 tests hold P18, P19 and P20 on any host: what the broker can reach, ownership by the authenticated identity alone, and no intrusive peer resolution | Installer pre-creation, retail-build matrix and remaining broker release qualification. |
| IC-015 metrics/entities | Source-observation metrics, process/executable grouping, TCP/UDP relations, peer/channel lower bounds; since revision 156 the relation index counts records by their other end, and a relation's untimed records, as it derives, so the overview reads no row's relation; since revision 157 a generation's instances and relations extend the previous generation's, exactly, or are derived in full; since revision 162 a finished session publishes their state as a derivation checkpoint, which a reopen builds both from (`derivation-checkpoint-v1`), and since revision 163 its whole-session overview counts beside it (`overview-index-v1`), so a reopen opens no segment, with each overview column's bytes per mechanism since revision 289 (minor 2), and each process's and TCP channel end's whole-session bytes before any policy since revision 292 (minor 3); since revision 166 each instance's own records per mechanism (`process-activity-v1`, entities-v1 §4a), extended between generations and kept in the checkpoint's format 1.1, rank the ranked table; since revision 173 IPv6 ends relate (`transport-endpoint-relation-v4`); since revision 178 RPC calls are derived as operations (`rpc-call-operation-v1`), and since revision 183 counted on the logical-operations basis (`metrics-v1` §8a); since revision 373 `icat operations`, `icat exchanges` and `icat channels` say what the capture covered of what they list, from its ledger, and no source is said uncollected from its records' absence (R21); since revision 374 `icat processes` states the coverage `icat metric` states of the same answer, and never that a process none of whose bytes was measured sent nothing (R21, P1); since revision 379 its table says a process's bytes are unmeasured, or that it made no sends or receives, as the window's rows do (R21, R18); since revision 380 its call, exchange and peer tables say untimed, not reached, unmeasured or none where they printed a dash (R21, R18); since revision 376 a binding's reason reads in one set of words, and direct evidence only's exclusions are said as what they are (R5, R22); since revision 377 `icat session` says the coverage ledger's facts in its coverage reasons' words (R5); since revision 378 `icat metric` names its basis, byte domain and accounting in the window's words (R5); since revision 404 a grouped answer groups by terminal session, a process whose lifecycle records name none unattributed by that reason (`metrics-v1` §6, `icat metric --group-by session`); since revision 440 a finished session keeps its RPC calls and their other ends in an operation index beside its checkpoint (`operation-index-v1`), so a reopen's first call ranking, RPC listing or brush pairs none, and since revision 441 its HTTP exchanges (minor 1), so a reopen's first HTTP listing groups none | Canonical transfer owner, operation durations and operations beyond RPC calls (counted since revision 183), resource topology (process parents and children are shown since revision 211), relations beyond TCP and UDP, full coverage epoch publication. |
| IC-015a segments | Complete observation/source-field tables; since minor 1, every byte a reader interprets has a checksum of its own, and a published segment's reader reads each column when it is first asked for; since revision 161 the reader cache charges what a reader holds and trims readers to session time and mechanism past its budget; since revision 172 `observation-v2` holds IPv6 endpoint addresses, written only for a segment that has one | Compression and derived scale structures are later work. |
| IC-016 store | Complete M1 commit/recovery/lease/explicit-retention scope; a lease confirms measured dependencies from one directory listing; a viewer opens a session from one listing and hashes its segments, dictionaries and journals after the first view, falling back to the last-known-good, stated, when a file changed; queries share verified immutable segment readers, safe across threads, admitted within 256 MiB of published payload per store, pruned to what the selected generation names; a viewer holds one store per session, a capture's writer included, and keeps readers only for the session it shows; a writer removes superseded manifests as it publishes, and a reader waits out that removal; since revision 162 an index is published as a generation of its own (`CommitIndex`), carried by no additive generation and released with the segments it describes; since revision 234 kept content is a `Content` dependency beside the journal (`content-v1`), carried like a journal and released with its journal chunk, and since revision 306 on its own too, by a `Content` retention once its capture has finished; since revision 315 every later generation carries the latest journal and content release; since revision 442 a person's pins keep a session's records from a moment through every release, within the size each allows, in a file of their own beside the generations (`retention-pins.json`, store-v1 §8, ADR-046), which `icat pin` lists, places and removes, and since revision 443 the window's **Keep records…** from its time scope's start, its size line stating them; since revision 446 a chunk release states the chunks and records its recording gave up in all (store-v1 §8, ADR-048), which `icat session` says | None open in its M1 scope; since revision 450 the size line says what a capture whose broker rolls its evidence holds of its journal limit (IC-016a, S5). |
| IC-016a checkpoint | In progress: since revision 434 an interval release (`Interval`, ADR-043) gives up a session's oldest journal units with their rows, keeping as evidence the lifecycle and first records of every process and connection a retained record names, so every retained record binds, pairs and keys as before; since revision 435 an RPC call, its other end's ALPC evidence and an HTTP exchange open across the boundary keep their records (I20); since revision 436 coverage over any scope reaching before the boundary is a partial gap that says why, and the window says from when a session keeps every record; since revision 437 `icat retain --release-before <moment>` discloses a release and performs it with a reason once no recorder can be writing the session; since revision 438 a follow goes on across releases of the session it derives into (ADR-044); since revision 439 `icat follow` and `icat capture --keep-last` keep the newest window of session time (ADR-045), and since revision 442 stop at a pin's moment, stopping the follow once the session outgrows what the pin allows (ADR-046); since revision 445 the window keeps one too, and draws a released session from its boundary; since revision 446 a follow goes on across the evidence's chunk releases, placed by their bytes and what each states it gave up in all (ADR-048), and since revision 448 the broker releases the chunks a rolling capture's follow gave up (`ReleaseFollowed`), so its journal allowance bounds what its evidence holds; since revision 450 the size line projects a rolling capture's stop from what its window holds, says when it begins releasing, and when a pin keeping it past its window stops it | A rolling capture measured on Windows for hours, its evidence and session holding their window: here only scripted captures exercise the broker's releases. |
| IC-017 Desktop projection | Real overview, channel/evidence ladder, bounded metadata search, layout scheduling, live follow, interval/zoom/minimap with wheel and keyboard, exact L0 mechanism lanes, L1 process-owner lanes, L2 source-direction rows and L3 channel-end lanes banded by direction, with shared scale, own coverage, hover/time selection, persistent table/step focus and keyboard/wheel scrolling, exact bounded query data carried through live publications, the visible range as the default scope with a scope lock, and a bounded §6.3 graph with relationship-first layout, semantic hover, manual pinning/re-layout, quiet folding, minimal group collapse, table-shared selection, anchored carried layout, per-rung neighbourhoods with a context node, §6.7's edge double-click and back/forward history that restores each rung's interval, a per-rung timeline focus that counts what E reads, a selection highlighted in the timeline by its own exact count (§6.4) and a Ctrl+click multi-selection that Enter turns into a filter (§6.7), a labelled live edge that previews unpublished records within §12's steady-state budget (P26 asserted), a designed waiting state before a capture's first publication, a launch-time offer to finish a session a crashed viewer left, and the saved sessions listed while none is open; since revision 189 the machine and group rungs rank by records or by bytes sent or received (§6.1's metric selector), since revision 190 by RPC calls made or served, since revision 196 by bytes sent and received and by RPC errors, since revision 199 by median RPC call and serve time, and since revision 200 by peers, each listed by its basis since revision 201, and read per second over the ranked interval since revision 209; a large group keeps its process lanes when zoomed, counted coarser, since revision 210; the machine rung's lanes plot what a byte ranking measures since revision 284, a group's process lanes since revision 285, and a process's direction rows since revision 286; since revision 287 every chosen state (a selected row, a toggle or box that is on, a tab, a focused field, a combo box's list) is drawn from the tokens in every mode, never in the platform's accent colour; since revision 323 each relationship names the rule and version that derived it, and a TCP one the key of each connection it rests on, in the table's evidence tooltip, its screen-reader name and the edge's card (R4), and since revision 324 an RPC one its own key, which opens the calls its links join; since revision 325 the relationship chosen in the table is explained beneath it, for a keyboard as for a pointer, and since revision 327 Enter or a double click on it opens it as a double click on its edge does, an RPC one at the calls its links join; since revision 329 a click on an edge chooses its relationship, which its halo, the inspector and the timeline's highlight then show, and since revision 330 the inspector's evidence card and E follow it too, at every rung since revision 345; since revision 335 a selected process states the rule that bound its records and how strongly, a reused PID's later holder with how many the evidence policy left out (§6.8), since revision 336 a chosen paired channel the rule that paired its ends, whether the capture saw it open and close, and its key, since revision 337 a chosen group how it was formed and what its total leaves out, and since revision 338 a reused PID's candidates are counted one action from the explanation that names them, every read following, with the rail saying so until one click returns to correlated evidence, and since revision 339 a session opened from an investigation keeps that choice there with its pins and ranking (`workspace-v13`); since revision 340 a reused PID's holders are numbered wherever a process is named ("PID 100 #2"), as `icat processes` numbers them, and since revision 341 an edge's card names each process end by name and PID, as the relationship table does; since revision 342 the legend keys the lanes' heights and offers each lane its own scale (§6.2's normalization scope), which an investigation keeps since revision 343 (`workspace-v14`); since revision 344 either main pane fills the column by an explicit command (§6.1); since revision 348 a row chosen among a process's rows is what the evidence card counts, the timeline highlights and E lists; since revision 350 the timeline states its time base, session time since the capture's recorded wall-clock start; since revision 352 Esc cancels a drag in progress before it ascends; since revision 353 the card, with nothing selected, names the records E lists; since revision 354 a reused PID's later holder's empty rung says its records were left out as candidates and offers to count them there; since revision 355 a channel's own rung counts the channel on the card, as E lists it; since revision 356 an aggregate chosen in the graph is what the card counts and E lists, its processes' records; since revision 358 a double click follows only a plain click, so a press where a pan, brush or cancelled drag began zooms or opens nothing; since revision 359 E from an aggregate names it; since revision 360 an investigation keeps the window's panes, the split and the pane filling the column, for every session opened from it (`workspace-v15`); since revision 362 the inspector states beneath its time scope what the capture covered there, mechanism by mechanism (R21); since revision 367 an export, the window's and `icat export`'s, states the same of its scope (R21, R18), and since revision 407 how its processes were grouped, `icat export --group-by session` reaching a session's rung as the window does (R18); since revision 368 a record without a size says why in the rail and the inspector (R3, R5); since revision 370 a channel's own rung names the channel in the inspector, as its card counts it (I5); since revision 371 a selected record states its own coverage (R21); since revision 372 an export's CSV, on every line, and the redacted sharing report state their scope's coverage too (R21); since revision 373 the channel browser says what the capture covered of TCP, announced as the reason an empty list holds none (R21); since revision 375 `icat overview` states the coverage the inspector states beneath its time scope (R18); since revision 382 a one-sided connection's bytes, on its rung and in `icat channels`, and a selection's in the inspector say a direction no transfer measured as unmeasured and one with no record as such, never 0 B (R21); since revision 385 `icat processes` explains each instance's binding in the inspector's words, and what the evidence policy left out of a reused PID's later holder (R18, R21); since revision 386 `icat channels` says how each channel was paired, as the inspector does (R18); since revision 387 a process's lane can be pinned at the top of its group's lanes, by `P` with the timeline focused or the timeline's own button, marked with a pin's head and kept across the session's publications (§6.2); since revision 388 an investigation keeps them for the session, in the order pinned (`workspace-v16`); since revision 389 the timeline draws only the rows in view, so a 200-lane group's frame costs what a group of a few lanes' does (§6.2); since revision 390 a search typed at a group marks the lanes it finds and scrolls its hit's into view (§6.2); since revision 405 the machine rung groups processes by executable or by the terminal session their records name, regrouping the overview it projected, each session named in `icat metric --group-by session`'s words (§6.3), which an investigation keeps for the session since revision 406 (`workspace-v17`); since revision 409 a timeline cell chosen by a click, a step or the interval table is explained beneath the time scope, and a click on a lane counted coarser than the view chooses its own cell (§6.8, R13), and since revision 411 E from it lists exactly its records, behind a filter chip (§6.4), which since revision 466 the inspector previews, listing the first of them beneath the time scope under a title for the cell, and since revision 468 opens one of them in E, selected there, and since revision 471 the graph highlights the relationships its records belong to, or the nodes of its processes, which since revision 473 the relationship and ranked tables mark too; since revision 472 `F6` and `Shift`+`F6` move the keyboard between the ranked table, graph, timeline and inspector, or the tables while shown, each where a person works it; since revision 474 a ranked row stays chosen when a brush or a ranking restates its rows, Enter on an interval's row lists that moment's records, and a rung with no rows gives the reason it has none the keyboard; since revision 475 `F1`, or the status strip's Keys button, lists every key the window takes, by where it acts; since revision 476 a chosen record's facts are a list the keyboard reads a fact at a time; since revision 477 the graph says the node its arrows move to, and the timeline the moment `[` and `]` step to; since revision 478 the investigation window's timeline says the column its keys move to, the window opens with the keyboard on its session and never leaves it on nothing, and `Ctrl`+`Tab` shows its pages, and since revision 479 `F1` lists its keys; since revision 480 every dialog opens with the keyboard on one of its own controls and closes on Esc, and Enter does a form dialog's action, and since revision 481 the windows a record opens give the keyboard to what they read, and since revision 482 the main window gives it to Stop until a capture's first view and to the ranked table once a session is shown, since revision 483 as the held-generation banner or an unfinished capture's card goes, and since revision 484 as any control goes from under it, a list's row restated under it giving it to the row chosen in that list since revision 485, and since revision 486 the capture card says aloud what an action did, never a recording's count or a package's percent, and since revision 487 each control is named in a few words, what it does its help text; since revision 416 InterCat's own processes are labelled wherever a process is described, since revision 417 one command sets them aside from the rows, the graph and the channels, counting what it set aside (§19.5), and since revision 419 an investigation keeps that choice (`workspace-v18`); since revision 420 a group past the 200-lane bound draws its pinned and busiest members in lanes of their own and folds the rest into one exact lane beneath them, from which a pin brings any of them out (§6.2); since revision 421 a Wall clock toggle reads every instant the view states on the wall clock the capture's machine read, the axis naming its day and offset (§6.2), which an investigation keeps since revision 422 (`workspace-v19`), and since revision 423 a time typed into the search is gone to, its cell made the analysis interval (§6.7), and a zoom draws every lane at its own resolution, a mechanism with nothing in view among them; since revision 426 a moment gone to gives the timeline the keyboard, which says its analysis interval; since revision 427 the interval table states a wall-clock window whole at the smallest window, and keys no mechanism for an interval with no record; since revision 429 a relationship table listing none says why in its place; since revision 431 the timeline's axis ticks round instants of the ladder between its ends, in session time or on the wall clock; since revision 432 export, a redacted report and a re-layout are offered only where they act, saying why not; since revision 485 a view paused while a capture recorded shows the session as saved once it ends, never saying recording continues | Resource topology once derived. L4 lanes beyond RPC calls (drawn since revision 181) and HTTP exchanges (since revision 290), and byte composition once IC-015 derives operations that carry a length. A level of the overview pyramid spanning segments, and a live generation's levels (S4; a finished generation's top level is persisted since revision 163, its deeper levels since revision 456, and what their records bind to, which a brush counts, since revision 457, a group's lanes since revision 458, a process's direction rows since revision 459, and a channel's ends since revision 460), and exact live cadence at 1M rows and beyond. A real screen-reader pass on Windows (the automation tree is audited headlessly since revision 131). |
| IC-018 query identity | Metrics identity frozen; CLI/Desktop export scopes share projection, and since revision 326 an evidence rung's pages and its export read its scope through one mapping, an RPC or HTTP key included; since revision 410 a timeline lane's narrowing to one mechanism, source direction or channel end too, with an identity of its own (§6.4); since revision 424 `icat evidence --from` places a moment by the reader the window's search places it with, and since revision 425 every `--interval` takes a time with its unit, as the window states one | Full UI query identity, generation-aware numeric cache/cursors and coherent bundle publication. |
| §11.3 sharing | All three presets, CLI and Desktop: the metadata-only report (`intercat-share-report-v1`), the reopenable redacted session package (`redacted-session-v1`) and the unredacted original evidence package (`original-evidence-package-v1`); since revision 372 the report states its scope's coverage, mechanism by mechanism, in words a leak test pins (R21); since revision 397 a redacted package can hold an interval of its source with its processes' lifecycle records, its coverage stated for the interval only (`redacted-session-v1` §11), which the window offers from its time scope since revision 399 | A redacted package of a whole investigation, which is not defined: a package's pseudonyms hold only within it. |
| M3 IPC breadth and content | Exit gate met for its measured scope ([review](reviews/M3-exit-review.md), revision 252) | Pipe and shared-section topology unavailable (to M7/M9); RPC over TCP, HTTP/2, compressed responses and asynchronous WinINet unmeasured; timing profile unavailable. |
| M4 multi-machine | In progress: since revision 253 an investigation's workspace (ADR-038) names separately valid sessions by identity, one member per capture, each resolved against where it was last found with the reason, never writing to one; a live capture's host identity includes its installation's. Since revision 254 (`workspace-v2`, ADR-039) a person aligns members to one member's clock with stated bounds, and `icat workspace compare` orders two members' instants only beyond their combined uncertainty. Since revision 255 (ADR-040) a live capture records its clock against the wall clock and its boot, and since revision 256 (`workspace-v3`) a workspace aligns one boot's captures exactly and others through their recorded wall clocks; since revision 257 the Desktop shows an investigation in a window of its own that lists, relinks, adds and opens its sessions; since revision 258 (ADR-041) `icat workspace correlate` proposes candidate joins between captures, since revision 259 the investigation window aligns sessions and lists candidates, since revision 260 (`workspace-v4`) a person's decisions about candidates are kept revisions, flagged when the alignments change, since revision 261 the window draws the merged time, a lane per session, since revision 262 two captures of one host that ran at once are flagged, since revision 263 (ADR-042) an investigation is packaged with its sessions, since revision 264 (`workspace-v5`) two instants a person reads in both measure the clocks' rate, since revision 265 (`workspace-v6`) a session is aligned through another, since revision 266 (`workspace-v7`) a person confirms two host identities are one host, since revision 267 the window compares two instants, since revision 268 it zooms the merged time and opens a column's records, since revision 269 (`workspace-v8`) candidates mirror through known address translations, since revision 270 (`workspace-v9`) a person keeps notes, pinned on the merged time, since revision 271 (`workspace-v10`) saved views of it, since revision 276 a capture that ends before its last publication, as a killed broker's does, still names its boot and start's wall clock to align by, since revision 277 candidate joins and the merged time name the generation of each capture they read (I16), since revision 347 its window lists what it keeps of each session's view, as `icat workspace show` does, since revision 381 its merged time hatches, and says, where a session's capture saw nothing (R21), since revision 383 its candidate joins say what each compared capture covered of TCP and UDP, and since revision 384 `icat workspace timeline` draws its merged time as the window does (R18), and since revision 428 its axis ticks round instants of the 1–2–5 ladder, each named where it is drawn, and its words say times in the digits their span needs; since revision 430 its Align dialog opens on the alignment in force; since revision 496 a session it opens is shown in front of it, its window InterCat's peer | A known two-host exchange (needs a second host); alignment from shared markers, which waits on a marker both captures record (M7, M11). |
| M5 release | Open; since revision 391 `icat support` writes §20.6's support bundle, with nothing a record holds (`support-bundle-v1`, P16), and since revision 392 the window saves it for the session it shows. Since revision 393 a session's size, bytes per record and §12.1 tier, and a recording's time left under its limits, are stated in the window, `icat session` and `icat capture` (S5), and since revision 450 for a capture keeping a window from what the window holds, with when it begins releasing - the eviction point S5 asks for - and when a pin stops it. Since revision 395 `icat demo` writes the gate's demo investigation of two hosts, generated and saying so wherever it is read (`demo-investigation-v1`), and since revision 396 the window's Explore the demo makes it once beside the sessions folder and opens it. Since revisions 456 and 457 a finished session's zooms and brushes count from its persisted tiles, opening no segment, and a brushed ranking of 10M rows meets §12's 250 ms with room, at 9.5 ms, and at 15 ms since revision 460 counts a channel's ends from the same tallies (S3, S4) | Full scale/reliability/accessibility/installer/build matrix and release gates. |

## Recent slices

- **Revision 554 — the projection opens Other hosts into its busiest hosts (§6.3):**
  - Every connection to another host ran to one node, so the graph could not show two processes reaching one host.
  - Opened, the busiest hosts of the drawn connections are drawn on their own, named by address, each edged from the
    nodes that reached it per transport, the rest kept in Other hosts; no process moves, as many hosts are drawn as
    the budget allows, and a re-count keeps them. The window does not open it yet.
  - Debug and Release each ran 2,373 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 11 mutations was caught: Other hosts never opened; a host past the busiest opened; hosts opened past the
    budget; hosts ranked with the rest of the machine's connections; every connection drawn to Other hosts, or
    counting a record per address; a node's edges to different hosts sharing lanes; a host counting no process; a
    re-count dropping the hosts opened; a link drawn twice to one host accepted; and a host node refused for holding
    no process.

- **Revision 553 — the graph marks what a search finds (§6.7):**
  - A search marked the processes it found in the timeline's lanes only; the graph showed none of them.
  - While a search is typed, each node holding a process it finds carries a dot, filled where every process it
    holds is found and open where some are; Other hosts is marked for an address. The summary counts them, a found
    node's card and its spoken name say so, and nothing is selected.
  - Debug and Release each ran 2,372 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 12 mutations was caught: an aggregate found in part marked wholly; Other hosts never found by an
    address; marks kept from an earlier search or an earlier drawing; the summary not counting what is found; an
    aggregate's card counting all its processes as found, or a process's card silent; a process not found by an
    address it reached; the filled mark drawn in the pane's colour, the open one not drawn; and the spoken name not
    saying a node is found, or partly.

- **Revision 552 — the transport relation index over random captures (R10, relations-v1):**
  - A property test derives ninety random captures - reused PIDs, loopback, unseen local ends and other hosts,
    IPv4, IPv6 and mapped, TCP and UDP, lifecycles among transfers - and holds the census to the relationships
    and one-sided connections under every policy, each process's reach of other hosts to its one-sided
    connections by address, and every answer to the same records cut into other segments.
  - The extension and checkpoint tests compare the census, one-sided connections and reach too. Nothing needed
    fixing; a withheld relationship's record can have an admitted other end, as relations-v1 §4 intends.
  - Debug and Release each ran 2,370 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    property test alone caught 9 of 10 mutations: a census counting loopback as other hosts, a connection's own
    address or an IPv4-mapped address of another host as this machine's, ambiguous incarnations in the reach,
    records naming no endpoint or unpaired ones miscounted, relationships admitted by one end's binding, other hosts
    counted by their local address, and connections listed with their own endpoint as the remote one. The tenth, a
    process's reach taking its last connection's binding rather than the weakest, answers alike while entities-v1
    binds every transfer of one instance at one strength.

- **Revision 551 — find the processes that reached an address (§6.7, §6.3):**
  - The search found processes by name or PID only; a person holding an address had to open every process's rung.
  - An address whole, or begun up to a dot or colon, now finds the processes whose connections reached it, IPv6 in
    either case and a mapped address by its IPv4 one; a hit says what reached the matching addresses, never the
    address (P16), and a group's lanes are marked by the same rule. Other hosts and the search box say so.
  - Debug and Release each ran 2,367 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 12 mutations was caught: a number or word read as an address; an address found by any beginning rather
    than up to a dot or colon; a mapped address not found by its IPv4 one; IPv6 found in one case only; a begun
    address said beside the whole one; a hit not saying what reached it, its UDP flows uncounted or an address
    counted once per transport; the hits, the rule lanes are marked by or the lanes themselves finding no process by
    an address; and a process ranked by its name where an address it reached matched better.

- **Revision 550 — name the busiest other hosts by address (§6.3, P7):**
  - The Other hosts node said how many hosts the drawn processes reached, never which; only each process's rung
    listed its own. A host link now carries its connections address by address, its counts its addresses'.
  - The node's card names the three busiest hosts by records with what reached each, its description five, and an
    edge's card the three its source reached; a transport's line counts distinct hosts, and a process's rung names
    its own alone. icat overview lists the five busiest, and its JSON carries each host link's addresses.
  - Debug and Release each ran 2,365 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 24 mutations was caught: a process's addresses out of order by records, connections or address, IPv6
    written as IPv4, its connections miscounted per address; a link's connections not its addresses'; a host's UDP
    flows counted as TCP, its processes uncounted, the hosts out of order or ties; a transport's hosts counted once
    per process; one process's hosts saying whose; a list that fits named as the busiest; a link with no address, an
    address twice or fewer records than connections accepted; the overview dropping addresses; Other hosts naming
    the whole machine's hosts on a process's rung, an edge's card another's, or either naming too many or too few;
    and icat overview leaving out the busiest.

- **Revision 549 — the graph's compaction over random machines (§6.3, R10):**
  - A property test draws six hundred random machines - groups of every size, TCP, UDP and RPC relationships in
    either order, connections to other hosts - under random budgets, opened groups, kept processes and focused
    rungs, and holds each drawing to §6.3: every process and relationship once, every connection to other hosts
    but a context node's, no aggregate of one, even lanes, and Other hosts only folding only the less busy.
  - It found an opened group none of whose processes a snapshot holds throwing over budget; the window builds no
    such snapshot today, and the projection now finds nothing to fold there.
  - Debug and Release each ran 2,362 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    property test alone caught each of 11 mutations: edges met in the other order not parted, lanes not centred, a
    node's edges to other hosts not centred, an aggregate of one, the busiest folded, a related process or the kept
    process folded, a collapsed group's members folded again, connections drawn from where the process is not, a
    context node's connections drawn, and an opened group none of whose processes the snapshot holds throwing over
    budget, which the example test catches too.

- **Revision 548 — say how to draw RPC calls wherever a graph leaves them undrawn (§6.8, R18):**
  - Only a graph that drew nothing said that RPC calls are drawn only from a capture that collects ALPC, and how to
    record one; a busy machine's graph, relating a few processes over loopback, never pointed to RPC peers.
  - A session holding RPC calls nothing links now says so in the graph's full scope and beneath icat overview,
    whatever else the graph draws; linked calls, or none, say nothing of it.
  - Debug and Release each ran 2,357 tests with revision 473's 93 failures and 4 skips; the one other failure, a
    test of the Other hosts node expecting the graph's old words, was updated, and the Desktop tests then passed in
    both configurations. The tests caught each of 5 mutations: the guidance said of linked calls, or of a session
    with none; an empty graph's reasons leaving it out; and a drawn graph's scope and icat overview never saying it.

- **Revision 547 — the README and icat's help say what the product is now (R18):**
  - The README described milestone M1's first slice - a synthetic graph prototype, TCP-only Explore, content
    blocked; it now says what the window captures and draws, what icat reads and shares, and how to try it with no
    capture, each command run as written against the demo.
  - icat's general help omitted RPC peers from icat capture's profiles; they are now one list, which the parser's
    refusal and both helps read.
  - Debug and Release each ran 2,357 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    tests caught each of 3 mutations: RPC peers left out of capture's profiles, the general help listing them by
    hand as before, and the refusal naming them by hand.

- **Revision 546 — fold the long tail of processes that only reach other hosts (§6.3):**
  - A capture shaped as a busy machine - sixty processes reaching other hosts, three pairs related over loopback -
    drew a star of sixty spokes that hid the relationships between processes.
  - Outside the explicit focus, past the eight busiest by their connections' records, the processes that relate to
    nothing and only reach other hosts fold into one Other hosts only node with the hub's globe, edged to Other hosts
    per transport; collapsed groups, an opened group and a kept process keep theirs, and no aggregate holds one.
  - Its cards and description say what it folds; chosen, it counts and lists its processes' records as any aggregate
    does. The same capture draws fifteen nodes beside Other hosts.
  - Debug and Release each ran 2,356 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    tests caught each of 13 mutations: no fold however many; the busiest folded rather than the least; a process
    ranked by one connection's records rather than all of them; a related process, the kept process, a collapsed
    group's member or an opened group's member folded; the node drawn as Other processes; a budget below zero
    refused only by its slice; and the summary, the node's card, its description and its edges' card not saying what
    it folds.

- **Revision 545 — draw paired UDP flows between processes in the graph (§6.3):**
  - Two processes exchanging only datagrams showed no relationship: the overview drew TCP alone, though the relation
    index has paired UDP flows since revision 57.
  - The graph draws each pair's UDP flows as an edge of its own in UDP's hue, its channels judged by UDP's coverage;
    a brush, the channel rung and its ends, E, the channel browser, icat channels and a byte ranking read them as
    they read TCP's. A persisted overview keeps a flow's ends at minor 5, and an older one's bytes are read again for a
    session with a flow (`overview-index-v1`, `relations-v1` §5e).
  - Two edges between the same two nodes run side by side, 8 px apart, so either is hovered and chosen where drawn.
  - The census explains both transports, a flow's pairing says no record opens or closes it, and the demo's UDP
    answers name their sender first, as a UDP receive does, so its browser's flow no longer pairs with itself.
  - Debug and Release each ran 2,353 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    tests caught each of 33 mutations: TCP's relations drawn alone; a pair's TCP and UDP merged into one edge, or
    keyed as TCP; a flow's channel named TCP, or judged by TCP's coverage over the session, a brush or a page; a
    brush, the channel pages, E or a channel's focus reading TCP alone; a page stating TCP's coverage alone; a byte
    measure, or the overview's kept bytes, without a flow's ends; an overview before minor 5 read for a session with
    a flow, or one written as minor 4; the census without UDP's reason, name, hosts or records; rows without a peer
    said of TCP alone; parallel edges on one line, two met in the other order not parted, or 4 px apart; a flow's
    pairing, its evidence scope, its card, the evidence card's two counts and its ends' words said as TCP's; and the
    demo's answers naming their own end first.

- **Revision 544 — extend RPC calls and their other ends between live generations (I14, §12.1):**
  - A live RPC peers capture, which the window draws since revision 543, paired every call and read every ALPC record
    again at each generation: 0.58 s at 80,000 calls over 640,000 records.
  - A call index keeps the records of each key a later record can still change - a call open, or a record within
    15 s of the latest - so a later generation reads only its added segments, pairs those keys again and merges the
    rest in canonical order; the other ends keep their sorted ALPC records and merge the added ones
    (`operations-v1` §3a). That generation projects in 0.18 s.
  - Both decline, for a full derivation, an earlier record of a key not kept, another file under a segment's name or
    a field published apart from its record, and equal full derivations of random chunked captures.
  - Debug and Release each ran 2,344 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    tests caught each of 18 mutations: an added record of a key not kept read before the late window, or one read
    before what is kept of its key, extended; a source field of a call already read extended; segments told apart by
    name alone; a withdrawn call left standing; calls merged out of canonical order; an ambiguous run kept from its
    first start alone; the window's first record left out of what is kept; no late window; a start's thread misread;
    an ALPC message id of a record already read extended; the ALPC records taken by two extensions, or kept by none
    after one declined; another session's segments extended where no source field tells them apart; server starts
    not read again; sends and receives merged out of order; and calls or their other ends never extended by the
    derivation cache.

- **Revision 543 — record RPC peers from the window and icat capture (§9.4, ADR-035):**
  - The window's Keep lists RPC peers, 10 minutes: every record of ten minutes with the kernel's ALPC messages, a
    choice of its own so the card grows no taller at the smallest window; icat capture --profile rpc-peers asks
    the broker for it. The census, icat operations and a metric's reason name both where they named icat record.
  - The broker refused the profile, wanting a provider request per source where a kernel flag group has none; it
    now counts one per provider-enabled source and refuses a request for, or a process scope on, a kernel flag
    group (`broker-v1` §2).
  - A live RPC peers capture drew no RPC edge until saved, since only its ledger, published last, said it collected
    ALPC; until a ledger exists the plan it retains says so (`normalizer-plan-v1`), so its graph, a brush and a
    checkpoint have the links as it records.
  - Debug and Release each ran 2,336 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    tests caught each of 36 mutations: a session that retains no plan read as an empty one, a plan's bound shrunk, a
    plan without its descriptors refused unexplained, a plan of another contract read or one naming none admitted,
    or a descriptor's mechanism misread; ALPC judged collected with no plan, by RPC calls, or by a plan beside a
    ledger naming none; the overview's edges or census, a brush or a checkpoint judging by the ledger alone; the
    census, a metric's reason or icat operations naming icat record; icat capture refusing rpc-peers or its refusal
    naming explore alone; the window's runner asking for the other profile, its request losing the choice, or a
    capture named as another or without its capital; the Keep choice linking no call, its note or options losing it,
    or the card's note, its start action's words or its preparing headline; and the broker counting a provider
    request for a kernel flag group, letting one name it, taking it for a provider's source, or letting its scope be
    undefined, filtered by process, given process IDs or capture outside what was asked.

- **Revision 542 — draw processes' connections to other hosts (§6.3):**
  - The relation index gives each process's connections to other hosts by transport (`relations-v1` §5d); the
    overview carries them as host links under the evidence policy, and icat overview lists who reaches other hosts.
  - The graph draws such a process on its own, edged to one square Other hosts node: a transport an edge, side by
    side, in its hue and evidence pattern at a constant thin stroke, with no traffic value and in no total; its
    summary, cards and selection say what they are, and a click or double click on an edge reaches the process.
  - The line beneath an empty graph shows only while its pane draws nothing; two keyboard tests' third process now
    sends to an unseen port on this machine, so their graphs stay as they were.
  - Debug and Release each ran 2,328 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    tests caught each of 64 mutations: a reach of other hosts counting an end on this machine, another transport's,
    a paired one - between two of this machine's own addresses too - or one two processes hold, a host per
    connection, a record per connection, or not the weakest binding; host links admitted past the evidence policy,
    without UDP, at the wrong strength, lost to the workspace, kept for a process set aside, or keyed by anything; a
    process reaching other hosts folded as quiet, a context process's connections drawn, two transports drawn over
    each other, the node's processes or records miscounted, an edge's binding not its weakest, the drawing called
    clustered, the node or its edges weighed by bytes, a second node, one holding a process, an edge into it that is
    no host edge, a host link drawn twice or a relationship drawn as one, a link naming no process, records below
    zero, no host or more hosts than connections, the links left unchecked, or an edge's link lost; the node or its
    edges setting a scale, the node drawn smaller, or its edges thickened; a UDP flow called a TCP connection, its
    hosts not "with", one process's hosts unsaid, or UDP said first; the keyboard reaching the node first, its edges
    drawn on one line, or its label's "from"; the window's summary note, a process's or a context's line, the quiet
    node's words, the brush's caveat, an edge's gestures, the node's or an edge's card, a click or a double click on
    an edge, the node's description, its evidence heading or records, the line beneath the graph, the scope's words,
    the summary's node counts, or a cell highlighting an edge to the other hosts; and icat overview's list.

- **Revision 541 — say why the graph draws no relationship (§6.3, §6.8; reported from a live capture):**
  - A live Explore capture shaped as Windows delivers it - manifest-compiled plans, bodies read as the admitter
    reads them, a rundown, loopback TCP over IPv4 and IPv6 - relates both connections in the window's live view,
    now a test of that path; a busy machine's TCP mostly runs to other hosts, and the graph said nothing of it.
  - The relation index takes a census of each related mechanism (`relations-v1` §5c); where a graph draws none,
    its summary, a line beneath it, its node, the relationship table and icat overview say where the TCP went and
    that UDP and RPC calls without ALPC are not drawn. Drawing connections to other hosts in the graph is open.
  - A reader's lease listing failed on a file the writer staged and removed while it listed, ending a live
    follow; such a file is now left out of the listing (`store-v1` §3).
  - Debug and Release each ran 2,314 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    tests caught each of 31 mutations: a census admitting a withheld relationship, counting a relationship from both
    its ends, taking an end two processes hold for one-sided, placing an end's other end on the wrong side of this
    machine - at its own IPv4 or IPv6 address, at another loopback address named as IPv4 or mapped into IPv6, or at
    a mapped address that is not loopback - counting a host per connection, or losing the records naming no
    endpoint; a withheld relationship not given first, one record said as all of several, a lone UDP datagram or a
    capture with no TCP left unsaid, RPC calls called unjoinable from a capture that collected ALPC, or a list's
    last item without its and; the overview's RPC count, UDP census or workspace census lost; the window's summary
    or its node's, table's or line's reason left out, or the line shown at a rung, beside relationships set aside,
    or hidden; icat overview's reason; an IPv6 address the plan compiler refuses, or TCP owned by its header,
    breaking the live view's relationships; and a listing failing on a file removed while it lists.

- **Revision 540 — icat retain's journal prefix release, run whole (S5, R18):**
  - A confirmed release the store refused - beside content kept with a single journal, or from a session read at
    its last complete generation - ended the command with an unhandled exception; what the store refuses at every
    boundary is now disclosed before it is asked, and a refusal is said in the report, exiting 1.
  - A boundary that empties the journal is never offered for confirmation; a session read at its last complete
    generation refuses every confirmed release before measuring, naming icat recover (exit 3).
  - Debug and Release each ran 2,303 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    tests caught each of 22 mutations: content beside a single journal or a standing pin left undisclosed, or a
    release that empties the journal, keeps content or is pinned offered for confirmation or attempted; the store's
    content or pin refusal in other words; a confirmed release from a session read at its last complete generation
    measured rather than refused; that session's note left out of a journal, interval or content measurement, or
    such a measurement exiting 0; a reason alone told to add one; the report not written where asked; and a refused
    release's note, the content release's command, the pin's field or JSON, or the content beside the journal left
    unsaid.

- **Revision 539 — the refusals of pin, checkpoint, channels, exchanges and profiles, run whole (R18):**
  - Each refusal of an option's value names what it expects and the value given; icat profiles' "Invalid process
    ID" and "Unknown mechanism" are worded as every other command's, and a repeated --pid or --channel is said to be.
  - icat checkpoint says what came of it once, in its report, where it said it twice when nothing was published.
  - Debug and Release each ran 2,298 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    tests caught each of 15 mutations: --allow-mib's, --process's or --page-size's value left unnamed; icat pin's
    hint naming the folder unquoted; --pid's, --retention's, --inspection's or --mechanism's value refused in the
    old words, or a --pid or --channel given twice; an existing report or an unknown profile refused in the old
    words; and icat checkpoint saying what came of it on standard error as well as in its report, saying it
    publishes without saying when it does not, or told it has no report.

- **Revision 538 — icat rederive and icat content run whole (R18, R20, §3.7):**
  - icat rederive refuses a session read at its last complete generation before replaying it, naming `icat
    recover`; a check of one says the generation it checked; a session with no saved plan is refused without a
    progress line, in words that promise no migration.
  - Every command names an option given twice as given twice, not as unknown.
  - icat content's text view, saves, whole parts and what there is not to show are tested; a range past the kept
    bytes names them, in the window's content dialog as well.
  - Debug and Release each ran 2,275 tests with revision 473's 93 failures and 4 skips, and one more: a test still
    held the no-plan explanation's words "legacy session". Its assertion now holds the new ones, and the journal
    tests passed in both configurations again, but for their 3 known Windows-only ticket tests. The tests caught
    each of 12 mutations: an option or a flag the command line took forgotten, so one given twice reads as unknown,
    or a repeated one never named; a range's or a part's buffers checked at one end only; --rows-per-segment's value
    unnamed; a session read at its last complete generation replayed rather than refused, or its check said to
    re-derive it; a progress line said for a replay or a check that never begins; and the no-plan explanation or
    refusal in their old words.

- **Revision 537 — a coverage pass over the command line's untested paths, each run whole (R5, R18, R21):**
  - icat operations lists a process's calls with each call's other end named as its group's is, and says of each
    side it lists how many calls found their other end, never "0 of the 0 client calls".
  - icat session states a segment's byte totals in icat metric's words, not TransportObserved and SendSide; icat
    metric names a record's accounting in words and counts one declared contribution in the singular.
  - Tests now run the call listing and its refusals, a session's kept content, and icat metric's process focus,
    matrix and canonical request.
  - Debug and Release each ran 2,264 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    tests caught each of 13 mutations: a call's other end named "PID · 1960", or by its executable without its PID;
    the server calls listed never counted, or a side with no call listed counted; none said as 0, or a lone call
    agreeing in the plural; the two sides joined otherwise; a metric's declared contributions counted in the plural,
    or a record's accounting named by its identifier; and a segment's byte domain or accounting named by its
    identifier, its bytes written as Bytes, or its contributions uncounted.

- **Revision 536 — icat recover and icat staging run whole, as a person runs them (§20.1, R18):**
  - A publication or release refused on a session whose newest generation does not verify says so in plain words
    and names `icat recover "<folder>"`; icat session names it too, and names icat staging with the folder.
  - icat recover's review says what readers read, what is refused until the repair and what it keeps; after a
    repair it no longer promises a cleanup nothing performs.
  - icat staging's review lists every file, what it is and whether a cleanup removes it - the ownership markers a
    confirmation removed unnamed included - and each refusal names the review that supplies what it lacks.
  - Debug and Release each ran 2,253 tests with revision 473's 93 failures and 4 skips, and one more: a test still
    held recover's old "No repair is needed or performed" sentence. Its assertion now holds the new one, and the
    CLI's 96 tests passed in both configurations again. The tests caught each of 29 mutations: either half of a
    confirmation accepted alone, by recover or staging; a review that loses what readers read, or claims to keep the
    bytes of a pointer that is gone; a repair that claims a backup it did not make or is silent about the files it
    kept; those files counted without their noun or as no bytes; a generation that fails called a damaged pointer;
    icat session or a storage refusal naming recover or staging without the folder; a cleanup counting fewer files
    than it removes, or a review leaving a marker unlisted; a cleanup saying it keeps what it kept; every row listed
    past twelve, or the rest uncounted; files removed stated in a review; a writer's file or an unmarked one unsaid
    or said as plural; nothing staged said as a cleanup that removes nothing; the confirmation printed unindented;
    five files removed said as none; and a cleanup headed as a review.

- **Revision 535 — icat verify re-evaluates a curated fixture's run and says what it found (I14, R18, R21):**
  - Runs rebuilt from each of the four curated transport fixtures verify to the coverage they were published with;
    only the IPv6 fields added since, each zero, differ.
  - Its report stated only the tier, so every UDP run - whose topology criteria cannot be measured - exited partial
    unexplained; it now states the coverage as icat measure does, every gap included, from one report both use,
    with each criterion named in words.
  - Its JSON names its contract, fixture-verification-v1.
  - Debug and Release each ran 2,248 tests with revision 473's 93 failures and 4 skips. The tests caught each of 12
    mutations: a criterion named by its identifier in the domain's words, in a gap's sentence or in the report's
    table; a single false attribution that were recorded; the report's gaps left out, its tier named by its
    enumeration, a criterion counted against its own numerator, or its observed operations counted as all of them; a
    verification naming another contract, leaving its coverage out, naming its fixture without its run, or counting
    an unrelated observation it did not exclude.

- **Revision 534 — icat counts one in the singular (R5):**
  - Run on the smallest sessions - one record, one segment, chunks of one record, an investigation of one session -
    icat said "1 records", "First 1 of 1 observations", "1 observations in 1 segment", a re-derivation's "1
    observations, 1 fields", "1 source fields", a release's "2 to 2 records", "1 of 1 sessions" and "PID(s) 4242".
  - Each now counts in the window's words (`CountText`), and so do the last "(s)" hedges: icat profiles' compile
    note, a profile's descriptors and processes, icat record's losses, and the broker's recovery, maintenance and
    interrupted-capture messages.
  - Debug and Release each ran 2,241 tests with revision 473's 93 failures and 4 skips, and one more: a profile test
    that still asserted the hedged PID(s), corrected, after which the command line's 85 tests passed in both. The
    tests caught each of 14 mutations: a session's extent or newest chunk counting 1 records, or listing its only
    observation as the first 1 of 1; a metric's read or answer counting 1 observations; a re-derivation counting its
    source-field segments from its observation segments; a package's rows, redacted or original, counting 1 source
    fields; a compaction counting 1 source fields; a journal release naming its one boundary as a range, or saying
    the boundaries run from it when only one is allowed; an investigation's copies counting 1 sessions; a join
    refused saying there is 0 candidates; and a profile naming one process as PIDs.

- **Revision 533 — icat follow finishes an unfinished capture in the card's words (§3.1, R5, R18):**
  - `icat follow <session-dir>` had never run in a test. An unfinalized capture finished whole was reported as
    "Following stopped … run this again to continue", which was then refused; it is now a partial recording
    derived, saying what can never be recovered.
  - Unreadable evidence said "The session keeps the 0 chunk(s)" of a session holding none, with a partial result's
    exit code; it now says nothing had been saved and exits as corrupted input, and otherwise keeps "the 1
    published chunk", then gives the reason. A session that did not exist was said to hold everything.
  - The card and icat share their follow words (`FollowText`): a capture's first publication reads "1 published
    chunk", not "1 published chunks", and so do the live preview's card and icat capture's progress.
  - Debug and Release each ran 2,235 tests with revision 473's 93 failures and 4 skips. The tests caught each of 19
    mutations: a follow's count of none said in the singular and of one in the plural, its chunks not called
    published, or its progress counting the evidence's chunks as derived or as records; the session's finality read
    inverted; a finish saying a session that does not exist holds everything, or that a finalized capture ended
    unfinalized; unreadable evidence said of a session holding nothing as keeping its chunks, exiting as a partial
    result where nothing was saved, keeping one chunk more than it does, or giving its reason in parentheses; a
    finish's progress counting only chunks; an unfinalized capture finished whole still said to have stopped and
    asking to be run again, or one still recording called partial for good; the capture card counting its
    generations as chunks; a live preview's card saying 1 published chunk are, or 1 previewed record fall; the
    card's finishing text losing its progress; and a follower refused for a released first chunk saying its first 1
    chunks.

- **Revision 532 — icat raw says a record as the window does (§3.7, R5, R18):**
  - icat raw printed a record's provider as a bare identity and its codes as enumeration names (Qpc,
    ApprovedMetadata/Retained); it now lists the window's facts in the window's words, each length counted, from
    one set of lines the window and the command share.
  - A redacted package's record is called its synthetic entry by both, found or not, with the package's own reason
    when its entry is missing.
  - icat raw now runs in tests on real sessions - hidden and revealed bytes, a preview stopped at 256 bytes, a
    released journal batch, a redacted package - where no test had run it on one.
  - Debug and Release each ran 2,228 tests with revision 473's 93 failures and 4 skips. The tests caught each of 20
    mutations: a provider named from the wrong identity; a clock's encoding or a body's classification printed as an
    enumeration's name; a length of one counted as 1 bytes; a record outside the retained journal listed without its
    raw identity; a record's content left out of its facts; an extended item's retained length read from its
    original; a preview's last row dropped; a code's words begun with a space; a CPU cycle counter called unknown;
    icat raw listing only a record's first fact, calling a record synthetic because it names no admission policy,
    saying a preview stopped when it had not, or saying no body bytes were kept of a package's entry rather than an
    original's; the query calling an original record synthetic, calling a package's missing entry an original
    record's, or giving it an original's reason; and the window not presenting a package's record as synthetic,
    listing a fact without its colon, or leaving out why no body bytes were kept.

- **Revision 531 — the main window's untested paths, run whole (§3.1, R15, R21):**
  - A capture that ends before it shows anything gives back the session shown before it - its records, actions
    and name - as revision 130 promised; a capture that fails after saying it records is said to have failed after
    that, and one that saved nothing says so, never pointing to a session below.
  - A new investigation that cannot be made says why on the card and opens no window; a theme that cannot be kept
    says so, and where, in place of where choices are kept.
  - O on a call, a record or a saved session double-clicked, the graph's buttons, a support bundle that cannot be
    written, an original copy opened here, the investigation's Open this column and Esc, and a content capture
    through the broker's review now run in tests as a person runs them.
  - Debug and Release each ran 2,222 tests with revision 473's 93 failures and 4 skips. The tests caught each of 28
    mutations: a new investigation that could not be made opening a window anyway, or its card not saying none was
    made; one already there no longer opened as it is, a window whose file had gone not reading it again, and a new
    one not said to be new; a theme not kept still said to be kept, or not saying where it could not be; the session
    shown before a capture not given back when the capture said it could not run or when it failed, never
    remembered, given back over a view the capture recorded or published, or given back without its path or its
    session; a failed capture always pointing to a session below, or said before what it said last; the content
    chooser listing the machine's processes rather than the window's, or its review closing without asking the
    broker; a support bundle that could not be written said nothing; an original copy not opened here; O opening
    only a call whose other end is unknown, and a double click on a record not opening it; Pin node laying the graph
    out again and Re-layout doing nothing; a saved session double-clicked not opening, and a capture's session not
    listed once it ends; Esc not closing the investigation window, and Open this column opening nothing.

- **Revision 530 — the capture card's own actions say why they wait (R15):**
  - Stop and save, Start exploring, Open saved session, the unfinished capture's action and Forget, and Explore
    the demo say why they wait, on themselves and as their help text, in the words their keys or a click say.
  - The redacted package prompt says why its whole session and Choose a folder wait for a session too large to
    package whole.
  - Debug and Release each ran 2,216 tests with revision 473's 93 failures and 4 skips. The tests caught each of 25
    mutations: Start exploring's and Open saved session's reasons dropped, or kept only while a session opens or
    only while a capture is finished, Start's help, Open's fuller hover and the hover saying why the last capture
    could not run; the unfinished capture's action offered once asked to stop or while a session opens, and Forget's
    reason inverted or its hover lost; the reasons not restated once stopping is asked for, as finishing starts, as
    a session starts or ends opening, or as the Keep choice changes; Stop and save's reason once pressed and while
    the capture is saved, and its help; Explore the demo's reason on the card and in the menu, and its help from the
    first frame; the redacted package prompt's reasons for its whole session and Choose a folder; and an offered
    action's tooltip saying only its help.

- **Revision 529 — an investigation dialog's action not offered says why (R15):**
  - The saved views', translations', one host's, package's, align, compare and note dialogs, and the pin dialog's
    Keep records, say why an action or a choice is not offered, on the disabled control and as its help text, and
    what it does once offered.
  - A dialog opened on an investigation that no longer reads says so on its lists' actions, not that they hold none.
  - Debug and Release each ran 2,215 tests with revision 473's 93 failures and 4 skips. The tests caught each of 39
    mutations, one once State translation's explanation was read before a first statement: the saved views' Save
    reason with nothing shown and its help, their reasons once read, with no view saved, chosen or readable and once
    readable again, and for a view saved on another time reference; the translations' explanation, State
    translation's and Withdraw's reasons while a change is written and after, and Withdraw's with none stated,
    chosen or readable and once readable again; One host's reasons while the word is recorded, with no other host,
    with the hosts one already or not, and its help; the package's reasons for a session that cannot be copied, for
    none chosen or none copyable, and its help; the session to align to when it is the only one, and its help;
    Align's and Compare's reasons while they work and after; where a note is pinned while it is not to be, and Add
    note's reason while it writes, after, and its help; and Keep records' before a reason is written and with one of
    spaces alone.

- **Revision 528 — a dialog's action not offered says why (R15):**
  - The record window's Reveal, the content window's Copy as hex, Save bytes, Decode and part toggle, the channel
    browser's Show and Next, and the content capture's Review with the broker say why they are not offered, on the
    disabled button and as their help text, from the window's first frame.
  - A channel page that could not be read no longer says no channel is in scope; a failed reveal of kept bytes
    offers itself again while Copy and Save say why they cannot.
  - Debug and Release each ran 2,215 tests with revision 473's 93 failures and 4 skips. The tests caught each of 36
    mutations, one once written so that it builds: a reason hidden while its action is disabled, an offered action's
    help dropped or set to its reason; Reveal's reason before the record is read, while it is read, with no body or
    original retained, once revealed and when not verified; the channel browser's before its first page, while a
    page is read, with a page not read or older than the window's, with no channel in scope or chosen, and on the
    last page; the content window's before and while its content and bytes are read, with no content kept, no byte
    of the message or none in it, without consent, before its bytes are shown and when they could not be read, a
    failed reveal offered again, Copy and Save offered with what they do, Save refused for a part not whole, Decode
    while decoding and after, and the part toggle offered; the review's reason and help; and the investigation
    window's explanations of its offered actions.

- **Revision 527 — a main-window action not offered says why (R15):**
  - Show source records, Browse paired channels and both Share actions had reasons that never showed while they
    were disabled; each now shows its reason on the disabled button and says it as its help text.
  - Go to parent, the children action, Pin node, Re-layout graph and the record action say why when not offered,
    and what they do when they are; a window with no session says so from its first frame.
  - Debug and Release each ran 2,213 tests with revision 473's 93 failures and 4 skips. The tests caught each of 13
    mutations, one once the first frame was read before the window shows: an offered action's help set to its reason
    or a reason's to what it does, a tooltip apart from its help, the first frame left unexplained, Show source
    records' reason hidden while disabled, Re-layout graph's tooltip dropped or its reason without a session, Pin
    node's reason inverted, the record action offered with no record chosen, Keep records' and Share redacted
    session's help, and Go to parent's and the children action's reasons inverted.

- **Revision 526 — a session of an investigation is called a session wherever it is read (§8.4, R5):**
  - The investigation's refusals, its file problems and the reasons its window's rows give said member, the
    file's word for a session, where the window lists sessions; they now say session, by its leading digits.
  - icat workspace show heads its field and its hosts' table Sessions; its caveats, its help and icat's own help
    say session throughout.
  - Debug and Release each ran 2,212 tests with revision 473's 93 failures and 4 skips. The tests caught each of 25
    mutations, two once placing a session not in the investigation and aligning one to its own clock were tested: a
    member named again in what a session's row says to do, another session's folder, a relink, an addition and a
    capture added twice, a host none of its sessions was on, placing, aligning and packaging a session not in it,
    its time across sessions, a time reference not one of them, an alignment to another than the reference, a host
    confirmation, icat's field, hosts' column, caveats, help and overview.

- **Revision 525 — an investigation action not offered says why (R15):**
  - Eighteen buttons of the investigation window were disabled without a word; each now says why as its tooltip
    on the disabled button and its help text, and what it does once offered where its name does not.
  - Enter or a double click on a session not where it was last found said nothing; it now says to relink it.
  - Debug and Release each ran 2,212 tests with revision 473's 93 failures and 4 skips. The test caught each of 19
    mutations: a reason not shown on a disabled button, an explanation dropped, a tooltip or help text set apart
    from the other, an unreadable investigation called one still being read, Open offered for a session not where it
    was last found and Enter saying nothing for it, the time reference's align and withdraw reasons, one host
    offered among one host's sessions, a decision offered again or a missing one offered to withdraw, a note about
    the whole investigation offered to show, no session or note said as none chosen, the zooms at the whole
    investigation, the zoom with no session placed, Compare instants with no session, and Open this column with its
    column chosen.

- **Revision 524 — a redacted package's record is synthetic from the window's first word (§11.3, R15, R21):**
  - The record window is told what a package's rows open: its title, heading, status, accessible names and
    failures say synthetic before a byte is read, and Reveal is not offered; a row the package holds no entry for
    says the package is not as it was saved.
  - The inspector's record button is named by its label, its explanation its help text, synthetic for a package.
  - An investigation with no session says so, never that every session is where it was last found; Package says
    when the file names none, or could not be measured.
  - Tests run a record whose journal a release gave up, a package's journal gone while it is open, and the
    investigation's package states.
  - Debug and Release each ran 2,211 tests with revision 473's 93 failures and 4 skips; Release also failed once the
    call lane's density test, which hovered a column the lane could count again as the window settled. That test and
    its HTTP twin now wait for the view's own count and point again at a lane counted again, and the UI tests then
    passed whole in both configurations. The tests caught each of 20 mutations: the record window's synthetic title,
    heading, disclosure, status, accessible names, unavailable and failure wording and Reveal, the main window's
    flag and the record button's name and help text, and the investigation's empty status and nothing-to-package
    words.

- **Revision 523 — an investigation is called one wherever it is read (§8.4, R5):**
  - Its refusals and file problems, and icat's help and output, said workspace where the window and the rest of
    icat said investigation; they now say investigation, and workspace names only the command, the file's
    extension and the contract.
  - The main window says what it shows is unchanged, not the current workspace; a refusal of icat raw that sent a
    person to the current workspace says to restart from icat evidence.
  - Debug and Release each ran 2,207 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 18
    mutations: the investigation called a workspace in its members, its time, its time reference, a leading part
    naming two sessions, a file made over another, a file kept inside a session, a file not found, a file that does
    not read as one and its time across members; icat's heading, what new and an empty show say, its help, its
    reference instant, an instant with no place and its caveat; and the main window's dropped and unchosen paths
    saying the current workspace.

- **Revision 522 — what an investigation says, as its window says it (§8.4, R5):**
  - The workspace named sessions, captures, hosts and notes by all 32 hex digits, which neither the window's rows
    nor icat show; every message now names each by its first eight, and an alignment others rest on names whose
    alignment each sentence means.
  - A failed action in the investigation's window or a dialog says first what it did not do, then why; a file
    gone from under the window is said to be gone, never with the command line's advice to make one.
  - Sessions that could not be added no longer read the investigation again, whose failure hid that they were not.
  - Debug and Release each ran 2,206 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 18
    mutations: identities named by all 32 digits, everywhere or in a relink's refusal; the one-session and
    several-session sentences swapped, and the dependent's alignment unnamed; a gone file said in the command line's
    words; a removed note, a withdrawn alignment or a relink said without what was not done; a join's decisions
    swapped; nothing added read again, or not waiting for a reading under way; the aligned session unnamed; a host
    decision unprefixed; a note's adding and rewording swapped, and a translation's stating and withdrawing; a
    view's removal unnamed; and a comparison, and the card's unkept sentence, in the command line's words.

- **Revision 521 — the search's hits by keyboard (§6.7, R15):**
  - No test walked the hits by keyboard, and Up on the first did nothing: a person who went into them had to
    clear the search to go on typing it. Up there now gives the search box the keyboard back, its words kept.
  - The keys sheet lists the hits' keys - Up or Down, Enter, Esc - under a place of their own.
  - Debug and Release each ran 2,202 tests, with revision 473's 93 failures and 4 skips; Release once failed a live
    pin test besides. Its recording fixture publishes on a timer, which can put the first record in a chunk of its
    own, wholly before the pin, and the step then rightly releases it: the split the command line's two pin tests
    were fixed for earlier. The test now publishes the first record alone on purpose and expects its release, never
    one past the pin, and passed 16 runs in a row, 8 in each build. The tests caught each of 5 mutations: Up on the
    first hit keeping the keyboard there, Up on the second leaving the hits, the search's words lost on the way
    back, the caret left where it was, and the hits' keys missing from the sheet.

- **Revision 520 — what an investigation could not keep, said once (§8.4, §11.3, §26.3):**
  - A change a session's investigation could not keep added a sentence beside the session's status each time,
    so dragging the split grew the card a sentence at a time, and the sentence stayed once a change was kept.
  - Now it is said once, however often changes fail, and taken back once one is kept. The coverage pass that
    found it also found a package stopped by its own button, or one that could not be made, run by no test; both
    are now, and save nothing, leave no unsaved folder, and say which.
  - It also found revision 519 had made a files check refused by a package's hold say no earlier generation was
    complete. A package asked for now waits for the check, saying so; a check that finds changed files while
    something holds the session leaves it as it is.
  - Debug and Release each ran 2,201 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 21
    mutations: what the investigation could not keep said again at each change, never taken back once its layout or
    its panes are kept, left unsaid for either, or the one said in place of the other; Cancel packaging doing
    nothing for a redacted package, an exact copy or an investigation's package; a cancel or a failure left unsaid,
    for a package, a copy or an investigation's package; the card's headline not put back after a cancel; the button
    left offering the cancel; a redacted package or an exact copy asked for before the files check, or its wait
    unsaid; a second ask waiting beside the first; and a check reopening a session a package holds.

- **Revision 519 — what holds the session shown says so (§3.1, §8.4):**
  - While a package was being saved, Open saved session and Ctrl+O did nothing and said nothing; Start
    exploring started a capture whose card the package then wrote over; an investigation's Open said only that
    its session could not be opened.
  - One rule now names what holds the session shown - a capture, an open, a package, a finish - and every way
    to replace it waits and says why, on the card and in an investigation's status line. Start exploring waits
    for a package, as Open saved session did.
  - Debug and Release each ran 2,195 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 17
    mutations: Start exploring, Open saved session or an open going ahead while the session shown is held, or
    waiting unsaid; Ctrl+R silent while a capture runs; a capture, an open, a package or a finish not holding the
    session shown; a capture being saved said as one under way; a start refused in the words of an open, in either
    of two ways; and an investigation's Open or its column giving no reason, or saying the session opened.

- **Revision 518 — closing over work under way (§3.1, §8.4, §11.3):**
  - Closed while a package was being saved or an unfinished capture finished, the window went at once: the
    package was cancelled as InterCat ended, which could leave its unsaved folder in the folder chosen, and the
    finish stopped unsaid. Only a recording was asked about, and no test had closed the window during one.
  - One question now names all that closing would stop - a recording, a package and where it goes, a finish, an
    investigation's package - and keeps them going on Esc. Stopped, the window says so and closes once each has
    ended, no unsaved folder left; a package begun meanwhile is stopped too, and an investigation's window asks
    the same about its own package.
  - Debug and Release each ran 2,190 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 27
    mutations: a recording, a package, a finish or an investigation's package not asked about; the answer ignored;
    the stopping, or a recording's saving, unsaid; a recording, a package or a finish not stopped, or not waited
    for; an investigation's package not stopped, or one begun meanwhile left running; an exact copy's, a redacted
    package's, a finish's or an investigation's package's work not kept for the close; an investigation's window
    closing without asking, without waiting, without stopping its package or without saying so; and several things
    named as one, only the first said, Escape stopping them, or the keyboard placed on the answer that stops them.
    One, an investigation's package not kept, was first caught only by the run's time limit, and promptly when run
    again; every wait these tests make for a window's work is now bounded, so work that never ends fails a test
    rather than hangs it.

- **Revision 517 — the session's folder from the card, and Enter on a button (§3.1, §6.7, R15):**
  - The card's line naming the session shown was text a person could neither copy nor open. Open folder beside
    it opens the session's folder in the file manager, and the line's menu copies the path whole; a folder gone,
    or one the system would not open, says so.
  - With a row chosen in the ranked table, Enter on any button descended into the row instead of pressing it. A
    button the keyboard is on now takes its own Enter, and a list of choices opens on it; the ladder tests'
    helper now really gives the ranked table the keyboard.
  - Debug and Release each ran 2,182 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 11
    mutations: Enter on a button, or on a list of choices, descending into the chosen row; a folder gone since the
    session was opened opened anyway; the system's refusal, a failure, or the line saying the folder was not opened
    left unsaid; the path not copied, or its copy unsaid; and the button, or either item of the line's menu, doing
    nothing. Opening the list of choices on Enter by the window's own hand survived: a list opens on its own Enter
    once the window lets the key through, so only that is written.

- **Revision 516 — the window opens where it closed (§26.3):**
  - InterCat's window opened at its first size wherever the system put it, each launch, however a person had
    placed it.
  - Where it closes is kept in the settings file (app-settings-v1's `window` key). It opens there, as large and
    maximized if it was, inside the screen that still shows its title bar and no larger than it, or as it first
    did when none does. It is written as the window closes, never over a file that cannot be read or another
    version wrote.
  - Debug and Release each ran 2,179 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 22
    mutations: the place unread, a place not read left unsaid, the key reported as unknown, or one with another
    member, no width or a negative height taken; the place written over a file that cannot be read or another
    version wrote, or dropping the file's other keys; a window opened where no screen shows its title bar, left off
    its screen, larger than it or smaller than it can be, or measured without the screen's scale; the place not
    applied, not maximized, kept while maximized, not followed as the window moves or is resized, or not kept as it
    closes or that it was maximized.

- **Revision 515 — sessions dropped on an investigation's window (§8.4):**
  - The window took nothing dragged onto it. Sessions' folders dropped on it now join the investigation as the
    folders Add sessions chooses do, several at once; an investigation file opens in a window of its own; anything
    else is not added, which the window says.
  - An addition, relink, alignment or note that finished while the investigation was being read was never read
    again: its words were lost and the rows could miss it. A reading asked for meanwhile now waits, then reads.
  - Debug and Release each ran 2,163 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 9
    mutations: a refresh asked for during a reading skipped again; the window taking no drop, or taking text; a
    folder dropped not added, or a file added as one; an investigation file not opened; what was not added unsaid,
    dropped from the status, or unsaid when only files were dropped.

- **Revision 514 — a session or an investigation dropped on the window (§3.1):**
  - A session folder named on the command line or dropped on the program opened, but the open window took
    nothing dragged onto it.
  - It now takes files and folders, never text, while a saved session could be opened: a folder opens as Open
    saved session opens the one chosen, an investigation file in a window of its own. Anything else, or several
    things at once, opens nothing and the card says why. Open saved session's tooltip says a folder can be dropped.
  - Debug and Release each ran 2,161 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 12
    mutations: the window taking no drop; a drop taken during a capture, or text taken; a drop opened during a
    capture; several things opening the first, or said as one; a folder or an investigation not opened, or any file
    opened as an investigation; the card's refusal or headline unsaid; the drop and Ctrl+O not waiting while Open
    saved session is hidden.

- **Revision 513 — the keys that act anywhere, from the search box (§6.7, R15):**
  - The keys sheet says Ctrl+R, Ctrl+E, Ctrl+O, F5, and Alt+Left or Alt+Right act anywhere, but the window gave
    every key pressed in the search box to the box: after a search, Ctrl+E exported nothing and said nothing, and
    Alt+Left did not go back a level.
  - None of them types anything, so each now acts from the search box too, the three commands only while their
    buttons are offered; the search keeps its words and the keyboard, and a letter typed there stays text.
    §6.7's table now lists Ctrl+R, Ctrl+O and F11, and says which keys act from the search box.
  - Debug and Release each ran 2,159 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 12
    mutations: the keys that act anywhere acting nowhere, or not from the search box; Ctrl+R, Ctrl+E or Ctrl+O
    dropped, or acting without Ctrl; Ctrl+O acting while a capture hides Open saved session; F5, Alt+Left or
    Alt+Right dropped; Left or Right acting without Alt.

- **Revision 512 — a dialog the system could not show (§6.1):**
  - No flow behind a file or folder choice caught a picker that failed, so a shell that refused one, or no
    service to show it, ended InterCat with every window it had open.
  - The seam every choice goes through asks inside a guard: a picker that fails chooses nothing, as one declined
    does, and the main, investigation and content windows each say why where they say what an action did. A
    picker whose starting folder cannot be found still shows.
  - Debug and Release each ran 2,157 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 10
    mutations: a save, open or folder picker asked outside the guard; a failure chosen as nothing but never said;
    the main window's headline or detail, the investigation's or the content window's words unsaid; where a picker
    starts asked outside its guard, or asked of the platform past the seam.

- **Revision 511 — `Ctrl+O` opens a saved session (R15):**
  - Open saved session had no key, though `Ctrl+O` is where a person reaches to open anything; Start exploring
    has `Ctrl+R` and Export this view `Ctrl+E`.
  - `Ctrl+O` now does what the button does, only while the button is offered. The keys sheet lists it, and the
    button says it on hover and to a screen reader: in its words it wrapped at the rail's narrowest, and the
    smallest window's ranked list lost the start of its third row.
  - Debug and Release each ran 2,157 tests, with revision 473's 93 failures and 4 skips. The first run caught the
    key in the button's words wrapping at the rail's narrowest; the tests then caught each of 5 mutations: Ctrl+O
    unhandled, or acting while a capture runs; the keys sheet without it; and the button's hover or its screen
    reader's word without it.

- **Revision 510 — the main window named by what it shows (R15):**
  - The window was "InterCat · IPC Explorer" whatever it showed, so switching between windows listed InterCat
    windows a person could not tell apart, and a screen reader said no more as one came to the front.
  - It is now named by the session's folder, and while a capture runs by what it does - starting, recording or
    saving, with the folder once it has one - as the investigation's window is named by its file.
  - Debug and Release each ran 2,156 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 7
    mutations: the window left unnamed by an update, or still named by a session forgotten; a capture starting,
    recording with or without its folder, or saving named only as a session; and a folder given with a separator at
    its end named by nothing.

- **Revision 509 — the session folder read again as the window comes back into use (§3.1):**
  - The saved sessions and an unfinished capture's card were read when the window took its folder and after a
    capture: a session saved or deleted elsewhere, or a capture finished or forgotten in another window, stayed as
    first read until a row or the card was pressed.
  - Each time the window comes back into use it reads the folder again: the saved sessions while it shows them,
    and the card. An unchanged list is left as it is; a changed one keeps the keyboard on its row while that is
    still listed, else on the newest, and gives it where the window does once none is.
  - Debug and Release each ran 2,155 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 9
    mutations: the folder not read again on coming back, or read on the window's opening; the list read while a
    session is shown, or the card not read; an unchanged list replaced; and the keyboard not kept, kept on the
    newest rather than its own row, or not placed on its row at all. Holding the window's keyboard rule while the
    list is read again, and placing the keyboard once none is listed, survived: the rebuilt list keeps the
    keyboard's row container, and a row gone gives the keyboard where the window gives what a control leaves, so
    neither is written.

- **Revision 508 — an unfinished capture changed since its card offered it (§3.1 step 6):**
  - A capture whose evidence went since was offered Finish again and again, each refused as a "session
    directory" that does not exist; one forgotten in another window was refused as being finished there; and Open
    what was saved, its session gone, was refused while the card kept offering it.
  - A finish that does not go reads the card again: the reason stands on the card while it offers the capture as
    it did, a card that offers it otherwise says what changed, and the status says why once it no longer offers
    it, never on another capture's card. Evidence not there is named as the capture's, a ticket gone as finished
    or forgotten elsewhere, and neither refusal writes anything (live-follow-v1 §4).
  - Open what was saved asks for the session the card described: one deleted, emptied or written over since is
    said gone in the card's words, and the card goes once nothing of the capture is left.
  - Debug and Release each ran 2,153 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 15
    mutations: what was saved opened as whatever session its folder holds, its session gone left unsaid, a session
    written over said only gone, every refusal said as gone, the card not read again after an open, or a capture
    opened still offered; a finish's card not read again; its reason unsaid once the card goes, pinned on another
    capture's card, unsaid on an unchanged card, or said on a changed one; a ticket gone said as another window
    finishing, evidence not there named as a session, the session a card described left unread, and a session
    written over worded as gone.

- **Revision 507 — a saved session gone from its folder since the rail listed it (§3.1):**
  - A row stayed as the rail read it while its folder changed: one written over by another session opened that
    session under the row's name, one deleted was refused by its folder's path, one emptied as a folder no session
    was ever published in, and each stayed listed.
  - A row now opens only the session it was read from. A folder that no longer holds it says so in the row's
    words, adding that another session is there when one is, and the list is read again, the keyboard on its
    newest row; files that are there but cannot be read say why, and their row stays.
  - Debug and Release each ran 2,147 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 11
    mutations: a row opened as whatever session its folder holds, or read without its session; a session gone or
    written over left unsaid, or unsaid while the list is read again; every refusal said as gone; another session
    there unnamed, or named where none is; the list not read again; the keyboard left where its row went; and the
    open's refusal not returned. A twelfth, the opener's result reported false, survived because nothing read it,
    and the opener now returns none.

- **Revision 506 — the capture card at the rail's narrowest, in every phase (§6.8):**
  - Read at both rail widths before a capture, once one could not start, starting, recording, stopping and saved,
    with processes named by long image paths, chosen and down every rung: at the narrowest rail "Start exploring
    (Ctrl+R)" lost its shortcut before a capture and once one was saved, and "Retry exploring (Ctrl+R)" once one
    could not start.
  - Every button's words now wrap where their place is narrower than they are; the long names already wrapped or
    ended in an ellipsis their tooltips complete.
  - Debug and Release each ran 2,146 tests, with revision 473's 93 failures and 4 skips. The tests caught a button's
    words kept from wrapping. Wrapping only the primary action's survived: in no state a test reaches does another
    button's label outgrow its place, and the rule holds for every button so that none is cut off when one does.

- **Revision 505 — an investigation's merged time zoomed while its alignments change (§8.2):**
  - The window kept its zoom whatever changed: with every alignment withdrawn no session was placed and the zoom
    buttons still acted; with another session's clock made the investigation's time, the range was read on that
    clock, an unrelated stretch that held nothing; and the whole a zoom is kept within was read only while
    unzoomed, so Zoom out stopped at the investigation as it had been.
  - Each read of the merged time states the whole investigation's time and whose clock it is. A zoom is let go
    once that clock is another's or none, or no placed session meets it, and the window says why after what the
    change did; one still meeting a session stays, and zooms within the investigation as it is now.
  - Debug and Release each ran 2,145 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 9
    mutations: a zoom kept across a change of clock, or where no placed session meets it; the whole kept only while
    unzoomed, or read as the interval shown; a change of clock said as no session meeting the zoom, a lost clock
    said at all, the reason unsaid, or said in place of what the change did; and the time reference left unread.

- **Revision 504 — a rolling capture's publications under a person's navigation (§6.7, ADR-045, R21):**
  - The timeline kept its zoom across every view it was given: a release that gave up the time in view left it
    drawing time the session no longer held while the ranking counted the whole session, and a session shown
    afresh opened at the previous one's zoom, often a range wholly outside its own time.
  - A zoom carried to a newer generation stays inside the time the session keeps - moved, at its span, to the
    earliest time kept, or the whole session where wider - and is said; another session, a capture's first view
    and a session opened again open whole.
  - What a release took was said twice and in the store's words. Each change is now said once: the rung gone by its
    breadcrumb and where the view went, the chosen lane's mechanism, the selected process, and the analysis
    interval cut or cleared.
  - The live preview's card said exact records come about every 2 s; it says the capture's own interval - 5 s for
    one keeping its last hour - and none it was not told.
  - Debug and Release each ran 2,143 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 21
    mutations: a zoom left in time a release gave up, or never whole again; a session shown afresh, another or the
    same, kept at the zoom before; the view's move unsaid, said of a whole view or with no release, or said as the
    wrong move; the selected process said twice, or only beside a rung gone; the rung gone unsaid, in the window and
    the view model, or said gone back to the wrong rung; a lane's mechanism, or the analysis interval cut or
    cleared, unsaid; the rung's own time said beside the interval again; and the live preview's card stating 2 s, or
    no rate where the capture said 5 s, its interval lost in any of three places. Three survived at first, where the
    test's second session was too short to hold the first's zoom, so keeping a zoom inside a session fitted it
    anyway; a longer one caught them, and showed that the capture's waiting view needed no reset of its own, which
    is gone.

- **Revision 503 — a next page that cannot be read (§3.2, R21):**
  - E's records, an RPC channel's calls and an HTTP channel's exchanges, a first page listed and the next
    unreadable: the rail and the inspector said all of them were unavailable while the first page stayed listed,
    why was said nowhere, and Load more was gone.
  - A next page that cannot be read now leaves the rows listed; the rail says it could not be read and the
    inspector why, and M asks for it again. A scope's first page that cannot be read no longer leaves the previous
    scope's rows on screen.
  - Ctrl+E with nothing to export says why, as the button's tip does, telling records that could not be read from
    records still being read and from a scope holding none.
  - Debug and Release each ran 2,138 tests, with revision 473's 93 failures and 4 skips. The tests caught 18 of 19
    mutations: for E's records, a channel's calls and its exchanges, a next page that failed said as all unavailable,
    not asked for again, left unsaid, still said once read, or said with the first page's sentence before its reason;
    the inspector without why; Ctrl+E without why; and the tip saying records not read, or none in scope, are still to
    come. The previous scope's calls left on screen when a new scope's first page failed survived: no test can have a
    scope counted while its calls cannot be read.

- **Revision 502 — an investigation's member changed under its window (§8.4, ADR-038, R22):**
  - Another session written into a member's folder, or the folder emptied, after the investigation read it: Open and
    a column of the merged time opened the folder as it stood - InterCat's window showed the other session - and the
    investigation said it had opened its member.
  - A member is now opened by its identity: InterCat's window refuses another session or none, in revision 501's
    words, and keeps what it showed; the investigation reads its sessions again and says why, as the row says it.
  - Another session at a member's path, and a folder holding no session, now end with relinking the member where it
    is now; the latter no longer says that no session was published there. A screen reader says the step once.
  - Debug and Release each ran 2,134 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 11
    mutations: an emptied member's folder said as never published, another session's or a moved member's reason without
    its step, the step said twice or never; a member opened, or its column, as whatever its folder holds, or kept as the
    session shown by its path alone, or its store opened by path; and a member that did not open said without reading
    its folder again, or without why.

- **Revision 501 — a session's folder written again or emptied under the window (§20.1, R21):**
  - Every read said the session identity had changed after its store was opened; an emptied folder, that no
    session was ever published there. A lease now says the folder holds another session, and to open it again,
    or that its session's files were moved or deleted.
  - The registry handed a reader of one session the store of whatever the folder held: Keep records offered the
    other session's pins and placed its pin there, Share original session would have packaged it, and counting
    candidates did nothing without a word. It now refuses, in the same words, and every read of the shown session
    asks for it by its identity; the window's own checks, in three wordings no read reached, are gone.
  - A choice of evidence policy that cannot be counted says why on the capture card; a zoom whose own count cannot
    be read says the overview's columns stand in, and why.
  - Debug and Release each ran 2,132 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 20
    mutations: a lease saying the old words, or that no session was published where one was read - by its manifest, its
    guard or its folder gone - or a store that never read one saying it is gone; the registry handing out a store of
    another session, kept or opened afresh, saying another session for none, or a removed folder raw; the evidence
    source asking for no session; a zoom's failure unsaid, kept after a count, or dropped from either caption; the
    highlight repeating the zoom's reason; the package's preview or copy, or the channel browser, reading any session; a
    policy choice's failure unsaid; and an original record's refusal in other words.

- **Revision 500 — a read that fails says so, never that there is nothing (R21, §6.7):**
  - With a shown session's files damaged, a process's rung said it had no channel where its RPC calls, HTTP
    exchanges or connections could not be read. It now says which could not be read and why, once for each reason,
    in the reason it is empty and both summaries, and promises nothing one step away.
  - A selection whose highlight could not be counted left the caption saying it was still counting; it now says it
    could not be, with why unless the caption already says it.
  - Ctrl+click and Ctrl+Space on a channel's, a call's or a record's row, which join no selection, are left to the
    list, which chooses the row; they were taken and did nothing.
  - Debug and Release each ran 2,126 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 12
    mutations: a failed read said as nothing to list; the RPC calls', HTTP exchanges' or connections' failure left
    unsaid; each failure said apart where one reason failed them all; the failure unsaid in the rail or the inspector;
    the highlight still counting, its reason repeated or dropped; and Ctrl+click or Ctrl+Space taken on a row that joins
    no selection.

- Revisions 230–499 are in [the third historical log](history/IMPLEMENTATION-STATUS-revisions-230-to-499.md), whole and in order.
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
     1. Done in revision 457 for a finished session: its tile index keeps each record's owner and channel and each
        tile's tallies of them, so a brush opens no segment, and at 10M rows the brushed ranking took 6.3 ms first and
        9.5 ms at p95, from 631 and 273. A live generation's brush still binds the rows of the segments it meets.
     2. Done in revision 210 as far as the lanes go: past the 20,000-cell bound a group's lanes are counted in the
        columns it allows (§12's 2,000 × 40 query answers with lanes at 500 columns). Lanes at every view column for
        large groups remain bounded by the cell budget; since revision 462 lanes counted coarser are drawn as
        density where their columns are under 3 device pixels, and as bars where wider.
     3. Done in revision 458 for a finished session: its zooms, brushes and groups open no segment, and the working set
        at the scale gate's end is 263 MB at 10M rows, from 1.35 GB; a live session, and a focus that reads its rows,
        still cache segment readers within the reader cache's budget.
   - A live session still counts its overview from tiles until its writer finishes.
   - A zoom of a live generation still builds a segment's tiles from its rows when first drawn; since revision 456 a
     finished generation's are persisted (`tile-index-v1`), with the records a column boundary can fall among, so its
     zooms open no segment, and since revision 457 neither do its brushes, which count from the tiles' tallies. Since revision 400 a zoom opens and tiles only the segments its interval meets, by the readings their headers
     declare: a hundredth of a reopened 1M-row session in twenty segments drew in 3-17 ms, from 86-237 ms. Since
     revision 401 a brush's counts, its lanes' bytes and a time scope's byte and peer rankings do too, with the
     checkpoint's instances and channels; since revision 402 an evidence page opens only the segments it reads and
     stops past its time scope, and since revision 403 a process's connections read only the segments their lifetimes
     meet. Since revision 408 the call ranking and a process's RPC and HTTP listings read the calls and exchanges once
     paired or grouped, opening no segment, and a call's or an exchange's evidence opens only the segments holding its
     records. Since revision 412 the generation keeps an operation's records by segment, and a page or a zoom of them
     reads their rows alone: a busy channel's zoom takes 4 to 9 ms after the first, where it took 123 to 167. Since
     revision 440 a finished session keeps its calls and their other ends in an operation index, so a reopen's first
     brush, ranking or listing pairs none, and since revision 441 its HTTP exchanges, so its first HTTP listing groups
     none; since revision 544 a live generation's extends the previous generation's, reading only its added segments,
     where it derived them over every segment, since a call's request and response can lie in different ones (P25).
     A search reads the overview, never a segment.
   - An operation's focused count, and a channel's and owners' at once, still read their rows (§10.3: the tallies hold
     neither apart); since revisions 458, 459 and 460 a group's, a process's and a channel's count from the tile index.
     A focus on the whole session's records of one source direction reads rows too; since revision 461 one mechanism's
     comes from the whole timeline's own counts.
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
   announcement. Since revision 477 the graph says the node its arrows move to, and the timeline the moment `[` and `]`
   step to, each renaming itself while it has the keyboard and raising the name change, and since revision 478 the
   investigation window's timeline the column its keys move to; check that Narrator and NVDA say a focused pane's
   new name, and that the investigation window opens with the keyboard on its session as the window activates. Since revision 426 the search's status is a polite live region, and the timeline's status names its
   analysis interval. Since revision 472 `F6` moves the keyboard between the panes, as screen-reader users expect, and
   a test walks the investigation by keyboard alone (M5's accessible keyboard workflow); since revision 474 Enter and
   E in the tables no longer leave the keyboard on nothing, and an empty rung gives it to the reason it is empty;
   since revision 475 `F1` lists every key the window takes, a row a screen reader reads at a time, and since
   revision 479 the investigation window's own; since revision 476 a chosen record's facts are a list the keyboard
   reaches, a fact at a time. Since revision 480 every dialog opens with the keyboard on one of its own controls,
   and since revision 481 the windows a record opens give it to what they read; check that Narrator and NVDA say
   that control as the dialog opens, and a record's text a line at a time. Since revision 482 a capture gives Stop
   the keyboard and a session shown its ranked table; check that a screen reader says Stop as recording begins and
   the first ranked row as a session opens, and that the main window, activated again, keeps the keyboard where it
   was.
   Collapsing a group's lanes is done: pinning in revisions 387 and 388, drawing only the rows in
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
     2. Done in revision 544, once a live RPC peers capture needed it: calls and their other ends extend between live
        generations, pairing again only the keys a late record can touch, and since revision 440 a finished session
        keeps them in its operation index. A generation of 80,000 calls projects in 0.18 s, from 0.58; binding,
        grouping and linking every call again remain linear in a generation.
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
        first view opens no segment either; done in revision 230, the call at a call's other end opened from its row;
        done in revision 543, recorded through the broker from the window's Keep and icat capture, and drawn as it
        records.
     4. Name more RPC interfaces. Only interfaces a protocol specification names get a name; a real session's busiest
        (COM's `00000134-…`, `e60c73e6-…`, `00000136-…`, `00000132-…`, the DHCP client's) are named by none, so a name
        for them needs another source of truth than a guess.
     5. Done in revision 185 for the call lane: a view denser than its budget is drawn as density. Since revision
        462 every other lane of a zoomed view is too, a column per device pixel; the call lane keeps its own
        columns of about 5 px, each its own pointer target.
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
     releases kept content on its own; revision 444 mirrors it in a follow (ADR-047), so `icat record --evidence-only`
     keeps it; revision 451 has the broker prepare a content capture of its client's own processes, each named by its
     ID and start (ADR-049), and revision 452 start one, holding those processes while it records; revision 453 has
     `icat capture --profile content` ask for one, and revision 454 the window, reviewed before it starts; revision 455
     decodes the fixture's messages when a person asks (§11.2's `DecodedFields`). Next: HTTP/2 and compressed responses
     through it.

   - **One-sided connections (§7.1).** Revision 248 lists a process's connections no record's other end holds on its
     rung, and revision 249 counts its records apart in the timeline. Later: naming the other host where a source
     names it (a DNS name is content or another source's, never guessed).
   - **IPv6 beyond loopback.** Revisions 172–174 store, relate, redact, show and capture IPv6 endpoints, measured on
     `::1` (FX-TCP-002, FX-UDP-002; ADR-029). Still unmeasured: two-host IPv6 traffic, link-local addresses on several
     interfaces (a record carries no zone index, so two interfaces' equal addresses are one address to a relation), and
     IPv6 multicast.
   - **IC-016a**, rolling retention, in order: done in revision 434, an interval release that gives up a session's
     oldest journal units with their rows and keeps, as evidence, the lifecycle and first records of each process and
     connection a retained record names (ADR-043, store-v1 §8); done in revision 435, the operations open across its
     boundary, kept whole - calls, their other ends' ALPC evidence and HTTP exchanges (I20); done in revision 436,
     readers that state the interval before the boundary as released, never quiet: coverage over any scope reaching
     before it is a partial gap saying why - in the timeline, the status line, the inspector, exports, redacted packages
     and every `icat` coverage statement - and the window's size line says from when the session keeps every record;
     done in revision 437, `icat retain --release-before <moment>`, disclosed first and performed with a reason once no
     recorder can be writing the session; done in revision 438, a follow that goes on across releases of the session it
     derives into (ADR-044), since in a broker capture the watched session is the follower's; done in revision 439,
     `--keep-last` on `icat follow` and `icat capture`, the follower's rolling policy (ADR-045); done in revision 442,
     pins that keep a session's records from a moment through every release, within the size each allows, which a
     rolling follow stops at (ADR-046, `icat pin`); done in revision 443, the window's pins - from its time scope's start,
     listed and removable, stated in its size line; done in revision 445, the window's rolling capture, its timeline
     starting at the window (ADR-045 decision 6); done in revision 446, a follow that goes on across the evidence's
     chunk releases, each stating what the recording gave up in all (ADR-048); done in revision 448, the broker releasing
     the chunks its follow gave up, between its own publications, told by the capture's owner as it renews its lease
     (ADR-048 decision 5); done in revision 449, a rolling capture's live publication interval set by the window it
     keeps, so its exact view is as fresh as a ten-minute capture's, and a stop once its follow stops giving chunks up;
     done in revision 450, the size line's projection for a rolling capture from what its window holds, when it begins
     releasing, and when a pin keeping it past the window stops it. What remains is measuring a rolling capture on
     Windows for hours, its evidence and session holding their window.
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
     §6.1's column widths: the rail's edge resizes it, since revision 469 the inspector's edge resizes the inspector,
     and since revision 470 both widths are kept with the split, once for every session (`workspace-v20`).
   - §6.7's table is complete since revision 160's multi-selection. What it leaves open:
     - a set of channels, which a timeline focus cannot name;
     - a lane view of an arbitrary set, which would need the graph to expand several groups at once;
     - (done in revision 205: the set's rows are marked in the ranked table itself, and since revision 473 a row drawn
       after the set was made, as one scrolled into view, is marked too.)
     Indexed/progressive search belongs to the later M4 scale gate.
   - §6.2's minimum drawn width (5 px) and pointer snapping: done in revision 462 with the density regime for every
     zoomed view, and in revision 463 for the whole session. Cells 3 device px wide or more are reached only at
     sub-tick zoom, where they stay bars, one per tick, each its column's target (plan revision 463 says how the
     band and event regimes' intent is met elsewhere). Direction arrows between a channel's ends remain unbuilt,
     since nothing pairs a channel's records one by one.
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
     names the aggregate. Since revision 471 a chosen cell highlights in the graph what its records belong to: its
     relationships, or the nodes of its processes.
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
     instants of the ladder between its ends, on the wall clock the times of day it read. Since revision 495 the size
     line's instants - what a release keeps, the pins, a pin's stop - are read on it too. The host's own zone waits on
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
   - §6.8's motion: since revision 493 the graph's nodes travel to new places - a level change's, a layout settling,
     a re-layout's, a pin's - in the 200 ms level-change token, and a node newly selected is ringed from wider, the
     ring settling in the 120 ms selection token (theme 1.5.0). Under the platform's reduced motion, on Windows
     Animation effects, every change is drawn where it ends at once. Since revision 494 a view coming back to the
     newest generation - following resumed, the newest asked for, records no longer read, a paused capture saved -
     is carried there from the time it showed. A publication followed live, and the timeline's rows on a level
     change, are drawn at once, as §6.8 now says.
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
     clock; alignment from shared markers waits on a marker both captures record - an application's (M7) or an
     exchange between the hosts (M11) - since a connection joined across captures orders their clocks only once a
     source's send and receive records are measured as taken before and after the transfer (plan §8.2);
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
     the merged time on the command line, `icat workspace timeline` (384); a session opened from it shown in front,
     its window a peer of InterCat's, one per investigation file (496);
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
     an exchange (TCP is not process-scoped at capture). Since revision 490 a test runs these commands through
     `icat` on the demo's two generated hosts ("two hosts' sessions are added, aligned by their wall clocks,
     correlated, decided and compared on the command line"), so what the check adds is the two machines.

## Verification and cautions

- Revisions 230–499: [the third historical log](history/IMPLEMENTATION-STATUS-revisions-230-to-499.md), whose
  verification entries are those of revisions 230–441; later revisions state theirs in their slices above.
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

`contracts/journal-v1.md`, `store-v1.md`, `segment-v1.md`, `metrics-v1.md`, `entities-v1.md`, `operations-v1.md`, `derivation-checkpoint-v1.md`, `overview-index-v1.md`, `operation-index-v1.md`, `tile-index-v1.md`,
`query-identity-v1.md`, `live-follow-v1.md`, `app-settings-v1.md`, `workspace-v20.md`, `support-bundle-v1.md`, `demo-investigation-v1.md`, `clock-calibration-v1.md`, `coverage-v2.md`; ADR-008, ADR-010, ADR-012, ADR-013, ADR-023–031, ADR-038–041; the complete historical ledger linked above.
