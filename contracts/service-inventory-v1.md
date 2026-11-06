# service-inventory-v1

Status: revision 994 (ADR-095); every live capture writes them since revision 995
Owner: `InterCat.Storage` (`ServiceInventoryV1`); written by `InterCat.Capture.Recording` (`LiveRecorder`), read from the
service control manager by `InterCat.Capture.Windows` (`RunningServices`)

The services running as a capture started, or as it stopped, as the service control manager lists them, each with the
process hosting it (ADR-095). Microsoft-Windows-Services names no running service as a capture starts (FX-SVC-001), so
which services a process hosts is the manager's own list to say, read from user mode beside the capture. A service is
named by its host's PID and the moment the operating system created that process, which a lifecycle record of the
instance carries too, so a reader binds it to that instance alone - never to another holder of the PID, and never by a
process's name or its image.

## 1. Publication

A capture publishes the inventory it read as it started with its first generation, and the one it read as it stopped with
its last - both with the one generation of a capture published once, at its stop - each as the dependency kind
`ServiceInventory` (code 12, `contracts/store-v1.md`) named for its moment and the generation that publishes it:

```text
service-inventory-start-<generation:D10>.json
service-inventory-stop-<generation:D10>.json
```

Each later generation carries both unchanged, as re-derivation, compaction and checkpoints do; a follower mirroring a
broker's capture mirrors each when it first appears, and retention never releases one. A generation carries at most one
inventory of each moment, both of one capture, each named for the moment it is of and a generation no later than the one
carrying it, and a stop's is published no earlier than a start's. A redacted package never carries one: its services,
and their hosts' PIDs and creation times, describe the machine and would link the package to the moment its processes
started, so the package's leak scan looks for their digests. An original evidence package copies them like every other
file. An import, a redacted package and every capture that wrote none carry none, and a
reader says that the processes' services are not recorded rather than that they hosted none.

## 2. JSON contract

The UTF-8 JSON object is at most 1 MiB:

```text
{
  "contract": "service-inventory-v1",
  "captureId": <non-empty GUID: the journal's capture>,
  "moment": "Start" | "Stop",
  "readStartedUtc": <DateTimeOffset: when the list began to be read>,
  "readEndedUtc": <DateTimeOffset: when it had been read, no earlier>,
  "services": [
    { "name": <the service's name, 1 to 256 characters>, "processId": <integer, 1 to 2,147,483,647>,
      "hostCreatedUtc": <DateTimeOffset> (absent when its host could not be opened to read it) }
  ]
}
```

- `services` holds 0 to 4,096 services: the Win32 services the manager lists active, each with the process hosting it
  (`EnumServicesStatusEx`). A service the manager lists without a process, as one stopping can be, is left out.
- No two name one service, names compared ignoring case as the manager compares them.
- The list is read after the capture's session runs, so a short-lived service's host is less likely to be missed, and it is
  no atomic view of the machine: a service can stop, start or change host while it is read, so its query's start and end
  bound when each service was running (§18.5).
- `hostCreatedUtc` is the host's creation time as the operating system keeps it (`GetProcessTimes`), at its 100 ns
  resolution: the instant a lifecycle record's creation time of the same instance carries.

Unknown members, another contract name, an empty capture identity, an unknown moment, more than 4,096 services, a name
empty or longer than 256 characters, a PID outside its range, a service named twice, a read that ended before it began, an
empty file or one over 1 MiB are refused.

## 3. What a reader may say

A process instance hosted a service at an inventory's moment when its PID is the service's and its creation time, from its
lifecycle records, is the host's: the start key a lifecycle record carries is the identity, and a PID alone, or an image
name, is never taken for one. A service whose host's creation time was not read, or whose PID no instance with that
creation time held, binds to no instance, and a reader keeps it as unbound rather than guess an instance for it. A service
list is metadata of its host (§6.3): no record of the host is attributed to one of its services without evidence naming
the service (ADR-095).
