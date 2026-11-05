# ADR-084: A named synchronization object is measured as the one number its closes name, and is topology at most

- Status: accepted; FX-SYNC-001 measured through the product, topology only on 10.0.26220.0-x64 (addendum)
- Date: 2026-11-05
- Decision owners: InterCat maintainers
- Relates to: ADR-082, ADR-083, §14.2, R3, P1, P7, P27, FX-SYNC-001, `CoverageTierCalculator`, `SyncObjectCoverageEvaluator`,
  `UnixSocketCoverageEvaluator`, `ComCallCoverageEvaluator`

## Context

The synchronization objects profile records the object manager's handles of every event, mutant and semaphore through the
synchronization handle gate, each object numbered in the capture and never by its address (ADR-083), each handle's
creation and close bound to the process that raised it (its third addendum). No build carries a tier for named
synchronization objects: nothing has been measured against a truth through the product.

§14.2 computes a tier from fixture counters. Traffic visualization needs at least 95% of truth operations observed, 90%
bound to a resource instance, at most 1% false peer attributions and 90% with a byte measurement. Topology needs at least
95% of truth resources discovered with their lifetimes, every membership resolved, and no claim of bytes or operations.
A named object's handle records carry no size, and no record names a signal or a wait on an object (ADR-082): what they
show is which processes share an object, from its first handle to its last close.

## Decision

1. **A truth object is the one number its closes name.** A creation carries no name and a close does (ADR-082), so
   `SyncObjectCoverageEvaluator` finds each object the truth logged by name as the number of the capture's closes whose
   kernel name names it: the whole name after its namespace, `Local\` against `\Sessions\<n>\BaseNamedObjects\`. Closes
   naming it under two numbers - a handle made before the capture, numbered alone - make no one number the object.
2. **It is discovered with its lifetime where that number has its creation and every close the truth logged.** The
   creator's creation of its first handle, and each close the truth logged, each in the process that logged it, must be
   among the number's records; an object cut by the capture, its creation or a close unseen, is not discovered.
3. **Its members are exactly the truth's.** The processes the number's records bind to are its members. Each member the
   truth says held it resolves a membership; one the truth says never held it is a false membership, counted as a false
   peer attribution, and the object is not discovered: an object is never claimed shared with a process it was not.
4. **Its handle opens and closes are the truth's operations, and it claims neither bytes nor operations.** Each open and
   close the truth logged, seen in its own process under its object's number, is observed and bound to the object. A
   signal or a wait is in no record, so none is an operation here; no record carries a size, so no operation is byte
   eligible and the byte criterion is not measured, never a measured zero (R3, P1). The handle group shows topology, so a
   measurement claims no bytes or operations and the mechanism reaches topology at most.
5. **Records of processes the truth does not name are scope**, which a machine-wide capture sees, not error.

## Consequences

- With every object discovered and every membership resolved, a measured build carries topology only for named
  synchronization objects; a false membership or a cut object holds it at experimental evidence.
- The evaluator is pure and portable; a test runs it on committed evidence once a fixture is measured through the
  product (R19, R18).
- Tests hold it on synthetic captures: a whole run is topology only, a stranger bound to an object is a false membership,
  an object of two numbers or with its creation or a close unseen is not discovered, and a capture of none of the truth's
  processes is unsupported; seven mutations are caught.

## Addendum: FX-SYNC-001 through the product (2026-11-05)

`InterCat.SyncProbe --session` reads a session `icat record` recorded under the opt-in synchronization objects profile as the
product holds it: each handle record's process under `process-binding-v6`, the number the gate wrote over its object's
address, and its name. It writes the ledger of the workload's records, and of other processes' on objects the workload's
processes held, by role - never a PID, a handle value, an address or another object's name - and evaluates it as decided
here.

- In four runs, machine-wide and loss-free - twice paced as the fixture is, once unpaced over 256 rounds, and once with the
  opener opening and closing each object 16 more times - each of the run's three objects was one number from the creator's
  creation of its first handle to every close the truth logged, each in its own process, its closes naming it under the
  session's namespace, and bound to exactly the creator and the opener: 12 of 12 handle opens and closes a run, 108 of 108
  churned, no membership false.
- Other processes' records on objects the workload's processes also held - an unnamed object whose handles they only
  closed, and a named object dozens of processes hold, mostly in the stop's rundown - named none of the run's objects, so
  they are scope (decision 5).
- Named synchronization objects are topology only on 10.0.26220.0-x64: the build's per-build coverage carries the tier from
  FX-SYNC-001, and a test reproduces it from the first run's committed ledger; eight alterations of that evidence are caught.
