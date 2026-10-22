# InterCat implementation status

Updated: 2026-10-22 · Plan revision: 711 · Branch: `main`

This is the **current resume point**, not a running transcript. Update the backlog and open-work tables in place after
each slice, then add only a short latest-change note. The complete pre-revision-104 chronology, measurements, and old
verification ledger are preserved in [the historical status](history/IMPLEMENTATION-STATUS-through-revision-103.md),
revisions 104–229 in [the second historical log](history/IMPLEMENTATION-STATUS-revisions-104-to-229.md),
revisions 230–499 in [the third](history/IMPLEMENTATION-STATUS-revisions-230-to-499.md), revisions 500–579 in
[the fourth](history/IMPLEMENTATION-STATUS-revisions-500-to-579.md), revisions 580–619 in
[the fifth](history/IMPLEMENTATION-STATUS-revisions-580-to-619.md), and revisions 620–679 in
[the sixth](history/IMPLEMENTATION-STATUS-revisions-620-to-679.md).
The product contract and milestone gates remain in [the implementation plan](design/INTERCAT-IMPLEMENTATION-PLAN.md), and
what each of its revisions from 173 on did and found in [its revision log](design/INTERCAT-PLAN-REVISIONS.md).

## Where the product stands

M0 and its IC-010a capture-impact follow-on are complete on the measured development build. M1's journal, profiles,
physical store, commit/recovery, and leases exist, and since revisions 434 and 435 an interval release keeps the
identity evidence and the open operations of what it retains. Since revision 439 a follow keeps a rolling window of
session time, and since revision 442 a person's pin keeps a session's records from a moment through every release,
within the size it allows, and since revision 448 a rolling capture's broker releases what its follow gave up, and since revision 705 an import into the folder that already holds it reuses it, and since revision 709 one into a new folder reuses the folder the catalogue of imports lists; mechanism breadth remains. M2 has a real broker-driven Explore and saved-session Desktop flow, a navigable real
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
its processes and relationships from every record, and since revision 710 so does a session still being recorded,
from the checkpoint of the generation before that each live generation names: at 4,000,000 records it derives in
0.2 s rather than 1.5 s. Since revision 163 a finished session's first view comes from a persisted
overview too, and opens no segment: 4M and 10M rows both reopen in about 0.17 s, down from 1.1 s and 2.2–2.4 s.
Since revision 440 its RPC calls and their other ends are kept beside them, so its first call ranking, RPC listing or
brush pairs no call, and since revision 441 its HTTP exchanges, so its first HTTP listing groups no buffer. Since
revision 456 its zooms count from persisted tiles, since revision 457 its brushes from those tiles' tallies of what
their records bind to, since revision 458 a group's lanes, since revision 459 a process's direction rows and since
revision 460 a channel's two ends, so none opens a segment: at 10M rows a brushed ranking takes 15 ms at the 95th
percentile, a 40-process group's first lanes 52 ms, a process's first direction rows 156 ms, a connection's first ends
192 ms, and the session's working set ends near 320 MB. Since revision 704 a zoom ranked by bytes adds up its lanes
from the tiles' byte sums, and since revision 706 a brush ranked by bytes measures its processes and channel ends
from their byte tallies: 1.9 to 12.5 ms at 10M rows, where reading their rows took up to 1.3 s. Since revision 707
a group's lanes under a byte ranking add up from them too: at 10M rows and 2,000 columns 68 ms at the median and 641
ms the first time, from 114 ms and 2.4 s, and since revision 708 a process's direction rows: 51 ms at the median and
312 ms the first time, from 82 ms and 1.8 s.
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
| IC-013 canonical import | ETL import into verified session implemented; since revision 705 the folder that already holds an import is reused, and any other folder that is not new refused with what it holds; since revision 709 an import into a new folder reuses the folder the user's catalogue of imports lists, once it verifies | Normalizer-upgrade generations, ETL/journal overlap disclosure; an import that publishes runs, and records itself in the catalogue, only on Windows. |
| IC-014 broker | Authenticated pipe, protected root, durable ownership/recovery, live evidence and live preview counts, ordinary CLI/Desktop client implemented; parent-owner parser blocker repaired and CLI/Desktop Explore exercised on the affected host; a crashed client's capture qualified to stop at lease expiry, finalized and leak-free, and its session finished by the next launch from the follow's ticket (`live-follow-v1`, qualified on real ETW), and a crashed `icat capture`'s by `icat follow <session>`; a connection bounded by request rate rather than a total, so an owner keeps it for a 24-hour capture; since revision 394 tests hold P18, P19 and P20 on any host: what the broker can reach, ownership by the authenticated identity alone, and no intrusive peer resolution | Installer pre-creation, retail-build matrix and remaining broker release qualification. |
| IC-015 metrics/entities | Source-observation metrics, process/executable grouping, TCP/UDP relations, peer/channel lower bounds; since revision 156 the relation index counts records by their other end, and a relation's untimed records, as it derives, so the overview reads no row's relation; since revision 157 a generation's instances and relations extend the previous generation's, exactly, or are derived in full; since revision 162 a finished session publishes their state as a derivation checkpoint, which a reopen builds both from (`derivation-checkpoint-v1`), and since revision 163 its whole-session overview counts beside it (`overview-index-v1`), so a reopen opens no segment, with each overview column's bytes per mechanism since revision 289 (minor 2), and each process's and TCP channel end's whole-session bytes before any policy since revision 292 (minor 3); since revision 166 each instance's own records per mechanism (`process-activity-v1`, entities-v1 §4a), extended between generations and kept in the checkpoint's format 1.1, rank the ranked table; since revision 173 IPv6 ends relate (`transport-endpoint-relation-v4`); since revision 178 RPC calls are derived as operations (`rpc-call-operation-v1`), and since revision 183 counted on the logical-operations basis (`metrics-v1` §8a); since revision 373 `icat operations`, `icat exchanges` and `icat channels` say what the capture covered of what they list, from its ledger, and no source is said uncollected from its records' absence (R21); since revision 374 `icat processes` states the coverage `icat metric` states of the same answer, and never that a process none of whose bytes was measured sent nothing (R21, P1); since revision 379 its table says a process's bytes are unmeasured, or that it made no sends or receives, as the window's rows do (R21, R18); since revision 380 its call, exchange and peer tables say untimed, not reached, unmeasured or none where they printed a dash (R21, R18); since revision 376 a binding's reason reads in one set of words, and direct evidence only's exclusions are said as what they are (R5, R22); since revision 377 `icat session` says the coverage ledger's facts in its coverage reasons' words (R5); since revision 378 `icat metric` names its basis, byte domain and accounting in the window's words (R5); since revision 404 a grouped answer groups by terminal session, a process whose lifecycle records name none unattributed by that reason (`metrics-v1` §6, `icat metric --group-by session`); since revision 440 a finished session keeps its RPC calls and their other ends in an operation index beside its checkpoint (`operation-index-v1`), so a reopen's first call ranking, RPC listing or brush pairs none, and since revision 441 its HTTP exchanges (minor 1), so a reopen's first HTTP listing groups none | Canonical transfer owner, operation durations and operations beyond RPC calls (counted since revision 183), resource topology (process parents and children are shown since revision 211), relations beyond TCP and UDP, full coverage epoch publication. |
| IC-015a segments | Complete observation/source-field tables; since minor 1, every byte a reader interprets has a checksum of its own, and a published segment's reader reads each column when it is first asked for; since revision 161 the reader cache charges what a reader holds and trims readers to session time and mechanism past its budget; since revision 172 `observation-v2` holds IPv6 endpoint addresses, written only for a segment that has one | Compression and derived scale structures are later work. |
| IC-016 store | Complete M1 commit/recovery/lease/explicit-retention scope; a lease confirms measured dependencies from one directory listing; a viewer opens a session from one listing and hashes its segments, dictionaries and journals after the first view, falling back to the last-known-good, stated, when a file changed; queries share verified immutable segment readers, safe across threads, admitted within 256 MiB of published payload per store, pruned to what the selected generation names; a viewer holds one store per session, a capture's writer included, and keeps readers only for the session it shows; a writer removes superseded manifests as it publishes, and a reader waits out that removal; since revision 162 an index is published as a generation of its own (`CommitIndex`), carried by no additive generation and released with the segments it describes; since revision 710 a live follow stages a checkpoint of the generation before into each generation it publishes, and a superseded generation goes with the indexes only it named; since revision 234 kept content is a `Content` dependency beside the journal (`content-v1`), carried like a journal and released with its journal chunk, and since revision 306 on its own too, by a `Content` retention once its capture has finished; since revision 315 every later generation carries the latest journal and content release; since revision 442 a person's pins keep a session's records from a moment through every release, within the size each allows, in a file of their own beside the generations (`retention-pins.json`, store-v1 §8, ADR-046), which `icat pin` lists, places and removes, and since revision 443 the window's **Keep records…** from its time scope's start, its size line stating them; since revision 446 a chunk release states the chunks and records its recording gave up in all (store-v1 §8, ADR-048), which `icat session` says; since revision 702 a reader meeting the pointer while a writer publishes acquires the generation it names, rather than refuse a session that verifies (store-v1 §6) | None open in its M1 scope; since revision 450 the size line says what a capture whose broker rolls its evidence holds of its journal limit (IC-016a, S5). |
| IC-016a checkpoint | In progress: since revision 434 an interval release (`Interval`, ADR-043) gives up a session's oldest journal units with their rows, keeping as evidence the lifecycle and first records of every process and connection a retained record names, so every retained record binds, pairs and keys as before; since revision 435 an RPC call, its other end's ALPC evidence and an HTTP exchange open across the boundary keep their records (I20); since revision 436 coverage over any scope reaching before the boundary is a partial gap that says why, and the window says from when a session keeps every record; since revision 437 `icat retain --release-before <moment>` discloses a release and performs it with a reason once no recorder can be writing the session; since revision 438 a follow goes on across releases of the session it derives into (ADR-044); since revision 439 `icat follow` and `icat capture --keep-last` keep the newest window of session time (ADR-045), and since revision 442 stop at a pin's moment, stopping the follow once the session outgrows what the pin allows (ADR-046); since revision 445 the window keeps one too, and draws a released session from its boundary; since revision 446 a follow goes on across the evidence's chunk releases, placed by their bytes and what each states it gave up in all (ADR-048), and since revision 448 the broker releases the chunks a rolling capture's follow gave up (`ReleaseFollowed`), so its journal allowance bounds what its evidence holds; since revision 450 the size line projects a rolling capture's stop from what its window holds, says when it begins releasing, and when a pin keeping it past its window stops it | A rolling capture measured on Windows for hours, its evidence and session holding their window: here only scripted captures exercise the broker's releases. |
| IC-017 Desktop projection | Real overview, channel/evidence ladder, bounded metadata search, layout scheduling, live follow, interval/zoom/minimap with wheel and keyboard, exact L0 mechanism lanes, L1 process-owner lanes, L2 source-direction rows and L3 channel-end lanes banded by direction, with shared scale, own coverage, hover/time selection, persistent table/step focus and keyboard/wheel scrolling, exact bounded query data carried through live publications, the visible range as the default scope with a scope lock, and a bounded §6.3 graph with relationship-first layout, semantic hover, manual pinning/re-layout, quiet folding, minimal group collapse, table-shared selection, anchored carried layout, per-rung neighbourhoods with a context node, §6.7's edge double-click and back/forward history that restores each rung's interval, a per-rung timeline focus that counts what E reads, a selection highlighted in the timeline by its own exact count (§6.4) and a Ctrl+click multi-selection that Enter turns into a filter (§6.7), a labelled live edge that previews unpublished records within §12's steady-state budget (P26 asserted), a designed waiting state before a capture's first publication, a launch-time offer to finish a session a crashed viewer left, and the saved sessions listed while none is open; since revision 189 the machine and group rungs rank by records or by bytes sent or received (§6.1's metric selector), since revision 190 by RPC calls made or served, since revision 196 by bytes sent and received and by RPC errors, since revision 199 by median RPC call and serve time, and since revision 200 by peers, each listed by its basis since revision 201, and read per second over the ranked interval since revision 209; a large group keeps its process lanes when zoomed, counted coarser, since revision 210; the machine rung's lanes plot what a byte ranking measures since revision 284, a group's process lanes since revision 285, and a process's direction rows since revision 286; since revision 287 every chosen state (a selected row, a toggle or box that is on, a tab, a focused field, a combo box's list) is drawn from the tokens in every mode, never in the platform's accent colour; since revision 323 each relationship names the rule and version that derived it, and a TCP one the key of each connection it rests on, in the table's evidence tooltip, its screen-reader name and the edge's card (R4), and since revision 324 an RPC one its own key, which opens the calls its links join; since revision 325 the relationship chosen in the table is explained beneath it, for a keyboard as for a pointer, and since revision 327 Enter or a double click on it opens it as a double click on its edge does, an RPC one at the calls its links join; since revision 329 a click on an edge chooses its relationship, which its halo, the inspector and the timeline's highlight then show, and since revision 330 the inspector's evidence card and E follow it too, at every rung since revision 345; since revision 335 a selected process states the rule that bound its records and how strongly, a reused PID's later holder with how many the evidence policy left out (§6.8), since revision 336 a chosen paired channel the rule that paired its ends, whether the capture saw it open and close, and its key, since revision 337 a chosen group how it was formed and what its total leaves out, and since revision 338 a reused PID's candidates are counted one action from the explanation that names them, every read following, with the rail saying so until one click returns to correlated evidence, and since revision 339 a session opened from an investigation keeps that choice there with its pins and ranking (`workspace-v13`); since revision 340 a reused PID's holders are numbered wherever a process is named ("PID 100 #2"), as `icat processes` numbers them, and since revision 341 an edge's card names each process end by name and PID, as the relationship table does; since revision 342 the legend keys the lanes' heights and offers each lane its own scale (§6.2's normalization scope), which an investigation keeps since revision 343 (`workspace-v14`); since revision 344 either main pane fills the column by an explicit command (§6.1); since revision 348 a row chosen among a process's rows is what the evidence card counts, the timeline highlights and E lists; since revision 350 the timeline states its time base, session time since the capture's recorded wall-clock start; since revision 352 Esc cancels a drag in progress before it ascends; since revision 353 the card, with nothing selected, names the records E lists; since revision 354 a reused PID's later holder's empty rung says its records were left out as candidates and offers to count them there; since revision 355 a channel's own rung counts the channel on the card, as E lists it; since revision 356 an aggregate chosen in the graph is what the card counts and E lists, its processes' records; since revision 358 a double click follows only a plain click, so a press where a pan, brush or cancelled drag began zooms or opens nothing; since revision 359 E from an aggregate names it; since revision 360 an investigation keeps the window's panes, the split and the pane filling the column, for every session opened from it (`workspace-v15`); since revision 362 the inspector states beneath its time scope what the capture covered there, mechanism by mechanism (R21); since revision 367 an export, the window's and `icat export`'s, states the same of its scope (R21, R18), and since revision 407 how its processes were grouped, `icat export --group-by session` reaching a session's rung as the window does (R18); since revision 368 a record without a size says why in the rail and the inspector (R3, R5); since revision 370 a channel's own rung names the channel in the inspector, as its card counts it (I5); since revision 371 a selected record states its own coverage (R21); since revision 372 an export's CSV, on every line, and the redacted sharing report state their scope's coverage too (R21); since revision 373 the channel browser says what the capture covered of TCP, announced as the reason an empty list holds none (R21); since revision 375 `icat overview` states the coverage the inspector states beneath its time scope (R18); since revision 382 a one-sided connection's bytes, on its rung and in `icat channels`, and a selection's in the inspector say a direction no transfer measured as unmeasured and one with no record as such, never 0 B (R21); since revision 385 `icat processes` explains each instance's binding in the inspector's words, and what the evidence policy left out of a reused PID's later holder (R18, R21); since revision 386 `icat channels` says how each channel was paired, as the inspector does (R18); since revision 387 a process's lane can be pinned at the top of its group's lanes, by `P` with the timeline focused or the timeline's own button, marked with a pin's head and kept across the session's publications (§6.2); since revision 388 an investigation keeps them for the session, in the order pinned (`workspace-v16`); since revision 389 the timeline draws only the rows in view, so a 200-lane group's frame costs what a group of a few lanes' does (§6.2); since revision 390 a search typed at a group marks the lanes it finds and scrolls its hit's into view (§6.2); since revision 405 the machine rung groups processes by executable or by the terminal session their records name, regrouping the overview it projected, each session named in `icat metric --group-by session`'s words (§6.3), which an investigation keeps for the session since revision 406 (`workspace-v17`); since revision 409 a timeline cell chosen by a click, a step or the interval table is explained beneath the time scope, and a click on a lane counted coarser than the view chooses its own cell (§6.8, R13), and since revision 411 E from it lists exactly its records, behind a filter chip (§6.4), which since revision 466 the inspector previews, listing the first of them beneath the time scope under a title for the cell, and since revision 468 opens one of them in E, selected there, and since revision 471 the graph highlights the relationships its records belong to, or the nodes of its processes, which since revision 473 the relationship and ranked tables mark too; since revision 472 `F6` and `Shift`+`F6` move the keyboard between the ranked table, graph, timeline and inspector, or the tables while shown, each where a person works it; since revision 474 a ranked row stays chosen when a brush or a ranking restates its rows, Enter on an interval's row lists that moment's records, and a rung with no rows gives the reason it has none the keyboard; since revision 475 `F1`, or the status strip's Keys button, lists every key the window takes, by where it acts; since revision 476 a chosen record's facts are a list the keyboard reads a fact at a time; since revision 477 the graph says the node its arrows move to, and the timeline the moment `[` and `]` step to; since revision 478 the investigation window's timeline says the column its keys move to, the window opens with the keyboard on its session and never leaves it on nothing, and `Ctrl`+`Tab` shows its pages, and since revision 479 `F1` lists its keys; since revision 480 every dialog opens with the keyboard on one of its own controls and closes on Esc, and Enter does a form dialog's action, and since revision 481 the windows a record opens give the keyboard to what they read, and since revision 482 the main window gives it to Stop until a capture's first view and to the ranked table once a session is shown, since revision 483 as the held-generation banner or an unfinished capture's card goes, and since revision 484 as any control goes from under it, a list's row restated under it giving it to the row chosen in that list since revision 485, and since revision 486 the capture card says aloud what an action did, never a recording's count or a package's percent, and since revision 487 each control is named in a few words, what it does its help text; since revision 416 InterCat's own processes are labelled wherever a process is described, since revision 417 one command sets them aside from the rows, the graph and the channels, counting what it set aside (§19.5), and since revision 419 an investigation keeps that choice (`workspace-v18`); since revision 420 a group past the 200-lane bound draws its pinned and busiest members in lanes of their own and folds the rest into one exact lane beneath them, from which a pin brings any of them out (§6.2); since revision 421 a Wall clock toggle reads every instant the view states on the wall clock the capture's machine read, the axis naming its day and offset (§6.2), which an investigation keeps since revision 422 (`workspace-v19`), and since revision 423 a time typed into the search is gone to, its cell made the analysis interval (§6.7), and a zoom draws every lane at its own resolution, a mechanism with nothing in view among them; since revision 426 a moment gone to gives the timeline the keyboard, which says its analysis interval; since revision 427 the interval table states a wall-clock window whole at the smallest window, and keys no mechanism for an interval with no record; since revision 429 a relationship table listing none says why in its place; since revision 431 the timeline's axis ticks round instants of the ladder between its ends, in session time or on the wall clock; since revision 432 export, a redacted report and a re-layout are offered only where they act, saying why not; since revision 485 a view paused while a capture recorded shows the session as saved once it ends, never saying recording continues | Resource topology once derived. L4 lanes beyond RPC calls (drawn since revision 181) and HTTP exchanges (since revision 290), and byte composition once IC-015 derives operations that carry a length. A live generation's levels (S4; a finished generation's top level is persisted since revision 163, its deeper levels since revision 456, and what their records bind to, which a brush counts, since revision 457, a group's lanes since revision 458, a process's direction rows since revision 459, a channel's ends since revision 460, and a level spanning its segments since revision 701, beneath whose finest tiles a focus at more columns than they are counts the segments' sections, its tiles' byte sums since revision 704, from which a zoom ranked by bytes adds up its lanes, and their byte tallies since revision 706, from which a brush ranked by bytes measures its processes and channel ends, and since revision 707 a group's lanes their bytes, and since revision 708 a process's direction rows theirs), and exact live cadence at 1M rows and beyond. A real screen-reader pass on Windows (the automation tree is audited headlessly since revision 131). |
| IC-018 query identity | Metrics identity frozen; CLI/Desktop export scopes share projection, and since revision 326 an evidence rung's pages and its export read its scope through one mapping, an RPC or HTTP key included; since revision 410 a timeline lane's narrowing to one mechanism, source direction or channel end too, with an identity of its own (§6.4); since revision 424 `icat evidence --from` places a moment by the reader the window's search places it with, and since revision 425 every `--interval` takes a time with its unit, as the window states one; since revision 699 every export names its generation's manifest digest, and a ranked export of the machine, the window's and `icat export`'s, the `icat metric` query whose groups its rows are - identity, canonical form and command - checked value for value over random machines, or why no request's groups are its rows; since revision 700 each evidence page states its manifest and query identity, `icat evidence` reads HTTP keys and a chosen evidence policy, and an evidence export names the `icat evidence` command that lists its records under that identity | None open in its acceptance (revision 700): exports match the command line's answers, the golden corpus holds, cursors are bound to identity, generation and manifest. Canonical forms for the window's graph projection, viewport and generation wait for a consumer (`query-identity-v1` §9). |
| §11.3 sharing | All three presets, CLI and Desktop: the metadata-only report (`intercat-share-report-v1`), the reopenable redacted session package (`redacted-session-v1`) and the unredacted original evidence package (`original-evidence-package-v1`); since revision 372 the report states its scope's coverage, mechanism by mechanism, in words a leak test pins (R21); since revision 397 a redacted package can hold an interval of its source with its processes' lifecycle records, its coverage stated for the interval only (`redacted-session-v1` §11), which the window offers from its time scope since revision 399 | A redacted package of a whole investigation, which is not defined: a package's pseudonyms hold only within it. |
| M3 IPC breadth and content | Exit gate met for its measured scope ([review](reviews/M3-exit-review.md), revision 252) | Pipe and shared-section topology unavailable (to M7/M9); RPC over TCP, HTTP/2, compressed responses and asynchronous WinINet unmeasured; timing profile unavailable. |
| M4 multi-machine | In progress: since revision 253 an investigation's workspace (ADR-038) names separately valid sessions by identity, one member per capture, each resolved against where it was last found with the reason, never writing to one; a live capture's host identity includes its installation's. Since revision 254 (`workspace-v2`, ADR-039) a person aligns members to one member's clock with stated bounds, and `icat workspace compare` orders two members' instants only beyond their combined uncertainty. Since revision 255 (ADR-040) a live capture records its clock against the wall clock and its boot, and since revision 256 (`workspace-v3`) a workspace aligns one boot's captures exactly and others through their recorded wall clocks; since revision 257 the Desktop shows an investigation in a window of its own that lists, relinks, adds and opens its sessions; since revision 258 (ADR-041) `icat workspace correlate` proposes candidate joins between captures, since revision 259 the investigation window aligns sessions and lists candidates, since revision 260 (`workspace-v4`) a person's decisions about candidates are kept revisions, flagged when the alignments change, since revision 261 the window draws the merged time, a lane per session, since revision 262 two captures of one host that ran at once are flagged, since revision 263 (ADR-042) an investigation is packaged with its sessions, since revision 264 (`workspace-v5`) two instants a person reads in both measure the clocks' rate, since revision 265 (`workspace-v6`) a session is aligned through another, since revision 266 (`workspace-v7`) a person confirms two host identities are one host, since revision 267 the window compares two instants, since revision 268 it zooms the merged time and opens a column's records, since revision 269 (`workspace-v8`) candidates mirror through known address translations, since revision 270 (`workspace-v9`) a person keeps notes, pinned on the merged time, since revision 271 (`workspace-v10`) saved views of it, since revision 276 a capture that ends before its last publication, as a killed broker's does, still names its boot and start's wall clock to align by, since revision 277 candidate joins and the merged time name the generation of each capture they read (I16), since revision 347 its window lists what it keeps of each session's view, as `icat workspace show` does, since revision 381 its merged time hatches, and says, where a session's capture saw nothing (R21), since revision 383 its candidate joins say what each compared capture covered of TCP and UDP, and since revision 384 `icat workspace timeline` draws its merged time as the window does (R18), and since revision 428 its axis ticks round instants of the 1–2–5 ladder, each named where it is drawn, and its words say times in the digits their span needs; since revision 430 its Align dialog opens on the alignment in force; since revision 496 a session it opens is shown in front of it, its window InterCat's peer | A known two-host exchange (needs a second host); alignment from shared markers, which waits on a marker both captures record (M7, M11). |
| M5 release | Open; since revision 391 `icat support` writes §20.6's support bundle, with nothing a record holds (`support-bundle-v1`, P16), and since revision 392 the window saves it for the session it shows. Since revision 393 a session's size, bytes per record and §12.1 tier, and a recording's time left under its limits, are stated in the window, `icat session` and `icat capture` (S5), and since revision 450 for a capture keeping a window from what the window holds, with when it begins releasing - the eviction point S5 asks for - and when a pin stops it. Since revision 395 `icat demo` writes the gate's demo investigation of two hosts, generated and saying so wherever it is read (`demo-investigation-v1`), and since revision 396 the window's Explore the demo makes it once beside the sessions folder and opens it. Since revisions 456 and 457 a finished session's zooms and brushes count from its persisted tiles, opening no segment, and a brushed ranking of 10M rows meets §12's 250 ms with room, at 9.5 ms, and at 15 ms since revision 460 counts a channel's ends from the same tallies (S3, S4) | Full scale/reliability/accessibility/installer/build matrix and release gates. |

## Recent slices

- **Revision 711 — a coverage pass over revisions 699 to 710; bytes past what a sum holds refused with why (R10, I6):**
  - Of the 2,993 lines revisions 699 to 710 added that a test can reach, 143 ran in none. Tests now run whole a tile
    of more than 64 byte tally entries, a live checkpoint that cannot be derived, a ranked export whose session cannot
    be read again and a catalogue of imports that cannot be read. Sizes adding up past what a sum holds ended a brush,
    a byte ranking and a finished session's checkpoint with an overflow, the window's read unhandled and icat with a
    stack trace, and wrapped a process's connections' totals small; one record in another unit made a checkpoint
    publish nothing. Every byte read now refuses them with a reason, as unreadable input that blames no index; a byte
    view sums bytes only; and a checkpoint publishes all the same, keeping no bytes (`segment-v1` §6, `tile-index-v1`,
    `overview-index-v1`).
  - Debug and Release each ran 2,842 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    thirty-five mutations were caught, eight once the tests were strengthened: a side's or a group's row sizes, or
    any two sizes, wrapped or overflowing as they are summed; the refusal left unmarked, or every malformed read
    taken for one; a tally summing seconds, or a tally's slot wrapped; the answer of a ranking, measured or kept, of
    an interval, of mechanism lanes, of owner lanes or of direction rows left unfitted; the tiles blamed for a
    refused ranking, owner lanes or mechanism lanes; a section keeping sums of another unit or past what a sum
    holds, or a spanning level keeping sums past it; a tile's tallies or sums not grown; a checkpoint not published
    once its bytes are refused, or staged of the generation asked for; an unreadable checkpoint, query or catalogue
    thrown rather than said; a metric's total, group or groups wrapped; connections' sizes or total wrapped; and an
    HTTP part's, requests' or responses' bytes wrapped.

- **Revision 710 — a session still being recorded reopens from its newest chunk (S1, S4):**
  - Each generation a live follow publishes - the window's, `icat capture`'s, `icat follow`'s - names a derivation
    checkpoint of the generation before it, staged beside its chunk (`derivation-checkpoint-v1` §2), and a reader
    extends it over that chunk wherever a derivation kept in memory would: at 4,000,000 records a cold open derives in
    0.2 s rather than 1.5 s and takes about 0.5 s whole. A writer removing a superseded manifest now removes the
    indexes only it named, so a checkpoint per publication leaves no orphans (store-v1 §9). A recording's own writer
    stages none.
  - Debug and Release each ran 2,826 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    fifteen mutations were caught, one once it was written to build: indexes taken by a compaction or a
    re-derivation, or twice; a file that is not an index, or one not completed, taken; the indexes staged dropped,
    or staged for the next generation; a superseded generation's indexes kept, or an index a kept generation names
    removed; a follow's indexer ignored; a checkpoint said to derive the generation it is named for, or named for
    another; none staged of a generation with segments; and the window's or icat follow's follow given no indexer.

- **Revision 709 — an import into a new folder reuses the folder the catalogue of imports lists (§18.4):**
  - `icat import` records every import it publishes or reuses in `import-catalogue-v1.json` in the user's InterCat
    folder, or the file `--catalogue` names. An import into a new folder looks there first, hashing the file only when
    an import of a file its length is listed, and reuses a listed folder once it holds this import and every file its
    generation names verifies, writing nothing into the new folder; `--again` imports it there all the same. Folders
    that no longer hold the import are forgotten, one that cannot be read is named and kept, and a file that is not a
    catalogue is named once and never replaced. `import-reuse-v1` names the folder `--into` named and the catalogue.
  - Debug and Release each ran 2,818 tests with revision 473's 93 failures and 4 skips, and failed nothing else; a
    first run found that the test of writers recording at once could start its reader after every writer had
    finished, so the writers now start once it has read. All twenty-five mutations were caught, one once it was
    written to build: a folder listed twice, folders said gone kept, a thousand and one entries kept, the oldest
    first, a file that is not a catalogue replaced, another contract's read, writers not locked, a file past the
    size bound read, a relative folder or a malformed session listed, a trailing separator kept, the length not
    compared; the catalogue looked in only with --again, or despite it; a reuse in place not recorded; gone folders
    not forgotten; another file's import verified, or the folder asked for; a folder that cannot be read forgotten;
    another session reused; the folder asked for reported as the session's; the file hashed with nothing of its
    length listed; a folder taken for the catalogue; a failed write stopping the import; and the note for a reuse
    elsewhere given as the one in place.

- **Revision 708 — a process's byte direction rows add up from the tile index (S3):**
  - Each byte tally entry keeps the direction its records' source states (`tile-index-v1` minor 7), at no cost in
    size, and a process's direction rows under a byte ranking add whole tiles from them, as a group's lanes do: at
    10,000,000 records the busiest process's rows in 2,000 columns take 312 ms the first time and 51 ms at the median
    after, from 1,817 and 82, opening no segment. A minor 6 index still serves brushes and group lanes; its direction
    rows read their rows until a checkpoint publishes it again.
  - Debug and Release each ran 2,796 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    eleven mutations were caught, one once a test asked a minor 6 file's sections for direction rows: a record's
    direction left out of its tile's byte tallies or written in the wrong bits; a minor 7 file read as minor 6, or a
    minor 6 file as minor 7; a minor 6 section adding direction rows; a direction row taking any process's records,
    or whatever the policy admits; a record's or a tally's direction taken as unknown; the direction rows left to
    their rows, or added from a minor 6 file.

- **Revision 707 — a group's byte lanes add up from the tile index (S3):**
  - Under a byte ranking a finished session's group lanes add each tile that falls in one column of the machine row's
    and one of the lanes' from its byte tallies, and read records only where a boundary falls among a tile's: a
    40-process group at 10,000,000 records in 2,000 columns takes 641 ms the first time and 68 ms at the median after,
    from 2,366 and 114, opening no segment. The scale gate measures it. The contract now says what the writer has done
    since revision 706: a tile keeps its byte tallies where they take at most a third of its records' bytes.
  - Debug and Release each ran 2,790 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    eleven mutations were caught, one once a test's interval ended among a tile's records: a lane's records taken
    whatever the policy admits, or placed in the machine row's column; a tile across a lane boundary added whole, or
    a record outside the range added; a record's lane column taken from the machine row's; a size declared and not
    recorded counted as measured; the machine row leaving out records no process owns; the index or the spanning
    level left unread; a section keeping no byte tallies left out rather than read; and an index stating another
    derivation added from.

- **Revision 706 — a brush ranked by bytes measures from the tile index (S3, S4):**
  - Each tile keeps its records' transport-observed bytes tallied by owner, channel and accounting side
    (`tile-index-v1` minor 6). A finished session's brush ranked by bytes adds a tile it holds whole from them, each
    process's and drawn channel end's under the evidence policy, and reads records only where one of its ends falls
    among a tile's: at 10,000,000 records 3.0 ms the first time and 1.9 to 12.5 ms after, from 1,336 ms and up to 513,
    opening no segment. A segment holding an untimed record, or, for a brush from the capture's epoch or before it, a
    record before the epoch, is measured from its rows. The index grows from 21.1 to 22.3 bytes a record; an index of
    minor 5 or earlier is read as before until a checkpoint publishes it again.
  - Debug and Release each ran 2,784 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    twenty mutations were caught, one once a damage case was added: byte tallies keeping no channel, none of a
    wider tile's children, or a size declared and not recorded as measured; an owner taken whatever the policy
    admits, or a channel's two ends swapped; a tile past the brush's end taken whole, or records outside it
    measured; the spanning level keeping byte tallies past its bound, dropping those its widening merges, or not
    adding two sections' measured counts; entries out of order, or a tile's beginning where the one before's does,
    let through; the index or the spanning level left unread; a segment holding an untimed row measured from its
    tiles, or the spanning level read beside it; and a segment holding a record before the epoch measured from its
    tiles, also by a brush starting at the epoch, one whose first record is at the epoch measured from its rows, and
    one the brush does not meet opened.

- **Revision 705 — an import run again reuses the session that already holds it (§18.4):**
  - `icat import <etl> --into <folder>` reads what the folder holds before compiling a schema or reading a record. A
    folder holding this file's import - the session whose identity the import derives, verified - is reused: the file
    is hashed and read no further, and nothing is written. Another session, an import of the same path from other
    bytes, a session that does not verify, staged files an unfinished publication left or a writer holds, and anything
    else are refused with what was found and what to do; a folder holding only lock files, as a confirmed cleanup of
    staged files leaves it, is new. `--json` writes `import-reuse-v1`.
  - Debug and Release each ran 2,768 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    twenty-eight mutations were caught: a folder of lock files refused, or a folder named like a lock taken for one;
    a file at the path, a session found only through its fallback pointer, staged files, a writer still staging or
    the bytes staged missed; an unreadable session let through; another identity taken for this import; this import
    reused without hashing its files, or without saying why it reads its fallback; ownership markers taken for
    strangers' files; the reuse skipped, or a new folder refused; an unreadable session's exit code, the hashing
    line, the observation count, a fallback's partial result, either layout note, or the two notes' options swapped;
    the words for an import of the same path, for a file at the path and for either unreadable session; a folder's
    listing past three names; the JSON's fallback reason; and a report written over one already there.

- **Revision 704 — a zoom ranked by bytes adds up its lanes from the tile index (S3, S4):**
  - Each tile keeps its records' transport-observed bytes summed by mechanism and accounting side, and each record its
    measure (`tile-index-v1` minor 5). A finished session's byte lanes in 2,000 columns take 4 to 11 ms at 1,000,000
    records, from 88 to 99, and 8 to 20 over 400 segments, from 141 to 406, opening no segment, and so do the interval
    table's bytes of every record or of one mechanism. The index grows from 17.5 to 21.1 bytes a record; an index of
    minor 3 or 4 is read as before until a checkpoint publishes it again.
  - Debug and Release each ran 2,754 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    nineteen mutations were caught, two once their tests were sharpened: a section never keeping bytes, or keeping
    them across two units; sums dropping a record's measured count or a wider tile's children; records keeping no
    measure; tallies weighed with the measures; a wider tile summed across a column boundary, or records outside a
    range added; byte sums out of order, a measure past 8 or links out of order let through; the spanning level
    adding no section's sums, dropping those its widening merges, or keeping a section's that keeps none; the index,
    the spanning level or an interval's sums left unread; one mechanism's interval summing every mechanism; and a
    size declared and not recorded counted as measured.

- **Revision 703 — a pinned node is laid out exactly where it was pinned (R12):**
  - The layout read a pin into its drawn units and back, so a node pinned at 0.95 of the pane came back at
    0.9499999999999998, and a graph motion test failed whenever the layout landed before it looked. A pinned node's
    place is now the pin itself.
  - Debug and Release each ran 2,739 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    graph motion test revision 702's run failed passed in both, and three times more alone. The one mutation was
    caught: a pin read back through drawn units.

- **Revision 702 — a reader meeting a session's pointer mid-publication acquires what it names (I15):**
  - Opening a session as its first generation published could find no pointer, then the one just written, and refuse a
    session that verifies; a window following a capture met it at its first step. A reader now reads the pointer again
    when what it names fails, and acquires the generation it names where a publication changed it or left it
    unreadable for an instant, up to three times, before falling back or refusing.
  - Debug and Release each ran 2,738 tests with revision 473's 93 failures and 4 skips. Release failed nothing else;
    Debug failed one more, a graph motion test whose pinned node was drawn at 0.9499999999999998 of the pane rather
    than 0.95 when the layout landed before its check: the layout reads a pin through drawn units and back, which
    revision 703 removes. All four mutations were caught: a pointer never read again, one read otherwise ignored,
    one there but unreadable ignored, and a fourth attempt allowed.

- **Revision 701 — a tile-index level spanning a generation's segments (S4):**
  - A finished generation of two or more segments keeps a level of tiles over all of them (tile index minor 4): a
    brush of a third of 1,000,000 records in 400 segments takes 5 to 7 ms instead of 152 to 195, and a whole session's
    focus at 256 columns about half what it took. A zoom, a brush and a focus count it first and the segments only
    beneath the ranges it leaves, exactly as their rows. Minor 3 is still read, and a checkpoint publishes it again.
  - Debug and Release each ran 2,737 tests with revision 473's 93 failures and 4 skips. Release failed nothing else;
    Debug failed two more, each passing three times alone: the journal allowance test that fails now and then under
    load, and a live view whose first step opened the evidence session as its first generation published, a race in
    opening a session that revision 702 removes. All twenty-two mutations were caught: a brush's range begun before
    its interval, a focus's ended a tick early, ranges joined across a tile counted whole or never joined; a
    section's extent, the top level's sum or a finest tile's link left unchecked; minor 3 taken as current, a level
    spanning another segment counted, one segment given a level; the records rule made coarser, widened tiles
    dropped, the bound ignored, widened tiles' record bytes or a merged entry's count dropped; a zoom's, an owner
    focus's or a channel's records outside a range counted; only the last section meeting a range read, the
    directory's trailer unread, the level left flat, and a section counting its whole interval beneath a range.

- **Revision 700 — an evidence export names the icat evidence command that lists its records; IC-018's acceptance met (R18, I16):**
  - icat evidence states each page's manifest and query identity, takes a process's HTTP exchanges' key and
    --evidence-policy, and an evidence export names the command that lists the same records under that identity, in
    JSON and a CSV's last column. Nine scopes round-trip through the command with the same identity and records.
  - Debug and Release each ran 2,718 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    fifteen mutations were caught: the policy unwritten, the RPC or HTTP key, owners, interval end, end or direction
    dropped from a command; HTTP keys refused, the policy ignored, the manifest, next page's policy or HTTP label
    left out by icat evidence; the JSON identity swapped, the CSV identity dropped, and the export's or the window's
    policy named wrong.

- **Revision 699 — a ranked export names the icat metric query its rows answer (IC-018, R18, I16):**
  - Every export, the window's and icat export's, names its generation's manifest digest; a ranked export of the
    machine names the request whose groups its rows are, its identity, canonical form and command, which run prints
    the same identity, and otherwise says why no query is named. A property test over random machines checks the rows
    against the answer value for value; it found icat metric's grouped error count list no group when no call carried
    a status, now fixed.
  - Debug and Release each ran 2,716 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    twenty mutations were caught: the bytes-sent side, the call time's interval, an answered call count, the
    unwritten policy, the interval's end, the kebab names, a group's, a set-aside view's or no session's rows named,
    any generation identified, the CSV identity, the JSON, CSV and icat export manifests, the page's digest, the
    grouped errors' groups, the window's unread folder, manifest and query, and icat export's printed identity.

- **Revision 698 — the plan's revision paragraphs move out of §7.4 into a revision log of their own:**
  - Some 1,400 lines of revision paragraphs had run on at the end of §7.4, among the correlation contracts: over half
    the plan. They moved whole to docs/design/INTERCAT-PLAN-REVISIONS.md, the full paragraphs in revision order, and
    the plan keeps a pointer where they were; later revisions are added at the log's end. A live pass over the
    investigation demo's opened programs, and the heavy machine's inspector lists at the smallest window, found
    nothing to fix.
  - Documents only: no source changed, so no test or mutation ran. The move was checked to carry each of the 715
    paragraphs into the log exactly once, and every one of the 209 index lines to point to where its revision's
    paragraph now is.

- **Revision 697 — Esc on the machine lets go of the ranked row with the rest of the choice (§6.7, R15):**
  - The ranked table showed the row still chosen while the inspector said nothing was. The row now goes with the
    choice, before a brush let go with it restates the rows, and the keyboard stays on it; a press on the graph's
    empty space still keeps the choice. The command line's live pass on the heavy machine found nothing to fix. Slices
    620–679 moved to a sixth historical log.
  - Debug and Release each ran 2,709 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    five mutations were caught: the row kept chosen, let go without the table hearing so, or let go only after the
    brush, whose restated rows chose it again; and the keyboard given to the first row, or the row let go not
    remembered.

- **Revision 696 — icat processes --program lists one program's instances (§7.4, R18):**
  - Named as icat programs lists it or by its executable's path; a Program field names it and counts its instances,
    and the document names it. With --port, those of them that accepted connections at that port, and where none did
    it says so by the program's name; beside --pid it is refused. The four commands that take --program now resolve
    and refuse it in one place.
  - Debug and Release each ran 2,707 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    nine mutations were caught: every instance kept, the Program field or the document's count and name lost or
    counting every instance, a program beside --pid taken, a blank or unknown name taken, the no-instance note
    naming no program, and a name two programs share taken as the first.

- **Revision 695 — a program's rung, with nothing chosen there, describes the program it opened (§6.3, R15):**
  - A live pass over the heavy machine found Enter on svchost.exe left the inspector saying "Nothing selected" over
    the processes it had just described. Its rung now names the program, says how its processes are drawn there, how
    they were grouped and the connections they made and accepted, and lists its pairs of programs, hosts and listening
    ports, as choosing it on the machine rung did. It is described, not chosen: nothing is highlighted for it, Esc
    still climbs back, and the ways back from its ports and pairs come back to it described.
  - Debug and Release each ran 2,706 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    eighteen mutations were caught: the title, the subtitle, the grouping, the connections, the pairs, the hosts and
    the ports each left to a chosen program alone, a pair's other end read from no chosen program, the pairs asked
    for whole, toggled or walked from none, the hints past the most listed naming no program, the machine's ports
    listed on any rung, the program described while a process is chosen, and the ways back from a port or a pair
    choosing the program at its own rung, or never choosing one at all.

- **Revision 694 — icat ports --program lists one program's listening ports (§7.4, R18):**
  - Named as icat programs lists it or by its executable's path, as icat hosts --program takes it; a Program field
    names it and counts its processes, and the document names it. A name two programs share is refused with how each
    is listed, and a program whose processes accepted nothing says so by its name. The window's hint for a program's
    list past its hundredth port now names that command, with the program's count rather than the machine's, quoted
    where a shell would split the name.
  - Debug and Release each ran 2,702 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    twelve mutations were caught: every listener kept, every program's processes taken, the Program field or the
    document's count and name unsaid, the no-port note naming the machine, a shared name taken as the first program,
    a blank or untrimmed name taken, and the window's hint without --program, unquoted, or counting the machine's
    ports.

- **Revision 693 — a chosen program's inspector lists its processes' listening ports (§6.3, R15):**
  - As the machine's list does with nothing chosen; Enter chooses a port's process and Esc comes back to the program
    on the port, before the walk of programs that reached it. The rows are built once from lookups, not a scan per end
    at every read, and the first choice made now takes the machine's list away. A window test's cleanup raced a
    candidate read still finishing after its window closed; it now waits for none, and temporary folders retry their
    removal.
  - Debug and Release each ran 2,701 tests with revision 473's 93 failures and 4 skips, and nothing else failed; the
    first run's one other failure, the investigation test's cleanup, ran clean six times alone and in the second
    run, its race being a timing one no mutation can force. All thirteen mutations were caught: no program's list, a
    program's listing the machine's ports, either asked for whole making both so, the heading and the list's name
    not saying whose, the way back forgetting its program, coming back to nothing or to a program gone, the list
    restated at every choice or never at the first, rows not rebuilt for candidates read or built again at every
    read, and Esc taking the walk of programs back before the port's way back.

- **Revision 692 — a session opened from its investigation names each listening port's candidate other ends (§6.3, §8.3):**
  - The window's counterpart of icat ports --investigation, read with the hosts' from one comparison: on the demo
    server, 443 names the client's browser.exe and 8443 its sync.exe, never as an established join, and the keyboard
    stays on its port as they arrive.
  - Debug and Release each ran 2,699 tests with revision 473's 93 failures and 4 skips, and nothing else failed; the
    demo server's ports name the client's browser.exe at 443 and sync.exe at 8443 once read with the demo
    investigation, none at 5432, and a later publication's take them over. All eight mutations were caught: the
    ports' candidates not read, not kept, their rows not restated, looked up at the wrong port, left out of a row's
    words or of what a screen reader hears, the note said where none is named, and the keyboard lost from its port
    as the rows are restated.

- **Revision 691 — live pass over the window's listening ports (§6.3):**
  - On the heavy machine at the default width, "PID" broke from its number beside the port column and who connected
    ran four lines a row; a row now keeps each PID to its number and says who connected beneath its port and process,
    across the row: two lines where it took four. Enter, Esc and the list asked for whole held.
  - Debug and Release each ran 2,697 tests with revision 473's 93 failures and 4 skips, and nothing else failed; the
    window test now holds who connected beneath the port and process, from the port's edge and below the process's
    last line, and the process's PID kept to its number. All three mutations were caught: the PID left to break from
    its number, who connected put back in the process's column, or beside the process rather than beneath it, which
    the first form of the test missed until it measured below the process's last line.

- **Revision 690 — with nothing chosen, the inspector lists the ports the machine's processes accepted connections at (§6.3, R15, R18):**
  - The window's counterpart of icat ports: each port in a column of its own beside the process that accepted there
    and who connected, the busiest five or a hundred on request; Enter or a double click chooses that process, the
    keyboard going to who connected, and Esc comes back to the port, in a newer generation too.
  - Debug and Release each ran 2,697 tests with revision 473's 93 failures and 4 skips, and nothing else failed;
    icat ports reads as before, its words now shared with the window. All twenty mutations were caught: a process
    connected to itself unsaid, three programs counted, programs ordered by name rather than connections, unobserved
    ends said where none were, the overview or the view filter keeping no port or InterCat's own, the list shown on
    another rung or with a process, a range or a set chosen, the toggle stuck, the rest said past five, a port
    chosen while hidden, Esc coming back across rungs, one kind of end counted, the way back lost to a newer
    generation, the list's visibility unsaid, and the keyboard sent to the hosts first, back to the first port, or
    off its port in a newer generation.

- **Revision 689 — a process's list of other hosts is headed by those that connected to it, where every one did (§6.3, R18):**
  - The demo server's files.exe and api.exe now list "Other hosts that connected to it", a program whose processes
    accepted every such connection "to its processes"; one that reached any, or sent a UDP flow, keeps "Other hosts it
    reached", in the inspector and icat processes --pid alike.
  - Debug and Release each ran 2,691 tests with revision 473's 93 failures and 4 skips, and nothing else failed; the
    demo server's api.exe and files.exe now head their other hosts "Other hosts that connected to it" in icat
    processes --pid, and the client's browser.exe and sync.exe "Other hosts it reached". All eight mutations were
    caught: no list headed by those that connected, one with none of them accepted or only some headed so, the
    window's or the command line's count of those accepted lost or its UDP flows left out, and a program's list
    headed as a process's.

- **Revision 688 — the graph's host nodes are captioned which way their connections went (§6.3, R18):**
  - Found by the window pass over the demo server: its Other hosts now reads "to 2 processes", both of which only
    accepted that host's connections; a host connected both ways reads "with" its processes, and one they only
    connected to "from" them, as before.
  - Debug and Release each ran 2,689 tests with revision 473's 93 failures and 4 skips, and nothing else failed; the
    three-host test now reads Other hosts captioned with its two processes, some of whose connections each end
    opened, and each host drawn on its own captioned with, to or from its process. All seven mutations were caught:
    no caption said to the processes, every one said from them or none with them, a host's count of those the hosts
    opened read from those the processes made, and its count of connections, or Other hosts' of either, lost.

- **Revision 687 — icat ports --investigation names who on another machine connected to each listener (§6.3, §8.3, R18):**
  - Port by port where icat hosts --investigation names candidates address by address: the demo server's 443 the
    client's browser.exe, its 8443 the client's sync.exe; - for a listener no other host connected to, none where no
    candidate mirrors; the JSON's candidateOtherEnds.
  - Debug and Release each ran 2,689 tests with revision 473's 93 failures and 4 skips, and nothing else failed; a
    new unit test names the demo server's listeners' candidate other ends port by port, none for the client, which
    accepted none, and none for a rejected join beside an accepted one, and a new CLI test reads them in icat ports'
    table and JSON, - and none where they apply, null without an investigation, and the refusals. All nine mutations
    were caught, one after it was rewritten to compile: a rejected join counted, a connection made counted as
    accepted, its remote port taken, the two ends swapped, an accepted join unsaid, the table's - or none unsaid,
    the JSON's ends looked up at another port, and a session no member of the investigation read anyway.

- **Revision 686 — a host's own lines say which way its connections went (§6.3, R18):**
  - Found by a window pass over the demo server after revision 685: a host that connected to the processes now reads
    "2 TCP connections to api.exe · PID 5100 and 1 more", one connected both ways "with" them, and one they connected
    to "from" them, in Other hosts' card and list and a host's own card.
  - Debug and Release each ran 2,687 tests with revision 473's 93 failures and 4 skips, and nothing else failed; a
    new test lists three hosts in Other hosts' list and card, and draws each on its own, one the processes connected
    to, one that connected to them and one both ways, and the wording test says a host's connections from, to and
    with its processes, beside UDP flows too. All nine mutations were caught: a host's connections never said to its
    processes, said to them beside UDP flows or always from them, one transport's said from where to, with where
    from or with where UDP, and the host's words, Other hosts' rows or a host's card left saying from.

- **Revision 685 — other hosts' words say which way the connections went (§6.3, §6.8, R18):**
  - To the hosts where none was seen accepted, from them where every one was - the demo server's "2 TCP connections
    from 1 host to 2 processes", which said "to 1 host" - and with them, or between processes and hosts, where both;
    UDP flows stay with their hosts. Found by a live pass over the demo after icat ports.
  - Debug and Release each ran 2,686 tests with revision 473's 93 failures and 4 skips, and nothing else failed; the
    wording test now says one process's and several processes' TCP connections to, from and with other hosts, and
    UDP flows with them, and the overview's, an aggregate's process rows' and a brushed card's tests read the
    accepted connections from their host and a process's both ways with its hosts. All eight mutations were caught:
    every connection said to the hosts, none said from them, UDP flows given a way, one process's accepted or mixed
    connections said to its hosts, several processes' from the hosts said from the processes or between them unsaid,
    and several processes' UDP flows said between them.

- **Revision 684 — icat ports lists the ports a session's processes accepted connections at, and who connected (§6.3, §7.4, R18):**
  - Each port with the process that accepted connections there, how many, and who connected - processes of the
    capture, other hosts by address, or unobserved processes on this machine - the busiest first, with --limit and
    --json (listening-ports-v1); refused where no open is recorded. icat overview's busiest processes and icat
    processes --port point to it.
  - Debug and Release each ran 2,686 tests with revision 473's 93 failures and 4 skips, and nothing else failed; two
    new tests list a session's listeners - a port two processes listened at, connections from processes of the
    capture, from other hosts and from unobserved processes over IPv4's and IPv6's loopback, an IPv4-mapped address
    and the listener's own, ranked, limited and as JSON - and say why none is listed where every open was made or no
    open is recorded, and the overview's and icat processes --port's tests read their pointers to it. All thirty-two
    mutations were caught: the accepting end or the connecting one swapped, a one-sided connect counted, read at its
    remote port or with its ends swapped, the ranking or its ties reversed, an unobserved process at the listener's
    own address, at a mapped loopback address or at loopback counted as a host, the connectors', hosts' and
    programs' orders or ties reversed, connections counted by process or by host, the refusal dropped, ports,
    processes or connections counted as listeners, the table's or the JSON's limit ignored, none listed as an empty
    table, the more line said wrongly, itself unsaid, one process or host counted rather than named, unobserved ones
    unsaid, and three programs counted or four named.

- **Revision 683 — the command line says at which port relationships' connections arrived (§6.3, §6.8, R18):**
  - icat overview's busiest relationships and pairs of programs and icat programs say "TCP at 9222", a pair's ports
    together; icat processes --pid says each end's connections at the ports they arrived at, "1 made to 135", "1
    accepted at 9223", in its JSON as madeToPorts and acceptedPorts; icat programs --json lists a pair's
    acceptedPorts, and a chosen program's pairs in the inspector say what they ran over.
  - Debug and Release each ran 2,684 tests with revision 473's 93 failures and 4 skips, and nothing else failed;
    icat processes --pid's test now reads each end's connections at the ports they arrived at in its table and JSON,
    null where no open is recorded, icat overview's and icat programs' tests read a relationship's and a pair's
    ports, a new test lists a pair seen opening none without them, and a pair's words are tested with one port,
    several and a mechanism after them. All thirty mutations were caught: an end's ports swapped, dropped or left
    out of its JSON, its connections made counted as accepted, a one-sided connection's port read at its own end or
    dropped, a pair's ports dropped, unsaid or said for UDP, the comma after a list of them misplaced, said alone or
    without its "and", the overview's, icat programs' and icat processes' cells and notes without them or saying
    them where none are said, the JSON's ports or their null, and the inspector's pairs without what they ran over.

- **Revision 682 — a relationship says at which port its connections arrived (§6.3, §6.8, §7.4):**
  - From the same openings its made counts read, at the end that accepted each: the relationship table and a chosen
    relationship say "TCP at 8080", its edge's card begins so, and a chosen process's relationships say "2 accepted ·
    TCP at 8080 · 8 records". The command line's tables are next.
  - Debug and Release each ran 2,683 tests with revision 473's 93 failures and 4 skips, and nothing else failed; the
    chosen relationship's test now reads where its connections arrived in its row, subtitle and edge card for a
    one-way, a both-ways and an unopened relationship, and the inspector's relationship rows say it in the view
    model and the window, spoken too. All nine mutations were caught: a relationship's ports dropped, counted for a
    process connected to itself, repeated or reversed, read at the connecting end, and left out of the peers' words,
    the table's cell, the edge's card or the peer rows.

- **Revision 681 — live pass over listening ports and the port search; a channel's hit names who connected to whom (§6.7, §6.3):**
  - The ports and the port search held on the heavy machine; a channel's hit, titled "Channel", is now titled by the
    processes at its ends, the maker first - "chrome.exe · PID 3028 → chrome.exe · PID 3000" - with its transport,
    never its endpoint, and a hit's name wraps within the rail rather than being cut.
  - Debug and Release each ran 2,683 tests with revision 473's 93 failures and 4 skips, and nothing else failed; a
    new search test names a connection's hit by its maker and the process it connected to, and one seen opening
    neither way by its two joined by "and", neither repeating the endpoint searched, and a new window test reads the
    hit's whole name wrapping within the rail at the smallest window. All seven mutations were caught: the ends
    named in their name's order, never by an arrow or never swapped to the maker, the hit titled "Channel" again, a
    held channel named by its relationship, its transport left out of its detail, and its name cut at the rail's
    edge.

- **Revision 680 — `icat processes --port` lists the instances that accepted connections at a port of their own (§5.2, §6.7, R18):**
  - Whoever connected, as the window's port search finds them, ranked by connections accepted unless `--rank-by` names
    another measure; a port none accepted at points to `icat hosts --address`, and the JSON names the port.
  - Debug and Release each ran 2,681 tests with revision 473's 93 failures and 4 skips, and nothing else failed; a
    new test lists the server by 8080 and the self-connected process by :52001, none and where to read them by a
    port only connected to, the JSON's port, its ranking and null without one, refuses four values that name no port
    and the option beside --pid, and says a session recording no open cannot tell. All ten mutations were caught:
    every instance listed, the JSON's port unnamed, the list ranked by bytes, a colon refused, port 0 accepted, the
    option allowed beside --pid or where no open is recorded, the Accepted at line, the empty list's words and the
    help's line left out.

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
   - A live session still counts its overview from tiles until its writer finishes; since revision 710 it derives its
     processes, relations and counts from the checkpoint its generation names, reading only the newest chunk.
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
     ranked rows, channel rows and graph - reads no segment. Since revision 704 a finished session's zoomed byte lanes,
     and an interval's bytes of every record or one mechanism's, add up from its tile index's byte sums (`tile-index-v1`
     minor 5), opening no segment, and since revision 706 a brushed interval's bytes by process and channel end from
     its byte tallies (minor 6), since revision 707 a group's byte lanes, and since revision 708 a process's byte
     direction rows (minor 7); a live session's are still read when asked for. Revision 206 reads the interval table's bytes when it is shown. Each
     metric is listed under its basis, which stays beside the selector, since revision 201.
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
