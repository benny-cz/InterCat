# ADR-077: COM-Perf records each out-of-process COM call at both ends, and its activations name nothing

- Status: accepted for M6's COM/DCOM/WinRT work package; no source is inventoried for it yet, and no profile requests it
- Date: 2026-11-04
- Decision owners: InterCat maintainers
- Relates to: plan §4.2, §9.4, M6, P7, P8, R22, ADR-034, ADR-072, FX-COM-001, FX-COM-002, `tools/InterCat.ComProbe`,
  `bench/results/com-provider-inspection-20261104T141555Z`, `bench/results/com-perf-feasibility-*`,
  `bench/results/com-perf-scope-*`

## Context

The plan expects COM, DCOM and WinRT to give "higher-level annotation and out-of-process relations when supported",
from RPC plus validated component-specific providers. It warns that in-process COM is not IPC and that activation alone
is not proof of subsequent calls, and M6's exit says a COM activation event cannot become a call count. §4.2's
inspection (revision 907) found that `Microsoft-Windows-COM-Perf` declares client and server call events, each start with
the call's interface IID and method, a causality GUID, a call trace number and the other end's process and thread, and
activation events carrying no field. A schema is no evidence of emission.

## Measurement

FX-COM-001's workload, `InterCat.TestWorkloads com-loopback`, has a server process put an automation object in the
running object table under an item moniker of its own, and a client process take it from there and call it through
`IDispatch`. Each process logs every call it issued or served, and nothing is registered with the machine.
`tools/InterCat.ComProbe --measure` ran it under a real-time session of its own on build `10.0.26220.0-x64`, enabling
COM-Perf machine-wide at verbose with every keyword, and read each record by the provider's registered layout. Four runs:
64 calls paced, 256 and 1,024 unpaced, and four bursts of 128.

| Quantity | Result |
|---|---|
| Truth calls with an `IDispatch::Invoke` start at the client naming the server, and one at the server naming the client | every one, each run |
| A call's start joined to its stop by its trace number, with its HRESULT | every one, at both ends |
| A client invoke paired with the server invoke of its causality and rank, the server's start inside the client's call | every one |
| Causalities a run | one or two: a causality names a chain of calls, not a call |
| Threads | the client's target thread is none of the server's serving threads; the server names no client thread |
| Calls beyond the truth's, recorded alike | each late-bound call's `GetIDsOfNames`, and `IRundown`'s remote QueryInterface - 64 answered `E_NOINTERFACE` - AddRef and Release |
| Activations | starts and stops carrying no field: nothing names what was activated |
| Lost | nothing, in any run; 468 to 9,016 records a run were other processes' |

1. **COM-Perf records an out-of-process call at both ends.** Each call of the workload was a client start and stop and a
   server start and stop, the client's naming the server's process and the server's the client's, with the call's
   interface and method and, at its stop, its HRESULT.
2. **A call's ends pair by causality and rank.** A causality is shared by a chain of calls, and threads name nothing the
   other end recorded. Taken in order within its causality, each client invoke had the server invoke of its rank, whose
   start fell inside the client's call.
3. **Activations name nothing.** COM-Perf's activation events carry no field, so they say that an activation ran in a
   process, never what was activated or which calls followed it.
4. **A real-time session is drained before its source is closed.** An earlier version of the probe disposed its session
   before its reader had taken the buffers the stop delivered. In 6 of 17 unpaced runs every record after a point was
   missing, and ETW reported nothing lost - no event and no real-time buffer. Drained first, 10 of 10 held every call, as
   did 6 of 6 recorded to a file. The product's capture already stops its session by name, reading the final losses, and
   waits for its pump to take what the stop delivered before it closes the source.

## Decision

1. COM-Perf is the lead for COM's out-of-process calls. It is not inventoried yet: EN-Mechanism names COM only as
   `ComActivation`, which a call is not, so admitting its calls needs a mechanism for them first, a contract change of its
   own.
2. A call's two ends are joined by their processes, causality, interface, method and rank, the server's start inside the
   client's call: never by time proximity alone (P8), by causality alone, or by thread. A peer is the process a call's
   own record names, checked against truth (P7).
3. COM-Perf's activations carry no field, so none becomes a call count or names a class.
4. Before any profile admits COM-Perf, the work package needs: a mechanism for COM calls, its collection's cost
   machine-wide, whether a process filter holds for it, and what a call within one process - not IPC - records.

## Consequences

- FX-COM-001 is indexed for its evidence, with no tier. Its committed ledger holds each of the paced run's workload records
  by role, never a PID, a thread, a pointer or an IPID.
- Tests hold the ledger to the truth: each truth call one invoke at each end, paired as measured, each start joined to its
  stop; every call naming the other workload process; and activations naming nothing. Three alterations of the ledger are
  caught.

## Addendum: what scopes COM-Perf's records (revision 909)

The fourth decision asked whether a process filter holds for COM-Perf and what a call within one process records.
`tools/InterCat.ComProbe --measure --filter` and `--in-process` measured both, each in two runs, 64 calls paced and
256 unpaced, nothing lost.

- **A process filter holds.** FX-COM-001's exchange ran with its two processes in the session's process filter, beside
  a decoy pair making as many calls outside it, every process waiting to initialize COM until the filter was in place.
  The filtered pair's every call was recorded at both ends, and not one record of the decoy pair or of any other
  process arrived. COM-Perf is raised in the process that makes or serves a call, where AFD's records are raised in the
  kernel (ADR-072), so a capture scoped to named processes collects their calls alone.
- **A call within one process is recorded as an out-of-process one.** FX-COM-002 has one process call the system's
  apartment-threaded in-memory dictionary from its multithreaded apartment, so COM hosts the object in a single-threaded
  apartment of its own and reaches it through a proxy. Every call was a client start and a server start, both raised in
  the process and both naming that same process as the other end, with the host apartment's activation and `IRundown`'s
  calls recorded alike. Nothing in a record says a call stayed in its process except that the process it names is its
  own.

A fifth decision follows: **a COM call is between processes only where the process its record names is another.** One
naming its own process is that process's own work across an apartment, never IPC and never a relation, as the plan's
"in-process COM is not IPC" asks. What remains before a profile admits COM-Perf is a mechanism for COM calls and its
collection's cost. Tests hold FX-COM-001's filter result and FX-COM-002's ledger to their truth; four alterations of
them are caught.
