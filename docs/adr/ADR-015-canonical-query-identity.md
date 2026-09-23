# ADR-015: The canonical query identity, and what it hashes

- Status: accepted for M1
- Date: 2026-09-23
- Decision owners: InterCat maintainers
- Relates to: §10.4 (query identity and API), §10.5 (canonical specification), §21.2 artifact 10, §23 (specification
  member order, `EN-FilterDimension`), §24 (version axes), R18, I16, ADR-012, ADR-014,
  `contracts/query-identity-v1.md`, `contracts/metrics-v1.md`

## Context

§21.2 lists `contracts/query-identity-v1.md` among the M0/M1 closure artifacts, while the backlog placed IC-018, which
freezes it, in M2. The metrics a session answers already had everything the canonical form needs - a request
resolved against §5.3's matrix and materialized - and `metrics-v1` shaped its request in §23's member order for this
purpose. Writing the form down against real requests found five places where the plan could not be implemented as
written.

**`requestedRows` was both hashed and not hashed.** §23's member order ends with `requestedRows`, which puts it inside
the hashed specification, while §10.4 and §10.5 define `QueryIdentity` as the hash *and* `RequestedRows` beside it. A
top-5 and a top-20 ranking of one total aggregate exactly the same records; hashing the cut would give them no shared
cache entry.

**A rate had no member for its numerator.** The member order names `metric` but nothing that says what a rate
divides, so `Rate` of sent bytes and `Rate` of observations would have hashed alike.

**Filter terms are sorted "by dimension code", and no dimension has a code.** §10.4 lists the dimensions and §23
assigns none.

**A snapshot vector entry did not pin a snapshot.** `(captureId, generation)` names a generation number, not its
contents. Two sessions holding one capture - an import into two directories - can each publish a generation 2 by
different acts, a re-derivation in one and a retention in the other, and one identity would name two answers.

**The meaning of a metric is not a version axis.** §24 lists fourteen axes, and a query cache key carries every axis
its result depends on. None of them changes when the rules that give a total its meaning change - which records a
filter keeps, which process a grouped record belongs to - and those rules changed twice in two days (ADR-012,
ADR-014).

## Decision

1. **The identity is `(canonicalizationVersion, SHA-256(canonical bytes), requestedRows)`**, written
   `v1:sha256:<hex>` with the cut beside it. `requestedRows` leaves §23's canonical member order.
2. **`rateNumerator` follows `metric`** in the member order, present for a rate only.
3. **`EN-FilterDimension`** gives the dimensions of §10.4 codes: 1 `Host`, 2 `ProcessInstance`, 3 `Executable`,
   4 `Mechanism`, 5 `Layer`, 6 `Endpoint`, 7 `Direction`, 8 `Operation`, 9 `Status`, 10 `Quality`, 11 `Source`,
   12 `ByteValue`. A peer is a member of the process-instance term, because §19.1 makes it relative to its focus, and
   time is the specification's `timeScope`; neither is an independent term.
4. **Each snapshot entry carries its generation's manifest digest**, which pins every byte the answer read.
5. **`metricsContract` is a version axis**, owned by Analysis, bumped when what a metric result means changes, and
   invalidating query caches and cursors. §24 lists fifteen axes.
6. **Only the axes and terms an answer depends on are written.** The binding rule and the evidence policy appear only
   when a record is bound to a process; the relation rule only when records' other ends are used. A policy that
   cannot change an answer would otherwise split one query into two identities, which is exactly what materializing
   defaults exists to prevent.
7. **Entity and correlation revisions are named by their rule** while derivations are computed on demand from one
   generation, since the manifest-pinned generation and the rule decide them completely.
8. **The form is written token by token**, not through a general JSON serializer, and every token is drawn from ASCII
   letters, digits, `:` and `-`. Its bytes then depend on this contract alone, never on a library's escaping or number
   formatting, and a token that would need escaping is refused as a defect.

## Consequences

- Every `icat metric` answer - a value, a grouping or an unavailable reason - names its identity, and
  `--print-canonical` prints the canonical line a UI can be compared against. The golden corpus
  `fixtures/FX-QUERY-001` pins twelve specifications to their bytes and hashes, and its hashes check against an
  independent SHA-256 implementation.
- Answering a structurally unavailable request, such as a logical-operations basis, now opens and verifies the
  generation's segments first, because its identity names the snapshot. An unavailable answer is therefore never
  asserted about a generation that would not have verified.
- Viewport, graph projection and query generation stay the UI's (IC-017, IC-018). Multi-value, negated and `is unknown`
  filter terms follow §10.5's normalization when they exist, under this version only if the existing bytes do not
  change.

## Alternatives considered

- **Hash `requestedRows` as §23 listed it.** Rejected: it gives identical aggregations different identities, and §10.4
  and §10.5 already say otherwise.
- **Snapshot by `(captureId, generation)` as §10.4's example shows.** Rejected: a generation number is local to one
  session, and two sessions can hold one capture.
- **Always write the evidence policy.** Rejected: it hashes a member that cannot change the answer, which is the one
  thing materialization exists to prevent.
- **Serialize with `System.Text.Json`.** Rejected: the bytes would then depend on a library's escaping and formatting
  choices, which a contract cannot freeze.
