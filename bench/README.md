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

`capture-comparison-20260921-baseline` and `capture-comparison-20260921-stacks` are the two runs ADR-008
cites. Both still emit `decisionReady: false`: one configured load point is not the queue and disk
saturation series the gate requires, and the host build is outside the section 1.3 support matrix.

Only counters are committed. A run's `.ijp0` journal and `.etl` file hold records from every process on
the machine, so they are gitignored and stay local (P16). The fixture's own truth logs are committed,
because they are the independent expected result the run is scored against.
