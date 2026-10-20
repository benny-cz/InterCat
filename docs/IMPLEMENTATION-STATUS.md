# InterCat implementation status

Updated: 2026-10-20 · Plan revision: 638 · Branch: `main`

This is the **current resume point**, not a running transcript. Update the backlog and open-work tables in place after
each slice, then add only a short latest-change note. The complete pre-revision-104 chronology, measurements, and old
verification ledger are preserved in [the historical status](history/IMPLEMENTATION-STATUS-through-revision-103.md),
revisions 104–229 in [the second historical log](history/IMPLEMENTATION-STATUS-revisions-104-to-229.md),
revisions 230–499 in [the third](history/IMPLEMENTATION-STATUS-revisions-230-to-499.md), and revisions 500–579 in
[the fourth](history/IMPLEMENTATION-STATUS-revisions-500-to-579.md).
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
revision 546 the less busy of those that only reach other hosts in another, past the eight busiest - since revision 593 the eight busiest programs, several processes of one drawn as its node - and groups
collapse only under budget pressure. Since revision 550 the Other hosts node names the busiest addresses the drawn
processes' connections ran to, and since revision 555 Enter on it draws them on their own; since revision 564 a host chosen there, and since revision 565 one chosen in the list it gives the inspector, is the records of every connection to it, which the timeline highlights and E lists, as `icat evidence --host` reads them since revision 563. Since revision 570 a member of an investigation names each host's candidate other ends in another member's capture, never as an established join, and since revision 571 a session opened from its investigation names them in the window. Hubs draw as stars with components apart, executable groups read as file names,
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

- **Revision 638 — every rung comes back with its row, by Esc and by forward alike (§3.2, §6.7):**
  - Forward to a rung other than E chose no row, and Esc back from E taken from a row a process rung reads after it is
    shown - a connection to another host - came back to none. A rung now records the row it is left with, the row a
    way down goes through included, and forward and the way back both choose it again, once the rung lists it where it
    reads its rows later.
  - Debug and Release each ran 2,612 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    eight mutations were caught: forward choosing no row on a rung other than E, or none it reads later, or one not
    described as it was; a way down recording no row, or the row chosen rather than the one it goes through; Esc not
    waiting for a row read later; a rung left before reading its rows losing the row it was still to choose; and the
    ladder recording no row.

- **Revision 637 — Page Down and Page Up move the keyboard through a list; a path wraps between its folders (§6.7, §6.8, R15):**
  - A live pass at the smallest window found Page Down and Page Up only scrolling a list's rows, the keyboard left on
    a row out of sight, and the inspector wrapping svchost.exe's path with "C:" alone on a line. Every list now pages
    as a list on Windows does, choosing the row it comes to; a path wraps after a backslash, never after its drive's
    colon.
  - Debug and Release each ran 2,610 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    twelve mutations were caught: the lists' paging not taken, choosing nothing, leaving the keyboard or the row out
    of sight, going a page past the last or first row shown, paging a whole page, or taking a modifier's key; and a
    drive's colon not joined to its backslash, no break after a backslash - in the text and in the window - or the
    inspector not breaking its path at all.

- **Revision 636 — at the smallest window, crowded graph names keep a word's space (§6.3):**
  - A live pass over the busy machine at 1080 × 700 in the light and high-contrast themes found two graph names
    standing edge to edge at the machine rung, reading as one word - "svchost.exems-teams.exe". A name standing nearer
    another than names keep where there is room now still keeps a word's space across and a pixel down from it, or
    goes unnamed.
  - Debug and Release each ran 2,602 tests with revision 473's 93 failures and 4 skips, and nothing else failed;
    revision 612's test of a pane too narrow for two names side by side now expects the second unnamed. All six
    mutations were caught: crowded names keeping no room, none across or none down - the window's own check among
    them - and the slots a name may take, or must, nearer the names than they keep, only clear of them.

- **Revision 635 — forward along a way down and back again comes where the first way came (§3.2, §6.7):**
  - Esc gave the keyboard back to a record opened from a cell's list only once, and Alt+Right to E chose no record.
    The way back now holds until a way down of its own, as the other ways back do, and forward to E chooses the record
    chosen there when it was left; a record opened and left before E listed it is no longer chosen by a later E.
  - Debug and Release each ran 2,600 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    ten mutations were caught: the way back spent when taken, kept for another rung at its depth, or that rung not
    checked; forward to E choosing no record; an ascent or the breadcrumb leaving E without recording its record;
    the record E was still to choose not recorded, or outliving E; and the ladder recording no record, or recording
    only one already recorded.

- **Revision 634 — the way back from a moment the interval table lists gives the keyboard back to its row (§3.2, R15):**
  - A live pass stepping and resizing by keyboard on the busy machine found that Enter on an interval-table row, which
    lists its moment's records in E, came back by Esc with the keyboard on the ranked table. The way back - Esc, the
    breadcrumb, or forward and back - now gives it to the row again where the tables are shown and the row is still
    the analysis interval's.
  - Debug and Release each ran 2,600 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    twenty mutations were caught: Enter or E in the interval table listing the moment as E from elsewhere does; the
    window not answering the ask, or not giving the row the keyboard; the way down not kept; Esc or the breadcrumb
    not asking; the tables hidden on the way back asking; the way back spent when taken, kept for another rung at
    its depth, or that rung not checked; hidden tables keeping the ask; the rows listed again not offering it,
    offering it for another row, or asking again; a navigation or another interval keeping the ask; a way down of
    its own keeping the way back; and a newer generation not carrying or not restoring it.

- **Revision 633 — a cell a step or the interval table chose stays the cell when the timeline cuts its columns afresh (§6.8):**
  - A step or an interval-table row chooses a column explained as a cell of the lane it follows, and a resize or zoom
    turned it into a time range. Each rung now keeps the cell the analysis interval is, as its lane drew it last,
    however it was chosen, while that lane and that interval stand.
  - Debug and Release each ran 2,598 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    six mutations were caught: no cell kept before the timeline is counted afresh, or when a click chooses one; a
    kept cell standing for another lane or another interval; the recount a navigation asks for keeping one; and a
    way down letting the rung it left forget its own.

- **Revision 632 — live pass over timeline cells, their columns and the ways back on the busy machine (§6.8, §3.2, §6.4):**
  - A wider or narrower window cuts the timeline's columns afresh, and a clicked cell held as revision 629 meant. Back
    from E taken from a group's process-lane cell, the way back no longer chooses that process's row in the cell's
    place; a kept cell's column reads plainly; and E with nothing selected no longer shows an evidence line saying so.
  - Debug and Release each ran 2,597 tests. Release had revision 473's 93 failures and 4 skips and nothing else;
    Debug had one more, the journal allowance test, whose follow stalled once under the suite's load and did not
    again in 21 runs alone, with its assembly or under full CPU load, so its assertion now says why a follow stalls.
    All eight mutations were caught: the way back choosing the lane's process's row, E from a cell left unmarked or
    marked when a selection led, a kept cell's column worded as before or without saying the timeline no longer
    draws it, and the evidence line always shown, its change unsaid, or unbound.

