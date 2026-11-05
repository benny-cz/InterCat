# ADR-079: A COM call is the RPC call that carries it, and the client's activity links its two ends

- Status: accepted for M6's COM/DCOM/WinRT work package; since revision 916 `com-call-link-v1` implements its second and
  third decisions (`contracts/com-calls-v1.md`), and since revision 917 `rpc-call-peer-v2` serves a call through it,
  and since revision 918 the opt-in COM calls profile collects COM-Perf beside RPC, and since revision 919 COM calls
  measured through it carry ExperimentalEvidence (ADR-080), and since revision 923 a COM-Perf whose schema lacks a field
  the rule decides by is refused (its fifth addendum), and since revision 926 a nested call is measured a call of its own
  (its sixth addendum); since
  revision 914 a COM call's records bind to the process that raised them (ADR-077's fifth addendum), and since revision
  915 COM-Perf's call events are admitted, the process at a call's other end a source field of its own (its addendum)
- Date: 2026-11-04
- Decision owners: InterCat maintainers
- Relates to: plan §4.2, §5.1, §7.4, M6, P4, P7, P8, ADR-030, ADR-031, ADR-034, ADR-077, ADR-078, FX-COM-001,
  `tools/InterCat.ComProbe --measure --layering`, `bench/results/com-perf-layering-*`

## Context

ADR-077's third addendum held COM-Perf's call events out until their layering over the RPC call that carries them was
settled, and ADR-078's fourth decision called a COM call carried by an RPC call that the RPC source records one exchange
(P4), to be measured before a profile admits both. Explore collects Microsoft-Windows-RPC, and ADR-034's real capture
found that the machine's client calls whose several ALPC sends left them unlinked were mostly COM calls: the RPC source
records COM's calls. Admitted beside it, COM-Perf's records would describe each such call a second time unless the two are
known to be one. ADR-031 pairs an RPC call's start with its stop by activity id on one side, and ADR-034 links a client's
RPC call to the server's through ALPC. Neither says which RPC call carries a COM call, or how a COM call's two ends join.

## Measurement

`tools/InterCat.ComProbe --measure --layering` ran FX-COM-001's exchange under one real-time session collecting COM-Perf
as ADR-077 measured it, beside Microsoft-Windows-RPC requested as the product's profiles request it: informational, with no
keyword mask. It used the four shapes ADR-077 measured: 64 calls paced, 256 and 1,024 unpaced, and four bursts of 128.
Nothing was lost. A COM call is a start joined to its stop by its trace number, and an RPC call a start joined to its stop
by activity id (ADR-031).

| Quantity | Every run |
|---|---|
| COM calls at each end | 195, 771, 3,075 and 1,539 |
| At the client: the RPC client call sharing a COM call's activity id | exactly one for every COM call, on its thread, on its interface with its method as the procedure, over local RPC; its start inside the COM call and its stop 0.6 to 14.5 µs after the COM call's, the two stops sharing the id |
| At the server: the RPC server call around a COM call on its thread | exactly one for every COM call, on its interface and procedure, over local RPC, with an activity id of its own |
| RPC calls carrying no COM call | at the client 16 a run, every one before its first COM call; at the server none |
| Across: the server COM call carrying a client COM call's activity id | exactly one for every client call, naming the client's process as the client names the server's, on one interface and method, inside the client's call; no server call of another id, and none carrying one twice |
| ADR-077's invoke pairs by causality and rank | 64, 256, 1,024 and 512, every one the pair the activity links |
| Client RPC calls so linked to the RPC call that served them | every one carrying a COM call, on one interface and procedure, no server call reached twice |
| Status | the 64 calls of a paced run that COM answered `E_NOINTERFACE` stopped with RPC status 0 |

1. **Each COM call is carried by one RPC call at each end.** At the client the COM channel's start precedes the runtime's
   and its stop the runtime's by a few microseconds. The RPC call is on the COM call's thread, its interface is the COM
   interface and its procedure the COM method, and the two share one activity id at their starts and their stops.
   Containment in time would join none of them, since the runtime's call ends after the channel's. At the server the
   runtime's call holds the COM call on the thread that received it.
