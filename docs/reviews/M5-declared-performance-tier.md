# M5 declaration — the performance tier (§12.1)

Date: 2026-11-19, at plan revision 1090 (revision 1079 declared T1, revision 1089 T2)
Declared: **T2 standard** - sessions up to 20 GiB answer every §12 budget, cold and warm, on the reference machine below,
running InterCat's code as the package ships it, compiled ahead of time; the measured sessions keep their segments within
§20.1's targets. T1 holds with it. T3 was measured too: every warm budget holds, and so do S1's reopen, S2's working set
and since revision 1090 a group's first timeline cold, but a group's first byte lanes are just over budget cold, which
T3's indexed predicates do not allow. T4 is M13's to measure and is not declared.

## How it was measured

- Reference machine: 24 logical processors, Windows build 26320 - each report names it as "24 logical processors ·
  Microsoft Windows NT 10.0.26320.0" - running at below-normal priority, in use by its owner and 22 to 55% busy before
  each run.
- Code: since revision 1089, InterCat's code as the package ships it (ADR-116) - the gate published for x64 Windows and
  compiled ahead of time, its InterCat assemblies the same bytes as the CLI's precompiled publish from the same tree but
  for the version the package stamps into them. Each report counts the InterCat assemblies its run loaded and how many of
  them were precompiled - all of them, in every run - and, for each cold answer, how long the runtime spent compiling
  inside it and, since revision 1090, what it allocated and how long collections paused it.
- Sessions: synthetic and finished - 200 loopback TCP conversations between 400 processes of 10 executables, each also
  sending UDP to an off-machine resolver, one record every 60 µs - published in chunks of 500,000 records as a live
  capture publishes, then given their derivation checkpoint and overview. 5, 30 and 72 million records: one session
  per tier, each measured in three runs (`bench/results/scale-tiers-20261119T181920Z`, `t1-1` to `t3-3`).
- Cold is the first time a process answers a question of a session after reopening it, nothing derived in memory and no
  segment open, though the operating system's file cache may hold the files. Warm is the same question asked again over
  other viewports, brushes or scopes, at its 95th percentile. Every timeline question counts 2,000 columns, the most a
  view asks.
- Each figure is the median of a tier's three runs, as `ScaleTierTests` holds them. Where the median is within budget
  but one run was not, that run is named beside it: a run's worst is the machine's noise as much as the product's, and
  the tests do not hold it.

## The tiers

| Tier | Records | On disk | Reopen | Steady working set |
|---|---|---|---|---|
| T1 | 5 million | 1.6 GiB | 302 ms | 269 MiB |
| T2 | 30 million | 9.9 GiB | 311 ms | 292 MiB |
| T3 | 72 million | 23.7 GiB | 350 ms | 330 MiB |

| Question | Budget | T1 cold | T1 warm p95 | T2 cold | T2 warm p95 | T3 cold | T3 warm p95 |
|---|---|---|---|---|---|---|---|
| Machine timeline (L0) | 100 ms | 4.7 | 45 | 5.7 | 49 | 50 | 35 |
| A group's timeline (L1), 2,000 columns | 100 ms | 31 | 30 | 42 | 51 | 91 | 35 |
| A group's byte lanes (L1), 2,000 columns | 100 ms | 52 | 45 | 49 | 48 | **104** | 51 |
| A connection's two ends (L3), 2,000 columns | 100 ms | 6.8 | 24 | 15 | 12 | 42 | 31 |
| Ranking, the whole session | 250 ms | 5.6 | 5.3 | 6.0 | 6.1 | 5.7 | 6.8 |
| Ranking, brushed | 250 ms | 30 | 9.5 | 26 | 11 | 30 | 12 |
| Ranking by bytes, brushed | 250 ms | 15 | 7.3 | 17 | 9.9 | 20 | 10 |
| First evidence page, a process | 150 ms | 109 | 11 | 123 | 19 | 147 (one run 164) | 47 |
| First evidence page, a channel | 150 ms | 13 | 14 | 21 | 24 | 35 | 41 |

