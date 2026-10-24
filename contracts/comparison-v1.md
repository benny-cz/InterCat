# InterCat comparison v1

Status: **implemented** for two intervals of one session's generation, on the command line (`icat compare`, revision
752), in the window (Compare with…, revision 753, §8a), and as a redacted report to share from either (revision 754,
§8b); its rows are of processes or, since revision 755, of executables (§2a); and since revision 756 two captures
compare, by executable (§2b) - on the command line, and since revision 757 in the window too. Since revision 758 an
investigation saves a comparison under a name and the command line runs it, and since revision 759 the window saves
and reopens one (§8c). Since revision 760 the command line states a row's difference timeline, and since revision 761
the window draws the chosen row's (§8d). Since revision 762 the window shows a comparison on its graph, each edge
marked by how the rows it draws changed (§8e).

This contract fixes what plan §15's M12 asks of a comparison - normalized rates beside absolute counts, ranked changed
relations, unavailable measurement told apart from disappeared activity, and side-by-side values with an explicit
reason wherever no change can be stated - for two intervals of one generation, compared row by row, and two captures.
It owns no bytes: every value is read from the generation's published segments and indexes through the queries the
window and the command line already answer (`metrics-v1`, `relations-v1`, `coverage-v2`), and a comparison changes
none of them. ADR-058 records the decisions, ADR-059 how rows are grouped by executable, ADR-060 how two captures
compare, ADR-061 how a comparison is saved, ADR-062 how a difference timeline is cut and ADR-063 how a comparison
marks the graph.

## 1. A request

- A session, read one generation at a time (§6); or two, the baseline's interval of one and the compared interval of
  the other (§2b), each read one generation at a time.
- A **baseline** and a **compared** interval, each half-open (I3) in 100-nanosecond presentation ticks, as every
  `--interval` takes them. They may differ in length, overlap, or be the same interval.
- A **metric** (§3), `Records` by default.
- An **evidence policy**, `IncludeCorrelated` by default, which admits relationships and records as the overview admits
  them.
- A **grouping**: a row per process, `Process`, by default, or per executable, `Executable` (§2a).

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

## 2a. By executable

Grouped by **executable**, a row is an executable's: every relationship the overview draws between a process of one
executable and a process of another - or of the same one - over one mechanism, together, keyed
`executables:<mechanism>:<first>|<second>` by the two executables' group keys in ordinal order; or every connection an
executable's processes made over one transport to one other host's address, together, keyed
`executable-hosts:<mechanism>:<executable>@<address>`. An executable is the overview's group: the path its processes'
image was witnessed at, letter case aside, named as the window names its groups, with the fewest folders that tell two
of one name apart. It is a grouping, never an identity (P6, ADR-059): its processes stay distinct and none is taken
for another, so an executable whose process restarted between the two intervals keeps one row, both processes in it.

A row's records, bytes, coverage and change are §3 to §5's, summed over its processes' relationships or connections:
what each sent across, each transfer at its sender, a lower bound where any send recorded no size. Who made a
relationship's connections or calls is summed in its two directions, from the executable whose key orders first to the
other and back, and the ports its connections arrived at are each port any of them did.

A process whose executable was not witnessed is in **no row**: no path groups it with any other, and grouping it with
the other unwitnessed processes would merge strangers. Its relationships and connections to other hosts with a record
in either interval are counted apart (`unmatched`), with their records in each interval, and said beneath the rows;
one with none is not counted, and quiet counts executable rows only.

## 2b. Two captures

`--baseline-session` reads the baseline interval from another capture than the compared one. Two captures compare by
executable (§2a) only: a process of one capture is never another's, and nothing observed maps the processes of one
onto the other's (P6, R22). Each capture's rows are its own processes', grouped by the path their image was witnessed
at, and a row of one is paired with the other's of the same key - the same paths, letter case aside. That pairing is a
grouping, never an identity: the file at a path may differ between the captures, and two captures of different host
identities pair a path all the same, which the comparison says (`sameHost`, evidence of one host and never proof of
it). Executables are named over both captures' groups, so two of one name, one in each, are told apart. ADR-060
records the decisions.

