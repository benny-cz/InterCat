# Per-build coverage

Generated from `fixtures/index.json` - each fixture's mechanism and each environment entry's measured tier - and
checked against it by `MeasuredCoverageTests`; edit the index, not this page. For each build, a mechanism's tier is
its latest fixture evidence there. A mechanism no fixture measured on a build has no tier on it: `icat capabilities`
says so, and never borrows another build's tier (P27).

## 10.0.26220.0-x64

| Mechanism | Tier | Fixture | Measured | Build support |
|---|---|---|---|---|
| Http | ExperimentalEvidence | FX-HTTP-003 | 2026-09-28 | Primary |
| NamedPipe | Unsupported | FX-PIPE-001 | 2026-09-21 | Untested at the time of the run; ADR-007 later added this build |
| Rpc | ExperimentalEvidence | FX-RPC-001 | 2026-09-27 | Primary |
| Tcp | TrafficVisualization | FX-TCP-002 | 2026-09-27 | Primary |
| Udp | TrafficVisualization | FX-UDP-002 | 2026-09-27 | Primary |

## 10.0.26220.9223-x64

| Mechanism | Tier | Fixture | Measured | Build support |
|---|---|---|---|---|
| NamedPipe | Unsupported | FX-PIPE-001 | 2026-09-21 | Primary |
| Rpc | ExperimentalEvidence | FX-RPC-001 | 2026-09-21 | Primary |
| Tcp | TrafficVisualization | FX-TCP-001 | 2026-09-21 | Primary |
