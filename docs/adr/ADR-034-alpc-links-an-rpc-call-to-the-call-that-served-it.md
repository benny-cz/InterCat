# ADR-034: ALPC links an RPC client call to the server call that served it

- Status: accepted for M3 as a measurement; the capture and the relation rule it enables are later slices
- Date: 2026-09-27
- Decision owners: InterCat maintainers
- Relates to: §4 (ALPC), §7.4 (RPC and ALPC correlation contracts), IC-006, P7, P8, ADR-002, ADR-004, ADR-031, FX-RPC-001,
  `tools/InterCat.AlpcProbe`, `bench/results/alpc-feasibility-20260927T165107Z`

## Context

ADR-031 pairs an RPC call's start with its stop on one side. ADR-004 measured that a client call and the server call
that served it carry different activity ids, so local RPC peers stay unresolved (P7), and it left ALPC unmeasured: ALPC
is a kernel flag group with no manifest provider, and ADR-002's capture creates no system logger. §7.4 expects an ALPC
message id to give candidates only, because ids repeat, and allows thread nesting to supplement a validated synchronous
path. IC-006 is the spike that measures which of these hold.

## Measurement

`tools/InterCat.AlpcProbe` ran FX-RPC-001's workload - calls to the service control manager - while one session of its
own collected the kernel's ALPC flag group, process names and the RPC provider's call events 5-8. Four runs, 200 and
three times 600 truth calls, gave 1,002 and 3,002 client calls each:

- **The session.** A kernel provider in a session not named NT Kernel Logger is a private system logger on Windows 8 and
  later. The probe named it uniquely, owned it and stopped it; no event was lost, no other session was touched, and none
  was left running.
- **Volume.** 1,600 to 2,500 ALPC events a second on this workstation, the workload included.
- **Message ids are not identities.** 40 to 125 distinct ids per run across 11,000 to 33,000 sends; most ids were sent by
  several processes, and every client send's id was sent by another process within a second.
- **The chain links every call it can reach, and checks out.** All but one client call per run made exactly one ALPC
  send on its own thread between its start and stop. That id was received exactly once in another process before the
  call stopped, except once in one run, where it was received twice and was left unlinked. The first server call to
  start on the receiving thread within 5 ms carried the same interface and procedure in 10,003 of 10,003 links. No server
  call was linked twice, and every linked call's reply came back to the client.

## Decision

1. **The correlation contract, for the relation rule to implement.** A client call is related to a server call when it
   made exactly one ALPC send on its thread between its start and its stop, that message id was received exactly once
   in another process after the send and before the call stopped, and a server call began on the receiving thread
   within 5 ms of the receive and before the client call stopped. Any other shape leaves the call's other end
   unresolved, with the reason; nothing is paired by time alone (P8).
2. **A message id is never a key by itself.** It joins only inside one client call's window and one thread chain.
3. **A link is checked, never assumed.** The two calls must carry one interface and one procedure; a disagreement is
   conflicting evidence, not a link. None was measured.
4. **What this does not decide.** No capture profile admits ALPC here. ADR-002 excludes a system logger from the
   product's capture; admitting ALPC needs an ADR amending it for a private, uniquely named, owned system logger - never
   the NT Kernel Logger, never adopted, stopped with its capture, within the machine's limit of eight - and a measured
   capture cost and class (§12). ADR-004's fourth decision, that ALPC stays unmeasured, is superseded by this
   measurement, and its third, that local RPC peers stay unresolved, holds until the relation rule exists.

## Consequences

- The next slices are the capture (a private system logger in the broker's capture, with its cost measured), the relation
  rule and its contract, and RPC peers in the graph: a process's calls related to the process that served them, with
  ALPC as transport evidence beneath the call and never a second count of it (§5.1, M3's exit gate).
- A lab tool now creates a system logger. It is elevated, owned and uniquely named like the capture-comparison harness's
  ETL sessions, and it writes counters only.

## Alternatives considered

- **Pair a client call and a server call by interface, procedure and time.** Rejected: a peer guessed from timing (P7),
  and concurrent calls to one interface are the ordinary case.
- **Pair by ALPC message id alone.** Rejected: ids are reused within seconds across processes; a run had 40 of them for
  10,946 sends.
- **Keep ALPC out until a manifest provider exists.** Rejected as a measurement stance: the kernel flag group is the
  documented source (§4), and a private system logger reads it without touching another session.
