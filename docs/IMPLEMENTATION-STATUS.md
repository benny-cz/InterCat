# InterCat implementation status

Last updated: 2026-09-21  
Plan revision: 3  
Current milestone: M0 — feasibility and product proof

This is the resume document for implementation work. Update it after every coherent slice with verified results, known limitations, and the next dependency-ordered actions. Capability statements here are evidence-based; a provider being registered does not mean its mechanism is supported.

## Current outcome

The repository has a buildable .NET 10 solution, enforced module boundaries, a schema-driven capability inventory, an owned ETW session with an independent health ledger, a seeded two-process TCP truth workload, and a measured process-and-network vertical path whose coverage tier is computed rather than argued. Durable sessions, the journal envelope, the broker and mechanism breadth beyond TCP are still not implemented.

The first measured run of `FX-TCP-001` met all six §14.2 criteria at 100% with zero false peer attributions and byte totals equal to the truth log. It was taken on build `10.0.26220.0-x64`, which is outside the §1.3 support matrix, so TCP stays at tier `ExperimentalEvidence`: the thresholds were met, the build was not one we support.

`FX-PIPE-001` answered the plan's hard NPFS feasibility gate with a measured negative: `Microsoft-Windows-Kernel-File` produced 2,472 records from the workload's own processes in the same capture and not one create, read or write for the pipe object. Named pipes are therefore `Unsupported` by measurement, and ADR-003 records the resulting scope decision instead of restating the original goal as achieved.

## Backlog status

| Item | State | Evidence / remaining work |
|---|---|---|
| IC-001 solution and boundaries | Complete for M0 | `InterCat.slnx`, central build/package settings, ADR-001, 5 architecture tests. Build has zero warnings. |
| IC-002 capability/schema inventory | Complete for M0 | `icat capabilities` reads the TDH manifest per registered provider, compiles the admission plan those schemas allow, and reports registration, enablement, observed health and semantic coverage as four separate checks with exact fields, units, attribution roles and unavailable reasons. Contract: `contracts/capability-report-v1.md`. Artifact: `capabilities/10.0.26220.0-x64/windows-etw-inventory.json`. Remaining: classic/kernel (non-manifest) source inventory beyond the ALPC placeholder. |
| IC-003 owned ETW lifecycle | Complete for M0 | `OwnedCaptureSession` implements the §9.2 states, unique name plus ownership token, allowlisted provider plans, event-id scoping, bounded queue with independent drop counters, post-start rundown, and cleanup limited to the session an attempt created. 8 lifecycle tests run against a fake host with no ETW or elevation. ADR-002 records the strategy. |
| IC-004 TCP/RPC truth workloads | TCP and named pipe complete; RPC not started | `tcp-loopback` and `pipe-loopback` each run a seeded two-process exchange, and each process writes its own JSONL truth log from ordinary socket or pipe calls. The pipe scenario includes a deliberately short read so requested and completed sizes differ (§21.1). Local RPC workloads remain for IC-006. |
| IC-005 pipe/section feasibility | Named pipes measured; sections not started | `icat measure pipe` runs `FX-PIPE-001` under the owned session and computes the tier. Result on this build: `Unsupported`, with a 2,472-record control proving the source was live for the same processes. ADR-003 records the scope decision and routes the gap to M7 and M9. Shared sections remain a lead only. |
| IC-006 ALPC/RPC feasibility | Not started | RPC schema is inventoried and its payload-bearing descriptors are denied by default; no pairing is measured. ALPC needs a system logger, which ADR-002 deliberately excludes from M0. |
| IC-007 IDs and clocks | In progress | Normative enums, capture/process/raw/observation IDs, half-open ranges, integer viewport math and observed process start keys (`ProcessSequenceNumber` with `CreateTime`) exist. Lifecycle epochs, deterministic fact keys, clock encoding and PID-reuse fixtures remain. |
| IC-008 interaction prototype | In progress | Equal graph/timeline panes, ranked-process selection, inspector, keyboard graph navigation, timeline pan/zoom and 7 viewport/selection tests exist. Manual visual/accessibility QA, theme tokens with contrast tests and the scored §17 review remain. |
| IC-009 journal/admission validation | Not started | Requires the journal envelope. No session format has been frozen. |
| IC-010 benchmark baseline | Not started | Reference hardware and stage budgets are still unmeasured; `OverheadClass` is reported as `Unmeasured` everywhere. |

M1 and later items have not started. `InterCat.Storage` remains a boundary placeholder.

## Implemented structure

- `InterCat.Domain`: `Ids/`, `Time/`, `Measurements/`, `Capabilities/`, `Observations/` — wire enums, strong IDs, half-open ranges, pure viewport math, the §4.3 capability descriptor, the §14.2 tier calculator, the §1.3 support matrix, and the M0 observation and truth-record shapes.
- `InterCat.Storage`: boundary placeholder referencing only Domain.
- `InterCat.Analysis`: `Tcp/TcpCoverageEvaluator` and `Pipes/PipeCoverageEvaluator` — pure, portable comparisons of a truth log with admitted observations, producing the §14.2 counters and their evidence, including the control count that makes a negative result trustworthy.
- `InterCat.Application`: deterministic synthetic workspace and the single revisioned selection coordinator.
- `InterCat.Capture.Windows`: `Tdh/` schema reading and parsing behind `IEtwMetadataSource`; `Profiles/` source catalog; `Etw/` admission compilation, admitted-record struct, health ledger, owned session state machine and the TraceEvent host; `Inventory/` capability probe.
- `InterCat.CaptureBroker`: still an intentionally disabled placeholder.
- `InterCat.Desktop`: synthetic Avalonia prototype with custom graph/timeline drawing.
- `InterCat.Cli`: `capabilities`, `measure tcp`, `measure pipe`, `verify tcp`, with documented exit codes, stdout for data and stderr for progress.
- `InterCat.TestWorkloads`: the FX-TCP-001 and FX-PIPE-001 seeded scenarios and their truth log writer.
- tests: Domain, Analysis, Application, Capture.Windows and Architecture.