Each interval is in its own session's presentation ticks, its rate over its own whole interval, so no alignment of the
two clocks is applied or needed; each capture is read at one generation of its own (§6). A row's coverage in each
interval is its own capture's (§5's rules 2 and 3). Rule 4 applies to each capture's own epochs over its interval, and
then across them: where the two captures collected the row's mechanism through different descriptors their counts were
not made alike, and the row states both values with that reason, so a changed capture profile never reads as a
workload change. Two stores holding one session are refused: its intervals compare as one session's.

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
begins holding another session is refused. Of two captures, each is read at one generation of its own, and the answer
names both.

## 7. Ranking

The comparable rows first, by the absolute difference of their rates, the largest first; then the rows that cannot be
compared, by the larger of their two rates of records, the busiest first; each tie by key, ordinal. A comparison read
twice lists its rows alike.

## 8. The command line

```
icat compare <session-directory> --baseline <start:end> --interval <start:end>
    [--rank-by records|bytes-sent] [--group-by process|executable] [--limit <n>] [--json]
    [--baseline-session <session-directory>]
    [--output <path> [--share-redacted] [--format json|csv] [--overwrite]]
    [--timeline <rank> [--columns <1-100>]]
icat compare --workspace <workspace> --saved <name> [--limit <n>] [--json]
    [--output <path> [--share-redacted] [--format json|csv] [--overwrite]]
    [--timeline <rank> [--columns <1-100>]]
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
| `baselineSessionPath` | Of two captures, the baseline's session folder; null otherwise. |
| `saved` | Of a saved comparison run (§8c), `{ workspace, name, revision }`: the investigation's file, the comparison's name and the revision run; null otherwise. Since revision 758. |
| `timeline` | With `--timeline <rank>`, the ranked row's difference timeline (§8d): `{ rank, key, columns }`, each column `{ baselineInterval, comparedInterval, baseline, compared, change, fromLowerBound, differencePerSecond, reason }`, its measures as a row's; null otherwise. Since revision 760. |
| `comparison.sessionId`, `generation` | The generation every value was read from: of two captures, the compared interval's. |
| `baselineSessionId`, `baselineGeneration`, `sameHost` | Of two captures, the baseline's session and generation, and whether the two name one host identity, null where either names none; all null of one session. |
| `metric`, `policy` | §3, §1. |
| `byteDomain`, `accounting` | `TransportObserved` and `SendSide` under `BytesSent`; null under `Records`. |
| `groupedBy` | `Process` or `Executable` (§2a). |
| `baseline`, `compared` | Each interval (`startTicks`, `endTicks`, `spanTicks`) and its coverage: every mechanism's state and reason, never one rolled-up state. |
| `relationships`, `quiet` | The rows in all, and the quiet ones (§2). |
| `unmatched` | By executable, what no executable groups: `{ rows, baselineRecords, comparedRecords }` (§2a); null by process. |
| `rows` | The rows, ranked (§7), as many as `--limit`. |

Each row: `key`; `kind`, `Processes` or `OtherHost`; `mechanism`; `source` and `target`, each `{ instance, processId,
name }`, a reach's target null and its `address` named; `connected`, `SourceToTarget`, `TargetToSource`, `Both` or
`Unobserved`, where a reach's source is its process and its target the host; `acceptedPorts`, at the end that accepted;
`hostPorts`, a reach's ports at the host; `baseline` and `compared`, each `{ records, value, measured, unmeasured,
perSecond, coverage }`; `change`; `fromLowerBound`; `differencePerSecond`, null where not comparable; and `reason`,
null where a change is stated. A rate is for presentation, rounded half to even to three decimal places; the integers
it comes from are the row's (`metrics-v1` §7). By executable, `kind` is `Executables` or `ExecutableToOtherHost`, and
`source` and `target` are each `{ group, name, path }`: the executable's group key, the name the window gives it, and
the path its image was witnessed at.

`--output <path>` writes the `--json` document to a file instead, complete before it replaces anything, and refuses a
file that is there unless `--overwrite` is given; a file holds every row unless `--limit` is given, being read whole
rather than scrolled. With `--share-redacted` it writes §8b's report instead, as JSON, or as CSV with `--format csv`
or a path ending `.csv`; the comparison itself is written as JSON only, and `--json` and `--output` are not given
together.

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

Share redacted… saves the comparison shown as §8b's report, every row of it rather than the 200 listed: it says first
what such a report keeps and leaves out, as the main window's redacted report does, then asks for the file - JSON, or
CSV by its name - and says beside its buttons where the report went, or why it could not be written, the comparison's
own status still saying what it found.

Rows chooses each process, or each executable's processes together (§2a). Another saved capture… asks for a saved
session's folder and offers its whole recording, or the span of its records where it recorded no stop, as the
baseline, the latest chosen the one offered: two captures compare by executable alone (§2b), and Rows says so while
one is the baseline. A folder that holds no session, or holds the session shown, is said beside the buttons, and the
baseline chosen before is chosen again, its comparison still the one shown. The status names both captures'
generations, and the notes say how the rows pair them.

For a row of executables, Show in the window opens an executable's rung, its processes listed - of a pair, the first
the session shown ran - where the window groups processes by executable. Where it groups them by terminal session, or
no process of the session shown ran the executable, as of another capture's, Show says why instead, and Enter and a
double click open nothing.

Timeline, checked, reads the comparison again with its difference timeline (§8d) - 20 columns of each interval, or as
many as a tick allows - and draws the chosen row's beneath the rows: a slot per column pair, the baseline's bar beside
the compared interval's on one rate scale, and a column pair that cannot be compared hatched, as the timeline hatches
what the capture did not cover, so a quiet bar there never reads as nothing happening. Left, Right, Home and End, or a
click, choose a column, which the line beneath states in §8's words with its reason; choosing another row draws its
columns, the cursor where it was. Unchecked, the chart goes and the comparison shown stays.

Show on the graph closes the window and marks the main window's graph by the comparison shown (§8e).

Save in the investigation… keeps the comparison shown, by name, in the investigation the session shown was opened
from, and a comparison saved there opens in this window as saved, saying the notes about it; Add a note… adds one
(§8c).

## 8b. A redacted report

A comparison shares as a metadata-only report of the sharing report's contract (`intercat-share-report-v1`, kind
`comparison`) under its redaction policy (`docs/design/SHARING-REPORT-REDACTION.md`): pseudonymized, not anonymous. It
records what plan M12 asks a shared comparison to record - its scope, byte domain, accounting, missing sources and
alignment revision:

| Field | Meaning |
|---|---|
| `comparison.comparedBy`, `evidencePolicy`, `groupedBy` | §3, §1, §2a. |
| `byteDomain`, `accounting` | `TransportObserved` and `SendSide` under `BytesSent`; null under `Records`. |
| `alignmentRevision`, `alignment` | Null, and why: both intervals are of one session, read on its own clock, so no alignment of captures applies; or, of two captures, each interval is read on its own capture's clock and rates over whole intervals need no alignment. |
| `captures`, `sameHost` | 1, or 2 where the baseline is another capture's, and then whether the two name one host identity, never which; null of one session. |
| `baseline`, `compared` | Each interval's relative `startTicks` and `endTicks`; its `coverage`, every mechanism's state and reason, and `coverageSummary`, the same in words; and its `missingSources`, each mechanism it did not collect in full, or cannot show it did: not collected, a partial gap, or unknown. |
| `rowsCompared`, `rowsListed`, `complete`, `quiet` | The rows compared and those the report lists, whether it lists them all, and the quiet ones (§2). |
| `unmatched` | By executable, the relationships and connections no executable groups and their records in each interval (§2a); null by process. |
| `ratesMeaning`, `intervalUnit`, `exportedUtc` | What a rate is over (§4), the ticks' unit, and when the report was written. |

Each row: its `rank`, `kind` and `mechanism`; its ends as random tokens, consistent within the report only -
`sourceToken` and `targetToken` for processes, a reach's target null; `sourceExecutableToken` and
`targetExecutableToken` for their executables, keyed by the path a process's group is keyed by, so two processes of
one executable share one and an executable of the same name elsewhere has another, and none where no executable was
witnessed; and `hostToken` for another host's address; `connected`; `baseline` and `compared`, each `{ records, value,
measured, unmeasured, perSecond, coverage }`; `change`; `fromLowerBound`; `differencePerSecond`; and `reason`, one of
§5's sentences, whose only variable parts are mechanism names, a coverage reason of `coverage-v2` §4, epoch numbers
and counts. No process name, PID, executable path, address, port, row key, session identity, generation or session
folder is written. A row by executable, `Executables` or `ExecutableToOtherHost`, names no process: its process tokens
are null, and each executable's token is keyed by its group, as a process row's executable token is.

CSV writes the same a line a row: on every line the policy and its disclosures, what was compared and both intervals,
then the row, then each interval's missing sources (`Udp:NotCollected;Http:UnknownCoverage`) and coverage in words,
and what a rate is over; a comparison with no row writes one line without one (`row_present=false`), so all of that is
still said.

## 8c. A saved comparison

An investigation keeps a comparison under a name (`workspace-v23` §7, ADR-061): its two intervals, each of a member's
session in that session's own ticks, what it compares by and what each row is of - a specification, never an answer.
`icat workspace comparison` saves one, replacing one of that name, and removes one; `icat workspace show` lists those
in force in `icat compare`'s words: "[10.0, 20.0) s of session 1a2b3c4d against [0.0, 10.0) s of it, by records, a
row per process".

`icat compare --workspace <workspace> --saved <name>` runs the one in force under that name, in any case, as the
command line compares two intervals it is given: each session read where the investigation finds it now, which must
hold its capture (`workspace-v23` §3), at the generation found there, so a comparison saved before a live capture grew
reads it as it is. Only how it is shown is chosen: `--limit`, `--json` and `--output` with its options, never a
session, an interval, `--rank-by` or `--group-by`. Its text and its file name the comparison and its revision, and a
redacted report never does, a name being a person's own words (§8b). A run says the notes a person keeps about the
comparison and its rows (`workspace-v23` §7, ADR-069) beneath its rows - a row's by its rank and ends in that run, or
as one the run does not compare - and its document's `saved.notes` carries each with its words and its row's key and
rank, the rank null where the run does not compare the row; a redacted report carries none.

In the window, Save in the investigation… keeps the comparison shown in the investigation the session shown was opened
from, under a name it asks for, saying as one is typed whether it replaces a comparison of that name; its baseline
capture, where it is another, must be a session of the investigation, or the window says to add it first. A session
opened on its own has no investigation to keep one, which the action says. The investigation's window lists those in
force under Comparisons…, in the words above, and opens one: its compared session at its compared interval in
InterCat's window, and the Compare window on that interval, the saved baseline offered first and chosen, compared by
what it was saved with - another capture's read from its folder where the investigation finds it, which must hold its
capture - or says which session to relink. It removes one too, keeping its revisions. Showing a comparison the
investigation saved alike - the same two intervals of the same sessions, by the same metric and rows; of several, the
one the window opened as or last saved under, else the first saved - the Compare window says the notes about it
beneath its rows in the order written, a row's by its rank and ends or as one it does not compare, and marks each row
a note is about, beside its ends and in what a screen reader says of it. Add a note… writes one, about the whole
comparison or, chosen instead, about the row chosen in the list, by its key; it says why not where the session was
opened on its own, where the comparison shown is not one saved, and where the investigation cannot be read.

## 8d. A difference timeline

A row's difference timeline (plan M12's baseline and difference timelines, ADR-062) cuts each interval into the same
number of equal columns, end to end - the k-th column of each the same share of its interval, in its own session's
ticks - and compares the row's k-th baseline column with its k-th compared column as two intervals are compared (§3 to
§5): by the same metric, its rates over each column, a change stated only where both columns covered the row's
mechanism and the capture collected it alike in both, and elsewhere both values with the reason, which speaks of each
column's share of its interval. A quiet column is compared too, so a column the capture did not collect never reads as
nothing happening. It is read with the comparison, at its generation - of two captures, each at its own - so the row's
columns add up to its values over the whole intervals.

`icat compare ... --timeline <rank> [--columns <n>]` states the ranked row's timeline in place of the table: the row,
its values over the whole intervals, how the intervals are cut - 10 columns unless `--columns` says, 1 to 100, each at
least a tick long - and a line per column with its two intervals, values and change, each reason once beneath them;
`--json` and `--output` write the comparison with it (§8). A rank past the rows compared is refused, as are
`--columns` without `--timeline`, a timeline with `--limit`, and one in a redacted report, which holds rows over the
whole intervals only. The window draws the chosen row's (§8a).

## 8e. On the graph

Show on the graph, in the Compare window, closes it and marks each edge the main window's graph draws by how the rows
it draws changed (plan M12's graph overlays, ADR-063): ▲ up, ▼ down, + appeared, − disappeared, = unchanged, ? not
comparable and ± up and down, each a glyph in a ringed disc on the edge's stroke, clear of the nodes it joins, so no
change is read from colour alone (R14). It is offered once a comparison holding a row is shown, and waits while one is
read, shared or saved.

- **What an edge draws.** The rows of the relationships it draws, and, to a host or to Other hosts, of its processes'
  connections to that host's address or to every address not drawn on its own. Of a comparison by executable, a row is
  drawn on every edge of its processes' relationships or connections - a grouping, never an identity (P6, ADR-059) -
  so a mark of more than its edge has a broken ring, and its words say it is of the executables' processes together. A
  comparison of another capture marks the graph shown, its compared capture's.
- **One mark an edge.** One row's change is the edge's; rows that agree, their change; rows that each rose, appeared
  or stayed, up, or appeared where none held anything in the baseline, and likewise down or disappeared; one row that
  cannot be compared makes the edge not comparable, no change being stated of what it holds; and rows that went both
  ways, up and down. Nothing is summed or judged again: a mark presents the rows `icat compare` states (§8), which its
  card names.
- **What is not marked.** An edge that draws no row had no record in either interval as far as the comparison read,
  carries no mark, and its card says so (R21). A row no edge draws - between processes drawn together in one node, or
  of a process or host the graph does not show - is counted, never lost.
- **Words beside the marks.** A line beneath the graph's header states both intervals in the window's time base, what
  the rows are, how many edges went each way with their glyphs, the edges unmarked and the rows no edge draws; an
  edge's card states its change in §8's words; and the relationship table, the graph's table equivalent, carries each
  row's glyph and says its change to a screen reader (R15). Clear, beside the line, takes the marks away.
- **How long.** The marks follow every drawing of the session, each marked again as a group is opened, hosts are drawn
  on their own or a time scope is set, and newer generations, the line then saying that what the session holds since
  has no mark until Compare with… reads it again. Showing another session takes them away.

## 9. Not defined at this version

- **Another capture's interval in the window** other than its whole recording: the command line's `--baseline` takes
  any.
- **A bounded change under a gap.** The losses a ledger counts bound the records a mechanism lost, not a row's (§5); a
  bound per row would need what a lost record could have moved.
- **A difference timeline in a redacted report.** The command line and the window state one (§8d); a report holds rows
  over the whole intervals only.
- **A comparison's marks on the timeline**, or on nodes; the graph marks edges (§8e).
- Recurring patterns, outliers, causal navigation and bottleneck views, which `patterns-v1`, `outliers-v1`, `links-v1`
  and `waits-v1` define.
- Wall-clock bounds, which `icat timeline --wall-clock` states; a comparison takes session ticks.