S1: a T3 reopen at the median is 1.16 times a T1 reopen; the plan allows 2.

At T2 no run of any question was over its budget, cold or warm: a group's first timeline took 42 to 51 ms, its byte lanes
48 to 51 and a process's first evidence page 100 to 135. Over budget at the median, in bold: at T3, a group's first byte
lanes, 94 to 106 ms. Asked again over any other viewport, each answers well within its budget at every tier.

What the runtime still compiled inside a cold answer, at T2's median: 7 ms of a group's first timeline, 20 of its byte
lanes and 40 of a process's first evidence page, summed over the threads compiling at once - the framework's generic code
made for InterCat's own types, which no InterCat assembly can carry compiled, and the methods marked to compile optimized
at their first use (ADR-116). At T3 a group's first timeline allocated 51 MiB, three collections pausing it 21 ms, and its
first byte lanes 55 MiB, one pausing them 5 ms.

## What each tier holds

| Tier | §12.1 asks | Holds |
|---|---|---|
| T1 | Every §12 budget, cold and warm | Yes - declared since revision 1079 |
| T2 | Every §12 budget; compaction stays inside §20.1's segment targets | Yes - declared since revision 1089: every budget cold and warm, in every run; its segments within §20.1's targets, none compacted (below) |
| T3 | Interactive budgets for indexed predicates; progressive answers say so | Every warm budget, and a group's first timeline cold since revision 1090; not a group's first byte lanes cold |
| T4 | Bounded memory, honest coverage, recoverable reopen, declared quotas, beyond 100 GiB | Not measured: M13's |

S1 and S2 hold across the three tiers: the reopen and the steady working set stay within twice T1's (`ScaleTierTests`),
the reopen reading no observation data in proportion to the session and opening no segment before the first view.

## T2's segment targets

§20.1 makes compaction mandatory past 64 live segments in a time block or 512 in a session, coalesces into outputs of at
least 8 MiB or 64,000 rows, and allows at most 128 segment opens to serve the first viewport after a reopen. The T2
session, published as a capture publishes at §12's ingest target, in units of 500,000 records, holds 120 segments of
250,000 records, about 44 MB each, none small: nothing was compacted, and no run opened a segment before its first view.
A capture publishing smaller units relies on compaction to coalesce them (ADR-026), whose targets its tests hold;
compaction's own throughput at T2's size is not part of this measurement.

## What it would take to declare T3

A group's first byte lanes within 100 ms at 72 million records: 104 ms today at the median, from 151 before revision 1090.
They read some 29,000 lists' readings and measures across 288 sections, each section's group as one part (ADR-113's
fourth addendum), beside the machine's own byte row, which sums every tile's bytes and takes about 30 ms of them; their
sections are measured on six workers. A process's first evidence page, 147 ms at the median with one run at 164, sits at
its budget too. Both are measured by every run of the procedure in `bench/README.md`, so a later release can declare T3
from its own run without changing how it is measured.

## Before revision 1089

Revision 1079 declared T1 from `bench/results/scale-tiers-20261118T142911Z`, the gate run from the Release build with
its code compiled at first use. T2 and T3 held every warm budget then, but a group's first timeline took 315 ms cold at
T2 and 500 at T3, its byte lanes 240 and 445, and a process's first evidence page 191 and 169. Revision 1088 read a
group's lists ahead on several workers (ADR-113's second addendum): about 140 ms for the timeline at T2, the evidence
page unchanged, being code compiled at first use. Revision 1089 shipped the code compiled ahead of time and measured it
so (ADR-116), made a group's count walk behind its lists as they are read (ADR-113's third addendum), and declared T2
from `bench/results/scale-tiers-20261119T164559Z`, where T3's first group timeline took 118 ms and its byte lanes 151.
Revision 1090 reads a group's lists on their own, a section's into one buffer kept as one part, and measures its byte
lanes' sections on several workers beside the machine row (ADR-113's fourth addendum): T3's first group timeline within
budget, its byte lanes 4 ms over.
