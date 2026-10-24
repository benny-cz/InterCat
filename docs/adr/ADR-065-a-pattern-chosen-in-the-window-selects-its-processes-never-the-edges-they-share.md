# ADR-065: A pattern chosen in the window selects its processes, never the edges they share

- Status: accepted; revision 764. It records the window's recurring patterns
- Date: 2026-10-24
- Relates to: plan M12, §6.7, R15, R18, R21, P6, ADR-063, ADR-064; `contracts/patterns-v1.md` §7

## Context

Revision 763 listed the recurring communication patterns on the command line. The window is to show the same reading
and let a person go from a pattern to what took part in it. A pattern's participants are processes, hosts and ends no
record holds; the graph draws processes, or the programs they ran, as nodes, and each edge between two of them stands
for every connection, flow and call between its ends, whatever their port or interface.

## Decision

1. **One reading, one set of words.** The window reads the patterns as `icat patterns` does, under the window's
   evidence policy, and says them in the words the command line says them in, now shared by both.
2. **A pattern's processes become the selection.** Choosing a pattern makes its processes, of either role and each
   once, the selection Ctrl+click builds (§6.7), or goes to its one process; Enter then lists their records, as on any
   such selection.
3. **What cannot be selected is said.** A process only a newer generation holds is left out; the list says when it
   read a newer generation than the window shows, and a choice of which the window holds no process leaves the
   selection as it was, saying why.

## Why

- **An edge claims more than a pattern holds.** The edge between a browser and a server stands for all their traffic:
  marking it for "TCP at port 8080 here" would mark their connections to other ports too, and an RPC edge stands for
  every interface called across it. Selecting the processes claims only that they took part.
- **The selection already has every next step.** It is drawn on the graph, counted in the inspector and turned into
  records by Enter, so following a pattern needs no view of its own.
- **Shared words keep the two surfaces agreeing** (R18): a count or caveat the command line says one way, the window
  says the same way.

## Consequences

- Recurring patterns… among the window's actions: the list, the chosen pattern's every end, and Select its processes.
- `icat patterns` names the evidence policy as `--evidence-policy` takes it, and says why a session holds no pattern
  in the sentence the window says it in.
- Not built: marking a pattern's own connections on the graph, which would need edges drawn by port and interface.

## Alternatives considered

- **Marking the edges between a pattern's processes.** Rejected: a drawn edge is not a pattern's occurrences, as Why
  says.
- **An overlay like a comparison's (ADR-063).** Rejected for now: a comparison marks each edge by every row it draws,
  while a pattern holds only some of an edge's records, which a mark on the whole edge would misstate.
- **Opening the pattern's records at once.** Rejected: Enter on the selection reaches them, and the selection keeps
  the graph in view first.
