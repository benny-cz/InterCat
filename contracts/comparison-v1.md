# InterCat comparison v1

Status: **implemented** for two intervals of one session's generation, on the command line (`icat compare`, revision
752) and in the window (Compare with…, revision 753, §8a). Comparing two captures, difference timelines, graph
overlays and saved comparison specifications are not defined at this version (§9).

This contract fixes what plan §15's M12 asks of a comparison - normalized rates beside absolute counts, ranked changed
relations, unavailable measurement told apart from disappeared activity, and side-by-side values with an explicit reason
wherever no change can be stated - for its first form: two intervals of one generation, compared row by row. It owns
no bytes: every value is read from the generation's published segments and indexes through the queries the window and
the command line already answer (`metrics-v1`, `relations-v1`, `coverage-v2`), and a comparison changes none of them.
ADR-058 records the decisions.

## 1. A request

- A session, read one generation at a time (§6).
- A **baseline** and a **compared** interval, each half-open (I3) in 100-nanosecond presentation ticks, as every
  `--interval` takes them. They may differ in length, overlap, or be the same interval.
- A **metric** (§3), `Records` by default.
- An **evidence policy**, `IncludeCorrelated` by default, which admits relationships and records as the overview admits
  them.

## 2. Rows

A row is either

- a **relationship between two processes** the overview draws: paired TCP incarnations and UDP flows whose two ends both
  belong to the capture's processes (`relations-v1` §5), and RPC calls linked to the calls that served them where the
  capture collected ALPC (`operations-v1` §5c), keyed by the relationship's key; or
- one process's **connections over one transport to one other host's address**, as the census counts them (`relations-v1`
  §5d) - the connections the graph draws to its Other hosts node - keyed `hosts:<transport>:<instance>@<address>`. Who is
  at the address is not observed (P7).

Every such row with at least one record in either interval is compared; the others are counted as **quiet** and have
no row. A relationship's records in an interval are those a brushed ranking counts there (both ends' records, the
interval's own); a reach's are its connections' records whose session time falls in the interval, as `icat evidence
--host` lists them. A record without a usable session time is in no interval.

## 3. Metrics

| Metric | A row's value over an interval |
|---|---|
| `Records` | Its records there: an observed count, exact. |
| `BytesSent` | The transport-observed bytes sent across, under sender accounting, each transfer counted once at its sender (`metrics-v1` §4): for a relationship, what its two ends' sends on its channels carried; for a reach, what the process's sends to the host carried. What the other host sent has no sender among the machine's records and is not counted. RPC calls record no size (§5). |

A byte value is the sum of the sends that measured a size. A send that recorded none is counted apart and makes the
value a **lower bound**; a value with no measured send and some unmeasured is **unknown**, never zero; nothing sent is
zero (R3, I6). A row also carries its records in each interval whatever the metric.

## 4. Changes

A value's **rate** is over its **whole interval** (`metrics-v1` §7), never over the part of it the capture covered or the
span its records happen to fill. Two rates are compared **exactly**, in integers: the compared value times the baseline's
length against the baseline value times the compared length.

Where both values are exact (§5 passed):

| Change | When |
|---|---|
| `Unchanged` | The rates are equal. |
| `Increased`, `Decreased` | The compared rate is higher, or lower. |
| `Appeared` | Nothing in the baseline - no record, or under `BytesSent` no send, measured or not - and something in the compared interval. |
| `Disappeared` | Something in the baseline and nothing in the compared interval. |

Where one value is a lower bound, a change is stated only where the **bound alone decides it**: `Increased` where the
compared bound's rate already exceeds the baseline's exact rate, `Decreased` where the baseline bound's rate already
exceeds the compared exact rate. The row says so (`fromLowerBound`): the change is at least as large as stated. Two
lower bounds, or a bound that does not decide, state no change (§5). An unknown value decides nothing.

## 5. Not comparable

A row whose two values are not of one measurement states both values, their rates and its reason, and no change
(`NotComparable`). The first that applies is its reason:

1. **Bytes asked of RPC calls**, which record no size.
2. **The baseline's coverage** of the row's mechanism (`coverage-v2` §4) - for RPC calls the worse of RPC's and ALPC's,
   whose messages link each call to the call that served it - is anything short of `Covered`: not collected, a partial
   gap, reduced fidelity or unknown. Its sentence, with the fact that decided it, is the reason.
3. **The compared interval's coverage**, likewise.
4. **The capture's sources changed**: the epochs either interval spans did not all collect the mechanism through the same
   descriptors, so its counts there were not made alike.
5. **Bytes that do not decide**: sends that recorded no size, in one interval or both, whose bound does not decide (§4).

So a mechanism a capture did not collect in one interval never reads as activity that disappeared or appeared there,
and a change of the capture's sources never reads as a change of the workload (M12, R21, P1).

A partial gap is **not bounded**. A lost record may have been a process's lifecycle record or a connection's only record
at one end, which moves the attribution or pairing of other records, so the records read in a gapped interval are
neither an upper nor a lower bound of the row's own (§9).

## 6. One generation

Every read a comparison makes - the overview, both intervals' counts, byte measures and connections, and the ledger and
clock that judge them - is of one generation, which the answer names (I16). A session that publishes between reads is
read again, up to three times; one that publishes between each attempt's reads is refused in words. A directory that
begins holding another session is refused.

## 7. Ranking

The comparable rows first, by the absolute difference of their rates, the largest first; then the rows that cannot be
compared, by the larger of their two rates of records, the busiest first; each tie by key, ordinal. A comparison read
twice lists its rows alike.

## 8. The command line

```
icat compare <session-directory> --baseline <start:end> --interval <start:end>
    [--rank-by records|bytes-sent] [--limit <n>] [--json]
