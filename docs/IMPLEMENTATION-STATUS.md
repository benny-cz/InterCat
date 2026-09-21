# InterCat implementation status

Last updated: 2026-09-21  
Plan revision: 6
Current milestone: M0 — feasibility and product proof

This is the resume document for implementation work. Update it after every coherent slice with verified results, known limitations, and the next dependency-ordered actions. Capability statements here are evidence-based; a provider being registered does not mean its mechanism is supported.

## Current outcome

The repository has a buildable .NET 10 solution, enforced module boundaries, a schema-driven capability inventory, an owned ETW session with an independent health ledger, seeded truth workloads, and measured TCP, named-pipe and RPC feasibility results whose coverage tiers are computed rather than argued. IC-007 supplies the production identity and local-clock foundation. IC-009 has now been measured, not merely made runnable: the owned callback envelope copies bounded arbitrary ETW extended-data items, persists the identity-v1 source clock descriptor with the file, and carries an isolated per-stage overhead ledger, and `InterCat.CaptureComparison` has run twice from an elevated shell against the same seeded workload. Both runs replayed with an identical ordered fingerprint and both remain `decisionReady: false`: the baseline run states three blockers and the call-stack run two, and none of them is hidden. Durable production sessions, journal-v1, the broker and mechanism breadth beyond the measured probes are still not implemented.

The first measured run of `FX-TCP-001` met all six §14.2 criteria at 100% with zero false peer attributions and byte totals equal to the truth log. It was taken on build `10.0.26220.0-x64`, which is outside the §1.3 support matrix, so TCP stays at tier `ExperimentalEvidence`: the thresholds were met, the build was not one we support.

`FX-RPC-001` measured local RPC: every truth call was observed, bound to the expected interface and paired with its completion, reproduced across two runs in one capture. RPC is `ExperimentalEvidence` with two gaps that no amount of work on this source can close: it carries no size on any descriptor, and an interface has no lifetime to discover. ADR-004 records the resulting scope, including that no RPC volume claim may be made anywhere in the product.

The workspace prototype now carries a measured palette, randomized navigation and bucketing properties, table equivalents for both canvases, and a scored interaction review taken from the running application.

`FX-PIPE-001` answered the plan's hard NPFS feasibility gate with a measured negative: `Microsoft-Windows-Kernel-File` produced 2,472 records from the workload's own processes in the same capture and not one create, read or write for the pipe object. Named pipes are therefore `Unsupported` by measurement, and ADR-003 records the resulting scope decision instead of restating the original goal as achieved.

## Backlog status

