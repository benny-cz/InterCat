# ADR-064: A communication pattern is a structural signature, never a process, address or time

- Status: accepted; revision 763. It records `icat patterns`, M12's recurring communication patterns
- Date: 2026-10-24
- Relates to: plan M12, §7.4, R18, R21, R22, P6, P7, ADR-031, ADR-059; `contracts/patterns-v1.md`

## Context

M12 asks for recurring communication patterns built from structural signatures - a mechanism, an interface or
operation and an endpoint role, with source-specific normalization - and delivered as explainable pattern groups. On a
busy machine the same kinds of communication recur across many processes: browsers to other hosts' 443, services
calling the endpoint mapper, clients of a local database's port. The graph and the tables say who talked to whom; a
pattern says what kind of communication recurs and who took part in it, without inventing a cause or an identity.

## Decision

1. **A signature is structure alone.** A mechanism; the service port, or the RPC interface and procedure; and where
   the server end is, this machine or another host. No process, address, port a client chose for itself, or time is
   part of it.
2. **Each source normalized by what it records.** A TCP connection's server is the end the capture saw accept it, at
   that end's port; a UDP flow's service port is the one outside the dynamic range, the lower where both are, since
   nothing opens a flow; an RPC call's interface and procedure are those its start recorded, and its side is its role.
3. **A role rests on evidence.** A TCP connection seen open at neither end has no roles and is in no pattern; it is
   counted apart, as are flows between two dynamic ports, calls no start named an interface for, and what the evidence
   policy did not admit.
4. **Participants, never identities.** A pattern lists its ends in each role - processes, hosts by address, ends no
   record holds - and counts programs as the overview groups processes by executable (P6).
5. **Recurrence as observed.** A pattern recurs when it occurred more than once, and how far through the session it
   recurred is how many tenths its occurrences began in; no period, cause or anomaly is inferred.
6. **Command line first.** `icat patterns` lists them with every participant; the window reads the same reading when
   it shows them.

## Why

- **Structure recurs where identity does not.** Processes restart, addresses change and a client takes a new port each
  time; a port served, or an interface's procedure called, is what repeats.
- **A role invented would be a finding.** Calling the end at the lower port of a TCP connection its server, where the
  capture saw no accept, would state a direction no record shows; UDP's convention is said in its explanation, since a
  flow has no opening.
- **Counting apart keeps absence honest** (R21): a connection in no pattern is not one that did not happen.

## Consequences

- `icat patterns`, with `--pattern <rank>`, `--all` and `--json` (`communication-patterns-v1`).
- Not built: the window's patterns, other mechanisms' signatures - HTTP exchanges, named pipes and ALPC ports - and
  patterns over an interval or across captures.

## Alternatives considered

- **Grouping by program pair.** Rejected: `icat programs` does so, and a pattern would then hide that two programs'
  traffic is the same kind.
- **Clustering by timing or volume.** Rejected: a learned cluster explains itself poorly, and would read as a finding
  rather than a grouping.
- **A port alone as a TCP signature, wherever it was served.** Rejected: 443 served here and 443 reached at other
  hosts are different communication, so where the server is stays in the signature.
