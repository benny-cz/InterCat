# InterCat implementation status

Updated: 2026-10-20 · Plan revision: 657 · Branch: `main`

This is the **current resume point**, not a running transcript. Update the backlog and open-work tables in place after
each slice, then add only a short latest-change note. The complete pre-revision-104 chronology, measurements, and old
verification ledger are preserved in [the historical status](history/IMPLEMENTATION-STATUS-through-revision-103.md),
revisions 104–229 in [the second historical log](history/IMPLEMENTATION-STATUS-revisions-104-to-229.md),
revisions 230–499 in [the third](history/IMPLEMENTATION-STATUS-revisions-230-to-499.md), revisions 500–579 in
[the fourth](history/IMPLEMENTATION-STATUS-revisions-500-to-579.md), and revisions 580–619 in
[the fifth](history/IMPLEMENTATION-STATUS-revisions-580-to-619.md).
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

- **Revision 657 — a chosen program names the programs its processes talked with (§6.3, §6.8):**
  - On the heavy machine, choosing svchost.exe said its processes made 62 connections and accepted 55, not with whom.
    The inspector now lists its five busiest pairs of programs, maker first, each over its relationships and records -
    svchost.exe → svchost.exe, svchost.exe → lsass.exe, chrome.exe → svchost.exe and on - and how many more, the whole
    session's under a brush.
  - Debug and Release each ran 2,641 tests with revision 473's 93 failures and 4 skips, and nothing else failed, the
    cut-off text test now measuring the new section with a program chosen. All nine mutations were caught: session
    groups named as programs, a pair matched by its first program alone, four pairs named, one more program left
    unsaid, the heading's whole session said without a brush, the list's and the heading's changes unheard, arrows
    read aloud, and the section shown with nothing chosen. A tenth, the heading never saying whole session, did not
    build; its replacement, saying it only without a brush, was caught.

- **Revision 656 — `icat overview` lists the busiest pairs of programs (§6.3, R18):**
  - On the heavy machine four of its five busiest relationships were chrome.exe's own processes. It now also sums them
    by the two programs at their ends, maker first, with their relationships, processes, mechanisms and records -
    svchost.exe → svchost.exe, chrome.exe → chrome.exe, msedge.exe → msedge.exe, Code.exe → node.exe, svchost.exe →
    lsass.exe - wherever two programs share more than one relationship.
  - Debug and Release each ran 2,640 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    nine mutations were caught: the maker not named first, no tie broken by name, a program's own connections split
    as two ways, fewer relationships ranked first, a target's process uncounted, a connection counted in drawn
    order, both ways read as one, the table listed where each pair shares one relationship, and only its first row
    listed.

- **Revision 655 — the investigation's notes and candidate joins say why they list none (§8.3):**
  - A fresh investigation's Notes tab and, before a search, its Candidate joins tab showed blank boxes. Each now says
    in its place what it will hold and how: "No note yet. Add a note… writes one…", and what Find candidate joins
    lists there.
  - Debug and Release each ran 2,637 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint on every rung still allocating nothing of its own. All three mutations were caught: the notes list's
    sentence never hidden or never shown, and the candidate list's kept after a search.

- **Revision 654 — the inspector's headings wrap (§6.8):**
  - With a group chosen, "How its processes are grouped" was cut to "…are groupec" at the inspector's edge: the
    headings that read the selection did not wrap. They do now, and the minimum-window cut-off test checks each rung
    with its row chosen, where those headings show, as well as opened.
  - Debug and Release each ran 2,637 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint on every rung still allocating nothing of its own. The test of cut-off text failed before the headings
    wrapped, on "How its processes are grouped", and its one mutation, that heading unwrapped again, was caught.

- **Revision 653 — Other hosts' list and card name who reached each host (§6.3, R5):**
  - The inspector listed the heavy machine's 97 hosts each "from 1 process". Each host listed or named on the card now
    says who reached it as `icat hosts` does, "5 UDP flows from msedge.exe · PID 3400 and 4 more", from one helper
    both read; one drawn process's hosts still name none.
  - Debug and Release each ran 2,637 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint on every rung still allocating nothing of its own. All six mutations were caught: a process that reached
    a host over two transports counted twice, the quietest named first, how many more left out, the list's rows and
    the card's lines counting the processes again, and one drawn process's hosts naming it.

- **Revision 652 — at a process's rung a peer of its own program is named by its PID (§7.4):**
  - At chrome.exe's busiest process, 34 of whose 36 peers are chrome.exe's, the rail cut every row before the PID:
    each read "← chrome.exe · Pl…". Such a peer now reads "← PID 3028", as the graph names it at that rung; another
    program's keeps its whole name, and every peer is read aloud whole.
  - Debug and Release each ran 2,637 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint on every rung still allocating nothing of its own. All three mutations were caught: a same-program peer
    named whole, every peer named by its PID, and a peer read aloud by its PID alone.