- **Revision 631 — the way back from a record opened from a cell's list gives the keyboard back to it (§3.2, §6.4, R15):**
  - Enter on a record listed beside a cell opened E with the keyboard on it, and Esc came back with the keyboard on
    the ranked table. The way back, by Esc or the breadcrumb, now gives it to that record once the cell's records are
    listed again, so the next is a key away; a newer generation keeps the way back, and another interval, a navigation
    or a way down of its own lets it go.
  - Debug and Release each ran 2,596 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    thirteen mutations were caught: no way back kept from the list; none taken by Esc or the breadcrumb; one kept
    past a way down of its own, a navigation or another interval; one given up before the records are wanted or
    while they are read, or never offered once they are; a newer generation not given it or not restoring it; and
    the window neither focusing the record nor choosing it.

- **Revision 630 — the way back to a clicked timeline cell chooses it again (§3.2, §6.8):**
  - Esc or the breadcrumb back from E, a listed record or Enter brought back the cell's interval but not the cell: the
    inspector explained another lane's cell or none, and E lost the lane's filter. A rung left with a cell chosen now
    keeps the way back to it - Esc, the breadcrumb and forward choose it again - taken when used and kept by a newer
    generation. Revision 629's kept cell now stands in only while its lane is drawn, which also keeps E from failing
    on a channel end's cell before the rung's lanes are counted again.
  - Debug and Release each ran 2,594 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    seventeen mutations were caught: no way back kept on a way down, an ascent, the breadcrumb or forward; none
    taken by Esc, the breadcrumb, the crumb of the rung shown or forward; one taken at another rung at its depth, or
    left to be taken again; one kept past a way down of its own; the cell chosen again unsaid, or its timeline name
    unsaid; a newer generation not given the ways back, not restoring them, or keeping none for the cell it
    restored; and a kept cell standing in while its lane is not drawn.

- **Revision 629 — a clicked timeline cell stays the cell when the timeline cuts its columns afresh (§6.8, §6.4):**
  - A wider or narrower window, or another zoom, counts the view in other columns; a clicked cell none of them is lost
    its explanation, was titled a time range, listed no records, and E lost its lane's filter. The cell is now kept as
    drawn when chosen until another interval, lane or rung is chosen, and explained in the columns it was chosen in.
  - Debug and Release each ran 2,590 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    five mutations were caught: no kept cell to fall back on, none kept when chosen, one kept once another interval
    was chosen, one kept while the timeline draws its column, and the kept cell described in the columns drawn now.

- **Revision 628 — the way back from Enter on an aggregate's process chooses its edge to other hosts again (§3.2, R15):**
  - E on a process chosen in an aggregate's list kept the way back to the list and to the aggregate's edge chosen in
    the table; Enter on it kept only the list's, so Esc back to the machine left the table's row unchosen. Enter now
    keeps both, as E does.
  - Debug and Release each ran 2,589 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Both mutations were caught: Enter keeping no way back to the edge's row, and taking it at the process's rung it
    opened rather than where it began.

- **Revision 627 — live pass over exporting from the window; the Rank by box names its metric whole (§6.4, R15):**
  - Exports at the machine, group, process and evidence rungs of the busy machine held what each rung listed, and the
    card said how many rows or records and whether complete. The Rank by box, beside its label, had 69 pixels for a
    name: "Bytes sent and received" read "Bytes sent …", and Connections made and accepted, or RPC calls made and
    served, read alike. Its label and basis now stand above it and it takes the rail's width, so every name shows
    whole at the smallest window; Group by follows, the boxes still in line.
  - Debug and Release each ran 2,589 tests with revision 473's 93 failures and 4 skips, and nothing else failed; the
    window's 454 tests passed with the rail's new rows, the smallest window's ranked list still keeping two rows and
    the start of a third. Both meaningful mutations were caught: the Rank by box held to its old width, and the
    Group by box no longer spanning the rail; a third, a margin that left the basis above the box, changed nothing
    and went.

- **Revision 626 — a newer generation keeps a channel's rung whole (§6.4, R7):**
  - An RPC channel's calls and a process's HTTP exchanges were listed afresh by each publication, so the list went
    empty every couple of seconds until read, the chosen call coming back only then. The last publication's rows for
    the same channel and scope now stay listed until this one's first page replaces them, the chosen one chosen at
    once; another scope's rows are never handed on, and a failed read drops them with the reason.
  - Debug and Release each ran 2,588 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    18 mutations were caught: a publication carrying no channel list or taking none on; carrying no calls or no
    exchanges, or another scope's while that scope's are read, or none of an earlier publication's still unread;
    taking on no calls or no exchanges, appending its own first page to them, not saying the rows changed, not
    marking them as taken on, or listing them with no channel summary.

- **Revision 625 — an RPC channel's and HTTP exchanges' rung export their calls and exchanges (§6.4, R18):**
  - Both rungs list calls or exchanges a page at a time but exported nothing, saying "This rung lists no row", and
    `icat export --at` could not reach them. Their export now holds every call or exchange of the scope, read in one
    pass up to the evidence limit, each with its session time and how it went, culture-invariant; `icat export --at
    <channel key>` reaches the rung and writes the same file, `--limit` bounds the calls or exchanges too, and an
    export it cut short says so. A rung with none listed yet, none in scope or an unreadable read says which.
  - Debug and Release each ran 2,587 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    40 mutations were caught: the window exporting a channel's rung as ranked rows, offering or writing an export of
    calls it could not read, not saying what its limit left out, ignoring that limit or the brushed interval; the
    export offered with none listed or read, or for neither rung; its tip calling calls exchanges, saying none are
    in scope before they are read, or silent on a failed read; icat export not descending to the channel, writing
    its ranked rows, ignoring --limit or the interval, not saying what the limit left out, or refusing --limit
    without --evidence; the builder listing no HTTP rung, its reads always complete, its counts unagreed or all
    calls, its context never incomplete or silent; a call's duration where it has none, its procedure, missing
    status, failure, other end, records or start time; an exchange's unrecorded end or its parts; and the one-pass
    reads ignoring their limit or interval.

