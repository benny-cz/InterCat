# InterCat metrics v1

Status: **frozen for the source-observations basis and implemented**. The logical-operations and
resource-topology bases, entity grouping and canonical-owner accounting are defined here as contract and
reported as unavailable by every session until the derivations they need exist (§10).

This contract is §21.2's `metrics-v1`: contribution keys, byte domains, accounting sides, cohorts, unknown
values and the exact scenarios of §21.1. It owns what a metric **means** — which records a total takes, which
it leaves out and how it says so. It owns nothing about segment bytes, which `contracts/segment-v1.md`
freezes, nothing about how a generation is published or held, which `contracts/store-v1.md` freezes, and
nothing about the canonical form and hash of a whole analysis specification, which
`contracts/query-identity-v1.md` will freeze (IC-018). ADR-012 records the decisions below.

## 1. A request

A metric request names:

| Member | Values | Default |
|---|---|---|
| `basis` | `EN-Basis` | `SourceObservations` |
| `metric` | `EN-Metric` | required |
| `byteDomain` | `EN-ByteDomain` | the metric's fixed domain, when it has one |
| `accountingSide` | `EN-AccountingSide` | the metric's fixed side, when it has one |
| `rateNumerator` | `EN-Metric`, a rate only | none |
| `timeScope` | `EN-TimeScope` | `AnalysisInterval` when an interval is named, otherwise `RetainedCapture` |
| `interval` | half-open `[start, end)` in the native ticks of the session's clock | none |
| `layer` | `EN-Layer` projection | the metric's implied layer, when it has one |
| `mechanism` | `EN-Mechanism` projection | none |

A request is resolved against §5.3's matrix (§2) **before** it is planned, and a request the matrix accepts is
**materialized**: every default above is written out, so "unset" and "set to the value the metric fixes" become
one request (§10.5). A result always names the materialized request it answers.

## 2. The matrix

| Metric | Source | Operations | Topology | Byte domain | Accounting side |
|---|---|---|---|---|---|
| `Observations` | yes | no | yes | none | none |
| `OperationsStarted`, `OperationsCompleted` | no | yes | no | none | none |
| `BytesSent`, `BytesReceived` | yes | yes | no | one of `TransportObserved`, `CompletedIo` | one of `SendSide`, `ReceiveSide`, `CanonicalOwner` |
| `EndpointActivityBytes` | yes | yes | no | one of `TransportObserved`, `CompletedIo` | fixed `EndpointActivity` |
| `RequestedIoBytes` | yes | yes | no | fixed `RequestedIo` | any of the four |
| `ApplicationPayloadBytes` | yes, application layer only | yes | no | fixed `ApplicationPayload` | any of the four |
| `CapturedContentBytes` | yes | yes | no | fixed `CapturedContent` | any of the four |
| `Rate` | yes | yes | no | the numerator's | the numerator's |
| `Duration` | no | yes, with a named cohort | mapping lifetime only | none | none |
| `ActiveChannels`, `ActivePeers` | yes | yes | yes | none | none |
| `MappingCapacity` | no | no | yes | fixed `Capacity` | none |
| `Errors` | yes | yes | no | none | optional: `SendSide`, `ReceiveSide` or `EndpointActivity` |

Refusals, each with its reason and, where a basis is at fault, the metrics that basis does define:

- A metric outside its basis.
- A byte domain on a metric that measures none, no domain on one that requires one, and a domain other than
  the one a metric fixes.
- **A domain another metric owns.** `BytesSent` over `RequestedIo` is a requested length reported as sent
  bytes, which is P3; the refusal names `RequestedIoBytes`. The same holds for `ApplicationPayload`,
  `CapturedContent` and `Capacity`, which is never traffic.
- An accounting side on a metric that has none, no side on one that requires one, and a side outside the
  metric's list.
- **`EndpointActivity` on `BytesSent` or `BytesReceived`.** Those name one direction and endpoint activity
  counts both, so the request contradicts itself; the refusal names `EndpointActivityBytes`.
