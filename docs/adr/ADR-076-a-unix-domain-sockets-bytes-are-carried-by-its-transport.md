# ADR-076: A Unix-domain socket's bytes are carried by its transport

- Status: accepted; Winsock AFD's admitted lengths read in the `TransportObserved` domain
- Date: 2026-11-04
- Decision owners: InterCat maintainers
- Relates to: ADR-012, ADR-072, ADR-074, ADR-075, §5.3, I6, P3, FX-UNIX-001, `relations-v1` §4

## Context

ADR-074's addendum admitted a Unix-domain socket transfer's length, the bytes its call moved, in the `CompletedIo`
domain, as a named pipe's completion is. The views that show traffic ask for one domain, as I6 requires:

- `icat processes` sent and received columns;
- the window's process activity, byte rankings and timeline tiles;
- an interval's bytes.

All of them ask for `TransportObserved`, the domain TCP's and UDP's records carry. Recorded through the product, a
process that sent 268 KB over AF_UNIX was therefore said to have made no send. Its bytes were answered only when a reader
named `CompletedIo`, and its process detail claimed it had made no transport record at all.

What the length measures decides the domain, not what the views ask for. A send's exit is raised in AFD, the socket
transport's own layer beneath Winsock, and its length is what the AF_UNIX transport took for that call; a receive's exit
is what the call took from it. A stream socket adds no framing, so those are exactly the bytes the transport carried.
FX-UNIX-001 sums them to the truth's at both ends. Kernel-Network records no AF_UNIX traffic (ADR-072), and AFD's gate
admits AF_UNIX alone (ADR-073), so no transfer is counted by two sources. A receive's requested size is never admitted:
its entry is counted as nothing.

## Decision

1. **A Unix-domain socket's transfer bytes are `TransportObserved`.** They are what the socket's transport carried, as a
   TCP or UDP record's bytes are, sender-accounted on a send and receiver-accounted on a receive. This supersedes
   ADR-074's addendum on the domain, and ADR-075's consequence that `icat metric` answers them in the completed-I/O
   domain. A session recorded before this decision keeps the domain its retained plan admitted.
2. **They join their process's traffic, never a relation.** A process's sent and received bytes, its byte ranking, its
   activity and the timeline's tiles count them in the same domain as TCP's and UDP's. No relation rule covers AF_UNIX,
   so their other end is unresolved under `NoRelationRule`, as `relations-v1` §4 leaves every such mechanism; no peer is
   guessed and no edge is drawn.
3. **The process detail says them apart.** `icat processes --pid` states a process's Unix-domain socket records, that
   their bytes are in its sent and received, and that AF_UNIX names no socket's other end, so theirs is unresolved. It
   says a process made no TCP or UDP record, where it said no transport record.

## Consequences

- On a session recorded under the Unix sockets profile, `icat processes` and `icat metric --byte-domain
  transport-observed` give a process its AF_UNIX bytes. The truth's byte totals are the evidence that they are the
  transport's, measured on FX-UNIX-001 (ADR-075).
- A total of transport bytes across mechanisms now counts AF_UNIX's beside TCP's and UDP's. These are different sockets'
  bytes, never the same transfer twice.
- The tier ADR-075 measured does not change: the evaluator reads lengths, not their domain.
