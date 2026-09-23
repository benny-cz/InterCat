# ADR-014: Transport relations from mirrored endpoints, and the process filters built on them

- Status: accepted for M1
- Date: 2026-09-23
- Decision owners: InterCat maintainers
- Relates to: §5.1 (double counting), §5.2 (peer ranking), §5.3 (cross-side and canonical-owner accounting), §7.1
  (`Relation`, `Channel`), §7.4 (correlation contracts), §19.1 (process and peer filters), §23 (`EN-Grouping`,
  `EN-RelationStrength`, `EN-EvidencePolicy`), ADR-012, ADR-013, `contracts/relations-v1.md`,
  `contracts/metrics-v1.md`, `contracts/entities-v1.md`

## Context

`process-binding-v2` says which process made each record, and the question the product exists for is the next one:
who did it talk to. §19.1 defines `participant(P)`, `sender(P)`, `receiver(P)`, `between(A,B)` and `peer(P,Q)` for
that, and until now `participant(P)` failed closed while the others had no syntax. §7.4 sets the bar a correlator
must clear first: it states its join keys, lifecycle scope, timeout, cardinality, ambiguity policy and evidence
requirements, and "the concrete keys per mechanism cannot be settled from documentation alone: they are an M0
measurement". Four facts decided what could be built.

**The endpoint orientation is measured, and uniform.** Kernel-Network names a source and a destination endpoint on
every TCP descriptor. `FX-TCP-001` established in M0 that on send and receive records the source is the owning
process's own endpoint. Whether that held for connect, accept and disconnect was not measured. On the 2026-09-23
IC-009 `peak` capture it does, with no exception. All 40 connection lifecycle records carried their owner's own data
pair, and none carried it mirrored. So one record's end and another record's mirrored end are two ends of one
connection, on every admitted descriptor.

**The provider connection identifier is unusable.** `connid` was already admitted as a source field. It was zero on
the measured build and is reusable, so it cannot prove a peer or a connection's lifetime (R22). The endpoint pair is
the only key the evidence supports.

**Most traffic that leaves a process leaves the host.** On the 555-record baseline capture every TCP record's other end
is remote: 485 records, no mirror, no local peer. On the `peak` capture, 74,681 of 74,788 TCP records have a
mirrored end held by one local process, and the other 107 are the machine's own traffic to remote hosts. A rule
that pairs only what both ends evidence answers the local case completely and must say, record by record, why it
answers nothing for the rest.

**The metrics contract already defined cross-side totals, and then refused them.** `BytesSent` under `ReceiveSide` is
a sent total measured where it arrived. `metrics-v1` answered it for a whole session and refused to attribute it to
a process "until a relation proves the other end". The relation is what makes it attributable.

## Decision

1. **`tcp-endpoint-relation-v1`: an end is `(family, own endpoint, remote endpoint)`, and its other end is its
   mirror.** A record's other end is the mirror's **holder**: the one instance every record there binds to, when
   every record there that names a PID names that instance's PID. An ambiguous mirror (two instances or two PIDs), an
   unbound one, a missing one and a record with an incomplete endpoint each leave the other end unresolved with that
   reason; a mechanism no rule covers says so. `contracts/relations-v1.md` freezes it.

2. **The strength is `Correlated`, or `Candidate` when the other end binds only as a candidate; never `Direct`.** No
   single record names both ends. A record's other end depends on the evidence at that end, not on the record's own
   binding, so the same relation can be `Correlated` seen from one end and `Candidate` seen from the other. The
   default evidence policy therefore attributes a send to a reused PID's later instance only when candidates are asked
   for, as it does for the instance's own records (ADR-013).

3. **An end's scope is the whole capture.** A port reused by a second instance makes the end ambiguous for all of its
   records rather than split at a guessed boundary. On both measured captures no end had two holders, so the
   conservatism costs nothing measured; a time-scoped rule is a later, separately identified revision.

4. **Address classes are not evidence.** A mirrored end with a local holder is a local connection whether its address
   is loopback or the host's own LAN address. An unmirrored one is not "remote": it is `PeerNotObserved`, because a
   local peer whose records were lost looks the same without a coverage ledger.

