# ADR-060: Two captures compare by executable, each on its own clock, paired by path and judged by both ledgers

- Status: accepted; revision 756. It records `icat compare --baseline-session`, M12's comparison of two captures
- Date: 2026-10-23
- Relates to: plan M12, §8.3, R21, R22, I12, I14, I16, P6, ADR-039, ADR-058, ADR-059; `contracts/comparison-v1.md` §2b

## Context

M12's comparison of two captures - yesterday's and today's, before and after an update, a machine that misbehaves and
one that does not - needs what ADR-058 deferred: a qualified mapping of entities between sessions. A process of one
capture is never a process of another; PIDs are reused; and P6 forbids establishing identity from an equal name or the
same executable path. Each capture also has its own clock, its own coverage ledger, its own generations and perhaps
its own host.

## Decision

1. **By executable, paired by path.** Two captures compare only by executable (ADR-059): each capture's processes
   grouped by the path their image was witnessed at, and a row of one paired with the other's of the same path, letter
   case aside. The mapping is qualified: it is a grouping and says so, never claiming that the processes, or the files
   at a path, are the same; and it states whether the two captures name one host identity - evidence of one host,
   never proof (§8.3). Processes of no witnessed executable are in no row in either capture, and are counted apart.
2. **Each interval on its own clock.** An interval is in its own session's presentation ticks and a rate is over its
   whole interval, so two captures' rates compare with no alignment of their clocks; none is applied, and a shared
   report says so.
3. **Each capture at its own generation.** Each is read at one generation of its own, read again where it publishes
   between reads, and the answer names both (I16).
4. **A change only where both captures measured alike.** Each interval's coverage is its own capture's; each capture's
   epochs over its interval must have collected the mechanism alike (ADR-058's rule 3), and then the two captures must
   have collected it through the same descriptors. Otherwise both values stand with the reason, so a changed capture
   profile never reads as a workload change.
5. **One session is not two captures.** Two stores holding one session are refused: its intervals compare as one
   session's.

## Why

- **The path is the only key both captures share.** Instance identities, PIDs and start times do not carry over, and a
  name is ambiguous across folders. A path still says nothing of what is at it, which is why the pairing is a grouping
  and is said to be one.
- **Descriptors decide comparability across captures as within one.** Two captures can each be covered while recording
  different events of a mechanism - another profile, another build - and their counts are then of different things.

## Consequences

- Identical captures compare as equal, and a capture profile that changed between them states no change of what it
  changed - M12's first two exit tests across captures - each by a named test.
- The window offers another saved capture's whole recording as a baseline, comparing by executable (revision 757);
  difference timelines and graph overlays will read either form.

## Alternatives considered

- **Matching processes across captures by command line, user and start order.** Rejected: each is reusable or absent,
  and together they still establish no identity; summing by path claims less and loses nothing a person needs to see
  what changed.
- **Aligning the two captures' clocks.** Rejected as unneeded for rates over whole intervals; a difference timeline,
  which places instants side by side, will need the workspace's alignment (ADR-039) and is not built.
