# ADR-033: A duration names its interval, its cohort and its statistic

- Status: accepted for M3
- Date: 2026-09-27
- Decision owners: InterCat maintainers
- Relates to: §5 (duration and latency), §5.3 (the matrix), §19.2 (cohorts, censoring, summed versus busy time), §23,
  §24, R3, R21, ADR-031, ADR-032, `contracts/metrics-v1.md` §8a, `contracts/query-identity-v1.md`

## Context

§5.3's matrix defines `Duration` on the logical-operations basis "with a named cohort", and §5 lists the named
intervals a duration can be - a client call, a server execution, an I/O completion, an ALPC send-to-receive, a wait -
and says they are not interchangeable. §19.2 says a latency distribution identifies its cohort, the operations
completed in range by default and those started in range on request, marks left- and right-censored operations, and
distinguishes summed operation time from the union of busy time. Until now a request could name none of these, and a
duration was unavailable.

ADR-031 pairs RPC calls on each side of a process: a client call from its start to its stop in the calling process, a
served call in the serving one. The two are the §5 intervals "client call" and "server execution", and they differ by
design: a client call spans the transport and the server's work.

## Decision

1. **Three request members.** `durationInterval` (`EN-DurationInterval`: 1 `ClientCall`, 2 `ServerExecution`,
   3 `IoCompletion`, 4 `AlpcSendToReceive`, 5 `Wait`, 6 `MappingLifetime`), `cohort` (`EN-Cohort`: 1 `CompletedInRange`,
   2 `StartedInRange`) and `statistic` (`EN-DurationStatistic`: 1 `Median`, 2 `Percentile95`, 3 `Maximum`). They apply to
   `Duration` alone and are refused on any other metric.
2. **The interval has no default.** A duration without one is refused: picking one would silently choose between two
   quantities §5 keeps apart. A mapping lifetime is asked on the resource-topology basis, and any other interval on the
   logical-operations basis; the other combinations are refused. An interval no derived operation measures is
   `NoLogicalOperations`.
3. **The cohort and the statistic have defaults,** written out before a request is identified: the calls completed in
   range (§19.2) and the median. So leaving them implicit and naming them are one query identity.
4. **The side is the interval.** A client-call duration reads the client side's calls and a server execution the
   server side's; the other side's calls measure another interval and are excluded as a projection is, and counted.
5. **Censoring is stated, never measured.** A call of the cohort without both records - a stop whose start is not in the
   evidence, a start still open at capture end, a record with no activity id or of a reused one - is stated by state and
   gets no duration. None is given a lower bound as though it were a measurement.
6. **A distribution, not a mean.** The answer holds the count, minimum, median, 95th percentile and maximum, each a
   duration one call took by nearest rank, with the summed call time and the busy time their union covers. The value is
   the named statistic. A mean is not offered: one long call moves it arbitrarily, and it is a duration no call took.
7. **Groups rank by the statistic and do not partition it.** Each call belongs to one group, but a median does not add,
   so a result says its groups are not parts of its value; a remainder is the distribution of the calls it merges.

## Consequences

- `icat metric --metric duration --duration client-call|server-execution [--cohort started] [--statistic p95|max]`
  answers durations over an interval, for one owner, ungrouped or ranked by process, executable or mechanism, with the
  counted records as evidence.
- The canonical form gains three members, written for a duration alone, so no existing identity changes; the golden
  corpus gains two lines.
- Durations of I/O completions, ALPC exchanges and waits wait on operations of those kinds, and a mapping lifetime on the
  resource-topology basis.

## Alternatives considered

- **Mix both sides into one "call duration".** Rejected: a client call and a server execution are different intervals
  (§5), and a distribution of both describes neither.
- **Report a mean beside the percentiles.** Rejected: a mean is a duration no call took, and one long call - a real
  session had an 11 s call among 3,945 whose median was 44 µs - makes it describe none of them.
- **Give a call open at capture end the time it had run by then.** Rejected: that is a lower bound, and folding it into a
  distribution of measured durations would read it as a short call (§19.2's right-censoring).
