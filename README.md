# InterCat

InterCat is a Windows IPC visualizer for exploring which processes communicate, through which mechanism, and with what observable evidence. The project is in milestone M0: capability claims are intentionally gated on measured fixtures.

## Current vertical slice

- a pinned .NET 10 solution with enforced inward dependency boundaries;
- a schema-driven capability inventory that keeps registration, enablement, observed health and validated semantics as four separate checks;
- an owned ETW session with a unique name, an ownership token, bounded admission and an independent health ledger;
- seeded two-process TCP and named-pipe workloads with independent truth logs, and measured coverage results whose tiers are computed from the plan's §14.2 thresholds;
- a synthetic Avalonia graph/timeline prototype with linked selection;
- pure viewport, tier and semantic-domain contracts with automated tests.

A registered ETW provider is reported separately from a validated capability tier. The first measured TCP run met every §14.2 threshold but was taken on a build outside the supported matrix, so TCP is reported as `ExperimentalEvidence`, not as supported traffic visualization. The named-pipe measurement came back negative with its control intact, so named pipes are reported as `Unsupported` and the scope decision is recorded in ADR-003.

## Build and run

```powershell
dotnet restore InterCat.slnx
dotnet build InterCat.slnx --no-restore
dotnet test InterCat.slnx --no-build

# Read-only inventory. Starts no capture, needs no elevation.
dotnet run --project src/InterCat.Cli -- capabilities

# Measured vertical paths. Each starts one owned ETW session; needs an elevated shell.
dotnet run --project src/InterCat.Cli -- measure tcp
dotnet run --project src/InterCat.Cli -- measure pipe

# Offline re-evaluation into shareable, fixture-scoped evidence.
dotnet run --project src/InterCat.Cli -- verify tcp --run <run directory> --output fixtures/FX-TCP-001/evidence

dotnet run --project src/InterCat.Desktop
```

## Where the evidence lives

| Path | Contents |
|---|---|
| `capabilities/<build>/` | Machine-readable capability reports, per build and adapter |
| `contracts/` | Frozen artifact contracts, starting with the capability report |
| `fixtures/index.json` | Fixture traceability matrix and the contract-coverage ledger |
| `fixtures/FX-TCP-001/evidence/` | Curated truth log, scoped observations and verification result |
| `fixtures/FX-PIPE-001/evidence/` | Curated truth logs and the measured named-pipe result |
| `docs/adr/` | Architecture decisions, including the M0 capture-session strategy |
| `docs/IMPLEMENTATION-STATUS.md` | Where implementation stands and what comes next |

The complete product and engineering blueprint is in [the implementation plan](docs/design/INTERCAT-IMPLEMENTATION-PLAN.md).
