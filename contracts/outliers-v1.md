# InterCat outliers v1

Status: **implemented** on the command line (`icat outliers`, revision 768) and in the window (Outliers…, revision
770, §7).

This contract fixes plan M12's optional outlier detection for one session's current generation: an interval's rows -
each relationship between two processes and each process's connections to another host, or by executable the same of
executables - judged against the windows of a baseline, each window as long as the interval, by where the interval's
records fall among the records each window held; the baseline, its windows and how often chance alone would put a row
outside their range, stated beside them; and what is not judged, with why. It owns no bytes: every row and count is a
comparison's (`comparison-v1`), and judging changes nothing. ADR-068 records the decisions.

## 1. A reading

- A session, read at its current generation: the comparison of the compared interval with the whole baseline by
  records (`comparison-v1` §3 to §5), and each row's records in every window, all of one generation, read again where
  a newer one was published between them, up to three times, and then refused with a reason rather than mixed (R7).
- An **evidence policy**, `IncludeCorrelated` by default, and a **grouping**, by process or by executable
  (`comparison-v1` §2a), as a comparison takes them.
- What the capture covered over each interval, stated beside the rows (R21).

## 2. The baseline and its windows

- **The compared interval** is the interval judged, half-open, in presentation ticks.
- **The baseline** is an interval that does not overlap it, before or after it. Where none is named, it is all the
  session keeps before the compared interval: from the session's start, or from where it keeps every record after an
  interval release.
- **Its windows** are as many intervals as long as the compared interval as fit in the baseline, at most 199, spread
  evenly from its start to its end: the first starts where the baseline does and the last ends where it does, and the
  ticks the windows do not fill are spread between them as evenly as whole ticks allow, so no two overlap.

The windows are the sample. A baseline that holds fewer than 19 is refused in words that say how long a baseline would
do, so that chance alone puts a row above every window no more than 1 time in 20 (§4).

## 3. A judgement

Only rows a comparison of the two intervals compares are judged. A row whose two values are not of one measurement -
an interval that did not cover its mechanism, or collected it through other sources (`comparison-v1` §5) - is not
judged, and is counted by its reason. A row judged has:

| Field | Meaning |
|---|---|
| Records | Its records over the compared interval. |
| Windows | Its records in each window, in order; a window it has none in counts 0. |
| Fewest, median, most | The fewest records any one window held; the median window's, the lower of the two middle ones where the windows are even in number, so one window's own count; and the most. |
| Side | **Above** where its records are more than every window's; **below** where they are fewer than every window's; **within** otherwise. |
| Beyond | How many records more than the most, or fewer than the fewest; none within. |

The rows above or below every window come first, the most records beyond first, then those within; each tie in the
comparison's order.

## 4. The uncertainty

A judgement assumes one thing and nothing else of how records arrive - not their bursts, not their distribution: were
the compared interval like the baseline's windows, its records arriving as theirs do, it would be one more interval
among them, as likely as any to hold the most records. Chance alone would then put a row above every window at most 1
time in N, N one more than the windows, and below every one at most as often; ties make it rarer. Across the rows
judged, on average no more than their number divided by N are above every window by chance alone, and as many below,
and the reading says so in those numbers.

A row outside its windows' range is that observation and nothing more: no label says what its records mean or why they
differ, and none names malware, an attack, a fault or a cause (plan M12).

Bytes are not judged: a byte total is a sum of sizes rather than a count of records, and records are what a comparison
by records compares.

## 5. The command line

`icat outliers <directory> --interval <start:end>` judges the compared interval against all the session keeps before
it, or against `--baseline <start:end>`. It states the session, its generation, the compared interval, the baseline
with its windows - "[0.000, 0.100) ms, 0.100 ms long, as 20 windows of 5.0 µs, end to end", or "spread evenly across
it" where they do not fill it - what the rows are judged by, and how many were judged and found above or below every
window. Beneath are the rows above or below every window, 20 unless `--limit` says, each with its ends, what it ran
over, its records, its windows' - "10 to 12, median 10", or "10 in each" - how far beyond - "38 more than the most" -
and where it falls - "above every window of its baseline"; then how often chance alone would put a row there, what was
not judged and why, and what a judgement is and is not. `--group-by executable` judges executables' rows
(`comparison-v1` §2a), and `--evidence-policy` takes a policy as written or in kebab form.

`--json` writes `outliers-v1`: the session and generation, the policy and grouping; the baseline and the compared
interval, each with its coverage; the windows; N; how many rows there are, how many were judged, and how many are
above and below; what was not judged, by reason with how many rows; and the rows above or below every window listed,
each with its key, kind, mechanism and ends - instance, PID and name, or executable - its address, which end
connected, its side, its records over the compared interval and the baseline, its records in every window, the fewest,
median and most, and how far beyond.

A baseline that overlaps the compared interval or holds fewer than 19 windows, and a compared interval nothing is kept
before where no baseline is named, are refused in words.

## 6. One generation

A reading's comparison and windows are of one generation (§1).

## 7. The window

Outliers… in InterCat's window, offered beside Compare with… while the window has a time scope - a brush, or else the
range zoomed to - judges that scope as `icat outliers` judges the compared interval, read from the session's current
generation under the window's evidence policy:

- **The baseline** is all the session keeps before the scope, as on the command line where none is named (§2), or all
  after it, to the end of the recording or of the session's records; the rows are each process's or each executable's,
  as `--group-by` chooses. The baseline and its windows are stated beneath the choice in the command line's words, and
  a baseline the scope cannot be judged against is refused in them.
- **The rows above or below every window** are listed, the most records beyond first, each with its ends, what it ran
  over, its records, its windows' fewest, median and most, how far beyond and where it falls; the status line says how
  many rows were judged and found, and the evidence policy where it is not the default.
- **The chosen row's windows are drawn**: a bar per window, the earliest first, then apart and wider the scope's, on
  one scale of records, with a dashed line at the most and at the fewest any window held. Left, Right, Home and End
  move a cursor along the bars, and the line beneath states the bar chosen in words - "Window 3 of 20, [x, y): 10
  records", or the scope's records with where they fall. No meaning rests on colour alone: the scope's bar stands
  apart, and every bar is said in words (R14, R15).
- **How often chance alone puts a row outside its range** (§4), what is not judged and why, and what a judgement is
  and is not are said beneath the rows.
- **Show in the window**, Enter or a double click, closes the window and shows the chosen row as a comparison's row is
  shown: a relationship as a double click on its edge opens it, a process's connections to another host at the
  process. The window itself changes nothing.

## 8. Not defined at this version

- **Bytes** (§4), and any measure other than records.
- **Two captures**: a baseline read from another capture than the compared interval's.
- **A seasonal baseline**: windows chosen by time of day or of week; the windows are spread over the baseline as it
  is.
- **Any word of why** a row differs (§4).
