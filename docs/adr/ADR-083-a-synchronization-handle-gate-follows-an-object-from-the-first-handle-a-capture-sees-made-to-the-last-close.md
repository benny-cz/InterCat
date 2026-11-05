# ADR-083: A synchronization handle gate follows an object from the first handle a capture sees made to the last close

- Status: accepted for M6's named synchronization objects work package; since revision 936 the handle group's events
  admit through the gate (its addendum), and no profile requests the group yet
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
