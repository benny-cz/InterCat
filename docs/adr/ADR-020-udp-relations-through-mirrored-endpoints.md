# ADR-020: UDP datagrams related through mirrored endpoints

- Status: accepted for M1
- Date: 2026-09-23
- Decision owners: InterCat maintainers
- Relates to: §7.1 (`Channel`), §7.4 (network correlation), §19.1 (process filters), §24 (`correlationRevision`),
  R22, P6, ADR-014, ADR-016, ADR-019, `contracts/relations-v1.md`, `contracts/metrics-v1.md`

## Context

ADR-019 admitted UDPv4 datagrams, with an orientation measured on FX-UDP-001:

- a send names its owner's own endpoint first;
- a receive names the datagram's sender first.

`tcp-endpoint-relation-v2` read TCP only, so every UDP record's other end was `NoRelationRule`. As a result:

- a process's UDP conversation could not be filtered or grouped by the process at the other end;
- a channel count over a capture with UDP was a lower bound by every datagram;
- `participant(P)` could not see a UDP exchange from P's peer.

## Decision

1. **`transport-endpoint-relation-v3` reads TCP and UDP.** A record's own end is read through its descriptor's measured
   orientation, which the domain states once by mechanism and kind. The records holding the mirrored pair are its other
   end, as for TCP.
2. **An end is keyed by its protocol as well as its endpoints.** TCP port 5000 and UDP port 5000 are different sockets,
   and a relation never crosses protocols.
3. **A UDP end is one incarnation for the capture.** UDP has no connect, accept or disconnect, so nothing can divide
   an end the way a TCP lifecycle does. A UDP flow is one channel between its first and last record. A port reused by
   another process within the capture leaves the end ambiguous, and is never split at a guessed moment (P6).
4. **Every TCP answer is unchanged.** TCP ends sort before UDP ends, so TCP channels keep their numbers. The rule's
   identity changes anyway, because a result that reads relations now reads UDP too (§24).
5. **The overview graph stays TCP until its edges carry a mechanism.** The leased overview labels every edge TCP and
   builds its graph-eligible timeline from those edges. It keeps its TCP relations exactly as before, and UDP joins it
   when IC-017 gives edges and graph buckets a mechanism of their own.

## Consequences

- On an imported capture of the UDP workload (three client sockets, 30 datagrams and 30 acknowledgements):
  - the client's whole conversation, 23,008 B, was attributed to the server as correlated, and the peer rows
    partitioned it;
  - the client had exactly 3 channels, one per socket, with no unknown part;
  - the ledger reported UDP as covered.
- A datagram to a broadcast or multicast address, or to a port no local process held, is `PeerNotObserved`. So is
  one from a remote sender.
- §13.1's remaining UDP cases - endpoint reuse, multicast and absent receivers - still need fixtures of their own.

## Alternatives considered

- **Split a UDP end by gaps between datagrams.** Rejected: a quiet flow is still one flow, and any gap threshold is a
  guess (P6).
- **Key UDP ends without the protocol.** Rejected: a TCP and a UDP socket on the same numbers would be paired as one
  connection.
