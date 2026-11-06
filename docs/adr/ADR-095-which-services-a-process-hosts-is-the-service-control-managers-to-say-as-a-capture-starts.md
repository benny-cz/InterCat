# ADR-095: Which services a process hosts is the service control manager's to say, as a capture starts

- Status: accepted for M6's process enrichment; no service is said of a process yet
- Date: 2026-11-06
- Decision owners: InterCat maintainers
- Relates to: ADR-093, plan M6, §4.2, §6.3, §18.2, P27, FX-SVC-001

## Context

The plan asks process nodes for package, service and user-session information as accessible, and §6.3 groups processes by
service container. Process enrichment's metadata inspection (revision 987) found `Microsoft-Windows-Services` declaring a
service's status change (event 105, under its Performance keyword) naming the service and the process hosting it, and
control and configuration events naming the client that asked for them; no provider declared which services a process
hosts at a moment. A capture asks each provider it collects for its state as it starts. Whether this provider answers that
request with the services already running is a question of what it emits, which no layout answers.

## Measurement

FX-SVC-001: `tools/InterCat.ProcessProbe --services`, three runs of 60 seconds
(`bench/results/service-provider-measurement-20261106T155713Z`). Each run lists the running services and the process
hosting each by the service control manager's own query (`EnumServicesStatusEx`), enables Microsoft-Windows-Services under
a real-time session of the probe's own at Verbose with every keyword, asks for its state, runs, stops, and lists the
services again. It starts, stops and configures no service, so a change it records is one the machine made of itself.
Nothing was lost:

- Nothing arrived on the provider's enabling, nor on its state asked for. None of the 127, 127 and 128 services the
  manager listed running throughout - hosted by 116, 116 and 117 processes, several to many a process - was named by any
  event of any run.
- In the second run a service started by itself, the manager listing 128 once the session stopped. Four status changes and
  four other service events arrived, and the status changes named that service with the process hosting it, as the manager
  then listed it. No status change named a service and host the manager never listed.

## Decision

1. **Microsoft-Windows-Services is no source of which services a process hosts as a capture starts.** It answers no
   request for its state, and its records name a service only as the service's status changes. It is not inventoried as a
   lead for a process's services.
2. **Which services a process hosts is the service control manager's to say.** Its own list of the running services and
   the process hosting each, a documented query needing only the right to enumerate services, is read from user mode as a
   capture starts and again as it stops - the catalog's first API inventory, never a guess from a process's name or its
   image. Its records, their admission and their binding to the instance holding each PID at each reading are the next
   slices'.
3. **A status change during a capture is the provider's.** It records a service started or stopped while a capture runs,
   with its host; collecting it is a later option, which would not make the provider a lead for the services running before.
4. **A service list is metadata of the process hosting it** (§6.3): an IPC operation of a shared host is never attributed
   to one of its services without evidence that names the service.

## Consequences

- No service is said of a process yet, and no capability is claimed for one (P27).
- A test holds FX-SVC-001's three runs to their truth and their bench report; eight alterations of them are caught.

## Addendum: the service inventory's contract (revision 994)

`contracts/service-inventory-v1.md` is the inventory a capture will keep: the running services the manager lists at one
moment - its start or its stop - each by its name and its host's PID and creation time, the read's start and end bounding
when each was running, as §18.5 asks of an inventory that is no atomic view of the machine. It is a store dependency of its
own, `ServiceInventory` (code 12), named for its moment and the generation publishing it: a capture publishes its start's
with its first generation and its stop's with its last - both with the one of a capture published once, at its stop - and
every later generation and re-derivation carries both, a generation carrying at most one of each moment, the stop's no
earlier than the start's. Retention never releases one, and a redacted package never carries one, its leak scan looking
for their digests. A reader binds a service to the instance whose PID is the service's and whose creation time is the
host's, never by a PID alone. No capture writes one yet; twelve mutations of the path are caught.

## Second addendum: a capture reads its services (revision 995)

Every live capture - `icat record`'s and the broker's - reads the service control manager's list of the running services
and the process hosting each once its session delivers, so a host started meanwhile is not missed, and again as it stops,
while its session still delivers (§18.5). The start's is published with its first generation and the stop's with its last,
both with the one of a capture published once, and a follower mirrors each once. Each host's creation time is read once, as
the operating system keeps it; a list the manager will not give, or no inventory could hold, is recorded as none and the
capture goes on, never with an empty list in its place (R3). On this machine a 10-second capture under the explore profile
read 130 services in 119 hosts at its start and at its stop, in 29 and 16 ms, every host with its creation time, and each
host was exactly one of the session's process instances by its PID and the creation time its process start or rundown
carried. Seven mutations of the path are caught.

## Third addendum: a process says the services it hosted (revision 996)

`service-binding-v1` binds each service a capture's inventories list to the process instance whose PID is the service's
and whose creation time, from its lifecycle records, is the host's: never to a later holder of the PID, and never by a PID
or an image alone. A service whose host's creation time was not read, or that no instance held, is kept unbound. The window's
inspector and `icat processes --pid` say of a process the services it hosted, each said to be at one end alone where the
capture read both and listed it at one; none, where it hosted none of those read; or not recorded, where the capture read
none - three answers never given one for another (R3). `icat processes --json` keeps them apart as a list, an empty list
and null, beside the moments the capture read them at. The services stay metadata of their host: no record is attributed
to one. Ten mutations of the path are caught.
