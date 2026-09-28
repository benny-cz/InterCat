# ADR-030: An RPC record binds to the process that raised it

- Status: accepted for M1; revision 238 adds HTTP to its list as `process-binding-v4` (ADR-037)
- Date: 2026-09-27
- Decision owners: InterCat maintainers
- Relates to: §4.1 (header and payload), §7.1 (process instances), §7.3 (`EntityBindingRevision`), §24
  (`entityRevision`), R1, R20, R22, P6, ADR-004, ADR-013, ADR-019, `contracts/entities-v1.md`,
  `contracts/derivation-checkpoint-v1.md`, FX-RPC-001

## Context

`process-binding-v2` binds a record to the PID its own payload names, and never to its event header's process (§4.1).
The kernel sources are why. The kernel raises a network record in whatever process context it was in, and a UDP
receive's header named another process than its payload owner every time it was measured (ADR-019, ADR-029).

Microsoft-Windows-RPC's call events are 5 client start, 6 server start, 7 client stop and 8 server stop. They name an
interface, a procedure, a protocol and an endpoint, but no process. Under v2 every RPC record named no owner, so none
could reach a process. RPC could be captured and counted, but a call belonged to nobody. It could not be a process's
activity, pass an owner filter, or become a lane.

The provider is user-mode. It raises a call's events on the thread that makes or serves the call. If so, the header's
process is exactly the process the record describes. That needed a measurement, not an assumption.

## Measurement

FX-RPC-001 (`icat measure rpc`, run `20260927T122620Z`, adapter `windows-etw-inventory-0.7.0`) drives a workload that
queries the Service Control Manager through its RPC interface, `367abb81-9844-35f1-ad32-98f038001003`. The workload
records its own PID and the service host's.

- **Client side.** 12 of 12 truth calls had a client start on the expected interface, raised in the workload's process.
  The source admitted 126 records from that process during the capture.
- **Server side.** 122 of 122 server-side calls to the expected interface were raised in the service host the workload
  recorded (services.exe). Inside the client's call window, 61 of 61 server starts paired with their stops by
  activity id.
- **Across sides.** No client call paired with a server call: the two sides carry different activity ids. The ids pair
  one side's start with its stop, never a caller with its server.

The fixture ran twice in one capture, and both runs produced the same shape.

## Decision

1. **A header binds where it was measured to.** A record belongs to the PID its payload names. When its payload names
   none, and its mechanism is one measured to raise its records in the process they describe, it belongs to the process
   its header names. RPC was the only such mechanism (`RecordAttribution`); HTTP joined it in revision 238, measured
   on FX-HTTP-001 (ADR-037). A test pins that list, so adding a mechanism brings its own measurement, a new rule
   identity and the texts that name the list.
2. **It is a binding rule, not a normalization.** The stored row keeps an empty owner and an unknown attribution
   quality (R1). The derivation reads the header column only in a segment where such a record names no owner, so a
   segment of transfers costs no more to read. Re-deriving an older session applies the rule without reading its
   journal.
3. **One rule for every reader.** The rule changes what binds, so it is a new identity, `process-binding-v3` (§24).
   Everything that asks whose a record is asks it the same way:
   - instances: an RPC server that no lifecycle record names becomes an activity-only instance, witnessed by its first
     call (P6);
   - bindings, `owner(P)` and the other roles;
   - each instance's own records;
   - relation holders;
   - evidence text, the rail, exports and the command line.
4. **Earlier checkpoints are read where they agree.** A checkpoint under v2 equals v3's state wherever every record named
   its owner. Its counts say so: they hold no record without an owner. Any other v2 checkpoint is refused with that
   reason and derived again, because a count cannot say which mechanism an ownerless record was. A format-1.0
   checkpoint holds no counts, so it is refused too (`contracts/derivation-checkpoint-v1.md` §4).

## Consequences

- An RPC call is its caller's own record, and a served call is its server's. RPC joins a process's counted activity,
  its owner filter and its evidence. The grouped answer's caveat names RPC as the one exception to §4.1.
- A session whose checkpoint counts any ownerless record, such as a kernel record whose payload named no PID, is
  derived in full on its next open. The overview says why in one caveat, and the next checkpoint a writer publishes
  replaces it.
- This decides whose each call record is. It does not decide which records make one call, or which process is at a
  call's other end. A call's start and stop pair by activity id on one side (revision 178). A channel between a caller
  and its server needs a relation of its own, because no id links the sides (revision 179).
- A record raised on a thread that serves another process's request is still its own process's. A source that raises
  records in a broker or proxy on another's behalf, as ALPC can, needs its own measurement before it joins the list.

## Alternatives considered

- **Write the header's PID into the owner column at normalization.** Rejected. It would change stored observations
  (R1), make a raised-by owner indistinguishable from a payload-named one, and apply to a session only when it is
  normalized again.
- **Bind every ownerless record by its header.** Rejected. The kernel sources raise records in arbitrary context, and
  the UDP receives measured wrong every time.
- **Leave RPC unattributed until calls are operations.** Rejected. An operation needs an owner first: a call that
  belongs to no process cannot be a lane.
