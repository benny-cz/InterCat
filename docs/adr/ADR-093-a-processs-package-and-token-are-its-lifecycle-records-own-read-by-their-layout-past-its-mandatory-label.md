# ADR-093: A process's package and token are its lifecycle records' own, read by their layout past its mandatory label

- Status: accepted for M6's process enrichment; no field of them beyond the session is admitted yet
- Date: 2026-11-06
- Decision owners: InterCat maintainers
- Relates to: ADR-035, ADR-091, plan M6, §4.2, §18.5, R3, R22, FX-PROC-001

## Context

Process enrichment's metadata inspection (revision 987) found `Microsoft-Windows-Kernel-Process`'s process start (event 1,
version 4) and rundown (15, version 2) - which every capture collects for lifecycle - declaring each process's package full
name and package-relative application ID, its token's elevation type and whether it is elevated, and its mandatory label,
beside the session the product admits; and `Microsoft-Windows-Services` declaring a service's status change naming the
process hosting it. A layout on one machine is no evidence of what a provider emits. The plan asks process nodes for
package, service and user-session information as accessible.

## Measurement

FX-PROC-001: a process starts two children, each created suspended - App Installer's winget through its execution alias,
asked its version, in a package, and a child of the workload's own, in none - and before resuming each reads its package
full name and application user model ID by the application model, and its token's elevation type, whether it is elevated,
its integrity level and its session by the token's own information. `tools/InterCat.ProcessProbe --measure` runs it under a
real-time session of its own collecting Kernel-Process at Informational with its process keyword (`0x10`), as every capture
does, asks for its process rundown and reads every running process's package by the application model as the rundown
runs. Three runs, nothing lost:

- Each child's one process start, at version 4, carried every field as the workload read it: winget's package
  `Microsoft.DesktopAppInstaller_1.29.250.0_x64__8wekyb3d8bbwe` and application ID `winget`, the workload's own child an
  empty package and application ID; and both children's elevation type, elevation, integrity level and session, as the
  elevated workload made them.
- Every rundown record, at version 2, carried a running process's package as the application model gave it as the
  rundown ran: 505, 505 and 510 processes read a run, 141, 141 and 142 of them packaged, none disagreeing; every record
  carried a mandatory label.
- Each field was read by the registered layout past the mandatory label's SID, whose sub-authority count says where the
  image after it begins, as the product reads the image's name (§18.5). TDH's own decoding by name, as TraceEvent gives
  it, lost every package after the SID: neither child's start nor any rundown record had one by it.

## Decision

1. **A process's package and token are its lifecycle records' own.** Its package full name and package-relative
   application ID, its token's elevation type and whether it is elevated, and its mandatory label are fields of its
   process start and rundown, which every capture collects: they need no source of their own, and no query of the
   process, which a passive capture never makes.
2. **An empty package is an observed none (R3).** A start or rundown at a version that declares the package carries it
   empty for a process in no package: the process is known to run in none. A record at a version that declares no package
   says nothing of one - unknown, never none.
3. **They are read by the registered layout, past the SID.** The mandatory label is a SID of variable length; a reader
   finds what follows it by its sub-authority count, never by TDH's decoding by name, which loses its place there.
4. **No service is any process's by these records.** None declares which services a process hosts at a moment;
   `Microsoft-Windows-Services` declares only a service's changes of state, naming the process hosting it. A service list
   under a process is a later source's, measured on a fixture of its own, and attributing one IPC operation to one hosted
   service needs evidence beyond it (§6.3).
5. **No field of them but the session is admitted yet.** Admission, and saying them of a process, come next.

## Consequences

- A process can carry its package and its token's elevation and integrity from records every capture already holds; a
  process started before the capture has them from its rundown, as witnessed presence (§18.5).
- The probe's reports carry no PID; the first run's scenario - each child by its PID, its creation time and its image's
  file name alone - is committed under `fixtures/FX-PROC-001/evidence/` with the three runs' comparisons.
- A test holds the three runs to the committed truth and to their bench reports; nine alterations of that evidence are
  caught.
