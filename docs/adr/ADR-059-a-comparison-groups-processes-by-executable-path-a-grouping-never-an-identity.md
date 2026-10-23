# ADR-059: A comparison groups processes by the path their executable was witnessed at, a grouping and never an identity

- Status: accepted; revision 755. It records how `icat compare --group-by executable` forms its rows, and the rule a
  comparison of two captures pairs its rows by
- Date: 2026-10-23
- Relates to: plan M12, §6.3, R21, R22, P6, ADR-058; `contracts/comparison-v1.md` §2a

## Context

On a busy Windows machine processes come and go: a browser's renderers, a service host its manager restarted, an
updater that runs once a minute. A comparison by process (ADR-058) states such churn honestly - one process's
relationship disappeared, another's appeared - but a person asking what changed between two moments usually asks it of
programs: did the browser talk to the proxy more after the update? M12 lists entity grouping among what a comparison
is made by. The overview already groups a session's processes by executable: the path their image was witnessed at,
letter case aside, and one group apart for every process whose image was never witnessed.

P6 forbids establishing identity from an equal name or from the same executable path, since independent instances
merged into one convincing entity is exactly the failure R22 guards against.

## Decision

1. **A row of executables is a grouping.** Grouped by executable, a comparison's row is every relationship the
   overview draws between a process of one executable and a process of another, or of the same one, over one
   mechanism, or every connection an executable's processes made over one transport to one other host's address, each
   summed. The processes stay what they are - each its own instance, none taken for another - and the row says whose
   they are, never that they are one.
2. **By the path, never the name.** An executable is the overview's group: the path its image was witnessed at, letter
   case aside. Two executables of one name at two paths are two rows, named apart by their folders, as the window
   names its groups.
3. **What no path groups is counted apart.** A process whose executable was not witnessed is in no row: no path groups
   it with another, and grouping it with the other unwitnessed processes would merge strangers. Its relationships and
   connections to other hosts with a record in either interval are counted, with their records in each interval, and
   said beneath the rows, so nothing is left out silently and nothing reads as zero (R21).
4. **Everything else is ADR-058's.** Rates, coverage, descriptors, lower bounds, one generation and ranking apply to a
   summed row as to any other.

## Why

- **Churn hides change.** A restart between the two intervals turns one program's steady traffic into two rows, one
  disappeared and one appeared, which a person must pair by eye; summed by executable, it reads unchanged, which it
  is.
- **A grouping claims less than an identity.** A row of executables states what the processes run from one path did,
  together. It does not say the process before a restart is the one after it, which nothing observed establishes.
- **Unwitnessed processes are strangers.** The overview keeps them in a group apart so that none is shown under a
  guessed name; summing their traffic as one executable's would compare unrelated processes across the intervals.

## Consequences

- A comparison of two captures can pair its rows by the same keys: the processes each capture ran from one path. That
  is still a grouping, not an identity - the file at the path may differ between the captures - and it will say so.
- The redacted report reads these rows as it reads process rows, naming an executable by a token keyed by its group
  and no process; the window's comparison will read them too.

## Alternatives considered

- **Grouping by image name.** Rejected by P6 and R22: two programs of one name in two folders are two programs.
- **One row for every unwitnessed process together.** Rejected above: it merges strangers.
- **Pairing a restarted process with its predecessor.** Rejected: nothing observed says the later process is the
  earlier one; the executable's row states what both did without claiming it.
