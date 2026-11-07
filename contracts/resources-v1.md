# Resources contract, version 1

Status: M6, revisions 1002 to 1005 (ADR-097, ADR-098)
Owner: `InterCat.Analysis` (`ResourceInstanceIndex`, rule `resource-instances-v3`)
Read by: `icat resources`

A resource instance is an object with a bounded lifetime that processes held (plan §7.1): here, a named synchronization
object, a section by its handles and by its views, and an AF_UNIX socket. It is derived on demand from one generation's segments, as process instances are
(`entities-v1`); nothing is persisted.

## 1. A resource instance

| Field | Meaning |
|---|---|
| `mechanism` | `Synchronization` for a named synchronization object, `SharedSection` for a section by its handles or by its views, `UnixDomainSocket` for an AF_UNIX socket |
| `layer` | The layer its records are of: `Resource` for a synchronization object's and a section's, `Transport` for an AF_UNIX socket's, which a metric's layer projection keeps or drops it by |
| `byViews` | Whether it is a section found by its views - the number the view gate gives the value its views carry, `SectionNumber` (28) - rather than by its handles: the two numbers are never joined, so one section can be two instances (ADR-092) |
| `image` | For a section by its views, whether they are an image's - their extra information's (`ViewInformation`, 30) high byte 0x47 - a module loaded rather than memory a process shared |
| `views`, `largestView` | For a section by its views, how many views of it the capture saw (`SectionViewNumber`, 27), and the largest extent one mapped (`ViewSize`, 29), never bytes moved through it; zero and null for any other |
| `number` | The number the capture's gate gave this incarnation over its kernel address (ADR-083, ADR-090): `SynchronizationObjectNumber` (`EN-SourceField` 22), `SectionObjectNumber` (31), a section's by its views (28), or the socket's number the AF_UNIX gate writes as `ConnectionId` (7), read on AF_UNIX records alone, since TCP's connection ids share the field (ADR-073). Never an address, never zero; a reused address is another number |
| `type` | Its object type's name, as the stop's rundown names its type's number (`ObjectTypeNumber`, 23); null where no rundown named it |
| `names` | The names its closes and the stop's rundown carry, each as recorded, in the order first given; empty where none named it. A creation carries no name |
| `firstNativeTicks`, `lastNativeTicks` | Its first and last records' readings |
| `firstHandleMade` | Whether its first record is a handle made, a socket created or a view mapped in the capture - rather than a close, a transfer, an unmapping or a rundown of one made before it |
| `lastHandleClosed` | Whether its last record is a close or an unmapping: its end was seen |
| `heldAtStop` | Whether its last record is the stop's rundown: a handle or a view of it was held at the stop. A view's rundowns at the start and the stop are both inventories, told apart by their opcodes (39 and 40); one whose last record is the start's was neither seen unmapped nor listed at the stop, its end unseen |
| `holders` | The process instances its records bind to, by PID and then by which of the PID's holders each was, each with its `handlesMade`, `handlesDuplicated`, `handlesClosed` and `heldAtStop`, and for a section by its views its `viewsMapped`, `viewsUnmapped` and `viewsListed` by a rundown at the capture's start or stop |
| `traffic` | The transfers its records carried where its mechanism records any - an AF_UNIX socket's: `sends`, `receives`, `bytesSent` and `bytesReceived` of those whose size was measured, `unmeasuredTransfers`, and `idle` where it carried none. Null for a synchronization object or a section, whose mechanisms record no transfer: never said to be idle (R3) |
| `unbound` | Its records bound to no process instance, or by a binding the evidence policy does not admit, by reason (`entities-v1` §4) |
| `records` | Every record of it |

Its key is the capture's host and boot, the mechanism and the number, at lifecycle epoch 1 (`identity-v1`, "Resource
instances"): the gate numbers each incarnation, so no epoch is needed beyond it. A name is an attribute, never identity.

## 2. Names and namespaces

