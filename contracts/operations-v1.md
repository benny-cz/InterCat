# InterCat operations v1

Status: **implemented** as `rpc-call-operation-v1`, for RPC calls (revision 178), and counted on the logical-operations
metric basis since revision 183 (`contracts/metrics-v1.md` §8a). No other mechanism derives an operation at this
version (§8).

This contract fixes how §7.1's `Operation` - a derived logical call with an optional start, end and status and the
observations it is made of - is derived from a generation's published segments. It is §7.4's RPC correlator, with the
join key, lifecycle scope, timeout, cardinality, ambiguity policy and evidence requirements §7.4 requires every
correlator to state before it is implemented. It owns no bytes: a derivation is computed from the observation and
source-field segments a generation names and from the process instances `contracts/entities-v1.md` derives over
them, and it changes none of them (R1, R20). ADR-031 records the decisions, and FX-RPC-001 the measurement they rest
on.

## 1. Scope

A derivation reads every segment of one generation, of **one capture on one clock**, as the process derivation it
rests on does. The process instances are `process-binding-v3`'s over the same segments.

## 2. Call records

A **call record** is a row whose mechanism is `Rpc`, whose kind is `RequestStart` or `RequestEnd`, and whose direction
names its side:

| Direction | Side | Descriptors (Microsoft-Windows-RPC) |
|---|---|---|
| `Outbound` | client | 5 call start, 7 call stop |
| `Inbound` | server | 6 call start, 8 call stop |

Its process is the PID it belongs to under `process-binding-v3`, which for RPC is the process that raised it
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
  no call is paired across sides and a call's other end is unresolved (P7).

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
lists an instance's channels, a channel's calls a page at a time in reading order, and, as an evidence scope, the
records of a channel's calls or of one call: each call's start, then its stop. A channel or call a generation no longer
holds is stated as such, never read as an empty one. A channel belongs to one process, so a process's channels add
up to its calls; unlike a paired channel, it is not listed for any other process.

## 5b. Counting calls (revision 183)

`contracts/metrics-v1.md` §8a counts these calls: a started count takes a call by its start, and a completed or failed
count by its stop when the stop is paired with its start, each where that record's reading falls. Its completed and
failed calls are this contract's, so a metric, `icat operations` and the ladder give one number for one channel's
calls over the whole capture. A call counted in one interval is outside every other, and a stop with no start is stated
with its state and status rather than counted (ADR-032).

## 6. Assumptions

- The provider raises a call's start and stop on one clock, in order. A stop that sorts before its start is read as
  one with no start, and the start as open.
- An activity id is unique to one call on one side until its stop. FX-RPC-001 held no reuse; a reuse is stated as
  ambiguity rather than paired.
- A call record the capture lost leaves its call unpaired, stated by the state above. No generation publishes which
  record was lost.

## 7. Identity of a derivation

A derivation is identified by `rpc-call-operation-v1`, the `process-binding-v3` derivation it rests on, and the
generation it was derived from. A change to what pairs, how, or what a call holds is a new rule identity (§24
`correlationRevision`).

## 8. Not defined at this version

- Operations of any other mechanism.
- A duration metric over these calls, which needs a cohort a metric request cannot name yet (`contracts/metrics-v1.md`
  §12).
- Pairing a client call with the server call that served it, and pairing by thread nesting.
- Persisting calls in a checkpoint, or extending them from one live generation to the next: a derivation reads its
  generation whole, holding each call record's key while it pairs.
- Late evidence revisions (I17): a later generation derives its calls again.