| Item | State | Evidence / remaining work |
|---|---|---|
| IC-001 solution and boundaries | Complete for M0 | `InterCat.slnx`, central build/package settings, ADR-001, 5 architecture tests. Build has zero warnings. |
| IC-002 capability/schema inventory | Complete for M0 | `icat capabilities` reads the TDH manifest per registered provider, compiles the admission plan those schemas allow, and reports registration, enablement, observed health and semantic coverage as four separate checks with exact fields, units, attribution roles and unavailable reasons. Contract: `contracts/capability-report-v1.md`. Artifact: `capabilities/10.0.26220.0-x64/windows-etw-inventory.json`. Remaining: classic/kernel (non-manifest) source inventory beyond the ALPC placeholder. |
| IC-003 owned ETW lifecycle | Complete for M0 | `OwnedCaptureSession` implements the §9.2 states, unique name plus ownership token, allowlisted provider plans, event-id scoping, bounded queue with independent drop counters, post-start rundown, and cleanup limited to the session an attempt created. 8 lifecycle tests run against a fake host with no ETW or elevation. ADR-002 records the strategy. |
| IC-004 TCP/RPC truth workloads | Complete for M0 | `tcp-loopback` and `pipe-loopback` each run a seeded two-process exchange, and each process writes its own JSONL truth log from ordinary socket or pipe calls. The pipe scenario includes a deliberately short read so requested and completed sizes differ (§21.1), and `rpc-local` issues a known number of local RPC calls through the documented service API with no package dependency. |
| IC-005 pipe/section feasibility | Named pipes measured; sections not started | `icat measure pipe` runs `FX-PIPE-001` under the owned session and computes the tier. Result on this build: `Unsupported`, with a 2,472-record control proving the source was live for the same processes. ADR-003 records the scope decision and routes the gap to M7 and M9. Shared sections remain a lead only. |
| IC-006 ALPC/RPC feasibility | RPC measured; ALPC not measurable in M0 | `icat measure rpc` runs `FX-RPC-001` twice in one session and computes the tier: 8 of 8 calls observed, bound and completion-paired, tier `ExperimentalEvidence`, gaps recorded in ADR-004. Server-side records arrive but share no activity id with the client, so peers stay unresolved rather than inferred. ALPC remains a kernel flag group with no registered manifest provider and no tier claimed. |
| IC-007 IDs and clocks | Complete for its live/local M0 scope | `contracts/identity-v1.md`, ADR-005 and ADR-006 define raw and normalized observation IDs, process/resource lifecycle epochs, alias revisions and native/local/wall/workspace time. `FX-IDENTITY-001` asserts PID and resource reuse, late old-key resolution, cross-host isolation, immutable alignment, checked QPC conversion and quarantine. The full I1/I2 standalone-ETL proof remains correctly assigned to IC-013 rather than being claimed here. |
| IC-008 interaction prototype | Near complete for M0 | Equal panes, ranked selection, inspector, legend with glyphs, table equivalents for graph and timeline, keyboard and automation paths, a measured theme (`theme/`) whose thresholds are enforced by tests, 41 randomized §6.7 and boundary property tests, and the scored §17 review in `docs/reviews/M0-interaction-review.md` (11 of 20, with 7 defects found and fixed). Remaining: the L0–L5 ladder, a per-edge evidence list, the diagonal coverage hatch, and a headless UI test lane for keyboard shortcuts. |
| IC-009 journal/admission validation | In progress; envelope, clock and stage ledger measured on real callbacks | `FX-JOURNAL-001` asserts unknown-schema/content omission, per-record attribution, extended-data replay, owned copies, corruption refusal and clock-frame refusal. The callback envelope copies up to four extended items of up to 64 bytes with original lengths and truncation flags, persists the source clock descriptor as the file's first frame, and reports callback latency, delivery-thread CPU and allocations, queue high-water, writer CPU and per-flush latency as separate quantities. Two elevated runs are recorded in `bench/results/`. Remaining: a declared load series to queue and disk saturation on the reference device, and a repeat on a supported build. ADR-008 stays proposed; no production format is frozen. |
| IC-010 benchmark baseline | Not started | Reference hardware and stage budgets are still unmeasured; `OverheadClass` is reported as `Unmeasured` everywhere. |

M1 and later items have not started. `InterCat.Storage` contains only explicitly disposable IC-009 batch/file probes; journal-v1, segments, manifests and production recovery remain unimplemented.

The two elevated IC-009 runs of 2026-09-21 are the current state of the evidence gate:

| | baseline | call stacks requested |
|---|---|---|
| Journal observed / admitted | 707 / 707 | 909 / 822 |
| ETL observed / admitted | 854 / 852 | 849 / 759 |
| Loss, drops | none in either variant | none in either variant |
| Evidence size, journal vs ETL | 226 KiB vs 1.63 MiB | 277 KiB vs 1.75 MiB |
| Extended items copied / persisted / replayed | 0 / 0 / 0 (none requested) | 190 / 190 / 190 `STACK_TRACE64` |
| Source clock | confirmed and replayed identical | confirmed and replayed identical |
| Callback p50 / p99 | [1.02, 2.05) µs / [8.19, 16.38) µs | [1.02, 2.05) µs / [16.38, 32.77) µs |
| Queue high-water | 706 of 65,536 | 657 of 65,536 |
| Decision blockers | 3 | 2 |

## Implemented structure

