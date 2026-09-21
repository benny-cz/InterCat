# ADR-009: How the callback allocation budget is measured, and what it measured

- Status: accepted for M1
- Date: 2026-09-21
- Decision owners: InterCat maintainers
- Relates to: ADR-001 (toolchain pins), ADR-008 (authoritative journal), §12 budgets, §18.3 (adapter)

## Context

§12 gives the callback stage two budgets in one line: "p99 under 50 µs with no unpooled allocation (R9,
R11)". The IC-009 series measured the latency half and met it. For the allocation half it reported the
delivery thread's total allocation divided by the records admitted — about 1,400 bytes per record — and
ADR-008 accepted the journal direction carrying that as its one measured debt, with pooled buffer
ownership named as IC-011's first condition.

That figure was measuring the wrong thing, and the debt it implied was not InterCat's to pay.

## What was measured

The delivery thread runs the adapter's dispatch and InterCat's admission on the same stack, so a thread
total cannot say which of them allocated. Attribution needs the same work done twice with only admission
removed.

The harness now replays the same ETL through the same adapter twice: once with the compiled admission
table, and once with an empty one, where every record is refused at the first check before any InterCat
work. Both runs use a sink that keeps nothing — a sink that stored its records would allocate more than
either path under measurement and swamp the difference. The gap between the two runs is admission's own
cost.

One level cannot tell a fixed cost from a per-record one: any setup allocation divided by a record count
looks like per-record allocation. The series fits the gap across its smallest and largest levels, and the
slope between them is what "no unpooled allocation" means.

Measured on Windows 11 25H2 x64 (26220), replaying the series' own ETL evidence:

| Level | Records observed | Admission's allocation |
|---|---|---|
| paced | 734 | 1,616 B |
| peak | 75,565 | 1,680 B |

**Admission allocates about 0.0009 bytes per record.** Admitting a hundred times more records costs
64 more bytes in total. About 1,615 bytes is fixed setup that does not grow with the record count.

This is what the code says it should be. Admission writes into a struct with inline arrays, copies names
and extended-data items through `stackalloc` spans, and hands the record to a bounded channel by value.
There is no per-record heap object in it. The measurement confirms the design rather than discovering it.

## Decision

**The callback allocation budget is measured as a slope, not as a thread total divided by a record
count.** The slope is fitted across at least two levels whose record counts differ by an order of
magnitude, from two replays of the same evidence through the same adapter with only the admission table
differing.

**The budget's bound is one byte per admitted record.** The rule is "no unpooled allocation"; one byte
per record is the resolution at which a slope measurement can tell growth from noise, and it is three
orders of magnitude below anything that would matter at §12's ingest rate. A budget stated as exactly
zero cannot be met by any measurement, because every measurement carries a fixed setup cost, and a budget
that cannot be met is not a budget. §12's rule is unchanged; this is how it is checked.

**The adapter's allocation is a separate quantity, reported beside it and never added to it.** It is not
InterCat's to pool, and calling it InterCat's would hide where the cost is.

### Consequences for ADR-008

ADR-008's second condition on IC-011 — "replace the disposable framing with pooled buffer ownership so
the R9/R11 allocation budget is met" — rested on a figure that did not separate the two. Pooling
InterCat's buffers cannot meet that budget, because InterCat does not allocate them per record. The
condition is amended there: pooled ownership remains worth doing for the writer stage and for buffer
lifetime discipline, and it is not what the allocation budget was waiting for.

The budget is now **met**, and it was met before this ADR was written. Nothing about the code changed.

### Consequences for the adapter

The live delivery thread still allocates about 1,400 bytes per delivered record. Admission contributes
effectively none of it, so it belongs to the managed adapter's real-time dispatch. §18.3 already
anticipated this: TraceEvent is "replaceable by a native TDH consumer if M0 fixtures show a semantic or
throughput gap". This is a measured allocation gap on the real-time path, and it is now the reason to
evaluate that replacement rather than a suspicion. IC-019 owns the evaluation.

Two limits on that claim, stated because they bound it:

- The attribution replays a **file**, not a real-time session. It proves admission allocates nothing on
  the shared admission path; it does not itself measure the real-time dispatch. The conclusion that the
  live allocation is the adapter's follows from admission being allocation-free in the same code, not
  from a live split measurement.
- A native consumer would move the cost rather than remove it, and it brings its own risks: hand-written
  `EVENT_RECORD` decoding, a second schema path, and the loss of a maintained library. IC-019 decides on
  evidence, and no part of this ADR commits to it.

## Alternatives

- **Keep the thread total as the measurement.** Rejected: it attributes another component's cost to
  InterCat, which is the same error as reporting an unreadable counter as zero, in the opposite
  direction.
- **Keep the bound at exactly zero.** Rejected: unmeasurable, so the budget would read as permanently
  missed and stop carrying information.
- **Subtract a measured constant instead of fitting a slope.** Rejected: it assumes the constant, and
  the slope measures it.

## Reversal cost

Low. The measurement is a method, not a format: changing it changes a number in the next run's report and
nothing that is persisted. Reverting the bound to zero would reinstate a permanently missed budget.