- **Revision 624 — a process's rung exports every row it lists (§6.4, R18):**
  - Its export held the paired channels alone: none of the connections to other hosts, RPC channels or HTTP exchanges
    listed beside them, and a process whose rows are all connections said it had nothing to export. The window and
    `icat export` now build the same rows through one builder (`ProcessRungRows`) and write them in the rung's order,
    byte for byte and culture-invariant; an export waits for rows still being read, and refuses, saying why, when they
    could not be read. Open: an RPC channel's or HTTP exchanges' rung still exports nothing (revision 625).
  - Debug and Release each ran 2,582 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    39 mutations were caught, one after a test added for it: the window's export holding the channels alone, or none
    of the connections, RPC channels or HTTP exchanges; the connections, or the RPC and HTTP rows, unranked by
    bytes; the words in the reader's culture, here and in icat export's; the ranking named by the channels alone; an
    export not waiting for each kind of row it reads, or giving up at once; one written or offered, or its tip
    silent, when a kind could not be read; icat export reading its rows over the whole session in an interval, none
    of its RPC or HTTP rows, no datagram flow, unranked, or naming its ranking by the channels; and the builder's
    known bytes, datagram wording, ranking, RPC side and other end, HTTP wording and order.

- **Revision 623 — a process's rung counts and ranks its connections to other hosts by bytes (§6.1, R21):**
  - A live pass over a recording's newer generations found the byte ranking's note at a process's rung summing its
    paired channels alone: "800 B on 2 sends" beside a 1.4 KB connection, and "No sends in scope" for a process whose
    only row, a connection to another host, sent 200 B. It now counts every row the rung ranks and is restated once
    the connections are read. "Bytes sent and received" now ranks connections too, rather than the rung falling back
    to records under byte figures; "1 send" replaces "1 sends", and no zero is stated where nothing measured one.
    Open: the rung's export holds its channels alone (revision 624).
  - Debug and Release each ran 2,575 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    17 mutations were caught: the note leaving the connections out; a connection unranked by bytes sent and
    received, ranked where nothing measured, by its sends alone, or with no transfer unmeasured; "1 sends", "with no
    size" and "none with a size" swapped either way, "0 B on 0 measured sends" or "no record" stated where nothing
    measured, "1 measured sends"; the note or its detail not restated once the connections are read; and the
    definition not naming them.

- **Revision 622 — a newer generation keeps a process's rung whole (§6.4, R7):**
  - A process's connections, RPC channels and HTTP exchanges were read afresh by each publication, so its rows to
    other hosts vanished every couple of seconds until read, and the one chosen among them was lost, the inspector
    going back to the process. The last publication's rows for the same process and scope are now listed until this
    one's are read, and a row chosen there - or a call on its channel's rung - is chosen again once listed, described
    as it was.
  - Debug and Release each ran 2,574 tests with revision 473's 93 failures and 4 skips, and nothing else failed. Of
    13 mutations 12 were caught: a publication carrying none of its rung's rows, or taking on none of its
    connections, RPC channels or HTTP exchanges; a restored row never chosen once listed, or chosen only at a
    process's rung and not as a call on its channel's; the row described where its process had been chosen again
    over it; and the row chosen after a choice made meanwhile - the selection cleared, another row or process
    chosen, a node chosen in the graph - or on a rung entered again. The one left, the adoption saying the rows
    changed itself, survived because the ranking restated after it says so, so it went.

- **Revision 621 — a live publication keeps Other hosts' list in its byte ranking (§6.3, §6.4):**
  - Under a byte ranking the list ranked its hosts by records until each publication had read their bytes, so its rows
    moved to records order and back every couple of seconds. The last publication's bytes now rank it, for the same
    byte ranking, said to be updating in the heading and aloud per row, until this one's are in; a failed read lets
    them go.
  - Debug and Release each ran 2,571 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    ten mutations were caught: no host traffic standing in, or one for another byte ranking; a stand-in unmarked in
    the heading, its rows or aloud; a publication carrying none of the traffic, or not that it was itself standing
    in, taking none on, or not saying its list changed when it does; and the last one's carried on after this
    publication's own read failed.

- **Revision 620 — a live publication keeps the interval table's bytes until it has read its own (§6.4, R15):**
  - Each publication read every listed interval's bytes again, its rows saying "reading bytes…" meanwhile. The
    timeline's columns keep their places as a live extent grows, so the bytes the last publication read for the same
    records over exactly an interval now stand in, marked updating on screen and aloud; a new interval or another
    lane's rows are read afresh, and a failed read lets none remain.
  - Debug and Release each ran 2,570 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    eleven mutations were caught: no interval's bytes standing in, one standing in with no read under way, or for
    another lane's records; a stand-in unmarked in its row, aloud or in the caption; a publication carrying none of
    the interval bytes, or not those it was itself standing in, taking none on, or not restating its rows when it
    does; and the last one's carried on after this publication's own read failed.

- **Revision 619 — a publication reads its bytes once (R11):**
  - Ranked by bytes with the table or a selection shown, each live publication read the whole session's bytes twice,
    for the ranking and for the table and card, and read the machine rung's edge traffic and interval bytes before
    re-entering the rung shown. A description now reads through the ranking's read, a ranking takes bytes a
    description read, and a restored view shows its tables once its rung and ranking are back.
  - Debug and Release each ran 2,569 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    five mutations were caught: a description reading its own bytes under a byte ranking; a restored view showing
    its tables before its rung and ranking are back; a byte ranking reading again what a description had read, or
    taking the last publication's bytes a description stated as its own; and a description left unread when a change
    of ranking cancelled the read it shared.

- **Revision 618 — a failed read lets the last publication's stand-in go (§6.4, R21):**
  - A ranking's measures from the publication before stayed once this one's read of them failed: the rows ranked by
    them, the note said "updating" for good on a capture's last publication, and an export waited and refused. The
    rows now rank by records and the note says why; an export names that ranking; nothing unread is carried on, and
    the table's edge traffic and a host's count follow the same rule.
  - Debug and Release each ran 2,568 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    five mutations were caught: the whole session's stand-in kept once its read failed, never said to have stood in,
    or the rows not ranked again without it; and the table's edge traffic or a host's count carried on after this
    publication's own read of it failed.