- `InterCat.Domain`: `Ids/`, `Time/`, `Measurements/`, `Capabilities/`, `Observations/` — wire enums; deterministic raw/observation/process/resource identities and a deterministic local host identity; lifecycle epochs and alias revisions; native-clock descriptors, checked conversion and quarantine; a bounded allocation-free latency histogram that reports quantile bounds rather than interpolated values; half-open ranges; pure viewport math; capability/tier contracts; and the M0 observation and truth-record shapes.
- `InterCat.Storage`: portable `JournalProbe` admission policy, semantic envelope, attribution summary, bounded checksummed version-0 batch codec, a checksummed source-clock frame, and disposable multi-batch file framing with durable flushes, an explicit terminal frame and a per-stage writer ledger. None is a supported session format.
- `InterCat.Analysis`: `Tcp/TcpCoverageEvaluator`, `Pipes/PipeCoverageEvaluator` and `Rpc/RpcCoverageEvaluator` — pure, portable comparisons of a truth log with admitted observations, producing the §14.2 counters and their evidence, including the control count that makes a negative result trustworthy.
- `InterCat.Application`: deterministic synthetic workspace and the single revisioned selection coordinator.
- `InterCat.Capture.Windows`: `Tdh/` schema reading and parsing behind `IEtwMetadataSource`; `Profiles/` source catalog; `Etw/` admission compilation, admitted-record struct with bounded inline extended-data storage, an `EVENT_RECORD` extended-data walker and its accessor, the ETW source-clock descriptor and its plausibility check, per-thread processor time, health and stage ledgers, owned live and diagnostic-ETL session state machines, shared live/offline TraceEvent admission, and ETL replay; `Inventory/` capability probe.
- `InterCat.CaptureBroker`: still an intentionally disabled placeholder.
- `InterCat.Desktop`: `Theme/` measured palette, colour math and token resources; `Presentation/` legend and table rows; synthetic Avalonia prototype with custom graph/timeline drawing that carries no colour literal.
- `InterCat.Cli`: `capabilities`, `measure tcp`, `measure pipe`, `measure rpc`, `verify tcp`, with documented exit codes, stdout for data and stderr for progress.
- `InterCat.TestWorkloads`: the FX-TCP-001, FX-PIPE-001 and FX-RPC-001 scenarios and their truth log writer.
- `tools/InterCat.ThemeReport`: regenerates `theme/tokens.json`, `theme/contrast-report.json` and `theme/README.md`.
- `tools/InterCat.JournalProbe`: runs the portable Release admission/replay measurement and writes a self-limiting benchmark artifact.
- `tools/InterCat.CaptureComparison`: elevated IC-009 same-seed journal/ETL harness. It maps callbacks into the envelope candidate on a dedicated writer thread, prints a side-by-side stage ledger with numbered decision blockers, refuses existing output, and emits `decisionReady: false` until every gate input is present.
- tests: Domain, Storage, Analysis, Application, Capture.Windows, CaptureComparison, Desktop, Property and Architecture.

## Decisions and plan corrections

