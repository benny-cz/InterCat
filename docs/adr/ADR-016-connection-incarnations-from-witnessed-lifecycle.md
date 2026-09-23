# ADR-016: Connection incarnations from witnessed lifecycle

- Status: accepted for M1
- Date: 2026-09-23
- Decision owners: InterCat maintainers
- Relates to: §7.1 (`Channel`, `Relation`), §7.2 (port reuse through instance epochs), §7.4 (network correlation),
  §5 (`ActiveChannels`), R22, P6, ADR-014, `contracts/relations-v1.md`

## Context

`tcp-endpoint-relation-v1` (ADR-014) scoped each connection end to the whole capture. An end held by two processes,
typically a local port reused by a later connection, was therefore ambiguous for every record it held, and so were the
records at its other end. That cost nothing on the two measured captures, where no end had two holders. It would cost
most where the product matters most. A long capture of a busy local service cycles through ephemeral ports, and the
connections that reuse them are exactly the ones a user would want attributed. §7.2 already requires port reuse to be
handled "through instance epochs and lifecycle intervals", and §5 cannot count channels until a connection is an
instance with a lifetime.

The evidence to scope ends exists. Kernel-Network admits connect, accept and disconnect records on the same endpoint
pair as the data records. On the measured `peak` capture, 16 of the 18 mirrored ends carry exactly one open and one
disconnect, with the open first, the disconnect last, and no data record after it. The other two are a process
connected to itself, whose lifecycle the capture did not record. On the 555-record baseline, ends either carry both,
or show a connection opened before the capture or still open at its end.

## Decision

1. **`tcp-endpoint-relation-v2` divides each end into incarnations** at the lifecycle records the capture holds for
   it. A connect or an accept begins an incarnation with itself, and a disconnect is the last record of the incarnation
   it closes. Records are placed in canonical order - native reading, raw locator, fact key - so equal readings are
   ordered by evidence rather than by delivery. An end with no witnessed lifecycle is one incarnation, which is exactly
   v1.
2. **Holders are per incarnation**, under v1's rule: one instance and one PID across its records, or ambiguous.
3. **Incarnations pair in order when both ends have the same number** with records, because a 4-tuple carries one
   connection at a time, provided each pair's lifetimes overlap or each end has one. Otherwise an incarnation pairs only
   with the one mirror incarnation whose lifetime overlaps it and nothing else of its end. Lifetimes are bounded by
   lifecycle records, so this is witnessed lifecycle, never the distance between two readings (P6).
4. **An undecided pairing still names an other end when every candidate agrees.** If each incarnation that could be the
   partner is held by the same one instance, that instance is the other end whichever the partner is: a long-lived
   server accepting two connections on one tuple, with its accepts lost, is still the clients' peer. Any disagreement
   leaves the other end ambiguous.
5. **Only the relation rule's identity changes.** Results name `tcp-endpoint-relation-v2`, and every query identity
   that depends on relations changes with it, as §24's `correlationRevision` axis requires. The canonical form of
   `query-identity-v1` does not change: the version axes are an input to it, and the golden corpus fixes their values.

## Consequences

- A reused port whose lifecycle the capture holds pairs each connection with its own other end, where v1 left both
  without a peer. On the measured `peak` capture every answer is unchanged - 34,300,070 B of workload conversation,
  the same cross-side groups and unattributed bytes - because no port was reused.
- A relation now describes one connection incarnation, with whether each end's open and close were witnessed. That is
  the instance §7.1 calls a channel, which `ActiveChannels` needs before it can be counted.
- A lost connect, accept or disconnect merges two incarnations of an end; it never splits one connection in two. The
  merge can leave a reused port undecided, which is disclosed per record as before.

## Alternatives considered

- **Split an end wherever its holder changes.** Rejected: it infers a connection boundary from a change of process,
  which a shared or inherited socket also produces. It also uses the holder, which is the thing being decided.
- **Pair incarnations by the nearest reading.** Rejected: time proximity is a candidate annotation at best (§7.4, P6).
- **Split an end at a gap in its records.** Rejected: an idle connection is still one connection, and any gap threshold
  is a guess about the application.