- **Revision 617 — a live publication keeps the table's and card's bytes until it has read its own (§6.4, R15, R21):**
  - Each publication read again what the relationship table's rows, the card, a channel's total and a mark's card
    state, and all said "reading bytes…" meanwhile, every two seconds. The last publication's bytes, edge traffic and
    host count now stand in, for the same scope, marked updating on screen and aloud, until its own arrive; a read
    that fails lets none remain.
  - Found on the way: a newer generation turned a channel chosen among a process's rows back into its process, at
    every live publication; the navigation carried keeps the row described.
  - Debug and Release each ran 2,567 tests with revision 473's 93 failures and 4 skips, and nothing else failed. Of
    38 mutations 36 were caught: no stand-in, one standing in once a read failed, or the whole session's for an
    interval; a stand-in unmarked in a row, aloud, in the caption, the card, a channel's total, a mark's card, an
    edge's explanation or a host's count; a publication carrying none of its bytes, edge traffic or host count, or
    not those it was itself standing in, or taking none on, or not restating the table when it does; a mark's card
    calling the bytes unread meanwhile; edge traffic standing in for none, or after its read failed; a host's count
    standing in for none, or for another host; a described row not kept, described again once its process was chosen
    again, or never; and the caption not said again as the edges' traffic arrives, which a test of its own now
    catches. One, the adoption dropping the table's cached rows, survived because the restatement after it rebuilds
    them, so the reset went. The one left, the restore highlighting a described row itself, survived because the
    read the restore starts re-highlights it through the table's choice; it stays so the restore leans on no read.

- **Revision 616 — an edge to other hosts chosen in the table is its own records (§6.4, I5, R15):**
  - E on such a row listed its source's every record. A host's scope now names several addresses and the processes
    whose connections ran to them; E lists exactly the row's connections over its transport, a filter the breadcrumb
    can remove, the timeline highlights them and the card counts them.
  - Found on the way: removing a lane's mechanism, direction or end filter, or an RPC call's, at the evidence rung
    left the narrower list, since the rung compared scopes without their narrowing; it compares them whole.
  - Debug and Release each ran 2,563 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    twenty-eight mutations were caught: an edge's key unsorted, with an address twice or untrimmed, allowed no
    address or one holding its separator, or naming no processes; its addresses read with its processes, its
    processes read as none, several hosts named by the first, or read by the first alone; a scope ignoring its
    processes, keeping them in its channel key, or reading processes gone without a word; several hosts named by
    their addresses in an interval's bytes, an evidence page's error or the timeline's; E listing the source's
    records, any transport's or any process's; the highlight the source's, any transport's, or not updated as the
    row is chosen; the card's heading or count the source's; and an evidence rung keeping a list narrowed by a
    mechanism, direction, end or call its filters no longer name.

- **Revision 615 — the relationship table says what each edge to other hosts carried (R15, R21):**
  - Its rows said their bytes were "not counted", though their connections' records measured them. Once the table
    lists them in a session whose bytes are read, each says what crossed its connections, counted once at this
    machine's end, and its explanation what it sent and received; one pass reads the reaches drawn, each kept for the
    generation. Under a brush they say their counts and bytes are the whole session's.
  - Debug and Release each ran 2,558 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    twenty mutations were caught: a table's edges never read, read while it is hidden, read again though read, or
    said read with a reach unread; what crossed them counted from their sends alone, measured by their sends alone,
    or with transfers of no size stated as nothing; the split at this machine's end unsaid or swapped; under a brush
    their counts unsaid as the whole session's, or their bytes said whole when unread; the bytes unspoken; a view
    that cannot read bytes calling them unread rather than unknown; a failed read unsaid; rows left stale after a
    read, or unrestated as one starts; a chosen row lost as its rows are restated in other words, written or read;
    and, reading them, one connection taken for a reach's several, or sends of no size counted as measured.

- **Revision 614 — the table's edges to other hosts on the way down and back (R15, §3.2):**
  - From a live pass over revision 613's table on the busy machine: an edge chosen in it came back chosen at the rung
    E or Enter led to, choosing its process there; the way back from there chose nothing; each rung's table opened at
    the scroll of the rung left, a row cut in half; and its relationships stood in the order of their keys. The table
    now lets the edge go before its rows change, the way back chooses it again with its source and the keyboard on its
    row, a newer generation keeps it, each rung's table starts at its first or chosen row, and the relationships come
    most observed first.
  - Debug and Release each ran 2,553 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    eighteen mutations were caught: an edge to other hosts let go on the way down without a word, after the table's
    rows changed, or not at all; the way back finding no edge, choosing its source again over the process chosen in
    an aggregate's list, or not choosing it where nothing else did; E or Enter keeping no way back to it, or Enter
    keeping it before the way down let it go; a newer generation given no edge, choosing none, choosing its source
    again, or choosing it whatever the selection; the keyboard given to a relationship's row alone; the table left
    at the scroll of the rung left; and the relationships in the snapshot's order, ties unordered, or the quietest
    first.

- **Revision 613 — the relationship table lists the graph's edges to other hosts (R15):**
  - The table, the graph's table equivalent, listed none of the edges to other hosts the graph draws since revision
    542 - most of a busy rung. Each is now a row after the relationships, naming where it ran, who opened it and what
    Enter opens; chosen, it chooses its source as a click on the edge does, and Enter opens what a double click does.
  - Debug and Release each ran 2,548 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    twenty-four mutations were caught: no edge to other hosts listed, edges before the relationships, the quietest
    first; a host's port unsaid, the hosts uncounted, a sentence reading the count; who connected reversed; the
    hosts unnamed; a process's rung, or an aggregate's list, not said as what Enter opens, or the rung's own process
    said to open; a row chosen without its source, or not remembered, or taken for another edge's, or kept through a
    choice made in the graph or through another rung; Enter opening a relationship in an edge's place; the reason
    shown beside rows; the caption uncounting; a row read aloud as a relationship, or offering Enter where it opens
    nothing; the line beneath saying a relationship's; the window listing relationships alone; and one process said
    "on their own".

- **Revision 612 — a group's instances say what each did; the graph's names stand apart (§6.1, §6.3):**
  - From a live pass over the busy machine's svchost.exe and chrome.exe rungs: one program's instances were rows of
    one name, the PID cut off. Each is now named by its PID and says what it did - its relationships, and the host it
    reached where it reached one, "↔ 192.168.1.1:53" - then how it started.
  - The graph's names no longer stand against each other where there is room, edges break round their letters, and the
    search's chosen hit's name stands outside its ring.
  - Debug and Release each ran 2,543 tests with revision 473's 93 failures and 4 skips and one more: a window test
    that read a reused PID's two holders' rows in their old words, which now reads them in the new and passes in
    both. All twenty-six mutations were caught: names keeping no room, keeping it with no fallback, or falling back
    first; a chosen name allowed to crowd, or only an overlapping name stepping aside; no halo; a chosen hit's name
    kept clear of the selection's ring alone, a broken ring's not at all, or not placed again, and its leader drawn
    across the ring; one program's single instance renamed, several programs' processes renamed, rows named by
    program, or heard by PID; one relationship unsaid, the PID repeated, the brush unsaid; an accepted connection
    read as made, or its port unsaid; datagrams read as connections; a host's port unsaid; the first of several
    hosts named; an IPv6 address unbracketed; the group's rung left as it was; and the rung's instances, or their
    relationships, not read.

