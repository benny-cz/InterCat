# InterCat relations v1

Status: **implemented** as `tcp-endpoint-relation-v1`, for TCP over IPv4. Every other mechanism is outside every
relation rule at this version and says so (§8).

This contract fixes how the other end of a record is found: §7.4's network correlator, with the join key, lifecycle
scope, cardinality and ambiguity policy §7.4 requires every correlator to state before it is implemented. It owns no
bytes: a derivation is computed from the `observation-v1` segments a generation names and from the process instances
`contracts/entities-v1.md` derives over them, and it changes neither (R1, R20). How a filter or a grouping uses it is
`contracts/metrics-v1.md` §5 and §6. ADR-014 records the decisions.

## 1. Scope

A derivation reads every segment of one generation, of **one capture on one clock**, exactly as the process
derivation it rests on does: an endpoint, like a PID, means nothing outside the host and timeline it was observed
in. The process instances are `process-binding-v2`'s over the same segments.

## 2. The join key

Every admitted TCP descriptor names two endpoints: `SourceEndpointAddress`/`SourceEndpointPort` and
`DestinationEndpointAddress`/`DestinationEndpointPort` (`segment-v1`). On the measured build the **source is the
record's own endpoint and the destination the remote one, on every descriptor alike**: a send and a receive, a
connect, an accept and a disconnect. That was measured, not assumed:

- `FX-TCP-001` matched 48 of 48 truth operations, sends and receives, on the owner's own `(local, remote)` pair;
- on the 2026-09-23 IC-009 `peak` capture, all 12 connect, 8 accept and 20 disconnect records carried the same pair as
  their owner's own send and receive records on that connection, and none carried it mirrored
  (`bench/results/relations-20260923T001907Z/relations-summary.json`).

An **end** is therefore `(address family, own address, own port, remote address, remote port)`. A record with a
missing or zero address or port names no end. The **other end** of an end is its mirror,
`(family, remote address, remote port, own address, own port)`: the record holding it is the other end of the same
connection. Whether an address is a loopback address is not used: a mirrored end observed with a local holder is a
local connection whatever its address.

## 3. Holders

Each end has a **holder** when every record of it that binds to a process instance, at any strength, binds to the
same instance, and every record of it that names a PID names that instance's PID. An end whose records bind to two
instances, or name two PIDs, is **ambiguous**; one whose records bind to no instance is **unbound**. A record whose
PID binds to nothing still names its PID, so a second process at the same end is noticed even when it cannot be
bound.

The scope of an end is the whole capture: an end reused by a second instance - a port reused after the first
connection closed, a socket handle shared by two processes - is ambiguous for all of its records, never split by
time (§6).

## 4. Binding a record's other end

| The record | Its other end | Strength |
|---|---|---|
| A TCP record whose end's mirror has a holder | the mirror's holder | `Correlated`, or `Candidate` when any record of the mirror binds to its holder only as a candidate |
| Its end's mirror is ambiguous | — | `Unresolved`, `PeerAmbiguous` |
| Its end's mirror is unbound | — | `Unresolved`, `PeerUnbound` |
| No record in the capture holds its end's mirror | — | `Unresolved`, `PeerNotObserved` |
| It names no end | — | `Unresolved`, `PeerEndpointIncomplete` |
| Any other mechanism | — | `Unresolved`, `NoRelationRule` |

The strength is never `Direct`: no single record names both ends. A record's other end depends only on the evidence
at that other end; the record's own binding is a separate fact, so a record whose own owner is unresolved can still
have a resolved other end. `PeerNotObserved` means no record holds the other end: a remote peer, or a local one whose
records on that connection the capture does not hold. It is never read as "remote" alone.

## 5. Relations

A **relation** is two mirrored ends that each have a holder: the two holders, the endpoint each holds, the weaker of
the two ends' binding strengths, the earliest and latest reading of any record at either end, and how many records the
two ends hold. A process connected to itself is a relation with itself. Relations are listed by the end whose key
sorts first.

## 6. What is not inferred

- **Time proximity.** Two records are never paired because they are close in time, and an ambiguous end is never
  resolved by picking the holder nearest a record's reading (P6).
- **Connection identifiers.** The provider's `connid` is admitted as a source field and never used: it was zero on
  the measured build and is reusable (R22).
- **Addresses alone.** An endpoint pair with no local holder at its other end is not attributed to anything; an
  address class does not make a peer local or remote.
- **Per-transfer association.** A relation says which process is at a record's other end, not which of that
  process's records is the same transfer. The canonical owner of §5.3 needs that association and stays unavailable.

## 7. Assumptions

The orientation of §2 holds for the six admitted TCPv4 descriptors on the measured build. A descriptor whose
orientation is not measured is not a TCP descriptor this rule reads correctly, and admitting one is a catalog change
that requires re-measuring it. No session publishes a coverage ledger yet, so an other end that is `PeerNotObserved`
may be a lost record rather than a remote peer, and a result that needs other ends discloses how many it could not
decide rather than asserting they involve nothing.

## 8. Identity of a derivation

A derivation is identified by `tcp-endpoint-relation-v1`, the `process-binding-v2` derivation it rests on, and the
generation it was derived from. A change to what an end is, what holds it, how strongly, or when it is left
unresolved is a new rule identity (§24 `entityRevision`); a result names the rule it used.

## 9. Not defined at this version

- Relations for UDP, IPv6, named pipes, RPC, ALPC and shared sections.
- Time-scoped ends, which would split an end reused by a second instance at the first one's disconnect.
- Per-transfer associations and the canonical owner they enable.
- Persisting relations as a published table. They are computed from the segments on demand; IC-017 owns caching them.
