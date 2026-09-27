# ADR-032: An operation is counted by the record that puts it in scope

- Status: accepted for M3
- Date: 2026-09-27
- Decision owners: InterCat maintainers
- Relates to: §5 (operations started and completed), §5.3 (the matrix), §19.1, §19.2, §21.1, §24 (`correlationRevision`),
  R3, R21, P4, P7, ADR-012, ADR-015, ADR-031, `contracts/metrics-v1.md` §8a, `contracts/operations-v1.md`

## Context

§5.3's matrix has defined `OperationsStarted`, `OperationsCompleted` and `Errors` on a logical-operations basis since
the first metric contract, and every session answered them as unavailable, because no correlator derived an
operation. ADR-031 derives the first: an RPC call, its start and its stop paired by activity id on one side of one
process. §5 defines the counts as "distinct logical operations with qualifying observed start/completion evidence;
never raw ETW event count renamed as calls", and §21.1 fixes one case: an operation from 0.5 s to 2.5 s counts as
started in [0, 1) and completed in [2, 3).

A call's two records can fall in different intervals, and a call can have one record only: a start still open at
capture end, a stop whose start came before the capture, a record with no activity id, or a record of an id reused
before its stop. Each of those raises the question of what a count takes, and what it says about the rest.

## Decision

1. **The record that puts a call in scope.** A started count takes a call when its start's reading is in scope, and a
   completed or failed count when its stop's reading is. The interval and the process filter decide scope as they do
   for records; a call is outside the interval when that record is.
2. **What each count takes.**
   - `OperationsStarted`: every call with its start in scope, however it ended: completed, open at capture end, with
     no activity id or of a reused id. Every start is exactly one call on its side (FX-RPC-001).
   - `OperationsCompleted`: a call whose stop is in scope and paired with its start. That is `operations-v1`'s
     completed call, the call `icat operations` and the ladder count as completed, and the cohort a duration has.
   - `Errors`: a completed call whose stop reports a status other than 0. A completed call whose stop carried no
     status is an unknown contribution, neither failed nor succeeded, and an error count whose every completed call is
     unknown is `NothingMeasured`, never zero (R3).
3. **What each count states.** A stop in scope that no start is paired with (`StartNotObserved`, `NoActivityId`,
   `Ambiguous`) is stated with its reason and not counted, and so is how many of those stops report a failure status.
   A result carries every call in scope by state, so a count is read beside what it left out.
4. **Contributions.** On this basis a contribution is an operation, identified by its call's identity. A process
   group, a remainder and the unattributed groups partition a count exactly as they partition a source total.
5. **Filters and groupings.** `owner(P)` keeps the calls bound to P under the evidence policy, by the call's first
   record. A filter or grouping that needs an operation's other end - `participant`, `sender`, `receiver`, `peer`,
   `between` or grouping by peer - is `NoLogicalOperations`: no rule pairs a client call with the server call that
   served it (P7). So are a count of channels or peers, `Duration` (a request cannot name a cohort), `Errors` with an
   accounting side (a call is made at a client and served at a server, not sent or received), and a mechanism or
   layer projection no correlator derives operations for. A byte total is `NothingMeasured`: no call carries a length.
6. **Zero.** A count with no call in scope is 0 with that stated, as an observation count is: coverage is stated
   separately. Every operation is an RPC call, so an answer that names no mechanism states RPC's capture coverage.
7. **Identity.** An answer on this basis names the operation rule as its `correlationRevision` and the binding rule as
   its `entityRevision`, because the calls are joined within each process under that rule. At this version it reads no
   relation, so the axis holds one rule.

## Consequences

- `icat metric --basis logical-operations` answers three counts and their rates, over an interval, for one owner,
  ungrouped or grouped by process, executable or mechanism, with the counted records as evidence.
- The source records of one exchange are still never an operation: a TCP transfer seen at both ends gives 0 calls on
  this basis, and a TCP projection is unavailable rather than zero.
- The canonical corpus gains three lines under canonicalization version 1; no existing line changes.
- Duration distributions wait on a cohort member of the request, and an operation's other end on a relation rule
  between the two sides of a call.

## Alternatives considered

- **Count a stop without its start as a completion.** Rejected. The call did end, but it is not complete in the
  evidence, it has no duration, and the count would disagree with `icat operations` and the ladder, which count it
  apart. It is stated with its reason and its status instead.
- **Count a call in an interval when either record is in it.** Rejected. §21.1 counts a start where the start is and a
  completion where the stop is; counting a call in every interval it touches is the overlap count, a different metric.
- **Keep the basis unavailable until a second mechanism has a correlator.** Rejected. RPC calls are measured and
  derived; withholding a count they answer exactly would leave the ladder's numbers unreachable from a metric request.