- **Revision 611 — the way back to a chosen relationship chooses it again (§3.2, R15):**
  - E on a chosen relationship, or Enter or a double click opening it, then Esc or the breadcrumb back came to its
    rung with it no longer chosen. The way back to the rung that way began at now chooses it there again, and gives
    the keyboard to its row where the relationship table is shown. Each rung keeps its own way back, and a newer
    generation keeps them.
  - Debug and Release each ran 2,537 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    twelve mutations were caught: a newer generation given no way back to a relationship, or keeping none; a way
    down from a rung keeping that rung's way back; E keeping none, or Enter for a channel or for linked calls; an
    opened relationship's way back kept for the machine rung whatever rung it was opened at; a way back taken at a
    rung below its own, or at another rung at its depth; a way back to a rung replaced kept; no keyboard asked for;
    and the window not giving the relationship's row the keyboard.

- **Revision 610 — the inspector says who connected for a chosen relationship (§6.3, §6.8, R4):**
  - From a live pass over the busy machine's relationships: chosen, a relationship was titled "A ↔ B" whoever
    connected, and its subtitle was eight lines of channel key. It is now titled with its row's mark, "A → B", its
    subtitle says who connected, and how it was derived is the inspector's explanation further down.
  - Found and next: Esc after E on a chosen relationship comes back with nothing chosen.
  - Debug and Release each ran 2,532 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    nine mutations were caught: a relationship named with "↔" whoever connected, or with no mark where none opened;
    the inspector's title not that name; its subtitle without who connected, or its derivation again; the timeline's
    highlight named without the mark; the inspector's explanation not the relationship's, or headed as a pairing;
    and the explanation kept after the choice was let go.

- **Revision 609 — the graph rings the hit chosen among a search's hits (§6.7, R15):**
  - From revision 607's live pass: arrowing through the busy machine's 31 hits for svchost moved nothing in the graph.
    The nodes of the hit chosen - a process's own, a group's members, a channel's two ends - are now ringed in the
    search's ink, whole on a node that is only the hit's processes and broken on one holding them among others, and
    the ring follows the arrows.
  - Debug and Release each ran 2,531 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    eleven mutations were caught: the whole ring, or the broken one, never drawn; the two swapped; a ring kept for a
    hit no longer chosen; a process's, a group's, or a channel's second end left out; the whole ring drawn broken or
    the broken one whole; and the ring unsaid to a screen reader, or a broken one said as whole. The two that never
    draw a ring did not build at first, since the analyzers ask that a member reading no instance data be static;
    written to read it, both were caught.

- **Revision 608 — a bare number offers its port search (§6.7, R15):**
  - From revision 607's live pass: 5353, which six of the busy machine's svchost.exe instances sent to, found nothing,
    since a bare number is a PID and a port is typed after a colon. A number whose port search finds something now
    offers it among its hits, after a process whose PID it is, else first; Enter searches the port in the box, and
    Enter again opens its first hit.
  - Debug and Release each ran 2,529 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    twelve mutations were caught: the port never offered, or offered for a number whose port finds nothing; offered
    first ahead of the process whose PID the number is, or after any process at all; the number read untrimmed;
    going to it opening a rung, or giving the keyboard to the ranked table, or leaving the caret where it was; the
    summary saying only that nothing matched; and the row read aloud as a hit to open, counting records, or named a
    result.

- **Revision 607 — a typed search keeps its first hit chosen; a cell outlasts a taller timeline (§6.2, §6.7):**
  - From a live pass searching the busy machine: typed letter by letter, a search left no hit chosen at every second
    letter, so Enter opened nothing and Down reached no hit. The first hit now stays chosen at every letter.
  - The graph's summary said what a search found, or what a chosen cell highlights, at the end of a line a busy
    machine's census fills, past its ellipsis; it now says it first.
  - A process lane's cell chosen as a group's rung opened lost its explanation and highlight once the lanes' rows made
    the timeline taller and the view was counted again; a view is now counted again only when its width changes. A
    window test had failed on this about one run in eight under load, before this revision too.
  - §19.4 now says a newer generation's layout keeps every node seen anchored, as revision 605 built it.
  - Debug and Release each ran 2,526 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    ten mutations were caught: the hits list's letting go of its hit taken as the person's, the list marked as
    restating its hits never, or its choice read back as kept while they were restated; a view counted again for its
    height alone, or not for its width alone; the graph's summary saying its marks last, in either of its two forms
    or in both, uncapitalized, or a chosen cell's before a search's. A view not counted again for its width alone
    went unseen at first, since the test's wider window also wrapped the timeline's header and made it taller; the
    test now widens a window wide enough already, its height checked unchanged. The cell tests then passed 30 runs
    in a row in Release with three of four cores busy, where they had failed 4 of 30 before the fix.

- **Revision 606 — the inspector scrolls no further than a row needs (R15):**
  - The whole window suite ran five times in Release with three of four cores busy, and nothing failed.
  - A row given the keyboard back after a newer generation, or reached by arrows past the inspector's foot, scrolled
    the inspector two or three times as far as it needed; it now scrolls until the row shows whole, and no further.
  - An export test looked for a PID as any run of its digits, which a run's random hex identity can hold, and failed
    once; it and a like test of `icat hosts` now match the number whole.
  - Debug and Release each ran 2,524 tests with revision 473's 93 failures and 4 skips, and nothing else failed,
    after the export test's fix; before it, Debug had failed that test once, as said. All three mutations were
    caught: the row not laid out before it takes the keyboard, and the host list's or the process list's own
    bringing of a focused row into view kept.

- **Revision 605 — a recording's graph holds still while it grows (§6.3, §6.8):**
  - From a live pass recording the busy machine in chunks: each publication moved nodes the person had seen by up to
    most of the pane, as if a group had been opened. A newer generation's layouts are now settled until the view is
    back, so no node seen moves more than a nudge at a publication, however many join; the choices made between
    publications all stayed chosen.
  - Debug and Release each ran 2,522 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    six mutations were caught: a settled layout easing its anchors anyway; the settled request not reaching the
    layout, or dropped by the scheduler; a newer generation never asking for one, or not asking; and every later
    layout settled.

- **Revision 604 — a newer generation keeps a chosen relationship and a set chosen together (R7, §6.7):**
  - While recording, each publication let go a relationship chosen in the graph or its table, and processes chosen
    with Ctrl+click or Ctrl+Space. Both are now chosen again, what the generation no longer holds said once, and the
    keyboard returns to the relationship's row.
  - Debug and Release each ran 2,520 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    ten mutations were caught: the relationship, or the set, not carried; the relationship not chosen again, in the
    workspace and in the window; a relationship gone not said; the set not chosen again, or chosen beside the ranked
    group's row it began from; one process gone said as several; all gone said as some; and one gone not said. The
    set beside a ranked row went unseen at first, and a test of a set begun from one now holds it.

