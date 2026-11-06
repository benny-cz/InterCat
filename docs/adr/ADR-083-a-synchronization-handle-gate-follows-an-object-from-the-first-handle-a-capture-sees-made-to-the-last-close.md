# ADR-083: A synchronization handle gate follows an object from the first handle a capture sees made to the last close

- Status: accepted for M6's named synchronization objects work package; since revision 936 the handle group's events
  admit through the gate (its addendum); since revision 938 a duplication is the open of the target its record names, and
  the gate follows no kernel handle (its second addendum); since revision 939 a handle's creation and close bind to the
  process that raised them, as `process-binding-v6` (its third addendum); since revision 940 the opt-in synchronization
  objects profile requests the group, stating its cost (its fourth addendum); its duplication, compiled at version 2,
  admits no record on this build, which raises every duplication at version 3 (ADR-082's sixth addendum); since revision
  943 the profile's cost is measured through the product, High on a handle-heavy workload (its fifth addendum); since
  revision 949 a capture's compilation refuses the group where its gate could not decide its records (its sixth addendum);
  since revision 974 its algorithm is `ObjectHandleGate`, which the section handle gate shares (ADR-090)
- Date: 2026-11-05
- Decision owners: InterCat maintainers
- Relates to: ADR-073, ADR-082, plan M6, §4.2, R22, P7, FX-SYNC-001, `bench/results/sync-objects-types-20261105T053215Z`

## Context

ADR-082 made the object manager's handle group the lead for named synchronization objects. A capture can now set its mask
(ADR-082's third addendum), but the group delivers every object type's handles, about 15,000 records a second on this
workstation, and an event's, a mutant's or a semaphore's are about one in thirty. It names an object only by its kernel
address, which the kernel reuses once the object is gone. A handle's creation carries no name; its close does. A capture
admits records one at a time as they arrive (ADR-073), so admitting the group whole would journal every handle of the
machine to keep a thirtieth of them, and joining by address alone would merge objects that only shared an address (R22).
A record names its type by a number, and the object type table read from user mode as a capture starts names each number
as the session's type rundown at its stop does (ADR-082's fourth addendum).

## Decision

1. **A gate decides, as records arrive.** `SynchronizationHandleGate` reads each handle record - a creation, a close, a
   duplication or the stop's rundown of a handle still open - and admits only those of the synchronization types, Event,
   Mutant and Semaphore, by the numbers measured for the capture's build: the probe's object type table and the session's
   type rundown at its stop named them alike (ADR-082's fourth addendum). No product code reads the type table, as the
   query that reads it is the one that names a duplicated handle's object, which P20's fitness check keeps out of the
   product; each capture instead holds the build's numbers to its own type rundown, and a build none measured has no gate.
   Every other type's record is omitted as of no object it follows.
2. **An object is followed from the first handle the capture sees made at its address to the close of the last.** A
   creation or a duplication at an address no followed object holds begins an object there: whatever the address held
   before ended, seen or not. Each handle the gate sees made to it - created by any process, or duplicated into a target
   process - is held until its close, and the close of the last ends the object, so the next handle made at the address is
   another's. Each object is numbered in the capture, never named by its address.
3. **A handle made before the capture is numbered alone.** A close, or the stop's rundown, of a handle at an address no
   followed object holds is of a handle made before the capture began. Its object's other handles are unseen, so nothing
   says it is one object with any other record at its address, and two such handles closed at one address are two numbers,
   never joined by their shared address or name (P7). A followed object's handle in the rundown is its own.
4. **Bounded.** The gate follows at most 65,536 objects, saying of a handle that would begin one beyond them that it was
   not followed; it holds back no record.
5. **What this does not decide.** No profile compiles the gate yet. Wiring it needs the handle group's admitted events and
   fields - a handle's creation, close and duplication and the stop's rundowns, never an address - the records bound to the
   process whose handle each is, normalized as synchronization observations by the gate's numbers, the build's numbers held
   to the type rundown in each capture, and an opt-in profile, as collecting the group is past §12's throughput target
   (ADR-082's addendum). Whether two handles made before a capture are one object is FX-SYNC's ambiguity case to measure.

## Consequences

- Tests hold the gate on synthetic records - an address reused after its last close, two handles made before the capture
  closed at one address, a duplication's handle held by its target, the stop's rundown of a followed handle and of one made
  before, another type, the bound - and on FX-SYNC-001's ledger, read through it as it was delivered: three objects, each
  created by the creator, opened by the opener and closed by both, numbered apart. A test holds the build's numbers to the
  probe's measurement, and a build none measured to no gate. Ten mutations of the gate are caught.

## Addendum: what the handle group admits through the gate (revision 936)

The fifth decision left the group's admitted events and fields to wire. The handle group now admits five events of the
object manager's class at version 2, each compiled from the layout the machine registers for it and told apart by its
opcode, all of them synchronization records of the resource layer:

- a handle's creation (opcode 32), an open: its object, its handle and its type;
- its close (33): those and the object's name, which only a close and the stop's rundown carry;
- a duplication (34), an open in its target process: its object, the source's handle, the handle it made, that process
  as the record's other end, and the type;
- the type rundown at the stop (37), an inventory: a type's number and name, by which a capture holds the build's
  measured numbers to its session;
- the handle rundown at the stop (39), an inventory: a handle still open, the process holding it as its record names it,
  the handle, the type and the name.

Four source fields name what fits no observation column: `SynchronizationObjectNumber`, `ObjectTypeNumber`, `HandleValue`
and `PeerHandleValue`, the last a duplication's handle in its target. The object's field is admitted only for the gate to
read, and the gate writes the object's number over it, so no admitted record keeps an address (R22). The gate admits
only the synchronization types' records and the type rundown's entries of theirs, and omits every other as of no object
it follows, or beyond its bound. A build with no measured type numbers refuses the source, as does a schema without a
field the gate decides by: a creation's handle, a duplication's target, a rundown's process (R3). A classic class's
deciding fields are now checked per opcode, its events sharing event id 0. No profile requests the group yet.

Wired to its source, the gate reads its records on the capture's callback, which allocates nothing per record (P12), where
it had made a set of handles on the heap for each object it began. A followed object is now a value in one table, with how
many of the handles the gate holds are its, and each handle it holds an entry in another, keyed by the object's address,
the process and the handle. The fourth decision's bound holds the handles too: the gate holds at most 65,536, and so
follows at most as many objects. A further handle of a followed object beyond it is its object's but is not held, so the
object may end before that handle's close, which is then numbered alone: one object split in two, never two joined.

Tests hold the layout TDH describes on this machine, the compiled slots, the gate's admissions and omissions on compiled
records, and the refusals; the gate's callback path reached and allocating only where reviewed; and a thousand objects
begun and ended again through warmed tables allocating nothing. Nine mutations of the admission and six of the gate's
tables are caught.

## Addendum: a duplication is its target's open, and no kernel handle is followed (revision 938)

FX-SYNC-002 measured where the object manager raises a handle's records (ADR-082's fifth addendum). A duplication is
raised in the process that asked for it - its source, its target or a third process - and its record names the process
it made a handle in and that handle, but no source process. So the handle group's duplication is now an open coming
into the process its record names: `TargetProcessId` is its payload owner, as a rundown's `ProcessId` is; the handle it
made is its `HandleValue`, which its owner closes; and the handle it was duplicated from is its `PeerHandleValue`, a
value in a process no record names. Its header, which names the process that asked, owns nothing.

A kernel handle, its value's top bit set, is held in the system process's table, but FX-SYNC-002's were created and
closed in other processes' contexts, never the system process's, and only the stop's rundown named its holder. Bound by
its header, a kernel handle would be a process's it never was. So the gate follows no kernel handle: its creation, close
and rundown, and a duplication making one in the system process's table, are omitted as of no object it follows, and
never begin or end one. A duplication from a kernel handle into a process makes that process a handle of its own, which
the gate holds as the object's.

A creation's or a close's header still binds nothing (`contracts/entities-v1.md` §2a). Tests hold the duplication's
compiled roles and direction, and the gate's omissions on synthetic and compiled records; eight mutations are caught.

## Addendum: a handle's creation and close bind to the process that raised them (revision 939)

The second addendum left a creation's and a close's header binding nothing. FX-SYNC-001 had every handle's creation and
close of four runs raised in the process that made and closed it, and FX-SYNC-002 a duplicated handle's close in its
target; the gate admits no kernel handle, whose header names whichever process the kernel ran for. So ADR-030's header
binding now reaches the `Synchronization` mechanism (`contracts/entities-v1.md` §2a): a creation or a close whose payload
names no owner belongs to the process that raised it. A duplication binds by the target its payload names and a handle
open at the stop by the holder its record names, as a payload owner always wins.

An object type at the stop is an inventory whose payload names no process, raised, as FX-SYNC-002's handle rundowns
were, in processes other than any it describes. So a header now binds no inventory, of any mechanism; no mechanism bound
by its header before this one has an inventory, so this changes no earlier binding. What binds changes, so the rule is
`process-binding-v6`, as the entities contract's §7 requires: a checkpoint derived under version 2 to 5 is read as
version 6's wherever every record named its owner, and one counting a record without an owner is derived again. The
entities, checkpoint, operations, operation index, metrics, relations, query identity, HTTP exchange and COM call
contracts name the new rule; the grouped answer's caveat and the checkpoint's refusal name synchronization handles, and
the Desktop's explanations say version 6. Tests pin the mechanisms a header binds and the inventory it never does, bind
a session's handle records each way, and read a version 5 checkpoint; six mutations are caught.

## Addendum: the synchronization objects profile (revision 940)

The fifth decision's last requirement is met: an opt-in profile collecting the handle group and stating its cost.
`sync-objects` requires Kernel-Process and the object manager's handle group, so on a build with no measured type
numbers, or a schema without a field the gate decides by, it does not start rather than record lifecycle and no handle.
It takes no focus, as Explore takes none, and `icat capture` and `icat record` take it as they take the other opt-in
profiles; a capture asking for it sets the group's mask on its own private system logger (ADR-082's third addendum).

Its class is its sources' at most, Moderate for Kernel-Process. Its statement says, beside it, what ADR-082's addendum
measured: the handle group's collection alone took a median 0.4 and 0.0 CPU points, but a handle-heavy workload ran
5.3 and 8.3% lower in throughput, past §12's target, as each creation and close raises a record in the thread that
makes the call. That is why it is opt-in and Explore never admits the source. Nothing has measured the profile whole
yet, and no build carries a tier for named synchronization objects: a fixture measured through the product is next.
Tests compile the profile with and without the handle group and check both commands' profile lists, help and preview.

## Addendum: the profile's cost through the product (revision 943)

The fourth addendum stated the profile's class as its sources' at most, Moderate, nothing having measured it whole.
`tools/InterCat.SyncProbe --measure --product-impact` now has, as the COM calls profile's measurement had, FX-SYNC-001's
opener opening and closing each object by its name that many times, then its rounds unpaced, in nine rounds of three
trials: no capture, `icat record --profile sync-objects` and `icat record --profile explore`, in an order rotating each
round, machine processor time over the workload alone and the workload's own time measured, a capture's start and stop
outside both.

- 32,768 times each object (`bench/results/sync-objects-impact-20261105T112016Z`): about 78,000 handle records a second
  machine-wide, which the gate cut to about 17,600 admitted, about a million omitted a capture and none beyond its
  bound. The profile added a median 11.8 CPU points to no capture, every round above 5, its own process 4.2, and the
  workload ran 18.4% slower; Explore beside it added none measurable. Nothing was dropped or lost.
- 4,096 times each (`bench/results/sync-objects-impact-20261105T111353Z`), its workloads under a second on a machine
  already 43% busy: a median 6.2 points and 14.8% slower, nothing dropped or lost.

So on a handle-heavy workload the profile is High, past §12's 5-point processor target as well as its throughput one:
its records are the machine's every handle's, which a capture reads, gates and mostly omits, and its own process alone
is four points. The profile now states High and both series, the longer its evidence; it stays opt-in, and Explore never
admits the source. Tests hold both series to their rounds, their loss and their class, and the statement to their
medians; four alterations of the evidence and two of the statement are caught.

## Addendum: a capture refuses a group its gate could not decide (revision 949)

The gate decides each record by the type numbers measured for the capture's build and by the fields of its descriptors,
and `RecordGates.Undecidable` says why it could not: no numbers measured for the build, or a layout that admits no field
the gate decides by. But a capture's compilation asked it only of a manifest provider. The handle group, a classic
class's, compiled from whatever layout the machine registered, and on a build with no measured numbers its gate was built
with none: the synchronization objects profile would have started and admitted no synchronization record, saying it saw
none - and the profile's own test held a refusal no compilation produced.

- `CapabilityInventoryProbe` now asks it of a classic kernel group too, for the build the capture is compiled for, and
  refuses the group with the gate's reason, compiling no plan of it; `CaptureProfileCompiler` compiles a profile's sources
  for the build of the environment it is given. So the profile does not start, the group blocking with that reason.
- `icat capabilities` says such a group's schema is unknown, for that reason, where it otherwise keeps it a kernel group
  the provider registry cannot confirm.
- A test holds it on the measured layout and build, which compile and start, on a build with no measured numbers and on a
  layout whose creation carries no handle, both refused; four mutations are caught. It is the named synchronization
  objects package's schema-failure case.
