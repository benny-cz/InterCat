# M12 release review — comparative and causal exploration

Date: 2026-10-23, at plan revision 752
Scope reviewed: M12's first shippable part, two intervals of one session compared row by row (`icat compare`,
`contracts/comparison-v1.md`, ADR-058)
Method: §15.2 asks, before a post-v1 milestone is committed for shipment, for the user problem, the exact new evidence
obtainable, the supported environments, the expected overhead, the deployment burden and the exit fixtures. Each is
answered below for what revision 752 ships, and M12's exit tests are checked against named tests; what is not built is
listed as not built.

## The user problem

On a heavily used Windows machine a person wants to see which processes communicate and who connected to whom; the
overview, the graph and the ranked table answer that for a capture or an interval of it. The question that follows is
what changed - after an update, when an application started failing, while a service was restarted - and it was
answered only by reading two intervals' tables side by side and dividing by their lengths by hand, with no word on
whether the capture measured both alike. A difference read that way can be the capture's: a provider that stopped
delivering, a release of the oldest records, records lost under load.

## The new evidence obtainable

None is collected: a comparison reads what a session already holds. What is new is a statement the evidence supports
and nothing more:

- each relationship's, and each process's connections to another host's, records or bytes sent across in two intervals,
  as counts and as rates over each whole interval, compared exactly;
- a change only where both intervals covered the mechanism through the same sources, `Appeared` and `Disappeared` only
  where the interval with nothing covered it;
- elsewhere both values, with the reason in the words the capture's coverage is stated in everywhere else.

## Supported environments

Every environment that opens a session: the command line wherever .NET 10 runs, on any session with a coverage ledger.
A session without one - an import from before coverage was recorded, or a legacy generation - compares nothing, saying
its coverage is unknown, and shows both values. The window has no comparison view yet.

## Expected overhead

None on a capture. A comparison reads the overview once and each interval's counts, byte measures and connections to
other hosts once, each through the queries the window's brushes and lists already use: a finished session's from its
indexes, a live session's from its chunk tiles where they bind, opening no segment the interval does not meet (§12.1,
P25). Its memory is the rows it lists. A live session that publishes between reads is read again, three times at most.

## Deployment burden

None beyond the command line already shipped: no service, provider, driver, privilege or setting. `icat compare` is a
read-only command over a session folder.

## M12's exit tests

| Exit test | Verdict | Evidence |
|---|---|---|
| Identical captures compare as equal | Met: an interval compared with itself, and since revision 756 two captures of the same records, are unchanged everywhere, by records and by bytes | `SessionComparisonTests.AnIntervalComparedWithItselfIsUnchangedEverywhere`; "I14: two captures of the same records compare as equal, row by row" |
| Changed capture profiles cannot masquerade as workload changes | Met within a session and, since revision 756, across captures | "R21: a capture that collected a mechanism otherwise than the other states no change of it"; "R21: a mechanism the capture stopped collecting makes a relationship not comparable, never disappeared"; "R21: a capture whose sources collected a mechanism otherwise in one interval states no change of it"; RPC calls judged by ALPC's coverage too (`RpcCallsAreComparedByTheirRecordsAloneJudgedByRpcAndAlpc`) |
| Differing durations normalize correctly | Met | `RatesAreOverEachWholeIntervalAndTheMostChangedRankFirst`: twice the records in twice the time is unchanged, compared in integers |
| Uncertainty propagates | Met for coverage and unmeasured sizes | A partial gap or unknown coverage states both values and its reason (`CoverageShortOfCoveredStatesBothValuesAndNoChange`); "R3: a byte comparison's lower bound decides a change only where the bound alone exceeds the other rate"; "R21: icat compare says why it states no change, each reason once beneath the table, never a zero" |
| A synthetic known dependency is traversable, and unrelated simultaneous events are not labeled causal | Not built | Causal navigation is not built. Nothing a comparison states is causal: it states rates and their reasons only. |
| Outlier labels describe observations, not malware or root cause | Not built | No outlier detection exists. |

## Not built, in M12's order

- A baseline and difference timeline, graph overlays and saved comparison specifications.
- Recurring communication patterns, outlier detection with its baseline, sample size and uncertainty, causal navigation
  over evidence-backed links, and bottleneck views.
- Annotations and bookmarks across a comparison.

## Verdict

The interval comparison is ready to ship on the command line: it states only what both intervals measured alike, and
shows everything else side by side with its reason, which is M12's fallback where captures are not comparable. M12 is
not complete; the parts listed above follow, the window's view of this comparison first, since the person this product
serves works in the window.

## Addendum, revision 753

The window's view of the comparison ships: Compare with… (Ctrl+B), beside the time scope where it is an interval,
compares it with the same length just before or after it, the whole recording or an interval typed, by records or
bytes sent across, in `icat compare`'s words, and Enter on a row opens what it compares (`comparison-v1` §8a). The
window is now among the supported environments; nothing else above changes.

## Addendum, revision 754

A comparison shares as a redacted report, from the command line (`icat compare --output <path> --share-redacted`) or
the window (Share redacted…), under the sharing report's redaction policy (`comparison-v1` §8b): its processes,
executables and other hosts are random tokens, and it records its scope, byte domain, accounting, each interval's
missing sources and that no alignment applies, the last of the deliverables this review listed as reports. Its leak
tests give every name, path, key and address a secret and every PID and port a number a leak would show, and find none
of them in JSON or CSV. Nothing else above changes.

## Addendum, revision 755

A comparison groups its rows by executable on request (`icat compare --group-by executable`, `comparison-v1` §2a,
ADR-059): each executable's processes together by the path their image was witnessed at, a grouping and never an
identity, so a process that restarted between the two intervals keeps one row instead of one disappeared and one
appeared, and processes of no witnessed executable are counted apart rather than merged. This is M12's entity grouping
for one session; the mapping two captures need pairs their rows by the same keys and follows. Nothing else above
changes.

## Addendum, revision 756

Two captures compare (`icat compare --baseline-session`, `comparison-v1` §2b, ADR-060): by executable only, each
capture's processes grouped by path and paired across the captures by it, a grouping that says whether the two name
one host identity and never claims an identity; each interval on its own capture's clock, each capture read at its own
generation; and a change stated only where both captures covered the row's mechanism and collected it through the same
descriptors. The first two exit tests are now met across captures, each by a named test, and comparing two captures
leaves the list of what is not built. The window compares neither two captures nor executables yet; nothing else above
changes.