2. **The client's activity travels with the call.** The server's COM records carry the activity id of the client's call;
   the server's RPC records carry one of their own. ADR-031's finding that no RPC client call shares an id with the
   server's call still holds: COM-Perf's server record is what carries the client's id.
3. **A COM call's result is not its RPC call's.** A COM call's HRESULT is the method's answer, and its RPC call's status is
   the runtime's: a call refused `E_NOINTERFACE` completed as an RPC call.

## Decision

1. **One call, annotated.** Where both are captured, a COM call and the RPC call that carries it are one call. The RPC
   call stays the operation ADR-031 derives. COM-Perf's call records annotate it with the process at its other end, its
   HRESULT and its causality, and never become an operation, a call count or a relation of their own (P4, §5.1).
2. **The carrier.** At the client, a COM call's carrier is the one RPC client call of its process sharing its start's
   activity id. At the server, it is the one RPC server call of its process on its thread whose span holds it. Either
   must carry the COM call's interface, with its method as its procedure; a disagreement is conflicting evidence, not a
   carrier. A COM call without exactly one carrier annotates nothing and is stated with its reason. The server's join was
   measured for a server in the multithreaded apartment, which serves a call on the thread that received it. A call that
   a single-threaded apartment dispatches on another thread is unmeasured, and the rule finds it no carrier rather than a
   guessed one.
