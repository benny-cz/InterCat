# ADR-019: UDP datagrams admitted by their measured orientation

- Status: accepted for M1
- Date: 2026-09-23
- Decision owners: InterCat maintainers
- Relates to: §4 (UDP row), §7.4 (network correlation), §13.1 scenario 2, §14.2 (tiers), P16, P27, R1, R8, R21,
  ADR-014, `contracts/segment-v1.md`, `contracts/relations-v1.md`

## Context

The Kernel-Network source requests its IPv4 keyword, which delivers UDP send and receive events (42 and 43) as well as
TCP ones. The catalog omitted UDP, because nothing had measured what its events mean. The ETL import of 2026-09-22
omitted 223 such records, and a capture on this machine omitted every UDP datagram its processes exchanged. So the
product could not show local UDP at all. That includes DNS through a local resolver and every loopback datagram
protocol.

TCP's orientation was measured on FX-TCP-001: every TCP descriptor names the record owner's own endpoint first,
send and receive alike. Nothing said UDP follows the same rule, and the status document had said so: "UDP relations
need their own orientation measurement before any rule reads them".

## Measurement

`udp-loopback`, a new truth workload, runs a client that sends 16 seeded datagrams from two bound sockets and a server
that acknowledges each to the endpoint it came from. Each process writes its own truth log naming both ports.
`icat measure udp` runs it under an owned session. The raw events were first decoded independently through the
registered manifest, so the orientation was learned before any plan assumed it.

- A **send (42)** names the sender's own endpoint first: 16 of 16 on each side.
- A **receive (43)** names the **datagram's sender** first, and the receiver's own endpoint as its destination: 16 of
  16 on each side. This is the opposite of a TCP receive.
- Every receive carried a header PID different from its payload owner, so the payload owner is the attribution, as
  §4.1 requires.
- Sizes equal the truth payloads, and the connection identifier is zero.

With the orientation applied, FX-UDP-001 met every traffic criterion of §14.2 at 100%. 64 of 64 truth operations
were observed, bound to their flow and byte-measured, and all 4 peer attributions were correct. Truth and observed
transport were both 11,802 B. The tier is `TrafficVisualization`, reproduced by a second run.

## Decision

1. **Admit UDPv4 send and receive** in the Kernel-Network source, at adapter `windows-etw-inventory-0.6.0`. The
   capture impact is re-measured with the wider admission set, because a plan that persists more records is a
   different plan (R8).
2. **Store endpoints as the source names them** (R1, `segment-v1`): a UDP receive's source columns hold the sender. A
   reader that needs a record's own end reads it through the **measured orientation**, which the domain states once by
   mechanism and kind: every admitted TCP descriptor and a UDP send are owner-first; a UDP receive is origin-first.
   The measurement builder uses this table. A relation rule that reads UDP must use it too.
3. **A focused capture admits only the transport it names**, with its lifecycle context. A source that serves several
   transports contributes only the focused one's descriptors, and enables only their events. UDP becomes a focusable
   transport.
4. **An orientation that fails shows up as its own finding.** The coverage evaluator reports an operation seen only
   on the mirrored flow as an orientation gap, not as a missing observation. The committed evidence of both transport
   fixtures re-evaluates to its tier in the test suite.

## Consequences

- The re-measured capture impact of the wider plan is 1.61 CPU percentage points for Kernel-Network with seven
  pairs, just over the 1-point Low ceiling and well inside §12's target. The machine was not quiet, and the unchanged
  Kernel-Process source moved over the same ceiling in the same run, so the class is recorded as measured and a
  quiet-machine run is owed (`bench/results/capture-impact-20260923T095313Z/impact.json`).
- New captures and imports carry UDP datagrams as transport observations. Byte totals in `TransportObserved` include
  them, and a coverage ledger reports UDP as collected.
- `tcp-endpoint-relation-v2` still reads TCP only. Until a rule covers UDP, a UDP record's other end is
  `NoRelationRule`, which is disclosed like any other undecided end. That also makes a channel count over a capture
  with UDP a lower bound. Relating UDP datagrams through mirrored endpoints is the next step, and it needs the
  orientation of decision 2.
- §13.1 scenario 2 is only partly covered: one-way and reply datagrams on distinct sockets are covered, while endpoint
  reuse, multicast and absent receivers are not. UDP over IPv6 is not requested, and IPv6 endpoints do not fit the
  `observation-v1` address columns in any case.

## Alternatives considered

- **Write a UDP receive's endpoints owner-first at admission.** Rejected: `segment-v1` defines the source columns as
  the endpoint the source names as the origin, and rewriting them per descriptor would make a stored row say something
  its source did not.
- **Assume TCP's orientation.** Rejected by the measurement itself: every receive would have been keyed to its mirror,
  and a relation rule would have paired each datagram with the wrong end.
- **Keep UDP omitted until relations exist.** Rejected: the records carry measured bytes and owners today, and leaving
  them out makes every UDP exchange an unexplained absence (R21).