- **Revision 603 — a newer generation keeps a list's choice, and the keyboard on it (R7, §6.3):**
  - While recording, each publication kept the aggregate or Other hosts chosen but let the process or host chosen in
    its list go, and the way back to the list, and gave the keyboard to the ranked table. It now keeps them, says once
    when the list no longer holds the one chosen, and gives the keyboard back to its row.
  - Debug and Release each ran 2,517 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    ten mutations were caught: the process chosen in a list, the host chosen in Other hosts' list, or the way back
    to a list not carried; the choice, or the way back, not restored; a process, or a host, the list no longer holds
    not said; one still listed said gone; and the window forgetting, or not asking, which list a row had the
    keyboard in.

- **Revision 602 — every way back to an aggregate's list comes to its row (§3.2, §6.3):**
  - From a live pass over the busy machine: E on a process chosen in an aggregate's list, then Esc, comes back to its
    row, and so does forward and back again; a way down from that rung or above it is a way of its own, and the crumb
    of the rung shown chooses nothing. An opened group's Other members comes back the same way.
  - Other members whose processes have no relationship says so once, not again as "no relationship".
  - Debug and Release each ran 2,513 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    eleven mutations were caught: E on a listed process recording no way back, in the workspace and in the window; a
    way down from the list's rung keeping it; a way down below it letting it go, and E there dropping it; the way
    back taken once; a climb past it letting it go; the crumb of the rung shown choosing it again; Other members
    with no relationship saying so twice; its least active said without their relationships; and some without a
    relationship said as only its least active.

- **Revision 601 — the way back from a listed process returns to its list (§3.2, R15):**
  - Esc or the breadcrumb back to the rung a process was opened from in an aggregate's list chooses the aggregate, the
    process in its list and the keyboard on its row, where the way down began, rather than the process's group in the
    ranked table.
  - P26's window test waits for the timeline's own count of the view before reading the interval table it compares; a
    count arriving after the read had failed it once.
  - Debug and Release each ran 2,509 tests with revision 473's 93 failures and 4 skips. Debug also failed P26's
    live-preview window test once; a delayed timeline count reproduced that failure exactly, and the fixed test
    passed with the delay and then 40 runs in a row under load. Nothing else failed. All nine mutations were caught:
    the origin not recorded; the way back by Esc, and by the breadcrumb, not resuming it; a search's descent keeping
    it; it resumed twice; it resumed a rung too deep; the keyboard not asked to the row; the ask uncounted; and the
    process not chosen again. Removing the ask's count alone went unseen at first, since the window reads the
    notice; the workspace test now checks the count.

- **Revision 600 — an aggregate's process row says which connections it accepted (§6.3):**
  - "3 records · 1 TCP connection to 1 host · accepted here at port 445", as Other hosts' list says it of a host, so a
    process that only accepted connections no longer reads as connecting out.
  - Debug and Release each ran 2,508 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    five mutations were caught: a row not saying what was accepted; all accepted said as a count; UDP flows counted
    among what could be accepted; the ports unordered; and nothing accepted said as all accepted.

- **Revision 599 — the keyboard follows an aggregate into its list (§6.3, R15):**
  - Enter or a double click on No relationships, Other hosts only, Other processes or Other members in the graph
    chooses it and gives its process list the keyboard, where Enter opens one; it opened nothing before, so a keyboard
    reached the list only by F6. The node's card and the keys window say so.
  - Debug and Release each ran 2,508 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    six mutations were caught: an aggregate opening nothing again; one chosen again when already chosen, letting its
    chosen process go; the keyboard not asked for; the window not following the ask; the card not saying it; and a
    focus's context node listed and opened.

- **Revision 598 — a busy graph names more of its nodes where its pane has room (§6.3):**
  - Up to 40 names, where 24 were, each placed only where it fits; a process's PID line keeps its threshold of 24
    nodes. The busy machine is named whole at 1920 × 1080 and 27 of 30 at 1456 × 939, where six went unnamed whatever
    the room.
  - Debug and Release each ran 2,506 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    three mutations were caught, one once a test drew a small graph: the budget back at 24 names, the PID line
    following the names' budget again, and no PID line at all.

- **Revision 597 — the other intermittent window tests, stressed (R13, R15):**
  - With three of four cores busy, the HTTP lane test failed none of 120 runs. The path tests' failures were Escape
    pressed in a dialog before it had the keyboard, which this headless platform gives to the button that opened it;
    the tests press Escape once the dialog has the keyboard, as a person does, their waits allow 15 s and name what
    they wait for, and none of 120 runs failed. No product code changed.
  - Debug and Release each ran 2,504 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    change is to tests alone, so the stress runs are its evidence: under the same load the corrected path test
    failed none of 120 runs, where it had failed 4 and 5 of 60.

- **Revision 596 — a zoom of the merged time left before it is read is never drawn (R13):**
  - Every zoom's reading was drawn in turn, one already left included; drawn between Home and Enter, it made Enter act
    on a column of a view no longer asked for, which is why the merged-time window test failed now and then. A reading
    for a zoom no longer asked for is not drawn. With three of the machine's four cores kept busy, the test failed 6
    of 120 runs without it, each at that wait, and none of 120 with it.
  - Debug and Release each ran 2,504 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Both mutations were caught: a reading for a zoom already left drawn, and a drawing not counted.

- **Revision 595 — a process chosen in an aggregate's list is its own records (§6.4, §6.7):**
  - Chosen by a click or the arrow keys, it is what the timeline highlights, the card counts as the selected process
    and E lists; a node chosen in the graph lets it go, and a brush keeps it chosen as the list restates.
  - The list control let its choice go while its rows were restated, though the choice read back as kept, so the list
    showed none chosen while the card counted one; read during a restatement, the choice is now none.
  - Debug ran 2,503 tests with revision 473's 93 failures and 4 skips, and nothing else failed. Release ran the
    same, but for the merged-time window test known to fail now and then - Enter on its first column did not open
    alpha's session within 15 s - which passed five reruns. All ten mutations were caught: the aggregate's records
    highlighted, headed, counted or listed by E with a process chosen; the choice kept when a node is chosen in the
    graph; the list's restatement written back as no choice, or the choice read back during it; the choice not said
    again after it; the highlight not following a choice; and the card's heading not said again.

