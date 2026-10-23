# ADR-058: Two intervals compare row by row as rates over each whole interval, a change stated only where both measured alike

- Status: accepted; revision 752. It records M12's first comparison, `icat compare`, and the rules a later comparison of
  two captures, and the window's view of one, keep
- Date: 2026-10-23
- Relates to: plan M12, §15.2, §5.3, §10.3, §19.2, R3, R7, R18, R21, I3, I6, I16, P1, P7, ADR-012, ADR-034, ADR-054,
  ADR-055, ADR-057; `contracts/comparison-v1.md`, `metrics-v1`, `coverage-v2`, `relations-v1`

## Context

The question InterCat answers on a busy machine is which processes talk to whom. The next one a person asks is what
changed: after an update was installed, when a program started misbehaving, in the minute a service went quiet. M12
asks for comparisons of two captures or two time windows, normalized rates beside absolute counts, ranked changed
relations, and two refusals: a change of the capture profile must not read as a change of the workload, and unavailable
measurement must not read as activity that disappeared. Where two values cannot be compared, it asks for both with the
reason, never an invalid delta.

Two captures need a qualified mapping of entities between them, which does not exist; two windows of one session do
not, since a process instance and a relationship keep their identity throughout. Within one session the profile can
still change: a capture's epochs each record what they collected, a provider re-enabled after a failure can come back
with fewer descriptors, a release gives up the oldest records, and losses fall where they fall.

## Decision

1. **Two intervals of one generation, row by row.** A row is a relationship between two of the session's processes, as
   the overview draws it, or one process's connections over one transport to one other host's address, as the census
   counts them; on a busy machine most traffic is the latter. Every row with a record in either interval is compared;
   the rest are counted as quiet.
2. **Rates over each whole interval, compared exactly.** A value's rate divides by its interval's whole length
   (`metrics-v1` §7); two rates are compared in integers, the compared value times the baseline's length against the
   baseline value times the compared length, so intervals of different lengths compare without rounding, and an
   interval compared with itself is unchanged everywhere.
3. **A change only where both intervals measured alike.** Both intervals must cover the row's mechanism - for RPC calls
   ALPC's too - and every epoch either spans must have collected it through the same descriptors. Otherwise the row
   states both values and the first reason that applies, and no change. A mechanism not collected in an interval is
   therefore never activity that stopped there, and a source that changed is never a workload that did.
4. **A partial gap is not bounded.** The losses a ledger counts bound the raw records lost, not a row's: a lost
   lifecycle or end record moves the attribution or pairing of others, so a gapped interval's records are neither an
   upper nor a lower bound of a row's own. Such a row states both values with the gap's sentence.
5. **A lower bound decides only what it exceeds.** Bytes add the sends that measured a size; sends that recorded none
   make the value a lower bound, counted apart, never zero. A change is stated from a bound only where the bound alone
   decides it, and says so.
6. **Bytes are sent across, under sender accounting.** A relationship's are what its two ends sent on its channels,
   each transfer once at its sender, as the relationship table states them; a reach's are what the process sent to the
   host, since what the host sent has no sender among the machine's records. RPC calls record no size and are not
   compared by bytes.
7. **One generation per answer.** Every read is of one generation, which the answer names; a live session that
   publishes between reads is read again, three times at most, and then refused in words.
8. **Ranked by how much, then by how busy.** Comparable rows first, the largest difference of rates first; then the rows
   that cannot be compared, the busiest by records first; ties by key, so a comparison read twice reads alike.

## Why

- **Coverage, not counts, decides comparability.** Zero records in a covered interval is an observation; zero in an
  interval that did not collect the mechanism is no observation at all (R21). Comparing counts without coverage is
  exactly how a profile change masquerades as a workload change.
- **Descriptors, not just states.** Two epochs can each be covered while collecting different records of a mechanism;
  their counts are then of different things, and only the descriptor sets show it.
- **No bound under a gap.** Using a loss count as a bound per row would have stated more changes on lossy captures, at
  the price of claims the evidence does not support: a lost record's effect on attribution is not bounded by one.
  Stating both values with the gap's reason keeps every stated change true.
- **Hosts are rows.** A comparison of relationships between processes alone showed nothing on the demo's client, whose
  every connection runs to another host; that is the common case.

## Consequences

- A capture that reports any loss compares nothing in the intervals its lossy epochs span, and says why, row by row;
  its values are still shown side by side, the busiest first.
- A later comparison of two captures keeps rules 2 to 8, adds a qualified entity mapping, and compares the two ledgers'
  epochs as rule 3 compares one ledger's.
- The window's comparison view reads this comparison, in the command line's words (revision 753); difference
  timelines and graph overlays will too, rather than re-deriving it.
- A comparison shares as a metadata-only report under the sharing report's redaction policy (revision 754): its
  ends are random tokens, and it records its byte domain, accounting, each interval's missing sources and that no
  alignment applies, as M12 asks a shared report to.

## Alternatives considered

- **Bounding a gapped row by the epoch's loss count.** Rejected above.
- **Comparing only relationships between processes.** Rejected: it leaves out most of a busy machine's traffic.
- **A percentage change for every row.** Rejected: it is undefined where the baseline is zero, invalid where coverage
  differs, and hides the absolute counts M12 asks for beside the rates.
- **Rates over the span the records fill, or the covered part of an interval.** Rejected by `metrics-v1` §7: they
  inflate a quiet interval's rate and make two intervals' rates incomparable.