3. **Its two ends, linked by the client's activity.** A client's COM call and the server's COM call carrying its activity
   id are one call's two ends when these all hold:
   - exactly one server call carries the id;
   - each names the other's process;
   - they name one interface and method;
   - the server's call lies inside the client's.

   Through their carriers, that relates the client's RPC call to the RPC call that served it. It covers the COM calls
   whose several ALPC sends leave ADR-034's chain unresolved. An id shared in any other shape links nothing (P7). A link
   says that records belong to one call, never that they happened at once (P8). RPC's own records still pair no client
   with a server (ADR-031's fifth decision): the link rests on COM-Perf's server record, so it holds only where COM-Perf
   is captured.
4. **The activity, not causality and rank.** ADR-077's pairing by causality and rank agreed with the activity on every
   invoke of every run. The activity is one call's id, where a causality names a chain whose ranks shift at the first
   record missing, so the activity is the link, and ADR-077's checks are its checks.
5. **What remains before a profile admits COM-Perf:**
   - its records' binding by the process that raised them, a new binding rule identity;
   - a field for the process at a call's other end;
   - a relation rule implementing the third decision, with its identity;
   - an opt-in profile collecting RPC beside it, so every COM call can have its carrier, and stating its cost.

## Consequences

- FX-COM-001 holds the layering's paced run under `evidence/layering`: its ledger, with each record's thread and activity
  as ordinals of the run, never an id, and its truth beside it. Its measurement states all four runs, each with its report.
- Tests hold the ledger and the measurement:
  - each COM call's one carrier at each end, on its interface and method;
  - the client's RPC call stopping after its COM call;
  - the client's activity carried by one server call, linking the two RPC calls;
  - the HRESULT beside RPC's status.

  Eleven alterations of the evidence and of COM-Perf's catalog notes are caught.
- COM-Perf's catalog notes, which `icat capabilities` states, say what carries a call and what links its two ends.

## Alternatives considered

- **Count COM-Perf's calls as operations of their own.** Rejected: each is the RPC call it rides, counted twice (P4).
- **Make the COM call the operation, with its RPC call beneath it.** Rejected. RPC's calls are operations in every
  capture that collects RPC. One exchange would then be an RPC call in one capture and a COM call in another, its identity
  and counts changing with the profile that recorded it. An annotation adds what COM-Perf knows without changing what RPC
  counts.
- **Join at the client by containment in time.** Rejected: the runtime's call stops after the channel's on every call, so
  containment joins none, and time is never a join (P8).
- **Join at the server by activity id.** Not possible: the server's RPC call has an id of its own.
- **Keep ADR-077's causality and rank.** Rejected as the link: it agreed on every invoke, but a rank is a position in a
  chain, which one lost record shifts for every later call.

## Addendum: COM-Perf's call events admitted (revision 915)

The fifth decision listed a field for the process at a call's other end among what remains before a profile admits
COM-Perf. Its four synchronous call events are now admitted, compiled from the provider's registered layout: a start's
trace number, the process at its other end, its method and its interface, and a stop's trace number and HRESULT. Three
source fields are new:

- `ComCallTraceId` (19) joins a call's start to its stop in the process that raised them, never one process's records
  to another's.
- `PeerProcessId` (20) is the process a record names at its other end - the client's target, the server's source - and
  never the record's owner: a peer only where the third decision's link checks it.
- `ComMethodNumber` (21) is a method's number within its interface, which the RPC call carrying it names as its
  procedure.

The interface is a start's one identifier, as an RPC call's is. Nothing else is admitted: not the threads, which name
nothing the other end recorded; not the causality, since a descriptor keeps one identifier and the activity, not the
causality, links a call's ends; and none of the pointers, IPIDs, OIDs and addresses after the interface. The
asynchronous calls, which no fixture made, are not admitted. An admitted record is a COM call row whose payload names no
owner, so the process that raised it owns it (`process-binding-v5`), and its activity is its header's.

No profile requests COM-Perf yet: what remains is a relation rule implementing the third decision and an opt-in profile
collecting RPC beside it. Tests compile the four events to their slots and normalize a client's start and stop, and
the plan's EN-SourceField list is now held to the domain's enumeration (R5); eleven mutations are caught.

## Addendum: `com-call-link-v1` (revision 916)

The fifth decision's relation rule has its first half. `com-call-link-v1` (`contracts/com-calls-v1.md`, `ComCallIndex`)
reads a generation's COM call records and the RPC calls paired from the same segments:

- **It pairs** a COM call's start with its stop by process, side and trace number, as an RPC call's are by activity id: a
  stop without its start, a start without its stop, a record without a number and a number used again before its stop
  pair nothing, each stated.
- **It carries** each call as the second decision measured: at the client the one RPC client call of its activity, at the
  server the one RPC server call around it on its thread, either on its interface with its method as the procedure.
- **It links** each client call to the one server call carrying its activity, as the third decision does. A server in
  the client's own process is never linked, and every other shape is stated with its reason. A linked call carried at
  both ends relates the client's RPC call to the RPC call that served it; a server RPC call two relations reach is
  related to neither.

FX-COM-001's layering ledger, made a session of RPC and COM call rows as the capture admits them, derives under it what
was measured: 195 calls at each end, every one completed, carried and linked, and 195 client RPC calls related to the
RPC calls that served them, none reached twice. No reader uses the links yet: the RPC peer rule is to resolve a COM
call's RPC call by them, and an opt-in profile is to collect COM-Perf beside RPC. Tests derive the rule over synthetic
calls and over the ledger; eleven mutations are caught.

## Addendum: `rpc-call-peer-v2` (revision 917)

The fifth decision's relation rule has its second half. `rpc-call-peer-v2` (`contracts/operations-v1.md` §5c) serves a
completed client RPC call carrying a COM call that `com-call-link-v1` links by the RPC call carrying the server's COM
call, with or without ALPC. Where ALPC's chain reaches the same server call the call is ALPC's; where it reaches
another, the two disagree and the call is conflicting, linked to neither, as the third decision's checks require. A
call linked through the COM call it carries says so, at both ends.

Its consequences reach every reader of RPC calls' other ends:

- **A new rule identity.** An operation index of an earlier peer rule is refused and derived again. Its minor 2 says
  whether the generation holds COM call records and marks each call linked through one, so a generation with COM links
  and no ALPC keeps them. A generation holding a COM call record follows its other ends again from every segment, as
  `com-call-link-v1` derives, never extended.
- **Readers follow COM as they follow ALPC.** A metric, a channel's other ends and a call's row read the links where
  the generation holds either; the texts naming the rule name both ways.

FX-COM-001's ledger, made a session, has every one of its 195 client RPC calls carrying a COM call served through
COM, the client's 18 other calls unresolved for want of ALPC. Tests serve a call through COM with no ALPC record, keep
it ALPC's where ALPC agrees, conflict where ALPC reaches another call, decline to extend over a COM call record, and
read the links back from an operation index; eleven mutations are caught.

## Addendum: the COM calls profile (revision 918)

The fifth decision's last requirement is met: an opt-in profile collecting COM-Perf beside RPC and stating its cost.
`com-calls` requires Kernel-Process, Microsoft-Windows-RPC and COM-Perf, so without either call source it does not
start, rather than record COM calls whose RPC calls it cannot serve or RPC calls whose COM calls it cannot follow. It
takes no focus, as Explore takes none, and `icat capture` and `icat record` take it as they take the other opt-in
profiles.

Its class is its sources' at most, Moderate for Kernel-Process. Its statement says, beside it, what ADR-077's second
addendum measured: COM-Perf's collection alone took a median 0.3 to 0.6 CPU points but a COM-heavy workload ran 17
to 18% slower, past §12's throughput target, in proportion to its COM call rate. That is why it is opt-in and Explore
never admits the source. Nothing has measured the profile whole yet, and no build carries a tier for COM calls: a
fixture measured through the product is next. Tests compile the profile with and without each call source and check
both commands' profile lists, help and preview.

## Addendum: a COM-Perf whose schema lacks a field the link decides by is refused (revision 923)

M6's exit asks each mechanism for a schema-failure case. The admission plan compiler leaves out a field the machine's
schema does not admit, and admits the event without it. For COM-Perf that would have had `com-call-link-v1` state what
was never observed:

- without a stop's trace number, or with no client call stop registered at all, no call's start would pair with its
  stop, and each would read as open at capture end or as a call of its own;
- without a start's other end, a link would read as `PeerMismatch`, two ends that disagree, where nothing was seen to
  disagree;
- without a start's method or interface, no call would be carried or linked, each stated `CannotCheck`.

A field intent now says what a derivation rule decides by it (`AdmittedFieldIntent.Decides`). COM-Perf's starts mark
their trace number, other end, method and interface, and its stops their trace number. A source whose compiled plan has
a descriptor without such a field, or no compiled version of its event, is refused as its plan compiles, naming the
descriptor, the field and what the rule decides by it, as Winsock AFD is for a field its gate decides by (ADR-074's
addendum). The COM calls profile then does not start, `icat record` says why, and `icat capabilities` states the
source's schema unknown. A call's HRESULT, which no rule decides by, may be absent: the call's answer is then unknown.
The check runs as the plan compiles, never on the callback path, and the link rule is unchanged. Tests take each such
field out of COM-Perf's registered layout in turn, and its client call stop, and hold the plan, the capture's
compilation, the profile and the capability report to the refusal.

## Addendum: a nested call is a call of its own (revision 926)

The link's assumption left a nested call unmeasured: a call a server makes while serving another, on the thread serving
it. Had it shared the served call's activity id, one activity would have been carried by two server calls, and
`com-call-link-v1` would have stated both client calls `SeveralServedCalls` rather than link either. FX-COM-004 measures
it: the server, while serving each of the client's calls, calls a second, forwarded server's object before it answers.

Three runs through `icat record --profile com-calls`, machine-wide and loss-free, 64 calls paced twice and 256 unpaced:

- every client call was linked to the server's call of the same truth call;
- every nested call was linked to the forwarded server's call of its own, inside the call the server served it from;
- each was carried by one RPC call served through it, 768 of 768, none attributed to another process;
- every COM call of the three processes was carried and linked.

COM gives a nested call an activity of its own, so no served call or activity holds two calls, and nesting makes nothing
ambiguous. The rule is unchanged, and its assumption is now this measurement (`contracts/com-calls-v1.md` §6). The
first paced run is committed under `fixtures/FX-COM-004/evidence/`, and a test reproduces it. COM calls' ambiguity case,
which M6's exit asks for, remains: nesting is not one.