- ADR-002 records the M0 logger strategy: a uniquely named manifest-provider session created with `NoRestartOnCreate`, never a system logger, never an adoption of an existing session.
- ADR-005 makes reusable numeric values insufficient for identity, gives late provider start keys precedence over delivery time, and preserves provisional identities through versioned aliases.
- ADR-006 keeps native, session-relative, wall-clock and workspace-aligned time distinct; conversion uses checked `Int128`, nearest-even rounding and explicit quarantine rather than clamping.
- ADR-008 records the journal-versus-ETL gate as proposed, not accepted. Fidelity, separate loss counters and the stage ledger are now measured on real callbacks; saturation behaviour and a supported build are not.
- Extended-data items are opt-in per provider enablement, not a property of an event. A capture that does not request them observes none, so a zero count describes the capture's configuration and the plan now records the enable properties alongside the records (§18.1, §18.2).
- Requesting call stacks changes delivery enough that the 2-second default reorder grace truncated a run and produced a coverage collapse that was an artefact of the grace. The harness now defaults to 6 seconds when stacks are requested and says so.
- Per-thread processor time is the only honest attribution for a callback's cost, and the Windows thread clock advances about every 15.6 ms. A stage below one tick is reported as such, never as zero.
- A denied extended type is a counted policy omission whose bytes never reach the file, and replay reproduces the omission instead of inventing the item. SIDs, TraceLogging schema items and provider traits are denied under metadata-only admission.
- Plan revision 5 preserves revision 4's ADR/identity corrections and removes the IC-009/IC-011 cycle. IC-009 may implement a disposable real-callback envelope candidate needed for the evidence gate; IC-011 begins only after ADR-008 closes and owns the production `journal-v1` contract and implementation.
- Plan revision 6 corrects three things the implementation found: §18.1 now requires the capture's source clock descriptor to be persisted once per journal rather than only named per record, states that extended-data items are opt-in per enablement and must be recorded as a setting, and calls the item's second header field a linkage bit rather than a general flags word; §18.2's `ProviderPlan` names what `EnableProperties` decides; and §12 states how a quantized per-thread processor reading and a bucketed latency distribution are reported.
- Admission offsets are compiled from the machine's own TDH manifest rather than from constants, so a schema change turns into a refusal with a reason instead of a misread field.
- Measured field semantics for `Microsoft-Windows-Kernel-Network`: addresses and ports arrive in network byte order, and `saddr`/`sport` names the owning process's own endpoint on send and receive alike. The manifest message wording does not describe which side is local.
- Admission now compiles pointer-sized fields, one bounded resource name and one 16-byte identifier per descriptor, and preserves the header activity ids that relate a start to its completion.
- An ETW event-id filter is either an allow list or a deny list. Supplying both makes a provider refuse enablement, so the allow list is sent and the deny list stays in the recorded plan and in the callback as a second refusal (P28).
- Admission now compiles pointer-sized fields and one bounded resource name per descriptor. A record whose pointer width does not match the compiled offsets is counted as undecodable rather than read at a guessed offset.
- A measured negative result must carry its control. `PipeCoverageEvaluator` counts the records the source delivered from the fixture's own processes, so "nothing observed" can be told apart from "the source was never live".
- The palette is computed, not chosen: adjacent mechanism families are staggered in CIELAB lightness so they stay separable in greyscale and under simulated protanopia, deuteranopia and tritanopia. `theme/contrast-report.json` records every measurement, and a change that breaks one fails a test.
- Navigation and bucketing properties are generated from fixed seeds rather than demonstrated on examples, and a failure prints the case that broke.
- `fixtures/index.json` is the single machine-readable fixture declaration and the contract-coverage ledger. Two architecture tests keep it honest: declared coverage must equal what tests actually assert, and every fixture entry must name artifacts and tests that exist.
- Raw measurement runs are not committed. Only curated, fixture-scoped evidence is, because a raw run carries unrelated whole-machine flows (P16).

## Verification record

Run from the repository root; `measure` requires an elevated shell:

```powershell
dotnet restore InterCat.slnx
dotnet build InterCat.slnx --no-restore
dotnet test InterCat.slnx --no-build
dotnet run --project src/InterCat.Cli --no-build -- capabilities --output capabilities/10.0.26220.0-x64/windows-etw-inventory.json --overwrite
dotnet run --project src/InterCat.Cli --no-build -- measure tcp --connections 2 --messages 6
dotnet run --project src/InterCat.Cli --no-build -- measure pipe --messages 6
dotnet run --project src/InterCat.Cli --no-build -- measure rpc --calls 8
dotnet run --project src/InterCat.Cli --no-build -- verify tcp --run <run directory> --output fixtures/FX-TCP-001/evidence --overwrite
dotnet run --project tools/InterCat.ThemeReport --no-build -- theme
dotnet run --project tools/InterCat.JournalProbe -c Release -- bench/results/journal-probe-portable.json 10000 7
dotnet run --project tools/InterCat.CaptureComparison -c Release -- --output bench/results/capture-comparison-<run-id>
dotnet run --project tools/InterCat.CaptureComparison -c Release -- --output bench/results/capture-comparison-<run-id>-stacks --request-stacks true
dotnet run --project src/InterCat.Desktop --no-build
```

Results on 2026-09-21:

