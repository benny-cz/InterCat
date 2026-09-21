# ADR-007: The supported build matrix

- Status: accepted for M0
- Date: 2026-09-21
- Decision owners: InterCat maintainers
- Relates to: ADR-001 (toolchain pins), ADR-003 and ADR-004 (mechanism scope), ADR-008 (evidence gate)

## Context

Section 1.3 lists supported build candidates and says they are "revisable by ADR-007 once M0 measures
them". Until now that ADR did not exist, and the matrix named Windows 11 24H2 as its only primary build.

Every M0 measurement was taken on Windows 11 **25H2**, build `10.0.26220.9223` x64, which was outside the
matrix. The consequence was mechanical and correct: P27 capped every mechanism at experimental evidence,
because a measurement on an untested build cannot promote a tier no matter how good it is. Four separate
artifacts carried the same sentence — the build is not one we support — while the thresholds under it were
being met at 100%.

That is the right behaviour for an unsupported build. It is the wrong answer to the question of which
builds InterCat supports, which nobody had decided.

## Decision

**Windows 11 25H2 x64 joins Windows 11 24H2 x64 in the primary tier.** The rest of the matrix is
unchanged.

A release is listed by its **exact build numbers**, never by a range. A range would silently accept a
build nobody has run the fixture corpus on, which is precisely the claim section 1.3 forbids. 25H2 is
therefore listed twice:

| Entry | Build | Branch |
|---|---|---|
| Windows 11 25H2 x64 | 26200 | retail servicing |
| Windows 11 25H2 x64 | 26220 | pre-release servicing |

A pre-release servicing branch of a supported release is supported on the same terms, and **every result
states which of the two it ran on**. "Supported" alone would hide the difference between a retail build
and a branch whose behaviour can still change under a measurement. `BuildSupport.Describe()` produces that
statement, and the capability report, the CLI and the comparison harness all print it rather than a
boolean.

A build outside the matrix stays untested. It is never reported as probably working, and P27 still caps
what may be claimed from it.

## Consequences

Re-running the M0 fixture corpus on this build promotes what the thresholds already supported:

- `FX-TCP-001` reaches **traffic visualization**, the tier its six section 14.2 criteria have met at 100%
  since the first measured run. The tier was held down by the build, not by the evidence.
- `FX-PIPE-001` stays **unsupported**. Named pipes emit nothing through `Microsoft-Windows-Kernel-File`
  on this build, and a supported build does not change a measured absence (ADR-003).
- `FX-RPC-001` stays **experimental evidence**. Its gaps are structural — no byte domain on any
  descriptor, no interface lifetime — and no build fixes those (ADR-004).
- ADR-008's "outside the support matrix" blocker is removed. What remains of that gate is recorded there.

A promotion that follows from adding a build to a matrix deserves stating plainly: the evidence did not
improve, the question of which builds we support was answered. The measurements behind every tier are the
same runs, on the same machine, recorded before this decision was taken.

## Alternatives

- **Leave the matrix at 24H2 and measure on a 24H2 machine.** Cleanest evidence, and still the right
  thing before a release: 24H2 remains in the matrix and its fixture corpus is not yet run. It was not
  chosen as the only path because it blocks every M0 tier on hardware nobody has, while the product's
  development target is in fact 25H2.
- **Accept any build above 26100.** Rejected: it is the blanket assertion section 1.3 exists to prevent.
- **Treat a pre-release branch as unsupported.** Defensible, and it is what the previous state amounted
  to. Rejected because it would make the machine the work is done on permanently unable to produce a
  supported measurement, while hiding a real distinction is the actual risk — and that risk is answered
  by stating the branch in every result instead.

## Reversal cost

Low. The matrix is one list in `SupportedBuilds`, and every tier is recomputed from measured counters, so
removing an entry re-caps the claims that depended on it at the next run. What would not reverse is a
release note already published from a promoted tier, which is why the branch is stated in the artifacts
rather than only here.
