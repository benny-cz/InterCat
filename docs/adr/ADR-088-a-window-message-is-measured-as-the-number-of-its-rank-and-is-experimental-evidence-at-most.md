# ADR-088: A window message is measured as the number of its rank, and is experimental evidence at most

- Status: accepted for M6's UI messaging work package; no fixture is measured through the product yet
- Date: 2026-11-05
- Decision owners: InterCat maintainers
- Relates to: ADR-080, ADR-084, ADR-086, ADR-087, §14.2, R3, R22, P1, P27, FX-WMSG-001, `CoverageTierCalculator`,
  `WindowMessageCoverageEvaluator`, `ComCallCoverageEvaluator`, `SyncObjectCoverageEvaluator`

## Context

The opt-in window messages profile records Win32k's message pump through the window message gate, each message numbered in
the capture and never by its pointer (ADR-087), each end bound to the process that raised it (its second addendum). No build
carries a tier for window messages: nothing has been measured against a truth through the product.

§14.2 computes a tier from fixture counters. Traffic visualization needs at least 95% of truth operations observed, 90%
bound to a resource instance, at most 1% false peer attributions and 90% with a byte measurement. Topology needs at least
95% of truth resources discovered with their lifetimes, every membership resolved, and no claim of bytes or operations. A
window message record names neither which message it is - every record of FX-WMSG-001's processes carried zero for its
message number and parameters (ADR-086) - nor the window it went to, nor a size.

## Decision

1. **A truth message is the number of its rank.** Nothing in a record names which message it is, so
   `WindowMessageCoverageEvaluator` takes the sender's sends, in its log's order, as the capture's sent messages from the
   sender to the receiver - each a number whose start the sender raised naming the receiver - in the order of their first
   record; and its posts, the close last, as its posted ones - each a number the sender queued that no other process alone
   retrieved. The sender sends and posts one message after another, and a session delivers records in the order raised
   (ADR-086's third addendum), so the two orders are one. Where the capture holds more or fewer messages of a kind than the
   truth logged, no rank says which is which, and none of that kind is matched.
2. **It is observed with every end where its number has each end in its own process:** a send's start and stop in the sender
   and its retrieval's start and stop in the receiver; a post's queueing in the sender and its retrieval in the receiver.
3. **Its peer is the process at its other end, false unless it is the receiver.** A send's start names it and its retrieval
   binds to it; a post's retrieval binds to it. A number whose ends name a process the truth says did not receive the
   message is a false attribution, as a number joining two messages would be.
4. **It is bound to no resource, and claims operations without bytes.** No record names the window - its handle is not
   admitted - so no message is bound to a resource instance, and the window, with no record of its own, has no lifetime to
   show. No record carries a size, so no message is byte eligible and the byte criterion is not measured, never a measured
   zero (R3, P1). Window messages claim operations, so they reach experimental evidence at most (P27), as COM calls do
   (ADR-080).
5. **Records of processes the truth does not name are scope**, and so is a message the sender made to another process's
   window.

## Consequences

- A reproduced measurement with its messages observed carries experimental evidence for window messages on its build; the
  tier calculator decides it from the counters.
- The evaluator is pure and portable; a test runs it on committed evidence once a fixture is measured through the product
  (R19, R18).
- Tests hold it on synthetic captures: a whole run is experimental evidence and an unreproduced one reaches no tier; a missing
  end and a stranger's retrieval are said; a count that differs matches none; other windows and processes are scope; and a
  capture of none of the truth's messages observes none. Nine mutations are caught.
