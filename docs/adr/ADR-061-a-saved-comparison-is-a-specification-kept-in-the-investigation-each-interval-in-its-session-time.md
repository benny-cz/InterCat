# ADR-061: A saved comparison is a specification kept in the investigation, each interval in its own session's time

- Status: accepted; revision 758. It records `icat workspace comparison` and `icat compare --saved`, M12's saved
  comparison specifications
- Date: 2026-10-23
- Relates to: plan M12, §8.4, §26.3, R22, I16, ADR-038, ADR-039, ADR-058, ADR-060; `contracts/workspace-v22.md` §7,
  `contracts/comparison-v1.md` §8c

## Context

M12 asks for saved comparison specifications. A person who compared the hour before an update with the hour after it,
or yesterday's capture with today's, comes back to the same question: when the live capture has grown, when a
colleague has the package, when the answer is wanted again. Typing four bounds, two folders and two options again is
where mistakes are made. An investigation already keeps a person's other decisions about its sessions - alignments,
joins, notes and saved views - by revision, and names its sessions by identity rather than by path (ADR-038).

## Decision

1. **Kept in the investigation, by revision.** A saved comparison is a named entry of the investigation's file: a
   later revision of a name replaces or removes it, and every revision is kept, as a saved view is.
2. **A specification, never an answer.** It keeps the two intervals, the metric and the grouping, and nothing read
   from a session. Run, it reads each session where the investigation finds it then, at the generation found there,
   and its answer names the comparison and the revision run.
3. **Each interval in its own session's time.** Each interval names a member by its session identity and is in that
   session's own presentation ticks, as `icat compare` takes it - never the investigation's time.
4. **Two sessions by executable alone.** A comparison of two members' intervals is of executables (ADR-060); one of
   processes across two sessions is refused when saved and when read.
5. **A name is a person's words.** It is shown wherever the comparison is run, and a redacted report never carries it.

## Why

- **Revisions keep what a person decided.** A comparison replaced by mistake is still in the file, as every other
  decision of the investigation is.
- **An answer kept would go stale.** A live capture grows and a member is relinked to another generation; a kept answer
  would read as current when it is not, so only the question is kept (I16).
- **The investigation's time is optional and changes.** Two captures compare with no alignment of their clocks
  (ADR-060), and an alignment can be withdrawn or revised; an interval kept in the investigation's time would move or
  vanish with it, while one in its session's ticks means the same records whatever is aligned later.

## Consequences

- `icat workspace comparison` saves and removes one, `icat workspace show` lists those in force, and `icat compare
  --workspace <workspace> --saved <name>` runs one with every output `icat compare` writes.
- A package of the investigation keeps them, so a recipient runs the same comparisons on the copied sessions.
- The window saves and reopens them next; difference timelines and graph overlays will read the same specification.

## Alternatives considered

- **Saving the comparison's answer.** Rejected: it would go stale silently, and a redacted report already shares an
  answer when one is wanted.
- **Keeping intervals in the investigation's time.** Rejected for the reasons above; a saved view, which shows the
  merged timeline, keeps the investigation's time because that is what it shows.
- **Keeping comparisons in a session.** Rejected: a session is never changed by an investigation's decisions
  (ADR-038), and a comparison of two captures belongs to neither.
