# InterCat implementation status

Last updated: 2026-09-23
Plan revision: 70
Current milestone: M1 — evidence and persistence foundation. M0 and its explicit IC-010a capture-impact follow-on are complete.

This is the resume document for implementation work. Update it after every coherent slice with verified results, known limitations, and the next dependency-ordered actions. Capability statements here are evidence-based; a provider being registered does not mean its mechanism is supported.

## Latest slice: exact-name ETW orphan-stop adapter

`TraceEventSessionHost` now has a recovery-only stop path for a previously owned broker ETW session. It rejects a
name whose current or legacy shape does not match the durable token before touching ETW, attaches to the exact
active name without creating or restarting it, requests stop, then checks that the name disappeared. An absent
session is idempotent; an inaccessible session still present is a failure, not a claimed stop. The broker runtime
must validate the name against the capture ID and protected ownership log before calling this adapter.

The shape/refusal path is tested, and all 692 tests pass in Debug and Release. An elevated live crash/restart
exercise is still required before this path can justify enabling the broker executable.

Next: compose `IBrokerCaptureRuntime` with the evidence-only recorder, protected capture directory and orphan-stop
adapter. Define honest partial journal finalization after a process crash; enforce duration, journal and free-disk
quotas before enabling any broker command, then exercise the authenticated pipe and recovery together.

## Previous slice: the evidence recorder signals when it is ready

`LiveRecorder.RecordAsync` now offers a one-shot `onReady` callback for an asynchronous broker start. It fires only
after the ETW session has started, the source clock and first journal stage are ready, and the dedicated writer
thread has entered its run. A refused startup never calls it; the final result still says whether the capture
published and what it lost. The elevated `icat record` path is unchanged apart from binding its cancellation token
by name. A scripted test checks readiness before the recording wait and no signal on refused startup.

Verification: all 691 tests pass in Debug and Release.

## Previous slice: complete durable ETW ownership identity

The broker's session name previously truncated its 128-bit ownership token to 15 hex digits to fit the 64-character
ETW name limit. New names are 60 characters: `InterCat-b-`, 16 capture-ID hex digits and all 32 token hex digits.
Validation checks the name, token and capture ID together. The durable ownership store rejects a mismatched name.
Older 64-character names remain accepted for recovery cleanup, but cannot be newly created. The capture adapter
can now build `CaptureSessionIdentity` from a durable broker record without minting a different capture ID or token;
it requires the complete token in names used for new starts. This is still a runtime prerequisite, not an enabled
broker. Tests cover current and legacy names, mismatches, and the adapter identity factory.

Verification: all 690 tests pass in Debug and Release.

## Previous slice: restart recovery stops an unowned active capture

Before binding the live broker runtime, recovery had to be corrected. It previously kept a recording alive if its
owner lease had not expired, even after the broker process restarted. The new process has no owned ETW handle from
the old one; a lease is not proof that it can safely keep recording. Startup recovery now requests stop for every
active capture using the durable session ownership record, regardless of the old lease. If the runtime cannot
finish that stop, the durable state remains `Stopping` and recovery retries it, rather than reporting `Closed`.
In-process lease renewal and expiry sweeping are unchanged. Tests cover both a complete and a refused-then-retried
stop. The real runtime still needs an OS-level owned-session stop path for restart; until that exists the broker
executable must remain disabled.

Verification: all 687 tests pass in Debug and Release.

## Previous slice: an isolated evidence directory for each broker capture

The broker root now creates or reopens `capture-<id>` beneath its validated, pinned root. Each capture directory
receives the same protected ACL and no-write-up label at creation, is checked by open-handle path, volume, reparse
state and security before use, and stays pinned without delete sharing. A pre-existing capture directory whose
security drifted is **refused rather than repaired**: unlike an empty root, it may already contain evidence, so
repairing it could silently adopt untrusted bytes. Ordinary-integrity viewers can read the files but cannot modify
or plant them. The API returns an `IOwnedDirectory` implementation for the evidence-only `SessionStore`; it does
not yet start capture or enable the broker executable. Tests cover isolated captures, reopen, pinning, junctions,
security drift, invalid IDs and the read/no-write boundary.

Verification: all 686 tests pass in Debug and Release.

Next: implement `IBrokerCaptureRuntime` against this directory and `LiveRecorder` without a derivation. Keep the
executable disabled until runtime start/stop/recovery, quota enforcement, lifecycle persistence and the pipe host
are composed and exercised together; the ordinary follower then needs a desktop-reachable handoff. Retention on
an active recording still needs one scheduler for both writers.

## Previous slice: a recording module the broker can reference

The broker's recording path now lives in a module of its own (ADR-028). `InterCat.Capture.Journal` had come to hold
work that must never run privileged: the ETL parser, the normalizer, re-derivation, the follower and compaction. The
dependency map therefore kept the broker away from all of it, including the evidence-only path ADR-027 gives it.

- `InterCat.Capture.Recording` holds what a privileged recording runs: `LiveRecorder` (capture, one journal chunk
  per publication, plan first and ledger last), the envelope mapper, the coverage tally and the retained normalization
  plan.
- A derivation is a hook, `ILiveRecordingDerivation`. It adds each record's rows before the record is appended, and
  acts on every publication before the next chunk begins.
- `LiveSessionRecorder` in `Capture.Journal` supplies the normalizer and the compaction policy through that hook,
  which is what an elevated `icat record` runs. The broker will supply none.
- The dependency map and its architecture test gain the module. The broker's own edge arrives with its runtime.

Behaviour is unchanged: all 681 tests pass in both configurations. A live 6-second recording published 4 chunks and one
closing compaction, 785 records, with nothing lost.

## Previous slice: evidence-only recording and an ordinary follower

Live recording is now split along §9's boundary (ADR-027). §9 and §18.1 give the broker the authoritative journal
and leave decoding and normalizing to an unprivileged process. `LiveSessionRecorder` did both in the recording
process, and also compacted. The plan also never said where a broker-owned capture's derived segments live, because
the broker directory's no-write-up label keeps every ordinary-integrity process out of it.

- **`icat record --evidence-only`** publishes an evidence session: journal chunks, the normalizer plan and the
  coverage ledger, with no rows and no segments. Nothing in the elevated process derives, reads back or compacts.
- **`icat follow <evidence-dir> <session-dir>`**, run in an ordinary shell, derives the session into a directory of
  its own:
  - it mirrors each committed chunk byte for byte, with length and digest checked against the evidence manifest;
  - it derives the rows with the same normalizer and capture-wide journal index an in-process recording uses;
  - it copies the plan with the first chunk and the ledger with the last, and compacts;
  - it waits for the first publication, follows while the capture records, and ends when the ledger arrives;
  - Ctrl+C keeps what was mirrored, and running it again continues from there;
  - it refuses evidence it does not mirror, a session that already has rows, and changed files.
- The derived session takes the evidence session's identity and is an ordinary session: every command reads,
  re-derives, retains and compacts it.

Verified live:

