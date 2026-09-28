# InterCat implementation status

Updated: 2026-09-28 · Plan revision: 236 · Branch: `main`

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
broker kept, including what the broker recorded after the crash. Since revision 157, a live generation's derivation
extends the previous one's, and since revision 158 the overview and minimap come from each segment's in-memory tiles,
S4's first level. Since revision 162 a finished session reopens from its derivation checkpoint instead of deriving
its processes and relationships from every record. Since revision 163 its first view comes from a persisted overview
too, and opens no segment: 4M and 10M rows both reopen in about 0.17 s, down from 1.1 s and 2.2–2.4 s.
Since revision 166 the ranked table ranks groups and processes by each process's own records, kept in the checkpoint,
where it ranked by paired TCP alone and showed a real capture as zeros.
L4 lanes wait on derived operations, and the operation view is open.
M3–M5 are not complete. All three of §11.3's sharing
presets exist: a metadata-only **report**, a reopenable redacted **session package**, and an exact, unredacted
**original evidence package**. The communication graph is a bounded §6.3 projection with a relationship-first §19.4 layout, qualified on
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
| IC-005/006 feasibility | Measured: TCP and UDP `TrafficVisualization` over IPv4 and, since revision 174, IPv6 loopback; pipe `Unsupported`; RPC `ExperimentalEvidence`; since revision 187, ALPC measured in the lab through a private system logger, linking RPC client calls to the calls that served them (ADR-034); since revision 223 captured by the product's opt-in RPC peers profile (ADR-035), whose links name each call's other end (revision 225) and draw RPC peers (revision 227) | Sections and wider mechanisms are unqualified; ALPC stays opt-in (Moderate), and no RPC byte claims. |
| IC-011 journal | Complete for validated sources | New source/content adapters need their own evidence. |
| IC-012 profiles | Metadata Explore, Focused TCP and RPC peers enforceable; the content fixture keeps InterCat's own messages through `icat record` only; Content request preview refuses start | A validated content-capable source, its payload-specific scope and impact proof before enabling Content; broader profiles remain. |
| IC-013 canonical import | ETL import into verified session implemented | Completed-import reuse/catalogue, normalizer-upgrade generations, ETL/journal overlap disclosure. |
| IC-014 broker | Authenticated pipe, protected root, durable ownership/recovery, live evidence and live preview counts, ordinary CLI/Desktop client implemented; parent-owner parser blocker repaired and CLI/Desktop Explore exercised on the affected host; a crashed client's capture qualified to stop at lease expiry, finalized and leak-free, and its session finished by the next launch from the follow's ticket (`live-follow-v1`, qualified on real ETW), and a crashed `icat capture`'s by `icat follow <session>`; a connection bounded by request rate rather than a total, so an owner keeps it for a 24-hour capture | Installer pre-creation, retail-build matrix and remaining broker release qualification. |
| IC-015 metrics/entities | Source-observation metrics, process/executable grouping, TCP/UDP relations, peer/channel lower bounds; since revision 156 the relation index counts records by their other end, and a relation's untimed records, as it derives, so the overview reads no row's relation; since revision 157 a generation's instances and relations extend the previous generation's, exactly, or are derived in full; since revision 162 a finished session publishes their state as a derivation checkpoint, which a reopen builds both from (`derivation-checkpoint-v1`), and since revision 163 its whole-session overview counts beside it (`overview-index-v1`), so a reopen opens no segment; since revision 166 each instance's own records per mechanism (`process-activity-v1`, entities-v1 §4a), extended between generations and kept in the checkpoint's format 1.1, rank the ranked table; since revision 173 IPv6 ends relate (`transport-endpoint-relation-v4`); since revision 178 RPC calls are derived as operations (`rpc-call-operation-v1`), and since revision 183 counted on the logical-operations basis (`metrics-v1` §8a) | Canonical transfer owner, operation durations and operations beyond RPC calls (counted since revision 183), resource topology (process parents and children are shown since revision 211), relations beyond TCP and UDP, full coverage epoch publication. |
| IC-015a segments | Complete observation/source-field tables; since minor 1, every byte a reader interprets has a checksum of its own, and a published segment's reader reads each column when it is first asked for; since revision 161 the reader cache charges what a reader holds and trims readers to session time and mechanism past its budget; since revision 172 `observation-v2` holds IPv6 endpoint addresses, written only for a segment that has one | Compression and derived scale structures are later work. |
| IC-016 store | Complete M1 commit/recovery/lease/explicit-retention scope; a lease confirms measured dependencies from one directory listing; a viewer opens a session from one listing and hashes its segments, dictionaries and journals after the first view, falling back to the last-known-good, stated, when a file changed; queries share verified immutable segment readers, safe across threads, admitted within 256 MiB of published payload per store, pruned to what the selected generation names; a viewer holds one store per session, a capture's writer included, and keeps readers only for the session it shows; a writer removes superseded manifests as it publishes, and a reader waits out that removal; since revision 162 an index is published as a generation of its own (`CommitIndex`), carried by no additive generation and released with the segments it describes; since revision 234 kept content is a `Content` dependency beside the journal (`content-v1`), carried like a journal and released only with its journal chunk | Rolling retention policy and cross-process pin quota; releasing content alone. |
| IC-016a checkpoint | Not started; revision 162's derivation checkpoint holds the state it would take a still-live subset of, but is released with the segments a retention releases | Live entity/endpoint state and open-operation censoring at eviction boundary. |
| IC-017 Desktop projection | Real overview, channel/evidence ladder, bounded metadata search, layout scheduling, live follow, interval/zoom/minimap with wheel and keyboard, exact L0 mechanism lanes, L1 process-owner lanes, L2 source-direction rows and L3 channel-end lanes banded by direction, with shared scale, own coverage, hover/time selection, persistent table/step focus and keyboard/wheel scrolling, exact bounded query data carried through live publications, the visible range as the default scope with a scope lock, and a bounded §6.3 graph with relationship-first layout, semantic hover, manual pinning/re-layout, quiet folding, minimal group collapse, table-shared selection, anchored carried layout, per-rung neighbourhoods with a context node, §6.7's edge double-click and back/forward history that restores each rung's interval, a per-rung timeline focus that counts what E reads, a selection highlighted in the timeline by its own exact count (§6.4) and a Ctrl+click multi-selection that Enter turns into a filter (§6.7), a labelled live edge that previews unpublished records within §12's steady-state budget (P26 asserted), a designed waiting state before a capture's first publication, a launch-time offer to finish a session a crashed viewer left, and the saved sessions listed while none is open; since revision 189 the machine and group rungs rank by records or by bytes sent or received (§6.1's metric selector), since revision 190 by RPC calls made or served, since revision 196 by bytes sent and received and by RPC errors, since revision 199 by median RPC call and serve time, and since revision 200 by peers, each listed by its basis since revision 201, and read per second over the ranked interval since revision 209; a large group keeps its process lanes when zoomed, counted coarser, since revision 210 | Resource topology once derived. L4 lanes beyond RPC calls (drawn since revision 181), and byte composition once IC-015 derives operations that carry a length. Deeper levels of the overview pyramid (S4; its top level is persisted since revision 163) and exact live cadence at 1M rows and beyond. A real screen-reader pass on Windows (the automation tree is audited headlessly since revision 131), and pin/collapse/search for lanes as scale requires. |
| IC-018 query identity | Metrics identity frozen; CLI/Desktop export scopes share projection | Full UI query identity, generation-aware numeric cache/cursors and coherent bundle publication. |
| §11.3 sharing | All three presets, CLI and Desktop: the metadata-only report (`intercat-share-report-v1`), the reopenable redacted session package (`redacted-session-v1`) and the unredacted original evidence package (`original-evidence-package-v1`) | Redacted packages above 10,000,000 rows (an interval-scoped package), since revision 217 raised the bound from 1,000,000. |
| M3–M5 release | Open | Multi-machine/workspace, full scale/reliability/accessibility/installer/build matrix and release gates. |

## Recent slices

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

- **Revision 229 — an RPC peers session reopens without opening a segment (S1):**
  - The persisted overview (overview-index-v1 minor 1) keeps the RPC links of a capture that collected ALPC, by
    instance pair and strength, before any evidence policy; the graph's RPC edges are read from them.
  - A minor-0 overview reads as before and keeps none; a brush still follows the calls it holds.

- **Revision 228 — peers of RPC calls in metrics (metrics-v1 §8a):**
  - participant(P), peer(P,Q), between(A,B) either way, grouping by peer and ActivePeers with a focus read a call's
    other end from the links; calls no link reached are disclosed by reason, and the count of peers is a lower bound.
  - Sender, receiver and a directional between stay unavailable (a call has no data direction); a capture without
    ALPC says so. A served call no client call reached is stated as not reached.

- **Revision 227 — RPC peers in the graph (M3's exit gate):**
  - When the capture collected ALPC, an RPC edge joins a process and the process that served its calls, counting the
    call records at both ends and never the ALPC records that link them; a brush counts those it holds.
  - Its hover reads as linked call records with no size; a channel's rail row leads with who served it; the view's
    disclosure names the linked calls. A session without ALPC reads nothing for them.

- **Revision 226 — a call's other end on the RPC rung:**
  - A client channel's row says who served its calls and how many; a server channel's, whom it served; both follow
    the brush. Each call's row names the process at its other end, or why none is known.
  - A call's other end carries the key of the call there. A capture without ALPC names no other end at all.

- **Revision 225 — an RPC client call's other end, through ALPC (ADR-034):**
  - `rpc-call-peer-v1` links a client call to the server call its one ALPC send's one receive began on the receiving
    thread within 5 ms, checked by interface and procedure; every other shape is stated with its reason.
  - `icat operations` names who served each client group and each call's other end, and whom a server group served.
  - A real RPC peers capture of FX-RPC-001 linked 1,499 of the workload's 1,500 calls to the recorded service host,
    and 1,860 of the session's 1,911 client calls; none conflicted.

- **Revision 224 — the RPC peers profile's cost through the product (ADR-035):**
  - `InterCat.AlpcProbe --product-impact` rotates rounds of no capture, `icat record --profile rpc-peers` and
    `--profile explore` around FX-RPC-001, reading machine processor time and the capture process's own.
  - Two series of seven rounds: the opt-in added a median 0.12 and 0.21 CPU points to Explore, its process 0.04; no
    event lost. Against no capture both profiles sat within this workstation's background (1.9 to 3.8% busy).
  - The profile states Moderate, its sources' class, with the product's figures beside it; `icat profiles rpc-peers`
    prints them and the evidence.

- **Revision 223 — an opt-in RPC peers profile admits ALPC (ADR-034, ADR-035):**
  - `icat record --profile rpc-peers` takes lifecycle, RPC calls and ALPC's send and receive, as kernel flags, with TCP
    and UDP context; without ALPC it does not start. ALPC rows publish with their message id as a source field.
  - The coverage ledger names a classic descriptor with its opcode, and counts a classic record under its header's
    class, whatever TraceEvent reports; a display names the classes a system logger delivers unasked.
  - A real 15-second capture during 300 RPC calls admitted 9,382 ALPC rows and left no session behind.

- **Revision 222 — the owned session takes kernel flags first (ADR-035):**
  - A plan's kernel flag groups give it kernel flags, enabled in one call before any manifest provider, which makes the
    session a private system logger; refused (as at the limit of eight), they refuse the capture and stop only its own
    session.
  - A kernel flag group compiles to no manifest provider request; a plan without kernel flags enables what it did.

- **Revision 221 — ALPC's send and receive admitted by opcode (ADR-035):**
  - The ALPC source compiles from its class's registration into two plans told apart only by opcode, each admitting a
    message id (`AlpcMessageId`, source field 15); every descriptor key, lookup and plan check now carries the opcode.
  - Send and receive share one journal schema entry; manifest plans and journals are byte for byte unchanged, and
    Explore no longer lists ALPC, which measured Moderate. No profile enables it yet.

- **Revision 220 — a classic kernel event's layout from TDH (ADR-035):**
  - `TdhClassicSchemaReader` gives TDH a synthetic header (class, opcode, version) and reads the class's registered
    fields before capture starts; ALPC's send, receive and wait for reply each read as one 32-bit message id here.
  - The class's fingerprint follows its layout, so send and receive share the one schema entry a journal allows them.

- **Revision 219 — how a classic ALPC record names itself (ADR-035's addendum):**
  - Every ALPC record carries the generic kernel provider, ALPC's class as its task, event id 65535 (none) and version
    2; only its opcode (33 send, 34 receive, 35/36 waits, 37 unwait) tells the kinds apart.
  - Admission keys descriptors by provider, event id and version, which all ALPC records share, so classic descriptors
    need their own key; the four kinds the relation rule reads carry a 4-byte message id.

- **Revision 218 — ADR-035: a private system logger for an opt-in capture (§4, §9.2, ADR-034):**
  - `InterCat.AlpcProbe --session-check` made a session as the capture makes one, the ALPC flags first: it became a
    private system logger, ALPC and TCP both arrived with none lost, and its slot came back when it stopped.
  - One session per capture, a system logger only for a profile that needs a kernel flag group; opt-in only, refused
    at the limit of eight (this machine runs four), and a manifest provider first is refused by TraceEvent.

- **Revision 217 — a redacted package holds up to ten million rows (§11.3):**
  - The source-field join is a sorted address array (40 bytes an observation with fields, was about 100), released
    once written; verification keeps 12 bytes a row, was 41, and no longer holds every segment in the reader cache.
  - Ten million rows with a source field each package in 162 s under a 1 GiB heap limit, where the previous code fails;
    the preview skips the join. `INTERCAT_SCALE_FIELDS` gives the scale generator source fields per row.

- **Revision 216 — an export and a process rung say which one they are (§6.4, §3.2):**
  - An export's suggested file name carries its rung's focus ("intercat-group-worker.exe-b61da093-g6.json"), so a
    second group's export no longer offers to overwrite the first; the machine rung keeps the short form.
  - A process rung's crumb and filter name the instance with its PID ("Process: worker.exe · PID 87372"), where a
    group's 27 worker.exe rungs all read "Process: worker.exe"; "Exported 1 row" reads in the right number.

- **Revision 215 — Enter acts on the row that has the keyboard (§3.2, R15):**
  - With no row selected, Enter selects the ranked row, call or record that has the keyboard and opens it; before, it
    did nothing after each descent until an arrow key selected a row, though the row said "Press Enter".
  - Enter on a crumb returns to its rung and gives that rung's table the keyboard; a rung with no rows gives it to the
    step to its records, whose rows then take it.

- **Revision 214 — what a live pass of search, rankings, the row menu, graph and timeline found (§6.7, R15):**
  - Escape out of the search, Enter on a hit and "Show the selected processes' records" give the keyboard to the ranked
    table's row; they focused the list itself, which takes no focus, and the keyboard stayed put or fell to nothing.
  - The context-menu key and Shift+F10 open a ranked row's menu, Ctrl+click's keyboard equivalent; only a right-click
    did.
  - Enter and a double click open a process node's rung from the graph at any rung, through its group, as the graph's
    help promised; they opened only groups. A node's hover card names what the gesture opens.
  - The timeline and minimap state the range in view, so a zoom, pan or fit is heard as well as drawn.
  - The original-record window and the channel browser close on Escape, as the prompts did.
  - Counts read in the right number ("1 process lane", "of 1 call with a status", "1 record" in a search hit), and a
    record's quality reads "correlation unknown", not "UnknownQuality".

- **Revision 213 — the ranked table keeps the keyboard across rungs (§3.2, R15):**
  - After Enter, E or Esc the new rung's selected row, or its first, has the keyboard, and so does a table whose rows
    arrive later (RPC channels, record pages); before, focus fell to nothing and the next arrow and Enter did nothing.
  - Esc selects the row the rung was opened from again, so the way back lands where the way down began.

- **Revision 212 — the channel browser reads by process and opens on Enter (§3.2, R15):**
  - A row names the processes at its two ends, then its endpoints and records ("queue.exe · PID 84016 ↔ worker.exe
    · PID 90512 · :15624 ↔ :15647 on 127.0.0.1 · 125 records"), from the workspace (`Channel.SecondHolder`).
  - Enter opens the selected channel's records; the list has an accessible name; the detail says where bytes are stated.

- **Revision 211 — a process's parent and children in the inspector (ntities-v1 §3):**
  - "Started by" names the parent and how it is linked (start key, or PID and start time), a parent outside the
    capture as its PID, never guessed; "Started" names the processes it started (`WorkspaceViewModel.ParentText`).
  - Go to parent selects the parent; Select its children makes them a multi-selection Enter turns into their records.
  - Live: a pool worker read "pwsh.exe · PID 82636 · linked by its start key"; the PowerShell had started 66.

- **Revision 210 — a large group keeps its process lanes when zoomed (§6.2):**
  - Past the 20,000-cell bound a group's lanes are counted in fewer, wider columns of the same interval, in the same
    pass, rather than refused; on a real capture svchost.exe's 97 lanes zoomed now draw in 206 columns.
  - The timeline pairs owner lanes with the machine row by span, not by column, and draws each at its own resolution;
    the caption and a lane bucket's card say the lanes are coarser than the view, and why.
  - Only more than 200 lanes are still refused.

- **Revision 209 — §5.2's rate: a per-second reading of the ranking (§6.1, metrics-v1 §7):**
  - A "/s" toggle beside Rank by states each row's count or sum per second over the whole interval the rows count, in
    the order of its total, which the row's second line keeps; medians and peers have no rate, and it steps aside.
  - A whole session states no interval (its first-to-last span is not one), so its rows keep totals and the note says
    to brush or zoom; the choice stays set, travels with the ranking, and equals `icat metric --metric rate` per process.

- **Revision 208 — a redacted package is shared as it is, never called unredacted (§11.3):**
  - Found by a live pass over the sharing paths on a real capture, which otherwise held: the package reproduced the
    source's byte totals, the original copy reopened as the same generation, and the report leaked no name or path.
  - On an open package, "Share redacted session…" is unavailable and says why; the copy action reads "Share this
    package…", and every step of it calls the copy the package's pseudonyms under the redacted warning
    (`OriginalEvidencePackage.ContentsFor`/`WarningFor`); `icat package --original` heads it "Copy of a redacted
    package".
  - original-evidence-package-v1 §4 no longer states "unredacted" for a copy of a package.

- **Revision 207 — §6.6's unmeasured value is drawn where the graph plots bytes (R3):**
  - Under a byte ranking, a relationship whose sends recorded no size is an open cross-hatched band, never the
    hairline of nothing sent; a node none of whose relationships measured a size is an open cross-hatched disc.
  - The legend keys "Size not measured" only while the graph draws one (`WorkspaceViewModel.GraphDrawsUnmeasured`).
  - Cards tell unknown ("1 send recorded no size") from none ("nothing sent across"), which both read unknown before.

- **Revision 206 — a real session's interval table states each interval's bytes (§6.2, R15, R21):**
  - Shown, the table reads what each listed interval's records sent and received (`SessionIntervalByteQuery`) over
    exactly the rows it lists: every record, a mechanism lane, a process's own records or one direction of them, or
    a channel end.
  - Rows speak of records ("no transfer recorded", "no receive recorded"), never of the machine; unmeasured sizes apart.
  - A bucket's hover states the same bytes once read; a hidden table reads nothing.
  - `icat timeline` gains `--mechanism`, `--process [--direction]`, `--channel [--end]` and `--bytes` (R18); a process's
    lane there is judged by the capture's coverage, as in the window (`SessionFocusedTimeline.OwnerLane`).
  - Live: on a 20-second dense capture every listing matched `icat timeline --bytes` row for row.

- **Revision 205 — the multi-selection is marked in the ranked table (§6.7):**
  - A row in the set (a process, or every process of a group) has an accent bar at its edge; a group only partly in it a
    thinner one; a screen reader hears "in the selection" / "partly in the selection" (`WorkspaceViewModel.ShareOf`).
  - Marked on the drawn rows, never by rebuilding them, so keyboard focus stays through Ctrl+Space.
  - A disk-floor recorder test that flaked once under full-suite load now waits on a publication instead of a sleep.

- **Revision 204 — metric queries reuse the session's derivation (§12.1 S1):**
  - `SessionMetrics.Evaluate` takes a caller's derivation of the generation (`MetricDerivations`) and uses it only for the
    very generation it leases; `icat metric` and `icat processes` pass the overview cache's, from the checkpoint.
  - Answers are identical with and without it (random sessions, eight metric kinds, whole and interval).
  - 1M rows: active peers by process 1.85 → 1.64 s; a records count by process stays ~1.0 s, since store verification
    (0.86 s) and per-row binding dominate - the next costs to take on.

- **Revision 203 — a TCP connection event measures no bytes (R3):**
  - Connect, accept and disconnect events (12, 13, 15, 28, 29, 31) admitted the provider's `size` field, always zero, as
    transport bytes, so each was a measured zero-byte transfer ("TCP accept · 0 B", counted by endpoint activity). The
    provider's messages state bytes only for sends, receives and retransmissions; the catalog now admits no size there.
  - Sessions keep the plan they were captured under. Real ETW: `icat measure tcp` still TrafficVisualization, 64/64
    byte-measured, 37,313 B equal to truth; a recorded capture's connection records state no size.

- **Revision 202 — a second live pass: search, evidence and the original record (R5, R15):**
  - A record raised by, rather than naming, its owner read "owned by raised by gateway.exe" to a screen reader; it
    now says "raised by", "owned by" or "with no owner process named" (`EvidenceRowText.Ownership`).
  - The original-record window names a public provider beside its identity (`KnownProviders`) and says its envelope's
    codes in words ("approved metadata, retained", "QPC (performance counter)").
  - Left open: connect/accept events admit the source's always-zero size field as bytes ("TCP accept · 0 B").

- **Revision 201 — the basis and metric selector (§6.1, §3.2):**
  - The ten metrics are listed by basis under a heading each: **source observations** (records, bytes, peers) and
    **logical operations · RPC calls** (calls, errors, times).
  - The chosen metric's basis stays beneath "Rank by" ("observations" / "operations"), in full on hover and to a screen
    reader; each option's spoken name ends with its basis. One selector keeps every metric one choice away.

- **Revision 200 — rank by peers (§5.2, `metrics-v1` §6.1):**
  - **Peers:** the distinct process instances at the other end of each process's records; a lower bound beside the
    records whose other end is unresolved, and "unresolved" (after every row with a peer), never zero, when none resolved.
  - Counts overlap, so a group's peers are its members' records together and the note counts processes with a peer.
  - A read over the overview's own derivation (each row's other end cached with `SegmentBindings`) equals `icat metric
    --metric active-peers` by process (random sessions, whole and interval) and by executable; exports name
    `active-peers`. Live: queue.exe ranked first with 28 peers, as the command line answers.

- **Revision 199 — rank by RPC call time and serve time, the median (§5.2):**
  - **RPC call time / serve time:** the median time each process's completed client calls took, or it took to serve
    its completed server calls (`metrics-v1` §8a `ClientCall`, `ServerExecution`), slowest first, from the call read.
  - A stop paired with no start is never timed ("untimed", after timed rows); a group's median is its members' calls
    together, never a sum or mean of medians; a row's second line names the calls behind its median.
  - Equal to `icat metric --metric duration` by process (random sessions, whole and interval) and by executable;
    exports name `rpc-call-time-median` and `rpc-serve-time-median`. Live: lsass.exe served 535 calls, median 20.6 µs.

- **Revision 198 — the relationship table lists what the rung's graph draws (R15):**
  - **Why:** below the machine rung the graph draws the rung's neighbourhood, but its table equivalent still listed every
    relationship of the session; revision 197's live pass found it listing a whole machine's relationships at a channel.
  - **Now:** below the machine rung the table lists every relationship with an end in the neighbourhood, and a caption
    names the neighbourhood as the graph does and counts the rest ("… · 1 more elsewhere, listed at the machine rung").

- **Revision 197 — what a live pass found: coverage and bytes said truthfully, rows that read apart (R21, R5, §3.2):**
  - **Coverage:** on a 20-second dense capture whose ledger covered every interval, a process's source-direction rows
    hatched every quiet interval unknown, and every ranked row said "unknown coverage". Direction rows are now judged by
    the capture's coverage, as process lanes are (revision 165); a real session's process and group rows state the
    capture's coverage over the session or the brushed interval, and its paired channels TCP's.
  - **Bytes:** the channel rung states the bytes sent across its channel instead of "bytes unknown"; a real session's
    interval table leaves out the byte column its timeline never sums and says how an interval's are read; the
    relationship table tells "nothing sent across" and "no size measured" from "reading bytes…" and "bytes could not
    be read".
  - **Rows that read apart:** a process's paired channels read by their peer, own port first (`:48048 ↔ :48058 on
    127.0.0.1`), where the rail cut a server's channels to one truncated endpoint pair; relationship ends carry PIDs;
    a process's role reads in words ("created during capture"); the rail widens with the window and its edge drags;
    the channel browser opens from a group's rung.

- **Revision 196 — rank by bytes sent and received, and by RPC errors (§5.2):**
  - **Bytes sent and received** (endpoint activity): every transport contribution of a process's own records, including
    those stating neither side; it counts a local transfer at both ends, and the note and caveat say its sum is no
    transfer total. At a process's rung it ranks channels by the process's own end, both directions.
  - **RPC errors:** completed calls made or served whose stop reported a non-zero status; calls with no status are
    unmeasured ("status unknown"), never successes, and rank after rows with known outcomes.
  - Both equal `icat metric` (`endpoint-activity-bytes`, `errors`) grouped by process over random sessions, whole and within
    an interval; exports name `bytes-sent-and-received` and `rpc-errors`.
  - **Race fixed:** a Release full-suite run failed the RPC rung navigation test once. Revision 194's read of a
    selection's bytes re-ranked the rows when it arrived, which a navigation in progress could see mid-step. A read made
    for a description now only keeps its bytes and refreshes what states them; only a ranking's own read re-ranks.

- **Revision 195 — the graph is sized by the ranking's metric (§6.3):**
  - **Why:** §6.3 fixes edge thickness and node size to the selected metric so the panes cannot disagree about magnitude;
    since revision 189 the table could rank by bytes while the graph drew records.
  - **Now:** under a shown byte ranking, an edge's thickness reads the bytes sent across the relationships it draws
    (each transfer once at its sender) and a node's size the bytes its members' relationships carry, on the same log
    scale (`GraphDisplayEdge.Weight`, `GraphDisplayNode.Weight`, `GraphDisplay.WithMagnitudes`). Hovers name the scale in
    bytes; records stay what texts and in-brush dimming read; a call ranking keeps the TCP graph by records.
  - **Live run:** a 20-second dense capture drew by bytes sent without disturbing its layout.

- **Revision 194 — a real session's bytes are read and stated, not called unknown:**
  - **Why:** the overview sums no bytes, so "bytes unknown" in a real session's totals, rows, inspector, hovers and
    relationship table said nothing true: its records carry sizes no view had read.
  - **Now:** selecting a process, group or set, or showing the tables, reads the scope's bytes through the byte ranking's
    per-scope read (shared when a byte ranking already read them); the inspector says "reading bytes…" and then states
    the selection's bytes sent and received, measured or unmeasured. A relationship's hover and table row carry the bytes
    sent across its channels by either end, each transfer once at its sender. Totals and spoken rows drop the phrase.
  - A view without the session's directory, which cannot read bytes, keeps saying they are unknown.
  - **Live run:** on a 12-second TCP workload recording, selecting its group read 132 KB sent and 132 KB received.
  - **Flake fixed:** the clean-worktree verification caught the redacted report's leak test failing about once in two
    thousand CSV reports: CSV writes the random report id as 32 bare hex digits, which the test's random-value pattern
    did not strip, so the id's own digits could read as a leaked port. The pattern now strips that form too.

- **Revision 193 — RPC channels and calls answer the rung's scope:**
  - **Found:** under a brush or a zoomed view a process's paired channels counted the interval while its RPC channels
    still counted the whole capture, unlabelled, and a process with no call in the brush lost its RPC rows (M2's
    all-panes-agree gate).
  - **Fixed:** `SessionRpcCalls.Channels` and `Calls` take the interval; a channel holds the calls the interval holds by
    the record that counts each (`operations-v1` §5b, the operations metric's rule), with their failures, unpaired stops,
    durations and the call records read within it, and the channel's rung lists exactly those calls. A channel with none
    stays at zero; an interval holding every reading answers exactly as the whole capture (tested).
  - **Desktop:** the process and channel rungs re-read their RPC rows when the scope changes, keeping the previous rows
    until the new ones arrive; whether a process has RPC channels at all is decided from the whole session.

- **Revision 192 — a process's channels rank by its own bytes on each:**
  - **The read:** the byte ranking's pass also sums each end of every drawn channel (admitted paired TCP): the records
    its owner holds there, bound under the evidence policy as a process's records are (`SessionByteMeasures.ByChannelEnd`).
  - **The rung:** bytes sent or received rank a process's TCP channels by its own end, with unmeasured and empty rows
    after the measured ones; its RPC channels read "no size" and follow. A call ranking leaves the rung by records and
    the note says why. The selector now shows at the process rung too, and steps aside at the evidence rung.
  - **Export:** ranked the same way, with a caveat naming this process's bytes on each channel; `icat export --at` reaches it.
  - **Live run:** on a 15-second TCP workload recording the server's rung ranked its three connections 141, 128 and 111 KB
    by bytes sent, above its two RPC channels.

- **Revision 191 — a live test of the command line and the Desktop, and what it found:**
  - **Run:** a fresh 15-second `icat record` with the TCP, UDP and RPC workloads in it; every `icat` inspection
    command (session, overview, channels, evidence, raw, timeline, processes, operations, metric, export in all its forms,
    package, compact, checkpoint, rederive, retain, staging, recover); then the Release app driven through UI Automation:
    search, group to process to channel, E to its evidence, and Enter on a record for its original journal record.
  - **Fixed:** the general help now names `export --rank-by` and its five rankings; the original-record window wrote
    the raw identity as a C# record prints itself (`RawRecordId { CaptureId = … }`) and now reads it in words, held by a
    headless test.
  - Everything else behaved as designed, including exit code 1 for an evidence export cut at `--limit` and for a
    retention that would release nothing.

- **Revision 190 — rank by RPC calls made or served (§6.1's selector, the logical-operations basis):**
  - **The metrics:** a process's calls made are the client calls it completed, and its calls served the server calls,
    each counted by its stop (`metrics-v1` §8a) and bound by the call's first record under the evidence policy. The two
    sides of a local call are separate operations, so each ranks on its own (`SessionCallRanking`, one pass for both
    sides over the calls the generation derives once).
  - **Beside the count:** failed calls (a stop status other than 0) and stops paired with no start, stated and never
    counted; a row with only such stops reads "0 completed" and ranks after every row with calls, then "no calls".
  - **Not collected is not zero (R21):** where the coverage ledger says RPC was not collected, the rows keep their
    records ranking and the rail and an export say why; RPC coverage short of covered is stated in the note and export.
  - **Export:** `rpc-calls-made` and `rpc-calls-served`, a `ranked_failed` column (empty for bytes), `icat export
    --rank-by`, and the sharing report's allowlist.
  - **Desktop:** the byte ranking's per-scope reads became one generic holder per family, so bytes and calls share the
    one-step interval application, the live stand-in and the no-retry-after-failure rule.
  - **Live run:** a 20-second recording while the RPC workload called the service control manager: calls made put the
    workload first (1,514), calls served put services.exe first (1,537); both sides together equal `icat metric
    --basis logical-operations`'s 4,298 completed calls, and each executable's count equals its row there.

- **Revision 189 — the ranked table ranks by bytes sent or received (§6.1's metric selector, §5.2):**
  - **The selector:** "Rank by" under the ranked table's summary offers records, bytes sent and bytes received at the
    machine and group rungs. Bytes are the transport-observed bytes of each process's own send or receive records,
    sender- or receiver-accounted, exactly what `icat metric --group-by process` answers per instance
    (`SessionByteRanking`); a group sums its processes. A rung of channels keeps records, and the selector steps aside.
  - **Scope and timing:** one pass off the UI thread reads both directions for the scope the rows count: the whole
    session, a brush (read beside its counts, so the rows change once) or the visible range. Until the bytes answer
    that scope the rows keep their records order and the note says "Reading bytes sent…"; a failed read says why.
  - **Unmeasured is not zero (R21):** measured rows first, a measured zero among them; then rows whose records recorded
    no size ("unmeasured"), then rows with none ("no sends"). The records move to the detail line, the spoken name gives
    the bytes with their records, and the rung total drops the paired channels' "bytes unknown", which contradicted it.
  - **Live and export:** a publication keeps the choice and shows the previous publication's bytes, marked "updating",
    until its own arrive. An export names its ranking (`rankedBy`, a defining caveat, each row's value with its measured
    and unmeasured records); `icat export --rank-by` writes the same file; the sharing report's allowlist admits the
    ranking's name and those three numbers.
  - **Live run:** on a 25-second dense recording, bytes sent put worker.exe first (2.8 MB on 1,296 sends) where records
    put chrome.exe, and every group's value equals `icat metric --group-by executable`. The Release app was driven
    through UI Automation and keys posted to its window: selector, group rung, bytes received and the zoomed scope.

- **Revision 188 — what collecting ALPC costs, measured before building its capture:**
  - `InterCat.AlpcProbe --impact` runs the RPC workload in alternating pairs with and without the probe's ALPC system
    logger, whose consumer only counts. Seven pairs: a median of **1.86 CPU percentage points** at about 1,160 ALPC events
    a second, nothing lost - collection alone, a lower bound, already **Moderate**.
  - A second series ran while other work loaded the workstation (40 to 98% busy with or without the session); it is
    committed as a record and decides nothing (`bench/results/alpc-impact-20260927T171439Z`).
  - **Consequence:** ALPC cannot join Explore, which admits Low sources; its capture, when built, is an opt-in profile
    for resolving RPC peers (ADR-034's addendum).

- **Revision 187 — ALPC measured: it links an RPC client call to the call that served it (ADR-034):**
  - **The spike (IC-006):** `tools/InterCat.AlpcProbe` owns a private system logger for one run - uniquely named, never
    the NT Kernel Logger, stopped with the run - collecting the kernel's ALPC flag group, process names and RPC call
    events while FX-RPC-001 calls the service control manager. Four runs, no event lost, no session left behind.
  - **Message ids are not identities:** 40 to 125 distinct ids per run for up to 33,000 sends, most sent by several
    processes.
  - **The chain works:** the client call's one ALPC send on its own thread, the one receive of that id in another process
    before the call stops, and the server call that begins on the receiving thread within 5 ms. 10,003 links, every one
    with the same interface and procedure on both calls, no server call claimed twice, every reply back to the client;
    a message received twice within a call was left unlinked.
  - **Admitted nowhere:** product capture creates no system logger (ADR-002). Only counters are committed
    (`bench/results/alpc-feasibility-20260927T165107Z`).

- **Revision 186 — RPC call durations, by named interval, cohort and statistic (ADR-033):**
  - **The request:** `icat metric --metric duration --duration client-call|server-execution` with `--cohort
    completed|started` (default completed, §19.2) and `--statistic median|p95|max` (default median). The interval has no
    default, because a client call and a server execution are different quantities (§5); the defaults are written out
    before a request is identified. Three new §23 enumerations carry them.
  - **Censoring stated, not measured:** a call of the cohort without both records - a stop whose start came before the
    capture (left-censored), a call open at capture end (right-censored), a record with no activity id or a reused one -
    is stated by state and never given a duration. The other side's calls are counted apart as another interval.
  - **The answer:** a distribution by nearest rank (count, minimum, median, 95th percentile, maximum) with the summed
    call time and the busy time their union covers. Grouped, groups rank by the statistic with each group's measured
    calls beside it; a remainder is its calls' own distribution, and the result says its groups do not add up.
  - On a real 30-second recording: 3,945 client calls, median 44 µs, 95th percentile 249 µs, one 11 s call; 22.2 s
    summed, 21.6 s busy. Served calls ranked by 95th percentile put a three-call process first, which the new "Calls
    measured" column makes plain.

- **Revision 185 — a busy channel's calls are drawn as density rather than cut off:**
  - **What a real session needed:** a view holding more calls than the call lane draws one by one (4,000) drew the first
    4,000 and said how many it left out, so a whole-session view of a busy channel showed its first minutes and then an
    empty lane. It now falls back to §6.2's density and drops no call (R21).
  - **The density:** every call in view is read once into columns about five logical pixels wide (§6.2's minimum
    drawn width, so each is a pointer target), each counting the calls running in it (§21.1's overlap count) and the
    failed ones (`SessionRpcCalls.Spans`, `RpcCallDensity`). Height is the count on a log scale above the occupied floor;
    the failed share is caution ink on top, so a failure never disappears when zoomed out.
  - **Interaction:** the lane note and caption say it is density and up to how many calls a column holds; a resting
    pointer names a column's calls and failures; a click selects its interval; zoomed in to the budget, each call is a
    bar again.
  - **Live run:** the Release app opened a 90-second Explore recording in which the RPC truth workload made 8,930 calls
    to svcctl, walked to that channel through UI Automation, clicked a column and zoomed with keys posted to its window
    alone, and drew density, the selected column, and each call again at 2.5 s.
  - **Set aside, with the measurement:** extending the calls between live generations. At this workstation's rate
    (about 2,000 calls a minute) re-pairing a ten-minute capture costs tens of milliseconds a generation.

- **Revision 184 — RPC calls pair 3.7 times faster, measured before and after:**
  - **Measured first:** a new opt-in measurement (`RpcPairingScaleTests`, `bench/results/rpc-pairing-20260927T153638Z-*`)
    pairs the calls of 1M- and 10M-record sessions, four records in ten an RPC call record with its source fields
    (200,000 and 2,000,000 calls). With revision 183's build, 2,000,000 calls took 4.9 s to pair with every column in
    memory, a reopened session's first RPC rung 5.4 s, and the last live generation of the 10M session 5.3 s. Most of it
    was sorting four million large records by key and reading every field row whole.
  - **The change:** records are ordered by sorting their readings as plain integers, a tie broken by raw locator; one
    walk pairs every key at once and holds only the keys with a call open; the source fields are read a column at a time
    (`SegmentColumnSlice.GuidAt`, `SegmentReaderV1.Presence`), a field named twice still refused; and the calls are put
    in groups by a sort of plain integers.
  - **After, on the same sessions:** 2,000,000 calls pair in 1.3 s, the first rung opens in 1.8 s and the last live
    generation pays 1.7 s; 200,000 calls pair in 108 ms (402 ms before). Held memory is unchanged.
  - **Same calls:** old and new builds list byte-identical calls on the real Explore session and the 1M session, and the
    same calls on forty random sessions with reused ids, shared readings, late delivery and fields in later chunks. The
    one change is the order of calls whose first records share a reading: by raw locator (operations-v1 §5a), not by
    where a segment cut stored them.

- **Revision 183 — operations started, completed and failed, counted from the RPC calls (ADR-032):**
  - **The basis answers:** `icat metric --basis logical-operations` counts `operations-started`,
    `operations-completed` and `errors`, and their rates, over an interval, for one `--owner`, ungrouped or grouped by
    process, executable or mechanism, with the counted records as `--evidence` (`contracts/metrics-v1.md` §8a). Until
    now every session answered that basis as unavailable.
  - **By the record that puts a call in scope:** a started count takes a call by its start, a completed count by its
    stop when the stop is paired with its start, and an error count the completed calls whose stop reports a status
    other than 0; §21.1's call from 0.5 s to 2.5 s starts in the first second and completes in the third. A call open at
    capture end is started, never failed.
  - **Stated, not counted:** each answer lists the calls in scope by state. A stop paired with no start is stated with
    its reason, and with its status when that is a failure, so the completions a metric counts are the ones
    `icat operations` and the ladder count. A completed call with no status is unknown, and an error count of only such
    calls is unmeasured, never zero.
  - **Still unavailable, with the reason:** anything that needs a call's other end (participant, sender, receiver,
    peer and between filters, a peer grouping, peer counts), durations (no cohort in a request yet), byte totals (a
    call carries no length) and every mechanism but RPC. The two TCP records of one transfer are never renamed as a
    call, which asserts P4 for the first time.
  - **Identity:** an answer names `rpc-call-operation-v1` as its correlation revision and `process-binding-v3` beneath
    it; the canonical corpus gains three lines and changes none.
  - On revision 180's real Explore minute: 2,007 calls started, 2,000 completed, none failed, 7 stops stated as paired
    with no start; the same 2,000 completions `icat operations` lists.

- **Revision 182 — the RPC rungs tried in the real window, and what that found:**
  - **Opening a session by name:** `InterCat.Desktop.exe <session folder>` opens it once the window is shown, as
    "Open saved session" does, so a session can be opened from a shortcut, a script or a dropped folder; one that
    cannot be opened says why. It is what let a person-free run open the recorded session.
  - **The run:** the Release app opened revision 180's real Explore recording maximized on a 3856×2128 display, and UI
    Automation walked the ranked table from the machine to svchost.exe, PID 3632, its busiest RPC channel (266 calls)
    and one call's two records. Only the InterCat window was captured, through `PrintWindow`.
  - **Found and fixed:** a process whose rung lists only RPC channels stated "0 observations" in the rail and the
    inspector, because the total counted paired channels alone; it now states its call records ("798 call records in
    2 RPC channels · RPC carries no size"). On the tall window the call bars were capped at 12 px and read as specks;
    they now grow with the lane to 28 px. The saved-session note no longer says the graph is the only place calls are.
  - **A lesson for the next run:** the driver first sent Enter with `SendKeys`, which goes to whatever window has the
    foreground; one keystroke may have reached another application. Keys are now posted to the InterCat window alone.

- **Revision 181 — an RPC channel's calls are drawn in the timeline, each from its start to its stop:**
  - **The lane:** at an RPC channel's rung the timeline draws the machine's records as grey context and, under them, one
    lane of the channel's calls: a bar from each call's start to its stop, at least 2 px, in the RPC hue; a failed call
    in caution ink; a call open at capture end faint, running to the view's edge; a call with one record a mark. The
    selected call is outlined. The lane's note says how many calls are in view, or how many of them it drew.
  - **Stacks mean concurrency:** calls that ran at the same time stack, up to six rows. The first render packed by
    pixels, and on a real session calls lasting microseconds stacked six high though they ran one after another; they
    are packed by time now, so a burst of sequential calls reads as one row.
  - **Hover and click:** a resting pointer names the call's duration or state, status, start and procedure; a click
    selects its row, whose records are one step away, and never brushes an interval.
  - **Read per view:** the calls in view come from the generation's paired calls, at most 4,000 of them, with the count
    of every call the view holds (`SessionRpcCalls.Spans`).
  - Checked in headless renders at 1080×700 and on the real recorded session (a svchost channel's 266 calls over a
    minute, whole and zoomed).

- **Revision 180 — Explore takes RPC, and a 32-bit process's calls are admitted (adapter 0.8.0):**
  - **Measured class recorded:** RPC is Low (revision 176's seven pairs), so Explore compiles it beside process and
    network. It joined once its records bind to a process, pair into calls and reach the ladder (177–179).
  - **Found by the live test:** the first 60-second Explore recording counted 24 RPC records undecodable, every one a
    `PointerWidthMismatch`: a 32-bit process (mirc.exe) raises its records at width 4, and admission refused any width
    but the plan's. The ledger said so honestly, which hatched every timeline column as a partial gap and put "RPC has
    a coverage gap" in the status bar.
  - **The fix:** a descriptor whose admitted fields all lie before any pointer-sized field, and are none, decodes at
    the same offsets at either width. Its plan says so (`pointerWidthIndependent`), admits a record of either width,
    and the journal envelope keeps the record's own width; replay accepts it by the same rule. Any other plan still
    admits only its own width. RPC's three admitted fields come before its strings, so they qualify.
  - **Checked again on real ETW:** a second recording counted RPC Covered, 4,014 records, none refused; the journal
    held 24 width-4 records from mirc.exe, which paired into 12 completed calls with their procedures, and
    `icat rederive --check` replayed all 5,863 records exactly. Over the two recordings `icat operations` paired 2,254
    of 2,260 and 2,000 of 2,014 calls: the rest were open at capture end, begun before it, or carried no id. Headless
    renders of the real session's process, RPC channel and call rungs read as designed.
  - **The owner line of an RPC record** now reads "raised by svchost.exe · PID 3632": its stored attribution quality
    describes a payload that names no owner, and the line says where the owner came from instead.
  - **Adapter 0.8.0**, because admission changed. The fixtures were not measured again: every record of their 64-bit
    workloads is width 8, which both versions admit alike.

- **Revision 179 — a process's RPC calls on the ladder: channels by interface, their calls, each call's records:**
  - **L2:** a process that made RPC calls lists them as channels of its own, one per side and interface, ranked by
    records among its paired TCP channels. A row leads with the interface ("svcctl (Service Control Manager)", named
    from the protocol specifications, else its UUID) and states the side, the calls, failures, open calls and the
    median duration under it. The overview holds no calls, so a process's channels are read when its rung opens, and
    only for a process whose own records include RPC.
  - **L3:** an RPC channel lists its calls in reading order, 100 at a time with "Load more calls (M)". A call's row
    leads with how it went (its duration, or open, unpaired, ambiguous), then its time and status.
  - **Evidence:** Enter on a call opens its start and stop records; E on a channel opens every record of its calls.
    Both are evidence scopes of their own, paged with the usual cursors, and they say when a later generation no longer
    holds the channel or call. A channel or call is keyed by its instance, side, interface and first record, so a live
    refresh keeps the rung.
  - **Rendered and checked** at 1080×700: the rail shows each row's interface and outcome, the crumb and filter keep the
    whole name, and the paging button names what it loads. The session disclosure no longer says there is no operation
    rung.
  - **Not yet:** the timeline does not draw an RPC channel's calls apart from the rest; revision 180's call lanes do.

- **Revision 178 — an RPC call is an operation: its start and stop, paired by activity id (ADR-031):**
  - **The correlator, stated before it ran:** `contracts/operations-v1.md` fixes what §7.4 asks of every correlator.
    A call record joins by its process, its side and its activity id. A start opens a call and the next stop closes
    it. Nothing is paired by time (P8), and nothing expires. A start still open at the end is censored, not failed; a
    stop without its start is given none; a record with no id pairs with nothing. An id reused before its stop leaves
    the overlapping calls ambiguous rather than guessed. The client and server sides never meet, because FX-RPC-001
    measured different ids on each (P7).
  - **What a call holds:** its first record's identity, its binding, interface, procedure and protocol from the start,
    status from the stop, and the duration between them on one clock. Groups by process, side and interface state
    started, completed, failed and open calls, every unpaired call by reason, and the durations' median, 95th
    percentile and maximum, each a duration a call took.
  - **Checked against the real capture:** replaying FX-RPC-001's committed records pairs 60 of 60 client calls to the
    Service Control Manager, 2 of 3 to the Local Security Authority (a thread's first call carried no id), and 61 of
    61 served calls. The durations match ones computed from the evidence file apart from this code: the client's calls
    took a median 25.6 µs, the host's served part 4.8 µs.
  - **`icat operations`** lists the groups, and with `--pid` or `--interface` their first calls, in text or JSON.
  - **Cost:** 500,000 synthetic calls pair in 0.66 s, allocating 246 MiB. A first measurement allocated 2.4 GiB,
    nearly all of it enum comparisons in the sort boxing their operands.

- **Revision 177 — an RPC record belongs to the process that raised it (ADR-030, `process-binding-v3`):**
  - **Measured per side first:** FX-RPC-001 on adapter 0.7.0 started every truth call in the calling process. It saw
    122 of 122 server-side calls to the Service Control Manager's interface raised in the service host the workload
    recorded (services.exe). `icat measure rpc` now reports and stores that server side: the host's calls to the
    interface inside the client's call window, with their completions (61 of 61 paired by activity id).
  - **The rule:** a record belongs to the PID its payload names. When the payload names none, and its mechanism was
    measured to raise its records in the process they describe, it belongs to the process its header names. Only RPC
    qualifies, and a test pins that list. A kernel record's header stays context (§4.1). The stored row is unchanged:
    the derivation reads the header column only in a segment where such a record names no owner.
  - **One rule everywhere:** instances, bindings, `owner(P)`, each process's own records, relation holders, evidence
    text, the rail, exports and the command line all ask whose a record is the same way. An RPC server that no
    lifecycle record names, as a long-running host is not, becomes an activity-only process of its own calls.
  - **Earlier checkpoints:** a `process-binding-v2` checkpoint is v3's wherever its counts hold no record without an
    owner, and is read as such. Any other is derived again, with the reason in the overview, because a count cannot
    say whether an ownerless record was RPC. A format-1.0 checkpoint holds no counts, so it is derived again. Over this
    workstation's earlier scratch sessions, a format-1.1 checkpoint of 1M rows was read, and four format-1.0 ones each
    said why they were not.

- **Revision 176 — RPC's capture cost measured, the first step toward RPC operations (M3):**
  - **Cost:** seven pairs with RPC alone put it at a median 0.52 CPU pp (Low), with no throughput regression,
    loss-free and valid (`bench/results/capture-impact-20260927T121747Z`). The pairs spread from −4.2 to +4.7 pp, so
    the median is a noisy Low. The harness now captures the sources it is told to (`--sources process,network,rpc,file`),
    and still defaults to the Explore pair.
  - **Still qualified:** FX-RPC-001 on adapter 0.7.0 observed 12 of 12 truth calls, bound to their interface and
    completion-paired through the activity id, reproduced in a second run. Of the fixture process's 63 client calls,
    62 carried an activity id and all 62 paired.
  - **Not yet admitted to Explore:** a measured class is what admits an optional source to Explore, and RPC's records
    would arrive with no process. They name none in their payload, and only a payload owner binds (entities-v1). So
    the catalog keeps RPC unmeasured until its records bind and its calls are operations (open work item 3).

- **Revision 175 — a long IPv6 endpoint keeps its port on screen, from a render of the channel rung:**
  - **The lane:** a channel-end lane's endpoint line holds 24 characters and cut the end off, which a global IPv6
    endpoint reaches and loses its port to, though the port is what tells a looped process's two ends apart. It now
    drops the middle of the address: `[2001:db8…70:7348]:50000` (`EndpointText.Abbreviated`).
  - **The chips:** the breadcrumb and the filter chip abbreviate a channel's two endpoints the same way
    (`ChannelNames`), and their tooltips and accessible names keep the whole name.
  - **A stray character:** the breadcrumb pins its current crumb to the right, and the crumb cut at its left edge
    showed as a lone `0` or `)` beside the position, over IPv4 as over IPv6. A crumb the edge cuts is no longer drawn,
    and keeps its place and accessible name.

- **Revision 174 — IPv6 traffic is captured, measured on real ETW (ADR-029):**
  - **Admitted by measurement:** TCPv6 26-31 and UDPv6 58-59 under both keywords (0x30). FX-TCP-002 and FX-UDP-002
    ran the loopback truth workloads over `::1` (`--ipv6`) and met every applicable §14.2 criterion at 100%, reproduced
    by a second run. TCP measured 64 of 64 operations, 0 false peers of 4, and 25,403 B both sides; UDP 64 of 64 and
    9,166 B. Each IPv6 descriptor names its endpoints as its IPv4 counterpart does, and every UDPv6 receive's header
    PID differed from its owner, as over IPv4.
  - **The schema had hidden the fields:** TraceEvent rebuilds a manifest from TDH without a 16-byte address's `length`
    or `win:IPv6` out type, so every field after one was unreachable. The adapter reads each fixed binary length from
    TDH (`TdhGetManifestEventInformation`) and writes it back before parsing. The kernel network schema fingerprint
    covers it, and a manifest with nothing restored keeps its text.
  - **Whole addresses end to end:** an `Address128` slot copies 16 bytes into one of a record's two address slots. The
    journal projects such a record as `IAP2`, and an IPv4 one stays `IAP1` byte for byte. The normalizer fills
    `observation-v2`'s columns. A recorded IPv6 session paired its channels (`[::1]:60413 ↔ [::1]:60414`), attributed
    a UDP client's 11,674 B to its server on 16 of 16 records, and replayed its journal through `icat rederive --check`.
  - **Adapter 0.7.0**; `icat measure|verify tcp|udp --ipv6`; the M0 flow key and evaluator carry IPv6 loopback, and
    the committed IPv4 evidence reads back unchanged.
  - **Impact of the wider keyword:** seven pairs per source measured the network source at a median 0.00 CPU pp
    (observed −1.45), with no throughput regression and loss-free, where the IPv4 keyword alone measured 1.61 pp. That
    is within this machine's noise, so the catalog keeps it Moderate, the higher of the two.

- **Revision 173 — an IPv6 record finds its other end:**
  - **128-bit ends:** `transport-endpoint-relation-v4` keys an end by its family and its addresses in 128 bits, read from
    `observation-v2`'s columns for an IPv6 record. An IPv6 connection or datagram flow gets a peer, a channel and a
    graph edge, named in bracketed text (`[::1]:8080 ↔ [::1]:50000`). The families never meet: an IPv4-mapped address
    is kept as the source named it, and an IPv4-compatible `::7f00:1`, the very number 127.0.0.1 is, stays an IPv6 end.
    `::` names no endpoint, as `0.0.0.0` does not. IPv4 ends order, pair and number exactly as before.
  - **A new rule identity, and no lost checkpoints:** `relations-v1` §8 makes relating records v3 left without an end
    a new rule, as revision 57 did for UDP. The first plan for this slice missed that. Checkpoint format 1.2 holds an
    IPv6 end's addresses in 16 bytes. A v3 checkpoint is read as v4's when it counts no related record without an end,
    the one case where the two are provably the same state; otherwise it is set aside and derived again. The 10M
    scale session's v3 checkpoint met that, so its reopen still opens no segment.
  - **Tested:** IPv6 TCP and UDP pairs, both families on the same ports, mapped and IPv4-compatible addresses and `::`;
    the random captures behind the checkpoint, incremental-derivation and query-equivalence tests now draw one
    connection in four over IPv6; format 1.1 read as written, a 1.1 checkpoint claiming an IPv6 end refused, and the v3
    acceptance and its refusal. A key that dropped its family was caught.
  - **Measured:** the §12 gates at 10M, three runs each on this machine, read the same under this build and revision
    171's: a brushed ranking 215–226 ms against 214–227 (budget 250).

- **Revision 172 — an IPv6 endpoint has a place in a row:**
  - **A table, not a wider column:** `observation-v2` is `observation-v1`'s 39 columns, then two 16-byte address
    columns (`segment-v1` §5, table 3, codes 44 and 45, type `Address128`). A row keeps its addresses in the columns
    its family names, never both pairs; ports stay where they were.
  - **Nothing for IPv4:** a segment is `observation-v2` only when one of its rows has an IPv6 address. Any other is
    `observation-v1` byte for byte: a digest measured with revision 171's build pins it. It also stages the same bytes,
    so a derivation flushes where it always did, and a null address writes nothing. A reader before this revision
    refuses table 3 rather than reading an IPv6 row without its addresses.
  - **Every reader of an address:** a redacted package maps an IPv6 address into 2001:db8::/32, keeps `::` and `::1`,
    and gives an IPv4-mapped address its IPv4 part's pseudonym. Its tally counts both families as one namespace of
    hosts, so a mapping that parted a host from its mapped twin is refused. The share report tokenizes an IPv6
    endpoint by its address, so two hosts sharing a port stay two. The evidence text and `icat session --rows` write RFC
    5952's form, bracketed before the port (`EndpointText`).
  - **Tested:** round trips with nulls, a mixed generation, compaction and its reopen, row validation, the pinned
    IPv4 digest, 13 canonical-text cases, and a redacted package holding global, link-local, loopback and mapped IPv6
    rows. A mutation that passed IPv6 addresses through the package unchanged was refused by its own verification.
  - **Not yet:** no capture produces an IPv6 row, and an IPv6 record has no relation end (open work item 3).

- **Revision 171 — the inspector ordered by use, from revision 165's live test:**
  - **Actions before reference:** the selection and its summary come first, then what can be done with it, then the
    evidence-quality key. At an ordinary height the eight actions stood below the key, under a scroll bar a hairline
    wide, and read as one clipped button; at 1456×939 all of them are now in view.
  - **Name above value:** the time scope is stated as the other facts in its card are, so "No time recorded yet" or a
    long range no longer wraps a word at a time beside its name. The scrolled content keeps clear of the overlay scroll
    bar.
  - **A test that fails instead of crashing:** two UI tests sampled pixels of the key where it used to be; off screen,
    the unchecked read crashed the test host with an access violation. A point outside the rendered frame is now
    refused with the control named, the high-contrast test samples the summary card, and the key's test scrolls to it.
  - **Planned:** IPv6 capture, found missing while choosing this slice, is written into open work item 3.

- **Revision 170 — one name, one contract:** the persisted overview's contract is now `overview-index-v1`
  (`contracts/overview-index-v1.md`). Revision 163 had named it `overview-v1`, the name the JSON bundle of
  `icat overview --json` has carried since revision 87, so a reader of the bundle was sent to a binary file's contract.
  The bundle keeps its name; the file names neither, so no session changes.

- **Revision 169 — every §12 query gate met at 10M observations on the reference machine's 16 threads:**
  - **Side by side:** a brushed count and a focused timeline count each segment in a worker of its own, into dense
    tallies summed after (`SegmentPasses`); a count is the same sum in any order, and a failure surfaces unwrapped.
  - **Bindings kept with the reader:** each row's owner and channel binding, packed four bytes each, is derived once
    per segment and derivation and kept by its reader, charged as columns are and given back only after them
    (`SegmentReaderV1.DerivedRows`, `SegmentBindings`).
  - **Fewer columns read:** binding an owner reads a lifecycle record's five identity columns only in a segment holding
    one, and binding a channel reads a row's position only for an end a cut divides.
  - **Evidence pages:** a segment joins the merge only once its earliest reading could come next, so a first page
    reads the first chunk's keys, and a later page skips every segment that ends before its cursor.
  - **Measured** (`bench/results/scale-gates-*`, `DOTNET_PROCESSOR_COUNT=16`): at 10M rows, warm p95, a brushed
    ranking 197 ms (was 1,226, budget 250), a group's 40 lanes 20 ms (223), its timeline at 2,000 columns 13 ms (238),
    first evidence pages 35 and 32 ms (144 and 193). The working set ends at 1.2 GB, down from 2.3 GB; the retained
    heap is 284 MiB.
  - **Tested:** over random captures published in chunks with late records, a brushed count, a group's lanes, a
    process's directions, a channel's ends and every evidence page equal a count of every row; packed bindings read
    back as they were; derived rows outlast a trim of the columns. Six mutations were each caught, ignoring cuts
    by the relation tests.

- **Revision 168 — §12's query gates measured at 1M and 10M observations:**
  - **The benchmark:** the opt-in `ScaleGateTests` (`bench/README.md`) builds finished synthetic sessions of 1M and 10M
    records on disk in chunks, 400 processes in 10 executables, and reuses them between runs. It measures, cold and
    then warm, the reopen until usable, the 2,000-column timeline at L0 and at a 40-process group, the bounded graph
    and top-100 ranking over the whole session and within brushes, and the first evidence page of a process and a
    channel, through the store the window shares. Result: `bench/results/scale-gates-*`.
  - **Met at 10M, warm p95:** reopen 8 ms with no segment opened (budget 3 s), L0 timeline 5 ms (100), whole-session
    ranking 1 ms (250), first evidence page of a process 144 ms (150).
  - **Missed at 10M:** a brushed ranking 1,226 ms (250; 272 ms already at 1M); a group's timeline 223 ms with its 40
    lanes and 238 ms at 2,000 columns (100); the first evidence page of a channel 193 ms (150). What misses counts
    rows by owner or channel, which tiles holding per-owner and per-channel counts would answer whole.
  - **Also found:** §12's 2,000-column × 40-lane query is refused by the 20,000-cell lane bound, which the window never
    meets at its 256 columns. At 10M rows the retained heap is 290 MiB, 253 MiB of it the reader cache at its budget,
    but the working set peaks near 2.3 GB from transient allocation.

- **Revision 167 — process and thread lifecycle wear a family of their own (§6.6, theme 1.3.0):**
  - **The finding:** every capture collects process lifecycle, and it was drawn in the unknown grey and keyed
    "Unknown", which §6.6 reserves for what is not supported.
  - **The family:** **Lifecycle**, glyph ▼, a neutral slate in each mode, searched rather than picked. Among blue-grey,
    low-chroma candidates inside the lightness band the mode's other fills span, it takes the one with the largest
    worst margin over every measured threshold. It sits between RPC and ALPC in palette order, where the worst margin
    across the four modes is largest. High-contrast light needed a darker navy slate to stand apart from both the
    unknown grey and Legacy IPC's steel. The regenerated `theme/` report meets every threshold; the tightest new margin
    is light mode's greyscale step from RPC, 6.9 against 6.
  - **The test the plan asked for:** every mechanism a Windows source can collect, including those a profile omits,
    maps to a family other than unknown. Removing the lifecycle mapping fails it.
  - **Found by rendering a real session in all four modes:** the legend keyed "Unknown" for every session, because an
    empty timeline column carries the unknown mechanism as a placeholder and draws nothing. The legend now keys only
    columns and lanes that draw records, including a lane's mechanism that never dominates a column.

- **Revision 166 — the ranked table counts what each process did:**
  - **The finding:** L0 and L1 ranked by paired-TCP records only. A real capture's traffic mostly leaves the machine,
    so its groups and processes ranked as zeros, search listed "0 records", and L1's lanes stood in PID order.
  - **Own records** (`process-activity-v1`, `contracts/entities-v1.md` §4a): every record whose owner binds to an
    instance and that the evidence policy admits, per mechanism. A lifecycle record is its instance's under every
    policy; a later instance's other records count only when candidates are admitted, as their binding says. Every
    record is counted once, so a group's total is its members' and the machine's its groups'.
  - **Derived like the relations:** counted with them, extended from one live generation to the next when every PID's
    counted readings bind alike under the new instances (else counted in full), and kept in the derivation checkpoint.
    Its format 1.1 adds the counts and their rule. A 1.0 checkpoint still opens, counting from the segments, and a
    writer or `icat checkpoint` replaces it.
  - **In the window:** the machine and group rungs count own records, their dominant mechanism is what the count counts,
    and the summary says so, with how many rows no process holds. A brush ranks by the same counts inside the interval.
    L1's lanes sit in the table's order. The inspector leads with a selection's own records, then what its
    relationships carry.
  - **Search:** a name that is the query plus an extension is exact, so "chrome" finds `chrome.exe` before
    `chrome-native-host.exe`, and hits are ordered and labelled by own records.
  - **CLI:** `icat overview` lists the five busiest processes and the rows no process holds.
  - **Tested:** counts equal a recount from each row's owner binding under every policy; extension equals a full count
    chunk by chunk; a checkpoint round-trips, extends as written, and refuses another count rule; a 1.0 checkpoint is
    counted around and replaced; ranking, brushed counts, search, lane order and the summary. Eight deliberate
    mutations were each caught. On the real sparse ETL session 851 of 852 rows belong to a process, and its
    republished checkpoint gives an identical overview.

- **Revision 165 — fixes from a live test of the whole product:**
  - **Tested live:** the CLI end to end on real ETW and real data:
    - `record`, including evidence-only, and `follow`;
    - `import` of a real ETL;
    - `session`, `processes`, `metric`, `overview`, `timeline`, `evidence`, `channels` and `raw`;
    - `export`, and both packages, redacted and original;
    - `compact`, `rederive`, `checkpoint`, `recover` and `staging`;
    - `measure tcp`, and the broker qualification suite
      (`bench/results/broker-qualification-20260926T234418Z`, all five scenarios).

    Then the real Desktop, driven through UI Automation:
    - it opened a saved Explore session;
    - searched, descended to a group, jumped to evidence and hovered the timeline;
    - recorded a live Explore capture through the broker, stopped it and saved it, with its checkpoint and overview.

    No ETW session, broker or window was left behind.
  - **Fixed:**
    - **CLI text on a console with a legacy code page.** The ellipsis, arrows, middle dots and group separators
      printed as full stops, question marks or nothing, and piped output was in the console's code page. A console is
      now written in UTF-16, changing no code page, and redirected output in UTF-8.
    - **`icat raw`** printed its record's identity as a C# record dump. It now prints the locator an evidence page
      shows.
    - **Superseded manifests kept after a checkpoint.** Publishing a checkpoint held its evidence lease across its
      commit, so the writer could not remove manifests no pointer names (store-v1 §9), and a finished session kept
      them. The lease now ends before the commit.
    - **Process lanes all marked as gaps.** L1's lanes judged an empty interval by the process's own records, so
      every quiet interval of every process was coverage-unknown. The live test showed 82 `chrome.exe` lanes hatched
      end to end. A lane is now judged by what the capture collected there, as a mechanism lane is: quiet where the
      capture covered it, a gap where it lost records.
    - **A live capture looked broken.** Until its coverage ledger arrives at the stop, the status read "64
      coverage-unknown intervals" in the caution ink. It now says coverage is unknown until a ledger is published,
      plainly, and a single limited interval is named as one.
    - **Legend order.** The legend listed mechanisms in the order the first bucket showed them. It now follows the
      lanes.
  - **Found, next:**
    - **Revision 166.** L0 and L1 rank groups and processes by paired-TCP records only. A real capture, whose traffic
      mostly leaves the machine, ranks a table of zeros; search results say "0 records"; and L1's lanes run in PID order.
    - **§6.6 violation.** Process lifecycle, a supported mechanism, is drawn in the unknown grey and keyed "Unknown",
      which §6.6 forbids.
  - **Tests:** +4, one each for the superseded manifests, a quiet process lane, the no-ledger summary and plural, and
    the legend order. The superseded-manifest test fails without its fix.
- **Revision 164 — checkpoints qualified on real ETW and in the window:**
  - **Changed:**
    - `first-feedback` report schema v5 records what the viewer publishes when the follow finishes: the checkpoint's and
      the overview's bytes. It also records what a fresh store opening the saved session reads before its first
      overview.
    - The §6.8 latency benchmark measures each synthetic session twice: derived from its segments, and reopened from
      the checkpoint and overview its writer published.
    - `SessionCheckpoints.NamedBy` states which indexes a generation names.
  - **Measured:**
    - Real ETW, a bounded 10-minute Explore capture (`bench/results/first-feedback-20260926T232454Z-10min-bounded`):
      - it passed and met every budget, with 128,467 records and event-to-visible p95 796 ms;
      - derivation p95 53 ms and projection p95 23 ms, against 147 and 67 ms in revision 129's run;
      - the saved session names a 1.2 MiB checkpoint and a 12 KiB overview;
      - a fresh store projects it in 8 ms, opening no segment.
    - The window (`bench/results/interaction-latency-20260926T233525Z`, synthetic):

      | Session | Overview, derived | Overview, from its checkpoint | Opened to laid out, from its checkpoint |
      |---|---|---|---|
      | 100,000 rows | 242 ms | 12 ms | 29 ms |
      | 1,000,000 rows | 539 ms | 2 ms | 27 ms |

      Level changes, the brush and search stay within §6.8's second at both sizes. They read the segments they count,
      as before.
  - **Tests:** none added; the opt-in benchmark gains its two reopened sessions.
- **Revision 163 — a finished session's first view opens no segment (S1, S4's top level):**
  - **Found:** with revision 162's checkpoint, most of what still grew with a session was the tiles: at 10M rows,
    400 ms of a 0.62 s reopen went to reading every segment's session-time and mechanism columns, and 160 ms to
    opening the segments.
  - **Changed:**
    - A **persisted overview** (`contracts/overview-index-v1.md`) holds what a finished session's first view draws: its row
      counts, its extent, the 64 buckets' counts per mechanism and the minimap's columns. It is S4's top level.
    - It is published beside the derivation checkpoint, in the same index generation, by every writer that publishes
      one. `icat checkpoint` adds it to a session revision 162 checkpointed.
    - When both cover exactly the segments the manifest names, the projection opens no segment. The derivations are
      the checkpoint's as they stand, compared with the manifest rather than with segment readers. A checkpoint
      without an overview still gives the derivations, and the timeline is counted from the segments.
    - An overview this build would place otherwise (bounds, column count, minimap span) or whose counts do not add
      up is not used. One that cannot be read is counted around, with a caveat.
    - The bounded index-file fields both formats use moved to Storage (`IndexFileWriter`, `IndexFileReader`).
  - **Found while testing:** a damaged column index escaped the overview reader as an overflow instead of a refusal.
    Reads now compare indexes unsigned, and anything the counting rules refuse is refused as unreadable.
  - **Measured** (Release, synthetic, 100 processes and 50 relations):
    - reopen at 4M rows: 0.48–0.50 → 0.17–0.18 s;
    - reopen at 10M rows: 0.62–0.65 → 0.17 s, working set 223 → 37 MiB;
    - no segment opened before the first view.
    The real sparse ETL session's overview is identical under every evidence policy.
  - **Tests:** +3, and one strengthened:
    - a reopen opens no segment;
    - an unreadable overview is counted around;
    - a checkpoint without an overview;
    - the overview format's round trip, truncation, damage, session and bounds.
    Two mutations are each caught.
- **Revision 162 — a finished session reopens from its derivation checkpoint (S1):**
  - **Found:** at 4M rows the first derivation was 80% of a reopen: 225 ms of process instances and 615 ms of
    relations, against 99 ms of tiles. At 10M rows the reopen took 2.2–2.4 s and 787 MiB, growing with the session.
  - **Changed:**
    - A **derivation checkpoint** (`contracts/derivation-checkpoint-v1.md`) holds the state the process instances and
      transport relations keep, as derived from named segments. A reopen builds both from it and reads no row to do
      so. The overview is exactly a full derivation's.
    - It is an `Index` published as a generation of its own (`CommitIndex`, store-v1 §5). The writer that finished
      the session publishes it:
      - the Desktop's live follow, and its finish of an interrupted one;
      - `icat import`, `follow`, `capture`, `record`, `compact` and `rederive`;
      - `icat checkpoint`, for an older session. It refuses while a follow holds the session's ticket.
    - A checkpoint only saves time. One that is missing or stale is derived around. One that cannot be read is
      derived around with a caveat saying why. An additive generation does not carry it, so a damaged one costs at
      most a rollback to the generation before. A retention or compaction that releases a segment releases it too.
      A viewer reads an index before hashing it, because the reader hashes it against its recorded digest first.
    - Its bytes are a function of the records it covers. An ambiguous incarnation's first-seen holder, and the first
      PID of one naming two, depend on reading order and are never read, so neither is written.
  - **Measured** (Release, synthetic, 100 processes and 50 relations; checkpoint 20–23 KiB):
    - reopen at 4M rows: 1.10 → 0.48–0.50 s, working set 340 → 114 MiB;
    - reopen at 10M rows: 2.2–2.4 → 0.62–0.65 s, working set 787 → 223 MiB.
    What remains grows with the session: tiles, 400 ms at 10M rows, and segment opens, 160 ms.
  - **Tests:** +12:
    - read-back and extension property tests over random captures, which compare bytes as well as bindings;
    - refusal of truncated, changed, foreign and other-version checkpoints;
    - a repeated field after a checkpoint;
    - index publication and release in the store;
    - reopen, an unreadable checkpoint and stale writers in Application;
    - the Desktop's finished follow.
    Four mutations are each caught.
- **Revision 161 — the reader cache charges what a reader holds (column-granular step 3):**
  - **Found:** tiles are kept with their reader, and the cache charged a reader its file's length. From about 1.5M
    rows some readers went uncached, and every projection reopened them and rebuilt their tiles. A live generation took
    7 ms at 1M rows and 64 ms at 3M.
  - **Changed:**
    - A cached reader is charged the columns and chunk it holds plus its decoded dictionaries.
    - When a lease ends over budget, cached readers give back every column but session time and mechanism, one at a
      time until the charge fits. None is evicted.
    - A released column is read, and checked, again when next asked for.
  - **Measured** (Release, synthetic):
    - a live generation at 3M rows: 64 → 9 ms;
    - a 4M-row session keeps all 16 readers cached where it kept 6.
    Its reopen is unchanged at about 1.3 s, all derivation. The heap is larger, 244 against 110 MiB after a
    collection, because the budget now bounds bytes really held.
  - **Tests:** +1, a trim that keeps every reader and exactly the two columns where it trims. Two cache tests were
    restated for the new charge. Three mutations are each caught.
- **Revision 160 — `Ctrl`+click builds a multi-selection, and `Enter` makes it a filter (§6.7):**
  - **Changed:**
    - `Ctrl`+click on a graph node, or on a ranked row that stands for processes, adds them to a set or removes them.
      A single selection already standing becomes its first member, and a plain click, a clear or a navigation lets
      it go.
    - The set is ringed in the graph, named in the inspector and highlighted in the timeline.
    - `Enter`, or the inspector's **Show their records**, opens the evidence rung scoped to exactly the chosen
      processes. It uses a visible filter keyed by their instance identities (`ProcessSetFilter`).
    - Keyboard: `Ctrl`+`Up`/`Down` move the ranked table's focus without selecting (Avalonia's own list behaviour), and
      `Ctrl`+`Space` toggles the focused row, or the graph's keyboard node.
    - A row's context menu toggles it.
  - **Found:** a handler for `Ctrl`+`Up`/`Down` duplicated the list's built-in behaviour; a mutation removing it
    changed nothing, so it was removed.
  - **Tests:** +3: the set filter's scope and parsing, the table path with `Enter`, and the graph path with a plain
    click replacing the set. Five mutations are each caught.
  - **Open:** channel sets, a lane view of an arbitrary set, and marking the set's rows in the ranked table.
- **Revision 159 — a selection is highlighted in the timeline (§6.4):**
  - **Changed:**
    - Selecting any of these counts that entity's own records on the columns the timeline draws, with the rung
      focus's exact query:
      - a process;
      - an executable group;
      - an aggregate node;
      - a channel chosen among a process's rows.
    - Each bar's share is outlined in the accent token (§6.6), and each mechanism lane marks its own mechanism's share.
      It adds no fill and never exceeds the bar.
    - A selection that is the rung's own focus adds nothing, and only a descent or Focus filters.
    - The caption names the highlight, and the hover card gives its count in the hovered bar.
    - A live publication carries the highlight until the new generation's count replaces it.
    - A focused count now also returns its per-mechanism lanes.
  - **Found and changed:** the first encoding, a translucent accent fill, was nearly invisible on TCP's blue, and §6.6
    forbids fill for selection. It is an outline now.
  - **Fixed flake:** "I15: a long recording coalesces its small publications as it records" waited a fixed 400 ms
    between bursts, so under full-suite load two bursts could share a publication. Each burst now waits until the one
    before it is published. Three full-suite runs were clean.
  - **Plan:** §6.4 no longer says the original evidence package is open.
  - **Tests:** +4, window-level:
    - exact counts, a changed drawing, the card, clearing;
    - a group, the rung's own focus, a chosen channel;
    - per-lane shares with mixed mechanisms in one bucket;
    - the stand-in across a publication.
    Five mutations are each caught. Two survived the first draft of the tests: its data could not tell a lane's share
    from the total, and a descent cleared the selection before the focus rule was tested.
- **Revision 158 — the overview and minimap come from time tiles, S4's first level:**
  - **Changed:**
    - Each segment's timed records are counted once per reader into aligned decimal tiles. A tile holds its counts per
      mechanism, its row run and its earliest and latest reading.
    - A count takes a tile whole where its records fall in one column, and reads the rows of a tile a column boundary
      crosses (§10.3). A segment whose session times go backwards is read row by row.
    - The overview's extent and buckets, and zoomed detail without a focus, come from tiles.
    - The minimap's columns are aligned to the narrowest 1–2–5 width that covers the extent in at most 2,000 columns.
      Each is then a union of whole tiles, and the first and last are clipped to the extent. The minimap reads no row.
  - **Fixed:** a record exactly on a bucket boundary that the column count does not divide evenly was counted in the
    bucket before the one whose stated interval holds it. Columns now invert §10.3's boundaries exactly.
    - The real sparse session's buckets are unchanged.
    - A synthetic session with a record at every tick moves one record per such boundary.
    - Everything else is digest-identical to revision 157, relations and bindings included.
  - **Measured** at 1M rows, Release:
    - the same generation again: 32–33 → 1 ms;
    - a live generation: 37–38 → 8 ms;
    - a generation derived in full: 134–138 → 103–105 ms;
    - zoomed detail over a sixth of the extent: 7.2–7.7 → 0.9 ms.
  - **Tests:** +8.
    - Tiles count what rows count, over random intervals and column counts.
    - Minimap columns are whole tiles and read no row.
    - Disordered times fall back to rows.
    - Every column holds what it counts (the fixed defect's test failed before the fix).
    - Five mutations of the tiles are each caught.
  - **Open:** tiles are not persisted, so a reopen still reads every time column (S1), and a focused count still reads
    its rows.
- **Revision 157 — a live generation's derivation extends the previous generation's (IC-015):**
  - **Found:** after revision 156, a new generation at 1M rows still spent about 110 of its 142 ms re-deriving
    process instances and relations from every segment.
  - **Changed:**
    - A process index keeps what it read (lifecycle records, process fields by record, each PID's earliest record,
      and the files read). A later generation extends a copy of that with only the segments it adds.
    - The relation index extends a copy of every end's incarnations with the added records.
    - An extension declines, and the generation is derived in full, in three cases:
      - a segment it read is gone, after a compaction or a retention;
      - an added connect, accept or disconnect falls before or among an end's records;
      - the new process index binds an earlier record otherwise. Both indexes are compared at every lifetime boundary
        inside each holder's readings.
    - A rundown that re-identifies a process extends, its holders moved to the new identity.
    - The shared cache extends from the latest earlier generation it keeps.
    - A reader now carries the file its generation publishes it as. segment-v1 now says why: the header id repeats
      across generations.
    - The overview's row passes are compiled optimized at once. Promotion from the first JIT tier kept being delayed,
      and in some runs they ran at a quarter of their speed.
  - **Exact:**
    - The full derivation is digest-identical to revision 156's, under all four policies, on the sparse ETL session
      and a synthetic one.
    - The sparse session, published again in 12 and in 40 chunks with late records, extended at every chunk and
      equalled a full derivation each time.
  - **Measured** (Release, 2,000-row chunks), a new generation's projection:
    - 1M rows: 146–155 → 36–38 ms;
    - 200k rows: 38–40 → 11–13 ms.
    A full derivation went from 141 to 132 ms.
  - **Tests:** +5.
    - A seeded property test extends random captures chunk by chunk and compares every generation with a full
      derivation. The captures include late records, reused and undecided ends, PID reuse and late start keys.
    - A test that each declining case declines, and the rundown extends.
    - A store-level test of eight live generations through the cache, with a compaction.
    - Six mutations are each caught. A seventh, of an unreachable refusal, is not.
- **Revision 156 — the overview counts relation rows from the relations, not row by row:**
  - **Found:** warm at 1M rows, one pass over the time and mechanism columns cost 3 ms. Locating every row in its
    connection incarnation cost 76 of the projection's 91 ms. The lookup fed only the unresolved-TCP count, the
    displayed edges' records and their untimed share, and a graph-eligible timeline nothing drew.
  - **Changed:**
    - As it derives, the relation index now counts each incarnation's records without session time, and each related
      mechanism's records with no end (`relations-v1` §5).
    - `RecordsWithoutAdmittedPeer` answers the unresolved count per policy from incarnations alone, and a relation
      carries `RecordsWithoutSessionTime`.
    - The overview reads those, and the graph-eligible timeline is retired. The run-time check its construction made
      (that the edges hold exactly their channels' rows) is now a test.
  - **Exact:** every other overview field, the relations and every per-row binding are digest-identical to
    revision 155's. That holds under all four policies, on the sparse ETL session and on a synthetic one with untimed
    rows. Only two caveats changed: the retired timeline's caveat is gone, and one now says the untimed rows are absent
    from the timeline.
  - **Measured** (Release, steady state):
    - 1M rows: a new generation's projection went from 208–214 to 142–145 ms, and the same generation's again from
      102–105 to 31 ms.
    - 200k rows: 40–43 to 28–29 ms, and 19–21 to 7 ms.
  - **Tests:** +3, a seeded property test over random captures with reused, paired, one-sided and undecided ends,
    candidate owners, endless and untimed records, and random segmentation. Four mutations of the new counts each
    fail all three seeds.
  - **Plan:** M5 no longer lists the original evidence package as work to do; revision 154 built it.
- **Revision 155 — the window's aggregate queries allocate nothing per row (R11):**
  - **Found:** R11 was tested for paint only. Measured warm, per row, a projection allocated 20 bytes, a group's
    focused count 12 and an interval count 8. Every call rebuilt a segment-length array of each row's process or
    channel binding. The focused count also kept a dictionary per column and chose each bucket's mechanism with LINQ:
    about 3,200 dictionaries per call for a group of 50 owners.
  - **Changed:**
    - The bindings (`BindingsOf`, `ChannelsOf`, `OwnersOf`, `EndsOf`) fill buffers rented for one segment's pass and
      returned after it. Caching them would have kept arrays the size of the session resident, against S2. A rented
      buffer can be longer than its segment, so every loop bounds on the row count.
    - A column's tally is a flat array of counts per defined mechanism, with the same dominant-mechanism rule: the most
      rows, ties to the lowest code. An undefined mechanism code is now refused by these counts, as the row reader
      refuses it.
  - **Exact:** every answer (projection, detail, focused, interval) is digest-identical to revision 154's, on the real
    sparse session and on a synthetic one.
  - **Measured** warm at 1M rows, two runs each: timeline detail 35 → 21 ms, a group's focused count 145 → 100 ms,
    projection about 118 → 105 ms.
  - **Tests:** +1: the four queries measured warm at 10,000 and 30,000 rows fail on any allocation that grows with
    the rows. Rented buffers made to allocate again brought back exactly 20, 12 and 8 bytes per row, and the test
    named each query.
- **Revision 154 — the original evidence package, §11.3's third preset:**
  - **Gap:** only the metadata-only report and the redacted session existed. `icat package --original` said "not
    implemented yet".
  - **Package** ([`original-evidence-package-v1`](../contracts/original-evidence-package-v1.md)): an exact copy of
    the current generation. It holds every file the generation names, byte for byte, with its manifest, a pointer and
    the evidence lease guard, and reopens as the same session, generation and digest. It holds nothing else: no
    earlier generation, no superseded manifest, no stray file, no ETL.
  - **Made safely:**
    - The source generation is leased while it is copied, and each file is hashed as it is copied. A file that
      changed in place after the session was opened is refused before the rest is copied.
    - The package is built beside its destination, reopened and hashed by a fresh store, and only then renamed into
      place. A refusal or cancel leaves nothing behind.
  - **Stated first:** the generation, records and every file; the unredacted contents; the host identity; whether the
    session is itself a redacted package; the warning.
    - `icat package --original [--check]` prints this.
    - The Desktop's "Share original session…" asks first. Its confirmation sizes itself to what it states, starts on
      Cancel, and names its action "Save unredacted copy…".
    - One package is made at a time, and its own button cancels it.
  - **Real data:** the killed real capture from revision 153 (27 journal chunks) was packaged as 34 files (1.9 MB).
    `icat session` reopens it as generation 28.
  - **Also:** the §6.2 note on minimum drawn width and snapping now says they wait for the density regime, and why.
    Package dialogs state sizes as the saved-session list does ("3 KB", "4.2 MB").
  - **Tests:** +5. Four are package tests:
    - a byte-for-byte reopen holding nothing else, with a lease and an overview;
    - a file changed in place is refused at copy;
    - unsafe destinations are refused, and a cancel leaves nothing;
    - a preview that writes nothing.

    The fifth is a window test covering the button's states, the disclosure, and a rendered prompt with both actions
    whole and Cancel focused. It also saves and reopens a copy. A digest check skipped at copy, and a missing guard,
    each failed a test.
- **Revision 153 — a crashed `icat capture` can be finished by naming its session (§3.1 step 6, live-follow-v1 §5):**
  - **Gap:** only the Desktop's runner wrote a follow ticket. A command-line capture that ended early left no note of
    its evidence, and `icat capture` prints the evidence directory only when it finishes, so a user could not even
    run `icat follow <evidence> <session>` by hand.
  - **Changed:**
    - `icat capture` holds a ticket beside the session and renews it with every owner-lease renewal. It completes the
      ticket once the capture closed with every published chunk followed, or with none published. A ticket that
      cannot be written costs only this shortcut, and the command says how to finish without it.
    - `icat follow <session>` finishes the capture beside a session from its ticket (`LiveFollowTicket.FindInterruptedFor`),
      wherever the session is. It warns when the capture may still be recording, derives what was published, and keeps
      the ticket for a later run. It removes the ticket once the session holds everything.
  - **Qualified on real ETW** against the qualification broker (never the production root):
    - `icat capture` was killed after following 8 chunks.
    - `icat follow` at once derived the 15 chunks published so far and kept the ticket, with exit code 1.
    - Once the lease settled, it finished all 27 chunks (3,276 records) with their finality and removed the ticket.
    - No ETW session was left, and `icat session` reads the result as generation 28.
  - **Tests:** +1: naming a session finds its ticket only once no follow holds it, and never a copy beside another
    session. The command itself was checked end to end on the scripted-host fixture: finishing, still stopping,
    `--json`, and a second run once nothing is left.
- **Revision 152 — a viewer opens a session without hashing it first, and states a fallback (S1, S7, store-v1 §6):**
  - **Found:** a fresh open hashed every file a generation names. At a million rows (309 MiB, 140 MiB of it journal)
    that took about 420 ms, so about 3 s at T1's 2 GiB and 30 s at T2, where S1 wants time-to-interactive independent
    of size. Separately, the Desktop never read a store's rollback: a saved session whose newest generation did not
    verify opened on its last-known-good without a word (S7).
  - **Changed (store):**
    - `SessionStore.OpenForViewing` checks the pointer, the manifest's digest, and each dependency's presence and
      recorded length from one listing.
    - Segments, dictionaries and journals, whose readers check what they read, are hashed later by `VerifyContents`,
      under a lease. Every other kind is small, carries no checksum of its own, and is hashed at open as before.
    - A file whose digest disagrees is reported and forgotten, so the next lease fails its generation over to the
      last-known-good. `RollbackReason` says why, and every lease keeps it current.
    - Hashing reads 1 MiB at a time, which halved the opens that still hash: the command line's and a writer's.
  - **Changed (Desktop):**
    - The registry opens stores for viewing, and a saved session's files are hashed after its first view.
    - If a file changed, the window opens the fallback in its place and says so. A first view that itself meets a
      changed file hashes the generation at once and opens the fallback directly.
    - Every open onto a last-known-good generation says which one is shown and why.
    - The unfinished-capture card's five-second look lists the evidence instead of hashing it.
  - **Measured** at 1M rows: open 420 ms → 1 ms, hashing afterwards 0.2 s, and the hashed open 420 → 210 ms. Opening
    the session in the window fell from 1,067 to 729 ms, and at 100k rows from 423 to 367 ms
    (`bench/results/interaction-latency-20260926T190828Z`).
  - **Tests:** +6.
    - Store: a viewer's open neither opens nor hashes a segment, and neither does a lease; hashing afterwards finds a
      change, and the next lease falls back with the reason. A file with no checksum of its own is hashed at open. A
      missing or resized file is still refused at open. Each file is hashed once, and a hashed open leaves nothing.
    - Window: damage the first view cannot see falls back after the hashing, and damage it reads falls back at once.
      Both state the fallback.

    Eight mutations each failed a test: four in the store (hash at open, defer every kind, keep a mismatch, hash
    what was listed) and four in the window (hash at open, fall back silently, no hashing afterwards, no retry).
- **Revision 151 — a published segment reads a column when it is first asked for (§20.1, §12, segment-v1 §9):**
  - **Changed:** a published minor-1 segment opens by its header, its directories and its time column. Every other
    column, and the variable chunk, is read from the file when a caller first asks for it, checked, and held. Each
    read opens the file, checks the length its generation recorded, reads and closes it. A reader therefore holds no
    handle between reads, and the caller's evidence lease keeps the file in place. A minor-0 segment is still read
    whole and checked by its trailer. Readers report `ResidentBytes`, what they hold.
  - **Also:** the compaction planner counts a small unit's rows from each segment's checksummed header instead of
    opening the segment.
  - **Measured** at 1M rows (three alternating runs of each build):
    - The Desktop's queries read about 43% of a segment's bytes: cached readers hold 73 MiB of 169 MiB of files.
    - With a 64 MiB budget, where every query re-reads three segments of four: warm projection about 243 → 141 ms,
      timeline detail 133 → 42 ms, a group's focused count 257 → 172 ms.
    - At the 256 MiB default a million rows stay cached and warm queries are unchanged, but opening the session in
      the window fell from 1,209 to 1,067 ms (`bench/results/interaction-latency-20260926T184644Z`).
  - **Still admitted by file length:** the cache charges a reader its file's length, the most it can come to hold, so
    the bound holds however many columns later queries read. Charging what a reader holds would keep about twice the
    rows in the same budget (open work item 1, step 3).
  - **Tests:** +5 in `SegmentV1Tests`, on files published to disk:
    - a segment opens holding only its time column, and each column's first read adds exactly that column. Every row
      reads as it does from the whole file in memory, and a reader that read every column holds exactly the columns'
      bytes;
    - damage to a column is refused when the column is read, every time, while other columns are still served;
    - a minor-0 file is read whole, and its trailer refuses damage at open;
    - a file whose length changed is refused, by a reader already open and by a fresh open;
    - threads sharing a reader never read a column before it passes its checksum.

    Five mutations each failed a test: no column check, no length check, a minor-0 file read in pieces, no count of
    what a reader holds, and a column published before it is checked.
- **Revision 150 — CRC-32C at the processor's speed, and two intermittent failures fixed (§18.1, §20.1, §3.1):**
  - **Found:** the byte-table CRC-32C ran at 0.51 GiB/s. It checks every journal record, every segment column and
    null bitmap, and since revision 149 the directories. A segment's columns are checked on each first read, so
    checking a 42 MiB segment in full took about 80 ms, and every cache miss paid it again.
  - **Changed:** eight bytes at a time through `BitOperations.Crc32C`, which uses the SSE4.2 or Arm64 CRC-32C
    instruction where there is one: 9.4 GiB/s over 64 MiB. Every checksum is unchanged, with the same polynomial and
    bit order. The published vectors, the committed journal fixture and a new test against the bit-level definition
    (every length to 96 bytes, every alignment, and a split) all agree.
  - **Measured** at 1M rows with a 64 MiB budget, where every query re-reads and re-checks three segments of four
    (medians of four alternating runs of each build): warm projection 336 → 239 ms, timeline detail 167 → 136 ms, a
    group's focused count 355 → 254 ms. At the 256 MiB default a million rows stay cached, and the window-level
    benchmark is unchanged within noise: open 1,070 → 1,056 ms, group level change 336 → 320 ms
    (`bench/results/interaction-latency-20260926T180747Z`).
  - **Two intermittent failures, each found by running the full suite repeatedly:**
    - **R11's paint test** failed in about 3 of 25 runs. One batch allocated 3.6–5.7 KB with no collection during
      it, and each of three batches measured right after allocated nothing. A batch over the tolerance is now
      measured again, up to three times, and the least is taken. A pen made per frame still fails it, on every rung
      (2,456 B a frame).
    - **§3.1's still-stopping test** failed once in about 12 runs. Under load, the recording fixture could publish
      every record and the finality in one generation, so there was no generation to rewind to. The fixture's second
      burst now waits for the first publication instead of sleeping 400 ms.
  - **A real race, found while looking:** the unfinished-capture card's five-second recheck could begin before the
    user forgot a capture and end after it. It then showed the forgotten card again until the next recheck. Only
    the latest look's answer is shown now.
  - **Tests:** +2: the CRC reference, and a window test that holds a recheck's answer back until after a forget. It
    fails without the fix.
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
   - A zoom still builds a segment's tiles from its rows when first drawn. Persisting them is the pyramid's next level.
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
   expose no UI Automation Invoke pattern: the keyboard reaches them, but voice control may not, so check it there.
   A timeline status that changes with the view is read when asked for, not announced; check whether a zoom needs a
   live announcement. Then add pin/collapse/search for lanes as the observed lane
   count requires. L4's operation lanes, with duration bars and byte projections where a derivation supports them,
   wait on item 3's operations; L5 keeps its marks.
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
     the `content-fixture` profile; revision 236 shows it in the bounded hex and text viewer (§3.7). Next: one validated
     content-capable source or import path. Later: a follower that mirrors content, so a broker capture could keep it;
     releasing content alone; a fixture decoder (§11.2's `DecodedFields`); and measuring whether application providers'
     headers name their owners (ADR-030).
   - **IPv6 beyond loopback.** Revisions 172–174 store, relate, redact, show and capture IPv6 endpoints, measured on
     `::1` (FX-TCP-002, FX-UDP-002; ADR-029). Still unmeasured: two-host IPv6 traffic, link-local addresses on several
     interfaces (a record carries no zone index, so two interfaces' equal addresses are one address to a relation), and
     IPv6 multicast.
   - **IC-016a** waits on a retention that releases observation segments: today only a journal prefix is released, and
     every derived segment, with every identity, stays.
4. §11.3's redacted packages above 10,000,000 rows: an interval-scoped package, which must keep the lifecycle records
   of the processes it holds so they keep their names. Revision 217 raised the bound from 1,000,000 by compacting the
   field join and verification, measured at 10M rows with a field each under a 1 GiB heap. All three presets exist since
   revision 154.
5. Interaction follow-ups with no dependents:
   - Per second over a whole session: revision 209 states rates only over a brushed or zoomed interval, because a
     session states no interval of its own and the span between its first and last record is not one. Session time 0
     is the capture's own epoch reading, but its stop is recorded only as wall-clock provenance
     (`capture-finalization-v1`), and the coverage ledger's first and last readings are delivered records, not the
     recording's bounds. A whole-session rate needs the capture to record its stop reading in the source clock, and a
     rule for records before the epoch or without a time.
   - Qualify the **Other processes** remainder on real data when a naturally eligible capture exists. It is a budget
     fallback, covered synthetically; the dense capture never needs it.
   - Pins that survive reopening, once §26.3's workspace persistence exists.
   - §6.7's table is complete since revision 160's multi-selection. What it leaves open:
     - a set of channels, which a timeline focus cannot name;
     - a lane view of an arbitrary set, which would need the graph to expand several groups at once;
     - (done in revision 205: the set's rows are marked in the ranked table itself.)
     Indexed/progressive search belongs to the later M4 scale gate.
   - §6.2's minimum drawn width (5 px) and pointer snapping belong with the density regime, where a column is one
     device pixel. Revision 154 recorded why they wait for it: today every bar's column, at least 5 px, is its pointer
     target, and snapping would take an empty neighbouring interval away from the pointer.
   - R11 beyond paint and aggregation: since revision 155 a test holds the window's four aggregate queries to no
     allocation per row. The admission and decode loops keep IC-019's Windows allocation measurements and have no
     test that runs here. A pan still formats and lays out the ticks it draws, which §19.4 allows.
   - Theme modes (§6.1, §26.2, §26.3): light and dark follow the operating system since revision 138, and its
     high-contrast setting since revision 140; since revision 146 the user can choose one, kept in `app-settings-v1`.
     Revision 147 restated menus, tool tips, scroll bars and list selection in high contrast.
     - On Windows, check each of the system's high-contrast themes, and whether Avalonia reports light or dark for
       each as expected.
   - §6.6's unmeasured encoding is drawn since revision 207 where the graph plots bytes: a relationship or node none of
     whose sends measured a size is an open cross-hatched band or disc, keyed in the legend while drawn. §6.2's heat
     cells take it when they plot a value that can be unknown. The ranked and interval tables state it in words.
   - §6.1's metric selector ranks the machine and group rungs by records, bytes sent or received (revision 189), or RPC
     calls made or served (revision 190), and a process's channels by its own bytes on each (revision 192), bytes sent and
     received and RPC errors (revision 196), RPC call and serve time (revision 199), and peers (revision 200); each count or
     sum reads per second over the ranked interval since revision 209. Still open: bytes in the persisted overview itself
     (overview-index-v1 §4 sums none), so that edges, channels and intervals carry them without a read and the graph and
     timeline can draw volume. Meanwhile revision 206 reads the interval table's bytes when it is shown.
     Each metric is listed under its basis, which stays beside the selector, since revision 201.
   - Done in revision 206: a real session's interval table reads each listed interval's bytes when shown, which revision
     197's live pass found it had none of. (The relationship table's scope, the pass's other finding, is revision 198's.)
   - Done in revision 203: TCP connection events no longer admit the source's always-zero size field as bytes.

## Verification and cautions

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
- Revision 229 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,350 tests: 1,346
  passed, 4 skipped**, zero failures; two of them hold a reopen that draws the RPC edge with no segment open, a
  minor-0 overview read as before, and damaged links refused.
- Revision 228 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,348 tests: 1,344
  passed, 4 skipped**, zero failures; three of them hold peer(P,Q), grouping by peer, participant(P), between(A,B)
  and a count of peers over linked calls. A real elevated `icat record --profile rpc-peers` session was answered by
  `icat metric` and deleted.
- Revision 227 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,345 tests: 1,341
  passed, 4 skipped**, zero failures; three of them hold the edge, its interval count, its absence without ALPC, and
  its hover. A real elevated `icat record --profile rpc-peers` session was opened in the Release Desktop and driven
  through UI Automation and posted keys: its graph, the workload's RPC rung and its calls were read and captured, then
  deleted.
- Revision 226 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,342 tests: 1,338
  passed, 4 skipped**, zero failures; two of them hold a channel's and a call's other end through the query and on
  the rung.
- Revision 225 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,340 tests: 1,336
  passed, 4 skipped**, zero failures; four of them hold the link, every unresolved reason, a server call reached twice,
  and a capture with no ALPC. A real elevated `icat record --profile rpc-peers` of FX-RPC-001 was read by
  `icat operations` and deleted.
- Revision 224 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,336 tests: 1,332
  passed, 4 skipped**, zero failures. Two elevated `--product-impact` series of seven rounds recorded 28 real captures
  into scratch, each deleted once read; none left an InterCat session (`logman`).
- Revision 223 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,336 tests: 1,332
  passed, 4 skipped**, zero failures. A real elevated `icat record --profile rpc-peers` of 15 s, with 300 RPC calls,
  admitted 9,382 ALPC rows, each with its message id, published its ledger, and left no InterCat session (`logman`);
  its scratch was deleted.
- Revision 222 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,328 tests: 1,324
  passed, 4 skipped**, zero failures; four of them hold kernel flags first, a refusal's cleanup, plans without them
  unchanged, and no manifest request for a kernel flag group. No real session was started: no profile enables them yet.
- Revision 221 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,324 tests: 1,320
  passed, 4 skipped**, zero failures, four of them taking ALPC's send and receive from a class layout to the journal
  and a replay's plan selection.
- Revision 220 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,320 tests: 1,316
  passed, 4 skipped**, zero failures, one of them reading ALPC's registered layout from this machine's TDH.
- Revision 219 changed no product code: Debug and Release both ran **1,317 tests: 1,313 passed, 4 skipped**. The session
  check ran again elevated (`bench/results/alpc-session-check-20260928T034426Z`), recording each ALPC opcode's identity;
  no InterCat session remained and the machine's system logger count returned to four.
- Revision 218 changed no product code: Debug and Release both ran **1,317 tests: 1,313 passed, 4 skipped**. Its check
  ran elevated: `InterCat.AlpcProbe --session-check`, counters in `bench/results/alpc-session-check-20260928T033708Z`,
  left no InterCat session running (`logman query -ets`), and the machine's system logger count returned to four.
- Revision 217 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,317 tests: 1,313
  passed, 4 skipped**, zero failures. Heap dumps (`dotnet-dump`) of the CLI packaging synthetic sessions at 1M, 4M and
  10M rows found the three row-sized holders. Ten million rows with a source field each packaged in 162 s under
  `DOTNET_GCHeapHardLimit` 1 GiB; the previous code, bound raised, failed there and the new code at 768 MiB. The
  scratch sessions and packages were deleted afterwards.
- Revision 216 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,315 tests: 1,311
  passed, 4 skipped**, zero failures. The Release window exported a group's 27 rows and a process's 112 records through
  its native save dialog, driven by window messages into scratch and deleted afterwards. During the work the Release
  Application tests' host was killed from outside four times (exit code -1, no stderr, no crash record), while another
  session's test runs were active on the machine. Alone it passed eight runs in a row, and the full suite passed.
- Revision 215 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,314 tests: 1,310
  passed, 4 skipped**, zero failures. In the Release window on a 20-second dense capture, Enter alone walked from the
  machine through lsass.exe, its process and an RPC channel to a call's records; Enter on a call had done nothing.
- Revision 214 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,312 tests: 1,308
  passed, 4 skipped**, zero failures. A live pass of the Release window on a 20-second dense capture (743 processes,
  6,896 RPC calls) drove search, every ranking, the row menu, graph, timeline, evidence rung and original-record window
  by keys posted to its window. Each finding was reproduced headlessly first, and each fix was confirmed live, the
  capture deleted afterwards.
- Revision 213 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,307 tests: 1,303
  passed, 4 skipped**, zero failures. A keyboard-only walk of the Release window on a 20-second dense capture found the
  lost focus and, fixed, kept a row focused from L0 to L2, into the evidence and back. Alt and Shift posted as window
  messages are not seen as held (Avalonia reads the keyboard's state), so Alt+Left/Right stay covered by headless tests.
- Revision 212 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,306 tests: 1,302
  passed, 4 skipped**, zero failures. In the Release window on a 20-second dense capture the browser listed its 112
  channels by process, and Enter posted to it opened the first one's records; the capture was deleted afterwards.
- Revision 211 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,305 tests: 1,301
  passed, 4 skipped**, zero failures. A live pass on a 20-second dense capture followed a worker to the PowerShell that
  started it and on to that shell's 66 children, in the Release window; the capture was deleted afterwards.
- Revision 210 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,302 tests: 1,298
  passed, 4 skipped**, zero failures. A live pass on a 20-second dense capture found the first attempt counting
  svchost.exe's 97 lanes coarser but drawing none; with the timeline pairing lanes by span, they drew in 206 columns.
  The window test that pins it fails with that pairing disabled.
- Revision 209 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,299 tests: 1,295
  passed, 4 skipped**, zero failures. A first layout put the toggle on a line of its own; the full UI suite caught the
  rail losing a ranked row at 1080 × 700 (the multi-selection test found its third row unrealized), and it moved beside
  the selector.
- Revision 208 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,297 tests: 1,293
  passed, 4 skipped**, zero failures. The live pass recorded a 20-second dense capture with `icat record` from the
  elevated shell into scratch, made every sharing preset and a ranked export from it, opened the redacted package in the
  Release window, and deleted all of it afterwards.
- Revision 207 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,296 tests: 1,292
  passed, 4 skipped**, zero failures. The headless render `unmeasured-graph.png` shows the band beside a measured edge
  and a relationship with no sends.
- Revision 206 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,294 tests: 1,290
  passed, 4 skipped**, zero failures. A 20-second dense capture was recorded with `icat record` from the elevated shell
  into scratch and deleted afterwards. In the Release window its interval table matched `icat timeline --bytes` row for
  row for every record, the TCP lane, a process lane, both direction rows and both ends of a channel. The pass also
  found `icat timeline --process` calling a quiet interval's coverage unknown where the window judges it by the capture;
  it now lists the process's own lane.
- Revision 205 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,289 tests: 1,285
  passed, 4 skipped**, zero failures. `R16: live chunk rollover under a free-disk floor` failed once in revision 204's
  clean-worktree run under heavy load and passed on rerun; its fixture now waits for a publication rather than sleeping
  30 ms, and its assertions print the recording's state.
- Revision 204 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,288 tests: 1,284
  passed, 4 skipped**, zero failures. Timings on a generated 1M-row session, Release CLI, this workstation.
- Revision 203 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,287 tests: 1,283
  passed, 4 skipped**, zero failures. On real ETW, `icat measure tcp` computed TrafficVisualization with every criterion
  met, and a 15-second recording's connection records stated no size.
- Revision 202 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,286 tests: 1,282
  passed, 4 skipped**, zero failures. A keyboard-driven live pass covered search, the evidence rung and the original record.
- Revision 201 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,286 tests: 1,282
  passed, 4 skipped**, zero failures.
- Revision 200 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,285 tests: 1,281
  passed, 4 skipped**, zero failures. On a 20-second dense capture the peer ranking matched `icat metric` group for group.
- Revision 199 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,282 tests: 1,278
  passed, 4 skipped**, zero failures. A live pass ranked a 20-second dense capture by both times.
- Revision 198 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,279 tests: 1,275
  passed, 4 skipped**, zero failures.
- Revision 197 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,278 tests: 1,274
  passed, 4 skipped**, zero failures. A live pass on a 20-second dense capture confirmed each fix in the window.
- Revision 196 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,273 tests: 1,269
  passed, 4 skipped**, zero failures.
- Revision 195 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,270 tests: 1,266
  passed, 4 skipped**, zero failures. The 20-second dense capture was recorded with `icat record` from the elevated
  shell into scratch and deleted afterwards.
- Revision 194 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,269 tests: 1,265
  passed, 4 skipped**, zero failures. The 12-second TCP recording was made with `icat record` from the elevated shell
  into scratch and deleted afterwards; the Release app received UI Automation selections only.
- Revision 193 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,268 tests: 1,264
  passed, 4 skipped**, zero failures.
- Revision 192 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,266 tests: 1,262
  passed, 4 skipped**, zero failures. The 15-second TCP recording was made with `icat record` from the elevated shell
  into scratch and deleted afterwards; keys went to the InterCat window only.
- Revision 191 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,264 tests: 1,260
  passed, 4 skipped**, zero failures. The 15-second recording was made with `icat record` from the elevated shell into
  scratch and deleted afterwards; keys went to the InterCat window only, and screenshots captured its windows only.
- Revision 190 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,263 tests: 1,259
  passed, 4 skipped**, zero failures. A 20-second Explore recording was made with `icat record` from the elevated shell
  into scratch while the RPC workload ran, and deleted afterwards; the Release app opened it and received keys posted
  to its window only.
- Revision 189 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,257 tests: 1,253
  passed, 4 skipped**, zero failures. A 25-second dense capture was recorded with `icat record` from the elevated shell
  into scratch and deleted afterwards; the Release app opened it and received keys posted to its window only.
- Revision 188 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,246 tests: 1,242
  passed, 4 skipped**, zero failures. The two ALPC cost series ran from the elevated shell on this workstation, which is
  in use; no probe session was left running.
- Revision 187 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,246 tests: 1,242
  passed, 4 skipped**, zero failures, with the ALPC probe in the solution. The probe ran four times from the elevated
  shell; `logman query -ets` showed no probe session left after each run.
- Revision 186 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,246 tests: 1,242
  passed, 4 skipped**, zero failures. `icat metric --metric duration` was run over a 30-second Explore recording made
  with `icat record` while the RPC truth workload ran; the recording stays in scratch.
- Revision 185 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,242 tests: 1,238
  passed, 4 skipped**, zero failures. The Release Desktop opened a 90-second Explore recording made with `icat record`
  while the RPC truth workload ran, driven through UI Automation and keys and clicks posted to its window alone, and
  only its window was captured; the recording and captures stay in scratch.
- Revision 184 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,240 tests: 1,236
  passed, 4 skipped** (the fourth, the RPC pairing measurement, runs only when asked), zero failures. Revision 183's
  build and this one were measured over sessions each generated, and compared on the real Explore recording, the 1M
  session and forty random sessions; the sessions stay in scratch and only the two reports are committed.
- Revision 183 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,237 tests: 1,234
  passed, 3 skipped**, zero failures. `icat metric --basis logical-operations` answered each count, a grouped rate, an
  owner filter and every unavailable case over revision 180's real Explore recording, which stays in scratch.
- Revision 182 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,229 tests: 1,226
  passed, 3 skipped**, zero failures. The Release Desktop was run on real data, driven through UI Automation with keys
  posted to its window, and only its window was captured; the session and the captures stay in scratch.
- Revision 181 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,229 tests: 1,226
  passed, 3 skipped**, zero failures. The call lane was checked in headless renders at 1080×700 and on the real
  session revision 180 recorded, whole and zoomed.
- Revision 180 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,227 tests: 1,224
  passed, 3 skipped**, zero failures. Two 60-second Explore recordings on real ETW were checked with `icat operations`
  and `icat rederive --check`, and the second's rungs were rendered headlessly; both sessions stay in scratch.
- Revision 179 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,225 tests: 1,222
  passed, 3 skipped**, zero failures. The process and RPC channel rungs were checked in headless renders at 1080×700.
- Revision 178 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,218 tests: 1,215
  passed, 3 skipped**, zero failures. `icat operations` was run over a session built from FX-RPC-001's committed
  records, and over a 1M-row session with no RPC (0.63 s in all).
- Revision 177 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,207 tests: 1,204
  passed, 3 skipped**, zero failures. FX-RPC-001 was measured again on real ETW with adapter 0.7.0, and the Release
  command line read the overviews of five earlier scratch sessions under the new binding rule.
- Revision 176 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,200 tests: 1,197
  passed, 3 skipped**, zero failures. FX-RPC-001 was measured on real ETW, and the capture-impact harness ran seven
  pairs of RPC alone.
- Revision 175 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,199 tests: 1,196
  passed, 3 skipped**, zero failures. The channel rung was checked in headless renders at 1080×700 over IPv4 and IPv6.
- Revision 174 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,197 tests: 1,194
  passed, 3 skipped**, zero failures. FX-TCP-002 and FX-UDP-002 were measured on real ETW with adapter 0.7.0, twice each,
  and a focused TCP and a focused UDP recording over `::1` were checked end to end; the capture-impact harness ran
  seven pairs per source (`bench/results/capture-impact-20260927T115356Z`).
- Revision 173 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,188 tests: 1,185
  passed, 3 skipped**, zero failures. The scale-gate benchmark ran three times in Release at 1M and 10M rows with
  `DOTNET_PROCESSOR_COUNT=16`, and three times with revision 171's build from a worktree, over the same sessions.
- Revision 172 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,184 tests: 1,181
  passed, 3 skipped**, zero failures. The IPv4 segment digests `SegmentGoldenTests` pins were measured with revision
  171's build in a worktree.
- Revision 171 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,157 tests: 1,154
  passed, 3 skipped**, zero failures. The inspector was checked in headless renders at 1080×700 and 1456×939.
- Revision 170 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,157 tests: 1,154
  passed, 3 skipped**, zero failures.
- Revision 169 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,157 tests: 1,154
  passed, 3 skipped**, zero failures. The scale-gate benchmark ran in Release at 1M and 10M rows with
  `DOTNET_PROCESSOR_COUNT=16` for the committed report.
- Revision 168 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,146 tests: 1,143
  passed, 3 skipped**, zero failures; the third skipped test is the opt-in scale-gate benchmark, which ran in Release
  at 1M and 10M rows for the committed report.
- Revision 167 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,145 tests: 1,143
  passed, 2 skipped**, zero failures. The theme report was regenerated and meets every threshold, and the real sparse
  session was rendered headlessly in all four theme modes.
- Revision 166 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,143 tests: 1,141
  passed, 2 skipped**, zero failures. `icat overview` and `icat checkpoint` ran on the real sparse ETL session, whose
  format-1.0 checkpoint was republished as 1.1 with an identical overview.
- Revision 165 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,129 tests: 1,127
  passed, 2 skipped**, zero failures, after the ledger gained its new names (the first pass caught them missing).
  Every CLI command, the broker qualification suite and the Desktop were exercised live, on real ETW.
- Revision 164 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,125 tests: 1,123
  passed, 2 skipped**, zero failures. The bounded 10-minute first-feedback passed on real ETW, and the opt-in latency
  benchmark ran in Release.
- Revision 163 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,125 tests: 1,123
  passed, 2 skipped**, zero failures.
- Revision 162 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,122 tests: 1,120
  passed, 2 skipped**, zero failures.
- Revision 161 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,110 tests: 1,108
  passed, 2 skipped**, zero failures.
- Revision 160 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,109 tests: 1,107
  passed, 2 skipped**, zero failures; two consecutive full Debug runs were clean.
- Revision 159 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,106 tests: 1,104
  passed, 2 skipped**, zero failures; three consecutive full Debug runs were clean.
- Revision 158 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,102 tests: 1,100
  passed, 2 skipped**, zero failures.
- Revision 157 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,094 tests: 1,092
  passed, 2 skipped**, zero failures.
- Revision 156 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,089 tests: 1,087
  passed, 2 skipped**, zero failures.
- Revision 155 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,086 tests: 1,084
  passed, 2 skipped**, zero failures.
- Revision 154 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,085 tests: 1,083
  passed, 2 skipped**, zero failures.
- Revision 153 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,080 tests: 1,078
  passed, 2 skipped**, zero failures. A crashed `icat capture` was finished on real ETW, leaving no ETW session.
- Revision 152 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,079 tests: 1,077
  passed, 2 skipped**, zero failures.
- Revision 151 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,073 tests: 1,071
  passed, 2 skipped**, zero failures. Through the new reader, the sparse ETL sessions imported by revisions 148 and
  149 read with the same row digests as before.
- Revision 150 was built and tested on Windows with the pinned SDK: Debug and Release both ran **1,068 tests: 1,066
  passed, 2 skipped**, zero failures. Two further Debug runs were clean too.
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
- Last executed clean baseline on Windows: revision 165, **1,127 passed, 2 skipped, in Debug and Release**. Before
  it, revision 164: 1,123 passed, 2 skipped; revision 129: 959 passed, 2 skipped. Revision 129 adds two store, two
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

`contracts/journal-v1.md`, `store-v1.md`, `segment-v1.md`, `metrics-v1.md`, `entities-v1.md`, `operations-v1.md`, `derivation-checkpoint-v1.md`, `overview-index-v1.md`,
`query-identity-v1.md`, `live-follow-v1.md`, `app-settings-v1.md`; ADR-008, ADR-010, ADR-012, ADR-013, ADR-023–031; the complete historical ledger linked above.
