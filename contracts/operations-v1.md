# InterCat operations v1

Status: **implemented** as `rpc-call-operation-v1`, for RPC calls (revision 178), and counted on the logical-operations
metric basis since revision 183 (`contracts/metrics-v1.md` §8a). A client call's other end is `rpc-call-peer-v1`'s
since revision 225 (§5c). No other mechanism derives an operation at this version (§8).

This contract fixes how §7.1's `Operation` - a derived logical call with an optional start, end and status and the
observations it is made of - is derived from a generation's published segments. It is §7.4's RPC correlator, with the
join key, lifecycle scope, timeout, cardinality, ambiguity policy and evidence requirements §7.4 requires every
correlator to state before it is implemented. It owns no bytes: a derivation is computed from the observation and
source-field segments a generation names and from the process instances `contracts/entities-v1.md` derives over
them, and it changes none of them (R1, R20). ADR-031 records the decisions, and FX-RPC-001 the measurement they rest
on.

## 1. Scope

A derivation reads every segment of one generation, of **one capture on one clock**, as the process derivation it
rests on does. The process instances are `process-binding-v4`'s over the same segments.

## 2. Call records

A **call record** is a row whose mechanism is `Rpc`, whose kind is `RequestStart` or `RequestEnd`, and whose direction
names its side:

| Direction | Side | Descriptors (Microsoft-Windows-RPC) |
|---|---|---|
| `Outbound` | client | 5 call start, 7 call stop |
| `Inbound` | server | 6 call start, 8 call stop |

Its process is the PID it belongs to under `process-binding-v4`, which for RPC is the process that raised it
(ADR-030). A start carries the interface (`SourceIdentifier`) and, as `source-fields-v1` fields, the procedure number
and protocol sequence. A stop carries the status (`StatusCode`). Any other RPC row is not a call record, and a
derivation counts it as such.

## 3. Pairing

- **Join key:** the record's PID, its side and its activity id. A record with no activity id has no key.
- **Order:** a key's records are read in canonical order: native reading, then raw locator, then fact key. Every call
  record of the generation is read before any is paired, so the segments' cut and delivery order change nothing (I14).
- **Walk:** a start opens a call and the next stop closes it. A stop with no open call is a call whose start is not in
  the evidence. A start while its key has a call open means the id was reused before its stop: every record of the key
  from the earlier open start until as many stops as starts have been read is ambiguous and pairs with nothing. A start
  still open when the key's records end is open at capture end.
- **No timeout:** nothing is paired by time proximity (P8) and nothing expires. A missing stop is not a failure.
- **Sides never meet:** a client call and the server call that served it carry different activity ids (FX-RPC-001), so
  no call is paired across sides by its activity id. A call's other end is §5c's, found through ALPC or left unresolved
  with its reason (P7).

## 4. A call

| Field | Value |
|---|---|
| Identity | The observation identity of its first record: its start's, or its stop's when it has no start. |
| Side, PID | Its records'. |
| Process | Its first record's binding (`contracts/entities-v1.md` §4), by that record's reading. |
| Activity | The key's activity id; none for a record that carried none. |
| Interface, procedure, protocol | Its start's; none without a start or where the start carried none. |
| Start, end | Each record's observation identity, native reading and session time, when the call has it. |
| Status | Its stop's status, when the stop carried one. 0 is success; any other value is the call's failure code. |
| Duration | Session nanoseconds from its start's reading to its stop's, on the one clock; only for a completed call. |

Every call is in exactly one state:

| State | Start | Stop | Meaning |
|---|---|---|---|
| `Completed` | yes | yes | paired by activity id |
| `OpenAtCaptureEnd` | yes | no | no stop by the end of the derived evidence: censored, not failed |
| `StartNotObserved` | no | yes | its start began before the capture or was not delivered |
| `NoActivityId` | one of them | no | the record carried no activity id, so it pairs with nothing |
| `Ambiguous` | one of them | no | its key's id was reused before its stop |

