# M5 declaration — the performance tier (§12.1)

Date: 2026-11-18, at plan revision 1079
Declared: **T1 interactive** - sessions up to 2 GiB on disk answer every §12 budget, cold and warm, on the reference
machine below. T2 and T3 were measured too: every warm budget holds at both, and so do S1's reopen and S2's working set,
but a group's first timeline and a process's first evidence page are over budget cold, which T2 does not allow. T4 is
M13's to measure and is not declared.

## How it was measured

- Reference machine: 24 logical processors, Windows build 26320 - each report names it as "24 logical processors ·
  Microsoft Windows NT 10.0.26320.0" - running the Release build at below-normal priority.
- Sessions: synthetic and finished - 200 loopback TCP conversations between 400 processes of 10 executables, each also
  sending UDP to an off-machine resolver, one record every 60 µs - published in chunks of 500,000 records as a live
  capture publishes, then given their derivation checkpoint and overview. 5, 30 and 72 million records: one session
  per tier, each measured in three runs (`bench/results/scale-tiers-20261118T142911Z`, `t1-1` to `t3-3`).
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
| T1 | 5 million | 1.6 GiB | 378 ms | 271 MiB |
| T2 | 30 million | 9.9 GiB | 399 ms | 302 MiB |
| T3 | 72 million | 23.7 GiB | 389 ms | 356 MiB |

| Question | Budget | T1 cold | T1 warm p95 | T2 cold | T2 warm p95 | T3 cold | T3 warm p95 |
|---|---|---|---|---|---|---|---|
| Machine timeline (L0) | 100 ms | 5.4 | 58 | 7.3 | 58 (one run 203) | 74 (one run 306) | 69 (one run 116) |
| A group's timeline (L1), 2,000 columns | 100 ms | 82 | 33 | **315** | 46 | **500** | 33 |
| A group's byte lanes (L1), 2,000 columns | 100 ms | 62 | 35 | **240** | 61 (one run 118) | **445** | 53 |
| A connection's two ends (L3), 2,000 columns | 100 ms | 14 | 20 | 34 | 14 | 40 (one run 148) | 12 |
| Ranking, the whole session | 250 ms | 7.8 | 56 | 6.8 | 52 | 6.3 | 57 |
| Ranking, brushed | 250 ms | 28 | 6.0 | 34 | 9.5 | 37 | 9.2 |
| Ranking by bytes, brushed | 250 ms | 25 | 18 | 22 | 21 | 25 | 25 |
| First evidence page, a process | 150 ms | 129 (one run 179) | 20 | **191** | 21 | **169** | 41 |
| First evidence page, a channel | 150 ms | 13 | 29 | 18 | 34 | 33 | 47 |

S1: a T3 reopen at the median is 1.03 times a T1 reopen; the plan allows 2.

Over budget at the median, in bold: at T2 and T3, a group's first timeline - its 40 process lanes counted, or measured
in bytes, in one pass - and a process's first evidence page. Both read the session's persisted index and segments the
first time they are asked; asked again over any other viewport or brush, each answers well within its budget. Every
question at T1 answers within budget cold.

## What each tier holds

| Tier | §12.1 asks | Holds |
|---|---|---|
| T1 | Every §12 budget, cold and warm | Yes - declared |
| T2 | Every §12 budget; compaction inside §20.1's targets | Every warm budget; not two first questions cold. Compaction's targets are not part of this measurement |
| T3 | Interactive budgets for indexed predicates; progressive answers say so | Every warm budget; the same two cold misses |
| T4 | Bounded memory, honest coverage, recoverable reopen, declared quotas, beyond 100 GiB | Not measured: M13's |

S1 and S2 hold across the three tiers: the reopen and the steady working set stay within twice T1's (`ScaleTierTests`),
the reopen reading no observation data in proportion to the session.

## What it would take to declare T2

A group's lanes and a process's first evidence page answering within budget the first time at 30 million records:
today 315 ms and 191 ms at the median, against 100 ms and 150 ms. Both are measured by every run of
`scale-tiers-measure.ps1`, so a later release can declare T2 from its own run without changing how it is measured.
