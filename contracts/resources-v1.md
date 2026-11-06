# Resources contract, version 1

Status: M6, revision 1002 (ADR-097)
Owner: `InterCat.Analysis` (`ResourceInstanceIndex`, rule `resource-instances-v1`)
Read by: `icat resources`

A resource instance is an object with a bounded lifetime that processes held (plan §7.1): here, a named synchronization
object, and a section by its handles. It is derived on demand from one generation's segments, as process instances are
(`entities-v1`); nothing is persisted.

## 1. A resource instance

| Field | Meaning |
|---|---|
| `mechanism` | `Synchronization` for a named synchronization object, `SharedSection` for a section by its handles |
| `number` | The number the capture's gate gave this incarnation over its kernel address (ADR-083, ADR-090): `SynchronizationObjectNumber` (`EN-SourceField` 22) or `SectionObjectNumber` (31). Never an address, never zero; a reused address is another number |
| `type` | Its object type's name, as the stop's rundown names its type's number (`ObjectTypeNumber`, 23); null where no rundown named it |
| `names` | The names its closes and the stop's rundown carry, each as recorded, in the order first given; empty where none named it. A creation carries no name |
| `firstNativeTicks`, `lastNativeTicks` | Its first and last records' readings |
| `firstHandleMade` | Whether its first record is a handle made in the capture - a creation, or one duplicated into a process - rather than a close or the stop's rundown of one made before it |
| `lastHandleClosed` | Whether its last record is a close and no handle of it was held at the stop: its end was seen |
| `heldAtStop` | Whether a handle of it was held at the stop, as the stop's rundown lists it |
| `holders` | The process instances its records bind to, by PID and then by which of the PID's holders each was, each with its `handlesMade`, `handlesDuplicated`, `handlesClosed` and `heldAtStop` |
| `unbound` | Its records bound to no process instance, or by a binding the evidence policy does not admit, by reason (`entities-v1` §4) |
| `records` | Every record of it |

Its key is the capture's host and boot, the mechanism and the number, at lifecycle epoch 1 (`identity-v1`, "Resource
instances"): the gate numbers each incarnation, so no epoch is needed beyond it. A name is an attribute, never identity.

## 2. Names and namespaces

A name is kept as its record carried it. For browsing it is split at its last backslash into its namespace, the object
directory it lies in - `\Sessions\1\BaseNamedObjects` - and its leaf, the rest; a name whose only backslash leads has the
root `\` for its namespace, and one with none has no namespace. A namespace holds the resources named in it or beneath it,
compared without case, as the object manager compares names. An unnamed resource lies in no namespace.

## 3. `resource-instances-v1`

A record belongs to a resource where it is of `Synchronization` or `SharedSection` and its source fields carry an object
number, the section's by its handles; a record carrying a type's number and no object number, at the stop, names that type.
A record binds to a holder under `process-binding-v8` - a handle's creation and close by the process that raised them, a
duplication by the process it made its handle in, a handle at the stop by the process holding it - as the evidence policy
admits, the default counting correlated evidence. The instances are listed by mechanism, then by number.

No record of these mechanisms carries a signal, a wait or a byte through shared memory: a resource here is discovered,
its handle records topology and never traffic (P27, ADR-084, ADR-092).

## 4. `icat resources`

`icat resources <session> [--mechanism synchronization|section] [--namespace <directory>] [--limit <n>] [--json]` lists
the instances, with the namespaces their names lie in and how many each holds. Its document, `resources-v1`, carries the
rule and the binding rule, the filters, how many are listed and how many are unnamed, the namespaces, and each instance
listed with every field of §1, its namespace and leaf, and its readings in seconds after the capture's start.

## 5. Not defined at this version

- A section by its views (`SectionNumber`, 28), AF_UNIX sockets, named pipes, ALPC ports and listening endpoints.
- Traffic through a resource, and which of a mechanism's resources were idle: none of these mechanisms records any.
- The window's browsing of resources by namespace, and a resource drawn on the graph.