A call is **started** when it has a start, and **completed** when it has both. A **failed** call is a completed call
whose status is not 0. Calls with an unpaired record are counted by their reason, and none is counted twice: every call
record belongs to exactly one call.

## 5. Groups

A derivation's calls are grouped by process binding, side and interface. A call binds to an instance with the strength
its binding gives, and an evidence policy admits it or states it as not admitted (`contracts/entities-v1.md` §5). A
group states its started, completed, failed and open calls, the calls it could not pair by reason, and the
distribution of its completed calls' durations: count, minimum, median, 95th percentile and maximum. A percentile is a
duration some call of the group took, never an interpolation.

## 5a. Reading calls (revision 179)

A group of calls bound to an instance is an **RPC channel**: that instance's calls on one side to one interface. It is
named by the instance, the side and the interface, never by a position, so its name reads the same channel in every
generation that still holds it, and one call on it is named by its first record's raw locator and fact key. A reader
lists an instance's channels, a channel's calls a page at a time in the canonical order of their first records (§3),
and, as an evidence scope, the
records of a channel's calls or of one call: each call's start, then its stop. A channel or call a generation no longer
holds is stated as such, never read as an empty one. A channel belongs to one process, so a process's channels add
up to its calls; unlike a paired channel, it is not listed for any other process.

## 5b. Counting calls (revision 183)

`contracts/metrics-v1.md` §8a counts these calls: a started count takes a call by its start, and a completed or failed
count by its stop when the stop is paired with its start, each where that record's reading falls. Its completed and
failed calls are this contract's, so a metric, `icat operations` and the ladder give one number for one channel's
calls over the whole capture. A call counted in one interval is outside every other, and a stop with no start is stated
with its state and status rather than counted (ADR-032).

Revision 193: a reader answers an interval the same way. Within one, a channel holds the calls the interval holds by
the record that counts each - a completed call and a stop with no start by the stop, a call open at capture end by its
start, a record that pairs with nothing by itself - so every call is in exactly one place in time. Its failures,
unpaired stops and durations are those calls', its call records are the ones read within the interval, and its page
lists exactly those calls in the same order. A channel holding no call in the interval is still listed, counting none,
as a paired channel is. An interval holding every reading answers exactly as the whole capture does.

## 5c. A client call's other end (revision 225)

A client call's other end is the server call that served it. ADR-034 measured the chain that finds it through ALPC,
and `rpc-call-peer-v1` follows it, over this contract's calls and the generation's ALPC records: a row whose
mechanism is `Alpc` and whose kind is `Send` or `Receive`, its header process and thread, its reading, and its
`AlpcMessageId` source field. A client call is **served** by a server call when:

1. the client call is `Completed`. Its thread is its start's header thread;
2. exactly one ALPC send was read on that thread, in the call's process, after its start and before its stop, and the
   send carries a message id;
3. that message id was received exactly once in another process, read after the send and before the call's stop;
4. a server call's start was read on the receiving thread, in the receiving process, at or after the receive, within
   5 ms of it and before the client call's stop. The first such start is the server call;
5. the two calls carry one interface and one procedure.

Every other shape leaves the call's other end unresolved, with its reason:

| Reason | Meaning |
|---|---|
| `NoAlpcEvidence` | the generation holds no ALPC record, so nothing is resolved; whether its capture collected ALPC is its coverage ledger's to say (`coverage-v2`), never this absence's (R21) |
| `NotCompleted` | the call has no start or no stop, so it has no window |
| `NoSend` | no send in the window on the call's thread: another transport, or a send not delivered |
| `SeveralSends` | more than one send in the window: which one carried the call cannot be told |
| `NoMessageId` | its one send carried no message id |
| `NoReceive` | no other process received the message before the call stopped |
| `SeveralReceives` | more than one other process's receive of the message before the call stopped |
| `NoServerCall` | no server call began on the receiving thread within 5 ms and before the call stopped |
| `CannotCheck` | either call carries no interface or no procedure, so the link cannot be checked |
| `Conflicting` | the server call carries another interface or procedure: conflicting evidence, never a link |
| `ServerCallShared` | another client call reached the same server call, so neither is linked |

