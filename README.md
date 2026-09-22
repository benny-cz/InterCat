# InterCat

InterCat is a Windows IPC visualizer for exploring which processes communicate, through which mechanism, and with what observable evidence. The project is in milestone M1: capability claims and capture profiles are intentionally gated on measured fixtures and explicit admission policy.

## Current vertical slice

- a pinned .NET 10 solution with enforced inward dependency boundaries;
- a schema-driven capability inventory that keeps registration, enablement, observed health and validated semantics as four separate checks;
- a read-only capture-profile compiler that shows requested/effective sources, actual provider scope, exact provider settings, body policy, overhead evidence and omissions before capture;
- an owned ETW session with a unique name, an ownership token, bounded admission and an independent health ledger;
- seeded two-process TCP and named-pipe workloads with independent truth logs, and measured coverage results whose tiers are computed from the plan's §14.2 thresholds;
- a synthetic Avalonia graph/timeline prototype with linked selection, table equivalents for both canvases, and a palette whose contrast and colour-vision separation are measured rather than chosen;
- a durable session store: a commit protocol that publishes an immutable generation and rolls a torn publication back, `journal-v1` admitted evidence, and frozen `observation-v1` columnar segments with null bitmaps, availability counters, sorted dictionaries, a raw-record locator and time-block metadata;
- an unelevated import that turns a standalone ETL into a session a reader can open, and a read-only `session` command that re-verifies every dependency and reports each byte domain and side separately;
- pure viewport, tier and semantic-domain contracts with automated tests.

A registered ETW provider is reported separately from a validated capability tier. TCP is measured `TrafficVisualization` on the supported current build; named pipes are a measured `Unsupported` result with their control intact, and RPC remains `ExperimentalEvidence`. Explore currently compiles process and TCP metadata; optional RPC, ALPC and pipe sources are visibly omitted until their capture impact and adapter guarantees are sufficient. Focused transport compiles validated TCP only. A PID selection is an initial-view focus, not a false retention guarantee: because the network provider cannot filter by PID and lifecycle context remains machine-wide, the preview blocks until the operator explicitly accepts broader metadata collection. Content can compile a complete request-only preview with exact scope, budgets, truncation, retention and separate inspection consent, but it remains blocked: no payload source has an approved body contract and payload-specific impact measurement.

## Build and run

```powershell
dotnet restore InterCat.slnx
dotnet build InterCat.slnx --no-restore
dotnet test InterCat.slnx --no-build

# Read-only inventory. Starts no capture, needs no elevation.
dotnet run --project src/InterCat.Cli -- capabilities

# Read-only profile discovery and requested/effective preview. Starts no capture.
dotnet run --project src/InterCat.Cli -- profiles
dotnet run --project src/InterCat.Cli -- profiles explore
dotnet run --project src/InterCat.Cli -- profiles focused-transport --mechanism tcp

# A process focus previews the unavoidable wider provider scope first. Repeat with
# --allow-broader-capture only after reviewing the per-source disclosure.
dotnet run --project src/InterCat.Cli -- profiles focused-transport --mechanism tcp --pid 4242
dotnet run --project src/InterCat.Cli -- profiles focused-transport --mechanism tcp --pid 4242 --allow-broader-capture

# Review a bounded Content request. This intentionally returns a blocked plan and starts nothing.
dotnet run --project src/InterCat.Cli -- profiles content --source etw/manifest/Microsoft-Windows-RPC `
  --mechanism rpc --pid 4242 --channel rpc-interface:12345678-1234-1234-1234-123456789abc `
  --max-record-bytes 4096 --max-session-bytes 67108864 --retention stop-at-limit --inspection disabled

# Measured vertical paths. Each starts one owned ETW session; needs an elevated shell.
dotnet run --project src/InterCat.Cli -- measure tcp
dotnet run --project src/InterCat.Cli -- measure pipe
dotnet run --project src/InterCat.Cli -- measure rpc

# Import a standalone ETL into a session, then open it. Both need no elevation.
dotnet run --project src/InterCat.Cli -- import <source.etl> --into <session directory>
dotnet run --project src/InterCat.Cli -- session <session directory> --rows 10

# Offline re-evaluation into shareable, fixture-scoped evidence.
dotnet run --project src/InterCat.Cli -- verify tcp --run <run directory> --output fixtures/FX-TCP-001/evidence

dotnet run --project src/InterCat.Desktop
```

## Where the evidence lives

| Path | Contents |
|---|---|
| `capabilities/<build>/` | Machine-readable capability reports, per build and adapter |
| `contracts/` | Frozen artifact contracts: the capability report, `journal-v1`, `import-v1`, `store-v1` and `segment-v1` |
| `bench/results/` | Reproducible measurement runs; their counters are committed, their records are not |
| `fixtures/index.json` | Fixture traceability matrix and the contract-coverage ledger |
| `fixtures/FX-TCP-001/evidence/` | Curated truth log, scoped observations and verification result |
| `fixtures/FX-PIPE-001/evidence/` | Curated truth logs and the measured named-pipe result |
| `fixtures/FX-RPC-001/evidence/` | Curated truth log, scoped call records and the measured RPC result |
| `theme/` | Theme tokens and their recorded contrast and separation measurements |
| `docs/reviews/` | Scored interaction reviews with the screens they were taken from |
| `docs/adr/` | Architecture decisions, including the M0 capture-session strategy |
| `docs/IMPLEMENTATION-STATUS.md` | Where implementation stands and what comes next |

The complete product and engineering blueprint is in [the implementation plan](docs/design/INTERCAT-IMPLEMENTATION-PLAN.md).
