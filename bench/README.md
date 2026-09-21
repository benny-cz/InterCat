# InterCat benchmark artifacts

Benchmark results are evidence, not timeless product claims. Each committed result records its runtime,
machine/build context, input size and limitations.

Regenerate the current IC-009 portable probe in Release mode:

```powershell
dotnet run --project tools/InterCat.JournalProbe/InterCat.JournalProbe.csproj -c Release -- bench/results/journal-probe-portable.json 10000 7
```

`journal-probe-portable.json` measures the disposable version-0 admission and replay experiment against
a synthetic original-evidence copy. It explicitly has `decisionReady: false`: it is not an ETL
benchmark, does not exercise elevated ETW acquisition or disk saturation, and cannot close ADR-008.

The IC-009 evidence-gate artifacts are produced by the ownership-safe elevated comparison harness:

```powershell
dotnet run --project tools/InterCat.CaptureComparison/InterCat.CaptureComparison.csproj -c Release -- `
  --output bench/results/capture-comparison-<run-id>
dotnet run --project tools/InterCat.CaptureComparison/InterCat.CaptureComparison.csproj -c Release -- `
  --output bench/results/capture-comparison-<run-id>-stacks --request-stacks true
```

It runs the same TCP seed/settings twice, once through the disposable callback-envelope journal and once
into separately labeled diagnostic ETL, then replays both through the same admission table and evaluates
each against its own truth log. The output directory must not already exist. The tool refuses without
elevation before starting ETW or creating the result directory, and exits non-zero while any decision
blocker remains.

`--request-stacks true` asks ETW to attach call-stack extended items. Extended data is opt-in per
enablement, so without it no record carries an extended item and envelope fidelity for extended items
stays untested. Stack walking also slows delivery, so the reorder grace defaults to 6 seconds when
stacks are requested.

Without `--series` or `--levels` the harness runs the declared five-level series: `paced`, `steady` and
`peak` raise the workload's own rate, while `queue-bound` and `disk-bound` hold the peak rate and shrink
a bound instead, because a machine faster than the fixture never reaches its queue by running the fixture
harder. `--help` prints every level with its settings and its intent. `--series quick` runs the paced
level alone and says in its blockers that a point is not a series.

Before any capture starts, the harness measures the volume it will write to and reports it against the
section 12 reference device. A rate without its machine is not a measurement.

Four runs are committed. `capture-comparison-20260921-baseline` and `-stacks` are single points in the
older `intercat.capture-comparison.v0` shape, which wrote every truth operation into the result.
`capture-comparison-20260921-series` and `-series-stacks` are the five-level series ADR-008 cites; their
per-level documents use `intercat.capture-comparison.v1`, which keeps every counter and criterion whole
and reduces the per-operation rows to the ones that failed, bounded and counted. `capture-comparison-20260921-series-stacks` is the run ADR-008 is accepted on: it reports
`decisionReady: true` with no blockers. The clean series reports one, which is the statement that it did
not request extended data - a setting rather than a gap. The two single-point runs predate ADR-007 and
still carry their "outside the support matrix" blocker; they are kept as the evidence they were.

Only counters are committed. A run's `.ijp0` journal and `.etl` file hold records from every process on
the machine, so they are gitignored and stay local (P16). The single-point runs commit their fixture
truth logs, because they are the independent expected result those runs are scored against; a series'
per-level truth logs are gitignored instead, because its high levels reach tens of megabytes and every
one of them is reproducible from the recorded seed and level.