- An evidence-only Explore recording of 12 s, published every 3 s: 5 generations, 1,254 records, nothing lost.
- `icat follow`, started before it, mirrored each chunk as it appeared (607, 209, 265 and 173 records, then the
  ledger's empty chunk), compacted and finished.
- The derived session's observations by mechanism (TCP 723, process lifecycle 453, UDP 78) equal the recorder's own
  coverage counts, and it re-derives across its 5 chunks.
- The evidence session answers metrics with "no derived data".

Two recording tests cover the split, and passed eight runs out of eight:

- An evidence-only recording has no rows. Its follower yields byte-identical chunks, plan and ledger, and the rows,
  field values and journal indexes an in-process recording derives. Following again mirrors nothing.
- A fresh follower resumes a half-mirrored session without duplicating a record. Another capture's evidence, an
  ordinary session and an empty one are refused.

## Previous slice: compacting small publications

§20.1's compaction targets are implemented (ADR-026). A live recording publishes a few derived files at every
interval, and a one-hour recording would have named about 720 observation segments, each opened on every commit,
lease and query.

- **Unit and threshold.**
  - A publication unit is the derived files one generation published. Its dictionaries serve only its segments, so
    they go with it.
  - A unit is small below 64,000 rows and 8 MiB.
- **What a compaction does.**
  - Runs of consecutive small units are rewritten into bounded segments, run by run.
  - Every row moves unchanged, with its locator, journal index and values, and a row count guards the move.
  - The generation releases the replaced files through the retention path, so leases are respected and disk use
    stays flat.
  - The journals, plan, ledger and boundary are carried unchanged.
- **How the recorder uses it.** It compacts the oldest run, at most 250,000 rows, whenever 64 small publications have
  accumulated, and everything left when it stops. `icat record` reports the compactions. A compaction that fails
  leaves the recording whole and is reported rather than ending the capture.
- **Offline.** `icat compact <dir> [--check]` coalesces any session; it is lossless, so it needs no `--confirm`.

Measured:

- The real 5-chunk recording went from 4 observation segments to 1 and released 16 files (445,309 B). Its
  `observations` answer by mechanism was identical, and it still re-derives.
- A 120-publication benchmark of 2.4 million rows went from 120 observation segments to 10, and from 360 files to 140,
  at about 200,000 rows per second. `icat compact` took 16.4 s including its verified open. A grouped metric fell
  from 2.8-3.1 s to 2.6 s. The rest is hashing on open and the row scan.
- A live step of 250,000 rows takes about 1.25 s. The 65,536-record queue absorbs that at any rate where a chunk is
  still small; above about 12,800 records per second every chunk already reaches the target, and no step runs.

Three store tests and one recording test cover the slice:

- small publications coalesce with every row and field kept, and the evidence and boundary unchanged;
- large units stay where they are, runs stay separate, a live budget takes the oldest run, and a reader's lease keeps
  the replaced files;
- only segments and dictionaries can be replaced;
- a recording compacts while recording and when it stops, keeping all six rows and their journal indexes, and still
  re-derives. It passed eight runs out of eight.

Journal chunks are evidence and are not coalesced, so a long recording still names one journal file per publication.

The lease-expiry test gave its reader lease 100 ms, and the full parallel suite outlasted that once the compaction tests
ran beside it. It now gives 1.5 s, and it still proves that expiry releases the hold without another reader call.

## Previous slice: hashing each dependency once

A long live recording could not keep up with its own publications (ADR-025). `store-v1` re-measures every dependency
before a generation names it and whenever a reader acquires one, and re-measuring hashed every byte each time. A
recording carries every earlier chunk, so each publication hashed the whole session, twice: once to check that the
current generation had not changed under the writer, and once for the new manifest.

Measured with a benchmark that publishes 120 chunks of 20,000 records through the public store API:

| At | Session | Commit before | Commit after | Writer's lease before | after | Fresh reader's open |
|---:|---:|---:|---:|---:|---:|---:|
| chunk 1 | 6.2 MiB | 158 ms | 154 ms | 24 ms | 17 ms | 8 ms |
| chunk 30 | 185.6 MiB | 556 ms | 100 ms | 267 ms | 12 ms | 248-263 ms |
| chunk 120 | 742.2 MiB | 1,951 ms | 148 ms | 972 ms | 38 ms | 942-977 ms |

- Every dependency is still opened and its length checked.
- Its bytes are hashed unless the same instance already hashed that file and its last-write time has not moved.
  Any write to an immutable file moves that time.
- A fresh instance hashes everything once. A CLI command therefore now hashes a session once instead of twice, when
  it opens the session and again when it takes its lease.
- Two store tests pin the rule:
  - a writer re-hashes a file written since it measured it, and refuses to publish on top of it;
  - a fresh reader finds a change that kept the file's length and time.

## Previous slice: releasing a live recording's oldest chunks

`icat retain` now works on a live recording (ADR-024). A long recording could not shed its oldest evidence, because
journal-prefix retention read one journal and refused a chunk sequence.

- **A chunk is the unit of release.** `--release-journal-before-record <n>` releases each chunk that ends at or before
  record `n`, counting records in stored order across the chunks the generation names.
- **Nothing is rewritten.** The retention generation stops naming the oldest chunks and keeps its committed boundary.
- **What can go is limited.** Only a leading run of chunks is released, never the boundary's chunk, and never every
  record. That includes every chunk but an empty final one that carried only the ledger.
- **The record identifies what went.** The retention record lists the released chunks, and its source digest covers
  their dependency lines exactly as the superseded manifest held them.
- **The preview shows chunks.** It counts records, batches and chunks, and shows the boundaries the recording allows.
- **"Net change" is now correct.** It counts written bytes, so a chunk release shows 0 B written instead of the
  retained journal's size.

Writing this exposed a defect in the single-journal release of ADR-010. **Re-derivation after a release lost rows
without a word.** The release keeps every derived row, but the next `icat rederive` replayed only the retained
journal and carried none of the earlier rows. The released records' rows vanished, and the retained rows' journal
indexes restarted at 0. Re-derivation is now refused whenever it would drop rows no replay can rebuild:

- the retention generation is refused from its own record, before any evidence is read;
- a later generation is refused before publishing, when a stream's rows go back further than its retained records;
- `icat retain` states this before a release is confirmed, and `icat session` gives it as the reason
  re-derivation is not available.

Verified on a copy of the real 5-chunk recording:

- A boundary at record 700 previewed the first chunk: 626 records in 1 batch, with the boundaries it allows running
  from 626 to 1,162.
- Confirming it published generation 6 and freed 183,169 B, with 0 B written.
- `icat session` then showed 4 chunks and "re-derivation not available", with the reason.
- `icat rederive --check` refused with the same explanation and exit code 3.

Two store tests and two journal tests cover the slice:

- Oldest chunks are released whole, the boundary is kept and the record is exact.
- The store refuses a chunk from the middle, every chunk, and a release that would leave only an empty final chunk.
- After a recording's first chunk is released, `Assess`, the check and the rebuild all refuse. After a later
  derived-file release, which no longer names the journal release, the rebuild is still refused through the row scan.
- After a single-journal prefix release, the rebuild is refused.

The recording tests passed eight runs out of eight.

A retention published while a recording is still running changes the generation the recorder expects, so the
recorder's next commit is refused. Retention is safe on a finished recording, and a rolling window over a running
capture needs the broker to schedule both writers.

## Previous slice: re-deriving a live recording

`icat rederive` now works on a live recording (ADR-023). It used to read only one journal, so it refused every chunked
recording from ADR-022, which denied them the rebuild §20.1 promises for retained evidence. It now replays the chunks in
the order they were recorded. It then publishes one replacement generation that carries every chunk, the plan and the
ledger unchanged.

- Before it publishes anything, the replay proves the chunks are one recording:
  - every chunk's digest matches;
  - every chunk names one capture and one clock;
  - within each stream and epoch, each chunk's ordinals pass those of the chunks before it, so no record is replayed
    twice or out of order;
  - the boundary names the newest chunk and replays exactly its committed records.

  A failure publishes nothing, and the current generation stays published.
- A row's journal index now counts its record across the capture's chunks. The index beside a metric's evidence
  therefore locates a record in the capture, and a replay reproduces it. ADR-022 counted within each chunk, and
  re-deriving a session recorded by that build publishes the capture-wide index.
- `icat rederive` names the chunks in its progress and result, and adds `journalChunks` to its JSON.
- `icat session` now offers re-derivation for a chunked session. Its evidence section showed only the chunk the
  boundary names, so a recording whose last chunk carried only the ledger read "0 records". It now states the chunk
  count and total size, and labels the boundary's extent as the newest chunk's.

Verified on the real 5-chunk recording from the previous slice (1,288 records and 3,264 source fields):

- `icat rederive --check` replayed all five chunks, including the last one, which held no records.
- `icat rederive` published generation 6, read from 1 segment instead of 4.
- `icat metric --metric observations --group-by mechanism` answered identically before and after, apart from the
  generation it names.
- Generation 6 re-verifies.

Two fixture tests cover the slice, and each passed eight runs out of eight:

- A chunked recording re-derives into a replacement that carries every chunk, the plan and the ledger. Its rows and
  source-field rows match the originals exactly, including their journal indexes and byte counts.
- A recording whose second chunk repeats the first's ordinals is refused by both the check and the rebuild, and
  nothing is published.

Journal-prefix retention still reads one journal and still refuses a chunk sequence by name.

## Previous slice: following a live recording

`icat record` now publishes as it records (ADR-022): `--publish-every <s>`, 5 s by default, completes a **journal
chunk** at every interval that admitted records and publishes a generation holding every chunk so far. Other commands
can then read the capture while it runs; `--publish-every 0` publishes once, at the end.

- Each chunk is a complete `journal-v1` file - header, clock, schema table, batches, terminal frame - so every
  published file stays immutable and verifies exactly as before.
- Record ordinals continue across chunks, and a row's journal index counts within its chunk.
- The normalizer plan comes with the first generation; the coverage ledger only with the last, because an epoch still
  running has not stated what it lost, so coverage is unknown until the capture stops.
- Re-derivation and journal-prefix retention still read one journal. On a chunked session they now refuse by name
  instead of failing obscurely or dropping the other chunks' rows.

Verified live: a 12-second Explore recording published every 3 s, with the TCP workload running, was read by
`icat metric` in the middle of the capture - generation 1 with 626 observations, then generation 2 with 961. It ended
at 5 generations holding 1,288 records, with every mechanism covered and nothing lost. A fixture test records two bursts
of records 400 ms apart, published every 100 ms, and checks that the last generation holds every chunk, all rows and
one ledger; it passed six runs out of six.

A growing journal whose generations name longer prefixes was the other reading of §20.1's committed boundary. It is
deferred: a published file would change, dependency checks would measure prefixes, readers would share a file with its
writer, and a prefix would end without a terminal frame. Chunks give the same following behaviour and keep every rule.

## Previous slice: live recording into a session

`icat record <new-dir> [--profile explore|focused-transport] [--mechanism tcp|udp] [--duration <s>]` captures live
under an owned ETW session straight into a new session (ADR-021). Until now every session came from an ETL another tool
recorded, and the broker's `IBrokerCaptureRuntime` had only a test fake. `LiveSessionRecorder` is the runtime both use.
It works in these steps:

- it compiles nothing itself: the command compiles the capture profile, and the recorder takes the resulting plan;
- one writer thread drains the admission queue into the session's own `journal-v1`, in acquisition order, and each
  record derives its rows exactly as an imported one does;
- the normalizer plan and schema table come first, so the session re-derives like an import;
- when the capture stops, one generation is published through the §20.1 commit protocol, and Ctrl+C stops early while
  keeping what was recorded.

The live coverage epoch is measured, not assumed. The session now tells an optional delivery observer every outcome
with its descriptor, from the callback, with bounded, allocation-free work once a descriptor has been seen.

- **Collected:** the descriptors of providers whose enablement succeeded.
- **Losses:** the session's own counters - events it lost, buffers its consumer lost, records the full queue dropped,
  and records admitted but never journaled.
- **Queue drops:** a dropped record is loss, not a delivery of its descriptor.
- **Unreadable counters:** the generation is published without a ledger, and its coverage stays unknown.
- **Refused clock:** nothing is published.

An 8-second Explore recording on this machine, with the TCP and UDP workloads running, journaled 4,273 records (520
process lifecycle, 3,496 TCP, 257 UDP) and lost nothing. It reopened through `icat session`, `icat processes` and
`icat metric` like an imported session: 506 process instances, 493 of them from the capture-state rundown, with
relation-aware process rankings. Its ledger reported all three mechanisms covered.

Building it found a defect in the import's ledger: it named its providers by GUID, because an import enables no
provider. The coverage tally is now one public class shared by import and recording, and it names providers from the
source catalog. A re-import of the UDP capture shows `Microsoft-Windows-Kernel-Process` and
`Microsoft-Windows-Kernel-Network`.

What remains: publishing while recording, so a viewer can follow a capture, needs the journal's committed boundary to
advance under an open lease; and the broker must bind the same runtime and give the viewer a session root it can read.

## Previous slice: UDP datagrams related to the process at the other end

`transport-endpoint-relation-v3` (ADR-020) relates UDP datagrams the way v2 related TCP connections:

- a record's own end is read through the orientation FX-UDP-001 measured, so a receive, which names the sender first,
  is keyed by its own endpoint like every other record;
- every end is keyed by its protocol, so a TCP and a UDP socket on the same numbers are never paired;
- a UDP end has no connect, accept or disconnect, so it is one incarnation - one datagram flow - for the capture, and
  a port reused by another process within the capture stays ambiguous instead of being split at a guessed moment.

The `peer(P,Q)`, `between(A,B)`, `participant(P)`, `sender(P)` and `receiver(P)` filters, peer groupings,
cross-side groupings and peer and channel counts read UDP with no change of their own. Every TCP answer is unchanged:
TCP ends sort first, so TCP channels keep their numbers. The rule identity changes because relation-dependent answers
now read UDP. A fixture test pairs a datagram and its reply, keeps a TCP connection on the same port numbers apart,
and leaves a datagram from an unheld sender `PeerNotObserved`.

Verified on a real capture: an ETL recorded around the UDP workload (three client sockets, 30 datagrams, 30
acknowledgements) imported with 1,195 admitted records. The client's whole conversation, 23,008 B, was attributed to
the server as correlated, and the peer rows partitioned it. The client had exactly 3 channels, one per socket, with no
unknown part. The session's ledger reported UDP as covered: 917 records from its 2 admitted descriptors.

The leased overview graph stays TCP, because it labels every edge TCP and derives its graph timeline from those edges;
it now takes only TCP relations, so nothing it shows changed. UDP edges belong to IC-017 once edges and graph buckets
carry a mechanism of their own.

## Previous slice: UDP datagrams, measured before admitted

UDPv4 datagrams are admitted (FX-UDP-001, ADR-019). The Kernel-Network source always delivered them under its IPv4
keyword, and the catalog dropped them because nothing said what their endpoints mean. A new `udp-loopback` truth
workload settled that: a client sends seeded datagrams from two bound sockets, and the server acknowledges each one to
the endpoint it came from. Each process logs both ports. The raw events were first decoded independently through the
registered manifest, before any plan assumed an orientation:

- a UDP **send** names its owner's own endpoint first, as every TCP descriptor does;
- a UDP **receive** names the datagram's **sender** first, and the receiver's own endpoint as its destination (16 of 16
  each way);
- every receive's event-header PID differs from its payload owner, so the payload owner stays the attribution.

Records keep endpoints as the source names them. A reader that needs a record's own end reads it through one measured
orientation, stated in the domain by mechanism and kind. `icat measure udp` and `icat verify udp` share the TCP
path through a small scenario record. The fixture met every traffic criterion of §14.2 at 100%, and a second run
reproduced it:

- 64 of 64 truth operations were observed, bound to their flow and byte-measured;
- 0 of 4 peer attributions were false;
- 11,802 B was sent and received, equal to the truth log;
- the tier is `TrafficVisualization`.

The evaluator now reports an operation seen only with its endpoints mirrored as an orientation gap. A test
re-evaluates both committed transport fixtures to their tiers.

Two follow-ups came with admission. A focused capture used to compile every descriptor of its source, so a TCP focus
would now have persisted UDP as well. Focus now filters a source's descriptors to the named transport, which also
narrows the events it enables, and UDP is a valid focus. The capture impact was re-measured with the wider plan
(`bench/results/capture-impact-20260923T095313Z/impact.json`, seven pairs per source). Kernel-Network is 1.61 CPU percentage points and 0.00% throughput
regression: `Moderate`, loss-free, and well inside §12's 5-point target. Kernel-Process, whose admission did not change, measured 1.53 points in the same run. The previous committed run (three pairs, 2026-09-22) had both under the 1-point Low ceiling. A three-pair run just before this one (`bench/results/capture-impact-20260923T095041Z/impact.json`) read 0.62 for Kernel-Network and 2.16 for Kernel-Process. Seven pairs on a machine that was not quiet put both just over the ceiling, with single pairs spanning -2.5 to +7.3 points. So the catalog records the newer, wider measurement - Moderate for both - rather than keeping the older class for the source that did not change, and a run on a quiet machine is owed before the class is treated as settled.

The relation rule still reads TCP only, so a UDP record's other end is `NoRelationRule`. A channel or peer count over a
capture with UDP is a lower bound that discloses it.

## Previous slice: metric answers carry capture coverage separately

`SessionMetrics.Evaluate` now reads `coverage-v1` from the same leased generation as its segments and attaches a
structured `MetricCoverage` to the result. A mechanism filter receives one state over its exact native interval;
an all-mechanism request receives separate states, never an invented aggregate "covered" state. A legacy generation
states unknown coverage. The CLI includes these states in JSON and summarizes them without flooding the terminal
with every uncollected mechanism. Rate caveats no longer falsely claim that no session has a ledger. A rate remains
its observed numerator over the whole selected interval even when coverage is partial; neither missing records nor
healthy-time denominator are estimated. `metrics-v1` records that distinction. A fixture ETL smoke check produced a
published TCP rate of 485 observed records with `Covered` capture coverage; the temporary session was removed. New
tests cover covered, partial, quiet, out-of-reading, all-mechanism and legacy scopes. This is source coverage, not
proof that every process binding or relation was complete. The complete Debug and Release solutions each pass 658
tests across 12 assemblies.

## Previous slice: coverage-aware real-session overview

`SessionOverviewProjector` now reads `coverage-v1` from the same leased generation as its rows and relations. It
maps each 100-nanosecond presentation bucket to the exact half-open native-reading interval under the published
source clock, including negative session times across tick zero. Its all-observations timeline rolls up coverage of
mechanisms actually observed in that bucket; its graph-eligible TCP timeline applies coverage only to buckets with
displayed eligible rows. Empty buckets remain unknown, an imported mechanism with no delivered records remains
unknown in the bundle's per-mechanism summary, and an unlocated source loss makes each affected observed bucket
partial rather than locating a gap it cannot locate. Legacy generations remain unknown. The immutable bundle names
whether the ledger was published and preserves the graph/timeline eligible-set conservation check from revision 52.
Two published-session coverage cases and a negative-time boundary regression pass; the shared analysis evaluator now
validates a ledger once per multi-mechanism scope rather than once per mechanism. This is read-side composition, not
desktop publication: the default window is still a synthetic tour, and the UI still needs a coherent applied bundle
with ranking, navigation, supersession and coverage explanations. No absence of graph-eligible rows is presented as
proof of no communication. The complete Debug and Release solutions each pass 655 tests across 12 assemblies.

## Previous slice: imported-session coverage ledger

`coverage-v1` now records what an ETL source delivered and reported lost separately from the admitted journal:
each descriptor's admitted, policy-omitted and undecodable outcomes, first/last delivered native readings, the
compiled admitted descriptors and an explicit source-loss counter. The importer validates the ledger even without
session publication. A published import stages it as a checksummed, bounded immutable manifest dependency; reopen
verifies it, re-derivation carries it unchanged, and ordinary derived-file retention cannot release it. `icat session`
discloses per-mechanism coverage and the loss and omission facts. `SessionCoverage` distinguishes not collected,
quiet imported (unknown), covered and partial-gap mechanisms. It treats an unknown descriptor version as unassigned
decode loss that may affect any collected mechanism, and never extends coverage beyond delivered readings or across
an epoch gap. Missing required loss counters refuse a ledger rather than becoming zero. ADR-018 and
`contracts/coverage-v1.md` freeze the decision; `contracts/store-v1.md` now names the dependency kind.

The full Debug and Release solution test runs each pass 652 tests across 12 test assemblies. New regressions cover ledger round-trip
and refusal, persistence/re-derivation/retention, quiet mechanisms, policy omissions versus loss, unknown versions,
interval boundaries, maximum native ticks and ETL system records without provider GUIDs. An actual local fixture ETL
imported 1,090 delivered records (555 admitted, 535 policy-omitted, 0 source loss) into a published session. Its
coverage dependency survived an actual `icat rederive` unchanged into generation 2; the CLI read-back reported TCP
covered and uncollected mechanisms not collected. The two temporary smoke-test sessions were removed after the
check. The live capture runtime still does
not publish coverage epochs. At revision 53 the desktop and metric answer paths did not yet consume the ledger;
revisions 54 and 55 add the read-side overview and metric joins, not live or desktop publication.

## Previous slice: matching graph and timeline eligibility

`TransportRelation` now exposes the channel number its paired incarnation's records share within one relation
derivation. The number is a local join key, not a cross-generation identity. `SessionOverviewProjector` uses the channel
IDs of only the displayed, policy-admitted relations to produce a second, graph-eligible timeline on exactly the same
time axis as the all-observations timeline. A one-sided or unresolved connection remains in the latter but cannot
silently become a graph edge or a graph-eligible bar; a candidate relation enters both only after candidate-policy
opt-in. The bundle discloses graph-eligible rows whose session time is unavailable. It also checks that the number of
selected channel rows exactly equals the displayed edges' record count and refuses the bundle on disagreement instead
of publishing graph and timeline numbers from different eligible sets. Published-session tests cover that split and
the candidate transition. This resolves the read-side eligibility mismatch found in revision 51, not the remaining
UI publication or byte-accounting work.

## Previous slice: leased real-session overview bundle

`SessionOverviewProjector` now reads one leased published generation, including the session's own source clock and
source-field segments, then derives process instances, paired TCP connection relations and a bounded observed-only
timeline. Its graph identity names the session, generation, manifest digest and evidence policy. It includes every
process instance or refuses above the provisional 512-node bound; it aggregates admitted paired relations by process
pair or refuses above 4,096 edges. A candidate relation enters only when the caller explicitly admits candidates.
One-sided, ambiguous, unbound and unsupported relations are never guessed into an edge. The edge is canonically
ordered for display, explicitly not a claim of initiation or data direction; its record count is not a whole-session
total, and no byte value is fabricated. Rows with no usable session time and TCP rows with no admitted peer are counted
separately and disclosed. The timeline converts session nanoseconds to the workspace's 100-nanosecond presentation
scale, counts observed rows only and marks coverage unknown until a ledger is projected with it. Collections are
read-only, and count overflow refuses instead of wrapping. Three published-session regressions cover stable identity,
unresolved and untimed records, and candidate-policy opt-in.

The timeline canvas now draws an observed bar even when its interval has partial or unknown coverage, then overlays
the coverage hatch. Previously the hatch erased the observed bar, conflating an incomplete interval with no observed
activity. This bundle is not wired to the desktop's synchronized ladder yet: channel, operation, evidence and byte
projections, coverage-ledger composition, compaction above the graph bound and numeric-pane supersession were still
owed at that revision. The default window remains the synthetic tour.

## Previous slice: session-ready workspace presentation

The desktop now accepts an injected immutable `WorkspaceSnapshot` under that snapshot's own graph identity; its default
remains the explicitly synthetic tour. An empty snapshot opens with no fabricated process selection. The window header
names the snapshot instead of always claiming synthetic data, and its coverage strip derives partial-gap, unknown,
not-collected and reduced-fidelity interval counts from the snapshot instead of asserting an exact two-second gap.
Long labels are visually truncated with the full wording in a tooltip. The selected-time label derives the actual
workspace extent instead of saying every session is 24 seconds. A single presentation time constant replaces
independent 10-million-tick assumptions in the ladder, timeline, rows and view model; a real-session projection must
convert session nanoseconds into that 100-nanosecond viewport scale. Two Desktop and one headless UI regressions cover
non-tour identity and duration, empty selection and truthful header copy. The projector and launch/open flow remain
owed; no real-data capability is claimed by this foundation.

## Previous slice: channels by peer

`ActiveChannels --group-by peer` now ranks the distinct TCP connection incarnations a focused process has with each
resolved counterpart. It works with owner, participant, sender and receiver focus roles and respects the request's
evidence policy: a candidate reused-PID binding is not silently promoted under the conservative default. A channel
whose other process cannot be resolved stays under an unattributed reason, while the overall focused count still
includes its known channel identity. A record that identifies no channel remains an unknown contribution, so the
headline says "at least" where appropriate. Peer rows do not claim to partition the whole count. The top-N remainder
counts the union of the hidden channel identities rather than adding row counts, and grouped requests reject evidence
lists, which only enumerate one ungrouped total's records. The CLI help and `metrics-v1` contract state the distinction.
Three relation regressions exercise unresolved peers, candidate admission and port reuse across connection lifetimes.

## Previous slice: traffic between two process sets

`between(A,B)` completes §19.1's process filters (ADR-017). It names two sets of process instances. A record passes when
its maker is a process of one set and its other end, under `tcp-endpoint-relation-v2`, a process of the other. A new
enumeration, `EN-BetweenDirection`, keeps every such record, or only the data that left the first set for the second,
or only the reverse. A connect or a disconnect has no data direction, so it passes only under either way. The
accounting is unchanged, so `FirstToSecond` bytes can be measured where they left or where they arrived. A record a
process of either set made whose other end is undecided could connect the sets, so it is disclosed by reason, never
added. The filter names both ends, which makes it a filter of its own: with a focus, a peer or a peer grouping it is
refused rather than intersected. A channel count between the sets counts their connections. A peer count needs a
grouping by process. `icat metric` takes `--between <ids> --and <ids> [--direction either|first-to-second|second-to-first]`,
each set a comma-separated list, and prints both sets and the direction beside the answer. The canonical term writes
one meaning one way: sorted sets, and a backward direction written forward with the sets exchanged.

On the `peak` session the workload's two processes exchanged 34,300,070 B either way. That is 34,168,998 B from the
client to the server and 131,072 B back, over 8 channels. Every figure matches the focus answers.

Verifying it on that session found a defect the unit fixtures could not show, because they had no start keys. Only a
focus or a process grouping loaded the session's side fields. So a between filter derived instances without their
start keys and could not find the ids `icat processes` prints: it answered "not present in generation 1" for a
process that was there. An unfocused channel count derived instances the same way, and its query identity named the
relation rule but not the binding rule its relations read. One predicate now decides both: an answer that reads a
binding, through a filter, a grouping or a relation, loads the start keys, and its identity names the binding rule. The
policy is named only where it decides something. A regression test publishes start keys and fails without the fix.
The only identity that changed is that channel count's, which had existed for one plan revision; no golden line
changed, and the corpus gained both new forms.

## Previous slice: counting channels

`ActiveChannels` is answered, the second distinct count §5 defines. A channel is one connection incarnation under
`tcp-endpoint-relation-v2`. Two paired incarnations are one channel, a port reused by a later connection is another,
and a connection whose other end no record holds is a one-sided channel. When pairing is undecided, only the side with
more incarnations is counted, because each of those is bounded by its own lifecycle; the other side's records identify
no channel. So one connection is never counted twice, and two witnessed connections are never counted as one. Records
that identify no channel are the count's unknown part, with their reasons, and the count is a lower bound beside them:
another mechanism, no endpoint pair, or the uncounted side of an undecided pairing. A count needs no subject process.
With a focus it counts the focus's channels, and grouped by process or executable it overlaps as a peer count does. The
peer-count code was generalized, so both counts share one routine for a single count and one for a ranking.

On the `peak` session there are 29 channels: the 8 workload connections, a process connected to itself, and 20
one-sided channels to remote hosts, which is exactly the 38 ends of which 18 are mirrored. None is undecided, so the
count is exact and printed without "at least". Each workload process shows its 8 connections, and the busiest other
processes show 7 and 4.

## Previous slice: connection incarnations

`tcp-endpoint-relation-v2` (ADR-016) scopes each connection end by the lifecycle the capture witnessed for it. An end's
records, in canonical order, are divided into incarnations: a connect or an accept begins one, and a disconnect is the
last record of the one it closes. Incarnations at the two ends pair in order when their counts match, because a
4-tuple carries one connection at a time, and otherwise only through a lifetime that overlaps one partner and nothing
else. A reused port whose lifecycle the capture holds now pairs each connection with its own other end; the
whole-capture rule of v1 left both without a peer. When a pairing is undecided but every candidate is held by the same
instance - a long-lived server whose accepts were lost - that instance is still named. An end whose lifecycle was not
witnessed is one incarnation, exactly as under v1, so a lost boundary can merge two connections but never split one.
Relations now carry whether each end's open and close were witnessed.

The evidence supported the split before it was written. On the `peak` session, 16 of the 18 mirrored ends carry
exactly one open and one disconnect, with the open first, the disconnect last and no data after it, and nothing there
was reused, so every answer matches v1: 34,300,070 B of workload conversation, the same cross-side groups and 44,525 B
unattributed. The canonical query form now takes its version-axis values as an input. The new rule therefore changes
the identity of every relation-dependent query, as §24 requires, while the form and its golden corpus stay unchanged.
Help texts now print the rule name from the code rather than a literal.

## Previous slice: counting peers

`ActivePeers` is answered from relations. A peer is a process instance at the other end of a record under
`tcp-endpoint-relation-v1`, an identity rather than a PID or an address and port pair, so the count establishes none
from reusable values (R22). With a process focus it is one count, and grouped by process or executable it ranks each
process by its own peers. A process connected to itself is its own peer, so a focus's count equals the number of
processes its peer grouping lists. A record whose other end is unresolved names no peer: it is an unknown contribution
with its reason, the count is a lower bound beside those records, and a remote process is never counted. A count with
no resolved peer is `NothingMeasured`, and a process whose records resolve none is unmeasured rather than ranked at
zero (R21). Distinct counts overlap: each end of a resolved record is the other's peer. So a grouped count states that
its rows do not add up to its total, which is the number of processes with at least one resolved peer, and its
remainder is the distinct count over the merged groups' union. `ActiveChannels` stays unavailable with a precise
reason: a channel is a connection incarnation with a lifetime, and whole-capture ends would count a reused port once.

Measured over the `peak` session: three processes have a resolved peer - the two workload processes, each the
other's, and a process connected to itself. The busiest workload process's one peer carries 74,678 correlated records.
The one record that names a PID after its instance exited is shown as its peer's unresolved record, not a second peer.
Processes whose traffic all leaves the host are listed as unmeasured, with their 107 unresolved records, rather than as
having no peers. The text view renders a focus as "1 process at the other end", and adds "at least" when some of its
other ends are unresolved. A grouped row shows its unresolved records beside its count, and the JSON carries them by
reason.

## Previous slice: canonical query identity

§21.2 lists `contracts/query-identity-v1.md` among the M1 closure artifacts, and it is now frozen for the members
`metrics-v1` implements (ADR-015). Every metric request that reads a generation has an identity: `v1:sha256:<hex>`
over a canonical specification written token by token in §23's member order, with the rows a ranking was cut to kept
beside the hash. The canonical form materializes defaults, so an implied domain, side or layer and the same value
written out are one identity. It writes only the version axes and terms an answer depends on, so an evidence policy
that cannot change an ungrouped total does not split one query into two. Every `icat metric` answer names its identity,
unavailable answers included, and `--print-canonical` prints the exact line it hashes. On the `peak` session the
participant-by-peer ranking printed the same `v1:sha256:1f11d52d…` from `--print-canonical`, the text answer and the
JSON answer.

Writing the form against real requests found five plan defects, corrected in revision 44:
- `requestedRows` was in §23's hashed member order, although §10.4 and §10.5 put it beside the hash.
- A rate had no member for its numerator.
- Filter terms were sorted by dimension codes that did not exist; `EN-FilterDimension` now defines them.
- A snapshot entry named a generation number, which is local to one session; each entry now carries the manifest
  digest that pins its bytes.
- What a metric means was not a version axis; `metricsContract` is now §24's fifteenth.

The golden corpus `fixtures/FX-QUERY-001` pins twelve specifications to their bytes and identities, and all twelve
hashes match an independent SHA-256. Answering a structurally unavailable request now opens the generation's segments
first, because its identity names the snapshot, so no unavailable answer is asserted about a generation that would
not verify.

## Previous slice: off-thread graph layout and stale-result guard

`GraphLayoutScheduler` freezes every submitted graph and constraint, computes on a worker, and returns only the latest complete result. A new request invalidates and cancels the earlier one; even a worker that ignores cancellation cannot publish after a later request. Wrong-identity results are refused, window close cancels in-flight work, and caller cancellation is not misreported as supersession. The desktop shows the saved synthetic positions only as a first-frame fallback, then applies the complete layout on its UI continuation; headless frame tests wait for it. Five scheduler regressions cover these races and input freezing. This closes R7 for layout publication, not for the future numeric query bundle, which still needs coherent pane publication and P21 coverage.

## Previous slice: bounded, deterministic graph layout foundation

The desktop graph now reads a separate immutable layout result labelled by graph identity, not the sample nodes' stored X/Y coordinates. `GraphLayout` sorts inputs, seeds a reproducible per-group lattice from graph identity and process instance ID, keeps valid prior placements, honors hard pins, and relaxes edges and collisions for a fixed 120 steps while retaining the best-scored valid state. It refuses more than 512 nodes or 4,096 edges until compaction is available instead of silently omitting members; cancellation returns no partial result. Five new application regressions cover order independence, pins/prior positions and regrouping, band containment, explicit refusal and cancellation; a desktop test confirms that rendering consumes the separate layout coordinates. The minimum and review-size frames were rendered and inspected: the canvas transform and upper/lower label placement now keep node labels clear at 1080×700 and 1456×939.

This was a foundation, not completed IC-017. At revision 42 the five-node synthetic tour computed synchronously; revision 43 moved it off-thread. Real-session graph projection, numeric-bundle supersession, compaction/clustering, persisted pins and numeric/layout/paint bundle publication remain. Revision 42 corrected the plan's incompatible promises of wall-clock-capped relaxation and identical output regardless of wall-clock time: the result has a fixed work bound, while scheduling and cancellation protect responsiveness without publishing partial geometry.

## Previous slice: safe abandoned-staging cleanup

Every new staged file now has a paired root-owned `.lease` marker held exclusively from staging through completion and until publication or abandonment. A completed stage remains owned even after its data stream closes. `icat staging <directory>` previews abandoned, active, unmarked legacy and marker-only files without changing the session; its copyable `--confirm --expect-set <digest>` command rechecks under the publication lock and removes only the reviewed files whose owner has let go. Active writers, unmarked legacy staging and an interrupted file already renamed to a published name remain untouched. Normal `icat session`, pointer recovery and orphan removal still delete no staging. A disposed stage cannot later publish after losing its ownership. Six storage regressions cover active and abandoned ownership, stale-preview refusal, successful publication cleanup, legacy refusal, marker-only cleanup and disposed-stage refusal. This resolves cleanup for new guarded staging; older unmarked staging still requires manual review because no marker can prove its writer has stopped.

## Previous slice: transport relations and process focus

`tcp-endpoint-relation-v1` (`contracts/relations-v1.md`, ADR-014) finds the other end of every TCP record. It is the
process holding the mirrored endpoint pair, when every record at that end binds to one instance. The join key was
measured before the rule was written, as §7.4 requires. On the fresh IC-009 `peak` capture, all 12 connect, 8 accept
and 20 disconnect records named their owner's own endpoint first, exactly as its data records do, and none named it
mirrored. So one key serves all six admitted TCPv4 descriptors. The provider's connection identifier is not used,
since it is zero on this build and reusable. An end held by two instances or two PIDs stays ambiguous for the whole
capture, and one no record holds is `PeerNotObserved`, never read as remote from its address. Strength is
`Correlated`, or `Candidate` when the other end binds only as a reused PID's later instance; it is never `Direct`.

On top of it, `metrics-v1` now answers `participant(P)`, `sender(P)`, `receiver(P)` and `peer(P,Q)`. `--group-by peer`
(`EN-Grouping` 9) ranks the processes at the other end from a focus. Cross-side totals such as sent bytes measured at
the receiver now group by process through the relation instead of being unavailable. A filter discloses, by reason,
the records it left out only because their other end is unresolved, and states `PeerNotObserved` apart because it
involves the focus only if the focus's own records are missing. `owner(P)` with a cross-side total is now refused as
meaningless: it would relabel received bytes as sent. `icat processes --pid` became a detail view: instance id,
identity evidence, image path, parent, session, and what the instance sent to and received from each process at the
other end, with a copyable follow-up command. Three rough edges found while using it were fixed:
- `--side send`, `receive`, `endpoint` and `canonical` are accepted beside §23's full names.
- A grouped process label now includes the image name.
- The cross-side caveat no longer tells a grouped result to "group by process".

Measured over the fresh `peak` capture: 9 relations, one of them a process connected to itself. 74,681 of 74,788 TCP
records resolve their other end; the other 107 are the machine's own traffic to remote hosts. Deriving the relations
took 50 ms. The workload server's conversation is 34,300,070 B sender-accounted, all of it with the workload client,
equal to the byte to what the two processes sent each other. The client's sent bytes measured where they arrived are
34,168,998 B, the same figure its own send records carry. Received bytes measured at the senders rank the two
workload processes first, then a 1-byte process connected to itself. 44,525 B are unattributed as `PeerNotObserved`,
and the partition holds. Each query took about 1.6 s of wall time. On the 555-record baseline capture every TCP record
leaves the host: no relation is derived, and a participant is its own 243 records with 242 others disclosed as
`PeerNotObserved`. Evidence: `bench/results/relations-20260923T001907Z/relations-summary.json`, aggregates only.

## Previous slice: capture cost re-measured after process-name admission

Revision 30's bounded SID skip changed what the live process callback reads: start and rundown records now carry their image path. The elevated IC-010a impact series and both IC-009 load series were re-run on it from this shell (`capture-impact-20260922T234102Z`, `capture-comparison-20260922T234102Z-series` and `-series-stacks`, all Release). The live process journal of the first impact pair holds 502 full device image paths among its 537 records, against none in the 2026-09-21 journal, so these runs measured the changed callbacks rather than the old plan.

Nothing moved out of its band. Both sources still classify `Low` with `decisionReady: true`, and every capture trial was loss-free, drop-free and replayed exactly. Process: median classified CPU cost 0.91 pp (previously 0), per-pair 0/7.09/0.91 pp; the signed observations ranged from -2.78 to +7.09 pp, a wider spread than before on a machine that was also running other agents, and the median throughput regression was 0%. Network: median 0 pp and 1.29% throughput regression, per-pair 3.76/0/0 pp. The callback-admission p99 stayed in the same buckets at every level: [2,048, 4,096) ns at the unconstrained peak, and [1,024, 2,048) ns there with call stacks. No level reported an `AdmissionReadFailed` record, so the SID bound never refused a live record. The admission allocation slope was 0.00086 B/record, previously 0.00085, and 0.00044 with stacks. The unconstrained peak admitted 26,688 records/s at 99% queue depth. The call-stack series copied and replayed 73,310 of 73,310 extended items at peak and states no decision blockers; the clean series has only the blocker it always has, that a run requesting no extended data observes none. Names cost bytes: that process journal holds 302 bytes per record against 233 before. The regenerated IC-010 baseline `reference-machine-20260922T234102Z.json` again meets 3 budgets, misses sustained ingest (10,680 records/s with stacks against 100,000) and has 6 unmeasured.

## Earlier slice: explicit pointer recovery and occupied-generation skipping

`icat recover <directory>` now gives a read-only preview of a damaged current pointer and the fully verified last-known-good generation, including a copyable digest-bound confirmation command. `--confirm --expect-manifest <digest>` re-verifies that reviewed generation under the publication lock, durably backs up the damaged pointer when present (bounded to 1 MiB), re-points current and verifies the result. It never deletes an orphan, manifest, dependency or staging file. A missing pointer can be repaired without inventing backup bytes; a healthy one is a no-op, and a stale preview refuses to overwrite a newer publication. The next writer skips occupied immutable generation names so repair does not leave the session unable to publish over an orphan manifest; a generation staged before its number became occupied is refused rather than silently renumbered at commit. Six storage regressions cover those cases. This is a deliberate rollback to verified evidence, not automatic selection of a newer orphan generation; the preview lists what remains preserved for review (the first 12 files as text, every one in `--json`), states the pointer's condition in words rather than a bare yes/no that stayed "yes" after a repair, and prints the confirmation command on its own line (`confirmCommand` in JSON). A confirmation whose digest is stale changes nothing and exits 2 (invalid invocation) rather than 3, which reads as a permission failure. Walked through by hand on an imported and re-derived two-generation session with a damaged pointer: preview exit 1 with the five generation-2 files listed, stale digest exit 2, confirmation exit 0 with the backup named, and the next `icat rederive` published generation 3 over the preserved orphan generation 2. The same walkthrough exposed that `ConsoleUi.Field` ran any label of 22 or more characters into its value (`Owner process instance`, `Provider-reported loss` and five more across `metric` and `measure`); a label now always keeps two spaces before its value.

## Earlier slice: cross-process evidence lease guard

A reader now opens a shared root-owned lease guard before verifying the current pointer and acquiring a generation. Retention or explicit orphan cleanup takes the exclusive guard before physical deletion; if any process still has a reader, cleanup defers the released files. Newly published sessions create the guard before publishing the pointer, so a broker-owned read-only viewer needs only read access. An older session with no guard can be upgraded by a writable reader; a read-only viewer refuses it until that happens. Reader acquisition refreshes a stale snapshot without refreshing its writer publication baseline. Disposal, expiry and process exit release the OS hold. Five storage regressions cover independent stores, stale reader refresh, read-only viewer access, expiry without a later reader call and the stale-writer boundary. The guard is deliberately global and may delay cleanup of unrelated files; it does not persist a lease across process restart or reserve pinned quota across processes. Coordinated abandoned-staging cleanup remains separate work.

## Earlier slice: root-wide publication guard and safe staging recovery

Every session commit, replacement derivation and retention publication takes a root-owned exclusive `session-publication.lock` handle, re-verifies the on-disk pointer under that lock and refuses a stale writer or a pointer that only rolls back to last-known-good. Existing dependency and manifest target names are refused before an immutable file can be replaced. Independently opened stores can no longer publish generation 1 over each other. Opening a session reports and retains unreferenced `stg-` files rather than deleting work another process may have completed but not committed; explicit orphan removal skips staging. `icat session` explains the kept files. Storage regressions cover a stale independent writer, exclusive lock ownership, dependency and manifest collisions, stale retention and a staging file kept on reopen.

## Earlier slice: single-import publication safety

`icat import --into` now requires a new empty session directory even when `--overwrite` is present; the flag applies only to an optional report file. The import API itself refuses a store that already has a generation before reading the ETL, so a caller bypassing the CLI cannot append a second derivation of the same capture and silently double-count it. The refusal points to `icat session`, `icat rederive` or a separate empty directory. A focused regression proves the guard runs before opening an invalid ETL and leaves generation 1 untouched. During this slice, `contracts/import-v1.md` was corrected: its old “nothing writes a session” claim had been stale since canonical import gained `--into`. Reusing an existing completed import through a catalogue is still owed; refusing is safer than pretending an append is reuse.

## Earlier slice: read-only full re-derivation check and opened-handle integrity

`icat rederive <directory> --check` now reads the saved plan and every committed journal batch, validates schemas/policies and normalized observation/source-field rows, compares record counts with the boundary, and reports what it checked without staging a file or moving the generation pointer. The publishing command and the check use one replay path. Both re-measure the plan and journal SHA-256 from the opened handles before interpreting their bytes, closing a gap where an in-place change after store opening could retain the same length. The check's output explicitly says it has not tested a future segment write or commit. A focused regression proves a read-only check leaves the file list and generation unchanged, and refuses a changed plan or journal and cancellation without publication. This makes readiness two-stage: `icat session` reports structural eligibility; `icat rederive --check` provides full read/normalization validation.

## Earlier slice: visible re-derivation readiness

`icat session` now reports whether its verified manifest has the structural prerequisites for `icat rederive`: one committed journal matching the boundary and one bounded retained descriptor plan. The text view names an actionable refusal for empty, legacy, multi-journal, mismatched-boundary or ambiguous-plan sessions; JSON carries `rederivation.canAttempt`, its explanation and a copyable command only when an attempt is eligible. “Can attempt” is intentionally not “verified”: the saved plan and every journal batch are still validated during replay, before publication. This makes the new R20 path discoverable without misleading users into believing a manifest check proves replayability. Relation inspection found that a reusable, sometimes-zero TCP connection ID cannot prove a peer process or unique transfer lifetime; participant and cross-side owner totals remain unavailable rather than attributing them by guess.

## Earlier slice: replaying a published generation from retained evidence

`icat rederive <directory>` now streams the selected generation's own committed `journal-v1` batches, validates their source clock, schema/policy table, checksums and record count, and publishes replacement observation/source-field segments and dictionaries as a new generation. Newly imported sessions retain a bounded immutable `normalizer-plan-v1` dependency: the exact compiled descriptor interpretation needed to turn journal envelopes into fields. Re-derivation does not consult the machine's current TDH schema or the original ETL. Generation 2 carries the same journal and plan, while generation 1 stays last-known-good. A missing retained plan, multiple journals, a changed source generation or inconsistent journal is refused rather than guessed or partially published. The focused test replays four records across journal batches and proves byte-identical observation and source-field segments and stable raw identities. `contracts/normalizer-plan-v1.md` freezes the new dependency; `contracts/store-v1.md` describes replacement publication. This is the first measured R20 rebuild path, not a claim that a future normalizer algorithm is already implemented.

## Earlier slice: process-owner metric filters

`icat metric --owner <process-instance-id>` now scopes counts, byte totals, rates, grouped breakdowns and evidence listings to one process instance using `process-binding-v2` and the selected evidence policy. The selector is an instance id from a process-grouped result, never a PID or header PID. Rows outside the interval, owner filter and layer/mechanism projection have separate, non-overlapping exclusion counts. A process instance absent from the selected generation is unavailable, not an observed zero. Cross-side owner totals remain unavailable without a proven transfer association. `--participant <process-instance-id>` is accepted with an explicit `NoParticipantRelations` result until relation derivation exists; it never silently means owner. JSON names both selectors, the binding rule and owner exclusions, and the text view labels the selected instance and policy.

The focused fixture selected the created/exited PID 100 instance: four of nine records bound to it, five were excluded by the owner filter (including a record after its exit), and the four evidence identities matched. Its mechanism groups partitioned the filtered four; its sender-accounted bytes were 100 B from exactly one evidence record. Participant and cross-side owner requests failed closed; a missing instance returned `ProcessInstanceNotFound`. The complete test counts are recorded below. This slice does not remeasure the changed live callback cost or capture impact; that elevated experiment remains next.

## Earlier slice: source fields and process identity

The interrupted source-field slice has been completed. `source-fields-v1` adds a second segment table without changing any `observation-v1` column. Canonical ETL import publishes the numeric correlation/object fields its admitted descriptors carry; `icat session` displays their counts. `process-binding-v2` joins lifecycle rows to those fields, uses provider start sequences where present, splits contradictory keys with explicit gaps, and retains image paths, sessions and named parents. `icat processes` shows image names and exposes the full evidence in JSON. `icat metric --group-by executable` groups only by witnessed full path, leaves a missing path unattributed, and says unavailable if no path was witnessed. The committed original journal remains the source of every row.

Verified on the recorded 854-delivered-record baseline ETL: 852 admitted observations and 3,104 source-field rows published, including 592 process start sequences, 534 parent PIDs and sequences, and 260 TCP connection IDs. The reopened generation ranked 852 observations by executable path; the leading path held 157 and the only post-exit record stayed unattributed. Focused storage, capture-schema and analysis tests pass; full-suite totals and the commit are recorded below. This is an ETL replay measurement, **not** a new live-callback performance measurement. The bounded SID skip now admits start/rundown image paths; the live callback cost and capture impact of that changed plan were re-measured on 2026-09-23 (latest slice above).

## Current outcome

The repository has a buildable .NET 10 solution, enforced module boundaries, a schema-driven capability inventory, an owned ETW session with an independent health ledger, seeded truth workloads, and measured TCP, named-pipe and RPC feasibility results whose coverage tiers are computed rather than argued. IC-007 supplies the production identity and local-clock foundation. **ADR-008 is accepted: the admitted journal is InterCat's authoritative live source**, with a companion ETL kept as separately labelled original evidence. The gate closed on 2026-09-21 when two things changed - ADR-007 added Windows 11 25H2 to the §1.3 matrix, and a defect that read the ETL session's loss counters *after* stopping the session was fixed, so its loss became a measured zero rather than an unreadable default. IC-009 has been measured across a declared load series, not at a single point: the owned callback envelope copies bounded arbitrary ETW extended-data items, persists the identity-v1 source clock descriptor with the file, and carries an isolated per-stage overhead ledger, and `InterCat.CaptureComparison` runs five declared levels through both evidence variants from an elevated shell. The series reaches the bounded queue's limit and counts its drops without loss, and makes the writer the pacing stage. Every level of both series replayed with an identical ordered fingerprint. All five of ADR-008's gate inputs are recorded, the call-stack series reports `decisionReady: true` with no blockers, and IC-011 may begin under the three conditions that ADR records. One of those conditions turned out not to exist: ADR-008 carried a measured debt of about 1.2 KiB of allocation per admitted record and named pooled buffer ownership as IC-011's fix, but that figure was a thread total, and the delivery thread runs the adapter's dispatch and InterCat's admission on the same stack. Separating them showed **admission allocates 0.00085 bytes per record** - admitting a hundred times more records costs 64 more bytes in total. The allocation is the managed adapter's real-time dispatch, which InterCat cannot pool; §18.3 already allowed replacing it, and IC-019 now evaluates that on the measurement rather than on a suspicion. ADR-009 records the method and the result. IC-010a now measures each source against no capture: process and network both classify `Low`, with no loss, drops or replay mismatch. The frozen journal-v1 format and its live writer are implemented. IC-012 now has production Explore and Focused TCP profile compilers: every admitted descriptor carries a compiled body policy and a SHA-256 identity over schema, layout and policy; duplicate descriptors are refused; unknown descriptors and unknown versions have distinct health attribution; and `icat profiles` previews requested/effective sources, real provider scope and exact provider settings without starting capture. Explore requires the measured process and network sources and visibly omits unmeasured optional RPC, ALPC and pipe sources. Focused TCP accepts optional PID focus but never presents it as retention scope: required lifecycle context and the unfilterable network provider remain whole-machine, so start is blocked until broader collection is explicitly accepted. Timing, Content and Flight recorder remain exact refusals rather than fallbacks. IC-014 now has a non-starting broker control core: local plans are revalidated/frozen and named by a deterministic digest that includes reviewed duration/storage/retention limits; 256-bit prepared tokens are stored by hash, expire, bind to authenticated SID/logon and start at most one capture; intents/results are flushed as bounded checksummed snapshots; duplicate request IDs replay exactly; lease expiry and stop milestones are modeled; reopen/recovery handles torn, corrupt and interrupted state using persisted session tokens. Its bounded typed request and response schemas cover all seven commands, user-facing errors, capabilities, effective Prepare review and lifecycle/status outcomes. The per-connection dispatcher requires Hello, preserves correlations, refuses duplicate in-flight IDs, keeps owner credentials out of status, and connects broker-local compilation to lifecycle under an authenticated identity. The real Windows pipe boundary creates a first-instance, local-only native pipe with a protected SYSTEM/user DACL, impersonates the client, derives SID/logon/integrity/elevation from its token, matches SID plus logon session and treats PID as diagnostic only. The ordinary client cannot supply compiled provider/body settings. The broker also owns its filesystem boundary now: the root is created with a protected SYSTEM/Administrators DACL, a read-only entry for the capturing user and a high-integrity `no-write-up` label in the creating call, validated from the open handle by reparse state, final path, volume and an ACE-for-ACE security read-back, and held open without shared delete so it cannot be renamed away from under later writes. Real junctions substituted for the root and for its parent are refused rather than followed, and a token lowered one mandatory level reads the evidence back while every write beneath the root is refused. The ownership log compacts itself beneath that root: it publishes the current snapshot as the single frame of a new generation and replaces the live log in one directory operation, so a restart finds either the whole previous log or the whole new one and never a half-written name. IC-015a completes the §20.1 physical store: `contracts/segment-v1.md` freezes the `observation-v1` segment and its dictionaries, and an import now publishes a session rather than an index. `icat import <etl> --into <dir>` writes the admitted records as the session's own `journal-v1` evidence, derives one normalized row per record, sorts them into time-ordered columnar segments with null bitmaps and availability counters, publishes each segment's sorted dictionaries beside it, and commits the generation with ADR-010's boundary naming the journal's durable prefix and digest. `icat session <dir>` reopens it, re-verifies every dependency, and reports the evidence boundary, each segment's column availability and each byte domain and side separately. Evidence leases and retention are in: a reader acquires a generation and every dependency it names as one lease, retention publishes what it released and why instead of merely deleting, released bytes wait for the last reader holding them, and `icat retain` measures what releasing a journal prefix would give up before a separate decision performs it. IC-015 has begun with what a number means: `contracts/metrics-v1.md` freezes §5.3's matrix as a compiler for the source-observations basis, and `icat metric` answers a count, a byte total or a rate over every segment of a published generation, scoped by layer, mechanism and a half-open interval given in native ticks or in seconds after capture start. A request the matrix refuses is refused before a session is read, with the compatible metrics named; one it permits that a session cannot derive - operations, topology, entity instances, a status domain, a proven transfer association - is reported as unavailable with its reason; and a byte total that takes no known contribution is unavailable rather than zero. Building it corrected the plan (ADR-012): endpoint activity is now a metric of its own, `EndpointActivityBytes`, because as an accounting side of `BytesSent` it contradicted the metric's own direction, and a row's side is now distinct from a request's accounting. A total can now say which process: `contracts/entities-v1.md` derives process instances from a session's own lifecycle records - created, exited, running at capture start, or witnessed only by its own records - keys each one by identity-v1 rather than by its PID, and binds every record to the instance whose witnessed lifetime holds its reading, with a strength. `icat processes` lists them, and `icat metric --group-by process|mechanism` ranks any total with an exact remainder and the unattributed contributions by reason, the groups partitioning the total. On the IC-009 `peak` session it finds the two workload processes as instances created and exited in the capture, each other's 34,168,998 B, among 482 witnessed by the capture-state rundown. And it can say who talked to whom: `tcp-endpoint-relation-v2` finds the other end of every TCP record whose mirrored endpoint pair one local process holds, which the `participant`, `sender`, `receiver` and `peer` filters and the peer ranking use, disclosing every record whose other end it cannot resolve. Live ETW/journal integration is the remaining blocker. Scoped-content admission, the entity-state checkpoint of §20.2 and mechanism breadth beyond the measured probes are not implemented.

`FX-TCP-001` reaches tier **`TrafficVisualization`**. It has met all six §14.2 criteria at 100% since its first measured run - 48 of 48 truth operations observed, bound and byte-measured, zero false peer attributions, byte totals equal to the truth log - and what held the tier down was the build, not the evidence. ADR-007 put Windows 11 25H2 into the §1.3 matrix, the corpus was re-run on it, and the tier followed. The counters did not improve; the question of which builds we support was answered.

`FX-RPC-001` measured local RPC: every truth call was observed, bound to the expected interface and paired with its completion, reproduced across two runs in one capture. RPC stays `ExperimentalEvidence` on a supported build, which is the clearest statement of its limits: the gaps are the source's, not the build's. It carries no size on any descriptor and an interface has no lifetime to discover. ADR-004 records the resulting scope, including that no RPC volume claim may be made anywhere in the product.

The workspace prototype now carries the §3.2 L0-L5 ladder over synthetic data: one gesture per rung on the keyboard and the pointer, an ascent that restores exactly the rung it left, a breadcrumb that always states the position, filters a descent implied shown where they can be removed, evidence one step from anywhere, and a rung with no data naming the source that would supply it instead of showing an empty pane. Eight ladder invariants are generated over random descent paths, and a headless Avalonia lane delivers real keys to a real window. The prototype also carries a measured palette, table equivalents for both canvases, the §6.6 diagonal coverage hatch, and a rescored interaction review taken from the running application.

`FX-PIPE-001` answered the plan's hard NPFS feasibility gate with a measured negative: `Microsoft-Windows-Kernel-File` produced 2,472 records from the workload's own processes in the same capture and not one create, read or write for the pipe object. Named pipes are `Unsupported` by measurement, and re-running on a supported build produced the same 0 of 25 with the same control - a measured absence does not change when the build enters the matrix. ADR-003 records the resulting scope decision instead of restating the original goal as achieved.

## Backlog status

| Item | State | Evidence / remaining work |
|---|---|---|
| IC-001 solution and boundaries | Complete for M0 | `InterCat.slnx`, central build/package settings, ADR-001, 5 architecture tests. Build has zero warnings. |
| IC-002 capability/schema inventory | Complete for M0 | `icat capabilities` reads the TDH manifest per registered provider, compiles the admission plan those schemas allow, and reports registration, enablement, observed health and semantic coverage as four separate checks with exact fields, units, attribution roles and unavailable reasons. Contract: `contracts/capability-report-v1.md`. Artifact: `capabilities/10.0.26220.0-x64/windows-etw-inventory.json`. Remaining: classic/kernel (non-manifest) source inventory beyond the ALPC placeholder. |
| IC-003 owned ETW lifecycle | Complete for M0 | `OwnedCaptureSession` implements the §9.2 states, unique name plus ownership token, allowlisted provider plans, event-id scoping, bounded queue with independent drop counters, post-start rundown, and cleanup limited to the session an attempt created. 8 lifecycle tests run against a fake host with no ETW or elevation. ADR-002 records the strategy. |
| IC-004 TCP/RPC truth workloads | Complete for M0; UDP added in M1 | `tcp-loopback`, `udp-loopback` and `pipe-loopback` each run a seeded two-process exchange, and each process writes its own JSONL truth log from ordinary socket or pipe calls. The pipe scenario includes a deliberately short read so requested and completed sizes differ (§21.1), and `rpc-local` issues a known number of local RPC calls through the documented service API with no package dependency. |
| IC-005 pipe/section feasibility | Named pipes measured on a supported build; sections not started | `icat measure pipe` runs `FX-PIPE-001` under the owned session and computes the tier. Result on this build: `Unsupported`, with a 2,472-record control proving the source was live for the same processes. ADR-003 records the scope decision and routes the gap to M7 and M9. Shared sections remain a lead only. |
| IC-006 ALPC/RPC feasibility | RPC measured on a supported build; ALPC not measurable in M0 | `icat measure rpc` runs `FX-RPC-001` twice in one session and computes the tier: 8 of 8 calls observed, bound and completion-paired, tier `ExperimentalEvidence`, gaps recorded in ADR-004. Server-side records arrive but share no activity id with the client, so peers stay unresolved rather than inferred. ALPC remains a kernel flag group with no registered manifest provider and no tier claimed. |
| IC-007 IDs and clocks | Complete for its live/local M0 scope | `contracts/identity-v1.md`, ADR-005 and ADR-006 define raw and normalized observation IDs, process/resource lifecycle epochs, alias revisions and native/local/wall/workspace time. `FX-IDENTITY-001` asserts PID and resource reuse, late old-key resolution, cross-host isolation, immutable alignment, checked QPC conversion and quarantine. The full I1/I2 standalone-ETL proof remains correctly assigned to IC-013 rather than being claimed here. |
| IC-008 interaction prototype | Complete for M0 | Equal panes, the L0-L5 ladder with reversible navigation, a breadcrumb, visible removable filters, per-rung ranked tables reaching individual source records, inspector, legend with glyphs, table equivalents, the §6.6 diagonal coverage hatch, a measured theme (`theme/`) enforced by tests, 77 randomized property tests including 8 ladder invariants, 11 headless UI tests that deliver real keys to a real window, and the rescored §17 review in `docs/reviews/M0-interaction-review.md` (13 of 20, with 12 defects found and fixed). Remaining and deferred: per-level graph and timeline composition, which needs IC-017's deterministic layout. |
| IC-009 journal/admission validation | Complete for M0; ADR-008 accepted | `FX-JOURNAL-001` asserts unknown-schema/content omission, per-record attribution, extended-data replay, owned copies, corruption refusal and clock-frame refusal. The callback envelope copies up to four extended items of up to 64 bytes with original lengths and truncation flags, persists the source clock descriptor as the file's first frame, and reports callback latency, delivery-thread CPU and allocations, queue high-water, writer CPU and per-flush latency as separate quantities. `LoadSeries` declares five levels: three raise the workload's rate and two shrink a bound instead, because a machine faster than the fixture never reaches its queue by running the fixture harder. `StorageProbe` measures the output volume against the §12 reference device before any capture starts. Six single-point and series runs are recorded in `bench/results/`. The two series that accepted ADR-008 were recorded on the disposable probe format; both have since been re-run writing `journal-v1`, and the call-stack series still has no decision blockers. ADR-008 is accepted. |
| IC-010 benchmark baseline | Complete for M0 | `icat bench` publishes the reproducible baseline: this machine measured against the §12 reference, the whole §12 budget table with what measured each entry, and the per-build validation backlog. `--series` folds an IC-009 run into it, so the two artifacts agree rather than each carrying half the table. The fixture is no longer the ceiling: the TCP workload runs connections concurrently, which took the unconstrained peak from about 20,000 to 24,474 admitted records per second and drove the queue to 98% of its bound. Every ETL replay also attributes its allocation between admission and the adapter by replaying the same evidence twice with only the admission table differing, and the series fits the slope across its smallest and largest level. On this machine 3 budgets are met, 1 is missed and 6 are unmeasured, and the missed and unmeasured ones are named. `OverheadClass` has a calculator and declared bands; it remains `Unmeasured` in evidence until IC-010a is run from an elevated shell. |
| IC-010a capture impact | Complete on the current supported build | `InterCat.CaptureComparison --impact` runs each admitted source independently through three paired no-capture and journal-v1 capture trials with alternating order and a declared multi-second Explore workload. It reads Windows `GetSystemTimes`, retains signed CPU and throughput differences in every pair, classifies only non-negative cost, and requires admitted records, zero loss/drop and exact replay. Current evidence, re-measured after the SID/name admission change: `bench/results/capture-impact-20260922T234102Z/impact.json`, `decisionReady: true`. Process: median observed CPU difference +0.91 pp, classified cost 0.91 pp (`Low`), throughput regression 0%. Network: -0.82 pp observed, 0 pp classified (`Low`), 1.29% throughput regression. All six capture trials were loss-free and replayed exactly. The 2026-09-21 series (`capture-impact-20260921T200502Z`: process 0 pp, network 0 pp classified) measured the plan before image paths were admitted. A first 2026-09-21 run exposed Windows sub-tick delay rounding and was cancelled; its partial directory is explicitly not evidence. |

**M0 and its IC-010a follow-on are complete.** M1 has completed IC-011, IC-012, IC-015a and IC-016 for the currently validated sources; IC-013 composes with them into a session a reader can open, measure and retain from, and IC-014 and IC-015 are underway.

| Item | State | Evidence / remaining work |
|---|---|---|
| IC-011 journal-v1 contract and owned envelopes | Complete: frozen, implemented, and the live capture path writes it | `contracts/journal-v1.md` is the frozen format: a 64-byte header carrying the capture identity, a source clock frame and a schema and policy table before any batch, length-delimited batches declaring the first and last identity they carry, a per-record CRC-32C beside the per-frame SHA-256, and a terminal frame without which a file is refused as an interrupted capture. `RecordEnvelopeV1` carries every §18.1 field including the buffer context, the pointer width, the native reading in its own encoding, each extended item's original length and each body's disposition. Buffers are pooled leases with one owner at a time: a returned lease refuses every read, disposing twice is not a second return, and writing or disposing returns them. `FX-JOURNAL-002` holds the golden corpus. The clock frame is mandatory and singular: a journal that names no clock, or two, is refused rather than read against an assumed one, so I8's prohibition on converting a native reading cannot be reached by a malformed file. `InterCat.CaptureComparison` now writes `journal-v1` from the callback, and the elevated series re-ran on it: every level of both series replayed with an identical ordered fingerprint, the clock replayed identically at every level, and the call-stack series copied, persisted and replayed 77,355 of 77,355 extended items with no decision blockers. The disposable `.ijp0` framing has been deleted. Durability and the committed-boundary protocol stay IC-016's, so the comparison harness opens the file write-through and flushes each batch itself rather than the format pretending to own a guarantee it does not. |
| IC-012 capture profiles and body admission | Complete for currently validated sources; Content remains correctly unavailable | `CaptureProfileCatalog` exposes all five §9.4 intents and separates executable capture preview, request-contract preview only, and unavailable states. Explore compiles required process/network sources only when schema and measured impact permit them; optional RPC, ALPC and named-pipe breadth is shown as omitted with exact reasons. Focused transport requires an explicit mechanism and currently validates TCP only. Optional repeated PID selections become initial-view focus; each source records whether it is provider-filtered, whole-machine by request, required context, or unfilterable. Because TCP delivery cannot be PID-filtered and lifecycle context stays broad, a PID-focused request blocks until `--allow-broader-capture` records explicit consent. Provider PID-filter plumbing exists for future catalog sources that can enforce it. Content compiles exact source/process/channel selectors, per-record and session caps, stop-at-limit retention, separate disabled/hex-text inspection consent, prefix truncation with original length, and unknown-schema/out-of-scope omission. It deliberately emits no `BodyPolicy`, provider requests, approved events, fields or classifications and keeps `canStart: false`: no catalog source has a validated payload contract, pre-persistence scope proof and payload-specific impact evidence. Metadata-only impact measurements cannot qualify content settings. The immutable result keeps exact environment/adapter identity alongside requested/effective profile and admission, source/scope/content decisions and separately labelled original-ETL policy. `metadata-only-admitted-projection-v1` remains the only production policy; its schema fingerprint covers layout, admitted slots and policy. Contract: `contracts/capture-profile-preview-v1.md`. Timing and Flight recorder remain explicit refusals, not aliases. Future payload adapters extend the catalog evidence; they do not weaken this completed refusal boundary. |
| IC-013 canonical import | In progress: the contract is frozen, its portable core is implemented, and a real ETL now imports end to end into a session a reader can open | `contracts/import-v1.md` freezes source identity, import identity, the canonical record key and its exclusions, the ordering an import may claim, equal-time multiplicity, the bounded spill/cancellation path and the refusals. `CanonicalImporter` lives in `InterCat.Storage`, so it needs no elevation and opens no privileged handle by construction. A journal keeps its own acquisition ordinals; a standalone ETL is keyed and counted, because `ProcessTrace` delivery order is not reproducible. Eighteen tests cover it, including that an import which spills to disk produces exactly the index it would in memory, that a cancelled or refused import leaves no spilled run behind, and that reading the same records in the opposite delivery order yields identical keys, instants and multiplicities. `InterCat.Capture.Journal` composes the bridge - hash, derive and check the clock, replay through admission, map to envelopes, key and release - and `icat import <source.etl>` runs it unelevated. Imported evidence names no clock of ours, so both its clock and its host are derived from the file's content identity: minting either per run would make two imports of one file disagree about their records' identities, because the clock ID is inside the canonical key. The adapter library does not expose the recorded tick rate, so it is derived from the file's own readings and checked against every sampled one, and a file that does not reproduce is refused rather than read at an assumed rate. An import now has somewhere to import into: `--into <dir>` publishes the admitted journal and the segments derived from it as one generation, and the capture identity is derived from the import identity rather than minted. Implementing that found a defect: the importer called `CaptureId.New()` once per record. The canonical record key excludes the capture id, so keying was unaffected and no test caught it, but the capture id is inside every raw-record and observation identity, so a session built from those records would have had a different capture per row. ADR-011 and §18.4 record the rule. Remaining: reusing a matching completed import from a catalogue, derivation generations for normalizer upgrades, and journal/ETL overlap disclosure. |
| IC-014 broker protocol, leases and idempotency | In progress: prepare, ownership/recovery/compaction, request framing, authenticated pipe and broker root complete; no service/start surface | `BrokerPrepareCompiler` freezes locally compiled allowlisted plans; token/lifecycle/store layers provide owner binding, exact idempotency, leases, five stop milestones, checksummed flush-before-success snapshots and conservative restart reconciliation with separate session-token proof. The wire layer adds a fixed 32-byte header, 64-KiB payload cap before allocation, 64-field/16-KiB-field TLV bounds, strict UTF-8/list/type/duplicate checks, unknown optional versus required semantics, arbitrary-fragmentation reads and typed requests for Hello, capabilities, Prepare, Start, Status, Stop and Renew. Prepare maps only canonical profiles plus bounded typed scope/quota/retention; Content fields are all-or-none. Hello negotiates required versus optional features. The broker-owned root is created with its protected DACL and high-integrity `no-write-up` label in the creating call, validated from the open handle by reparse state, final path, volume and an ACE-for-ACE security read-back, and held open without shared delete so it cannot be renamed away; every broker file is opened by a single allow-listed name beneath it and `FileBrokerLifecycleStore` now takes that root instead of a path. Compaction publishes the current snapshot as the single frame of a new generation through that root's validated rename, refuses before writing anything if one frame cannot fit the bound, discards the temporary of an interrupted compaction on reopen, and reclaims superseded frames without ever dropping a record, because dropping a completed request would turn its replay into a second capture. An append that would pass the bound compacts first and only then refuses. One hundred and fifty-seven broker tests cover prepare, lifecycle, durable corruption/reopen, 17 wire cases including every truncation point, the authenticated pipe, 34 root-boundary cases and 9 compaction cases. Remaining: binding the broker to `LiveSessionRecorder`, which `icat record` already runs directly (ADR-021), and a session root the viewer can read. |
| IC-016 durable commit, recovery, leases and retention | Complete for its M1 scope; IC-016a holds what is left | `contracts/store-v1.md` freezes the session root, the manifest and pointer formats, the commit sequence and what recovery does. A generation stages files under unique names, completes them by flushing to the device and measuring them, publishes them under immutable names, re-measures every dependency before naming it in a manifest that carries its own digest, and only then replaces the pointer - retaining the previous one as last-known-good. Opening verifies the pointer, the manifest, its digest, the pointer's record of that digest, the generation numbers and every dependency's length and digest, and falls back to the last-known-good with a stated reason when any of it fails. ADR-010 settles the journal-lifetime decision §20.1 owed: a session retains its journal, releasing any part of it is an explicit retention checkpoint, and every generation carries the committed boundary it derives from. `DerivedGenerationBuilder` composes the sequence for a real derivation: the journal is staged first and its pending batch is flushed to the device before any segment derived from it is staged, so a generation can never name evidence that was not durable when it was derived. A reader now acquires the generation and every dependency it names as one `EvidenceLease` with a kind and an expiry: interactive leases are short and reserve nothing, a pin declares the allowance §10.1 requires and is refused when it would reserve less than it pins, and an expired lease holds nothing and is not revivable. Retention publishes a generation that no longer names what it released, with a record stating the kind, the extent, the files and the reason - a release with no stated reason is refused, because it is indistinguishable from data loss. Releasing and removing are separate: the bytes go when the last lease on them does, and a held file is reported as awaiting release rather than removed behind its reader. `JournalRetention` implements ADR-010's journal release by publishing a shorter complete journal rather than truncating one, in whole batches, naming the boundaries a journal allows, and refusing a release that would leave no admitted evidence at all. Remaining is IC-016a - §20.2's entity-state checkpoint and I20's open-operation censoring, both of which need IC-015's entity and operation revisions - and binding the protocol to the broker's root for live capture. |
| IC-015a derived segment format | Complete for observation and source-field tables | `contracts/segment-v1.md` freezes the immutable columnar container and both table layouts. The original 39-column `observation-v1` bytes are unchanged. The additive `source-fields-v1` table (`fld-` segments) carries one numeric or text source field per observation and field code, joined by raw locator and fact key, sorted and checksummed with the same availability/null-bitmap rules. Import publishes both tables and their dictionaries in one generation; `icat session` reports source-field counts and kinds. The reader refuses invalid table shapes and values, and the writer refuses duplicate fields within one segment. Three new storage tests cover round-trip, reproducibility and refusal. |
| IC-016a retention checkpoint | Not started: process instances now exist, the rest of what it carries does not | §20.2 requires a rolling eviction to publish the still-live process, thread and resource identities, the endpoint bindings, the continuity quality and the pending-operation summaries it carries across the boundary, and I20 requires an operation open across one to be censored rather than failed. IC-015 now derives process instances, so a checkpoint could name the ones live at a boundary; threads, resources, endpoint bindings and operations are still underived, so the retention record carries what was released and no fields nothing can fill: a field that is structurally present and always empty cannot be told from a genuinely empty interval. |
| IC-015 metric contributions and query basis | In progress: source-observation metrics, process/executable/peer grouping, process filters and TCP and UDP relations implemented; operations and topology owed | `metrics-v1` compiles the §5.3 matrix and `SessionMetrics` answers counts, byte totals and rates over a leased generation with exact exclusions and evidence. `process-binding-v2` derives instances from lifecycle records plus `source-fields-v1`: provider start keys, names, sessions and parent links when the evidence carries them; older generations fall back to witnessed creation. `icat processes` and grouped metrics show process identity and path, preserve candidate and unresolved reasons, and make executable grouping unavailable when no full path was witnessed. Ordinary additive groupings partition the total; peer and channel rankings explicitly do not. `tcp-endpoint-relation-v2` finds a TCP record's other end through the mirrored endpoint pair, so `owner(P)`, `participant(P)`, `sender(P)`, `receiver(P)`, `peer(P,Q)` and `between(A,B)` filter totals, groups and evidence, cross-side totals group through the relation, and `--group-by peer` ranks a focus's counterparts, with every undecided record disclosed by reason. Relations are scoped to witnessed connection incarnations, and `ActivePeers` and `ActiveChannels` count process instances and incarnations as lower bounds beside what they cannot identify. Focused `ActiveChannels --group-by peer` now counts each counterpart's distinct channels and keeps unresolved peers unattributed. Remaining: relations for UDP, IPv6 and non-TCP mechanisms, per-transfer associations and the canonical owner, operation and topology derivations, live coverage epoch publication, desktop coverage publication and the entity-state checkpoint. |
| IC-017, IC-018 | IC-017 layout and off-thread publication started; IC-018's canonical query identity frozen for metrics; remaining work substantial | The desktop consumes an immutable graph layout result from a pure, deterministic and bounded core. Its scheduler freezes inputs and discards superseded or wrong-identity layout results, and the viewer accepts non-tour snapshots under their own graph identity with truthful time and coverage labels. The default remains synthetic. A leased real-session overview now carries both the all-observations timeline and an exact graph-eligible timeline joined through relation channel IDs; it is not yet the desktop's synchronized ladder: desktop application of ledger-backed coverage, channel/operation/evidence/byte projection, launch/open flow, coherent numeric-pane scheduling, compaction, persistent pins, caching and numeric/layout/paint bundle publication remain. `contracts/query-identity-v1.md` freezes the canonical specification and identity hash for the metric members, with the `FX-QUERY-001` golden corpus, and every `icat metric` answer names its identity; the UI's graph projection, viewport, query generation and cursors are not in it yet. The commit protocol still treats a dependency as an opaque named file with a length and a digest, so the remaining formats can arrive without changing it. |

The latest IC-016/IC-015a increment adds replacement derivation generations: `CommitReplacingDerived` keeps one committed journal and its retained descriptor plan while replacing old derived files, with the previous manifest held as last-known-good. `JournalV1Reader.ReplayBatches` validates and streams one owned batch at a time. `JournalRederivation` checks the saved plan against the journal table before normalizing and rejects legacy no-plan sessions; since ADR-023 it replays a live recording's journal chunks as one capture. This closes the first rebuild-from-evidence path; live broker publication, algorithm-version upgrades and a migration for older sessions remain separate work.

The load series of 2026-09-23 (`capture-comparison-20260922T234102Z-series`) is the current state of the evidence gate: the capture path writes `journal-v1` and admits process image paths. The host measured 24 logical processors, 64 GiB and an `E:\` volume at about 2.0 GB/s sequential write and 8.7 GB/s read, which meets the §12 reference machine and device. The 2026-09-21 series, measured before image paths were admitted, is kept beside it and reached the same outcome at every level:

| Level | messages | admitted/s | queue high-water | drops | journal vs ETL | writer busy | outcome |
|---|---|---|---|---|---|---|---|
| paced | 16 | 249 | 669 of 65,536 | 0 | 0.18 vs 2.13 MiB | 1.8% | headroom |
| steady | 2,048 | 4,123 | 6,055 of 65,536 | 0 | 2.27 vs 3.25 MiB | 4.6% | headroom |
| peak | 16,384 | 26,688 | 64,782 of 65,536 | 0 | 16.95 vs 10.13 MiB | 11.7% | queue pressure |
| queue-bound | 16,384 | 2,970 | 512 of 512 | 65,386 | 1.95 vs 9.88 MiB | 2.2% | queue saturated |
| disk-bound | 16,384 | 22,904 | 65,536 of 65,536 | 5,275 | 15.27 vs 10.00 MiB | 134.4% | queue saturated |

Every level of both series replayed its admitted projection exactly and read its source clock back unchanged. The same series with call stacks requested copied, persisted and replayed 73,310 of 73,310 extended items at its peak level and states **no decision blockers**; the clean series states one, that a run which does not request extended data observes none.

Two measurements are worth naming because they contradict the headline a single low-volume run suggests:

- **`journal-v1` is 29% denser than the probe format it replaced** - 231 against 327 bytes per admitted record, measured at every level of the same series - but it still carries no dictionary. A provider, activity, related-activity and clock identifier are sixty-four bytes on every record, and the clock frame already names the capture's only clock. Compaction is §20.1's derived store, not this format, so the number is recorded rather than optimized here.
- **An admitted journal is smaller than a diagnostic ETL only where the ETL is carrying the same payload.** At the `paced` level the ETL is 17× the journal because it has pre-allocated buffers it never filled; at volume without call stacks the journal is about 1.6× the ETL per record; with call stacks requested, where the ETL carries the stacks too, the journal is 23.9 MiB against 62.8 MiB. The 226 KiB-against-1.63 MiB figure from the single-point baseline is the first of those three cases and is not a general claim.

## Implemented structure

- `InterCat.Domain`: `Ids/`, `Time/`, `Measurements/`, `Capabilities/`, `Observations/` — wire enums; deterministic raw/observation/process/resource identities and a deterministic local host identity; lifecycle epochs and alias revisions; native-clock descriptors, checked conversion and quarantine; a bounded allocation-free latency histogram that reports quantile bounds rather than interpolated values; half-open ranges; pure viewport math; capability/tier contracts; and the M0 observation and truth-record shapes.
- `InterCat.Storage`: the §20.1 commit protocol - `SessionStore` with its staged/completed files, immutable published names, re-measured manifest dependencies, replacing pointer and last-known-good rollback, `EvidenceLease` holds that retention cannot break, `ReleaseDependencies`/`ReleaseJournalPrefix` and `JournalRetention` for the retention ADR-010 requires, over the `IOwnedDirectory` boundary the broker's validated root and an ordinary session directory both satisfy - the frozen `segment-v1` derived store in `Segments/` - the `observation-v1` column set and row shape, `SegmentWriterV1` with its column buffers, canonical sort and per-segment dictionaries, `SegmentReaderV1` with its extent, order and checksum verification and a searched half-open row range, `SegmentDictionaryV1`, `SegmentMeasurement`'s one-pass per-side domain measurement and the one-domain one-side byte sum built on it, and `DerivedGenerationBuilder`/`SessionSegments`, which publish and reopen a generation through the commit protocol and read the source clock its journal describes without decoding a batch - the canonical importer of §18.4 - source and import identity, canonical record keys, the bounded external-sort index with spill and cancellation, and equal-time multiplicity - and the frozen `journal-v1` format - `RecordEnvelopeV1`, its codec, the schema and policy tables, the pooled `EnvelopeBuffer` lease, CRC-32C, and a writer and reader over the framing. This is what the live capture path writes. Alongside it the portable `JournalProbe` admission policy, semantic envelope, attribution summary and bounded checksummed batch codec remain as the disposable experiment behind ADR-008's admission decision; they are not a session format and the `.ijp0` file framing that once carried them is deleted.
- `InterCat.Benchmarks`: the machine and volume probe behind IC-010's baseline, the per-build validation backlog, and the baseline report shape. Portable; it references only the domain.
- `InterCat.Analysis`: `Tcp/TcpCoverageEvaluator`, `Pipes/PipeCoverageEvaluator` and `Rpc/RpcCoverageEvaluator` — pure, portable comparisons of a truth log with admitted observations, producing the §14.2 counters and their evidence, including the control count that makes a negative result trustworthy — and `Metrics/`: `MetricCompatibility`, §5.3's matrix as a compiler with the one mapping from a request's accounting to row sides, and `SessionMetrics`, which answers a materialized `MetricRequest` over every segment of a leased generation with its per-side breakdown, exclusions, unavailability reason and evidence, ungrouped or grouped by process instance, executable or mechanism — and `Entities/`: `ProcessInstanceIndex`, the process instances one capture's segments support and the `process-binding-v2` rule that binds a record to one — and `Relations/`: `TransportRelationIndex`, `tcp-endpoint-relation-v2`'s ends, holders and each record's other end, which `Metrics/ProcessFilters` turns into the process-role masks, counterparts and undecided-record disclosures the filters and peer grouping use.
- `InterCat.Application`: the §3.2 detail ladder as a pure navigator, its projection over a snapshot, the deterministic synthetic workspace with data at every rung, and the single revisioned selection coordinator.
- `InterCat.Capture.Windows`: `Tdh/` schema reading and parsing behind `IEtwMetadataSource`; `Profiles/` source catalog; `Etw/` admission compilation, admitted-record struct with bounded inline extended-data storage, an `EVENT_RECORD` extended-data walker and its accessor, the ETW source-clock descriptor and its plausibility check, per-thread processor time, validated total-machine `GetSystemTimes` intervals, health and stage ledgers, owned live and diagnostic-ETL session state machines, shared live/offline TraceEvent admission, and ETL replay; `Inventory/` capability probe.
- `InterCat.Capture.Recording`: what a privileged recording runs (ADR-028). `LiveRecorder` captures into journal chunks with the plan first and the ledger last, and takes an `ILiveRecordingDerivation` for anything more; `AdmittedEventEnvelopeMapper` turns one admitted record into an envelope and back; `CaptureCoverageTally` counts what sources delivered; `JournalNormalizationPlanV1` retains the descriptor interpretation.
- `InterCat.Capture.Journal`: everything that parses, decodes, replays or queries admitted evidence. `ObservationNormalizerV1` derives observation and source-field rows from journal envelopes; `JournalRederivation` streams a published journal into a replacement generation; `LiveSessionRecorder` and `LiveSessionFollower` derive live recordings; and `EtlCanonicalImport` hashes a standalone ETL, derives and checks its clock, replays it through the same admission adapter live capture uses, feeds each envelope to the canonical import builder, and optionally writes the journal and publishes the derived generation. Neither the Windows adapter nor the storage format references the other; the modules that bridge them carry that dependency.
- `InterCat.CaptureBroker`: the broker control core. `BrokerPrepareCompiler`/`PreparedCapturePlan` freeze a locally compiled plan under a deterministic digest; `PreparedPlanRegistry` issues expiring owner-bound tokens; `BrokerLifecycleCoordinator` and `FileBrokerLifecycleStore` hold idempotent lifecycle, leases, stop milestones and a checksummed append-only ownership log; the wire layer carries the bounded frame codec, typed requests/responses and the per-connection dispatcher; `WindowsBrokerPipeServer` and `WindowsBrokerTokenIdentity` authenticate a first-instance local pipe from the client's own token; and `WindowsBrokerRoot` with `BrokerRootSecurity` provisions, validates and holds the broker-owned filesystem root that every broker file is opened, replaced and removed beneath. `Program.cs` still exits fail-closed and starts nothing.
- `InterCat.Desktop`: `Theme/` measured palette, colour math and token resources; `Presentation/` legend, table and ladder rows; synthetic Avalonia prototype with custom graph/timeline drawing that carries no colour literal, a breadcrumb and filter bar, and one gesture per rung on both the keyboard and the pointer.
- `InterCat.Cli`: `capabilities`, `profiles`, `measure tcp`, `measure pipe`, `measure rpc`, `import` (with `--into` to publish a session), `session`, `rederive`, `retain`, `metric` (with `--matrix` and `--group-by`), `processes`, `verify tcp`, `bench`, with documented exit codes, stdout for data and stderr for progress. Names are accepted as written or in kebab form (`bytes-sent`, `send-side`), and a damaged format is exit code 4 with the reader's own reason rather than a stack trace.
- `InterCat.TestWorkloads`: the FX-TCP-001, FX-PIPE-001 and FX-RPC-001 scenarios and their truth log writer, which is thread-safe because the TCP scenario can now run its connections concurrently.
- `tools/InterCat.ThemeReport`: regenerates `theme/tokens.json`, `theme/contrast-report.json` and `theme/README.md`.
- `tools/InterCat.JournalProbe`: runs the portable Release admission/replay measurement and writes a self-limiting benchmark artifact.
- `tools/InterCat.CaptureComparison`: elevated IC-009 same-seed journal/ETL harness plus IC-010a capture-impact mode. Comparison mode runs a declared load series, maps callbacks into journal-v1 on a dedicated writer thread, measures the output volume against the §12 reference device before capturing, classifies each level's saturation outcome from its own counters, refuses existing output, and emits `decisionReady: false` until every gate input is present. `--impact` measures each source separately against no capture, alternates pair order, and publishes the raw trials, median §12 costs and `OverheadClass` without charging provider startup, stop or offline replay to the live interval.
- tests: Domain, Storage, Analysis, Application, Capture.Windows, Capture.Journal, CaptureBroker, CaptureComparison, Desktop, Ui (headless Avalonia), Property and Architecture.

## Decisions and plan corrections

- Plan revision 48 and ADR-017 define **`between(A,B)` as the records connecting two process sets**. §19.1 named it without a direction policy and §23 had none, so `EN-BetweenDirection` is new. "Connecting" means that one set holds the record's maker and the other its other end. It does not mean `participant(A)` and `participant(B)` together: when the sets overlap, that keeps a shared process's records to anyone. An empty set is refused rather than read as "any process", and so is a focus beside it, because two selections would intersect with no stated meaning.
- **An answer read through relations names the binding rule, and derives instances the one way the process list does.** A relation's ends are held by process instances, so `entityRevision` is present whenever `correlationRevision` is. The evidence policy is written only where it keeps records or places them in groups. Found by running the new filter against a real session with start keys, not by the fixtures.

- Plan revision 40 and ADR-014 find **a TCP record's other end through its mirrored endpoint pair**. §7.4 left the join key to measurement, and the measurement is uniform: every admitted TCPv4 descriptor, connection lifecycle ones included, names the record's own endpoint first. The provider's connection identifier is not a key, because it is zero on this build and reusable. The rule claims a peer only when every record at the other end binds to one instance and names one PID. It never pairs by time, by address class or by the nearest holder, and it leaves an end reused within a capture ambiguous rather than split.
- **A record's other end is a separate fact from its own owner.** It depends only on the evidence at that other end, so the same connection can be `Correlated` from one end and `Candidate` from the other, and a record whose owner exited can still name its peer. The strength is never `Direct`, because no single record names both ends.
- **A process filter selects records by role; the accounting still decides which end measures them.** `participant(P)` under sender accounting is the volume of P's conversations, each transfer measured once. Direction lives in the focus role (`sender`, `receiver`) and in grouping, where `metrics-v1` §6 already placed it, not in a second reading of `participant`.
- **`owner(P)` with a cross-side total is refused, not unavailable.** It would take P's receive records and call them sent bytes, and no derivation can make that meaningful. `sender(P)` selects the records that measure what P sent at the receiving end. `NoTransferAssociations` is now only the canonical owner's reason: a relation proves which process is at the other end, not which of its records is the same transfer.
- **What a filter cannot decide is disclosed, and `PeerNotObserved` is stated apart.** A record whose other end no record in the capture holds involves the focus only if the focus's own records are missing, which is a different claim from an other end that is present but ambiguous. Folding the two into one sentence would have told a user that 107 remote flows might be the workload's own.

- Plan revision 29 and ADR-013 derive **process instances from the session's own lifecycle records**, over `observation-v1` alone so the derivation stays portable (R19) and rebuildable (R20). The provider start key is admitted into the journal but has no column in the derived store, so an instance is keyed by its observed creation or by the record that witnesses it; carrying §7.3's source correlation fields is the next derivation rather than a guess now.
- **A PID that records name and no lifecycle record does is one provisional instance**, witnessed by its earliest record. On the measured corpus those instances carry almost all the transport bytes - 14 of 55 PIDs, the busiest ones - so requiring creation evidence would have left the answer to "which process" mostly empty. Two holders of such a PID would need a witnessed exit and creation between them, so one instance is what the evidence supports; its start is unknown rather than invented.
- **identity-v1's reuse rule was stricter than its own rationale, and is amended.** It left every PID-only record of a reused PID unresolved because a record inside a later lifetime can be a late record of an earlier one - its own fixture is exactly that case. A record inside the *first* lifetime carries only the risk a never-reused PID's record carries, so it now resolves (`EarliestLifecycle`, `Correlated`), while one inside a later lifetime is a `Candidate` the default evidence policy leaves unattributed and `IncludeCandidates` attributes, labelled. The domain resolver and the analysis derivation implement the same rule.
- **Grouping partitions a total.** Every record in scope belongs to exactly one group or one stated reason it is unattributed, so a grouped answer's rows add up to its total; a process that only received has no row in a sent-bytes ranking rather than a row of 0 B, a group with only unknown contributions is unmeasured and sorts apart, and the rows past `--top` are one exact remainder. Building it found a defect before it shipped: the per-side breakdown summed only the listed groups, so contributions of a group the accounting takes nothing from vanished from what the total left out.
- **A boot is derived from the capture's clock.** A monotonic clock does not survive a restart, so one clock scopes one boot, and two captures on different clocks never share a process identity.
- Plan revision 28 and ADR-012 correct §5.3 where it could not be implemented as written. **Endpoint activity is a metric, `EN-Metric` 15 `EndpointActivityBytes`.** §5.1, §5.2, §5.3 and §22 all describe it as a separately labelled metric, but §23 gave it no metric code and the matrix made it reachable only as an accounting side of `BytesSent` or `BytesReceived`, where it either contradicts the metric's direction or is sender accounting under another name - and neither reading produces §21.1's 200 bytes from one metric. The code is additive, and `EN-AccountingSide` 3 keeps its meaning as that metric's fixed side and as the label of a stored contribution whose source states no direction.
- **A row's side is a fact; a request's accounting is a rule.** They share one enumeration and were being conflated: passing a request's side straight through as a row filter made an endpoint-activity total sum only the 69 connect, accept and disconnect records of the measured corpus and report 0 B for a session holding 427,728 B of endpoint activity. Sender and receiver accounting take one row label, endpoint activity takes all three, and a canonical owner takes none - it is chosen per proven association, which is a relation and never an observation column (R1). The mapping lives once in the metric layer; the storage layer measures every side of one domain in one pass and keeps them apart.
- **A traffic metric takes only a traffic domain.** "Required, exactly one" let `BytesSent` be asked for in `RequestedIo`, which reports a requested length as sent bytes - P3's own example, reachable through the matrix. `BytesSent`, `BytesReceived` and `EndpointActivityBytes` now take `TransportObserved` or `CompletedIo`, and a domain another metric owns is refused with that metric named.
- **Meaningless and unanswerable are different results.** A request the matrix refuses is refused before any session is read. A request it permits that a session cannot derive - operations, topology, entity instances, a status domain, a proven transfer association - is reported as unavailable with its reason and no value, which is also how a byte total that takes no known contribution is reported: R21 applied to a sum, because "0 B" there would render absence as observed zero activity. An observed zero is a known contribution and still totals zero.
- **A rate is scoped by its interval and kept as integers.** An earlier draft of this slice counted every row of the session and divided by the interval, so a rate over an interval with no record in it came out as ten per tick; the numerator is now what the interval holds, the denominator the whole interval, and the per-second figure is derived from numerator, interval ticks and the clock's rate for reading. A session-relative bound becomes the first native reading at or after it under exactly the conversion that produced every stored session instant, so the native interval holds precisely the readings whose session time lies inside the requested one.
- **A reader reads the manifest its lease holds.** The store's current manifest can move on between acquiring a lease and asking for it, which would hand a reader a dependency list its lease does not protect; `EvidenceLease.Manifest` closes that, and `contracts/store-v1.md` states it.
- The plan's ADR table still reserved numbers for unwritten decisions, although an earlier correction recorded below says it no longer did: ADR-018 and ADR-019 were listed, the risk register cited an ADR-016, and the definition of done required "ADR-001 through ADR-019". They are now "not written" entries and references to what §16 requires, and ADR-012 is the metric-accounting decision.
- Plan revision 27 records two rules that came out of implementing retention, and one defect it exposed. **Releasing and removing are separate steps**: the generation stops naming a released file at once, which is what makes the retention visible and atomic with the manifest, and the bytes go when the last reader holding them lets go - retention never removes evidence behind an open reader (I18). **A retention that released anything re-aims the retained last-known-good pointer at the retention generation**, because the earlier one is missing a file and a pointer naming it would promise a rollback that cannot be performed. The defect: the orphan sweep only knew the current generation, so the manifest the last-known-good pointer names looked unreferenced and `RemoveOrphans` would have deleted the one thing a rollback needs. The sweep now treats the manifest and dependencies of both pointers as referenced.
- A journal release works in whole batches, because a frame's checksum covers the records it holds and releasing part of one would publish a frame whose digest describes records that are no longer in it. The consequence is a real constraint rather than an implementation detail: the granularity a session can retain at is the batch size its capture or import declared, and a journal written as one batch has no releasable boundary at all. The preview names the boundaries a journal does allow rather than only refusing the one asked for, and the journal batch size is a declared option with its trade-off stated.
- A release with no stated reason is refused, and a release that would leave no admitted evidence at all is refused. The first is because a retention that cannot be read afterwards is indistinguishable from data loss; the second is because ADR-010 keeps a journal by default and a session with no journal cannot re-derive anything.
- What a retention record may *not* yet contain is as much a decision as what it contains. §20.2's still-live identities, endpoint bindings, continuity quality and pending-operation summaries need entity and operation revisions that do not exist, so the record carries the released extent and no placeholder fields: one that is structurally present and always empty cannot be told from a genuinely empty interval, which is the same reasoning that kept `OverheadClass` out of a completed IC-010.
- Plan revision 26 and ADR-011 close a gap in the backlog itself: §20.1 specified a physical derived store - fixed-width columns, null bitmaps, variable chunks, dictionaries, a raw-record locator, time-block metadata - that no §14.1 item owned. IC-015 was written as metric contributions and IC-016 as commit and recovery, and the commit protocol shipped treating a dependency as an opaque named file, exactly as §20.1 asks, leaving the formats unowned. IC-015a now owns them. Three decisions came out of building them. A **measurement slot and a measurement are separate facts**: a descriptor that exposes a byte field labels its domain, side and unit in every row whether or not that record supplied a value, and one that exposes none carries no labels at all and reports `NotApplicable`. Without that split, a null means both "the source did not say" and "there was nothing to say", and every availability ratio above it is a guess. A **byte sum belongs to the storage layer**, not the scheduler: IC-017 owns when a result is computed and superseded, and if it also owned what a sum means, §5.3's semantics would have to be re-derived by the CLI, an export and every test. And the **dictionary budget's fallback is the variable chunk**: a refusal would turn a machine with many distinct resource names into one InterCat cannot derive a session for, which is a ceiling rather than a bound.
- A segment's bytes are a function of its row set, and it is measured rather than asserted: importing the same 852-record ETL twice into two fresh directories produced byte-identical segments and dictionaries. The journals differ in exactly one field, the creation time in their header, which is a fact about the run rather than about the evidence. What is deliberately not claimed is that two imports partition the same rows into the same segments - which rows land in which segment follows acquisition order, which §18.4 already records as not reproducible for an ETL.
- §7.3 names a status domain that §23 assigns no enumeration. A status code is stored with its availability and nothing claims which domain it is in; inventing codes to fill a frozen format would have been worse than recording the gap, so §20.1 now records it as owed.
- The observation layer is a source-contract fact, so it is declared per descriptor in the catalog and travels through the compiled plan into the row. Deriving it from the mechanism would be cheaper and wrong: one mechanism can carry evidence at more than one layer, and a guess there is exactly how a transport metric absorbs an application annotation (I11).
- ADR-002 records the M0 logger strategy: a uniquely named manifest-provider session created with `NoRestartOnCreate`, never a system logger, never an adoption of an existing session.
- ADR-005 makes reusable numeric values insufficient for identity, gives late provider start keys precedence over delivery time, and preserves provisional identities through versioned aliases.
- ADR-006 keeps native, session-relative, wall-clock and workspace-aligned time distinct; conversion uses checked `Int128`, nearest-even rounding and explicit quarantine rather than clamping.
- ADR-008 records the journal-versus-ETL gate as proposed, not accepted. Fidelity, separate loss counters and the stage ledger are now measured on real callbacks; saturation behaviour and a supported build are not.
- Extended-data items are opt-in per provider enablement, not a property of an event. A capture that does not request them observes none, so a zero count describes the capture's configuration and the plan now records the enable properties alongside the records (§18.1, §18.2).
- Requesting call stacks changes delivery enough that the 2-second default reorder grace truncated a run and produced a coverage collapse that was an artefact of the grace. The harness now defaults to 6 seconds when stacks are requested and says so.
- Per-thread processor time is the only honest attribution for a callback's cost, and the Windows thread clock advances about every 15.6 ms. A stage below one tick is reported as such, never as zero.
- A denied extended type is a counted policy omission whose bytes never reach the file, and replay reproduces the omission instead of inventing the item. SIDs, TraceLogging schema items and provider traits are denied under metadata-only admission.
- Plan revision 19 freezes the broker response/dispatcher boundary and corrects a preparation gap found while connecting it: duration, journal allowance, free-space reserve and retention were parsed but were not part of prepared-plan identity. They now change the digest, and every lifecycle command is reachable only after Hello through an injected authenticated owner. The executable remains fail-closed because identity injection is not authentication until the OS-token pipe host supplies it.
- Plan revision 25 and ADR-010 settle what a session keeps of its journal, which §20.1 said must not be arrived at by default. It keeps it. The two costs are not the same kind: keeping is a measured storage multiple - 231 bytes per admitted record against 128 per normalized observation, so roughly 1.8x on top of the derived store - and discarding is an unbounded loss of the ability to re-derive a fact from evidence after a normalizer revision, which is the property ADR-008 was accepted for. The default that loses information should be the one a user chooses, so releasing journal bytes becomes an explicit retention checkpoint that publishes the extent it released, and every generation carries the committed boundary it derives from rather than implying the whole file.
- The plan's ADR table had drifted into a numbering registry it was never able to be. It assigned ADR-009 to byte accounting and ADR-010 to the correlation-quality model, while the repository had already written ADR-009 for the callback allocation measurement, because implementation forced that decision first. The table now lists what must be decided and marks the unwritten ones as unwritten; a number is assigned when an ADR is written. A planning document cannot reserve numbers for decisions that have not happened yet without eventually lying about the ones that have.
- Recovery reports unreferenced files, including staging names and ownership markers, with their sizes and keeps them on open. An unreferenced published file may belong to an interrupted generation; a staged file may belong to another process still preparing a commit. Explicit orphan removal skips staging and runs under the publication lock. `icat staging` separately previews and cleans only marker-proven abandoned staging after digest-bound confirmation; unmarked legacy files remain for manual review.
- Plan revision 24 records what reading a real ETL settled. Evidence recorded outside InterCat carries readings but none of our identities, and it needs two: a clock and a host. Both are derived from the file's content identity rather than minted per run or borrowed from the reading machine. Minting would be worse than untidy - the clock ID is inside the canonical record key, so two imports of one file would not even agree on their records' identities - and borrowing the local host would let imported readings be compared against local captures as though they shared a clock. The recorded tick rate is the recording machine's fact, and the adapter library does not expose it, so it is derived from the file's own readings and then checked: every sampled reading must reproduce the file's recorded relative time under the derived rate. On this build that derives exactly 10,000,000 ticks per second and every sample reproduces; a file that does not is refused rather than read at an assumed rate.
- The module map gained `InterCat.Capture.Journal`, because turning an admitted Windows record into `journal-v1` evidence belongs to neither side of that boundary. Making the capture adapter depend on the storage format, or the storage format on the adapter, would have put a platform dependency where the module map keeps it out; a module whose whole job is the bridge keeps both clean and gives the CLI, the comparison harness and the future broker one implementation to share instead of three copies.
- Plan revision 23 records three things building the canonical importer settled. The buffer context is keyed content: which processor produced a record and which logger carried it are facts about the record, so two otherwise identical records from different processors are two records rather than one seen twice. File-local schema and policy reference numbers are not keyed, because a table position is not an identity; the table's own digest covers what they mean. And a sort needs a total order, so the canonical order ends in tie-breaks on stream, epoch and delivery ordinal - which makes a run repeatable and is deliberately not a claim about which of two indistinguishable records came first. Only the keys, instants and multiplicities are reproducible across readers; which ordinal is paired with which occurrence index is a property of the run.
- Plan revision 22 makes compaction reclaim frames, never records. Every frame is a complete snapshot, so the superseded ones are pure duplication and reclaiming them is ordinary maintenance; the records inside them are not. Dropping a completed request would turn its idempotent replay into a second capture, which is the exact failure the request ID exists to prevent. So the store's 4,096-capture and 16,384-request counts remain the retention policy, and a rule for records that outlive their usefulness is owed rather than smuggled into compaction. Two smaller rules came out of building it: a log is one generation, so a valid frame from another is the tail rather than a later state; and a compaction that cannot fit one frame inside the bound refuses before writing anything, because replacing a log with a generation already over budget trades a named refusal for a bound that no longer means anything.
- Plan revision 21 states the filesystem boundary in the terms Windows actually enforces. Three findings drove it. A DACL alone does not refuse an ordinary-integrity write from the same user, so the mandatory label is required rather than defence in depth. In a filtered administrator token `BUILTIN\Administrators` is present for denial only, so the viewer's read access has to come from an explicit ACE for the capturing user's own SID - relying on the administrator entry would leave a medium-integrity viewer unable to read its own evidence. And holding the root's handle stops the root being renamed but not an ancestor, which is why the parent's owner is checked in production. §20.5 now also records that `%ProgramData%` lets any user create an entry, so the root can be squatted before first run; the broker refuses with the owner named instead of adopting or repairing it, and installation should pre-create the root.
- A security descriptor is compared as facts, not as text. Windows renders `OICI` back as `CIOI`, an explicit SID as `SY`, and an exact mask as `FA`, and none of those differences changes what is granted; comparing strings would have produced refusals that are noise. The parser resolves masks numerically and refuses an abbreviation it does not know rather than reading it as no access, so an unrecognised right is unknown rather than harmless. The first run of the real fixtures found the gap this creates in both directions: an inherited (`ID`) entry had to become a known flag so that an inherited ACE on a protected root is *refused as unexpected* rather than crashing the parse.
- Plan revision 20 makes that identity real: a first-instance native pipe applies its protected SYSTEM/user DACL at creation, rejects redirector-style clients, and authorizes from an impersonated token's SID plus logon session. Integrity/elevation are recorded, PID is diagnostic, and inability to revert impersonation is treated as process-fatal because continuing under a client token would invalidate the broker boundary.
- Plan revision 5 preserves revision 4's ADR/identity corrections and removes the IC-009/IC-011 cycle. IC-009 may implement a disposable real-callback envelope candidate needed for the evidence gate; IC-011 begins only after ADR-008 closes and owns the production `journal-v1` contract and implementation.
- `journal-v1` is frozen and its bytes are a checked-in corpus. A format change fails `FX-JOURNAL-002` before it reaches anything else, which is what makes "frozen" a property of the repository rather than a promise.
- Buffer ownership is a type, not a convention: a returned lease refuses every read, so a use-after-return raises where the mistake is rather than yielding another owner's bytes later. Writing that type immediately caught a defect in its own first use - disposing the shared empty lease marked it returned for the whole process, which would have made every later empty body unreadable.
- ADR-009 records how the callback allocation budget is measured and what it measured. A per-record cost is a slope, not a total divided by a count, and attributing a cost on a shared thread needs the same work done twice with only the attributed component removed. The budget's bound moved from exactly zero to one byte per record, because a budget no measurement can meet stops carrying information; §12's rule is unchanged and it is now met.
- ADR-007 owns the §1.3 support matrix, which had no ADR until now. It adds Windows 11 25H2 x64 to the primary tier, lists a release by its exact build numbers rather than a range, and records a pre-release servicing branch as supported on the same terms provided every result states which branch it ran on.
- A counter is read while the thing it counts still exists. The ETL session's loss counters were being read after the session stopped, which fails and left the loss unknown for no reason; ordering the read before the stop turned an unreadable default into a measured zero. It is the same rule as §20.6's, found from the other side.
- Plan revision 14 separates a complete bounded Content request preview from an enforceable content capture. Requested scope, limits, retention, inspection and omission behavior can be reviewed while `bodyPolicy` and provider requests remain absent; a source needs content-specific descriptor/scope/impact evidence and a production admission compiler before it can start.
- Plan revision 13 makes wider-capture consent explicit for process-focused metadata. A selected-process view never masquerades as capture-side retention scope, and unavoidable whole-machine provider/context collection blocks until separately accepted.
- Plan revision 12 records what moving the capture path onto `journal-v1` measured. The journal costs about 231 bytes per admitted record where §12's retention arithmetic assumed 128 bytes per normalized observation, because the format deliberately carries no dictionary and §20.1's dictionaries belong to the derived store. §12 now says retention arithmetic must count the journal, and §20.1 now states that how long a journal batch outlives its published segment is a decision IC-016 owes and must record in an ADR - keeping every batch nearly triples a session, and discarding one loses the ability to rebuild a segment from admitted evidence after a normalizer revision. Neither may be arrived at by default.
- Plan revision 10 adds IC-010a to the backlog: measuring capture impact against a no-capture baseline is its own work item, because `OverheadClass` classifies a quantity nothing produces yet and leaving it inside IC-010 would have let a completed item carry an unmeasured field.
- Plan revision 9 adds Windows 11 25H2 to §1.3's matrix and states how a release is listed: exact build numbers, never a range, with a pre-release branch supported on the same terms and named in every result.
- Plan revision 8 corrects §3.2's "same numbers" invariant, which as written implied that every parent total is the sum of its children. Building the ladder showed that this only holds where the rows partition the rung: a channel belongs to both of its participants, so a process rung's rows overlap and their counts must not be added. §3.2 now names the accounting side and requires such a rung to report no total with a stated reason. It also records that the one-step jump to evidence has its own gesture and carries its scope into the filter bar.
- Plan revision 7 adds two rules the series made necessary: §20.6 states that a counter which could not be read is unknown rather than zero, and that no session carrying one may be called loss-free; §12 states that a result says whether its machine met the reference device, measured before capture rather than asserted.
- Plan revision 6 corrects three things the implementation found: §18.1 now requires the capture's source clock descriptor to be persisted once per journal rather than only named per record, states that extended-data items are opt-in per enablement and must be recorded as a setting, and calls the item's second header field a linkage bit rather than a general flags word; §18.2's `ProviderPlan` names what `EnableProperties` decides; and §12 states how a quantized per-thread processor reading and a bucketed latency distribution are reported.
- Admission offsets are compiled from the machine's own TDH manifest rather than from constants, so a schema change turns into a refusal with a reason instead of a misread field.
- Measured field semantics for `Microsoft-Windows-Kernel-Network`: addresses and ports arrive in network byte order, and `saddr`/`sport` names the owning process's own endpoint on send and receive alike. The manifest message wording does not describe which side is local.
- Admission now compiles pointer-sized fields, one bounded resource name and one 16-byte identifier per descriptor, and preserves the header activity ids that relate a start to its completion.
- An ETW event-id filter is either an allow list or a deny list. Supplying both makes a provider refuse enablement, so the allow list is sent and the deny list stays in the recorded plan and in the callback as a second refusal (P28).
- Admission now compiles pointer-sized fields and one bounded resource name per descriptor. A record whose pointer width does not match the compiled offsets is counted as undecodable rather than read at a guessed offset.
- A measured negative result must carry its control. `PipeCoverageEvaluator` counts the records the source delivered from the fixture's own processes, so "nothing observed" can be told apart from "the source was never live".
- The palette is computed, not chosen: adjacent mechanism families are staggered in CIELAB lightness so they stay separable in greyscale and under simulated protanopia, deuteranopia and tritanopia. `theme/contrast-report.json` records every measurement, and a change that breaks one fails a test.
- Navigation, bucketing and ladder properties are generated from fixed seeds rather than demonstrated on examples, and a failure prints the case that broke.
- A ranked table's rows either partition their rung or they do not, and the difference is carried in the row: process and group rows are attributed to the relationship's initiating side, so a parent total is the sum of its children, while channel rows belong to both participants and the rung refuses a total with a stated reason rather than adding overlapping counts (§5.1, `EN-AccountingSide`).
- The window renders headlessly to a file, so a layout defect can be found from a build without a display. Five defects in the second review pass were found that way.
- `fixtures/index.json` is the single machine-readable fixture declaration and the contract-coverage ledger. Two architecture tests keep it honest: declared coverage must equal what tests actually assert, and every fixture entry must name artifacts and tests that exist.
- Raw measurement runs are not committed. Only curated, fixture-scoped evidence is, because a raw run carries unrelated whole-machine flows (P16).

## Verification record

Run from the repository root; `measure` requires an elevated shell:

```powershell
dotnet restore InterCat.slnx
dotnet build InterCat.slnx --no-restore
dotnet test InterCat.slnx --no-build
dotnet run --project src/InterCat.Cli --no-build -- capabilities --output capabilities/10.0.26220.0-x64/windows-etw-inventory.json --overwrite
dotnet run --project src/InterCat.Cli --no-build -- profiles
dotnet run --project src/InterCat.Cli --no-build -- profiles explore --json
dotnet run --project src/InterCat.Cli --no-build -- profiles focused-transport --mechanism tcp
dotnet run --project src/InterCat.Cli --no-build -- profiles focused-transport --mechanism tcp --pid 4242 --allow-broader-capture --json
dotnet run --project src/InterCat.Cli --no-build -- profiles content --source etw/manifest/Microsoft-Windows-RPC --mechanism rpc --pid 4242 --channel rpc-interface:12345678-1234-1234-1234-123456789abc --max-record-bytes 4096 --max-session-bytes 67108864 --retention stop-at-limit --inspection disabled --json
dotnet run --project src/InterCat.Cli --no-build -- measure tcp --connections 2 --messages 6
dotnet run --project src/InterCat.Cli --no-build -- measure pipe --messages 6
dotnet run --project src/InterCat.Cli --no-build -- measure rpc --calls 8
dotnet run --project src/InterCat.Cli --no-build -- import <source.etl> --into <session directory> --output <summary.json>
dotnet run --project src/InterCat.Cli --no-build -- session <session directory> --rows 10
dotnet run --project src/InterCat.Cli --no-build -- rederive <session directory> --json
dotnet run --project src/InterCat.Cli --no-build -- rederive <session directory> --check --json
dotnet run --project src/InterCat.Cli --no-build -- retain <session directory> --release-journal-before-record <n>
dotnet run --project src/InterCat.Cli --no-build -- retain <session directory> --release-journal-before-record <n> --confirm --reason "<why>"
dotnet run --project src/InterCat.Cli --no-build -- metric --matrix
dotnet run --project src/InterCat.Cli --no-build -- metric <session directory> --metric bytes-sent --byte-domain transport-observed --side send-side
dotnet run --project src/InterCat.Cli --no-build -- metric <session directory> --metric endpoint-activity-bytes --byte-domain transport-observed --evidence 5
dotnet run --project src/InterCat.Cli --no-build -- metric <session directory> --metric rate --rate-numerator bytes-received --byte-domain transport-observed --side receive-side --interval 8s:10s --json
dotnet run --project src/InterCat.Cli --no-build -- metric <session directory> --metric bytes-sent --byte-domain transport-observed --side send-side --group-by process --top 10
dotnet run --project src/InterCat.Cli --no-build -- metric <session directory> --metric observations --group-by mechanism
dotnet run --project src/InterCat.Cli --no-build -- processes <session directory> --top 20
dotnet run --project src/InterCat.Cli --no-build -- verify tcp --run <run directory> --output fixtures/FX-TCP-001/evidence --overwrite
dotnet run --project tools/InterCat.ThemeReport --no-build -- theme
dotnet run --project tools/InterCat.JournalProbe -c Release -- bench/results/journal-probe-portable.json 10000 7
dotnet run --project tools/InterCat.CaptureComparison -c Release -- --output bench/results/capture-comparison-<run-id>-series
dotnet run --project tools/InterCat.CaptureComparison -c Release -- --output bench/results/capture-comparison-<run-id>-series-stacks --request-stacks true
dotnet run --project tools/InterCat.CaptureComparison -c Release -- --impact --output bench/results/capture-impact-<run-id>
dotnet run --project src/InterCat.Cli --no-build -- bench --series bench/results/capture-comparison-20260922T234102Z-series-stacks/series.json --output bench/results/reference-machine-20260922T234102Z.json --overwrite
dotnet run --project src/InterCat.Desktop --no-build
dotnet test tests/InterCat.Ui.Tests --no-build   # writes rendered frames beside the test binary
```

Results verified on 2026-09-23 (capture cost re-measured on 2026-09-23; the 2026-09-21 capture artifacts are kept beside it):

- revision 55 validation: the complete Debug and Release solutions each pass 658 tests; a local fixture ETL metric CLI check reports a TCP observed-rate numerator of 485 and a separate `Covered` state in JSON and terminal output, without changing rate arithmetic;
- revision 54 validation: the complete Debug and Release solutions each pass 655 tests; published-session regressions check covered versus partial buckets, a quiet imported mechanism, empty buckets and the exact native boundary around negative session time;
- revision 53 validation: the complete Debug and Release solutions each pass 652 tests, including new ledger storage, coverage interpretation and architecture fixture checks; a local fixture ETL publishes and re-derives a session with the exact same coverage dependency, 535 disclosed policy omissions and zero reported source loss;
- revision 52 focused validation: 16 Application tests, all 82 Analysis tests and 5 Architecture tests pass in Debug; graph-eligible timeline counts equal displayed edge records, and one-sided and candidate records change the eligible set only under the stated rule. An all-untimed relation keeps its graph edge with no invented timeline extent. Full-solution verification was pending the separate coverage-ledger interface work, now resolved in revision 53;
- revision 51 focused validation: 15 Application tests (including three published-session overview regressions), 12 headless UI tests and 5 Architecture tests pass in Debug; `InterCat.Application` builds with zero warnings. The full solution remains blocked by concurrent coverage-ledger interface work, so these are not a full-suite result;
- revision 50 focused validation: 12 Application, 13 Desktop, 12 headless UI and 5 Architecture tests pass in Debug. The full solution build remains blocked by the concurrent coverage-ledger interface change described below; no full-suite claim is made for this revision;
- revision 49 focused validation: all 82 Analysis tests and all 5 Architecture tests pass in Debug; the CLI builds with 0 warnings. The full solution build is temporarily blocked by concurrent, uncommitted coverage-ledger work changing `IAdmittedEventSink` before `InterCat.CaptureComparison` implements its new members. The revision 48 full-suite result below is the last complete baseline, not a claimed result for this worktree;
- build: revision 65 passed in Debug and Release with 0 warnings and 0 errors across 27 projects;
- tests: revision 65 passed 681 in both Debug and Release, 0 failed (96 Analysis, 59 Capture.Windows, 31 Capture.Journal, 144 Storage, and every other project as before); FX-UDP-001's evidence is registered in `fixtures/index.json` beside FX-TCP-001's, and both re-evaluate to their tiers in the suite;
- FX-UDP-001 UDP datagrams, measured: `fixtures/FX-UDP-001/evidence/verification.json`, fixture-scoped. 71 truth records and 64 observations of the workload's two processes and four loopback flows; nothing else on the machine was written (P16). One builder test keeps a UDP receive's delivered endpoints while reading it as its owner's flow, with a TCP receive owner-first on the same values. One evaluator test names a mirrored-only match as an orientation gap;
- IC-015 between filters: 3 tests. Between the client-server fixture's two processes, 6 records passed either way, with the evidence listed by reading. Two records were disclosed: the client's send to an endpoint nothing held and its record with no endpoint pair. The third process's send was not disclosed, because its maker is in neither set. `BytesSent` was 100 B from the client to the server and 40 B back. The 100 B measured at the receiver was the same, and the one-way record count was the send and its receive. Adding the unconnected third process to a set changed nothing. No record passed between that process and the server, and its one unresolved send was disclosed. The client and server pair had 1 channel with no unknown. An absent instance was `ProcessInstanceNotFound`. A focus, a peer, an empty set, an empty id, an undefined direction, a peer grouping and an ungrouped peer count beside it were refused. A set may overlap the other, and a grouped peer count was accepted. With start keys published, the ids a process list derives found 6 records between the pair and 7 for the server's participant filter, and ranked the client's channels. The unfocused channel count's identity named both rules and no policy. Restoring the old loading rule failed that test;
- IC-015 channel counts: 3 tests. The client-server fixture counted 3 channels. The connection was one at both its ends, and two sends to unheld endpoints were one-sided channels, over 8 records. The record with no endpoint pair was the one unknown, making the count "at least 3". The client had 2 channels and the server 1. Ranked by process, the rows overlapped and did not partition a total of 3, and a mechanism grouping was refused. A reused port with both connections witnessed counted 2 channels with no unknown. Two witnessed client connections against a server end with no lifecycle counted at least 2, with the server's 2 records as the undecided unknown part. A lone record with no endpoint pair was `NothingMeasured`, never zero;
- IC-015 connection incarnations: 2 tests. One local port used by two connections in turn, each opened and closed at both ends, gave two relations (100 with 200, 300 with 400), each of 6 records with its open and close witnessed. Every data and disconnect record named its own connection's other end, and the later server received exactly the later client's 7 bytes. When the server end carried no lifecycle, so both client connections could pair with it, the clients' records still named the one server holding it, while the server's records stayed ambiguous between the two clients and no relation was claimed;
- IC-015 peer counts: 3 tests. A server's participant count was 1 over 6 resolved records, with evidence listed. The client's was 1, and its caveat says "at least": one end is not observed and one record has no endpoint pair, each counted by reason. Its sender and receiver counts were 1 each. A process whose only record left the host had no count rather than zero. Ranked by process with a process connected to itself added, three processes had one peer each, with 6, 6 and 2 known records; the process with only an unresolved end was unmeasured and unranked. The rows were declared not to partition a total of 3, the self-connected process counted itself once, and a top-2 cut merged the rest into a remainder of 1 distinct peer. A focus combined with a grouping was refused, and a peer count with no subject was refused before any session was read. `ActiveChannels` stayed unavailable, naming time-scoped ends;
- IC-018 query identity: 5 tests and the `FX-QUERY-001` golden corpus. Fourteen specifications over a fixed snapshot reproduce their canonical lines byte for byte, and each identity is SHA-256 over its line. A requested-I/O total with its domain implied, and an application-payload total with its domain and layer implied, hashed the same as their written-out spellings. A mechanism grouping under `DirectOnly` hashed the same as under the default, while a process grouping did not. A top 5 and a top 20 shared a hash and kept different identities. Eleven variations - side, mechanism, two intervals, sender, participant, two peer directions, generation and manifest digest - gave eleven distinct identities; two captures listed in either order gave one; an owner with a cross-side total had no identity. An answer's identity equalled `Identify` for the same request and named the capture, generation and manifest it read; an unavailable canonical-owner answer carried its own identity; and publishing another generation changed the identity of the same request. `between` gave one identity for both orders of an undirected pair, one for a backward direction and its forward spelling, and one for a set with a repeated instance and without it; six directed, grown and focus-based variations gave six identities;
- IC-015 relations: 10 tests. A client and a server exchanging 100 and 40 bytes over loopback were one `Correlated` relation of 6 records, listed by the end that sorts first; a send to an endpoint nothing held, a send to a remote address and a record with no endpoint pair each kept its reason, and lifecycle records said no rule covers them. An end held by two PIDs left its peers' other end `PeerAmbiguous` while their own records still resolved theirs, and an end whose only record named no process left its peer `PeerUnbound`. A server end bound only as a reused PID's later instance made a `Candidate` relation that the default policy disclosed as one undecided record and `IncludeCandidates` counted as 64 B. `participant(server)` kept 7 of 12 records, including the client's three records whose other end it is, and disclosed 1 `PeerEndpointIncomplete` and 2 `PeerNotObserved` records but no lifecycle record; its sender-accounted conversation was 140 B and the client's 148 B. `sender(client)` took 108 B at its own sends and 100 B at the receiving end, and `receiver(client)` 40 B at either end. `peer(P,Q)` narrowed a participant to the 6 records between the two, an owner to 3 and a sender to 100 B, and alone was refused. Grouped by peer, a participant's 148 B split into 140 B with the server and 5 B and 3 B by reason; grouped by process, sent bytes measured at the receiver went to their senders and received bytes measured at the sender to their receivers, with 15 B unattributed and the partition exact. Relations, strengths and every record's other end were identical over one segment and over reversed segments of two rows;
- IC-016/IC-015a re-derivation: a saved descriptor plan round-trips and refuses a schema mismatch; a four-record, multi-batch journal replays into generation 2 with byte-identical observation and source-field segments, stable raw identities, no double-counted older segments and a retained generation 1; legacy no-plan re-derivation refuses without changing the current pointer;
- IC-015 process instances: 8 derivation tests and 7 grouping tests. A created and exited PID was one instance whose half-open lifetime ended one tick after its exit, both lifecycle records bound `Direct` and a transfer inside it `Correlated`. A PID seen only in three transfers delivered out of order was one provisional instance keyed by the earliest by reading. PID 400 created, exited and reused, with a record in the first lifetime delivered last, gave two instances: the late record bound to the first and the newer instance's total never included it, while the newer instance's own record was an unadmitted candidate by default and 7 B of it with candidates. A second creation with no exit, and a second exit with no creation, each opened an instance flagged with the gap instead of merging. A record with no payload owner was unattributed whatever its header said. Instance identities and every binding were equal whether the evidence was one segment or reversed into segments of two rows, and equal PIDs and readings on two clocks were two instances. Grouped by process, by mechanism and by rate, every row set - groups, remainder and unattributed - added up to the ungrouped total with the same exclusions; ties broke on instance identity; an unknown-only group was unmeasured and sorted after an observed zero;
- IC-015 process instances, measured: `bench/results/entities-20260922T193935Z/entities-summary.json`, aggregates only. The 555-record session holds 55 instances across 55 PIDs - 34 created in the capture, 7 running before their first record, 14 seen only in their own records - and binds all 555 records; the instances' own transport records sum to exactly the ungrouped 173,258 B sent and 254,470 B received. The 76,092-record `peak` session holds 538 instances, 482 witnessed by the capture-state rundown and 56 created in the capture; the two busiest are the workload's two processes, created and exited with code 0, one sending 34,168,998 B that the other received, and 4 records name a PID after its only instance exited and are reported as such. Each `icat processes` or grouped `icat metric` over it took about 1.3-1.5 s of wall time;
- IC-015 metrics: 33 Analysis tests over the matrix and §21.1's scenarios. One transfer seen from both ends answered 2 observations, 100 B sender-accounted with the receive record reported as another side, 100 B receiver-accounted, 200 B of endpoint activity with both sides taken, 100 B of sent bytes measured at the receiving end, and `NoTransferAssociations` for a canonical owner. A 4,096-byte requested write and its 1,024-byte completion stayed two quantities, and a transport-observed total over them was `NothingMeasured`, naming both domains. 100 operations in a 10-second window with a 2-second gap answered 10 per second, not 12.5. An interval [2,000, 5,000) took the readings at 2,000, 3,000 and 4,000 and counted seven outside it; a rate over it was 3 records over 3,000 ticks, 10,000 per second. `BytesSent` over `RequestedIo`, `EndpointActivity` on `BytesSent`, a distinct count as a rate's numerator and a transport-layer projection of application payload bytes were each refused with the metric that answers instead. A generation naming two derivations of one capture was refused, and a total's evidence was exactly the records it counted;
- IC-015 metrics, measured: `bench/results/metrics-20260922T190645Z/`, over the 555-record session of the IC-015a measurement. 555 observations; 173,258 B sender-accounted over 163 contributions and 254,470 B receiver-accounted over 253; 427,728 B of endpoint activity over 485 - the two one-sided totals plus 69 undirected records contributing zero; received bytes over [8 s, 10 s) 90,999 B, 45,499.5 B/s, with 427 records outside the interval; requested I/O `NothingMeasured`, naming the 485 transport-observed contributions in scope; canonical owner `NoTransferAssociations`. The same queries over a 76,092-record session imported from the IC-009 `peak` level answered in about 1.1 s of wall time each, including process start and re-hashing about 30 MiB of dependencies at open;
- IC-016 leases and retention: 14 tests. A lease acquires the generation and every dependency it names; a retention taken while it is held publishes the generation that no longer names them, keeps their bytes, and reports them as awaiting release, and the lease still reads exactly what it acquired from a generation that is no longer current. Releasing the lease and sweeping removes them. An expired lease holds nothing and a retention taken afterwards removes them immediately. A pin that reserves less than it pins is refused, and one that declares no allowance is refused. A retention generation publishes its kind, extent, files and reason, reopens, verifies against its own digest and round-trips its record. A manifest with no retention record hashes to a pinned golden digest derived independently from the documented canonical form, so a change that silently invalidated every already-published manifest fails there rather than on a user's disk. A journal release published a shorter complete journal - same capture, same clock, same schema table, terminal frame, the retained records in order - and the generation's boundary named it; a boundary inside a batch released nothing; a boundary that would empty the journal was refused with the largest one it allows; and dropping a journal's name instead of releasing an extent was refused;
- IC-016 retention, measured end to end: `bench/results/retention-20260922T144500Z/`. The 555-record ETL imported with a 100-record journal batch size; `icat retain` disclosed the boundaries that journal allows - 100 to 500 records - and, on a separate confirmation with a stated reason, gave up 300 records in 3 batches. Generation 2 publishes a 61,066-byte journal of 255 records in place of a 130,594-byte one of 555: 69,528 bytes of admitted evidence given up, 130,594 removed and 61,066 written, with no file held by a lease. Reopening acquired generation 2, re-verified every dependency against the manifest that names them, and reported the superseded manifest as the single orphan - kept, not deleted;
- IC-015a `segment-v1`: 26 tests over the format and its refusals. Every column round-trips, including its nulls, its 16-byte identifiers and its names. The same rows given to two writers in opposite order produce byte-identical segments. A column's availability counters are checked against its bitmap, and a bitmap bit past the row count is refused. Time blocks cover every row and state the interval their rows hold. A byte sum over one domain and side excluded a 4,096-byte requested-I/O contribution and counted it rather than adding it, kept an unknown as an unknown with its reason, and reported measurement availability against the declared slots. A later generation publishing application-layer rows left the transport-layer sum byte for byte unchanged and contributed nothing to a transport projection - not a zero it summed, but no contribution to sum. Damaged headers, a wrong major version, an unimplemented required feature bit, a tampered column whose file-level digest was rewritten to match, an unsorted time column, an empty segment and two rows sharing one observation identity are each refused with the reason;
- IC-015a session publication, measured: `bench/results/session-20260922T142600Z/`. The 240 KiB `Microsoft-Windows-Kernel-Process`/`-Network` ETL of the IC-013 measurement published generation 1 as a 130,214-byte journal of 555 admitted records, one 100,528-byte segment of 555 rows in one time block, and an 800-byte schema dictionary. The committed boundary names that journal, its full 130,214 bytes and 555 records, and its digest. Reopening the session re-verified every dependency and reported no orphan and no rollback. Its measurements are reported per domain and side and never combined: 173,258 transport-observed bytes send-side over 163 contributions with 322 excluded as another side and 70 as having no declared slot, and 254,470 bytes receive-side over 253 contributions. Both are 100% measured against their declared slots. A third pair, 69 contributions accounted to endpoint activity, totals 0 bytes: those are the connect, accept and disconnect descriptors, whose `size` field the source populated with zero;
- IC-015a reproducibility, measured on the 852-record baseline ETL: two imports of the same file into two fresh directories produced byte-identical `seg-`, `dict-` files at every segment; the journals differed only in the creation time their headers carry;
- IC-016 commit protocol: exercised at each interruption point the sequence has, by building the on-disk state a restart actually finds. A file published with no manifest referencing it left the previous generation current and was reported as an orphan with its size, not deleted. A fully written generation whose pointer had not been replaced left the previous generation current and reported the newer manifest as an orphan. A torn pointer rolled back to the retained last-known-good with the reason the newer one failed. A dependency whose bytes changed under a published generation - same name, same length, different content - failed that generation and rolled back, because a rename is not a power-failure guarantee. A tampered manifest whose contents no longer matched its own digest was refused rather than read. Revision 36 changed staging recovery: an interrupted staging file is now reported and kept on open, not removed behind a possible second writer;
- IC-013 standalone ETL import, measured: `bench/results/import-20260922T093739Z/import-summary.json`. A 240 KiB `Microsoft-Windows-Kernel-Process`/`-Network` ETL recorded on this machine delivered 1,090 records, of which 555 were admitted and indexed and 535 were policy omissions against the compiled descriptor plan, with 0 undecodable and 0 lost by the file itself. The rate derived from the file's own readings came out at exactly 10,000,000 QPC ticks per second, and every sampled reading reproduced its recorded relative time under it. Repeating the import produced the identical source digest, import digest, derived clock and schema-table digest; running it again with a 64-entry memory bound spilled 8 runs and 32,768 bytes, produced the same 555 indexed records and 555 distinct facts, and left its spill directory empty;
- IC-013 canonical import: the same records read in the opposite delivery order produced identical canonical keys, instants, occurrence indices and multiplicities; three equal-time byte-identical records were preserved as multiplicity 3 rather than deduplicated, while two differing only in processor number were two facts rather than one seen twice; a journal import kept its stored ordinals and order, and re-importing it reused the same import identity, clock and schema-table digest; an import limited to four entries in memory spilled five runs and produced exactly the index the unbounded run produced, and a cancelled import left its spill directory empty;
- IC-014 ownership-log compaction: measured against the on-disk states a restart actually finds. A compaction interrupted before its replacement - with either a complete temporary or a torn prefix of one - reopened at the previous generation with the previous snapshot byte for byte and the temporary discarded, and a completed start request still replayed to its stored outcome after compaction instead of starting a second capture. A log given a bound its own history already filled compacted itself and kept running; a bound no single frame can fit refused before writing anything, leaving generation 0 and an empty log. A valid, correctly checksummed frame whose sequence continued the compacted log but whose generation did not was rejected as the tail;
- IC-014 broker root: provisioned against real Windows objects at the test process's own integrity. A real junction substituted for the root and for the parent was refused rather than followed and left the target directory empty; a rename of the validated root was refused while its handle was held; a pre-existing directory carrying only inherited security was re-applied from the declared descriptor and revalidated; and a token duplicated and lowered one mandatory level read the evidence back byte for byte while every write and every create beneath the root was refused. The production descriptor was asserted separately as a pure value, so the high-integrity label and the read-only viewer entry are checked even when the run is not elevated;
- IC-012 profile preview: adapter `windows-etw-inventory-0.5.0` compiled Explore to process plus network, 17 admitted descriptor versions and two exact provider requests; RPC, ALPC and named pipes were omitted because their capture impact is unmeasured. Focused TCP compiled the same bounded descriptors with only required sources. A PID-focused request was blocked without wider-capture consent and became startable only after consent, while retaining empty provider PID filters and the whole-machine scope disclosure. UDP was refused without fallback. Content compiled a complete bounded request and returned `canStart: false`, null effective admission/body policy, no providers or approved body fields, and exact source-contract/scope/impact blockers. The JSON preview reports complete metadata scope/content decisions, no call stacks, no original source-byte retention and the IC-010a evidence paths only where they actually apply;
- capture cost after process-name admission, re-measured 2026-09-23 from an elevated shell in Release: IC-010a `capture-impact-20260922T234102Z` - process median classified CPU cost 0.91 pp (`Low`), per pair 0/7.09/0.91 pp, 537/497/543 admitted records, throughput regression 0%; network median 0 pp (`Low`), per pair 3.76/0/0 pp, 1.29% throughput regression; every capture trial loss-free, drop-free and replayed exactly, `decisionReady: true`. The first process pair's live journal held 502 full device image paths in 537 records (the 2026-09-21 journal held none), counted without reading any name out. Load series `capture-comparison-20260922T234102Z-series` and `-series-stacks`: no provider or buffer loss and no undecodable record at any level; admitted projection and clock replayed exactly at every level; callback p99 in [2,048, 4,096) ns at peak and [1,024, 2,048) ns with stacks; admission allocation slope 0.00086 and 0.00044 B/record; 73,310 of 73,310 extended items replayed at the stacks peak; clean series blocked only on requesting no extended data, stacks series with no blockers. IC-010 baseline `reference-machine-20260922T234102Z.json`: `E:\` at about 2.0 GB/s write and 8.7 GB/s read; 3 budgets met, sustained ingest missed at 10,680 records/s with stacks, 6 unmeasured;
- IC-010 baseline `bench/results/reference-machine-20260921b.json`: this machine meets the §12 reference - 24 logical processors, 64 GiB, `E:\` at about 1.7 GB/s write and 7.4 GB/s read. Of the ten §12 budgets, three are met - callback admission p99 at 2,048 ns against 50,000, callback allocation at a measured slope of 0.00085 bytes per record against one (ADR-009), and application drops at 0 - one is missed, sustained ingest at 10,717 records per second with call stacks requested against a target of 100,000, and six have no stage to measure them until the §20.1 store and M2 exist;
- comparison harness: six elevated Release runs recorded under `bench/results/` - two single points and four five-level series, two of each format - 0 InterCat ETW sessions were left behind by any of them;
- journal probe: 10,000 records, 7 Release iterations; median combined admit/encode/decode rate 215,720 records/s, exact identity and extended-data replay, 0 unapproved bodies retained, complete attribution. This allocated about 33.7 MB per iteration and is not callback-path code;
- theme verification: every recorded threshold met in both modes;
- capability probe: passed; 1,237 published providers seen, 6 of 7 catalog sources registered and schema-readable;
- measured run `20260921T080303Z`: session `InterCat-m0tcp-64872-98b7614f`, 42 other ETW sessions left untouched, 629 records observed and 629 admitted, zero application drops, zero provider-reported loss, zero buffer loss, zero undecodable records, no degradations;
- coverage: 48 of 48 truth operations observed and bound to a flow instance with a byte measurement, 4 peer attributions with 0 false, 2 of 2 connections discovered with lifecycle, 4 of 4 memberships resolved, tier `TrafficVisualization` on the re-run of 2026-09-21 and `ExperimentalEvidence` before ADR-007, from identical counters;
- IC-009 elevated comparison `capture-comparison-20260921-baseline`: journal 707 observed and 707 admitted with no loss, drops or undecodable records; ETL 854 observed and 852 admitted; 226 KiB of admitted journal against 1.63 MiB of unfiltered ETL for the same fixture; the ordered envelope fingerprint replayed identically; the source clock was confirmed against a delivered record and read back identically; no record declared an extended item, because none was requested;
- IC-009 elevated comparison `capture-comparison-20260921-stacks`: 190 `STACK_TRACE64` extended items copied in the callback, persisted in envelopes and replayed identically, plus 87 records from the unrequested kernel stack-walk provider counted as policy omissions rather than loss; both variants still met all six §14.2 criteria;
- IC-009 load series `capture-comparison-20260921-series` and `-series-stacks`: the two probe-format runs ADR-008 was accepted on. Five declared levels each, no source loss at any level, 74,682 of 74,682 extended items copied, persisted and replayed identically at the highest level of the call-stack run, and `decisionReady: true` with no blockers;
- journal-v1 load series `capture-comparison-20260921b-series`: the same five levels with the capture path writing `journal-v1`; no source loss at any level; `queue-bound` observed 73,861 records against a 512-record queue and counted 64,513 application drops with zero loss; `disk-bound` performed 2,230 durable flushes and kept the writer busy for 127% of its acquisition window; the unconstrained ceiling was 24,544 admitted records per second at 89% queue depth; every level replayed with an identical ordered fingerprint and read its clock back unchanged;
- journal-v1 load series `capture-comparison-20260921b-series-stacks`: the same five levels with call stacks requested; 77,355 of 77,355 extended items copied, persisted and replayed identically at the highest level; 23.9 MiB of admitted journal against 62.8 MiB of ETL carrying the same stacks; `decisionReady: true` with no blockers;
- IC-010a capture impact `capture-impact-20260921T200502Z`: three alternating-order baseline/capture pairs per source, `decisionReady: true`; process median classified CPU cost 0 percentage points (`Low`) and throughput regression 0%; network median classified CPU cost 0 percentage points (`Low`) and throughput regression 1.87%; all six capture trials admitted records, reported zero source loss and application drops, and replayed their admitted journal exactly. Per-pair classified CPU costs were process 0/1.98/0 pp and network 0/0/4.56 pp, so the raw dispersion remains visible rather than being hidden by the median;
- IC-009 stage ledger at the single load point: callback p50 in [1.02, 2.05) µs and p99 in [8.19, 16.38) µs without stacks and [16.38, 32.77) µs with them, mean about 11 µs, maximum 6.6–7.3 ms on the first callbacks; delivery thread allocated about 1.59 MiB per run, which ADR-009 later attributed to the managed adapter's dispatch rather than to admission; queue high-water 706 of 65,536; writer thread 16 ms of processor time, 2 durable flushes, flush p99 in [4.19, 8.39) ms;
- byte agreement: truth completed 28,527 B sent; observed transport 28,527 B sent (reported side by side, never summed across domains);
- process evidence: both workload processes observed with start, exit and a non-reusable sequence number;
- measured run `20260921T083314Z` (FX-PIPE-001): session `InterCat-m0pipe`, 13,198 records observed and admitted, zero loss, zero drops, zero undecodable;
- pipe coverage: 0 of 25 truth operations observed, 0 pipe creates, reads or writes for the fixture's pipe, and 2,472 control records from the same two processes in the same capture (420 opens, 384 closes, 1,660 completions, 6 writes, all for ordinary files). Tier `Unsupported`;
- measured run (FX-RPC-001): 8 of 8 truth calls observed, bound to interface `367abb81-9844-35f1-ad32-98f038001003` and completion-paired; 164 server-side records observed but 0 peers paired; reproduced across two runs; tier `ExperimentalEvidence`;
- desktop, second pass: the window was rendered headlessly at 1456 × 939 and at the 1080 × 700 minimum and at every rung of the ladder; five layout defects were found by reading those frames and fixed, the keyboard paths were asserted with real key delivery while the ranked list held focus, and the §17 review was rescored from 11 to 13 of 20;
- desktop, first pass: launched and driven at 1456 × 939 and at the 1080 × 700 minimum; the accessibility tree was read and the table toggle invoked through UI Automation; seven layout, drawing and keyboard defects were found and fixed, and three remain open in the review.

Curated evidence is `fixtures/FX-TCP-001/evidence/` (truth log, scoped observations, verification result), `fixtures/FX-PIPE-001/evidence/` (truth logs, scenario parameters, measurement counters), `fixtures/FX-RPC-001/evidence/`, the portable `fixtures/FX-IDENTITY-001/` scenario, and `fixtures/FX-JOURNAL-001/` plus its benchmarks under `bench/results/`. Raw runs stay local under `fixtures/**/runs/` and are gitignored; the pipe run's raw observations are deliberately not committed because they carry unrelated machine file paths (P16). The comparison runs commit their `comparison.json` and `series.json` counters; their `.icatj` and `.etl` records are gitignored because they hold records from every process on the machine, and a series' per-level truth logs are gitignored because the high levels reach tens of megabytes and are reproducible from the seed. A session or retention run commits its `import-summary.json`, `session.json` and `retention.json` counters, and a metrics run its metric documents, which are taken without `--evidence` so they carry totals and no record's endpoints or process ids; the store beneath it - the journal, the segments and their dictionaries - is gitignored for the same reason, because a segment derived from whole-machine capture carries resource names, endpoints and process ids belonging to unrelated processes (P16).

## Known limitations and cautions

- A reader following a recording should keep one `SessionStore` and acquire leases from it. Opening afresh hashes the
  whole session each time: 0.94 s at 742 MiB (ADR-025). One open per dependency per commit still grows with a
  recording, which §20.1's compaction targets bound.
- A session cannot be re-derived after a journal-prefix release. The release keeps the released records' rows, and a
  replacement can carry none of them yet, so `icat rederive` refuses rather than dropping them (ADR-024). A
  retention published while `icat record` is still running makes the recorder's next commit fail. Retain from a
  finished recording.
- The host build is `10.0.26220.9223`, Windows 11 25H2 x64 on its **pre-release servicing branch**. ADR-007 put it in the §1.3 matrix, so measurements taken on it can promote a tier; every artifact states the branch, because a pre-release build can change under a measurement in a way a retail build cannot. The retail 25H2 build (26200) and 24H2 (26100) are in the matrix and their fixture corpus has not been run.
- The live path builds a `RecordEnvelopeV1` per admitted record, but the record it builds it *from* is still bounded: an eight-slot field projection plus at most four extended-data items of at most 64 bytes each. An item longer than the bound is kept as a flagged prefix with its original length, and an item past the fourth is counted as an omission. The envelope is the production one; the callback capture bounds behind it are not, so a run proves the format's fidelity and not the adapter's completeness.
- Extended data is opt-in per enablement, so a run that does not request it observes none. The 77,355 items that prove the envelope path came from requesting call stacks, which is a different and more expensive load point - it roughly halves the admitted rate - not the default profile.
- The series records fidelity, separate loss counters, the full stage ledger and behaviour at the queue and writer bounds, but the 24,544 admitted records per second it reached is a quarter of §12's ingest target. That budget stays `Missed` and is reported as missed. The record body is an admitted projection under `metadata-only-admitted-projection-v1`, not a copy of the source payload, so the journal's size is not comparable to an ETL's field for field.
- The ETL variant's provider loss reads as a measured zero at every level. It previously read as unknown because the adapter queried the session after stopping it; the read now happens first. An unreadable counter is still reported as unknown rather than zero if it ever recurs.
- The journal is not uniformly smaller than an ETL. It is 9x smaller at the paced level, about twice the size at the peak level, and half the size again once call stacks are attached. The relationship follows from the admission policy and the enablement, so no size claim may be made from one load point.
- The §12 callback budget is met on both halves: p99 under 50 µs at every level, and admission's allocation slope is 0.00085 bytes per record against a bound of one (ADR-009). The delivery thread's own total is still about 1.2 KiB per record; that is the managed adapter's real-time dispatch, reported beside admission's figure and never added to it. Whether it can be reduced is IC-019's question.
- The allocation attribution replays a file, not a real-time session. It proves admission is allocation-free on the shared admission path; that the live allocation is the adapter's follows from admission being allocation-free in the same code, not from a live split measurement.
- The extended-data accessor reaches the callback record through a skip-visibility binding to a private TraceEvent field. It is a disposable IC-009 adapter detail: if a future TraceEvent build removes that field, every record reports extended data as unavailable with the reason, rather than reporting that records carried none. A production capture should use the native consumer path of §18.3 instead.
- Stopping the diagnostic ETL session reports a degradation on this build: its loss counters cannot be read through WMI at stop (`0x80071069`). The ETL variant's provider loss is therefore unknown rather than zero, and the degradation is recorded in both runs.
- Existing pre-contract evidence using `{rawRecordId,factIndex}` remains readable. New builders and serialized output use normalizer version plus a deterministic 128-bit fact key. Removing the compatibility reader requires an explicit fixture migration.
- `contracts/identity-v1.md` constrains canonical ETL import but does not implement it. Source-content identity, equal-time tie handling, collision comparison and multiplicity indexes remain IC-013; I1 and the full I2 claim stay uncovered until then.
- The capture CLI still runs elevated in process because the broker executable remains deliberately disabled. IC-014 now has transport-independent preparation/dispatch/ownership, but R16's process separation is not real until the OS-authenticated pipe host and broker-owned filesystem boundary replace that path.
- Broker quotas and retention are frozen into the prepared digest and returned in the effective summary, but the current runtime is a fake and enforces none of them. Duration, disk/free-space stop behavior and journal finalization must be bound to the real runtime before any capture command is enabled.
- Only TCPv4 loopback is measured. UDP, IPv6, remote peers, reconnect, retransmission under impairment, and flows already open at capture start are unmeasured, so §13.1 scenario 1 is only partly covered.
- `connid` is admitted but is not used as an identity; it was zero on this build.
- Named pipes are measured `Unsupported` through `Microsoft-Windows-Kernel-File` on a supported build (ADR-003). That is a measured absence with its control, not a claim that pipes carry no traffic.
- The pipe measurement enables whole-machine kernel file activity for the duration of the run, which is the dominant overhead of the M0 plan and is disclosed before the capture starts.
- RPC is measured `ExperimentalEvidence` (ADR-004). It carries no byte domain at all, so no RPC volume may ever be shown, and local client-to-server peers stay unresolved because the two sides share no activity id.
- Shared sections and ALPC have registration and schema evidence only. No tier is claimed for them, and ALPC is not measurable under the M0 session strategy.
- IC-010a's current-machine `OverheadClass` is `Low` for both measured sources, but each series is three pairs on one supported pre-release build, measured as whole-machine CPU. Since image paths were admitted, the process pairs range from 0 to 7.09 classified CPU percentage points and the network pairs from 0 to 3.76. The machine was also running other agents during that run, and a whole-machine reading cannot separate their load from the capture's. The artifact keeps that dispersion and the signed observations. Re-run after material adapter/profile changes, on a quieter machine and on the retail build matrix, rather than treating the median as a universal constant.
- Explore currently means process lifecycle plus TCPv4 metadata on this machine. RPC, ALPC and named-pipe entries remain visible optional requests but are omitted because their capture impact is unmeasured; this is intentionally narrower than the final §9.4 breadth. `icat profiles explore` is the source of truth for the effective set before capture.
- Focused transport compiles measured TCP. Without PIDs it requests all-process TCP metadata; with PIDs it preserves those PIDs as initial-view focus but blocks until wider collection is accepted, because network delivery is not PID-filtered and lifecycle context remains whole-machine. UDP and RPC are refused without TCP fallback. Content has a bounded request-only preview but cannot start: RPC debug descriptors 10/11 remain denied, their fragment semantics/scope are not production contracts, and no payload-specific impact series exists. Content is not silently mapped to metadata-only capture, inspection consent does not imply collection or export, and `--diagnostic-etl` blocks because the product path does not yet create a separately governed, potentially content-bearing original ETL. Timing and Flight recorder remain unavailable.
- Four of the ten §12 budgets are measured and six have no stage to measure them. Three measured budgets are met; sustained ingest is the only miss, at roughly a tenth of target in the current call-stack series. Callback admission and its attributed allocation slope are both met; the larger managed-adapter dispatch allocation remains separate IC-019 evidence, not a failed callback-allocation budget.
- The desktop still displays synthetic data only. The ladder, its breadcrumb and its evidence rung are real but the data under them is not: nothing in the prototype is evidence about Windows capture coverage.
- The graph and the timeline do not change with the ladder's rung. At a channel rung the graph still draws the whole machine, which §3.2 does not permit in a finished product; per-level composition needs IC-017's deterministic layout and is recorded as defect 16 in the review.
- The broker root is refused, not repaired, when an untrusted principal owns it. `%ProgramData%` lets any user create an entry, so an ordinary-integrity process can create `%ProgramData%\InterCat` before the broker first runs and the broker will then refuse to start with that owner named. That is the safe outcome and it is a denial of service until the directory is removed or installation pre-creates it; no installer exists yet.
- The root's security is validated at provisioning and pinned by an open handle that refuses rename and delete. Nothing re-validates the descriptor afterwards, so an administrator or SYSTEM process can still loosen it while the broker runs. That is inside the trust boundary by construction - both are already broker principals - but it means the guarantee is "validated at open and un-renameable", not "continuously enforced".
- The measured ETL import read one file on one build. Its 535 policy omissions are events outside the two compiled descriptor plans, which is correct behaviour but also means the admitted share says as much about the plan as about the file. A second file, a second build and a workload whose truth is known independently are what would turn that number into coverage evidence.
- Nothing catalogues completed imports, so the import contract's "reuse a matching completed import" is computable and unused: importing one file into two directories produces two sessions with identical derived bytes rather than one reused import.
- Compaction bounds the ownership log's *size*, not its *contents*. A completed request is kept so its replay stays idempotent, so a broker that runs long enough still reaches the 16,384-request count bound and refuses. What may be forgotten, and after how long, is a retention decision with a privacy and idempotency side to it; it is owed and deliberately not taken by default.
- A published segment holds 181 bytes per row for this corpus - 100,528 bytes over 555 rows - against §12's retention arithmetic of 128 bytes per normalized observation. The gap is the format's choice: every column is uncompressed and directly mappable, there is a slot for every row in every nullable column whether or not it has a value, and nothing is dictionary-coded except the schema and the resource name. Compression and the aggregate tiles of §10.2 are not implemented, so the number is recorded rather than optimized, and §12's figure should be read as a target the derived store has not met yet.
- The kernel-network catalog declares one field set for all six TCP descriptors, so a connect, accept or disconnect record carries a `size` of zero in the transport-observed domain accounted to endpoint activity. It is an observed zero from a field the source populated, and it is reported rather than smoothed away - but what that field means on a non-transfer descriptor is not established by a truth workload, and that is a limitation of the catalog rather than of the segment format.
- A byte metric needs an accounting side, and a descriptor whose kind is neither send nor receive has no proven one, so its contributions are labelled `EndpointActivity`. Since ADR-012 they count toward `EndpointActivityBytes` only and appear in every one-sided total's breakdown as a side it did not take. That is a labelled placeholder, not a resolution: which contribution owns a total once a transfer association is proven is §5.3's canonical-owner rule, which needs the correlators and is still owed as an ADR.
- A query identity covers the members `metrics-v1` implements. Graph projection, viewport, query generation, multi-value and negated filter terms, and keyset cursors are not written yet (IC-017, IC-018); entity and correlation revisions are named by their rule because derivations are computed on demand, and a persisted revision table will replace them under a new canonicalization version.
- A metric is one total over one scope. It can filter by one process focus or by `between(A,B)`, but there is no general filter AST, cache or aggregate cells, and every answer reads every segment in scope: the interval is found by search, process bindings are re-derived and the rows inside it are scanned. That is an inspection path, not the interactive path §12 budgets, and the scheduling that makes it one is IC-017's.
- A total can be grouped by process instance, executable path, mechanism or, with a process focus, peer. Executable grouping uses only a witnessed full image path, keeps missing paths explicitly unattributed, and is unavailable for older generations with no witnessed paths. Owner, participant, sender, receiver and peer filters compose with every grouping. `between(A,B)` over participant sets is still owed.
- Relations exist only for TCP over IPv4, on the six descriptors whose endpoint orientation was measured on one build. An end is divided into connection incarnations only where the capture holds its connects, accepts and disconnects. An end reused while its lifecycle records were lost, or a socket inherited by a second process, stays one incarnation with two holders, and is ambiguous rather than split at a guessed moment. `PeerNotObserved` cannot distinguish a remote peer from a local one whose records were lost; the imported-session coverage ledger reports aggregate loss but cannot locate it at that end or distinguish a remote peer. `ActivePeers` is therefore a lower bound on local peers, with every unresolved end counted beside it. `ActiveChannels` counts TCP connection incarnations only, so a count over other mechanisms is a lower bound whose unknown part is every record no rule covers.
- Process instances now retain source-witnessed image paths, exit basenames, provider start keys, sessions and named parents where present. Paths may be truncated by bounded admission and remain source names, not verified executable-file identities. A name-only exit never merges distinct executables.
- Binding without provider start keys still assumes no missing lifecycle record that would split a lifetime. A contradictory key creates a separate instance with explicit gaps. The imported-session coverage ledger can report source loss, but cannot locate lost lifecycle records or verify this assumption for every binding; live coverage is not published yet.
- A record of a reused PID inside a later instance is unattributed under the default evidence policy. That is identity-v1's deliberate conservatism, and on a long capture it can leave a busy process's later records unattributed; `--evidence-policy include-candidates` attributes them, labelled.
- `Errors` is unavailable in every session because §7.3's status domain has no enumeration, and `Duration`, `MappingCapacity` and every logical-operations or topology request are unavailable because nothing derives what they count. Each says so with its reason rather than answering from what is there.
- A rate is always an observed rate. Imported sessions publish a coverage ledger and metric answers now carry its scoped state, but unlocated source loss cannot identify the particular interval it affected, and the desktop still does not publish the metric/coverage bundle. A rate is never corrected for missing records or presented as a guaranteed complete interval.
- Newly imported sessions can derive a second generation from their published journal and retained descriptor plan. Legacy sessions created before the plan dependency cannot: the journal's schema fingerprint alone cannot reconstruct the compiled field layout. No alternative normalizer version is implemented yet; a semantic change must introduce one with explicit identity and comparison tests, not merely increment a version number.
- Retention is an explicit action on a named extent, not a policy. Nothing decides when to take one, nothing evicts by time or size, and the S5 disclosure that must precede a rolling eviction - session size, measured bytes per observation, retained extent and time remaining under the active policy and free disk - exists only as the per-action measurement `icat retain` prints.
- A journal's retention granularity is the batch size the run that wrote it declared. The default is 4,096 records, so a session smaller than that has exactly one batch and no releasable boundary at all. The preview says so rather than only refusing the boundary it was asked for, but it means retention is not usable on a small session.
- Publication and evidence deletion are serialized across processes by root-owned file guards. Leases themselves remain per-process: process exit releases their OS handles, and a lease is not a durable promise across restart. The global deletion guard can conservatively delay cleanup of unrelated files. Pinned byte allowances are validated per lease but not yet enforced as a cross-process storage quota. Staging is kept on reopen; explicit cleanup proves abandonment only for new marker-owned stages. Old unmarked stages remain for manual review.
- Confirmed recovery selects the already-verified last-known-good generation, not the newest orphan manifest. A newer orphan may contain usable evidence, but promoting it without a verified user choice would hide a rollback. Repair preserves it for inspection and skips its generation number on the next publication. A damaged pointer larger than 1 MiB is refused instead of copied without bound.
- A screen-reader audit has still not been run. The keyboard paths are now asserted in a headless lane with real key delivery, but what a reader announces is untested.

## Recommended next slice

1. **Bind the broker to live recording.** A privileged recording can publish evidence only, and `icat follow` derives it in an ordinary process (ADR-027). The evidence-only path is in `InterCat.Capture.Recording`, which the broker may reference (ADR-028), and each capture now has a protected broker directory. Implement `IBrokerCaptureRuntime` over these, then compose the executable and pipe host; bring the follower where the desktop can reach it. Let the broker schedule retention beside a running recorder, whose next commit a retention would otherwise refuse (ADR-024). §20.1's compaction targets are in (ADR-026); a column-level copy would make a compaction faster than its 200,000 rows per second when a budget needs it. **Widen relations.** UDPv4 is related (ADR-020); §13.1's remaining UDP cases - endpoint reuse, multicast and absent receivers - need fixtures of their own, and the overview graph needs mechanism-labelled edges before UDP joins it (IC-017). §19.1's process filters are complete over TCP, and `ActivePeers` and `ActiveChannels` are answered as lower bounds, including the number of channels a process had with each resolved peer. UDP and IPv6 relations need their own orientation measurement before any rule reads them.
2. **Finish re-derivation compatibility.** Pointer recovery and guarded staging cleanup are explicit and preserve unverified evidence. Carry the rows of records a retention released across a replacement, with their journal indexes and their own derivation label, so a session can be re-derived after a release (ADR-024). Add a legacy-plan migration only where the exact original descriptor interpretation can be proven; extend replacement to multi-capture sessions without dropping another capture's rows. A semantic normalizer change needs a real contract version and stable-raw-identity tests, not an arbitrary bump. Pre-guard unmarked staging cannot be safely deleted automatically and remains for manual review.
3. **Publish §20.2's entity-state checkpoint (IC-016a), once IC-015 exists.** A rolling eviction has to carry the still-live identities, the endpoint bindings, the continuity quality and the pending-operation summaries across the boundary, and an operation open across one has to be censored rather than failed (I20). The retention mechanism beneath it is in; what it can state is what is missing.
4. **Run the fixture corpus on the retail builds in the matrix.** 25H2 retail (26200) and 24H2 (26100) are listed in §1.3 and neither has been measured; the tiers above rest on a pre-release branch of 25H2. §13.4 asks for the corpus on every supported build, and a second environment row is what makes a tier more than one machine's result.
5. What IC-008 deferred - per-level graph and timeline composition - belongs to IC-017, which owns deterministic layout, and should be picked up with it rather than as UI polish.
6. **Continue IC-017 on real data.** The leased overview now has graph, exact graph-eligible timeline and ledger-backed coverage, while the window accepts a non-tour snapshot but still launches the synthetic tour. Add channel/operation/evidence and byte projections, then publish graph, ranking, timeline and coverage as one eligible set to the window. Supersede stale numeric bundles as the layout scheduler now supersedes layout, and compact above the provisional bound. Persist pins by stable instance ID. Keep the graph and its accessible table on the same eligible set; no covered source state may be read as proof that an empty relationship view is complete.

IC-011 and IC-012 are complete for the source evidence the repository actually has. Explore and Focused TCP compile enforceable metadata policies; Content compiles a reviewable request contract and an enforceable refusal. IC-014's local prepare, ownership/recovery/compaction, bounded dispatcher, authenticated pipe and broker-owned filesystem root are complete; what remains is binding it to a live ETW/journal runtime, which depends on M1's store layers rather than on the broker. IC-013 now imports a real standalone ETL end to end and unelevated into a published session, which is what it was missing. M1's durable commit protocol, its manifests and the derived segments a generation publishes are now in, and an import produces a session `icat session` reopens and verifies. A reader holds a generation and its dependencies as one lease, and retention publishes what it released rather than merely deleting it. What a number means is now fixed too: `metrics-v1` answers counts, byte totals and rates over a whole session, refuses what means nothing and reports what cannot be answered. What the store still needs are the entity and relation tables a query joins and groups by, and the checkpoint §20.2 owes at a rolling boundary. Live viewer integration still waits on those layers.
