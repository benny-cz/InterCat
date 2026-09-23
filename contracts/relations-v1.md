# InterCat relations v1

Status: **implemented** as `transport-endpoint-relation-v3`, for TCP and UDP over IPv4. `tcp-endpoint-relation-v1`
scoped each TCP end to the whole capture; v2 divided an end into the connection incarnations its lifecycle records
witness (§3, ADR-016); v3 reads UDP datagrams through their measured orientation and keys every end by its protocol
(ADR-020). Every other mechanism is outside every relation rule at this version and says so (§9).

This contract fixes how the other end of a record is found: §7.4's network correlator, with the join key, lifecycle
scope, cardinality and ambiguity policy §7.4 requires every correlator to state before it is implemented. It owns no
bytes: a derivation is computed from the `observation-v1` segments a generation names and from the process instances
`contracts/entities-v1.md` derives over them, and it changes neither (R1, R20). How a filter or a grouping uses it is
`contracts/metrics-v1.md` §5 and §6. ADR-014, ADR-016, ADR-019 and ADR-020 record the decisions.

## 1. Scope

A derivation reads every segment of one generation, of **one capture on one clock**, exactly as the process
derivation it rests on does: an endpoint, like a PID, means nothing outside the host and timeline it was observed
in. The process instances are `process-binding-v2`'s over the same segments.

## 2. The join key

Every admitted TCP and UDP descriptor names two endpoints: `SourceEndpointAddress`/`SourceEndpointPort` and
`DestinationEndpointAddress`/`DestinationEndpointPort` (`segment-v1`), stored as the source names them. Which of the
two is the record's own is the descriptor's **orientation**, measured rather than assumed. On every TCP descriptor - a
send and a receive, a connect, an accept and a disconnect - and on a UDP send, the source is the record's own endpoint
and the destination the remote one. On a UDP receive the source is the **datagram's sender** and the destination the
receiver's own endpoint:

- `FX-TCP-001` matched 48 of 48 truth operations, sends and receives, on the owner's own `(local, remote)` pair;
- on the 2026-09-23 IC-009 `peak` capture, all 12 connect, 8 accept and 20 disconnect records carried the same pair as
  their owner's own send and receive records on that connection, and none carried it mirrored
  (`bench/results/relations-20260923T001907Z/relations-summary.json`);
- `FX-UDP-001` matched 64 of 64 truth datagrams and acknowledgements only through that orientation: 16 of 16 sends on
  each side named the owner first, and 16 of 16 receives on each side named the sender first (ADR-019).

An **end** is therefore `(protocol, address family, own address, own port, remote address, remote port)`, the own and
remote endpoints read through the record's orientation. TCP port 5000 and UDP port 5000 are different ends. A record with a
missing or zero address or port names no end. The **other end** of an end is its mirror,
`(protocol, family, remote address, remote port, own address, own port)`: the record holding it is the other end of
the same connection or datagram flow. Whether an address is a loopback address is not used: a mirrored end observed with a local holder is a
local connection whatever its address.

## 3. Incarnations and holders

A 4-tuple carries one connection at a time, and a port reused by a later connection is another connection. So an
end's records, in canonical order - native reading, raw locator, fact key - are divided into **incarnations** by the
lifecycle records the capture holds for that end: a `Connect` or an `Accept` begins one with itself, and a
`Disconnect` is the last record of the one it closes. Records before the first boundary, after the last, or between
a disconnect and the next open belong to the incarnation their position implies. An incarnation's **lifetime** runs
from the open that began it, or from the preceding boundary, to the disconnect that closed it, or to the next boundary;
with no boundary on a side it is unbounded on that side. An incarnation with no records - the gap between a disconnect
and the next open - takes no part in anything below.

Each incarnation has a **holder** when every record of it that binds to a process instance, at any strength, binds to
the same instance, and every record of it that names a PID names that instance's PID. One whose records bind to two
instances, or name two PIDs, is **ambiguous**; one whose records bind to no instance is **unbound**. A record whose PID
binds to nothing still names its PID, so a second process at the same end is noticed even when it cannot be bound.

An end whose lifecycle the capture did not witness is one incarnation, as under v1. A reused port whose connects and
disconnects were lost therefore stays ambiguous rather than being split at a guessed moment (§6). A UDP end has no
lifecycle records at all, so it is always one incarnation for the capture: a datagram flow, bounded by its first and
last record. A UDP port reused by another process within the capture is ambiguous for the whole capture.

## 3a. Pairing incarnations

The incarnations of an end pair with those of its mirror:

1. When both ends have the same number of incarnations with records, they pair **in order** - one connection at a time
   per tuple - provided each pair's lifetimes overlap, or each end has exactly one.
2. Otherwise an incarnation pairs with the one mirror incarnation whose lifetime overlaps its own, when that mirror
   incarnation overlaps no other incarnation of this end.
3. An incarnation with no overlapping mirror incarnation is **not observed** at the other end; one with several, or
   whose candidate overlaps several of this end's, is **undecided**.

Pairing is decided by witnessed lifecycle and the one-connection-per-tuple rule, never by the distance between two
readings.