A name is kept as its record carried it. For browsing it is split at its last backslash into its namespace, the object
directory it lies in - `\Sessions\1\BaseNamedObjects` - and its leaf, the rest; a name whose only backslash leads has the
root `\` for its namespace, and one with none has no namespace. A namespace holds the resources named in it or beneath it,
compared without case, as the object manager compares names. An unnamed resource lies in no namespace.

## 3. `resource-instances-v3`

A record belongs to a resource where it is of `Synchronization` or `SharedSection` and its source fields carry an object
number, the section's by its handles, or where it is of `UnixDomainSocket` and its gate numbered its socket - a create, a
bind, a connect, an accept, a send, a receive or a close, its transfers its traffic - or where it is a section's view, a
mapping, an unmapping or a rundown, carrying the number its section's views carry; a record carrying a type's number and no object number, at the stop, names that type.
A record binds to a holder under `process-binding-v8` - a handle's creation and close by the process that raised them, a
duplication by the process it made its handle in, a handle at the stop by the process holding it, and a view's mapping,
unmapping or rundown by the process whose view it names - as the evidence policy admits, the default counting correlated
evidence. The instances are listed by mechanism, a section by its handles before one by its views, then by number.

No record of these mechanisms carries a signal, a wait or a byte through shared memory: a resource here is discovered,
its handle records topology and never traffic (P27, ADR-084, ADR-092). A socket's sends and receives are its traffic,
their bytes the transport's where measured (ADR-076), and a socket that carried none was idle: discovered, no traffic
observed. A section by its views carries no traffic either: a view's size is the extent it mapped. Version 1, revision
1002's, derived no socket, and version 2, revision 1003's, no section by its views. The resource-topology basis counts
these resources, the processes sharing them and sections' capacity (`metrics-v1` §8b, revision 1005).

A resource's records are those of its mechanism whose source field carries its number - a section's views by the number
they carry and its handles by theirs, a socket's by its own and never a TCP connection's of the same number - whichever
process they bind to; 0 is no number and names none. A resource two or more processes held, other than an image's section
or a socket, is the window's hub (ADR-099), keyed `resource:hub:{mapped-section|section|object}:{number}`, and its key
scopes its records as an RPC channel's scopes its calls: `icat evidence --channel` reads them by it, a page of them
taking `resource-instances-v3` as its rule in its identity (revision 1008). The window's search finds a hub by its name
or, where no record named it, what it is, and going to it draws the hub, first whatever the graph's budget, and chooses
it (revision 1009). Every resource but an image's section is keyed so, a socket `resource:hub:socket:{number}`, and the
window's inspector lists them with nothing chosen on the machine rung, by namespace - named ones by their object
directory and leaf, those no record named after, idle sockets last - one chosen there its records (revision 1010), and
Enter on one chooses its hub or the processes holding it (revision 1011). A program's inspector, chosen or opened,
lists the resources its processes held the same way (revision 1012).

## 4. `icat resources`

`icat resources <session> [--mechanism synchronization|section|socket] [--namespace <directory>] [--idle] [--images] [--limit <n>] [--json]` lists
the instances, with the namespaces their names lie in and how many each holds, and how many carried traffic, were idle,
or are of mechanisms recording none; `--idle` keeps the idle ones, apart from the traffic observed. A section whose views
are an image's is listed only with `--images`, and how many the other options admit but were not listed is said - a session
whose only resources are an image's sections is never said to number none. Its document, `resources-v1`, carries the
rule and the binding rule, the filters, how many are listed and how many are unnamed, the namespaces, and each instance
listed with every field of §1, its namespace and leaf, and its readings in seconds after the capture's start. Revision
1003 added `traffic` to each and `idle` to the document under this version: a reader of revision 1002's document finds
every member it had; revision 1004 added `byViews`, `image`, `views`, `largestView` and each holder's views to each, and
`images` and `imagesNotListed` to the document, likewise, and revision 1005 `layer` to each.

## 5. Not defined at this version

- Named pipes, ALPC ports and listening TCP endpoints.
- An AF_UNIX socket's path, which no record names, and the socket at its other end, which none names either (ADR-072).