5. **Process filters select records by role; the accounting decides which end measures them.** `owner(P)` keeps the
   records P made, `participant(P)` also those whose other end is P, `sender(P)` the records in which data left P, and
   `receiver(P)` those in which it reached P. `peer(P,Q)` narrows any of them to the records whose other end *from P*
   is Q. It is refused on its own, because §19.1 forbids it becoming a global filter. Ungrouped, a filtered total
   takes the kept records as a whole-session total takes all of them. So `participant(P)` under sender accounting is
   the volume of P's conversations, each transfer measured once, at its sender.

6. **An owner filter never takes a cross-side total.** `owner(P)` with `BytesSent` under `ReceiveSide` would take P's
   receive records and call them sent bytes. The records that measure P's sent bytes at the receiving end are P's
   peers', which `sender(P)` selects. The request is refused as meaningless and names `sender` and `receiver`, rather
   than answered. It was previously unavailable, which implied a derivation could fix it; none can.

7. **Records a filter cannot decide are disclosed, not guessed.** A record outside a filter whose other end is
   unresolved might involve P, and is counted by reason (`unresolvedCounterparts`). A record P made is never counted
   there: a process connected to itself holds both ends, and both would be observed. `PeerNotObserved` is stated
   apart, because it involves P only if P's own records are missing.

8. **Grouping follows the metric's direction, and a peer grouping follows the focus.** Grouped by process, a
   `BytesSent` record belongs to the process the data left and a `BytesReceived` record to the process it reached. A
   record made at the other end is grouped through the relation, so cross-side totals group, and a record whose other
   end is unresolved is unattributed with the reason. The records a total does not take are attributed the same way,
   so a group's side breakdown shows its transfers measured at both ends. `EN-Grouping` gains 9 `Peer`: with a focus,
   each record belongs to the process at its other end from the focus. It is what ranks "who did P talk to" (§5.2).

## Consequences

- `participant(P)`, `sender(P)`, `receiver(P)` and `peer(P,Q)` are answered, with the binding and relation rules
  named in every result. `NoParticipantRelations` is no longer produced and keeps its meaning for older results.
  `NoTransferAssociations` now means only what it says: a canonical owner needs per-transfer associations, which a
  relation does not provide.
- On the `peak` capture, the workload server's conversation is 34,300,070 B sender-accounted. All of it is with the
  workload client, and it equals, to the byte, the 34,168,998 B plus 131,072 B the two processes sent each other. The
  client's sent bytes measured at their receiver are 34,168,998 B, the same number measured at the other end. Each
  query took about 1.6 s of wall time, of which deriving 9 relations over 74,788 TCP records took 50 ms.
- `icat processes --pid` shows each instance's id, evidence, parent and what it sent to and received from each process
  at the other end, so a focus is one copy away. `icat metric` takes `--participant`, `--sender`, `--receiver`,
  `--peer` and `--group-by peer`.
- A long capture where a port is reused by another process, or a socket is inherited by a child, leaves those
  connections' other ends ambiguous rather than split. Time-scoped ends, UDP, IPv6 and every non-TCP mechanism remain
  outside every relation rule, and say so per record.
- `ActiveChannels` and `ActivePeers` stay unavailable. A distinct count over relations would silently leave out every
  peer the capture cannot resolve, and a count has no representation for an unknown part yet.

## Alternatives considered

- **Pair by the connection identifier.** Rejected: measured zero, and reusable (R22).
- **Pair each unresolved record with the peer nearest in time on the same port pair.** Rejected: it resolves port
  reuse by proximity, which §7.4 and P6 forbid, and it would turn an ambiguous end into a confident wrong peer.
- **Restrict the rule to loopback addresses.** Rejected: a connection to the host's own LAN address is equally local,
  and both ends being held locally is the evidence; the address class adds nothing the mirror does not already prove.
- **Make `participant(P)` directional through the metric** (`BytesSent` meaning only what P sent). Rejected: it would
  duplicate `sender(P)` and leave no request for P's conversation volume; the direction belongs to the focus role and
  to grouping, where §6 already put it.
- **Answer `owner(P)` with a cross-side total from P's own records.** Rejected: it relabels bytes P received as bytes
  P sent.
