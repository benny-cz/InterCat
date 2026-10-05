# InterCat / Windows IPC Visualizer

## 1. Purpose and product decisions

**Status:** reviewed product architecture and implementation blueprint, revision 298, 2026-10-05. M0 and its capture-impact follow-on are complete; M1 is in progress, with measured limitations and resume actions tracked in `docs/IMPLEMENTATION-STATUS.md`. This design document does not itself imply a capture benchmark or capability claim. Revision 20 implemented the Windows pipe-authentication boundary with first-instance/native-DACL creation, remote-client rejection, impersonated-token SID/logon/integrity/elevation derivation, owner matching and a bounded authenticated dispatch loop; PID remains diagnostic only. Revision 21 adds the broker-owned filesystem root: created with a protected DACL and a high-integrity no-write-up label in one call, validated from the open handle by reparse state, final path, volume and an ACE-for-ACE security read-back, held open without shared delete, and opened only by single validated names beneath it. §9.2 now states why the mandatory label rather than the DACL is what refuses an ordinary-integrity write, and §20.5 states why installation should pre-create the root. Revision 22 adds bounded ownership-log compaction with the generation and refusal rules §20.3 now states. Revision 23 freezes the canonical import contract of §18.4 in `contracts/import-v1.md`: source and import identity, the canonical record key and what it excludes, the ordering an import may claim, equal-time multiplicity, and the bounded spill and cancellation path. It also records that the buffer context is keyed content rather than consumer state, and that the tie-breaks which make a sort total are not an ordering claim. Revision 24 connects a real ETL to that contract through a new `InterCat.Capture.Journal` module and the `icat import` command, and records what reading one measured: evidence recorded outside InterCat carries no clock identity of ours, so both the clock and the host are derived from the file's own content - minting either per run would make two imports of one file disagree about their records' identities - and the recorded tick rate, which the adapter library does not expose, is derived from the file's own readings and then checked against every sampled one rather than assumed. Revision 25 implements the §20.1 commit protocol in `contracts/store-v1.md` - staged and completed files, immutable published names, a manifest that re-measures every dependency before naming it, a replacing pointer with a retained last-known-good, and a recovery that rolls a torn publication back rather than reading it - and ADR-010 settles the journal-lifetime decision §20.1 owed. Revision 26 adds IC-015a, which owns the physical derived store §20.1 specifies but no backlog item claimed: `contracts/segment-v1.md` freezes the segment and dictionary bytes, ADR-011 records what building them decided, and an import now publishes a generation - admitted journal, sorted columnar segments, their dictionaries and the committed boundary that names the evidence - which `icat session` reopens and verifies. It also records two rules that came out of it: a measurement slot and a measurement are separate facts, so an unknown value in a declared slot keeps its domain, side and unit and stays in the denominator; and an identity that appears inside a record's identity is derived from the evidence rather than minted per run, which fixed an importer that minted a capture identity per record. Revision 27 adds evidence leases and retention to §20.1 and §20.2: a reader acquires a generation and every dependency it names as one lease, retention publishes a record of what it released and why rather than merely deleting, and a journal release works in whole batches and is refused when it would leave no admitted evidence. It also splits IC-016a out of IC-016 for the part of §20.2's checkpoint that needs entity and operation revisions, and records a defect the work exposed: the orphan sweep treated the manifest the retained last-known-good pointer names as unreferenced, so removing orphans could delete the one thing a rollback needs. Revision 28 turns §5.3's matrix into a compiler and freezes `contracts/metrics-v1.md` for the source-observations basis, and corrects three places where the matrix could not be implemented as written (ADR-012). Endpoint activity, which §5.1, §5.2, §5.3 and §22 all describe as a metric of its own, had no `EN-Metric` code and was reachable only as an accounting side of `BytesSent` or `BytesReceived`, where it contradicts the metric's direction; it is now `EN-Metric` 15, `EndpointActivityBytes`. A row's side - which end a measurement describes - and a request's accounting - which rows a total takes - are now stated as different facts with one mapping between them. And a traffic metric takes only a traffic domain, because "required, exactly one" let `BytesSent` be asked for in `RequestedIo`, which is P3's own example. A request the matrix refuses means nothing and is refused before a session is read; a request it permits that a session cannot derive is unavailable with its reason, and a byte total that takes no known contribution is unavailable rather than zero (R21). The ADR table in §16 no longer reserves numbers for unwritten decisions. Revision 29 derives process instances from a session's own lifecycle records and binds every record to one (`contracts/entities-v1.md`, ADR-013), so a total can be grouped by process instance or by mechanism with the groups partitioning it. Two findings shaped it. Most traffic belongs to processes the capture never saw start, so a PID that records name and no lifecycle record does is one provisional instance witnessed by its earliest record rather than unattributable. And identity-v1's rule that a reused PID stays unresolved was stricter than its own rationale: only a record inside a *later* lifetime can be a late record of an earlier one, so a record inside the first lifetime resolves as a never-reused PID's would, and a later one is a candidate the default evidence policy does not admit. The boot a process key needs is derived from the capture's clock, because a monotonic clock does not survive a restart. The executable stays fail-closed until the live capture runtime and its cleanup boundary are composed.

Revision 30 adds the source-field carriage that revision 29 deliberately deferred. The additive `source-fields-v1` table is keyed to an observation by raw locator and fact key; `process-binding-v2` uses its provider start sequence, parent key and source time claims without rewriting `observation-v1`. Executable grouping uses a witnessed full image path, not an exit basename or PID. A missing path is explicit un-attribution, and an older generation with no paths says the grouping is unavailable. The bounded SID skip that admits a start/rundown image has been verified by schema tests and ETL replay; its changed live callback and capture-impact cost still requires a new elevated measurement before the earlier impact figures are reused.

Revision 31 implements the direct-owner part of §19.1's process filtering in `metrics-v1`: `owner(P)` selects an instance under the chosen binding evidence policy, after interval scoping and before layer/mechanism projection. Ungrouped totals, evidence lists and grouped totals consume the same admitted rows and report excluded owner rows separately. A missing instance is not presented as zero; cross-side owner accounting and `participant(P)` remain unavailable until a proven relation exists. `participant(P)` has explicit fail-closed request syntax, not an owner approximation. Capture-side admission and impact remeasurement noted in revision 30 remains owed.

Revision 32 implements the first R20 rebuild path: `icat rederive` streams a published generation's own committed journal through its retained compiled descriptor plan and atomically replaces derived segments in a new generation, leaving the prior manifest as last-known-good. The journal's schema fingerprint is not itself a field layout, so new imports retain the immutable bounded `normalizer-plan-v1` dependency. Legacy sessions without that plan and multi-journal sessions refuse re-derivation until a verified migration or complete multi-capture replacement exists. A future semantic normalizer change must introduce a real contract version and preserve raw identities in tests; incrementing a number alone is not an acceptance test.

Revision 33 makes that path discoverable in `icat session`: its report states whether the manifest has the structural prerequisites for an attempt, why not when it does not, and a command when it does. This is a preflight, not a claim that the journal and plan have replayed successfully; only `icat rederive` validates their contents before publishing. Connection ID alone remains insufficient proof of a TCP peer or transfer association, so revision 31's participant and cross-side refusals remain correct.

Revision 34 adds a read-only full replay check to that path. `icat rederive --check` validates the saved plan, journal digest, all checksummed frames and normalized rows without staging or publishing; it states that a later segment write or commit has not been tested. Both checking and publishing re-measure dependency digests on the handles they read, so an in-place change after store opening is refused even if the length remains the same. The two commands share one replay implementation to keep their interpretation consistent.

Revision 35 closes an import publication ambiguity: `--overwrite` never authorizes appending to or replacing evidence in a nonempty session directory. It applies only to an optional report file. An import with `--into` publishes into a fresh empty root, and the import API refuses a store that already has a generation before it reads an ETL. Appending the same capture as another generation would make every existing observation count twice; catalogue-backed reuse remains separate work. `contracts/import-v1.md` now states the session import that has existed since revision 26 instead of its stale pre-session limitation.

Revision 36 serializes publication across independently opened store instances through a root-owned exclusive file lock. A writer re-verifies the current pointer under that lock and refuses stale generations and existing immutable targets before publishing; retention follows the same rule. Opening no longer deletes an unreferenced staging file, because another process may have completed it but not yet committed. It reports the file for coordinated cleanup instead. This closes competing-manifest publication, not cross-process evidence leasing: retention in one process still cannot see another process's in-memory reader lease, so broker/viewer composition needs a single-owner retention coordinator or a persistent lease mechanism before concurrent retention is enabled.

Revision 37 closes that deletion gap with a root-owned lease guard. A reader takes a shared read handle before acquiring and verifying the pointer; retention takes an exclusive handle before deleting released evidence or orphans, and defers cleanup while any process holds a reader. A writer creates the guard before first publication, so a read-only viewer can lease a new broker-owned session. The guard is global and may delay unrelated cleanup, and a process exit releases its holds; it is neither a durable pin across restart nor a cross-process quota reservation. A stale reader refreshes its verified snapshot, but that does not refresh its writer publication baseline. Legacy sessions with no guard require an owner upgrade before a read-only viewer can lease them concurrently with retention.

Revision 38 makes a torn current-pointer rollback actionable without treating recovery as silent mutation. `icat recover` previews the verified last-known-good, reason and manifest digest; only confirmation bound to that digest backs up a bounded damaged pointer and re-points current under the publication lock, then verifies the result. It does not select a newer orphan or delete any evidence. A later publication skips generation numbers already used by immutable orphan files, so rollback to generation 1 with an orphan generation 2 can publish generation 3 whose predecessor is 1. A gap is valid; reusing generation 2 would overwrite recoverable evidence or leave the writer blocked.

Revision 39 discharges the measurement revision 30 left owed: the live callback cost and capture impact of the SID/name admission plan. With image paths admitted, both sources still classify `Low`, and the callback-admission p99 is in the same buckets as before. The process source's median classified cost moved from 0 to 0.91 percentage points, with wider per-pair dispersion. The plan's rule is unchanged: a material admission change re-measures before older impact figures are reused as evidence for it.

Revision 40 implements §7.4's network correlator and the process filters of §19.1 that rest on it (`contracts/relations-v1.md`, ADR-014). The join key had to be measured. On every admitted TCP descriptor, connect, accept and disconnect included, the source endpoint is the record's own and the destination the remote one. So a record whose end is another record's mirror is the other end of the same connection, and when every record at that end binds to one process instance, that instance is the record's peer. The provider's connection identifier is not used: it was zero on the measured build and is reusable. An end reused by a second instance stays ambiguous for the whole capture rather than being split by time, and an end nothing in the capture holds is `PeerNotObserved`, never inferred to be remote from its address. `participant(P)`, `sender(P)`, `receiver(P)` and `peer(P,Q)` are now answered, and the records a filter cannot decide are disclosed by reason. Cross-side totals group by process through the relation. `EN-Grouping` gains 9 `Peer`, which ranks the processes at the other end from a focused one. Two corrections came out of it. `owner(P)` with a cross-side total is now refused as meaningless rather than reported unavailable: it would relabel bytes P received as bytes it sent, and no derivation can fix that. And a relation proves which process is at a record's other end, not which of its records is the same transfer, so the canonical owner of §5.3 still needs per-transfer associations.

Revision 41 gives staged files an OS-held ownership marker from creation through completion and publication. A data stream closing is not abandonment: a writer may still be preparing a manifest. `icat staging` previews active, marker-proven abandoned, marker-only and unmarked legacy files. Only a digest-bound confirmation under the publication lock removes the exact reviewed abandoned set; it never removes active, unmarked or published evidence. The marker is released on abandonment or process exit and removed after successful publication. Old staging from before markers cannot be proved abandoned and stays for manual review. This closes coordinated cleanup for new files without making ordinary session open destructive.

Revision 42 starts IC-017's layout foundation. The desktop now consumes a separate immutable, graph-identity-labelled layout result rather than source-model coordinates. A pure layout core sorts nodes and edges, seeds nodes on a stable per-group lattice from the graph identity and instance ID, retains prior positions when the group still permits them, enforces hard pins, and runs a fixed 120-step bounded relaxation with a scored best state. It refuses a graph above its explicit 512-node/4096-edge provisional bound rather than omitting members. The five-node tour is small enough to compute synchronously; query scheduling, off-thread layout for real graphs, compaction, persisted pins and stale-result rejection remain IC-017 work. Visual inspection of both supported rendered window sizes also changed the canvas transform and label side so upper-band labels do not collide with lower-band nodes. The old "300 iterations or 120 ms, whichever comes first" instruction was internally inconsistent with deterministic final positions: a wall-clock stop changes the number of iterations with host load. The corrected contract uses a fixed bounded computation for results; responsiveness comes from off-thread scheduling and cancellation, not publishing a partial wall-clock-dependent geometry.

Revision 43 gives that layout its first off-thread publisher. `GraphLayoutScheduler` snapshots nodes, edges, prior positions and pins at submission; a new request invalidates and cancels the old one, and even a worker that ignores cancellation cannot publish a late layout. A result naming another graph identity is refused, a closed window cancels its work, and caller cancellation remains distinguishable from supersession. The desktop keeps the synthetic positions only as a first-frame fallback and applies the complete result on the UI continuation; render tests wait for that result. This implements R7 for layout, not for the still-owed numeric query bundle. The scheduler cancels outside its lock so a worker callback cannot reenter it and stall the UI.

Revision 44 freezes §10.5's canonical form for the members `metrics-v1` implements (`contracts/query-identity-v1.md`, ADR-015). §21.2 lists it as an M1 closure artifact, although the backlog had filed it under IC-018 in M2. Writing it against real requests found five defects. `requestedRows` sat in §23's hashed member order while §10.4 and §10.5 put it beside the hash; it now stays beside it, so the top 5 and the top 20 of one total share an identity hash. A rate had no member for its numerator; `rateNumerator` now follows `metric`. Filter terms were sorted "by dimension code", and no dimension had one; `EN-FilterDimension` now gives them codes, with a peer as a member of the process term (§19.1) rather than a term of its own. A snapshot entry named a generation number, which is local to one session; each entry now carries the manifest digest that pins its bytes. And what a metric means was not a version axis, though its rules changed twice in two days; `metricsContract` is now §24's fifteenth axis. Only the axes and terms an answer depends on are written, so an evidence policy that cannot change an ungrouped total does not split one query into two identities. Every `icat metric` answer, available or not, names its identity, and `--print-canonical` prints the bytes it hashes.

Revision 45 answers `ActivePeers`, one of the two distinct counts §5 defines and §5.2's peer ranking needs. A peer is a process instance at the other end under `relations-v1`, which is an identity, so counting peers establishes none from reusable values (R22). The count is a lower bound: a record whose other end is unresolved names no peer and is an unknown contribution with its reason, a remote process is never counted, and a count with no resolved peer is unmeasured rather than zero (R21). A count needs a subject process, a focus or a grouping by process or executable, and a grouped count's rows overlap, so it states that they do not partition its total. `ActiveChannels` stays unavailable, and the reason is now precise: a channel is a connection incarnation with a lifetime, and whole-capture ends would count a reused port once.

Revision 46 scopes connection ends by their witnessed lifecycle (`tcp-endpoint-relation-v2`, ADR-016), as §7.2 asks for port reuse. An end's records are divided into connection incarnations at the connects, accepts and disconnects the capture holds for it. Incarnations at the two ends pair in order when their counts match, since a 4-tuple carries one connection at a time, and otherwise only by a uniquely overlapping lifetime. An undecided pairing still names the other end when every candidate is held by the same instance. A reused port whose lifecycle was witnessed now pairs each connection with its own other end, where the whole-capture rule left both without a peer. An end whose lifecycle was not witnessed is one incarnation, exactly as before, and a lost boundary can merge two incarnations but never split one. The canonical query form now takes its version-axis values as input, so the new rule changes identities that depend on relations without changing the form or its golden corpus.

Revision 47 answers `ActiveChannels`, the second distinct count of §5, now that a connection incarnation is an instance with a lifetime. Two paired incarnations are one channel, a port reused by a later connection is another, and a connection whose other end no record holds is a one-sided channel. When pairing is undecided, only the side with more incarnations is counted, since each of those is bounded by its own lifecycle, so one connection is never counted twice. Records that identify no channel - another mechanism, no endpoint pair, the uncounted side of an undecided pairing - are the count's disclosed unknown part, making it a lower bound. A channel count needs no subject process; per process it overlaps as a peer count does.

Revision 48 completes §19.1's process filters with `between(A,B)` (ADR-017), whose direction policy §23 did not define. A record passes when its maker is a process of one set and its other end, under `relations-v1`, a process of the other. `EN-BetweenDirection` keeps either way, or only the data that left the first set for the second, or only the reverse. A record with no data direction passes under either way only. The filter names both ends, so combining it with a focus or a peer is refused rather than intersected. Its canonical term writes one meaning one way: each set sorted, and a backward direction written forward with the sets exchanged. Verifying it on the `peak` capture found a defect. An answer that reads process bindings without a focus or a process grouping derived instances without the session's start keys. So a between filter could not find the instances the process list prints, and a channel count over every process named no binding rule in its identity, although its relations read bindings. Every answer that reads a binding now derives instances one way, and its identity names the binding rule.

Revision 49 adds focused `ActiveChannels` grouped by peer. The overall distinct channel count remains the same as its ungrouped focus; each resolved counterpart gets its own distinct count of connection incarnations, while known channels with an unresolved counterpart remain unattributed by reason. Unknown channel identities remain a lower-bound disclosure, never zero. The grouped rows do not claim to partition the total, and a top-N remainder counts the union of hidden channel identities. The candidate-binding policy continues to apply to the counterpart. Grouped distinct requests refuse a record evidence list, which enumerates one ungrouped total rather than a group's records.

Revision 50 prepares IC-017's real-session workspace bridge without claiming it exists. The viewer now accepts an immutable non-tour snapshot under its own graph identity and opens an empty snapshot without selecting a made-up process. The header and selected-interval label describe that snapshot, and the coverage strip states its interval coverage states without asserting a gap duration the ledger did not measure. Workspace viewport time is explicitly 100-nanosecond presentation ticks; a future projector must convert the capture's native or session-relative readings to this scale and must keep graph, timeline and accessible tables on one leased generation. The default tour remains visibly synthetic.

Revision 51 starts the real-session read-side projection under one evidence lease. A bounded overview derives process instances and paired TCP relations using the session's own clock and source fields, aggregates only admitted relationships into stable display-order edges, and converts session-relative nanoseconds to the viewport's 100-nanosecond scale for an observed-only timeline. It refuses graphs above the provisional 512-node/4,096-edge bound instead of silently dropping entities. Candidate relations require explicit policy, unknown times and unresolved TCP counterparts are counted separately, byte totals are not invented, and timeline coverage remains unknown until the coverage ledger is composed. The timeline renderer now overlays a coverage hatch on observed bars instead of erasing them: incomplete coverage and observed activity are independent facts. This bundle is not yet the coherent graph, ranking and timeline view §19.3 requires; channel, operation, evidence and byte projections and numeric scheduling remain.

Revision 52 gives §19.3's eligible-set rule a concrete read-side join. A paired relation exposes the same within-derivation channel number that each of its two ends' records names. The overview keeps an all-observations timeline and separately constructs a graph-eligible timeline from only the channels of displayed, policy-admitted relations, on the same interval grid. It verifies that selected row count equals the displayed edges' record count before returning either. A one-sided or unresolved flow stays in the all-observations view and cannot become a graph bar; a candidate relation enters the graph and its timeline together only when policy admits it. The channel number is not a cross-generation identity, so the query/graph identity continues to name the derivation and generation. The UI still must publish these projections with its ranking and coverage as one applied bundle.

Revision 53 implements the first capture coverage ledger for imported sessions (`contracts/coverage-v1.md`, ADR-018). It publishes delivered, admitted, policy-omitted, undecodable and source-reported loss facts as an immutable manifest dependency, carries them unchanged through re-derivation and protects them from derived-file retention. A quiet mechanism in a standalone ETL remains unknown because the file cannot prove provider enablement; a reported zero loss is kept distinctly from an absent counter. An unknown descriptor version has no assigned mechanism and conservatively affects every collected mechanism. An ETL system record with no provider GUID remains counted only in the whole-provider policy-omission bucket; it cannot become admitted evidence under an invented identity. Coverage is bounded by delivered readings, never extrapolated outside them, and does not turn an empty metric into zero. Live capture must emit measured counters for every required loss layer and open epochs at policy/source changes before it may publish this contract. The desktop still needs to compose ledger state with its leased real-session overview.

Revision 54 joins a published coverage ledger to the leased real-session overview's time buckets using the source clock's exact native boundary conversion, including negative times around presentation tick zero. Each all-observations bucket rolls up the states of mechanisms with observed rows in that bucket; the graph-eligible TCP timeline uses the same ledger only where it has displayed, eligible rows. Empty buckets remain unknown, a quiet imported mechanism stays unknown in the bundle's mechanism summary, and an unlocated source loss makes every affected observed bucket partial without fabricating a loss time. The bundle names whether a ledger was published and preserves read-only results. The desktop still must apply this bundle atomically with its ranking and ladder; coverage over absent rows or unproven relations cannot be inferred from a covered source.

Revision 55 joins the same ledger to every metric answer under its evidence lease, separately from the observed value and its measurement availability. A mechanism-scoped request carries that mechanism's coverage over its native interval; an all-mechanism request carries separate states rather than a false single "covered" claim. Legacy generations remain unknown. A rate keeps exactly its observed numerator and whole-interval denominator, with no correction for a reported gap. The CLI reports this distinction in JSON and in a compact terminal summary, and `metrics-v1` now states it. Source coverage still does not prove the completeness of a process binding or relation, and live capture epochs are not yet published.

Revision 56 admits UDPv4 datagrams after measuring what their events mean (FX-UDP-001, ADR-019). The Kernel-Network source already delivered them under its IPv4 keyword, and the catalog omitted them because no measurement said which endpoint each descriptor names first. A new `udp-loopback` truth workload answered that. A send names its owner's endpoint first, as every TCP descriptor does, but a receive names the datagram's sender first, 16 of 16 each way. Records keep endpoints as the source names them, and a reader that needs a record's own end reads it through one measured orientation, by mechanism and kind. With it applied, the fixture met every traffic criterion of §14.2 at 100% and reached `TrafficVisualization`, reproduced by a second run. A focused capture now admits only the transport it names, and UDP is a focusable transport. The capture impact was re-measured with the wider plan. With seven pairs per source on a machine that was not quiet, both sources came out just over the Low ceiling (1.61 and 1.53 CPU percentage points, Moderate), well inside §12's target, and the catalog records that measurement rather than the older, narrower one. The relation rule still reads TCP only, so a UDP record's other end is `NoRelationRule` until a rule reads UDP.

Revision 57 relates UDP datagrams through their mirrored endpoints (`transport-endpoint-relation-v3`, ADR-020). A record's own end is read through the measured orientation of revision 56, and every end is keyed by its protocol as well as its endpoints, so a TCP and a UDP socket on the same numbers are never paired. A UDP end has no lifecycle records, so it is one incarnation - one datagram flow - for the capture, and a port reused by another process within it stays ambiguous rather than being split at a guessed moment. Every TCP answer is unchanged, and the rule's identity changes because relation-dependent answers now read UDP. On an imported capture of the UDP workload, a client's 23,008 B conversation was attributed to its server as correlated and partitioned by peer, and its three sockets were exactly three channels. The leased overview graph keeps its TCP relations exactly as before; UDP joins it when IC-017 gives edges a mechanism of their own.

Revision 58 records live captures straight into sessions (ADR-021). Until now every session came from an ETL another tool recorded, and the broker's capture runtime had only a test fake. `LiveSessionRecorder` drains an owned capture's admission queue on one writer thread into the session's own `journal-v1`, in acquisition order, and derives rows as the import does. When the capture stops, it publishes one generation through the §20.1 commit protocol, with a live coverage epoch: the providers whose enablement succeeded, and the session's own counters for lost events, lost consumer buffers, queue drops and records admitted but never journaled. A queue-dropped record is loss, not a delivery. An unreadable loss counter withholds the ledger, so coverage stays unknown, and a refused clock withholds the generation. `icat record` runs it from an elevated shell for a profile and a duration, and Ctrl+C keeps what was recorded. An 8-second Explore recording journaled 4,273 records, lost nothing, and reopened through every reader like an imported session. Building it found that an import's ledger named its providers by GUID, because an import enables none; the shared tally now names them from the source catalog. Publishing while recording, so a viewer can follow a capture, and the broker's binding of the same runtime remain.

Revision 59 publishes a live recording as it records (ADR-022). Every publication interval that admitted records completes a journal chunk, a complete and immutable `journal-v1` file with its own header, clock, schema table and terminal frame. It then publishes a generation carrying every earlier chunk and segment plus the new ones. Record ordinals continue across chunks. The coverage ledger comes only with the last generation, because an epoch still running has not stated what it lost. A reader follows the capture by acquiring the current generation again: during a 12-second recording published every 3 s, `icat metric` read 626 and then 961 observations. A growing journal whose generations name longer prefixes was the other reading of §20.1's committed boundary. It is deferred because it would make a published file change, make dependency checks measure prefixes, make readers share files with a writer, and leave prefixes without terminal frames. Chunks give the same following behaviour and keep every rule the store proves. Re-derivation and journal-prefix retention still read one journal and now refuse several by name, and §20.1's compaction targets become necessary as chunks accumulate.

Revision 60 re-derives a live recording (ADR-023). ADR-022 left re-derivation reading one journal, so every chunked recording was refused the rebuild §20.1 promises for retained evidence. Re-derivation now replays every chunk in recorded order and publishes one replacement generation, which carries every chunk, the plan and the ledger unchanged. Before it publishes anything, the replay proves the chunks are one recording. Each chunk's digest must match. Every chunk must name one capture and one clock. Within each stream and epoch, each chunk's ordinals must pass those of the chunks before it. The boundary must name the newest chunk. ADR-022 also made a row's journal index count within its chunk, so the index beside a metric's evidence did not locate a record in the capture, and a replay could not reproduce it. The index now counts across the capture's chunks. On the 5-chunk, 1,288-record recording from revision 59, the replacement answered `observations` by mechanism exactly as the recording did, reading 1 segment instead of 4. Journal-prefix retention still reads one journal.

Revision 61 lets a live recording shed its oldest evidence, and stops re-derivation from quietly dropping rows (ADR-024). A chunk is the unit of release in a recording. A boundary releases each chunk that ends at or before it, the generation stops naming them, and nothing is rewritten. Only a leading run of chunks can go, never the one the committed boundary names, and never every record. The retention record lists the chunks, and its source digest covers their dependency lines. Writing that exposed a defect in ADR-010's release. The release keeps every derived row, but the next re-derivation replayed only the retained journal and carried none of the earlier rows, so the released records' rows vanished with no refusal and no note. Re-derivation is now refused whenever it would drop rows no replay can rebuild. The retention generation is refused from its own record. A later generation is refused when a stream's rows go back further than its retained records. `icat retain` states this before a release is confirmed. On the 5-chunk recording, releasing the first chunk freed 183,169 B of its 626 records without writing anything, and `icat session` then gave the reason re-derivation was no longer available.

Revision 62 makes a store instance hash each immutable dependency once (ADR-025). `store-v1` re-measures every dependency before a generation names it and whenever a reader acquires one, and re-measuring hashed every byte each time. A live recording carries every earlier chunk, so each publication hashed the whole session, and did so twice. A benchmark publishing 120 chunks of 20,000 records measured commits growing from 158 ms to 1,951 ms at 742 MiB, and a writer's lease reaching 972 ms. At §12's ingest target a 5-second publication would have spent longer hashing than recording within a minute or two. Every dependency is still opened and its length checked. Its bytes are hashed unless the same instance already hashed that file and its last-write time has not moved, which any write to an immutable file would do. A fresh instance hashes everything once. The same benchmark now commits in 148 ms and leases in 38 ms at 742 MiB. §20.1's disclaimer already covers the trade-off: checksums detect corruption and do not claim tamper resistance, so corruption that leaves a file's time alone is found by the next fresh instance. File counts and viewport opens still grow with a recording, which is what §20.1's compaction targets address.

Revision 63 implements §20.1's compaction targets (ADR-026). A publication unit is the derived files one generation published; its dictionaries serve only its segments, because a segment resolves them through its own generation. A unit is small below 64,000 rows and 8 MiB. Runs of consecutive small units are rewritten into bounded segments, and every row moves unchanged, so every observation keeps its identity. The generation releases the replaced files through the retention path, which respects leases and keeps disk use flat, and carries the journals, plan, ledger and boundary unchanged. A live recording coalesces the oldest run, at most one segment's worth of rows, whenever 64 small publications have accumulated, and every run left when it stops. `icat compact` does the same for any session. §20.1's per-time-block bound is read as the recorder's count of small publications, because a chunked recording's segments cover consecutive intervals. On a 120-publication benchmark, compaction reduced 120 observation segments to 10 and 360 files to 140, at about 200,000 rows per second. The 5-chunk recording went from 4 segments to 1 with identical answers. Journal chunks are evidence and are not coalesced.

Revision 64 splits live recording along §9's boundary and closes a gap in it (ADR-027). §9 and §18.1 give the broker the authoritative journal and leave decoding and normalizing to an unprivileged process. `LiveSessionRecorder` did both in the recording process, and since revision 63 it also compacted. That is acceptable for an elevated `icat record` but not for the broker. The plan also never said where a broker-owned capture's derived segments live: the broker directory's no-write-up label keeps every ordinary-integrity process out of it. A privileged recording now publishes an evidence session - journal chunks, plan and ledger, with no rows - through the ordinary commit protocol (`icat record --evidence-only`). An ordinary process follows it (`icat follow`) into a session directory of its own. It mirrors each committed chunk byte for byte against the evidence digest, derives the rows with the same normalizer and capture-wide journal index, copies the plan and ledger, and compacts. It also resumes where it stopped and refuses evidence it does not mirror. The derived store of a broker-owned capture is therefore the viewer's own directory. Verified live: a 12-second evidence-only Explore recording, followed as it recorded, yielded a derived session whose observations by mechanism equal the recorder's coverage counts (TCP 723, process lifecycle 453, UDP 78). Still owed for the broker binding: a module the broker may reference for the evidence-only path, per-capture directories in the broker root, the runtime and the executable.

Revision 65 gives the broker's recording path a module of its own (ADR-028). `InterCat.Capture.Journal` was meant as the one bridge between the capture adapter and the storage formats, shared with the future broker. It had since come to hold the ETL parser, the normalizer, re-derivation, the follower and compaction, none of which may run privileged, so the dependency map kept the broker away from all of it. `InterCat.Capture.Recording` now holds what a privileged recording runs: `LiveRecorder` with its journal chunks, plan and ledger, the envelope mapper, the coverage tally and the retained normalization plan. A derivation plugs in through `ILiveRecordingDerivation`. `LiveSessionRecorder` supplies the normalizer and compaction through it for an elevated `icat record`, and the broker will supply none. Behaviour is unchanged: every test passes, and a live recording published 4 chunks and one closing compaction.

Revision 66 gives every broker capture its own evidence directory beneath the pinned root. The fixed capture ID names one ordinary child component; creation applies the root's protected ACL and no-write-up label before the directory exists, and adoption checks the open handle's reparse state, final path, volume and security. Unlike an empty root, an existing capture directory with changed security is refused, not repaired: silently blessing files already in it would cross the evidence boundary. Its held handle prevents rename during publication. The viewer reads that directory but writes its mirrored, derived session into an ordinary user-owned directory (ADR-027). The broker executable remains disabled until the live runtime, quotas, recovery and authenticated pipe host are composed.

Revision 67 corrects a broker recovery rule before the live runtime is enabled. The coordinator previously preserved an unexpired active capture on process restart, as though a still-valid owner lease also preserved the former broker process's ETW handle. It does not. Startup recovery now requests stop for every active capture from the durable ownership token, even when its lease is unexpired. A refused stop remains partial and retryable, never relabeled closed. Ordinary in-process lease renewal and expiry sweeping are unchanged; restoring an active capture across broker restart would require an explicit, separately proven reattachment protocol.

Revision 68 binds a broker ETW session name to the durable capture and ownership token without truncating the token. The old 64-character name lost more than half of its token; the new 60-character `InterCat-b-<capture-prefix>-<token>` uses 64 bits of capture ID and the full 128-bit token. Durable records of the old form remain valid for cleanup only; new starts never issue one. The file-backed ownership store checks the name against its capture ID, and the ETW adapter can construct a capture identity from an already-persisted ID, name and token without minting another. This is an identity prerequisite for the runtime, not live broker enablement.

Revision 69 gives the evidence-only recorder an explicit readiness signal for the broker's asynchronous `Start`. It fires only after ETW has started, the source clock and first journal stage have been established, and the dedicated journal writer thread has entered its run; a startup refusal never fires it. The completed recording result remains the final authority on stop and publication. An elevated ordinary `icat record` uses the same path without a readiness observer. This does not itself enable the broker runtime.

Revision 70 adds an exact-name ETW recovery-stop capability to the Windows adapter. It validates the durable token's current or legacy broker-name shape before touching ETW, checks whether that exact session exists, uses TraceEvent's `Attach` option (which fails rather than creating/restarting an absent session), requests stop, and checks that the name disappeared. An inaccessible session that remains is an explicit failure, so the lifecycle record stays partial and retryable. This is the adapter path a broker runtime will use for orphan cleanup; it has shape/refusal tests but no elevated live crash/restart verification yet, and the executable remains disabled.

Revision 71 composes an evidence-only `IBrokerCaptureRuntime` behind the disabled broker executable. It starts only from a matching durable capture/token/plan digest, creates a new protected per-capture directory, acknowledges only after ETW and the journal writer are ready, and stops with separately verified ETW, callback-drain and journal-publication milestones. Recovery attaches only to the exact durable ETW name and reports callback drain and journal finalization as **unproven**, never inferring them from an absent session. The recorder now rejects an append before an exact journal-v1 byte allowance would be exceeded, accounting for buffered records, batch framing and the terminal; it ends acquisition and publishes the admitted prefix plus a loss ledger. The runtime applies a duration limit and checks the free-disk reserve before starting and each second while recording. The previous rule requiring the free-disk reserve to be *smaller* than the journal allowance was nonsensical: these bounds protect different things, and a small journal with a larger system reserve is safer. They are now validated independently. A bounded journal currently publishes once on stop because periodic rollover cannot yet reserve the bytes and final ledger of its next chunk. Before enabling the executable, persist autonomous quota-stop completion in the lifecycle log, give the free-space limit a hard per-write/reserved-finalization guarantee rather than only a one-second observation, verify crash recovery against a live elevated ETW session, and exercise the authenticated pipe, protected root and lifecycle together. The ordinary follower cannot follow this bounded capture until publication rollover is made quota-aware.

Revision 72 makes the runtime's autonomous end observable to the lifecycle coordinator without a client stop request. An in-process completion probe is read-only; the coordinator first writes a durable `AutonomousStop` intent, then obtains stop milestones and persists the result. Status and lease renewal reconcile a completed capture before answering, so neither claims it is still recording or renews a finished capture; a host may also sweep completed captures proactively. File-backed tests prove status-triggered and no-client reconciliation survive reopen, and an integrated evidence-runtime test proves a duration stop becomes durable status. The broker executable remains disabled because the host does not yet run the sweep, a persistence failure may leave a pending stop that needs restart recovery, the disk reserve remains observational, and quota-aware live publication plus elevated pipe/restart verification remain open. A live journal that finalized before a broker crash must be verified from its committed manifest before recovery can promote its journal milestone; merely finding the ETW name absent is not enough.

Revision 73 lets an evidence-only recorder publish live journal chunks while enforcing one aggregate journal-byte ceiling. Before rolling over it accounts for all already published journals, the exact completed size of the current buffered chunk and a complete empty next chunk (header, clock, schema table and terminal). If the next chunk cannot fit, it keeps the current chunk open for finalization rather than publishing and spinning on a due timer. Each append uses the remaining aggregate allowance, and the final generation carries the coverage ledger. A test opens an intermediate generation as a reader while the capture pump is still active, then verifies every published journal together stays under the cap when the limit stops acquisition. The broker runtime still uses no publication interval: choose and disclose a bounded, performance-qualified cadence in the effective prepared plan before wiring live follow, since frequent manifests repeatedly verify growing dependency sets. The executable remains disabled for the other revision 72 gates as well.

Revision 74 makes stop-result persistence retryable without replaying the capture runtime. Once a stop has produced its milestones, a failed durable completion write is retained in the coordinator as an in-process pending completion; host maintenance, authenticated status and authenticated lease renewal may retry that exact completion. The retry does **not** call `IBrokerCaptureRuntime.StopAsync` again, because a second runtime stop after the active capture has been removed can only reconstruct weaker restart evidence and would incorrectly downgrade already-proven callback/journal milestones. A different owner still cannot trigger reconciliation. This is an in-process durability bridge, not crash recovery: after a broker process loss, §20.3 recovery must verify any already-published final journal from the protected capture store before promoting `JournalFinalized`.

The live-publication sequencing is also tightened. The 4 Hz value in §19.3 is an **analytic snapshot/UI freshness target**, not a journal-manifest publication rate. Journal chunk publication has a different cost profile because each generation re-verifies an expanding immutable dependency set. Its cadence must therefore be a separately named, bounded setting compiled into the effective prepared plan, included in the plan digest/effective summary, and benchmarked against capture impact before the broker enables live follow. A client may request a supported policy/level, but it must not inject an arbitrary millisecond interval into the privileged runtime.

Revision 75 closes the restart-finality proof gap before that live-publication cadence is enabled. A complete `journal-v1` chunk proves only that one immutable chunk committed; because intermediate live chunks are complete too, it cannot prove that capture publication ended. A new bounded `capture-finalization-v1` dependency is staged only with the last generation after the owned capture session stop returns, recording the capture ID and the provider-stop/callback-drain observations then available. Restart recovery reopens an existing protected capture directory without creating one, verifies the store and prepared-plan source identity, verifies that finalization marker, binds the committed boundary to its journal length/digest/capture identity, and replays that journal through its terminal and committed record count before promoting `JournalFinalized`. `CallbacksDrained` is promoted only when the verified marker records it; provider stop is still checked independently against the durable ETW ownership. A verified coverage ledger remains a compatibility-only last-publication signal for older recordings, and cannot recover callback-drain evidence. The follower uses the new marker as its normal finished signal, so a clean capture with unreadable loss counters is finished with unknown coverage rather than appearing live forever.

Revision 76 tightens the remaining free-space gate before code is added. `MinimumFreeDiskBytes` is a floor for InterCat's own write admission, not a second journal quota and not a promise that unrelated processes cannot consume the volume between observations. The one-second monitor remains useful telemetry but is not enforcement. Every evidence write/publication path must refuse new growth before its own write would cross the floor, while separately preserving enough calculable headroom to finish the already-admitted journal tail and publish bounded final evidence/manifest metadata. That headroom is derived from the actual pending journal state and bounded encodings, not an arbitrary fixed polling margin; finalization may consume the reserved headroom but must still preserve the configured floor. If InterCat ever claims an exclusive volume reservation against other writers, it needs an OS/filesystem reservation mechanism rather than this admission invariant. §20.2 and the broker status UX must report the distinction plainly.

Revision 77 implements that floor and corrects an overstatement. The live recorder now admits a journal append only if the projected complete chunk stays under a ceiling computed from the last free-space probe: bytes already written to the chunk, plus the space the broker identity may still allocate (GetDiskFreeSpaceEx, so a per-user quota counts), minus the configured floor, minus finalization headroom. That headroom is bounded, not guessed: the coverage-ledger and finalization-marker encoding limits, a manifest-and-pointers bound per named dependency (`SessionStore.PublicationMetadataBound`, checked against real metadata by a test), one allocation unit per file the last publication creates and the staging stream's write buffer. The unwritten journal tail is already part of the projection. A refusal on the floor probes once more before stopping, observations expire after 250 ms, an impossible or failed probe stops acquisition rather than assuming space, and intermediate rollover is admitted only if its metadata and the next chunk's header also fit. Start is refused, with the numbers and what to do, when the volume cannot hold the floor plus that headroom. The one-second monitor remains only to end an idle capture whose volume another program filled. Work on this also found that the recorder's writer could spin once a publication was due but refused; it now waits for records whenever no rollover is possible. Section 20.3 previously said the bounded journal-publication policy was already frozen into the prepared digest; it is not yet, and that sentence now says so.

Revision 78 freezes the journal-publication cadence in the prepared plan. A client asks for a named policy - `OnStop`, or `Live` so an ordinary process can follow the capture - and never supplies an interval. `Live` compiles to `max(2 s, ceil(maximum duration / 1024))`. A fixed interval was not viable: at 1-2 s a 24-hour capture would publish 43,200-86,400 chunks, exceeding the 65,536-dependency manifest limit and rewriting a multi-megabyte manifest on every publication. Capping a capture at about 1,024 chunks keeps each publication's verification and metadata cost bounded for the whole capture, at the price of coarser follow latency for very long captures (85 s at 24 hours), which the effective summary states. The policy is part of the plan digest; the effective summary carries the policy and its compiled interval, and a client refuses a summary whose interval is not what the policy compiles to. The broker runtime now passes the compiled interval to the recorder, where the journal quota and the free-disk floor already govern rollover. The 2-second floor and the 1,024-chunk cap are engineering bounds, not measurements; an elevated capture-impact benchmark must confirm or change them before the broker is enabled, and changing them changes the digest input rather than silently altering prepared plans.

Revision 79 adds the broker host's maintenance loop as a composable component, `BrokerMaintenanceLoop`. The host runs its `RecoverAsync` to completion before the pipe accepts commands, then runs passes at a one-second default (bounded to at most one minute). Each pass makes autonomous duration, journal-limit and disk-floor stops durable, retries stop completions whose durable write failed, and stops captures whose interactive owner lease expired - without waiting for a client to ask. The two halves are isolated, so a failed lease read does not skip completion reconciliation. A persistence failure inside a stop counts as a failed pass, and a failed pass is reported with a consecutive-failure count rather than ending the loop. A throwing report sink is ignored, and cancellation or coordinator disposal ends the loop normally. The coordinator's completion sweep now retries queued stop completions even when the runtime has no completion probe. The executable is still not composed: wiring the pipe server, protected root, file-backed lifecycle store, evidence runtime and this loop into one elevated process, then exercising crash/restart with real ETW, is the remaining enablement gate.

Revision 80 composes the broker executable behind an explicit opt-in, and closes a gap in the pipe trust model. `BrokerHost` recovers the durable store before the pipe exists, then serves one authenticated client at a time on a single first-instance pipe it holds for its whole lifetime - it disconnects and waits again on the same handle, so the name is never free between clients - while the maintenance loop runs beside it. A refused client (wrong user or logon session) is disconnected and the host keeps serving. An on-demand elevated broker must not linger: it exits after a configurable idle period (default five minutes) with no client and no active capture, and never while a capture holds an ETW session; an unreadable store counts as active. On shutdown it stops and finalizes what is still recording (a new `HostShutdownStop` request kind) rather than leaving the successor an orphan with partial evidence. `serve` takes the owner SID, the owner's logon-session LUID as the client's own token reports it (elevation can use another account and always a different logon session), and the pipe instance GUID; none is trusted as authentication. Serving additionally requires `--enable-unqualified-live-capture` until the elevated crash/restart and capture-impact runs qualify live capture; without it the executable fails closed exactly as before. Exit codes follow §20.4. The gap: §20.3 authenticated the client to the broker but never the broker to the client. First-instance creation stops the *broker* from joining a squatter's pipe, but a process that claims the name first would receive the client's Hello and Prepare, and an elevated process's command line - which carries the GUID - is readable at ordinary integrity. The client must therefore launch the broker with a process handle and, after connecting, require `GetNamedPipeServerProcessId` to equal that process, refusing and reporting otherwise. §20.3 now states this.

Revision 81 records the first elevated qualification of the composed broker with real ETW (`tools/InterCat.BrokerQualification`, evidence in `bench/results/broker-qualification-*`). It found a production defect the fixtures could not: an elevated administrator's objects are owned by the user under Windows' default "object creator" owner setting, so the broker refused the root it had just created as untrusted. The root and capture-directory descriptors now name an explicit Administrators owner (`O:S-1-5-32-544`), which an elevated administrator token and SYSTEM can both assign. With that fix, a clean stop is fully finalized over live chunks, and a broker killed mid-capture leaves exactly one orphaned session, which its successor reclaims before its pipe exists. The published prefix stays readable, the result is an honest partial stop, and no InterCat session leaks. The client half of server authentication (`WindowsBrokerPipeClient`: the pipe server's process must be the launched broker) is implemented and used by the run. The run also shows two gaps that are next in order. First, an interrupted capture's staging files (`stg-*.tmp`/`.lease`) are never removed. Second, a partial stop whose remaining milestones can no longer be proven - the process that could have drained callbacks is gone - stays `Stopping` and is retried by every later broker start without any possible progress. Recovery must classify such a capture as terminally interrupted: it verifies and keeps the published prefix, salvages the complete frame prefix of the unpublished tail only if one is readable, releases the staging files, and closes the capture with its partial milestones and an `Interrupted` reason, so status says what was kept rather than "stopping" forever. Capture impact at the compiled live cadence remains unmeasured; that benchmark is still required before the opt-in is removed.

Revision 82 closes interrupted captures terminally. A partial stop is retryable only while a retry can prove something. Once recovery has proven the owned ETW session stopped, the process that could have drained callbacks and finalized the journal is gone. The runtime then releases that writer's abandoned staging, but only files whose ownership marker nobody holds; a staging file a live writer still owns keeps the capture retryable. It returns a terminal outcome whose reason begins `Interrupted:` and states what was kept. The coordinator closes the capture with its partial milestones: the state is `Closed`, the milestones say what is proven, and the stop code stays `StopPartial`. Later recoveries leave it alone, and the host's exit code is decided by milestones rather than by state. A provider stop that failed stays retryable, because stopping the session later is real progress. Salvaging the unpublished tail is deferred. It needs its own contract for a generation that is final without a finalization marker, and the loss it would recover is bounded by one publication interval under `Live`. Under `OnStop`, however, a killed broker loses the whole capture, because nothing was published. An interactive client should therefore prepare `Live`, and its review screen must say that `OnStop` keeps nothing if the broker is killed. §20.3 now states the retry rule. Real-ETW qualification confirms the behavior: the killed capture closes as interrupted, its published records replay, and no staging file or session is left.

Revision 83 measures the broker's capture impact at the compiled live cadence (`InterCat.BrokerQualification impact`, `bench/results/broker-impact-*`). The run uses the same seeded Explore TCP workload (4 connections × 768 messages, about 13 s) with no capture, with a broker capture publishing `OnStop`, and with one publishing `Live` every 2 s (7-8 chunks). Order rotates within five triplets. Throughput regression is zero in every summary. The broker process's own CPU over the window is about 470 ms under `Live` against about 170 ms under `OnStop`: the cadence costs roughly 0.3 core-seconds per 13 s, or 0.08 percentage points of a 24-thread machine. Median machine-wide impact of `Live` against no capture was 2.2 pp in one run and 0.0 pp in the next (Low to Moderate, under §12's 5 pp target). The difference between those runs is the problem: this development machine's busy share varies by ±3 pp with no capture at all, so machine CPU is not decision-grade here. An earlier three-triplet run showed 6.3 pp from two noisy trials, which is why the artifact now attributes broker CPU directly. The 2-second floor and the 1,024-chunk cap therefore stand as measured, not only as engineering bounds, at this workload scale. A quiet-host rerun remains an M5 release obligation before any published overhead claim. The enablement gates of revisions 79-82 are now met; the opt-in is removed together with the first client that launches the broker, since no client passes it today.

Revision 84 gives clients a way to reach the broker, and removes the opt-in. Protocol v1 - frame and field codecs, typed requests and responses, the shared stop/operation/refusal types, token identity, the pipe client and a new launcher - moves into `InterCat.CaptureBroker.Protocol`, which depends only on Domain, so the ordinary-integrity viewer can speak the protocol without referencing the privileged runtime or the ETW adapter. `ContentCaptureRequest`, its two modes and `ProviderProcessScope` move to Domain as plain values. The codec now checks structure only; which profiles exist and what each admits is the broker's installed catalog, checked by `BrokerPrepareRequestPolicy` right after decoding, with the same `InvalidRequest` refusal. `WindowsBrokerLauncher` starts the broker through ShellExecute `runas` with the caller's own SID and logon-session LUID and a fresh instance, keeps the process handle so the PID it checks cannot be reused, waits for the pipe while watching for an early exit, and connects only when the pipe server is that process. Every failure is a designed state with its own sentence: broker not installed, elevation declined (nothing started), launch failed, broker exited with a documented code, not listening in time, or served by an impostor. With the gates of revisions 79-83 met and a real client path in place, `serve` no longer requires `--enable-unqualified-live-capture`. Real-ETW qualification now also runs the launcher path end to end (`bench/results/broker-qualification-20260923T205911Z`).

Revision 85 delivers live capture from an ordinary-integrity prompt, `icat capture`, and fixes three defects the work exposed.

The command launches the broker (Windows asks for approval), prepares the explore or focused-transport profile with `Live` publication and prints the effective summary. It starts the capture, reads where the broker publishes the evidence, and follows that evidence into a new session of the user's own, renewing the owner lease as it goes. It ends at the duration or after Ctrl+C, which requests the stop and then derives everything published, and reports what was kept. The broker now states the evidence directory in status (field 13, optional), so a client never hard-codes the root layout. A test proves an ordinary-integrity follower derives from protected evidence without changing a byte of it. The follow ends on the broker reporting the capture closed rather than on the finalization marker alone, because an interrupted capture never writes one.

The three defects:

1. `SessionStore` wrote manifests and pointers through unmarked staging files, so a writer killed mid-publication left a file no cleanup could prove abandoned. Qualification caught one after a kill. Every staging file now carries an ownership marker.
2. A client cannot rediscover a broker it did not launch, yet brokers idled for five minutes holding the machine's ownership log. A second capture in that window crashed the new broker. Brokers now idle-exit after 10 s with nobody connected and nothing recording. A new broker waits up to 15 s for a finishing predecessor, then exits with code 6 ("another live capture is running") instead of crashing.
3. An unmapped failure now ends with a sentence and exit code 70, never an unexplained crash.

One live capture per machine is now an explicit v1 limit. Serving several clients from one broker needs a discoverable, server-authenticated endpoint, which is an M2 question and not addressed here. Qualification runs `icat capture` end to end with real ETW (`bench/results/broker-qualification-20260923T211410Z`). The Ctrl+C path of `icat capture` is implemented but not yet exercised by an automated test.

InterCat is a new Windows application for exploring communication between processes: who talks to whom, through which mechanism, when, how often, with what measurable volume, and with what observable contents. Its primary experience is a synchronized communication graph and time visualization, each given equal prominence. Users move fluidly from a whole-machine overview to a process, channel, time interval, operation, and underlying evidence.

This specification is self-contained: it carries every rule, value, enumeration and contract needed to implement InterCat, and depends on no other document, repository, product or prior discussion. Where a principle was demonstrated elsewhere, the principle is restated here with its own rationale and its own numbers rather than referenced.

Reading guide: [binding rules and invariants](#2-binding-engineering-rules-and-data-invariants), [prohibitions](#24-prohibitions), [first run and the detail ladder](#31-first-run-turn-it-on-and-see-the-flow), [product workflows](#3-core-exploration-workflows), [Windows coverage](#4-capture-coverage-and-evidence-contract), [volume semantics](#5-measurement-semantics-what-volume-means), [workspace design](#6-workspace-and-visual-design), [domain model](#7-domain-model-and-identity), [multi-machine analysis](#8-time-and-multi-machine-investigations), [architecture](#9-architecture-and-module-boundaries), [storage and queries](#10-storage-snapshots-and-query-implementation), [payloads](#11-payload-inspection-privacy-and-source-fidelity), [performance](#12-performance-and-responsiveness-targets), [scale at multi-gigabyte sessions](#121-session-size-tiers-and-scale-invariants), [verification](#13-verification-strategy), [v1 milestones M0–M5](#14-implementation-milestones-and-release-gates), [post-v1 milestones M6–M13](#15-full-post-v1-implementation-milestones), [risks](#16-risk-register-and-decision-rules), [open choices](#17-remaining-conceptual-choices-to-revisit-after-the-prototype).

Implementation detail: [capture and replay contracts](#18-capture-and-replay-implementation-contracts), [analysis and rendering contracts](#19-analysis-and-rendering-implementation-contracts), [storage and operational contracts](#20-storage-and-operational-implementation-contracts), [acceptance examples and review findings](#21-executable-acceptance-specification-and-review-findings). Reference material: [enumerations and wire codes](#23-normative-enumerations-and-wire-codes), [version axes and invalidation](#24-version-axes-and-invalidation), [non-goals and definition of done](#25-non-goals-and-definition-of-done), [solution layout and settings](#26-solution-layout-settings-and-operational-defaults), [glossary](#22-terminology-and-reference-notes), [illustrative manifest](#27-appendix-a-illustrative-session-manifest), [illustrative analysis specification](#28-appendix-b-illustrative-analysis-specification).

Sections 18–28 make the earlier architecture concrete; they are part of the specification, not optional commentary. Section 1.4 states how requirement words and identifiers in this document bind an implementation.

### 1.1 Confirmed product choices

| Decision | Requirement |
|---|---|
| Main use case | System exploration: discover who communicates with whom across Windows. Performance diagnosis is a supporting workflow; threat detection is not the organizing concept. |
| Capture | Driverless first. Optional deeper collectors and application instrumentation are later extensions. |
| Machine scope | Capture locally; import and correlate captures from multiple machines in one analysis workspace. No remote deployment or fleet control is required for the first release. |
| Main workspace | Timeline and graph have equal visual prominence, with linked selection, filters, and a ranked process/channel table. |
| Payloads | Include explicit opt-in inspection wherever a source exposes contents. Absence of contents remains an ordinary supported state. |
| Visual fundamentals | Preserve responsive timeline aggregation, pointer-focused zoom, pinch, panning, overview/minimap, reversible navigation, and exact evidence drill-down. |
| Post-v1 priority | Broader Windows IPC coverage and deeper visibility lead the roadmap, including optional collectors where they add proven value. |
| Deep application visibility | Support both cooperative SDK instrumentation and optional attachment to selected unmodified processes; deliver the SDK first. |

### 1.2 Meaning of whole-system visibility

“Whole-system” means system-wide discovery across supported IPC mechanisms and accessible processes. It does **not** mean that Windows offers a universal, lossless feed of every message, memory write, endpoint, or payload. Coverage depends on Windows build, provider schemas, privileges, capture settings, and event loss.

The product must always distinguish:

1. **Observed activity:** a source directly recorded an operation or lifecycle event.
2. **Correlated relationship:** multiple observations were linked by a documented rule.
3. **Discovered resource:** an endpoint, handle, or mapping exists; traffic may be unobserved.
4. **Unknown or unavailable:** information was not collected, not exposed, lost, or ambiguous.

An empty timeline cannot by itself establish that no IPC occurred. A mapped section cannot establish that bytes were exchanged. A temporal coincidence cannot establish a causal chain.

### 1.3 Delivery defaults, toolchain and supported builds

Use a new repository and solution. Target Windows x64 first; validate native ARM64 separately after the first complete vertical slice. Introduce native code only for a measured interoperability or throughput need. Offline viewing runs without elevation; system capture uses a narrowly privileged broker.

Stack: C#/.NET for domain, query engine, desktop, and capture orchestration; Avalonia with custom Skia drawing for timeline and graph; Microsoft TraceEvent behind a capture adapter, supplemented by Windows TDH metadata decoding.

Pin the following before the first production commit and record the choice and upgrade policy in ADR-001. “Pinned” means a failing build rather than a silent upgrade:

| Item | Initial pin | Policy |
|---|---|---|
| .NET SDK | .NET SDK 10.0.401, pinned in `global.json` with roll-forward disabled | Move only on an LTS boundary, through ADR-001 |
| Language and compiler settings | C# latest of the pinned SDK; nullable reference types enabled and warnings as errors solution-wide | No per-project suppression without a recorded reason |
| UI framework | Avalonia 11.3.22, centrally pinned; custom drawing uses Avalonia's Skia-backed rendering path | A major-line change is an ADR |
| ETW adapter | Microsoft TraceEvent 3.2.6, centrally pinned and referenced only by `InterCat.Capture.Windows` | Replaceable by a native TDH consumer if M0 fixtures show a semantic or throughput gap (§18.3). M0 measured an allocation gap on the real-time path; IC-019 evaluates the replacement (ADR-009) |
| Static analysis | Compiler analyzers plus an architecture fitness test enforcing §9's dependency direction (R19) | A failing fitness test blocks the build |

Supported build candidates for the first release, revisable by ADR-007 once M0 measures them. A build is supported only when its fixture corpus (§13.4) passes on it; every other build is reported as untested, never as probably working:

| Tier | Builds | Meaning |
|---|---|---|
| Primary | Windows 11 25H2 x64 (26200, and the 26220 pre-release branch); Windows 11 24H2 x64 (26100) | Development target and per-pull-request CI gate |
| Secondary | Windows 11 23H2 x64; Windows Server 2025 x64 | Fixture corpus maintained; release gate |
| Candidate | Windows Server 2022 x64; Windows 11 24H2 ARM64 | Qualified in M13; may ship as a separate edition |

Support named builds from this matrix, not a blanket “Windows 10+” assertion. A release is listed by its exact build numbers rather than by a range, because a range would silently accept a build nobody has run the fixture corpus on. Where a pre-release servicing branch of a supported release is listed, it is supported on the same terms and every result states which of the two it was measured on: "supported" alone would hide the difference between a retail build and a branch that can still change under it. ADR-007 owns this matrix and records why each entry is on it.

### 1.4 How this document binds an implementation

Requirement words have one meaning throughout:

| Word | Meaning |
|---|---|
| **must**, **must not** | A contract. Code, fixtures and gates depend on it; changing it requires an ADR |
| **should** | Strong default. A deviation is recorded in the owning module's ADR with its reason |
| **may** | Genuine latitude for the implementer |
| `TUNABLE:` | A named starting value to be benchmarked, not a contract. Measurement can change the value; it cannot remove the surrounding rule, and the value lives as a recorded profile setting rather than a literal |

Identifiers exist so that code, tests and gates can cite this document. A test asserting a rule names it; a milestone gate lists the rules and fixtures it verifies. A gate that cannot name them is not a gate.

| Prefix | Meaning | Defined in |
|---|---|---|
| `R<n>` | Binding engineering rule | §2.1 |
| `I<n>` | Product and data invariant | §2.2 |
| `P<n>` | Prohibition: a blocking defect if found | §2.4 |
| `S<n>` | Scale invariant | §12.1 |
| `EN-<Name>` | Normative enumeration and its wire codes | §23 |
| `IC-<n>` | Implementation backlog item | §14.1 |
| `FX-<mechanism>-<nnn>` | Validation fixture | §13.5 |
| `ADR-<n>` | Architecture decision record | §16 |

Units: storage, memory, buffer and message sizes use IEC binary units (KiB, MiB, GiB). Rates, frequencies and durations use SI units; a decimal byte rate is written MB/s and a binary one MiB/s, and the two are never interchanged. Every displayed, exported and logged measurement carries its unit and its semantic domain (§5); no number reaches a user, a report or a machine-readable result without both (R2). Displayed numbers and dates use the current user locale, with a thousands separator on counts and at most three significant decimals on rates; machine-readable output is locale-independent and uses the invariant forms of §10.5 and §20.5. Wall-clock times state their time base and offset (§6.2); a time without one is never displayed.

## 2. Binding engineering rules and data invariants

These are the contracts every module inherits. Each rule states the failure it prevents, because a rule whose failure mode is unstated gets negotiated away under schedule pressure. Rules constrain how code is written; invariants constrain what the data may ever be. Both are cited at the point of implementation and named by the tests covering them (§1.4).

### 2.1 Binding engineering rules

| ID | Rule | Failure prevented |
|---|---|---|
| R1 | Source-derived facts are immutable. Resolved identities, correlations and derived metrics live in versioned derived tables, never in writable observation columns | Evidence rewritten behind a reader; irreproducible snapshots |
| R2 | Every measurement carries value-or-null, unit, semantic domain, observation side, source and quality | An unexplained single “volume” number |
| R3 | Null is unknown and zero is an observed zero. No missing measurement is defaulted, substituted, interpolated or scaled into existence | Fabricated totals and invented traffic |
| R4 | Every relationship carries its rule identity, rule version, evidence IDs and strength | A convincing graph edge nobody can explain |
| R5 | One canonical enumeration and one display mapping per semantic dimension, defined in §23 | Two views disagreeing about what a mechanism or a quality level is |
| R6 | All panes answer from one published analysis bundle identified by §10.5's query identity | Old counts shown beside new filters |
| R7 | A result whose identity has become obsolete is discarded, never merged or partially applied | Late work overwriting newer intent |
| R8 | Every queue, cache, index, pending-join table, layout pass, spill file and retention policy is bounded, and exhaustion is a reported state | Memory explosion; silent invisible loss |
| R9 | The capture callback path performs bounded admission and copying only: no queries, name resolution, symbol lookup, decoding beyond a descriptor and shape check, unpooled allocation, or blocking I/O | Machine-wide instability caused by the observer |
| R10 | Interaction and bucketing math is pure, integer-based in the time domain, and property-tested independently of the UI | Pointer drift, hit-test error, records lost at cell boundaries |
| R11 | No allocation, boxing, LINQ, reflection or logging inside paint, aggregate, admission or decode loops | Diagnostics and convenience becoming the bottleneck |
| R12 | Drawing consumes immutable published numeric results, layout results and transforms only; it performs no storage query and no correlation | Frame stalls; results changing while being drawn |
| R13 | Hit testing resolves against a data-space index. Cosmetic widening changes no interval, no count, and not the evidence a mark resolves to | A mark reporting a time or a record it does not represent |
| R14 | No meaning is carried by color alone; every channel assignment in §6.6 has a redundant encoding | Unreadable under color-vision differences, high contrast or print |
| R15 | Every canvas affordance has a keyboard path and an accessible table equivalent yielding the same result set | A product usable only with a mouse and full vision |
| R16 | Privileged work is confined to the broker's allowlisted capture operations. Parsing imports, decoding content, drawing, querying and exporting never require elevation | A privileged parser or decoder as attack surface |
| R17 | Content admission is decided per record, by policy identity, before persistence. A view filter is never a substitute for not retaining bytes | Sensitive content retained and then merely hidden |
| R18 | The core runs headlessly. Every analysis result in the UI is reachable from the CLI through the same specification and yields the same machine-readable values | UI-only validation; untestable analysis semantics |
| R19 | Platform adapters stay outside the domain, analysis and query modules, which carry no Windows or UI dependency and whose tests run without either | Interop and UI assumptions leaking into analysis semantics |
| R20 | Every derived artifact records the version axes it was produced from (§24) and is rebuildable from retained raw evidence alone | Derived state that cannot be reproduced or correctly invalidated |
| R21 | Coverage is stated independently from data. Absence of observations is never rendered, exported, summed or ranked as observed zero activity | A quiet channel and an unobserved channel looking identical |
| R22 | No identity is established by a reusable numeric value alone: PID, TID, port, handle, object address or name | Independent instances merged into one convincing entity |

### 2.2 Product and data invariants

- **I1** A raw-record key is unique within its capture and stable across every re-read, replay and re-import of that capture.
- **I2** One raw record may yield several observations; each has a deterministic fact key, and the set is reproducible from the same record, saved schema and normalizer version.
- **I3** Every time interval is half-open: `[startInclusive, endExclusive)`, and its length is a tick count: an end more than 2^63 - 1 ticks after its start is no interval, refused where it is read rather than overflowing where it is measured.
- **I4** For a declared boundary set, each eligible point observation belongs to exactly one cell.
- **I5** A cell's count equals the number of detail records returned for that cell under the same analysis specification and snapshot.
- **I6** A byte sum covers exactly one byte domain and one accounting side; unknown values are counted as unknown and never coerced to zero.
- **I7** Source acquisition order and chronological display order are recorded separately and never conflated.
- **I8** Original timestamp encoding, source clock identity, session-relative time and workspace-aligned time remain distinguishable at every layer.
- **I9** Manual alignment, host aliasing, accepted candidate joins and user annotations never rewrite a source timestamp or a source field.
- **I10** A locally measured duration is unchanged by any cross-host alignment revision.
- **I11** Adding a higher-layer annotation to existing evidence changes no transport-layer metric.
- **I12** Instance epochs make every reusable identifier non-merging: two independently proven instances never collapse into one.
- **I13** Every acquired record is attributable to an admitted observation, a recorded policy omission, or a recorded loss or undecodable counter.
- **I14** Re-importing the same bytes with the same versions and retention policy yields equivalent facts and identical normalized IDs, independent of worker count.
- **I15** A published snapshot contains all data and relations at its declared generation; a partially published generation is never visible.
- **I16** Every query result names the snapshot vector, revisions and analysis specification it answers.
- **I17** Late evidence creates a new revision. Earlier snapshots remain reproducible and their identities unchanged.
- **I18** A pinned or open evidence reference is never invalidated by retention, compaction or export.
- **I19** No viewport operation changes stored observations, capture policy, or the size of the application window.
- **I20** An operation open at a capture or retention boundary is censored, not failed; a missing completion is never a timeout unless a source reported one.
- **I21** Every retained content fragment records classification, direction, retained length, original length when exposed, truncation state and its admission policy identity.
- **I22** A redacted export contains no original payload bytes, no reference that resolves to them, and no embedded unredacted source.

### 2.3 Approaches explicitly excluded

Severity or log-level taxonomies as an organizing dimension; text mining of message bodies as a primary analysis; the assumption that every fact is a point record owned by exactly one process; `sender PID / receiver PID / bytes` as a universal row shape; a single scalar confidence percentage; and any performance figure not measured against InterCat's own workloads on the reference machine of §12.

The rules and invariants above are not novel. Each was validated in a shipped desktop application of comparable interaction and storage scale, and each is restated here with the failure it prevents so that this document remains the only source required to implement InterCat. If any external implementation is reused later, retain its applicable license notices and re-verify its invariants against this domain.

### 2.4 Prohibitions

These are the specific mistakes this design exists to prevent. They restate, in one place, what the rest of this document forbids in context, so that a reviewer or an implementer has a single page of “what not to do”. A review or test that finds one reports a blocking defect rather than a preference. The last column names the contract that forbids it and the section that specifies the correct behavior, which is also what a test asserting the prohibition must cite (§13.5).

| ID | Never | Enforced by |
|---|---|---|
| P1 | Render, export, sum or rank absence of data as observed zero activity | R21, I13 |
| P2 | Substitute, interpolate, pad or scale a missing measurement — including padding absent content bytes to present a complete message | R3 |
| P3 | Sum or rank two byte domains together, or relabel requested bytes as transferred bytes | I6, §5.3 |
| P4 | Count one exchange twice by treating transport evidence and its logical operation as independent communications | §5.1 |
| P5 | Deduplicate observations by equal timestamp and size, or fold retransmissions into unique delivered payload | §5.1 |
| P6 | Establish identity from a reusable numeric value alone, from an equal name, or from the same executable path | R22, I12 |
| P7 | Pair every client with every server sharing a name, or invent a peer to complete an edge | §7.4 |
| P8 | Present time proximity as causality, or a shared activity identifier as simultaneity | §7.4, §8.2 |
| P9 | State a cross-host order, one-way latency or causal chain that the combined uncertainty does not support | I10, §8.2 |
| P10 | Rewrite, clamp or re-timestamp a source record, or let an alignment revision change a locally measured duration | I9, I10 |
| P11 | Collapse the four quality dimensions into one confidence percentage | R2, §7.3 |
| P12 | Run a query, name resolution, symbol lookup, unpooled allocation or blocking I/O on the capture callback path | R9, §9.3 |
| P13 | Silently disable a provider, switch to sampling, or change an effective profile without opening a new coverage epoch | §9.3 |
| P14 | Take over a session because its name resembles InterCat's, or stop another tool's session to make room | §9.2, §18.2 |
| P15 | Retain content the active profile did not admit and rely on a view filter to hide it | R17, §18.2 |
| P16 | Place payload bytes, endpoint strings or command lines into logs, telemetry, crash diagnostics, search snippets or support bundles | §11.1, §20.6 |
| P17 | Ship an original unredacted source inside a package labeled redacted | I22, §11.3 |
| P18 | Parse an imported archive, decode content, or draw UI inside the elevated broker | R16 |
| P19 | Accept a PID, a nonce or a stored session name as authentication or as proof of ownership | R22, §20.3 |
| P20 | Connect to an unknown application pipe, read another process's memory, or duplicate a handle merely to resolve a peer | §4.4 |
| P21 | Publish a result whose identity is obsolete, or mix one pane's old numbers with another's new filter | R6, R7, I15 |
| P22 | Let stale or cosmetically widened geometry answer a hit test, or let a drawn width change a reported interval | R13, §6.2 |
| P23 | Block input, clear a populated view, or raise a modal because analysis work is in flight | R12, §6.8 |
| P24 | Carry a meaning on color alone, or reuse the coverage hatch or the unknown grey for a supported mechanism | R14, §6.6 |
| P25 | Scan a whole session on an interactive path, or recompute a whole-session overview on reopen | S3, S4 |
| P26 | Export or report a coarse preview as an exact result | §19.3 |
| P27 | Promote a mechanism's tier, or claim a supported build, without its fixture evidence | §14.2 |
| P28 | Enable a payload-producing debug keyword, a symbol download or a reverse DNS lookup by default | §4.2, §19.5 |

## 3. Core exploration workflows

### 3.1 First run: turn it on and see the flow

The product's first promise is that a new user launches InterCat, does nothing else, and watches the machine's IPC appear. That promise is a specification, not a slogan, so first run is defined step by step with the required state at each step.

| Step | The user does | What must be on screen, and when |
|---|---|---|
| 1 | Launches InterCat | The workspace itself, not a wizard and not a modal: both panes in their designed empty state, a single primary `Start exploring` action already focused, the Explore profile preselected, one line stating what Explore collects and what it does not, and the sessions the user saved, newest first, where the ranked table will be. First paint under TUNABLE: 1 s |
| 2 | Presses `Start exploring` or `Ctrl`+`R` | At most one elevation prompt, preceded by one sentence naming what is elevated and why. Nothing else is asked: no profile editor, no provider list, no destination dialog. The broker writes raw evidence to its protected directory; an ordinary-integrity follower derives a default session under the user's local application data (ADR-027) |
| 3 | Waits | Capability and coverage state appear immediately. Lifecycle inventory populates process nodes before traffic arrives, so the graph is never blank while the machine is visibly busy. First useful overview inside §12's 3 s budget, with the health strip naming whatever is still starting |
| 4 | Watches | A live L0 overview (§3.2): mechanism lanes in the timeline, host and process clusters in the graph, the ranked table filling, follow-latest on. Nothing needs configuring for this view to be correct |
| 5 | Sees something interesting | One gesture descends one rung of §3.2's ladder. Selection, breadcrumb and time context follow; nothing resets |
| 6 | Stops, or leaves it running | `Stop` finalizes and reopens the same view over the finished session. Closing the window while recording asks once, states the consequence, and never silently discards a session. A viewer that crashes loses no evidence: the broker stops the capture when the owner lease lapses and keeps it finalized, and the next launch offers to finish deriving the session from it |

Every first-run default must be correct without a user choice: Explore profile; mechanism-overview lanes; process-to-process graph with resource hubs for ambiguous channels; the `Observations` metric with its transport breakdown; analysis scope following the viewport; evidence policy `IncludeCorrelated`; follow-latest on; ranking by observed activity. Each is visible and changeable, and none must be touched to reach step 4.

If capture cannot start, the empty state names the specific thing that failed, what remains possible — opening a session, importing a trace — and the one action that would fix it. A refused elevation is an expected outcome with a designed state, not an error dialog (§6.8).

Revision 117 repairs a real first-run blocker: the broker parsed all of ProgramData's inherited ACEs merely to decide whether its parent owner was trusted, and rejected a standard Windows `DCLCRPCR` rendering before opening its own root. Parent trust now reads only the owner's SID; the broker's own root still requires the complete strict DACL and mandatory-label read-back. An elevated CLI Explore capture subsequently finalized on the affected host. If startup still fails, the Desktop places the persistent reason and retry action together, keeps long reasons scrollable, and avoids suggesting deletion of protected evidence as a generic fix. An already-elevated caller need not see another UAC prompt. This corrects the blocker and refusal path, not the still-open lane and first-feedback gates.

Revision 118 qualifies that path in Desktop on the affected host. A live screenshot at generation 35 showed process and network observations but no admitted paired TCP graph edge and no coverage ledger yet; neither state means the capture is blank or losing events. Live counters are provisional. On stop, the coverage ledger was published and two edges became admitted in the saved session. The legend now says hatching can mean *unknown* coverage as well as a gap, the live loss sentence says final coverage is pending, and a zero-edge graph header points to observed records in the timeline. The graph must continue refusing guessed peers; clearer copy does not widen its relation evidence policy.

Revision 86 clarifies the directory wording in step 2 after ADR-027: the broker-owned directory is the protected raw-evidence source, not a viewer-writable analysis session. The automatic user-owned derived session is still one action and needs no destination prompt. At revision 86, the Desktop implementation showed a published L0-L2 overview; its deeper rungs were explicitly unavailable. This staged implementation did not satisfy the full first-run exit gate, nor was the three-second feedback budget measured.

Revision 143 implements step 6's offer. While the Desktop follows a capture it holds a ticket beside the session directory (`contracts/live-follow-v1.md`). The ticket names the broker's evidence and the owner lease's expiry at the last renewal, and it is removed once the session holds everything the capture published. The next launch looks for tickets no follow holds. It offers the newest in the rail, in a card above the Explore card that never takes first run's focus. The card is in one of four states, and forgetting a capture removes only its ticket:

- **Finishable:** the capture was finalized and chunks are missing. The card says how many, and that finishing records nothing again.
- **Still stopping:** the capture is unfinalized and its lease has not settled. The card waits and looks again.
- **Ended unfinalized:** the lease settled but no finalization came, as when the broker or the machine stopped first. The card says that records after the last publication were never kept, and offers to save what was published.
- **Evidence gone:** the card opens what the session kept.

The card's rechecks and the user's own actions each look at the folder, and only the latest look's answer is shown. Before revision 150, a recheck that began before the user forgot a capture could finish after the forget and show its card again for five seconds.

Revision 153 gives `icat capture` the same ticket. Before it, a command-line capture that ended early left no note of where its evidence was, and it prints the evidence directory only when it finishes. So the user could not even run `icat follow <evidence> <session>` by hand. Now `icat follow <session>` finishes the capture beside a session from its ticket, wherever the session is. The Desktop's next launch offers one in its session folder, as before. A real-ETW run killed `icat capture` after it had followed 8 chunks. `icat follow` then derived the 15 chunks published so far and kept the ticket while the capture stopped. Once the lease settled, it finished all 27 chunks with their finality, removed the ticket, and left no ETW session behind.

A viewer that crashes while recording loses more than the chunks it had not yet followed: the broker keeps recording until the lease lapses, and only the finish recovers that tail. A real-ETW scenario (`crashed-viewer`) kills a Desktop capture runner mid-capture and finishes its session as the next launch does. In it, a viewer that had followed 6 chunks left 14 more for the finish.

Revision 145 closes a gap in step 1. Every capture saves a session in the user's folder, but reopening one meant finding that folder in a picker. While no session is open, the rail now lists the eight most recent sessions where the ranked table will be, newest first. Each row says when it was saved, how many records it holds, how large it is, and whether its follow ended before the session was finished. Listing reads only each session's current manifest and its segments' fixed-size headers, never verifying or hashing a session; opening one verifies it in full, as before. Enter or a double click opens a row. A running capture hides the list with the other actions that wait for it to end. *Open saved session* now starts its picker in that folder.

### 3.2 Levels of detail: from the whole machine to one record

One ladder, the same gestures at every rung, and the current position always visible. `Enter` or double-click descends; `Esc` or `Alt`+`Left` ascends to exactly where the user was, and `Alt`+`Right` goes forward again to the rung an ascent or a crumb left (§6.4). The ladder behaves identically on a live capture and a finished session.

| Level | Timeline | Graph | Ranked table | Descend by |
|---|---|---|---|---|
| L0 Machine | One lane per mechanism, density cells across the retained extent | Host and group clusters with aggregate edges | Mechanisms and top groups | Selecting a lane, cluster or interval |
| L1 Group | One lane per process group: executable, service container, session | Groups expanded to member clusters within a bounded neighborhood | Groups and their peers | Selecting a group |
| L2 Process instance | The instance's records, one row per source direction (`EN-Direction`), under a machine-context row | The instance, its peers one hop out, context nodes for explanation | Peers and channels of that instance | Selecting an instance |
| L3 Channel | One lane per end of the channel, named by its holder and its own endpoint, banded by source direction around its midline, under a machine-context row | The channel's participants and its resource hub | Channels, endpoints, operations | Selecting a channel or edge |
| L4 Operation | Individual operations with duration bars and status | Only the participants of the selected operation | Operations with their measurements | Selecting an operation |
| L5 Evidence | Individual source observations as marks | Unchanged, with the contributing edge highlighted | Source records, keyset-paged | Selecting a mark |

**The graph follows the rung.** From L1 down, the graph draws the rung's neighbourhood and counts every other process in one context node, **Rest of the machine** (§6.3). L1 draws the opened group's members and every process one relationship away from them. L2 draws the instance and its peers. L3 and L4 draw the channel's two participants. Real sessions admit paired TCP only, so no resource hub is drawn yet. The hub column applies once a pipe or section mechanism is admitted. L5 keeps the graph of the rung it was reached from.

**The timeline follows the rung too.** L0 draws mechanism lanes as of revision 119, L1 draws exact
process-owner lanes as of revision 122, L2 draws exact source-direction rows as of revision 123, and L3 draws each
channel end banded by direction as of revision 124. Every deeper rung retains an exact focus count for the records E
reads:

- L1: the records the group's members own.
- L2: the records the instance owns.
- L3 and L4: the records of the channel's two ends.
- L5: the evidence scope.

Every observed record stays behind as grey context on the same rate scale, so the colour shows when the focus was active against the whole machine. The focus is counted in the same pass and on the same columns as the whole timeline, so a colour bar never exceeds its grey bar. The interval table gives each window's focus count beside its own. A focus the generation cannot resolve is named with its reason, and every record is then drawn in its hue. A live refresh keeps the previous counts on screen until its own arrive, as the graph keeps its layout.

The graph header names the focus and says "and its peers" only when the focus has any. It then gives how many processes are drawn, "as N nodes" when some of them are folded, and how many the context node counts. A relationship between a drawn process and the context node is drawn faded. The context node is parked below the neighbourhood, and its edges do not move anything, so the focus keeps its own shape. Ascending restores the wider rung's graph and its layout.

Revision 116 prepares L0's mechanism lanes without another source scan: the overview's existing mechanism tally
publishes one exact bucket series per observed mechanism, and zoomed detail publishes the same split on its own
columns. The series partition each whole-timeline bucket; rows without session time belong to neither. A lane's
capture-coverage state is computed for its mechanism, including empty buckets, not borrowed from the whole bucket's
dominant hue. Revision 119 draws those series as L0 rows on one shared visible rate scale, with an occupied-floor
height, a separate coverage treatment per row, exact hover and click targets, and a scrollable label gutter with
keyboard lane scrolling. Zoomed detail replaces the overview columns for all mechanisms only when its lane set is
complete; a partial carried result never mixes two peak scales. L1–L5 lane derivation and individual-lane
accessibility/organization are not implied by this L0 result. Revision 120 makes an L0 row a persistent mechanism
focus by clicking its name or choosing it from the keyboard-addressable table selector. The table then lists that
lane's exact buckets and coverage (zoomed where complete); bracket stepping skips empty buckets in that lane. A lane
focus survives same-session publications by mechanism identity; a mechanism no longer observed is reported and the
focus returns to All mechanisms. Lane choice does not filter the graph or ranked counts. The
screen-reader/UI Automation audit and scale controls remain open.

Revision 121 prepares L1 multi-instance process rows from the same leased scan as the focused group count. The
row key is the canonical process-instance ID, not its current display position. Each admitted, timed record goes
to exactly one owner row, so rows partition the group's focus bucket by bucket; each row computes capture
coverage from its own observed mechanisms and leaves an empty bucket unknown. Query allocation is bounded at
200 rows and 20,000 cells, both checked before row models are allocated. If either bound is exceeded, the exact
aggregate remains and an explicit reason accompanies the absent rows. The measured 96-process saved Explore
group fits and partitions 137 focused records. The Desktop carries these rows across a same-focus publication,
and revision 122 renders and interacts with them. L1 lane virtualization and the higher-rung semantics remain open.

Revision 122 draws L1's owner rows and a separately labelled machine-context row on the same peak rate scale.
Rows retain their own coverage and stable process-instance identity. Hover gives the canonical owner, exact bucket
and count; clicking a row's label selects its process, and selecting a process from the ranked table or graph brings
its row into view without changing the time viewport. The interval table then lists that owner's exact buckets,
with Group totals returning to the aggregate; bracket keys step through only its occupied buckets. Zoomed detail
replaces the group and context columns together only when both match the viewport. An over-budget focus retains
the truthful aggregate and a refusal reason instead of implying that omitted owners had zero activity. L2–L5,
pinning/search/virtualization for very large lane sets, and the screen-reader/UI Automation audit remain open.

Revision 123 splits L2's focus into one row per `EN-Direction` code, counted in the same leased pass as the focus:
Outbound, Inbound, Bidirectional, Unknown direction and No data direction, under the machine-context row. The rows
partition the instance's admitted, timed records. Since revision 197 each row's coverage is the capture's, as a process
lane's is (revision 165): the row splits one process's records, and that process could have made any record the capture
collects. All five rows are always drawn, so no row moves under a zoom; a row with nothing drawn is labelled "none", so
a coverage hatch on it is not read as records. A row holds each record's **source-catalog** direction (§23): a connection attempt
and an RPC client call are outbound, an accept and an RPC server call inbound, and lifecycle records and disconnects
have none. The row therefore says which way a record went as its source marks it. It says neither who initiated
the conversation nor which way bytes moved: `between(A,B)`'s data direction (§19.1) remains send and receive only.
A record whose source stated no direction keeps its own row and is never guessed into one. Hover gives the row, its
direction word (§6.6), and the instance's count across all rows. A row's name, or the tables' direction selector,
makes that row the interval table's and `[`/`]`'s focus; the machine row's name returns to all directions. The
choice survives a same-session publication, and it does not filter the graph or the ranked table. L3–L5 lanes and
the screen-reader/UI Automation audit remain open.

Revision 124 gives L3 one lane per end of the focused paired channel, under the machine-context row. A record's end
is decided by its own endpoint, not by who holds it, so a process connected to itself over loopback still has two
ends. The ends partition the channel's records bucket by bucket. Each end is named by its holder and its own endpoint.
Its outbound records rise above the lane's midline and its inbound records fall below, on the shared rate scale, so
the channel reads as a conversation (§6.6's band side). A record with no data direction, such as a disconnect, is a
neutral mark on the midline and never a bar on either side. An end can hold only the channel's mechanism, so its
coverage is judged on that mechanism even in an empty bucket, as an L0 lane's is: a quiet interval the capture
covered reads as observed-empty, and a gap is still hatched. The aggregate focus, which judges only what it observed,
still calls an empty bucket unknown. Hover gives the end, the bucket's outbound, inbound and undirected counts with
where each is drawn, and the channel's count at both ends. An end's name, or the tables' end selector, scopes the
interval table and `[`/`]`. The machine row's name returns to both ends. The choice survives a same-session
publication. It is forgotten when the focus moves to another channel, because the first end of one channel says
nothing about another. The evidence rung keeps the channel's count but draws no end lanes. L4 has no lanes while
real sessions project no logical operations (§3.2's table), and L5 keeps its evidence marks.

Ladder invariants, each property-tested:

- **One gesture per rung.** Descending and ascending never require a menu, a mode switch, or a hand-typed query.
- **Position is always stated.** A breadcrumb names the level and the selection at each rung, and the effective time range, basis and metric stay on screen at every level (§6.4).
- **Reversible.** Ascending restores the previous viewport, selection, lane grouping and graph focus as one navigation state. The user never loses their place (§6.7).
- **The same numbers.** A level change alters grouping and lane composition, never eligibility: an L1 total is the sum of its L2 constituents under the same specification, and any difference has a named denominator (§19.1). A sum is only available where the rung's rows partition it. Rows attributed to a canonical owner do partition; rows attributed to endpoint activity do not, because a channel belongs to both of its participants. A rung whose rows overlap states its accounting side and reports no single total, rather than printing a number that counts the same observations twice (§5.1, `EN-AccountingSide`).
- **A level is not a filter.** Descending sets scope and grouping; it never silently adds a predicate. Where a descent does imply a filter, that filter appears in the filter bar where it can be seen and removed.
- **Evidence is always one step away.** Every rung reaches L5 in at most one step from its selection, because “show me the actual records” is the question the product exists to answer. That step has its own gesture at every rung, and it carries the rung's scope into the filter bar so the jump is visible rather than implicit.
- **No dead ends.** A level with no data for a selection names the source that would supply it (§4.3) instead of showing an empty pane.

### 3.3 Discover the machine

Start a short Explore capture. Within seconds, the graph shows processes and observed channels; the timeline shows activity by transport. Select a process to highlight its incoming/outgoing relations. Expand one hop, pin relevant nodes, group by executable or service host, and inspect endpoint names and evidence quality. Switch lanes to process or channel and sort the ranked table by activity or measured bytes.

Discovery must remain useful even when peer resolution is incomplete: render `process -> unresolved pipe instance`, rather than omit the event or invent another process.

### 3.4 Explain a burst

Brush an interval in the timeline. The graph and ranking adopt that analysis interval. Sort by observed bytes sent, or operations if bytes are unavailable. Select a channel; inspect its operations and exact source events. Zoom to distinguish a sustained flow, short burst, retry sequence, or repeated RPC operation. Pin the result and return to the full overview without losing the selected entity.

### 3.5 Understand a shared resource

Find a section mapped by multiple processes. Display a section node with membership edges and known mapping lifetimes. Show that this proves shared access, not message traffic. Display size as capacity, with traffic marked unavailable unless a suitable source supplies actual transfer observations.

### 3.6 Follow a relationship across machines

Import two independently recorded captures into an investigation. Assign or verify host identities, inspect clock alignment, and correlate compatible connection observations. Show the relationship between host A's client process and host B's server process only at the evidence strength available. Allow uncertain matches to remain candidates. Local RPC durations remain useful even when network one-way latency cannot be established.

### 3.7 Inspect an available payload

Select an operation with content evidence. Inspect bounded hex/text previews, encoding, direction, source, original length if known, captured length, truncation, and reassembly state. An RPC fragment is initially a fragment, not a decoded function argument. Explicitly request decoding or export; keep binary contents inert. For unavailable content, show the precise reason and which future capture source could provide it, if known.

## 4. Capture coverage and evidence contract

### 4.1 Required capability matrix

Every adapter publishes capabilities per tested OS build and profile. The following table is a design target and feasibility backlog, not a claim that each Windows provider exposes every field.

| Mechanism | Driverless starting point | Intended first-release result | Boundaries and validation needs |
|---|---|---|---|
| Process/thread lifecycle | Kernel lifecycle events, rundown where supported, bounded startup inventory | Process instances, thread ownership, names, lifetimes | Starting mid-run, protected metadata, PID/TID reuse, and missing lifecycle events need explicit states |
| TCP IPv4/IPv6 | Kernel network ETW; bounded IP Helper table snapshots as enrichment | Local owner, connection lifetime, endpoints, source-defined byte observations | Use payload owner fields where appropriate; async event header PID may be unrelated. Loopback, port reuse, reconnect, offload and retransmits need fixtures |
| UDP IPv4/IPv6 | Kernel UDP events and endpoint inventory | Datagram observations and directional endpoint relationships where fields permit | No TCP-style connection lifetime; multicast, reuse and ambiguous recipient ownership must remain visible. A receive names the datagram's sender first, unlike a TCP receive (ADR-019) |
| Unix-domain sockets | Investigate relevant Winsock/AFD schemas and controlled fixtures | Explicit capability result; discovery/activity only when validated | Do not assume TCP/IP tracing covers AF_UNIX or every socket family |
| Named pipes | Candidate kernel file events and name/lifetime correlations; optional existing external evidence import | Pipe resource/activity when validated; peers and bytes only to supported confidence | NPFS coverage is a hard feasibility gate. Equal pipe names do not identify one instance. Reads/writes need completion semantics |
| Anonymous pipes | Candidate file events plus inherited/duplicated resource evidence where available | Resource/operation discovery where validated | Parent/child relationship alone does not establish shared pipe ownership |
| RPC | `Microsoft-Windows-RPC` schemas, activity metadata, client/server fixtures | Interface UUID, operation number, protocol, endpoint, status, local call spans where available | Async, nested, multiplexed and cancelled calls require protocol-specific pairing; remote pairing is separately qualified |
| ALPC | Kernel ALPC send/receive/wait events | Activity, candidate peer pairing and bounded wait observations | Documented send/receive schemas provide MessageID; do not assume payload length, port name, or globally unique IDs |
| Shared memory/sections | Feasibility-gated mapping/object evidence, bounded accessible inventory; later cooperative instrumentation | Resource topology and mapping lifetimes where supported | Ordinary loads/stores are not a universal ETW message stream. Mapping size, committed pages and dirty pages are not transfer volume |
| COM/DCOM/WinRT | RPC plus validated component-specific activation/metadata providers | Higher-level annotation and out-of-process relations when supported | In-process COM is not IPC; activation alone is not proof of subsequent calls; runtime coverage varies |
| Synchronization | Optional context-switch/ready-thread tracing, object evidence, later application markers | Supporting waits and correlations | A blocked thread is not necessarily waiting on IPC; no automatic definitive deadlock diagnosis |
| WM_COPYDATA/window messages, clipboard, DDE, mailslots | Mechanism-specific research or imported instrumentation | Visible unsupported/experimental entries until validated | Do not market one provider as coverage for all legacy/UI IPC |
| Remote pipes/SMB, TLS, QUIC | Underlying socket observations plus validated higher-level adapters | Remote endpoints and available protocol annotations | Transport encryption stays encrypted; SMB multiplexing and QUIC stream ownership need independent evidence |

Windows documents a broad set of IPC mechanisms, including file mapping, pipes, RPC and sockets. This taxonomy is intentionally extensible. [Microsoft IPC overview](https://learn.microsoft.com/en-us/windows/win32/ipc/interprocess-communications).

TCP ETW documents source-specific process attribution and warns against assuming event-header PID/TID identifies the network originator. [TCP/IP event documentation](https://learn.microsoft.com/en-us/windows/win32/etw/tcpip). Documented file read/write `IoSize` is **requested** bytes, so it cannot be relabeled as successful transfer size. [FileIo_ReadWrite](https://learn.microsoft.com/en-us/windows/win32/etw/fileio-readwrite).

ALPC has documented send, receive, and wait events; its documented send/receive payloads expose a message identifier, not a universal content/size contract. [ALPC events](https://learn.microsoft.com/en-us/windows/win32/etw/alpc), [send schema](https://learn.microsoft.com/en-us/windows/win32/etw/alpc-send-message), [receive schema](https://learn.microsoft.com/en-us/windows/win32/etw/alpc-receive-message).

Shared mappings expose memory to processes through mapped pointers. The design inference is that discovering a mapping cannot quantify arbitrary accesses through those pointers. [Creating named shared memory](https://learn.microsoft.com/en-us/windows/win32/memory/creating-named-shared-memory).

### 4.2 Metadata inspection already performed

Read-only provider inspection on Windows build `10.0.26220.0` found `Microsoft-Windows-RPC`, GUID `{6AD52B32-D609-4BE9-AE07-CE8DAE937E39}`. Its local version-1 metadata describes client/server start events 5/6 with interface UUID, procedure number, protocol, network address and endpoint; stop events 7/8 with status; and debug events 10/11 with binary fragments. This is evidence of schemas on one machine, **not** proof of runtime emission, portable event IDs, complete payloads, correlation quality, or safe enablement masks.

Implementation must reproduce the inspection on each supported build, capture synthetic calls, and record which fields actually appear. Do not enable every RPC debug keyword by default. Derive narrowly scoped settings from validated descriptors and store them in the capture manifest.

### 4.3 Source capability descriptor

Required fields: adapter/version; provider identity; supported event descriptors and schema fingerprints; tested build/architecture; required privileges; enablement keywords/levels; available entity, timing, byte and content fields; capture-side filtering support; startup/rundown behavior; documented versus experimental status; validation fixture IDs; measured overhead class.

Each admitted descriptor also declares its **observation layer** (`EN-Layer`) beside its mechanism, kind and direction. The layer decides which metrics may be summed together (§5.1, I11), and it is a fact about the descriptor rather than about the mechanism: one mechanism can carry evidence at more than one layer, so deriving the layer from the mechanism would let a transport metric absorb an application annotation.

Runtime states: `Available`, `Experimental`, `Unsupported`, `PermissionDenied`, `DisabledByProfile`, `SchemaUnknown`, `ProviderFailed`. Distinguish “enabled but no matching events” from demonstrated health. Controlled fixture probes are an explicit diagnostics action, not hidden traffic generation during everyday capture.

### 4.4 Practical limits of enrichment

An IP Helper table gives a point-in-time connection inventory, not historic byte counts or every short-lived flow. [GetExtendedTcpTable](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable).

Pipe client/server PID APIs require suitable existing pipe handles. They are not a passive API for enumerating and resolving arbitrary named pipes. Never connect to unknown application pipes merely to discover their peer. [GetNamedPipeClientProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid).

If handle/object enumeration requires native interfaces without a stable public contract, isolate it as optional, build-tested enrichment with a kill switch. Time-limit potentially blocking name lookups in disposable helper processes. Do not place handle duplication, remote memory reads, or blocking name queries on the capture callback path. Protected-process failure is a supported outcome.

Existing Sysmon imports can add pipe creation/connection evidence, but Sysmon itself installs a driver/service. It is not a dependency of the driverless baseline and does not provide general pipe byte or content accounting. [Sysmon capabilities and events](https://learn.microsoft.com/en-us/sysinternals/downloads/sysmon).

## 5. Measurement semantics: what volume means

The UI must offer named metrics, never an unexplained single “volume” number.

| Metric | Definition and scope |
|---|---|
| Observations | Number of normalized source observations of the selected kinds; useful across mechanisms but sensitive to instrumentation density |
| Operations started/completed | Distinct logical operations with qualifying observed start/completion evidence; never raw ETW event count renamed as calls |
| Bytes sent/received | Sum of eligible byte measurements in one named byte domain and observation side |
| Endpoint activity bytes | Every eligible byte measurement at the endpoint that recorded it, sent and received alike, in one named byte domain. Summed across endpoints it counts a local transfer at both of its ends by design, and is labelled so (§5.1) |
| Requested I/O bytes | Sum of requested lengths; separate from completed bytes |
| Application payload bytes | Only source-verified application lengths, including explicit partial/unknown status |
| Captured content bytes | Amount retained for inspection; not the actual volume transferred |
| Rate | Selected count or compatible byte sum divided by specified duration; incomplete coverage is disclosed |
| Duration/latency | Named interval: client call, server execution, I/O completion, ALPC send-to-receive, or wait. These are not interchangeable |
| Active channels/peers | Distinct channel/entity instances under the selected interval and evidence policy. A peer is a process instance at the other end of a proven relation, and a channel is one connection incarnation, counted once at both of its ends; each count is a lower bound beside the records that identify none |
| Mapping capacity | Resource capacity where known, not traffic; available as a separate topology metric |

Each measurement carries `value?`, unit, semantic domain, observation side, source, quality, and unavailable reason. Zero is an observed zero. Null is unknown. Show “12 MiB observed; byte size available on 63% of eligible operations” only when that denominator is defined; this is measurement availability, not percentage of all actual traffic captured.

### 5.1 Avoid double counting

Represent a transport observation separately from a logical operation. RPC over ALPC or a pipe is one application operation with transport evidence beneath it, not three independent communications to sum. Application and transport modes use different counting domains with visible labels. Cross-layer relationships can be many-to-many; never assume one RPC call corresponds to one transport event.

For a local transfer observed as A-send and B-receive, sender accounting counts A-send once. A process's sent-plus-received total is endpoint activity, and summing this across processes intentionally counts both endpoints. Label it accordingly. Do not deduplicate by equal timestamp/size. Retransmit observations remain separate from unique delivered payload. Cross-host matched observations retain both records and a canonical accounting policy.

### 5.2 Sorting and scale

Default ranking for Explore: observed communication activity, with metric name shown and transport breakdown. Offer sent bytes, received bytes, total endpoint bytes, operations, rate, peers, errors, and local duration statistics. Unknown byte values sort in a separate `Unmeasured` group; never below measured zero without explanation. Mixed byte domains require separate groups or an explicit selected domain.

Ranking scope is selectable: analysis interval (default), whole retained capture, or visible viewport. Freeze order during gestures and while inspecting a row. Live reranking is optional, throttled, and uses stable entity-ID tie breaks. Pins remain fixed. Top-N displays include an exact remainder or a clearly labeled approximation; the remainder is a grouping, not a real communicating peer.

Use a shared magnitude scale across comparable lanes by default. Optional per-lane normalization is prominently labeled because it hides absolute volume differences. Logarithmic display affects only drawing, not counts, sorting, exports or units (§6.2). Quantiles are computed from mergeable distributions or exact selected durations, never by averaging bucket percentiles.

### 5.3 Metric compatibility and accounting ownership

The query compiler resolves every request against this matrix before planning (§19.1). A metric outside its basis is rejected with the compatible alternatives named; it is never silently substituted. `EN-Metric`, `EN-Basis`, `EN-ByteDomain` and `EN-AccountingSide` are defined in §23.

| Metric | Source observations | Logical operations | Resource topology | Byte domain | Accounting side |
|---|---|---|---|---|---|
| `Observations` | yes | no; use operation counts | yes, over membership and lifecycle records | not applicable | not applicable |
| `OperationsStarted`, `OperationsCompleted` | no | yes | no | not applicable | not applicable |
| `BytesSent`, `BytesReceived` | yes | yes, where operation-level lengths are proven | no | required, exactly one of `TransportObserved` or `CompletedIo` | required: `SendSide`, `ReceiveSide` or `CanonicalOwner` |
| `EndpointActivityBytes` | yes | yes, where operation-level lengths are proven | no | required, exactly one of `TransportObserved` or `CompletedIo` | fixed `EndpointActivity` |
| `RequestedIoBytes` | yes | yes | no | fixed `RequestedIo` | required |
| `ApplicationPayloadBytes` | yes, only from application-layer sources | yes | no | fixed `ApplicationPayload` | required |
| `CapturedContentBytes` | yes | yes | no | fixed `CapturedContent` | required |
| `Rate` | yes | yes | no | inherited from the numerator | inherited from the numerator; the numerator is a count or a byte sum, never a duration, a distinct count or a capacity |
| `Duration` | no | yes, with a named cohort | mapping lifetime only | not applicable | not applicable |
| `ActiveChannels`, `ActivePeers` | yes | yes | yes | not applicable | not applicable |
| `MappingCapacity` | no | no | yes | fixed `Capacity` | not applicable |
| `Errors` | yes | yes | no | not applicable | inherited where a side is known |

Two byte domains are never summed, ranked together, or shown in one total. A request mixing them is rejected or split into labeled groups (§5.2, I6). A domain that a fixed-domain metric owns is never accepted by a traffic metric: `BytesSent` over `RequestedIo` would report a requested length as sent bytes, which is P3, so the request is refused with `RequestedIoBytes` named.

**A row's side and a request's accounting are different facts.** A stored contribution's side is a fact about its record — the end of the exchange its measurement describes, or none the source states. A request's accounting is a rule for building a total from those facts: sender accounting takes send-side contributions, receiver accounting takes receive-side ones, endpoint activity takes every contribution at the endpoint that recorded it, and a canonical owner takes one contribution per proven association and maps to no row label at all. The mapping is fixed once, in `contracts/metrics-v1.md` §4; every contribution a total does not take stays visible in its breakdown. Endpoint activity names both directions, so it is `EndpointActivityBytes` rather than an accounting of `BytesSent` or `BytesReceived`, whose names state one (ADR-012).

**A request that means nothing is refused; a request a session cannot answer is unavailable.** The matrix decides meaning, before any session is read. A permitted request that needs a derivation the session lacks — operations, topology, entity bindings, a status domain, a proven transfer association — returns no value and names what it needs. A byte total that takes no known contribution is unavailable for the same reason, never an observed zero (R21, P1).

**Canonical accounting owner.** When a transfer association is proven and both sides carry a measurement in the selected domain, exactly one contribution owns the total:

1. the send-side contribution, when it has a known value in the selected domain;
2. otherwise the receive-side contribution;
3. ties, including two send-side records for one association, resolve to the lower `(host ID, stream ID, record ordinal)`.

The non-owning side stays visible as corroborating evidence and navigates to the same records while contributing zero to the total. Alignment revisions never change ownership (I10). A resource projection drawing two legs for one exchange assigns the total to the leg carrying the owning contribution (§19.1). Endpoint activity is a separate, explicitly labeled metric that counts both sides by design (§5.1).

## 6. Workspace and visual design

### 6.1 Layout

```text
+--------------------------------------------------------------------------+
| Capture / Open / Investigation    Host(s)   Coverage   Recording         |
| Filters: process, peer, mechanism, endpoint, time, quality               |
| Basis / Metric / Byte domain / Accounting side / Evidence policy         |
+------------------------------------+-------------------------------------+
| COMMUNICATION GRAPH                | TIMELINE                            |
| equal main-pane weight             | grouped virtualized lanes           |
| stable layout, clusters, pins      | pan / zoom / pinch / brush          |
| process <-> channel <-> process    | counts / bytes / durations          |
| bounded neighborhood + context     | retained-capture minimap            |
+------------------------------------+-------------------------------------+
| Ranked processes / channels        | Inspector / evidence / content      |
+--------------------------------------------------------------------------+
| Live lag · ETW loss · app drops · unknown schemas · disk · scope         |
+--------------------------------------------------------------------------+
```

The two main panes start at equal widths and share available height. Users can resize, swap or maximize either, with layout saved per workspace. Smaller windows can stack them; neither becomes a token thumbnail. The graph is a navigable map and the timeline is the time axis for that map.

Layout rules, so that equal prominence is testable rather than aspirational:

| Rule | Value |
|---|---|
| Default split | 50/50 by width; both panes take the full height of the main region |
| Minimum pane size | TUNABLE: 420 × 320 logical pixels each, below which the layout stacks rather than shrinks |
| Stacking breakpoint | A window narrower than TUNABLE: 1,100 logical pixels stacks the panes vertically, graph above timeline, each keeping its minimum |
| Collapse floor | Either pane may be maximized or hidden by explicit command only. No automatic layout pass reduces a pane below its minimum or to a thumbnail |
| Persistence | Split ratio, orientation, maximized pane, lane grouping, pinned lanes, graph pins, sort column and column widths are saved per workspace and restored on reopen |

Use restrained chrome, crisp typography, persistent legends and rounded selection emphasis. All colors, spacings, radii, stroke widths, font sizes and animation durations are named tokens resolved from one theme definition per mode — dark, light, and high contrast in a dark and a light form, as the operating system's high-contrast scheme is — and no view hard-codes a color or a size (R5). Channel assignments, the mechanism palette and its contrast requirements are specified in §6.6; interaction and navigation math in §6.7.

Revision 137 records how far this is from true. The dark and light token sets exist and their contrast is verified (§6.6), but the Desktop always runs dark: the drawn panes build their brushes once from dark tokens, and the view model writes dark colours into legend and table rows. No high-contrast set exists. §26.2 now names the default this section lacked, following the operating system, and the work is listed in the implementation status.

Revision 138 follows the operating system's light or dark setting at runtime. The tokens record the mode they were last applied in; the graph, timeline, minimap and hover layer build their brushes once per mode and draw with the current one, and a change of mode redraws them and the legend. The legend had computed each family's fill and ink and bound neither, so its chip glyph and name were drawn in body text and it keyed no hue at all; the glyph now takes the family's fill and the name its ink, as §6.6 asks. A high-contrast setting keeps the light or dark set it is paired with until a verified high-contrast set exists, and the mode is not yet a stored application setting (§26.3).

Revision 140 adds that set, in two forms, because Windows offers light high-contrast schemes as well as dark ones and the platform reports which one is on. The platform's high-contrast preference selects the form that matches its light or dark scheme:

- **Tokens:** each form has its own surfaces, families and status tokens. Ink clears 7:1 on every surface and every fill 4.5:1 (§6.6). The families keep their hue roles and glyphs; they were searched within a window around each role colour, so steel stays desaturated and amber stays amber.
- **Divider:** a token of its own. In dark and light it is the elevated tone, the quiet seam those modes always drew. In high contrast it is a visible line, at least 3:1 on every surface, edging panes and cards; the cards give up a pixel of padding to their edge, so the ordinary modes move nothing.
- **Control chrome:** the control theme draws a button's and a text box's edges faintly against the ground, so a high-contrast mode restates them from the tokens:
  - a button rests on the elevated face inside a divider edge, and takes the accent edge under the pointer;
  - pressed, it is the measured action pair, and disabled, a divider-toned label on the bare ground;
  - an ordinary mode hands those keys back to the control theme.

Revision 147 restates the rest of the chrome the Desktop shows, now that the header's *Theme* menu can switch into high contrast:

- **Menus:** the elevated face inside a divider edge. An item under the pointer or pressed is the action pair, and a line that cannot be chosen keeps the muted ink rather than a faint grey.
- **Tool tips:** the card's look, body ink on the elevated face inside a divider edge.
- **Scroll bars:** a divider-toned thumb on the bare ground, the accent under the pointer.
- **List selection:** a row under the pointer, pressed or selected lies on the elevated face, where every ink a row carries is measured, muted included. A fill alone cannot both stand 3:1 from the ground and keep muted ink at 4.5:1 on it, so a selected row also draws an accent edge. Every row keeps a transparent two-pixel edge, so selecting one moves nothing.

A pass with Windows' own high-contrast themes is still owed on Windows.

Revision 287 corrects revision 147's claim to have restated the rest of the chrome. The window's four combo boxes and its per-second toggle, the content and investigation windows' tabs and the package window's check box kept the control theme's look, and in every mode that look draws a chosen state in the platform's accent colour: a selected row in every list, a toggle or box that is on, a selected tab, a focused field and the text selected in it. Windows lets a person set that colour to any hue, so no report measured it. Even the control theme's default blue failed: in the ordinary modes a selected row's muted ink read as little as 3.7:1 on its tint, and its accent ink 4.1:1, below §6.6's 4.5:1. In high contrast an open combo box's rows kept the control theme's greys, the accent heading on its chosen row read as little as 5.0:1 where 7:1 is owed, and the toggle's edge 1.3:1 against the ground. Every chosen state is now drawn from the tokens in every mode:

- **Rows:** a row of a list, or of a combo box's open list, lies on the elevated face under the pointer, pressed or selected, and a selected row is also ringed in the accent: the rounded selection emphasis this section asks for. The ring is drawn inside the row, as a multi-selection's bar now is, so neither selecting nor choosing a row moves its content, and a row keeps one size in every mode. A list lies on the surface it is placed in, never on the control theme's grey.
- **Toggles and boxes:** a toggle button or a check box that is on is the measured action pair, at rest, under the pointer and pressed. The per-second toggle's label is centred, where it sat at the top.
- **Combo boxes:** the open list is the canvas inside the divider's edge, its rows labelled in body ink. In high contrast the closed box is a button: body ink on the elevated face inside the divider's edge, and the accent's edge under the pointer.
- **Tabs and fields:** the selected tab is named in body ink and underlined in the accent, the others in the muted ink. A focused field is edged in the accent, and the text selected in it is the action pair.
- **The accent:** the control theme's accent is set to the mode's action fill, so a control the window does not restate draws in the application's own colour, never in the platform's.

Whether Windows' accent setting still reaches anything a test does not draw is owed a check on Windows with a vivid accent, beside the high-contrast pass.

### 6.2 Timeline

Modes: mechanism overview; host -> process instance; process -> peer/channel; endpoint/resource; and selected operation/thread detail. Keep entity identity independent of lane position. Pin lanes, collapse groups, search lane names, and virtualize rows. A stable low-cardinality mechanism overview remains available even with thousands of processes.

At coarse zoom show density/rate cells. At medium zoom expose directional traffic bands and operation clusters. At fine zoom show individual events and duration bars, capped by a visible rendering budget. Expand overlapping marks to a detail list. Do not draw millions of arrows.

Wheel zoom anchors time under the pointer; pinch uses the gesture-start viewport and current focal point; horizontal scrolling/dragging pans. Vertical scrolling over lane headers scrolls lanes. A documented modifier/drag gesture brushes time without conflicting with pan. Keyboard offers pan, zoom, fit, selection, next/previous observation and navigation history. Buttons expose every essential gesture.

Minimap shows the retained time range, current viewport, selection and capture gaps. Follow-latest and recording are separate states: pausing the view never stops capture. An “as-of” snapshot pin holds analysis stable while ingestion continues. Rejoin live deliberately. Ring-retention eviction must not silently invalidate pinned evidence (I18).

**Level of detail.** The regime is chosen from cell width in device pixels, not from a zoom level or an observation count:

| Regime | Entered when | Drawn |
|---|---|---|
| Density | cell width < 3 device px | One intensity cell per column per lane, carrying the selected count or compatible byte sum |
| Band | 3–12 device px per cell | Directional traffic bands and operation clusters with count badges |
| Event | > 12 device px per cell | Individual marks, duration bars and direction arrows |

Named widths use a 1–2–5 ladder so cached tiles, CLI resolutions and exports are comparable: 1 µs where source precision permits, then 10/20/50 µs, 100/200/500 µs, 1/2/5 ms, 10/20/50 ms, 100/200/500 ms, 1/2/5 s, 10/20/30 s, 1/2/5/10/30 min, 1 h. Pixel-driven boundaries (§10.3) remain primary; the ladder only names resolutions. Never allocate a model for an empty unit.

Mark budget: TUNABLE: 20,000 drawn marks per frame across all lanes and 2,000 per lane. A lane exceeding its budget in the event regime falls back to its band representation and says so in the lane header; the budget never silently drops individual marks (R21). Arrows are drawn in the event regime only.

**Cell intensity.**

```text
intensity = log2(1 + v) / log2(1 + vScale)
```

`v` is the cell's value in the selected metric and `vScale` is the maximum over the normalization scope. Rules: intensity is display only and changes no count, sum, ordering, export or unit; the scope toggle between shared-across-lanes (default) and per-lane is visible at all times, because per-lane normalization hides absolute differences; the active scale, its scope and `vScale` appear in the legend and the hover card; linear and square-root alternatives may be offered under the same disclosure; a cell whose value is unknown draws in the unmeasured pattern of §6.6 and never as intensity 0; an observed zero draws as empty; and a cell whose value is above zero never draws below the occupied floor. For a count the last two rules read as "no eligible observation" and "any eligible observation"; for a byte sum a cell can hold eligible observations that measured zero bytes, and it is an observed zero, empty, since a floor would draw volume that was measured not to exist.

**Mark geometry and hit testing.** A data column is one device pixel wide in the density regime, which reads the shape of a dense lane correctly but leaves an isolated burst as an unpointable hairline. Therefore:

| Constant | Value | Reason |
|---|---|---|
| Occupied floor | TUNABLE: 0.42 of lane height | Any occupied cell keeps a visible share of its lane and only growth above the floor tracks intensity, so magnitude is carried by height as well as alpha (R14). Scaling linearly from zero would draw a lone critical mark thinner than the gridline beneath it |
| Minimum drawn width | TUNABLE: 5 logical px, ceiling 12 | One device pixel is under half a millimetre on a dense display: legible as a signal, not as a pointer target |
| Widening scope | Runs narrower than the minimum only | A dense lane already exceeds the minimum everywhere and keeps its true shape untouched |
| Widening geometry | Expand around the run centre, clamped inside the plot; members share the widened width evenly | Preserves each column's relative position and the run's proportional ink |
| Pointer snap radius | `minimumWidth / 2 + 5` logical px | Every painted pixel selects the mark it belongs to, plus a small allowance for aim |
| Snap search cap | TUNABLE: 128 columns | Snapping never degenerates into a full lane scan |
| Snap tie break | The earlier column | Deterministic hit testing |

Snapping applies only when the pointer is not already on data, and clicking genuine emptiness still selects that empty interval — an empty answer is a supported result (§1.2). Widening is cosmetic: hit tests resolve through a data-space index to the true interval and the true records (R13), and the hover card states the actual interval.

Revision 133 applies this, and P22, to a pointer that stays still. The timeline no longer stores the instant it hovers. It keeps where the pointer rests, relative to the window, and answers from that point on every read, against the drawing as it is then. So a zoom, a pan, a resize, a lane scroll, a growing live edge, a moved pane or a newer generation can no longer leave the card describing a time the pointer has left. While recording, the live edge re-scales the plot at 4 Hz, so the old card went stale almost at once. The graph answers its hover again whenever a layout, a pin, a resize or a generation moves the drawing. Its whole pane now answers the pointer. Before, only its ink did, so a press on empty space never gave it keyboard focus, and its hover noticed a node that left the pointer but not another node arriving under it. The table's minimum drawn width and pointer snapping are not built. The timeline floors a bar at 1 px wide and 3 px high, and that floor changes no answer, because hit tests resolve by time. At the minimum window the plot is 458 px wide, or 326–378 px beside lane labels. So the 64 overview columns are 5–7 px and their bars 3–5 px, under the table's minimum only in lane views, and a hit there still takes the whole column.

Revision 154 settles when they are owed: with the density regime, and not before. The table was written for a column one device pixel wide, where an isolated burst is a hairline nobody can point at. The timeline does not draw that regime yet. It draws 64 overview columns, or detail columns of about 10 px, and a hit takes the whole column. So every bar already has a pointer target at least 5 px wide, even at the minimum window. Snapping there would do harm: within 7.5 px of a bar lies the whole of the next column, so hovering it would name the bar and no longer the empty interval that is there. Both rules arrive with the density regime (cell width under 3 device px) and its data-space index.

**Viewport bounds and overscroll.**

| Bound | Value |
|---|---|
| Minimum span | `max(1 native tick, W × minimumTicksPerPixel)`, TUNABLE: 1 µs per device pixel, never below one native tick |
| Maximum span | TUNABLE: 1.1 × retained extent under the current analysis scope |
| Overscroll allowance | `min(0.05 × retained extent, 0.10 × current viewport span)` |

The allowance must be bounded by **both** terms. Bounding it by the retained extent alone makes the margin a fixed amount of *time* at every zoom: invisible at fit, but at deep zoom it can consume most of the plot, leaving a largely empty canvas beneath an axis printing an interval in which no data exists. That reads as a failure to draw rather than as an edge, and it collapses the minimap brush to a sliver exactly where a reader most needs to know where they are. The viewport term keeps the affordance while making it read as an edge at every zoom.

**Axis and ticks.** Choose 1–2–5 tick intervals from the same ladder. Measure label text rather than estimating its width. Keep tick spacing independent of data column width. Escalate units only at the span that needs them: hours and minutes at coarse spans, milliseconds below about 10 s, microseconds below about 10 ms, nanoseconds only where the source clock justifies the digit. Show the date when the viewport crosses a day. State the displayed time base at all times — host-local wall clock, session-relative, or workspace-aligned — and account for device scale. When the axis is workspace-aligned across hosts, draw each host group's uncertainty half-width as a band at the lane edge and repeat the value in the hover card; never draw an aligned axis as though uncertainty were zero (§8.2).

**Minimap.** Draws the whole retained extent at a coarse fixed resolution, TUNABLE: 2,000 columns, plus the viewport brush, the selection, capture gaps and coverage defects. Minimum brush width TUNABLE: 8 logical px, so the brush stays grabbable at deep zoom.

**Hover contract.** A hover over any cell or mark states at least: the exact half-open interval; the metric name, unit, semantic domain and accounting side; the value or `unknown`; the unmeasured count inside the cell; the number of contributing observations; the coverage state and any defect in the interval; and the intensity scale with its normalization scope. The unmeasured count is never omitted, because it is what distinguishes a quiet channel from an unobserved one (R21).

Revision 113 implements this contract for the timeline's buckets.
- **Pointer:** a bucket on the plot is outlined and gets a card. A press begins a gesture and removes the card.
- **Card contents:** the bucket's half-open interval; its record count and dominant mechanism; the basis, unit, domain and accounting of a record count; the rung's focus count inside it; its rate against the busiest visible bar; what is unmeasured; bytes; coverage; the resolution it was counted at, and the generation when a zoomed count came from a newer one; and whether a click would make it the analysis interval. A records timeline has no unmeasured part inside a bucket. A record without a usable session time is placed in no bucket, and the card says so rather than printing a zero.
- **Hover layer:** graph and timeline cards are drawn by one window-level layer above every pane, which takes no input. A card is therefore never clipped by the pane its mark is in: at the minimum window the timeline pane is shorter than a complete card.

**Live edge.** Revision 126 draws the broker's live preview (`contracts/broker-v1.md` §5.9) as the timeline's live
edge while the view follows a recording.

- **Placement:** the time after the last published record takes the right-hand part of the plot, beyond a dashed rule
  and under a `live` label. It uses the published scale where that leaves both parts legible: at least 36 px, at
  most 40 % of the plot. The published plot keeps its own hit testing unchanged.
- **Content:** only the chunks after those the displayed generation holds, in 100 ms bins, each mechanism's hue at half
  strength. A bar above the published peak is drawn full height rather than rescaling the published timeline four
  times a second.
- **By rung:** at L0 each lane shows its own mechanism, and the label names any previewed mechanism with no lane yet.
  A focused rung shows the preview only in its machine row, because preview counts are not attributed to processes
  or channels.
- **Interaction:** hover explains a bin as a preview, what it counts and when exact records replace it. A press
  selects nothing.
- **When it is hidden:** a paused, held or zoomed view draws no edge, because its gap to the preview is not previewed.
  Stopping retires it, and so does the publication that holds its chunk.
- **What it is not:** a preview is never ranked, tabled, exported or added to a published count.
- **Never exact (P26):** revision 130 asserts that a preview reaches no ranking, interval table, bracket step, brush or
  export. Exports are byte-identical with and without one, and a brush dragged into it stops at the published extent.
- **Before the first publication:** no clock is displayed to place bins on, so no edge is drawn. The waiting workspace
  of §6.8 says the capture is recording instead.

### 6.3 Communication graph

Use a directed multigraph with optional explicit resource nodes. Processes are nodes, channels distinguish parallel relationships, and hosts form outer groups. Shared memory is a section hub with membership edges, not a complete directed clique of supposed traffic. Unresolved resources remain visible. Separate initiator/responder roles from actual data direction; a server often sends data.

Provide process-to-process projection for overview and process-resource-process projection for explanation. Group by executable, service container, host, or user session while preserving instance drill-down. A service list under `svchost` is metadata; attributing an individual IPC operation to one hosted service requires additional evidence.

Default to bounded neighborhoods and explicit expansion at high cardinality. The starting display budget is 200 visible nodes and 500 edges. **Compaction never omits a process:** every instance remains present either as an individual node or as a member of a visibly counted cluster, and the full ranked/table projection remains un-compacted. Large captures open clustered, not as an unreadable force-directed cloud. Run layout off-thread; preserve positions across refreshes; avoid relayout during time gestures; offer manual pins and explicit re-layout.

Encoding is fixed so that the two panes cannot disagree about magnitude:

| Element | Encoding |
|---|---|
| Edge thickness | `1.25 + 4.75 × intensity(metric)` logical px, using §6.2's intensity function and the same normalization scope; TUNABLE bounds 1.25 and 6.0 |
| Node radius | `6 + 10 × intensity(incident metric)` logical px, floor 6 so a low-traffic participant stays selectable. The scale is the busiest node the rung draws for itself, so the context node never sets it |
| Edge hue | Mechanism, from §6.6's palette; never magnitude or quality |
| Inferred link | Dashed stroke plus an explicit `inferred` marker in the legend and hover card |
| Candidate relation | Dotted stroke at reduced opacity; excluded from definitive causal views |
| Resource-membership edge | Neutral hue, thin constant stroke, no arrowhead — membership is undirected and carries no traffic value |
| Context node | **Rest of the machine** on a focused rung (§3.2). It has a dashed, muted outline over the pane's own colour and the aggregate rims, and adds to no total (§19.1). Its label and count identify it, so no separate badge is needed. It is drawn at the aggregate floor radius, because the rest of the machine is not measured on the focus's scale. The keyboard reaches it last. Its inspector text separates relationships with drawn processes, which are drawn faded, from relationships among its own processes, which it folds |
| Cluster collapse | A group collapses when more than TUNABLE: 25 of its members would be drawn individually, or when the projection would exceed the display budget. Additional groups collapse lowest-whole-scope incident-metric first; every collapsed node exposes its member and relationship counts, with selection/hover detail always stating them and on-canvas labels stating them where the label budget permits |
| Aggregate nodes | A stack of offset rims says "several" without colour; the face's stroke names the kind: solid for a collapsed group, muted for an opened group's **Other members**, dashed for **Other processes**, dotted for **No relationships**. An aggregate's size follows the same incident-metric radius as a process, so it never looks busier than its relationships are |

Compaction is a presentation transform over one published graph identity, not a query filter. Its membership is chosen from the unbrushed published scope and stays fixed while a time brush re-counts magnitudes, so a pointer gesture cannot make nodes jump between clusters. The deterministic fallback order is:

1. **Count processes with no relationship.** A process that is an endpoint of no relationship in the published scope has nothing to draw, and a real machine has hundreds of them: drawn one by one they are exactly the unreadable cloud this section forbids. Outside the explicit focus they fold into one **No relationships** node — an opened group's own quiet members into its **Other members** node — which has no edges and states its count. A lone quiet process stays drawn as itself, because a one-member aggregate saves nothing and hides a name.
2. Collapse every group with more than the threshold of individually drawn members, except the group the user explicitly opened.
3. Collapse additional groups lowest-metric-first until the node/edge budget fits. The node budget is met by an exact count; the edge budget by the shortest prefix of the same order that fits, which exists because merging nodes can only merge edges.
4. When an opened group alone is too large, keep its busiest members individual and fold its low end into its **Other members** node.
5. If many singleton groups still exceed the budget, fold the least-active remaining presentation components into an explicit cross-group **Other processes** node. It never absorbs **No relationships**, so each aggregate keeps one meaning.

A focused process is protected from every fold. Aggregated edges retain their source relationship identities, count them explicitly, and use the least-certain/worst evidence strength among their members. A relationship whose endpoints compact into the same node resolves to that node as an internal relationship instead of disappearing or being drawn as a false self-edge. No stage may silently truncate a process or relationship identity.

An executable group is labelled with its file name, extended by just enough parent folders to tell same-named executables apart (`git.exe (cmd)`, `git.exe (bin)`); the witnessed image path is the group's detail, shown by the inspector, never the label. The pane header states the scope in one line — processes, relationships and among how many of them, and the node count when groups are collapsed — and labels are placed only where they overlap no other label, in one line that ends in an ellipsis rather than wrapping mid-word. Label placement searches outward from the four immediate sides through a small deterministic set of diagonal/wider callout slots before omitting a non-selected label; a hub is not left unnamed merely because all four cardinal slots contain its peers. Selection/focus may force a final in-pane label because the user explicitly asked which mark is active.

Hover explains evidence and time scope and adapts §6.2's hover contract to a graph mark rather than pretending an edge has a timeline cell's own interval. A graph card states the exact half-open analysis scope currently applied (whole session or brush); basis, metric, unit and semantic domain; whether accounting side applies; the mark's value and contributing-observation count; byte availability and the unmeasured denominator at the finest granularity the projection actually has; relation evidence strength and coverage; and the node-size or edge-thickness normalization scale. It never invents a narrower mark interval, a byte denominator the model cannot derive, or a direction from display order. A graph edge selection filters relevant detail records. Double-clicking an edge that stands for one relationship descends the ladder to it through its group and source process. The descent reaches the channel rung, with direction, lifetime, statistics and source evidence, when the relationship has exactly one channel. Otherwise it stops at the source process, whose rows list the channels. An aggregate edge stands for several relationships and opens nothing. The edge's hover card says which of these a double-click will do. Activating a collapsed executable group opens that group; activating **Other members**, **Other processes** or **No relationships** explains the aggregate and points to the full ranked table rather than pretending it is a real process. Selection is shared with the table (§6.4): a selected group row rings the nodes that stand only for its members and marks with a broken ring any aggregate that holds some of them among other processes, so an aggregate is never passed off as the selection; E then scopes evidence to that group. Layout, determinism and hit testing are specified in §19.4.

Revisions 107–111 implement this bounded projection and its first complete interaction surface. The 200/500 display budget is intentionally below the layout worker's 512/4,096 safety cap, cluster membership is stable across interval brushes, and no process instance is dropped to make the drawing fit.

- **Revision 108, sparse real import.** A real 534-process import with one admitted relationship. Revision 107 alone drew 197 unrelated circles, one edge and colliding device-path labels. The machine rung now draws the two related processes and one **No relationships** node of 532, and an opened `svchost.exe` adds its 98 members as **Other members**.
- **Revision 109, dense real capture.** Recorded with `icat record` while 65 workload processes under 13 executable names exchanged loopback TCP as nine hubs and their clients: 621 processes and 56 relationships. The machine rung draws 40 nodes and 31 edges, with a 27-member `worker.exe` pool collapsed; opening it draws 66 nodes and 56 edges. Under the band layout the drawing was a tangle of crossing edges. §19.4's relationship-first layout draws nine separate stars with no crossing.
- **Revision 110, interaction contract.** Hover cards disclose the graph-specific §6.2 facts without inventing per-mark time or byte precision; a node can be dragged to a hard pin, P pin/unpins the selected node, and L explicitly re-lays out while preserving pins. Pins carry across live publications of the same open session. Crowded label placement searches wider and diagonal slots so a star's hub can stay named. Workspace-file persistence of pins remains gated on §26.3 rather than being falsely implied by an in-memory pin.
- **Revision 111, per-rung neighbourhoods.** Each rung draws its neighbourhood and counts the rest in the context node (§3.2). Double-clicking a single-relationship edge opens its channel.
  - Dense capture: opening `worker.exe` draws its 27 members and the `queue.exe` hub they talk to, as a star. The header reads "worker.exe and its peers: 28 processes drawn · 593 more in Rest of the machine". The context node holds 593 processes and 29 relationships, all among themselves. Projection and layout take 57 ms.
  - Sparse import: `svchost.exe` has no peers. It reads "svchost.exe: 98 processes drawn as 1 node · 436 more in Rest of the machine".
  - Defects found in these frames were fixed:
    - The context node held the most observations, so it set the size scale.
    - The context node came first in keyboard order.
    - The header claimed peers for a focus with none, and called folded processes "drawn".
    - A new rung's ranked table opened at the scroll offset of the rung it left.

### 6.4 Shared state and interaction rules

Maintain three separate time concepts: retained capture extent, visible viewport, and analysis interval. Default graph/ranking scope is the brushed interval if present, otherwise the viewport. A scope lock holds it while navigating. Always show the effective range. Zoom changes presentation and default scope, never capture policy or stored observations.

Selecting a graph entity highlights it in the timeline; `Focus` makes it a filter. Selecting a timeline cell opens the exact contributing evidence and highlights graph relationships. Distinguish hover, selection, highlight and filter. Multi-select composes explicit predicates. Back/forward restores filters, time, graph focus and lane grouping as one navigation state. Back is the ladder's ascent (§3.2), and forward re-enters the rung an ascent or a crumb left, exactly as it was left. A descent to anywhere else, or a filter taken off, ends forward history, as a new page does in a browser; descending by hand to the very rung forward names keeps the rest of the way. Time here is the analysis interval the user had on that rung when they left it, and a return restores it through the one selection source, so the brush, the ranking's counts and the timeline always name the same interval. A rung left with no brush comes back with none.

Maintain requested and applied query states. Publish a coherent result bundle identified by snapshot, filters, metric, scope, graph projection and alignment revision. While work is pending, keep the previous bundle with a pending indicator. Export operates on a named applied snapshot, never a mixture of old counts and new filters.

Revision 98: a brushed interval is the graph and ranking scope for a published session, as this section requires. Dragging across the timeline brushes an arbitrary range, and a press without a drag still selects one bucket. The workspace then counts each edge and channel only inside the range, using exactly the overview's admission and channel rules, and re-ranks every rung from those counts. Identities, positions and the timeline stay as they are; edges quiet in the range dim rather than disappear, the range is drawn on the axis with everything outside it dimmed, and the rail states "Ranked within …" wherever totals are read. A newer brush supersedes a count still being read, and clearing the brush returns to whole-session counts. Ranges are written in the unit their span needs (s, ms or µs), as §6.2 escalates axis units. On a real 94,694-record session, a brushed ranking applied in 132–153 ms. The viewport as the default scope when nothing is brushed, a scope lock, and edge thickness by metric intensity (§6.3) remain open.

Revision 134 closes those scope rules. With nothing brushed, the timeline's settled viewport is the scope. Zoom or pan a published session, and once the view rests, every rung, the graph, the tables, the inspector and `E` count only what is drawn. The rail reads "Ranked within the visible …" while it counts and after, and the inspector's time scope reads "Visible …". A brush is explicit and wins however the view moves; clearing it hands the scope back to the view, and fitting the timeline returns it to the whole session. A rung's own time is never taken from the view, so a descent does not freeze a zoom into the ladder. The scope lock is **Keep this range**: it makes the visible range the analysis interval, drawn on the axis and cleared like any brush, so the user can zoom and pan to look around while the counts hold. `E` lists the records behind the counts on screen, so it reads the same scope (I5). Edge thickness by metric intensity had already arrived with §6.3's log-scale encoding.

Revision 136 keeps that scope on screen through a live capture. Each publication is a new workspace, and one counts its scope afresh; until then the ranking, graph and tables had blinked back to whole-session numbers under "Ranking within …", for the length of every count. Now the previous publication's counts stand in, the rail says so ("… the previous publication's counts until then"), and this generation's own replace them, never merged, as the timeline's carried detail is. A zoomed view's range is carried too, so the next publication counts it before the view is bound to it. A stand-in is never claimed: until it is replaced the export names no interval, and an export asked for meanwhile waits for this generation's own counts. When counting takes longer than the publication cadence, the stand-in is carried on, still marked pending.

Revision 99: the live view can be paused and resumed at any rung, with `F` or the capture card's button, and pausing never stops recording (§6.2, §6.7). A paused view keeps its generation, as the evidence rung already did. Each newer publication is offered in a banner with a one-time update (F5) and a resume (F). The health strip now states four things:

- the capture and view state: following live, view paused, view held while records are read, stopping and saving, or a saved session;
- the loss the capture has stated: "Loss is stated when recording stops" while no ledger exists, "No coverage ledger · loss unknown" for a saved generation without one, and "No loss reported" or the affected mechanisms once a ledger is published;
- the coverage of the timeline's intervals;
- how long ago the last publication arrived.

Timeline gestures now follow the table above. A drag pans from the viewport it began with; Shift+drag or a middle-button drag brushes; a press selects a bucket; and Home, End and 0 work as the pane's hint had already promised. Revision 98's plain-drag brush was a deviation, because §6.2 gives plain dragging to pan. Axis labels use the same span-driven units as every range. A zoom on a session shorter than the 10-ms minimum span now returns the extent instead of throwing on an inverted clamp. Live loss counters during recording (queue drops, ETW buffer loss) are not yet in the broker's status; showing them in the strip as §20.6 asks needs an optional status field.

Revision 100: `Ctrl+E` and an inspector button export the applied view. The export holds the current rung's ranked rows, or at the evidence rung the records loaded so far. It is named by session, generation, rung, breadcrumb, filters, and the interval the shown counts answer; a brush still being counted is not yet applied and is not claimed. An export that holds fewer records than its scope says `complete: false`. JSON (`intercat-export-v1`) describes itself. CSV repeats that context in every row and neutralizes any text cell a spreadsheet would evaluate as a formula, because process, resource and executable names come from the observed machine; numeric cells, negative times included, stay numbers. Evidence exports carry normalized metadata and raw locators only: no body or extended-data bytes leave through this path, and the export says so. The user chooses the file; nothing is written anywhere else. Revision 103 added the CLI export in the same contract (R18); §11.3's sharing presets are revision 104's metadata-only report and revision 106's reopenable redacted session package.

Revision 101: while a capture records, the broker's status carries its acquisition counters (optional fields 14–20, broker contract §5.8):
admitted and observed records, records dropped by the bounded queue, ETW's provider-reported event loss and consumer-reported buffer loss, and the queue's depth and capacity.
Only `Recording` offers them; from stopping on, the published ledger states loss. An unreadable ETW loss counter is omitted and shown as unreadable, never as zero. A status carrying only part of the counters is refused. The Desktop passes a changed reading on at most once a second, until it sends stop, and the health strip states each loss separately beside "N admitted · last publication":

- "So far 3 records dropped by InterCat's queue · ETW lost 2 events";
- "So far no drops · ETW loss unreadable";
- "So far nothing dropped or reported lost".

None is summed. A counter-only update repeats the last recording state without an overview. It leaves the view, its generation and anything written to the detail line since as they were.

Three robustness defects surfaced with it.

- Status requests now read ETW's loss counters concurrently with the health sampler and the final stop. A read-then-write maximum could let an older, smaller reading replace a newer one, so the ledger now raises each loss counter with a compare-and-swap maximum.
- A read that raced the session's stop failed its query against a stopped session. It then marked the whole capture's loss as unreadable, a false unknown on a clean capture. The health sampler could already do this; status reads made it likelier. Such a failure now counts only while the session is still the owned, running one: cleanup takes the session before stopping it, and the read taken while finalizing is the final one.
- A late counter update could have re-sent the recording state after stop was sent and returned the window to Recording. Counter updates now end when stop is sent.

Policy omissions, undecodable records and quarantined timestamps stay in the final ledger. The strip does not show them yet, although §20.6 asks it to; it shows per-mechanism coverage derived from that ledger.

Real ETW (`bench/results/first-feedback-20260924T132726Z-live-counters`): 14 distinct readings reached the viewer in each 15-s run, with loss readable and zero. The first overview arrived 1.0–1.4 s after the first record. Event-to-visible p95 was 2.6–3.2 s, which is revision 97's open miss and is unchanged by the status reads. Broker qualification passes with the new status (`bench/results/broker-qualification-20260924T133432Z`).

Revision 102: the timeline has a minimap and resolves the viewport it shows. Before, the overview projected 64 buckets over the whole session and zooming only stretched them, so a minimap drawing those buckets would have added nothing (§6.2).

**The minimap level.** The overview bundle now carries up to 2,000 whole-session columns, counted in the same pass as the 64 buckets:

- observed rows per column;
- the capture's own coverage per column, from `SessionCoverage.CaptureStates`: the worst state of every mechanism each spanned epoch collected.

Capture coverage does not depend on observation, so a column emptied by a capture gap still shows the gap, while the timeline's buckets rightly leave empty buckets unknown. `MinimapView` draws the columns under the timeline:

- §6.2 intensity, `log2(1+v)/log2(1+vScale)`, above the 0.42 occupied floor, taking the largest column per pixel so a varying column-per-pixel ratio adds no false texture;
- a hatched gap row wherever the capture was not covered;
- the analysis interval;
- the timeline's viewport as an 8-px-minimum brush.

Dragging the brush moves the viewport, dragging an edge resizes it, a press beside it centres it there, and a double press fits (§6.7). Revision 105 makes the minimap itself focusable: its wheel zooms at the pointer, moving the frame toward an out-of-frame pointer before zooming; its arrow/Home/End/+/-/0 keys use the timeline's exact navigation implementation. `CenterOnTick` preserves integer time and clamps the frame inside the retained extent.

**Zoomed detail.** Once the viewport has rested 150 ms, the Desktop asks `SessionTimelineQuery.Detail` for that interval in 16–256 columns, depending on the plot's width. The query is off the input path, cancellable, and superseded by a newer viewport. The coarse buckets stay on screen until the answer arrives (P25). The whole extent needs no request. Detail buckets follow the overview's rules exactly: over the extent at 64 columns the query returns the overview's own timeline, which a test asserts.

Bar heights are now rates (records per tick), because coarse and fine bars share one axis while the detail replaces the coarse ones inside its interval; the axis states the peak in records per second. A press selects the finest bucket drawn. A paused or held view keeps its generation, while a zoomed count reads the newest one and says "zoomed detail from generation N".

**R15 table and CLI.** The interval table follows the drawn resolution and states whether it lists the whole session or a zoomed view. Its windows now use span-driven units; they read "5 s to 5 s" for any sub-second bucket before. `icat timeline <dir> --interval <start:end> [--columns]` gives the same query headlessly; the CLI help also stops splitting `icat evidence`'s options around `icat raw`.

**Cost, measured on a real 94,694-row, 59-s session:**

| Measure | Before | After |
|---|---|---|
| Opening the store, per Desktop query | ~57 ms | once per session source |
| Zoomed detail (the same saving also reaches evidence pages and interval ranking) | 75–98 ms | 20–25 ms |
| Whole-extent detail | — | ~40 ms |
| Warm overview projection, median | 158–162 ms | 168–175 ms |
| Warm overview projection, maximum | — | ≤ 199 ms, inside the 250-ms budget |

The first measurement had projection at +35–50 ms. The cause was the hot per-row helpers running unoptimized until tiered compilation promoted them, which a busy process kept delaying. They are now compiled optimized at once, and each column boundary is mapped to native time once instead of twice.

Real ETW (`bench/results/first-feedback-20260924T140714Z-minimap`): live projection p50 16–32 ms, p95 21–129 ms. The first overview arrived 0.9–1.1 s after the first record.

The current implementation recomputes the minimap level with each generation's overview rather than persisting it. That is an implementation-status exception, not the target contract: S4's durable multiresolution pyramid, which a 100 GiB session needs, remains open, as do incremental updates at commit.

Revision 158 serves the minimap and the overview's buckets from each segment's tiles, built once per reader (§12.1).
It aligns the minimap's columns to one 1–2–5 width, clipped to the extent at either end, so each is a union of whole
tiles. The tiles are not persisted yet, so a reopen still reads every time column.

Revision 103: `icat export` reaches every export the Desktop makes, in the same `intercat-export-v1` contract (R18).

The rung is reached the way a person reaches it, by descending through row keys from the machine rung (`--at`, repeated): group, then process instance (dashed or not), then channel. An unknown key is refused with the keys that rung does have. `--interval` ranks within an interval by the same count, and `--evidence` exports the rung's evidence scope instead of its rows. The file is staged beside its destination and moved into place, so a failed or cancelled export never leaves a partial file under the requested name, and it replaces an existing file only with `--overwrite` (§20.4). An evidence export that stops at `--limit` (100,000 by default) says what it left out and exits with the partial-result code.

The export context is now built in one place, `WorkspaceExport.RankingContext` and `EvidenceContext`, and the real-session disclosure lives in `OverviewWorkspace`. A test drives the view model through a group and a process, then asserts that the Desktop's export and `SessionExport.Build` are byte-identical: JSON and CSV, at the ranked rung and at evidence.

Evidence exports now read their whole scope in one pass under one lease (`SessionEvidenceQuery.ReadScope`), through the same merge and filter code the pages use; a test asserts the concatenated pages equal the single pass across 11 segments. On a real 94,694-record session, the paged export took 83.5 s and the single pass takes 2.9 s, with identical output.

The Desktop's evidence export therefore no longer holds only the pages loaded so far. It writes the whole scope up to the same bound, names the generation the records were read in (a live session may have published a newer one), and says when the bound left records out. This revises revision 100's loaded-pages rule.

§11.3's sharing presets followed: a metadata-only redacted report (revision 104), a reopenable redacted session package (revision 106) and an exact, explicitly unredacted original evidence package (revision 154).

Revision 159 builds this section's first interaction rule: selecting a graph entity highlights it in the timeline.

- **What is highlighted.** A selected process, executable group or aggregate node, or a channel chosen among a
  process's rows, is counted on the columns the timeline draws. The count is the exact focused count a rung's focus
  uses, so each bucket's share is the entity's own records and never exceeds the bar it marks.
- **How it is drawn.** Each share gets a rounded outline in the accent token, §6.6's encoding for selection. The
  outline sits just outside the bar's sides and its top crosses the bar where the share ends. It adds no fill, so it
  never reads as a mechanism's hue. In the machine rung's mechanism lanes, each lane marks its own mechanism's share.
- **What it is not.** A selection that is the rung's own focus adds no highlight over the colour already showing
  it. A chosen channel row is the latest choice at a process rung, so it is highlighted rather than the process.
  Only a descent or Focus filters.
- **How it is stated.** The caption names the highlight or says it is being counted. The hover card gives the
  selection's count in the hovered bar, and says so where the bar was drawn at another resolution. A live
  publication carries the highlight until the next generation's own count replaces it.

The first attempt drew a translucent accent fill. On a TCP bar, blue over blue, it could barely be seen, and §6.6
forbids fill for selection, so it became the outline. §6.7's `Ctrl`+click multi-selection extends this highlight
next.

Revision 288 makes the answer to every background read arrive on a later turn of the UI thread, never inside the step
that asked for it. Each read the workspace starts - counts, bytes, measures, evidence pages, RPC and HTTP lists - runs
on the thread pool, and the view awaited it plainly. A read that had already finished by the time it was awaited then
continued inline: a ranking's bytes were applied within the very setter or publication that asked for them, re-entering
it before it ended, and the "reading" or "updating" state that step had just published never showed. Three tests of
that state failed whenever a read beat its step, which under load was every run of one and most runs of the others.
Each such await now yields to the asking thread's next turn, so a step finishes before its answer lands, the state it
publishes is the state a person sees first, and the rows still change once, in one step, when the answer arrives. A
test reads the workspace's source to hold every evidence read it awaits to that rule.

### 6.5 Inspector and accessibility

Inspector tabs: overview; endpoints and lifetimes; operations; source events; correlation explanation; content; capture coverage. Raw evidence includes provider/event descriptor and schema, source clock, decoded fields, adapter version and original record reference.

All canvas selections have keyboard-accessible table equivalents and UI Automation descriptions (R15). Test high contrast, color-vision differences, 100–250% DPI, touch and precision touchpads. Widening a tiny drawn mark improves hit testing but does not expand its actual time interval. State the actual interval in the hover card.

Revision 131 audits the window's UI Automation tree at every rung from the machine to a channel, with the tables
shown. These rules came out of it, and an audit test holds each one:
- **A list item is named at its container.** Avalonia names an item from its container's automation name, then from a
  text block that is the whole item template, then from the row's `ToString()`. A name set inside a template never
  reaches the item. The ranked, relationship, interval and search tables were therefore read aloud as record dumps
  ("RungRow { Key = …"). Every list and combo box now names each container from its row's `AccessibleName`, and an
  option's `ToString()` is its label, because a combo box reads its value from it.
- **A sentence is written for the ear.** Counts take their number ("1 observation"). A coverage state reads "coverage:
  unknown", not the column's "coverage unknown coverage". A focus count is its own clause ("12 observations, 3 in
  focus"). A kind is a word, not the eyebrow's capitals, which some voices spell out.
- **A drawn pane has a role.** The graph, timeline and minimap were focusable elements with no control type, which
  screen readers skip or call unknown. Each is now a custom control with a role word ("graph", "timeline",
  "minimap"). Its help text gives its keyboard path and the table that lists what it draws. Its status is its current
  caption. The status is read on request and raises no change event, so a live view does not chatter.
- **Every focusable element has a name,** including the pane splitter.

A lane is not an automation element of its own. Its keyboard path and accessible equivalent are the lane selectors and
the interval table, which the audit names. A pass with a real screen reader on Windows is still owed.

Revision 171 orders the inspector by use, as the live test of revision 165 found it wanting: the selection and its
summary, then what can be done with it, then reference. At an ordinary window height the actions stood below the
evidence-quality key, under a scroll bar a hairline wide, so they looked like one clipped button. Every fact in the
summary card is now its name above its value, since a range set beside its name wrapped a word at a time.

### 6.6 Visual encoding channels and palette

Four meanings compete for the same canvas: which mechanism, how much, how trustworthy, and whether the period was observed at all. Each owns distinct channels, and no meaning borrows another's:

| Meaning | Primary channel | Redundant channel | Never used for it |
|---|---|---|---|
| Mechanism / transport | Hue from the fixed palette below | Legend chip glyph and lane label | Opacity, thickness, pattern |
| Magnitude | Intensity (alpha) plus height in the timeline or thickness in the graph | Numeric value in the hover card and ranked table | Hue |
| Evidence quality | Border treatment: solid, ticked, dotted | Quality words per dimension in the hover card and inspector | Hue, height |
| Coverage defect | Diagonal hatch across the affected interval, drawn above data | Explicit gap entries in the minimap and health strip | Hue, opacity |
| Unmeasured value | Open cross-hatch outline with no fill | `unknown` in the hover card and an `Unmeasured` ranking group | Intensity 0, empty cell |
| Direction, only where the derivation supports it | Arrowhead in the graph; in the timeline, L2's source-direction row and the side of an L3 end's midline | Direction word in the hover card | Hue; an undirected/unknown relation gets no invented arrow |
| Selection and focus | Rounded outline in the accent token plus a full-height locator line | Stated selection in the header and the accessible table | Hue, hatch |

One hue per mechanism family, defined once as theme tokens (R5):

| Mechanism family | Hue role |
|---|---|
| TCP | Primary blue |
| UDP | Teal |
| Named and anonymous pipes | Violet |
| RPC, COM, DCOM, WinRT | Amber |
| ALPC | Mint |
| Shared sections | Magenta |
| Other socket families, including AF_UNIX | Indigo |
| Synchronization, window messaging and other legacy IPC | Steel |
| Process and thread lifecycle | Neutral slate, never the unknown grey |
| Unknown or unsupported mechanism | Desaturated grey |

Revision 165 added the lifecycle row. Every capture collects process lifecycle, and the table had no family for it, so
the implementation drew it in the unknown grey and keyed it "Unknown". That broke the requirement below that the grey
never marks a supported mechanism. A contract test must map every mechanism the capture supports to a family other than
unknown, not only compare family colours.

Revision 167 implements it (theme 1.3.0). Process and thread lifecycle are the **Lifecycle** family, glyph ▼, a slate
chosen by search in each mode: the candidate of blue-grey hue and low chroma, within the lightness band the mode's other
fills span, with the largest worst margin over every threshold below. It sits between RPC and ALPC in palette order,
the position where its worst margin across the four modes is largest; palette order only decides which pairs are
held to the neighbour thresholds. High-contrast light needed a darker navy slate, because a lighter one cannot stand
15 apart from both the unknown grey and Legacy IPC's steel. The contract test takes every mechanism a Windows source
can collect, including those a profile omits, and holds each to a family other than unknown. Checking the change on a
real session found the legend keying "Unknown" for every session: an empty timeline column carries the unknown
mechanism as a placeholder and draws nothing. The legend now keys only columns and lanes that draw records.

Requirements the palette must satisfy, each enforced by a test rather than by judgement:

- Fills and ink are separate questions. A hue chosen to read well as an area on a dark ground can fail badly as text on a light one, and mechanism names appear as ink in the ranked table, the legend and the inspector. Define a fill variant and an ink variant per mechanism per theme mode.
- Every ink variant clears a 4.5:1 contrast ratio against every surface token it can land on, measured and recorded per mode; every fill variant clears 3:1 against its own ground. A high-contrast mode holds ink to 7:1 and fills to 4.5:1, and its divider clears 3:1 against every surface.
- Any two families, neighbours or not, clear 15 CIE76 in normal vision: the legend sets every family side by side, so a look-alike pair is a defect wherever it sits in palette order.
- Adjacent mechanisms in palette order keep a stated minimum perceptual separation, verified under protanopia, deuteranopia and tritanopia simulation and in greyscale.
- Hatches and warning patterns are reserved for coverage and quality. No mechanism may use one.
- The unknown/unsupported grey is never reused for a supported mechanism.
- A condition or an action is never drawn in a mechanism's hue. The caution ink that strokes the coverage hatch and words a warning clears 4.5:1 on every surface and keeps the adjacent-family distance from every family's fill and ink in normal vision; under a simulated deficiency the hatch pattern and the words carry it. The primary action's ink clears 4.5:1 on its fill in every state it is drawn in, and that fill clears 3:1 against every surface.
- A legend or key shows only marks a pane draws, drawn by that pane's own routine, so the key cannot drift from the canvas.
- Measured ratios and separations are stored with the theme definition, so a palette change that breaks one fails a test rather than a review.

Revision 139 found four places where a condition or an action wore a mechanism's identity, and one legend entry for a mark nothing draws:

- **Coverage:** the hatch, the health strip's coverage summary and the words of an unavailable capture used the RPC family's amber ink. The summary was amber even when coverage was complete.
- **Live dot:** a followed recording's dot was ALPC's mint, and a paused one RPC's amber.
- **Evidence-quality key:** the inspector coloured *direct* in ALPC mint behind TCP's glyph and *candidate* in RPC amber behind UDP's, although evidence quality owns the dash pattern and never a hue.
- **Primary action:** the style put white text on the TCP fill, measuring 2.79:1 in dark mode and 4.18:1 in light, and no button used it.
- **Legend:** "outline = unmeasured" keyed an encoding no pane draws. The only outlined marks are an L3 end's records with no data direction, so the entry taught a wrong reading.

Caution and action tokens now exist per mode and are measured with the rest (theme 1.1.0):

- **Caution ink:** a coral or a vermilion at least 39 CIE76 from every family. It strokes the hatch and colours a warning's words; the coverage summary takes it only while some interval's coverage is limited.
- **Live dot:** the accent while the view follows a recording; caution while the view is held or paused, or capture is unavailable.
- **Primary action:** Start and Stop are primary. They draw in the accent's value with the canvas as ink (9.6:1 dark, 7.6:1 light), and their pointer-over and pressed states are restated, because the theme would otherwise turn them grey.
- **Evidence key:** each strength is drawn by the graph's own edge routine in body ink: solid, dashed, and dotted with the open ring.
- **Legend:** the unmeasured entry is gone, and the hatch is keyed by a swatch drawn by the hatch routine itself. §6.6's unmeasured encoding is not drawn yet, because no pane plots a value that can be unknown; its legend entry comes with its first drawing.

The accent sits 12 CIE76 from TCP's ink in dark mode and 7.8 from Other sockets' ink in light. Selection is carried by the outline's shape and the stated selection rather than by hue, so the separation requirement binds the caution ink, which marks the canvas, and not the accent or the action fill.

Animate transitions, selections and layout settling only; never animate per-message activity in a whole-system view. Respect the platform reduced-motion setting: transitions are skipped without changing the final layout, selection or values.

### 6.7 Interaction reference and navigation math

The viewport is `[t0, t1)` with span `span = t1 - t0` over drawable width `W` device pixels.

| Input | Behavior |
|---|---|
| Wheel or precision-trackpad scroll over the plot | Zoom anchored at the pointer, `f = 1.25^(∓notches)` |
| Pinch | Zoom against the gesture-start viewport and the current focal point; `f` from the gesture scale |
| Primary-button drag over the plot | Pan |
| Horizontal wheel or two-finger horizontal scroll, or `Shift` + wheel | Pan by a tenth of the span per notch |
| Vertical scroll over lane headers | Scroll lanes |
| `Shift` + drag, or middle-button drag | Brush a time interval; never conflicts with pan |
| Wheel over the minimap | Zoom at the pointer; if it lies outside the brush, move the brush there first |
| Drag on the minimap | Move the viewport; drag an edge to resize it |
| Minimap keyboard focus | Arrows pan; Home/End jump; +/- zoom; 0 fits, shared with the timeline |
| Click a cell, mark, node or edge | Select it; update inspector and detail |
| `Ctrl` + click | Add to or remove from a multi-selection as an explicit predicate |
| Double-click a cell | Zoom by TUNABLE: 2.0 around the pointer |
| Double-click an edge | Open the channel view of the one relationship it stands for (§6.3); an aggregate edge opens nothing |
| Hover | Highlight only; never changes selection or filters |
| `Enter` on a selection | Focus: turn the selection into a filter |
| `+` / `-` | Zoom around the selection, else the viewport centre |
| Arrow keys | Left/right pan by TUNABLE: 10% of the span; with `Shift`, by one cell. At L0 and L1, up/down scroll visible lane rows, with Page Up/Down scrolling a page |
| `Home` / `End` | Go to the retained extent's edges |
| `0` | Fit the current analysis scope |
| `[` / `]` | Previous / next observation in the selected lane or channel is the target behavior. At L0 a selected mechanism lane steps to the previous or next occupied bucket in that lane; with All mechanisms selected it steps the whole machine. At L1 a selected process lane steps through that owner's occupied buckets; with Group totals selected it steps the group's focus. At L2 a selected source-direction row, and at L3 a selected channel end, steps through that row's occupied buckets. At an operation lane - an RPC channel's calls or a process's HTTP exchanges (§3.2's L4) - it steps the rung's own records bucket by bucket, as at any rung with no row selected; the table beside it steps call by call or exchange by exchange with `Up`/`Down`. At the evidence rung this moves to the previous or next record; elsewhere, with no row selected, it moves the analysis interval to the previous or next drawn bucket holding a record of the rung's focus. A zoomed view with nothing further pages on |
| `Tab` | Move focus between graph, timeline, lane list, ranked table and inspector |
| `Alt`+`Left` / `Alt`+`Right` | Navigation history back and forward: back ascends as `Esc` does, and forward re-enters the rung an ascent or a crumb left. Each restores that rung's filters, time, graph focus and lane grouping as one state (§6.4) |
| `F` | Toggle follow-latest |
| `Ctrl`+`F` | Focus search |
| `Ctrl`+`E` | Export the applied result |
| `Esc` | Cancel the gesture in progress, else clear the selection |

Every row has a visible button or menu equivalent and an accessible-table equivalent (R15). No behavior is reachable by gesture alone.

Revision 114 completes the timeline's rows of this table. It adds double-click zoom, pinch, `Shift`+arrow, horizontal and `Shift`+wheel pan, and `[`/`]`, and makes `+`/`-` zoom around the analysis interval. The pan and zoom rows are shared with the minimap's keyboard path. Revision 115 implements `Ctrl`+`F` as a bounded search of the current snapshot's group/process/channel metadata. An endpoint may match, but is not copied into a result snippet (P16). Search navigation validates a complete ladder path before changing rungs, and clears an interval brush before opening a whole-session hit. This is not the indexed, progressive content search of M4. Revision 132 adds forward history. The ladder keeps the rungs an ascent or a crumb left, nearest first; that list is always a way down from the current rung, one valid descent at a time, so it never holds more than the rungs below it. `Alt`+`Right` and a **Forward** button re-enter them; the button's tooltip and accessible name say which rung. The button stands left of Back, so Back does not move under a pointer that clicks it repeatedly. Forward history survives a live publication for as long as the new generation still has its rungs, and stops before the first rung the new generation lacks. The same revision fixed an ascent that dropped the brush while the ranking still counted inside it (§6.4). The lane rows are done: `Up`/`Down` and `Page Up`/`Page Down` scroll lane rows, and `[`/`]` step L0–L3 rows. Revision 160 closes the table's last open row, `Ctrl`+click.

Revision 160: `Ctrl`+click is a multi-selection of processes, an explicit predicate.

- **What it composes.** A graph node, or a ranked row that stands for processes (a process, or a machine-rung
  group), adds its processes to the set, or removes them when every one is in it already. A single selection
  already standing becomes the set's first member, as a list's selected item does when a second is `Ctrl`+clicked.
  A plain click, a clear or a navigation lets the set go.
- **What it shows.** The set is the selection. Its members are ringed in the graph and named in the inspector, and
  the timeline highlights their records with §6.4's highlight.
- **`Enter` turns it into a filter.** It opens the evidence rung scoped to exactly the chosen processes. The set is
  the rung's visible, removable filter, keyed by every member's instance identity (`ProcessSetFilter`), so it never
  becomes a group's current membership or a PID.
- **Equivalents (R15).** The keyboard has the ranked table's Windows pattern: `Ctrl`+`Up`/`Down` move the focus
  without selecting, and `Ctrl`+`Space` toggles the focused row, or in the graph the node the keyboard is on. Each
  ranked row's context menu toggles it, and the inspector's **Show their records** button is `Enter`.
- **Out of scope.** Channels do not join a set, because a timeline focus names one channel. A group-like rung over
  an arbitrary set would need the graph to expand several groups at once, so the set's filter is its records rather
  than its lanes.

Navigation math is pure and property-tested (R10):

```text
tc    = t0 + (x / W) * span                  # time under the pointer or focal point
span' = clamp(span * f, spanMin, spanMax)
t0'   = tc - (x / W) * span'
pan:    t0' = t0 - (dx / W) * span
brush:  [min(xa, xb), max(xa, xb)] -> half-open range, widened to spanMin when degenerate
```

each result then clamped by §6.2's overscroll allowance. Required properties:

- **Pointer invariant.** The time under the pointer is identical before and after a zoom, within integer-tick and pixel rounding tolerance. This is the contract; `f` and the tick floor are TUNABLE.
- **Inverse.** Zooming in and back out by the same factor at the same focus restores the viewport unless a clamp intervened, and the same holds for pan.
- **Bounded overscroll.** No sequence of gestures leaves empty edge space exceeding the allowance.
- **Pinch stability.** Pinch resolves against the gesture-start viewport, so one continuous gesture accumulates no drift.
- **Clamp independence.** Clamping depends only on the retained extent, the analysis scope and the viewport — never on graph layout state or an alignment revision (I10).

Lane order and graph positions freeze for the duration of an active gesture (§19.4). Zoom changes presentation and default scope only; it never changes capture policy or stored observations (I19).

### 6.8 Interaction quality, responsiveness and states

Fast usability is a set of testable rules, not an aspiration.

**Perceived latency.** Each window has a different obligation, and the obligation is on the UI thread, not on the query:

| Window | Requirement |
|---|---|
| Under 16.7 ms | Every pointer, wheel, pinch, key and drag produces visible motion from cached geometry and current transforms. Input is never gated on a query (R12, P23) |
| Under 100 ms | Hover feedback, selection outline, breadcrumb change, and lane reorder on an explicit command |
| Under 1 s | A coarse but correct answer for any level change, brush or filter, labeled as coarse while refinement is pending |
| Beyond 1 s | Progressive results, cancellable, with a determinate indicator where a bound is known and an indeterminate one where it is not, keeping the previous coherent answer on screen until the new one is complete (§6.4) |

Never raise a blocking modal for analysis work, never disable the canvas while a query runs, and never clear a populated view because a refresh is in flight.

Revision 127 measures these windows rather than asserting them. An opt-in benchmark (`INTERCAT_LATENCY_OUTPUT`) drives the real
window over synthetic sessions of 100,000 and 1,000,000 records and, when named, a real saved session.

- **What is timed:** a gesture's canvas cost is the timeline, minimap and graph building their drawing, recorded without
  rasterizing. That is the UI thread's share of a frame, not GPU time. Level changes, a brush and a search are timed
  from the gesture to their answer.
- **What it found:** each new generation's workspace opened its own store, and a fresh store hashes every file its
  generation names (ADR-025). That is about 0.4 s at a million records, before the first answer of every live
  publication and every reopen. The window's workspaces of one session now share one verified store.
- **Result** (`bench/results/interaction-latency-20260925T204410Z`), at 1,000,000 records:

  | Window | Measured | Budget |
  |---|---|---|
  | Pan canvas p95 | 9.7 ms | 16.7 ms |
  | Zoom canvas p95 | 7.8 ms | 16.7 ms |
  | Hover p95 | 26 ms | 100 ms |
  | Group level change | graph 48 ms, exact lanes 799 ms (1,328 ms before the fix) | 1 s |
  | Process level change | exact count 355 ms | 1 s |
  | Channel level change | exact count 476 ms | 1 s |
  | Brush to ranking | 342 ms | 1 s |

  The real saved session answers every window within 51 ms. The ranked table and graph answer a level change at once;
  the timeline's exact lanes follow.
- **Still open:** that measurement opened a 1,000,000-record session in 1.55 s. Revisions 152–163 took that away:
  since revision 163 a finished session of 4 or 10 million records reopens in about 0.17 s from its derivation
  checkpoint and persisted overview, opening no segment. The window's own open at that scale is still to be
  measured.

**Progressive disclosure.** The default surface carries only what §3.2's ladder needs: record state and profile, filter bar, basis and metric selector, lane grouping, legend, breadcrumb. Byte domain, accounting side, evidence policy, normalization scope and graph projection are one click away and always display their current value when it is not the default. A non-default setting is never invisible, because a silently unusual setting is how a user comes to distrust every number in the product.

**Defaults that need no configuration.** The first-run defaults of §3.1 are correct for the whole L0-to-L5 path. Once changed, a setting is remembered per workspace and shown as changed, and returning to defaults is one command.

**States are designed, not dialogs.** Empty, starting, permission-denied, provider-failed, unsupported-mechanism, no-match, loss-affected, alignment-unknown and retention-boundary are designed in-pane states. Each states what is true, why, what remains possible, and the one action that changes it, naming the specific source or setting rather than a generic failure (§20.6).

Revision 130 adds a capture that records but has published nothing yet, which read "No capture is running". The
header now reads "Recording · first view pending", and the empty rung and disclosure say when the first view comes. A
workspace with no session states no time scope and draws no axis for its placeholder extent. A saved session shown
before a capture gives way only once the capture records, so a declined approval or a failed start loses nothing.

Revision 135 fits the minimum window to a running capture. While a capture starts, records or finishes, the capture card
holds only its state and what can be done now, stop and pause. Starting another capture, opening a saved session and the
words about starting one wait for it to end, and their height goes to the ranked list: three records at 1080×700 where
one showed. A filter chip names what it narrows, as a crumb does ("Channel: …", "Records of: …"), so a channel's filter
and the evidence scope of the same channel no longer read as one filter shown twice. The chips wrap within the bar
rather than running off the window's edge. A channel's ranked row names its mechanism with the one display mapping (R5),
not the enumeration's own "Tcp", and cut text - a row's name, a crumb, a chip - shows whole in a tooltip.

**Discoverability.** The legend is always visible and names the active metric with its unit, domain, accounting side and intensity scale. Every visual claim is one action from its explanation: an `Explain` affordance on any cell, edge or ranked row opens the correlation rule, its version, the evidence IDs and the coverage that produced the mark. §17's prototype questions are the acceptance test for this, not a sentiment survey.

**Motion.** Animate only what helps the eye follow a change: level changes, selection, layout settling and the follow-latest advance. Durations are theme tokens, TUNABLE: 120 ms for selection and 200 ms for a level change. Nothing animates per message. Reduced motion skips all of it without altering a value, a final position or a selection.

## 7. Domain model and identity

### 7.1 Core entities

| Entity | Required meaning |
|---|---|
| CaptureSession | One recording/import, configuration, host/boot evidence, source clocks, adapters, retention and quality ledger |
| Investigation | References one or more immutable capture generations plus host mappings, alignments, annotations and saved views |
| ProcessInstance | Host + boot epoch + creation identity; numeric PID is an attribute, never the global key |
| ThreadInstance | Owning process instance + thread creation identity; numeric TID is reusable |
| Endpoint | Mechanism-specific address/name plus namespace, host, scope and observed validity |
| ResourceInstance | Pipe instance, section, ALPC port when known, socket or other object with a bounded lifetime |
| Channel | Relationship through a resource/connection incarnation, possibly one-sided or with multiple participants |
| Observation | Immutable fact from one source record, preserving original attribution and measurements |
| Operation | Derived logical I/O, message or call with optional start, end, status and participating observations |
| Relation | Observed or inferred link with evidence IDs, rule/version, validity range and quality |
| PayloadFragment | Content evidence, source semantics, parent observation, lengths, direction, offsets, truncation and protection policy |
| CoverageInterval | Mechanism/source capability and known interruptions or losses over a time interval |

Do not force all IPC into a `sender PID / receiver PID / bytes` row. Discovery, resource membership, observations and completed operations have different shapes.

### 7.2 Stable identifiers

A raw-record key is `(capture UUID, source stream ID, source epoch, record ordinal)`. Live ordinals are assigned at acquisition before parallel decode and are retained in the authoritative journal. One raw record may produce several observations: use `(raw-record key, normalizer contract version, deterministic fact key)` for observation identity. Correlation changes never replace these identities. ETL import uses the canonicalization contract in section 18.4; callback delivery order alone is not a reproducible identity when equal-time events come from different CPUs. Raw acquisition/delivery order and chronological display order remain separate.

A process uses a provider start key when available, otherwise host/boot/PID plus observed creation time. For a process already running at startup, create a provisional instance with evidence strength; do not invent a start event. Keep an alias-resolution table when better lifecycle evidence arrives. Handle reuse, object-address reuse, port reuse and section-name reuse through instance epochs and lifecycle intervals. Same executable path does not mean same process.

For endpoints, retain original and normalized names separately. Include terminal session, object-manager namespace, container/network compartment and IPv6 scope when available. Do not blindly lowercase every endpoint or strip prefixes before identity matching. An unavailable kernel address is not zero and must not collapse unrelated objects.

### 7.3 Observation schema

```text
Observation
  Id, CaptureId, SourceId, RawRecordReference
  ProviderGuid, EventId/ClassicType, Version, Opcode, SchemaFingerprint
  NativeTimestamp, ClockId, LocalRelativeTime, SourceOrdinal
  HeaderPid/Tid, SourceOwnerFields, SourceEndpointFields, SourceObjectFields
  Mechanism, Layer, Kind, Direction?
  ActivityId?, RelatedActivityId?, SourceCorrelationFields
  ByteValue?, ByteDomain?, ByteMeaning?, StatusDomain?, StatusCode?
  FieldAvailability, QualityFlags, PayloadReference?

EntityBindingRevision
  ObservationId, Revision, ProcessInstanceId?, ThreadInstanceId?
  EndpointIds[], ResourceInstanceId?, AttributionRule, EvidenceIds[], Quality

OperationRevision
  OperationId, Revision, Kind, EvidenceIds[], ParticipantIds[]
  Start?, End?, CompletionState, Measurements[], CorrelationRuleVersion
```

Source-derived facts remain immutable; subsequently resolved identities live in `EntityBindingRevision`, not writable observation columns. A materialized query row can join them for speed, with its binding revision in the cache key. `contracts/entities-v1.md` fixes the current binding rule, `process-binding-v3`: a record binds to the instance of the PID it belongs to whose witnessed lifetime holds its reading — `Direct` for the lifecycle record that creates, ends or confirms the instance, `Correlated` inside the PID's first instance, and `Candidate` inside a later instance of a reused PID, which the default evidence policy does not admit (ADR-013). A record belongs to the PID its own payload names, or, where the payload names none and its mechanism was measured to raise its records in the process they describe (today RPC, ADR-030), to the process that raised it. Provider start keys distinguish lifecycle instances when carried by `source-fields-v1`; older generations retain the witnessed-creation fallback. A reading no lifetime holds is unresolved with its reason and is never moved to the nearest instance. Field availability reasons include not exposed, profile-disabled, denied, event lost, schema unknown, redacted and not applicable. Quality is multidimensional: attribution, correlation, measurement and timing. A single confidence percentage would imply calibration that the product does not have.

Revision 172 gives an IPv6 endpoint a place in a row. `observation-v1`'s address columns are 32 bits and a table's column set is frozen, so a new table, `observation-v2`, is `observation-v1`'s 39 columns and then two 16-byte address columns (`segment-v1` §5). Revision 171 left open what that may cost a row with no IPv6 address, and the answer is nothing: a segment is written as `observation-v2` only when one of its rows has an IPv6 address, and every other segment is `observation-v1` byte for byte and stages the same bytes, so a session of IPv4 records publishes exactly the files it did. A digest measured with the previous build pins this. Every surface that reads an address reads the new columns in the same slice. A redacted package maps an IPv6 address into the documentation prefix, keeps `::` and `::1`, and gives an IPv4-mapped address its IPv4 part's pseudonym, so it still names that host (`redacted-session-v1` §3.2). The share report tokenizes an IPv6 endpoint by its address, not its port alone. The evidence text writes it in RFC 5952's form, bracketed before its port. No capture produces an IPv6 row yet: relating 128-bit ends, then admitting the TCPv6 and UDPv6 descriptors with a real-ETW loopback fixture, are the slices that follow.

### 7.4 Correlation contracts

Every correlator states its join keys, lifecycle scope, timeout, cardinality, ambiguity policy and evidence requirements. The concrete keys per mechanism cannot be settled from documentation alone: they are an M0 measurement, recorded in that adapter's capability descriptor (§4.3) and fixed in the correlation-quality ADR before the correlator is implemented. The contracts below constrain what any such rule may conclude. Produce `Direct`, `Correlated`, `Candidate`, `Unresolved` or `Conflicting` relationships with explanations.

* **Network:** join compatible tuples within host/compartment and connection lifetimes, using provider connection identifiers where validated. Account for reconnect and port reuse. Local loopback endpoints can identify both local owners when corresponding evidence exists. UDP association is scoped by observations, not fabricated connection state. Measured for TCPv4 (`contracts/relations-v1.md`, ADR-014): every admitted descriptor names the record's own endpoint first, so a record's other end is the holder of its mirrored endpoint pair when one instance holds it; the connection identifier is zero on the measured build and is not used. An end's records are divided into connection incarnations at the connects, accepts and disconnects the capture witnessed, so a reused port pairs each connection with its own other end; an end whose lifecycle was not witnessed is one incarnation, and a reused one stays ambiguous rather than split by time.
* **Pipes:** use object/lifetime evidence to identify instances. Name alone creates an endpoint grouping. Keep multiple same-name instances distinct; never pair each client with every server.
* **RPC:** use validated activity/call identities and role-specific lifecycle schemas. Thread nesting may supplement a validated synchronous path; it cannot generally pair async or interleaved calls. Interface UUID + procedure number is a grouping key, not a unique call ID. Measured for local RPC (`contracts/operations-v1.md`, ADR-031): the activity id pairs a call's start with its stop on one side of one process, as FX-RPC-001 paired 62 of 62 client calls that carried one and 61 of 61 served calls; the client and server sides carry different ids, so no call is paired across sides. A reused id leaves its overlapping calls ambiguous, a start with no stop is censored at capture end, and nothing is paired by time.
* **ALPC:** message ID plus host/boot/time and available lifecycle/thread evidence produces candidates. Reuse, missing sends/receives and repeated IDs prevent blanket uniqueness. Send-to-receive duration is not automatically server execution time. Measured in the lab (ADR-034, IC-006): ids are reused within seconds across processes - 40 to 125 distinct ids per run for up to 33,000 sends - so an id alone identifies nothing; but a client RPC call's one ALPC send on its own thread, the one receive of that id in another process before the call stops, and the server call that begins on the receiving thread within 5 ms link a client call to the call that served it, which carried the same interface and procedure in 10,003 of 10,003 links.
* **Shared memory:** connect verified mappings to a section instance. Membership alone is undirected and cannot establish writer/reader roles or transferred bytes.
* **Cross-layer:** link only through explicit identities or a documented qualified rule. Time proximity alone remains a candidate annotation and is excluded from definitive causal views.

Bound pending joins by count, age and memory. Eviction produces an unresolved reason. Capture ending leaves operations open/censored, not failed. A lost completion is not a timeout unless a source actually reports timeout. Late evidence adds a new correlation revision, invalidates affected aggregates, and preserves earlier snapshot reproducibility (I17, §24).

Revision 173 relates IPv6 ends: `transport-endpoint-relation-v4` keys an end by its family and 128-bit addresses, so an IPv6 connection or datagram flow finds its other end, its peer and its channel as an IPv4 one does. The family is part of the key, so the two families never meet. An IPv4-mapped address is kept as the source named it rather than equated with its IPv4 host, because no capture has shown the source needs that. The rule identity changes because relation-dependent answers now read IPv6 records, which v3 left without an end, as revision 57 changed it for UDP. The rule's own contract (`relations-v1` §8) requires it, and the first plan for this slice missed that. That would have cost every existing session its checkpoint and made its reopen read every segment again. So a checkpoint of v3 is read as v4's where the two are provably one state: where it counts no related record without an end, which is every scale-gate session and every capture whose transport records name both endpoints (`derivation-checkpoint-v1` §4, format 1.2). On this machine the §12 gates at 10M read the same under this build as under revision 171's, three runs each.

Revision 174 admits the kernel network provider's IPv6 descriptors, TCPv6 26-31 and UDPv6 58-59, under the IPv6 keyword beside the IPv4 one (ADR-029). What made it possible was not the capture callback but the schema: TraceEvent rebuilds a registered provider's manifest from TDH and writes a 16-byte IPv6 address as `win:Binary` with no length. A bounded read therefore could not know where any later field of the event starts, and every IPv6 field was refused as unknown. The adapter now asks TDH for each binary field's fixed length and out type and writes them back into the manifest before parsing, so the schema fingerprint covers them. A binary is read as an address only when the manifest says 16 bytes of `win:IPv6`. An admitted record copies the address whole into one of two address slots, and the journal projects it as `IAP2`, written only for a record that holds an address, so an IPv4 journal keeps its bytes (`normalizer-plan-v1`). Admission rests on measurement (P27). FX-TCP-002 and FX-UDP-002 ran the loopback truth workloads over `::1` on real ETW, and met every applicable §14.2 criterion at 100%, reproduced. The IPv6 descriptors name their endpoints as their IPv4 counterparts do. A recorded IPv6 session then paired its channels, attributed a UDP client's bytes to its server, and re-derived from its journal.

Revision 175 fits an IPv6 endpoint to the room the Desktop gives it, found by rendering the channel rung. A global IPv6 endpoint is up to 47 characters and an IPv4 one 21. Where space is bounded, the lane's endpoint line and a channel's breadcrumb and filter chip, an endpoint drops the middle of its address and keeps its port, which is what tells two ends of one host apart. The whole name stays in the tooltip and the accessible name. The same render showed a sliver of an earlier crumb beside the current position whenever the trail scrolled, over IPv4 too. A crumb the trail's edge cuts is no longer drawn.

Revision 293 fits the current crumb itself, found by rendering the RPC and HTTP channel rungs at the minimum width. A channel's crumb is up to 220 pixels, and beside the header's actions at 1080 pixels the trail had less, so scrolling to its end cut the current position's own start: "nel: RPC calls to svcctl (Service…". The current crumb is now narrowed to the trail when it is wider, and ends in an ellipsis instead ("Channel: RPC calls to svcct…"); a wider window gives it its own width back, and its whole name stays in the tooltip and the accessible name. The IPv6 channel's test of its crumbs had failed on Linux since revision 287, taken for a difference of fonts; it was this defect, which Linux's wider fallback font made its channel's crumb wide enough to show, and it now passes.

Revision 294 does the same for a text field's placeholder, from the same renders. The rail's search box was cut at its edge, mid-glyph, so at the rail's default width it said "Search names or PID (Ctrl+I", naming a shortcut that is not its own. A field scrolls its text sideways and so let its placeholder be as wide as its words; every placeholder is now held to the width its field shows and ends in an ellipsis, in every window. The search box's tooltip says its placeholder whole, and Ctrl+F is its accelerator for a screen reader.

Revision 295 corrects how two panes wrote a PID, from the same renders. The inspector wrote a selected process's PID as a quantity, "PID 8,204", where the graph, the rows and the crumbs write the identity the operating system gave, "PID 8204"; it now writes it as they do. And a process no executable names, which the graph labels by its PID, drew "PID 100" over "PID 100"; its node now has no second line.

Revision 296 makes an interval past the clock's range answer rather than fail. The command line's `--interval` takes raw session ticks, and one ending some three thousand years on overflowed converting its bound to the clock's readings: the byte, peer and call rankings and the interval count each failed with an overflow, where the timeline already declined such a bin. A bound beyond every instant the clock can read now lies after every reading, or before every one (`metrics-v1` §5), so such an interval holds the readings within it, and one starting there holds none. Nothing was declined instead: an interval reaching past the end holds every reading from its start, and answering nothing for it would have been a zero that is not one (R21). `icat metric`, which converts a bound written with a unit itself, refused one beyond the clock's range; it now takes an end past it, or a start before it, the same way, and refuses only a bound that leaves the interval holding nothing, saying so ("'90000000000s' is after every reading this session's clock can hold, so the interval holds none").

Revision 297 makes an interval's length a tick count (I3), found by giving the command line intervals no session holds. `icat timeline --interval -9223372036854775808:0`, whose end is more than 2^63 - 1 ticks after its start, failed with an overflow, and so did `icat metric --metric rate` over such an interval. `icat export` took one and described it in the wrong unit, its length wrapped negative. A time range could hold such bounds and overflowed only where something later measured it. It now refuses them where it is made, so measuring one cannot overflow, and whatever reads bounds from outside asks first. `evidence`, `export`, `metric` and `timeline` say the interval is longer than any interval and ask for a narrower one. A workspace file with a saved view that wide is refused as one that shows no interval, and a persisted overview with an extent that wide is refused in those words and its timeline is counted from the segments. Converting a presentation interval to a clock's readings can still reach further than a tick count: a bound past the clock's range stands for every reading on its side (revision 296), and a 3 GHz cycle counter makes 300 readings per tick. Such a native interval is held as the widest range there is, centred on the capture's epoch. Nothing is lost by that. Every reading a session admits lies within its clock's plausible distance of the epoch, a matter of days, and the range held reaches about 14,600 years of 10 MHz readings, or 48 of a 3 GHz counter's, either side. A timeline column that wide is judged on that range, where it once failed the whole timeline.

Revision 298 has a refused metric request say what would complete it, found by running `icat metric` the way a person first does. `icat metric <session> --metric bytes-sent` was refused because it named no byte domain: "name one of TransportObserved or CompletedIo". It never said which option names one, and a guess such as `--accounting-side` for the side that follows is an unknown option. A refusal about one part of a request now names that part and the values it takes in that request (`metrics-v1` §2). It names none when the part is right only left out, which is a domain or side the metric fixes or has none of, the layer it implies, or a numerator on anything but a rate. `icat metric` turns that into the option, for example "Name it with --side: SendSide, ReceiveSide or CanonicalOwner." or "Leave out --layer.". A duration without its interval lists the intervals its basis measures, which the reason gave only in prose. A rate's numerators are listed as numerators, where they were headed "Metrics defined on a SourceObservations basis".

Revision 176 opens RPC's path to L4, the source-specific, qualified start/completion correlator §19 reserves L4 for. RPC alone costs a median 0.52 CPU pp over seven pairs (Low), and FX-RPC-001 still pairs 12 of 12 truth calls with their completions through the activity id. What stands between RPC and Explore is attribution, not cost. The provider names no process in its payload, and entities-v1 binds only a payload owner, so its records would arrive held by no one. The order is therefore: a binding rule for the process that raises an RPC record, measured per side; call operations paired through the activity id; one-sided RPC channels at L3 and their calls at L4; and only then the measured class that lets Explore take the source.

Revision 177 is that binding rule, `process-binding-v3` (ADR-030). §4.1 keeps a kernel record's header as context, because the kernel raises a record in whatever process it was in. A user-mode provider raises a record in the process it describes, and for RPC that was measured on each side: FX-RPC-001 started every truth call in the calling process and saw 122 of 122 server-side calls to its interface raised in the service host the workload recorded. So a record whose payload names no owner belongs to the process its header names when its mechanism was measured to raise its records there, and only RPC's was. It is a binding rule, not a normalization: the stored row keeps its empty owner, and re-deriving an older session applies it. Every reader of an owner reads it through the one rule, and an RPC server no lifecycle record names becomes an activity-only process of its own calls. The same measurement bounds what comes next. The activity id pairs one side's start with its stop and never a caller with its server, so an RPC call's other end stays unresolved until a relation rule proves one (P7).

Revision 178 derives the first operations: RPC calls, under `rpc-call-operation-v1` (`contracts/operations-v1.md`, ADR-031). The correlator was stated before it was written, as this section requires: its join key is the process a record belongs to, its side and its activity id; a start opens a call and the next stop of that key closes it; nothing is paired by time or expires. A start still open at the end is censored, a stop without its start is given none, and a record with no id, or of an id reused before its stop, pairs with nothing and says why. Every call record of the generation is read before any is paired, so a segment cut or a late delivery changes no call. Replaying FX-RPC-001's committed records reproduces the measurement: 62 client calls and 61 served calls, with the durations the evidence file gives. `icat operations` lists them by process, side and interface. The logical-operations metric basis, L3's one-sided RPC channels and L4's call lanes read these calls next; none of them is built here.

Revision 179 puts those calls on the ladder. A process that made RPC calls lists them at its rung as channels of its own, one per side and interface, ranked by records among its paired TCP channels; unlike a paired channel, an RPC channel belongs to one process, so a process's channels add up to its calls. A channel lists its calls a page at a time, each leading with how it went, and a call's start and stop records are one step away, as every rung's records are. The overview holds no calls: a process's channels are read when its rung opens, only for a process whose own records include RPC, so a session without RPC reopens exactly as before. A channel and a call are named by their instance, side, interface and first record, never a position, so a live publication keeps the rung. The timeline does not yet draw a channel's calls apart from the rest; that and admitting RPC to Explore come next.

Revision 180 admits RPC to Explore under its measured class, Low, and the first real recording with it found what no fixture could: a 32-bit process raises its RPC records at pointer width 4, and admission refused every width but the plan's, so those calls were missing and the ledger marked the whole capture as a partial gap for RPC. A descriptor whose admitted fields all lie before any pointer-sized field decodes at the same offsets at either width; its plan now says so and admits both, and the journal keeps each record's own width (`contracts/normalizer-plan-v1.md`). A second recording held the 32-bit caller's calls, paired, and replayed exactly. A descriptor with a pointer before an admitted field still admits only its own width.

Revision 181 draws an RPC channel's calls in the timeline, §3.2's L4 picture of individual operations with duration bars and status, at the channel's rung where its calls are listed: under the machine row, each call is a bar from its start to its stop, a failure in caution ink, a call open at capture end faint, and one with a single record a mark. Calls that ran at the same time stack. On a real session they were first packed by pixels, and calls lasting microseconds stacked as though concurrent; they are packed by time. A view draws at most 4,000 calls and says how many more it holds, which §6.2's density regime will lift. A click on a call selects its row rather than brushing time, because a call is an operation, not an interval.

Revision 182 tried the RPC rungs in the real window rather than a headless render. The app now opens a session folder named on its command line, which let a person-free run open a real recording, walk from the machine to one RPC call through UI Automation, and capture the window alone. It found a process rung whose only rows were RPC channels stating "0 observations", because the rung total counted paired channels alone, and call bars capped so low that a tall window showed specks; both are fixed. Headless renders had shown neither: one needs the full-size window, the other a process with no paired channel.

Revision 183 counts those calls on §5.3's logical-operations basis, which every session had answered as unavailable since the matrix was written (`contracts/metrics-v1.md` §8a, ADR-032). A call is counted by the record that puts it in scope, as §21.1 requires of an operation from 0.5 s to 2.5 s: a started count takes a call by its start, a completed count by its stop when that stop is paired with its start, and an error count the completed calls whose stop reports a status other than 0. A start still open at capture end is started and never failed. A stop paired with no start is stated beside the count, with its status, rather than counted, so the completions a metric counts are the ones `icat operations` and the ladder count. A completed call whose stop carried no status is unknown, and an error count of only such calls is unmeasured, never zero. `owner(P)` and grouping by process, executable or mechanism answer as they do for records, and the groups partition the count. What needs an operation's other end stays unavailable: a participant, sender, receiver, peer or between filter, a peer grouping or a peer count, because no rule pairs a client call with the server call that served it. So do durations, whose cohort a request cannot name yet. Transport records are never renamed as calls, which asserts P4 for the first time: the records beneath a call add no operation, and the matrix refuses a count of both. An answer names `rpc-call-operation-v1` as its correlation revision, with the binding rule beneath it; the canonical corpus gains three lines and changes none. On revision 180's real Explore minute, `icat metric` counts 2,007 calls started and 2,000 completed, none of them failed, the completions `icat operations` lists, and states the seven stops no start is paired with.

Revision 184 measured what pairing costs before choosing how to keep it, and made it several times cheaper instead. A new opt-in measurement (`RpcPairingScaleTests`, `bench/results/rpc-pairing-*`) builds sessions of 1M and 10M records, four in ten of them RPC call records carrying their source fields: 200,000 and 2,000,000 calls. Revision 183's build paired 2,000,000 calls in 4.9 s with every column in memory, so a reopened session's first RPC rung took 5.4 s and each late live generation of the 10M session 5.3 s. Most of it was sorting four million large records by key, and reading each field row whole to join it to its start. The pairing now sorts the records' readings as plain integers, breaks a tie of readings by raw locator as §3 orders them, and walks every key at once, holding only the keys with a call open; the fields are read a column at a time, and a field a row names twice is still refused. The same sessions now pair in 1.3 s, open the first rung in 1.8 s and pay 1.7 s for the last live generation; 200,000 calls pair in 108 ms. Old and new builds list the same calls on the real Explore session, on the 1M session and on forty random sessions with reused ids, shared readings, late delivery and fields in later chunks. The one change is the order of calls whose first records share a reading: by raw locator, as the contract now says, rather than by where a segment cut stored them, which compaction could change. Extending the calls between live generations, which a live session still pays in full at every generation, comes next.

Revision 185 set that extension aside on the evidence and did what a real session needed first. This workstation records about 2,000 RPC calls a minute, so a ten-minute capture pairs some 20,000, which now costs tens of milliseconds a generation; an extension that stays exact must re-pair every key a late record touches and still regroups every call, so it waits until a real capture needs it. A busy channel, though, already holds more calls than the call lane drew: a view with more than 4,000 drew the first 4,000 and said how many it left out, so a whole-session view of a busy channel showed its first minutes and then nothing. Such a view now falls back to §6.2's density rather than drop a mark (R21). Every call in view is read once into columns about five logical pixels wide, §6.2's minimum drawn width, so each is a pointer target without widening; each column counts the calls running in it - the overlap count of §21.1, so a call spanning columns counts in each - and the failed ones among them. A column's height is its count on a log scale above the occupied floor, and its failed share is drawn in caution ink on top, so a failure survives zooming out. A resting pointer names a column's calls and failures, a click selects its interval as a bar's would, and zoomed in to the budget the lane draws each call again. The real app drew it over a 90-second recording in which the RPC truth workload made 8,930 calls to the service control manager: density from the workload's start to the capture's end, a column selected by a click, and each call again at 2.5 s. The other lanes' density regime, with one-device-pixel columns and the widening of isolated runs, remains open.

Revision 186 answers `Duration` on the logical-operations basis (ADR-033, `contracts/metrics-v1.md` §8a). A request names the interval it measures, because §5 keeps a client call and a server execution apart: a client call spans the transport and the server's work. There is no default interval. The cohort is the calls completed in range unless the request names those started in range (§19.2), and the value is the median unless it names the 95th percentile or the maximum; both defaults are written out before a request is identified. A call of the cohort is measured only when both of its records are in the evidence. A stop whose start came before the capture, a call still open at capture end, and a record with no activity id or a reused one are stated, left- or right-censored, and never given a duration. The answer is a distribution by nearest rank, with the summed call time beside the busy time their union covers. Groups rank by the statistic, slowest first; a remainder is its calls' own distribution, and a result says its groups do not add up to its value. On a real 30-second recording, 3,945 client calls took a median of 44 µs and a 95th percentile of 249 µs; one took 11 s, and the calls summed to 22.2 s, 21.6 s of it busy. `EN-DurationInterval`, `EN-Cohort` and `EN-DurationStatistic` join §23, and the canonical form writes them for a duration alone.

Revision 187 is IC-006's ALPC spike, measured in the lab and admitted to no profile (ADR-034). An RPC call's other end has been unresolved since ADR-004 found that the two sides carry different activity ids, and ALPC was left unmeasured because it is a kernel flag group, which only a system logger reads, and ADR-002's capture creates none. A lab probe, `tools/InterCat.AlpcProbe`, owns a private system logger of its own for the length of a run: uniquely named, never the NT Kernel Logger, and stopped when the run ends. It ran FX-RPC-001's calls to the service control manager four times with no event lost and no session left behind. An ALPC message id turned out to be no identity: runs used 40 to 125 ids for up to 33,000 sends, most of them shared by several processes. But a chain of three steps links a client call to the call that served it: the client's one send on its own thread inside the call, the one receive of that id in another process before the call stops, and the server call that begins on the receiving thread within 5 ms. Across 10,003 links the two calls carried the same interface and procedure every time, no server call was claimed twice, and every reply came back. One message received twice within a call was left unlinked, as the rule requires. What follows is the capture - an ADR amending ADR-002 for a private system logger, with ALPC's cost measured, at 1,600 to 2,500 events a second here - then the relation rule, and then RPC peers in the graph with ALPC as transport evidence beneath the calls, never a second count (§5.1, M3's exit gate).

Revision 188 measured what collecting ALPC costs, before any capture path is built for it. In seven alternating pairs of the RPC workload, the probe's system logger, with a consumer that only counted events, cost a median of 1.86 CPU percentage points at about 1,160 ALPC events a second, with nothing lost. That is collection alone, a lower bound on a capture that also admits and stores the records, and it is already Moderate. A second series ran while other work loaded the workstation to 40–98% busy with or without the session, and is kept as a record rather than as evidence. ALPC therefore cannot join Explore, which admits sources measured Low (§14.2): once a capture admits it, it is an opt-in profile for resolving RPC peers, with its own measured class. §17's question of which IPC breadth Explore carries by default gains a measured answer for ALPC: none.

Revision 189 gives the ranked table §6.1's metric selector over §5.2's first metrics beyond records. The machine and group rungs rank by records, as before, or by bytes sent or received: the transport-observed bytes of each process's own send or receive records, sender- or receiver-accounted, which is what `icat metric --group-by process` answers for each instance; a group sums its processes, which partition it. The bytes are read in one pass, off the UI thread, for the scope the rows count: the whole session, a brushed interval, whose bytes are read beside its counts so the rows change once, or the visible range. They rank the rows only while they answer that scope; until then the rows keep their records ranking and the rail says the bytes are being read. §5.2's Unmeasured group is a row whose records recorded no size. It reads "unmeasured", never zero, and ranks after every measured row, a measured zero included; a row that made no such record reads "no sends" and ranks last. A rung whose rows are channels keeps ranking by records, and the selector steps aside there. A live publication keeps the choice and shows the previous publication's bytes, marked as updating, until its own arrive. An export names its ranking with a caveat defining it, and gives each row its ranked value and the records that measured it or recorded no size; `icat export --rank-by` writes the same file, and the metadata-only sharing report admits the ranking's name and those three numbers. On a 25-second dense recording, bytes sent put worker.exe first, 2.8 MB on 1,296 sends, where records put chrome.exe first, and every group's value equals `icat metric --group-by executable`. §5.2's other ranking metrics, the basis selector and a channel rung ranked by bytes remain open.

Revision 190 adds RPC calls made and RPC calls served to §6.1's metric selector: the logical-operations basis beside the source records' bytes. A process's calls made are the client calls it completed, and its calls served the server calls, each counted by its stop as `metrics-v1` §8a counts a completed call and bound by the call's first record under the evidence policy. The two sides of one local call are separate operations, made by one process and served by another, so each side ranks on its own and neither is a share of the other. Beside each count stand the calls whose stop reported a failure and the stops paired with no start, which are stated and never counted; a process with only such stops ranks after every process that completed a call. One pass counts both sides for the scope the rows count, from the calls the generation derives once. Where the coverage ledger says the capture did not collect RPC, no call ranks the rows: they keep their records ranking, and the rail and an export say why, since an absence of calls there is not a count of zero (R21). Where RPC's coverage is less than covered, the note and the export state it. An export names the ranking (`rpc-calls-made`, `rpc-calls-served`) and gains a failed-call column, which the sharing report admits too. On a 20-second recording while the RPC workload called the service control manager, calls made put the workload first (1,514) and calls served put services.exe first (1,537); made and served together equal the 4,298 completed calls `icat metric --basis logical-operations` answers, and each executable's count equals its row there.

Revision 191 live-tested the command line and the Desktop over a fresh 15-second recording with TCP, UDP and RPC workloads running in it: every `icat` inspection command, the exports and both redacted presets, and in the window search, the descent from a group to a channel, its evidence and an original record. Two defects were found and fixed. The general help did not name `export --rank-by`, which revisions 189 and 190 added. The original-record window wrote the record's raw identity as a C# record prints itself, with its type and field names; it now reads "Raw record: capture … · stream … · source epoch … · ordinal …", and a test holds it to words (R15).

Revision 192 takes the byte ranking to a process's rung, whose rows are its channels. The byte read also sums each end of every channel the overview draws: the transport-observed bytes of the records its owner holds there, bound as a process's records are. A process's TCP channels rank by its own end's bytes sent or received, with §5.2's Unmeasured group and rows with none as at the rungs above; its RPC channels read "no size" and follow, since RPC carries none. A call ranking leaves this rung by records, and says so: its RPC channels already list their calls, and no TCP channel carries one. An export at the rung is ranked the same way and says its bytes are this process's on each channel. On a 15-second recording of the TCP workload, the server's rung ranked its three connections by the bytes it sent on each, 141, 128 and 111 KB, above its RPC channels.

Revision 193 makes a process's RPC channels and a channel's calls answer the scope the rung counts. Under a brush or a zoomed view, the paired channels beside them counted the interval while the RPC channels still counted the whole capture and said nothing of it, and a process with no call in the brush lost its RPC rows altogether: two panes disagreeing about one scope, which M2's exit gate forbids. Within an interval a channel now holds the calls the interval holds by the record that counts each (`operations-v1` §5b), with those calls' failures, unpaired stops and durations and the call records read within it, and its rung lists exactly those calls. A channel with no call in the interval stays, counting none, as a paired channel does, and an interval holding every reading answers exactly as the whole capture. The previous scope's rows stay on screen until the new ones are read.

Revision 194 stops calling a real session's bytes unknown. The overview sums no bytes, so "bytes unknown" in its totals, rows, inspector, hovers and relationship table said nothing true: the records carry sizes no view had read. Now a selection or the relationship table reads the bytes of the scope the rows count through the byte ranking's per-scope read, and says so while it does. The inspector states the selection's own bytes sent and received, each measured or stated as unmeasured (R21). A relationship carries the bytes sent across its channels by either end, each transfer counted once at its sender, in its hover and its table row. Totals no longer name bytes they never summed. A view of a session without its directory, which cannot read them, keeps saying they are unknown. On a 12-second recording of the TCP workload, selecting its group read 132 KB sent and 132 KB received.

Revision 195 sizes the graph by the ranking's metric, as §6.3's encoding table requires so that the two panes cannot disagree about magnitude. Since revision 189 the ranked table could rank by bytes while the graph still drew edge thickness and node size from records. Under a byte ranking whose bytes are shown, an edge is now as thick as the bytes sent across the relationships it draws, each transfer counted once at its sender, and a node as large as the bytes its members' relationships carry, each relationship once, on the same log scale. Hovers name the scale: "Thickness: bytes sent across, log scale against the busiest drawn edge, 5.3 KB". Record counts stay what the texts and the in-brush dimming read. A call ranking leaves the graph of TCP relationships sized by records.

Revision 196 adds two more of §5.2's ranking metrics, from reads the selector already makes. **Bytes sent and received** is endpoint activity (`metrics-v1` §4, §5.1): every transport-observed contribution of a process's own records, sends, receives and those that state neither side. It counts a local transfer at both of its ends by design, and the note, the definition and an export's caveat say the rows' sum is not a transfer total. **RPC errors** are the completed calls a process made or served whose stop reported a status other than 0. A call whose stop carried no status is unmeasured, never a success, and a row with only such calls ranks after every row that knows its outcomes, with a measured zero among the known. Both equal `icat metric`'s `endpoint-activity-bytes` and `errors` grouped by process over random sessions, whole and within an interval. `icat export --rank-by` names them `bytes-sent-and-received` and `rpc-errors`. Still open among §5.2's metrics: rate, peers and local duration statistics.

Revision 197 fixes what a live pass on a 20-second dense capture found. The capture's ledger covered every interval, yet every quiet interval of a process's source-direction rows was hatched unknown, because each row judged coverage by what it observed; the rows are now judged by the capture's coverage, as process lanes have been since revision 165. Every ranked row of a real session said "unknown coverage": a process or group row now states the capture's coverage over the scope it counts, the whole session or the brushed interval, as the worst of every mechanism the capture collected (§10.3), and a paired channel states TCP's. The channel rung said "bytes unknown" of a channel whose records carry sizes; it now states the bytes sent across the channel, each transfer counted once at its sender, as the channel's relationship does. A real session's interval table said the same of every interval, since its timeline sums no bytes per interval (`overview-index-v1`); it now leaves the byte column out and says how an interval's bytes are read. The relationship table says a relationship sent nothing across, or that none of its sends measured a size, apart from bytes not yet read or that could not be read. At a process's rung a paired channel reads by the process at its other end, its own port first and a host both ends share said once (`:48048 ↔ :48058 on 127.0.0.1`): the rail had cut every channel of a server to the same `127.0.0.1:48048 ↔ 1…`. The tooltip, the crumb and exports keep the channel's own name. The relationship table names each end with its PID, and a process's role reads in `icat processes`' words rather than the enumeration's name (R5). The rail widens with a wide window, to at most 400 pixels, until its edge is dragged or moved by the arrow keys, and the paired-channel browser opens from a group's rung as well, scoped to the selected process.

Revision 198 makes the relationship table the graph's table equivalent below the machine rung too (R15). Since the graph drew only a rung's neighbourhood, the table still listed every relationship of the session, so at a process's rung it listed dozens the graph did not draw and the rung was not about. It now lists every relationship with an end in the rung's neighbourhood, which the graph draws between its processes or out to Rest of the machine, and a caption beside its title names the neighbourhood as the graph's caption does and counts the relationships elsewhere, listed at the machine rung, so none is dropped without a word. The machine rung lists every one, as before.

Revision 199 adds §5.2's local duration statistics to the selector: **RPC call time** and **RPC serve time**, the median time each process's completed client calls took and the median time it took to serve its completed server calls, slowest first (`metrics-v1` §8a's `ClientCall` and `ServerExecution` intervals, the calls completed in scope, by nearest rank). They come from the read the call rankings already make. A stop paired with no start is never timed: a row with only such stops is "untimed" and ranks after every row that timed a call, then rows with no call. A median does not add, so a group's value is its members' calls taken together, read with the calls, and never a sum or mean of their medians; the note states the median over the rows shown the same way, and a row's second line names how many calls stand behind its median, since one slow call can outrank hundreds. Both equal `icat metric --metric duration` grouped by process over random sessions, whole and within an interval, and by executable for a group. `icat export --rank-by` names them `rpc-call-time-median` and `rpc-serve-time-median`. A live pass on a 20-second dense capture ranked lsass.exe's 535 served calls at a 20.6 µs median. Still open among §5.2's metrics: rate and peers.

Revision 200 adds §5.2's **peers** to the selector: the distinct process instances at the other end of each process's records, under `relations-v1` (`metrics-v1` §6.1's `ActivePeers`), a process connected to itself its own peer. A record whose other end is unresolved, or bound more weakly than the evidence policy admits, names no peer: each count is a lower bound beside those records, and a row none of whose records resolved a peer reads "unresolved" and ranks after every row with one, never at zero. Distinct counts overlap - two processes are each other's peer - so a group's value is its members' records together, read with the processes' own, and the note counts the processes with a peer rather than adding the rows. A read of its own counts it from the derivation the overview already holds, with each row's other end cached beside its owner (`SegmentBindings`), rather than the separate derivation `icat metric` makes, and equals `icat metric --metric active-peers` grouped by process over random sessions, whole and within an interval, and grouped by executable for a group. On a 20-second dense capture it ranked queue.exe, the worker pool's hub, first with 28 peers, as the command line does. `icat export --rank-by active-peers` names it. Rate, the last of §5.2's metrics, is its numerator per second over one interval: it orders the rows as its numerator does, so it waits for a per-second display rather than a ranking of its own.

Revision 201 makes the ranked table's selector §6.1's basis and metric selector. Its ten metrics span two of §5.3's bases, and nothing on screen said which a ranking was on, although §3.2 keeps basis and metric on screen at every rung. The list now orders them by basis under a heading each - **source observations** (records, bytes sent, received, and both, peers), each record as the capture recorded it, and **logical operations · RPC calls** (calls made and served, errors, call and serve time), each call paired from its start and stop records - and the chosen metric's basis stays beneath "Rank by" as "observations" or "operations", in full on hover and to a screen reader, as each option's spoken name ends with its basis. One selector rather than two keeps every metric one choice away; a basis is never chosen apart from a metric it holds, so a separate basis control would only add a step. Resource topology joins the list when a topology is derived.

Revision 202 fixes what a second live pass found, driving search, the evidence rung and the original record by keyboard on a 20-second dense capture. A record whose payload names no owner, such as an RPC event, is owned by the process that raised it (ADR-030), and a screen reader heard "owned by raised by gateway.exe"; the record's sentence now says "raised by", "owned by" or "with no owner process named" as the record has it. The original-record window named a record's provider only by its identity and gave its envelope's codes as enumeration names (`ApprovedMetadata / Retained`, `encoding Qpc`); a public provider is now named beside its identity, and the codes read as words. Search, the evidence rung and the window otherwise read as intended. Left open: the source's connect and accept events carry a size field, admitted as a byte count that is always zero, so the evidence list says "TCP accept · 0 B" and endpoint activity counts those records as measured zeros; whether that field measures anything is a normalization question for a later contract revision, not a display one.

Revision 203 settles what revision 202 left open. Microsoft-Windows-Kernel-Network's connection events - a connection attempted, established or closed, over IPv4 and IPv6 (events 12, 13, 15, 28, 29 and 31) - carry the `size` field its transfer events do, and the source catalog admitted it as a transport-observed byte count for all of them. The provider's own messages state a byte count for a send, a receive and a retransmission and none for a connection event, whose field read zero on every one observed. Admitted, it made each such record a measured zero-byte transfer: the evidence list said "TCP accept · 0 B", and endpoint activity counted those records among its measured ones (R3: an unknown or inapplicable size is never a zero). The catalog now admits a connection event's fields without its size, so its records state no size and take no part in a byte total. This is a change to the capture's admission intent, not to normalization: a session keeps the plan it was captured under, so an earlier session reads, and re-derives, as it always has; captures and imports from now on admit no size there. On real ETW, `icat measure tcp` still computes TrafficVisualization with every criterion met (64 of 64 transfers byte-measured, 37,313 bytes equal to the truth log), and a recorded capture's connect, accept and disconnect records report their size as not applicable while its sends and receives carry theirs.

Revision 204 lets a metric query reuse the derivation a finished session already holds. `SessionMetrics.Evaluate` derived the generation's process instances, and on demand its transport relations and RPC calls, from every segment for each answer, beside the checkpoint that holds the first two. It now takes a caller's derivation of the generation (`MetricDerivations`) and uses it only when its session, generation and manifest digest are the ones the answer leases; otherwise it derives as before, so a derivation of another generation is never used. `icat metric` and `icat processes` pass the derivation the overview's own cache gives, from the checkpoint when it covers the generation's segments. Answers are the same ones: over random sessions with a checkpoint, eight kinds of metric, grouped and ungrouped, whole and within an interval, answer identically with and without it, and the relations and calls they need come from it. The saving is the derivation's: on a 1M-row session, `icat metric --metric active-peers --group-by process` went from 1.85 to 1.64 s, while a records count by process stayed at 1.0 s, because opening and verifying the store (0.86 s for `icat session`) and binding each row dominate there. Both remain for a later slice.

Revision 205 marks §6.7's multi-selection in the ranked table itself, which revision 160 left to the graph and the inspector. A row whose process is in the set, or a group all of whose processes are, carries an accent bar at its edge; a group only some of whose processes are carries a thinner one, as when one of an executable's instances was Ctrl+clicked in the graph; and a screen reader hears "in the selection" or "partly in the selection" with the row. The marks are set on the rows as they are drawn, not by rebuilding the rows, so the keyboard focus a Ctrl+Down and Ctrl+Space selection moves stays where it was. Channel and record rows stand for no process of their own and are never marked.

Revision 206 gives a real session's interval table the bytes revision 197's live pass found it without. The persisted overview counts records and sums none (overview-index-v1 §4), so once the table is shown the rows it lists are read, in one pass over exactly the records their counts count: every record, one mechanism's lane, one process's own records or one source direction of them, or the records made at one end of a channel. Each row states what those records sent, under sender accounting, and received, under receiver accounting, with the records that stated no size counted apart. The rows speak of records, not of the machine: an interval with no transfer reads "no transfer recorded" and a side with none "no receive recorded", because "nothing sent" beside an interval of unknown coverage would claim what was not observed (§10.3). Where a focus's count stands beside a row's own, the caption says the bytes are every record's, not only the focus's. A timeline bucket's hover states the same bytes once the table has read them, and a hidden table reads nothing. `icat timeline` reaches every listing with `--mechanism`, `--process` (with `--direction`) and `--channel` (with `--end`), and `--bytes` adds each interval's sent and received bytes (R18); a process's own lane there is judged by the capture's coverage, as its lane in the window is. At 1,000,000 rows the read adds about 70 ms to `icat timeline`. On a 20-second dense capture every listing the window showed matched the command line row for row. Persisting byte sums with the overview remains what the scale tier, and a graph or timeline drawn by volume without a read, need.

Revision 207 draws §6.6's unmeasured value, which waited for a pane that plots a value that can be unknown. Under a byte ranking the graph is sized by the bytes sent across each relationship, and one whose sends recorded no size was drawn at the thinnest thickness, as if nothing had been sent (R3). It is now an open band as wide as the widest edge, its sides in the mechanism's hue and evidence pattern, cross-hatched inside and never filled; a node none of whose relationships measured a size is an open cross-hatched disc at the smallest size. Neither adds to the scale the others are read against. The legend keys "Size not measured" with a sample drawn by the same routine, only while the graph draws such a mark. Each mark's card says which it is: "Bytes: unknown · 1 send recorded no size", and why it has no thickness, while a relationship with no send at either end reads "nothing sent across", zero rather than unknown; before, both read unknown. A partly measured mark is drawn by its measured part, a lower bound its card qualifies with the sends that recorded no size.

Revision 208 fixes what a live pass over §11.3's sharing paths found on a real capture. A redacted package, an original package, a ranked export and the metadata-only report were all made from a 20-second dense recording: the package reproduced the source's per-interval and grouped byte totals exactly, the original reopened as the same generation and digest, and the report named no host, path or process. Opened in the window, though, the redacted package still offered "Share redacted session…", which the builder refuses because a package is not built from a package, and "Share original session…", whose confirmation called the copy "Unredacted… the session as it was recorded". A redacted package is now shared as it is. The first action is unavailable, and its tooltip says why. The second reads "Share this package…". Its confirmation, folder picker, progress and result describe the copy as the package's pseudonyms and synthetic records, under the redacted package's own warning. `icat package --original` heads such a copy "Copy of a redacted package" and says the same. `contracts/original-evidence-package-v1.md` §4 no longer lists "that it is unredacted" beside "whether the session is itself a redacted package", which contradicted each other for a package.

Revision 209 adds §5.2's rate, the last of its metrics, as a way of reading a ranking rather than a ranking of its own. A "/s" toggle beside Rank by states each row's count or sum per second over the whole interval the rows count: the value over that interval divided by it (metrics-v1 §7). The rows therefore keep the order of their totals, which each row's second line still states. A median or a distinct count has no rate, and the toggle steps aside for them. A whole session states no interval to divide by: the span between its first and last record is not one, which §7 refuses. Its rows keep their totals, and the note says to brush an interval or zoom; the choice stays set and applies as soon as one is chosen. A byte rate reads in decimal units per second ("875 MB/s"), a count as "214/s", with three significant figures and at most three decimals, and a row with nothing measured keeps its words. The note names the interval ("per second over 2.5 s"), the choice travels with the ranking to the next publication, and each process's value equals `icat metric --metric rate --rate-numerator` over the same interval. The toggle sits on the selector's own row, because a line of its own cost the rail a ranked row at 700 pixels.

Revision 210 keeps a large group's process lanes when it is zoomed. Past the 20,000-cell bound a group's lanes were refused with a message, and a real capture met that bound at once: svchost.exe's 97 instances over the window's 256 columns need 24,832 cells, so zooming its group took every lane away. The lanes are now counted in the fewer, wider columns the bound allows, over the same interval and in the same pass as the group's own count: exact counts at a coarser resolution. The timeline draws each lane at its own columns beside a machine row at the view's, on the shared rate scale. It pairs owner lanes with the machine row by span rather than by column, which the first attempt left out: the caption said 97 lanes while the canvas drew none, and a live pass found it. The caption says so ("97 process lanes in 206 columns, coarser than the view, to stay within 20,000 cells"), and so does a lane bucket's card. Only more lanes than the 200-lane bound are still refused. §12's 2,000 × 40 lane query therefore answers with its lanes, in the 500 columns the bound allows; lanes at every view column for large groups wait on §6.2's density regime.

Revision 211 shows the parent link ntities-v1 §3 has derived since revision 30, which only `icat processes` printed. For a selected process the inspector states who started it and whom it started. A parent is linked by its start key or by its PID and this process's start time, and the card says which. A parent named but not in the capture stays a PID ("PID 9 · not in this capture") and is never guessed. A process none of whose records names one says so. Each side is one action away: Go to parent selects the parent, and Select its children makes the processes it started a multi-selection, which Enter turns into their records (§6.7); a single child is simply selected. The links come from the derivation checkpoint's lifecycle fields, so a reopened session keeps them. On a real capture a pool worker read "Started by pwsh.exe · PID 82636 · linked by its start key" and had started one conhost.exe, and the PowerShell had started 66 processes, itself started by the shell that ran the script. This is the process topology of IC-015's gap; resource topology remains.

Revision 212 brings the paired-channel browser up to the rest of the window, which had outgrown it. Its rows named only endpoints ("127.0.0.1:2711 ↔ 127.0.0.1:2756 · 128 observed records"), so a list of a hundred channels between a hub and its workers read as a hundred near-identical addresses. A row now reads by the processes it joins, first end first, then the endpoints compacted and the records: "queue.exe · PID 84016 ↔ worker.exe · PID 90512 · :15624 ↔ :15647 on 127.0.0.1 · 125 records". The names come from the workspace the browser opens over, and each channel now carries its second end's holder beside its first. Enter on a row opens its source records, as a double click did and as Enter does on a ranked row; before, only a mouse or a Tab to the button could. The list has an accessible name that says so. The detail no longer claims byte totals are not established: it says where the channel's own rung states what was sent across it. The browser had no test; one now covers its rows, its name and Enter.

Revision 213 fixes what a keyboard-only walk of the ladder on a real capture found. Enter descended a rung, and the keyboard went nowhere: the new rung's table replaced the row that had it, so the next Down moved nothing and the next Enter opened nothing, and a keyboard user had to click back into the table after every step. The headless tests had asserted each key's effect on the view model, never where the keyboard landed. The window now follows who owns the keyboard: the ranked table does once focus moves into it, until focus moves to another control. Whenever the table's rows are rebuilt while it owns the keyboard, the selected row, or else the first, gets it back once laid out. A rung change is one such rebuild, and so are rows that arrive later for the same rung: a process's RPC channels, or a page of records. Esc also selects again the row the rung was opened from, so the way back lands where the way down began. The walk now runs L0 to L2, into the evidence and back to the machine, with the keyboard on a row at every step.

Revision 214 fixes what a live pass of the rest of the window found: search, every ranking, the ranked rows' menu, the graph, the timeline and the evidence rung, driven by keys posted to the Release window over a 20-second dense capture. Each finding was reproduced headlessly before it was fixed. Escape out of the search box, Enter on a hit, and the button that lists the selected processes' records each focused the ranked list itself. A list takes no focus of its own, so the keyboard stayed in the search box, or fell to nothing once the hit it was on was hidden. They now give it to the table's row, as revision 213's rung changes do. The context-menu key and Shift+F10 raised their request on the focused row, above the element its menu is attached to, so only a right-click opened the menu that is the keyboard's way to Ctrl+click. The graph's help promised that Enter opens a group or process, but Enter and a double click opened only groups; a process node now opens its rung through its group, the path a search hit takes, and its hover card says so. The timeline's status never said which range was in view, so a zoom from the keyboard was drawn but never heard. It now does, and so does the minimap, whose help no longer claims 0 always fits the whole session. The original-record window and the channel browser now close on Escape, as the prompts already did. Counts now read in the right number ("1 process lane", "of 1 call with a status"), and a record's quality reads "correlation unknown" rather than an enumeration name.

Revision 215 closes the gap revision 213 left between where the keyboard is and what Enter acts on. A new rung's table takes the keyboard on its first row with nothing selected, and every row says "Press Enter", yet Enter acted only on the selection. A live walk found that at an RPC channel's rung Enter on the call with the keyboard did nothing, and so did Enter right after every descent until an arrow key had selected a row. Enter now selects the row that has the keyboard when none is selected, then acts. A selection or multi-selection the user made stays, so Ctrl+arrows and Ctrl+Space keep their meaning. Enter on a crumb with the keyboard returns to its rung, as the crumb says it will, and that rung's table then has the keyboard. A rung with no rows, a TCP channel's, now gives the keyboard to the step to its records beside the reason it has none, where it had fallen to nothing; the records that step opens then take it. Enter alone now walks from the machine to a record.

Revision 216 makes an export and a process rung say which one they are, from a live pass that exported views through the Release window's own save dialog. The dialog suggested "intercat-group-b61da093-g6.json" for every group, so exporting a second group offered to overwrite the first, and the file's name never said what it held. It now carries the rung's focus, its deepest implied filter made safe for a file name: "intercat-group-worker.exe-b61da093-g6.json". The machine rung keeps the short form. The process rung named its instance by executable alone, in its crumb and in the filter its exports and evidence carry, so the 27 worker.exe instances of one group each read "Process: worker.exe". A descent now names a process by name and PID ("worker.exe · PID 87372"), as the graph and the timeline already did. The redacted report keeps its own names, which carry no entity.

Revision 217 raises a redacted package's bound from one million rows to ten million, from a measurement of what the bound protected against. Heap dumps of packages at one, four and ten million synthetic rows found three things growing with rows. Joining each source field to its row took a hash set and a map of every observation with fields, about a hundred bytes each; it is now a sorted array of their addresses beside their package ordinals, 40 bytes each, sized by a first pass over the field segments and released once the fields are written. Verification kept a 32-byte descriptor per row and read the package's segments through the store's reader cache, which holds every reader until its lease ends, so it held every column at once. It now keeps twelve bytes a row and reads each segment on its own. The scale generator gained an opt-in number of source fields per row, since a real capture carries about one field row per record and synthetic sessions had none. With a field on every row, ten million rows package in 162 s. They complete under a 1 GiB heap limit, where the previous code fails at the same limit. A preview, which the share prompt shows first, no longer builds the join and reads ten million rows in 43 s. Beyond ten million rows, an interval-scoped package remains the way on.

Revision 218 is the first of the capture slices ADR-034 set out, and the ADR it asked for: ADR-035 amends ADR-002 for a private system logger in an opt-in capture. The feasibility run's session was the probe's own, so a new check (InterCat.AlpcProbe --session-check) made one as the capture makes one: named by CaptureSessionIdentity, created with `Create | NoRestartOnCreate`, the ALPC flag group enabled first and a manifest provider after it. It became a private system logger, both kinds of event arrived with none lost, and it held one of the machine's eight slots, of which this workstation already fills four, and gave it back when stopped. A manifest provider enabled first leaves an ordinary session, into which TraceEvent refuses the kernel flags, so the order is fixed, not chosen. ADR-035 decides one session per capture, a system logger only when the profile needs a kernel flag group, so ownership, recovery and cleanup are unchanged. It is never another tool's session, never adopted or restarted, and refuses rather than competes at the limit of eight. It is opt-in only, since ALPC measured Moderate, and admits classic kernel events from the machine's own schema as it does manifest events. What follows is the capture itself: kernel flags in the owned session's plan, an opt-in profile for RPC peers, admission for the classic descriptors, and its measured class.

Revision 219 measures how a classic ALPC record names itself, which the admission of ALPC depends on. ADR-035's addendum records it. TraceEvent gives every ALPC record the generic kernel provider id, with ALPC's class in its task. Its event id is the value that means none, and only its opcode tells send, receive, the two waits and unwait apart. The admission table keys a descriptor by provider, event id and version, which every ALPC record shares, so classic descriptors need a key of their own: class, opcode and version. The four kinds the relation rule reads carry a 4-byte body, the message id. The capture slice that follows plans from these measured shapes rather than from a guess.

Revision 220 reads a classic kernel event's layout the way ADR-035 requires, from the machine's own schema before capture starts. A classic event has no manifest; its schema is its class's registration, which TDH resolves from a record's header alone. `TdhClassicSchemaReader` hands TDH a synthetic header carrying the class GUID, the opcode and the version, with a zeroed body, and reads back each field's name, type and width. This removes the need to decode a delivered record in the callback, as the decision first proposed. On this workstation it reads ALPC's send, receive and wait for reply as one 32-bit message id each, its unwait as a status, and its wait for a new message as a port name of variable length. The class's fingerprint follows its layout, so two opcodes of one layout share it, as the journal's schema table needs of them. The admission compiler takes the reader next.

Revision 221 admits ALPC's classic send and receive, from the class's layout to the journal and back, though no profile enables them yet. The catalog's ALPC source now carries its event class, its kernel flag and two intents, send and receive, told apart only by opcode, each admitting its 32-bit message id as a new source-field meaning, `AlpcMessageId`. Its measured cost, Moderate, is recorded, and Explore no longer lists it at all. A kernel flag group compiles from the class's registration through the metadata source. Every key that names a descriptor now carries a classic descriptor's opcode: the admission table, the recorder's and the import's plan lookups, the envelope mapper's checks and a replay's plan selection. The callback names a classic record by its class and opcode with event id 0, as its header has them, when the plan admits its class; any other classic record, a trace header among them, keeps the identity it had, so a capture with no classic source tallies exactly as before. Send and receive share one layout, so they share one journal schema entry, which the journal requires; a plan file refuses a class whose opcodes disagree on it. A manifest descriptor's plan and journal are byte for byte what they were. Next, the owned session takes the kernel flags first, and a profile admits them.

Revision 222 gives the owned capture session its kernel flags, first, as ADR-035 decided. A plan now knows the kernel flags its kernel flag groups need from its own sources, so no caller that builds a plan changes. A session whose plan has any enables them before any manifest provider, in one enablement, which starts it as a private system logger. If they are refused, as at the machine's limit of eight system loggers, the capture is refused before any manifest provider is enabled, and only the session it made is stopped. A kernel flag group compiles to no manifest provider request, so the enablement compiler never asks ETW to enable a class GUID as if it were a provider. A plan without kernel flags enables exactly what it did. What remains of the capture is a profile that admits ALPC, its record's normalization, and its measured class.

Revision 223 adds the RPC peers profile, the first to admit ALPC, as ADR-035 set out. It is opt-in and takes Explore's lifecycle and RPC calls with ALPC's send and receive as kernel flags, and TCP and UDP context when they compile; without ALPC it does not start, since it would record calls it cannot resolve. ALPC's record now reaches the published session as an observation with its message id kept as a source field, which a redacted package keeps too. The coverage ledger names a classic descriptor with its opcode, because send and receive share a class, an id and a version: without it a live capture of ALPC could not publish its ledger. A real capture showed that a system logger delivers the kernel's thread and process rundown unasked, and that TraceEvent names such records only when its kernel parser is registered. So a classic record is counted under its header's class, whatever a decoder reports, and a display names the classes a system logger delivers unasked. ADR-035's addendum records the capture. Its measured class through the product path is the next slice, and ADR-034's relation rule after it.

Revision 224 measures the RPC peers profile through the product, as ADR-035's fifth decision asks of the profile that admits ALPC. A new probe mode runs FX-RPC-001's paced workload in seven rounds, with no capture, under `icat record --profile rpc-peers` and under `icat record --profile explore`, rotating the order each round, and reads machine processor time over the workload alone; it also reads the capture process's own processor time, which background load cannot move. In two series the opt-in added a median 0.12 and 0.21 CPU points to Explore, and its process 0.04, while this workstation's trials without a capture ranged from 1.9 to 3.8% busy: a whole profile's cost against no capture is within the background here. So the profile states Moderate, the class its sources measured one by one, with what the product showed beside it, and `icat profiles` prints both and the evidence. A profile's descriptor now carries a measured cost of its own, and a test holds every measured profile, as every measured source, to evidence the repository has. Its opt-in rests on the system logger it takes, one of a machine's eight, more than on its cost.

Revision 225 implements ADR-034's relation rule, `rpc-call-peer-v1` (`contracts/operations-v1.md` §5c). A client call is served by a server call when it made exactly one ALPC send on its own thread during the call, that message id was received exactly once in another process before the call stopped, and the receiving thread began a server call within 5 ms of the receive and before the call stopped, with one interface and one procedure on both. Every other shape is stated with its reason, among them several sends, several receives, no server call, a server call reached by two client calls, and a capture that collected no ALPC; nothing is linked by time or by message id alone. `icat operations` names, for each client group, the process that served its calls and how many, and for each call its other end or why none is known; a server group names the client processes it served. On a real RPC peers capture of FX-RPC-001, 1,499 of the workload's 1,500 calls were linked to the service host the workload recorded, and 1,860 of the machine's 1,911 client calls in all; no link conflicted. A metric still answers a peer as unavailable, now saying where the links are listed. RPC peers in the ladder and the graph come next.

Revision 226 brings a call's other end into the viewer. On the RPC rung a client channel's row says who served its calls - "served by services.exe · 1960 (1,499 of 1,500)" - and a server channel's whom it served, over the whole capture or under a brush; each call's row names the process at its other end, or why none is known. A call's other end carries the key of the call there, so a later step can open it on its own channel. A capture without ALPC says nothing of other ends rather than calling every call unresolved. The derivation is cached with the generation's calls and read only when the rung is, so opening a session pays nothing for it. The graph's RPC edges come next, as a layer read after the overview for the same reason.

Revision 227 draws RPC peers in the graph, the part of M3's exit gate this chain was for. When a session's capture collected ALPC, the overview joins a process and the process that served its calls by an RPC edge in the RPC and COM hue, correlated and so dashed, counting the call records at both ends and never the ALPC records that link them (§5.1); a brush counts the records it holds. Its hover says it counts linked call records, carries no size, and leaves which end called to each end's RPC rows. A channel's row on the rail now leads with who served it, which the narrow rail otherwise cut off, and what the view says it draws names the linked calls. A session whose ledger names no collected ALPC reads nothing for them, so its first view still opens no segment; an RPC peers session's first view pairs its calls and follows their messages, which its derivation checkpoint does not hold yet, rather than the layer read after the overview that revision 226 foresaw, because a graph that grows edges after it is drawn would move under the reader. A live check on a real RPC peers capture showed the workload joined to the service host and the LSA process, and 41 relationships among 33 processes machine-wide.

Revision 228 counts peers of RPC calls from the links (`contracts/metrics-v1.md` §8a). A call's other end is the process of the call it is linked to, a client call's server and a served call's client, and the filters and groupings of §19.1 read it as they read a record's: participant(P), peer(P,Q), between(A,B) either way, grouping by peer, and ActivePeers with a focus, whose count is a lower bound beside the calls no link reached. Each call is read by the existing record filters, as a record with its maker and its other end, so a call's disclosure follows the rule a record's does: a call left out only because no link reached it is disclosed with its reason, and under a peer grouping it is unattributed. A served call that no client call reached is now stated as not reached. Sender, receiver and a directional between stay unavailable, because a call has no data direction, and every peer question over a capture without ALPC says the capture collected none. On a real RPC peers capture, the workload's calls grouped by peer gave 499 to the service host and 2 to the LSA process, with 2 unlinked calls stated.

Revision 229 restores §12.1 S1 for RPC peers sessions: a finished session's first view opens no segment again. The persisted overview (`contracts/overview-index-v1.md`, minor 1) keeps, beside its counts, the RPC links of a capture that collected ALPC: each pair of instances a served call joins, at each strength its links have, with the call records at both ends. They are kept before any evidence policy, which the projection applies to them as it does to relations, so the graph's RPC edges are exactly those followed from the calls, now read rather than derived. A minor-0 overview is read as before and keeps none, and a session that collected no ALPC keeps none; a brush still follows the calls it holds. The whole call list stays out of the checkpoint on purpose: at about a hundred bytes a call, the links a graph needs are the few hundred pairs, not the millions of calls.

Revision 230 takes the step revision 226 kept a call's other-end key for: opening the call at an RPC call's other end. On a channel's rung a linked call's row offers it in its context menu - "Open the call services.exe · 1960 served", or "made" from a server's call - and as O, which its spoken name mentions. It runs the ladder as a person would: from the machine through that process's group and instance into the process's own channel for the interface, read by its key as a restored rung is, and onto the call's records, which the call's key finds wherever the channel's pages would list it. The breadcrumb states each rung and Esc climbs one, so the rung above the call is the host's channel, whose served calls name their caller. The path to the process is checked before the ladder moves, as a graph edge's is, so a process the view no longer holds leaves the user where they were; a call no link reached offers neither the menu item nor O.

Revision 231 makes Esc land on the call it came up from, as revision 213 has every rung do and a live pass of revision 230 found an RPC channel did not: a channel's calls are read after the climb, a page at a time, so the row the rung was opened from was not there to select, and one past the first page never would be. Climbing from a call's records to its channel - by Esc, Alt+Left or the channel's crumb - now reads the channel's calls from the first through the page that holds that call (`SessionRpcCalls.CallsThrough`) and selects it, up to the 5,000th call; past that the channel reads its first page as before. A brush that still holds the selected call keeps it listed and selected the same way. The live pass went from a client call to the call that served it by O, and back by the served call's menu, each Esc landing on the call it left.

Revision 232 has the inspector read a chosen ranked row that is neither a process nor a group - a channel, an RPC channel or an RPC call. The live pass of revision 231 found it said "Nothing selected" for one, though its own hint names ranked rows among what can be chosen, while the rail cut the row's detail short, and for a call the part cut was who served it. The row's own name now heads the inspector, its whole label and detail are beneath, and a line says what its keys do: "Enter opens its records · O opens the call services.exe · 1960 served". A process, group or aggregate chosen afterwards replaces it, and the selected process's lineage steps aside while a row is described, since under a call's heading it would read as the call's.

Revision 233 begins §11's content work, which §11 asks for from the first release and which no revision had started, with the half of §3.7 that needs no content: for unavailable content, the precise reason and which source could provide it. No source InterCat admits records a message's bytes. The kernel's network events carry endpoints and a transfer's size, RPC's a call's interface, procedure and status, and ALPC's a message id. The evidence inspector, the original record's window and `icat raw` (`RecordContent`) now say so for each record, and name a packet capture as the source that could hold a transfer's bytes, with encrypted traffic staying encrypted there; for RPC they name no driverless source and point to instrumenting the process. The statement stands apart from the retained event body the original record's view already describes, which is the event's own fields and never the message. The rest follows in order: §11.2's content contract (classification, per-record and per-session caps, truncation and missing ranges, and a fragment's link to its record), a controlled fixture path that carries a test provider's bytes through a scoped content profile, the bounded hex and text viewer, and then one validated content-capable source or import path, as M3 asks.

Revision 234 freezes §11.2's content contract and gives it a place in the store (ADR-036, `contracts/content-v1.md`). The journal could not hold content: its records' bodies are the metadata projections every row is re-derived from, under a policy that forbids source bytes, and a content body would either take their place or make every metadata reader parse content. So kept content is restricted evidence beside the journal, as §10.1's `content/` layout already had it: a chunk, store kind `Content` (code 9), published with the journal chunk whose records it holds content for and under that generation's number. Each fragment states what I21 asks - classification, direction, the encoding its source declares, its offset in its message, the message's original length, the bytes kept and how they were cut: whole, truncated by the per-record limit, or omitted by the session limit - and the chunk names the per-record limit and the inspection consent it was kept under, so a reader states a fragment kept without consent and never shows its bytes. A chunk checks itself fragment by fragment, so a viewer lists it without hashing it at open. Every later generation carries it as it carries a journal; releasing a recording's journal chunk releases its content in the same retention record, releasing content by name is refused, and so is rewriting one journal's prefix while content is kept. Pages for a person carry each record's kept content, so the inspector and `icat raw` state "Kept in part: the first 8 of 16 bytes of an application payload it sent, UTF-8 text; the other 8 were cut by the 8-byte record limit"; a whole-scope read, which an export makes, carries none. The original evidence package carries the chunks and says how many records' bytes it holds, where it said "no message content"; a redacted package holds none and names what it left behind; a follower refuses an evidence session that keeps content until it mirrors it. No capture writes a chunk yet: the next slice carries a controlled fixture provider's bytes through a scoped content profile, and the viewer follows.

Revision 235 is §11's controlled fixture path: a capture now keeps content, and only InterCat's own. The FX-CONTENT-001 workload raises `InterCat-Fixture-Content`, an EventSource whose layout the source catalog reads from the type that raises it, so the workload and the admission plan cannot disagree; `icat record --profile content-fixture` admits its messages under the one reviewed scoped content policy, `scoped-content-fixture-v1`, which copies at most 4,096 bytes of each in the ETW callback, writes them as a content chunk with each publication, and once keeping a message would pass 16 MiB writes it as omitted, with its length, and stops the capture (`contracts/content-v1.md` §5). A policy reaches only a source whose catalog entry carries a validated content contract; every other source of the same capture keeps metadata only. Two decisions shaped it. The fixture is an instrument of InterCat's, not a capability of Windows, so its source and its profile sit in separate fixture lists: a name resolves to them, but the machine's capability report and the profiles the broker offers never list them, and the broker refuses the profile outright, because its evidence follower does not mirror content yet. Until one does, the Desktop, whose live capture runs through the broker, cannot record content, and the viewer is qualified on sessions `icat record` wrote. And the fixture names its own process in its payload rather than extending ADR-030's header binding to application providers: `entities-v1` §7 makes a change to what binds a new rule identity, which every relation, operation and checkpoint derived under `process-binding-v3` would have to follow, for a mechanism no real source yet produces; whether an application provider's header names its owner will be measured on the first real one. Qualified live against the workload's truth log, which states each message's length and SHA-256 and never its bytes: 24 messages kept 23 whole and one cut to 4,096 bytes, and 8,000 unpaced messages stopped the capture at 16,773,163 kept bytes with 2,151 whole, 3,015 cut and 2,834 omitted; every length, direction and whole message's hash matched, nothing was lost, and no message text reached a journal, segment or dictionary. `icat session` now states a session's content in sum before anyone shares it, and an empty message reads as one that held no bytes rather than as lost ones. The Application mechanism family (theme 1.4.0) colours the fixture's records. Next is the bounded hex and text viewer (§3.7).

Revision 236 is §3.7's inspection of an available payload, with §11.2's first-release tools: hex, bounded text, byte-range selection and explicit binary export. At the evidence rung, C or "Inspect its content" opens a record's kept content in a window of its own, apart from the original record's, since content is kept beside the journal rather than in it; `icat content` reads the same from an evidence page's locator. Both re-find the fragment in the current generation by the record's raw identity under a lease, and state its facts before any byte: what it is and its source, its declared encoding, the message's length, which bytes were kept and which are missing, that it is one fragment rather than a reassembled whole, and the policy it was kept under. Its bytes are read only when the person asks, and never when the capture kept them without consent to inspect them. A typed range, by offset in the message, decimal with any digit grouping or hexadecimal, chooses the bytes; at most 64 KiB is shown at once, as inert hexadecimal with each byte's printable ASCII beside it, and, only for content its source declares text, as that text with every control, format, separator, private and unassigned character shown as a visible mark, so a direction override or an invisible character cannot make the shown text differ from the bytes. Copy places exactly the shown bytes on the clipboard as hex; Save writes the chosen range as the bytes it is, to a file the person names, published whole or not at all. `icat content --json` states the facts and never a byte, since a document programs read is not a person asking. Qualified live on the Release build against a fixture capture: the reveal moved the keyboard to the first line of bytes, Esc returned it to the record, and a message saved through the native dialog matched the workload's SHA-256. The live check also found the inspector naming a size's byte domain by its code ("604 B · ApplicationPayload"); it now says what the size measures in words (R5). §11.2's small synthetic fixture decoder, which would validate `DecodedFields` linked to its fragment and decoder version, is still to come; next is one validated content-capable source or import path, the M3 exit condition.

Revision 237 finds M3's content-capable source (ADR-037). Revision 233 had found none among the sources InterCat admits, and packet captures neither see loopback traffic nor reassemble the streams whose frames they hold. Windows' HTTP client library, WinINet, has a capture provider of its own, Microsoft-Windows-WinINet-Capture, whose registered schema declares a request head, request body, response head and response body event, each a session id, a sequence number, flags and a length before that many bytes: the layout revision 235's content slot admits. A lab probe, `tools/InterCat.WinInetProbe`, measured it against a new workload, FX-HTTP-001, whose WinINet client posts seeded bodies from empty to 256 KiB to a server it runs on loopback; the server is the wire-level witness and logs every head and body it received and sent by length and SHA-256, never the bytes. The probe's own uniquely named session enabled the provider with a process filter naming the workload alone, beside a decoy running the same exchange outside it. In four runs every part of every exchange, concatenated from its buffers, was the wire's by length and hash; nothing was lost or cut; the session id named the exchange, the sequence number a part's buffers from 0 and the flags its first and last, in all 255 parts of each 64-request run; the client process raised every record; and none of the decoy's reached the session. So the source states each semantic M8 asks for - direction, byte ranges within a message, message identity, length and completeness - and it is scoped to named processes before persistence. That scoping is not optional: on this workstation the provider is loaded by the browser, cloud clients and chat application, and unscoped it would record their requests with their cookies and authorization headers. Its admission is the next slice: a content source whose contract names the four events, a Content profile that refuses a request without a process scope, the rule by which its records bind to the client process that raised them - ADR-030 reaching a new mechanism, so a revision of `process-binding-v3` - and its overhead. HTTPS, asynchronous WinINet, HTTP/2, chunked and compressed responses, redirects, proxies and authentication are each measured before a profile claims them. FX-HTTP-001 joins the fixture index with its admission's tests, as FX-CONTENT-001 should.

Revision 238 puts ADR-037's source in the catalog and decides whose its records are. An HTTP exchange's messages are a mechanism of their own, `Http` (`EN-Mechanism` 21), in the Application family: filed under TCP they would count messages among a connection's transfers, and "Application SDK" names an application's own instrumentation, not Windows' client library. WinINet's capture source joins the machine's capabilities with its four events admitted as HTTP messages sent and received, each keeping its exchange, its buffer's place and its ends as source fields - `HttpExchangeId`, `ContentBufferSequence` and `ContentBufferFlags`, `EN-SourceField` 16 to 18 - and its buffer's length as an application payload's; its bytes stay out under any policy that does not name it. The records name no process and are raised in the client that made the exchange, 444 of 444 as measured, so ADR-030's header binding reaches HTTP. That changes what binds, so it is a new rule identity, `process-binding-v4`, as §7 of the entities contract requires; a checkpoint derived under version 2 or 3 is read as version 4's wherever every record named its owner, which its counts show, because each earlier rule differs only in binding fewer records that name none. The slice also closed a gap revision 235 left: a scoped content policy now names the sources it keeps content of, and the plan compiler and the inventory check it, so the fixture's policy - the one reviewed - can never keep WinINet's bytes, and a policy widened past its source is refused rather than enforced. No profile captures the source yet: a Content request for it compiles no admission policy while its channel scope and overhead are unsettled, which is revision 239's.

Revision 239 admits M3's content source: a capture now keeps a real application's content, from the processes a person names. WinINet's capture was measured first, as §12 requires before a source serves a profile: seven alternating pairs of FX-HTTP-001, each moving 4,096 exchanges through a session scoped to the workload whose consumer copied every record, cost a median 0.71 CPU percentage points and 2.5% of the workload's time, loss-free, so its class is Low. The Content profile's bounded request - which revisions before this could only preview - now compiles for a source whose content contract holds its process scope before persistence and whose impact is measured, into `scoped-content-request-v1`: that one source's content, within the request's per-record and session limits and its inspection consent, with the provider enabled for the named processes alone by the session's process filter and lifecycle kept as whole-machine metadata. The request model asked for channel selectors, which this source cannot enforce - its records carry no host or port - so a request names every channel of its processes with the one selector `*`, and any other selector is refused rather than trusted; the collection statement says so. `icat record --profile content` takes the request on its command line, and the broker previews such a request and never starts it, because its evidence follower does not mirror content. Qualified live against the workload's wire truth: 229 buffers kept whole across 32 exchanges; regrouped by exchange id and sequence number, all 128 parts matched the server's SHA-256; every record bound to the workload under `process-binding-v4`; and the Release window opened the records' content and saved a response head that matched its hash. The pass found one older fault it did not cause: a process's evidence rung still hatches every interval without its own records as a coverage gap, though the ledger covers the capture throughout - the reading revisions 165, 197 and 206 corrected for lanes and direction rows. That is the next slice; after it, the fixtures join the index and M8's reassembly of a part from its buffers can begin.

Revision 240 corrects the reading revision 239's live pass found. The machine timeline, a focus's timeline - a process's or a group's, which the evidence rung draws - and the overview judged each bucket by the mechanisms observed in it, so a bucket with nothing observed had nothing to judge and read as unknown: a scoped capture, quiet but for its workload's burst, drew its evidence rung as one long coverage gap and counted 58 coverage-unknown intervals in the caution ink, although its ledger covered every collected source throughout with nothing lost. Revisions 165, 197 and 206 had corrected exactly this for process lanes and direction rows, on the ground that a process could make any record the capture collects; the same holds for the machine's rows and for any focus's. An empty bucket now takes the capture's own coverage over its interval: observed-empty where the capture covered what it collects, a gap where it lost records, and unknown past the readings it delivered or where the ledger cannot vouch for a quiet interval, as an import's cannot. A bucket that holds records keeps its judgement by the mechanisms in it. The overview's caveat and `icat timeline` say so, and a quiet bucket's hover says the capture covered it rather than that emptiness proves nothing.

Revision 241 enters M3's content evidence in §13.5's traceability matrix, which a gate reads and which names only fixtures and tests that exist. FX-CONTENT-001 and FX-HTTP-001 join `fixtures/index.json` with their truth logs - lengths, directions and SHA-256 of synthetic messages, never a byte - and a measurement recorded afresh with `icat record` from the elevated shell, counters only; the captures, which hold this machine's process names, stay out. The matrix names a test by the contract item it asserts, so the WinINet tests now carry theirs: R17 for admission decided per record by a policy that names its source and a scope that holds before persistence, P15 for a policy that keeps no source it does not name, P28 for a payload-producing capture enabled by an explicit request only; and a recording test that keeps HTTP buffers beside their journal chunk with their exchange, place and ends asserts I21. P15 and P28, declared uncovered since the matrix began, are covered now. Next is M8's first step on this source: a part reassembled from its buffers for a person.

Revision 242 is M8's first step, on the one source whose records state message identity and completeness: a part reassembled from its buffers for a person. WinINet's capture records a head or body as buffers that name their exchange, their place and their ends, and InterCat keeps those three as source fields; a query finds a record's part from them - the same exchange, event and raising process, in sequence order - reading only source fields and the rows they name, and the content of those rows only when a person asks. The viewer's and `icat content`'s facts now say which buffer of which part a record is, rather than that nothing joins it to another record, and whether the part was kept whole: every buffer from the one flagged first to the one flagged last, each kept whole. Only such a part is shown, copied or saved as one - the viewer toggles between the buffer and its part, `icat content --part` reveals or saves it - and a part missing a buffer, or holding one cut, names what it lacks and is shown a buffer at a time, since nothing may stand in for missing bytes (I21, P2). Qualified live: a 262,144-byte response body reassembled from 17 buffers matched the loopback server's SHA-256. Showing a part that is not whole with its gaps in place, and reassembly for any other source, wait on a source that states the same.

Revision 243 measures HTTPS through M3's content source, as ADR-037 required before any profile claimed it. FX-HTTP-002 is FX-HTTP-001 over TLS, against a loopback server whose certificate is made in memory for the run and installed nowhere, and whose truth is the bytes above the encryption. WinINet's capture holds the plaintext - every part matched what the server decrypted and encrypted, in the lab probe and through `icat record` alike, and no buffer began as a TLS record does - and nothing in a record says whether its exchange was encrypted: the layout, flags, numbering and buffering are plain HTTP's. Its impact stays Low over TLS. So a record's bytes are stated as the application's message above any encryption of its connection, never as having crossed the wire in the clear or encrypted, and since an HTTPS exchange is kept as its plaintext, headers, cookies and authorization included, a WinINet content request says so in its collection statement before anything is recorded (§11.1), from a statement its source's content contract carries. The same pass corrected two texts that denied what revisions 239 and 242 do: the preview contract, which said no Content request could start, and the inspection disclosure, which said inspection consent authorized no reassembly or export - joining a part's buffers, copying and saving are each a person's deliberate action, as §11.1 asks.

Revision 244 measures what revision 242's reassembly relies on, under load: WinINet exchanges at once in one process, and chunked responses (FX-HTTP-003). Eight clients in one process keep their exchanges apart - each keeps one number, its parts numbered from 0 and flagged at their ends - and every part matched, 8,192 of 8,192 over 2,048 exchanges in the lab and 1,024 of 1,024 through `icat record`; a chunked response body is kept as its client read it, without the framing only its head names. The run also showed what the number is: the client process's own count from 1. It never recurred within a process, but a process ID used again, or WinINet loaded again, counts from 1 once more, and the part query found a part by number, event and process ID alone - two exchanges' buffers would have sorted as 0, 0, 1, 1 and passed every check as one whole part, a fabricated message (R22, P2). A part is now its record's use of the number, told apart in time: a buffer flagged first, one after a buffer flagged last, or one numbered no later than the one before it opens another use, and a reused number is said to be. The same measurement shows a scope gap: a Content request names process IDs, which the provider's filter holds by number, so a process ID used again during a capture would be in scope too; binding the scope to the named process instances is next.

Revision 245 closes the scope gap revision 244 named. A Content request names processes by ID, and the provider's process filter holds IDs by number, so a named process that exited during a capture could have had its ID given to a new process, whose HTTP messages - cookies and authorization among them - the filter would then have admitted: R22's failure, an identity established by a reusable number, on the path that decides what content is kept. Windows gives no process an ID while a handle to the process that had it is open, so every session that enables a provider with a process filter now holds each process it names open until the session is gone: a named process's exit ends its content, and no other process can enter through its ID. A process that is not running, or has already exited, when the provider is enabled is refused, and `icat record` refuses one before anything starts, names each process it keeps content from by image and start, and states what it collects before it records, as §11.1 asks of every payload-producing capture - revision 243's HTTPS sentence had reached only the preview.

Revision 246 is what a live pass over a fresh content session found. Its persisted overview was refused as not readable - `a count of 1,061 is more than the 77 bytes left can hold` - because the reader checked the timeline's and the minimap's column widths as counts of the fields after them, and a few records over a long extent fill few of many columns: every small session's overview had been refused since revision 163 and counted from its segments instead, which gave the same result more slowly, and a small RPC peers session lost revision 229's persisted links. The widths are compared as widths now, a sparse overview reads back, and overview-index-v1 says the widths bound nothing. The pass also found the session's busiest process - 582 HTTP records with their content kept - saying Nothing at this level on its rung, its exchanges reachable only as a flat list of buffers at the evidence rung; the exchanges on the process rung are next.

Revision 247 answers revision 246's live finding: a content session's busiest process said Nothing at this level on its rung, its HTTP exchanges reachable only as a flat list of buffers. A process's exchanges are now a row of its rung beside its paired and RPC channels; Enter lists them a page at a time, each leading with how long it took and saying what was recorded of its request and response heads and bodies, and Enter on one opens its buffers, where C shows a buffer and its whole part; `icat exchanges` lists the same (R18, `contracts/http-exchanges-v1.md`). The exchanges are grouped from records' metadata and source fields alone - the rule, `http-exchange-v1`, opens a new use of an exchange number at a request head flagged first or at a buffer repeating a place the use holds - and never from content, so a row says how large each part was and whether it was recorded whole, never what it said. The live session is why the rule is that narrow: WinINet raises the empty buffer that ends a request body after the response has ended, and a first rule that closed an exchange at its response's end read 96 exchanges as 191, each missing its request body's end. The evidence scope's RPC key became an operation key naming either kind, and an evidence cursor names the rule its key reads by, so it never continues across a change of rule. An exchange's method, target and status are content, and wait on a decoder.

Revision 290 draws a process's exchanges in the timeline at their rung, as revision 181 drew an RPC channel's calls: each a bar under the machine's records, from its first recorded buffer to the buffer that ended its response, or to its last buffer when that end was not recorded - not to the empty request-body buffer WinINet can raise after the response, which revision 247 found is no exchange's end. A whole exchange is the HTTP hue, and one with any part not recorded whole is the same hue faint, so a capture's gaps show where they fell. Past 4,000 exchanges in view the lane draws §6.2's density columns, with the share not recorded whole stacked faint on top; the call lane's failures are stacked the same way, which they had been overlaid. A bar's card states the exchange's duration, number, messages and recording, and a click selects its row, as a call's does. The call lane's layout, density, hover and click now serve either kind of operation.

Revision 248 is what a live pass over a real 30-second Explore capture found: chrome.exe, holding 460 TCP and UDP records, said Nothing at this level on its rung, and so would every process whose connections go to other hosts - on a real machine, most of them - because the process rung listed only paired channels, both of whose ends a record holds. §7.1's channel is possibly one-sided, and revision 47 already counted a connection whose other end no record holds as a one-sided channel; the relation index now keeps each one held by one process instance, keyed by its holder and its earliest raw fact, and the process rung lists them beside its paired channels as connections: '→ 3.72.134.85:443', TCP or UDP from the process's own endpoint, whether the capture saw it open and close, and the transport bytes its own sends and receives measured, which rank with the paired channels' under a byte ranking. Nothing is said of who is at the other end (P7). Enter opens a connection's records, and `icat channels --one-sided` lists the same. Live, chrome.exe's rung listed 25 connections holding 459 of its 460 records - its lifecycle record the last.

Revision 249 finishes what revisions 247 and 248 opened: every evidence rung's timeline counts its own records apart. A process's and a paired channel's always did; a one-sided connection's, an HTTP exchange's and an RPC channel's or call's drew the whole session beside their records. The timeline's focus now takes a one-sided connection by its channel number - at its one end, so without a paired channel's two-ends lanes - and an RPC or HTTP key by the very records its evidence reads, the same `RecordsOf` sets, so the timeline and the list beside it cannot disagree about which records they mean (R21, §6.2).

Revision 250 is what a live pass over sharing a content session found. The redacted package was sound where it matters most - no content file and no reference to one (I22), the export carrying no byte, the original package stating the kept bytes it holds - but its HTTP records grouped into no exchange: §11.3's policy withholds any source field it does not name, and it named none of HTTP's, so a recipient could see the requests' sizes and timing only as loose buffers, and `icat exchanges` blamed the capture for it. A buffer's place and ends are the shape of one message and identify nothing, so they are kept; the exchange number is its client's count, which says how many exchanges it had made, so it becomes a pseudonym of a namespace of its own, never a value the source held and the same wherever one exchange appears. The package's exchanges now group exactly as the source's, with their sizes and timing, and none of their content. The policy's own list of kept fields also names the ALPC message id it always kept.

Revision 251 publishes per-build coverage, the M3 exit gate's last item that had nothing behind it. `icat capabilities` reported every mechanism Unsupported - no capture has measured it - on the very build whose fixtures measured TCP at TrafficVisualization and HTTP at ExperimentalEvidence, because the probe measures nothing and the fixture index held each tier only as prose. Each fixture now names its mechanism and each environment entry its measured tier, and a test holds the two to the prose. The latest evidence of each mechanism on each build is projected into `docs/PER-BUILD-COVERAGE.md` and into a file every InterCat build embeds, both checked against the index, so the capability report states the tier measured on its own build, names the fixture, says it measured nothing itself, and names another build's evidence as another build's, never borrowing it (P27).

Revision 252 reviews M3's exit gate (`docs/reviews/M3-exit-review.md`), each item against evidence that names it. RPC layered over its local transport adds no volume: a call carries no byte value, calls are counted from the calls and never from the records beneath them, and an RPC edge counts the linked calls' records, never ALPC's. Content truncation is measured by FX-CONTENT-001 and HTTPS's plaintext state by FX-HTTP-002, and both are stated where a person reads content and before a request records it. Per-build coverage is published since revision 251. Pipe instance topology and shared-section membership are not implemented: named pipes measured Unsupported and no section source is proven, and both remain explicitly unavailable, which the gate requires of an unavailable feature, moving with their measured gaps to M7 and M9 as §14's fallback allows. RPC over TCP, HTTP/2, compressed responses and asynchronous WinINet are unmeasured, and the timing profile is unavailable. The gate is met for M3's measured scope; M4, multi-machine investigation, is next in dependency order.

Revision 253 starts M4 with its persistence (§8.4, ADR-038, `contracts/workspace-v10.md`, then version 1): an `.icat-workspace` file that names separately valid sessions by identity and never writes to one, made and read by `icat workspace new`, `add`, `show`, `relink` and `alias`. Building it found that a store's source identity names no capture: a broker capture's is its plan's digest, which every capture under that plan shares, an import's names the path it was read from, and a redacted package's is its contract's name, so keying members by it would have refused a second capture made under one plan and taken one file imported from two paths twice. A member is keyed instead by the capture its journal records, which an import derives from the file's content, so a capture is one member however many copies or re-imports of it exist. Each member resolves against where it was last found - present, advanced (a newer generation was published), replaced (an older copy, or a generation derived apart), missing, different or unreadable, with the reason - by opening the session as a viewer does and writing nothing, and only a relink to the member itself selects what is there. A file with a field this version does not define is refused, so a later version's file is never rewritten without it, and a write goes only over the text it read. The slice also found that a live capture's host identity was derived from its machine's name and build alone, an identity from an equal name that P6 forbids and that would have shown two machines of one name as one host: it is now derived from the installation's machine GUID together with the name and build, so only an exact clone shares it, and §8.3 states what equal host identities do and do not say. Sessions recorded before keep the identity they recorded, so one machine's older and newer captures read as two hosts, the safe direction. Clock mappings with their uncertainty, confirmed host equivalence, cross-host correlation and the Desktop's workspace follow.

Revision 254 builds §8.2's alignment model and its manual mode (ADR-039, `contracts/workspace-v10.md`, then version 2). A clock mapping is an affine map with named uncertainty contributions: bounds - a person's statement, an unverified claim, a drift bound - add linearly, independent measured contributions combine in quadrature, and one unknown contributor makes the whole unknown, never zero; a drift adds nothing at its anchor. The workspace's time is one member's clock, and a person aligns another member to it by stating that one of its instants is one of the reference's, within a bound, the clocks drifting apart by at most a stated rate; with no rate stated, every uncertainty away from the anchor is unknown. `icat workspace compare` places two members' instants in that time and states an order only when they are further apart than the pair's uncertainty, an ambiguity when they are not, and nothing - not even the difference - when an instant has no workspace time or an unknown uncertainty; two instants of one member are ordered exactly. Building it found §8.2's pair formula counting the systematic part twice, since each side's `u(t)` already holds it, so it now names each side's random and bound parts, and that a stated bound written to a display's precision could read smaller than it is, so bounds are rounded up where written. Alignments are versioned revisions of the workspace file, kept when superseded or withdrawn, and none changes a timestamp (I9); the file is `workspace-v2`, which reads revision 253's version 1. Live, two captures of this machine's one QPC counter, aligned by their recorded epochs, compared 26.1 µs apart as ordered beyond ±1.0 µs and exactly at the anchor as ambiguous. Alignment from recorded wall clocks - which needs captures to record paired monotonic and wall-clock samples - and from shared markers, a rate from two anchors, and the Desktop's merged time follow.

Revision 255 makes live captures record what §8.1 asked of them and none did: their clock against the wall clock, and their boot (ADR-040, `contracts/clock-calibration-v1.md`). When a capture starts and when it stops it pairs the performance counter with the precise wall clock, bracketing each wall-clock read between two counter reads and keeping the tightest of sixteen, so each pair's uncertainty - half the bracket, widened by one counter tick, plus the wall clock's 100 ns resolution - is measured; it bounds how far apart the pair was taken and never how right the wall clock was. A boot is named by a random token the first capture of it keeps in a volatile registry key Windows deletes on restart, because Windows' own boot count is no identity - a clone counts as its original did - and claiming two machines' counters one clock is the worst false claim §8.2 allows; the count is kept beside it for people. The calibration is a dependency of its own kind, published with the last chunk, carried by every later generation and by a follower, never released, and never in a redacted package, whose leak scan now looks for its digest and boot token; `icat session` shows it with the wall clock's rate against the counter. Live, a 4-second capture recorded its boot token and two samples of ±200 ns, the wall clock running 0.0 ± 0.05 ppm against the counter. The workspace's use of it - two captures of one boot aligned exactly, others through their wall clocks under a synchronization bound a person states - follows.

Revision 256 aligns a workspace's members by what their captures recorded (ADR-039 decision 7, `contracts/workspace-v10.md`), §8.2's recorded-wall-clock mode and a case of it §8.2 did not name. Two captures that recorded one boot's token read one performance counter, so `icat workspace align --same-boot` aligns them exactly through their capture epochs, with no drift - within 2 ns only when a tick is no whole number of nanoseconds - and refuses captures of two boots, or with no calibration or no boot. `--wall-clock` anchors on the pair of calibration samples, one of each capture, taken closest in wall-clock time; its bound adds what the samples measured, their acquisition, to what no sample can measure and a person must state, the wall clocks' agreement, and a stated drift over the time between the samples and away from the anchor. Live, two captures of this machine aligned by their boot placed the second's start 5.0632029 s into the first, exactly as their epochs' 50,632,029 ticks say, and ordered instants 100 ns apart; aligned instead by their wall clocks under a stated 5 ms agreement and 20 ppm, the anchor agreed with the exact one to the tick and instants 6.8 ms apart were ordered beyond ±5.1 ms. The file is `workspace-v3`, which reads versions 1 and 2. Alignment from shared markers, a rate from two anchors, and the Desktop's workspace and merged time follow.

Revision 257 brings the investigation into the Desktop (ADR-038). The start page's Investigation button opens an investigation file or starts one - an existing file is opened, never written over - and an `.icat-workspace` named on the command line, or dropped on the program, opens too. The investigation has a window of its own beside InterCat's: its sessions where each was last found, with its host and its time in words - the investigation's own clock, aligned by a person, by one boot's counter or by the wall clocks, with its bound, or not aligned and so ordered against nothing - and the reason a session is not present. Enter or Open shows a session in InterCat's window, Relink points one that moved at where it is now, and Add sessions takes folders, each refusal said in words. Resolving runs off the window's thread and writes to no session. Live, an investigation of two captures aligned by their boot opened from the command line; its rows read to a screen reader as sentences - "Aligned by one boot's counter: its 0.000 s is the reference's 4.8844366 s, exactly" - and Open showed the second in InterCat's window. Aligning and comparing in the Desktop, and the merged time across sessions, follow.

Revision 258 proposes joins across an investigation's captures (§8.3, ADR-041, `contracts/workspace-v10.md` §6). Every one-sided connection of each member - a TCP connection or UDP flow whose other end its own capture holds no record of - is compared with every other member's: a candidate needs mirrored endpoints of one protocol, and lifetimes that overlap once placed in the investigation's time and widened by their uncertainty; a pair that lies apart beyond it is counted and not proposed, and a pair that cannot be placed is a candidate whose timing is said to be unknown. Loopback endpoints join only captures of one host identity. A candidate is never established: `icat workspace correlate` lists each with its evidence - the endpoints, the timing, the bytes each side measured of each direction, the same or not - and how many other candidates either connection has. Building it found that one machine cannot capture an exchange's two ends apart, since the TCP source is machine-wide and a focused capture's process IDs scope only its first view, so the rule is proven on synthetic sessions and live only in proposing nothing between two real concurrent captures, in 0.4 s; M4's known two-host exchange needs a second host. Accepting and rejecting candidates as versioned revisions, and the Desktop's candidate list, follow.

Revision 259 brings alignment and candidate joins into the Desktop's investigation window. Its Sessions tab aligns the selected session through a dialog offering the three ways the investigation knows - exactly by the boot both captures recorded, by their wall clocks with the agreement and drift only a person can state, or by an instant the person reads in both, its bound and an optional drift - saying in words what is missing or refused, and withdraws an alignment, the revision kept. Its Candidate joins tab finds, on request, the candidates between the sessions and lists each with its endpoints, timing, both ends and evidence, none established, every row named for a screen reader as a sentence. What a person types for time - a duration with its unit, seconds, a rate - is read by one parser the command line shares, a comma taken as a decimal point. Live, the dialog aligned two captures of this machine by their boot and the tab stated that no connection one session holds one end of has its mirror in the other. Building it found a byte statement that read "received 0 B in 0" where a side recorded no receive; it now says so. Accepting and rejecting candidates as versioned revisions, and comparing instants in the window, follow.

Revision 260 keeps a person's decisions about candidate joins, §8.3's manual joins (ADR-041 decision 6, `contracts/workspace-v10.md`). Accepting a candidate as one connection, or rejecting it, is a revision of the investigation recorded with the alignment revision in force for each of its two sessions, kept when replaced or withdrawn; a candidate then says what a person decided of it, never as evidence, and when either session's alignment has changed since - revised, withdrawn or made - it says the decision was made under alignments since changed, to review, which is how re-aligning time invalidates what depended on it without changing a source. A decision whose pair is no candidate now, its lifetimes moved apart, is said and never dropped. `icat workspace join <n> --accept | --reject | --withdraw` decides candidate n of `correlate`'s list, and the investigation window's Candidate joins tab does the same with a button each. §21.1's cross-host scenario is asserted as written: host B's receive placed 0.2 ms before host A's send within a combined 3 ms is an ambiguous order, stated as 200 µs apart within ±3.0 ms and never as a latency, the exchange stays a candidate whose lifetimes overlap, and host B's own span is unchanged.

Revision 261 draws M4's merged time. `InvestigationTimeline` places every session that has a place - the time reference, or an aligned session - on the investigation's own axis: alignments are offsets with no rate, so one uniform grid of the investigation's time is one uniform grid of each session's own, and each session's records are counted over the same columns mapped back into its own time, never rewritten (I9); a session with no alignment, or not where it was found, is listed with the reason. The investigation window's Timeline tab draws a lane per session, each scaled to its own busiest column with its extent shaded, and states every lane in words - its records, where they fall in the investigation's time, and how sure that placement is: exactly for the reference and one boot's captures, or within the alignment's bound. Live, two captures of this machine aligned by their boot drew as lanes 0 to 3.0 s and 5.93 to 8.92 s, where their shared counter puts them. Zooming the merged timeline and opening a column's records follow.

Revision 262 flags partial overlap, as §8.4 asks: two captures of one host identity may have recorded the same events, so every such pair is compared by its record extents in the investigation's time. They ran at once when each reaches past the other's start by more than the pair's uncertainty - records of one event may be in both, so no count across them is summed and nothing is deduplicated by time; they may have when they are nearer than that; a pair of which not both have a place with a known uncertainty is said to be unknown; and two captures that recorded two different boots yet seem to run at once contradict each other, which two boots cannot, so one of their alignments is wrong - a check the boot token makes possible. Two hosts' captures are never compared. `icat workspace show` and the investigation window state each overlap in words. Live, two concurrent captures of this machine read as unknown until aligned by their boot, then as having run at once for 3.0 s; the boot token they named had changed since the day before, when the machine restarted, as a volatile key must.

Revision 263 packages an investigation with its sessions, as §8.4's export asks (ADR-042): one new folder holding the investigation's file and, under `sessions/`, an original evidence package of each chosen session whose capture is where it was last found, which the file names relative to itself, so the folder moves whole. The investigation's identity, time, alignments, host names and join decisions go unchanged, since every copy is the same session. A session that is not where it was last found, or was not chosen, stays a reference by the whole path it was last found at - relative to the original folder it would name nothing beside the package, or something else - so elsewhere it opens as missing, to relink; a session that has moved on since it was selected is copied as it is and still reads as advanced until a person relinks it, because a package never selects a generation. What the copies and the file expose - names and notes as written, and the paths references disclose - is stated before anything is saved, and nothing appears under the folder's name until every copy verified and the investigation, reopened from its own file, found each copy as the session it is. `icat workspace package` (`--only`, `--check`, `workspace-package-v1`) and the investigation window's Package… make it; the window's confirmation lists each session to choose and states again what the choice exposes. Live, an investigation of a real 3-second capture and an imported ETL was packaged from the CLI and from the Release Desktop, whose native folder picker was filled by message, and the package, moved elsewhere, reopened with both sessions present and its alignment and host name kept; a session moved away before packaging stayed a missing reference, and the CLI exited 1. The workspace command now takes options with values before its verb and workspace, which a value written first had been read as, and its help names `workspace-v4`.

Revision 264 measures a rate from two separated anchors, as §8.2 allows only with them (ADR-039 decision 8, `workspace-v5`): a person's alignment may take a second instant of both clocks, well apart from the first, and the line through the two is the mapping, at the rate it measures, pivoting on the first anchor so each stays exactly where the person put it. Each anchor may be off by the stated bound, so the line is off by that bound between them and, beyond them, by that bound carried along the rate's own uncertainty, twice the bound over the anchors' distance; a stated drift now bounds how far the rate may wander from a constant, which moves an instant by up to twice the wander times its distance from the nearer anchor, and with none only the anchors themselves have a known uncertainty. A second instant at the first's, or a rate more than 1,000 ppm from 1 - no working clock's, so an instant was misread - is refused. Building it found the merged timeline placed a lane by its offset alone and stated its uncertainty at its ends; a lane is now placed through its mapping, whose uniform grid stays uniform, and is as uncertain as its widest instant, which with two anchors may lie midway between them. The file's version is 5, so a version 4 file that seems to hold a second anchor is refused whole; bumping it also found that a version 4 file's join decisions would have been refused as an earlier version's, and that the tests building earlier versions' files named the current version by its literal, which would have left them testing nothing after the bump - they now name it through the code. `icat workspace align` takes the second pair of instants and `show` states the rate and its wander; the Desktop's Align offers the second instant and says the rate in words. Live, a real capture aligned at two instants of an imported ETL 2 s apart measured +200 ppm, placed an instant between them within ±1.1 ms of the other's, and its timeline lane within ±1.5 ms: two instants only 2 s apart, each within ±1 ms, pin the rate only to ±1,000 ppm, which the lane's bound beyond them says.

Revision 265 aligns a member through another (ADR-039 decision 9, `workspace-v6`): a member may be aligned to any member placed in the investigation's time, not only to its reference, and is placed through both alignments in turn - each adds its own uncertainty, taken at the widest instant what was carried so far allows, and carries the rest at its rate. What makes it worth having is how two members aligned through one compare: that alignment's errors move both alike, so it counts only by its growth - a drift, or a two-anchor line's slope - over the time the two may lie apart, and by each instant's own roundings, never by twice its bound, as counting the two sides as independent would. A second capture of one boot, aligned exactly to the first while the first carries a wall-clock or stated alignment to another host, therefore compares with it to the nanosecond, and two such captures of one host that ran at once read as having done so rather than as "may have". Roundings are now marked as each instant's own, which no shared alignment cancels. A member cannot be aligned to one with no place or through itself, an alignment others are aligned through cannot be withdrawn, and a join decision records every alignment on both chains, so one changed anywhere flags it for review. Building it found that every alignment set the time reference to the member aligned to, harmless while that could only be the reference, and that the join checks refused any decision recording more than its own two sessions. `icat workspace show` names what each member is aligned to and through, and the Desktop's Align offers every placed session but the member's own dependents. Live, of two concurrent captures of this machine, one aligned by a stated instant to an imported ETL and the other exactly to it by their boot, the two compared to the nanosecond and read as having run at once for 871 ms, while either against the import stayed within ±1.1 ms.

Revision 266 lets a person confirm that two host identities are one host, versioned, as §8.3 asks (ADR-038 decision 7, `workspace-v7`). Different identities are never one host by name or address, yet one machine can carry two: its captures before revision 253 derived its identity from its name and build alone, a machine renamed or reinstalled derives another, and a file imported from it has one derived from the file. A confirmation is recorded with its note as a revision, kept when a later one withdraws it, and joins identities transitively while in force. Captures of confirmed identities are then compared as one host's - for sessions that ran at once, and for loopback connections between them - and each statement resting on a confirmation says so: an overlap reads "of one host, by a person's confirmation", and a loopback candidate's evidence says its endpoints name one host only because a person confirmed it. No identity and no session changes. `icat workspace same-host` records and withdraws it, `show` lists the identities one host with each, and the investigation window's One host… offers the other identities, withdrawing only a confirmation made directly; its summary counts hosts, and says how many identities they are.

Revision 267 compares two instants in the Desktop, which only `icat workspace compare` could: the investigation window's Timeline tab offers Compare instants…, where a person reads an instant in each of two sessions, in seconds of its own time, and is told each one's place in the investigation's time - exactly, within its uncertainty, with an unknown one, or nowhere and why - and what may be said of their order: exactly on one clock, as an alignment they share allows, and otherwise only beyond the pair's uncertainty. Its sessions are named by their folders, and marked when one is the reference clock or not aligned. Nothing is written. Live, an instant of a real capture read in the Release Desktop against the imported ETL it was aligned to within 2 ms was said to be one moment with it, their order ambiguous within ±2.1 ms: the stated 2 ms and 50 ppm of drift over the half-second from its anchor.

Revision 268 zooms the merged time and opens a column's records. The investigation window's timeline zooms in and out around a chosen column, by its buttons, the wheel, or plus and minus, and 0 or Whole investigation shows it all; each zoom reads the lanes again at their full column count within the investigation's time. A column cursor - the arrow keys, Home and End, or a click - says its session, its time and its records beside the chart, and Enter, a double click or Open this column opens it in InterCat's window: the session, its timeline zoomed to that column in the session's own time, the column's interval selected, so its records are what the window shows. Each lane keeps its columns in the session's own time for that, so a column is opened exactly where its records were counted, through whatever alignment chain placed it. Only what the session holds of a column is selected - a first column can reach back before its capture began - and a column holding none of a session's records opens nothing, and says so; the live check found both, opening an empty stretch before a capture until they were fixed. The chart had no automation peer, so a screen reader could not reach it: it now reads as a timeline, with its keyboard path and the column chosen.

Revision 269 lets network pairing consider known address translations, as §8.3 lists them (ADR-041 decision 7, `workspace-v8`): a person may state that an endpoint one capture sees - a port forward's, a NAT's or a proxy's public endpoint - is an endpoint the other capture holds, a whole endpoint or an address whose ports pass through. Candidate joins then mirror through the statement, one hop either way and never for a loopback address, which names its own host, under `cross-capture-connection-candidate-v2`; a candidate that does lists each translation it rests on and says the join rests on that statement, which is never evidence of its own. Endpoints are written as InterCat writes them, so a statement cannot fail to match by its spelling. `icat workspace translate` states and withdraws one, `show` lists those in force, and the investigation window's Known translations… does both beside the candidates it changes. One machine cannot capture both sides of a translation, so the candidate it yields is proven on synthetic sessions; live, the verb stated and withdrew one, wrote an IPv6 endpoint canonically, and refused a loopback address.

Revision 270 keeps a person's notes on an investigation, which §8.4's manifest stores (`workspace-v9`): each a revision of the file, about the whole investigation or pinned at an instant of a member's session - which the investigation's time places as it places any other instant, so the merged timeline marks the note on that session's lane with a flag, and its words list says where it falls and how surely. Rewording keeps where a note is pinned, removing it keeps its revisions, and none changes a session or is evidence; a note pinned at an instant is the investigation's pin, so graph pins are left to the session's own window. `icat workspace note` adds, rewords and removes one and `show` lists those in force with their place; the investigation window's Notes tab does the same, pins a new note at the timeline's chosen column by default, and shows one on the timeline, zoomed around it with the cursor on its column. Building it found that the timeline read once per request and dropped a request made while it was reading - a zoom asked for mid-read, or a note to show before the tab's first read had finished - so requests now wait for the read in flight and read again; and that two of the file checks' refusal cases had named the version and the field that would come next, `workspace-v9` and `notes`, so they now name ones no version will.

Revision 271 keeps saved views, the last of §8.4's manifest items (`workspace-v10`): a named interval of the investigation's time, saved under a name that replaces the view of that name, as a revision of the file. An interval is in the time reference's clock, so a view records the reference it was saved under, and one saved before the reference changed is kept, said not to be of the time now, and never shown on another clock. `icat workspace view` saves and removes one in seconds of the investigation's time and `show` lists them; the timeline's Views… saves the interval it shows, and shows a saved one again. Building it found that the domain's interval type, which checks its ends in its constructor, is read back from JSON as an empty one, so a view keeps its two ends as fields of its own; and that the command line built an interval straight from what was typed, so an end before the start would have ended the command with an exception rather than a sentence.

Revision 272 lets a whole session's rates divide by its recording, which `metrics-v1` §7 now defines: from the capture's epoch to the stop reading its clock calibration records, widened to hold every record with a session time, since a real capture delivered a record 2.8 ms before its own epoch. Per second at the whole session, and `icat metric`'s rate asked without an interval, divide by it and say so; a session whose capture recorded no stop still has none, and its whole-session rate stays unavailable. Its live check found that the stop reading had been taken once the session drained, about a second after the recording ended, so it is now taken as the capture asks its session to stop; and that `icat record` never delivers a capture's last second or so, because delivery stops before the session's partly filled buffers are flushed and no ledger counts them lost, which is the next slice's work.

Revision 273 makes a capture deliver its last second. Its stop had ended delivery first and stopped the ETW session after, and a real-time session hands its partly filled buffers to its consumer only when ETW flushes them: every live capture lost what they held, roughly its last second, and no loss counter counted it, since the records were never delivered. A loopback sender running past a 4-second capture's stop showed it, its records ending 1.2 s before the recording did. Asking ETW to flush first did not settle it, since the flushed buffers arrived over the next two seconds in batches. The stop now stops the session while delivery still runs: ETW hands over every remaining buffer, and the pump returns by itself once it has delivered them, 6 ms after the stop in the live run. A stopped session answers no query, so the final loss counters come from the stop's own answer, which TraceEvent's stop discards; that answer also holds the real-time buffers the consumer lost, which the ledger's consumer layer now includes. Every capture path shares the stop - `icat record`, `icat measure` and the broker - and the broker qualification passed on real ETW with it.

Revision 274 fixes what revision 272's live UI check found: at the whole session the inspector's time scope stated the span between the first and last record, beside a rate that divides by the recording, and the per-second choice still said a whole session states no interval. The time scope of a whole session whose capture recorded its stop is now its recording ("All · 4,178 s recorded"), and the span of its records otherwise; the choice says that a session with no recorded stop, such as an import, needs an interval brushed or zoomed to.

Revision 275 lets a live capture's coverage speak for its whole recording (`coverage-v2`). `coverage-v1` bounded every epoch by its first and last delivered readings, because a file does not prove its session was enabled before its first record or after its last; a live capture does know, since it reads its epoch once every source is enabled and, since revision 273, stops its session so that what its sources raised is delivered or counted. A capture that delivered through its stop now records its epoch and stop readings in its ledger, and its epoch speaks for every reading between: a quiet start or end is covered, and a whole-recording rate states the coverage of its mechanisms rather than unknown for all of them. The readings are recorded, not inferred at reading time, because a capture before revision 273 lost its last second uncounted, and widening its ledger to its recording would have called that loss covered. A `coverage-v1` file reads as a `coverage-v2` one that states none, and so do imports and a capture whose delivery had to be ended first. The broker qualification caught that a stale `icat` refuses the new file, which is the strict contract working as intended, and a reminder that the Release CLI it launches must be rebuilt first.

Revision 276 lets a capture that ends before its last publication still be aligned by what it recorded. A capture published its clock calibration only with its last chunk, so one whose broker was killed mid-capture - which recovery closes as interrupted, keeping its published prefix - named neither its boot nor its wall clock, and an investigation could align it only by a stated instant. It now publishes the calibration of its start alone with its first chunk that is not its last, and the calibration of its start and stop with its last, which replaces it: a generation carries at most one, and the whole one holds nothing the early one does not. A follower mirrors each as it appears, so the Desktop's copy of a broker's capture has it as soon as the evidence does. A reader takes a lone sample as the start, whose stop is not recorded, and `icat session` says so; the whole recording, which needs the stop, stays unknown for such a capture, as its rates do.

Revision 277 gives an investigation's results their snapshot vector, M4's "source snapshot vectors" and I16's "every query result names the snapshot vector it answers". A single session's metric query named its generation already; candidate joins and the merged time read several sessions and named none, though a member that records on holds more at every read. Each now states, for each capture it read, the one generation and that generation's manifest digest, since a generation number is local to one session (§10.5): the candidates beside their list and in `workspace-correlation-v4`, the merged time beside each lane. The merged time reads each member twice, for its placement and then for its columns, so a member that publishes in between would be answered from two generations; such a read is taken again, up to three times, and otherwise states the generation of its columns. `icat workspace show`'s overlaps and a person's decisions, which record the alignments they were made under, do not name generations yet.

Revision 278 names the snapshot vector of the last investigation result that had none, `icat workspace show`'s overlaps of one host's captures (`workspace-resolution-v12`), so every result an investigation reads its sessions for now says which generation of each it read. Its live check found the text form printing "Read from ." for an overlap of two unplaced sessions, which reads neither: that line now appears only when a session was read, and the overlap says only that it is unknown. A person's decision about a candidate still records the alignments it was made under and not the generations, since a session that records on would otherwise put every decision up for review at each publication.

Revision 279 fixes a defect in how an investigation file's versions were checked. Every version since the fifth checked a kind of fact the way it was first written: allowed in the newest version, refused in any other. So each time a version was added, a file an earlier version had written with what that version added stopped reading: a `workspace-v5` file with two anchors was refused from revision 265 on, as "a workspace-v5 file holds no alignment with a second anchor", and a `workspace-v9` file with notes from revision 271. Planning the next version found it, since adding one would have repeated it. A kind of fact is now refused only in a file of a version before the one that added it, and a test writes each version's newest kind of fact in a file of that version and reads it back.

Revision 280 keeps the pins a person places on a session's graph in the investigation it was opened from (§26.3's workspace scope, `workspace-v11`): one layout per member, the pinned nodes by their stable keys and where they were put, replaced as it changes. Opening the member from the investigation again puts them back, and the status says where they are kept; a session opened on its own keeps them only while it is open, as before. Writes happen off the window's thread, one after another, and one that fails is said beside the status. Adding the version found the defect revision 279 fixed.

Revision 281 reviews M4 against §14's exit gate. Every item it implements is done, from the workspace file of revision 253 to the snapshot vectors of revisions 277 and 278. Three of its four exit checks hold by test: injected clock uncertainty is exposed, an order is stated only beyond it, and a manual alignment persists and reopens without changing a timestamp. The fourth, a known two-host exchange from separately captured traces, cannot be met on one machine, since one host cannot split an exchange; the status file gives the steps to run it on two, and correlation stays tested synthetically until someone does.

Revision 282 makes `icat workspace show`'s text say what its JSON already held of the pins an investigation keeps: for each session, how many nodes are pinned on its graph, which the Desktop puts back when the session is opened from the investigation. Revision 280's pins were also checked live on a dense capture: the Release app, opened from an investigation, wrote the pin a posted P placed, and a relaunch put it back.

Revision 283 has `icat session` state the recording beside its clock calibration: how long the capture recorded, from its start to its stop, which is what a whole session's rates divide by (`metrics-v1` §7), with its native bounds in the JSON. Its live check found the seconds printed with a decimal point beside the calibration's own figures in the reader's culture; the text now takes the reader's culture and the JSON stays invariant.

Revision 284 makes the machine rung's timeline carry the selected metric when it is a byte sum, as §6.2's cells require: under a byte ranking each mechanism lane plots the bytes the ranking measures - sent, received, or both - per second, on one scale shared across lanes. The overview sums no bytes (`overview-index-v1` §4), so the lanes' bytes are read for the columns drawn, one pass for every lane: the overview's once, and a zoomed view's own when it rests, which replace the overview's there as a zoomed count does. Until they arrive the lanes plot records and the caption says their bytes are being read; a live publication shows the previous one's bytes meanwhile. A column whose records declared sizes none of them recorded draws §6.6's open cross-hatched cell at the occupied floor, keyed in the legend beside the graph's, and one whose records measured zero bytes draws empty, which the §6.2 rule above now says. A lane none of whose records carries the metric's size, as a lifecycle or RPC lane under bytes sent, says so beneath its name ("no sends"), so it does not read as quiet. A card states what its bar plots, the records that measured it and those that recorded no size, and its byte rate against the busiest lane. The selection's highlight and the live edge count records, so neither is drawn on the byte scale: the highlight waits for a records ranking, and the live edge keeps a records scale of its own, labelled so. The lower rungs' lanes count records under a byte ranking and say so; plotting their bytes, and bytes in the persisted overview so a first view draws volume without a read, remain open.

Revision 285 does the same for a group's rung, where ranking by bytes and reading a process lane's volume belong together (§3.3's "switch lanes to process or channel and sort the ranked table by activity or measured bytes"): each process lane plots its own records' bytes, and the machine row above it every record's, on the scale they share, read in one pass over the columns the lanes were counted in - the machine row's and, past the lanes' cell budget, their own fewer and wider ones. A lane's rows are chosen as its count chooses them, each record in the lane of the process instance that canonically owns it under the evidence policy. A process lane's bar keeps the hue its records' most frequent mechanism gives the same interval, as the records view draws it, and its card says so ("mostly TCP records"): a byte sum mixing mechanisms has no hue of its own, and splitting every lane's bytes by mechanism would triple what each worker of the pass holds. A process's direction rows and a channel's end lanes still count records under a byte ranking and say so.

Revision 286 completes it for every rung the ranking orders: a process's direction rows each plot the process's own records' bytes of that source direction, and the machine row above them every record's, read in one pass over their shared columns, a record in the row its source's direction names as its count puts it. Under bytes sent the outbound row carries a process's sends, and a row with none says "no sends" beneath its name, as a lane does. A channel's end lanes are drawn at a rung no ranking orders, and keep counting records. A rung whose rows could not be counted says its timeline counts records there, not bytes; while they are being counted it says that instead.

Revision 289 keeps the machine rung's lane bytes with the persisted overview (`overview-index-v1` minor 2). When a finished session's checkpoint is published, each overview column's bytes per mechanism are summed in the same pass a read of the lanes makes, and a viewer asked for the overview's own columns answers from them: the lanes of a reopened session under a byte ranking open no segment, and plot exactly what a read would, unmeasured columns included. Any other columns - a zoomed view's, or the whole session at another width - are read as before, and so is a live session's whole overview until its writer publishes one. Choosing a byte ranking still reads the ranked table's whole-session bytes per process, which nothing persists yet, so the first byte view reads the segments once where it read them twice. Keeping each process's bytes with the derivation checkpoint, and a relationship's with its relation, is what would let it read none.

Revision 292 keeps them, and the first byte view of a finished session reads no segment (`overview-index-v1` minor 3). They are kept with the persisted overview rather than the derivation checkpoint: they are a count of a finished session's records, as the lanes' bytes are, and are summed in the same pass, while the checkpoint holds the state later generations extend. Every process instance's bytes are kept at each strength its records bind at, and every TCP channel end's at the one strength its holder's records bind at, before any evidence policy. A reader applies the policy as a read would, so the ranked table's whole-session byte ranking and the graph sized by bytes equal a read of every segment under every policy, a candidate's bytes and a process connected to itself included. A reopened session's lanes, ranked rows and graph under a byte ranking now open no segment between them. An interval is still read, as is a session with more TCP channels than an overview keeps the ends of (20,000) and a live session. Bytes naming an instance or channel the checkpoint's derivations do not hold are not used. At a million rows, a reopened session's whole-session byte ranking answered in 26-30 ms where a read of every segment took 162-182 ms, and publishing the checkpoint took 475-521 ms with the process bytes or without them: they are summed in the pass that sums the lanes'.

## 8. Time and multi-machine investigations

### 8.1 Preserve native clocks

Record each source clock and frequency, raw timestamp, capture epoch and conversion policy. Use integer session-relative time for indexing, with checked wide intermediate arithmetic. Preserve native ticks even if the internal display/index resolution is nanoseconds. Precision of representation is not accuracy of measurement; do not advertise nanosecond accuracy simply because a field stores nanoseconds.

For owned ETW sessions prefer a suitable monotonic clock, validated against the actual ETW consumer conversion mode. Collect paired monotonic/wall-clock calibration samples with acquisition uncertainty. Imported traces retain their recorded clock mode. Avoid converting an already converted ETW timestamp a second time.

QPC is not synchronized to external UTC, so values from different hosts cannot be directly compared. Wall-clock alignment also depends on clock synchronization and its accuracy. [Microsoft timestamp guidance](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps).

### 8.2 Alignment model

An investigation maps each host's relative clock into a workspace clock using versioned affine or piecewise-affine segments:

```text
workspaceTime = scale * hostRelativeTime + offset
u(t)          = u_systematic + sqrt(u_calibration^2 + u_synchronization^2 + u_drift(t)^2)
```

Uncertainty is a symmetric half-width in workspace ticks, never a percentage and never a quality word. Independent random contributions combine in quadrature; any contribution known only as a bound, including an unverified synchronization claim, is added linearly as `u_systematic`. If a required contributor is unknown the total is unknown, not zero and not a default, and every cross-host ordering, latency and pairing conclusion that depends on it is withheld rather than estimated (R3, R21). Comparing observations on hosts A and B uses the pair uncertainty `u_pair = sqrt(rA(tA)^2 + rB(tB)^2) + sA + sB`, where `r` is a side's random part - the square root in its `u(t)` - and `s` its systematic part, so each part is counted once; their order is reportable only when `|tA - tB| > u_pair` and is otherwise ambiguous, which is the rule the cross-host scenario in §21.1 asserts.

Store original samples, fit residuals, valid intervals and evidence provenance. Offer three alignment modes: recorded wall clock, shared marker/activity evidence, and explicit manual alignment. Manual alignment is an annotation, never a rewrite of source timestamps (I9). A single anchor supplies offset only; drift estimation requires separated anchors and a validated model. Split segments at wall-clock discontinuities or unsupported clock behavior.

A shared activity ID does not make a send and receive simultaneous. Network marker exchanges constrain offset through measured round trips and delay assumptions; they are not exact equal-time anchors. Keep calibration evidence independent from the relationship being tested where possible. Do not fit an offset from a guessed connection pairing and then use that fitted proximity as independent confirmation of the same pairing.

If synchronization accuracy is unknown, display it as unknown; a small local sampling error does not bound remote clock offset. Refuse an unjustified one-way latency number. A cross-host time difference smaller than combined uncertainty has ambiguous order. Preserve stable display tie breaks without calling them causal order.

### 8.3 Cross-host correlation

Host IDs are opaque identities derived from evidence. A live capture's is derived from its Windows installation's own identity together with its machine's name and build, so captures of one installation share it while a renamed or reinstalled machine, a clone given a name of its own, or another machine of the same name each has its own; an imported file's is derived from its content, so it is the file's own; a redacted package's is minted. Equal host IDs are the strongest local evidence of one host, never proof - an exact clone shares its original's - and different ones are never merged without a person. Hostname/IP equality is not identity: DHCP, NAT, VPNs, aliases and cloned VMs can invalidate it. User-confirmed host/boot equivalence is versioned. Local IPC objects never match across hosts just because names coincide.

Network pairing considers protocol, addresses/ports, lifetime overlap under timing uncertainty, known address translations and source identifiers. Unknown NAT or proxy boundaries remain visible endpoint nodes. RPC correlation uses explicit propagated identifiers if available; client and server activity IDs are not assumed to be automatically shared. Content hashes, if an approved decoder produces them, are sensitive optional supporting evidence, not a default global matching mechanism.

The correlation UI previews candidate joins, their evidence and alternatives. Accepted manual joins retain `Manual` provenance. Default graph distinguishes candidate from established relations. Re-aligning time invalidates dependent candidates, ranking caches and graph projections without mutating source sessions.

### 8.4 Investigation persistence

An `.icat-workspace` manifest stores capture content identities, selected generations, host aliases, clock mappings, correlation revisions, graph pins, notes and saved views. Sources remain separately valid `.icat` sessions. Re-importing the same capture must not duplicate its observations; partial overlap between different captures is flagged and handled conservatively, not deduplicated by time alone.

Export can package selected captures plus the workspace. Missing captures reopen as unresolved references with a relink workflow. Automated remote installation, synchronized remote start/stop and streaming over the network are later features, not prerequisites for multi-machine analysis.

## 9. Architecture and module boundaries

```mermaid
flowchart LR
  OS[Windows ETW and inventory] --> Broker[Privileged capture broker]
  Broker --> Raw[Authoritative admitted-event journal]
  Raw --> Decode[Unprivileged decode and normalize]
  Import[ETL and InterCat imports] --> Decode
  Decode --> Store[Immutable observation segments]
  Store --> Correlate[Versioned entity and relation analysis]
  Correlate --> Snapshot[Analysis snapshot]
  Store --> Snapshot
  Snapshot --> Query[Shared query engine]
  Query --> Timeline[Timeline and minimap]
  Query --> Graph[Communication graph]
  Query --> Detail[Ranking, evidence and content]
```

| Module | Responsibility |
|---|---|
| `InterCat.Domain` | IDs, clocks, measurements, source capabilities, filter AST, query/result and evidence contracts; no Windows/UI dependencies |
| `InterCat.Storage` | Immutable columns, dictionaries, checksums, manifests, raw references, reader leases and crash recovery |
| `InterCat.Analysis` | Entity lifetimes, correlation, alignment, aggregation, ranking, adjacency queries and coverage propagation |
| `InterCat.Application` | Capture/import/analyze/export use cases, cancellation, budgets, snapshot publication and workspace lifecycle |
| `InterCat.Capture.Windows` | ETW adapter, TDH/TraceEvent decoding boundaries, inventory, profile negotiation and build-specific capabilities |
| `InterCat.CaptureBroker` | Elevated executable: session ownership, allowlisted capture control, bounded transport, resource limits and audit state |
| `InterCat.Desktop` | Avalonia workspace, Skia timeline/graph, accessible tables, inspector and commands |
| `InterCat.Cli` | Scriptable local capture, import, inspect, query, verify, export and capability reporting |
| `InterCat.TestWorkloads` | Reproducible communicating processes, application truth logs and adversarial fixtures |

Keep platform adapters outside the portable domain/query code even though the product is Windows-focused. This supports deterministic offline tests, not a commitment to another desktop platform. The broker handles only privileged capture operations; parsing imported archives, drawing UI and executing payload decoders must not require elevation.

### 9.1 Capture adapter interfaces

```text
ProbeCapabilities(environment) -> CapabilityReport
ValidateProfile(request, capabilities) -> EffectiveProfile + omissions
StartCapture(effectiveProfile, sink, budgets) -> CaptureHandle
ObserveCaptureHealth(handle) -> counters + coverage transitions
StopCapture(handle) -> final source metadata

Decode(rawRecord, savedSchema) -> observations + decode diagnostics
Correlate(snapshot, changes, budget) -> immutable relation delta
Query(snapshot, AnalysisSpec, viewport, budget) -> versioned result
```

Adapters cannot silently change a requested profile. Store both requested and effective settings and the reasons for omissions. Plugin-like interfaces do not imply unrestricted in-process third-party code: external decoders later run in isolated, unprivileged workers with resource limits.

### 9.2 Capture lifecycle

States: `Idle -> Probing -> Starting -> Recording -> Stopping -> Finalizing -> Closed`. `Recording` begins when the first provider is enabled and delivery has started, not when the startup inventory finishes: inventory runs concurrently with early `Recording` and completes into a recorded witnessed-presence interval (§18.5). Failures produce `Degraded` or `RecoverablePartial` with concrete diagnostics; degradation is orthogonal status rather than a lifecycle state (§20.3). View pause is independent. Restart creates a new recording epoch and an explicit gap.

Use unique ETW session names and explicit ownership tokens. Never stop a session solely because its name resembles InterCat. Startup failure cleans up only resources created by that attempt. Detect other profilers/session limits and explain conflicts without disabling them. A broker lifetime/lease policy stops orphaned captures by default; headless CLI recording has an explicit owner and bounded duration or retention policy.

The initial capture requests one UAC elevation for the broker when required. The viewer stays at ordinary integrity. Use a local authenticated control pipe with restrictive ACLs, remote-client rejection, client identity verification, bounded/versioned messages and a small command allowlist. Validate destinations through a broker-owned capture directory and resist junction/reparse substitution. No arbitrary shell execution, arbitrary provider configuration supplied by imported files, or generalized privileged file access.

The broker-owned directory is a boundary, not a location. It is created with its protected DACL and its high-integrity `no-write-up` mandatory label already applied by the creating call, never set after a permissive directory exists. It is validated from an open handle rather than from the path that was asked for: the final component must not be a reparse point, the handle's final path and volume must be the configured ones, and the security read back from the handle must be exactly the declared descriptor, ACE for ACE, with a difference named rather than repaired into silence. The handle stays open for the broker's lifetime without sharing delete access, so the directory that passed validation cannot be renamed away from under later writes. Three facts about Windows make the rest of the rule specific:

- A DACL alone does not refuse an ordinary-integrity write from the same user. The mandatory label does, and is therefore required rather than optional. A root whose label is missing or lower than the broker's is refused rather than used.
- In a filtered administrator token `BUILTIN\Administrators` is present for denial only, so a viewer's read access must come from an explicit ACE for the capturing user's own SID. Relying on the administrator entry would leave an ordinary-integrity viewer unable to read its own evidence.
- Holding the root's handle stops the root being renamed; only an already-trusted parent stops an ancestor being renamed around it. The parent is therefore checked for a trusted owner, and every broker file is opened by a single validated name beneath the validated root rather than by a composed path.

A root that already exists is adopted only when a trusted principal owns it. If its security has drifted, the broker re-applies the exact declared descriptor and validates again; if an untrusted principal owns it, the broker refuses and says so instead of repairing a directory an attacker may have prepared. Because the default ACL of `%ProgramData%` lets an ordinary user create entries there, the root can be squatted before the broker first runs; that is a refusal the user can act on, and §20.5 records that installation should pre-create the root.

Each capture writes into one `capture-<id>` child of that pinned root, not into a shared flat namespace. The broker creates the child with the same protected ACL and mandatory label, validates its open handle and holds it without delete sharing while recording. An existing child whose security differs is refused rather than repaired, because it may already contain evidence. The ordinary-integrity follower reads committed evidence there and writes the derived session in its own user-owned directory; it is never given write access to the broker child (ADR-027).

### 9.3 ETW ingest and overload

An ETW callback must promptly copy the necessary record data out of callback-owned memory into bounded pooled buffers. It must not perform graph queries, symbol lookup, name resolution or blocking storage writes. A full queue cannot safely throttle Windows event generation; return promptly and record application drops using counters that do not depend on the full queue.

Use separate logical stages for acquisition, raw preservation, decode, entity enrichment, correlation, immutable segment commit and snapshot publication. ETW real-time and file consumers use the documented consumption APIs; TDH provides schema information. [Consuming ETW events](https://learn.microsoft.com/en-us/windows/win32/etw/consuming-events), [TdhGetEventInformation](https://learn.microsoft.com/en-us/windows/win32/api/tdh/nf-tdh-tdhgeteventinformation). Evaluate TraceEvent as the implementation adapter, not as the domain model. [Microsoft TraceEvent guide](https://github.com/microsoft/perfview/blob/main/documentation/TraceEvent/TraceEventProgrammersGuide.md?plain=1).

The selected implementation default is a broker-owned, bounded admitted-event journal as the authoritative source for live capture, consumed incrementally by unprivileged analysis. This preserves one record identity from live viewing through replay and applies content policy before InterCat persists event bodies. Optional ETL recording is a separately labeled diagnostic artifact, not an automatically merged second source. ETL-only import remains supported. M0 benchmarks journal fidelity/overhead against ETL; changing the default requires an ADR and updated identity/privacy tests. Do not assume a growing ETL supports arbitrary live random access or that live delivery and file replay have identical loss or order. An unfiltered ETL cannot be described as sanitized. Sections 18.1–18.4 define the exact contracts.

Bound reordering by a watermark and configured lateness window. Equal timestamps use deterministic source/ordinal tie breaks without implying causal order. Late observations go into new sorted delta segments, not dropped or silently retimestamped. Finalized offline ingest can compact them. Every published snapshot includes all data/relations at its declared generation.

A watermark is an optimization boundary, not proof of source completeness. Quiet/idle streams cannot stall publication forever; record their idle status and admit subsequent late records normally. Coverage state remains independent from the event-time watermark, and a stalled source is not equivalent to a stopped source.

Capture health records ETW reported lost events/buffers, broker/application drops, undecodable events, lag, disk failures and provider transitions as different quantities. ETW counters may only bound a loss to a polling interval; do not fabricate exact missing timestamps or add overlapping counters into a fictitious total. [ETW session properties and loss counters](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace_properties).

Overload response: reduce nonessential UI refresh, defer enrichment/layout, use reserved bounded buffering, and expose lag. Stop cleanly at configured disk limits or use explicit rolling retention. Never silently disable providers or switch to sampled capture. Any user-selected sampling or adaptive profile change creates a new coverage epoch.

### 9.4 Capture profiles

| Profile | Contents and use |
|---|---|
| Explore | Lifecycle + validated network/RPC metadata; optional validated pipe metadata if overhead permits; no payload-producing debug settings by default |
| Focused transport | One validated mechanism with optional selected-process focus; uses capture-side filtering where enforceable, otherwise requires explicit wider-capture consent; preserves required lifecycle/correlation context |
| RPC peers | Explore's lifecycle and RPC calls plus the kernel's ALPC messages, in a private system logger of the capture's own, to find the process that served each local call (ADR-034, ADR-035); opt-in, because collecting ALPC measured Moderate |
| Timing | Explore plus selected thread scheduling/stack evidence after an overhead preview; avoids always-on stack collection |
| Content | Explicit scope, allowlisted payload-capable sources, byte limits, retention and inspection settings; unsupported mechanisms remain unavailable |
| Flight recorder | Bounded rolling history with visible oldest retained time; pin/export freezes required evidence before eviction |

These rows define requested intent, not permission to enable every named source. Before capture, compile
an immutable effective profile against the machine's schema, adapter guarantees and measured overhead.
Store and show both forms. A required source that cannot meet the contract blocks the capture; an optional
source is omitted with its exact reason. Explore's minimum useful effective profile is process lifecycle
plus validated network metadata. RPC and pipe breadth remains requested-but-optional until each source
has both a bounded adapter and capture-impact evidence. ALPC is never Explore's: it needs a system logger, of which a
machine runs at most eight, and its collection alone measured Moderate. It is the RPC peers profile's, which the
operator chooses. Never substitute a different profile or body mode.

Capture-side filtering and view filtering are different. If a provider cannot filter on PID before emission, or required peer/lifecycle context must remain broader, disclose the per-source effective scope and block start until the operator explicitly accepts the wider metadata collection. Consent is attached to the exact compiled request; it is not inferred from choosing a process-focused view. Capture enough peer/lifecycle context to explain selected activity, while recording any excluded context.

Request-contract availability and capture availability are separate states. A Content request may be validated and previewed so the operator can review exact source/process/channel selectors, per-record and session limits, retention, inspection consent, truncation and unknown-schema behavior. That preview keeps `bodyPolicy` and provider requests absent and remains unable to start until the selected source has approved content descriptors/classifications, enforces the requested scope before persistence, and carries capture-impact evidence measured with its payload-producing settings. A metadata-only benchmark does not qualify a content enablement.

## 10. Storage, snapshots and query implementation

### 10.1 Durable format

Use `.icat` session directories and optional verified `.icat.zip` transport packages. Version the format independently from application releases. Suggested layout:

```text
session.icat/
  manifest.json
  sources/             raw ETL or event journal chunks, schema snapshots
  observations/        immutable sorted column segments
  entities/            process/thread/resource revisions and dictionaries
  relations/           immutable correlation revisions and evidence links
  indices/             time, postings, adjacency, intervals and aggregate tiles
  coverage/            capture configuration epochs and health/loss ledger
  content/             optional restricted payload chunks
  annotations/         user bookmarks and saved views
```

Manifest includes UUIDs; host/boot evidence; actual Windows build; adapter/schema/normalizer/correlator versions; clock definitions; requested/effective profile; counts; extent; finalized/partial state; checksum/file inventory; content policy; and retention epochs. Retain imported source bytes unchanged when requested. A derived/redacted package has a new identity and explicit provenance.

Use append-only journal records and immutable sorted segments with atomic manifest publication after durable writes. Recovery selects the last valid generation and reports incomplete tails. Checksums detect corruption; they are not a claim of hostile-tamper resistance or forensic chain of custody. Guard all lengths, offsets, decompression sizes, paths and dictionary cardinalities.

Readers lease generations. Compaction, retention and deletion cannot remove referenced segments/content. Pinning a snapshot must either reserve disk space or materialize the selected evidence; do not promise indefinite pins within a strict circular quota. Export freezes a generation and verifies its dependencies before publishing the result.

A lease is one generation and every dependency it names, taken together and never migrating to a later generation, with a kind and an expiry. An interactive lease is short and reserves nothing; a pinned one declares the allowance it reserves and is refused when it would reserve less than the evidence it pins. An expired lease holds nothing and is not revivable, because a hold that can come back from expiry bounds nothing.

### 10.2 Indices suited to IPC

Memory-map fixed-width columns and keep variable metadata/content in separate blobs. Use low-cardinality bitmaps for mechanism/kind/quality. Use compressed postings or sparse indices for high-cardinality process, endpoint and channel IDs; do not allocate a full bitmap for every unique resource. Add time-block min/max metadata, directional adjacency postings and interval start/end indices.

Counts can use rank subtraction. Byte metrics need weighted sums: block summaries for compatible unfiltered spans and vectorized masked scans or cached filtered summaries for the rest. A count bitmap alone cannot answer byte sums. Keep values nullable with availability counters. Use checked wide accumulators so long high-volume captures cannot wrap silently.

Maintain the durable multiresolution overview pyramid required by S4, updated incrementally at commit, so a whole-session overview never needs recomputation at open. Beyond it, do not precompute the Cartesian cube of host × process × peer × resource × operation × time: build compact common summaries and bounded query-driven caches. Dictionary and correlation-state growth have explicit budgets and fallback policies.

### 10.3 Aggregate contract

For a viewport `[t0,t1)` and device width `W`, define boundaries using integer arithmetic:

```text
b(i) = t0 + floor((t1 - t0) * i / W), for i = 0..W
cell(i) = [b(i), b(i+1))
```

Use wide intermediates. Two boundary policies are fixed rather than left to the renderer:

- **Sub-tick zoom.** Clamp the column count to `Weff = min(W, t1 - t0)` in native ticks, so no two boundaries coincide and every cell spans at least one tick. Draw `Weff` cells across the full plot width, each occupying `W / Weff` device pixels, and state the effective resolution in the axis and the hover card. A cell that is empty by construction is never drawn or exported as an observed zero (R21).
- **Fit range.** A fit computes `[firstStart, lastEnd)`, where `lastEnd` is the greatest end among eligible observations and, for a point observation, its instant plus one native tick. The final observation therefore lands inside cell `Weff - 1` rather than on the exclusive endpoint. §6.2's overscroll allowance applies to the resulting viewport, never to the fit range itself.

Each eligible point observation belongs to exactly one cell and the final endpoint is exclusive (I3, I4). Rendering never rewrites or coalesces source observations.

An aggregate cell stores count; compatible byte sum; known/unknown measurement counts; duration distribution reference where meaningful; coverage state; provisional state; and query identity. For operations, separate start counts, completion counts, overlap count and occupancy. A long operation contributes clipped occupied time to each intersected bucket but is not a newly started operation in each bucket. Censored operations are excluded from completed-duration percentiles and counted separately.

Coverage and quality roll up into a cell by worst case, never by averaging. A cell's coverage state is the maximum over the ordered lattice `Covered < ReducedFidelity < PartialGap < NotCollected < Unknown` across every (mechanism, source) pair contributing to the eligible set over that cell's interval, and the cell retains the number of contributing sources and the identity of each defective one so the hover card can explain the state. Quality rolls up per dimension — attribution, correlation, measurement, timing — as the worst value in that dimension plus the count of contributions at each value. Dimensions are never combined into a scalar, and no roll-up yields a value better than its worst contributor (R2).

Build a multiresolution tile hierarchy for common lane/metric views. Merge additive summaries exactly; retain a documented approximation for quantile sketches. Arbitrary filters must fall back to valid filtered queries rather than reuse unrelated tiles. Boundary fragments require exact treatment. Label approximate top-K/quantiles; exact evidence drill-down always uses the contributing records and predicate.

### 10.4 Query identity and API

```text
AnalysisSpec:
  CaptureSnapshotVector, NormalizationRevision, EntityRevision
  CorrelationRevision, AlignmentRevision
  Basis, FilterExpression, TimeScope, LayerProjection, Grouping
  Metric + ByteDomain + AccountingSide, EvidencePolicy

QueryIdentity:
  hash(AnalysisSpec), Viewport, RequestedRows, QueryGeneration

QueryTimeline / QueryOverview / QueryGraph / QueryRanking
QueryOperations / QueryEvidence / QueryCoverage / QueryContent
```

The snapshot vector contains one generation per capture. A workspace query cannot read arbitrary “latest” generations midway through execution. Changing metric, evidence policy or alignment invalidates affected caches. Use cancellation and supersession; no result may publish after its identity becomes obsolete.

Filter AST supports host, process instance/executable, peer, mechanism, layer, endpoint, direction, operation, status, time, quality and source. Restrict text search to selected metadata/content fields; regex has explicit time and result limits. Discovery facets may omit their own dimension to reveal alternatives, but are labeled with that scope. They are not promised to equal the final result count.

Details use keyset pagination over stable order keys. For live changes, preserve selected observation IDs and show when the result's as-of snapshot differs from capture latest. Graph edges reference relation/evidence IDs so every visual claim is explainable.

### 10.5 Canonical analysis specification and query identity

The UI and the CLI must produce byte-identical identities for the same request (R18), so the canonical form is a contract rather than an implementation detail. Normalize the filter expression first:

1. Resolve every name to its enumeration code (§23) and every time to native ticks.
2. Constant-fold, drop no-op terms, and reject an empty include set rather than treating it as “all”.
3. Push negations to leaves and keep `is unknown` predicates explicit (§19.1).
4. Deduplicate and sort include values within a facet by value code; sort facets and independent AND terms by dimension code.

Then serialize the whole specification:

- UTF-8, no insignificant whitespace, members emitted in the declared order of §23's specification schema.
- Integers in decimal without leading zeros; identities in lowercase hexadecimal; no floating-point value anywhere — instants and durations are integer ticks, ratios are explicit numerator and denominator pairs.
- Optional members absent rather than null. Defaults are materialized before hashing, so “unset” and “set to the default” hash identically only when they are semantically identical.
- Every version axis of §24 the result depends on is included, and no other: an axis or term that cannot change the answer, such as an evidence policy when no record is bound to a process, is absent, so it cannot split one query into two identities.
- Each snapshot vector entry carries its generation's manifest digest. A generation number is local to one session, and two sessions holding one capture can reach the same number with different contents.

`QueryIdentity = (canonicalizationVersion, SHA-256(canonicalBytes), Viewport, RequestedRows, QueryGeneration)`. The canonicalization version prefixes the hash, so a future change to the canonical form cannot silently collide with cached entries. `RequestedRows`, the viewport and the query generation cut or place a result without changing what it aggregates, so they sit beside the hash rather than inside the canonical bytes. `contracts/query-identity-v1.md` freezes the form for the members `metrics-v1` implements. A golden corpus mapping specification to canonical bytes to hash is part of the test suite, and the CLI can print the canonical form and hash of any accepted specification, so a UI/CLI mismatch is diagnosable rather than mysterious.

## 11. Payload inspection, privacy and source fidelity

Opt-in content inspection is part of the first release, even though many baseline sources supply metadata only. Implement the evidence model, bounded viewer, availability UX and controlled fixture path immediately. Production content adapters ship only for validated source semantics; no generic interception promise is needed to make the viewer useful.

### 11.1 Two separate content choices

**Collection:** enabling payload-producing sources is explicit before capture. Preview scope, mechanisms, source fields, limits and retention. Metadata-only recording persists event bodies only for a validated descriptor/schema allowlist; unknown or unapproved bodies are omitted with a visible coverage diagnostic. Provider-side filtering is preferred but is not a universal content guarantee. Events may transiently reach the OS or callback before admission; the guarantee concerns InterCat's retained evidence, not all Windows buffers. An adapter unable to enforce its body contract is unavailable in this profile. Original ETL preservation is a separate source-retention choice. Section 18.2 specifies admission and unknown-event handling.

**Inspection/import:** imported ETL or instrumented traces may already contain content. Explain that before enabling content previews. Inspection consent does not change what the imported file already contains. Preserve its source policy and do not silently place previews into search snippets, reports or crash diagnostics.

Payload search, reassembly and export have separate deliberate commands. Keep content out of routine telemetry/logging; the product itself has no automatic upload path. Endpoint names, command lines and RPC options can also be sensitive metadata.

### 11.2 Content data contract

Store source bytes plus content classification: `OpaqueProviderData`, `TransportFragment`, `ApplicationPayload`, `DecodedFields` or `EncryptedContent`. Never automatically interpret arbitrary ETW event payload as bytes sent by the application. Keep a decoder's output linked to the original fragment and decoder version.

Require per-record and per-session byte caps, truncation flags, missing-range markers, direction, encoding and original length when exposed. Never pad missing bytes and present a complete message. Stream reassembly is mechanism-specific: one TCP event is not one application message, and equal read/write buffer sizes do not establish boundaries. Binary decoders run with length limits, timeouts and isolation; no scripts, macros or active rendering from captured content.

First-release inspection: hex, bounded text, structured event fields, byte-range selection and explicit binary export. Protocol decoders are later incremental work, except a small synthetic fixture decoder used to validate the model. TLS/other encrypted data remains encrypted unless the selected source legitimately supplies pre-encryption application content.

### 11.3 Storage and sharing

Restrict session ACLs to the capturing user and necessary broker identity. Optional encryption uses a reviewed authenticated-encryption library and OS-protected key storage; portability requires an explicit key/password wrapping design. Do not invent cryptography or suggest local encryption removes exposure while content is being inspected.

Export presets: metadata-only derived report, redacted normalized session, or original evidence package. Show included fields, hosts, raw files and payload chunks before export. A filtered/redacted package must not include an original unredacted ETL as an unnoticed attachment (I22). Redaction produces new dictionaries/indices and scans annotations/content references for leakage; original evidence stays separate. Deleting files is not a guaranteed secure erase on SSDs.

Revision 104 implements the **metadata-only derived report** preset as `intercat-share-report-v1`, with an explicit
allowlist, per-report random relationship tokens, JSON/CSV disclosures, and a separate Desktop sharing action with
pre-save warning. Its exact field policy and threat limits are in
[`SHARING-REPORT-REDACTION.md`](SHARING-REPORT-REDACTION.md). This does **not** satisfy the redacted normalized
session preset: a report cannot reopen as a session, and the package must be built with new identity, provenance,
dictionaries and indices, plus an annotation/reference leakage scan and I22 inspection. The original evidence
package remains a third, explicitly unredacted choice.

Revision 105 audited the path against the store: `CommitReplacingDerived` carries the admitted journal, plan, ledger and
finalization marker by design, so a redacted package cannot be a replacement generation of its source. Revision 106
implements the package that audit called for, frozen in [`redacted-session-v1`](../../contracts/redacted-session-v1.md):

- **A new session, not a relabelled one.** Fresh session, capture, clock and host ids; `sourceIdentity` names the
  contract; a synthetic journal record per row (pseudonymous descriptor and header, no body, no extended item) under the
  admission policy `normalized-session-redaction-v1`; rows, source fields, dictionaries and segments rebuilt from the
  projection; the coverage ledger rewritten under the same pseudonyms; the plan and finalization marker omitted; a new
  `RedactionPolicy` dependency (store code 8) as provenance that retention cannot release. The drill-down resolves only
  into the package and every reader calls its entries synthetic records.
- **Fidelity is a requirement, not a hope.** Every analysis reads identity by equality, so each namespace is a
  bijection on the source's values with fixed points where a value means the same on every machine and replacing it
  would change a conclusion: address and port 0 (a relation's incomplete endpoint), loopback, PIDs 0/4/-1, empty
  identifiers. Names are pseudonymized a path component at a time, executables case-insensitively, so an image path's
  file name and an exit's bare name stay one executable and groupings are unchanged. The capture epoch moves to whole
  seconds from zero, keeping relative time exact while hiding boot-relative and wall-clock readings.
- **Verified before it exists.** The package is built beside its destination and renamed only after it is reopened as
  a recipient would; every reference and value is checked against the pseudonyms issued; the source's tallies, sums,
  fixed points and distinct counts are reproduced; and every byte is searched for the source's identities and names
  (§8 of the contract). Mutation tests show a moved fixed point or a case-sensitive executable key is refused by this
  check, not only by the test suite.
- **Where.** `icat package --redacted` (with `--check`) and the Desktop's "Share redacted session…", which states what is
  kept and left out, shows cancellable progress, and offers to open the package for review.

On a real imported session (852 rows, 3,104 source fields, 534 processes) the package reproduced every process instance,
parent link, grouped byte total and per-process count, and no source identity, name or absolute reading was found in it.

Revision 154 implements the third preset, the explicitly unredacted **original evidence package**
([`original-evidence-package-v1`](../../contracts/original-evidence-package-v1.md)). It is an exact copy of a session's
current generation: every file the generation names, byte for byte, with its manifest, a pointer naming it and the
evidence lease guard. It reopens as the same session at the same generation and digest. It holds nothing else: no
earlier generation to fall back to, no superseded manifest, no stray file, and no ETL the session was imported from.
The source generation is leased while it is copied, and each file is hashed as it is copied, so a file that changed in
place after the session was opened is refused before the rest is. The package is built beside its destination and
reopened by a fresh store, which hashes it again, before it is renamed into place. What it holds is stated first, from
the files themselves: the generation, its records and every file, the unredacted contents, the host identity, and the
warning. `icat package --original` (with `--check` to measure only) and the Desktop's "Share original session…" do
this. The Desktop's confirmation sizes itself to what it states and starts on Cancel. A killed real capture of 27
journal chunks was packaged as 34 files and reopened as its generation 28.
Limits: 1,000,000 rows per package, one capture on one clock, and pseudonymized is not anonymous.

## 12. Performance and responsiveness targets

These are proposed engineering acceptance budgets, not measured results. Revise them openly through an ADR with measured evidence rather than quietly dropping a benchmark. Report broker, analysis and UI costs separately, including OS ETW buffers and mapped-file residency.

A result states whether the machine it ran on met this reference, measured rather than asserted: throughput is what defines the device here, so a harness measures the volume it will write to before it starts capturing, and reports the figures next to the budgets they are compared against.

Reference machine for every number in this section, confirmed or revised in M0: an x64 desktop-class CPU with 8 physical cores and 16 threads; 32 GiB RAM; an NVMe SSD sustaining at least 2 GB/s sequential read and 1 GB/s write; a 2560 × 1440 display at 100% scaling; the primary supported build of §1.3; and no other profiler or tracing session running. Publish the exact CPU and storage models, Windows build, source schema versions, workload seed and distributions with every result: a number without them is not a measurement.

| Area | Initial target and measurement |
|---|---|
| First feedback | Capture status/coverage immediately; first useful overview within 3 seconds of first delivered events, with provider flush latency reported separately |
| Interaction | Cached pan/zoom drawing p95 within a 16.7 ms frame budget; uncached query never blocks input |
| Timeline query | Warm 2,000-column × 40-visible-lane common query p95 under 100 ms at 10 million observations; publish coarse preview before a longer exact refinement |
| Graph/ranking | Bounded 200-node/500-edge projection and top-100 ranking p95 under 250 ms for common indexed scopes at 10 million observations |
| Detail | First evidence page p95 under 150 ms for indexed filters; content preview streams with a strict size bound |
| Ingest | Sustain 100,000 normalized metadata observations/second for 10 minutes on the reference machine with no application drops; explicitly measure provider/ETW loss separately |
| Bursts | Exercise 1 million observations/second for short bursts; bounded memory and accurate loss disclosure are mandatory even if lossless handling is not achieved |
| Scale | Validate 1M, 10M and 100M observations; 100M is a scale qualification tier, not the first vertical-slice gate |
| Memory | Initial configurable default budgets: 256 MiB acquisition queues, 512 MiB analysis/cache, 512 MiB UI/layout; measure total private bytes/residency separately and cap ETW buffers independently |
| Reopen | Indexed 10M-observation session usable within 3 seconds on reference NVMe without re-decoding the source |
| Capture impact | Target under 5 percentage points of total-machine CPU for Explore at the stated workload, and under 5% workload-throughput regression; measure both against a no-capture baseline |

End-to-end live latency budget, so that “first useful overview within 3 seconds” is verifiable stage by stage. Provider flush latency sits outside this budget and is reported separately:

| Stage | Budget |
|---|---|
| Callback admission and copy, per record | p99 under 50 µs with no unpooled allocation (R9, R11) |
| Admitted record to committed journal batch | p95 under 250 ms |
| Committed batch to normalized immutable segment | p95 under 500 ms |
| Normalized segment to published analytic snapshot | p95 under 250 ms, with analytic snapshots published at up to the 4 Hz UI freshness target (§19.3); this is not a journal-chunk cadence |
| Published snapshot to first drawn frame | p95 under 100 ms |
| Event acquired to event visible | p95 under 1.5 s, p99 under 3 s |

Revision 97 measures first feedback on the Desktop's own live path (`InterCat.BrokerQualification first-feedback`, `bench/results/first-feedback-*`). The harness runs the unchanged Desktop capture runner with the qualification tool as its broker over a qualification root. It stamps each overview it hands to the window with the machine's QPC reading, and computes every record's delay from its own QPC reading to the first overview that included it. That makes the delay exact per record, not estimated. Baseline, three 15-second runs: the first overview arrived 3.2–3.6 s after the first delivered record, and event-to-visible was p50 2.5–3.4 s and p95 3.3–4.4 s. Three waits dominated. ETW's own flush of partly filled buffers took up to about 2 s at a low event rate; the first chunk waited a whole 2-s publication interval; and the viewer polled once a second. A live capture now asks ETW to deliver partly filled buffers every 250 ms and publishes its first chunk 0.5 s after it holds records (one extra small chunk; later chunks keep the interval), and the viewer polls every 250 ms.

Results after the change: the first overview arrived 0.96–1.3 s after the first delivered record, or 1.5–1.9 s after the start action (the elevated harness shows no approval prompt). That is inside §3.1's 3-s budget with margin. Derivation p95 was 203–359 ms and projection p95 31–113 ms, inside their stage budgets. Event-to-visible was p50 1.3–1.7 s and p95 2.5–3.2 s. Capture impact stays Low: Live against no capture was 0.74 pp, and broker CPU was 531 ms per 14-s window against 470 ms before. Broker qualification passes every scenario.

The steady-state budget of p95 under 1.5 s is not met, and the plan no longer implies that tuning will meet it. Under ADR-027 a viewer sees only published chunks, so a record waits for its chunk to close and then for the viewer to follow and project it. An experiment with a 1-s publication floor still measured p95 1.7–2.6 s, because flush, poll, mirroring, derivation and projection remain on top of any chunk wait, and projection grows with session size. The 2-s floor therefore stays. Meeting 1.5 s needs one of two designs, decided with evidence rather than assumed:

- a non-durable live preview stream from the broker to the viewer, labeled as a preview until its chunk publishes (§19.3 already allows a labeled preview before an exact result);
- or incremental derivation and projection that makes a sub-second cadence affordable (§12.1 S3, S4).

Until one lands, the row above is an open, measured miss, not a met target.

Revision 125 measured what the second design would have to remove, and landed the broker half of the first.

- **What grows with a session.** On synthetic TCP sessions in Release, a new generation's overview took about 0.33 s
  cold at 100,000 rows and 1.0 s at 1,000,000. The same generation warm took 0.1 s and 0.5 s. One plain pass over
  the time and mechanism columns cost only 24 ms per million rows. At 1,000,000 rows the rest was:
  - re-deriving process instances (115 ms) and transport relations (212 ms) for every generation;
  - the per-row relation lookups of the overview's two passes (97 ms and 66 ms);
  - per-column mechanism tallies.
- **What that implies.** S4's persisted timeline pyramid is needed so a 100 GiB session opens in bounded time, but
  alone it removes only the cheap part of a live publication's cost. A sub-second cadence at scale needs process and
  relation derivation that extends the previous generation's rather than repeating it. That is IC-015 work: a late
  record, such as an end-of-capture rundown, can re-identify an instance and so every binding of it.
- **The broker half of the preview.** `GetStatus` now carries a **live preview** (`contracts/broker-v1.md` §5.9):
  every journaled record counted by chunk, mechanism and 100 ms bin, for the chunk being written and the four
  published before it. It is counted on the recorder's writer thread, never in the ETW callback. It holds counts
  only, and its counts plus its unbinned records always equal the records it covers.

Revision 126 draws that preview as the timeline's **live edge** (§6.2) and measures it. `first-feedback` schema v2
times each record to the first preview holding it and to the first overview holding it, and judges this row on
whichever came first. A preview holds exactly the records whose capture-wide journal indexes lie in `[journaled −
counted, journaled)`, so each record's delay is exact, not estimated.

Three 15-second real-ETW runs (`bench/results/first-feedback-20260925T203141Z`) met every budget:

| Measure | Result |
|---|---|
| Event-to-visible | p95 717–766 ms, p99 799–924 ms (p50 560–607 ms) |
| Event-to-exact | p95 2.57–2.59 s, unchanged |
| Previews handed off | about 55 per run |
| Records missing a preview | none |

**The steady-state row is therefore met through the labelled preview, which is what the user sees first.** The exact
path's p95 still exceeds 1.5 s, as the 2-second chunk floor and per-generation derivation require. Revision 125's
measurement stands: exact live cadence at scale needs incremental derivation.

Revision 128 runs the default Explore capture to its own bound: `first-feedback --seconds 600 --bounded` sets the
600-second quota and never stops the capture itself (`bench/results/first-feedback-20260925T205636Z-10min-bounded`).
Schema v3 adds memory samples every 10 s and one-minute trend windows, so growth over a long capture shows rather than
averaging away. The broker ended the capture at its quota and the session was saved whole: 88,800 records, 292
overviews, no drops and no provider or consumer loss.

| Measure | Whole run | Second minute | Last minute |
|---|---|---|---|
| Event-to-visible p95 / p99 | 966 / 1,173 ms | 787 / 921 ms | 1,031 / 1,171 ms |
| Event-to-exact p95 | 2.99 s | 2.73 s | 3.10 s |
| Follow p95 | 250 ms | 139 ms | 202 ms |
| Projection p95 | **264 ms** | 111 ms | **266 ms** |
| Records derived by the minute's end | 88,800 | 16,342 | 88,659 |

Every budget but one was met: projection p95 was 264 ms against 250 ms. Projection grows with the session, about
2.5 ms per thousand records, from p95 53 ms in the first minute. The preview's delay grows with it, because one viewer
loop follows, projects and only then reads the preview. At the end the viewer held 153 MiB private (106 MiB managed),
up from 50 MiB after the first minute. The broker stayed flat at 42–44 MiB private. The session took 58.6 MB on disk,
660 bytes per record with its mirrored journal.

So a quiet machine's default capture reaches the projection budget by its end, and a busier machine would pass it
within minutes. Viewer memory in proportion to the session also contradicts §12.1's S2. Both have one cause: each
publication re-reads, re-verifies and re-derives the whole session. That makes the bounded-session work of §12.1 (S3,
S4) and IC-015's incremental derivation prerequisites of a usable long capture, not only of large ones. The live preview
must also stop waiting behind projection, so its delay stays at the 4 Hz cadence of §19.3 however long projection takes.

The crash half of M2's gate: `run`'s new abandoned-client scenario starts a capture, renews its lease while traffic
flows, then drops the connection without a stop, as a crashed viewer does. The broker stopped the capture 32.1 s later
(the 30-second lease and one maintenance pass), finalized its journal, left no ETW session and idle-exited with code 0
(`bench/results/broker-qualification-20260925T210750Z`). Closing the window takes the ordinary stop path, which every
stopped first-feedback run exercises.

Revision 129 removes those whole-session costs from each publication. They were found by profiling the 10-minute
session (88,800 rows, 2,557 process instances, 292 chunks):

- **Leases.** A lease opened every file its manifest names to confirm what the store instance had already hashed, and a
  live session names every chunk it published: 33 ms per lease by the end, for every query and every publication. One
  directory listing now confirms them (ADR-025, amended), in 1 ms. A file that is missing, or listed with another
  length or time, is still opened and checked.
- **Process derivation** materialized all 97,897 source-field rows to find 19,031 process fields (42 ms). It now reads
  the field code from its column, and builds a record key only when the record could be its PID's earliest.
- **Relation derivation** decoded every row's endpoints in both of its passes, and the projector decoded them twice
  more. The first pass now reads only connect, accept and disconnect rows, and the projector reads each segment's
  bindings once.
- **The runner** follows and projects as one background step at a time, so status, the preview and renewals keep their
  250 ms cadence, and a finished step's overview is handed over at once.

Old and new builds produce identical digests of every instance, relation, per-row binding and overview bundle, on the
10-minute session and on the sparse 534-process session. The same bounded capture then met every budget
(`bench/results/first-feedback-20260925T215018Z-10min-bounded`, 73,660 records):

| Measure | Revision 128 | Revision 129 |
|---|---|---|
| Projection p95 | 264 ms | 85 ms |
| Projection growth | about 2.5 ms per thousand records | about 0.8 ms per thousand records |
| Follow p95 | 250 ms | 72 ms |
| Event-to-visible p95 / p99 | 966 / 1,173 ms | 849 / 978 ms |
| Event-to-exact p95 | 2.99 s | 2.69 s |
| Viewer private memory at the end | 153 MiB | 76 MiB |

The first attempt at this run found a defect the default capture's length had hidden. The broker closed any
connection after 4,096 commands. An owner reading status four times a second reaches that in 17 minutes of a capture
that may last 24 hours, and the Desktop then reported its capture interrupted. `broker-v1` now bounds a connection by
rate: 256 requests at once and 64 a second, a request past that answered late rather than refused. The runner also
reads status on its own cadence rather than again whenever a step finishes.

Projection still grows with the session, so §12.1's S3 and S4 and IC-015's incremental derivation remain the path to
1M- and 10M-row sessions. Each projection also re-read and re-hashed every segment it opened, 21 MB and 17 ms at the
end of this capture, until revision 142 kept verified readers (§20.1). A live session also kept every superseded
manifest, because `store-v1` kept an unreferenced file until asked to remove it. That was 297 manifests and 16 MB after
10 minutes, growing with the square of the chunk count. Revision 144 removes them as it publishes (store-v1 §9). Its
bounded 10-minute capture ended with one manifest in the session and two in the broker's evidence, 68 KB and 121 KB
(`bench/results/first-feedback-20260926T163447Z-10min-bounded`).

Revision 156 measured what remained of a warm projection at 1,000,000 rows, and it was not the timeline. One plain pass
over the time and mechanism columns cost 3 ms. Locating every row in its connection incarnation cost 76 of the
projection's 91 ms, and that lookup served four numbers only:

- the TCP records with no admitted peer;
- the records of the displayed edges;
- how many of those have no session time;
- revision 52's graph-eligible timeline, which nothing drew: each rung's colour comes from its own focused count (§3.2).

The relation index now counts two more things as it derives: each incarnation's records without session time, and
each related mechanism's records with no end (`relations-v1` §5). The overview takes all four numbers from the
relations, and the graph-eligible timeline is retired. Its construction checked at run time that the displayed edges
hold exactly the rows their channels name. That check is now a seeded property test over random captures. A second
test asserts that every count by other end equals a per-record read, under every policy. Against revision 155, every
other overview field, the relations and every per-row binding are digest-identical, under all four evidence policies,
on the sparse ETL session and on a synthetic session with untimed rows. Release, medians of the last 20 of 40 passes:

| Rows | Projection of a new generation | The same generation again |
|---|---|---|
| 200,000 | 40–43 → 28–29 ms | 19–21 → 7 ms |
| 1,000,000 | 208–214 → 142–145 ms | 102–105 → 31 ms |

What a new generation still pays is its derivation: about 110 of the 142 ms at 1,000,000 rows, 93 of them the
relations. That is IC-015's incremental derivation, which is therefore next. S4's persisted tiles follow it; they are
what lets a reopen avoid reading every segment's time column (S1). §10.3 already says how tiles stay exact: whole tiles
are summed, and each cell's two boundary fragments are read. segment-v1 sorts a segment's rows by native reading (§4).
Where session time is monotonic in the native reading, a fragment's records in one segment are therefore one
contiguous run found by binary search, not a scan. The tile design must check that premise, because a quarantined
record has no session time.

Revision 157 makes a live generation's derivation extend the previous generation's (IC-015). The process instances
are built from what the records say taken together:

- the lifecycle records, in canonical order;
- the process fields, keyed by the record they describe;
- the earliest record of each PID.

None of this depends on the order segments are read in. A later generation extends a copy of it and reads only the
segments it adds. Its relations extend the same way, from a copy of every end's incarnations, unless something added
would change a decision already made. The extension then declines, and the generation is derived in full, in three
cases:

- A segment it read is no longer named, after a compaction or a retention.
- An added connect, accept or disconnect falls before or among the records an end already holds. It would move them
  to another incarnation.
- The new process index would bind an earlier record otherwise. A binding changes only where a lifetime starts or
  ends, so both indexes are compared at every such boundary within each holder's readings. A capture-end rundown that
  re-identifies a process binds every record alike, under a new identity, and extends.

Segments are told apart by the file their generation publishes: its name and content digest. A segment's header id is
derived from its ordinal within its generation, so two generations' segments can share it (segment-v1 §3).

Measured in Release on a session gaining 2,000-row chunks:

| Session | Projection of a new generation, before → after |
|---|---|
| 1,000,000 rows | 146–155 → 36–38 ms, about what projecting the same generation again costs |
| 200,000 rows | 38–40 → 11–13 ms |

A full derivation is no slower: 141 → 132 ms at 1,000,000 rows. In some runs, promotion from the first JIT tier kept
being delayed, and the overview's two passes over every row ran at about a quarter of their speed. They are now
compiled optimized at once, as revision 129 did for the per-row helpers.

Exactness is tested three ways:

- A seeded property test extends random captures chunk by chunk and compares every generation with a full derivation,
  instance by instance and binding by binding. The captures include late records, reused and undecided ends, PID
  reuse, and start keys that arrive a chunk after their record. Six mutations of the extension are each caught. A
  seventh, of a refusal no input can reach, is not.
- The real sparse ETL session was published again in 12 and in 40 time-ordered chunks, one record in eight delivered
  late. It extended at every chunk and equalled a full derivation throughout.
- A store-level test runs a live session through the shared cache, including a compaction.

What a new generation now pays is the overview's own pass over every row, about 35 ms at 1,000,000 rows. S4's
persisted tiles are next.

Revision 158 builds the first level of S4's pyramid, in memory. Each segment's timed records are counted into aligned
decimal tiles, `[j·10^k, (j+1)·10^k)` ticks, once per verified reader. A tile keeps its count per mechanism, the row
run its records occupy, and its earliest and latest reading. A count into columns takes a tile whole wherever its
records fall in one column, and reads a tile's rows only where a column boundary falls among them: §10.3's exact
boundary fragments. The premise is checked, not assumed: session time is a monotonic scaling of the native reading
that rows are sorted by. A segment whose session times ever go backwards has no tiles, and its rows are read.

- **Overview and zoomed detail.** The overview takes its extent and its 64 buckets from tiles, and so does zoomed
  detail without a focus. A focused count still reads its rows: its filter is not what tiles hold (§10.3).
- **The minimap.** It is the pyramid's first level, as M2's gate asks. Its columns are aligned multiples of the
  narrowest 1–2–5 width (§6.2) that covers the extent in at most 2,000 columns. A tile is never wider than a tenth of
  such a column, because its width is the narrowest power of ten spanning the segment in at most 20,480 tiles. So
  every column is a union of whole tiles, and the minimap reads no row. The first and last columns are clipped to the
  extent, both where they are drawn and where the capture's coverage is judged.
- **A defect found while testing it.** `TimelineColumns` placed a record that fell exactly on a bucket boundary in
  the bucket before the one whose stated interval holds it. This happened wherever the column count does not divide
  the span. §10.3 defines `b(i) = t0 + floor(span·i/W)`, and the column is now its exact inverse,
  `floor(((t − t0 + 1)·W − 1)/span)`. The real sparse session's buckets did not change. A synthetic session with a
  record at every tick moves one record at each such boundary. Totals are unchanged everywhere.

Measured at 1,000,000 rows, Release:

| Measure | Revision 157 | Revision 158 |
|---|---|---|
| Projection of the same generation again | 32–33 ms | 1 ms |
| Projection of a live generation, 2,000-row chunks | 37–38 ms | 8 ms |
| Projection of a new generation, derived in full | 134–138 ms | 103–105 ms |
| Zoomed detail over a sixth of the extent, 256 columns | 7.2–7.7 ms | 0.9 ms |

A live generation's remaining cost is mostly the uncompacted chunks. Their tiles hold about one record each, and
consecutive tiles reuse the column the tile before them found.

Everything else in the overview, the relations and every binding is digest-identical to revision 157's, on the
sparse ETL session and a synthetic one. A seeded property test compares counts through tiles with counts of rows,
over random intervals and column counts, with bursts, standstills, negative and missing readings. Other tests cover
disordered session times, minimap columns that read no row, and columns that hold what they count. Five mutations of
the tiles are each caught.

**What remains of S4:** tiles are built from the rows once per reader and are not persisted. A reopen still reads every
segment's time column (S1), and a focused count still reads its rows.

Revision 161 removes the cliff the tiles still had past the reader cache. The cache charged each reader its file's
length, so from about 1.5M rows some readers went uncached. Every projection then reopened them and rebuilt their
tiles: a live generation took 7 ms at 1M rows and 64 ms at 3M. The cache now charges what a reader holds, and when a
lease ends over budget, readers give back every column but session time and mechanism until the charge fits (§20.1).
That completes the third step of the column-granular program, admission by what a reader holds.

| Measure | Revision 160 | Revision 161 |
|---|---|---|
| Live generation, 3M rows | 64 ms | 9 ms |
| Readers cached after reopening 4M rows | 6 of 16 | all 16 |

The 4M-row reopen itself is unchanged at about 1.3 s, all of it the first derivation. The budget now bounds bytes the
readers really hold, so a large session uses it. After a full collection the managed heap was 244 MiB, where 110 MiB
had been with six readers cached. Measured on a synthetic session.

Revision 162 takes the first derivation out of reopening a finished session (S1). Measured at 4M rows, that
derivation was 80% of the reopen: 225 ms of process instances and 615 ms of relations, against 99 ms of tiles.

- **The checkpoint.** A finished session publishes a derivation checkpoint (`contracts/derivation-checkpoint-v1.md`):
  the state its process instances and transport relations keep, as derived from named segments. A reopen builds both
  from it and reads no row to do so.
- **Who publishes it.** It is an `Index` that its own generation names (store-v1 §5, `CommitIndex`). The writer that
  finished the session publishes it: an import, a live follow in the Desktop or the command line, a recording, a
  compaction or a re-derivation. `icat checkpoint` publishes one for an older session.
- **Only an index.** One that is missing or stale costs time. One that cannot be read also costs a stated caveat.
  None changes an answer. An additive generation does not carry it, so a damaged checkpoint costs at most a rollback
  to the generation before it (§20.1).
- **A function of its records.** Its bytes are the same whatever order its segments were read in, and whether the
  state was derived at once or extended chunk by chunk. A property test checks both, and that a checkpoint read back
  answers every binding, relation and count as the derivation it was written from.

| Measure | Revision 161 | Revision 162 |
|---|---|---|
| Reopen to the first overview, 4M rows | 1.10 s | 0.48–0.50 s |
| Reopen to the first overview, 10M rows | 2.2–2.4 s | 0.62–0.65 s |
| Working set after that reopen, 10M rows | 787 MiB | 223 MiB |
| Checkpoint, 100 processes and 50 relations | — | 20–23 KiB |

**What still grows with the session:** the store and segment opens, 160 ms at 10M rows over 40 segments, and every
segment's session-time and mechanism columns, read to build the tiles, 400 ms. Persisting the tiles, S4's next step,
removes the second. Opening a segment only when a query needs its rows removes the first.

Revision 163 removes both, and a finished session's reopen no longer grows with it (S1).

- **The pyramid's top level.** A persisted overview (`contracts/overview-index-v1.md`), S4's top level, is published beside
  the derivation checkpoint and holds exactly what the first view draws: the rows and untimed rows, the extent, the 64
  overview buckets' counts per mechanism, and the minimap's columns. Coverage is judged from the ledger when they are
  presented, as for counts made from rows. Revision 170 renamed its contract from `overview-v1`, the name the JSON
  bundle of `icat overview --json` had carried since revision 87: the bundle is what a view shows, the index a file a
  generation publishes, and one name for both sent a reader of the one to the other's contract.
- **No segment opened.** When the checkpoint and the overview both cover exactly the segments the manifest names, the
  projection opens no segment. Deeper levels, zoomed detail and focused counts, open the segments a view reads, as
  before. A generation holding only a checkpoint, as revision 162 published, takes its derivations from it and counts
  its timeline from the segments.
- **No position is guessed.** An overview whose columns this build would place otherwise is not used: other bounds,
  another column count, another minimap span, or counts that do not add up. A tile count cannot move with the extent,
  so an overview covering fewer segments is not extended.

| Measure | Revision 162 | Revision 163 |
|---|---|---|
| Reopen to the first overview, 4M rows | 0.48–0.50 s | 0.17–0.18 s |
| Reopen to the first overview, 10M rows | 0.62–0.65 s | 0.17 s |
| Working set after that reopen, 10M rows | 223 MiB | 37 MiB |
| Segments opened before the first view | every one | none |

About 80 ms of what remains is the store's open, and about 90 ms the first projection, most of it compiling code
that a running Desktop has already compiled. Measured on synthetic sessions of 100 processes and 50 relations. On the
real sparse ETL session the overview is identical to a derivation from every segment under every evidence policy. Next,
the Desktop and first-feedback paths are re-measured at scale: §6.8's latency windows, and a 10-minute capture's
publication of its checkpoint on real ETW.

Revision 169 brings every gate of that table within its budget at 10M observations, on the reference machine's 16
threads (`DOTNET_PROCESSOR_COUNT=16` on a 24-thread machine). A count that reads rows counts its segments side by
side, each worker into tallies of its own, summed after. Each row's owner and channel binding is kept with the
segment's reader, packed into four bytes each and charged to the reader cache, so a later query reads it rather than
binding the row again from up to thirteen columns (§20.1). An evidence page opens a segment only once its earliest
reading could come next, so a first page reads the first chunk's keys alone. Binding an owner reads a lifecycle
record's five identity columns only in a segment that holds one, and binding a channel reads a row's position only
for an end a cut divides. Warm p95 at 10M, against revision 168:

| Gate | Budget | Revision 168 | Revision 169 |
|---|---|---|---|
| Brushed ranking | 250 ms | 1,226 ms | 197 ms |
| A group's 40 lanes, at 256 columns | 100 ms | 223 ms | 20 ms |
| That group at 2,000 columns | 100 ms | 238 ms | 13 ms |
| First evidence page, process / channel | 150 ms | 144 / 193 ms | 35 / 32 ms |
| Working set at the end | — | 2.3 GB | 1.2 GB |

The retained heap is 284 MiB, 252 of it the reader cache with its bindings. What remains of a brush's p95 is the first
touch of a segment, which derives its bindings. Two things wait on later work: §12's 2,000 × 40 lane query is still
refused by the 20,000-cell lane bound, which the window meets only once §6.2's density regime draws a column per
device pixel; and at 100M observations, the scale tier, faster scans are not enough, and the pyramid must hold what a
brush and a group count.

Revision 168 measured the query gates of the table above at 1M and 10M observations, over finished synthetic sessions
of 400 processes in 10 executables, in Release (`bench/results/scale-gates-*`, the opt-in `ScaleGateTests`). Warm p95:

| Gate | Budget | 1M | 10M |
|---|---|---|---|
| Reopen until usable (store open and first projection) | 3 s | 172 ms | 8 ms, no segment opened |
| Timeline, 2,000 columns at L0 | 100 ms | 8 ms | 5 ms |
| Timeline at a 40-process group, 40 lanes at the window's 256 columns | 100 ms | 102 ms | **223 ms** |
| Timeline at that group, 2,000 columns | 100 ms | 27 ms | **238 ms** |
| Bounded graph and top-100 ranking, whole session | 250 ms | 3 ms | 1 ms |
| The same within a brushed interval | 250 ms | **272 ms** | **1,226 ms** |
| First evidence page, process | 150 ms | 27 ms | 144 ms |
| First evidence page, channel | 150 ms | 30 ms | **193 ms** |

What reads only the overview or the tiles meets its gate at 10M with room. What counts rows by owner or channel does
not: a brushed ranking counts every record in the interval by process, edge and channel, and a group's lanes resolve
each record's owner. Both are what the tiles of revision 158 could answer whole, if a tile also held its records per
owner instance and per channel; only a tile a boundary crosses would then read its rows (§10.3). Two further findings:
§12's 2,000 × 40 lane query is refused by the 20,000-cell lane bound, which the window never meets because it asks for
at most 256 columns, so the bound and the gate need reconciling; and at 10M rows the retained heap is 290 MiB, 253 MiB
of it the reader cache at its budget, within the 512 MiB analysis budget, but the working set peaks near 2.3 GB from
transient allocation.

Revision 166 settles the first of revision 165's two larger findings, stated below. The ranked table's groups and processes rank by each process's own
records: every record whose owner binds to the instance and that the evidence policy admits, per mechanism
(`process-activity-v1`, `contracts/entities-v1.md` §4a). Every record is counted once, by its instance or as one no
process holds, so a group's total is its members' and the machine's its groups'. The counts are made with the
relations, extended from one live generation to the next when every PID's counted readings bind alike, and kept in
the derivation checkpoint, whose format 1.1 adds them, so a finished session ranks from persisted counts (S3). A
format-1.0 checkpoint still opens, and its counts are made from the segments until a writer replaces it. A brush ranks
by the same counts inside the interval. L1's process lanes now sit in the table's order, search takes a name that is
the query plus an extension as exact and orders its hits by these counts, and the rung summary says what it counts and
how many rows no process holds. On the real sparse ETL session 851 of 852 rows belong to a process; its republished
checkpoint gives the same overview. The paired-TCP graph is unchanged: it draws relationships, which the ranked table
now no longer stands in for.

Revision 165 tested the whole product live: every CLI command on real ETW and a real ETL, the broker qualification
suite, and the real Desktop driven through UI Automation, including a live capture saved with its checkpoint. It fixed
what it found:

- CLI text on legacy-code-page consoles, and redirected output not in UTF-8;
- a checkpoint's lease that kept superseded manifests;
- process lanes that judged every quiet interval unknown, where §10.3's roll-up judges a cell by every mechanism that
  can contribute to it, which for a process is every mechanism the capture collects;
- a live capture's status that counted every interval as a limit before its ledger existed;
- a legend in arrival order.

Two findings are larger. L0 and L1 rank by paired-TCP records only, so a real capture, whose traffic mostly leaves the
machine, ranks zeros: S3 wants ranking from persisted summaries, and per-instance record counts belong with the
derivation checkpoint. And process lifecycle wore the unknown grey (§6.6, table above).

Revision 164 measured both. A bounded 10-minute Explore capture on real ETW met every §12 budget, with event-to-visible
p95 796 ms over 128,467 records. It ended by publishing a 1.2 MiB checkpoint and a 12 KiB overview, and a fresh store
projected the saved session in 8 ms without opening a segment
(`bench/results/first-feedback-20260926T232454Z-10min-bounded`). In the window, a synthetic 1M-row session opens from
its checkpoint and overview in 2 ms of projection and 27 ms to laid out, where deriving it takes 539 ms. Level changes,
the brush and search stay within §6.8's second (`bench/results/interaction-latency-20260926T233525Z`).

Derived requirement from these budgets and the memory table: at 100,000 observations per second and 128 bytes per normalized observation, a 256 MiB acquisition budget holds roughly 20 seconds of full-rate data. The pipeline must therefore survive a 20-second analysis stall without application drops, report lag before half that budget is consumed, and turn the remainder into explicit counted loss rather than an unbounded queue (R8).

A per-record cost is measured as a slope, not as a total divided by a count: a fixed setup cost divided by a record count looks like a per-record cost and is not one. Where two components share a thread, attributing a cost to one of them needs the same work done twice with only that component removed; a thread total cannot say which of them spent it, and reporting another component's cost as InterCat's is the same error as reporting an unreadable counter as zero (ADR-009).

Per-stage processor time is measured on the thread that does the work, not on the process, so a callback's cost is attributable to the callback. The Windows thread clock advances in ticks of roughly 15.6 ms, so a stage that consumes less than one tick reports "below one clock tick" rather than zero, and a stage measured on a host without a per-thread clock reports the quantity as unmeasured. A latency distribution is published as bucket bounds and an exact total, never as an interpolated percentile the histogram cannot support (R3).

Common-query budgets apply to documented indexed predicates and visible row budgets. Arbitrary regex, full-content search, or novel graph expansion may take longer; they must remain cancellable, progressive and honest about exactness. Measure cold and warm caches separately and publish hardware, Windows build, source schema versions, workload seed and distributions.

At 100,000 observations/second, 128 bytes per normalized observation alone is about 12.8 MB/s or 46 GB/hour, before raw evidence and indices. Use actual measured sizes to estimate retention in the UI. Large recordings require disk planning and bounded retention, not a claim that memory mapping makes storage cost disappear.

The journal is part of that arithmetic, not a rounding error beside it. `journal-v1` measured **231 bytes per admitted record** across every level of the 2026-09-21 series, because the format deliberately carries no dictionary: a provider, activity, related-activity and clock identifier are sixty-four bytes on every record, and §20.1's dictionaries belong to the derived store rather than to the append-only journal. A session that keeps its journal for the life of the session therefore costs roughly 359 bytes per observation, not 128, and reaches T2's 20 GiB in about fifteen minutes at the §12 ingest target. Retention arithmetic and the UI's remaining-time estimate must count the journal, and §20.1 must state how long a journal batch outlives the segment published from it. Until it does, no session-duration claim follows from the normalized figure alone.

### 12.1 Session size tiers and scale invariants

Sessions are expected to reach tens of gigabytes: by the arithmetic above, one hour of sustained capture at 100,000 observations per second is roughly 46 GB of normalized data before raw evidence and indices. The tiers are qualification targets; the invariants below them are contracts that hold at every tier.

| Tier | On-disk session size | What must remain true |
|---|---|---|
| T1 interactive | up to 2 GiB | Every §12 budget, cold and warm |
| T2 standard | up to 20 GiB | Every §12 budget; compaction stays inside §20.1's segment targets |
| T3 large | up to 100 GiB | Interactive budgets hold for indexed predicates; full-content search and novel graph expansion may be progressive and must say so |
| T4 qualification | beyond 100 GiB, including 24-hour rolling capture | Bounded memory, honest coverage, recoverable reopen and declared quotas; measured in M13 |

- **S1 Time-to-interactive is independent of session size.** Reopen reads the manifest, the top level of the overview pyramid, and only the index heads it needs. It never scans observation data in proportion to session bytes. The 3-second reopen budget is measured at T1 and again at T3, and the two must not differ by more than TUNABLE: 2×.
- **S2 Resident memory is independent of session size.** UI, layout and cache footprints are bounded by the viewport, the row budgets and the declared cache budgets — never by observation count. A T3 session and a T1 session show the same steady-state working set within those budgets.
- **S3 No interactive path scans the whole session.** Overview, minimap, ranking and facet results are served from persisted summaries or bounded index reads. Anything that cannot be answered that way is explicitly progressive, cancellable, and never blocks input (P25).
- **S4 The whole-session overview is persisted, not recomputed.** Maintain a durable multiresolution overview pyramid for the common lane and metric views, updated incrementally at commit, so the minimap and the L0 view of a 100 GiB session open in bounded time. Its top level is small enough to load eagerly; deeper levels load by viewport. Rebuilding it is a background repair, never a precondition for opening a session.
- **S5 Growth is disclosed before it hurts.** The UI states current session size, measured bytes per observation, and time remaining under the active retention policy and free disk, from measured sizes rather than estimates. Crossing a tier is visible, and so is the point at which retention will begin evicting.
- **S6 Retention never breaks a reference.** Rolling eviction publishes §20.2's boundary checkpoint and cannot remove leased, pinned or open evidence (I18).
- **S7 Degradation is stated, never silent.** When a scope is too large for an exact answer inside the interaction budget, show the coarse answer labeled coarse, refine in the background, and never let a preview leave the product as an exact result (P26, §19.3).

These invariants are what make §3.1's promise survive a long session: the view a user gets three seconds after launch must be the same view they get three seconds after reopening a 100 GiB capture.

## 13. Verification strategy

### 13.1 Controlled workload suite

Create small cooperating executables with independent truth logs containing scenario IDs, process creation identities, monotonic times, call IDs, declared sizes, successful counts, failures and lifecycle events. The truth log is an oracle for the test, not data silently supplied to the production ETW correlator. Separate workload truth from intentionally supported cooperative instrumentation.

Required scenarios:

1. TCP IPv4/IPv6 loopback and two-host connections, reconnect/port reuse, partial sends, retransmissions under controlled impairment, multiple concurrent clients, and a flow already open at capture start.
2. UDP one-way/reply, endpoint reuse, multicast and absent receivers; do not expect every sender observation to have a receiver match.
3. Named pipe byte/message modes, duplex, multiple instances sharing one name, overlapped I/O, cancellation, partial completion, denied access and disconnect.
4. Anonymous pipes with inherited and duplicated handles, including a non-parent recipient where the workload deliberately transfers a handle.
5. RPC over local and network transports, concurrent/nested/async calls, errors, cancellation and long-running calls spanning the capture boundary.
6. ALPC activity generated through supported local RPC paths; a separate version-gated native ALPC fixture may be used in the lab. Assert only the events/identities the source contract supports.
7. Shared memory with two and three participants, named/unnamed sections where feasible, duplicated handles, unmap/remap and repeated names; many memory writes must not fabricate ETW byte traffic.
8. Protected/inaccessible processes, service-host groups, multiple interactive sessions and container boundaries where supported.
9. Content with binary zeros, invalid encoding, truncated fragments, very large lengths, encrypted data and imported content without opt-in preview.
10. Two-machine offset/drift/clock-step scenarios, NAT/proxy ambiguity, duplicate capture imports, missing host capture and manual alignment revisions.

### 13.2 Correctness properties

| Property | Required assertion |
|---|---|
| Conservation | Sum of timeline point counts equals the exact eligible evidence query for the same scope/snapshot (I4, I5) |
| Weighted conservation | Byte sum equals compatible known measurements; unknown values and requested/completed domains stay distinct (R3, I6) |
| Layer accounting | Adding an RPC annotation to ALPC evidence does not increase transport bytes; endpoint and canonical transfer totals have their documented differences (I11, §5.3) |
| Identity | PID/TID/object/port reuse never merges independently proven instances (R22, I12) |
| Reproducibility | Same raw source, saved schemas, versions and settings yield identical normalized IDs and deterministic analysis, independent of worker count (I2, I14) |
| Late evidence | New relation revisions update current views without mutating old snapshots or changing source IDs (I1, I17) |
| Boundaries | Half-open buckets conserve records at edges, equal timestamps and final observation; overflow is checked (I3, I4, §10.3) |
| Intervals | Start counts, overlap counts and clipped occupancy obey their distinct definitions, including open operations (I20) |
| Navigation | Pointer/pinch focus remains within representable tolerance; inverse zoom/pan restores position unless clamped; overscroll stays bounded within §6.2's allowance (R10, R13, §6.7) |
| Async publication | Delayed queries cannot mix old graph/ranking with a new timeline/filter/alignment (R6, R7, I15, I16) |
| Clock safety | Unknown synchronization never yields an exact cross-host causal order or one-way latency; local durations are unchanged by alignment (I9, I10, §8.2) |
| Content safety | Disabled content collection/preview behaves as specified; redacted exports contain no original payload references or unredacted embedded source (R17, I21, I22) |
| Coverage | Missing providers and deliberate drops produce visible quality states, not zero-traffic assertions (R21, I13) |
| Encoding | Every meaning in §6.6 keeps a redundant channel; contrast and perceptual-separation targets hold in all theme modes and under three color-vision simulations (R14) |
| Layout determinism | The same graph identity, seed, constraints and iteration cap produce identical positions independent of thread count and wall-clock time (§19.4) |
| Specification identity | Equal specifications hash equally and unequal ones differ, in the UI and the CLI alike, across the golden corpus (R18, §10.5) |

Use scan-based reference implementations for small randomized traces to validate optimized indices and tiles. Property tests should stress adversarial identities and intervals, not mirror production algorithms.

### 13.3 Capture and failure tests

Test cancellation during startup, provider-enable failure, broker crash, UI crash, user logoff, disk full, slow disk, queue overflow, CPU contention, source event loss, malformed schema, missing manifests on another machine, corrupted segments and unsupported future format versions. Restart must recover the last durable generation and leave no unowned ongoing ETW sessions.

Inject deliberate loss separately at provider/ETW reporting boundaries, callback queues, decode and storage. Verify that the health ledger identifies the correct layer and that incomplete operations/relationships remain qualified. A trace with zero reported loss is still not proof of universal coverage.

Verify coexistence with WPR/PerfView and existing monitoring software. Test standard-user viewing, UAC cancellation, restricted users, local pipe ACL rejection and broker command validation. Archive tests include traversal, reparse points, decompression bombs, huge dictionaries, oversized event fields and content decoder timeouts.

### 13.4 Compatibility and independent checks

Maintain fixture captures per explicitly supported Windows build, architecture and relevant feature configuration. On Windows updates rerun source-contract tests before expanding claims. An unknown schema can be preserved as opaque evidence only when the selected admission/original-evidence policy permits its body; otherwise retain approved diagnostics and an omission reason. It must not be decoded using a guessed old layout.

Compare selected ETW results with WPA/PerfView where the same source events are available and compare capture semantics against the workload truth. Another tool displaying the same provider is a useful cross-check, not an independent proof of complete observation. Maintain synthetic shareable captures free of machine/user data for demos and regression artifacts.

### 13.5 Fixture identity and traceability

Fixtures are named `FX-<mechanism>-<nnn>`, where `<mechanism>` is a code from `EN-Mechanism` (§23) and `PLT` covers platform, clock and multi-host scenarios: `FX-TCP-001`, `FX-PIPE-014`, `FX-ALPC-003`, `FX-PLT-007`. A number is permanent; a superseded fixture is retired, never renumbered or reused.

Each fixture declares, in machine-readable form beside its data:

| Field | Content |
|---|---|
| `id`, `title`, `scenario` | Identity and the §13.1 scenario family it belongs to |
| `covers` | The `R<n>` rules, `I<n>` invariants and §21.1 scenarios it asserts |
| `truth` | The independent truth log or expected result, and how it was produced |
| `expected` | The expected query bundle as canonical bytes plus its hash (§10.5) |
| `environment` | Builds, architectures and profiles on which it has passed, with dates |
| `provenance` | Tool and adapter versions, workload seed, and whether the artifact is shareable |

`fixtures/index.json` is the traceability matrix: fixture to rules and invariants to tests to milestones. Two derived checks run in CI: every rule and invariant is named by at least one passing fixture or test, and every milestone gate names only fixtures that exist. A rule nobody asserts is reported as an uncovered contract rather than silently trusted.

### 13.6 Determinism, golden files and test policy

- Property tests are required for navigation math, bucket boundaries, identity, interval accounting and layout determinism. They generate adversarial identities and intervals rather than mirroring the production algorithm.
- Every optimized index, tile or aggregate has a straightforward scan-based reference implementation, and randomized small traces compare the two exactly.
- Every determinism claim is tested at worker counts 1, 2 and N and across repeat runs, with the seed printed in any failure.
- Golden files are regenerated only by an explicit tool command that prints a diff summary; a regenerated golden is reviewed as a change to the contract, never as test maintenance.
- Fixtures containing machine or user data are never committed. The generator and its seed are committed instead (§13.5).
- Every test names the rules, invariants or prohibitions it asserts. That naming is what makes §13.5's coverage check meaningful rather than decorative.

## 14. Implementation milestones and release gates

Complete each milestone as a demoable slice with code, fixture evidence, updated capability documentation and explicit limitations. Delivery estimates should be made after M0; unknown capture behavior is the dominant schedule risk. Separate a useful preview from the full first-release contract.

### M0 — Feasibility and product proof

**Implement:** repository skeleton; capability probe; owned ETW capture/stop utility; minimal raw decoder; process/network/RPC/ALPC and pipe/shared-memory fixtures; source schema inventory; overhead counters; a synthetic two-pane graph/timeline interaction prototype. Select and pin supported toolchain versions. Validate the selected journal strategy against ETL before freezing session-format details; any change follows the identity/privacy ADR gate.

**Deliver:** build-by-mechanism capability report, synthetic ETLs/truth logs, field semantics, a stack/collector ADR, and UI interaction review. Validate named-pipe NPFS activity and byte completion semantics specifically. Validate whether useful section membership can be discovered without intrusive enumeration. Test RPC content event semantics in a dedicated opt-in lab capture.

**Exit gate:** a measured end-to-end network/process vertical path, and for every proposed initial adapter an explicit tier assignment computed from §14.2's promotion thresholds rather than argued in prose. §6.7's pointer invariant, inverse and pinch-stability properties and linked graph selection pass as property tests, not as a demonstration video. The §17 prototype questions are run as a scored usability review of the two-pane prototype, with the score and the observed failures recorded. No mechanism is promoted from “candidate” solely because a provider name exists.

**Fallback:** if pipes or shared-memory discovery cannot be made reliable driverlessly, assign the measured tier from §14.2, retain explicit coverage entries and partial resource evidence, and route the gap to M7 or M9. Do not substitute parent-child guesses or mapping sizes. A preview may ship with qualified coverage; a claim of broad pipe or shared-memory visualization stays gated on a `Traffic visualization` tier result or an ADR-recorded product scope revision.

### M1 — Evidence and persistence foundation

**Implement:** stable IDs and lifecycle epochs; nullable typed metrics; immutable columns; raw/schema retention; manifests/checksums; partial recovery; query snapshots; basic CLI import/verify/query; synthetic adapters; content fragment schema with bounded inspection backend.

**Exit gate:** deterministic offline import/reopen; source-to-observation traceability; PID/handle reuse tests; corruption/partial recovery tests; a reference query agreeing with indexed counts and bytes. Freeze format v0 with migration/refusal policy, not an accidental permanent schema.

Required closure artifacts and exact scenarios are in section 21; IC-011 through IC-018 turn them into implementation work. M1 closes only when the foundation contracts and their applicable fixtures pass, not merely when a sample file opens.

### M2 — First live exploration slice

**Implement:** ordinary-integrity viewer and elevated broker; Explore profile for validated process/network capture; the §3.1 first-run flow with its designed empty, starting and permission states; the §3.2 L0–L5 ladder with breadcrumb and reversible navigation; equal graph/timeline panes; minimap backed by the first level of §12.1's overview pyramid; process/channel grouping; filters; directional metrics; sorting; exact evidence inspector; record/view pause distinction; health strip; stop/reopen/export.

**Exit gate:** a first-time user reaches a live L0 overview by pressing one action, with no configuration and no prior state, inside §12's first-feedback budget; then descends L0 to L5 and back with one gesture per rung, without losing position. Find a known loopback client/server, brush its burst, rank it, inspect its evidence and reopen the same result. Complete a 10-minute bounded live session; close or crash the UI without leaking an unmanaged trace. All panes agree on scope and counts, and §6.8's latency windows are measured rather than asserted. This is the earliest usable preview.

### M3 — Windows IPC breadth and content

**Implement:** validated RPC/ALPC adapters and correlators; pipe adapter at proven coverage; shared-section resource topology at proven coverage; application/transport projections; unresolved-resource UX; optional timing profile; content opt-in flow, raw/hex/text viewer and at least one validated content-capable source or import path.

**Exit gate:** demonstrate RPC layered over transport without duplicate volume, pipe instance ambiguity without invented peers, shared-resource membership without invented traffic, and content truncation/encryption states. Publish per-build coverage. An unavailable pipe/section feature remains explicit and cannot be called implemented.

### M4 — Multi-machine investigation

**Implement:** workspace manifest; host/boot identity mapping; clock calibration/alignment UI; merged time navigation; source snapshot vectors; candidate connection matching; confidence/evidence explanations; duplicate import detection; portable workspace packaging and relinking.

**Exit gate:** inspect a known two-host exchange from separately captured traces, expose injected clock uncertainty, reject unjustified causal ordering, and persist/reopen manual alignment without changing raw timestamps. Multi-machine analysis is required for full v1, even if previews were local-only.

**Status (revision 281's exit review):** everything above is implemented, and three of the four exit checks hold, each by a test named in `docs/IMPLEMENTATION-STATUS.md`. The known two-host exchange waits on a second machine: one host cannot split an exchange, so correlation is tested only synthetically until then, and the status file says how to run the check.

### M5 — Scale, reliability and release

**Implement:** adaptive indices/tiles, the complete §12.1 overview pyramid, bounded graph layout, cache/queue budgets, rolling retention with pin semantics, accessibility polish, signed installer/binaries, update compatibility and support diagnostics. All three of §11.3's sharing presets exist since revision 154. Keep payloads and raw sensitive events out of automatic diagnostic bundles.

**Exit gate:** correctness suite, declared performance tier, compatibility matrix, crash/disk/loss tests, accessible keyboard workflow, content-export review, installation/uninstallation and capture cleanup checks. Scale tiers T1 through T3 measured with invariants S1 through S7 holding, including the T3-versus-T1 reopen ratio and a steady-state working-set comparison. Ship a reproducible demo investigation showing multiple transports and two hosts, with explicit unsupported rows.

### Dependency order

```mermaid
flowchart LR
  M0[Feasibility M0] --> M1[Evidence M1]
  M1 --> M2[Live workspace M2]
  M2 --> M3[IPC breadth and content M3]
  M1 --> M4[Multi-machine M4]
  M3 --> M5[Release qualification M5]
  M4 --> M5
```

Clock/host identities and source capability contracts begin in M1 even though the multi-host UI arrives in M4. Payload storage policy begins in M0/M1 even if richer adapters arrive later. Do not defer these schema decisions until after a local-only store has shipped.

### 14.1 First implementation backlog

Every item names the milestone that owns it and the items it depends on, so the backlog can be scheduled without re-reading the milestone prose. Rules and invariants in the last column are the contracts the acceptance artifact must assert (§13.5).

| ID | Milestone | Depends on | Concrete work item | Acceptance artifact | Asserts |
|---|---|---|---|---|---|
| IC-001 | M0 | — | Initialize new solution and inward dependency boundaries | Clean build, minimal test/CLI harness, architecture fitness check | R19 |
| IC-002 | M0 | IC-001 | Implement capability/schema inventory without starting capture | Machine-readable report with provider/event versions and unavailable reasons | R5, R21 |
| IC-003 | M0 | IC-001 | Implement owned ETW session lifecycle and health counters | Start/stop/crash cleanup tests; no interference with another session | R8, R9 |
| IC-004 | M0 | IC-001 | Build seeded two-process TCP and local RPC truth workloads | Shareable fixtures with independent expected results | I14 |
| IC-005 | M0 | IC-003, IC-004 | Spike named/anonymous pipes and section discovery | Tested matrix of events, names, instance/peer attribution, bytes and gaps; tier assignment | R22, I12 |
| IC-006 | M0 | IC-003, IC-004 | Spike ALPC and RPC pairing/content semantics | Ambiguity cases and proven metadata/content boundaries | R4, R17 |
| IC-007 | M0 | IC-004 | Define live process/resource/observation IDs and local clock contract | Reuse, late-start, cross-host collision and timestamp quarantine tests; canonical ETL identity remains IC-013 | I8, I9, I12 |
| IC-008 | M0 | IC-001 | Prototype equal graph/timeline layout, pure transforms, and the §3.2 ladder over synthetic data | §6.7 property tests, §6.6 palette contrast tests, ladder reversibility tests, scored interaction review | R10, R13, R14 |
| IC-009 | M0 | IC-003 | Build a disposable owned-envelope candidate and validate authoritative journal/content admission against diagnostic ETL | ADR-008 and same-seed benchmarks; real callback extended-data replay, unknown-schema policy, stage-overhead and saturation tests | R9, R17, I13 |
| IC-010 | M0 | IC-004 | Establish benchmarks, reference machine and Windows support candidates | Reproducible baseline and explicit release-build validation backlog | §12 budgets |
| IC-019 | M1 | IC-009 | Evaluate replacing the managed ETW adapter's real-time path with a native consumer, on the measured allocation gap of ADR-009 | Paired real-time allocation and latency measurements for both adapters against the same fixture, and an ADR that decides | §18.3, R9, R11 |
| IC-010a | M0 | IC-010 | Measure capture impact against a no-capture baseline so `OverheadClass` is computed rather than reported unmeasured | Paired runs with and without a capture, and the resulting class per source | §4.3, §12 capture impact |
| IC-011 | M1 | IC-009 | Freeze the accepted journal-v1 contract and implement production owned envelopes/schema persistence | Golden framing plus extended-data round-trip, buffer ownership and callback-lifetime tests | R9, I1 |
| IC-012 | M1 | IC-009, IC-011 | Compile capture profiles and enforce body admission | Unknown-schema omission, content scope and original-evidence policy fixtures | R17, I13, I21 |
| IC-013 | M1 | IC-007, IC-011 | Implement canonical ETL import and multi-fact identity | Equal-time/multiplicity replay and normalizer-revision tests | I2, I7, I14 |
| IC-014 | M1 | IC-003 | Implement broker protocol, leases and idempotency | Duplicate start/stop, disconnect, permission and crash fixtures | R16 |
| IC-015a | M1 | IC-011, IC-016 | Implement the physical derived segment format of §20.1 and publish the first derived generation | Frozen `contracts/segment-v1.md`, column/null-bitmap/dictionary/locator/time-block and refusal tests, and a generation an import can be reopened from | R1, R2, R3, I6, I11, I15 |
| IC-015 | M1 | IC-007, IC-015a | Implement metric contributions and query basis semantics | Exact accounting/filter scenarios from §21.1 and the §5.3 matrix | R2, R3, I6, I11 |
| IC-016 | M1 | IC-011 | Implement durable commit/recovery, evidence leases and the retention a generation publishes | Crash-at-each-commit-step and reopen-after-retention tests | I15, I18 |
| IC-016a | M1 | IC-015, IC-016 | Publish §20.2's entity-state checkpoint at a retention boundary and censor operations open across it | Reopen-after-eviction tests with still-live identities, continuity quality and left-censored operations | I20, S6 |
| IC-017 | M2 | IC-015, IC-015a | Implement coherent query scheduling and render separation | Supersession, cancellation, stale hit-test and graph-layout identity tests | R6, R7, R12, R13 |
| IC-018 | M2 | IC-015, IC-017 | Bind UI/CLI to common specifications and freeze contracts. The canonical form itself is an M1 closure artifact (§21.2) and is frozen for the metric members in `contracts/query-identity-v1.md`; IC-018 extends it to the UI's projection, viewport and cursors | Matching query output, canonical-form golden corpus, cursor validation and §21.2 artifacts | R18, I16 |

### 14.2 Definition of first-release completeness

Full v1 requires the applicable confirmed product decisions in section 1, not merely a socket graph. It must provide zero-configuration first run to a live overview (§3.1), the complete L0–L5 detail ladder with reversible navigation (§3.2), local capture, durable offline sessions that hold the scale invariants of §12.1 at tier T3, equal graph/timeline exploration, accurate typed ranking, explicit pipe/RPC/ALPC/section capability results, opt-in available-content inspection, multi-machine import/alignment/correlation, accessible navigation, and visible loss/uncertainty. Post-v1 priorities remain obligations of the later milestones.

“Capability result” alone does not satisfy a promise of active visualization for that mechanism.

**Coverage tiers and promotion thresholds.** A mechanism's tier is computed from its fixture results on every supported build, not argued:

| Tier | Entry criteria, measured against the truth workload |
|---|---|
| Traffic visualization | At least 95% of truth operations produce an admitted observation; at least 90% bind to a specific resource instance; at most 1% false peer attributions; a byte measurement in a named domain on at least 90% of eligible operations |
| Topology only | At least 95% of truth resources discovered with their lifetimes, and memberships or endpoints resolved, with no byte or operation claim made |
| Experimental evidence | Reproducible observations on at least one supported build, below the thresholds above, with the gap stated field by field |
| Unsupported | Anything less, including a provider that registers and enables but emits nothing useful |

Release notes and any marketing material list every mechanism with its tier and the build it was measured on. If driverless feasibility leaves named pipes or shared-memory topology below `Topology only`, that is an explicit release-scope decision recorded in an ADR, never a silent restatement of the original goal as achieved.

## 15. Full post-v1 implementation milestones

Post-v1 work is part of the planned product program. These are bounded implementation milestones, not an undifferentiated wish list. Optional collectors remain optional to install and use even if their development milestone is completed. M6–M13 identifiers describe work packages; their dependency graph determines execution order. The confirmed priority is broader IPC coverage and deeper visibility, with the cooperative SDK delivered before selected-process attachment. Reassess detailed scheduling after each release against actual coverage gaps and user investigations.

Every milestone inherits the v1 invariants: immutable source evidence, traceable relationships, typed measurements, bounded work, explicit coverage and content consent. A feature is not complete merely because its collector emits records; it needs storage, query, timeline, graph, inspector, export, tests and documentation.

### M6 — Broader driverless Windows discovery

**Goal:** expand useful whole-system topology without adding installation burden.

**Prerequisites:** M0 capability findings and M5's tested capture architecture. Build on M3 rather than replacing its adapters.

**Implementation:** create independent capability work packages for AF_UNIX, richer COM/DCOM/WinRT annotations, named synchronization objects, mailslots, UI messaging and improved section discovery. For each, first identify a supported passive source or an explicitly experimental source contract; build a truth fixture; then implement only proven metadata/activity fields. Enrich process nodes with package, service and user-session information as accessible. Add namespace-aware endpoint browsing and resource lifetimes. Expose discovered-but-idle resources separately from observed traffic.

**Deliverables:** versioned adapters; per-build coverage extensions; graph/resource inspectors; searchable capability catalog explaining unsupported mechanisms; synthetic investigations for each promoted capability. Keep speculative names and handle-derived candidates visually distinct from established identity.

**Exit tests:** two independent instance/lifetime fixtures per newly supported mechanism, one ambiguity case and one permission/schema failure case; exact source drill-down; baseline overhead comparison. A COM activation event cannot become a call count, and an AF_UNIX endpoint cannot be reported as fully observed if only its name was discovered.

**Fallback:** a mechanism without reliable passive evidence stays explicitly unsupported and is routed to M7 or M10. No UI hook or broad process instrumentation is silently introduced into the driverless Explore profile.

### M7 — Cooperative instrumentation and shared-memory semantics

**Goal:** obtain precise application meaning, especially where memory access and higher-level message boundaries are invisible to OS tracing.

**Prerequisites:** M1 identity/content contracts and M3 layered graph model. Can proceed alongside M6.

**Implementation:** publish a small versioned SDK schema with host/process instance binding; channel and peer identities; operation/message IDs; parent/related operation IDs; explicit source clock; started/committed/consumed/completed transitions; application lengths; status; and optional bounded content. Provide native C/C++ and .NET reference libraries and samples. Use ETW/EventSource or a validated local transport behind the SDK; measure event size and rate limits rather than assuming arbitrarily large ETW payloads.

For a shared-memory ring buffer, instrument logical publish/consume boundaries, not every CPU load/store. Include channel epoch, sequence, slot reuse and fan-out reader identity. Distinguish logical payload size, bytes copied, and number of consumers; one write consumed by three readers is not automatically three physical writes. Define batching and loss behavior. App-provided peer/call IDs are assertions whose authenticity is limited to the source trust boundary.

**Deliverables:** documented SDK/version negotiation; sample pipe, socket and shared-memory applications; source adapters; a shared-resource view with measured logical messages; an overhead guide; explicit content opt-in integration. Support size/count metadata without requiring content capture.

**Exit tests:** producer/consumer and multi-reader shared-memory fixtures match independent truth counts; wrapping sequence IDs and process restart create new epochs; missing consumption remains unknown/open; uninstrumented memory access remains unmeasured; SDK-disabled overhead and enabled throughput are published. Old viewers retain unknown new event fields safely.

**Fallback:** if a high-rate path exceeds the chosen event transport, offer bounded batching or metadata-only instrumentation with explicit semantics. Never silently estimate individual message counts from sampled events. Existing applications remain usable without the SDK.

### M8 — Conversation reconstruction and payload decoders

**Goal:** turn available fragments into navigable conversations while preserving gaps and source boundaries.

**Prerequisites:** M3 content viewer plus at least one source with validated direction, byte-range/message identity and length semantics; M7 is the preferred application-level source but not mandatory for imported evidence.

**Implementation:** define a decoder contract with input content classification, protocol/version, framing rules, resource budgets and output provenance. Add mechanism-specific reconstruction state machines for supported application framing, pipe messages, RPC fragments or packet streams only when their source supplies enough ordering information. Ordinary kernel TCP event sizes alone cannot reconstruct a TCP byte stream. Add explicit missing-range, duplicate, out-of-order, retransmitted and truncated states. Isolate decoders in unprivileged workers and index decoded metadata separately from original content.

Choose the first real decoder based on validated fixtures and user value, not name recognition. A custom length-prefixed sample protocol proves the contract; an HTTP, RPC or other production decoder requires its own complete specification and tests. RPC interface UUID/operation number does not reveal an argument schema; symbolic method/field names require matching definitions or a qualified lookup source. TLS, authenticated RPC, SMB encryption and QUIC content remain unavailable without an appropriate authorized plaintext source.

**Deliverables:** conversation inspector; fragment-to-message-to-operation navigation; decoder manager and version pinning; bounded content search with match previews disabled until opted in; JSON/columnar metadata export and explicit binary content export; decoder fixture corpus.

**Exit tests:** fragmented, malformed and truncated conversations never become falsely complete; correlation remains correct under concurrent calls and direction changes; decoder timeout/crash does not stop capture; opening a source with a new decoder changes a derived revision only; content search/export respects scope and redaction.

**Fallback:** display raw fragments and structured source metadata. A decoder that cannot prove framing produces candidate annotations, not authoritative message boundaries or byte totals.

### M9 — Optional supported kernel collectors

**Goal:** address specific measured gaps that passive user-mode capture cannot close.

**Prerequisites:** M6 gap report, repeatable workloads, product decision that added coverage merits deployment cost, and a dedicated driver engineering/release capability. The baseline application must remain fully usable without any driver.

**M9a: pipe metadata collector.** Prototype a supported NPFS/MSFS minifilter, starting with lifecycle, operation identity, status and completed-length metadata. Windows exposes a registration option for filtering these requests. [FLT_REGISTRATION](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/fltkernel/ns-fltkernel-_flt_registration). Validate create/close, async completion, cancellation, fast paths, instance reuse and bypass cases against the truth workload. Establish exactly which object identities and peer relationships the callbacks prove. Content copying is a separate opt-in subfeature with separate overhead and validation gates.

**M9b: network collector, only if justified.** Evaluate supported WFP layers for the required flow/process attribution or available content semantics. ALE is network-specific; it does not monitor ALPC, shared sections or arbitrary IPC. [Application Layer Enforcement](https://learn.microsoft.com/en-us/windows/win32/fwp/application-layer-enforcement--ale-). Preserve passive behavior and explicitly test any layer-specific buffering, ordering and attribution limits.

**Common implementation:** small bounded callbacks; restricted user/kernel control interface; no caller-supplied arbitrary memory access; validated lengths and ownership; no blocking payload decoding in kernel; explicit loss counters; version negotiation; driver presence/capability status; signed installation, upgrade, rollback and uninstall. Run kernel validation in disposable test environments and publish HVCI/Secure Boot and architecture compatibility. The installed driver must not silently turn itself on in ordinary captures.

**Deliverables:** separately packaged signed component, adapter, updated evidence matrix, workload benchmarks, installation/rollback guide and crash diagnostics that exclude contents by default.

**Exit tests:** independently reviewed driver boundary, stress and verifier runs on declared builds, suspend/resume, low memory, driver/service restart, consumer disconnect, invalid control messages, uninstall during idle and refused unsafe unload states. Re-run semantic conservation tests with both ETW and driver evidence enabled so duplicate observations do not inflate traffic.

**Stop rule:** no unsupported kernel patching, SSDT hooks, bypass of protected processes, or claim to observe arbitrary shared-memory loads/stores. If the supported callback path does not expose the needed fact, retain the gap. Signing/compatibility failure blocks this optional package, not the driverless product.

### M10 — Selected-process instrumentation

**Goal:** offer deeper capture for unmodified applications where the operator deliberately accepts instrumentation overhead and compatibility limits.

**Prerequisites:** a specific gap from M6, the M7 semantic schema, M8 decoder isolation where contents are involved, and explicit product choice to support attachment. This is a separate profile and distribution component, never implicit whole-system injection.

**Implementation:** assess launch-under-instrumentation and attach modes separately. Scope to selected process instances and compatible architecture/runtime/API paths. Prefer supported runtime/profiler mechanisms where applicable; document any function interception as a compatibility-sensitive adapter. Record instrumentation start/stop and all known bypass paths. Track synchronous versus asynchronous completion accurately; function entry/return does not necessarily bracket the whole I/O operation. Capture content only from explicitly enabled source/API categories with strict limits.

**Deliverables:** preflight compatibility report, explicit target selector, overhead preview, launch/attach/detach workflow, target-liveness reporting and a prominent instrumented-process marker in every result/export. Use the same observation model as M7 but label the source as instrumentation rather than a cooperative application assertion.

**Exit tests:** unsupported/protected target rejected cleanly; normal app functionality preserved in the declared test matrix; process exit, collector loss, instrumentation failure and reentrancy handled; complete and partial async I/O matched correctly; no unrelated process attached; declared overhead measured. If live detachment is unsafe, require launch mode and state the restart requirement before use.

**Fallback:** offer SDK instrumentation or existing metadata sources. Direct native calls, custom runtimes, mitigations and protected processes may remain outside coverage. API interception still cannot reveal every ordinary write to a shared mapping.

### M11 — Remote capture coordination

**Goal:** extend v1's offline multi-machine investigations to deliberately coordinated remote recording.

**Prerequisites:** M4 host/clock/workspace contracts and M5 broker hardening. Can proceed independently of kernel collectors and process instrumentation.

**Implementation:** add an explicitly installed, authenticated remote agent with device enrollment and mutual transport authentication. Separate permission to start metadata capture, collect contents, download evidence and install optional collectors. Define bounded capture duration, disk limits and expiry when the controller disappears. Preview the effective profile on each host; protocol compatibility/capability negotiation may produce different supported subsets. Keep local policy authoritative and audit remote actions.

Start with remote control plus store-and-forward files; live streaming follows only after quotas and reconnect semantics work. A coordinated start is a control barrier, not proof of synchronized clocks. Record per-host acknowledgment times and uncertainty, and use M4's alignment model. Make partial starts/stops, offline hosts and missing fragments visible. Deduplicate resumed chunk transfer by content identity and verify hashes before workspace publication.

**Deliverables:** host inventory with capability/health; coordinated start/stop; reliable session download/resume; partial-host workspace; short-lived control leases; credential rotation/revocation; remote content scope preview; CLI automation API. Remote driver installation is a separate administrative workflow, not a side effect of pressing Record.

**Exit tests:** expired/revoked credentials denied; wrong host identity rejected; no remote arbitrary shell; capture survives controller disconnection only within configured policy; quota stops safely; partial host failure does not erase other captures; transferred package matches source; clock offset is not mistaken for network latency. A remote metadata-only policy cannot be upgraded to contents by a local viewer setting.

**Fallback:** keep offline `.icat`/ETL import and manual transfer working. Remote features must not require a cloud service or make local viewing network-dependent.

### M12 — Comparative and causal exploration

**Goal:** make repeated investigations useful without turning uncertain observations into automated conclusions.

**Prerequisites:** stable metric/coverage contracts and M4 investigations; richer sources from M6–M10 are optional inputs.

**Implementation:** compare two captures or selected time windows by compatible mechanism, metric, entity grouping and capture profile. Provide normalized rates and absolute counts, baseline/difference timelines, ranked changed relations, and graph overlays. Entity matching across sessions is a separate qualified mapping; executable name alone cannot establish identity. Distinguish unavailable measurement from disappeared activity.

Add recurring communication patterns using structural signatures such as mechanism + interface/operation + endpoint role, with source-specific normalization. Optional outlier detection explains the baseline window, minimum sample size and uncertainty. A causal navigation view traverses only evidence-backed parent/related links; proximity candidates stay separate. Include bottleneck views using validated local call/wait spans, with no unsupported critical-path or deadlock claims.

**Deliverables:** saved comparison specifications, relation-difference explorer, explainable pattern groups, annotations/bookmarks, shareable redacted reports and an accessible tabular equivalent. Reports record scope, byte domain, accounting policy, missing sources and alignment revision.

**Exit tests:** identical captures compare as equal; changed capture profiles cannot masquerade as workload changes; differing durations normalize correctly; uncertainty propagates; a synthetic known dependency is traversable and unrelated simultaneous events are not labeled causal. Outlier labels describe observations, not malware or root cause.

**Fallback:** where captures are not comparable, show side-by-side values with explicit incompatibility reasons rather than an invalid numeric delta.

### M13 — Platform and long-term operation qualification

**Goal:** turn the expanded product into a maintainable Windows diagnostics platform.

**Prerequisites:** select which M6–M12 features are shipping; qualify each optional component independently.

**Implementation:** native ARM64 qualification; selected Windows Server deployments; unattended bounded capture; installation/update/rollback; format migration tools; old-session compatibility; signed release provenance and dependency inventory. Add a repeatable provider-schema diff/fixture job for candidate Windows updates and a published deprecation policy. Test remote protocol and optional collector version skew.

Make 24-hour rolling recording and large multi-host workspaces explicit qualification tiers with measured quotas and recovery. Retain the short interactive diagnostic profile as the default. If a persistent service is offered, it is explicitly enabled, visible and removable, with a finite retention policy. Boot-time collection, if later requested, is its own feasibility track rather than an assumed property of a background service.

**Deliverables:** expanded support matrix, scale report, long-running recovery tests, compatibility corpus, upgrade/rollback guides and a privacy-reviewed support bundle generator.

**Exit tests:** migration preserves IDs/evidence; old viewer refuses unsupported features safely; 24-hour tests stay within declared memory/disk budgets; Windows update/schema drift degrades affected adapters honestly; uninstall removes installed components without deleting user evidence unless explicitly requested; optional components failing to qualify do not block unrelated supported editions.

### 15.1 Dependency graph and sequencing

```mermaid
flowchart LR
  V1[M5 qualified v1] --> M6[Broader driverless discovery M6]
  V1 --> M7[Cooperative instrumentation M7]
  V1 --> M11[Remote coordination M11]
  M6 --> M9[Optional kernel collectors M9]
  M7 --> M8[Conversation reconstruction M8]
  M6 --> M10[Selected-process instrumentation M10]
  M7 --> M10
  V1 --> M12[Comparative exploration M12]
  M8 --> M13[Platform and operation qualification M13]
  M9 --> M13
  M10 --> M13
  M11 --> M13
  M12 --> M13
```

The M13 arrows mean “qualify if selected for shipment,” not that every optional component must exist before another release. M8 can begin earlier with a validated existing content source. Prefer incremental releases by completed capability; do not hold all improvements for one enormous v2.

Recommended sequence under the confirmed priorities: first M6 and M7; then the justified M9 collector work and M8 reconstruction; then M10 attachment after the SDK contract is proven. Schedule M11 remote coordination and M12 comparisons after the coverage/depth work has a usable release, unless new needs justify a change. Apply M13 qualification incrementally to every release and complete its long-running/expanded-platform tier after the selected features stabilize. This order is a product priority, not a requirement to leave independent engineering work idle.

### 15.2 Post-v1 release review

Before each milestone is committed for shipment, record the user problem, exact new evidence obtainable, supported environments, expected overhead, deployment burden and exit fixtures. Review these decisions with the product owner when feasibility materially changes the proposed scope. Ordinary implementation details follow this specification without repeated conceptual approval.

Success is deeper trustworthy exploration: each added source should make an actual previously unresolved relationship, measurement or conversation explainable. More enabled providers, more graph edges, or more retained bytes alone are not success criteria.

## 16. Risk register and decision rules

| Risk | Consequence | Mitigation and release decision |
|---|---|---|
| Pipe or section coverage weaker than expected | Product misses requested Windows breadth | M0 truth fixtures; explicit topology/traffic tiers; release scope review before claims |
| Schema drift or undocumented events | Misdecoded facts or broken adapter | Saved schemas, versioned adapters, fail-to-opaque behavior and build matrix |
| False process/resource pairing | Convincing but incorrect graph | Lifetime identities, conservative joins, evidence inspector, unresolved nodes |
| Cross-layer/dual-endpoint duplication | Misleading volume ranking | Typed accounting domains and conservation tests |
| Capture overwhelms storage/consumer | Missing history or unstable machine | Measured profiles, quotas, bounded stages, visible loss and lag |
| High-cardinality indices/layout | Memory explosion and unusable graph | Sparse indices, cache budgets, visible expansion limits and persistent pins |
| Incorrect clock alignment | False cross-host causality/latency | Versioned clock mappings, uncertainty and refusal to overstate timing |
| Payload collection beyond expectations | Sensitive information retained/shared | Narrow sources, explicit content policy, separate original/redacted exports |
| Broker attack surface | Privileged misuse | Small allowlist, authenticated local channel, safe storage root, no elevated parsers |
| Instrumentation perturbs workload | Diagnosis changes the observed behavior | Overhead measurement, profile disclosure and reproducible no-capture baseline |
| Retention evicts referenced evidence | Broken bookmarks or irreproducible reports | Generation leases and explicit pin storage policy |
| Session growth outruns disk, reopen and overview budgets | A long capture becomes unopenable or unusable exactly when it is most valuable | §12.1 tiers and invariants; persisted overview pyramid; measured T1/T3 reopen ratio; growth disclosure and retention with boundary checkpoints |
| Interaction latency degrades as data grows | The product feels slower the longer it runs, which reads as unreliability | §6.8 latency windows measured per tier; cached geometry on the input path; coarse-then-exact publication; S2 and S3 |
| Graph interaction design unvalidated | The pane given equal product prominence is the one with no proven interaction contract | The graph-layout ADR of §16; deterministic bounded layout; M0 scored usability gate on the §17 questions before breadth work |
| Palette and encoding collisions | Four meanings competing for the same channels become unreadable, especially under color-vision differences | §6.6 channel allocation with redundant encodings and measured contrast enforced by tests (R14) |
| Oversized first release | Delay before useful tool | Deliver M2 preview while keeping M3/M4 requirements visible for full v1 |

Required ADRs, each recorded before the corresponding implementation and each stating evidence, alternatives and reversal cost. A number is assigned when an ADR is written, in the order they are written, so this list is what must be decided rather than what each decision will be called. Three entries below were not foreseen here and were written because implementation forced the decision; that is the intended behaviour, not a deviation.

| ADR | Decision |
|---|---|
| ADR-001 | Stack, toolchain pins and UI framework (§1.3) |
| ADR-002 | Owned ETW session lifecycle and safety strategy (§9.2, §18.2) |
| ADR-003 | Named-pipe coverage scope after M0 measurement (§4, §14.2) |
| ADR-004 | RPC coverage scope after M0 measurement (§4, §14.2) |
| ADR-005 | Identities, instance epochs and alias revisions (§7.2) |
| ADR-006 | Native clocks, local conversion and timestamp quarantine (§8, §18.3) |
| ADR-007 | Supported build policy and compatibility matrix (§1.3) |
| ADR-008 | Raw-evidence strategy: authoritative journal versus ETL (§9.3) |
| ADR-009 | How the callback allocation budget is measured, and what it measured (§12, §18.3) |
| ADR-010 | Journal retention and the committed boundary a generation carries (§20.1) |
| ADR-011 | The derived segment format and where a metric contribution is decided (§20.1, §5.3) |
| ADR-012 | Metric accounting: a row's side versus a request's accounting, endpoint activity as a metric of its own, and unavailable versus refused (§5.3, §19.2) |
| ADR-013 | Process instances from lifecycle evidence, and how a record binds to one (§7.2, §7.3, §7.4) |
| ADR-014 | Transport relations from mirrored endpoints, and the process filters built on them (§7.4, §19.1, §5.3) |
| ADR-015 | The canonical query identity, and what it hashes (§10.4, §10.5, §23, §24) |
| ADR-016 | Connection incarnations from witnessed lifecycle (§7.2, §7.4) |
| ADR-017 | `between(A,B)`: the records connecting two process sets, its direction policy, and the binding rule of relation-read answers (§19.1, §10.5, §23) |
| ADR-018 | Capture coverage is immutable evidence, distinct from admitted journal rows and metric values; unknown source and decode facts fail closed; since revision 275 a live epoch that delivered through its stop speaks for the readings it recorded between (§7.1, §10.3, §18.1, §20.1) |
| ADR-019 | UDP datagrams admitted by their measured orientation (§4, §7.4, §14.2) |
| ADR-020 | UDP datagrams related through mirrored endpoints, keyed by protocol (§7.1, §7.4, §24) |
| ADR-021 | A live recording publishes one session generation when its capture stops (§9.3, §20.1, §20.6) |
| ADR-022 | A live recording publishes as it records, one journal chunk per generation (§19.3, §20.1) |
| ADR-023 | Re-derivation replays a live recording's journal chunks as one capture (§18.4, §20.1) |
| ADR-024 | A live recording is released a chunk at a time, and re-derivation never drops rows it cannot rebuild (§20.1) |
| ADR-025 | A store instance hashes each immutable dependency once, and confirms what it hashed from one directory listing (§20.1, revision 129) |
| ADR-026 | Small publications are coalesced into bounded segments, keeping every row (§20.1) |
| ADR-027 | A privileged recording publishes evidence only, and an ordinary process derives the session (§9, §18.1) |
| ADR-028 | The broker's recording path is a module of its own (§9, R16, R19) |
| ADR-029 | IPv6 endpoints are stored in a table version, related by family and admitted by measurement (§7, §18.3, R22, P27) |
| ADR-030 | An RPC record, which names no process, binds to the process that raised it, as measured per side (§4.1, §7.1, §24, R22) |
| ADR-031 | An RPC call is its start and its stop, paired by activity id on one side, never by time and never across sides (§7.1, §7.4, P7, P8) |
| ADR-032 | An operation is counted by the record that puts it in scope: its start for a started count, its paired stop for a completed or failed one (§5, §5.3, §21.1, P4) |
| ADR-033 | A duration names its interval, with no default, and its cohort and statistic, and states censored operations rather than measuring them (§5, §19.2, §23) |
| ADR-034 | ALPC links an RPC client call to the server call that served it, through the call's one send, its one receive and the receiving thread's call, checked by interface and procedure; measured in the lab, admitted by the opt-in RPC peers profile and implemented as `rpc-call-peer-v1` (§7.4, §9.4, P7) |
| ADR-035 | A capture whose opt-in profile needs a kernel flag group makes its one owned session a private system logger, kernel flags first; never another's, never adopted or restarted, refused at the machine's limit of eight, recovered like any owned session; classic events admitted from the machine's schema (amends ADR-002; §9.2, P14, P19) |
| ADR-036 | Content a capture keeps is restricted evidence beside the journal, never in its metadata projection: a `content-v1` chunk per journal chunk, each fragment with I21's facts, bound by the request's limits and inspection consent, released only with its journal chunk, never read by anything that reads metadata, and never in a redacted package (§10.1, §11, I21, I22) |
| ADR-037 | WinINet's capture provider records an HTTP exchange exactly - each request and response head and body, in order, whole, with its direction, exchange identity and boundaries - for the processes a capture names, scoped by the session's process filter before anything is kept; M3's content-capable source, measured in the lab and admitted to no profile yet (§11.2, M3, M8, I21) |
| ADR-038 | An investigation is one `workspace-v1` file that references separately valid sessions by identity - the session and the capture its journal records, never a store's source name - one member per capture, resolved against where each was last found with the reason, relinked only to itself, and never writing to a session; hosts are the sources' own identities, grouped only when equal and never by name, or by a person's confirmation kept as a revision (§8.3, §8.4, M4, I9, R22, P6) |
| ADR-039 | A manual alignment is a person's bounded statement that one member's instant is one of the workspace's time reference, with an optional drift bound; bounds add linearly and measured parts in quadrature, an unknown contributor leaves the uncertainty unknown, and an order across members is stated only beyond the pair's uncertainty, counting each part once; two separated anchors measure a rate, whose wander a person bounds; a member may be aligned through another, whose alignment two members share and a comparison counts only by its drift between them (§8.2, M4, I9, R3, R21) |
| ADR-040 | A live capture records its clock paired with the wall clock at start and stop, each pair's acquisition bracket measured, and names its boot by a random token kept in a volatile registry key Windows deletes on restart, never by the boot count a clone shares; the calibration is capture evidence of its own kind, carried, never released, never in a redacted package (§8.1, §8.2, M4, R3, R22, I22) |
| ADR-041 | A join across captures is a candidate, never established: one member's one-sided connection and another's with mirrored endpoints of one protocol, whose lifetimes can overlap in the investigation's time within their uncertainty, loopback only within one host identity, listed with its evidence and alternatives; a person's decision about one is a kept revision with the alignments it was made under, flagged for review when they change; a known address translation a person states lets endpoints mirror through it, and the candidate says it rests on it (§8.3, M4, P6, R22) |
| ADR-042 | An investigation is shared as one folder: its file beside an original evidence package of each chosen member whose capture is where it was last found, named relative to the file so it moves whole; identity, time, names and decisions go unchanged, a member that moved on still selects what it did until relinked, every other member stays a reference by its whole path, what it exposes is stated first, and nothing appears under its name until each copy verified and the investigation reopened finding each (§8.4, §11.3, M4, I15, R22) |
| not written | Byte accounting: the canonical owner rule for a proven association (§5.3). ADR-011 fixed how a domain, a side and an unknown are stored and summed over one segment, and ADR-012 which contributions each accounting takes; which contribution owns a total once a transfer association is proven is still owed and needs the correlators |
| not written | Correlation quality model (§7.4) |
| not written | Broker trust boundary and client authentication (§20.3) |
| not written | Content policy and privacy (§11) |
| not written | Indices, tiles and cache budgets (§10.2) |
| not written | Snapshot publication and query scheduling (§19.3) |
| not written | Retention, pinning and export (§20.2) |
| not written | Graph projection, layout algorithm and determinism (§19.4) |
| not written | Query identity and canonical specification form (§10.5) |
| not written | Visual encoding, palette and accessibility targets (§6.6). `theme/` already measures and enforces them; the ADR recording why these targets were chosen is owed |
| not written | Fixture naming and traceability scheme (§13.5). `fixtures/index.json` and its two checks implement it; the ADR is owed |

## 17. Remaining conceptual choices to revisit after the prototype

The central choices are settled in section 1. Remaining choices have safe proposed defaults so implementation can proceed without reopening the product concept:

| Choice | Proposed default | Evidence that could change it |
|---|---|---|
| Graph projection on first open | Process graph with explicit resource hubs for shared/ambiguous channels | User testing shows resource-first graph explains the system better |
| Amount of raw evidence retained | Bounded admitted-event journal for live capture; imported originals and diagnostic ETL are explicit choices | M0 journal overhead, decode fidelity and content-policy findings |
| Background history | User-started bounded recording; no persistent always-on service | Clear need for pre-incident history and acceptable measured overhead |
| Support breadth | Tested Windows x64 client/server builds, then ARM64 | Concrete deployment targets and capture compatibility results |
| Content decoding depth | Hex/text/structured source fields first | Repeated need for a specific protocol and a reliable decoder contract |
| Timing and stack depth | Focused optional profile | Exploration studies demonstrate that stacks are essential by default |
| Licensing/distribution | Decide for the new repository before public packaging | Owner preference; a license inherited with any reused component constrains that component only, not this product's whole distribution strategy |

The prototype review should answer: Can a new user discover a surprising but real communication relationship in under two minutes? Can they explain exactly why InterCat drew the edge? Can they distinguish an unmeasured channel from a quiet one? Can they navigate from a whole-system burst to source evidence without losing their place? These are stronger product gates than merely displaying many nodes.

## 18. Capture and replay implementation contracts

### 18.1 Authoritative live journal and event ownership

Use one journal writer per capture, owned by the broker. Callbacks perform bounded admission, assign stream/epoch ordinals and copy approved data into pooled buffers. The writer appends framed batches and publishes immutable committed journal chunks/generations; analysis reads committed evidence without using the UI control channel as an event firehose. Acquisition can continue if the analysis process stalls, until the declared queue/disk policy is reached. The UI receives low-rate progress and generation notifications.

The journal is **InterCat's admitted evidence**, not a byte-identical replacement for an ETL and not a guarantee of zero ETW loss. Record policy omissions and source loss separately. By default, analysis publishes durable snapshots only after the source batch and derived segment are committed. If a future low-latency mode renders uncommitted data, it must visibly label that state and cannot export it as durable evidence.

An event envelope contains:

```text
RecordEnvelopeV1
  CaptureId, StreamId, SourceEpoch, RecordOrdinal
  EventHeaderFields, BufferContextFields, TimestampEncoding, ClockId
  ExtendedItems[] = { type, flags, owned bytes }
  Body = { classification, retained length, original length?, owned bytes }
  SchemaReference?, AdmissionPolicyId, IntegrityChecksum
```

The per-record `ClockId` names a clock; it does not describe one. Persist the capture's full source clock descriptor of §8.1 once per journal, before its first record batch, so a reader converts native ticks offline without the session that produced them. A journal whose records name a clock the file does not describe is incomplete evidence, and replay says so rather than assuming the reader's own clock.

Extended-data items are opt-in per provider enablement, not a property of an event: `EnableTraceEx2` enable properties decide whether Windows attaches a SID, a terminal-services id, a process start key, an event key or a call stack to a record. A capture therefore records which enable properties it requested alongside its records, because without that setting "no extended items were observed" describes the capture's configuration rather than the source. Measured on build `10.0.26220.0-x64`: `Microsoft-Windows-Kernel-Network` and `Microsoft-Windows-Kernel-Process` emit no extended item at all under a plain enablement, and emit `STACK_TRACE64` items once call stacks are requested.

Copy the contents of permitted extended-data items, not native addresses. An item's second header field is a linkage bit, not a general flags word; keep it, because it is what marks a continued item. Preserve fields needed for related activities, architecture/pointer-width interpretation and embedded decoding metadata. Do not serialize `UserContext` or other callback pointers. Windows defines header, extended data and user data separately in `EVENT_RECORD`; serializing only its user-data buffer loses potentially important decoding/correlation context. [EVENT_RECORD contract](https://learn.microsoft.com/en-us/windows/win32/api/evntcons/ns-evntcons-event_record).

Every buffer has a single owner at a time: callback -> queue -> journal writer -> returned pool. Use explicit disposable leases with failure-path tests. Do not keep TraceEvent callback objects or spans into callback memory for later asynchronous work. All allocations and copies are bounded by envelope limits, including extended items; do not assume an event's user-data limit bounds its complete serialized envelope.

### 18.2 Profile compilation and content admission

Compile a profile into an immutable `EffectiveCapturePlan` before starting:

```text
ProviderPlan
  Identity, SessionKind, Level, MatchAnyKeyword, MatchAllKeyword
  EnableProperties (which extended-data items the capture asks Windows to attach)
  SupportedSourceFilters, RequiredLifecycleSources
  AllowedDescriptorsAndSchemaContracts, AdmissionPolicy, FieldPolicy
  ClockMode, BufferBudget, FlushPolicy, ContentBudget, ValidationFixtureIds
```

Separate classic/kernel event enablement from manifest-provider enablement. Select and record the tested logger strategy per Windows build: prefer a privately named system logger session where the build supports one, fall back to a named manifest-provider session otherwise, and record in the manifest which strategy the capture actually used. Never take over `NT Kernel Logger`, and never stop a session because its name merely resembles InterCat's (§9.2). Treat provider registration, enablement success, observed event health and validated semantic coverage as different checks.

`EnableTraceEx2` supports provider configuration and filter descriptors, but support and filtering behavior must be evaluated per source; an application-side filter is not equivalent to preventing emission. Handle keyword-zero events and provider-specific enable properties explicitly instead of assuming a narrow keyword mask is a complete allowlist. [EnableTraceEx2](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-enabletraceex2), [enable parameters](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-enable_trace_parameters).

Admission policies are explicit:

| Mode | Known permitted event | Unknown/unapproved event body |
|---|---|---|
| Metadata only | Persist the body only if all retained fields are approved metadata; otherwise persist a labeled metadata projection using a validated adapter | Discard body before persistence; retain allowed header/diagnostic fields and omission counters |
| Scoped content | Apply validated source/process/channel scope and content budgets; preserve classification and truncation | Omit unless a separately approved opaque-evidence rule permits retention |
| Original evidence import/diagnostic ETL | Preserve the selected original unchanged and mark it potentially content-bearing | Preserve as original evidence; no claim of metadata-only sanitation |

Admission is a bounded descriptor/schema/shape check, not general-purpose payload decoding inside the privileged callback. Preload validated schema contracts. Providers requiring expensive interpretation to enforce policy need a separately bounded admission stage before persistence or must be unavailable for that profile. Reject unsupported scope guarantees rather than capturing all contents and merely hiding them in the view.

The metadata contract identifies field categories, not a guarantee that arbitrary endpoint strings or command lines contain no secrets. Opaque omitted bodies cannot later be recovered from the InterCat session. A transformed metadata projection must never be labeled original raw bytes. Unknown-body suppression is a visible policy omission, not unexplained parser loss.

### 18.3 Schemas and timestamp decoding

Schema lookup keys include provider, event descriptor/classic type, version, opcode where relevant, source architecture, decoding kind and schema fingerprint. Support manifest, classic and self-describing events through distinct tested paths. Persist required decoding metadata and enum/map definitions when obtainable; do not assume the viewing machine has the recording machine's manifests or binaries. TDH metadata and event-map lookup can fail independently. [TdhGetEventInformation](https://learn.microsoft.com/en-us/windows/win32/api/tdh/nf-tdh-tdhgeteventinformation), [TdhGetEventMapInformation](https://learn.microsoft.com/en-us/windows/win32/api/tdh/nf-tdh-tdhgeteventmapinformation).

A missing schema yields a diagnosable opaque or header-only record according to admission policy. WPP/TMF-dependent events without matching metadata stay unsupported. Decoder numeric values remain available even when localized display labels cannot be resolved. Fuzz variable counts, strings, pointer-sized fields and extended metadata before promoting an adapter.

For the native consumer path, explicitly select record callbacks and the intended timestamp mode. `ProcessTrace` converts timestamps to system time by default unless raw timestamp processing is selected. Save the actual mode and source clock metadata; a .NET wrapper must prove equivalent behavior in fixtures. [EVENT_TRACE_LOGFILE processing modes](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace_logfilew).

Convert local QPC deltas through checked `Int128` arithmetic and an explicit rounding rule. Keep native ticks for precise local interval calculation. Quarantine implausible or overflowed timestamps with raw evidence intact; a corrupted future event must not stretch the whole viewport or advance the reorder watermark arbitrarily.

### 18.4 Stable import and live replay

**An identity that appears inside a record's identity is derived from the evidence, never minted per run.** That rule already covers an import's clock and host (plan revision 24); it covers the capture identity too, because the capture ID is inside every raw-record key and therefore inside every observation identity. An importer that minted one per run would produce a session in which two imports of one file disagree about which records they hold — and because the canonical record key deliberately excludes the capture ID, nothing in the keying would notice (ADR-011, plan revision 26).

Replaying an InterCat journal preserves its original record IDs. Re-importing an existing `.icat` session reuses its identity after integrity validation. An optional companion ETL is not silently merged into that journal, even if it contains more events; a user can import it as separate evidence, with overlap disclosed.

For standalone ETL, compute a source content identity and import-contract version before assigning persistent identities. Its clock and host identities are derived from that content identity rather than minted per run or taken from the reading machine: the clock ID is part of the canonical record key, so a minted one would make two imports of one file disagree about their records' identities, and a borrowed host would let imported readings be compared against local captures as though they shared a clock. The recorded tick rate is the recording machine's fact and must come from the file; where the adapter does not expose it, derive it from the file's own readings and check every sampled reading against the derived rate, refusing a file that does not reproduce rather than reading it at an assumed rate. Reuse a matching completed import when available. Do not assume `ProcessTrace` callback order is deterministic: equal-time records from different CPUs can arrive in an unpredictable order. [ProcessTrace ordering limits](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-processtrace).

Use an external-sort import path with canonical keys: source identity, clock/time representation, semantic event header/context fields, and a fingerprint of the admitted envelope. Resolve hash collisions by comparing canonical bytes. Preserve multiplicity for byte-identical records with an occurrence index; their individual physical identity cannot be distinguished by these fields, and the UI must not invent an ordering between them. Exclude callback-local pointer values and transient consumer context from the key. Preserve original delivery ordinal as separate import metadata, not as a reproducibility guarantee. `contracts/import-v1.md` freezes the exact key inputs and three decisions building it forced. The buffer context is keyed: which processor produced a record and which logger carried it are recorded content, so two otherwise identical records from different processors are two records rather than one seen twice. File-local schema and policy reference numbers are not keyed, because a table position is not an identity; the table's own digest covers what those references mean. And a sort needs a total order, so the canonical order ends in tie-breaks on stream, epoch and delivery ordinal - which makes a run repeatable and is explicitly not a claim about which of two indistinguishable records came first. Which ordinal ends up paired with which occurrence index is a property of the run, and only the keys, instants and multiplicities are reproducible across readers.

The canonical importer supports bounded spill files and cancellation. The retained evidence policy and import-contract version are part of import identity, so importing metadata-only and importing original contents are not silently treated as identical derived sessions. If original bodies are intentionally omitted, document the resulting identity granularity. Normalizer upgrades produce a new derivation generation with links back to raw-record identities; old bookmarks can fall back to raw evidence when a fact is split or renamed.

### 18.5 Startup inventory, loss and correlation continuity

Start capture delivery before taking asynchronous inventory so short-lived processes are less likely to be missed. Reconcile events with per-item inventory timestamps and a query start/end interval. Inventory is not an atomic view of the OS: a row can disappear or be reused during enumeration. A snapshot saying “present” supplies a witnessed presence interval, not an exact creation time.

Keep `ObservedStart`, `PresentAtInventory`, `ObservedEnd`, `LastSeen` and `UncertainLifetime` separate. An empty inventory result after an access error is not process exit. Inventory enrichment must not overwrite a proven start-key identity using only a matching PID.

Track continuity epochs for correlation fields susceptible to reuse. A gap that could hide lifecycle transitions reduces confidence in joins across it; retain valid explicit identities where available, but do not pair solely on a recycled numeric identifier. Pending operation states are `Started`, `Completed`, `ExplicitlyFailed`, `OrphanCompletion`, `OpenAtBoundary`, `Ambiguous`, and `EvictedUnresolved`. A heuristic pending-state timeout produces the last state, not a Windows timeout error.

Initial tuning values to benchmark: 2-second reorder grace, 1-second health sampling and 30-second pending-join expiry for adapters without a stronger lifecycle contract. Long-lived identified calls either spill pending state to bounded disk or stay open; they are never falsely terminated at expiry. Spill files are append-only under the session root, framed like a journal batch with a spill header, keyed by correlation key, bounded to TUNABLE: 128 MiB per capture, and evicted oldest-first within that budget with an `EvictedUnresolved` reason. Spill is derived state (R20): recovery never depends on it, and losing a spill file degrades pending joins rather than the session. Timeouts and budgets are recorded profile settings, and every adapter can require stricter semantics.

## 19. Analysis and rendering implementation contracts

### 19.1 Unified analysis basis and process/peer filters

Compile each user query into one explicit basis — source observations, logical operations, or resource topology (`EN-Basis`, §23), whose permitted metrics, byte domains and accounting sides are fixed by §5.3's matrix. Source events used as supporting evidence do not become additional operations. A topology query can count resources/memberships but cannot invent traffic values. The query compiler rejects a metric that is undefined for the selected basis and offers a compatible metric.

Define process filters precisely:

* `owner(P)` means the directly attributed source process, where available.
* `participant(P)` means P participates under the selected binding/correlation revision; this is the ordinary process-focus behavior.
  It is answered through the relation revision that proves a record's other end (`contracts/relations-v1.md`); a record whose other end it cannot resolve is disclosed, never treated as `owner(P)` or guessed.
* `sender(P)` and `receiver(P)` require a known data direction, not an assumed client/server role.
* `between(A,B)` selects relationships connecting the selected participant sets under the chosen direction policy.
  A record connects them when its maker is a process of one set and its other end, under the relation revision, a process of the other; the direction policy is `EN-BetweenDirection` (`contracts/metrics-v1.md` §5, ADR-017). It names the processes at both ends, so it is never combined with a focus.
* `peer(P,Q)` is relative to the focused process P; it cannot silently become a global PID filter.

Unknown values use explicit `is unknown` predicates. Negation of a known-value comparison does not include unknowns implicitly. Include values within a facet are ORed; independent dimensions are ANDed; exclusions are explicit. If generic Boolean expressions are supported, show the normalized expression and test it against the same three-valued logic.

Filtering a graph does not remove the unselected counterpart needed to explain a selected relation. Render that counterpart as a context node unless the user requests a strict induced subgraph. Context nodes do not enter selected-node totals. Optional neighborhood expansion adds context outside the filter and must be visually distinguished; it cannot increase timeline/ranking totals silently.

Graph edges, timeline cells and ranked rows use the same eligible fact set and accounting policy. A bidirectional aggregate edge references two directional metric sets. Resource projection may draw two legs for one exchange; only one is the accounting owner, while both navigate to the same evidence. Per-node incident totals and edge totals need not be arithmetically interchangeable; expose the selected denominator.

### 19.2 Accounting and rate rules

Introduce an internal `MetricContribution` keyed by `(basis identity, metric domain, observation side)`, with evidence links. This is the unit deduplicated before graph grouping or timeline aggregation. Alternative source records remain visible as corroborating evidence. Without proof that observations describe the same contribution, retain them separately and state the unresolved accounting issue rather than guessing a transfer total.

Default rates divide by the full selected interval. Show `observed rate` and coverage defects when records are missing; do not divide by a shorter apparently healthy interval and label the result the whole-interval rate. An optional covered-time rate needs a source-specific valid exposure duration and a different label. Statistical sampling requires an explicit estimator and error model; no general scaling factor applies to all event-loss modes.

Report mapping capacity once per identified resource, with separate per-process mapped-view lengths. Alias views, partial mappings and multiple participants must not multiply resource capacity. Latency distributions identify their cohort: operations completed in range by default, with started-in-range available separately. Mark left/right-censored operations and distinguish summed operation time from union-of-busy-time; concurrent work can make the former exceed wall time.

Multi-host selection defaults to transformed point-estimate timestamps. Report how many observations have uncertainty intervals crossing a selection boundary. An optional uncertainty-overlap mode includes those possible matches and labels the result accordingly. Cross-host alignment changes must not alter locally measured operation durations.

### 19.3 Query execution and cancellation

Implement a small query planner before adding sophisticated tiles:

```text
acquire snapshot leases and normalize AnalysisSpec
resolve clock bounds and candidate segments
intersect sparse postings / low-cardinality bitmaps
apply revisioned entity/relationship predicates
produce eligible basis IDs and metric contributions
aggregate once into requested timeline, graph and ranking projections
publish one coherent AnalysisBundle when still current
```

The eligible-ID set may be streamed rather than fully materialized. Exact common queries should share expensive predicate work across projections. Graph layout is subsequent presentation work: it does not delay correct numeric results. A late layout result can apply only to the matching graph identity.

Start with one coalescing scheduler per workspace, a latest-request slot and bounded worker pools. Cancel superseded jobs; check cancellation at each segment/block and within expensive joins. Give selected evidence pages and visible gestures precedence over background overview refinement, but reserve capacity for ingestion. Never launch a new complete query pipeline for every pointer-move event.

Suggested initial cadence: coalesce gesture requests over 30–50 ms, publish live analytic snapshots at up to 4 Hz, update live ranking at up to 1 Hz, and redraw cached interaction geometry at display rate. These are analysis/UI cadences, not journal chunk-publication intervals. Benchmark these values; they are not source flush guarantees. Exact counts can arrive after a clearly labeled cached/coarse preview. A preview cannot be exported as an exact result.

Cache keys use canonical serialized filter semantics, snapshot vector, normalization/entity/correlation/alignment versions, basis, metric, accounting side, time scope, grouping and requested resolution. Budget each cache by bytes, not entry count alone. Cache aliases cannot survive a rebind from one process instance to another. Cursors carry snapshot/query identity; applying a cursor to another identity is a handled restart, not a silently shifted page. A keyset cursor over an order that a newer snapshot only extends may instead continue strictly after its last row in that snapshot, stating the snapshot it came from; it still restarts when the query, a rule or the derivation changes (revision 95).

Revision 89: the first read-side channel and observation pages acquire one manifest lease per request. `icat channels` pages every admitted paired TCP incarnation, optionally scoped by a stable process-instance ID, even when the 4,096-channel overview projection is withheld. It uses exactly the overview's admission and channel-shape rules. `icat evidence` pages the actual admitted `observation-v1` rows, unscoped or for one admitted paired TCP channel; each row carries its observation identity, source descriptor, native reading, raw-record locator and the immutable segment position in that generation. Output pages have a 200-row maximum. Their cursors bind the manifest digest, relevant relation-rule revision and query scope and return a visible restart on change; they do not silently reuse an offset in a newer generation. These normalized rows are not original payload-byte export or completion-paired logical operations. L4 and a full L5 raw-record inspector remain gated on their own derivation and provenance work; a row page must never be marketed as either.

Revision 90: an evidence page may additionally take a half-open interval in the same 100-nanosecond session-relative presentation ticks as the workspace. It excludes rows without usable session time only when a deliberate interval exists, and the interval is part of cursor identity. The Desktop's first bounded source-row inspector reads 100-row pages off the UI thread at the current whole-machine or channel context, applies a deliberate time brush, names the provider, descriptor, raw locator, normalized fact and quality, and refuses to continue if the session or generation changes. It deliberately disables whole-machine inspection while a process is selected, rather than silently presenting unscoped rows as if they were process-filtered. This is an intermediate observation inspector, not completed L4 operation pairing, original payload inspection, or the final L5 ladder projection. An explicit saved-session open replaces even an older generation of the same session; live publications alone use the newer-generation guard.

Revision 91: the bounded evidence page adds an optional canonical owner-process instance scope, using the existing `process-binding-v2` and the requested evidence-strength policy, never a bare PID. It intersects channel and time scope when both are named, and all scopes and binding-rule revisions enter cursor identity. The Desktop may therefore offer the source-row inspector for the selected process at Machine or the focused Process rung, as well as whole-machine and paired-channel contexts; it names the scope and does not imply peer participation. A Group rung cannot be substituted for a process. `icat evidence --owner-process` provides headless parity. This remains a normalized observation read path, not a logical-operation projection or original payload inspector. The RPC feasibility evaluator pairs fixture truth to captured activity for scoring; its simple pending map is not production correlator evidence for duplicate/reused activity IDs, loss boundaries, nested or asynchronous calls. L4 requires a separately qualified source-specific state machine.

Revision 92: Desktop channel discovery uses the same leased, bounded `SessionChannelQuery` as `icat channels`, rather than replacing a capped overview with a misleading empty L3. It pages the complete admitted paired-TCP set for the machine or focused process, names the exact total, and can open bounded source rows for a selected stable channel key. The page carries SessionId as well as generation and cursor identity so a saved-session replacement or live publication cannot silently redirect a browser. Discovery counts cover all session time; an active brush narrows only the subsequent source-row inspection and the UI states this distinction. This is an intermediate browser for a missing L3 projection above the cap, not full in-ladder L3/operation projection; no L4 or raw-payload claim follows from observed-record counts.

Revision 93: the source-row inspector adds a selected-fact path back to one original retained journal-v1 envelope. It verifies the current manifest session/generation and the selected segment row's observation identity, streams complete journal batches under the lease, rejects duplicate raw IDs or descriptor/schema disagreement, and reports an explicit unavailable reason when the source chunk has been released. It exposes original header/buffer/clock data, schema fingerprint and admission policy, body disposition and original/retained lengths, plus extended-item length metadata. No body bytes appear automatically: an explicit reveal copies at most 256 bytes for inert hex display (1,024-byte query hard maximum), without retaining pooled envelope buffers. This is not a decoder, message reconstruction, full payload/extended-byte export, or the final L5 ladder; large-session raw lookup still needs an index and a measured latency bound. UI/CLI parity for this raw-record path remains owed under R18.

Revision 94: `icat raw` supplies the matching headless path for one original journal envelope by the `icat evidence` page's session ID, generation, segment and row locator. The application resolves the coordinate under a lease, then rechecks the same manifest identity under the raw-record lease before scanning; a new publication cannot shift it onto another row. Body bytes remain hidden unless `--reveal-bytes` requests the same 256-byte inert hex prefix as Desktop. JSON names the same `raw-record-view-v1` result and an unavailable retained-raw reason. This closes read-side feature parity for this intermediate raw-record view, not the full §21.2 UI/CLI parity artifact, decoded content, bulk export or an indexed latency bound. The command's actual persisted-session invocation still requires qualification; compilation/help and the shared application locator tests alone do not prove it.

Revision 95: an evidence cursor names the last row it returned, in `segment-v1` §4's canonical order (native reading, raw locator, fact key), together with what the query means: session, captures, derivations, policy, relation and binding rules, channel, owner set and interval. It no longer binds the manifest digest. Revision 89's generation-bound cursor made paging impossible while a live capture publishes a generation every two seconds; an evidence list that restarts on every publication is not a usable L5. A following page therefore continues strictly after its row in whatever generation is current and names the generation the cursor came from. Rows published since then that sort before the cursor are not inserted into a list already under way; the first page includes them. A changed query, rule or derivation still restarts, and a generation that names one row twice is refused. Pages follow the canonical order across segments by merging them, so overlapping live segments no longer page out of time order. An owner scope may name a set of instances, which is how a Group rung reaches its evidence in one step. A page may also resolve each row's canonical owner: the instance and its executable, a candidate named as a candidate, or the reason there is none. A generation's process and relation derivations are computed once per manifest digest and shared by the overview, channel pages and evidence pages.

The original-record path adds a lookup by stable raw identity for a row read from an earlier generation of the same session, which is what a viewer needs while a live capture moves on. It searches the current generation's retained journals, checks the envelope against the row's descriptor, reading and schema, and skips a batch whose declared ordinals cannot hold the record. Journal-v1 does not itself promise that stored order follows ordinals, so the skip only speeds a hit: absence is reported only after an exhaustive search agrees. A lookup by segment coordinate stays generation-bound. On a 94,694-record, 29-chunk Explore session, `icat raw` for a late row went from 0.97 s to 0.59 s wall time including process start, which also qualifies the command against a real persisted session. Duplicate raw identities in retained journals are no longer searched for: the first verified envelope is returned, and journal-v1's per-capture identity uniqueness (I1) is relied on rather than re-proved per lookup.

Revision 96: a published session's evidence rung is part of the ladder rather than a separate dialog. `E` from any rung opens L5 with that rung's scope as a visible, removable filter, and the filter carries the scoped entity's stable key and rung. The scope is the latest entity filter: a channel, a process instance, a group's member instances in this snapshot, or the whole session, narrowed by a deliberate time brush. Removing that filter widens the scope to the next one out. A scope the generation cannot read is stated, never silently widened to the whole session. At the machine rung a selected process is the scope, and a channel chosen in the discovery list above the overview cap opens at L5 too. Records load off the UI thread 100 at a time and read as plain language: what happened, its size, when, and whose. More load on request. The loaded records are individual marks on the timeline, and the scope's channel edge is highlighted in the graph. `Enter` opens the selected record's original journal envelope by its stable raw identity.

While the user reads L5, the viewer holds the generation it was projected from, as §5.2 asks for a row being inspected. A newer live generation is offered, not applied; it is applied on F5 or when the user leaves the rung, and "load more" continues into it after the last row shown (revision 95). Recording is unaffected.

This also settles how the §3.2 ladder meets a mechanism without operation semantics. TCP and UDP records are completed transfers with no start/completion pair, so their channel rung has no L4. It says so and offers its evidence step in place of an empty pane. The M2 gate's "L0 to L5 with one gesture per rung" is met through that step, which the ladder's own invariant already permits ("evidence is always one step away"). Synthesizing operations from transfer records to fill L4 remains excluded, since §5 never lets raw event counts be renamed as calls. L4 arrives only with a source-specific, qualified start/completion correlator.

### 19.4 Stable timeline and graph drawing

Use separate immutable numeric results, layout results and paint resources. Skia drawing consumes these results and current transforms; it performs no storage queries. Dispose or reuse GPU and text resources on the appropriate UI and render lifetimes. Device loss or a software fallback must preserve navigation and the accessible tables.

Frame-loop rules (R11). These are the reason §12's frame budget is reachable, not optional optimizations:

- Cache immutable brushes, pens and typefaces per (semantic token, alpha) pair. A heat-map frame touches thousands of cells; constructing a brush per cell per frame is the single most common way to lose the budget.
- Use spans and pooled buffers; keep per-frame allocation out of the steady state so a long interaction accumulates no collection pauses.
- No boxing, LINQ, reflection, string formatting or logging inside a paint, aggregate, admission or decode loop.
- Format label text only for ticks and cells actually drawn; measure text once per (string, font, DPI, theme) and cache the result.
- Draw only visible lanes plus a small overscan, and never replace or recreate a control to resize, zoom or re-theme it.

Revision 141 measured these rules and made them hold for a repaint. Each pane's allocation per repaint was measured against an empty drawing context, on every rung of a real session, with and without a card under the pointer:

| Pane | Before | After |
|---|---|---|
| Timeline | 58–125 KB | 0 |
| Graph | 13–15 KB | 0 |
| Minimap | 11 KB | 0 |
| Hover layer with a card | 44–51 KB | 0 |

The causes, and what replaced them:

- **The drawing layer's own costs:**
  - drawing a formatted text allocates about 2 KB on every call, while a cached text layout draws for nothing;
  - a dashed pen allocates a dash effect for every stroke, on every frame and on the render thread as well. Dashes are now cached geometry under a solid pen: a ring per radius and pattern, an edge's strip per length and pattern, placed by a transform.
- **Our own per-frame work:** a new brush for every bar, pens and translucent brushes built per frame, LINQ over buckets and edges, strings formatted per frame, and the selection and pin sets rebuilt on every read. Brushes and pens are now cached per mode, buffers and maps are kept between repaints, and a label is formatted only when the value it states changes.
- **Two subtler cases:**
  - a lambda's captured locals are allocated where its method begins, so a memo that returns early still paid for the lookup behind it;
  - in code the JIT does not optimise, a span of constants is built from a field handle on every call.

A pan still formats the ticks it draws, as this section allows. A test now measures a repaint of every pane on every rung (R11). It covers the paint loops; the aggregate, admission and decode loops keep the Windows measurements of IC-019.

Revision 290 found two repaints still allocating, both hidden by how that test measured. It drew each frame into a fresh drawing context and took off one estimate of the context's own cost, the opacity stack it grows on first use, 56 bytes; a frame that pushed no opacity had those bytes to spare. Two allocations fit inside them. The RPC call lane's note was formatted on every frame, 40 to 56 bytes, and the new exchange lane's copied it. The graph's byte weighting allocated its lambdas' captured locals on every read, 48 bytes on every repaint at every rung and again in a node's card, the trap the list above names. The test now draws all its frames into one context, whose stacks grow once in the warm-up, so a counted frame's allocation is the pane's own with nothing estimated, and its tolerance fell from 32 bytes a frame to 8: an object of the smallest size, 24 bytes, allocated on every other frame now fails it. Each lane's note is formatted once per read of the lane, the weighting's lambdas moved out of the method every repaint calls, and the call and exchange lanes are measured as bars and as density, with a mark and its card under the pointer and without.

Revision 291 finds why revision 155's test of the aggregate loops failed now and then under the whole suite: twice in 48 runs, a focused and an interval count, or a projection, allocating 16 to 62 bytes a row. A query is warm only while its session's derivations stay in the derivation cache, which keeps four for the whole process, and the tests running beside it pushed them out, so a warm query bound its rows again. A test clearing the cache beside it failed it three runs in three, with the suite's own figures: a focused count 16 bytes a row and an interval count 28. A projection allocated 10 bytes a row only when its pooled buffers had gone cold as well. The test now runs alone, after the tests that run in parallel, as the tests of what the cache extends already did. Beside the same churn it passed three runs in three, and an array the size of a segment allocated in an interval count still fails it.

Revision 155 extends R11 to the aggregate loops the window runs on every interaction, and found them allocating. Warm, a projection allocated 20 bytes per row, a focused count 12 and an interval count 8: every call rebuilt an array of each row's process or channel binding, a segment's length each. The focused count also kept a dictionary per column and chose each bucket's mechanism with LINQ, about 3,200 dictionaries per call for a group. The bindings are now filled into buffers rented for each segment's pass and returned after it. Caching them instead would have kept arrays the size of the session resident, against S2. A column's tally is a flat array of counts per mechanism. Every answer is digest-identical to before, on a real session and a synthetic one. At a million rows the warm timeline detail fell from 35 to 21 ms, a group's focused count from 145 to 100 ms, and a projection from about 118 to 105 ms. A test measures the four queries at two sizes and fails on any allocation that grows with the rows.

Timeline: draw only visible lanes plus a small overscan; retain world-time coordinates and lane IDs; update pointer/pinch transforms immediately while fresh cells are pending. Indicate when cached cells are being reprojected and prevent exact hit assertions from stale geometry. Maintain a CPU-side hit index over actual data cells/intervals; cosmetic widening maps back to the correct original evidence.

Graph layout runs in three stages, off-thread, and only after a graph change or an explicit re-layout:

1. **Regions, not bands.** A node with no drawn edge (**No relationships**, an opened group's quiet **Other members**, a focused process with no relationship) has nothing to relax toward and is parked: a left-aligned footer below the related nodes, which reads as a summary rather than as part of a nearby component, or a centred row across the middle when nothing is related. Its slot depends only on the parked set. Related nodes occupy the region above it. Revisions 88–108 partitioned related nodes into one full-width band per executable group; on a real capture of hubs and their clients (revision 109) that guaranteed a tangle, because relationships almost always cross executables, so every hub sat in another strip than its clients. Executable identity is shown by labels, by the ranked table and by collapse, not by position. Host regions return as an outer partition when M4 draws more than one host.
2. **Seeding.** Place a new node beside a placed neighbour, one ideal distance away at an angle derived from `(graph identity, node ID)`; a component with nothing placed starts at its best-connected node in a cell of a deterministic grid over the region, largest component first. No random number generator is used without a recorded seed. Existing nodes keep their positions, and pins are hard constraints. "Existing" means drawn before: in this workspace, or in an earlier publication of the same session, whose laid-out positions the next workspace carries. A projection's placeholder coordinates are not positions anyone has seen, so a fresh workspace seeds every node itself and draws its first frame there. A node that returns, such as a group collapsing again on the way back up, returns to its last laid-out place, and a node still on screen stays where it is.
3. **Relaxation.** Run a fixed, bounded number of Fruchterman-Reingold steps in drawn units (the pane's design aspect, so a distance means the same both ways). Within a component every pair repels by k²/d and every relationship attracts by d²/k, with k = 0.75·√(area / related nodes) capped at 0.45 of the pane's height, so a hub and its peers form a star. Separate components repel only when closer than 2.2 k, and a gravity that is weaker across the pane's width gathers them, so they pack side by side in the pane's shape instead of being pressed flat against its edges. Two discs closer than their §6.3 radii plus a clearance are pushed apart, so no node hides another; the view scales discs down, never below half, in a pane shorter than the design height. Steps are capped by a temperature that cools quadratically. A node drawn before is anchored to its place and its steps sum to at most about 0.09 of the pane's height, so a refresh settles new nodes around the graph the user has seen instead of rearranging it; when a layout adds many nodes that are also a large share of it — opening a group, focusing a process — the anchoring eases off smoothly, as neighbours must make room. The result is the final cooled state; its stated energy (relationship length error plus disc overlap) is reported, not optimized directly. The layout worker uses 120 steps and refuses un-compacted input above 512 nodes or 4,096 edges as a hard safety contract. The Desktop applies §6.3's smaller 200-node / 500-edge bounded projection before layout; callers that bypass projection still fail closed rather than partly drawing an oversized graph. The 120 ms interaction budget is a scheduling and measurement target, not a wall-clock cutoff that may publish a different partial geometry on a busy host. Run real-session layout off-thread; cancellation or supersession keeps the last valid layout until a complete result for the current graph identity is ready.

Determinism contract: the same graph identity, seed, constraint set and iteration cap produce identical positions, independent of thread count and wall-clock time. The caps are TUNABLE; the determinism is not. Relaxation never runs during an active gesture, and a completed layout may be applied only to the graph identity it was computed for (R7). Hit testing resolves against cached drawn geometry with a hit radius of the node radius plus TUNABLE: 4 logical px and returns the node, edge or cluster actually drawn (R13). At §6.3's current hard display bound of 200 nodes / 500 edges, a measured bounded nearest scan is acceptable; require a spatial index only when a larger projection or measurements show that scan missing §6.8's input budget. This keeps the contract about latency and correct geometry instead of prescribing a data structure that the display bound may make unnecessary. Clusters expose member and relationship counts; selected/hover detail always states them, while on-canvas labels remain subject to the label budget. Pins are stored in graph coordinates, independent of timeline time, and survive refresh and explicit re-layout. They survive a later publication of the same open session by stable ID. Reopen persistence begins only when §26.3's `.icat-workspace` settings path exists; until then the UI must not claim an in-memory pin survives closing the workspace.

Revision 87: the first real L3 channel rung is derived only from the same admitted paired TCP incarnations as the graph, under one leased generation. A channel's display key uses its earliest raw-fact anchor and both process-instance IDs, not the relation index's generation-local channel number. If a later publication supplies an earlier fact, the key changes and selection falls back visibly instead of attaching to a different incarnation. The provisional TUNABLE: 4,096-channel projection bound does not erase the L0-L2 overview: above it, L3 reports that a scoped channel query is required while the graph and all-observations timeline remain available. Logical operations and exact records remain unprojected, not synthetic substitutes. The same bundle is available headlessly through `icat overview --json` (R18).

Freeze lane order and graph positions during active gestures. Process groups and graph pins use stable IDs, not row numbers or display names. Hover changes highlighting only; click establishes selection; a separate focus command changes filters. Reduced motion skips animated transitions without changing final layout or selection.

### 19.5 Self-observation and optional name/symbol enrichment

InterCat's broker, viewer, helper processes, control pipe, journal files and optional remote-agent traffic can appear in its own capture. Record their process/channel identities and label them as collector activity. Default exploration may hide them through a visible view filter while retaining evidence and excluded counts. Do not silently remove every event involving InterCat, because legitimate interactions with it may be the investigation target.

DNS reverse lookup and symbol downloads are disabled by default; they can generate traffic and change the system under observation. Resolve names from captured evidence or local caches first. Optional online enrichment requires an explicit action, runs outside the callback path and records its provenance. Names are time-scoped annotations; a currently resolved DNS name is not proof of the peer's historical identity. Stack frames remain raw module/address evidence until symbol identity matches the captured module; unresolved frames remain usable.

## 20. Storage and operational implementation contracts

### 20.1 Physical storage v0 and commit protocol

Implement the minimum store first: immutable fixed-width little-endian columns, null bitmaps, variable-data chunks, dictionaries, a raw-record locator and time-block metadata. Initial tuning defaults are up to 250,000 observations or 64 MiB of staged normalized data per segment, whichever comes first; flush a smaller segment when its evidence journal chunk is published or when another bounded storage trigger requires it. The privileged journal-chunk cadence is a prepared capture setting and is **not** §19.3's 4 Hz analytic snapshot/UI target. Coalesce small segments asynchronously under a budget. These values are tunable; memory and latency budgets take precedence.

Compaction targets, so a long live session stays reopenable within §12's budget: at most TUNABLE: 64 live segments per time block and TUNABLE: 512 per session before compaction becomes mandatory rather than opportunistic; coalesce into outputs of at least 8 MiB or 64,000 rows; and require no more than TUNABLE: 128 segment opens to serve the initial viewport after reopen. Any periodic journal publication accumulates immutable chunks and small derived units during a long capture; compaction bounds that growth independently of whatever prepared cadence is eventually performance-qualified.

Interactive/query readers additionally keep a TUNABLE: 256 MiB per-store cache of **verified immutable segment readers**, charged what each reader holds: the columns and variable chunk it has read and the dictionaries it decoded (revision 161; before, the published byte lengths). A hit is valid only when the selected manifest still names the exact segment dependency and every exact dictionary dependency; a generation change prunes entries it can no longer reach, including evidence a retention publication released. Readers are admitted while they fit and served uncached once the cache is full; a reachable reader is never evicted to make room. What a cached reader holds grows as queries read more of it, so when a lease ends with the cache holding more than its budget, readers give back every column but session time and mechanism, one at a time until the charge fits; a released column is read and checked again when next asked for. Since revision 169 a reader also keeps values derived from its rows under the generation's derivation - each row's owner and channel binding, packed four bytes each - charged as columns are, and given back only when giving back the columns did not bring the charge within the budget: they are about a quarter of the columns a binding reads, and a brushed count, a focused timeline or an evidence page reads them instead of binding every row again. Every projection reads those two columns of every segment for its time tiles (S4), and under such scans a recency (LRU) policy would evict exactly the readers the next scan reads first; trimmed readers hold about nine bytes a row, so the budget keeps a reader for every segment of a session of tens of millions of rows. Pruning also frees room as compaction and retention replace segments. One-pass compaction and journal re-derivation deliberately bypass the cache. A cached reader is shared by concurrent queries, so anything it verifies lazily, such as a column's checksum on first read, is marked only after the check passes: another thread either sees the mark or checks the immutable bytes itself, and no thread reads a column it has not seen pass. The bound is per store, and a viewer holds one store per session: a live capture's writer store is also the store the window's queries read through. A viewer that keeps several sessions' stores open, so that returning to one hashes nothing again, lets only the session it opened last keep its readers; the others keep only what they verified. The 256 MiB figure bounds the payload readers hold, not CLR object overhead: decoded strings, indexes and object headers still count against §12's 512 MiB analysis/cache process budget and must be measured at the scale gates. This cache avoids repeated file reads and segment/dictionary integrity work for carried immutable segments; it **does not satisfy S4** and never makes a whole-session scan an acceptable interactive-open algorithm.

Revision 142 measured it on the saved 10-minute Explore session: generation 297 names two observation and two field segments and four dictionaries, 16.3 MiB of payload, beside 292 journal chunks. Opening every segment took 16.9 ms and allocated 16.4 MiB uncached, and 0.04 ms with nothing allocated from the cache. A whole overview projection with no derivation cached, as each live generation pays it, fell from a median of 106 ms and 34.9 MiB allocated to 56 ms and 18.5 MiB. The overview and every derivation were digest-identical either way. The cost is residence: those 16 MiB stay allocated between projections instead of being read again. A bounded 10-minute real-ETW capture (`bench/results/first-feedback-20260926T153533Z-10min-bounded`) met every budget with 44% more records than revision 129's (105,944 against 73,660): projection p50 44 ms and p95 81 ms against 53 and 85, event-to-visible p95 846 ms, exact p95 2.69 s. The viewer ended at 118 MiB private against 76 MiB; its session held 22.5 MiB of segment payload, which the cache keeps resident. A hit checks reachability by scanning the manifest's dependencies, as opening already did. Live compaction keeps a recording to about 64 small units, which bounds the scan; the dependencies are indexed by name only if a measurement at that bound shows the scan matters.

Revision 148 raised the budget from 64 to 256 MiB after measuring a million-row session, 169 MiB of payload. At 64 MiB the cache held one segment of four, so every warm query re-read and re-hashed the rest. At 256 MiB, warm projection fell from 315 to 127 ms, timeline detail from 158 to 29 ms and a group's focused count from 329 to 145 ms. In the window, a group level change fell from 681 to 350 ms and a brush's ranking from 337 to 182 ms (`bench/results/interaction-latency-20260926T173620Z`). Half of §12's analysis budget holds a million rows; beyond about 1.5M rows the cliff returns, and column-granular reads, not a larger bound, are the fix.

Revision 149 takes the first step toward column-granular reads: a reader that opens a segment by its header, its directories and its time column, and reads any other column only when a query first needs it. Such a reader never hashes the whole file, so every byte it interprets needs a checksum of its own. At minor 0 the column and time-block directories and the variable chunk had only the whole-file trailer. Segment-v1 minor 1 fills two words minor 0 reserved: the header's word at 124 carries the directories' CRC-32C, checked at open before an entry is interpreted, and a chunk-encoded column's word at 44 carries the chunk's, checked on that column's first read. A reader that predates minor 1 never read either word, so it reads minor-1 segments unchanged. A minor-0 segment is still verified by its trailer, and a reader never takes its zero words for checksums. Revision 148's build and revision 149's read a session imported by either build with identical rows. The remaining steps are the lazy reader itself and a cache that admits a reader by the bytes it holds rather than by its file's length. After them comes a decision: must a fresh store still hash every segment it names before the first view (store-v1 §3)? S1's open budget at scale depends on the answer.

Revision 150 made those checks cheap. The byte-table CRC-32C ran at 0.51 GiB/s, so checking a 42 MiB segment in full took about 80 ms, and every cache miss paid again for each column it read. It now takes eight bytes at a time through the processor's CRC-32C instruction (SSE4.2 or Arm64, via `BitOperations`), at 9.4 GiB/s, and every checksum is unchanged. At a million rows with a 64 MiB budget, where every query re-reads three segments of four, warm projection fell from 336 to 239 ms, timeline detail from 167 to 136 ms and a group's focused count from 355 to 254 ms. At the 256 MiB default a million rows stay cached, and the window-level benchmark is unchanged within noise (`bench/results/interaction-latency-20260926T180747Z`).

Revision 151 is the reader itself. A published minor-1 segment opens by its header, its directories and its time column, and reads every other column from the file when a query first asks for it. Each read opens the file, checks its recorded length, reads and closes it, so a reader holds no handle that retention would have to wait for, and the caller's evidence lease keeps the file in place. A minor-0 segment is still read whole and checked by its trailer. The queries that serve the Desktop read about 43% of a segment's bytes: with a million rows cached, readers hold 73 MiB of 169 MiB of files. With a 64 MiB budget, where every query re-reads three segments of four, warm projection fell from about 243 to 141 ms, timeline detail from 133 to 42 ms and a group's focused count from 257 to 172 ms. At the 256 MiB default, opening the million-row session in the window fell from 1,209 to 1,067 ms (`bench/results/interaction-latency-20260926T184644Z`). The cache still admits a reader by its file's length, the most it can come to hold, so the bound holds however many columns later queries read; admitting by what a reader holds, and growing its charge as it reads, would let the same budget keep about twice the rows.

Revision 152 settles the decision those steps led to: a viewer no longer hashes a session before its first view. S1 forbids an open that costs every byte of a session, and hashing did. A fresh open of the million-row session, 309 MiB of which 140 MiB is journal, spent about 420 ms hashing, which extrapolates to 3 s at T1's 2 GiB and 30 s at T2. A viewer now checks the pointer, the manifest's digest, and each dependency's presence and recorded length from one listing. It hashes at once only the small kinds that carry no checksum of their own. Segments, dictionaries and journals check what their readers read, so they are hashed after the first view, while the session is open. A file that fails its digest then fails its generation over to the last-known-good, as it would have at open. The window opens that generation in the newest one's place and says why. A first view that itself meets a changed file hashes the generation at once and opens the fallback directly. Stating the fallback also closed an older gap: a saved session whose newest generation did not verify opened on its last-known-good without a word. The open now takes 1 ms at a million rows and the hashing 0.2 s, and opening that session in the window fell from 1,067 to 729 ms (`bench/results/interaction-latency-20260926T190828Z`). Hashing now reads 1 MiB at a time, which also halved every open that still hashes: the command line's and a writer's.

A segment header records magic, format major/minor, feature flags, segment ID, derivation version, row count, min/max local time, column directory and checksum references. Column entries carry type, row count, byte offset, byte length and encoding. Unknown required encodings/features are refused. Uncompressed fixed-width columns permit direct mapping; compressed variable chunks use bounded decode buffers. Do not describe compressed columns as directly memory-mappable arrays.

`contracts/segment-v1.md` freezes those bytes and ADR-011 records what building them decided. Four rules belong in this section rather than in the contract, because they constrain any future segment format and not only this one. A column entry carries **availability counters** beside its null bitmap, and a reader checks them against the bitmap: a counter that disagrees with the data makes every denominator above it a guess. A **measurement slot and a measurement are separate facts** — a descriptor that exposes a byte field labels its domain, side and unit in every row whether or not that record supplied a value, and a descriptor that exposes none carries no labels at all, which is what lets an unknown be counted against a defined denominator instead of vanishing (R2, R3, §5). The **raw-record locator is carried in the row**, not implied by the row's position, so it survives the compaction this section requires. And a segment's bytes are a **function of its row set**: rows sort by native reading, then locator, then fact key, dictionary entries sort by their UTF-8 bytes, and a segment ID is derived from the capture, clock, derivation, ordinal, row count and interval rather than minted, so rebuilding a generation from the same evidence produces the same files. Which rows land in which segment follows acquisition order and is a property of the run, not a reproducible partition.

§10.2's dictionary budget needs a fallback, and the fallback is the variable chunk: a text column whose segment has more distinct values than a dictionary holds is stored as offset/length references instead, per column and per segment. A refusal would turn a machine with many distinct resource names into a machine InterCat cannot derive a session for, which is a ceiling rather than a bound.

**The status domain of §7.3 has no enumeration in §23 and is owed.** A status code is stored with its availability and nothing claims which domain it is in. Assigning codes without a source contract that fixes their meaning would put a guess inside a frozen format.

Journal batches use length-delimited framing, record count, first/last source IDs and checksums. Partial trailing batches are not committed. Blob references include file/chunk identity, offset and length and are verified against the same open file handle used to read them. Source locators survive sorted-segment compaction. `contracts/journal-v1.md` is the frozen framing; this section owns what is built on top of it and owns nothing inside it.

**A journal's lifetime is a decision this section owed, and ADR-010 makes it.** The journal is append-only and undictionaried by design, so it costs about 231 measured bytes per record where a normalized observation costs about 128 (§12). Keeping every batch for the life of a session nearly triples the session's storage; discarding a batch once its segment is durably published loses the ability to rebuild a segment from admitted evidence after a normalizer revision. ADR-010 retains the journal by default and makes releasing any part of it an explicit retention checkpoint that publishes the extent it released, because the default that loses information should be the one a user chooses rather than the one they get: the cost of keeping is a visible storage multiple, and the cost of discarding is an unbounded loss of the ability to answer a later question from evidence. Every published generation therefore carries the committed boundary it derives from - which journal, how many bytes and records of it were durable, and that prefix's digest - so a generation names its evidence instead of implying the whole file.

Commit sequence:

1. Write/flush a complete admitted journal batch and its committed-boundary record.
2. Write derived segments, required dictionaries and relation/entity revisions to unique staging files; validate and flush them.
3. Publish immutable final files, then write a new immutable manifest generation referencing only durable dependencies.
4. Update the current-generation pointer with `ReplaceFileW`, or `MoveFileExW` with write-through where a replace does not apply, after flushing the buffers of every dependency; retain the previous pointer and generation as last-known-good.
5. Announce the generation. Readers acquire it and its dependencies as one lease.

Do not assume a filesystem rename alone proves every dependency survived power failure. Windows offers no directory flush, so recovery must never depend on rename atomicity: the manifest carries its own checksum and complete dependency list, and the retained last-known-good pointer lets a torn publication be detected and rolled back rather than trusted. Recovery verifies manifests and checksums and falls back to the latest complete generation; replay committed journal batches to rebuild missing derived data. It reports omitted incomplete tails and orphan files. Opening reports staging files without deleting them: another process may own a completed but unpublished staged file. Cleanup requires explicit coordination proving no writer still owns them, under the verified root.

For a started live capture, the last generation additionally stages `capture-finalization-v1` after the owned capture session stop returns and before the manifest commits. Intermediate live generations never carry it. A terminal journal frame proves a chunk is complete, not that the capture ended; restart finality therefore depends on this explicit last-publication evidence (or the narrowly defined legacy compatibility path of §20.3).

New staging pairs the `stg-<id>.tmp` data with `stg-<id>.lease`, an exclusively held ownership marker created first. Completing the data closes its stream but not its ownership; a disposed stage cannot publish. Explicit cleanup previews the markers without deleting, then rechecks a digest of eligible names under the publication lock and removes only files whose owner marker is no longer held. A marker with no data may be removed after an interrupted rename, but the published orphan it once guarded is not. Pre-marker staging has no abandonment proof and remains untouched.

Opening and repair are different acts. A read reports rollback but changes nothing. Explicit pointer repair re-verifies the last-known-good under the publication lock, preserves bounded damaged pointer bytes before replacing current, and verifies the repaired pointer; later orphan manifests and dependencies remain for inspection. A generation number occupied by an orphan is skipped rather than reused, and the new manifest names the actual earlier verified predecessor. No recovery path promotes an unselected orphan simply because its number is newer.

### 20.2 Derived state, revisions and retention checkpoints

Raw/admitted evidence is authoritative; indices, tiles and correlation tables are versioned derivations. They can be rebuilt when their source and required schemas remain retained. Changing a decoder does not edit old columns in place. Keep an explicit dependency graph from a derived generation to raw chunks, schemas, dictionaries and algorithm versions.

The first rebuild path retains the compiled descriptor interpretation as well as the journal: `contracts/normalizer-plan-v1.md` freezes that dependency. `icat rederive` verifies the published journal boundary, streams checksummed batches against the saved schema and policy table, and replaces observation/source-field segments in one new manifest. It does not borrow current TDH metadata or the original ETL. A missing plan or another journal is a refusal, because either would risk a plausible but incomplete derivation. Algorithm-version upgrades remain a separate, semantic change with explicit tests of derived facts and stable raw IDs.

Retention state is disclosed before it bites: the UI shows the current session size, measured bytes per observation, the retained extent and the time remaining under the active policy and free disk (S5). Before rolling retention evicts an old interval, publish a boundary checkpoint containing still-live process/thread/resource identities, known endpoint bindings, continuity quality, clock state and pending-operation summaries (S6). Preserve supporting lifecycle evidence separately when exact provenance is required. A checkpoint preserves known state but does not prove activity in the removed interval or turn a missing start into an observed start. If provenance was deliberately evicted, label the retained summary accordingly.

Checkpoint pending calls/resources so an operation beginning before the retained window can still be represented as left-censored. Old raw traffic remains unavailable. Remove stale index/adjacency references and update the visible retention boundary atomically with the new manifest. Test reopen after eviction, not only uninterrupted live viewing.

Two rules come from implementing the mechanism beneath that checkpoint, and they hold for any retention this section later adds. **Releasing and removing are separate steps.** The generation stops naming a released file immediately, which is what makes the retention visible and atomic with the new manifest; the bytes go when the last reader that acquired them lets go, and a file a live lease still holds is reported as awaiting release rather than removed behind the reader (I18, S6). **A retention that released anything re-aims the retained last-known-good pointer at the retention generation.** The earlier generation is missing a file, now or as soon as a lease lets go, so a pointer that still named it would promise a rollback that cannot be performed - which is worse than naming no earlier generation at all. For the same reason, the sweep of §20.1 treats the manifest and dependencies of *both* pointers as referenced: a sweep that called the last-known-good unreferenced would delete the one thing a rollback needs.

The first rule also applies across processes. A reader opens the root-owned lease guard for shared reading before it verifies a pointer, and retention cannot physically delete released files until it can open that guard exclusively. A reader needs no write access to the guard; the session writer creates it before first publication. A global guard can delay unrelated cleanup, which is a recoverable cost, while deleting one dependency under an open reader is not. Process exit releases the OS hold, so this is a lifetime guarantee for an open lease, not a durable pin across restart or a quota reservation.

**A journal release works in whole batches.** A frame's checksum covers the records it holds, so releasing part of a batch would publish a frame whose digest describes records that are no longer in it. The granularity a session can retain at is therefore the batch size its capture or import declared, a boundary inside a batch releases nothing and says which boundaries the journal does allow, and a release that would leave no admitted evidence at all is refused because ADR-010 keeps a journal by default.

**What a retention record may not yet contain is as much a decision as what it contains.** The still-live process, thread and resource identities, the endpoint bindings, the continuity quality and the pending-operation summaries this section requires are derived from the entity and operation revisions IC-015 owns. Until those exist, a checkpoint carries what was released and no fields nothing can fill: a published field that is structurally present and always empty is a worse contract than an absent one, because a reader cannot tell it from a genuinely empty interval.

Revision 162's derivation checkpoint (`contracts/derivation-checkpoint-v1.md`) is not this boundary checkpoint, though it is the natural source of one. It holds the process instances and endpoint state derived from the segments a generation names, so a reopen need not derive them again (§12.1 S1). A retention that releases a segment releases it too, because it describes what was released. IC-016a's boundary checkpoint must instead keep the still-live identities and endpoint bindings across the release. Those are a subset of the state the derivation checkpoint holds, taken at the boundary, and they are what IC-016a should publish rather than a second, separately derived structure.

Default interactive queries have short leases. Long-running exports or user pins reserve a declared disk allowance. If quotas conflict, stop recording with an explicit reason or require an explicit eviction/unpin decision; never invalidate evidence behind an open inspector. Disk-reserve space for final health/manifest writes must be included in quota planning.

### 20.3 Broker protocol and failure semantics

Use a versioned length-prefixed binary protocol with a small reviewed schema, not unrestricted object deserialization. Separate control/status from event-journal access. Initial commands:

```text
Hello(protocol range, client instance, negotiated features)
GetCapabilities()
PrepareCapture(profile ID, approved overrides, quota, retention policy)
StartCapture(prepared plan token, request ID)
GetStatus(capture ID)
StopCapture(capture ID, request ID)
RenewOwnerLease(capture ID)
```

The v1 frame has a fixed 32-byte header and a payload capped at 64 KiB before allocation. Typed TLV payloads cap fields at 64 and each value at 16 KiB; duplicate IDs, invalid widths/UTF-8/list counts, unknown required fields and truncated input are refused. Unknown optional fields are skipped and still consume the count/byte budget. Hello distinguishes requested optional feature bits from required bits. Typed request and response codecs cover all seven commands, user-facing protocol errors, capabilities, exact effective Prepare scope and lifecycle/stop outcomes. A per-connection dispatcher requires Hello, preserves correlations and rejects a duplicate while its first command is in flight. Duration, journal/free-space limits, retention and the named journal-publication policy are frozen into the prepared digest and returned in the effective summary, the policy together with its compiled interval (revision 78). The dispatcher accepts only an identity the transport authenticated from an OS token; the first-instance local pipe of §20.3 supplies it. The executable is composed (revision 80), qualified with real ETW (revisions 81-83) and launched by clients through `WindowsBrokerLauncher` (revision 84).

`PrepareCapture` accepts only the profile ID and bounded typed overrides, quota and retention policy. It never accepts a serialized effective plan, provider settings, event IDs, body offsets or schema fingerprints from the ordinary-integrity client. The broker discovers capabilities and compiles the effective plan locally from its installed allowlist. It returns the exact effective summary and deterministic prepared-plan digest; if either differs from the preview the user reviewed, the client blocks start and returns to review rather than accepting a fallback. The digest is plan identity only, not authentication or a prepared token. `contracts/broker-v1.md` freezes the canonical prepare handoff and records which transport/ownership pieces remain unimplemented.

The broker authenticates the local OS identity and connection ownership as follows; a supplied PID or random nonce is never accepted as authentication:

- The broker creates the control pipe with first-instance semantics and an explicit DACL granting only the capturing user's SID and the broker identity, and sets the pipe to reject remote clients.
- On each connection it impersonates the named-pipe client, reads the client token, and verifies SID, logon session, integrity level and elevation state against the capture owner before reverting.
- Prepared plan tokens bind to that authenticated SID and logon session with an expiry. A token presented by another client, or after expiry, is rejected.
- The client authenticates the broker too. It launches the broker with a retained process handle and, after connecting, requires `GetNamedPipeServerProcessId` to name that process; any other server - a process that claimed the pipe name first - is refused before Hello is sent. First-instance creation protects only the broker side.
- A client PID is diagnostic information only, never an authorization input (R22). Bind prepared tokens to that authenticated client/session and an expiry. Ordinary UI elevation can involve a different administrative account; account for the requesting user's read access explicitly, and never rely on permissive default ACLs. Reject remote clients and unsupported commands/versions.

Use idempotent start/stop request IDs and persist the capture ownership record before exposing success. Duplicate start returns the existing capture, not a second ETW session. Disconnect does not imply successful stop. Stop returns separate milestones for requested, providers stopped, callbacks drained, journal finalized and analysis finalized. If draining exceeds a timeout, preserve a partial session and explain the incomplete tail; do not forcibly claim finalization.

The lifecycle core records a durable intent before calling the capture runtime and records completion before returning success. Reusing a request ID for a different command or target is a conflict. Replaying the same completed request returns the stored result; replaying its partial stop does not relabel it as complete. A new stop request may retry incomplete finalization. Interactive owner leases cannot be revived after expiry, and a sweeper requests stop for expired captures. The ownership log uses bounded append-only checksummed snapshots and flush-to-device; recovery keeps the last complete valid frame, refuses a wholly invalid nonempty store, stops interrupted starts and expired owners using the persisted ownership token, resumes partial stops, and requests stop for every active recording after a broker restart even if its old owner lease has not expired: the new process cannot inherit the prior process's ETW handle. An incomplete cleanup remains a visible retryable partial stop while a retry can still prove something (an ETW session not yet proven stopped, staging a live writer still owns). Once the session is proven stopped and the recording process is gone, recovery releases the abandoned staging and closes the capture with its partial milestones and an `Interrupted` reason; it is not retried again.

A restarted runtime never infers capture finality from an absent ETW session or a terminal journal alone. New live recordings commit `capture-finalization-v1` only with their last publication. Recovery opens the already-existing protected capture directory, verifies the current manifest and dependencies, requires the manifest's source identity to equal the durable prepared-plan digest, verifies the marker's capture ID, binds the committed boundary to the named journal dependency, and replays that journal through its terminal while matching the boundary's committed record count. Only that verified publication may promote `JournalFinalized`; a marker whose `callbacksDrained` member is true may also recover that milestone. Provider stop remains a separate exact-owned-session check. A verified `CoverageLedger` is accepted only as a legacy last-publication signal for captures written before the finalization contract, and does not prove callback drain. Recovery opening a missing capture directory is a failure and never creates an empty directory from which to infer evidence.

Compaction publishes the current ownership snapshot as the single frame of a new generation and replaces the live log in one directory operation beneath the validated root, so a restart finds either the whole previous log - discarding a temporary it already contains - or the whole new one. One file is one generation, and a frame from another is rejected as the tail even when its own sequence and checksum are valid. An append that would pass the size bound compacts first and only then refuses, so the bound is maintenance rather than a stop. Compaction reclaims superseded snapshots and never records: dropping a completed request would turn its idempotent replay into a second capture, so the record-count bounds stay the retention policy and a retention rule for records that outlive their usefulness is still owed.

`Recording`, `Stopping`, etc. describe lifecycle; degradation is orthogonal status with a reason list. This avoids impossible transitions such as a degraded recorder no longer knowing whether it is recording. Health/status messages must still function when data queues are full. Orphan recovery verifies ownership plus session metadata before cleaning up; a stored matching name alone is insufficient.

### 20.4 CLI, files and reproducible headless operation

The shipped CLI (revision 106) addresses a session as a directory, and `icat help` lists every command:

```text
icat capabilities --json
icat capture <new-session-dir> --profile explore --duration 60
icat import source.etl --into <new-session-dir>
icat session | overview | channels | evidence | timeline | processes | metric <session-dir> [--json]
icat raw <session-dir> --session-id <id> --generation <n> --segment <name> --row <n>
icat export <session-dir> --output view.json [--at <row-key>]... [--evidence] [--share-redacted]
icat package <session-dir> --redacted --output <new-dir> [--check]
icat rederive | compact | retain | recover | staging <session-dir> ...
```

Still proposals, to be frozen with M4's workspace: `icat query <session> --spec analysis.json --format jsonl` over the
full analysis specification, `icat workspace create` over several sessions, and a workspace-level export. The earlier
single-file `capture.icat` form was superseded by the directory store of §20.1.

The JSON analysis specification uses the same versioned AST/metric contracts as the UI; no separate approximate CLI interpretation. Status/progress goes to stderr and machine-readable data to stdout. Return distinct documented exit codes for success, partial-result success, invalid invocation, permission/capability failure, corrupted input and cancellation. Machine-readable status includes actual effective profile, capture IDs and quality/loss summary.

Do not overwrite existing outputs without an explicit overwrite option. Save/export stages in the destination filesystem and publishes only a verified result. Cancel leaves either the previous destination intact or a clearly labeled recoverable partial capture. Archive extraction validates paths, links and quotas before publication. Imported manifests cannot start capture or request elevation merely by being opened.

### 20.5 Deployment and compatibility boundaries

Installation should pre-create the broker-owned root with its final security, because the default ACL of `%ProgramData%` lets any user create an entry there first. A broker that finds the root owned by an untrusted principal refuses rather than adopting or repairing it, so squatting is a clear failure to act on and never a silently weakened boundary; on-demand elevation without an installer therefore carries that one first-run failure mode and must state it. Package the viewer/CLI and capture broker with a version compatibility handshake. A viewer opening a session requires neither an installed service nor a driver. On-demand broker elevation is the baseline. Updates do not restart a running broker/capture silently; negotiate stop/finalize or defer the update. Future service/driver components use explicit separate installation choices and rollback paths.

Revision 88: a normal framework-dependent Windows Desktop or CLI build and publish stages the complete separate broker output beside the client, including its apphost, deps/runtime configuration and native subdirectories. Staging is shared by both clients so a first run does not need a manual copy or a development-only path override. A self-contained client publish is refused until a matching self-contained broker package exists; otherwise a seemingly self-contained download could fail only when the user approves capture. Staging is not an installer or a version-compatibility handshake, and it does not remove the protected-root pre-creation, real Desktop/UAC qualification or update/rollback gates above.

Format major changes require migration into a new destination; preserve the original. Minor additions are ignorable only when marked optional. Use a feature-bit/required-feature list rather than assuming every future minor version is readable. Store canonical JSON values for public interchange where numbers exceed consumer-safe integer ranges, with explicit string encodings for large IDs/timestamps.

Create CI lanes for deterministic pure tests, Windows ETW integration, elevated VM scenarios, visual/accessibility tests, fixture compatibility, performance qualification and later driver validation. Routine pull requests need not run every 100M-event or multi-day test, but those tests gate releases claiming that tier. Keep test results tied to exact build/profile/schema versions.

### 20.6 Errors, defect counters and diagnostics

Error categories, each with its own presentation and its own counter. Presentation is a designed state (§6.8), not a dialog, except where the user must decide something:

| Category | Meaning | Presentation |
|---|---|---|
| `UserInput` | An invalid filter, specification or invocation | Inline beside the input, with the accepted form |
| `Capability` | A source cannot supply what was asked | A coverage entry plus an `EN-CapabilityState` value; never an error dialog |
| `Permission` | Elevation refused, protected process, denied ACL | A designed state naming what remains possible |
| `SourceLoss` | Provider, ETW, broker or application drop | The health ledger, attributed to the layer that lost it |
| `Decode` | Unknown or mismatched schema, malformed field | An opaque or header-only record plus a counter; never a guessed layout |
| `Storage` | Corruption, checksum mismatch, disk full, slow disk | A recoverable-partial state naming the last valid generation |
| `Protocol` | Broker or agent version, token or command rejection | A refusal stating the negotiated range |
| `Internal` | A violated invariant | Fail loudly in debug; contain and report in release, naming the rule or invariant ID |

Defect counters are session statistics, written to the manifest and surfaced in the health strip: admitted records; policy omissions by reason; undecodable records by reason; provider-reported loss; ETW buffer loss; broker drops; application drops; unresolved-join evictions; quarantined timestamps; dictionary, cache and spill evictions; and capability transitions per source. A counter is never folded into another, and no total is presented that adds overlapping counters (§9.3).

A counter that could not be read is unknown, not zero. When a source refuses its own counters — a session whose loss counters cannot be read at stop, a host without a per-thread processor clock — the quantity is recorded as unmeasured with the reason, the health strip shows it as unknown, and no session carrying one may be called loss-free. Reporting a failed read as a zero would turn a diagnostic failure into a clean result, which is the one direction an error must never take (R3, R21).

Structured diagnostics obey R11: no logging on the callback, decode, aggregate or paint path — counters instead. The support bundle contains manifests, capability reports, counters, versions and timings, and by default contains no payloads, endpoint strings, command lines or raw events (P16). Its contents are listed before it is written.

## 21. Executable acceptance specification and review findings

### 21.1 Small scenarios with exact expected results

These examples specify accounting and failure semantics before optimization. Each becomes a fixture plus a reference-query assertion; do not encode the examples only as UI screenshots.

| Fixture | Expected result |
|---|---|
| A sends 100 known-domain bytes to B; B receives the same transfer; one proven logical call is associated | Source basis: 2 transport observations. Sender-accounted transport total: 100 bytes. Endpoint activity: A sent 100, B received 100; sum of endpoint activity 200, labeled accordingly. Logical basis: 1 call. Source associations add no bytes. |
| A requests a 4,096-byte pipe write and a validated completion reports 1,024 bytes | Requested-byte metric 4,096; completed-byte metric 1,024; content may still be unavailable. No generic unqualified 4,096-byte transfer claim. |
| Two processes map the same 8 MiB section | 1 section, 2 memberships, capacity 8 MiB; no observed traffic count or byte rate unless another source supplies it. |
| Two server instances share a pipe name and one client name event lacks instance identity | One endpoint grouping with distinct known server instances and an unresolved client relation; no fabricated choice or all-to-all edges. |
| PID 400 exits, is reused, and a late event belongs to the earlier start key | The late event binds to the earlier process instance — by its start key when it carries one, and otherwise by its reading inside the earlier lifetime — and the newer instance's totals do not change. |
| Two same-time, byte-identical imported records | Preserve multiplicity 2; repeated canonical import yields stable equivalent facts; neither record receives invented causal priority. |
| An operation starts at 0.5 s and ends at 2.5 s; buckets are [0,1), [1,2), [2,3) | Start counts 1/0/0; completion counts 0/0/1; overlap counts 1/1/1; occupied durations 0.5/1/0.5 s. |
| A 10-second window contains 100 observed operations and a 2-second loss interval | Observed rate 10/s with incomplete coverage; no automatic 12.5/s “corrected” whole-window rate and no invented missing count. |
| Host B appears to receive 0.2 ms before host A sends, with combined uncertainty 3 ms | Cross-host order is ambiguous; no negative network-latency diagnosis; valid local spans are unchanged. |
| Metadata-only profile encounters a new event schema with a binary body | Body not persisted; permitted diagnostic header and policy-omission counter visible; no payload preview or silent coverage claim. |
| Recording crosses retention boundary while a resource is still open | Reopen knows the checkpointed resource's qualified identity; pre-boundary traffic is unavailable; lifetime does not become a new observed creation. |
| A slow old query completes after a new filter request | It cannot replace the applied new bundle or attach its graph layout to a different graph. |
| The writer crashes after raw-batch commit but before derived-manifest publication | Recovery replays the committed batch once into new derived data, preserving raw IDs and reporting any uncommitted tail. |

Metric examples with both endpoints assume a proven transfer association; without that proof, show side-specific observations and unresolved aggregate accounting as specified in section 19.2.

### 21.2 Concrete M0/M1 closure artifacts

Produce each contract below before its governed production format or behavior is frozen or promoted beyond an explicitly labeled spike. M0 feasibility probes and disposable prototypes may precede a contract, but they cannot become the production implementation until the applicable contract and fixture exist. This removes a sequencing contradiction with IC-011–IC-018 while preserving the rule that a contract must not merely document an accidental shipped design:

IC-009 therefore owns the minimum disposable version-0 capture envelope required to compare real ETW callbacks with ETL, including arbitrary extended items and owned callback-lifetime copies. It may be checksummed and replayable for measurement, but it is not `RecordEnvelopeV1`, is never opened as an `.icat` session, and has no compatibility promise. IC-011 begins only after ADR-008 accepts an authority direction; it turns the measured choice into `journal-v1` and the production pooled implementation. This experimental/production split prevents the old cycle in which IC-009 required fidelity evidence that only its dependent IC-011 was allowed to implement.

1. `capabilities/<build>/<adapter>.json`: supported descriptors, exact fields, units, attribution, enablement and profile admission rules, with links to fixtures.
2. `contracts/journal-v1.md`: complete framing, bounds, checksum, extended-data and replay contract; golden binary files and corruption tests.
3. `contracts/identity-v1.md`: live/import/subrecord IDs, process/resource epochs, alias revisions and canonical ETL tie handling.
4. `contracts/metrics-v1.md`: contribution keys, domains, accounting sides, cohorts, unknown values, grouping and the scenarios above.
4b. `contracts/entities-v1.md`: how process instances are derived from a capture's lifecycle records, how each is keyed, and the rule and strengths by which a record binds to one.
5. `contracts/broker-v1.md`: message framing, the authentication mechanism of §20.3 and its assumptions, ownership, idempotency, states, timeout behavior, and a threat model naming the assets, the boundary, the assumed attacker positions and the abuse cases each command rejects.
6. `contracts/store-v1.md`: the session root, manifest and pointer formats, the commit sequence and exclusive publication guard, what a reader acquires, what recovery rolls back, and why staging cleanup cannot run on open.
6b. `contracts/capture-finalization-v1.md`: the explicit last-publication marker, its bounded stop-milestone evidence, and the restart checks required before broker recovery may promote journal/callback finalization.
7. `contracts/import-v1.md`: source and import identity, the canonical record key and its exclusions, the ordering an import may claim, equal-time multiplicity, the bounded spill and cancellation path, and the refusals.
8. `fixtures/`: truth logs, admitted journals/ETLs where shareable, expected query bundles and exact tool/build provenance.
9. `bench/results/`: journal-vs-ETL fidelity/overhead, queue/disk saturation, mixed IPC query timings, the end-to-end latency budget of §12 measured stage by stage, and the chosen values for every `TUNABLE:` setting exercised.
10. `contracts/query-identity-v1.md`: the canonical specification form, hash construction and the golden corpus mapping specifications to canonical bytes and hashes (§10.5).
10b. `contracts/segment-v1.md`: the derived segment and dictionary bytes of §20.1 — header, column directory, encodings, null bitmaps and availability counters, time-block metadata, the raw-record locator, the canonical row and dictionary order, and what a reader verifies and when.
10c. `contracts/normalizer-plan-v1.md`: the immutable descriptor interpretation retained beside a journal so a later generation can re-derive from evidence without consulting a changed machine schema.
11. `theme/`: the theme definitions of §6.6 with recorded contrast ratios, perceptual separations and color-vision verification output, plus the tests that fail when one regresses.
12. `fixtures/index.json`: the traceability matrix of §13.5 and the two CI checks that keep it honest.

These paths are deliverables in the new InterCat repository, not files created by this planning task. IC-011–IC-018 cover them: complete foundation portions in M1 and their live/presentation integration in M2. M1's exit requires the corresponding contracts and foundation fixtures. Avoid implementing all future adapters before the first durable vertical slice.

### 21.3 Reanalysis disposition

| Review finding | Resolution in revision 2 |
|---|---|
| “Implementation-ready” overstated untested capture feasibility | Status now distinguishes a concrete blueprint from gated runtime capabilities |
| Competing journal/ETL authorities could change live IDs or duplicate records | One authoritative admitted-event journal selected; companion ETL is separate evidence |
| Callback ordinal was assumed reproducible for ETL | Canonical import handles equal-time ordering and preserves duplicate multiplicity |
| One raw record could produce multiple observations without unique IDs | Raw-record identity and deterministic fact keys are separate |
| Immutable observations included mutable resolved identities | Entity bindings moved to versioned derived tables |
| Metadata-only and unknown raw preservation conflicted | Explicit source/body admission matrix and separate original-evidence policy |
| Broker, schemas, recovery and retention were principles without sufficient contracts | Added wire commands, owned envelopes, schema replay, commit order and boundary checkpoints |
| Graph filtering could change denominators or hide counterpart nodes | Shared basis/contribution model and distinct context-node semantics |
| CPU/GPU responsiveness lacked a concrete scheduling path | Added coalescing scheduler, revision-aware caches and separate numeric/layout/paint results |
| Self-observation and enrichment could perturb exploration | Explicit collector identities and opt-in network/name/symbol enrichment |
| Milestone acceptance was broad | Added exact scenarios and required M0/M1 contract artifacts |

Remaining uncertainty is principally empirical: NPFS/section coverage, provider semantics on target builds, journal throughput, instrumentation overhead and graph usability at scale. Resolve those through the stated fixtures/prototype, not by adding speculative certainty to the plan. No user-level conceptual decision needs reopening to implement these revisions.

## 22. Terminology and reference notes

| Term | Meaning |
|---|---|
| Observation | An immutable fact recorded by one source record, preserving original attribution and measurements |
| Operation | A derived logical message, I/O or call lifecycle, with optional start, end and status |
| Channel | A scoped communication relationship through a resource or connection incarnation |
| Resource | An object through which communication may occur: pipe instance, section, port, socket |
| Endpoint | A mechanism-specific address or name, with namespace, host, scope and observed validity |
| Relation | An evidence-backed link carrying its rule identity, version, evidence IDs and strength |
| Snapshot | An immutable, identified analysis state, named by a generation per capture |
| Snapshot vector | One generation per capture in an investigation; a query answers exactly one vector |
| Generation | A published, immutable version of a session's derived state |
| Revision | A versioned derived binding or relation set layered over unchanged observations |
| Epoch | A continuity interval for an identifier or a capture configuration, after which reuse is non-merging |
| Coverage | What the configured sources could observe during a period, including known defects |
| Coverage epoch | An interval over which capture configuration and admitted sources were unchanged |
| Correlation | An evidence-backed link produced by a named rule with a stated ambiguity policy |
| Basis | The fact family a query counts: source observations, logical operations, or resource topology |
| Contribution | The deduplicated accounting unit keyed by basis identity, metric domain and observation side |
| Eligible | Passing the analysis specification's filter, basis, evidence policy and time scope together |
| Accounting side | On a stored contribution, which end of the exchange its measurement describes; on a request, the rule deciding which contributions a total takes, including the canonical owner (§5.3) |
| Byte domain | The named meaning of a byte measurement; two domains are never summed |
| Canonical owner | The single contribution that owns a matched transfer's total (§5.3) |
| Endpoint activity | `EndpointActivityBytes`: a metric that counts both sides of a transfer by design, and says so |
| Occupancy | Clipped time an interval occupies inside a cell, distinct from a start or completion count |
| Censored | An operation open at a capture or retention boundary, excluded from completed cohorts |
| Cohort | The population a duration distribution describes, for example operations completed in range |
| Payload | Content whose semantic classification is known, distinct from generic event data |
| Admitted evidence | What InterCat retained after applying admission policy, which is not every OS buffer |
| Admission policy | The per-record, pre-persistence decision about bodies and content |
| Journal | The broker-owned, append-only authoritative record of admitted events |
| Segment | An immutable, time-sorted set of columnar observations and their indices |
| Tile | A cached multiresolution aggregate for a common lane and metric view |
| Watermark | A reordering optimization boundary; never evidence of source completeness |
| Viewport | The visible half-open time range |
| Analysis interval | The half-open range that scopes graph, ranking and detail results |
| Retained extent | The time range still held by the session after retention |
| Context node | A node drawn to explain a selected relation, contributing to no total |
| Follow-latest | A view state that tracks newly committed data, independent of recording |
| Workspace clock | The investigation-wide time base each host's clock maps into, with uncertainty |
| Tier | A mechanism's measured support level, from traffic visualization to unsupported (§14.2) |
| Collector activity | Observations produced by InterCat's own processes and channels |

Public technical references are linked beside the claims they support. They establish Windows mechanisms and API contracts, not that an InterCat adapter has been tested. Local RPC metadata inspection establishes only the reported single-build schema. The rules and invariants in section 2 are stated on their own merits, with the failure each prevents; no external benchmark is carried into this document as evidence about InterCat.

All throughput numbers and delivery budgets in this document are proposed targets. All unsupported or experimental Windows paths require their stated feasibility gates. The design's central invariant is that every displayed relationship, measurement and content preview can be traced to evidence and interpreted at its actual level of certainty.


## 23. Normative enumerations and wire codes

Every enumeration below is a contract (R5). Codes are stable and never reused; retiring a value leaves a gap. A reader encountering an unknown code in a **required** field refuses the artifact and reports the version axis responsible; in an **optional** field it preserves the raw code and renders it as unknown rather than guessing. Names are the identifiers used in the CLI, the canonical specification form and the JSON interchange; codes are what the durable format stores.

**`EN-Mechanism`** — address family (IPv4/IPv6) is an endpoint attribute, not a separate mechanism.

| Code | Name | Fixture prefix |
|---|---|---|
| 1 | `ProcessLifecycle` | `PROC` |
| 2 | `ThreadLifecycle` | `PROC` |
| 3 | `Tcp` | `TCP` |
| 4 | `Udp` | `UDP` |
| 5 | `UnixDomainSocket` | `UDS` |
| 6 | `NamedPipe` | `PIPE` |
| 7 | `AnonymousPipe` | `APIPE` |
| 8 | `Rpc` | `RPC` |
| 9 | `Alpc` | `ALPC` |
| 10 | `SharedSection` | `SECT` |
| 11 | `ComActivation` | `COM` |
| 12 | `Synchronization` | `SYNC` |
| 13 | `WindowMessage` | `WMSG` |
| 14 | `Clipboard` | `CLIP` |
| 15 | `Mailslot` | `MSLOT` |
| 16 | `Dde` | `DDE` |
| 17 | `RemoteFileOrSmb` | `SMB` |
| 18 | `Quic` | `QUIC` |
| 19 | `ApplicationSdk` | `SDK` |
| 20 | `Instrumented` | `INST` |
| 21 | `Http` | `HTTP` |
| 99 | `UnknownMechanism` | — |

**`EN-Layer`**: 1 `Transport`, 2 `Application`, 3 `Resource`, 4 `Lifecycle`, 5 `Collector`.

**`EN-ObservationKind`**: 1 `Send`, 2 `Receive`, 3 `RequestStart`, 4 `RequestEnd`, 5 `Open`, 6 `Close`, 7 `Bind`, 8 `Connect`, 9 `Accept`, 10 `Disconnect`, 11 `Map`, 12 `Unmap`, 13 `Wait`, 14 `Signal`, 15 `Create`, 16 `Exit`, 17 `Inventory`, 18 `Error`, 19 `Discovery`, 99 `UnknownKind`.

**`EN-Direction`**: 0 `UnknownDirection`, 1 `Outbound`, 2 `Inbound`, 3 `Bidirectional`, 4 `DirectionNotApplicable`. Initiator and responder roles are separate attributes; a server commonly sends data. The code is the source catalog's, fixed per event descriptor. The Windows catalog marks sends, retransmits, connection attempts, RPC client calls and pipe writes `Outbound`, and receives, accepts, RPC server calls and pipe reads `Inbound`. Lifecycle records, disconnects and pipe opens, closes and operation ends are `DirectionNotApplicable`. So a connect's `Outbound` is the direction of its connection request, not data: `EN-BetweenDirection`'s data direction reads the observation kind (send or receive) and keeps a connect only under `Either`. No current source emits `Bidirectional` or `UnknownDirection`; both keep their codes and their L2 rows (§3.2) so that a future source's records are never folded into another direction.

**`EN-Basis`**: 1 `SourceObservations`, 2 `LogicalOperations`, 3 `ResourceTopology`.

**`EN-Metric`**: 1 `Observations`, 2 `OperationsStarted`, 3 `OperationsCompleted`, 4 `BytesSent`, 5 `BytesReceived`, 6 `RequestedIoBytes`, 7 `ApplicationPayloadBytes`, 8 `CapturedContentBytes`, 9 `Rate`, 10 `Duration`, 11 `ActiveChannels`, 12 `ActivePeers`, 13 `MappingCapacity`, 14 `Errors`, 15 `EndpointActivityBytes`. Permitted combinations with basis, byte domain and accounting side are fixed by §5.3. Code 15 was added by revision 28 (ADR-012); `EN-AccountingSide` 3 keeps its meaning as that metric's fixed side and as the label of a stored contribution whose source states no direction.

**`EN-ByteDomain`**: 1 `TransportObserved`, 2 `RequestedIo`, 3 `CompletedIo`, 4 `ApplicationPayload`, 5 `CapturedContent`, 6 `Capacity`.

**`EN-AccountingSide`**: 1 `SendSide`, 2 `ReceiveSide`, 3 `EndpointActivity`, 4 `CanonicalOwner`.

**`EN-EvidencePolicy`**: 1 `DirectOnly`, 2 `IncludeCorrelated` (default), 3 `IncludeCandidates`, 4 `AllIncludingConflicting`. Definitive causal views permit 1 and 2 only.

**`EN-GraphProjection`**: 1 `ProcessToProcess`, 2 `ProcessResourceProcess`, 3 `HostToHost`, 4 `EndpointCentric`.

**`EN-Grouping`**: 1 `InstanceOnly`, 2 `Executable`, 3 `ServiceContainer`, 4 `UserSession`, 5 `Host`, 6 `Mechanism`, 7 `Endpoint`, 8 `Package`, 9 `Peer` (the processes at the other end from one focused process; it needs a process focus).

**`EN-SourceField`**: 1 `ProcessStartSequence`, 2 `ProcessCreateTime`, 3 `ParentProcessId`, 4 `ParentStartSequence`, 5 `ProcessExitTime`, 6 `ProcessSessionId`, 7 `ConnectionId`, 8 `IoRequestPacket`, 9 `FileObject`, 10 `FileKey`, 11 `IssuingThreadId`, 12 `RpcProcedureNumber`, 13 `RpcProtocolSequence`, 14 `FileByteOffset`, 15 `AlpcMessageId`, 16 `HttpExchangeId`, 17 `ContentBufferSequence`, 18 `ContentBufferFlags`. These are meanings assigned to admitted source fields, not provider field names. They are immutable rows of `source-fields-v1`, joined to an observation by raw locator and fact key; no resolved identity is stored in them.

**`EN-TimeScope`**: 1 `AnalysisInterval` (default), 2 `RetainedCapture`, 3 `VisibleViewport`.

**`EN-QualityDimension`**: 1 `Attribution`, 2 `Correlation`, 3 `Measurement`, 4 `Timing`.

**`EN-QualityLevel`** — ordered worst-last; roll-ups take the worst (§10.3): 1 `Proven`, 2 `Qualified`, 3 `Weak`, 4 `UnknownQuality`.

**`EN-CoverageState`** — ordered lattice, worst-last: 1 `Covered`, 2 `ReducedFidelity`, 3 `PartialGap`, 4 `NotCollected`, 5 `UnknownCoverage`.

**`EN-FieldAvailability`**: 1 `Present`, 2 `NotExposed`, 3 `ProfileDisabled`, 4 `Denied`, 5 `EventLost`, 6 `SchemaUnknown`, 7 `Redacted`, 8 `NotApplicable`.

**`EN-CapabilityState`**: 1 `Available`, 2 `Experimental`, 3 `Unsupported`, 4 `PermissionDenied`, 5 `DisabledByProfile`, 6 `SchemaUnknown`, 7 `ProviderFailed`.

**`EN-Tier`**: 1 `TrafficVisualization`, 2 `TopologyOnly`, 3 `ExperimentalEvidence`, 4 `Unsupported` (§14.2).

**`EN-RelationStrength`**: 1 `Direct`, 2 `Correlated`, 3 `Candidate`, 4 `Unresolved`, 5 `Conflicting`.

**`EN-OperationState`**: 1 `Started`, 2 `Completed`, 3 `ExplicitlyFailed`, 4 `OrphanCompletion`, 5 `OpenAtBoundary`, 6 `Ambiguous`, 7 `EvictedUnresolved`.

**`EN-ContentClassification`**: 1 `OpaqueProviderData`, 2 `TransportFragment`, 3 `ApplicationPayload`, 4 `DecodedFields`, 5 `EncryptedContent`.

**`EN-AdmissionMode`**: 1 `MetadataOnly`, 2 `ScopedContent`, 3 `OriginalEvidence` (§18.2).

**`EN-CaptureLifecycle`**: 1 `Idle`, 2 `Probing`, 3 `Starting`, 4 `Recording`, 5 `Stopping`, 6 `Finalizing`, 7 `Closed`. Degradation is orthogonal status, not a state: `Degraded` and `RecoverablePartial` are flags with a reason list (§20.3).

**`EN-AlignmentMode`**: 1 `RecordedWallClock`, 2 `SharedMarkerEvidence`, 3 `ManualAnnotation` (§8.2).

**`EN-ExitCode`** for the CLI (§20.4): 0 `Success`, 1 `PartialResultSuccess`, 2 `InvalidInvocation`, 3 `PermissionOrCapabilityFailure`, 4 `CorruptedInput`, 5 `Cancelled`.

**`EN-FilterDimension`**: 1 `Host`, 2 `ProcessInstance`, 3 `Executable`, 4 `Mechanism`, 5 `Layer`, 6 `Endpoint`, 7 `Direction`, 8 `Operation`, 9 `Status`, 10 `Quality`, 11 `Source`, 12 `ByteValue`. The codes order a canonical filter's terms (§10.5). A peer is a member of the process-instance term, relative to its focus (§19.1), and time is the specification's `timeScope`; neither is an independent term. `between(A,B)` is the process-instance term's other form.

**`EN-DurationInterval`**: 1 `ClientCall`, 2 `ServerExecution`, 3 `IoCompletion`, 4 `AlpcSendToReceive`, 5 `Wait`, 6 `MappingLifetime`. The named interval a duration measures (§5); intervals of different names are never interchangeable, and a request names one, with no default. A mapping lifetime is a resource's, measured on the resource-topology basis; the others are operations' (ADR-033).

**`EN-Cohort`**: 1 `CompletedInRange` (default), 2 `StartedInRange`. Which operations a duration describes (§19.2): those whose completion, or whose start, is in scope.

**`EN-DurationStatistic`**: 1 `Median` (default), 2 `Percentile95`, 3 `Maximum`. The number a duration answer and its ranking read from a distribution, each a duration one operation took, by nearest rank.

**`EN-BetweenDirection`**: 1 `Either`, 2 `FirstToSecond`, 3 `SecondToFirst`. Which records a `between(A,B)` filter keeps (§19.1): every record connecting the sets, or only the data that left the first set and reached the second, or the reverse. A record with no data direction, such as a connect, is kept under `Either` only.

**Specification member order** for canonicalization (§10.5), emitted exactly in this sequence: `specVersion`, `snapshotVector`, `versions`, `basis`, `metric`, `rateNumerator`, `byteDomain`, `accountingSide`, `evidencePolicy`, `timeScope`, `graphProjection`, `grouping`, `filter`. `requestedRows` is carried by the query identity beside the hash, never inside it (§10.4, ADR-015).

## 24. Version axes and invalidation

Fifteen independent version axes decide reproducibility, cache correctness and what a bump costs. Each is owned by exactly one module, persisted in exactly one place, and invalidates a stated set of derived state. No bump ever edits data in place (R1, R20).

| Axis | Owner | Bumps when | Persisted in | Invalidates |
|---|---|---|---|---|
| `formatMajor` | Storage | An incompatible layout or a newly required feature | Segment headers, manifest | Everything: a reader refuses the session and offers migration into a new destination |
| `formatMinor` | Storage | A purely additive, optional feature | Segment headers, manifest | Nothing, provided the feature is marked optional |
| `adapterVersion` | Capture adapter | Enablement, field mapping or admission behavior changes | Manifest, per source | Capability claims and tier assignments; applies to new captures, never to past sessions |
| `schemaFingerprint` | Capture adapter | A provider's event layout differs from the saved one | Schema snapshots, per descriptor | Decode of the affected descriptors, which fall back to opaque or header-only |
| `normalizerContract` | Analysis | Field-to-observation mapping or fact-key derivation changes | Manifest, observation identity | Observation identities: a new derivation generation with links back to raw-record keys |
| `derivationVersion` | Storage and Analysis | Any index, tile or column encoding change | Segment headers | The affected derived files only; rebuildable from retained raw evidence |
| `entityRevision` | Analysis | New lifecycle or alias evidence rebinds observations | Entity revision tables | Entity-bound caches, ranking, graph projections |
| `correlationRevision` | Analysis | A correlator, its version, or late evidence changes relations | Relation revision tables | Relation-dependent aggregates, operation metrics, graph edges |
| `alignmentRevision` | Workspace | Clock mapping, host alias or manual alignment changes | Workspace manifest | Cross-host scopes, candidate joins, multi-host aggregates — never local durations (I10) |
| `metricsContract` | Analysis | What a metric result means changes: which records a filter keeps, which a total takes, or which group a record belongs to | Query identity (`versions`) | All query caches and cursors; published results name the contract they were computed under |
| `importContract` | Application | Canonical import keying or retained-evidence policy changes | Import identity | Import identity: the same bytes produce a distinct derived session |
| `canonicalizationVersion` | Domain | The canonical specification form changes | Query identity prefix | All query caches and all cursors |
| `brokerProtocol` | Broker | Wire schema or command set changes | Handshake only | Nothing persisted; incompatible peers refuse to connect |
| `sdkSchema` | SDK | Application instrumentation schema changes | Observation source metadata | Nothing retroactively; older viewers retain unknown fields safely |
| `themeVersion` | Desktop | Tokens, palette or contrast targets change | Theme definition | Rendered geometry and label caches only |

```mermaid
flowchart TD
  raw[Raw admitted evidence] --> norm[normalizerContract]
  schema[schemaFingerprint] --> norm
  norm --> obs[Observation segments + derivationVersion]
  obs --> ent[entityRevision]
  ent --> corr[correlationRevision]
  obs --> agg[Aggregates, tiles, ranking, graph]
  corr --> agg
  align[alignmentRevision] --> agg
  agg --> cache[Query caches and cursors]
  canon[canonicalizationVersion] --> cache
```

Rules: a cache key carries every axis its result depends on (§10.5); a snapshot lease pins the axes it was taken under, so a reader never observes a mixed set (R6); and the dependency graph above is declared in code with a test asserting that every derived artifact names its upstream axes. An artifact that cannot name them cannot be published.

## 25. Non-goals and definition of done

### 25.1 Non-goals for the first release

Stating these once prevents them being rediscovered as scope: threat detection, malware classification or alerting as an organizing concept; a universal lossless feed of every message, memory access or payload; remote deployment, fleet control or live streaming between hosts; always-on background recording or a persistent service; kernel drivers and process injection in the baseline product; protocol decoders beyond hex, bounded text and structured source fields plus one synthetic fixture decoder; decryption of TLS, authenticated RPC, SMB or QUIC content; automated root-cause, deadlock or critical-path conclusions; any cloud service, telemetry upload or automatic network egress; forensic chain of custody or hostile-tamper resistance; and capture on non-Windows platforms. Each of these is either a later milestone with its own gates (§15) or explicitly out of scope.

### 25.2 Definition of done for v1

v1 is done when every item below is demonstrably true, each traceable to a named fixture or test (§13.5):

- every `R<n>` rule and `I<n>` invariant is named by at least one passing test, and the CI coverage check reports no uncovered contract;
- a standard user can open, explore and export a session with no elevation, and a single elevation starts capture;
- a first-time user on a machine with no prior state reaches a live L0 overview by pressing one action, inside §12's first-feedback budget, with every §3.1 default correct and unmodified;
- the §3.2 ladder descends and ascends one rung per gesture at every level, states its position, and restores viewport, selection, grouping and graph focus exactly;
- §6.8's four latency windows are measured and met at tiers T1 and T3, and no analysis work blocks input, clears a populated view, or raises a modal;
- scale invariants S1 through S7 hold at tiers T1 through T3, with the T3-versus-T1 reopen ratio and steady-state working set published;
- no prohibition P1 through P28 is present in the shipped build, each covered by a test or a review checklist item;
- each initial mechanism carries a measured tier from §14.2 for every supported build, published with the release;
- graph and timeline hold equal prominence at every supported window size, both reachable by keyboard alone, with accessible table equivalents yielding identical result sets;
- §6.7's navigation properties pass as property tests, and overscroll stays inside its allowance under randomized gesture sequences;
- §6.6's contrast and perceptual-separation tests pass in dark, light and high-contrast modes and under three color-vision simulations;
- timeline, graph, ranking, inspector and CLI agree on counts, byte sums and scope for every fixture, in the same units and domains;
- unknown, unmeasured and observed-zero are visually and textually distinct everywhere they can occur;
- all §21.1 scenarios pass as executable assertions rather than screenshots;
- a 10-minute live session, a 10-million-observation reopen and a 100-million-observation qualification tier each meet §12's budgets on the reference machine, with published hardware and build;
- the end-to-end latency budget of §12 is measured stage by stage and met;
- injected loss at each of the provider, queue, decode and storage boundaries is attributed to the correct layer in the health ledger, and no gap is rendered as zero activity;
- crash at each commit step recovers the last durable generation, replays committed batches exactly once, and leaves no unowned ETW session;
- retention eviction, pinning and export never invalidate an open or pinned reference;
- a metadata-only profile provably retains no unapproved bodies, and a redacted export contains no original payload or resolvable reference to one;
- two independently recorded hosts can be imported, aligned, correlated and reopened with manual alignment preserved and no source timestamp altered;
- an unjustified cross-host order, latency or causal claim is refused, and the refusal is visible rather than silent;
- the broker rejects remote clients, unauthenticated clients, expired tokens and unsupported commands, with fixtures for each;
- the CLI can capture, import, verify, query, workspace and export, returning the documented exit codes, and its machine output matches the UI;
- every `TUNABLE:` value is a recorded setting with its measured basis, not a literal;
- format v0 is frozen with a migration and refusal policy, and an older viewer refuses an unsupported required feature safely;
- installation, update, uninstall and capture cleanup leave no orphaned session, driver, service or evidence the user did not ask to keep;
- every ADR §16 requires exists, each recording evidence, alternatives and reversal cost;
- the repository builds on the pinned SDK with no unexplained warnings, and the architecture fitness test passes.

## 26. Solution layout, settings and operational defaults

### 26.1 Suggested solution layout

```text
src/
  InterCat.Domain/           Ids/ Time/ Measurements/ Filters/ Queries/ Capabilities/
  InterCat.Storage/          Columns/ Segments/ Journal/ Manifests/ Recovery/
  InterCat.Analysis/         Entities/ Correlation/ Alignment/ Aggregation/ Ranking/ Graph/
  InterCat.Application/      UseCases/ Coordination/ Ports/ Snapshots/
  InterCat.Capture.Windows/  Etw/ Tdh/ Inventory/ Profiles/ Adapters/
  InterCat.Capture.Recording/ what a privileged recording runs: capture into journal-v1 chunks, plan and ledger
  InterCat.Capture.Journal/  normalization, ETL import, re-derivation, following and compaction policy
  InterCat.CaptureBroker/
  InterCat.Desktop/          Presentation/ Timeline/ Graph/ Views/ Theme/
  InterCat.Cli/
  InterCat.TestWorkloads/
tests/
  InterCat.Domain.Tests/                 pure, no platform
  InterCat.Storage.Tests/                includes corruption and recovery fuzzing
  InterCat.Analysis.Tests/               reference scan implementations
  InterCat.Application.Tests/
  InterCat.Capture.Windows.Tests/        Windows ETW lane
  InterCat.Capture.Journal.Tests/        envelope mapping and standalone ETL import
  InterCat.Desktop.Tests/                transforms, palette, layout determinism
  InterCat.Ui.Tests/                     rendering and accessibility lane
  InterCat.Property.Tests/               navigation, boundaries, identity
  InterCat.Architecture.Tests/           dependency direction (R19)
bench/
  InterCat.Benchmarks/
tools/
  InterCat.FixtureGen/  InterCat.SchemaProbe/
fixtures/    contracts/    theme/    docs/adr/
```

Project names may change; the dependency direction of §9 may not.

### 26.2 Consolidated tunable defaults

Every tunable below has one named default/configuration contract rather than duplicated call-site literals (§1.4). Capture-affecting settings must be recorded in the capture manifest and effective profile, and changing one creates a new coverage epoch (§9.3). View-only settings are workspace-owned; until workspace persistence exists for a setting, its named application default is authoritative and the UI must not imply that it has been persisted. Release qualification still requires persisted/recorded effective values where the acceptance gates say so.

| Setting | Default | Scope | Section |
|---|---|---|---|
| Reorder grace | 2 s | Capture | §18.5 |
| Health sampling interval | 1 s | Capture | §18.5 |
| Pending-join expiry | 30 s | Capture | §18.5 |
| Pending spill budget | 128 MiB per capture | Capture | §18.5 |
| Segment flush rows | 250,000 | Storage | §20.1 |
| Segment flush bytes | 64 MiB | Storage | §20.1 |
| Mandatory compaction thresholds | 64 segments per time block; 512 per session | Storage | §20.1 |
| Segment opens for initial viewport | 128 | Storage | §20.1 |
| Verified segment-reader cache admission | 256 MiB of published segment + dictionary payload per open session; managed overhead remains inside the 512 MiB analysis/cache budget | Storage/query | §20.1, §12 |
| Acquisition queue budget | 256 MiB | Capture | §12 |
| Analysis and cache budget | 512 MiB | Analysis | §12 |
| UI and layout budget | 512 MiB | Desktop | §12 |
| Gesture request coalescing | 30–50 ms | Desktop | §19.3 |
| Live snapshot publication | up to 4 Hz | Application | §19.3 |
| Live ranking refresh | up to 1 Hz | Desktop | §19.3 |
| Graph display budget | 200 nodes, 500 edges | Workspace | §6.3 |
| Cluster collapse threshold | 25 individually drawn members | Workspace | §6.3 |
| Graph labels | 24 per frame, placed without overlap; 150 logical px, one line with an ellipsis | Desktop | §6.3 |
| Layout caps | fixed 120 steps; hard 512 nodes / 4,096 edges after projection; 120 ms measured UX target | Desktop | §19.4 |
| Layout forces | ideal distance 0.75·√(area / related nodes), at most 0.45 of pane height; components repel within 2.2 ideal distances; gravity 0.25 (÷ aspect² across); parked strip 0.28; disc clearance 0.03; anchored travel ≤ ~0.09; design aspect 2.6, 250 px per height unit | Desktop | §19.4 |
| Paired TCP channel overview cap | provisional 4,096; L3 unavailable with reason above it, L0-L2 retained | Application/workspace | §19.4 |
| Graph hit padding | 4 logical px | Desktop | §19.4 |
| Occupied floor | 0.42 of lane height | Workspace | §6.2 |
| Minimum drawn width | 5 logical px, ceiling 12 | Workspace | §6.2 |
| Snap search cap | 128 columns | Desktop | §6.2 |
| Minimum viewport span | 1 µs per device pixel, floor one native tick | Workspace | §6.2 |
| Theme mode | Follow the operating system: its light or dark setting, and its high-contrast setting when on | Application | §6.1, §6.6 |
| Maximum viewport span | 1.1 × retained extent | Workspace | §6.2 |
| Overscroll allowance | min(5% of extent, 10% of viewport) | Workspace | §6.2 |
| Minimap columns | 2,000 | Workspace | §6.2 |
| Minimap brush minimum | 8 logical px | Workspace | §6.2 |
| Mark budget | 20,000 per frame, 2,000 per lane | Workspace | §6.2 |
| Wheel zoom factor | 1.25 per notch | Workspace | §6.7 |
| Double-click zoom factor | 2.0 | Workspace | §6.7 |
| Arrow-key pan step | 10% of span | Workspace | §6.7 |
| Minimum pane size | 420 × 320 logical px | Workspace | §6.1 |
| Stacking breakpoint | 1,100 logical px | Workspace | §6.1 |
| Selection animation | 120 ms | Workspace | §6.8 |
| Level-change animation | 200 ms | Workspace | §6.8 |
| First paint | 1 s | Desktop | §3.1 |
| Reopen ratio T3 versus T1 | 2× | Storage | §12.1 |

### 26.3 Settings storage

This is the target persistence contract. Until a workspace/application setting has that persistence path implemented, §26.2's named default is authoritative and the UI must not claim the value survives reopen. Three scopes, three locations, and no hidden fourth:

| Scope | Contents | Stored in |
|---|---|---|
| Application | Theme mode, units, default profile, update policy, enrichment opt-ins | A per-user configuration file |
| Workspace | Layout, lane grouping, pins, sort, view filters, and every view-scoped tunable of §26.2 | The `.icat-workspace` manifest |
| Capture | Effective profile, admission policy, budgets, retention, coverage epochs | The capture manifest, where they are evidence rather than preference |

The format is documented, versioned and hand-editable. An unknown key is preserved and reported, never dropped. A capture setting is never mutated after the fact, because it describes what was collected (I9).

Revision 146 implements the application scope as `contracts/app-settings-v1.md`: `%APPDATA%\InterCat\settings.json`, whose first key is the theme mode. The header's *Theme* button offers following the system and the four verified token sets. A choice applies at once and is written by replacing only its own key. The menu states where the settings are kept and whatever reading the file reported. A file that cannot be read is kept aside before a choice is written over it, and one another version wrote is left untouched. The workspace scope waits for `.icat-workspace`, which M4's multi-machine workspace defines; pins therefore did not survive reopening. Revision 280 begins it with pins: a session opened from an investigation keeps the nodes pinned on its graph there, one layout per member (`workspace-v11` §7), replaced as it changes rather than kept as revisions, since a layout is a preference and not a finding; a session opened on its own keeps them only while it is open, and says nothing else. Lane grouping, sort and view filters are not kept yet.

## 27. Appendix A: illustrative session manifest

Illustrative, not normative field-by-field; the contract is §10.1, §23 and §24. Tick values and 128-bit identities are encoded as strings regardless of magnitude, so no consumer silently loses precision (§20.5).

```json
{
  "formatMajor": 1,
  "formatMinor": 0,
  "requiredFeatures": ["columns.v1", "journal.v1"],
  "captureId": "9f1c4e0a7b2d4f11a3c6e58d90b7a412",
  "kind": "LiveCapture",
  "finalized": true,
  "host": {
    "hostId": "3a7f22c1d04b4e8e9c5f1b6a2d8e4470",
    "bootId": "c41d8e2f5a6b47c0b9e3f7a1d2c5b806",
    "windowsBuild": "10.0.26220.0",
    "architecture": "x64",
    "displayName": "recorded for the user, never used as identity"
  },
  "clocks": [
    {
      "clockId": 1,
      "kind": "Qpc",
      "frequencyHz": 10000000,
      "consumerMode": "RawTimestamp",
      "epochUtc": "2026-09-20T09:14:02.1183947Z",
      "epochUncertaintyTicks": "1500"
    }
  ],
  "profile": {
    "requested": "explore",
    "effective": "explore",
    "admissionMode": "MetadataOnly",
    "contentBudgetBytes": "0",
    "omissions": [
      { "mechanism": "NamedPipe", "reason": "DisabledByProfile", "detail": "overhead gate not met on this build" }
    ]
  },
  "sources": [
    {
      "sourceId": 1,
      "mechanism": "Rpc",
      "provider": "{6ad52b32-d609-4be9-ae07-ce8dae937e39}",
      "adapterVersion": "rpc-1.2.0",
      "capabilityState": "Available",
      "tier": "TrafficVisualization",
      "schemaFingerprints": ["b91f7c0e", "7c02aa14"],
      "fixtures": ["FX-RPC-001", "FX-RPC-004"]
    }
  ],
  "versions": {
    "normalizerContract": 3,
    "derivationVersion": 5,
    "entityRevision": 12,
    "correlationRevision": 9,
    "canonicalizationVersion": 1
  },
  "extent": { "startTicks": "0", "endTicks": "5988231004" },
  "counts": {
    "admittedRecords": 41822910,
    "observations": 52310774,
    "operations": 9118432,
    "policyOmissions": 1204,
    "undecodable": 17,
    "reportedProviderLoss": 0
  },
  "coverageEpochs": [
    { "epoch": 1, "startTicks": "0", "endTicks": "5988231004", "state": "Covered" }
  ],
  "retention": { "mode": "StopAtLimit", "limitBytes": "34359738368", "pinnedBytes": "0" },
  "files": [
    { "path": "observations/0001.seg", "bytes": "67108864", "sha256": "…" },
    { "path": "sources/journal-0001.icj", "bytes": "134217728", "sha256": "…" }
  ]
}
```

## 28. Appendix B: illustrative analysis specification

The same document drives `icat query` and the desktop UI (R18). Names come from §23; member order is the canonicalization order of §23's final entry, and `requestedRows`, which the query identity carries beside the hash, closes the document.

```json
{
  "specVersion": 1,
  "snapshotVector": [ { "captureId": "9f1c4e0a7b2d4f11a3c6e58d90b7a412", "generation": 84, "manifest": "sha256:…" } ],
  "versions": { "normalizerContract": 3, "entityRevision": 12, "correlationRevision": 9, "alignmentRevision": 2 },
  "basis": "SourceObservations",
  "metric": "BytesSent",
  "byteDomain": "TransportObserved",
  "accountingSide": "CanonicalOwner",
  "evidencePolicy": "IncludeCorrelated",
  "timeScope": { "kind": "AnalysisInterval", "startTicks": "1200000000", "endTicks": "1800000000" },
  "graphProjection": "ProcessResourceProcess",
  "grouping": "Executable",
  "filter": {
    "and": [
      { "facet": "Mechanism", "include": ["Tcp", "Rpc"] },
      { "facet": "ProcessInstance", "participant": ["7f0c5b3d914a42e8b0d61c2fa3845e19"] },
      { "not": { "facet": "Direction", "include": ["Inbound"] } },
      { "facet": "ByteValue", "isUnknown": false }
    ]
  },
  "requestedRows": 100
}
```

```text
icat query capture.icat --spec analysis.json --format jsonl
icat query capture.icat --spec analysis.json --print-canonical     # canonical bytes and hash
```

Reading it: count the send-side transport bytes that the canonical owner rule attributes, over TCP and RPC evidence, inside a named 60 ms analysis interval, for relations in which one selected process instance participates, excluding inbound direction and excluding observations whose byte value is unknown, grouped by executable, projected through resource hubs, top 100 rows. Every one of those choices is an explicit member: none of them has a silent default, and two specifications differing in any member are different queries with different identities (§10.5).