- **Revision 594 — an aggregate chosen lists the processes it counts (§6.3, §6.7):**
  - No relationships, Other hosts only, Other processes and Other members list their processes in the inspector, the
    busiest first and at most 100, each with its records, relationships and connections to other hosts; Enter or a
    double click on one opens its rung. On the busy machine No relationships' twenty-two are read there, where the
    ranked table, ranking executables, needed their groups opened.
  - Debug and Release each ran 2,502 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    fifteen mutations were caught, one once its test watched the list restated after an interval's counts arrive
    rather than when it is chosen: Other hosts only or Other processes listing nothing; a hundred and one rows; the
    quietest first; names ordered by case; records counted over the whole session under a brush; a process's one
    relationship unsaid, or its relationships counted at one end; its connections to other hosts unsaid, or unmarked
    as the whole session's; the note counting every process; a process the list does not hold opened; the list not
    restated when the scope or the choice changes; and Enter on the list left to the graph.

- **Revision 593 — a program's processes that only reach other hosts take one place (§6.3):**
  - The eight places beside Other hosts go to programs, ranked by their processes' records together: several of one
    are its group's node, one is itself, and one place left over is drawn rather than folded. The busy machine's eight
    places had gone to Teams' four processes, two of svchost.exe's, one of chrome.exe's and ntoskrnl.exe; now
    chrome.exe's eleven, svchost.exe's nine and Teams' four are three nodes, and nothing is folded.
  - A node's card says "no relationship" rather than "0 relationships"; under the node budget a group's node that
    holds its processes that only reach other hosts takes its other members, each counted as a node saved.
  - Debug and Release each ran 2,497 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    fifteen mutations were caught: places by process rather than by program, here and in the property; a place
    ranked by its busiest process or by its size; ties broken the other way; a place too few drawn; a place counted
    in Other hosts only rather than drawn as its group's node; a place skipped by the fold; one place left over
    folded, or one place more drawn, here and in the property; an opened group's processes placed; a group's node
    refused its one remaining member, or credited a node less for it; and "0 relationships" said again.

- **Revision 592 — the ranked list's rows take only their room (§6.1, R15):**
  - A row is 62 px, its two lines and touch target with 4 px of padding, not the theme's 21 px: six rows whole at 1456
    × 939 where there were five, and two and most of a third at 1080 × 700 with a saved session shown.
  - Debug and Release each ran 2,494 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    one mutation, the theme's padding restored, was caught: a row of 75 px, and five rows whole at 1456 by 939.

- **Revision 591 — a connection ranking's figure names its side (§6.1):**
  - "13 made" or "8 accepted" rather than "13 connections", so at 1080 × 700 a row's name is shown whole beside it.
  - Still open from the same pass: with a saved session open, its card leaves the ranked list two rows at 1080 × 700.
  - Debug and Release each ran 2,492 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Both mutations were caught: the figure naming the other side, and the figure saying connections again, which
    leaves the name 72 of the 82 px it needs at 1080 by 700.

- **Revision 590 — two intermittent window tests (tests only):**
  - The dense HTTP lane's pixel check reads the columns of the count the frame drew, since the lane is counted again
    as the window settles; the investigation window's waits allow 15 s and name what they waited for when one times
    out.
  - Debug and Release each ran 2,491 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    merged-time test passed 20 runs alone and 12 runs of its class before the change, and the lane test's columns,
    probed idle, never moved after the first read: its settling matters only when a count lands late, as in a full
    parallel run. No mutation run: no product code changed.

- **Revision 589 — names stay where they stand when a node is chosen or the keyboard moves (§6.3, §6.7, R15):**
  - The names drawn with nothing chosen are placed first, as that frame places them; a chosen or focused node's name
    takes a free slot, or else its preferred one, where unchosen names step aside rather than move. This closes the
    item revision 588 left open.
  - Debug and Release each ran 2,491 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 16 mutations of the placement, made a function of its own, was caught: a chosen name inside its ring, or
    taking a held slot before a free one, or leaving the name it overlaps, or treating unchosen names as chosen
    ones; the keyboard's node not always named, or not named at all; a name already drawn not kept as chosen, or
    placed again though it stands beyond its ring, or kept twice; the budget never spent, or ranks ignored; a name
    and its second line said alone, or one alone said with it; a chosen node's leader from its rim; and the view not
    saying whose its names are, or not telling the placement what is chosen. An earlier run of 8 mutations against
    the drawing alone left 5 uncaught, which is why the placement was made testable on its own.

- **Revision 588 — a name set away from its node is tied to it (§6.3):**
  - From a live pass over a busy synthetic machine, where names in the outer rings read as their neighbours': a name
    takes a slot nearer its own node than any other where one is left, before standing over an arrowhead, and one more
    than 10 px from its node's rim, or nearer another node, is tied to it by a hairline from the rim.
  - Still open from the same pass: choosing a node can move the names around it.
  - Debug and Release each ran 2,486 tests with revision 473's 93 failures and 4 skips, and nothing else failed. Of
    14 mutations 13 were caught: a name taking a slot nearer another node, or one over an arrowhead before one
    nearer another, or its gap measured from its node's centre; a name hiding a node; no leader for a name far off,
    or near but nearer another, through the decision a drawn name takes or alone; a leader for a name over its node,
    from the centre rather than the rim, or to the wrong side of a box above; and the window keeping no leader or no
    owner of a name. The one left, the window's call deciding a near leader without its neighbours, survived because
    the busy machine leaves no name near its node yet nearer another, as names now avoid such slots; the decision's
    own test catches the same change.

- **Revision 587 — the inspector states the selection's connections made and accepted (§6.8, R15):**
  - A Connections fact beneath the selected process's or group's summary, as its card counts them, where a keyboard
    and a screen reader reach it: none the capture saw open, not recorded where the session records no open (R21), and
    the whole session's under a brush.
  - Debug and Release each ran 2,484 tests with revision 473's 93 failures and 4 skips, and nothing else failed. Of
    12 mutations 11 were caught: the snapshot never saying an open is recorded; a process counting no member, or a
    group only its first, or no group stated; a session with no open recorded counted as none; a selection with none
    saying nothing; the brush not said; either side's counts swapped; the screen reader not told what the line is,
    or the line shown with nothing chosen; and the inspector not told when the selection changes. The one left, the
    inspector told again once a brush is counted, survived because the chosen row the brush keeps re-tells it; it
    stays for a selection no row holds.

- **Revision 586 — icat processes says the connections each instance made and accepted (§5.2, §6.8, R18):**
  - A Connections column ("3 made · 12 accepted", "none"), a Connections line under --pid and connectionsMade and
    connectionsAccepted in --json: the overview's own counts, as the window's card says them; not recorded, and null,
    where the session records no connection's open (R21).
  - Debug and Release each ran 2,482 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 11 mutations was caught: a session whose opens are all paired, or all to other hosts, taken for one that
    records none; the overview never saying any open is recorded; the document's made taken from what was accepted,
    or its accepted given where no open is recorded; the note on connections given where none is recorded; the table
    without its column, a count of none written as two zeros, or its two sides swapped; and --pid saying none where
    no open is recorded, or the counts where there are none.

