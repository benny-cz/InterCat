# ADR-077: COM-Perf records each out-of-process COM call at both ends, and its activations name nothing

- Status: accepted for M6's COM/DCOM/WinRT work package; COM-Perf is inventoried as COM calls' lead (revision 912), a call
  is the RPC call that carries it and its two ends are linked by the client's activity (ADR-079, revision 913), and only
  the opt-in COM calls profile requests it (revision 918)
- Date: 2026-11-04
- Decision owners: InterCat maintainers
- Relates to: plan §4.2, §9.4, M6, P7, P8, R22, ADR-034, ADR-072, FX-COM-001, FX-COM-002, `tools/InterCat.ComProbe`,
  `bench/results/com-provider-inspection-20261104T141555Z`, `bench/results/com-perf-feasibility-*`,
  `bench/results/com-perf-scope-*`, `bench/results/com-perf-impact-*`

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

## Addendum: what collecting COM-Perf costs (revision 910)

`tools/InterCat.ComProbe --measure --impact` ran FX-COM-001's exchange unpaced - 16,384 calls, about 2.5 seconds - in
alternating pairs, once with no session and once while a session of its own collected COM-Perf machine-wide at verbose
with every keyword, a consumer only counting the records, as AFD's cost was measured (ADR-072's addendum). Two series
of nine pairs, about 65,000 records a second, nothing lost:

| Series | Median CPU impact | Workload slower by | Throughput regression |
|---|---|---|---|
| `bench/results/com-perf-impact-20261104T160252Z` | 0.6 pp: Low | 18.1% | 15.3% |
| `bench/results/com-perf-impact-20261104T160518Z` | 0.3 pp: Low | 17.3% | 14.8% |

Seventeen of the eighteen pairs ran slower with the session, about 27 microseconds a call: a call raises four records,
each written in the thread that makes or serves it, so the time is the call's own. §12 targets under 5 percentage
points of machine processor time and under 5% workload-throughput regression. COM-Perf meets the first and misses the
second on a COM-heavy workload, by an amount in proportion to a process's COM call rate. Its class is therefore Low,
stated beside the regression it measured. A profile that admits it is opt-in and says what it costs, and Explore never
admits it. What remains before a profile admits COM-Perf is a mechanism for COM calls. A test holds both series to
their class and regression; two alterations of them are caught.

## Addendum: COM-Perf inventoried as COM calls' lead (revision 912)

The first decision held COM-Perf out of the catalog until a mechanism for COM calls existed, and ADR-078 gave it one,
`ComCall`. COM-Perf now enters the source catalog as its lead, as Winsock AFD entered as AF_UNIX's (ADR-072):

- **Inventoried, experimental, requested by no profile, admitting no event.** A COM call's records need a binding of
  their own by the process that raised them, a field for the process at the other end, and their layering over the RPC
  call that carries them settled before any is admitted.
- **Its measured notes are what `icat capabilities` states of it:** every out-of-process call at both ends, paired by
  causality and rank; a call across an apartment naming its own process; activations naming nothing; no rundown; and
  a process filter that holds, so it is a source a capture can scope.
- **Its cost is Low, stated beside its regression.** The second addendum's series is its evidence, and its notes say
  that a COM-heavy workload ran 17 to 18% slower, so only an opt-in profile stating it may admit it, never Explore.

No build carries a tier for COM calls: no fixture has measured them through the product. A test holds the lead, its
absence from every profile and the absence of a tier; six mutations of the catalog entry are caught.

## Addendum: what carries a call, and what links its ends (revision 913)

The third addendum held COM-Perf's events out until their layering over the RPC call that carries them was settled.
ADR-079 measured FX-COM-001's exchange with Microsoft-Windows-RPC collected beside COM-Perf, in this record's four shapes:
each COM call is carried by one RPC call at each end, on its interface with its method as the procedure, and the
server's COM call carries the client's activity id. So a COM call and its RPC call are one call, COM-Perf annotating
it (ADR-079's first decision). The second decision's pairing by causality and rank agreed with the activity on every
invoke; the activity is now the link, this record's checks its checks (ADR-079's fourth decision).

## Addendum: a COM call's records bind to the process that raised them (revision 914)

ADR-079's fifth decision names first, of what remains before a profile admits COM-Perf, its records' binding. Their
payload names no owner: a start names the process at the call's other end, never its own, so that field is never an
owner. FX-COM-001 measured where each is raised: in four runs every call's client start and stop were raised in the
calling process and its served start and stop in the serving one, and under a session's process filter the two
processes' every call arrived and none of a decoy pair's; FX-COM-002's calls across an apartment were raised in their
own process at both ends. So ADR-030's header binding reaches COM calls: a COM call record whose payload names no owner
belongs to the process its header names. An activation is not a call, carries no field and stays unbound.

That changes what binds, so it is a new rule identity, `process-binding-v5`, as `contracts/entities-v1.md` §7 requires.
A checkpoint derived under version 2, 3 or 4 is read as version 5's wherever every record named its owner, which its
counts show, since each earlier rule differs only in binding fewer records that name none; one counting any record
without an owner is derived again, its refusal naming the rule it was derived under. No COM call is recorded yet:
COM-Perf admits no event until a field for the other end's process exists. Tests pin the mechanisms that bind by
their header, bind a session's COM call records to their caller and their server, and read a version 4 checkpoint;
six mutations are caught.
