# ADR-066: Causal navigation follows only recorded links, and lists nearness in time apart

- Status: accepted; revision 765. It records `icat links`, M12's causal navigation
- Date: 2026-10-24
- Relates to: plan M12, §7.4, P7, P8, R18, R21, ADR-034, ADR-055; `contracts/links-v1.md`

## Context

M12 asks for a causal navigation view that traverses only evidence-backed parent and related links, with proximity
candidates kept separate, and its exit test asks that a synthetic known dependency be traversable while unrelated
simultaneous events are not labeled causal. The capture already derives three kinds of evidence that one end led to
another: a child's creation record naming its parent (`entities-v1` §3), a client call's one ALPC message reaching the
server call that served it (`operations-v1` §5c, ADR-034), and a TCP connection's connect or accept record saying
which end made it (`relations-v1` §5). On a busy machine many processes start, call and connect in the same instant,
so time alone invites chains no record shows.

## Decision

1. **A link is a recorded fact.** One end led to another only where one of the three rules says so - started, called
   and served, connected and accepted - each at the strength its rule gives. No time window, shared name, address or
   activity id joins two ends.
2. **Ends are processes and addresses.** A link joins processes of the capture, other hosts by address and ends on
   this machine no record holds; links of one kind, ends, interface or port and strength are one, with how many times
   the evidence holds it.
3. **The evidence policy says what is followed.** Direct and correlated links by default; the rest are listed as not
   followed - possible, not established - and no chain is made through them.
4. **Nearness in time is listed apart.** The processes created within a second of a process and linked to it by
   nothing are listed as near it, never followed and never in a chain.
5. **A chain is the shortest run of followed links** through processes alone, as strong as its weakest link; a host or
   an unseen end is an address, never a step.
6. **What links to nothing is counted by why**: connections opened at neither end, flows, unresolved calls, calls no
   client reached, and a parent named but not held.
7. **Command line first.** `icat links` lists them; the window reads the same reading when it shows them.

## Why

- **Each link can be checked against its records.** A person asking what led to a process gets an answer they can
  verify, and a chain says no more than its links.
- **Nearness is the trap, so it is shown, not hidden** (P8, §7.4): listing it beside the links answers what happened
  at the same time without implying that it caused anything.
- **An address joins whoever was at it** (P7): two connections to one address are not linked, so no chain runs through
  a host.
- **Counting what links to nothing keeps absence honest** (R21): a process with no link is not one that depended on
  nothing.

## Consequences

- `icat links --process <process>`, with `--to <process>` for a chain and `--json` (`evidence-links-v1`);
  `--evidence-policy` takes a policy in kebab form there and in `icat patterns`.
- Not built: links in the window, links between operations or records, through named pipes, ALPC ports beside RPC,
  HTTP exchanges, related activity ids or thread nesting, and links over an interval or across captures.

## Alternatives considered

- **Following time proximity as a candidate link.** Rejected: plan §7.4 keeps proximity out of definitive causal
  views; it is listed apart and never followed.
- **Thread nesting or a shared activity id.** Rejected at this version: `operations-v1` §8 leaves both undefined, and
  a shared activity id is not even simultaneity (P8).
- **Chains through hosts.** Rejected: who is at an address is not observed, so two connections to it say nothing about
  each other.
