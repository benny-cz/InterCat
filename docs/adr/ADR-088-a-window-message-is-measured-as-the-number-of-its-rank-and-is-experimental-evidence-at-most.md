# ADR-088: A window message is measured as the number of its rank, and is experimental evidence at most

- Status: accepted for M6's UI messaging work package; FX-WMSG-001 measured through the product, experimental evidence on
  10.0.26220.0-x64 (addendum); FX-WMSG-002, windows made one after another, its second instance and lifetime fixture (second
  addendum); FX-WMSG-003, messages a capture cuts, its ambiguity case (third addendum)
- Date: 2026-11-05
- Decision owners: InterCat maintainers
- Relates to: ADR-080, ADR-084, ADR-086, ADR-087, §14.2, R3, R22, P1, P27, FX-WMSG-001, FX-WMSG-002, FX-WMSG-003, `CoverageTierCalculator`,
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

## Addendum: FX-WMSG-001 through the product (revision 964)

`InterCat.UiMessageProbe --measure --product` records FX-WMSG-001 by the product's own `icat record` under the opt-in
window messages profile, the workload started once the session runs, and reads the session it published as the product
holds it: each window message record's process under `process-binding-v7`, the number the gate wrote over its pointer, and
the process a send's start names. It writes the ledger of the workload's records, and of other processes' on the
workload's messages, by role - never a PID, a window, a pointer or a message - evaluates it as decided here, and deletes
the session, which holds every window's messages on the machine.

- In three runs of 32 sends and 32 posts, machine-wide and loss-free - 37,367, 40,677, 37,627
  records admitted, none dropped and none a gate omitted - each of the run's 65 messages was the number of its rank among
  the capture's messages of its kind from the sender to the receiver, with every end in its own process and its other end
  the receiver: 65 of 65 a run, no false attribution.
- Other processes' window messages, tens of thousands a run, never shared a number with the workload's, so none is in
  the ledger.
- Window messages are experimental evidence on 10.0.26220.0-x64: the build's per-build coverage carries the tier from
  FX-WMSG-001, and a test reproduces it from the first run's committed ledger; nine alterations of that evidence and the
  runs' report are caught.

## Addendum: FX-WMSG-002, windows made one after another (revision 965)

FX-WMSG-002 is FX-WMSG-001's sender and receiver over 16 message-only windows of the run's own class, made one after
another: the receiver makes each under a name of its own once the last is destroyed, and the sender finds it by its class
and name, sends it 4 WM_COPYDATA messages, posts it 4 numbered messages and last its close, its messages numbered on from
the last window's. The receiver's one queue carries every window's messages in turn, and no record names a window.

- The sender posts each window's close after that window's posts and before the next window's messages, so its posts and
  closes are one order: `WindowMessageCoverageEvaluator` ranks them together in the order the sender logged them, as
  decision 1 ranks a run of one window, its close last. The windows are the truth's resources, by its name for each: none
  has a record of its own, so none is discovered, and each one's two ends are memberships, resolved where a message to it
  was observed at that end.
- `InterCat.UiMessageProbe --measure --product --generations 16` recorded it three times by `icat record` under the opt-in
  window messages profile, machine-wide and loss-free - 37,730, 38,724, 41,831 records admitted, none dropped
  and none a gate omitted. Each of the run's 144 messages - 64 sends, 64 posts and the 16 closes - was the number of its
  rank with every end in its own process and its other end the receiver, 144 of 144 a run, no false attribution. Every
  window's nine messages were numbers of their own before the next window's, its close the last of them, and each window's
  two ends were resolved by its own messages, 32 of 32.
- So a window made after another's close is messaged as the first was, its messages its own: window messages stay
  experimental evidence on 10.0.26220.0-x64, the build's per-build coverage carrying the tier from FX-WMSG-002, its latest
  fixture. A test reproduces it from the first run's committed ledger, and eleven alterations of that evidence and the runs'
  report are caught. FX-WMSG-002 is the package's second instance and lifetime fixture.

## Addendum: messages a capture cuts (revision 966)

M6's exit asks each mechanism for an ambiguity case: evidence that cannot decide, stated so rather than resolved by a
guess. A capture begun or ended while window messages are in flight holds only part of them. A message begun before it is
in the capture by its ends after the start, which the gate numbers from the first it sees (ADR-087) and which name no truth
message; one begun inside but ended after it holds its start alone. Decision 1 ranked every truth message of a kind
against the capture's messages of that kind, so one cut message made the counts differ and matched none of its kind.

- `WindowMessageCoverageEvaluator` now holds the truth to the capture's window: its first window message record to its last,
  on the wall clock its calibration reads - every window on the machine raises records, so they run from the capture's
  start to its stop. A truth message begins at its first truth record, the receiver's receipt of a sent one or the
  sender's queueing of a posted one, and one begun outside the window is said so and ranked with none. A number of the
  truth's processes with ends but neither a send's start nor a post's queueing was begun before the capture, counted so,
  and names no truth message. A message begun inside and ended after it is ranked as before, its gap naming each end it
  lacks.
- FX-WMSG-003 is FX-WMSG-001 with a receiver that takes 300 ms over each of 20 copies and 20 posted messages:
  `InterCat.UiMessageProbe --measure --product --handle-ms 300` starts `icat record` two seconds into the run for six
  seconds, so its start falls while a copy is in flight and its stop while posted messages wait their turn. Three runs,
  loss-free - 18,546, 18,311, 16,899 records admitted, none dropped and none a gate omitted, windows of
  6,217, 6,231, 6,223 ms:
  - the copy in flight at the start was one number with its retrieval's stop in the receiver and its stop in the sender
    alone, begun before the capture and naming no truth message, and it and the copies before it, 8, 7 and 7 a run, were
    begun before the capture's first record and matched to none;
  - every copy after it was the number of its rank with every end, and so was every post the capture held to its
    retrieval, 20 a run; every post and the close its stop cut, 13, 14 and 14, was the number of its rank lacking its
    retrieval alone;
  - no attribution was false.
- It measures what a cut message is said to be; window messages' tier is FX-WMSG-002's. A synthetic test holds the
  window's rule, the first run's ledger is committed under `fixtures/FX-WMSG-003/evidence/` with its capture's window, and
  eight mutations of the evaluator and nine alterations of that evidence and the runs' report are caught. FX-WMSG-003 is the
  package's ambiguity case.
