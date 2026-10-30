# InterCat implementation status

Updated: 2026-10-30 · Plan revision: 835 · Branch: `main`

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
253), and M12 with `icat compare` (revision 752), the window's Compare with… (revision 753), a comparison shared as a
redacted report (revision 754), rows by executable (revision 755), two captures compared (revision 756), both in the
window (revision 757) and comparisons saved in an investigation, on the command line (revision 758) and in the window
(revision 759), with notes about them, on the command line (revision 771) and in the window (revision 772), and a
row's difference timeline, stated (revision 760) and drawn in the window (revision 761), a comparison shown on the
graph (revision 762), recurring communication patterns, on the command line (revision 763) and in the window (revision
764), over an interval of a session too (revision 795), and causal navigation over the links the evidence records, on
the command line (revision 765) and in the window (revision 766), over an interval too (revision 797), the time spent
inside calls, on the command line (revision 767) and in the window (revision 769), over an interval too (revision
796), and outliers against a baseline's windows, on the command line (revision 768) and in the window (revision 770);
M12's exit gate is met ([the M12 exit review](reviews/M12-release-review.md), revision 773). M8 began with HTTP/1.1
heads decoded from WinINet's whole parts (revision 774), since its validated content source exists here while M6, M7
and M9 need Windows sources, and its exit gate is met for HTTP through WinINet ([the M8 exit
review](reviews/M8-release-review.md), revision 786); M4 and M5 are not complete. All three of §11.3's sharing presets
exist: a metadata-only **report**, a reopenable redacted **session package**, and an exact, unredacted **original
evidence package**. The communication graph is a bounded §6.3 projection with a relationship-first §19.4 layout,
qualified on two real sessions, one sparse and one dense. Since revision 545 it relates processes by paired UDP flows
beside TCP connections, a pair's edges side by side. Processes with no relationship are counted in one parked node,
since revision 546 the less busy of those that only reach other hosts in another, past the eight busiest - since
revision 593 the eight busiest programs, several processes of one drawn as its node - and groups collapse only under
budget pressure. Since revision 550 the Other hosts node names the busiest addresses the drawn processes' connections
ran to, and since revision 555 Enter on it draws them on their own; since revision 564 a host chosen there, and since
revision 565 one chosen in the list it gives the inspector, is the records of every connection to it, which the
timeline highlights and E lists, as `icat evidence --host` reads them since revision 563. Since revision 570 a member
of an investigation names each host's candidate other ends in another member's capture, never as an established join,
and since revision 571 a session opened from its investigation names them in the window. Hubs draw as stars with
components apart, executable groups read as file names, and positions survive descents and live publications. A
bounded metadata search now finds groups, process instances and channels by name, PID or endpoint, and since revision
551 processes by an address they reached, which since revision 553 the graph marks; endpoint strings and addresses can
match but are omitted from result snippets. Graph marks now explain their scope and evidence on hover; nodes can be
dragged into pinned positions or pinned from the keyboard, and an explicit re-layout preserves those user constraints.
Below the machine rung the graph draws only the rung's neighbourhood, and one **Rest of the machine** node counts
every other process. Double-clicking a relationship's edge, or Enter on its row in the relationship table, opens its
channel, or an RPC relationship's linked calls. The timeline draws in colour the records E would list for the rung,
over every record in grey. A timeline cell explains itself on hover, as graph marks do, and once chosen in the
inspector, which lists its first records; since revision 471 the graph highlights what they belong to. Since revision
493 the graph's nodes travel to new places and a new selection's ring settles onto its node, and since revision 494 a
timeline coming back to the newest generation is carried there, unless the platform reduces motion.

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
| IC-017 Desktop projection | Real overview, channel/evidence ladder, bounded metadata search, layout scheduling, live follow, interval/zoom/minimap with wheel and keyboard, exact L0 mechanism lanes, L1 process-owner lanes, L2 source-direction rows and L3 channel-end lanes banded by direction, with shared scale, own coverage, hover/time selection, persistent table/step focus and keyboard/wheel scrolling, exact bounded query data carried through live publications, the visible range as the default scope with a scope lock, and a bounded §6.3 graph with relationship-first layout, semantic hover, manual pinning/re-layout, quiet folding, minimal group collapse, table-shared selection, anchored carried layout, per-rung neighbourhoods with a context node, §6.7's edge double-click and back/forward history that restores each rung's interval, a per-rung timeline focus that counts what E reads, a selection highlighted in the timeline by its own exact count (§6.4) and a Ctrl+click multi-selection that Enter turns into a filter (§6.7), a labelled live edge that previews unpublished records within §12's steady-state budget (P26 asserted), a designed waiting state before a capture's first publication, a launch-time offer to finish a session a crashed viewer left, and the saved sessions listed while none is open; since revision 189 the machine and group rungs rank by records or by bytes sent or received (§6.1's metric selector), since revision 190 by RPC calls made or served, since revision 196 by bytes sent and received and by RPC errors, since revision 199 by median RPC call and serve time, and since revision 200 by peers, each listed by its basis since revision 201, and read per second over the ranked interval since revision 209; a large group keeps its process lanes when zoomed, counted coarser, since revision 210; the machine rung's lanes plot what a byte ranking measures since revision 284, a group's process lanes since revision 285, and a process's direction rows since revision 286; since revision 287 every chosen state (a selected row, a toggle or box that is on, a tab, a focused field, a combo box's list) is drawn from the tokens in every mode, never in the platform's accent colour; since revision 323 each relationship names the rule and version that derived it, and a TCP one the key of each connection it rests on, in the table's evidence tooltip, its screen-reader name and the edge's card (R4), and since revision 324 an RPC one its own key, which opens the calls its links join; since revision 325 the relationship chosen in the table is explained beneath it, for a keyboard as for a pointer, and since revision 327 Enter or a double click on it opens it as a double click on its edge does, an RPC one at the calls its links join; since revision 329 a click on an edge chooses its relationship, which its halo, the inspector and the timeline's highlight then show, and since revision 330 the inspector's evidence card and E follow it too, at every rung since revision 345; since revision 335 a selected process states the rule that bound its records and how strongly, a reused PID's later holder with how many the evidence policy left out (§6.8), since revision 336 a chosen paired channel the rule that paired its ends, whether the capture saw it open and close, and its key, since revision 337 a chosen group how it was formed and what its total leaves out, and since revision 338 a reused PID's candidates are counted one action from the explanation that names them, every read following, with the rail saying so until one click returns to correlated evidence, and since revision 339 a session opened from an investigation keeps that choice there with its pins and ranking (`workspace-v13`); since revision 340 a reused PID's holders are numbered wherever a process is named ("PID 100 #2"), as `icat processes` numbers them, and since revision 341 an edge's card names each process end by name and PID, as the relationship table does; since revision 342 the legend keys the lanes' heights and offers each lane its own scale (§6.2's normalization scope), which an investigation keeps since revision 343 (`workspace-v14`); since revision 344 either main pane fills the column by an explicit command (§6.1); since revision 348 a row chosen among a process's rows is what the evidence card counts, the timeline highlights and E lists; since revision 350 the timeline states its time base, session time since the capture's recorded wall-clock start; since revision 352 Esc cancels a drag in progress before it ascends; since revision 353 the card, with nothing selected, names the records E lists; since revision 354 a reused PID's later holder's empty rung says its records were left out as candidates and offers to count them there; since revision 355 a channel's own rung counts the channel on the card, as E lists it; since revision 356 an aggregate chosen in the graph is what the card counts and E lists, its processes' records; since revision 358 a double click follows only a plain click, so a press where a pan, brush or cancelled drag began zooms or opens nothing; since revision 359 E from an aggregate names it; since revision 360 an investigation keeps the window's panes, the split and the pane filling the column, for every session opened from it (`workspace-v15`); since revision 362 the inspector states beneath its time scope what the capture covered there, mechanism by mechanism (R21); since revision 367 an export, the window's and `icat export`'s, states the same of its scope (R21, R18), and since revision 407 how its processes were grouped, `icat export --group-by session` reaching a session's rung as the window does (R18); since revision 368 a record without a size says why in the rail and the inspector (R3, R5); since revision 370 a channel's own rung names the channel in the inspector, as its card counts it (I5); since revision 371 a selected record states its own coverage (R21); since revision 372 an export's CSV, on every line, and the redacted sharing report state their scope's coverage too (R21); since revision 373 the channel browser says what the capture covered of TCP, announced as the reason an empty list holds none (R21); since revision 375 `icat overview` states the coverage the inspector states beneath its time scope (R18); since revision 382 a one-sided connection's bytes, on its rung and in `icat channels`, and a selection's in the inspector say a direction no transfer measured as unmeasured and one with no record as such, never 0 B (R21); since revision 385 `icat processes` explains each instance's binding in the inspector's words, and what the evidence policy left out of a reused PID's later holder (R18, R21); since revision 386 `icat channels` says how each channel was paired, as the inspector does (R18); since revision 387 a process's lane can be pinned at the top of its group's lanes, by `P` with the timeline focused or the timeline's own button, marked with a pin's head and kept across the session's publications (§6.2); since revision 388 an investigation keeps them for the session, in the order pinned (`workspace-v16`); since revision 389 the timeline draws only the rows in view, so a 200-lane group's frame costs what a group of a few lanes' does (§6.2); since revision 390 a search typed at a group marks the lanes it finds and scrolls its hit's into view (§6.2); since revision 405 the machine rung groups processes by executable or by the terminal session their records name, regrouping the overview it projected, each session named in `icat metric --group-by session`'s words (§6.3), which an investigation keeps for the session since revision 406 (`workspace-v17`); since revision 409 a timeline cell chosen by a click, a step or the interval table is explained beneath the time scope, and a click on a lane counted coarser than the view chooses its own cell (§6.8, R13), and since revision 411 E from it lists exactly its records, behind a filter chip (§6.4), which since revision 466 the inspector previews, listing the first of them beneath the time scope under a title for the cell, and since revision 468 opens one of them in E, selected there, and since revision 471 the graph highlights the relationships its records belong to, or the nodes of its processes, which since revision 473 the relationship and ranked tables mark too; since revision 472 `F6` and `Shift`+`F6` move the keyboard between the ranked table, graph, timeline and inspector, or the tables while shown, each where a person works it; since revision 474 a ranked row stays chosen when a brush or a ranking restates its rows, Enter on an interval's row lists that moment's records, and a rung with no rows gives the reason it has none the keyboard; since revision 475 `F1`, or the status strip's Keys button, lists every key the window takes, by where it acts; since revision 476 a chosen record's facts are a list the keyboard reads a fact at a time; since revision 477 the graph says the node its arrows move to, and the timeline the moment `[` and `]` step to; since revision 478 the investigation window's timeline says the column its keys move to, the window opens with the keyboard on its session and never leaves it on nothing, and `Ctrl`+`Tab` shows its pages, and since revision 479 `F1` lists its keys; since revision 480 every dialog opens with the keyboard on one of its own controls and closes on Esc, and Enter does a form dialog's action, and since revision 481 the windows a record opens give the keyboard to what they read, and since revision 482 the main window gives it to Stop until a capture's first view and to the ranked table once a session is shown, since revision 483 as the held-generation banner or an unfinished capture's card goes, and since revision 484 as any control goes from under it, a list's row restated under it giving it to the row chosen in that list since revision 485, and since revision 486 the capture card says aloud what an action did, never a recording's count or a package's percent, and since revision 487 each control is named in a few words, what it does its help text; since revision 416 InterCat's own processes are labelled wherever a process is described, since revision 417 one command sets them aside from the rows, the graph and the channels, counting what it set aside (§19.5), and since revision 419 an investigation keeps that choice (`workspace-v18`); since revision 420 a group past the 200-lane bound draws its pinned and busiest members in lanes of their own and folds the rest into one exact lane beneath them, from which a pin brings any of them out (§6.2); since revision 421 a Wall clock toggle reads every instant the view states on the wall clock the capture's machine read, the axis naming its day and offset (§6.2), which an investigation keeps since revision 422 (`workspace-v19`), and since revision 423 a time typed into the search is gone to, its cell made the analysis interval (§6.7), and a zoom draws every lane at its own resolution, a mechanism with nothing in view among them; since revision 426 a moment gone to gives the timeline the keyboard, which says its analysis interval; since revision 427 the interval table states a wall-clock window whole at the smallest window, and keys no mechanism for an interval with no record; since revision 429 a relationship table listing none says why in its place; since revision 431 the timeline's axis ticks round instants of the ladder between its ends, in session time or on the wall clock; since revision 432 export, a redacted report and a re-layout are offered only where they act, saying why not; since revision 485 a view paused while a capture recorded shows the session as saved once it ends, never saying recording continues; since revision 741 a session's first view does not wait for its RPC calls to be followed, saying they are until their edges join the graph; since revision 743 a session opened while another capture records it follows that capture's publications, saying so, until its recording ends; since revision 744 a live session's owner focus and byte lanes count every chunk but the newest from its tiles, where its instances bind the chunk's records alike; since revision 745 its brushes too, where its relations also hold each end's records the chunk's tiles name in one incarnation, and since revision 746 its channel focuses | Resource topology once derived. L4 lanes beyond RPC calls (drawn since revision 181) and HTTP exchanges (since revision 290), and byte composition once IC-015 derives operations that carry a length. The pyramid's open measured at T3, where a live session counts one section for each of its compacted segments (S4; a finished generation persists its top level since revision 163 and its deeper levels since revision 456, with what their records bind to, their spanning level and their byte sums and tallies, and since revisions 739 to 746 a live one counts every chunk but the newest from that chunk's tiles), and exact live cadence at 1M rows and beyond. A real screen-reader pass on Windows (the automation tree is audited headlessly since revision 131). |
| IC-018 query identity | Metrics identity frozen; CLI/Desktop export scopes share projection, and since revision 326 an evidence rung's pages and its export read its scope through one mapping, an RPC or HTTP key included; since revision 410 a timeline lane's narrowing to one mechanism, source direction or channel end too, with an identity of its own (§6.4); since revision 424 `icat evidence --from` places a moment by the reader the window's search places it with, and since revision 425 every `--interval` takes a time with its unit, as the window states one; since revision 699 every export names its generation's manifest digest, and a ranked export of the machine, the window's and `icat export`'s, the `icat metric` query whose groups its rows are - identity, canonical form and command - checked value for value over random machines, or why no request's groups are its rows; since revision 700 each evidence page states its manifest and query identity, `icat evidence` reads HTTP keys and a chosen evidence policy, and an evidence export names the `icat evidence` command that lists its records under that identity | None open in its acceptance (revision 700): exports match the command line's answers, the golden corpus holds, cursors are bound to identity, generation and manifest. Canonical forms for the window's graph projection, viewport and generation wait for a consumer (`query-identity-v1` §9). |
| §11.3 sharing | All three presets, CLI and Desktop: the metadata-only report (`intercat-share-report-v1`), the reopenable redacted session package (`redacted-session-v1`) and the unredacted original evidence package (`original-evidence-package-v1`); since revision 372 the report states its scope's coverage, mechanism by mechanism, in words a leak test pins (R21); since revision 397 a redacted package can hold an interval of its source with its processes' lifecycle records, its coverage stated for the interval only (`redacted-session-v1` §11), which the window offers from its time scope since revision 399 | A redacted package of a whole investigation, which is not defined: a package's pseudonyms hold only within it. |
| M3 IPC breadth and content | Exit gate met for its measured scope ([review](reviews/M3-exit-review.md), revision 252) | Pipe and shared-section topology unavailable (to M7/M9); RPC over TCP, HTTP/2, compressed responses and asynchronous WinINet unmeasured; timing profile unavailable. |
| M4 multi-machine | In progress: since revision 253 an investigation's workspace (ADR-038) names separately valid sessions by identity, one member per capture, each resolved against where it was last found with the reason, never writing to one; a live capture's host identity includes its installation's. Since revision 254 (`workspace-v2`, ADR-039) a person aligns members to one member's clock with stated bounds, and `icat workspace compare` orders two members' instants only beyond their combined uncertainty. Since revision 255 (ADR-040) a live capture records its clock against the wall clock and its boot, and since revision 256 (`workspace-v3`) a workspace aligns one boot's captures exactly and others through their recorded wall clocks; since revision 257 the Desktop shows an investigation in a window of its own that lists, relinks, adds and opens its sessions; since revision 258 (ADR-041) `icat workspace correlate` proposes candidate joins between captures, since revision 259 the investigation window aligns sessions and lists candidates, since revision 260 (`workspace-v4`) a person's decisions about candidates are kept revisions, flagged when the alignments change, since revision 261 the window draws the merged time, a lane per session, since revision 262 two captures of one host that ran at once are flagged, since revision 263 (ADR-042) an investigation is packaged with its sessions, since revision 264 (`workspace-v5`) two instants a person reads in both measure the clocks' rate, since revision 265 (`workspace-v6`) a session is aligned through another, since revision 266 (`workspace-v7`) a person confirms two host identities are one host, since revision 267 the window compares two instants, since revision 268 it zooms the merged time and opens a column's records, since revision 269 (`workspace-v8`) candidates mirror through known address translations, since revision 270 (`workspace-v9`) a person keeps notes, pinned on the merged time, since revision 271 (`workspace-v10`) saved views of it, since revision 276 a capture that ends before its last publication, as a killed broker's does, still names its boot and start's wall clock to align by, since revision 277 candidate joins and the merged time name the generation of each capture they read (I16), since revision 347 its window lists what it keeps of each session's view, as `icat workspace show` does, since revision 381 its merged time hatches, and says, where a session's capture saw nothing (R21), since revision 383 its candidate joins say what each compared capture covered of TCP and UDP, and since revision 384 `icat workspace timeline` draws its merged time as the window does (R18), and since revision 428 its axis ticks round instants of the 1–2–5 ladder, each named where it is drawn, and its words say times in the digits their span needs; since revision 430 its Align dialog opens on the alignment in force; since revision 496 a session it opens is shown in front of it, its window InterCat's peer | A known two-host exchange (needs a second host); alignment from shared markers, which waits on a marker both captures record (M7, M11). |
| M5 release | Open; since revision 391 `icat support` writes §20.6's support bundle, with nothing a record holds (`support-bundle-v1`, P16), and since revision 392 the window saves it for the session it shows. Since revision 393 a session's size, bytes per record and §12.1 tier, and a recording's time left under its limits, are stated in the window, `icat session` and `icat capture` (S5), and since revision 450 for a capture keeping a window from what the window holds, with when it begins releasing - the eviction point S5 asks for - and when a pin stops it. Since revision 395 `icat demo` writes the gate's demo investigation of two hosts, generated and saying so wherever it is read (`demo-investigation-v1`), and since revision 396 the window's Explore the demo makes it once beside the sessions folder and opens it. Since revisions 456 and 457 a finished session's zooms and brushes count from its persisted tiles, opening no segment, and a brushed ranking of 10M rows meets §12's 250 ms with room, at 9.5 ms, and at 15 ms since revision 460 counts a channel's ends from the same tallies (S3, S4). Since revision 787 the gate's content-export review ([review](reviews/M5-content-export-review.md)) lists every path by which kept content leaves InterCat, a test keeping it whole. Since revision 830 an error nothing mapped (§20.6 `Internal`) is contained where the process runs: `icat` says it and exits 70, and the window says it where the person is looking and keeps running, and since revision 831 counts each one it contained in its support bundle, by kind and where thrown | Full scale/reliability/accessibility/installer/build matrix and release gates. |
| M8 reconstruction and decoders | Exit gate met for HTTP through WinINet ([review](reviews/M8-release-review.md), revision 786): a decoder contract (`decoders-v1`, ADR-070) holding the fixture decoder and an HTTP/1.1 head decoder; `icat content --part --decode` decodes a whole request or response head kept with consent into its start line and field lines, each with its bytes, stopping where HTTP/1.1 does not hold, and `icat content --decoders` lists the decoders; since revision 775 the content viewer's Decode its part does the same, and since revision 776 `icat exchanges --decode` lists each exchange's request, host and status from its whole heads, and since revision 777 C on an exchange opens its conversation, its heads decoded when asked, and since revision 778 each body is set beside the length its head declares, and since revision 779 `icat content --find` searches kept content within its budgets, as the window's Find in kept content… does since revision 780 (each keeping to a process by its instance since revision 788, and finding a text two buffers of one HTTP part split since revision 789, and three or more since revision 800, the window's stopped when asked, keeping what it found, since revision 790, and what either lists written to a table when asked since revision 792, the window's read on from where it ended since revision 793, the command line's within budgets of its own since revision 794, and each counting the records the capture kept no byte of since revision 798), and since revision 781 a buffer's viewer opens its exchange's conversation, and since revision 782 each decoder's corpus pins what its version decodes, and since revision 783 `icat exchanges --decode` writes the listed exchanges' heads when asked, as JSON or a table, saying it holds content, as the window's Export with their heads… does since revision 784, and since revision 785 a decoder that fails says so, stopping nothing, and since revision 791 the content viewer names InterCat's decoders where none reads a record | None in its exit gate. Bodies' content, other mechanisms' reconstruction and a decoder worker wait on the sources and decoders M6, M7 and M9 bring (the review's limits). |
| M12 comparative | Exit gate met ([review](reviews/M12-release-review.md), revision 773): `icat compare` and Compare with… compare two intervals or two captures by relationship or by executable, by records or bytes sent across, stating a change only where both measured alike (revisions 752 to 757), share as a redacted report (754), save comparisons and notes about them in an investigation (758, 759, 771, 772), state and draw a row's difference timeline (760, 761) and mark the graph (762); `icat patterns`, `icat links`, `icat waits` and `icat outliers`, and the window's Recurring patterns…, Follow its links…, Time inside calls… and Outliers…, give recurring patterns (763, 764), over an interval of a session too (795), causal navigation over evidence-backed links (765, 766), over an interval too (797), the time inside calls (767, 769), over an interval too (796), and outliers against a baseline's windows (768, 770) | Outliers judge records alone, against one session's windows; patterns, links and waits read a whole generation or one interval of it, never two captures, all over TCP, UDP, RPC and process creation alone; only a comparison shares as a redacted report of its own. |

## Recent slices

- **Revision 835 — the plan lays out the solution as it is, held to it by a test (§26.1, R19):**
  - §26.1's layout named two tools that never existed and none of the six that do, put the benchmarks in a `bench/`
    folder that holds only their results, and left out the broker's protocol project and three test projects, `icat`'s
    own among them. It now lays out all 33 projects where they are, each with what it holds, and an architecture test
    holds the layout to the solution's projects, so one added, moved or removed without it fails.
  - Debug and Release each ran 3,441 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the new R19 test. Both mutations were caught: a test project dropped from the layout, and a
    project named as the solution does not name it.

- **Revision 834 — the plan lists every command, and a folder named twice is added once (§20.4, §8.3):**
  - §20.4 still showed revision 106's twenty commands under a sentence saying `icat help` lists every command, and
    still called `icat workspace create` a proposal; Appendix B queried the superseded `capture.icat` file. The plan
    now lists all 40 commands by what each is for, a test holding the listing to the commands `icat` dispatches, says
    which of its proposals shipped, and queries a session's folder.
  - `icat workspace add` given one folder twice added it, refused it as a member the second time, and answered 1, a
    partial result, though everything asked was done. A folder named twice, with a separator after it or without, is
    now added once and said to be named twice.
  - Revision 833's note overstated one thing: `icat retain --release-before` already waited for a session's capture to
    finish, so the releases it newly refuses beneath a running follow are of kept content and of a journal prefix.
  - Debug and Release each ran 3,440 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    5 mutations were caught: a folder named twice tried again, or told apart by a separator after it; named twice
    and not said to be; the exit code counting the operands as given; and a command dropped from the plan's listing.

- **Revision 833 — nothing a person asks for is published beneath a running follow (ADR-044):**
  - A generation published beside a running follow makes the follow's next publication fail, which ends it, and only
    `icat checkpoint` refused a session a capture was being followed into. `icat compact`, `rederive` and `retain
    --confirm` published regardless, so freeing room with `icat retain` during a long capture in the window ended the
    capture: the window reads its follow's failure as an interrupted capture, and stops it. Each now refuses while a
    follow holds the session's ticket, in checkpoint's words, saying what to do instead; measuring goes ahead, and so
    does a pin, which every release keeps to (`live-follow-v1` §6). Since only Windows enforces a ticket's hold within
    one process, the tests say which session is followed through the check itself.
  - Debug and Release each ran 3,438 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    10 mutations were caught: icat checkpoint, compact, rederive and retain each publishing into a followed session;
    compact's and rederive's checks and retain's measurement each refused; a ticket a follow let go read as a
    follow; the refusal without what to do instead; and compact's given another exit code.

- **Revision 832 — a value a command cannot read is named, and a missing one said required (R18):**
  - 29 refusals in 12 commands said a value "must be" something, or "names no interval", without the value given, and
    `icat timeline`, `compare` and `outliers` said an `--interval` or `--baseline` given the wrong way round "is
    required", as if none had been given. Each now says what the option expects and the value given that is not one,
    or that it is required when none was, in the form the rest of the command line uses: the record locator of `raw`
    and `content`; `evidence`'s page size, owner and interval; `export`'s format, interval, limit, ranking and
    grouping; `package`'s interval; the columns of both timelines; and the intervals of `patterns`, `waits`, `links`,
    `outliers`, `compare` and a saved comparison.
  - Tests now run every reader at a folder that is not there and at a file in its place, each refused in one sentence
    naming it with nothing made, and `icat export` at every ranking it takes: an RPC ranking over a capture that
    collected no RPC ranks by records and says why.
  - The Release suite once failed a window test that replaces a session's folder under a reader: a reader that found
    the evidence guard gone while the session's pointer was still there made the guard again, as a reader of a session
    older than the guard does, and moving the other session's guard in then failed. The test now moves over what a
    reader made meanwhile.
  - Debug ran 3,436 tests with revision 473's 93 failures and 4 skips, and failed nothing else. Release failed one
    more, a window test that replaces a session's folder under a reader, through a race in the test it now allows
    for, and the Desktop tests then passed in both. The fixture index names the new R18 test. All 22 mutations were
    caught: the shared form leaving out the value given, or calling every value required; and each of 20 refusals
    given no value to name, the intervals of timeline, compare and outliers among them, which then said a value
    given was required.

- **Revision 831 — the window's support bundle counts the errors it contained, by kind and where thrown (§20.6):**
  - §20.6 gives each error category its own counter, and revision 830 had the window say an error nothing mapped
    without counting it, keeping nothing of where it was thrown for whoever fixes it. The window now counts each one
    it contains, and its support bundle holds them: how many in all, and the first 16 kinds by type, count and the
    methods each was thrown from, innermost first and at most 32, a method the compiler rewrote, async or a lambda,
    named as it was written. Never what an error said, which may hold what a record holds; the bundle's listing says
    what it holds before a file is chosen. `icat support`, an invocation that ends on its first such error and writes
    it whole, counts none, and its bundle says so (`support-bundle-v1`).
  - Debug and Release each ran 3,421 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    17 mutations were caught: the tally not counting a kind again, listing past sixteen, counting nothing in all,
    naming a kind without its namespace, keeping one kind for two places, keeping past thirty-two methods, or
    handing out its own list; an async method or a lambda named as the state machine the compiler made, or through a
    bracket left unmatched; the bundle dropping the errors, leaving them out of its listing, or listing them always;
    the window counting nothing it contained, or what it could not say; and its Save support bundle… writing or
    listing none. An eighteenth found that a frame names a generic type as it is declared, whatever it was made of,
    so the code that named it so was dead, and is gone.

- **Revision 830 — an error nothing mapped is said, never a crash or a click that does nothing (§20.6):**
  - §20.6 has a violated invariant contained and reported, naming the rule, and nothing did either. An exception no
    command mapped ended `icat` with .NET's stack trace and a crash's exit code; one an event handler of the window
    met closed it with everything it had open; and one in any of the 108 actions the window starts without waiting for
    them was lost with its task, so a button seemed to do nothing. `icat` now says it stopped on an error InterCat did
    not expect, a defect in InterCat rather than in what was asked, names it, writes it whole for whoever fixes it and
    exits 70, the broker's code, now listed in its help. The window says it where the person is looking, on its card
    or in the investigation's status, and keeps running; running out of memory, and an error no open window can say,
    still end the program. No test contains one, so a defect still fails the test that meets it.
  - Debug and Release each ran 3,418 tests with revision 473's 93 failures and 4 skips, and failed nothing else;
    every window test passed with the 108 actions started without waiting watched, so none of those the tests run
    had been failing unseen. All 19 mutations were caught: icat containing running out of memory, answering 4 rather
    than 70, numbering the code 71, or writing the error's words without where it was thrown; the shared words
    adding a full stop after a question or an exclamation, keeping the spaces around them, leaving a wordless
    error's kind without one, or taking a blank subject; the window saying an error another handler took, containing
    running out of memory, or containing what no window could say; saying it in the window listed first, not walking
    from a dialog to its owner, or stopping at a first window that could not say it; the main window or an
    investigation saying it once closed; a cancelled action said; and an action's one error said as several, or
    several as their first.

- **Revision 829 — every option a command reads is named in its help (R18):**
  - `icat measure --help` gave only its tcp and udp forms, so `measure pipe` and `measure rpc`, and rpc's `--calls`
    and `--service`, were named in no help; `icat import --help` never named `--retain-content`, which it refuses.
    Both helps now name them, and a test reads every command's source for the options it takes and finds each in its
    help. `icat import`'s refusals of a missing path end as the other commands' do.
  - Debug and Release each ran 3,413 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the new R18 test. All 3 mutations were caught: icat import's help leaving --retain-content
    unnamed; and its missing-path refusals back in their old words.

- **Revision 828 — `icat workspace` says once that an investigation is missing or unreadable (R18):**
  - A verb run on an investigation that was not there, or could not be read, said why once for each session it was
    given and again from the summary it shows after every verb, which also set the exit code. The investigation is now
    read once before any verb, and what keeps it from being read is said once. `icat workspace new` beneath a file is
    refused by naming the file, where it failed in .NET's words, exit code 4.
  - Debug and Release each ran 3,410 tests with revision 473's 93 failures and 4 skips, and failed nothing else.
    Both mutations were caught: the investigation left unread before its verb, so each operand and the summary said
    why again; and an investigation made beneath a file unchecked.

- **Revision 827 — what the program is started with opens as a drop would, or says why not (§3.1):**
  - A path on the window's command line, or a file dropped on the program, opened only when it was one session folder
    or one investigation file; anything else, a path moved or mistyped since, a file of another kind, or several at
    once, was passed over in silence and the start page shown as if nothing had been asked. The window now opens what
    it was started with as a drop on it opens what is dropped, and says on its card why anything else opens nothing,
    in a drop's words, a missing path as missing.
  - Debug and Release each ran 3,409 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    4 mutations were caught: a missing path said as a file InterCat does not open; several things given said to be
    dropped; and an investigation and a session folder the program was started with each left unopened.

- **Revision 826 — a file standing where a folder must be is refused before any work (R18):**
  - A report named beneath a file, `--output notes.txt/report.json`, ran the whole command and then failed with "The
    file ... already exists", exit code 4; `icat follow`, `verify`, `record`, `demo`, `package` and `import --into`
    making a folder where a file stood, or beneath one, failed in .NET's words, exit code 4, after the work they had
    done. The shared check of a file a command writes, and a new one of a folder it makes, now refuse the file in the
    way before anything is read or printed, naming it.
  - Debug and Release each ran 3,408 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the new R18 test. All 9 mutations were caught, two of them by a hang until the follow runs
    took --once and they were caught again at once: a report beneath a file let through; a file at a folder said as
    one above it, and not seen; and icat follow, verify, record, demo, package and import each making its folder
    unchecked.

- **Revision 825 — a report path that is a folder is refused before any work, by one check (R18):**
  - `--output` naming an existing folder ran the whole command, printed what it found, and then failed with "Access to
    the path ... is denied", exit code 3, which named a permission rather than the folder. The nineteen copies of the
    check for a report that exists are one, which every command that writes a file a person names uses: a folder there
    is refused before anything is read or printed, with `--overwrite` or without, naming the option, and a file there
    without `--overwrite` as before.
  - A test runs every one of those commands at a folder and at an existing file, `icat support`, `content --find
    --output`, `import`, `compare`, `package --report` and `content --save` for the first time.
  - Debug and Release each ran 3,407 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the new R18 test. All 5 mutations were caught: a folder written to unless --overwrite was
    given; a folder's refusal naming no option; a file that exists replaced unasked; and icat content --save and
    icat package --report each naming --output.

- **Revision 824 — away from Windows, `icat record` and `icat measure` say they need it (R18):**
  - Away from Windows, `icat record` and every `icat measure` said that starting an ETW session requires elevation,
    even to a root shell, in three wordings; the reason is that ETW is Windows' alone. One refusal now says which
    stands in the way: away from Windows, that the command needs Windows and what works here; on Windows without
    elevation, that it needs an elevated shell and what works without one.
  - `icat measure` read a whole number it could not parse, `--seed abc` or `--calls 1.5`, as if the option were
    absent, and measured with its default; it now refuses it, naming the option and the value.
  - Debug and Release each ran 3,406 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the new R18 test. All 7 mutations were caught, three on a second run after a pattern the
    compiler refuses: Windows taken for elevation; what works here unsaid for record and for measure; a malformed
    number's words changed; and icat measure tcp, pipe and rpc each measuring with the default in place of a number
    they could not read.

- **Revision 823 — an empty argument is refused by what it follows, never read as a path (R18):**
  - An empty argument, which an unset variable leaves in a script (`icat session "$DIR"`), ended some two dozen
    commands with an unhandled exception and its stack trace, as each read it as a path; `--output ""` did the same.
    The command line now refuses an empty argument before any command reads it, naming what it follows, and an empty
    command too; a test runs every command so.
  - `icat pin` was missing from the list of commands the command line's tests hold every command to; it is in it, and
    the list is now held to the commands the overview names.
  - Debug and Release each ran 3,401 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the new R18 test. All 3 mutations were caught: an empty argument let through to the command
    that reads it as a path; an empty option value said to follow the command; and an empty command answered as an
    argument after itself.

- **Revision 822 — `icat verify` names a run file it cannot read, and no command crashes on JSON (R18):**
  - A run whose `measurement.json`, truth log or observations held something other than the JSON a measurement writes
    ended `icat verify` with an unhandled exception and its stack trace. It now refuses the run as one it cannot read,
    exit code 4, naming the file and, in a log, the line.
  - The command line answers any JSON a command cannot read as unreadable input, exit code 4, in the reader's words,
    so no command ends the process with a stack trace on it; the commands that read JSON a person names say which file
    themselves. A missing run folder is said as a missing session folder is.
  - Debug and Release each ran 3,400 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    4 mutations were caught: a run header and a log line icat verify cannot read each left to the command line's
    guard, in words that name no file; a log's lines miscounted; and a missing run folder said as before.

- **Revision 821 — `icat bench` answers as every command does, and `icat capture --help` anywhere (R18):**
  - `icat bench` refused `--help` as an unknown option and named an unknown option in words of its own; it now gives
    its help, and refuses an unknown option in the words every command uses, its help after them.
  - A `--series` file that is not JSON ended `icat bench` with an unhandled exception and its stack trace, and a
    missing one was called corrupted input; the first is now refused as a series it cannot read, exit code 4, and the
    second as a mistake, exit code 2, naming the path. A report it may not write over is refused before the machine is
    measured or anything is printed.
  - Away from Windows, `icat capture --help` gives capture's help, which now says it runs on Windows alone, rather
    than refusing to run; a test asks every command for its help.
  - Debug and Release each ran 3,400 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the two new R18 tests. All 7 mutations were caught: icat bench's help and capture's away
    from Windows each taken for an unknown option; icat bench naming an unknown option in its own words, writing
    over a report unasked, calling a missing series damage, and ending with an exception on a series that is not
    JSON or whose JSON is no object.

- **Revision 820 — every command reads `--evidence-policy` alike (R18):**
  - Ten commands take `--evidence-policy`. Seven read it by the policy's name, as written or in kebab form, and refuse
    its number, which is what the durable format stores and not what a person types; `icat exchanges`, `operations`
    and `processes` read it with code of their own that took `2` as a policy. They now read it as the others do, and a
    theory runs all ten, a number refused in the same words and either form of a name taken.
  - `icat exchanges` says one HTTP record that names no exchange as one, where it said "1 HTTP records name no
    exchange"; that line and the note that no exchange is grouped had never run. `icat exchanges`, `operations`,
    `processes`, `session` and `record` end their refusal of a missing directory with a full stop, as every other
    command's does.
  - Debug and Release each ran 3,395 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the new R18 theory, of ten runs, and the new R22 test. All 7 mutations were caught: icat
    exchanges, operations and processes each ignoring the policy given; one unnumbered record said as many; icat
    exchanges' and icat record's missing-directory refusals unpunctuated; and icat exchanges' missing session folder
    unnamed.

- **Revision 819 — `icat content` and `icat raw` refuse a mistaken locator as a mistake (R10):**
  - A `--segment` the generation does not name was answered as damage to the session, exit code 4 in the storage
    reader's words; it is now a mistake in the locator, exit code 2, naming the generation and saying to take the
    locator from `icat evidence`. A row past the segment's last says which rows the segment holds.
  - `icat content` says in words of its own why it shows no byte of a part kept without consent to inspect it, where
    it had repeated the part's whole statement after a colon; and a part recorded whole that held no bytes is answered
    whole, as an empty message is, not as partial.
  - Its other untested answers now run: a record's bytes hidden until asked, a part that is not whole hidden until
    `--reveal` and never saved as one file, shown or not, and a stale locator of either command.
  - Debug and Release each ran 3,378 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the new P2 test. All 9 mutations were caught: a segment the generation does not name read as
    damage; the row just past the last read; the rows said one too many; an empty part answered as partial; a part
    kept without consent said to be too long; a part neither whole nor read saying its statement; the hidden-bytes
    notes of a record and of a part not whole left unsaid; and a part not whole, shown, saved without a word.

- **Revision 818 — every RPC call counted once wherever two intervals split a session (R10):**
  - A process's RPC channels counted within an interval had been tested for calls that completed, failed or stayed
    open; a call whose start the capture did not hold, a record that carries no activity id and the records of an
    activity id reused before its stop were counted within an interval by no test. A test now splits a session holding
    calls that ended every way at eighteen places, before a call, inside one and on a record, and checks that each
    call is counted on one side only, by the record that counts it, that each record is counted by its own reading,
    and that each side's page lists the calls its channel counts there.
  - An HTTP exchange whose request body's last buffer was not recorded says its request body is not whole, the one
    part no test had said so of.
  - Nothing was wrong; each is now held by a test.
  - Debug and Release each ran 3,375 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the new R22 test. All 9 mutations were caught: a call whose start was not held, a record
    with no activity id and an ambiguous record each counted as another state; an unpaired start and an open call
    counted nowhere; stops left out of the records counted; every call counted as started; a request body named as a
    head; and a request body never said to be missing.

- **Revision 817 — a coverage pass over the content viewer's facts and buffer ranges (R10):**
  - The facts the content viewer gives a record were run for what is unusual in one: content recorded encrypted, shown
    as the bytes it is and never decrypted; UTF-16 text; a record whose source does not say which way it went, or how
    long its message was; and a fragment that starts past its message's first byte, the bytes before it said to be
    never recorded. A part of one buffer, its part's first and last, that the record limit cut says so of the buffer
    and says how much of it was cut; and a buffer range whose first or last is not a number says which number to
    enter.
  - Nothing was wrong; each of these is now held by a test.
  - Debug and Release each ran 3,373 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the new P2 test. All 10 mutations were caught: the words for an unstated direction, UTF-16
    text, encryption and an unstated length; the bytes before a fragment dropped or said one too many; a fragment
    past its message's start said to start at its first byte; a buffer its part's first and last said to be only its
    first; and each buffer refusal's example number swapped for the other's.

- **Revision 816 — two promises every command makes, held by tests across them:**
  - Every command asked for `--json` prints one JSON document and nothing else, what it says of its work going to the
    error stream, so its output can be read by another tool; a theory now runs 19 commands so, from `icat channels` to
    `icat profiles explore`, and parses what each printed. And a report file that exists is refused before anything is
    read or printed: the test of it, which named seven commands after revision 815, names twelve.
  - Neither found a command that broke its promise; each guards the commands it names from now on.
  - Debug and Release each ran 3,371 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the new R18 theory, of 19 runs. All 7 mutations were caught: icat session and icat patterns
    saying a word after their JSON, and icat metric, retain, rederive, processes and session writing over a report
    that exists.

- **Revision 815 — one wording for a report that exists, and `icat capabilities` refuses one before it probes:**
  - A report file that exists was refused in two wordings: "exists" by most commands, "already exists" by `icat
    capabilities`, `compare`, `export` and `support`; and six commands said where they wrote without ending the
    sentence. Every command now says "<path> exists. Pass --overwrite to replace it.", and every report written ends
    its sentence. `icat capabilities` also probed and printed its whole report before refusing a report file that
    exists; it refuses first now, as every other command does.
  - Tests run what a coverage pass found no test ran of `icat compact`, `export`, `timeline` and `operations`: a
    folder that holds no session, a `--process` or `--end` the timeline cannot read, a report that exists refused with
    nothing printed, and the reports `icat capabilities`, `compact` and `operations` write with `--output`, each the
    document `--json` prints.
  - Debug and Release each ran 3,352 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the 2 new R18 tests. All 11 mutations were caught: icat capabilities replacing a report
    unasked; capabilities, export or compare saying a report already exists; the capability report's sentence left
    unended; the compaction's or the calls' report misnamed; and the refusals of no folder, a missing session, an
    unread process and an unread end reworded.

- **Revision 814 — a coverage pass over `icat profiles`' refusals; its catalog said to be its catalog:**
  - Of the 550 lines of `icat profiles`' command a test can reach, 94 ran in none, most of them refusals: a channel
    selector holding a control character, more than 64 process IDs or channel selectors, a byte limit that is no
    positive integer, content options given to focused transport, focused transport without its mechanism,
    `--allow-broader-capture` without a process or with content, an incomplete content request, and one whose source
    the adapter catalog does not hold. Tests run them now, and the catalog written with `--output`.
  - Running them found the catalog written with `--output` said as "Profile preview written to …"; it is said as the
    profile catalog now, and a preview as a preview. What stays unrun is a plan that can start and its scope table,
    which only Windows' trace metadata compiles.
  - Debug and Release each ran 3,350 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the new R18 test; the 12 new refusals are rows of an R18 theory. All 11 mutations were
    caught: a control character in a selector, a 65th process ID or channel, a byte limit's words, content options
    taken by focused transport, focused transport taken without its mechanism, --allow-broader-capture taken without
    a process or with content, the content request's own refusal unsaid; and the catalog said as a preview, or
    written cut short.

- **Revision 813 — a clock descriptor refuses what the contract does not define:**
  - A coverage pass over the domain found that a source clock descriptor refused a kind or encoding of 0 but took any
    other value its code does not define, and any rounding at all. A journal clock frame of rounding 9 was read, and
    its first reading's conversion then threw, neither quarantined nor the journal refused as damaged. The descriptor
    now refuses a kind, an encoding or a rounding the contract does not define, and both journal readers,
    `journal-v1`'s and the probe's, refuse such a frame as malformed evidence and say why; `journal-v1` says so.
  - The pass also runs two quarantines no test ran: a reading in another encoding than its clock's, and one past what
    a session instant holds.
  - Debug and Release each ran 3,337 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the 4 new tests, of I8; one is a theory of 4 cases. All 8 mutations were caught: an
    undefined rounding, kind or encoding taken, by the descriptor or from a journal's frame; a reading in another
    encoding converted, and one past what a session instant holds converted; and either journal's refusal said
    without what it refused.

- **Revision 812 — verbs that agree with their counts:**
  - Revision 811 swept the nouns after a count; this sweeps the verbs, and found four said wrongly of one. A graph
    card said "1 contributing observations are on relationships with no byte total"; a record the record limit cut
    said "the other 1 were cut"; a buffer whose one byte was not kept said "none of its 1 byte were kept; nothing
    stands in for them"; and `icat measure` said "1 admitted records were not decoded". Each now agrees with its
    count. The measurement's sentence, which its TCP, pipe and RPC runs each said, is said once, its budget written
    with its group separators.
  - Debug and Release each ran 3,330 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index indexes none of the 4 new tests, each of a plan section; two are theories of 2 cases. All 6
    mutations were caught: one contributing observation said in the plural, or with an unknown total where there is
    none; one cut byte said in the plural; one unkept byte said as them, or in the plural; and one undecoded record
    said in the plural.

- **Revision 811 — counts said in words that agree with them:**
  - A sweep of the counts the command line and the window print before a noun found five that a count of one, or of
    none, reads wrongly. `icat import` said "1 runs" spilled; `icat content --find --max-seconds 1` said "1 seconds";
    the interval table of a session one tick long said "Whole session in 1 intervals", as did a view drawn in one
    column; and an `icat record` that published nothing said "0 generations, one journal chunk each; the last holds
    them all". Each now agrees with its count, and a recording that published nothing says so.
  - Every other count the sweep found is said by `CountText` or `Spoken`, or cannot be one where it is said: an
    aggregate on the graph folds two processes at least, a toggle offers more than its list names, and an outlier
    baseline holds 19 windows at least. The import's and the recording's words, which only a real trace or a Windows
    capture make, are said by small functions their tests reach; a process lane's caption counts its intervals as the
    table's does.
  - Debug and Release each ran 3,324 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index indexes none of the 3 new tests, each of a plan section; two are theories of 3 cases. All 6
    mutations were caught: a recording that published nothing said as generations; one run, one byte spilled or a
    budget of one second said in the plural; and a whole session or a zoomed view in one interval said in the
    plural.

- **Revision 810 — a coverage pass over the content viewer, channel list and pin removal; one character said as one:**
  - Of the lines a test can reach, 21 of the content viewer's 670 ran in none, 8 of the channel list's 187 and 15 of
    the window's pins' 232: the text view's notes of control characters shown as marks, bytes its encoding does not
    read and text past its bound; a session that publishes a newer generation between two pages of channels, whose
    list starts again from its first page and says why; and a pin that cannot be removed, its folder now holding
    another session. Tests run them now.
  - Running them found the text view saying "1 control or invisible characters are shown as marks" and "1 show as �"
    of a single character; each note now agrees with its count. What stays unrun of the viewer is a part it does not
    read, kept without consent in another chunk or past the 64 MiB a part is read up to, and a decoding asked for
    where no decoder reads the record, which its button already refuses.
  - Debug and Release each ran 3,317 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index indexes neither of the 2 new tests, each of a plan section. All 9 mutations were caught: one
    control character or one replaced byte said in the plural, one replaced byte unsaid, the text's bound unsaid and
    the plain text's note changed; a restarted list unsaid, or the empty page that asks for the restart shown; and a
    pin removal that failed said without why, or said done.

- **Revision 809 — a coverage pass over the investigation's words; the pairs not proposed said as many as they are:**
  - Of the lines a test can reach, 11 of the investigation rows' 245 ran in none and 8 of the instant-compare dialog's
    130: a session that published a newer generation since it was added, read as advanced to it, and one an earlier
    copy was put in place of, read as replaced by it; a join a person rejected; a session aligned by one boot's
    counter; an instant placed with no known certainty; and the dialog's Enter and its refusal of an instant with no
    session. Tests run them now.
  - Running them found the mirrored pairs a correlation sets aside said as "Not proposed: 0 mirrored pairs whose
    lifetimes lie apart beyond their uncertainty, and 1 loopback pairs of two hosts.", in the window and in `icat
    workspace correlate` alike: a kind there was none of counted, and one pair said in the plural. One wording,
    `WorkspaceCorrelation.NotProposed`, now says each kind there is, as many as it is, for both. And the dialog's
    refusal of an instant with no session kept saying where the instants compared before fall; it clears them now, as
    its other refusals do.
  - Debug and Release each ran 3,315 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the 3 new tests; one is a theory of 4 cases. All 16 mutations were caught: a count of one
    said in the plural, a kind there is none of said, nothing said as none, the kinds joined without their and, and
    the command line's or the window's words changed; a member's newer or older generation left unnumbered; a
    rejected join said to be accepted; one boot's alignment said as a person's; an instant of unknown certainty said
    without it; Enter comparing nothing; and a refusal keeping the placements, or either refusal the comparison,
    before it.

- **Revision 808 — a coverage pass over the graph view, the minimap and the investigation window:**
  - Of the lines a test can reach, 60 of the graph view's 1,455 ran in none, 17 of the minimap's 250 and 51 of the
    investigation window's 1,368: a node's drag the system cancels, as another window or a dialog taking the pointer
    does, carried back with nothing pinned; the hover card following the pointer across the node it describes; a
    double click on the minimap fitting the timeline; the saved package's dialog saying why each session not copied
    stays a reference; and the host dialog, the timeline and the candidate joins of an investigation whose file no
    longer reads, or is gone, saying why. Tests run them now; running them found nothing to change.
  - What stays unrun of the graph view is mostly what it only draws, such as a focused node's outline, a stack's
    hatching and the words that say its layout is unavailable, or a guard for a node or a frame that is not there; of
    the investigation window, what follows a dialog only its modal showing answers, panes kept while the investigation
    is read, and where focus goes back to on each tab.
  - Debug and Release each ran 3,309 tests with revision 473's 93 failures and 4 skips, and failed nothing else. No
    test is new, so the fixture index is as it was; six tests grew. All 10 mutations were caught: a drag the system
    cancels unnoticed, its node not carried back, or the drag still held after it; the hover card left behind as the
    pointer crosses its node; a double click on the minimap not fitting the timeline; a package's sessions left out
    unsaid, or said without naming the session; and why the host dialog, the timeline and the candidate joins could
    not read an investigation unsaid.

- **Revision 807 — a coverage pass over the window's wheel zoom, minimap, settings and alignment dialog:**
  - Of the lines a test can reach, 123 of the timeline view's 2,587 ran in none, 17 of the minimap's 250, 14 of the
    settings file's 149 and 15 of the alignment dialog's 256: the wheel's zoom at the pointer, in and out, and a wheel
    that does not turn; the minimap's left edge dragged; the two high-contrast themes written and read by their names;
    a settings file past its 1 MiB bound; and every refusal of the alignment dialog that no test made, of a session
    unchosen, an agreement or an instant's bound that is no duration, and a drift that is no rate. Tests run them now;
    running them found nothing to change.
  - What stays unrun of the timeline view is mostly what it only draws, such as a selection's outline, the words of a
    lane with nothing to plot and the machine row's label, or a guard for a lane, a bucket or a pointer that is not
    there; of the minimap, its cursors and a double click fitting the whole session; of the settings, the folder
    Windows names and a file that cannot be read. The graph view's 60 of 1,455 are left to a later pass. Three wrapped
    lines that began with a dash, which Markdown reads as a list item of its own, are reworded: in revision 761's note
    here, in `coverage-v2` and in `workspace-v23`.
  - Debug and Release each ran 3,309 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index indexes neither of the 2 new tests, each of a plan section; one is a theory of 5 cases. All 14
    mutations were caught: the wheel's zoom turned the other way, and a wheel that does not turn zooming; the
    minimap's left edge moving its right edge or the whole brush; the high-contrast light theme written or read as
    the dark one, and the system theme written unnamed; a settings file past its bound read, and its bound said in
    bytes as kibibytes; in the alignment dialog, an unchosen session's refusal reworded, the refusals of an
    agreement, a wall-clock drift and an instant's bound each said in another's words, and a drift that is no rate
    taken.

- **Revision 806 — a coverage pass over `icat retain`, `icat session` and `icat package`:**
  - Of the lines of their commands a test can reach, 81 of `icat retain`'s 798 ran in none, 65 of `icat session`'s 947
    and 63 of `icat package`'s 429: a redacted package measured with `--check`, its interval said and its `--report`
    written; a package or a report already there refused; `icat session`'s report written with `--output`, kept from
    being replaced unless asked, and a `--rows` that is no count refused; and a release a pin stops partway, measured.
    Tests run them now.
  - Running them found a later moment offered past a pin. A measured release says, where one exists, a later moment
    before which a release would give up more; it was found among every chunk but the newest as though no pin stood,
    so with a pin from 2 s `icat retain` offered a release up to 2.0001 s, which the pin forbids and which would have
    given up nothing more. The most a release can give up now stops before the earliest pin, whether the release asked
    for reaches it or not, and `icat retain --json`'s `mostReleasingNanoseconds` with it.
  - Debug and Release each ran 3,303 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the 2 new tests. All 12 mutations were caught: the most a release gives up taken past the
    pins, or past them only where the release asked for does not reach them; the pin's note and the pinned field
    unsaid; the measurement's note and the interval's words changed, the package's and the session's reports left
    empty or replaced unasked, a package written into a folder already there, and the refusal of --rows reworded.

- **Revision 805 — a coverage pass over `icat processes` and `icat pin`:**
  - Of the 802 lines of `icat processes`' command a test can reach, 54 ran in none: a process's parent, named by its
    instance or by the source alone, its terminal session and the gaps in its evidence, the words for an instance
    running when the capture began, first seen exiting or replaced with no exit seen, the instances past `--top`, and
    what it lists written to `--output`, which keeps a file there unless asked to replace it.
  - Of the 227 of `icat pin`'s, 38 ran in none: a pin placed with the allowance asked for, said in words with what it
    keeps, and that the content kept of those records stays releasable on its own, the pins standing listed, and the
    refusals of a reason past its bound, an identity two pins share and a session none of whose records has a session
    time. Tests run both now; every path did as its contract says, and nothing was changed.
  - Debug and Release each ran 3,301 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index indexes neither of the 2 new tests, each of a plan section. All 17 mutations were caught: in icat
    processes, a parent's instance or its absence unsaid, the terminal session or the evidence gaps unsaid, a
    lifetime's or a witness's words changed, a replacement's missing exit unsaid, the rest past --top uncounted, the
    output unwritten or replaced unasked; in icat pin, an allowance read in kibibytes, the placed pin's heading, the
    pins unlisted, the content note unsaid, the reason's bound moved, a shared identity's pins unlisted and an
    untimed session's refusal reworded.

- **Revision 804 — a coverage pass over `icat workspace`:**
  - Of the 1,224 lines of `icat workspace`'s command a test can reach, 107 ran in none: an alignment at two instants
    and the rate it measures, a note reworded, removed or pinned at an instant, a view removed or refused for ending
    before it starts, a folder holding no session added, a decision rejected or written as JSON, and the refusals of a
    candidate named by no number and of a grouping no saved comparison makes. Tests run them now.
  - Running them found a measurement said backwards. Two instants measure the rate of a member's clock against the
    reference's, and the window and the command line said it with its sign turned - the reference's rate against the
    member's - so a clock running 10 ppm slow read "+10 ppm against the reference's", where the session clock's own
    "ran +10.0 ppm against the capture's own clock" means it gained. The rate is now the member's against the
    reference's, as it was named, positive where the member's clock runs fast; the mapping and its uncertainty, which
    use the measured rate itself, never changed.
  - Debug and Release each ran 3,299 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index indexes none of the 3 new tests, each of a plan section. All 14 mutations were caught: the rate's
    sign turned back, a second pair naming the sessions crossed taken, the rate unsaid, the one instant's warning
    given for two, earlier revisions unsaid; a note's change said the other way, a pinned note unpinned, a backwards
    view saved, a view's removal said as saved; a folder not added left unnamed, a candidate's refusal and a
    rejection reworded, a decision's JSON unwritten and an unknown grouping saved.

- **Revision 803 — a coverage pass over the window's workspace:**
  - Of the lines a test can reach, 183 of the workspace view model's 4,687 ran in none and 149 of the main window's
    3,855. Most are guards, failures said in words that only a session breaking mid-read reaches, and what only a live
    capture on Windows reaches; some paths a person takes were among them, and run now.
  - The direction and channel-end selectors' options were never read as a screen reader hears them: each is a sentence
    saying what choosing it shows, and the chosen one reads as its label. A process's records the source marked
    bidirectional, or marked no way, are said as it marked them, in a chosen cell's explanation and in its row's card.
    Inside a group, Ctrl+Space on a process's row chooses that process and takes it out alone, each row saying whether
    it holds its process, and a set of more than three names its first three and counts the rest. A group's row only
    some of whose processes are chosen is marked lighter and says it is partly in the selection.
  - A session of one generation whose files changed after they were published was opened again as though they had not:
    the check after its first view had its store refuse the changed file, the window read that refusal as the folder
    holding another session and opened it afresh, with a store that lists files without hashing them, and the check
    that followed found the file changed again and opened it again, on and on. The store that refused it is kept now:
    the view stays, the window says what it shows was read before the change was found, and opening the session again
    is refused, saying why.
  - Debug and Release each ran 3,296 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the new R15 test; the other two are of plan sections, which it does not index. All 10
    mutations were caught: a session of one generation whose files changed opened afresh again; a direction's or an
    end's option said by its label alone; records marked bidirectional said to have no stated direction, and the
    unknown direction's meaning cut short; a set's rest past three left uncounted, a process's row choosing nothing
    or holding its process only in part; and a group's row partly in the selection said to be wholly in it, or
    marked so.

- **Revision 802 — a coverage pass over the graph's, the tiles' and the timeline's derivations:**
  - Of the lines a test can reach, 77 of the graph projection's 949 ran in none, 85 of the tile index's 3,391 and 47
    of the timeline's 1,002. Nearly all are guards against a damaged file, a sum past what it holds or a call no
    caller makes; three paths real data takes were among them, and run now.
  - A group's, a process's or a channel's focus over a session one of whose segments reads backwards, as ETW delivers
    a busy processor's buffer late, counts that segment from its rows and every other from its section, exactly as the
    rows count, opening that segment alone. A graph weighed by bytes still finds a relationship inside a node in that
    node. A column reaching past the last time a session's nanoseconds hold, which `--interval` can name, says its
    coverage unknown rather than wrapping its end to a time after the epoch. Two members no caller used are gone.
  - `icat channels --process` took an instance id alone, and one naming no process of the session was refused in the
    window's words, "Return to the overview and select it again", with a permission failure's exit code, or with
    `--one-sided` listed as a process with no connection. It names a process by the PID it alone held or by its
    instance, as `icat links`, `waits` and `content` do, and refuses a reused PID or one naming no process in their
    words, as a mistake in what was asked. Its pages in words, each saying the cursor of the next, and a stale
    cursor's restart ran in no test, and do now.
  - Of those lines, 76 of the graph projection's run in none now, 85 of the tile index's and 37 of the timeline's 998:
    guards against damaged files, sums past what they hold and calls no caller makes; and 13 of the 155 of `icat
    channels`, a folder that is not there and a session that cannot be read.
  - Debug and Release each ran 3,293 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the 2 new tests. All 8 mutations were caught: a segment reading backwards left uncounted by
    a focus, a graph weighed by another metric losing the relationships inside its nodes, and a column's end wrapped
    past what a session's nanoseconds hold; and in icat channels, a process naming none refused with a permission
    failure's code, --one-sided without --process let through, the process named resolved to another, the next
    page's cursor left out and a stale cursor's page listed.

- **Revision 801 — a coverage pass over `icat metric`:**
  - Of the 1,580 lines of `icat metric`'s command a test can reach, 351 ran in none: RPC calls started, completed,
    failed and timed in words, peers and channels, rankings by mechanism, executable and the other end, the records
    between two sets, `--output`, `--print-canonical` and most refusals. Running them found four faults, now fixed.
  - Grouped, a peer or channel count no record identified one of answered a total of none, "at least 0 channels in
    scope", where ungrouped it is unmeasured, as the contract has a peer count be; its total is `NothingMeasured` now,
    its groups each unmeasured beside it, each record that identifies none counted once by why. A request no derived
    operation answers was headed "No logical operations", though the session held calls; it says that no derived
    operation answers it.
  - A count of no observation said the session publishes no coverage ledger, whether it did or not; it says so only
    where none is published. A ranking by mechanism named its rows by their codes, "Rpc" and "ProcessLifecycle"; it
    names them as the window's lanes do, and a ranking with no row says so in place of an empty table.
  - Of 1,591 such lines, 65 run in none now: 22 record declarations the instrumentation counts, and 43 sessions and
    requests no test makes - a capture that recorded its stop, a session publishing no derived segment or describing
    no clock, a bound beyond the clock on its own side, other byte domains measured, a remainder past `--top` by
    mechanism, session or instance, metrics and aliases no test names, and two guards.
  - Debug and Release each ran 3,291 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the 7 new tests. All 21 mutations were caught: the zero count's claim of no ledger restored,
    or the no-ledger sentence unsaid; the reason's old words, the mechanism codes, an empty ranking's table or
    words; the calls' counted words, an unpaired failure unsaid, a statistic's or a direction's words, a remainder's
    label, a byte rate's unit, a focus's peers' words, an empty file written and the sets unparsed; and in a grouped
    count, a total of none, a record of a process without the grouping's fact identifying nothing, the reasons
    swapped or counted twice, the groups dropped and one record said in the plural.

- **Revision 800 — a content search finds a text three or more buffers of one HTTP part split (M8):**
  - A buffer of an HTTP part is searched with the end of the buffers kept whole right before it, back through as many
    as follow one another so, where revision 789 joined only the buffer right before it: a buffer shorter than the
    text no longer hides a match running through it. A part's matches are those one search of its bytes from the start
    finds, none overlapping another, and read on page by page the search finds the same.
  - Where a match began is said as so many bytes before the record in its part, "4 bytes before it in its part", and
    the command line's note says it began earlier in its HTTP part, since it may lie several buffers back; the
    sentence each answer carries says a buffer is searched with the buffers kept whole right before it.
  - A test sets five texts, each cut at random into buffers of one to seven bytes or one to four UTF-16 code units,
    beside one search of each part's bytes, read whole, a record at a time and a match at a time, in either case; a
    part's start or a buffer of another encoding still ends what is joined, and the command line and the window find
    the host's name three buffers split.
  - Debug and Release each ran 3,284 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the new test and the two it renamed. All 7 mutations were caught: a run's free bytes or its
    last bytes not carried to the next buffer, a match across a seam not ending what is free, every match leaving
    the run free, a seam read from a buffer's whole length, and the old words for where a match began in the window
    or on the command line.

- **Revision 799 — a coverage pass over revisions 752 to 773:**
  - Of the 6,966 lines revisions 752 to 773 added that a test can reach and no later revision rewrote, 239 ran in
    none. Two were gaps. `icat compare` and `icat outliers`, as the older `icat timeline` did, exited 3, a permission
    or capability failure, on a session that does not verify, where §20.4 has every reader exit 4, corrupted input;
    they now do. And an investigation file edited to list an empty saved comparison stopped its reader without a
    reason, its notes read first; it is refused in words now, as an empty entry of every other list is.
  - New tests run what else could be reached: every reader of a session on a damaged one and on a missing folder,
    sizes past a sum for `icat compare` and `icat timeline`, JSON written when asked whatever the file is named, a
    difference timeline's reasons and the links not followed in JSON, a process's and an executable's connections to
    another host in words and JSON, a comparison's words at every scale, an RPC call no start named an interface for,
    the overlay's words for every change, a note with no words, and in the windows the Compare window's Enter, save
    prompt and failures, the saved comparisons' failures and Escape, the charts' keys and clicks, the Compare button
    and a wait only a newer generation holds. `LinkEnd.Unseen` and `LinksWindow.Step`, which nothing called, are gone.
  - Of 6,954 such lines, 98 run in none: 42 a reading a newer generation interrupted three times or another session
    took the folder from, and the commands' answers to it; 24 a window closed or a request replaced while its read
    runs; 25 an action pressed while not offered, a dialog that fails to open, or InterCat's window closed beneath
    one; 5 guards no caller reaches; and 2 record declarations the instrumentation counts.
  - Debug and Release each ran 3,283 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the 5 new tests. All 57 mutations were caught: a damaged session answered as a permission
    failure again by icat compare, outliers or timeline; a missing folder not refused by any of seven readers; JSON
    written as CSV; a difference timeline's reasons unnumbered; links not followed left out of JSON; five comparison
    words; a call naming no interface uncounted or unsaid; an investigation's comparisons checked after its notes,
    an empty one unsaid or a blank note read; four overlay words; a host or executable row's kind, ends, ports,
    two-way connection or JSON source, target, name or path wrong in icat outliers or compare; and seventeen window
    behaviours, from the Compare window's Enter, save prompt and failures to the charts' keys and clicks, the
    Compare button and a wait only a newer generation holds.

- **Revision 798 — a coverage pass over revisions 774 to 797:**
  - Of the 2,621 lines revisions 774 to 797 added that a test can reach, 91 ran in none. One was a gap: a content
    search skipped a record the capture kept no byte of, its content limit reached, without counting it. It now counts
    them (`ContentSearchResult.Omitted`), and `icat content --find` and Find in kept content… say how many were not
    searched, the answer whole for them as for a cut record.
  - New tests run what else could be reached: a time scope's RPC calls the policy does not admit, the window's search
    of a folder holding another session, a table it cannot write, Escape on it and on its prompt, a buffer's exchange
    not admitted under direct evidence, a conversation or exchange lookup in another session's folder, an exchange no
    longer listed when its buffers are asked for, a heads table that cannot be written, `icat content --find` on a
    missing or unpublished folder, and `--format json` named. Of 2,632 such lines, 37 run in none: 11 the
    instrumentation counts with no statement, 6 a window closed while it reads, 14 guards no window action reaches, 4
    a process released between naming it and searching, and 2 an empty pattern every caller refuses first.
  - Debug and Release each ran 3,277 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the 4 new tests. All 6 mutations were caught: the omitted record left uncounted, an empty
    message counted with it, the count not summed across pages or not kept, or unsaid on the command line or in the
    window.

- **Revision 797 — causal links read an interval of a session:**
  - `icat links --interval <start:end>`, and the window's Follow its links… over the window's time scope, list and
    follow what began in the interval alone: a start where the child's creation was witnessed (a start a rundown names
    began before the capture), a call where the client's began, a server call no client reached at its own start, and
    a connection or flow at its first record. What of a process began outside it is counted for each process it
    touches, never also as not linked, and said beneath its links; a chain runs through the interval's links alone,
    and says so where none does; nearness still reads every witnessed creation. `--json` adds `interval` and
    `outsideInterval`.
  - Debug and Release each ran 3,273 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the 4 new tests. All 31 mutations were caught: what began outside the interval read as in
    it, or a start no record witnessed read as in it, either process it touches left uncounted or a process
    connected to itself counted twice, a start, an unheld parent, a connection or a flow read outside it, a group's
    calls counted or linked over the whole session, a server call a client reached placed by its own start, a served
    call's server left uncounted, a call placed by its stop, the interval never read or kept, a process's outside
    count unset or unsaid, or said as listed, the interval, its outside count, its heading or its chain's absence
    unsaid on the command line or in its document, never read by the window or InterCat's window, or the window's
    scope and outside count unsaid or said off its clock.

- **Revision 796 — the time inside calls reads an interval of a session:**
  - `icat waits --interval <start:end>`, and the window's Time inside calls… over the window's time scope, time the
    client calls that began in the interval alone (at their start or, where none was recorded, their stop), each to
    its stop even past the interval's end, and count those that began outside it apart, never as not validated or
    unadmitted too. The answer states the interval, the window on its clock; what no time is said for counts the calls
    outside it; a note says where a call is read and to when; an interval no completed call began in says so; and
    `--json` adds `interval` and `outsideInterval`. Revision 795's three tests are filed under R21 and R18, which they
    test, rather than P6, which is about identity.
  - Debug and Release each ran 3,269 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the 3 new tests and files revision 795's under R21 and R18. All 24 mutations were caught: a
    call that began outside the interval read or uncounted, placed by its start alone, by its stop, or in
    nanoseconds as ticks, an unadmitted caller's call counted as unadmitted rather than outside, the interval never
    read or never kept, its none or what began outside it unsaid, the interval unsaid on the command line, in its
    document, its note or its answer for one process, never read by the command line, the window or InterCat's
    window, or the window's scope, note, none or one process's answer said of the session or off its clock.

- **Revision 795 — recurring patterns read an interval of a session:**
  - `icat patterns --interval <start:end>`, and the window's Recurring patterns… over the window's time scope, make
    the patterns of the connections, flows and calls that began in the interval alone (a connection or flow by its
    first record, a call by its start or, where none was recorded, its stop), spread their tenths over it and count
    what began outside it apart, never as unadmitted too. The list states the interval, the window on its clock; the
    note beneath and a pattern's Occurred say the interval's tenths; an interval holding none says so; and `--json`
    adds `interval` and `outsideInterval`. The window no longer reads the whole session whatever its scope.
  - Debug and Release each ran 3,266 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the 3 new tests. All 24 mutations were caught: what began outside the interval read or
    uncounted, the tenths spread over the session, a connection, one-sided connection or call read outside it, an
    unadmitted call or relation counted as unadmitted rather than outside, the interval unsaid on the command line,
    in its document, its refusal or the window, its tenths, its none or its note said of the session, the interval
    never read by the command line, the window or InterCat's window, or the scope said off its clock.

- **Revision 794 — `icat content --find` takes budgets of its own:**
  - `--max-mib` (1 to 65,536) and `--max-seconds` (1 to 3,600) set how much a search reads and for how long; an answer
    within budgets other than the default states them as Budgets, and one a budget stopped says the two options let it
    read more. A value out of range, and either option without `--find`, is refused, naming what it expects.
  - Debug and Release each ran 3,263 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the new test. All 8 mutations were caught: no mebibyte or too many seconds taken, either
    budget ignored, the budgets never or always said, how to read more unsaid, or either option taken without
    --find.

- **Revision 793 — a content search that ended early reads on from where it ended:**
  - A search that ends before reading every record it could - at a budget, stopped, or finding more records than it
    lists - answers with a `ContentSearchContinuation`: what it was asked, the segments it read up to the record it
    had neither listed nor read, and the ends of the HTTP parts it had open. `ContentSearch.Continue` reads on from it
    as one search from the start would, never a record twice nor losing a match a seam splits, reading at least that
    record so no budget holds it in place; it reads the current generation, refused where that no longer holds every
    segment the search read, or holds another session.
  - A page that found more records than it lists counts the bytes of the record that showed it, and the page read on
    from it does not count them again, so `ContentSearchResult.Then`, which joins a page with the one read on from it,
    sums every count once.
  - The search window's Search on reads on, adding what it finds after the records listed and choosing the first
    added; what it then says, and what Export… writes, is the whole search's so far. It says why it is not offered
    before a search and after one that read every record.
  - Debug and Release each ran 3,259 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the two new tests. Of 19 mutations, 18 were caught at once: a stop, a budget or the limit
    leaving no continuation, a page read on held in place by its budgets, the record that showed there were more
    counted twice or not at all, a continuation taken in another session or with a segment released, the parts' ends
    not carried, the stopped segment's rows read again from its start, the joined pages keeping only the later
    page's records, records read or bytes, and in the window Search on not wired, the pages not joined, the list
    replaced, the first record added not chosen, or Search on offered after a search that read every record. The
    19th, every page restarting at the generation's first segment, was missed because every session the tests paged
    through held one segment; a three-segment session now pages through, and the rerun caught it, with the stopped
    row and the other session's refusal again.

- **Revision 792 — what a content search lists is written to a table when asked:**
  - `ContentSearchTable` writes the records a search listed as CSV, a line each: its session, generation, segment and
    row, its time as seconds after the capture epoch on the clock of the generation searched, which the search's
    answer now carries, its process ID, mechanism and event, how often it holds the text and where first, and the text
    searched for and how; the bytes around each first match only where previews were asked for. Its numbers are
    invariant, and its text cells quoted and neutralized as every table InterCat writes.
  - `icat content --find --output <file>` writes it, replacing a file already there only with `--overwrite`, and says
    what it wrote, and with `--previews` that the table is content; `--overwrite` without `--output`, and `--output`
    without `--find`, are refused.
  - The search window's Export… writes the same table to a file it asks for, and where the previews were shown it says
    first that the table is content, asking for no file when declined.
  - `ContentExportReviewTests` treats a call to `ContentSearchTable.Csv` as a path content leaves by, so a new caller
    needs its row; the command's and the window's rows say what they write.
  - Debug and Release each ran 3,257 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the three new tests. Of 22 mutations, 21 were caught at once: the previews' columns or cells
    always written, the text or a preview not neutralized, the times, where first or the case unsaid, the clock not
    carried; on the command line the table unwritten, a file already there replaced unasked, --overwrite taken
    alone, --output taken without --find, the warning that the table is content never or always given, an empty
    table's words, or --overwrite not passed to the writer; and in the window the prompt skipped or always shown,
    Export offered before a search or with nothing listed, or that the table is content unsaid. The 22nd, a record's
    process taken from the process that raised it rather than its owner, was missed, since no record listed in the
    table's test was raised by the kernel; a test now lists one, and the rerun caught it.

- **Revision 791 — the content viewer names InterCat's decoders where none reads a record (M8):**
  - Where no decoder reads a record or its part, the content viewer's Decode stays in view, disabled, saying so - of a
    body's buffer, that InterCat decodes a head and never a body - and naming each decoder by its name, identifier and
    version, and what it reads, as `icat content --decoders` lists them, from one sentence `ContentDecoders.Listed`
    makes; Decode and Decode its part are offered as before where a decoder reads.
  - Revision 784's export test narrowed the interval and awaited the rung's HTTP reload before the interval's counts,
    read off the window's thread, had been applied, so under the suites' load it once found the rung still listing the
    whole scope; it awaits the counts first now, as every other test that narrows the interval does.
  - Debug and Release each ran 3,252 tests with revision 473's 93 failures and 4 skips; Release failed nothing else,
    and Debug failed revision 784's export test once, the race named above. Fixed, it passed five times beside eight
    spinning processes, but so did the unfixed test: the race shows only under the suites' own load, and the fix
    follows from the code, which applies an interval's counts once they are read off the window's thread and only
    then reads the rung's HTTP again. The whole window test project passed in Debug after the fix. All 6 mutations
    were caught: the decoders listed without their versions or only the first of them, Decode hidden or offered
    where none reads, a body's buffer not named, or the list unsaid.

- **Revision 790 — a running content search can be stopped, keeping what it found:**
  - `ContentSearch.Find` takes a stop apart from its cancellation: stopped, it ends before the next record it would
    read and answers with what it found, saying it was stopped when asked and after how many records, the answer
    partial; cancelled, it throws and answers nothing.
  - The window's Find in kept content… offers Stop beside Find while a search runs, and says why not otherwise; Find,
    while one runs, says Stop ends it. Closing the window still abandons a search.
  - Debug and Release each ran 3,252 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    window's stop test passed three times running before the suites. The fixture index names the two new tests. All
    6 mutations were caught: a stop ignored or said as a budget's; and in the window Stop not wired to the search,
    its stop never passed, or Stop offered always or never. Clearing the window's stop once a search ends is
    defensive and was not mutated: Stop is offered only while a search runs, so a finished search's stop is never
    reached.

- **Revision 789 — a content search finds a text two buffers of one HTTP part split (M8):**
  - `ContentSearch` searches a buffer of an HTTP part with the end of the buffer right before it in its part, where
    that one was kept whole, of one encoding and, in UTF-16, ending at a code unit's end; a match the seam splits is
    the later buffer's, its first match begun that many bytes before its first byte, and matches are counted from the
    part's start, none overlapping another across it. A part's buffer flagged its last keeps no end, so only open
    parts hold one.
  - Its preview takes the bytes of both buffers; `icat content --find` lists such a record first at "4 B before" and
    says what that means, and the window's Find in kept content… says it holds the text "beginning 4 bytes before it,
    in the buffer before it in its part".
  - A match across three buffers, or across a buffer not recorded, not kept or cut, is still not found, and the
    sentence every answer carries says which matches a search can find.
  - Debug and Release each ran 3,250 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    fixture index names the three new tests. All 21 mutations were caught: the seams never read; a seam searched
    over the earlier buffer's last match, across a buffer never recorded, from a cut buffer, into a buffer flagged
    first, across two encodings or from a UTF-16 buffer ending inside a code unit, past a part's end, or across two
    events' parts; the later buffer's own matches counted from its start, the seam's match uncounted or not placed
    before its first byte, or the earlier buffer's last match forgotten; a tail too short for the preview, a preview
    misplaced or taken from the later buffer alone, or where it began unsaid; on the command line the cell unsaid,
    or its note never or always given; and in the window a match begun in the buffer before said as one at a byte of
    its record.

- **Revision 788 — a content search keeps to one process, never every holder of its PID (P6):**
  - `ContentSearch` keeps to the records bound to a process instance under an evidence policy, as every other view
    binds a record to its owner, and counts the process's records the policy leaves out; a PID and an instance at
    once, an instance the generation does not hold, or a policy InterCat does not define is refused.
  - `icat content --find --process <pid | instance>` names the process as `icat links` and `icat waits` do - by a PID
    one process alone held, else by its instance id, a reused PID refused with each of its holders - and says whose
    records it kept to, under which policy, and how many of them the policy left out; `--evidence-policy` chooses
    which bindings count, and `--pid` now says it keeps to whichever process held the PID.
  - The window's Find in kept content… keeps to the process selected in InterCat's window, named with its holder's
    number where its PID was reused, under the window's evidence policy, and says how many of its records the policy
    left out.
  - The live follow's journal allowance test waits for a publication after each burst begins, not for a count of
    chunks, so a burst a publication splits no longer lets later bursts pile into one chunk under load: revision 787's
    one Release failure.
  - Debug and Release each ran 3,247 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    allowance test passed in both under the suites' load, and three times alone beside eight spinning processes. The
    fixture index names the three new tests. All 25 mutations were caught: the search's instance or policy ignored,
    a PID and an instance taken at once, an undefined policy or an unknown instance taken, or the binding skipped;
    the records a policy left out uncounted, another process's counted, or the count not returned; on the command
    line the instance or the policy dropped, --pid taken with --process, --evidence-policy taken alone or unread,
    --process or --evidence-policy taken without --find, or whose records, which policy, what it left out, or that
    --pid keeps to any holder of the PID, unsaid; and in the window the instance or the policy dropped, the holder's
    number or what it left out unsaid, or the main window's selection not passed.

- **Revision 787 — M5: the content-export review:**
  - [The review](reviews/M5-content-export-review.md) lists every source file outside the store's own that keeps,
    reads, decodes, searches, shows, writes or packages kept content, with what leaves through it, who asks, the
    consent it needs and the tests that hold it, and the paths that never carry content, each with its tests; it
    passes, finding no path that leaves unasked or unconsented.
  - `ContentExportReviewTests` finds the files that name a content chunk or call a reader, decoder or writer of
    content, and fails when one has no row in the review or a row's file no longer does any of it.
  - The review found two statements no test held, and holds them now: an investigation's package says how much message
    content its sessions kept, and `icat content --json` refuses `--reveal`, `--save` and `--decode`.
  - Debug and Release each ran 3,238 tests with revision 473's 93 failures and 4 skips; Debug failed nothing else,
    and Release failed one test besides, a live follow's journal allowance, which took 21.7 seconds under the
    suites' load and passed alone three times in Release in 2 seconds each: it shares no code with this revision,
    and a follow that falls behind its 50 ms publications under load reaches the allowance it asserts is never
    reached. The fixture index names the two new tests. Nine of ten mutations were caught: a review's row dropped,
    or one naming a file that reads no content; raw records, chunks or the store's own code no longer watched; the
    investigation package's content unsaid, one session said as many, or its records miscounted; and --json taking
    --reveal. The tenth, counting comments, is equivalent today: no file names content in a comment alone.

- **Revision 786 — M8's exit review:**
  - M8's exit gate is met for HTTP through WinINet ([review](reviews/M8-release-review.md)): every implementation item
    and deliverable is checked against what ships and the tests that hold it, and each exit test against named tests;
    its limits are listed - only WinINet's HTTP/1.1 heads are decoded, decoders run in the process that asked, and a
    search reads each record on its own.
  - The plan's M8 text now says what M8 decided rather than what it first proposed: a decoder isolated in a worker
    once its parsing outgrows a bounded managed parser, decoded metadata kept out of every store and written only when
    asked, and a decoder list rather than a manager, since no decoder runs unasked.
  - Docs only: Debug and Release each ran the 32 architecture tests, which passed. Every test the review names
    passed in revision 785's suites, where Debug and Release each ran 3,235 tests with revision 473's 93 failures
    and 4 skips and failed nothing else.

- **Revision 785 — M8: a decoder's failure is contained:**
  - A decoder that throws on some bytes decodes nothing and says it failed and how, as a decoding of nothing under its
    name and version (`decoders-v1` §2), so the content viewer, a conversation, `icat content --decode`, `icat
    exchanges --decode` and an export of heads each say why where they would have stopped; the bytes stay as they are.
    Running out of memory is not contained.
  - Debug and Release each ran 3,235 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the new test. All seven mutations were caught: a failure left uncaught, or running out of
    memory contained; the failure unsaid; the part, or the decoder's version, unnamed; a decoding that did not fail
    copied rather than returned as made; and the failure's message left with its own full stop. Calling a decoder
    around the containment rather than through it was not mutated: no decoder fails on any input a test gives it, so
    no test could tell the two apart.

- **Revision 784 — M8: the window exports a process's exchanges with what their heads say:**
  - At a process's HTTP exchanges, Export with their heads… says first what its table holds and that it is content,
    and only then asks for a file: every exchange the rung lists in its scope, a line each, with what its heads say,
    as `icat exchanges --decode --format csv` writes them (`http-exchanges-v1` §5f), up to the records an evidence
    export holds; the window says how many it wrote, whether the limit left some out, and how many heads were not
    decoded.
  - The table is read in one pass of one generation (`SessionHttpExchanges.Table`), each head read whole from content
    kept with consent to inspect it; the command line and the window write a time after the capture epoch through one
    formatter, so their tables agree.
  - Four contract bullets revision 783 wrote with their dash doubled - `decoders-v1` §2 and §6, `content-v1` §4 and
    `http-exchanges-v1` §6 - are written again with one.
  - Debug and Release each ran 3,234 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the three new tests. All 22 mutations were caught, one once rewritten so it builds: a table
    not kept to its scope, past its limit, saying none were left out, read under a policy that does not admit the
    process, or for another session; its heads unread or set on another exchange's line; its process key or first
    time dropped; a time written to the millisecond; heads read for one position only, or one fewer than asked; the
    export offered everywhere, its offer never raised, or run off its rung; responses' heads left uncounted; the
    limit or a single head said otherwise; Choose table file focused first; a file asked for without asking first;
    what was written left unsaid; and the export's state not raised as its rows load.

- **Revision 783 — M8: the listed exchanges' heads written when asked (`http-exchange-heads-v1`, a table):**
  - `icat exchanges --decode` with `--json` or `--output` writes `http-exchange-heads-v1` (`http-exchanges-v1` §5f,
    ADR-071): the listing under its own name, saying it holds content and naming the decoder version, each listed
    exchange with its heads' decodings, each field with its bytes, and what each head declares of its body; a file
    written says beside it that it holds content.
  - `--format csv`, or a `.csv` path, writes the listed exchanges as a table, one line each, with what their start
    lines and Host say under `--decode` and why a head was not decoded; a cell a spreadsheet would evaluate begins
    with an apostrophe. Without `--decode`, `http-exchanges-v1` holds no head's value, not even as null.
  - `decoders-v1`, `content-v1` §4 and ADR-070 now name this export as the one place a decoding is written; the
    overview's entry for `icat exchanges` names `--decode` and the table.
  - Debug and Release each ran 3,231 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the new test. All 25 mutations were caught, one only once a test held a head recorded but
    not whole: the contract left unnamed, its content unsaid or its decoder unnamed; heads left off the listed
    exchanges, or content, decoder or heads written into http-exchanges-v1 as null; a table without its head
    columns, or with another exchange's heads on a line; a .csv path or --format csv writing JSON; --format taken
    without --output, or a table without listed exchanges; heads read unasked; the content caution unsaid; the
    request body set against the response's; a decoding's bytes dropped, a field's ends swapped, or its end unsaid;
    the process unnamed; a cell left for a spreadsheet to evaluate; a head never recorded left unsaid, or said to
    reach its end; a part's whole flag taken from whether it was recorded; and a status line's reason taken from a
    field line.

- **Revision 782 — M8: each decoder's fixture corpus pins its version:**
  - `fixtures/decoders` holds a corpus for each decoder at its version (`decoders-v1` §3a): the HTTP/1.1 head
    decoder's 24 heads and the content fixture decoder's 5 messages, each case's bytes as text with the decoding that
    version makes of it: the bytes it read, each field with its bytes, what it did not decode and whether it reached a
    head's end.
  - A test decodes every case as the viewer and the command line do and requires the corpus's decoding, and one corpus
    for each listed decoder at its version and no other, so a decoder whose decoding changes takes a new version with
    a corpus of its own.
  - Debug and Release each ran 3,229 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the new test. All nine mutations were caught: a value's optional whitespace kept; a line's
    carriage return kept in it; an empty reason taken for a field; a head's end left unmarked; the head decoder's
    version raised, or its identity renamed, with no corpus beside it; a byte outside visible ASCII written as
    itself; and the fixture's filler left unchecked, or its range cut one byte short.

- **Revision 781 — M8: from a buffer to its exchange's conversation:**
  - The content viewer of an HTTP buffer offers Its exchange's conversation…, which opens the conversation of the
    exchange the buffer belongs to (`http-exchanges-v1` §5d), found among its process's exchanges by its raw identity
    under the window's evidence policy; from there Show its buffers is not offered. A record of no exchange, or of a
    process the policy does not admit or no instance binds, says why it has none.
  - The viewer and Find in kept content… carry the window's evidence policy and clock for it, and at its smallest the
    viewer of a head's buffer offers every action, its bytes shown, without cutting one off.
  - Debug and Release each ran 3,228 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the two new tests. All thirteen mutations were caught, one only once a test held two
    exchanges whose buffers share an ordinal in two epochs: another session's folder answered; a record of no buffer
    searched; the first exchange taken for any buffer; an unbound process's exchange answered, or any policy
    admitted; a buffer matched without its epoch or its ordinal; the viewer's button offered for every record or
    never; the conversation left unopened, offering buffers from the viewer, or naming no process; and Show its
    buffers never hidden.

- **Revision 780 — M8: the window finds a text in kept content:**
  - Find in kept content… (Ctrl+Shift+F, beside Time inside calls…) searches the content kept with consent to inspect
    it as `icat content --find` does (`content-v1` §4): letters in either case, the selected PID's records alone, and
    the bytes around each match only when asked, as inert text. It lists each record found with its time, PID,
    mechanism and event and how often and where it holds the text, and says what was searched and what was not.
  - Enter on a record found, or Inspect its content, opens it in the content viewer, its bytes hidden until asked
    there. Ctrl+Shift+F is taken before Ctrl+F, which searches what the window draws, and F1's sheet lists it.
  - Debug and Release each ran 3,226 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the three new P2 and R15 tests. All eighteen mutations were caught: either case, the
    selected PID or previews dropped from the search; the first record found left unchosen or without the keyboard;
    Enter in the text or on a record ignored; the text left without the keyboard as the window opens; once said
    otherwise; records kept without consent, more than listed, or a session keeping none left unsaid; Find offered
    with no text; no record listed said otherwise; the PID option offered with no process selected; the button never
    offered; Ctrl+Shift+F left to Ctrl+F's search; and F1's sheet missing the key.

- **Revision 779 — M8: bounded content search (`icat content --find`):**
  - `icat content <session> --find <text>` lists the records whose kept bytes hold a text (`content-v1` §4), in
    reading order, each with its time, process, mechanism and event, how often and where first, and the locator that
    shows its bytes; `--ignore-case`, `--pid` and `--limit` narrow it, and `--previews` shows the bytes around each
    first match as inert text, which nothing else does.
  - Only bytes kept with consent to inspect them are searched, each record's on its own, within 256 MiB and 10
    seconds; what was not searched (kept without consent, past a budget, unreadable) is said and leaves the answer
    partial. It refuses `--json` and a record's locator.
  - Debug and Release each ran 3,222 tests with revision 473's 93 failures and 4 skips, and failed one test more:
    the overview's test of every command's forms, which the help's new --find form had outrun; with the overview
    naming it, the 238 command-line tests passed in both. The fixture index names the three new tests. All
    thirty-three mutations were caught: the answer whole with records unconsented or past a budget; an empty text
    taken; another session's folder searched; a session keeping none unsaid; every process searched, or a record's
    raiser taken for its owner; unconsented bytes searched or uncounted; the byte or time budget ignored, or checked
    after reading; UTF-16 unknown; the limit unmet or more unsaid; previews always shown; misaligned UTF-16 matches
    counted; matches counted overlapping; the last match taken for the first; letters left unfolded; previews
    unbounded before or after; a backslash left unescaped in either encoding; --find taken with --json or a record's
    locator; a limit of none taken; --pid, --previews or --ignore-case dropped; --previews taken without --find;
    records kept without consent unsaid; and the answer whole regardless.

- **Revision 778 — M8: each body beside the length its head declares:**
  - Where an exchange's heads are decoded, each body is set beside what its head declares of it (`http-exchanges-v1`
    §5e, RFC 9112 §6.3), reading none of its content: a Content-Length held, a body holding fewer bytes, more, or
    none, a body where none is declared, and a response to HEAD or of status 1xx, 204 or 304, which holds none. The
    conversation says each beneath its body, a disagreement in the caution colour, and `icat exchanges --decode` notes
    each disagreement.
  - A chunked body is not compared, since WinINet keeps it without its framing (FX-HTTP-003), nor a coded one, which
    was not measured; a head not decoded to its empty line declares nothing known, which a decoding now says
    (ReachedEnd).
  - Debug and Release each ran 3,212 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the two new tests, one a theory of 27 heads. All twenty-eight mutations were caught, one
    only once a head named its length in lower case and one rewritten to build: a head's end never or always
    reached, or one not reached trusted; a response to HEAD, or of status 1xx or 304, given a body; bytes beneath a
    bodiless response unsaid; every transfer coding taken for chunked, a request's chunking taken as measured, or
    transfer codings ignored; a request's unlengthed body unsaid, or a response's read as a request's; lengths that
    are not one taken; identity taken as a coding, or codings ignored; a held length unsaid; an unrecorded body
    taken as held; an unknown method said to disagree; a short body said to be long, or its whole buffers unsaid;
    field names matched in one case; a list left unsplit; agreements noted on the command line, or a response's body
    unnoted; the window's declaration hidden, a disagreement shown plain or an agreement in caution, or a response's
    body declared as a request's.

- **Revision 777 — M8: the window's conversation inspector:**
  - C on an HTTP exchange, or its inspector's Read its conversation, opens the exchange's request and response side by
    side (`http-exchanges-v1` §5d): what was recorded of each head and body, when it began, how long it took and which
    parts were not recorded whole, all from the records alone. Decode its heads reads both heads whole and says what
    each said (the request's method and target, of which host; the response's status and reason) above its fields and
    their bytes, and what was not decoded and why.
  - Decode its heads is offered disabled, saying why, where neither head was recorded whole; nothing decoded is kept.
    Show its buffers opens the exchange's buffers in the main window, where C shows a buffer's content. The heads
    query now finds a head's buffers by the segment names its index holds, so a reopened session's exchanges, read
    from its operation index, decode as a fresh grouping's do.
  - Debug and Release each ran 3,184 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the five new tests. All twenty-nine mutations were caught: the heads read from the group's
    first exchange, counted from none, or read past those listed; a head's rows read from the first segment named;
    heads decoded unasked; the first exchange read for every key, another session's folder read, every policy
    admitted, an exchange no longer held taken for the first, or a key naming none answered; the window decoding as
    it opens, offering heads not whole, or not saying none was recorded; Show its buffers closing without asking for
    them; both heads said undecoded; the host or the response's answer unsaid; what was not decoded, the fields or
    the decoder hidden; every part said whole, the parts not whole unnamed, or how long an unended exchange took
    unsaid; the keyboard left on Close; C doing nothing; the buffers not opened; the inspector's button hidden, its
    words generic, or its offer never raised.

- **Revision 776 — M8: `icat exchanges --decode` says what each exchange's heads say:**
  - `icat exchanges --pid <id> --decode`, or `--exchanges <n> --decode`, lists each exchange with what its request
    asked for (its method and target), of which host, and what its response answered (its status and reason), each
    head read whole with the HTTP/1.1 head decoder (`http-exchanges-v1` §5c). A head not whole, or kept without
    consent, is not decoded and leaves the answer partial; the listing says why beneath, as it does where a head stops
    being HTTP/1.1.
  - Every listed head is read under one lease in one pass over the session's segments, and only the exchanges listed
    are read; parts read together are each the part one read alone is. `--decode` refuses `--json` and `--output`,
    since a decoding is for a person and never kept, and refuses a listing of no exchanges.
  - Debug and Release each ran 3,179 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the three new tests. All thirty-one mutations were caught, one only once the parts test held
    a head without its last buffer beside the other head without its first: a part taking another exchange's, head's
    or process's buffers, parts answered out of order, no segment scanned or only those of exchanges not asked
    about, a request said by its method alone, the Host field matched in one case or the last one taken, a reason
    taken from a field line or never, the heads swapped or read at the wrong event, exchanges read past those listed
    or past the last, --decode taken with --json, --output or no exchange listed, ignored, or reading every
    process's heads, a head said unrecorded where it was undecoded or the reverse, the host unsaid, every exchange
    said whole, a response's notes unsaid or the two heads' swapped, the answer whole with a head undecoded, the
    plain table printed too, and the decoder unnamed.

- **Revision 775 — M8: the content viewer decodes a buffer's whole HTTP head:**
  - Shown, a buffer of an HTTP head offers Decode its part, which reads the head whole with the HTTP/1.1 head decoder
    (`decoders-v1` §5a): the Decoded tab names the part it read and lists each field with its bytes in the part, and a
    field's Show its bytes shows them in the whole part's hex view, staying there for the next field.
  - A head that is not whole offers Decode its part disabled, saying why, and is never decoded; a body's buffer offers
    no decoding. The part's toggle is now named by the label it shows, as a screen reader says it, with what it does
    beside.
  - Debug and Release each ran 3,173 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the new P2 test. All eleven mutations were caught, one rewritten to build: the part's
    decoding unoffered, the record decoded in its place, the button's label or spoken name left as the record's, a
    head not whole offered or decoded when asked anyway, the part read said to be the record's message, a field's
    bytes shown in the buffer or the view toggled back for the next field, the part unsaid in the status, and a
    whole head refused.

- **Revision 774 — M8 begins: HTTP/1.1 heads decoded from WinINet's whole parts:**
  - `contracts/decoders-v1.md` (ADR-070) fixes M8's decoder contract: a decoder's identity, name, version and what it
    reads, and a decoding made only when asked, from bytes kept with consent to inspect them, within 64 KiB and 250
    ms, a part only whole, its fields inert, and never kept; `docs/reviews/M8-release-review.md` answers §15.2 for it.
  - `icat content <session> <locator> --part --decode` decodes a whole request or response head into its request or
    status line and its field lines, each with its bytes (RFC 9112 §2 to §5), stopping at the first line HTTP/1.1 does
    not allow there and saying which and why; a head not whole, or kept without consent, is never decoded. A buffer
    decoded alone names the part to decode, and `icat content --decoders` lists the decoders.
  - Debug and Release each ran 3,172 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the five new P2 tests. All fifty-two mutations were caught: a part decoded without being
    one, a body or any HTTP record read as a head and a response as a request, a part not whole, kept without
    consent, empty or too long decoded or said otherwise, the 64 KiB bound or the time budget dropped, each way a
    head ends early said as another, a line's carriage return kept or a bare one allowed, the empty line read as a
    field, what follows it miscounted or its words disagreeing, a method, target, version, status code, status space
    or reason unchecked, a too short status line read, an empty reason given, a tab fold or a nameless field
    allowed, a value's spaces kept or its control characters allowed, the tab refused, a space written as hex, a
    digit or a special character refused in a name, and a version of any length or with any middle; and on the
    command line, a part's bytes unread for decoding, a decoding followed by its bytes unasked, the answer taken
    from the bytes alone or a decoding that read nothing said whole, the part unsaid where it was read, the buffer's
    word misplaced, and --decoders taking more.

- **Revision 773 — M12's exit review:**
  - `docs/reviews/M12-release-review.md` closes with M12's exit review: each implementation item and deliverable of
    plan M12 is built on the command line and in the window, held by named tests; every exit test is met, the causal
    test by the P8 tests of revisions 765 and 766 and the outlier labels by revision 768's R18 test; and M12's limits
    are listed. M12 is complete.
  - `comparison-v1` no longer lists recurring patterns, outliers and causal navigation as undefined: `patterns-v1`,
    `outliers-v1`, `links-v1` and `waits-v1` define them.
  - Docs only, with no source changed since revision 772's suites: each of the 36 tests the exit review cites, 33 by
    name and 3 by method, passed in both of them, every case of a theory included; the architecture tests pass in
    Debug and Release, 32 each, reading every ADR, contract and section the review names.

- **Revision 772 — M12: notes in the Compare window:**
  - The Compare window, showing a comparison its investigation saved alike - the same two intervals of the same
    sessions, by the same metric and rows; of several, the one it was opened as or last saved under, else the first
    saved - says the notes about it beneath its rows, a row's by its rank and ends or as one it does not compare, and
    marks each row a note is about, beside its ends and in what a screen reader says of it (`comparison-v1` §8c).
  - *Add a note…* writes one in the investigation, about the whole comparison or, chosen instead, about the row
    chosen, by its key (`workspace-v23` §7, ADR-069), and says why not where the session was opened on its own, where
    the comparison shown is not one saved, and where the investigation cannot be read.
  - The window keeps two rows in view at its smallest, now 720 × 660: its buttons wrap where it is narrow, what is
    said beneath the rows scrolls within its share and grows with the window, and its controls' names begin with what
    they show; a test checks each, with the note prompt.
  - Debug and Release each ran 3,167 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the four new tests. All forty-three mutations were caught, one rewritten to build: in the
    investigation, a saved comparison taken for one of another baseline session or interval, another compared
    session or interval, another metric or grouping, a capture's baseline read as the compared session's, a null
    comparison unrefused, and the name a window knows ignored, matched by case or put last; in the Compare window,
    the name it opened as or saved under forgotten, the notes unread with a comparison, after a save or after a
    note, the chosen row not kept, noted rows unmarked in the list or to a screen reader, the reasons for no note
    swapped or unsaid, none counted as notes, the first row said not to be compared, the prompt's row counted from
    zero, a row's note said as the whole comparison's, a note offered while one is written or about a comparison not
    saved, or not offered once one is read, the action line always shown, and what is said beneath the rows kept
    short in a taller window; and in the note prompt, no choice made first, a row offered with none chosen, the
    whole comparison noted as the row, and its title and the row's words unsaid.

- **Revision 771 — M12: notes about saved comparisons and their rows:**
  - `icat workspace note <workspace> <text> --about <comparison> [--row <key>]` keeps a note about a saved comparison,
    or about one of its rows by the key `icat compare --json` gives it, never by its rank, which a run again may
    change (`workspace-v23` §7, ADR-069). A comparison removed later keeps its notes, said to be about one no longer
    saved; `icat workspace show` and the investigation window say what each is about.
  - `icat compare --workspace <workspace> --saved <name>` says the notes beneath its rows, a row's by its rank and
    ends in that run, or as one the run does not compare, and its JSON's `saved.notes` carries each with its row's key
    and rank; a redacted report carries none. A file of version 22 or before holds no such note.
  - Debug and Release each ran 3,163 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the two new R22 tests. All twenty-eight mutations were caught: in the investigation, a
    comparison's name kept as typed rather than as saved, a row's key untrimmed, an empty or too long key allowed,
    what a note is about dropped when it is reworded, the notes about a comparison found by case or by an untrimmed
    name, a removed comparison said to be saved, a row unsaid, and a note about a comparison read in a file of the
    version before, one about a comparison never saved, one both pinned and about a comparison, or one naming a row
    by a blank key; in the investigation window, such a note's place said as the whole investigation's, the two
    reasons a note has no place on the timeline swapped, and what a note is about dropped from its row; and on the
    command line, --about taken by another command or with --at, --row taken without --about, --about or --row
    ignored, show silent on what a note is about, and the notes unread, unsaid, ranked from two, unlisted in JSON or
    without their rank there, or the first row's said not to be compared.

- **Revision 770 — M12: outliers in the window:**
  - Outliers…, beside Compare with… while the window has a time scope, judges the scope as `icat outliers` judges an
    interval: against all the session keeps before it, or all after it, by process or by executable, the rows above or
    below every window listed in the command line's words, with the baseline and its windows, how often chance alone
    puts a row there, and what is not judged (`contracts/outliers-v1.md` §7).
  - The chosen row's windows are drawn bar by bar beside the scope's, on one scale, with dashed lines at the most and
    the fewest any window held; Left, Right, Home and End move along the bars, each said in words. Show in the window
    opens the row as a comparison's row is opened. The command line and the window share the words for a reading with
    no row, none judged and none outside.
  - Debug and Release each ran 3,161 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the three new tests it indexes. All forty-two mutations were caught, one once a constant
    that left a field unread became one that reads it: in the window, the time after offered where none is left, the
    time after said of the recording, the baseline before taken whatever was chosen, the grouping ignored, the time
    after cut short, too few windows unchecked, an older generation shown, a newer generation said always, the
    policy unsaid, the baseline and its windows unsaid, the coverage run together, rows within listed, no row chosen
    first, the chance unsaid, what is not judged or how executables are grouped unsaid, the chance's line shown
    empty, a row offered while the rows are judged, no row outside said as none judged, why InterCat's window cannot
    show a row ignored, the details shown with no row, the chart kept with no row, a reading with no row said as one
    with none judged, the scope's records said without where they fall, the windows miscounted, the chart drawn of
    the first row whatever is chosen, Enter and Escape ignored, and a row chosen though it cannot be shown; in the
    chart, its cursor started on the first window, Left moving right, Right moving left, Home going to the scope,
    End to the first window, the cursor moved past the scope, a window's records read from the first, windows
    numbered from zero, how far beyond said of a row within, and bars counted with no row; in InterCat's window, the
    row chosen not opened and the rows judged under another policy; and in the words, a reading by executable with
    no row said as one by process.

- **Revision 769 — M12: the time inside calls in the window:**
  - Time inside calls… lists the waits in `icat waits`'s words, the most time first: each caller's calls by interface
    and by the process that served them, or why none is named, with their calls, failures, time in calls and the
    server's own time over the linked calls. The chosen wait says its median, 95th percentile and longest beside the
    servers callers waited on, and what no time is said for is said beneath (`contracts/waits-v1.md` §7).
  - Opened with a process selected, it lists that process's calls and the calls it served, as `icat waits --process`
    does, until the box beneath is unticked. Select caller and server, or Enter, makes the wait's caller and server
    the selection together, as a pattern's processes are (ADR-065); a wait no server is named for goes to its caller.
  - Debug and Release each ran 3,157 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the three new tests it indexes. All thirty-one mutations were caught: in the window, one
    process's filter inverted, the box ignored, the list started wide, the chosen wait forgotten, who served said
    the wrong way round, a share listed where none is measured, failures unsaid, the calls it served counted from
    those it made, callers counted once per wait, the policy unsaid, a newer generation said always, an older
    generation shown, a row offered with none chosen, the box offered with no process selected, one process said as
    two, the server dropped, details shown with no wait, what no time is said for hidden, an unmeasured share said
    as one, the median taken for the 95th percentile, the calls it served listed from those it made, the side list's
    heading swapped, an empty side list shown, the headings of no call and of none made swapped, and Enter and
    Escape ignored; and in InterCat's window, the button offered among the source records, its tip silent on the
    selected process, the selected process not passed, the caller alone selected, and the graph left without the
    keyboard.

- **Revision 768 — M12: optional outlier detection against a baseline's windows:**
  - `icat outliers --interval <start:end>` cuts the baseline - all the session keeps before the interval unless
    `--baseline` names another that does not overlap it - into as many windows as long as the interval as fit, at
    least 19 and at most 199, spread evenly across it, and lists the rows whose records over the interval are above or
    below every window's, the most records beyond first, each with its windows' fewest, median and most.
  - Nothing is fitted: simulated bursty arrivals put as many as 5 rows in 100 above a fitted 99% range by chance, so
    the rule is a rank, whose chance holds whatever records do: at most 1 time in one more than the windows each way.
    The reading says how many rows chance alone would put there. Rows the comparison cannot compare are not judged,
    each reason counted; bytes are not judged; no label says why a row differs. `--group-by executable` and `--json`
    (`outliers-v1`) as `icat compare` takes them (`contracts/outliers-v1.md`, ADR-068).
  - Debug and Release each ran 3,153 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the six new tests. All forty-nine mutations were caught: in the windows, an overlap of one
    tick allowed, an adjacent baseline taken for an overlapping one, too few windows allowed, the fewest raised, the
    most uncapped, windows cut end to end from the start rather than spread, no window said as some, the baseline
    wanted never offered, and an interval at the session's start given a baseline; in the judgement, rows not
    comparable judged, a row at the most or the fewest taken for outside, those within first, the fewest beyond
    first, the most taken for the fewest, the median taken from above, how far beyond measured from the wrong end on
    either side, and the chance taken as the windows alone; in the read, the windows given the compared interval's
    records, read one interval early, or grouped by process whatever was asked; in the words, filled windows said
    spread, one count said as a range, above said as below, within said as a number, none judged counted, its and
    their swapped, a chance said of no row or over the windows alone, reasons fewest first or out of order, and a
    row without a reason unsaid; and on the command line, a limit of none taken, a named baseline ignored, too few
    windows unchecked, the grouping ignored, rows within listed, the limit ignored, the rows beyond it unsaid, no
    row said as none outside or by process, nothing judged unsaid, the chance and what was not judged unsaid, and in
    the JSON the listed rows unlimited, below counted as above, ranks from zero and the windows' records cut.

- **Revision 767 — M12: bottleneck views over validated call spans:**
  - `icat waits` lists the time each process spent inside the RPC calls it made, from each call's start to its stop as
    paired on the caller, grouped by interface and by the process whose server calls served them through ALPC, the
    most time first: calls, failures, time in calls with its median, 95th percentile and longest, and, over the calls
    linked to a server call that completed, the server's own time beside the caller's. The servers callers waited on
    follow, each caller counted once.
  - A call without a validated span, or whose caller the evidence policy does not admit, is counted by why and never
    timed; calls no server is named for say why. No wait is said to have blocked or delayed anything, since calls
    overlap and can return before their work is done. `--process` lists one process's calls and the calls it served;
    `--json` writes `call-waits-v1` (`contracts/waits-v1.md`, ADR-067).
  - Debug and Release each ran 3,147 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the six new tests. Of forty-one mutations, forty were caught, three of them once a removal
    that left a field never assigned became adding nothing, and two on the servers' lines rewritten to drop the one
    that survived, which computed the servers for one process, where they are never shown: in the reading, server
    calls taken for waits, callers the policy does not admit timed or uncounted, calls without a validated span
    uncounted, a span reversed, a failure missed, a server the policy does not admit named or uncounted, a server's
    time unmeasured or its calls' times uncounted, unresolved calls unlisted or zeroed, the least time first, the
    fewest calls first among equals, a caller counted once per interface, the servers with the least time first,
    failures uncounted and the longest call taken for the sum; in the words, a server unnamed, one the policy does
    not admit unsaid, a single reason counted, reasons fewest first, a share said without a linked call or divided
    by nothing, a share's subset unsaid, calls without a validated span or not admitted unsaid, and states fewest
    first; and on the command line, a limit of none taken, one process's filter inverted, the calls it served taken
    from those it made, nothing said where it made none or served none, the limit ignored, the rows beyond it
    unsaid, the servers unlisted or written for one process, and what no time is said for unsaid.

- **Revision 766 — M12: the window follows a process's links:**
  - Follow its links…, for the selected process, lists what led to it and what it led to side by side in `icat
    links`'s words, each link with its evidence and strength; links the evidence policy does not admit are listed
    apart, the processes created near it in time as text, and what of it links to nothing beneath them.
  - Enter follows the chosen link to the process at its other end, either way along it, and the path walked is said
    above; Alt+Left or Back returns a link at a time. A link to another host or an end no record holds, and one the
    policy does not follow, are never followed, each saying why on Follow. Select this process makes the process
    reached the selection, the graph then having the keyboard (`contracts/links-v1.md` §8).
  - Debug and Release each ran 3,141 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names four of the five new tests, the fifth named by its section. All thirty-three mutations were
    caught: in the window, a link left chosen in both lists at once; Enter, Back, Follow, Select this process,
    Escape and Alt+Left each unwired or ignored; an older generation shown, a process no longer held listed, the
    coverage unsaid, the words for empty lists shown before any reading, the path walked said out of order or always
    shown, a newer generation unsaid, every strength said to be followed, the words for a list with no link hidden,
    links not followed always headed, an unwitnessed creation or the absence of any near process unsaid, what links
    to nothing always headed, a link not followed or one to an address followed, a step not kept or not left on
    Back, nothing selected, the keyboard given first to what led to it or with no link chosen, and a row heard
    without its evidence; and in the main window, the process reached never selected, the window's evidence policy
    ignored, its links offered with no process selected, and the reason none was selected unsaid.

- **Revision 765 — M12: causal navigation over the links the evidence records:**
  - `icat links --process <process>` lists what led to a process and what it led to, each link a recorded fact by one
    rule: a child's creation record naming its parent, by its start key or its PID alone; a client call's one ALPC
    message reaching the server call that served it, counted by interface; and a TCP connection the capture saw one
    end make, counted by the port it arrived at, to a process, another host by address, or an end no record holds.
    Each is listed with its evidence in words and its strength.
  - Only the links the evidence policy admits are followed; the rest are listed as not followed. The processes created
    within a second of it and linked to it by nothing are listed as near in time, never as a link, and what of it
    links to nothing is counted by why. `--to <process>` finds the shortest chain of followed links between two
    processes, through processes alone, as strong as its weakest link, or says why none leads there; `--json` writes
    `evidence-links-v1` (`contracts/links-v1.md`, ADR-066).
  - Debug and Release each ran 3,136 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the seven new tests. All sixty-two mutations were caught, two of them once a swap of two
    arms that would not compile became two changes of words: in the reading, a start reversed, an unheld parent
    misnamed, calls no client reached uncounted, unresolved calls zeroed, an RPC group's sides swapped, a
    connection's maker swapped, flows and connections opened at neither end uncounted at either end or one-sided, an
    end on this machine taken for a host, a connect or accept seen at one end ignored, creations misscaled, kinds
    reversed or the most held listed last, a process's link to itself said to lead to it, links not followed listed
    as followed or a candidate start never listed, linked processes listed as near, the second's boundary made
    exclusive, the earliest listed first rather than the nearest, candidates chained, a chain backwards and its
    strongest link taken for its weakest; in the words, each kind's roles swapped, a PID taken for a start key, a
    reused PID's later holder unsaid, one call or connection said as several, a host's sides swapped, an unseen
    end's sides swapped, before and after swapped, the same reading unsaid, one unresolved reason counted, reasons
    fewest first, a sentence left uncapitalized, flows, unopened connections and unreached calls unsaid, a host
    named as an unseen end, a start said backwards, and unknown coverage labelled twice; and on the command line, a
    policy in kebab form refused, a number taken for a policy, a reused PID taken, an unknown instance taken, a
    chain to itself allowed, a weakest strength given where no chain was found, links not followed headed where
    there are none, an unwitnessed creation unsaid, what links to nothing left unheaded, the host note given for any
    end, every strength said to be followed, a chain's connection said from the end it reached, and the hints for a
    chain through links not followed and for one the other way unsaid.

- **Revision 764 — M12: the window lists the recurring communication patterns:**
  - Recurring patterns…, beside Browse paired channels, lists what `icat patterns` lists, in its words: the recurring
    patterns, the heaviest first, each with how often it occurred, its clients and servers counted and its tenths, and
    those that occurred once on request. The chosen pattern says what its signature is made of, when it occurred, and
    every end in its role with its occurrences, a program named where the process's own name does not say it; beside
    the list are the generation, the counts, the coverage, the evidence policy where it is not the default, what no
    pattern holds, and why the list holds none where it does.
  - Select its processes - or Enter, or a double click - makes the pattern's processes the selection together, as
    Ctrl+click does, or goes to its one process; the graph has the keyboard, and Enter lists their records. A process
    only a newer generation holds is left out, and the window says so (`contracts/patterns-v1.md` §7, ADR-065). The
    command line and the window now share their words: `icat patterns` names the evidence policy as
    `--evidence-policy` takes it, says why a session holds no pattern, and the patterns contract's list of what it
    does not define is bullets again.
  - Debug and Release each ran 3,129 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names six of the seven new tests, the seventh named by its section. All fifty-one mutations were
    caught, four once a mutant that left a field unused or a message unbalanced was rewritten and the tests waited a
    bounded time for the window to close: in the shared words, an RPC end's calls made and served swapped,
    connections counted as flows, clients and servers swapped, a missing client or a TCP pattern's missing server
    said to be unrecorded, the evidence policy always blamed or named untyped, processes of no witnessed executable
    counted as a program, the coverage's label kept, and the last occurrence said as the first; an enumeration cited
    with a leading hyphen or none; on the command line, a session with no pattern and one with none recurring
    confused; in the window, its actions offered while it reads, an empty session unsaid, the once box offered with
    nothing to add, one process labelled or described as many, an unchosen pattern taken as chosen, details shown
    with none chosen, the host note kept or never shown, what no pattern holds always shown, none recurring taken
    for none at all, a pattern's ends kept once it is unchosen, those that occurred once never listed, the chosen
    pattern forgotten when the box changes, an older generation shown, a newer one or the policy unsaid, the
    coverage or what no pattern holds unsaid, the keyboard given to Close or to the list, Enter and Escape ignored,
    Select its processes and the box unwired, a process connecting to itself counted twice, an executable no record
    witnessed unsaid, a program never said or said where the process's name says it, and clients and servers
    swapped; and in the workspace and its window, processes selected among the records, none held taken as chosen,
    one process made a set, every process chosen, the graph left without the keyboard, the reason none was chosen
    unsaid, the patterns offered among the records, and the window's evidence policy ignored.

- **Revision 763 — M12: recurring communication patterns:**
  - `icat patterns` groups every TCP connection the capture saw open, UDP flow and RPC call of the current generation
    by a structural signature: its mechanism, its service port or RPC interface and procedure, and whether its server
    end is here or at another host - never a process, address, client-chosen port or time. A TCP connection's server
    is the end seen accepting it; a UDP flow's service port is the one outside the dynamic range, the lower where both
    are; an RPC call's side is its role.
  - It lists the recurring patterns, the heaviest first, each with its clients and servers - processes and the
    programs they ran, hosts by address, ends no record holds - and in how many tenths of the session it occurred;
    `--pattern <rank>` says what a signature is made of and lists every participant, `--all` adds those that occurred
    once, and `--json` writes `communication-patterns-v1`. A connection seen open at neither end, a flow between two
    dynamic ports, a call no start named and what the policy did not admit are counted apart
    (`contracts/patterns-v1.md`, ADR-064).
  - Debug and Release each ran 3,122 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the four new tests. All forty-three mutations were caught, two once the tests held a paired
    connection seen open at neither end and asked for the last pattern listed: in the reading, a place or procedure
    dropped from a signature, a connection's or flow's roles swapped, its later record taken for its start, unopened
    connections and dynamic flows uncounted, every relation, connection or call admitted, an end on this machine
    taken for a host, an accepting end made the client, a connection here placed at another host, a flow at another
    host placed here, an RPC call's sides swapped and its unadmitted calls uncounted, the lightest pattern first,
    ties ranked backwards, a weight summed, a single occurrence called recurring, a dynamic port taken for a service
    port, the higher of two ports taken, the dynamic range begun a port late, a moment past the end placed in no
    tenth or tenths cut in nine, a tenth counted twice, the first occurrence taken from the last, the fewest
    participants first, and here and elsewhere swapped in words; and on the command line, a single occurrence never
    listed, two listed by default, the last rank refused, a limit allowed beside a pattern, processes of no
    witnessed executable counted as a program, an end no record holds unsaid, dynamic flows, unopened connections
    and the unadmitted unsaid, connections and flows named for each other, and the last occurrence said as the
    first.

- **Revision 762 — M12: a comparison shown on the graph:**
  - Show on the graph, in the Compare window, closes it and marks each edge the main window's graph draws by how the
    rows it draws changed: ▲ up, ▼ down, + appeared, − disappeared, = unchanged, ? not comparable, ± up and down, a
    glyph in a ringed disc on the edge's stroke, clear of its nodes and of a weak relation's middle ring, staggered
    between edges drawn side by side.
  - An edge's rows are its relationships' and, to a host or Other hosts, its processes' connections to the addresses
    it draws; one that cannot be compared makes the edge not comparable. Of a comparison by executable, a row marks
    every edge of its processes, its ring broken and its words saying it is of the executables' processes together.
  - A line beneath the graph's header says the intervals, the counts with their glyphs, the edges with no record in
    either interval and the rows no edge draws, with Clear beside it; each edge's card and the relationship table, by
    a glyph column and item status, say each change. The marks follow every drawing and newer generations, which the
    line says it has not read, and go with the session (`comparison-v1` §8e, ADR-063).
  - Debug and Release each ran 3,118 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the eight new tests. All sixty-eight mutations were caught, one once the test gave Clear the
    keyboard before pressing it, since a raised click had left the graph holding it: in the marks, a host's or an
    executable's rows unmapped, a pair keyed by its source twice or unordered, a host's edge drawing no address and
    Other hosts' the opened ones, an unmarked edge uncounted, a row counted in one edge or inside a node it is not
    in, sharing blind to a node's inside or claimed of every row, no relationship shared, a drawn row counted
    inside, an edge's rows unranked, the table read from edges alone, not comparable ignored, unchanged taken from
    one row, rows from nothing read as rising and to nothing as falling, steady rows splitting a direction, both
    ways read as up, a glyph and a word swapped, a reason and a grouping unsaid, the counts and a breakdown out of
    order, a capture unnamed, the metric swapped, and the unmarked, inside, undrawn and shared sentences unsaid; in
    the window's model, a drawing's marks never renewed, another session's comparison shown or adopted, clearing
    none said, an older generation unsaid, an unmarked edge's card silent, a new drawing or time base unsaid, and a
    card silent of its mark; in the graph, marks keyed apart from their edges, a ring, a lane or a node ignored, a
    mark unclamped or put at a short edge's node, a broken ring drawn whole, a glyph undrawn and a hovered card left
    stale; in the Compare window, the graph offered while reading, sharing or saving, or of no row, the window
    closed without its comparison, and its offer left unrenewed around a save; and in the main window, the
    comparison not carried to a newer generation, the timeline left filling the column, the keyboard not given to
    the graph on showing or clearing, a row's glyph unset, a highlighted row's change unsaid, and rows not marked
    again.

- **Revision 761 — M12: the Compare window draws the chosen row's difference timeline:**
  - Timeline, a check box among the Compare window's choices, reads the comparison again with its difference timeline
    (20 columns of each interval, or as many as a tick allows, the listed rows' columns kept) and draws the chosen
    row's beneath the rows: a slot per column pair, the baseline's bar beside the compared interval's on one rate
    scale, every column pair that cannot be compared hatched as the timeline hatches what the capture did not cover.
  - Left, Right, Home and End, or a click, move a column cursor, whose column the line beneath states in `icat
    compare`'s words with its reason, and a screen reader hears as the chart's name; another row chosen draws its
    columns with the cursor where it was. Two captures are cut each on its own clock. Unchecked, the chart goes and
    the comparison shown stays (`comparison-v1` §8a, §8d).
  - Debug and Release each ran 3,110 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the new R21 test. All nineteen mutations were caught, one once it compiled, since taking out
    the chart's telling of its cursor's move left its event unused, which the build refuses, so it was told only for
    a column below zero, and one once the test kept the cursor at the second column, where the rows differ, rather
    than the first: in the chart, Right, Home or End leaving the cursor where it was, Left stopping past the first
    column, a click ignored or taken as the next column, the first column said whatever was chosen, a reason unsaid,
    the cursor sent back to the first column for another row, and its moves untold; and in the window, neither one
    session's nor two captures' columns ever read, the box ticked with no reading, the chart kept in view unticked,
    columns past one hundred asked for, one row's columns kept of all listed, the chart not redrawn for another row,
    the wrong line said with no row chosen, and the columns read dropped.

- **Revision 760 — M12: a row's difference timeline (`icat compare --timeline`):**
  - `icat compare ... --timeline <rank> [--columns <n>]` states the ranked row's difference timeline in place of the
    table: each interval cut into the same number of equal columns, 10 unless `--columns` says, and the k-th of the
    baseline compared with the k-th of the compared interval as two intervals are - rates over each column, a change
    only where both columns covered the row's mechanism and the capture collected it alike, both values with the
    reason elsewhere, quiet columns too. Two captures are cut each on its own clock (`comparison-v1` §8d, ADR-062).
  - The columns are read with the comparison, at its generation, so a row's columns add up to its whole values;
    `--json` and `--output` write them with the comparison, each column with its two intervals. A rank past the rows,
    columns without a timeline or shorter than a tick, and a timeline with `--limit` or in a redacted report are
    refused in words. The comparison engine now builds any two intervals' rows from one read, which both the whole
    comparison and its columns use.
  - Debug and Release each ran 3,109 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the four new I12, I16, R18 and R21 tests. All twenty-nine mutations were caught, one once it
    compiled, since leaving the written file's timeline unsaid passed a timeline that may be missing, which the
    build refuses, so it was said only for ranks below zero: in the engine, every column cut as the first, the whole
    compared as the first columns, a baseline column compared with itself, quiet columns dropped, every row kept
    whatever was listed, a column's coverage taken from the whole compared interval, the whole intervals' native
    readings judging a column, columns past the ticks or beyond one hundred or none, no row listed, two captures'
    columns shifted, one session taken as two captures, and two captures' columns judged by their whole readings;
    and on the command line, a column's change or value stated over the whole intervals, a rank past the rows or the
    first row whatever the rank, two columns by default, columns alone or unbounded or rank zero allowed, a timeline
    with --limit or in a redacted report, the JSON without it, columns past the ticks left to fail, two captures cut
    as one session, the rates note kept, and the written file's timeline unsaid.

- **Revision 759 — M12: the window saves a comparison in its investigation, and reopens a saved one:**
  - The Compare window's Save in the investigation… keeps the comparison shown in the investigation its session was
    opened from, under a name a prompt asks for, which says what is kept and, as a name is typed, whether it replaces
    a comparison of that name. A baseline capture the investigation does not hold is refused with what to do, and a
    session opened on its own is told it has no investigation to keep one.
  - The investigation window's Comparisons… lists the comparisons in force in `icat compare`'s words and opens one in
    InterCat's window: its compared session at its compared interval, and the Compare window on that interval with the
    saved baseline offered first and chosen, by its metric and rows - another capture's read from its folder - even
    where the session's records end before it, or a session already open on its own. A session not where it was last
    found is named, to relink; Remove keeps the revisions. `comparison-v1` §8c now follows §8b, where it belongs.
  - Debug and Release each ran 3,105 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the two new R22 tests. All thirty-five mutations were caught, one once it compiled, since
    sending another capture's interval to the session's own clock left a folder that may be missing, which the build
    refuses, so it was sent there only for revisions below zero, and two once the test opened a comparison past the
    session's records on a freshly opened session, whose earlier openings had left a time scope behind: in the
    Compare window, a saved baseline offered last, its metric or rows or its other capture's folder ignored, an
    opening without that folder offered, the other capture's interval labelled on the session's clock, the compared
    interval not said to be saved, saving offered with no investigation, a baseline capture the investigation does
    not hold saved anyway or checked by the session shown, the names kept unread, a name told apart by case, an
    unnamed comparison offered, the save left unsaid, the metric or rows or the baseline's session or interval not
    the shown one's; in the investigation window, Comparisons unwired, a session not where it was found opened
    anyway or said of the wrong session, and two captures opened as one session's or without the other's folder; in
    its dialog, removed revisions listed, Remove or Enter unwired, the empty list unsaid, and Open offered with
    nothing chosen; and in the main window, the saved interval replaced by the time scope, a time scope required,
    the investigation the session was opened from or the one the comparison came from ignored, the Compare window
    never shown, and the investigation's opening unwired.

- **Revision 758 — M12: an investigation saves a comparison, and `icat compare --saved` runs it:**
  - `icat workspace comparison <workspace> <name>` saves a comparison of two intervals of the investigation's sessions
    under a name: one session's two, or with `--baseline-session` two captures', which compare by executable alone; by
    records or bytes sent across; a row per process or executable. Saving under a name replaces the comparison of that
    name and `--remove` removes it, every revision kept (`workspace-v22` §7, ADR-061). It keeps the question and never
    an answer: each interval names its session and is in that session's own ticks, so it needs no alignment and no
    alignment changes it.
  - `icat compare --workspace <workspace> --saved <name>` runs it with every output `icat compare` writes, each
    session read where the investigation finds it now, at the generation found there; its text and its file name the
    comparison and its revision, a redacted report never does, and a session not where the investigation recorded it
    is named with why. `icat workspace show` lists those in force in `icat compare`'s words, and a package of the
    investigation carries them, their names counted among a person's own words. An unreadable investigation is refused
    as corrupted input, as `icat workspace` refuses it, and the overview help now lists `--group-by`,
    `--baseline-session` and `--output`.
  - Debug and Release each ran 3,103 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the two new R22 tests. All fifty-four mutations were caught, two once they compiled, since
    leaving out the line that names the saved comparison run read a run that may not be there, which the build
    refuses, so the line was skipped for every revision instead: in the investigation, a session of no member, an
    empty interval, two captures by process, or a metric or grouping no comparison offers saved, a name untrimmed or
    unbounded, a removal keeping its baseline, the first revision or a removed one in force, a name told apart by
    case or untrimmed when named, a revision numbered twice, and a file of the version before holding comparisons,
    or one naming a revision twice, a session of no member, two captures by process, a partial removal, an untrimmed
    name, an unknown metric or an empty interval, read; in words, the other capture unnamed, the metric or the rows
    misstated, and a removed comparison described; in a package, comparisons uncounted or their names unsaid; and on
    the command line, a session directory, --interval or --group-by taken with --saved, either option alone
    unrefused, the sessions or intervals swapped, the metric or grouping not the saved one, the run unnamed in JSON,
    on screen or with a written file, a session not where it was recorded run anyway, the baseline capture dropped,
    the hint left as it was, comparison options taken elsewhere, the required ones or --remove's limits unenforced,
    --interval taken by view, the baseline session, default grouping, ranking or grouping ignored when saved, the
    compared interval saved as the baseline's, and the comparisons left out of show or its JSON.

- **Revision 757 — M12: the window compares by executable and with another saved capture:**
  - The Compare window's Rows choice compares each process or each executable's processes together, as `--group-by`
    does, so a restarted process's program keeps one row on screen too. Another saved capture…, among the baselines,
    asks for a saved session's folder and compares the time scope with its whole recording, by executable, which Rows
    then says is the only way two captures compare; the latest capture chosen is the one offered. A folder that holds
    no session, or the session shown, is said beside the buttons and the earlier baseline comes back, its comparison
    still shown, not read again.
  - For a row of executables, Show in the window opens an executable's rung, its processes listed - of a pair, the
    first the session shown ran - where the window groups by executable; under terminal-session grouping, or for an
    executable only the other capture ran, Show says why instead. The status names both captures' generations, the
    notes say how the rows pair them, and the line beside the buttons now reports whichever action the window took
    last: sharing, or choosing a capture.
  - Debug and Release each ran 3,101 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the three new I12 and R22 tests. All twenty-one mutations were caught, one once it compiled,
    since taking out the guard that keeps a put-back baseline from being read again left its flag unread, which the
    build refuses, so the guard was made always true instead: in the workspace, an executable's rung opened under
    any grouping or for an executable no process ran, a pair shown or opened by its first executable only, and a
    host's row not opened; in the window, the Rows choice ignored, another capture not offered, a declined choice
    not put back, the session shown taken as another capture, a refusal or a folder holding no session left unsaid,
    every capture chosen kept offered, two captures' rows not made executables' or Rows still offered for them, two
    captures compared as one session's intervals, the status saying processes for executables or leaving out the
    baseline capture's generation, the grouping's notes dropped, Show offered where the window cannot open the row,
    and a put-back baseline read again; and in the main window, the chosen row not opened.

- **Revision 756 — M12: two captures compared (`icat compare --baseline-session`, ADR-060):**
  - The question after an update, or between a machine that misbehaves and one that does not, is what changed between
    two captures. `icat compare <session> --interval … --baseline … --baseline-session <other>` reads the baseline
    interval from another capture and compares the two by executable: each capture's processes grouped by the path
    their image was witnessed at, a row of one paired with the other's of the same path, executables named over both
    so two of one name are told apart. No process of one capture is taken for another's - PIDs and instances do not
    carry over - and the pairing is said to be a grouping, never an identity, with whether the two captures name one
    host identity.
  - Each interval is on its own capture's clock and each capture is read at its own generation, both named. A change
    is stated only where both captures covered the row's mechanism and collected it through the same descriptors, each
    capture's own epochs alike first, so a changed capture profile never reads as a workload change; elsewhere both
    values stand with the reason. Identical captures compare as equal, M12's first exit test across captures. One
    session given as both captures is refused, and so is comparing two captures by process.
  - `--json` names both sessions, generations and the host identity's sameness, and a shared report says it compared
    two captures, each on its own clock, naming neither session.
  - Debug and Release each ran 3,098 tests with revision 473's 93 failures and 4 skips; Debug failed one more, by
    chance: the window's support bundle test looked for a port's digits in the bundle's own write time, which read
    ".1440404". That test, and the command line's like it, now look for the ports everywhere but the bundle's times
    and the session folder's random name, and pass in both. The fixture index names the four new I12, I14 and R21
    tests. All twenty-seven mutations were caught: across the two ledgers, their descriptors unweighed, the baseline
    capture's own epochs unchecked, or the compared capture called the baseline's; in the comparison, one session
    taken as two captures, the baseline capture's interval unread, executables named from one capture only, two host
    identities taken for one, the baseline's generation named as the compared one's, what the baseline capture
    counted apart dropped, a row's two captures' tallies replaced rather than added, its connections, accepted ports
    or a host's connections not added, only the baseline capture's own epochs judged, and the compared interval
    judged by the baseline's coverage; in the report, two captures counted as one, their alignment unsaid, and
    whether they name one host left out of CSV; and on the command line, --baseline-session ignored, two captures
    compared by process when asked or by default, one session as both captures not refused in words, a missing
    baseline folder not refused, the compared session unlabelled, the baseline's folder left out of JSON, one
    session's notes said of two captures, and two host identities said to be one.

- **Revision 755 — M12: a comparison by executable (`icat compare --group-by executable`, ADR-059):**
  - On a busy machine processes restart all the time, and a comparison by process states it honestly but unhelpfully:
    one row disappeared, another appeared, to be paired by eye. `icat compare … --group-by executable` compares
    executables instead - each one's processes' relationships and connections to other hosts together, by the path
    their image was witnessed at, two executables of one name at two paths two rows, named apart by their folders - so
    a program whose process restarted between the intervals keeps one row, and reads unchanged where its traffic was.
  - It is a grouping, never an identity (P6): no process is taken for another, and a process whose executable was not
    witnessed is in no row, since grouping it with the other unwitnessed processes would merge strangers; its
    relationships and connections with a record in either interval are counted apart, with their records in each
    interval, and said beneath the rows. Rates, coverage, descriptors, lower bounds and ranking are the comparison's,
    summed over the row's processes (`comparison-v1` §2a). `--json` names each end's group, name and path, and the
    redacted report names each executable by a token and no process.
  - Debug and Release each ran 3,094 tests with revision 473's 93 failures and 4 skips, and failed nothing else but
    the check that every decision record named exists, until ADR-059 was written; the architecture tests, run again
    after it, pass in both. The fixture index names the four new I22, R21 and R22 tests. All thirty-one mutations
    were caught: in the comparison, the grouping ignored, unwitnessed processes grouped, a quiet relationship
    counted apart, what was counted apart swapped between the intervals, a pair keyed in its drawing's order, its
    connections or accepted ports not summed, a host's ports or connections not summed, either interval's records,
    the bytes sent or the sends measured not summed, a pair keyed as one executable, the executables left unnamed,
    and what was counted apart left out; in the shared words, a pair's two ends named as one, its direction unseen,
    the grouping's note dropped or said of processes, and what was counted apart unsaid; in the report, two
    executables given one token, what was counted apart reported of processes, and the grouping or what was counted
    apart left out of CSV; and on the command line, --group-by ignored, what was counted apart stated of processes,
    an executable row called a process row, its path dropped, its rows said to be between processes, and the
    grouping unsaid.

- **Revision 754 — M12: a comparison shares as a redacted report (`icat compare --share-redacted`, Share redacted…):**
  - A comparison found on a busy machine is often for someone else - an administrator, a vendor - who should not learn
    its process names, paths or addresses. `icat compare <session> --baseline … --interval … --output <path>
    --share-redacted` writes it as a metadata-only, pseudonymized report of the sharing report's contract, kind
    `comparison`, as JSON or CSV; the Compare window's Share redacted… does the same for the comparison shown, every
    row of it, after saying what such a report keeps and leaves out, and says beside its buttons where it went.
  - Its processes, executables and other hosts are random tokens, consistent within the report only: an executable's
    is keyed by its path, so two of one executable share it and a namesake elsewhere does not, and a process of no
    witnessed executable has none. It records what M12 asks of a shared report - what was compared by, the evidence
    policy, byte domain and accounting, each interval's relative ticks, coverage and missing sources, and that no
    alignment applies, both intervals being of one clock - and each row's counts, rates, change and reason, the
    reasons being fixed sentences the comparison's tests pin. No name, PID, path, address, port, row key, session
    identity, generation or folder is written (`comparison-v1` §8b).
  - Without `--share-redacted`, `--output` writes the `--json` document, complete before it replaces anything; a file
    is refused where one is unless `--overwrite` is given, and holds every row unless `--limit` is given. The shared
    comparison words gained the arrow's contract word, the rounded rate and the rounded difference, which the command
    line and the report now share.
  - Debug and Release each ran 3,089 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the five new I22, R18 and R21 tests. All thirty-two mutations were caught: in the report,
    the limit ignored, a partial report said complete, a byte domain or accounting named under records, an alignment
    revision named, the missing sources misjudged, a process of no witnessed executable given an executable token,
    an executable keyed by its name, a host keyed by its row, ranks counted from zero, a reason dropped, coverage in
    words dropped, the CSV's missing sources unsaid, and an empty report's line said to hold a row; in the shared
    words, accepted connections said as made, and a difference stated where no change is; on the command line, rates
    unrounded, a file cut at fifty rows, the file-only flags taken without a file, --json taken with --output, an
    unredacted CSV taken, a file there not refused first, the redacted and the full document swapped, every row said
    written when not, a CSV name written as JSON, and the full document called pseudonymized; and in the window, the
    report saved unasked, a CSV name saved as JSON, what was said of a report kept past a new comparison, Share
    offered with nothing shown, a failure unsaid, and the comparison's own status overwritten.

- **Revision 753 — M12: the window compares its time scope with another interval (Compare with…, Ctrl+B):**
  - Beside the time scope, wherever it is an interval - a brush, or the visible range while the timeline is zoomed -
    Compare with… (Ctrl+B) opens a comparison of that interval with the same length just before it, by default, or
    just after it, where either lies within the session's time; the whole recording, or the span of its records; or an
    interval typed in seconds. It compares by records or by bytes sent across, under the window's evidence policy, and
    reads the session afresh at each choice.
  - Its rows, values, changes and reasons are `icat compare`'s, from words the command line and the window now share
    (`ComparisonText`): the most changed first, at most 200 listed and the rest counted, each interval's coverage
    above them and each reason once beneath them, numbered from the rows. The value columns are as wide as their
    widest cell, measured, so they line up with the heading. Enter, a double click or Show in the window opens what
    the chosen row compares: a relationship as a double click on its edge opens it, or the process whose connections
    to another host it compares; Esc closes it. F1 lists Ctrl+B.
  - Debug and Release each ran 3,082 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the three new R3, R18 and R21 tests. All forty-two mutations were caught, three of them once
    a test named the case they found untested - two ends that each connected to the other, a change down, and a
    lower bound said in words - and a fourth found choosing a baseline to type kept the last baseline's rows, which
    it now lets go: in the shared words, coverage said without its interval, ends reversed or joined one way, a
    host's accepted connections or their ports left out, a zero stated as a rate, nothing sent left unsaid, a bound
    unmarked, appeared said as up, at least left unsaid, down said as up, rates too coarse, a byte rate unrounded,
    and the note on other hosts said with none listed; in the rows, a reason numbered afresh, one not given left
    blank, unopened ends joined by and, a reason unspoken, and a bound's mark read out; in the window, a baseline
    before offered always or one after never, a recording unnamed, the typed one unoffered or its rows kept, the
    metric ignored, a typed end taken as its start, the list uncapped, the quiet or the more left unsaid, the first
    row not given the keyboard, Show never offered, Enter ignored, the columns unmeasured, and the heading unsized;
    in the workspace, the whole session offered for comparing, a recording assumed, a process's rung not opened, and
    the scope's change unannounced; and in the main window, Ctrl+B ignored, and a host's row or a relationship's not
    opened. A name the comparison first gave the way to a process's rung, OpenProcess, matched the call P20's
    fitness test refuses, and is OpenProcessRung.

- **Revision 752 — M12: two intervals of a session compared row by row (`icat compare`, `comparison-v1`, ADR-058):**
  - What changed between two moments of a capture was read by setting two tables side by side and dividing by hand,
    with nothing saying whether the capture measured both alike. `icat compare <session> --baseline <start:end>
    --interval <start:end>` compares each relationship between two processes, and each process's connections over one
    transport to one other host's address, that had a record in either interval: by records, or with `--rank-by
    bytes-sent` by what was sent across, as counts and as rates over each whole interval, compared exactly in
    integers, the most changed first.
  - A change - up, down, appeared, disappeared or unchanged - is stated only where both intervals covered the row's
    mechanism, and for RPC calls ALPC too, through the same descriptors. Elsewhere both values stand with the first
    reason that applies, said once beneath the table: a mechanism not collected, a partial gap, unknown coverage,
    sources that changed between the capture's epochs, or sends that recorded no size whose bound cannot decide. A
    lower bound states a change only where it alone decides it, as up or down at least. A partial gap is not bounded,
    since a lost record can move the attribution or pairing of others.
  - Every read is of one generation, and a live session that publishes between reads is read again. The demo's client,
    whose every connection runs to another host, shows how each host's traffic changed; its server, whose capture lost
    two events, shows every row side by side with the gap's reason. `--json` answers `comparison-v1`. The release
    review §15.2 asks for records what ships and what of M12 is not built.
  - Debug and Release each ran 3,075 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the eight new R3, R18, R21 and I16 tests. Of fifty-four mutations, fifty-three were caught:
    a session read once though it published between reads, or its generations left unmatched; an interval of no
    length, or a metric not defined, let through; a quiet relationship or host not counted; RPC calls judged by
    RPC's coverage alone, or by the first of RPC's and ALPC's; bytes asked of RPC calls; either interval's coverage
    left unread; the capture's sources, or the intervals they are read over, left out; a lower bound stating a
    change it does not exceed, or one against another bound; every change said to rest on a bound; a value with
    sends of no size taken as exact, or one with none measured taken as nothing; the difference reversed; rows
    ranked by the smaller rate, an unscaled one or a signed difference, or with those that cannot be compared first;
    a rate in the wrong unit; a host's records counted from one connection, its sends of no size counted as
    measured, or its bytes not read, nor a relationship's; one end's sends alone, or the last end's; every epoch, a
    count of descriptors or every mechanism's taken for the sources; an undefined mechanism let through; connections
    read under no generation; and on the command line, bytes read as records, the limit not applied to the table or
    the JSON, a change without its sign, a reason repeated, a host's arrow or its JSON ends reversed, an accepted
    connection's port dropped, a rate unrounded, a baseline's refusal naming --interval, a note said where no row
    needs it, coverage unsaid, and a limit of zero let through. The one missed lets an undefined evidence policy
    through, which the overview's own read refuses alike.

- **Revision 751 — S4, S2: what queries read of every chunk's tiles is kept in one budget, so a live focus on a busy machine's traffic reads each part once:**
  - Where each record is another pair's than the one before, as on a busy machine, no tile keeps narrow tallies, so a
    live focus reads every block of each chunk's tiles, its tallies and its records. Each chunk kept 64 blocks
    decoded, fewer than a chunk of 100,000 such records holds, and every query read the tallies and records again, a
    read a block, so a focus read them all from the files at every view and took up to twice what every segment's rows
    took.
  - What queries read of a tile index, its blocks decoded and their tallies, byte sums and records, is kept within 64
    MiB charged in bytes; the tiles of every chunk a session's live generations name share one budget, and a
    generation's own tile index has its own. Past it, what a query reads takes the place of a part chosen at random,
    so a query reading more than fits still finds much of it. A query opens a tile file only to read a part it does
    not find kept.
  - Measured at steady state over 4,000,000 such records in forty chunks, the tiles keeping 60 MiB: a channel's focus
    took 61 to 87 ms where every segment's rows took 122 to 214 and the tiles had taken 92 to 112; one process's 52 to
    73 and a group's lanes 52 to 90, where the rows took 30 to 77 and 48 to 84 and the tiles had taken 77 to 107 and
    64 to 82; a brush's bytes 25 to 53 where the rows took 104 to 333. At 1,000,000 records in ten chunks, whose ten
    segments the rows hold in memory, an owner focus still takes up to twice the rows, 17 to 21 ms where they take 10
    to 18: what remains is decoding each packed record.
  - Debug and Release each ran 3,060 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    fixture index names the five new S2 and S4 tests. All twenty-two mutations were caught: a part larger than the
    budget kept, or the budget passed; a part kept twice returning the second; a part charged without its entry, or
    never given back its charge; the last part kept not moved into the place given up; a block's tiles found as
    bytes; a block never found or never kept; tallies kept as records, or sought as another kind's; records sought
    as byte sums, read from the tile asked for rather than the block's first, or a block's taken for the next
    block's; the chunks' cache not handed to a later generation or not given to a chunk's file, or each file keeping
    its own; a budget of a kilobyte; a block's longs charged as ints; and a reading opening its file at every read
    or at its creation, or not counting what it opened.

- **Revision 750 — R11, §20.1: a compaction copies each segment's columns as they are, rather than reading its rows back whole:**
  - A live compaction step rewrites up to 250,000 rows. It read each row back whole, an object of some 380 bytes, and
    wrote it again: measured with sessions in memory, 1.4 to 1.7 s and 1.5 KB allocated per row. It now copies each
    source segment's columns into the new segment's as they are, a schema or a name taken once for each code that
    names it, and cuts its segments at the rows and bytes writing the rows one by one would: 0.7 to 1.0 s and 1.1 KB
    per row, its segments and dictionaries byte for byte the same.
  - Building a segment, which every publication does, sorts its rows' places with their keys through a comparer rather
    than a delegate, and writes each column with its encoding chosen once rather than row by row. Most of what a step
    still costs here is hashing each segment twice, for its own trailer and for the manifest, and making it durable,
    at this machine's processor and disk.
  - Debug and Release each ran 3,055 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    twenty mutations were caught: a segment cut a row past its bound, or by bytes without its rows' names or their
    widening to observation-v2; a copy not widening the segment, or widening it only from the second IPv6 column; a
    field's discriminator dropped; a schema interned as another; a copied name's bytes left unstaged, an absent name
    staged as present, a chunked name's length read from its offset or a name's counted in characters; copied rows
    or field rows left uncounted, or copied without flushing a full segment first; and, in building any segment, two
    rows of one place let through, a plain column gathered past what was staged, a time block's last reading taken
    from its first row, a column's known values left uncounted, every schema code written as the first, or the
    earliest reading taken from the last row.

- **Revision 749 — coverage pass over revisions 741–748 (R10):**
  - Of 1,430 reachable lines those revisions added, 49 ran in no test; of 1,422 now, 14. They are a ticket held by a
    running follow, which only Windows detects, and four guards in the RPC extension against what its construction
    never makes: a standing call's instance a later derivation leaves unmapped, two groups coming to one, a withdrawn
    server call paired again unlike itself, and server starts trading places. Each would answer rightly if reached, by
    binding again, by linking again from the call's start, or by sorting again.
  - Now run: a chunk's tiles whose section cannot be read, which a live brush and its bytes, an owner's and a
    channel's focus, a group's byte lanes and every mechanism's bytes read around, from the chunk's rows; a derivation
    refusing to gather the end spans of rows it never read; each comparison of a record's place in the canonical
    order; a segment's owners asked into buffers a row short; and the window letting go of a look at a recording
    elsewhere still under way when another session is opened, whether it found nothing new, a newer generation or the
    recording ended. Had it been kept, a look finding the recording ended would have said so on the other session's
    card.
  - Removed: a count of end spans nothing read, and a search's handling of equal places among a generation's withdrawn
    calls, whose places are distinct.
  - Debug and Release each ran 3,049 tests with revision 473's 93 failures and 4 skips, and failed nothing else once
    the fixture index named the three new S4 tests, as the architecture tests then confirmed in both. All fourteen
    mutations were caught: a damaged section's refusal thrown on, rather than read around, by a live brush, its
    bytes, a focus, a group's byte lanes or every mechanism's bytes; either buffer of a segment's owners left
    unchecked; a row naming an end the gathering derivation does not hold passed over; each comparison of a record's
    place made inclusive or exclusive; the first withdrawn place taken one past the one found; and a look under way
    kept after another session was opened.

- **Revision 748 — S4: what the plan and the status said a live generation still reads, restated:**
  - The plan said the minimap level was recomputed rather than persisted, which a finished generation's overview index
    has persisted since revision 163, and that a live generation still built its levels from rows, which it counts
    from its chunks' tiles since revisions 739 to 746. IC-017's open gap and the metric selector's paragraph said the
    same. What S4 still owes is its qualification at T3.
  - Documentation only: the architecture tests pass and the fixture index is unchanged.

- **Revision 747 — S4, P25: a live session's chunk-tile queries measured at steady state:**
  - The warm figures revisions 744 to 746 gave were a query's second and third run, before the runtime had compiled
    it. Measured as the sixth to tenth of ten runs over 4,000,000 records in forty chunks, a live generation's channel
    focus took 30 to 43 ms on traffic in bursts where every segment's rows took 95 to 105, a group's lanes 23 to 30
    where they took 39 to 65, and a brush's bytes 38 to 58 where they took 285 to 319.
  - Where each record is another pair's than the one before, a live focus took up to twice what the rows did while all
    forty segments stayed held, and a brush and its bytes less. A level spanning a chunk's one section was tried and
    set aside: its tiles keep tallies within the same third of their records' bytes, so it reads as many records.
  - Documentation only: the architecture tests pass and the fixture index is unchanged. The figures come from a
    probe run in Release over live sessions of 1,000,000 and 4,000,000 records, removed once it ran; the spanning
    level tried for a chunk's one section was reverted.

- **Revision 746 — S4, P25: a live session's channel focus counts its chunks from their tiles; incremental updates at commit are done:**
  - A channel's focus - at both ends or at one, of one mechanism or source direction - counts each chunk from its
    tiles where the counting derivation's relations hold every span of the chunk's ends within one incarnation,
    whatever its owners bind to, and reads the chunk's rows where a lifecycle record read later divides one. A cold
    open's channel focus opens only the newest chunk's segments.
  - Every query a finished generation counts from its tile index a live one now counts from its chunks' tiles, reading
    the rows only of its newest chunk, of a chunk whose tiles are gone, and of one a record read later binds
    otherwise.
  - On traffic in bursts a channel's focus over 1,000,000 records took 27 to 30 ms warm and 97 cold, where the rows
    took 49 to 58 and 208. Where each record is another pair's than the one before, the tiles' records are read: 135
    to 146 ms warm where the rows took 74 to 85, since a chunk of one segment keeps no level spanning it.
  - Debug and Release each ran 3,043 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    twelve mutations of the code as committed were caught: a channel's focus read from its rows, or a chunk's tiles
    counted for it only where their owners bind alike, or wherever its tiles are, whatever their spans or owners
    bind to; an owner's focus counted only where a chunk's spans hold; channels or owners not rebound; a channel's
    ends dropped from the total or from each worker's tally; its end, source direction or mechanism not narrowed;
    and another channel counted.

- **Revision 745 — S4, P25: a live session's brush counts its chunks from their tiles (tile-index-v1 minor 9):**
  - A chunk's tiles key each record's channel by the end it was made at: by the span of that end's records within one
    incarnation under the derivation that tiles them, which their directory lists - the end, and the place of its
    first and last record. A later derivation finds each span's channel among its own wherever one of its incarnations
    holds the span's first and last record, and reads the chunk's rows where a connect, an accept or a disconnect read
    later falls among them.
  - A live generation's brush, its counts and its bytes by process and channel end, so counts every chunk but the
    newest from its tiles where its instances also bind the chunk's owners alike; a cold open's brush opens only the
    newest chunk's segments. A live channel focus still reads rows.
  - At 1,000,000 records in ten chunks a brush over the whole extent took 6 to 9 ms warm and 27 to 33 cold, where
    every segment's rows took 8 to 10 and 43 to 52, and its bytes 17 to 18 ms warm where the rows took 37 to 56.
    Staging a chunk's tiles takes 200 to 248 ms where each record is another process's than the one before, from 133
    to 154, and the tiles 9.0% of the segments' bytes, from 8.0%.
  - Debug and Release each ran 3,040 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    fifty-eight mutations of the code as committed were caught, three of them once tests were added for them, one by
    the tile index's own tests and two once they were made to compile: a span held across a divide of its end, one
    of an end the derivation does not hold, or one given no channel; records the tiles do not hold, or untimed ones,
    spanned, a span's first or last record not the least or greatest, an end's incarnations merged, or spans
    numbered from 0; no span listed, none stated as the derivation's channels, its fingerprint blind to the spans or
    to their order, or a chunk's channels keyed as a generation's; spans rebound under any minor or relation rule, a
    divided span counted, or channels not shifted by one; spans unread, a count below none or beyond what the
    directory holds accepted, a span no derivation gathers accepted - of any protocol or family, without a port or
    an address at either end, of an IPv4 address beyond 32 bits at either end, an IPv6 one refused as beyond them,
    or with its first record after its last - another number of spans than the derivation's channels accepted, or a
    generation's own index, which lists none, held to its channels; a channel key of none mapped, a generation's own
    channels mapped, a tally's, a byte tally's or a record's channel unmapped, a span bound to none counted, or its
    tiling number kept; the live brush or its bytes not counted from tiles, or only where every chunk keeps them, a
    chunk counted whose owners or spans are refused, owners or channels not rebound, a segment whose tiles cannot
    count it left uncounted, or one holding an untimed record measured from tiles; and tiles staged without spans.

- **Revision 744 — S4, P25: a live session's owner focus counts its chunks from their tiles (tile-index-v1 minor 8):**
  - A chunk's tiles bind its records' owners under the derivation of the generation that tiles them, and certify each:
    for each owner key, PID and kind, the instance's identity and the first and last native reading of its records. A
    later derivation counts from them where it binds every owner's records alike, decided from its instances at each
    lifetime's start and end among the readings, and reads the chunk's rows where a record read later binds them
    otherwise.
  - A live generation's owner focus, a group's byte lanes, a process's byte direction rows and a zoom's byte lanes so
    count every chunk but the newest from its tiles; a cold open's focus opens only the newest chunk's segments. A
    brush still reads rows, since the tiles bind no channel.
  - On traffic in bursts a group's lanes at 1,000,000 records took 28 to 33 ms warm and 88 cold, where the rows took
    38 to 49 and 195; where each record is another process's than the one before, the tiles' records are read and took
    88 to 100 ms warm where the rows took 51 to 59. Tiling a chunk of 100,000 records takes 133 to 154 ms, from 34 to
    94.
  - Debug and Release each ran 3,021 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    fifty-seven mutations of the code as committed were caught, six of them once tests were added for them and one
    once it was made to compile: a chunk's tiles counted under a derivation of another relation rule, of channels or
    of another binding rule; records bound to none counted where they now bind, or checked at their first reading
    alone; an owner's records checked at their first reading alone, or not for their instance or strength, a
    lifecycle record's not for binding directly; one instance standing for two, or none mapped; a binding not
    checked where a lifetime starts or ends, nor where one ends at an owner's last reading, over readings in
    reverse, or only for being bound; untimed records or ones naming no process certified, a first or last reading
    not the least or greatest, lifecycle records not told apart, an instance named by another's place, no owner
    certified, or the tiles stating a relation rule; certificates unread, unchecked, out of order, twice, more than
    held or fewer than none, of a PID below 0, of a first reading after the last, of records bound to none with an
    instance or as lifecycle records, of a key beyond the instances or of no strength, or of lifecycle records not
    bound directly; an owner read as the tiling derivation's, without its strength or uncertified, or a tally's, a
    byte tally's or a record's owner unmapped; the live focus, a group's byte columns or a zoom's byte lanes not
    counted from tiles, or only where every chunk keeps them, a refused chunk counted, owners unmapped, or a segment
    whose tiles cannot count it left uncounted; and tiles staged unbound.

- **Revision 743 — §6.4, §19.3: a session opened while another capture records it follows that capture's publications:**
  - A window that opens a session whose ticket a running follow holds, the follow of another window's capture or of
    `icat capture`, follows it as it follows its own capture. The card and the health strip say it is still recorded
    elsewhere, Pause view and the held banner act as for its own, the loss is stated once recording stops, and a
    package waits for the recording to end. Once the follow lets its ticket go, the newest generation, published or
    held while paused, is shown as the session saved, or said to have stopped early.
  - The plan had no rule for a session that another process records; §6.4 now has one, and `live-follow-v1` says a
    window reads the ticket's hold.
  - Debug and Release each ran 3,001 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    twenty-six mutations of the code as committed were caught, two of them again once a redundant call was removed:
    a held ticket read as none, or a missing one as left early; a session recorded elsewhere opened unfollowed, said
    to be saved, or its following not started; Pause view not offered, or the health strip not told, as the
    following starts; a follow that ended early, or one that ended, still followed; the generation held while paused
    forgotten, the shown one named as saved, an early end said as an end, a failed look ending the following, or a
    package withheld once records being read outlive the recording; a session opened afterwards still followed; a
    paused view resumed by a newer generation, Pause view hidden by one, a publication left unstamped, F ignored,
    the banner saying the session is saved, a pause unsaid, the strip saying a saved session, the freshness hidden,
    the loss said unknown, a package offered, or why not unsaid. A twenty-seventh, leaving packaging unrefreshed as
    the following starts, survived because the open refreshes it as it ends, so the call was removed.

- **Revision 742 — I14, P25: extending RPC calls and their other ends between live generations binds, groups and links again only what a generation's records can change:**
  - A call index ranks its calls by their first records' canonical order and keeps the ranks of the calls a later
    generation withdraws and each group's sorted durations, so an extension compares first records only where a call
    paired again falls, carries each group's standing calls in order where every binding holds, renumbering their
    instance, and binds and groups every call again only where a late lifecycle record changes a binding. The other
    ends carry server starts and links to where the calls went, and link again only the client calls whose evidence
    could have changed.
  - At 800,000 calls in Release an extension by 60,000 records took 0.45 s for the calls and 0.38 s for their other
    ends; it now takes 0.16–0.32 s and 0.07 s. Copying 144 bytes a call forward is what still grows with the session;
    this container charges up to 28 µs for each fresh page it touches.
  - Debug and Release each ran 2,996 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    thirty mutations were caught: a call paired again ranked without the calls withdrawn before it, or a standing
    call without those paired again; a withdrawn call kept; bindings left unchecked, checked at a group's first call
    only, or unbound ones checked without their reason; a group's instance not renumbered, the calls paired again
    dealt first, the canonical order, a group's durations or its withdrawn calls' durations not carried, or an
    emptied group kept; a regrouped call's place lost; every call paired again kept, a derivation's kept calls taken
    by place, its calls ranked by record, an operation index written in the index's order, or durations left
    unsorted; a server call paired again left unmoved, or a new one's start ignored; added sends or receives
    ignored, every standing link kept, a shared call carried as shared, calls paired again left unlinked, a server
    call shared only by three, a server call not linked back, new starts lost, or the messages after the last added
    one dropped.

- **Revision 741 — S3, P25: a session's first view in the window does not wait for its RPC calls to be followed; a staged live operation index measured and set aside:**
  - A session that collected ALPC and keeps no links, as one still being recorded and opened cold keeps none, is drawn
    at once: its graph says which process called which over RPC is still being followed, and draws those edges when
    the window's following, off its thread, ends, keeping the rung, selection, pins and interval. A policy chosen
    meanwhile waits for the same following, a following that fails says why, and opening another session or starting a
    capture stops it. `icat` still follows before it answers.
  - Staging each live generation's operations instead was built and measured: at 25,000 calls the index took 9.4 MiB
    and cost each two-second publication 400–500 ms in Debug (155 ms extending, 260 ms writing and hashing), growing
    with the session, for a cold open of 548 ms against 917, so it was not kept.
  - Debug and Release each ran 2,988 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    twenty mutations were caught: a first view that waits anyway; one said pending where the calls were followed,
    where links are kept or where no ALPC was collected; a pending view following anyway or saying nothing of it;
    its graph's identity, the bundle or the window's snapshot left unmarked; the words staying once followed, "only"
    said while following, or the empty graph's or the scope's sentence dropped; the window's first view or a
    policy's waiting, or either never followed; a following under another policy applied; opening another session
    leaving it running; and a failed following left unsaid.

- **Revision 740 — coverage pass over revisions 718–739; a checkpoint's records by owner checked against its counts (R10, derivation-checkpoint-v1 §3); a failed byte read shared until noted (§6.4):**
  - Of 1,762 reachable lines those revisions added, 61 ran in no test; of 1,774 now, 30, all guards no derivation
    reaches or the ETL import's Windows path. Now run: a live follow stopped while it stages a publication's
    checkpoint and chunks' tiles, which gives them up; a zoom over a live chunk whose readings go backwards; a metric
    of a folder with no generation; an undefined policy asked of one mechanism's records; and a checkpoint giving
    records by owner to an instance it counts none of. That one was read: a checkpoint whose records by owner are not
    the records it counts, at the strengths each instance binds at, or that counts no records for an instance, is now
    refused and the generation derived from its rows.
  - The suites caught a race in the window: a byte read that failed before the ranking noted why was read again for
    the inspector's description, leaving the ranking's note saying it was reading; a failed read is now shared until
    its failure is noted, as a running or answered one is.
  - Debug and Release each ran 2,983 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    seventeen mutations were caught: a checkpoint's records by owner taken unchecked, an instance with no records
    counted or none by owner taken, an instance's direct or bound records or the strengths it binds them at
    unchecked, any strength from direct up taken, records binding to none taken in an instance's, lifecycle records
    counted among them or records summed across strengths, an undefined policy answered, a stopped follow keeping
    the tiles it was writing, those it had staged or the checkpoint, a zoom passing over the rows of a chunk whose
    readings go backwards, and a failed byte read read again before its failure was noted, or shared once it was.

- **Revision 739 — S1, S4, P25: a live follow keeps each chunk's tiles (tile-index-v1 §6, store-v1 §5):**
  - Each live publication stages, beside its checkpoint, the tiles of the chunk the generation it extends added,
    binding no record; every later generation carries them while it names the chunk's segments, a release gives them
    up only with those, and a damaged one decides no generation and is dropped by the next commit. A live generation's
    overview and zooms' whole timeline count every chunk but the newest from them: at a million rows in ten chunks a
    cold open's overview counted in 7 ms opening one segment, where the rows took 42 ms and ten, for tiles a
    fourteenth of the segments' bytes and 34–94 ms of a publication.
  - Debug and Release each ran 2,979 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    thirty mutations were caught: a chunk's tiles not carried, every index carried, tiles carried without their
    chunk's segments or once they no longer verify, acquisition or a viewer's later hashing decided by them, a
    release giving up every chunk's tiles or none, a retention publication keeping a damaged one, a name of
    generation 0 or of no separator taken, a field segment or a non-segment taken as described, a generation out of
    range named, no tiles staged, the oldest chunk tiled first, a tiled chunk tiled again, a fifth chunk tiled or
    the row bound ignored, counted from the first or left uncounted, the overview or the zoom passing chunk tiles
    over, the overview counting a segment whose readings go backwards from its section, the zoom opening a segment
    its interval does not meet, the overview opening every segment at once, an earlier generation's tiles not taken,
    and a chunk's tiles stating a derivation other than the empty one.

- **Revision 738 — S3, P25: the derivation checkpoint keeps records by owner (derivation-checkpoint-v1 format 1.5):**
  - Each incarnation's and each instance's records by owner, with the transport bytes they measured, are kept after
    the transfers, so a viewer opening a live generation from the checkpoint it names ranks by bytes and peers opening
    only the newest chunk's segments. A 1.4 checkpoint reads the rows and is replaced when published again; staging a
    live checkpoint opens a segment only where the derivation still reads it. At a million rows in ten chunks a viewer
    ranked by bytes in 90–133 ms opening one segment (rows: 176–386 ms); the counts make the checkpoint a third to two
    fifths larger.
  - Debug and Release each ran 2,967 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    twenty-two mutations were caught: a checkpoint claiming format 1.5 without the counts, the counts never written
    or never read, their section's length unchecked, relations or counts read back as keeping none, an incarnation's
    records by owner not checked against its records, its PIDs unchecked, dropped where it names two or written
    unsorted, owners written as counted rather than in order, owners out of order, held twice or reversed taken (the
    first form of that mutation did not compile, and three replaced it), sums below none or bytes beside a refusal
    taken, a refused sum's bytes written, an instance's records naming another instance or those binding to none
    naming one taken, and a checkpoint without the counts kept at publication, for the relations or the counts, or
    taken as current.

- **Revision 737 — S3, P25: a live generation's whole-session byte and peer rankings come from its derivation:**
  - The activity and each incarnation count every record by the owner it binds to, with the transport bytes it
    measured, extended between live generations; the bytes and peer bindings a persisted overview keeps are sums of
    them, so a live generation's whole-session byte and peer rankings read no row. An extension now opens only the
    segments it adds. At 1M rows in ten chunks a new generation's byte ranking took 104 ms with the extension over its
    chunk, then 2.1 ms for bytes and for peers, where the rows took 218 and 332 ms. A derivation read back from a
    checkpoint keeps no such counts, and reads the rows.
  - Debug and Release each ran 2,963 tests with revision 473's 93 failures and 4 skips, and failed nothing else.
    Twenty-nine of thirty-two mutations were caught: another domain's records measured, a unit other than bytes
    taken, sizes dropped, lifecycle records counted among a peer count's records, received bytes taken as sent, an
    unmeasured send as measured, the other side's bytes dropped (caught once the property test gave endpoint records
    sizes of their own), a sum past what one holds not refused, a copy dropping the records binding to none or
    keeping old positions, unbound records left uncounted, bound lifecycle records counted, an incarnation counting
    no owners or its extension dropping them, an extension or a checkpoint read claiming owners, a relation's owners
    given for another's records or past a refused record, the activity counting no owned or unowned records, its
    copy left unmapped, its checkpoint read claiming owners, its refusal ignored, the bytes never or always taken
    from the derivation, records moved to their other end left counted at none, the peers never taken from it, a
    generation's instances made from every segment opened, and an extension reading every segment again. The other
    three change nothing a session can show: a process connected to itself counted at its second end, which is its
    first, and an owner or other end bound at a strength no rule binds at.

- **Revision 736 — S3, P25: a whole session's connections to other hosts open no segment (derivation-checkpoint-v1 format 1.4):**
  - Each connection's sends, receives and bytes are counted as its relations are derived, extended between live
    generations and kept with the checkpoint. A process's connections, Other hosts ranked by bytes and an
    investigation's connections over the whole session read no row: at 1M rows a reopened session ranks Other hosts by
    bytes in 36 ms first and 9 ms after, opening no segment, where its rows took 332 and 38 ms. A checkpoint of format
    1.3 reads the rows and is replaced when published again. A live generation's whole-session byte and peer rankings
    still read every segment.
  - Debug and Release each ran 2,957 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    thirty mutations were caught: a send left uncounted, a receive's bytes or an unsized receive counted as a
    send's, a sum past what one holds not refused, an extension's copy dropping the sends, the received bytes or the
    refusal, an extension or a format-1.3 read claiming counts, counts given unkept, for another connection or past
    a refusal, sizes not passed, a checkpoint's entry count, sends past the records, unsized sends or bytes beside a
    refusal left unchecked, a refused sum's bytes written, a writer claiming 1.4 without counts or always writing
    them, a reader skipping them or not keeping them, the whole session never counted, counted within a scope, taken
    with counts missing or its records from the transfers, the total left unchecked, a scope listing connections it
    holds none of, a publication keeping uncounted relations, and an uncounted checkpoint taken as current. That
    last survived until the test republished every index beside the older checkpoint; dropping the refusal outright
    fails the analyzers, so a refusal of negative totals alone stood in for it.

- **Revision 735 — S3, P25: a finished session's ranking by peers opens no segment (overview-index-v1 minor 7):**
  - A finished session's persisted overview keeps how its records bind at their two ends: each pair of an owner's
    binding and an other end's, with the records that bind so. Applying a policy, a grouping and the processes set
    aside to them counts each process's and group's peers exactly as reading every segment does. At 1M rows a reopened
    session ranks by peers in 44 ms first and 1.4 ms after, opening no segment, where its rows took 391 and 99 ms. A
    live generation's whole-session byte and peer rankings still read every segment.
  - Debug and Release each ran 2,950 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    fifteen mutations were caught: a pair's records counted once for its known peers, for its unknown ends or for
    those with neither end known, a lifecycle record kept, a segment's last run dropped, a run ended on a like pair,
    the kept pairs never used, a candidate kept as correlated, the writer's bound ignored, a publication keeping
    none, a reader taking pairs out of order, holding no record, binding none at a strength or past its bound, and
    none read as the first instance bound directly. Read as that instance at no strength, which no policy admits, it
    changed nothing.

- **Revision 734 — S3, P25: the whole session's ranking by connections opens no segment:**
  - The TCP records behind each process's connections made and accepted are counted from the derivation's activity,
    which binds every record by its owner as a pass over the rows does: kept with a finished session's checkpoint and
    extended between live generations. At 1M rows in ten segments the ranking takes 0.6 to 3.3 ms, where its rows took
    56 ms at each new generation; a reopened session answers in 16 ms opening no segment. A time scope still reads the
    segments it meets. The whole session's ranking by peers still reads every segment.
  - Debug and Release each ran 2,945 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    seven mutations were caught: every mechanism's records counted for TCP's, the policy ignored, records bound by
    reading never counted, lifecycle records dropped from one mechanism's count, the rows always read, counts taken
    for no index, and a brush answered from the whole session's counts.

- **Revision 733 — P25, I14: a live recording's HTTP exchanges extend from the generation before (§12.1 S3):**
  - A live generation's HTTP exchanges are extended from the earlier generation's over the segments it adds, as its
    RPC calls have been since revision 544, rather than grouped from every record at each publication. A grouping
    keeps each number's open or recent last use and every number it has read, and declines to a full grouping wherever
    an added buffer could change an exchange it cannot group again. A property test holds random chunked WinINet
    captures, with late, lost and reused buffers, to a grouping of every chunk at once. A process that opens a live
    session with nothing grouped before it still groups from every record.
  - Debug and Release each ran 2,941 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    nine mutations were caught: a use holding every buffer of its number checked as if split, a buffer read before a
    split use began taken in, a number no longer kept begun again, a field naming an earlier record taken in, every
    ended number kept past the late window, an exchange whose response had not ended dropped, standing exchanges
    left bound to the earlier generation's instances, a cache that never extends, and a full grouping that keeps
    nothing to extend.

- **Revision 732 — P13: a capture's configuration is fixed as it starts, and a refused provider is said (§9.3):**
  - A test finds in the IL of the broker's and recorders' assemblies that providers are enabled and their state asked
    for only while a capture starts, that a session's settings are set only as it is created, and that nothing
    disables a provider; another holds that only the capture adapter names an ETW session's controls. A recording
    whose second provider is refused starts, says so, and publishes one epoch in which that provider's mechanism reads
    as not collected. The contract ledger no longer lists P13 as uncovered; P25 remains.
  - Debug and Release each ran 2,933 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    seven mutations were caught: a provider enabled, a provider disabled and a buffer resized as delivery is
    flushed, a state request as health is read, a refusal left unsaid, a refused provider counted collected, and a
    command that names a session of its own.

- **Revision 731 — P12: the capture callback reaches nothing that blocks and allocates nothing per record (R9, §9.3):**
  - A test walks the IL of everything the ETW callback can run - the admitter, the capture's queue and the coverage
    tally the broker gives it - and finds no store, journal, query, file, network, name or symbol lookup, decoded
    payload or wait; its allocations and locks are each a reviewed site, once per descriptor or per capture. A second
    test measures the sink: once its descriptors have been seen, a callback's queue, counters and tally allocate
    nothing. The admitter's own event reads run only on Windows, so the walk alone holds them. The contract ledger no
    longer lists P12 as uncovered; P13 and P25 remain.
  - Debug and Release each ran 2,930 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    seven mutations were caught: a sleep, a lock, a file call and a decoded provider name put on the callback, an
    identifier read through a string, the queue depth passed through an array, and a stack trace read per record,
    which only the measurement sees. The measurement first filled its queue while something drained it, so one run
    saw the queue's storage grow on the callback's thread; it now fills the queue before measuring, and passed ten
    times beside its assembly's other tests and ten times with a sleep on the callback.

- **Revision 730 — I17: late evidence leaves an earlier generation's answer as it was (§7.4, §13.2, §24):**
  - A metric can be answered over the generation a caller's lease holds. A test publishes a connection's client side,
    then its server side a generation later: the newer generation answers with the joined records under a new
    identity, while the held one answers exactly as before, its records keeping their identities and rows and its
    files their bytes. A released, expired or foreign lease is refused. The contract ledger no longer lists I17 as
    uncovered.
  - Release ran 2,927 tests with revision 473's 93 failures and 4 skips, and failed nothing else. Debug failed one
    more: "R11: writing a record's envelope allocates only the envelope" measured 382.75 bytes a call for an RPC
    call's 264. It passed alone five times, in its assembly six times and in three concurrent runs of it six more;
    it measures one thread's rents from the process-wide envelope pool, which other tests in its assembly hold
    thousands of buffers from at once, so it now runs with the pool to itself, and the journal tests then ran in
    Debug and Release with only their 3 known failures. All four mutations were caught: a released, an expired and
    another session's lease answered over, and a held generation answered from the current one.

- **Revision 729 — ADR-057: a view reads one generation and asks only for its latest request (§19.3, §6.2):**
  - The record states how the window schedules its questions - a latest-request slot per kind, late answers dropped, a
    150 ms rest before a viewport's count, panes kept across a publication until their own answer is read - and
    §19.3's suggested cadences now say what the window does instead. With it, every owed decision record that can be
    written now is written; the byte accounting's canonical owner waits on per-transfer associations.
  - Documentation only: the architecture tests, which hold the decision table to its records, ran 31 and passed.

- **Revision 728 — ADR-056: content kept only when asked, leaving only in a file a person saves (§11, P16):**
  - The record states the policy §11's decisions make together: metadata by default, content only under a reviewed
    request from measured sources, kept as restricted evidence and read only on request, and four files a person can
    save, each saying what it holds. A new test holds that no product code reaches the network and that product code
    is built only on reviewed packages. Encryption at rest, a secure erase and tightened session access are listed as
    not built.
  - No product code changed: the architecture tests, where the new test lives, ran 31 and passed. Both mutations
    were caught: a product file that names a socket client, and a product project that references an unreviewed
    package.

- **Revision 727 — ADR-055: every correlation states one of five strengths (§7.4, §6.6, R14, P8):**
  - The record states the model the correlators share: five strengths that are claims about the evidence, each fixed
    by a named rule for every case it meets, nothing joined by a reusable value or by time alone, strengths combining
    downward. A conflicting relation, drawn only when a policy admits every strength, was dotted without the ring that
    marks a candidate, so it read as more asserted; it is ringed now. Pending joins are bounded by the generation a
    derivation reads; count and age bounds wait for a streaming correlator.
  - Debug and Release each ran 2,924 tests with revision 473's 93 failures and 4 skips, and failed nothing else.
    Both mutations were caught: a conflicting relation left without the ring, and a correlated one given it.

- **Revision 726 — ADR-054: exact indexes and budgeted caches (§10.2, §12.1 S1-S4, §19.3):**
  - The record says how the checkpoint, overview, tile index and operation index fit - published together, each exact
    or unused, released with their segments, bounded - and why. §19.3's rule that every cache is budgeted by bytes did
    not fit the derivation cache, which keeps four generations by count; the plan now names that exception and its
    measured basis, and what remains unmeasured.
  - Documentation only: the architecture tests, which hold the decision table to its records and the fixture index
    to the repository, ran 30 and passed.

- **Revision 725 — ADR-053: the graph's bounded projection and deterministic layout (§6.3, §19.4, R7, R12):**
  - The record says why the graph is a folded process graph of at most 100 nodes with every process drawn or counted,
    laid out by hashed seeding, bounded Fruchterman-Reingold relaxation and separation, with pins fixed and nodes
    drawn before anchored. A new test lays out the same graph on one, two and eight threads at once, repeatedly, and
    requires identical positions, as §13.6 asks. The determinism holds within one runtime on one processor
    architecture, since seeding reads the runtime's sine and cosine.
  - Debug and Release each ran 2,923 tests with revision 473's 93 failures and 4 skips, and failed nothing else. The
    one mutation was caught: a seed that read the thread it ran on, which only the new test notices.

- **Revision 724 — ADR-052: the broker believes the operating system about its client (§20.3, R19, R22):**
  - The record says why each check of the broker's boundary is the one it is, and what it lets through: a process of
    the capturing user in the same logon session passes as the window does while a broker serves. Writing it found the
    plan claiming integrity and elevation checks the broker does not need or make, and a revert failure after a
    successful read that threw rather than ending the broker; it now ends the broker on every path.
  - Two rows of records owed were already decided (ADR-015; ADR-010, ADR-024, ADR-043 to ADR-046, ADR-048) and are
    gone, and a test holds the decision table and `docs/adr` to each other.
  - Debug and Release each ran 2,922 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    six mutations were caught: the new record's row taken out of the table or given twice, a record named that does
    not exist, and the table's rows, the names or the files left unread. The revert change runs only on Windows,
    where no test can make a revert fail; it was built, not run.

- **Revision 723 — ADR-051: fixtures named for their area, each covering exactly what its tests assert (§13.5, P16, P27):**
  - Writing the owed ADR found 18 claims across ten fixtures that none of their listed tests asserts, a gate check
    with nothing to read, and bench results naming a fixture the index never declared. A fixture's covers now equal
    its tests' contracts: the tests that read the transport fixtures' evidence are listed, and a new test holds that
    evidence to its own loopback flows, backing 10 claims; the other 8 are removed. The checks also hold IDs,
    directories, milestones, backlog items and every fixture ID named elsewhere to the index, and the undeclared
    import probe is retired with its reason.
  - Debug and Release each ran 2,920 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    20 mutations were caught: each check of an entry, a retired number, a directory and a named ID left out in turn;
    IDs of any form accepted; names, or the plan's milestones, left unread; the retired number changed in the index;
    and a foreign endpoint in a fixture's committed evidence.

- **Revision 722 — ADR-050: the palette's targets and why each is the number it is, held by tests (§6.6, R14, R20):**
  - Writing the owed ADR found that a lowered target passed every test once the report was regenerated, that the
    committed theme files were held to the code only through their ratios, and that the summary could show a passing
    divider over a failing fill. The three files are now compared whole with what the code writes, each target is held
    to the ADR's value, and the summary shows each check's measurement nearest its own target.
  - Debug and Release each ran 2,896 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    eight mutations were caught: the summary's worst row chosen by value alone; the ink, greyscale, divider and
    any-pair targets lowered; TCP's dark ink one step off; and the committed summary's date and a committed token
    changed.

- **Revision 721 — a record no admission could have written is refused as damaged evidence (`normalizer-plan-v1`):**
  - A sealed record naming a stream past 2^31 or an ordinal past 2^63, or keeping more of an extended item than the
    item held, failed with an overflow or an argument error that no follow, rebuild or import handles, stopping it.
    Each is now refused as data, naming the record.
  - Debug and Release each ran 2,893 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    four mutations were caught: a stream, or an ordinal, past an admitted record's left unchecked; an item keeping
    more than it held handed on; and a stream past every source's overflowing when resolved.

- **Revision 720 — an envelope's bytes from a pool that keeps a batch's worth (R9, R11, §18.1):**
  - A batch holds 4,096 envelopes at once, and the shared pool keeps a few dozen arrays of a length per core, so most
    of a batch's bodies were allocated anew and dropped on return. Envelope bytes now come from a pool of their own
    that keeps two batches' worth of each length up to 4 KiB: a batch's round trip allocates its leases alone, and a
    replayed record 306 bytes where it allocated about 450.
  - Debug and Release each ran 2,891 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    four mutations were caught: buffers rented from the shared pool, or returned to it; a pool keeping 32 arrays of
    a length; and a pool keeping nothing longer than 64 bytes.

- **Revision 719 — every record a journal holds is derived, or its plan refused as data (R3, `normalizer-plan-v1`, `segment-v1`):**
  - A record without its declared status, an eight-byte count of 2^63 or more, a status the capability report calls
    present that the descriptor did not admit, and a plan whose codes or labels a row cannot carry each made a row the
    segment refused, which stopped a follow, a rebuild or an import at that record. Now the status is lost, the count
    not decoded, the unadmitted status not exposed, and such a plan is refused when it is read, naming the descriptor.
  - Debug and Release each ran 2,890 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    13 mutations were caught: a status slot forgotten, so a lost status read as the report's; a status reported
    present but not admitted kept present; an impossible count kept, or read as lost; and a plan's mechanism,
    direction, byte domain, byte unit, source field, reported field said not to apply, reported unit, mixed address
    families or whole refusal left unchecked.

- **Revision 718 — a journal's write and decode loops held to R11 (§19.4, `normalizer-plan-v1`):**
  - Resolving a record searched its plan with LINQ, reading its projection copied it into a stream, deriving its row
    hashed its fact key afresh and read the projection again for its field rows, a replay read each frame into an
    array of its own, and reading rows back boxed their codes: following a session allocated 8.4 KB a record, now 4.4
    KB. Resolving and reading a record allocate nothing, deriving it only its rows, writing it only its envelope,
    reading a row back only the row; tests hold each to the byte, and 3,000 varied records gave the same output, byte
    for byte, on the code before and after. A record two descriptors both select is refused as data, naming the
    record.
  - Debug and Release each ran 2,888 tests with revision 473's 93 failures and 4 skips, and failed nothing else; the
    allocation tests pass in both. All 28 mutations were caught: a table that forgets no reference index as it
    grows, or keeps a reference's last entry; a replay buffer that never grows, or an array per frame; row codes
    boxed, or a descriptor parsed per row; a changed fingerprint accepted; a plan index dropping a descriptor,
    ignoring opcodes, accepting two descriptors, reading an empty place, equal by contract alone, or kept stale by a
    copy; an allowlist taking a denied or repeated type; a projection read past its end or its tail ignored, its
    body copied, written to the heap, or a list per envelope; a fact key hashed per row or none for an unnamed
    mechanism; a status report ignored; field rows skipped, or kept from the last record by a follow, a rebuild or a
    recording. An equivalence probe of 3,000 records gave the same output, byte for byte, on the code before and
    after.

- **Revision 717 — coverage pass over revisions 573–716 (who connected, ports, programs) (R10):**
  - Of 8,728 reachable lines those revisions added, 204 ran in no test, most of them guards, Windows-only paths or
    record declarations. The eleven a person reaches now run: a brushed ranking by connections; bytes on records no
    process holds, said on the machine's rung alone; Other processes' card; a double click in an aggregate's process
    list and in a chosen process's relationships; a process rung's export ranked by bytes sent and received; a pair of
    programs no open was seen between, spoken; an export whose session cannot be read to name its query; icat ports
    and icat programs on a missing folder; a follow stopped mid-pause; and a publication that cannot read its
    fallback's manifest. None was wrong.
  - Debug and Release each ran 2,873 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    fourteen mutations were caught: a publication removing manifests while its fallback's manifest cannot be read; a
    follow sleeping out a pause it was stopped in; an unopened pair of programs spoken as if one connected; an
    unreadable session's query thrown rather than named as unread; a process rung's export ranked by bytes both ways
    stating a machine's caveat; a brushed connection ranking not read beside its counts, or not kept; bytes no
    process holds left unsaid, or said on every rung; Other processes' card unsaid; a double click on a listed
    process or relationship ignored; and icat ports and icat programs not naming a missing folder.

- **Revision 716 — a held pointer or manifest is waited for, and the generation fallen back to is kept (R10, `store-v1`):**
  - A pointer or a manifest another program held for a moment read as missing: opening read the session at its
    last-known-good, and a publication that read the pointer to that generation just after writing it removed that
    generation's manifest as superseded, and with it the rollback. Both are now waited for as a held file is, after
    pauses of 10 to 160 ms; while that pointer or its manifest still cannot be read, no manifest is removed and no
    file is called an orphan or removed as one.
  - Debug and Release each ran 2,867 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    fourteen mutations were caught, one by a case added once it survived: a held file read as missing, or a missing
    one as held; a held pointer or manifest not waited for, or its reason given as a missing one's; a held pointer's
    or dependency's reason ending in two periods; a pointer that reads otherwise not acquired again; a publication
    removing manifests while the pointer to the generation it falls back to cannot be read; and the sweep judging
    files unneeded, or removing them, while that pointer or its manifest cannot be read.

- **Revision 715 — the capture card says what a failure means within its lines at the smallest window (§6.8):**
  - Rendered at 1080×700, the card's six-line detail showed a long Windows path where its newer statements said what a
    failure meant: a compaction not published left that the session is whole below the fold, an unread view put its
    reason first, and an interrupted capture showed only the held file's path. The compaction's and unread view's
    words now say what they mean first and why last, in the window and icat alike, and the card names a file of the
    session from the session's folder down, which its path line names whole; the tool tips keep the reason whole.
    Other details, such as an export's or a pin's failure, still name a path whole.
  - Debug and Release each ran 2,864 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    seventeen mutations were caught: a folder given with its separator, matched in its own case only, or met once
    only; a folder named after a letter, a digit or a separator, or not followed by one; a path at the text's end; a
    separator or the ellipsis dropped; no folder emptying the text; the card showing the reason whole, or reading it
    only against an update's own folder; and the compaction's and the unread view's reasons put first, in their
    words and on the card.

- **Revision 714 — a session opened while one of its files is held for a moment is read whole (R10, `store-v1`):**
  - A file a generation names that another program held as the session opened - a scanner, a backup - read the session
    at its last-known-good generation as if the file were damaged, or refused it when that generation needed the same
    file. The generation is now acquired again after pauses of 10 to 160 ms, 0.31 s in all, before it is fallen back
    from; a missing file is not waited for. A held manifest is still read as missing.
  - Debug and Release each ran 2,859 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    five mutations were caught: a held file not waited for, not known as held, or a missing one waited for; one
    pause fewer; and a held file waited for without the generation being acquired again.

- **Revision 713 — a live capture rides out a moment a file cannot be read or written (R10, ADR-027):**
  - A follow step that could not read or write a file - another command publishing into the session, a program holding
    a file just written - ended the window's capture as interrupted and icat capture with an error, as did a view of a
    published generation that could not be read just now. The follows of a capture still recording now try the pass
    again after pauses of 0.25 to 2 s, ending the capture only when the failure outlasts them, saying so; an
    unreadable view is said on the capture card, and the capture goes on. Still open: a session opened while one of
    its files is held is read at its last-known-good generation, or refused, as if the file were damaged.
  - Debug and Release each ran 2,858 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    ten mutations were caught, one once its test measured the pauses: a follow's failed pass not tried again, tried
    without end, tried once fewer, tried without a pause, or ended without saying it failed each time; a failure not
    told before its pause; a failure of the evidence itself tried again; the window's live step given no pauses; and
    a view that cannot be read ending the capture, or said as a bounded overview.

- **Revision 712 — a coverage pass over the journal and follow code; a follow's failed compaction no longer ends it (R10, ADR-026):**
  - A follow's compaction that fails - as on a nearly full disk - ended the follow: the window stopped the capture as
    interrupted, and icat capture and icat follow ended with an error although every chunk was mirrored. It is now
    reported once and not tried again, as a recording's is, in one wording on the capture card, a finished interrupted
    capture's card, icat capture, icat follow and icat record (`compactionFailure`), and the follow goes on. A
    ticket's renewal writes over the ticket rather than emptying it first (`live-follow-v1` §1). The generated demo
    says it has nothing to re-derive, never that an earlier version published it (`demo-investigation-v1` §3). Tests
    now run a follow's compactions as it goes, renewals on every platform, cut names, declared statuses and each
    mechanism's row key.
  - Debug and Release each ran 2,853 tests with revision 473's 93 failures and 4 skips, and failed nothing else. All
    twenty-four mutations were caught: the demo's re-derivation said as an earlier version's, or replayed, or the
    demo not known by its identity; a ticket rewritten unpadded, not rewritten, rewritten for an older expiry or
    after its hold was released, or renewed only in memory; a session folder not made yet listed, or a ticket
    rewritten since it was found taken; a follow's failed compaction ending it, tried again or unreported, and no
    compaction while it follows; a recording's failed compaction ending it; the failure's reason left untrimmed; a
    cut name left unmarked, a declared status dropped, and RPC calls' or sections' rows keyed otherwise; icat
    follow's document left without the failure, for a follow or a finish, or its summary without the warning; and a
    finished capture's card without it.

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
   - A live session counts its overview from tiles: since revision 739 every chunk's but the newest's from the tiles a
     live follow keeps of it (`tile-index-v1` §6), carried with its segments, and the newest's from its rows. Since
     revision 710 it derives its processes, relations and counts from the checkpoint its generation names, reading
     only the newest chunk. Since revision 737 its whole-session byte and peer rankings come from what its derivation
     counted, extended from the generation before, and an extension opens only the segments it adds; since revision
     738 the checkpoint keeps those counts too, so a viewer opening a live generation from the checkpoint it names
     ranks so as well.
   - A zoom of a live generation counts its whole timeline from its chunks' tiles since revision 739, and since
     revision 744 its owner focuses and byte lanes from them too, where its instances bind a chunk's records as they
     were bound when it was tiled, and since revision 745 its brushes and since revision 746 its channel focuses,
     where its relations also hold each end's records the chunk's tiles name in one incarnation; a chunk a record
     read later binds otherwise still builds a segment's tiles from its rows when first drawn, and where each record
     is another pair's than the one before, which no narrow tile tallies, a chunk's tiles are read record by record,
     since revision 751 from what every chunk's tiles keep in one budget, so a live focus takes about what the rows
     take at 4,000,000 records and up to twice at 1,000,000, where every segment stays held; since revision 456 a
     finished generation's are persisted
     (`tile-index-v1`), with the records a column boundary can fall among, so its
     zooms open no segment, and since revision 457 neither do its brushes, which count from the tiles' tallies. Since revision 400 a zoom opens and tiles only the segments its interval meets, by the readings their headers
     declare: a hundredth of a reopened 1M-row session in twenty segments drew in 3-17 ms, from 86-237 ms. Since
     revision 401 a brush's counts, its lanes' bytes and a time scope's byte and peer rankings do too, with the
     checkpoint's instances and channels; since revision 402 an evidence page opens only the segments it reads and
     stops past its time scope, and since revision 403 a process's connections read only the segments their lifetimes
     meet, and since revision 736 none over the whole session: their relations count what they sent and received, and
     the checkpoint keeps it. Since revision 408 the call ranking and a process's RPC and HTTP listings read the calls
     and exchanges once paired or grouped, opening no segment, and a call's or an exchange's evidence opens only the
     segments holding its
     records. Since revision 412 the generation keeps an operation's records by segment, and a page or a zoom of them
     reads their rows alone: a busy channel's zoom takes 4 to 9 ms after the first, where it took 123 to 167. Since
     revision 440 a finished session keeps its calls and their other ends in an operation index, so a reopen's first
     brush, ranking or listing pairs none, and since revision 441 its HTTP exchanges, so its first HTTP listing groups
     none; since revision 544 a live generation's calls extend the previous generation's, and since revision 733 its
     HTTP exchanges, reading only its added segments, where they were derived over every segment, since a call's
     request and response, or an exchange's buffers, can lie in different ones (P25).
     A search reads the overview, never a segment.
     A viewer opening a live generation cold still pairs every call and follows every ALPC message once when a view
     asks for them; since revision 741 the window's first view does not wait for that (S3), and since revision 742
     each generation after it binds, groups and links again only what its records can change, copying the rest
     forward. A staged whole-state operation index, measured in revision 741, cost a publication 400–500 ms at
     25,000 calls.
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
        keeps them in its operation index. A generation of 80,000 calls projects in 0.18 s, from 0.58; since revision
        742 an extension binds, groups and links again only what the added records can change: at 800,000 calls
        0.16–0.32 s for the calls and 0.07 s for their other ends, from 0.45 and 0.38, copying the rest forward.
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
     Since revision 718 tests hold a journal's write and decode loops to what they hand on: writing a record allocates
     its envelope, resolving and reading it nothing, deriving it its rows, and reading a row back the row. Since
     revision 720 an envelope's bytes come from a pool that keeps two batches' worth, so a replayed record allocates
     306 bytes, its envelope's objects, where it allocated about 450. Since revision 750 a compaction copies each
     segment's columns as they are rather than reading its rows back whole. What stays: the callback's own admission
     keeps IC-019's Windows measurements.
     A pan still formats and lays out the ticks it draws, which §19.4 allows.
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
     direction rows (minor 7); since revision 744 a live session's add up from its chunks' tiles but for its newest
     chunk's, and since revision 745 its brushed bytes too. Revision 206 reads the interval table's bytes when it is
     shown. Each
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
`query-identity-v1.md`, `live-follow-v1.md`, `app-settings-v1.md`, `workspace-v23.md`, `support-bundle-v1.md`, `demo-investigation-v1.md`, `clock-calibration-v1.md`, `coverage-v2.md`, `comparison-v1.md`; ADR-008, ADR-010, ADR-012, ADR-013, ADR-023–031, ADR-038–041, ADR-058; the complete historical ledger linked above.
