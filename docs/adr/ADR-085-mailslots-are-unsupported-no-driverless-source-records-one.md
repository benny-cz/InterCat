# ADR-085: Mailslots are unsupported: no driverless source records one

- Status: accepted for M6's mailslots work package; revisit when a supported build or an optional collector is measured
- Date: 2026-11-05
- Decision owners: InterCat maintainers
- Relates to: ADR-003, ADR-035, plan M6, §4.1, §14.2, P6, P7, P20, P27, FX-MAIL-001, `tools/InterCat.MailslotProbe`

## Context

A mailslot is a file of the mailslot file system. Its server makes it by a name, a client opens it by that name as a file
and writes messages to it, and each of the server's reads returns one whole message. §4.1 asks of mailslots
mechanism-specific research, an explicit unsupported or experimental entry until a source is validated.

The inspection of revision 951 (`bench/results/mailslot-provider-inspection-20261105T153228Z`) found no registered provider
named for mailslots. `Microsoft-Windows-Kernel-File`, which ADR-003 measured recording no named pipe, declares no mailslot
event or keyword. The kernel's classic file I/O class, a flag group only a system logger receives (ADR-035), declares at
version 3 a create with its open path, reads and writes with their requested size, and an operation end with its
completion, each by its file object and issuing thread. It had never been measured.

## Measurement

Fixture `FX-MAIL-001` has a server make a mailslot under a name of the run's own and a client open it by that name and write
seeded messages, which the server reads one at a time, each read asking for 65,536 bytes. `tools/InterCat.MailslotProbe
--measure` ran it under two sessions of its own, machine-wide, on build `10.0.26220.0-x64`:

- a private system logger with the kernel's process, thread, file I/O and file I/O init flags;
- an owned session enabling `Microsoft-Windows-Kernel-File` at keywords `0x3F0`, as ADR-003 enabled it.

A record is the mailslot's where it names the run's own name or carries a file object such a record named. The two
processes' reads and writes of the truth's sizes are counted apart, as a mailslot's would carry no name where its creation was
not recorded.

| Quantity | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| Messages written and read, by the truth | 32, up to 2,048 bytes | 64, up to 400 bytes, 5 ms apart | 8, up to 32,768 bytes |
| The mailslot's records, classic file I/O class | 0 | 0 | 0 |
| The mailslot's records, Kernel-File | 0 | 0 | 0 |
| Reads or writes of a truth message's or read's size, either source | 0 | 0 | 0 |
| **Control**: the server's and client's other records, classic class | 1,496 and 1,498 | 1,502 and 1,502 | 1,489 and 1,492 |
| **Control**: the server's and client's other records, Kernel-File | 1,745 and 1,747 | 1,751 and 1,751 | 1,738 and 1,741 |
| Events lost, either session | 0 | 0 | 0 |

The control is what makes the negative result trustworthy. In the same sessions both sources recorded the same two processes'
own files, about a hundred of them, the writes of their truth logs among them. The mailslot simply produced no record at all:
not its creation, not the client's open, not one of its 104 messages, not a close, by its name or by a message's size. Nor did
either source record the anonymous pipe each process's standard output was redirected through.

## Decision

1. **Mailslots are `Unsupported` on this build**, computed from §14.2's thresholds: no truth operation has an observation.
2. **No inference replaces the missing evidence.** InterCat will not present a mailslot relationship derived from an equal
   name, a process's image, a handle enumeration or a broadcast's other end (P6, P7, P20). A mailslot remains an explicit
   coverage gap.
3. **The gap is routed, not dropped**, as M6's fallback and ADR-003 route one: to M7 (cooperative instrumentation), to M9a,
   whose supported NPFS/MSFS minifilter would see the mailslot file system's requests, and to M10 (selected-process
   instrumentation).
4. **No catalog source claims mailslots.** The capability report states the mechanism `Unsupported` with this measurement: a
   mechanism no source covers is reported where a fixture measured it on the build, so the absence is visible rather than
   left out of the report.

## What would reverse this

- The same fixture producing mailslot records under a source on a §1.3 supported build, which would move the mechanism to at
  least `Experimental evidence`.
- M9a's minifilter, or M10's instrumentation of a selected process, each an explicit installation or profile choice rather
  than part of the driverless baseline.

## Consequences

- Release material lists mailslots with tier `Unsupported` and the build it was measured on (§14.2), and `icat
  capabilities` says so.
- The classic file I/O class stays out of the catalog: it records no mailslot. It recorded no redirected standard output's
  anonymous pipe either, so it offers named pipes nothing on this evidence, though only a named-pipe fixture would measure
  that and change ADR-003.
- Tests hold the three runs' absence and their control, the build's coverage, and the report's entry for a mechanism no
  source covers.
