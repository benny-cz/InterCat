# ADR-035: A private system logger for an opt-in capture that needs the kernel's flag groups

- Status: accepted; the capture that uses it is the next slice
- Date: 2026-09-28
- Amends: ADR-002 (its first decision, and its consequence that the strategy creates no system logger)
- Relates to: §4 (ALPC), §7.4, §9.2, §12, §20.6, P14, P19, ADR-004, ADR-034, `tools/InterCat.AlpcProbe`,
  `bench/results/alpc-session-check-20260928T033708Z`

## Context

ADR-034 measured that the kernel's ALPC events link an RPC client call to the server call that served it. It also found
that collecting them alone costs 1.86 CPU points, Moderate by §12. ALPC has no manifest provider: it is a kernel flag
group, and only a system logger receives one. ADR-002's first decision creates a named session for manifest providers
and never a system logger. ADR-034 left admitting ALPC to an ADR that amends it for a private system logger: uniquely
named, owned, never the NT Kernel Logger, never adopted, stopped with its capture, within the machine's limit of eight.
The feasibility run's session was the probe's own. This ADR checked the product's.

## Measurement

`InterCat.AlpcProbe --session-check` created a session as the capture creates one. It was named by
`CaptureSessionIdentity` and made with `TraceEventSession(name, Create | NoRestartOnCreate)`. The ALPC flag group was
enabled first, then `Microsoft-Windows-Kernel-Network` for TCP, while the TCP and RPC truth workloads ran:

- **It became a private system logger.** Its log file mode was `0x0A400100`: real time, with the system logger bit.
  The kernel flags, as the first enablement, start the session in that mode.
- **Both kinds arrived, and nothing was lost.** There were 12,034 ALPC sends and as many receives, 12,410 waits and
  unwaits, and 664 TCP events. A further 1,314 events were of neither kind; an admission would drop them.
- **It held one slot and gave it back.** The machine ran four system loggers before, five during and four after. Once
  stopped, the session was gone and no session named InterCat remained.
- **The order is not a choice.** A manifest provider enabled first starts an ordinary session (`0x08400100`). TraceEvent
  then refuses the kernel flags: "The kernel provider must be enabled first and only once in a session".

## Decision

1. **One session, a system logger when the profile needs one.** A capture whose profile admits a kernel flag group
   creates its one owned session as a private system logger. The kernel flags are its first enablement, which starts it in
   that mode; its manifest providers follow in the same session. A profile with no kernel flag group creates the ordinary
   session of ADR-002. A capture never has a second session, so its name, token, recovery and cleanup are unchanged.
2. **Never another's.** Never the NT Kernel Logger or the Circular Kernel Context Logger, never adopted, never restarted
   (`Create | NoRestartOnCreate`). A name collision is a refusal, never an adoption (P14, P19).
3. **The limit of eight is a refusal, not a contest.** A machine runs at most eight system loggers, and this one runs four
   of its own. A capture that cannot create one is refused with the reason and the count. It never stops another session
   to make room.
4. **Stopped with its capture, recovered like any owned session.** Its owner stops it, and the broker's recovery stops it
   by its durable name and token like any other (§9.2). A system logger that outlives its owner holds a scarce slot, so
   recovery treats it as the leak it is.
5. **Opt-in profiles only.** Collecting ALPC alone measured Moderate (ADR-034's addendum), so it never joins Explore,
   which admits sources measured Low. The profile that admits it states its own measured class.
6. **Classic events are admitted from the machine's schema too.** A kernel event carries no manifest; its descriptor is
   the kernel task's GUID and opcode. Admission plans it as it plans a manifest event (ADR-002's fourth decision). TDH
   decodes the first delivered record of each descriptor, and a layout that disagrees with the plan refuses the
   descriptor with a reason rather than admitting a guessed field.
7. **What no plan admits is counted, not kept.** A system logger delivers events no profile asked for, kernel header
   records among them. Each is a policy omission, counted as any unadmitted event is (§20.6).

## Consequences

- The next slice is the capture. The owned session accepts kernel flags in its plan, first. An opt-in profile for RPC
  peers admits the RPC call events 5-8 and ALPC's send and receive, with admission for classic descriptors and an ALPC
  observation. Its capture cost is measured through the product path, and it states its class.
- ADR-002's consequence that the strategy deliberately creates no system logger holds for every profile without a kernel
  flag group, and no longer for the profile that needs one.

## Addendum: how a classic ALPC record names itself (revision 219)

A second run of the check (`bench/results/alpc-session-check-20260928T034426Z`) recorded, once per ALPC opcode, what
TraceEvent reports of a record's identity. Every ALPC record carries the generic kernel provider id
(`9e814aad-3204-11d2-9a82-006008a86939`), with ALPC's class in its task (`45d8cccd-539f-4b72-a8b7-5c683142609a`). Its
event id is `65535`, the value that means none, and its version is 2. Only the opcode tells the five apart: 33 send,
34 receive, 35 wait for reply, 36 wait for a new message and 37 unwait. Four carry a 4-byte body, the message id; the
wait for a new message carries 38 bytes. So:

- A classic descriptor is its class and opcode, with its version. The admission table's key today is a provider, an
  event id and a version, and every ALPC record shares the first two, so classic descriptors need a key of their own.
- The plan's body check is the measured length: 4 bytes for the four that carry only a message id. The wait for a new
  message carries a port name besides, and admitting it is a later question.

## Alternatives considered

- **A second session, a system logger beside the capture's.** Rejected. Ownership, recovery and cleanup would track two
  sessions per capture, each holding a scarce slot, and one session carries both kinds (measured).
- **Kernel flags enabled into a running ordinary session.** Not possible: the mode is set when the session starts, and
  TraceEvent refuses (measured).
- **The NT Kernel Logger.** Rejected by ADR-002: it is a shared session other tools own and restart.