- build: passed, 0 warnings, 0 errors across 21 projects, in Debug and Release;
- tests: 162 passed, 0 failed (41 Property, 37 Domain, 36 Capture.Windows, 14 Analysis, 12 Storage, 10 Desktop, 5 Architecture, 5 CaptureComparison, 2 Application);
- comparison harness: two elevated Release runs recorded under `bench/results/`, exit code 1 in both because decision blockers remain; 0 InterCat ETW sessions were left behind after either run;
- journal probe: 10,000 records, 7 Release iterations; median combined admit/encode/decode rate 215,720 records/s, exact identity and extended-data replay, 0 unapproved bodies retained, complete attribution. This allocated about 33.7 MB per iteration and is not callback-path code;
- theme verification: every recorded threshold met in both modes;
- capability probe: passed; 1,237 published providers seen, 6 of 7 catalog sources registered and schema-readable;
- measured run `20260921T080303Z`: session `InterCat-m0tcp-64872-98b7614f`, 42 other ETW sessions left untouched, 629 records observed and 629 admitted, zero application drops, zero provider-reported loss, zero buffer loss, zero undecodable records, no degradations;
- coverage: 48 of 48 truth operations observed and bound to a flow instance with a byte measurement, 4 peer attributions with 0 false, 2 of 2 connections discovered with lifecycle, 4 of 4 memberships resolved, tier `ExperimentalEvidence`;
- IC-009 elevated comparison `capture-comparison-20260921-baseline`: journal 707 observed and 707 admitted with no loss, drops or undecodable records; ETL 854 observed and 852 admitted; 226 KiB of admitted journal against 1.63 MiB of unfiltered ETL for the same fixture; the ordered envelope fingerprint replayed identically; the source clock was confirmed against a delivered record and read back identically; no record declared an extended item, because none was requested;
- IC-009 elevated comparison `capture-comparison-20260921-stacks`: 190 `STACK_TRACE64` extended items copied in the callback, persisted in envelopes and replayed identically, plus 87 records from the unrequested kernel stack-walk provider counted as policy omissions rather than loss; both variants still met all six §14.2 criteria;
- IC-009 stage ledger at that load point: callback p50 in [1.02, 2.05) µs and p99 in [8.19, 16.38) µs without stacks and [16.38, 32.77) µs with them, mean about 11 µs, maximum 6.6–7.3 ms on the first callbacks; delivery thread allocated about 1.59 MiB per run, which is the §12 allocation budget missed and IC-011's pooling obligation; queue high-water 706 of 65,536; writer thread 16 ms of processor time, 2 durable flushes, flush p99 in [4.19, 8.39) ms;
- byte agreement: truth completed 28,527 B sent; observed transport 28,527 B sent (reported side by side, never summed across domains);
- process evidence: both workload processes observed with start, exit and a non-reusable sequence number;
- measured run `20260921T083314Z` (FX-PIPE-001): session `InterCat-m0pipe`, 13,198 records observed and admitted, zero loss, zero drops, zero undecodable;
- pipe coverage: 0 of 25 truth operations observed, 0 pipe creates, reads or writes for the fixture's pipe, and 2,472 control records from the same two processes in the same capture (420 opens, 384 closes, 1,660 completions, 6 writes, all for ordinary files). Tier `Unsupported`;
- measured run (FX-RPC-001): 8 of 8 truth calls observed, bound to interface `367abb81-9844-35f1-ad32-98f038001003` and completion-paired; 164 server-side records observed but 0 peers paired; reproduced across two runs; tier `ExperimentalEvidence`;
- desktop: launched and driven at 1456 × 939 and at the 1080 × 700 minimum; the accessibility tree was read and the table toggle invoked through UI Automation; seven layout, drawing and keyboard defects were found and fixed, and three remain open in the review.

Curated evidence is `fixtures/FX-TCP-001/evidence/` (truth log, scoped observations, verification result), `fixtures/FX-PIPE-001/evidence/` (truth logs, scenario parameters, measurement counters), `fixtures/FX-RPC-001/evidence/`, the portable `fixtures/FX-IDENTITY-001/` scenario, and `fixtures/FX-JOURNAL-001/` plus its benchmarks under `bench/results/`. Raw runs stay local under `fixtures/**/runs/` and are gitignored; the pipe run's raw observations are deliberately not committed because they carry unrelated machine file paths (P16). The two comparison runs commit their `comparison.json` counters and their fixture truth logs; their `.ijp0` and `.etl` records are gitignored because they hold records from every process on the machine.

## Known limitations and cautions

