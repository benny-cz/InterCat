# ADR-055: Every correlation states one of five strengths, which its rule names for each case it can meet

- Status: accepted; revision 727. It records the model §7.4 set and the correlators' contracts share - process binding
  (`entities-v1`), transport relations (`relations-v1`), RPC calls and their other ends (`operations-v1`) and joins
  across captures (`workspace-v21`) - and draws a conflicting relation as unasserted as a candidate
- Date: 2026-10-22
- Relates to: §5, §6.3, §6.6, §7.4, §18.5, §23 `EN-RelationStrength`, `EN-EvidencePolicy`, I16, R7, R14, R22, P7, P8,
  ADR-013, ADR-014, ADR-016, ADR-020, ADR-030, ADR-031, ADR-034, ADR-041

## Context

Most of what the product shows is a join: a record to the process that raised it, a connection to the process at its
other end, an RPC call to the call that served it, a connection in one capture to its other end in another. Windows
names few of these outright. A PID is reused, ports repeat, message ids recur within seconds, and two readings close in
time prove nothing. §7.4 asked each correlator to state its join keys, scope, cardinality, ambiguity policy and
evidence, measured per mechanism, and to produce one of five strengths. Each correlator's keys were fixed in its own
decision record as it was measured; no record stated the model they share, how strengths combine, or what the person
sees of each.

## Decision

1. **Five strengths** (`EN-RelationStrength`), each a claim about the evidence, never a confidence score:
   - `Direct`: one record names the fact and what it is joined to - a lifecycle record names the instance it created,
     ended or confirmed.
   - `Correlated`: a measured rule joins exactly one partner and nothing contradicts it - a record inside its PID's
     first instance; a TCP or UDP record whose incarnation is paired with the one mirror incarnation that holds its other
     end; an RPC client call linked to the one server call its one send reached.
   - `Candidate`: the rule's join holds, but a known weakness remains - a record inside a later instance of a reused PID,
     a relation whose partner binds only as a candidate, a connection in one capture whose mirror in another overlaps it
     within the clocks' uncertainty (ADR-041).
   - `Unresolved`: no join, always with its reason - `PeerNotObserved`, `PeerAmbiguous`, `BeforeFirstEvidence`,
     `SeveralReceives`, `NoServerCall` and the others each contract lists.
   - `Conflicting`: the evidence disagrees with the join - a server call of another interface or procedure than the
     client's. It is stated, and never made a link.
2. **A rule names its cases.** Each correlator is a named, revisioned rule - `process-binding-v4`,
   `transport-endpoint-relation-v4`, `rpc-call-peer-v1`, `cross-capture-connection-candidate-v2` - whose contract lists
   every case it can meet with the strength or the reason it yields, and every answer names the rules it rests on (I16).
   A rule's keys are fixed by measurement on its fixture before it is implemented.
3. **What is never a key.** A reusable value alone - a PID, a port, a handle, a message id, a name - joins nothing (R22),
   and time proximity alone makes at most a candidate annotation, never a join (P8): a time bound may only choose among
   joins the evidence already allows, as the 5 ms bound chooses among one receiving thread's server calls.
4. **The person chooses what counts.** An evidence policy (`EN-EvidencePolicy`) admits `Direct` only; `Direct` and
   `Correlated`, the default; candidates as well; or every strength. It is part of every query's identity, the same in
   the window and in `icat`, and definitive causal views admit `Direct` and `Correlated` alone.
5. **Strengths combine downward.** A relation is no stronger than the weakest join it rests on; an aggregate edge takes
   the least certain strength among its members; a record's other end is decided by the evidence at that end alone, so a
   record whose own owner is unresolved can still have a resolved other end.
6. **The canvas shows strength without hue** (R14): a `Direct` relation solid, a `Correlated` one dashed, and anything
   weaker dotted with an open ring at its middle, so no candidate, unresolved or conflicting relation is read as asserted;
   cards and tables say the strength in words.
7. **A correlator derives over a generation.** Each runs over the segments a generation names, holding its open joins
   for that derivation and releasing them with it; a live capture's next generation extends the last. Nothing is joined
   from a stream yet, so the 30-second pending-join expiry §18.5 sets for a streaming adapter has no correlator to bound,
   and the `EvictedUnresolved` state it would produce is defined but unused.

## Why

- **Claims rather than scores.** A probability would invite arithmetic the evidence cannot support, and would differ
  between a capture that saw a lifecycle record and one that did not by an amount nobody could justify. Five named
  strengths, each tied to a case in a contract, can be checked against a fixture.
- **Unresolved with a reason.** `PeerNotObserved` - no record of the capture holds the other end - is not "remote", and
  `NoAlpcEvidence` says only that the generation holds no ALPC record, while whether the capture collected ALPC is the
  coverage ledger's to say (R21); a reason keeps an absence from being read as a fact.
- **Conflicting kept.** A disagreement between a client's and a server's interface is evidence that something is not
  what it seems; dropping it would hide exactly what an investigation looks for.
- **The ring for every weaker strength.** A conflicting relation is drawn only when a policy admits every strength; it
  carried the dots of a candidate without the ring, which read as more asserted than a candidate. Revision 727 rings it.

## Consequences

- Turning on candidates shows more edges and rows, each marked; the counts the default shows are those of joins a
  measured rule made with nothing contradicting them.
- A new mechanism's correlator brings its contract's table of cases, its fixture and its rule identity before it is
  drawn; until then its records stay unresolved with `NoRelationRule`.
- Pending joins are bounded by the generation a derivation reads and by the analysis memory budget, not by count and
  age as R8 and §7.4 ask; a streaming correlator, when one exists, owes those bounds and the eviction state.

## Alternatives considered

- **A numeric confidence per join.** Rejected above.
- **Joining by nearest time when no key exists.** Rejected: P8, and a nearest neighbour exists in every capture, so it
  would always "find" a partner.
- **Dropping conflicting evidence as noise.** Rejected above.
- **Strength by hue.** Rejected: hue is a mechanism's (§6.6), and a colour alone would fail R14.