```

Each bound is a count of ticks or a time with its unit, as every `--interval` takes it. Its text states both intervals
and their lengths, each interval's coverage in `icat overview`'s words, and a table of the rows the most changed first,
as many as `--limit` (50 by default): each row's two ends, the process that made its connections first, as `icat
overview` names a relationship, and a host by its address; the transport and ports; each interval's value and rate; and
its change, or `not comparable [n]` naming its reason once beneath the table.

`--json` answers `comparison-v1`:

| Field | Meaning |
|---|---|
| `contract`, `sessionPath` | `comparison-v1`, and the session's folder. |
| `comparison.sessionId`, `generation` | The generation every value was read from. |
| `metric`, `policy` | §3, §1. |
| `byteDomain`, `accounting` | `TransportObserved` and `SendSide` under `BytesSent`; null under `Records`. |
| `baseline`, `compared` | Each interval (`startTicks`, `endTicks`, `spanTicks`) and its coverage: every mechanism's state and reason, never one rolled-up state. |
| `relationships`, `quiet` | The rows in all, and the quiet ones (§2). |
| `rows` | The rows, ranked (§7), as many as `--limit`. |

Each row: `key`; `kind`, `Processes` or `OtherHost`; `mechanism`; `source` and `target`, each `{ instance, processId,
name }`, a reach's target null and its `address` named; `connected`, `SourceToTarget`, `TargetToSource`, `Both` or
`Unobserved`, where a reach's source is its process and its target the host; `acceptedPorts`, at the end that accepted;
`hostPorts`, a reach's ports at the host; `baseline` and `compared`, each `{ records, value, measured, unmeasured,
perSecond, coverage }`; `change`; `fromLowerBound`; `differencePerSecond`, null where not comparable; and `reason`,
null where a change is stated. A rate is for presentation, rounded half to even to three decimal places; the integers
it comes from are the row's (`metrics-v1` §7).

## 8a. The window

Compare with… (Ctrl+B), beside the time scope where it is an interval - a brush, or the visible range while the
timeline is zoomed - compares that interval with a baseline: the same length just before it or just after it, where
that lies within the session's time; the whole recording, or the span of its records where its capture recorded no
stop; or an interval typed in seconds of session time. It compares by records or by bytes sent across, under the
evidence policy the window counts by, and reads the session afresh at each choice, a newer choice letting an earlier
one go (ADR-057).

Its rows are §8's, in `icat compare`'s words (R18), but for two processes the capture saw open no connection between,
which it joins by "↔" as the window's relationship table joins them; it lists the 200 most changed and counts the
rest. It says above them what the capture covered over each interval, and beneath them each reason once and the notes
the rows need. Enter, a double click or Show in the window closes it and opens what the chosen row compares: a
relationship as a double click on its edge opens it, or the process whose connections to another host it compares,
whose inspector lists the hosts it reached. Esc closes it, choosing nothing.

## 9. Not defined at this version

- **Two captures.** Entity matching across sessions needs a qualified mapping (plan M12): an executable's name alone is
  no identity, and a process of one capture is never taken for another's by it. Two captures' coverage ledgers would be
  compared as two intervals' epochs are here (§5).
- **A bounded change under a gap.** The losses a ledger counts bound the records a mechanism lost, not a row's (§5); a
  bound per row would need what a lost record could have moved.
- Difference timelines, graph overlays, saved comparison specifications, recurring patterns, outliers, causal
  navigation and redacted comparison reports (plan M12).
- Wall-clock bounds, which `icat timeline --wall-clock` states; a comparison takes session ticks.