- **Revision 585 — one count of connections made and accepted (§5.2, §6.3, §6.8, R18):**
  - A process's card, the ranking by connections made or accepted and its export read one count from the relation
    index: a paired connection made by its connecting end and accepted by the other, as the capture saw it open at
    either end, a process connected to itself both; a one-sided one by its holder's own open. The ranking counted only
    a process's own connect and accept records, so a server whose accepts were lost ranked as accepting none.
  - A card now also counts a process's connections to itself and to local ports no process of the capture holds, which
    the graph does not draw; a brush takes a connection whose first record is within it.
  - Debug ran 2,481 tests with revision 473's 93 failures and 4 skips, and nothing else failed. Release failed one
    more, an HTTP exchange lane's pixel check, which passed in each of 6 reruns and draws nothing this change
    touches. Each of 22 mutations was caught: a connection seen open neither way, or bound as the evidence policy
    withholds, counted for an end, paired or one-sided; one taken under a brush that it opened outside, at either
    bound; the ends' parts swapped, or the maker or the accepter left out; a one-sided open read the other way; a
    process's later connection replacing its earlier; the card without what each process made or accepted, or
    counting what every policy admits; the ranking counting the whole session under a brush, or under the default
    policy only, or its TCP records not counted, or counted for every mechanism but lifecycle; and a set counting
    every process, or its accepts from what they made.

- **Revision 584 — a process's card counts the connections it made and accepted (§6.3, §6.8):**
  - "Connections: 3 made · 12 accepted, as the capture saw them open", beneath where its connections ran: its
    relationships by the end that connected, its connections to other hosts by its own connect or accept; a group's
    members' together, one between two of them made and accepted alike. None seen opening says nothing; the rest of
    the machine's card leaves its processes' to their own rungs.
  - Both that line and the one saying where the connections ran count the whole session, and under a brush now say so.
    The ranking counts a process's own connect and accept records, so the two differ where only one end of a paired
    connection was seen opening.
  - Debug and Release each ran 2,479 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 16 mutations was caught: a relationship's connections counted from the wrong end, made or accepted by
    its source alone, or once where both its ends are members; a host's accepts counted as connects, or left out, or
    every process's hosts counted for one; the line left out where only accepts were seen, or its two counts
    swapped; the rest of the machine's card counting its processes', the card counting none, or setting the line
    above where the connections ran; and a brushed card not saying either line is the whole session's.

- **Revision 583 — rank by connections made or accepted (§5.2, §6.1, R18):**
  - Rank by offers Connections made and Connections accepted: each process's own TCP connects or accepts in scope, a
    group's its members' together, so the processes that serve others rank first; unmeasured, never zero, where the
    session records no open.
  - icat export --rank-by connections-made and connections-accepted export the same rows; icat metric names neither
    yet.
  - Debug and Release each ran 2,477 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 29 mutations was caught: the reader counting no connect, no accept or no TCP record, every mechanism's
    records but lifecycle's, or the whole session under a brush; taking a session whose opens are all paired, or all
    to other hosts, for one that records none; a ranking of connections made reading the accepts, a session without
    opens measured rather than unmeasured, a process with no TCP record counted as none, or one in such a session as
    known; a group's count recorded where a member's is not, or holding only its last member's accepts; the ladder
    or the workspace leaving the counts out; the family taken for peers, connections accepted called made, or the
    export naming one ranking for the other or ranking by records; the selector without Connections accepted, or its
    measures never found, read or applied to the rows; the note saying the other side, or no TCP where no open is
    recorded, and the detail leaving the side out; a row's figure saying no TCP where no open is recorded, or read
    aloud with the other side; and icat export taking connections-accepted for connections-made.

- **Revision 582 — a lone arrowhead holds on a shorter edge (§6.3):**
  - An edge carrying one head needs room for it alone, not for a head at each end: a dense graph of 450 edges drew 205
    of its 375 heads rather than 153, repainting in about 6 to 7 ms with them, under a frame's 16.7 ms.
  - Debug and Release each ran 2,471 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 4 mutations was caught: a lone head still needing room for a head at each end, or no stroke behind it;
    and the graph taking every edge for one that carries a lone head, or for one that carries two.

- **Revision 581 — icat overview says who connected (§6.8, R18):**
  - It lists the five busiest relationships, maker first - "client.exe · PID 100 → server.exe · PID 200", ⇄ both ways,
    "and" where none was seen opening - and its caveat no longer calls every edge's direction display order only.
  - Its other-hosts tables say each process's and each host's accepted connections, as `icat hosts` does, rather than
    calling every process one reaching other hosts.
  - Debug and Release each ran 2,471 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 13 mutations was caught: a relationship named target first where its source connected, source first
    where its target did, with one arrow where each end connected, or with an arrow where none was seen opening; the
    quietest relationships listed first, or the note on the arrows left out; the accepted column left out, counting
    the connections made rather than accepted, or naming none of the ports they arrived at; the hosts' accepted
    column left out, or a host's ports left blank rather than marked none; the other-hosts line calling every
    process one reaching them; and the caveat calling every edge's direction display order only.

- **Revision 580 — the demo shows who connected (§14):**
  - The demo records each TCP connection's open as Kernel-Network does - a connect at the end that made it, an accept
    at the end that took it - so its graph, cards, table and `icat hosts` show the client's processes connecting out,
    the server's api and files accepting, and api connecting to db.
  - The graph's summary counts processes "with connections to other hosts" rather than saying they connect to them.
  - Debug and Release each ran 2,469 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 11 mutations was caught: the demo leaving out the browser's or sync's connect, api's or files' accept,
    or api's connect to db or db's accept of it; recording an accept as a connect, under the connect's event, or
    naming an open's ends the wrong way round; leaving the accept out of its descriptors; and the graph's summary
    saying its processes connect to other hosts.

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
     and since revision 470 both widths are kept with the split, once for every session (`workspace-v20`). Other
     hosts opened into its busiest hosts, a control since revision 555, is kept since revision 557 (`workspace-v21`).
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
`query-identity-v1.md`, `live-follow-v1.md`, `app-settings-v1.md`, `workspace-v21.md`, `support-bundle-v1.md`, `demo-investigation-v1.md`, `clock-calibration-v1.md`, `coverage-v2.md`; ADR-008, ADR-010, ADR-012, ADR-013, ADR-023–031, ADR-038–041; the complete historical ledger linked above.
