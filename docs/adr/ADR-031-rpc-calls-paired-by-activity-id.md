# ADR-031: An RPC call is its start and its stop, paired by activity id on one side

- Status: accepted for M3
- Date: 2026-09-27
- Decision owners: InterCat maintainers
- Relates to: §3.2 (L4), §7.1 (`Operation`), §7.4 (correlation contracts: RPC), §19.2, §24 (`correlationRevision`),
  R1, R20, R22, P1, P7, P8, ADR-004, ADR-030, `contracts/operations-v1.md`, FX-RPC-001

## Context

§7.4 asks every correlator to state its join keys, lifecycle scope, timeout, cardinality, ambiguity policy and
evidence requirements. They are fixed from a measurement before the correlator exists. For RPC it names "validated
activity/call identities and role-specific lifecycle schemas", and it rules out interface and procedure as a call's
identity: they are a grouping key.

Microsoft-Windows-RPC raises four call events. A client call raises 5 at its start and 7 at its stop, and a served call
raises 6 and 8. ADR-030 binds each record to the process that raised it. The records carry no size, so a call is an
operation with a start, an end and a status, never a volume (ADR-004).

FX-RPC-001 on adapter 0.7.0 measured what joins them:

- **The fixture's client:** 63 call starts and 63 stops. 62 starts carried an activity id, each id on one start and one
  stop only, and all 62 paired. The call without an id was the first on one of the client's two threads, and its
  start and stop both carry none.
- **The service host:** 61 of 61 server starts on the expected interface, inside the client's call window, paired with
  a stop by activity id.
- **Across sides:** no client call shared an activity id with a server call. The id pairs one side's start with its
  stop, and never a caller with the server that served it.

The M0 evaluator paired completions through a simple map for scoring against truth. It is no correlator: it says
nothing about a reused id, a stop without its start, or a start still open when the capture ends.

## Decision

1. **Join key.** A call record joins by the process it belongs to (its PID under `process-binding-v3`), its side
   (client for `Outbound`, server for `Inbound`) and its activity id. A record with no activity id has no key. It is an
   operation of its own, unpaired, with that reason. Thread nesting could pair it on a synchronous path, but no
   measurement validates that path, so it is not used (§7.4).
2. **Order and cardinality.** A key's records are read in canonical order: native reading, then raw locator, then fact
   key. A start opens a call, and the next stop closes it: one start, one stop. The derivation reads the whole
   generation and sorts before pairing, so how the evidence was cut into segments or chunks changes no pairing (I14).
3. **Ambiguity.** A start while its key already has a call open means the id was reused before its stop. Which stop is
   whose then cannot be told from the records. Every record of that key is ambiguous and pairs with nothing, from the
   earlier open start until as many stops as starts have been read. A stop with no open call is a call whose start
   is not in the evidence.
4. **No timeout, and censoring at the edges.** A stop is never found by time proximity (P8), and nothing expires. A
   start with no stop by the end of the derived evidence is open at capture end: censored, not failed. A stop whose
   start is not in the evidence is `StartNotObserved`: it began before the capture or its start was not delivered, and
   no one start time is invented for it.
5. **One side only.** A client call is never paired with the server call that served it. No id links them, and a
   pairing by time is what P7 forbids. A call's other end stays unresolved.
6. **What a call is.** Its identity is its first record's observation identity. It binds to a process instance by that
   record's reading, as any record does. It holds its interface, procedure and protocol from its start and its status
   from its stop. Its duration is the session time between the two readings on one clock; a call with a missing end
   has none. Status 0 is success, and any other status is the call's own failure code.
7. **A rule identity.** `rpc-call-operation-v1` names the rule; a change to what pairs, or how, is a new identity
   (§24). It rests on `process-binding-v3`.

## Consequences

- An RPC call exists as an operation. `icat operations` lists each process's calls by side and interface, with
  completions, failures, open calls and durations, and states every call it could not pair and why.
- A logical-operations metric basis, L3's one-sided RPC channels and L4's call lanes can read the calls, and so can a
  reused L5 evidence step. None of them is built here.
- The derivation holds every call record's key while it pairs, and it is made on first use rather than checkpointed
  or extended between live generations. A long capture with many calls pays for that on each generation; an index of
  pending calls that a live generation extends is the incremental form, and it is owed.
- A reused activity id costs the key's overlapping calls, which stay ambiguous rather than guessed. FX-RPC-001 held
  none.

## Alternatives considered

- **Pair a stop with the nearest earlier start on the same thread.** Rejected. It is right for a synchronous call and
  wrong for an asynchronous or interleaved one, and nothing in the record says which it was (§7.4).
- **Pair the most recent open start under a reused id (last in, first out).** Rejected. Nested calls would pair
  correctly and overlapping asynchronous ones would not, and the records cannot tell the two apart.
- **Pair a client call with a server call by interface, procedure and time.** Rejected. That is a peer guessed from
  timing (P7), and many calls to one interface overlap.
- **A timeout that fails an open call.** Rejected. A lost stop is not a timeout unless the source reports one (§7.4).
