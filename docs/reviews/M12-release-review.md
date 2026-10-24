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

## Addendum, revision 757

The window compares by executable (Rows) and with another saved capture (Another saved capture…, its whole recording
the baseline), in the command line's words, opening an executable's rung where it groups processes by executable and
saying why not where it cannot (`comparison-v1` §8a). Both forms of the comparison are now in the window; nothing else
above changes.

## Addendum, revision 758

Saved comparison specifications ship on the command line (`icat workspace comparison`, `icat compare --saved`,
`comparison-v1` §8c, `workspace-v22` §7, ADR-061): an investigation keeps a comparison under a name, by revision - its
two intervals, each of a session of the investigation in that session's own time, what it compares by and what each
row is of - and running it reads the sessions as they are then. It keeps no answer, so none goes stale, and a redacted
report never carries its name. Of the parts not built, difference timelines and graph overlays remain, and the window
neither saves nor reopens a comparison yet; nothing else above changes.

## Addendum, revision 759

The window saves and reopens comparisons (`comparison-v1` §8c): Save in the investigation… keeps the comparison shown
in the investigation its session was opened from, by a name it asks for, saying whether the name replaces one; the
investigation's Comparisons… lists them in the command line's words and opens one in InterCat's window as saved, or
removes one. Saved comparison specifications are now in the window as on the command line; of the parts not built,
difference timelines and graph overlays remain first, and nothing else above changes.

## Addendum, revision 760

A row's difference timeline ships on the command line (`icat compare --timeline <rank>`, `comparison-v1` §8d,
ADR-062): each interval cut into as many equal columns, the k-th of one compared with the k-th of the other as two
intervals are, each column judged by its own coverage and collection, read with the comparison so the columns add up
to it. M12's baseline and difference timelines are met there; the window draws none yet, and graph overlays come next
among what is not built. Nothing else above changes.

## Addendum, revision 761

The window draws a row's difference timeline (`comparison-v1` §8a, §8d): Timeline reads the comparison with its
columns and draws the chosen row's, the baseline's bar beside the compared interval's on one rate scale, a column pair
that cannot be compared hatched, and a column cursor whose column is said in the command line's words. Baseline and
difference timelines are now in the window as on the command line; graph overlays come next among what is not built,
and nothing else above changes.

## Addendum, revision 762

The window shows a comparison on its graph (`comparison-v1` §8e, ADR-063): Show on the graph marks each drawn edge by
how the rows it draws changed, a glyph in a ringed disc on its stroke, not comparable wherever one of its rows is, and
a comparison by executable on every edge of its processes with a broken ring. The line beneath the graph's header,
each edge's card and the relationship table say the same in words, an unmarked edge is said to hold no record in
either interval, and rows no edge draws are counted. Graph overlays are now built; recurring patterns, outliers and
causal navigation remain, and nothing else above changes.

## Addendum, revision 763

`icat patterns` lists the recurring communication patterns (`contracts/patterns-v1.md`, ADR-064): every TCP connection
the capture saw open, UDP flow and RPC call grouped by its mechanism, its service port or RPC interface and procedure,
and where its server end is, with who took part in each role and how far through the session it recurred; a connection
seen open at neither end, a flow between dynamic ports and a call no start named are counted apart. The explainable
pattern groups M12 delivers are now on the command line; the window's patterns, outliers and causal navigation remain,
and nothing else above changes.

## Addendum, revision 764

The window lists the recurring communication patterns in the command line's words (Recurring patterns…,
`contracts/patterns-v1.md` §7, ADR-065): the list, the chosen pattern's every end, what no pattern holds and the
coverage, and Select its processes, which makes a pattern's processes the selection Enter turns into their records.
M12's explainable pattern groups are delivered on both surfaces; outliers and causal navigation remain, and nothing
else above changes.

## Addendum, revision 765

`icat links` navigates the links a session's evidence records (`contracts/links-v1.md`, ADR-066): a child's creation
record naming its parent, a call reaching the call that served it through its one ALPC message, and a TCP connection
the capture saw one end make, each at its strength, followed only where the evidence policy admits it. The exit test
that a synthetic known dependency is traversable and unrelated simultaneous events are not labeled causal is now met
on the command line: in "P8: a known dependency is traversable link by link, and processes created and calling at the
same moment are never linked", a shell's child's calls lead to the service that served them, while a process created
and calling in the same instant is listed only as near in time, and no chain reaches or leaves it. Outliers,
bottleneck views and the window's links remain, and nothing else above changes.

## Addendum, revision 766

The window follows the same links (Follow its links…, `contracts/links-v1.md` §8): what led to the selected process
and what it led to side by side, a link followed to its process with Enter and back with Alt+Left, links not followed
and nearness in time listed apart and never followed, and Select this process making the process reached the
selection. Causal navigation is delivered on both surfaces; outliers and bottleneck views remain, and nothing else
above changes.

## Addendum, revision 767

`icat waits` gives M12's bottleneck views on the command line (`contracts/waits-v1.md`, ADR-067): the time each
process spent inside the RPC calls it made, from each call's validated start to its stop, by interface and by the
process that served them through ALPC, with that server's own time measured over the linked calls alone. A call
without a validated span is counted by why, never timed, and no wait is said to have blocked or delayed anything, so
no critical-path or deadlock claim is made. Outliers and the window's waits remain, and nothing else above changes.

## Addendum, revision 768

`icat outliers` gives M12's optional outlier detection on the command line (`contracts/outliers-v1.md`, ADR-068): an
interval's rows judged against the windows of a baseline, each window as long as the interval, a row listed where its
records are above or below every window's. Its baseline window, its minimum sample size - 19 windows - and its
uncertainty - at most 1 time in one more than the windows each way, assuming nothing of how records arrive - are
stated beside the rows. The exit test that outlier labels describe observations, not malware or root cause, is now met
on the command line: "R18: an outlier is said in one set of words, the command line's and the window's, each
describing what was counted and never why" checks every label and note against such words. The window's waits and
outliers remain, and nothing else above changes.

## Addendum, revision 769

The window lists the same waits (Time inside calls…, `contracts/waits-v1.md` §7): each caller's calls by interface and
by the process that served them, the most time first, with the server's own time over the linked calls, the servers
callers waited on, and what no time is said for, in the command line's words. Opened with a process selected, it lists
that process's calls and the calls it served, and Select caller and server makes a wait's two processes the selection
together. Bottleneck views are delivered on both surfaces; the window's outliers remain, and nothing else above
changes.

## Addendum, revision 770

The window judges its time scope the same way (Outliers…, `contracts/outliers-v1.md` §7): against all the session
keeps before it or all after it, by process or by executable, the rows above or below every window listed in the
command line's words, and the chosen row's windows drawn bar by bar, each bar said in words, beside the scope's. Show
in the window opens the row as a comparison's row is opened. Outlier detection is delivered on both surfaces, its
labels the same observations; every M12 view is now built on both, and what remains is the milestone's exit review.
Nothing else above changes.
