# ADR-082: The kernel's handle records join a named object's handles by its address, and a ready thread names no object

- Status: accepted for M6's named synchronization objects work package; no source is inventoried; since revision 930 the
  handle group's collection cost is measured, Low in processor time but a handle-heavy workload past §12's throughput
  target, and the object group's Moderate (its addendum)
- Date: 2026-11-05
- Decision owners: InterCat maintainers
- Relates to: plan §4.1, §4.2, M6, P7, P8, R22, ADR-035, FX-SYNC-001, `tools/InterCat.SyncProbe`,
  `bench/results/sync-provider-inspection-20261105T022919Z`, `bench/results/sync-objects-feasibility-*`,
  `bench/results/sync-objects-impact-*`

## Context

The plan's capability matrix expects synchronization to give "supporting waits and correlations", from optional
context-switch and ready-thread tracing, object evidence and later application markers, and warns that a blocked thread
is not necessarily waiting on IPC. §4.2's inspection (revision 928) found no manifest provider for synchronization
objects. The kernel's object manager declares a handle's creation, duplication and close with the object's address, type
and name, rundowns of types and open handles, and an object's creation and deletion. Its dispatcher declares a ready thread
naming the thread readied, and no event records a signal or a wait on an object. Each is a classic kernel class, a flag
group only a system logger receives (ADR-035), and a schema is no evidence of emission.

## Measurement

FX-SYNC-001's workload, `InterCat.TestWorkloads sync-objects`, has a creator process make three named objects in the
session's namespace - an event, a semaphore and a mutex, under a name of the run's own - and an opener process open each
by name. Each round the creator, holding the mutex, sets the event and releases the mutex 5 ms later. The opener waits on
both, then releases the mutex and the semaphore, on which the creator waits. Each process logs every object it created or
opened, every signal it gave and every wait that ended - whether its first look failed - and every handle it closed.

`tools/InterCat.SyncProbe --measure` ran it under a private system logger of its own on build `10.0.26220.0-x64`,
machine-wide. The session's kernel flags were Process, Thread, ContextSwitch and Dispatcher. TraceEvent sets the object
manager's groups only on the NT Kernel Logger, which a capture never uses (ADR-035). So the probe queried its own
session's handle by name and added `PERF_OB_HANDLE`, and in one run `PERF_OB_OBJECT`, to its group masks with
`TraceSetInformation`. Four runs of 64 rounds each, three with the handle group and one with both groups:

| Quantity | Result |
|---|---|
| Each process's handle to each object, created and closed once | every one, each run: 3 objects, 2 processes |
| Both processes' handles to an object at one address | every object, each run |
| A handle's creation naming its object | none: of about 68,000 creations a run, machine-wide, none carried a name |
| A handle's close naming its object | every close of the run's objects, as `\Sessions\<n>\BaseNamedObjects\<run>-<kind>` |
| An object's type | Event 21, Semaphore 24, Mutant 22, named by the type rundown at the session's stop |
| Duplicates of the run's objects, or handles to them elsewhere | none |
| With the object group, each object's creation and deletion | created in the creator 17 to 40 µs before its first handle, deleted 1 to 2 µs after its last close, at its handles' address |
| The object group's references | about 2.2 million references and 2.3 million dereferences in a 4.5-second session |
| Waits whose first look failed | 179 to 187 a run, of 256 |
| Of them, waits the kernel blocked: the waiter's thread switched out in a user-mode wait inside the wait's span | 174 to 183 a run |
| Waits the kernel blocked with the ready thread of their thread from the signaling process | every one, 21 µs before to 3 µs after the signal's truth record |
| Ready threads of a thread in a user-mode wait, between the two processes, that pair no wait | none |
| Ready threads between the two processes at their exit | 67 to 93 a run within 11 ms after their last truth records, of threads in the kernel's own waits or in none seen, pairing no signal |
| Lost | nothing, in any run |

The kernel's clock is set against the workload's by each handle's records: a process creates, opens or closes a handle in
a call whose truth record it writes once the call returns, and the median distance is a few microseconds. A blocked wait
pairs with the ready thread nearest its signal's record, from the signaling process to the waiter's, inside the span the
waiter's log bounds it in.

1. **The handle group records each process's handles to a named object at one address.** A creation carries no name. The
   close carries the object's full name, read by the layout the machine registers, which TraceEvent does not read. So a
   handle is named by its close, joined to its creation by process, handle and address, and a handle still open when the
   capture stops is named only by the handle rundown, 283,000 handles at each stop here.