- **Revision 651 — the relationship table names each relationship's maker first (§6.8, R5):**
  - On the heavy machine one program's processes each connecting to its browser process read two ways row by row,
    "chrome.exe · PID 3000 ← chrome.exe · PID 3028" above "chrome.exe · PID 3024 → chrome.exe · PID 3000", as their
    ids sorted. A relationship only one end made connections of now names that end first, its mark pointing on, as
    `icat overview` names it, in its row, the inspector's title and the timeline's highlight.
  - Debug and Release each ran 2,636 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint on every rung still allocating nothing of its own. All four mutations were caught: the maker never named
    first, a relationship made each way turned about too, the mark left pointing back, and the names left in the
    display order.

- **Revision 650 — a paired channel is named by its two processes and which connected to which (§7.1, R5):**
  - `icat channels` printed each channel as its key and endpoints alone, and the window's channel list joined its two
    processes by "↔" whichever made it. Both now read "msedge.exe · PID 3448 → msedge.exe · PID 3400 · TCP · :50113 →
    :9223 on 127.0.0.1", or "and" where neither was seen opening it; a page names its channels' processes itself
    (`processNames`), and the command line puts each key on a line of its own.
  - Debug and Release each ran 2,636 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint on every rung still allocating nothing of its own. All fourteen mutations were caught: the maker never
    led, nor any channel read as made, its endpoints or names left in the name's order, the arrow between endpoints
    dropped, an unknown end called unlisted, the transport left out, a reused PID's holder numbered as the first or
    not at all, a page naming no end or only the first, and the key or the page's names left out of icat channels
    and the window's list.

