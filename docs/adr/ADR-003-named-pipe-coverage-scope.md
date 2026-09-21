# ADR-003: Named-pipe coverage scope after the driverless feasibility measurement

- Status: accepted for M0; revisit when a supported build or an optional collector is measured
- Date: 2026-09-21
- Decision owners: InterCat maintainers
- Required by: plan §14.2 ("If driverless feasibility leaves named pipes … below `Topology only`, that is
  an explicit release-scope decision recorded in an ADR, never a silent restatement of the original goal
  as achieved") and the M0 fallback clause of §14

## Context

§4.1 calls NPFS coverage a hard feasibility gate, and M0 requires validating "named-pipe NPFS activity and
byte completion semantics specifically". `Microsoft-Windows-Kernel-File` was the driverless candidate: it
is registered, its schema is readable, and its `Read`/`Write` descriptors carry a requested `IOSize` while
`OperationEnd` carries the completed size, which together would satisfy §21.1's requested-versus-completed
scenario.

## Measurement

Fixture `FX-PIPE-001` ran a seeded two-process named-pipe exchange in message mode, including one
deliberately short read, under an owned session enabling `Microsoft-Windows-Kernel-File` at keywords
`0x3F0` (FILENAME, FILEIO, OP_END, CREATE, READ, WRITE) with an event-id filter for descriptors 10, 12,
14, 15, 16 and 24, on build `10.0.26220.0-x64`.

| Quantity | Result |
|---|---|
| Truth operations declared by the workload | 25 |
| Truth operations with an admitted observation | 0 |
| Pipe creates, reads or writes observed for the fixture's pipe | 0 |
| Operations from the fixture's processes with an unresolved resource | 0 |
| **Control**: admitted records from the fixture's own processes in the same capture | **2,472** |
| Session health | 0 provider-reported loss, 0 buffer loss, 0 application drops, 0 undecodable |

The control is what makes the negative result trustworthy: during the same session the same two processes
produced 420 file creates, 384 closes and 1,660 completions for ordinary files, so the source was live for
them. Their named-pipe operations simply produced no record at all — not an unnamed record, not a record
with a missing field.

## Decision

1. **Named pipes are `Unsupported` on this evidence**, computed from §14.2 thresholds rather than argued.
   The capability report states that tier with the measurement that produced it.
2. **No inference replaces the missing evidence.** InterCat will not present a pipe relationship derived
   from a parent-child link, an equal pipe name, a handle enumeration or a connection to an unknown
   application pipe (P6, P7, P20). A pipe remains an explicit coverage gap in the UI and in exports.
3. **The gap is routed, not dropped.** Named-pipe visibility moves to the post-v1 work that can carry it:
   M7 (cooperative instrumentation) and M9 (optional supported kernel collectors). An import path for
   existing external pipe evidence stays possible but is not part of the driverless baseline.
4. **The candidate stays in the catalog** with its schema, its keywords and this measured result, so a
   re-measurement on another build is a single command rather than a rediscovery.

## What would reverse this

- The same fixture producing pipe operations on a §1.3 supported build, which would move the mechanism to
  at least `Experimental evidence`.
- A different driverless source proving NPFS creates and transfers with their completion semantics.
- An optional collector from M9, which is an explicit installation choice rather than part of the
  driverless baseline.

## Consequences

- Release material lists named pipes with tier `Unsupported` and the build it was measured on (§14.2). No
  marketing statement may imply pipe traffic visualization.
- `Microsoft-Windows-Kernel-File` stays out of the Explore profile: it is whole-machine expensive and, on
  this evidence, contributes nothing to pipe coverage.
- The requested-versus-completed byte contract of §21.1 is implemented and unit-tested in the pipe
  evaluator, so it is ready the moment a source supplies those records.
