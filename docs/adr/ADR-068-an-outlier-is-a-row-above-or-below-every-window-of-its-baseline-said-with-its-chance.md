# ADR-068: An outlier is a row above or below every window of its baseline, said with its chance

- Status: accepted; revision 768. It records `icat outliers`, M12's optional outlier detection
- Date: 2026-10-24
- Relates to: plan M12, R18, R21, ADR-058, ADR-059, ADR-062; `contracts/outliers-v1.md`

## Context

M12 asks for optional outlier detection that explains its baseline window, its minimum sample size and its
uncertainty, with labels that describe observations, never malware or a root cause. A comparison (`comparison-v1`,
ADR-058) states each row's records over two intervals, and a change only where both measured alike, but not whether a
change is larger than the row's own variation over time.

Records arrive in bursts - a connection's sends together, a call's start and stop - so a row's count over an interval
varies far more than a count of independent events would. Simulated over bursty and heavy-tailed arrivals, a range at
99% taken from the windows' mean and spread, on the counts or on their square roots or logarithms, with a Student t
multiplier for the few windows that estimate it, put as many as 5 rows in 100 above it by chance alone where it
claimed 1 in 200.

## Decision

1. **The baseline is cut into windows as long as the compared interval**: as many as fit, at most 199, spread evenly
   from its start to its end, and at least 19, or the baseline is refused with how long one would do. Where none is
   named, the baseline is all the session keeps before the compared interval; one that overlaps it is refused.
2. **A row is above its baseline where its records over the compared interval are more than every window's, and below
   where they are fewer than every window's**; within otherwise. Nothing is fitted: no mean, spread or distribution.
3. **The chance is stated, not chosen**: at most 1 time in N each way, N one more than the windows, and on average no
   more than the rows judged divided by N each way across them.
4. **Only rows the comparison compares are judged, by records**: a row its two intervals did not cover or collect
   alike is not judged and is counted by its reason, and bytes are not judged.
5. **Every label is an observation**: above, below or within every window of its baseline, with how many records
   beyond; no word says what the records mean or why they differ.
6. **Command line first.** `icat outliers` judges; the window reads the same reading when it shows them.

## Why

- **A rank needs no model** of how records arrive. Were the compared interval one more window, it would be as likely
  as any to hold the most, whatever its bursts, so the bound holds exactly, and ties only make it rarer; a fitted
  range's stated chance was off by up to ten times on bursty records.
- **Nineteen windows keep the chance at 1 in 20 each way at worst**; with fewer, chance alone would put a row outside
  too often for its being there to say much. 199 make it 1 in 200, and are about as many intervals as a difference
  timeline reads (ADR-062).
- **A comparison's coverage rules carry over** (R21): a row whose mechanism one interval did not cover, or collected
  otherwise, would be judged against windows of nothing, or of another measurement.
- **An observation is all a count supports** (plan M12): more records than every window held is what was seen, and is
  said that way.

## Consequences

- `icat outliers`, with `--baseline`, `--group-by`, `--evidence-policy`, `--limit` and `--json` (`outliers-v1`).
- With few windows, chance alone puts more rows outside their range, up to 1 in 20 each way, and the reading says how
  many to expect.
- Not built: outliers in the window; bytes; a baseline of another capture; seasonal windows.

## Alternatives considered

- **A prediction range from the windows' mean and spread**, on the counts or on their square roots or logarithms, with
  a Student t multiplier. Rejected: its stated chance held only where records arrive independently, which they do not.
- **A test of two Poisson rates over the whole intervals.** Rejected for the same reason: it takes every record for an
  independent event.
- **Windows of a fixed number, whatever the compared interval's length.** Rejected: a count over a window of another
  length is not one to rank the compared interval's among.