- **Revision 649 — `icat processes --pid` lists the other hosts an instance reached (§6.3, R18):**
  - Its Exchanged with table folded every end with no process of the capture into one row: chrome.exe's 673,817 B to
    41 hosts read as one "unresolved" end. Beneath it the instance's other hosts are now listed by address, as `icat
    hosts` lists them, over its own connections: connections, ports, those accepted, records and bytes each way, the
    most bytes first, twenty and how many more; the JSON gives every one (`otherHosts`).
  - Debug and Release each ran 2,634 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint on every rung still allocating nothing of its own. All eleven mutations were caught: the hosts not
    listed or not read, another process's connections counted, the hosts ranked by records, every host listed or the
    rest not counted, the accepted connections, ports, scope or sent bytes left out or misread, and the note on who
    reached an address pointing where it did.

- **Revision 648 — the command line names who reached each other host (§6.3, R18):**
  - A live pass with `icat` over the heavy machine's session: `icat hosts` counted the processes that reached each of
    its 97 hosts and named none. Each host now names the process whose connections there hold the most records and how
    many more reached it - "msedge.exe · PID 3400 and 4 more" - in `icat hosts` and `icat overview` alike, and the
    JSON lists them all (`other-hosts-v7`).
  - `icat hosts --rank-by` listed a host a process accepted a connection from with the host's own port among its Ports
    and no Accepted here count: a host read for its bytes is now counted as the census counts it.
  - Debug and Release each ran 2,633 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint on every rung still allocating nothing of its own. All fifteen mutations were caught: a host read for
    its bytes counting an accepted connection's port among its ports, or not counting its connections made, its
    connections accepted or their ports, or taking those ports at the host's end, in the traffic's test and in icat
    hosts; a process that reached a host over two transports counted twice, the quietest named first, how many more
    left out or miscounted, the connections counting the processes again in icat hosts and icat overview, the JSON
    listing none, and the Processes column left blank in either.

- **Revision 647 — a graph names each program it draws before it names one twice (§6.3):**
  - At the default window the heavy machine named Code.exe and node.exe eight times each and left lsass.exe,
    services.exe and six more programs unnamed. A process whose name an aggregate or a busier process bears is now
    named after every process first to bear its own: 16 of the 19 programs are named, where 11 were.
  - Debug and Release each ran 2,631 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint on every rung still allocating nothing of its own. All four mutations were caught: processes ranked
    whatever their names repeat, an aggregate's name not counting, repeats ranked first, and repeats never named.

- **Revision 646 — an opened group's own processes are named by their PIDs (§6.3):**
  - Opened on the heavy machine, chrome.exe's every name read "chrome.exe", its rung's title already, where its table
    names each process "PID 3012". An opened group's own processes are now named by their PIDs, at its rung and a
    process's below it, with no second line repeating it; its peers keep their programs' names.
  - Debug and Release each ran 2,630 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint on every rung still allocating nothing of its own. All four mutations were caught: an opened group's
    processes named by their program again, every process named by its PID, a PID's second line under a name that is
    its PID, and the halo laid round the program's name under a PID's letters.

- **Revision 645 — Other hosts is named before the aggregates round it (§6.3):**
  - A live pass over a heavy machine shaped as one is - system hubs, two browsers, an editor and its servers, a
    database and its users, 255 processes - opened in 0.16 s and drew 72 nodes with no disc over another. At the
    smallest window Other hosts went unnamed, the aggregates round it having taken each slot first; it is now named
    before them, and every aggregate still is.
  - Debug and Release each ran 2,629 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    three mutations were caught: Other hosts ranked with the aggregates again, processes named before aggregates,
    and processes left unranked.

- **Revision 644 — an edge passing beneath a name breaks round its letters' corners too (§6.3):**
  - At 1920 × 1080 an edge two pixels above "chrome.exe" showed through the halo by nearly half at a letter's corner,
    where one halo copy alone covers the diagonal pixel. The four diagonal copies are laid twice, and the fleck shows
    a fifth of the ink; the window test checks the letters at 1920 × 1080 and 1456 × 939.
  - Debug and Release each ran 2,628 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint still allocating nothing of its own. Both mutations were caught: the corners laid once, or the sides
    laid twice in their place, leave the edge showing through beside a letter at 1920 × 1080.

- **Revision 643 — the graph's display budget is what the design pane holds (§6.3):**
  - On wide machines of 80 programs related at random, 200 nodes left up to 235 pairs of discs overlapping at the
    design pane, by up to 25 px, a disc's centre under another's; at 100 a few touched by at most 5 px and none hid
    another. The default budget is now 100 nodes: a busy machine past it folds more groups, each counted and opened by
    Enter, and the tables and search still list every process.
  - Debug and Release each ran 2,628 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Both mutations were caught: the budget back at 200 nodes, or at 120, leaves a wide machine's disc over another's
    centre at the design pane.

- **Revision 642 — a lone component is drawn about the pane's middle (§19.4):**
  - The seeding grid gave a lone component two columns on the wide pane and started it in the first, and grown at
    angles its hashes fix it leaned further; relaxation's weak pull across never brought it back, so dense drawings
    sat with their middles a quarter to a third of the way across. A short row of the grid is now centred and a
    component, once grown, is set back across on its cell's middle: the same drawings sit within 0.02 of the middle.
    The busy machine is named whole at 1456 × 939, and two window tests of its names check each rule where its premise
    holds; an edge's ink showing through a name's halo at 1920 × 1080, found on the way, is left for its own revision.
  - Debug and Release each ran 2,627 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    five mutations were caught: a short row's components started at its left or at its right; a component left
    leaning, or set back by its rightmost node rather than its middle; and revision 641's spread grown from the
    drawing's left edge rather than its middle, which a centred drawing now shows. Two mutations of a first draft
    went unnoticed, and the code they named went with them: no more columns than components, which a centred short
    row already gives a lone one, and a component set back down as well as across, which relaxation's pull down
    already does.

- **Revision 641 — a dense graph's discs stand apart (§6.3, §19.4):**
  - A live pass over a wide machine, 253 processes of 80 programs, found its machine rung drawn in the left half of
    the pane with 846 pairs of discs overlapping, some out of the pointer's reach, and an opened group's 61 nodes
    overlapping in 23. The layout now parts what relaxation leaves too near: the drawing widens about its middle as
    far as its discs need room, then each pair still too near is pushed apart. The group's discs all stand clear, and
    up to 100 nodes none overlap; at 199, more than the pane holds, 212 pairs still do.
  - Debug and Release each ran 2,625 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Thirteen of sixteen mutations were caught: no separation, or one parting the first frame; a pin giving way; a
    held node giving way wholly or not at all, ignoring a new drawing's freedom, or spread wholly; no spread, or one
    without room to spare; no pushes, or only ten rounds; pushes to touching rather than the clearance, or moving
    one disc of a pair. Three went unnoticed, none changing what the tests hold: a spread half a square wider; one
    grown from the drawing's left edge rather than its middle, the same while the seeding leaves a lone component
    against that edge, the pass's other finding; and a spread tried where no two discs are too near, where it found
    room enough on every drawing measured.

- **Revision 640 — a range of the investigation's time states both ends alike (§8.2, §8.4):**
  - A live pass over the investigation window at its smallest found its pages sound, and a lane saying its records ran
    "from 0.0001 s to 0.000103 s", each end to its own precision, as icat workspace timeline's interval did. A range
    now states both ends to the finer end's precision, at least milliseconds.
  - Debug and Release each ran 2,621 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    six mutations were caught: a range stated to its start's precision or its end's alone, or below milliseconds;
    and a lane's sentence, icat workspace timeline's interval or a saved view's confirmation stating its ends apart
    again.

- **Revision 639 — the breadcrumb up lands where Esc does (§3.2, §6.7):**
  - A crumb up chose no row, and left the deeper rungs' process selected at the machine rung. It now chooses the row
    the rung was left with - the row the way down went through - once the rung lists it, after an aggregate's list or
    a relationship a way down began at, which are chosen again first.
  - Debug and Release each ran 2,613 tests with revision 473's 93 failures and 4 skips, and nothing else failed; the
    test of forward history across a publication now expects nothing said of the process a crumb's view had kept
    selected, the crumb having chosen its group. All six mutations were caught: a crumb up choosing no row, or
    choosing it before an aggregate's list or a relationship a way down began at, or over either; and either way
    back taken but not said to have been.

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
