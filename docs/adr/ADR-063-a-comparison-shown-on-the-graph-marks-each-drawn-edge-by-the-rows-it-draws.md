# ADR-063: A comparison shown on the graph marks each drawn edge by the rows it draws

- Status: accepted; revision 762. It records Show on the graph, M12's graph overlays
- Date: 2026-10-23
- Relates to: plan M12, §6.3, §6.6, R14, R15, R18, R21, P6, ADR-058, ADR-059, ADR-060, ADR-062;
  `contracts/comparison-v1.md` §8e

## Context

M12 asks for graph overlays of a comparison. A comparison states rows; the graph draws a bounded compaction of the
session (§6.3), where one edge may stand for a collapsed group's relationships, an edge to Other hosts for a process's
connections to many addresses, and a comparison by executable has rows of many processes together. An overlay must say
which rows each edge stands for and what to mark where they disagree, without stating a change the rows do not state
(R21) or asking a person to read a colour (R14), whose hue already means mechanism (§6.6).

## Decision

1. **A mark per drawn edge, from the rows it draws.** One row's change is the edge's; rows that agree, theirs; rows
   that each rose, appeared or stayed, up - appeared where none held anything in the baseline - and likewise down; and
   rows that went both ways, up and down.
2. **Not comparable wins.** One row that cannot be compared makes the edge not comparable: what it holds has no stated
   change, so neither has its total, nor its direction.
3. **No value of its own.** A mark is a function of its rows' changes and of whether each held anything; nothing is
   summed, rated or judged again, so every mark resolves to rows `icat compare` states.
4. **A grouping marked wherever its processes are drawn, and said so.** Of a comparison by executable, a row marks
   every edge of its processes' relationships and connections, and a mark of more than its edge has a broken ring and
   says it is of the executables' processes together - a grouping, never an identity (P6).
5. **Words beside every mark.** The graph's line states the intervals and the counts with their glyphs, an edge's card
   its change, and the relationship table each row's (R15); an edge without a mark is said to have no record in either
   interval, and a row no edge draws is counted.
6. **The window's, until cleared.** The marks follow every drawing and newer generations of the session, saying what
   is new has none, and go when another session is shown; nothing of them is saved.

## Why

- **Marks are where a person looks first.** Marking each edge by an honest aggregate of its rows keeps the compacted
  graph readable without a mark claiming more than its rows.
- **Both ways is a finding, not a conflict.** ± says the rows went both ways, and the card counts them, where a sum
  would cancel them into a quiet edge.
- **A sum would be a new measurement.** An edge's rows may stand on lower bounds or differ in what they measured;
  their own changes are what the comparison judged.
- **A comparison by executable stays visible on a graph of processes.** Marking only rows drawn in one edge would mark
  nothing there.

## Consequences

- The Compare window's Show on the graph; a line beneath the graph's header with Clear; each edge's card; and the
  relationship table's glyph column and item status.
- The graph's height gives the line its room while it is shown.
- Not built: a comparison's marks on the timeline or on nodes, and a saved overlay; the command line states the rows,
  never the window's drawing (R18).

## Alternatives considered

- **Colouring edges by change.** Rejected: colour alone (R14), and hue is the mechanism's.
- **An edge's own change from its rows' summed values.** Rejected for the reasons above.
- **Marking only an edge that draws one row.** Rejected: a collapsed group's edge, the commonest on a busy machine's
  graph, would carry no mark.
- **Marking a row only where one edge draws all of it.** Rejected: a comparison by executable would mark nothing on a
  graph of processes.
