# ADR-081: A capture's acquisition queue holds the largest power of two of records within its 256 MiB budget

- Status: accepted; since revision 921 every capture's queue holds 262,144 records by default
- Date: 2026-11-05
- Decision owners: InterCat maintainers
- Relates to: §9.3, §12 (ingest, bursts and memory budgets), R8, R9, P12, ADR-008, ADR-080's addendum, FX-COM-001,
  `OwnedSessionPlan`, `OwnedCaptureSession`, `LiveRecorder`, `tools/InterCat.ComProbe --measure --product-impact`

## Context

A capture's callback copies each admitted record into a bounded queue, which the recorder's single writer thread drains
into the journal and the live derivation (§9.3, ADR-008). When the queue is full the callback returns at once and counts
an application drop, since a full queue cannot throttle Windows. The queue held 65,536 records.

ADR-080's addendum measured the COM calls profile on FX-COM-001's 16,384 calls unpaced, about 100,000 records a second
for four seconds. Every capture's queue filled and dropped a median third of its records, with nothing lost at the
sources. Explore's dropped some in seven rounds of nine.

A timing experiment on the writer thread, run once and not kept, showed where its time went over one such capture of
248,478 records:

- writing a record took about 7.2 µs, 1.8 s in all, so the writer drains at most about 140,000 records a second;
- each chunk publication stopped the drain for 350 to 435 ms at 50,000 to 180,000 records a chunk, the derivation's step
  after it under a millisecond;
- the queue reached its 65,536 high water.

The writer's rate is close to the burst's, and every publication stops it. The queue held well under a second of the
burst. §12 budgets 256 MiB for acquisition queues and asks a capture to survive a 20-second analysis stall at its ingest
target. An admitted record is about 850 bytes, carrying bounded inline copies of a name, slots and extended data, so
65,536 of them are about 52 MiB.

## Decision

1. **The queue holds the largest power of two of admitted records within §12's 256 MiB.** The bounded channel's array
   doubles as it fills, so only a power of two keeps its array within the budget at its fullest. The capacity is
   computed from the record's size, 262,144 records today, about 220 MiB at its fullest. A plan may still state its own,
   as the M0 measurement commands do.
2. **The queue grows only as the writer falls behind.** A capture that keeps up holds no more than before. A capture
   reaches the budget only when its writer is 262,144 records behind. The array grows a few times a capture, on the
   callback's thread, never once per record.
3. **A burst longer than the queue holds still drops, and says so.** The application drop is counted where it happens,
   and the capture's coverage states the gap, as §12's bursts budget requires.

## Measurement

The unpaced series again, with the queue at 262,144 (`bench/results/com-calls-impact-20261104T230451Z`): nine rounds,
the same 16,384 calls.

- No capture, the profile's or Explore's, dropped or lost a record: about 410,000 records each.
- The profile added a median 2.7 CPU percentage points over no capture, its own process 2.6, and the workload ran 35.2%
  slower.
- Explore added 1.2 points, and the workload ran 10.5% slower.

One such recording, kept as a session, held all 421,448 records. All 16,384 truth calls were observed, linked and carried
(ADR-080), where a recording with the smaller queue had 10,226.

## Consequences

- The COM calls profile's statement and evidence cite this series, nothing dropped at either rate.
- While its writer is behind, a capture's process may hold up to about 220 MiB of queued records: the broker's, or
  `icat record`'s.
- §12's ingest budget, 100,000 records a second for ten minutes with no application drops, is still not shown. This only
  gives a burst room. The writer drains at about 140,000 a second, less each publication's stall, so a sustained rate
  close to that still fills the queue. What a sustained rate needs is a publication that does not stop the drain, or a
  faster writer, measured over the ten minutes.

## Alternatives considered

- **A byte bound that is not a power of two.** The channel's array would double past it at its fullest.
- **A queue allocated whole when a capture starts.** Every capture would hold 220 MiB from its first second, quiet or not.
- **A compact queued record.** Four times as many records would fit in the same bytes, but the callback, the queue and
  the writer all read the admitted record as it is. That is a larger change than a bound, and the bound comes first.
- **Publishing off the writer thread.** It removes the stall the queue absorbs here, but the next chunk's generation
  follows the last one's through the commit protocol (§20.1). That change needs its own measurement and record.