- A layer projection other than the one a metric implies.
- A rate with no numerator, a rate as its own numerator, and a numerator that is not additive over time —
  `Duration`, `ActiveChannels`, `ActivePeers` and `MappingCapacity`. A numerator on anything but a rate.

## 3. A contribution

A contribution is §19.2's unit of accounting, keyed by

```text
MetricContribution = (basis identity, byte domain, observation side)
```

On the source-observations basis the basis identity is the observation identity of `contracts/identity-v1.md`
§7.2, so a contribution is one row's declared measurement slot. Its **observation side** is the row's
`AccountingSide` column: a fact about the record — which end of the exchange its measurement describes.

| Row side | Written by the normalizer for | Means |
|---|---|---|
| `SendSide` | a `Send` descriptor | measured at the sending end |
| `ReceiveSide` | a `Receive` descriptor | measured at the receiving end |
| `EndpointActivity` | any other descriptor that declares a byte field | measured at an endpoint whose direction the source does not state |

A row never carries `CanonicalOwner`: an owner is chosen per proven transfer association, which is a
correlation result and lives in a relation revision, never in an observation column (R1).

A contribution is **known** when its value is present — zero is an observed zero — and **unknown** when the slot
is declared and the value is absent; an unknown keeps its availability reason and stays in the denominator. A
row whose descriptor declares no byte field contributes to no byte metric and is counted as *no declared
slot*, never as an unknown (R2, R3).

## 4. Accounting

A request's accounting side is a **rule** for building a total from row sides. It is not the same thing as a
row side, and the mapping is fixed here once:

| Request accounting | Takes the rows labelled | A total under it is |
|---|---|---|
| `SendSide` | `SendSide` | sender-accounted: each transfer measured at its sending end |
| `ReceiveSide` | `ReceiveSide` | receiver-accounted: each transfer measured at its receiving end |
| `EndpointActivity` | `SendSide`, `ReceiveSide`, `EndpointActivity` | every measurement at the endpoint that recorded it; a local transfer counts at both ends, by design |
| `CanonicalOwner` | none: one owning contribution per proven transfer association | §5.3's canonical owner |

A byte total is the checked sum of the known contributions it takes, in exactly one domain. Every contribution
in that domain it does not take is **reported, not added**: a result carries a breakdown by row side with each
side's known count, unknown count and sum, and marks which sides the total took. Two domains are never summed;
rows in scope in another domain are counted per domain and named (I6, P3).

The metric names the **direction** of the flow relative to the entity a total is grouped by: `BytesSent` is flow
out of it, `BytesReceived` flow into it. Over a whole session, with no grouping, the direction selects nothing
further — `BytesSent` under `SendSide` and `BytesReceived` under `SendSide` take the same records — and the
accounting alone decides which end measured each transfer. A *cross-side* total, such as `BytesSent` under
`ReceiveSide`, is well defined for a whole session and needs a proven transfer association as soon as it is
attributed to one entity.

`EndpointActivityBytes` is endpoint activity as §5.1 defines it: a process's sent-plus-received total, which
summed across processes counts both endpoints. It is a separate metric and is labelled as one; it is never
compared with a transfer total.

## 5. Scope

A result is scoped by its projection and its interval together. Rows outside the layer or mechanism
projection and rows outside the interval are counted separately and never taken.

An interval is half-open (I3) and is expressed in native ticks of the session's clock. A caller that accepts a
session-relative time converts each bound to the **first native reading at or after it** under exactly the
conversion that produced every stored session instant, so that the native interval holds precisely the
readings whose session instant lies inside the requested one. An interval in native ticks names one clock: a
generation whose segments are on more than one clock refuses an interval-scoped request.

## 6. Rates

A rate is its numerator's count or byte sum **over the interval**, divided by **the whole interval** (§19.2):
never by the span between the first and last observation, and never by a shorter apparently healthy part of
the interval. A rate is kept as the integers it is made of — numerator, its unit, the interval in native ticks
and the clock's ticks per second — and its per-second value is derived from them for presentation only,
rounded half to even to three decimal places (§1.4, §10.5).

