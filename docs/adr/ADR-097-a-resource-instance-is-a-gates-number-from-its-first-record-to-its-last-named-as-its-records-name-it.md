# ADR-097: A resource instance is a gate's number from its first record to its last, named as its records name it

- Status: accepted for M6's resource lifetimes; named synchronization objects and sections by their handles are derived
  and listed by `icat resources` (revision 1002), AF_UNIX sockets with their traffic, an idle one listed apart
  (revision 1003), sections by their views (revision 1004), and the window's inspector lists them by namespace
  (revision 1010), Enter on one choosing where it is held (revision 1011)
- Date: 2026-11-06
- Decision owners: InterCat maintainers
- Relates to: ADR-083, ADR-084, ADR-090, ADR-092, plan M6, §7.1, §7.2, `identity-v1`, `entities-v1` §8, R3, P27

## Context

The plan's domain model has a resource instance - a pipe instance, a section, an ALPC port, a socket or another object
with a bounded lifetime (§7.1) - and M6 asks for resource lifetimes, namespace-aware endpoint browsing, and the resources
a capture discovered kept apart from the traffic it observed. `identity-v1` fixes a resource instance's key - host, boot,
mechanism, a non-zero source instance key and a lifecycle epoch, a name never identity - and `entities-v1` §8 has left
resource instances undefined. Capture-time gates already number each incarnation of an object from the first handle a
capture sees made to the close of the last, writing the number over its reusable kernel address (ADR-083, ADR-090), and
FX-SYNC and FX-SECT measured named synchronization objects and sections as those numbers with their lifetimes, topology
at most (ADR-084, ADR-092). No product derivation read the numbers: `icat processes` counted a process's records of them.

## Decision

1. **A resource instance is one gate number of one capture's mechanism**: a named synchronization object by
   `SynchronizationObjectNumber` (22), a section by its handles' `SectionObjectNumber` (31). Its key is the capture's
   host and boot, the mechanism and the number, at epoch 1, since the gate already gives each incarnation a number of its
   own; a reused kernel address is another number, never the same instance, and a record numbered zero names none.
2. **Its lifetime is its records' first and last readings**, and whether each end was witnessed is stated, never
   assumed: its start where its first record is a handle made in the capture, rather than a close or the stop's rundown
   of one made before it, which the gate numbers alone; its end where its last record is a close and no handle of it was
   held at the stop.
3. **Its name is the one its records carry** - a close's or the stop's rundown's, never a creation's - kept as recorded,
   and split for browsing into its namespace, the object directory it lies in, and its leaf. Names two records give
   differently are each kept, never one chosen; an instance no record named is unnamed, never named the empty text (R3).
   Its type is the name the stop's rundown gives its type's number; none where no rundown named it.
4. **Its holders are the process instances its records bind to** (`process-binding-v8`) as the evidence policy admits
   them, each with the handles it made, those duplicated into it, those it closed and those it held at the stop; a
   record bound to none, or by a binding the policy does not admit, is counted by why, never given a holder.
5. **It is discovered, and its handle records are no traffic.** No record of these mechanisms carries a signal, a wait
   or a byte through shared memory, so a resource here is topology - discovered with its lifetime and its holders - and
   is never ranked by traffic it shows none of (P27). A mechanism whose records carry traffic will say which of its
   resources carried any and which were idle.

## Consequences

- `contracts/resources-v1.md` defines the instances and `resource-instances-v1`, the rule deriving them; `icat resources`
  lists them by mechanism and by the namespace their names lie in, and its document keeps each name as recorded.
- Five tests hold the derivation and the command; 20 mutations of the path are caught.
- AF_UNIX sockets, sections by their views, and the window's browsing of resources by namespace are the next slices'.

## Addendum: AF_UNIX sockets carry their traffic, and an idle one is listed apart (revision 1003)

An AF_UNIX socket is a resource instance too, by the number the AF_UNIX gate writes over its address (ADR-073), read
as `ConnectionId` on AF_UNIX records alone, since TCP's connection ids share the field: its lifetime from its first
record to its last, its start witnessed where its create was seen, its holder the process its records name, and no
name, since no record names its path. Its sends and receives are its traffic, their bytes the transport's where
measured (ADR-076); one that carried none was idle - discovered, its traffic none observed - which `icat resources
--idle` lists apart from the traffic observed. A synchronization object's or a section's traffic is none recorded,
never idle: their mechanisms record no transfer (R3). `resource-instances-v2` derives them; version 1 derived no
socket. 16 mutations of the path are caught.

## Second addendum: a section by its views is a resource of its own (revision 1004)

A section found by its views - the number the view gate gives the value its views carry (ADR-090) - is a resource
instance of its own, never joined to the number its handles carry, so one section can be two instances, as ADR-092
measured it twice. Its holders are the processes whose views its records name, each with the views it mapped, unmapped
and was listed holding by a rundown at the capture's start or stop; its start is witnessed where a view was mapped in the
capture, and a view the start's rundown lists was mapped before it. It carries how many views were seen and the largest
extent one mapped, never bytes moved through it, and no name, since no view record carries one. A section whose views
are an image's - their extra information's high byte 0x47 - is a module loaded rather than memory a process shared:
`icat resources` lists it only with `--images`, and says how many it left out. A resource's last record decides its end
for every mechanism: a close or an unmapping seen, or the stop's rundown holding it. A view's two rundowns are both
inventories, and only their opcodes tell the start's from the stop's: a view whose last record is the start's was
neither seen unmapped nor listed at the stop, so its end is unseen, never said to be held. `resource-instances-v3`
derives them. 31 mutations of the path are caught.

## Third addendum: the window lists them by namespace (revision 1010)

With nothing chosen on the machine rung, the inspector lists every resource of the session but an image's section, as
`icat resources` does: named ones by the object directory their names lie in, then their leaf, those no record named
after, and idle sockets last, apart from the traffic observed - M6's namespace-aware browsing, its discovered but idle
resources apart. Each row says what the resource is, where its name lies, who held it, its records and, for a socket,
what it carried or that it was idle. The first eight are named, and every one up to a hundred on request. A resource
chosen there is its own records, read by its key as a hub's are (ADR-099) - a socket's too, now keyed
`resource:hub:socket:{number}` - which the timeline highlights, the card counts and E lists; anything else chosen hides
the list and lets its choice go. A session whose processes made only AF_UNIX socket records derives its resources too.
32 mutations of the path are caught.

## Fourth addendum: Enter on a listed resource chooses where it is held (revision 1011)

Enter or a double click on a resource the inspector lists chooses where it is held: its hub where the graph draws one -
on the machine rung, or on its holders' program's rung where the machine rung draws them as that program's one node,
first whatever the budget (ADR-099) - else the process holding it, or the processes holding it together. Esc comes back
along that way: nothing chosen, the machine rung shown again, the resource chosen in the list once more and the
keyboard on its row; a way the selection or the ladder has since left is let go. Each row says what Enter on it
chooses. Revision 1010's list stayed on screen beside the first choice made while it showed, its hiding never said to
the window; it now hides. 19 mutations of the path are caught.
