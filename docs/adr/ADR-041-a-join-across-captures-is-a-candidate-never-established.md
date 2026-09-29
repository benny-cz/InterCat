# ADR-041: A join across captures is a candidate, never established

- Status: accepted; revision 258, decisions revision 260
- Date: 2026-09-28
- Relates to: §7.4, §8.3, M4, P6, P7, R22, ADR-038, ADR-039, `contracts/workspace-v7.md`, `contracts/relations-v1.md`

## Context

Within one capture a TCP connection's two ends are paired exactly: the records holding the mirrored endpoint pair of one
protocol, divided into incarnations by the lifecycle the capture witnessed (`relations-v1`). Across separately captured
sessions nothing is exact. A connection one capture holds only one end of - a one-sided connection, most often to
another host - may be the connection another capture holds the other end of, but the two captures' clocks are unrelated
until an investigation aligns them (ADR-039), an address translation or a proxy makes two connections look like one,
and a port used again is another connection. §8.3 asks the correlation to preview candidate joins with their evidence
and alternatives, to keep a person's accepted joins as manual, and never to pair by time or name alone (P6).

## Decision

1. **A candidate needs mirrored endpoints of one protocol.** One member's connection and another's are compared only
   when the one's local endpoint is the other's remote endpoint and the other way round. No partial address match, no
   port alone, no process name.
2. **Its lifetimes must be able to overlap.** Each connection's lifetime runs from its first record to its last; placed
   in the investigation's time, each end widened by its uncertainty, the two must overlap. A pair whose lifetimes lie
   apart beyond their uncertainty is not proposed - it is counted - and a pair that cannot be placed, because a member is
   not aligned or its uncertainty is unknown, is a candidate whose timing is said to be unknown, never assumed.
3. **A loopback address names its own host.** Mirrored loopback endpoints join only two captures recorded on one host
   identity; on two hosts they are counted and never proposed.
4. **A candidate states its evidence and its alternatives, and is never established.** It lists what matched, whether
   the lifetimes overlap or why they cannot be compared, and the bytes each side measured of each direction; when either
   connection has another candidate, it says how many, so a match that is not the only one reads as ambiguous.
5. **Only one-sided connections are compared.** A connection both of whose ends one capture holds is that capture's own
   channel, and no other capture's record joins it.
6. **A person's decision is a revision, and never evidence** (revision 260). Accepting a candidate as one connection, or
   rejecting it, is recorded as a revision of the investigation with the alignments in force for its two sessions, and
   kept when replaced or withdrawn. A candidate says what a person decided of it; when either session's alignment has
   changed since, it says the decision was made under alignments since changed, to review, because the timing it was
   decided on may no longer hold (§8.3). A decision whose pair is no candidate now is said, never dropped.

## Consequences

- Revision 258 implements the rule `cross-capture-connection-candidate-v1` in `InterCat.Application`
  (`WorkspaceCorrelation`), over every one-sided connection of a session (`SessionConnections.All`), and
  `icat workspace correlate`, whose `--json` prints `workspace-correlation-v1`.
- One machine cannot capture a TCP exchange's two ends apart - the TCP source is machine-wide - so the rule is proven on
  synthetic sessions, and live only in proposing nothing on two real captures of one machine. A known two-host exchange,
  M4's exit gate, needs a second host.
- Revision 259 lists the candidates in the Desktop's investigation window, each with its evidence and named for a screen
  reader as a sentence. Revision 260 records a person's decisions (`workspace-v4`, `icat workspace join`, and Accept,
  Reject and Withdraw decision in the window). Known address translations follow.