2. **The object group bounds an address to one object.** Each object's creation and deletion are recorded by address and
   type, so an address names one object only between them. Its references come with it, about a million records a second
   machine-wide.
3. **A ready thread names no object, and is no signal by itself.** Its record names neither the object nor the thread that
   readied it. Set against the context switches, its readying thread is the one its processor last switched to, and every
   wait the kernel blocked had the ready thread of its thread from the signaling process within microseconds of the
   signal. A wait whose first look failed but which the kernel satisfied as it began waiting had none, as it had no thread
   to ready. The two processes' threads also readied each other dozens of times at their exit, in the kernel's own waits,
   with no object signaled.
4. **A private system logger takes the object manager's groups.** Its group masks are set by its handle, and the session
   delivered both groups with nothing lost. The NT Kernel Logger is not needed.

## Decision

1. The object manager's handle group is the lead for named synchronization objects: which processes hold a handle to which
   named object, from the handle's creation to its close. It is not inventoried yet: its collection's cost is unmeasured,
   and the product sets no group mask.
2. A named object is one object by its address within its lifetime, never by its name or its address alone (R22). Two
   processes' handles to one name are one object only at one address inside that lifetime (P7).
3. A ready thread is never a signal, a wait or a relation by itself (P8). It names neither the object nor the thread that
   readied it, and threads of two processes ready each other with no object between them. It can support a wait's
   observation only beside a thread switched out in a user-mode wait, and never names the object waited on.
4. Before any profile admits the handle group, the work package needs: its collection's cost, how the capture sets a group
   mask on its private system logger, and what a handle opened before a capture began records.

## Consequences

- FX-SYNC-001 is indexed for its evidence, with no tier. Its committed ledgers hold the first run's records and the object
  run's, by role, with object, handle and thread ordinals, never a PID, a thread id, a handle value or an address.
- Tests hold the ledgers to the truth: each process's handles joined to one object by its address, each named only at its
  close, with its type; each object created before its first handle and deleted after its last close; and every wait the
  kernel blocked paired with the ready thread of its thread from the signaling process. Six alterations of the ledgers are
  caught.

## Addendum: what collecting the object manager's groups costs (revision 930)

The fourth decision asked for the handle group's collection cost. `tools/InterCat.SyncProbe --measure --impact` ran a
handle-heavy workload - FX-SYNC-001 with `--churn 32768`, its opener opening and closing each object by its name 32,768
times, then its rounds unpaced, about 98,000 handles in two seconds - in alternating pairs, once with no session and once
while a private system logger of its own collected the handle group machine-wide, and in a third series the object group
beside it. The session starts with the kernel's process flag, the least a system logger takes, and its consumer only
counts the object manager's records, as COM-Perf's and AFD's costs were measured (ADR-077's and ADR-072's addenda).
Nine pairs a series, nothing lost:

| Series | Groups | Median CPU impact | Workload slower by | Throughput regression | Records a second |
|---|---|---|---|---|---|
| `bench/results/sync-objects-impact-handles-20261105T035540Z` | handle | 0.4 pp: Low | 5.6% | 5.3% | about 155,000 |
| `bench/results/sync-objects-impact-handles-20261105T035857Z` | handle | 0.0 pp: Low | 9.1% | 8.3% | about 180,000 |
| `bench/results/sync-objects-impact-objects-20261105T035712Z` | handle and object | 1.9 pp: Moderate | 31.0% | 23.7% | about 2.7 million |

A handle's creation and close each raise a record in the thread that makes the call, the close with the object's name, so
the time is the call's own: a workload opening and closing handles as fast as it can is slower in proportion to its handle
rate. The machine ran a virtual machine beside the measurement, whose load varied: two pairs of the second series ran with
the machine 36 to 40% busy, and both series' medians still put the handle group past §12's 5% throughput target and well
within its processor time. The object group's references come with it, every reference taken on any object on the
machine, and they cost a Moderate processor time and a fifth of the workload's throughput.

1. The handle group's class is Low, stated beside the regression a handle-heavy workload measured, and only an opt-in
   profile saying so may admit it; Explore never does.
2. The object group is Moderate and slows a handle-heavy workload by a fifth and more, its references dwarfing the
   lifetimes it records, so an object's lifetime is not collected by it in any profile without its own measurement.
3. What remains before a profile admits the handle group: how the capture sets a group mask on its private system logger,
   and what a handle opened before a capture began records.

A test holds the three series to their class and regression; five alterations of them are caught.