- The host build `10.0.26220.0` is outside the §1.3 support matrix. Every measured claim here is evidence on an untested build, and the tier calculator caps promotion accordingly (P27).
- The M0 live path admits a bounded eight-slot field projection plus at most four extended-data items of at most 64 bytes each, not `RecordEnvelopeV1`. An item longer than the bound is kept as a flagged prefix with its original length, and an item past the fourth is counted as an omission. The bounds are deliberate and are not the production envelope, so this still cannot prove final-envelope fidelity.
- Extended data is opt-in per enablement, so the baseline run observed none. The 190 items that prove the envelope path came from requesting call stacks, which is a different and more expensive load point, not the default profile.
- `journal-probe-v0`, its `.ijp0` file framing and `callback-envelope-candidate-v0` are deliberately disposable. The elevated harness now records fidelity, separate loss counters and the full stage ledger, but it exercises one load point whose queue never exceeded 1.1% of capacity. Nothing here justifies a throughput claim, and ADR-008 stays proposed until a saturation series runs on the reference device and on a supported build.
- The callback allocated about 1.59 MiB per run on the delivery thread. The §12 callback budget asks for p99 under 50 µs *and* no unpooled allocation: the latency half is met at this load, the allocation half is not, and pooled ownership remains IC-011's work.
- The extended-data accessor reaches the callback record through a skip-visibility binding to a private TraceEvent field. It is a disposable IC-009 adapter detail: if a future TraceEvent build removes that field, every record reports extended data as unavailable with the reason, rather than reporting that records carried none. A production capture should use the native consumer path of §18.3 instead.
- Stopping the diagnostic ETL session reports a degradation on this build: its loss counters cannot be read through WMI at stop (`0x80071069`). The ETL variant's provider loss is therefore unknown rather than zero, and the degradation is recorded in both runs.
- Existing pre-contract evidence using `{rawRecordId,factIndex}` remains readable. New builders and serialized output use normalizer version plus a deterministic 128-bit fact key. Removing the compatibility reader requires an explicit fixture migration.
- `contracts/identity-v1.md` constrains canonical ETL import but does not implement it. Source-content identity, equal-time tie handling, collision comparison and multiplicity indexes remain IC-013; I1 and the full I2 claim stay uncovered until then.
- The whole CLI runs elevated because no broker exists yet. R16's separation arrives with IC-014.
- Only TCPv4 loopback is measured. UDP, IPv6, remote peers, reconnect, retransmission under impairment, and flows already open at capture start are unmeasured, so §13.1 scenario 1 is only partly covered.
- `connid` is admitted but is not used as an identity; it was zero on this build.
- Named pipes are measured `Unsupported` through `Microsoft-Windows-Kernel-File` on this build (ADR-003). That is a measured absence with its control, not a claim that pipes carry no traffic.
- The pipe measurement enables whole-machine kernel file activity for the duration of the run, which is the dominant overhead of the M0 plan and is disclosed before the capture starts.
- RPC is measured `ExperimentalEvidence` (ADR-004). It carries no byte domain at all, so no RPC volume may ever be shown, and local client-to-server peers stay unresolved because the two sides share no activity id.
- Shared sections and ALPC have registration and schema evidence only. No tier is claimed for them, and ALPC is not measurable under the M0 session strategy.
- Overhead is unmeasured: no benchmark, no reference machine, no stage budgets (IC-010).
- The desktop still displays synthetic data only. The L0–L5 ladder, per-edge evidence, the diagonal coverage hatch and a screen-reader audit remain open, and the `T` and `Escape` shortcuts are implemented but were not verified end to end because synthetic key delivery could not be confirmed from a script.

## Recommended next slice

1. Extend `InterCat.CaptureComparison` to a declared load series rather than one point: a sequence of workload rates that drives the bounded queue to saturation and the writer to disk saturation, each level recorded with its own stage ledger and its own loss counters. The queue reached 1.1% of capacity in both current runs, so nothing measured so far says what happens at the limit.
2. Run that series on the §12 reference storage device and publish the device, the rates and the seed with it. Accept or reject ADR-008 only from the complete ledger; it stays proposed until then.
3. Only after ADR-008 closes, start IC-011: freeze `RecordEnvelopeV1`, replace the disposable framing with pooled production ownership so the §12 allocation budget is met, and keep identity, ownership, corruption and counter tests passing across the swap.
4. Re-run `FX-TCP-001` and both comparison runs on Windows 11 24H2 x64 and record the second environment row. Promotion to `TrafficVisualization` needs that run plus a repeat run setting `reproduced`.
5. Independently of the gate, IC-008 still owes the L0–L5 ladder, a per-edge evidence list, the diagonal coverage hatch and a headless UI lane for keyboard shortcuts.

Do not begin journal format freezing beyond IC-011's contract, or live viewer/broker integration (M2), until the journal-vs-ETL evidence gate of IC-009 is complete.