## Decisions and plan corrections

- ADR-002 records the M0 logger strategy: a uniquely named manifest-provider session created with `NoRestartOnCreate`, never a system logger, never an adoption of an existing session.
- Admission offsets are compiled from the machine's own TDH manifest rather than from constants, so a schema change turns into a refusal with a reason instead of a misread field.
- Measured field semantics for `Microsoft-Windows-Kernel-Network`: addresses and ports arrive in network byte order, and `saddr`/`sport` names the owning process's own endpoint on send and receive alike. The manifest message wording does not describe which side is local.
- Admission now compiles pointer-sized fields and one bounded resource name per descriptor. A record whose pointer width does not match the compiled offsets is counted as undecodable rather than read at a guessed offset.
- A measured negative result must carry its control. `PipeCoverageEvaluator` counts the records the source delivered from the fixture's own processes, so "nothing observed" can be told apart from "the source was never live".
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
dotnet run --project src/InterCat.Cli --no-build -- verify tcp --run <run directory> --output fixtures/FX-TCP-001/evidence --overwrite
```

Results on 2026-09-21:

- build: passed, 0 warnings, 0 errors across 14 projects;
- tests: 45 passed, 0 failed (16 Domain, 13 Capture.Windows, 9 Analysis, 5 Architecture, 2 Application);
- capability probe: passed; 1,237 published providers seen, 6 of 7 catalog sources registered and schema-readable;
- measured run `20260921T080303Z`: session `InterCat-m0tcp-64872-98b7614f`, 42 other ETW sessions left untouched, 629 records observed and 629 admitted, zero application drops, zero provider-reported loss, zero buffer loss, zero undecodable records, no degradations;
- coverage: 48 of 48 truth operations observed and bound to a flow instance with a byte measurement, 4 peer attributions with 0 false, 2 of 2 connections discovered with lifecycle, 4 of 4 memberships resolved, tier `ExperimentalEvidence`;
- byte agreement: truth completed 28,527 B sent; observed transport 28,527 B sent (reported side by side, never summed across domains);
- process evidence: both workload processes observed with start, exit and a non-reusable sequence number;
- measured run `20260921T083314Z` (FX-PIPE-001): session `InterCat-m0pipe`, 13,198 records observed and admitted, zero loss, zero drops, zero undecodable;
- pipe coverage: 0 of 25 truth operations observed, 0 pipe creates, reads or writes for the fixture's pipe, and 2,472 control records from the same two processes in the same capture (420 opens, 384 closes, 1,660 completions, 6 writes, all for ordinary files). Tier `Unsupported`;
- desktop startup smoke and manual visual/accessibility QA: not run in this slice.

Curated evidence is `fixtures/FX-TCP-001/evidence/` (truth log, scoped observations, verification result) and `fixtures/FX-PIPE-001/evidence/` (truth logs, scenario parameters, measurement counters). Raw runs stay local under `fixtures/**/runs/` and are gitignored; the pipe run's raw observations are deliberately not committed because they carry unrelated machine file paths (P16).

## Known limitations and cautions

- The host build `10.0.26220.0` is outside the §1.3 support matrix. Every measured claim here is evidence on an untested build, and the tier calculator caps promotion accordingly (P27).
- The M0 capture path is an explicitly disposable spike under §21.2. It admits a bounded eight-slot field projection, not the `RecordEnvelopeV1` journal of §18.1, and it cannot yet replay, persist or re-identify records. IC-011 replaces it.
- The whole CLI runs elevated because no broker exists yet. R16's separation arrives with IC-014.
- Only TCPv4 loopback is measured. UDP, IPv6, remote peers, reconnect, retransmission under impairment, and flows already open at capture start are unmeasured, so §13.1 scenario 1 is only partly covered.
- `connid` is admitted but is not used as an identity; it was zero on this build.
- Named pipes are measured `Unsupported` through `Microsoft-Windows-Kernel-File` on this build (ADR-003). That is a measured absence with its control, not a claim that pipes carry no traffic.
- The pipe measurement enables whole-machine kernel file activity for the duration of the run, which is the dominant overhead of the M0 plan and is disclosed before the capture starts.
- Shared sections, ALPC and RPC have registration and schema evidence only. No tier is claimed for them.
- Overhead is unmeasured: no benchmark, no reference machine, no stage budgets (IC-010).
- The desktop still displays synthetic data only, and its accessibility work is incomplete.

## Recommended next slice

1. Re-run `FX-TCP-001` on Windows 11 24H2 x64 and record the second environment row. Promotion to `TrafficVisualization` needs that run plus a repeat run setting `reproduced`.
2. Extend FX-TCP-001 toward the rest of §13.1 scenario 1: a flow already open at capture start, reconnect with port reuse, concurrent clients, and IPv6. Each new case is a new fixture number, never a renumbered one.
3. Start IC-011: define `RecordEnvelopeV1`, replace the field projection with owned envelopes, and keep the identity, ownership and counter tests passing across the swap.
4. Probe one more driverless lead for named pipes before accepting ADR-003 as final: enumerate whether any other registered source reports NPFS creates, and re-run `FX-PIPE-001` against it. If none does, the ADR stands as written.
5. Finish the IC-008 remainder: theme tokens with recorded contrast ratios, accessible table equivalents, and the scored §17 usability review at 1440×900 and at minimum size.

Do not begin journal format freezing beyond IC-011's contract, or live viewer/broker integration (M2), until the journal-vs-ETL evidence gate of IC-009 is complete.
