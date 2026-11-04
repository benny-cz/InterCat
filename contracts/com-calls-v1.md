# COM calls contract, version 1

Status: M6 additive contract, revision 916 (ADR-077, ADR-079)
Owner: `InterCat.Analysis` (`ComCallIndex`)
Read by: `rpc-call-peer-v2` (`contracts/operations-v1.md` §5c), which serves a call carrying a linked COM call through it

A COM call is a call made through COM's channel from one apartment to another, as Microsoft-Windows-COM-Perf records it
at its client and its server (ADR-078). It is never an operation of its own: it is the RPC call that carries it, which
stays the operation (`contracts/operations-v1.md`), and a COM call annotates it with the process at its other end, its
HRESULT and its causality (ADR-079's first decision). This contract fixes how one generation's COM call records pair into
calls, which RPC call carries each, and how a client's call is linked to the server's - `com-call-link-v1` - from the
records' metadata and source fields alone.

## 1. Scope

A derivation reads every segment of one generation, of **one capture on one clock**, as the RPC calls it joins do
(`contracts/operations-v1.md` §1); it is given the RPC calls paired from the same segments, in the same order.

## 2. Records

A record is a COM call record when its mechanism is `ComCall` and its kind is `RequestStart` or `RequestEnd`. Its side is
the client for `Outbound` - COM-Perf's client start and stop, 39 and 40 - and the server for `Inbound`, 43 and 44. Its
process is the one that raised it, by `process-binding-v5` (`contracts/entities-v1.md` §2a); a COM call record of no
process, or of another kind or direction, is counted and is no call record. It carries, as `source-fields-v1` rows
(`contracts/segment-v1.md`):

- `ComCallTraceId` (19): its call's number in its process, which its stop repeats;
- `PeerProcessId` (20), on a start: the process at its other end - the client's target, the server's source - never its
  owner;
- `ComMethodNumber` (21), on a start: the method's number within its interface.

A start's identifier is its interface; a stop's status is its HRESULT; its activity id and thread are its header's.

## 3. Pairing

A record joins by its process, its side and its trace number. Records are read in canonical order - native reading, then
raw locator, then fact key - and a key's records pair as an RPC call's do by activity id (`contracts/operations-v1.md` §3):

- a start opens a call and the next stop of its key closes it: `Completed`;
- a start with no stop by the end of the evidence is `OpenAtCaptureEnd`: censored, never failed;
- a stop of no open key is `StartNotObserved`, a call of its own;
- a start while its key is open is a number used again before its stop: every record of the key is `Ambiguous` and pairs
  with nothing, from the earlier open start until as many stops as starts have been read;
- a record with no trace number is `NoTraceNumber`, a call of its own.

A call holds its start's interface, method, other end, activity and thread, and its stop's HRESULT; a call without a start
holds its stop's activity and thread.

## 4. Carriers (ADR-079's second decision)

A COM call's carrier is the RPC call it rides:

- at the **client**, the one RPC client call of its process whose start carries the COM call's start's activity id;
- at the **server**, the one RPC server call of its process, completed, on the thread of the COM call's start, whose start
  is at or before the COM call's start and whose stop at or after its stop.

Either must carry the COM call's interface, with its method as its procedure. Every other shape names no carrier, with its
reason:

| State | Meaning |
|---|---|
| `Carried` | one RPC call carries it, on its interface and method |
| `NotCompleted` | a client call without a start, or a server call without both ends: nothing to join it by |
| `NoActivityId` | a client call whose start carries no activity id |
| `NoCarrier` | no RPC call of its activity at the client, or around it on its thread at the server |
| `SeveralCarriers` | more than one: which carried it cannot be told |
| `Conflicting` | the one RPC call names another interface or procedure: conflicting evidence, never a carrier |
| `CannotCheck` | the COM call or the RPC call names no interface or no procedure |

- **Time alone joins nothing** (P8). At the client the RPC call stops after its COM call, so containment would join none
  (ADR-079): the activity id is the join. At the server the thread and the span choose among one process's calls.
- The server's join was measured for a server in the multithreaded apartment. A call that a single-threaded apartment
  dispatches on another thread finds no carrier rather than a guessed one.

## 5. Links (ADR-079's third decision)

A client call and the server call carrying its start's activity id are one call's two ends - `Linked`, each the other's
other end - when every one of these holds; the first that does not is the client call's state:

| State | Meaning |
|---|---|
| `NoActivityId` | the client call has no start, or its start carries no activity id |
| `NoServedCall` | no server call's start carries the id: its server may not be in the capture |
| `SeveralServedCalls` | more than one does: which served this call cannot be told |
| `WithinOneProcess` | the server call is in the client's own process: a call across an apartment, that process's own work and never IPC (ADR-077's fifth decision), so never a link |
| `ServerCallShared` | another client call's start carries the same id, so neither is linked |
| `PeerMismatch` | the client does not name the server's process at its other end, or the server the client's |
| `CannotCheck` | either names no interface or no method |
| `Conflicting` | the two name another interface or method: conflicting evidence, never a link |
| `NotCompleted` | either call has no start or no stop |
| `NotInside` | the server call does not lie inside the client's |

A server call no linked client call reached is `NotReached`: its client may be remote or not in the capture. A link is an
identity of one call's records, never a statement about when they happened (P8), and a peer is the process a linked
call's records name, checked by both ends (P7). RPC's own records still pair no client with a server
(`contracts/operations-v1.md` §5c): the link rests on COM-Perf's server record, so it holds only where COM-Perf is captured.

A linked client call carried by an RPC call, whose server call is carried by one, **relates** the client's RPC call to the
RPC call that served it. A server RPC call two relations reach is related to neither.

## 6. Assumptions

- The provider raises a call's start and stop in the process that makes or serves it, on one clock, in order (ADR-077).
- A trace number is unique to one call in its process and side until its stop; FX-COM-001 held no reuse, and a reuse is
  stated as ambiguity rather than paired.
- A client call's activity id is its own and travels to the server's records (ADR-079); a nested call made while serving
  another is unmeasured, and one id carried by several server calls links none of them.

## 7. Identity of a derivation

A derivation is identified by `com-call-link-v1`, the `rpc-call-operation-v1` and `process-binding-v5` derivations it
rests on, and the generation it was derived from. A change to what pairs, what carries, what links, or when either is
refused is a new rule identity (§24 `correlationRevision`).

## 8. Not defined at this version

- Asynchronous COM calls (COM-Perf's 41, 42, 45 and 46), which no capture admits.
- Keeping a derivation: it is computed from the segments on demand, never checkpointed or extended between generations.
- A reader of the links beyond `rpc-call-peer-v2`: views, metrics and relations read them as the RPC calls' other ends.
