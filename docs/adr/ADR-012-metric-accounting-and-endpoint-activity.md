# ADR-012: Metric accounting, and endpoint activity as a metric of its own

- Status: accepted for M1
- Date: 2026-09-22
- Decision owners: InterCat maintainers
- Relates to: §5 (measurement semantics), §5.1 (double counting), §5.3 (metric compatibility), §19.1 (query
  basis), §19.2 (accounting and rate rules), §21.1 (scenarios), §23 (`EN-Metric`, `EN-AccountingSide`),
  ADR-011 (where a contribution is decided), `contracts/metrics-v1.md`

## Context

IC-015 turns §5.3's matrix into a compiler and answers metrics over a published session. Doing it exposed four
places where the plan, read literally, could not be implemented without either an inconsistency or a guess.

**Endpoint activity was two things at once.** §5.1 defines it as "a process's sent-plus-received total", §5.2
offers "total endpoint bytes" as a ranking beside sent and received bytes, §5.3 calls it "a separate, explicitly
labeled metric that counts both sides by design" and §22 calls it a metric. But `EN-Metric` had no code for it,
and `EN-AccountingSide` did: code 3, `EndpointActivity`, valid by the matrix on `BytesSent` and `BytesReceived`.
Read that way, `BytesSent` accounted as endpoint activity either contradicts its own name — sent bytes that also
count received ones — or means "each endpoint's own sends", which is exactly sender accounting under another
name. Neither reading produces the 200 bytes §21.1's first scenario expects from one metric.

**A row side and a request's accounting are different facts in one enumeration.** A segment row's
`AccountingSide` column records which end of an exchange its measurement describes; a request's accounting side
is a rule for building a total. An earlier draft of this slice passed the request's side straight through as a
row filter, so an endpoint-activity request summed only the rows whose descriptor names no direction — on the
measured TCP corpus, the 69 connect, accept and disconnect records whose size is zero — and reported 0 B of
endpoint activity for a session holding 427,728 B of it.

**The domain rule admitted relabelling.** "Required, exactly one" let `BytesSent` be asked for in `RequestedIo`,
which is a requested length reported as sent bytes: P3's own example, reachable through the matrix.

**Unanswerable and meaningless were conflated.** A canonical-owner total is a well-formed request whose answer
needs a proven transfer association. Rejecting it as invalid would tell a caller the request is wrong when it is
the session that cannot answer yet.

## Decision

### 1. Endpoint activity is `EN-Metric` 15, `EndpointActivityBytes`

It is every byte measurement at the endpoint that recorded it, sent and received alike, in one traffic domain,
and its accounting side is fixed to `EndpointActivity`. `BytesSent` and `BytesReceived` refuse the
`EndpointActivity` side and name the new metric. The code is additive: nothing is renumbered, and
`EN-AccountingSide` 3 keeps its meaning as the fixed side of this metric and as the row label for a measurement
whose descriptor names no direction.

Metrics whose names state no direction — `RequestedIoBytes`, `ApplicationPayloadBytes`,
`CapturedContentBytes` — keep all four sides. Every requested length at every endpoint is a meaningful total and
has only one spelling, because those metrics fix their own domain.

### 2. The request's accounting maps to row sides in one place

`SendSide` takes send-side rows, `ReceiveSide` takes receive-side rows, and `EndpointActivity` takes all three
row labels. `CanonicalOwner` takes no row label at all: an owner is chosen per association and is a relation, so
a row never carries it (R1). The mapping lives in the metric layer, beside the matrix, because it is §5.3's
semantics; the storage layer measures every row side of one domain in one pass and keeps them apart, and the
metric layer decides which it takes. Every side it does not take stays in the result's breakdown.

### 3. A traffic metric takes a traffic domain

`BytesSent`, `BytesReceived` and `EndpointActivityBytes` accept `TransportObserved` or `CompletedIo`. A domain
another metric owns is refused with that metric named, so `RequestedIo` points at `RequestedIoBytes` and
`Capacity` at `MappingCapacity`.

### 4. The matrix decides meaning; the session decides availability

A matrix refusal is returned before any session is opened. A permitted request a session cannot answer returns a
result with no value and a named reason: no operations, no topology, no entity bindings, no status domain, no
transfer association, no interval, or nothing measured. `NothingMeasured` is how R21 applies to a byte total: a
sum that takes no known contribution has no value, rather than a value of zero, and names the domains the records
in scope do measure.

### 5. A rate is its integers

A rate's numerator is scoped to the interval, its denominator is the whole interval, and it is kept as
numerator, unit, interval ticks and ticks per second. The per-second figure is derived for presentation. An
earlier draft counted every row in the session and divided by the interval, so a rate over an interval
holding no record at all came out as ten per tick; scoping the numerator is what makes the denominator mean
anything.

## Consequences

- `contracts/metrics-v1.md` freezes the source-observations basis. `icat metric` answers from it, and a request
  that means nothing, one that cannot be answered, and one that can are three exit codes: 2, 3 and 0.
- On the measured 555-record corpus, sender-accounted transport bytes are 173,258 B over 163 contributions,
  receiver-accounted 254,470 B over 253, and endpoint activity 427,728 B over 485 — the first two plus the 69
  undirected records that contribute zero. The two one-sided totals are not equal, because a whole-machine
  capture sees transfers whose other end is elsewhere; that is why a total names its side.
- A whole-session total cannot yet be grouped by process. `BytesSent` and `BytesReceived` separate from each
  other only under grouping, and grouping needs process instances rather than process ids (R22). That is the
  next part of IC-015.
- Plan revision 28 records the §5.3, §23 and glossary changes, and §5's table gains the endpoint-activity row.
