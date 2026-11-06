# ADR-097: A resource instance is a gate's number from its first record to its last, named as its records name it

- Status: accepted for M6's resource lifetimes; named synchronization objects and sections by their handles are derived
  and listed by `icat resources` (revision 1002), and AF_UNIX sockets with their traffic, an idle one listed apart
  (revision 1003)
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