## 4. Binding a record's other end

| The record | Its other end | Strength |
|---|---|---|
| A TCP or UDP record whose incarnation is paired with a held mirror incarnation | that incarnation's holder | `Correlated`, or `Candidate` when any record of the partner binds to its holder only as a candidate |
| Its incarnation is undecided, and every candidate has the same one holder | that holder | the weakest of the candidates' |
| Its incarnation is undecided otherwise, or its partner is ambiguous | — | `Unresolved`, `PeerAmbiguous` |
| Its partner is unbound | — | `Unresolved`, `PeerUnbound` |
| No record in the capture holds its end's mirror, or no mirror incarnation overlaps its own | — | `Unresolved`, `PeerNotObserved` |
| It names no end | — | `Unresolved`, `PeerEndpointIncomplete` |
| Any other mechanism | — | `Unresolved`, `NoRelationRule` |

The strength is never `Direct`: no single record names both ends. A record's other end depends only on the evidence
at that other end; the record's own binding is a separate fact, so a record whose own owner is unresolved can still
have a resolved other end. `PeerNotObserved` means no record holds the other end: a remote peer, or a local one whose
records on that connection the capture does not hold. It is never read as "remote" alone.

## 5. Relations

A **relation** is two paired incarnations that each have a holder: the two holders, the endpoint each holds, the weaker
of the two incarnations' binding strengths, the earliest and latest reading of any record of either, how many records
they hold, and whether each end's open and close were witnessed. A process connected to itself is a relation with
itself. Relations are listed by the end whose key sorts first, then by incarnation.

## 5a. Channels

A **channel** is one connection incarnation, the instance §7.1 calls a channel; for UDP it is one datagram flow between
two ends over the capture. Channels are numbered in a stable order, by end - protocol first - and then by incarnation:

- two paired incarnations are one channel;
- an incarnation no mirror incarnation could pair with is a one-sided channel of its own: a remote peer, or a local
  one the capture holds no record of on that connection;
- among undecided incarnations, each one on the side with more incarnations is a channel of its own, because each is
  bounded by its own lifecycle and is a distinct connection. The other side's undecided incarnations belong to one of
  those channels, undecided which, so their records identify no channel. Counting both sides would count one connection
  twice.

A record identifies its incarnation's channel; a record with no end, and one of a mechanism no rule covers, identifies
none.
The relation between two paired incarnations exposes that same channel number, so a projection can join its displayed
edge to exactly the records at both ends. The number is deterministic within one derivation, not a cross-generation
identity: another relation rule can number channels differently.

## 6. What is not inferred

- **Time proximity.** Two records are never paired because they are close in time, and an undecided incarnation is
  never resolved by picking the holder nearest a record's reading (P6). Lifetimes decide pairing only through the
  lifecycle records that bound them.
- **Connection identifiers.** The provider's `connid` is admitted as a source field and never used: it was zero on
  the measured build and is reusable (R22).
- **Addresses alone.** An endpoint pair with no local holder at its other end is not attributed to anything; an
  address class does not make a peer local or remote.
- **Per-transfer association.** A relation says which process is at a record's other end, not which of that
  process's records is the same transfer. The canonical owner of §5.3 needs that association and stays unavailable.

## 7. Assumptions

The orientations of §2 hold for the six admitted TCPv4 descriptors and the two admitted UDPv4 descriptors on the
measured build. A descriptor whose orientation is not measured is not one this rule reads correctly, and admitting one
is a catalog change that requires measuring it (P27). A UDP datagram sent to a broadcast or multicast address, or to a
port no local process held, has no mirrored end and is `PeerNotObserved`; a UDP flow's lifetime is only the span of
its records, because nothing marks when a socket was bound or closed. Imported sessions publish an aggregate coverage ledger, but it cannot locate a lost
record at one end, and live captures do not yet publish one. An other end that is `PeerNotObserved` may therefore be
a lost record rather than a remote peer, and a result that needs other ends discloses how many it could not
decide rather than asserting they involve nothing. Incarnations rest on the lifecycle records the capture holds: a lost
connect, accept or disconnect merges two incarnations of an end into one, which can leave a reused port undecided,
but never splits one connection in two.

## 8. Identity of a derivation

A derivation is identified by `transport-endpoint-relation-v3`, the `process-binding-v2` derivation it rests on, and
the generation it was derived from. A change to what an end or an incarnation is, what holds it, how incarnations pair, how
strongly, or when an end is left unresolved is a new rule identity (§24 `correlationRevision`); a result names the rule
it used. v1 differed from v2 only in scoping every end to the whole capture, and v2 from v3 only in reading UDP, which
it left `NoRelationRule`; every TCP answer is the same under both.

## 9. Not defined at this version

- Relations for IPv6, named pipes, RPC, ALPC and shared sections, and UDP endpoint reuse, multicast and broadcast
  beyond reporting them unobserved.
- Per-transfer associations and the canonical owner they enable.
- Persisting relations as a published table. They are computed from the segments on demand; IC-017 owns caching them.
