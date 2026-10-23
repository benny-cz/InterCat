# ADR-062: A difference timeline compares each interval's k-th equal share as two intervals are compared

- Status: accepted; revision 760. It records `icat compare --timeline`, M12's baseline and difference timelines
- Date: 2026-10-23
- Relates to: plan M12, §6.2, R18, R21, I16, ADR-057, ADR-058, ADR-060; `contracts/comparison-v1.md` §8d

## Context

A comparison says how much a row changed between two intervals; the question after it is when: did a relationship that
doubled do so throughout, or in one burst? M12 asks for baseline and difference timelines. Two intervals compared need
not be of one length, nor of one capture, nor on one clock, and a timeline of either alone cannot say whether a column's
difference is the workload's or the capture's.

## Decision

1. **Equal shares, by position.** Each interval is cut into the same number of equal columns, end to end, and the k-th
   column of one is set beside the k-th of the other: the same share of each interval, in each session's own ticks,
   whatever their lengths or clocks.
2. **Each column pair is a comparison.** It is compared as two intervals are (ADR-058): rates over each column, a
   change only where both columns covered the row's mechanism and the capture collected it alike in both - across two
   captures, through the same descriptors (ADR-060) - and elsewhere both values with the reason. Quiet columns are
   compared too.
3. **Read with the comparison.** The columns are read in the same read as the whole intervals, at one generation of
   each session (I16), so a row's columns add up to its whole values.
4. **Bounded.** At most 100 columns, each at least a tick long; only the rows asked for keep their columns.

## Why

- **Position is the only alignment two intervals always share.** Clock time does not align two captures or two
  intervals of different lengths; a share of each interval does, and says so.
- **One judge for columns and wholes** keeps a column from stating what the whole would refuse: a column the capture did
  not collect reads as not comparable, never as a disappearance.

## Consequences

- `icat compare --timeline <rank> [--columns <n>]` states a row's timeline, in text and JSON.
- Since revision 761 the Compare window draws the chosen row's timeline from the same read; a redacted report holds none
  yet.

## Alternatives considered

- **Aligning by clock time.** Rejected: two captures need no alignment of their clocks to compare (ADR-060), and an
  alignment would make a timeline depend on one.
- **Two independent timelines side by side.** Rejected: each would be judged alone, so a column one capture did not
  collect could read as activity that stopped.