A rate with no interval is unavailable, not defaulted. A rate whose clock the session does not describe is
stated per native tick. Until a session publishes a coverage ledger, every rate is an *observed* rate and says
so; no corrected rate exists (§21.1).

## 7. Unavailable is not rejected

A request the matrix refuses **means nothing** and is refused before any session is read. A request the matrix
accepts that a session cannot derive **means something this session cannot answer**, and the result says which,
with no value:

| Reason | When |
|---|---|
| `NoDerivedData` | the generation publishes no derived segment |
| `NoLogicalOperations` | a logical-operations basis, before any correlator derives operations |
| `NoResourceTopology` | a resource-topology basis, before resources and memberships are derived |
| `NoEntityBindings` | `ActiveChannels` or `ActivePeers`, before entity instances are bound |
| `NoStatusDomain` | `Errors`: §7.3 names a status domain §23 assigns no enumeration |
| `NoTransferAssociations` | `CanonicalOwner`, before a correlator proves an association |
| `NoInterval` | a rate with no interval |
| `NothingMeasured` | a byte total that takes no known contribution |

`NothingMeasured` is the rule R21 and P1 require of a byte total. A sum of nothing is not an observed zero:
when no contribution in scope is known — there is no declared slot the accounting takes, or every one is
unknown — the result has no value, states which, and names the other domains records in scope do measure.
An observed zero is a known contribution, and a total of known zeros is zero.

An observation count of zero is a count of records, not a finding that nothing happened: coverage is stated
separately from data (R21), and the result says so.

## 8. Evidence

A result may carry the first records it counted, in segment order, bounded by the request. Each carries its
segment and row — an address inside this generation only — and its observation identity, which survives every
re-read, replay and re-derivation (I1, I2). The bound limits the listing, never the total.

## 9. Reading a generation

A result answers exactly one generation and names it (I16). It is computed under an evidence lease and from
the manifest that lease holds, so neither a retention nor a later commit changes what it reads (I18).

A generation whose segments hold two derivations of one capture is refused: they describe the same evidence
twice, and a total over both would count it twice (I2, R20). Segments whose clock differs from the clock the
generation's journal describes are refused rather than interpreted (I8).

## 10. Scenarios

These are §21.1's scenarios as this contract answers them. The ones marked *owed* need a derivation that does
not exist yet and are answered as unavailable today.

| Scenario | Expected |
|---|---|
| A sends 100 bytes to B; B receives the same transfer | 2 observations. `BytesSent`/`SendSide` 100, one contribution taken, the receive row reported as another side. `BytesReceived`/`ReceiveSide` 100. `EndpointActivityBytes` 200, both taken, labelled as counting both endpoints. `BytesSent`/`ReceiveSide` 100, labelled as measured at the other end. `CanonicalOwner`: `NoTransferAssociations`. Logical basis, 1 call: owed, `NoLogicalOperations`. |
| A requests a 4,096-byte pipe write; a completion reports 1,024 | `RequestedIoBytes` 4,096. `BytesSent`/`CompletedIo` 1,024. `BytesSent`/`TransportObserved`: `NothingMeasured`, naming `RequestedIo` and `CompletedIo` as what was measured. |
| A 10-second window with 100 observations and a 2-second loss | Observed rate 10/s over the whole window, labelled observed; no corrected 12.5/s. The loss interval itself is owed with the coverage ledger. |
| Two processes map one 8 MiB section | Owed: `NoResourceTopology`. |
| PID 400 exits, is reused, and a late event belongs to the earlier start key | Owed: per-process totals need entity bindings (R22). |

## 11. Not defined at this version

- Grouping by entity. `BytesSent` and `BytesReceived` separate from each other only when a total is grouped by
  a process, peer or channel instance, and those are the entity bindings the next revision of this contract
  adds.
- The canonical-owner choice itself (§5.3 rules 1–3), which needs proven transfer associations and the
  correlation-quality ADR.
- Cohorts. `Duration` and the latency distributions of §19.2 need operations; the cohort a distribution
  describes is part of the request when they exist.
- Covered-time rates, which need a source-specific valid exposure duration and a different label.
- Aggregate cells over a boundary set (§10.3). A result here is one total over one scope.
