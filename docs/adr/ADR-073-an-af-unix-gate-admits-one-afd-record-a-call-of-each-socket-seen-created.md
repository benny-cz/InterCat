# ADR-073: An AF_UNIX gate admits one Winsock AFD record a call of each socket a capture saw created

- Status: accepted for M6's Unix-domain sockets work package; the gate exists, and no profile compiles it yet
- Date: 2026-11-04
- Decision owners: InterCat maintainers
- Relates to: ADR-072, plan M6, §4.2, §12, R22, P8, FX-UNIX-001, `tools/InterCat.AfdProbe`,
  `bench/results/afd-unix-feasibility-arrival-*`, `bench/results/afd-unix-feasibility-oversized-reads-*`

## Context

ADR-072 measured that Winsock AFD records every AF_UNIX socket call, but delivers every socket call of the machine,
whatever its family, as its process filter does not hold; names a socket only by its endpoint's kernel address, which the
kernel reuses; raises a completion on whichever thread finished it; and records a send four times and a receive twice.
A capture admits records one at a time as they arrive (`TraceEventRecordAdmitter`), and stateful grouping waits for
derivations over sorted rows. Admitting AFD whole would journal every socket call of the machine, several times each, to
keep a few of them; so a capture needs to decide, as records arrive, which socket a record is of and whether it counts.
Two things ADR-072 did not measure decide whether that can be done: whether a socket's records arrive after its create,
and which of a call's records holds what the call moved.

## Measurement

`tools/InterCat.AfdProbe` counted, of the records each socket owns by time, those the session delivered before its
create, in three runs of FX-UNIX-001's exchange - eight connections at once twice, and messages of up to 256 KiB:
none, of 36,088 records on 130 sockets. A fourth run had the server read each message in reads asking for 64 KiB,
taking what had arrived: every receive's entry named what it asked for - 65,536 bytes each - and its exit what it took,
the exits of the server's 256 reads summing to the truth's 576,729 bytes and the client's to its 2,048. A socket's
cleanup came before its close, its last call, and the transport's indication of a disconnect could still follow the
close.

## Decision

1. **A gate decides, as records arrive.** `UnixSocketGate` follows a socket from the create whose entry names its address
   as AF_UNIX to its close's exit, or to the next create at that address, owned by the process its create was made for,
   never by the thread that raised a record; an accept's new socket, when its process created none ahead of it, is its
   listener's owner's. A record of an address no followed socket holds is of none, and is omitted as such: another
   family's, or a socket made before the capture began. Each socket is numbered in the capture, never named by its
   address (R22).
2. **One record a call or step.** Of a followed socket's records the gate admits a create's entry, a close's exit, the
   exit of a bind, a listen, a connect or an accept, a send's exit in its own process - location 3051 of the two on build
   `10.0.26220.0-x64`, both with its length - and every receive's exit, whose length is what it took. Every other record
   of it counts nothing and is omitted as such: a call's other records, an option, the transport's indications.
3. **Bounded.** The gate follows at most 65,536 sockets, saying of a create beyond them that it was not followed, and
   remembers the last 4,096 closed so that their late indications are theirs; it holds back no record.
4. **What this does not decide.** No profile compiles the gate yet. Wiring it needs the source's admitted events and
   fields - no pointer, no buffer address and no address bytes, the path being cut to 14 characters - its omissions in
   the coverage ledger, the records normalized as Unix-domain socket observations by the gate's numbers and owners, and
   an opt-in profile, since collecting AFD costs Moderate (ADR-072). A location is the provider's own detail, so a build
   is held to the measured one by its fixture.

## Consequences

- Tests hold the gate on synthetic records - an address reused across families, an owner named by its create, an accept's
  new socket, a close and its late indication, the bound - and on FX-UNIX-001's two ledgers, read through it as they were
  delivered: one record a call of every AF_UNIX socket, each send and receive with the bytes it moved, and none of
  another.
- A record arriving before its socket's create would be omitted as of no socket; none did, and the gate's count of such
  omissions is what would show one.
