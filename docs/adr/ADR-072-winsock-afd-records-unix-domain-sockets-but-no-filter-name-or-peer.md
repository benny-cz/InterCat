# ADR-072: Winsock AFD records Unix-domain sockets, but holds no process filter, no socket's name and no peer

- Status: accepted for M6's Unix-domain sockets work package; revisit when a capture's cost of the source is measured
- Date: 2026-11-04
- Decision owners: InterCat maintainers
- Relates to: plan §4.2 ("Unix-domain sockets: investigate relevant Winsock/AFD schemas and controlled fixtures"), M6,
  §14.2, R21, R22, P27, ADR-003, ADR-034, FX-UNIX-001, `tools/InterCat.AfdProbe`, `bench/results/afd-unix-feasibility-*`

## Context

M6 begins each mechanism's work package by identifying a supported passive source and building a truth fixture, and
implements only the fields it proves; an AF_UNIX endpoint "cannot be reported as fully observed if only its name was
discovered". §4.2 warns not to assume TCP/IP tracing covers AF_UNIX. No source of the catalog named the mechanism.

## Measurement

FX-UNIX-001's workload, `InterCat.TestWorkloads unix-loopback`, runs FX-TCP-001's two-process exchange over an AF_UNIX
stream socket bound to a path, each process logging every message it sent and read. `tools/InterCat.AfdProbe` ran it
under a real-time session of its own on build `10.0.26220.0-x64`, enabling Microsoft-Windows-Winsock-AFD's socket calls
and transport indications and asking it to scope them to the workload's two processes, beside a decoy pair running the
same exchange outside them, and Kernel-Network machine-wide under the product's keywords. Each record was read by the
provider's registered layout. Three runs: 4 connections one at a time, 8 at once unpaced, and messages of up to 256 KiB.

| Quantity | Result |
|---|---|
| Sends: calls recorded with their length, against the truth | every one, both ends, each run: 64, 256 and 32 calls, the bytes exactly |
| Receives: calls recorded with their length | every one: the bytes exactly the truth's, the server's two reads a message |
| Sockets | each created with its family, bound, listening, connecting, accepted and closed |
| Kernel-Network records naming either process | none |
| Records of the decoy outside the process filter | every one of its own delivered |
| Sockets created at an address an earlier socket had | 8 to 11 a run |
| A bind's or connect's address | 16 bytes: the family and an AF_UNIX path's first 14 characters |

1. **AFD sees the exchange, and TCP/IP's tracing does not.** Every socket call of the exchange was recorded; a send is
   four records, two entries and two exits at their locations, and a receive two, each with the call's length.
2. **The process filter does not hold.** The provider, raised in the kernel, delivered the decoy's records and other
   processes' as well: a capture of it collects every socket call on the machine, TCP's and UDP's too.
3. **A socket is an incarnation, never an address.** A record names its socket by its endpoint's kernel address, which
   the kernel frees and reuses within seconds; read from its create to the next create at that address, every record
   counted against its own process's truth, the decoy's too.
4. **A record's header names no owner.** A completion is raised on the thread that finished it - an accept and a pended
   receive on the peer process's - so a socket's owner is the process its create was made for.
5. **No name and no peer.** A path is cut to 14 characters, so a socket's name is not observed whole, and no record names
   the socket at the other end; the completions raised on the peer's thread suggest a chain, as ALPC's did (ADR-034), but
   none is measured or contracted.

## Decision

1. Winsock AFD enters the source catalog as Unix-domain sockets' lead: inventoried, `Experimental`, requested by no
   profile and admitting no event, its measured notes stated by `icat capabilities`.
2. No tier is claimed for Unix-domain sockets on any build (P27): §14.2 computes one from admitted observations, and
   nothing admits these. FX-UNIX-001 is indexed for its evidence, with no tier.
3. Before any profile admits it, the work package needs, in order: the cost of collecting it machine-wide (§12), its
   records normalized by incarnation with the owner its create names, and a statement of what is not observed - the
   name beyond 14 characters and the peer - wherever a socket is shown. A peer link through the completions on the
   peer's thread needs its own measured contract (P7, P8).

## Consequences

- `icat capabilities` lists the mechanism with its lead and no tier, rather than no source at all.
- The fixture's ledger holds each record's metadata by incarnation, never a pointer, an address or a byte, and a test
  counts it against the truth.
- The other M6 packages - COM and DCOM annotations, synchronization objects, mailslots, UI messaging and section
  discovery - are not begun.