- **A message id is never a key by itself.** Ids repeat within seconds across processes (ADR-034). One joins only inside
  one client call's window, from its one send on one thread.
- **Nothing is linked by time alone** (P8). The 5 ms bound chooses among the server calls one thread began after the
  message reached it; a server call on another thread, however close in time, is never a candidate.
- **A link is checked, never assumed.** A disagreement of interface or procedure is stated, not linked.
- A served call's other end is the server call's process binding; the server call's other end is the client call.
  A server call reached by no client call is unresolved, and has no reason of its own: its client may be remote, may
  not use ALPC, or may not be in the capture.

Revision 226: a reader of §5a's channels also names who is at the other end of a channel's calls - for a client
channel the processes that served them, the most first, and why the rest are unresolved; for a server channel the
processes whose calls it served - over the whole capture or over the calls an interval holds by §5b's rule. A call
names its other end's process and the key of the call there, which opens that call on its own channel. A generation
that holds no ALPC record names no other end at all, rather than an unresolved one for every call.

Revision 227: the overview's graph joins two process instances by an RPC edge when a served call joins them and the
evidence policy admits both calls' bindings. The edge is correlated at best, as weak as the weaker binding, and counts
the call records at its two ends - a client call's start and stop, and the server call's start and stop when it has
one - never the ALPC records that link them. Under an interval it counts those its readings fall in. A generation
whose coverage ledger names no collected ALPC descriptor draws none and reads nothing for them.

Revision 373: `icat operations` states beside the calls it lists what the capture covered of RPC and of ALPC over the
session, from its coverage ledger (`coverage-v2` §4). Its JSON's `coverage` lists the two in that order, each with its
state and the fact behind it; its report says RPC's in the words `icat metric` uses and, when it resolves no other end,
whether the capture collected ALPC or only holds no record of it. A count of none never stands for a source the
capture lacked, and a generation without a ledger says it judged nothing.

## 5d. Across an interval release (revision 435)

A release of a session's oldest interval (`contracts/store-v1.md` §8, ADR-043) keeps every record of a call or an
ambiguous run one of whose records stays, so a call open across its boundary is still `Completed`, with its duration,
interface and status, and never reads as one whose start was not observed. It also keeps what a completed client call
that stays reads its other end from - the ALPC sends on its thread in its window, the receives of its one send's message
id before it stopped, the first server call the receiving thread began after the receive - and every client call that
reached a server call that stays. Every call, and every call's other end or reason for none, then reads as before. A
call whose start its capture never saw is still `StartNotObserved` (I20).

## 6. Assumptions

- The provider raises a call's start and stop on one clock, in order. A stop that sorts before its start is read as
  one with no start, and the start as open.
- An activity id is unique to one call on one side until its stop. FX-RPC-001 held no reuse; a reuse is stated as
  ambiguity rather than paired.
- A call record the capture lost leaves its call unpaired, stated by the state above. No generation publishes which
  record was lost.

## 7. Identity of a derivation

A derivation is identified by `rpc-call-operation-v1`, the `process-binding-v4` derivation it rests on, and the
generation it was derived from. A change to what pairs, how, or what a call holds is a new rule identity (§24
`correlationRevision`). The other ends of §5c are identified by `rpc-call-peer-v1`, the `rpc-call-operation-v1`
derivation they rest on, and the generation; a change to what links, how, or when a link is refused is a new rule.

## 8. Not defined at this version

- Operations of any other mechanism.
- Durations of any interval but a client call and a server execution: those two are measured by
  `contracts/metrics-v1.md` §8a since revision 186.
- Linking a client call to its server call other than through §5c's ALPC chain: by thread nesting, by time, or across
  machines.
- Counting peers from §5c's links: `contracts/metrics-v1.md` still answers a peer as unavailable.
- Persisting calls or their other ends in a checkpoint, or extending them from one live generation to the next: a
  derivation reads its generation whole, holding every call record while it pairs and the keys with a call open while
  it walks them (revision 184).
- Late evidence revisions (I17): a later generation derives its calls again.
