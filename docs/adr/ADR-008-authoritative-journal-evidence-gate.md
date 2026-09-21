# ADR-008: Authoritative journal versus ETL evidence gate

- Status: proposed; the elevated load series has run, the decision is blocked on a supported build and on unreadable ETL loss counters
- Date: 2026-09-21
- Decision owners: InterCat maintainers
- Relates to: ADR-002 (owned ETW capture), ADR-005 (identity), ADR-006 (clocks)

## Context

The implementation plan selects a broker-owned admitted-event journal as the intended live authority and
keeps ETL as optional diagnostic evidence. That direction has useful privacy and replay properties, but
it cannot become a production format merely because it was written into the plan. IC-009 requires an M0
fidelity and overhead comparison with ETL first; IC-011 may freeze `RecordEnvelopeV1` only after this
gate closes.

## Evidence completed

`FX-JOURNAL-001` and the version-0 portable probe establish these semantics without freezing a wire
format:

1. known approved metadata is retained;
2. an unknown schema with opaque bytes keeps permitted header diagnostics but persists no body;
3. known content under a metadata-only policy persists no body;
4. every input record has an envelope and an explicit body disposition;
5. allowlisted extended data and raw identities replay byte-for-byte;
6. admission owns copied bytes after source memory changes;
7. a corrupted checksummed batch is rejected; and
8. a corrupted source clock frame is refused rather than yielding a partial descriptor.

The Release probe at `bench/results/journal-probe-portable.json` processed 10,000 synthetic records for
seven measured iterations. Its median combined admit/encode/decode rate on the recorded machine was
about 216,000 records/s with exact identity and extended-data replay and zero unapproved bodies retained.
This is a portable implementation measurement, not ETW or ETL evidence. Allocation is intentionally
reported and is too high for a callback path; the probe uses ordinary arrays and streams to validate
semantics, while IC-011 still owes pooled ownership and batching.

### Elevated comparison runs, 2026-09-21, build `10.0.26220.0-x64`

`InterCat.CaptureComparison` ran twice from an elevated shell in Release, same seed and workload
(`20260920`, 2 connections × 8 messages, 77 truth records), each variant evaluated only against its own
truth log. Both runs are committed as counters under `bench/results/`; their `.ijp0` and `.etl` records
are not, because they carry whole-machine flows (P16).

| | baseline | call stacks requested |
|---|---|---|
| Journal observed / admitted | 707 / 707 | 909 / 822 |
| ETL observed / admitted | 854 / 852 | 849 / 759 |
| Provider loss / consumer loss / application drops | 0 / 0 / 0 in both variants | 0 / 0 / 0 in both variants |
| Evidence size, journal vs ETL | 226 KiB vs 1.63 MiB (7.4×) | 277 KiB vs 1.75 MiB (6.5×) |
| Ordered envelope fingerprint after replay | identical | identical |
| Source clock | confirmed against a delivered record, replayed identical | confirmed, replayed identical |
| Extended items copied / persisted / replayed | 0 / 0 / 0 | 190 / 190 / 190, all `STACK_TRACE64` |
| Coverage tier, both variants | experimental evidence, all six §14.2 thresholds met | experimental evidence, all six met |

Stage ledger, measured on the thread that does the work rather than on the process:

| Stage | baseline | call stacks requested |
|---|---|---|
| Callback latency p50 / p99 | [1.02, 2.05) µs / [8.19, 16.38) µs | [1.02, 2.05) µs / [16.38, 32.77) µs |
| Callback latency mean / max | 11.0 µs / 6.63 ms | 10.4 µs / 7.31 ms |
| Delivery-thread CPU / allocations | below one clock tick / 1.59 MiB | 31 ms / 1.59 MiB |
| Queue high-water / capacity | 706 / 65,536 | 657 / 65,536 |
| Writer-thread CPU / allocations | 16 ms / 1.40 MiB | 16 ms / 1.69 MiB |
| Durable flushes, flush latency p99 | 2, [4.19, 8.39) ms | 2, [4.19, 8.39) ms |
| ETL offline replay, CPU / allocations | below one clock tick / 48 KiB | 16 ms / 8 KiB |

Five findings follow from these runs, and none of them was assumed beforehand:

1. **Extended-data items are opt-in per enablement.** Under a plain enablement, every delivered record
   from `Microsoft-Windows-Kernel-Network` and `Microsoft-Windows-Kernel-Process` declared no extended
   item at all. Requesting call stacks produced 190 `STACK_TRACE64` items that the callback copied, the
   envelope persisted and replay returned unchanged. "No extended items observed" therefore describes a
   capture's configuration, not the source, and the plan now says so (§18.1).
2. **Stack walking shifts the record mix, not the loss counters.** The stacks run delivered 87 records
   from the kernel stack-walk provider this capture never requested. They are counted as policy
   omissions of an unrequested provider, not as loss or as admitted evidence.
3. **The default reorder grace is not safe for that load.** At the 2-second default, the stacks run
   truncated delivery and reported a coverage collapse that was an artefact of the grace. The harness now
   defaults to 6 seconds when stacks are requested and prints that it did.
4. **The callback fits its latency budget and misses its allocation budget.** §12 asks for p99 under
   50 µs with no unpooled allocation. The measured p99 bucket bound is 16 µs, and 33 µs with stacks, but
   the delivery thread allocated about 1.6 MiB across roughly 800 records. The latency half of R9/R11 is
   met at this load; the allocation half is not, and pooled buffer ownership stays IC-011's obligation.
5. **Per-thread processor time is quantized.** The Windows thread clock advances about every 15.6 ms, so
   several stage readings here are one or two ticks. A sub-tick stage is reported as "below one clock
   tick", never as zero, and a saturation series is the only way to turn these into rates.

The `AdmittedEvent` callback envelope now carries the two activity identifiers, the bounded field
projection, up to four extended-data items of up to 64 bytes each with their original lengths and
truncation flags, and the capture's identity-v1 source clock descriptor persisted once per file. An item
the persistence policy denies — a SID, a TraceLogging schema item, provider traits — is a counted
omission whose bytes never reach the file, and replay reproduces the omission rather than inventing the
item. The extended-data walker is asserted against a synthetic `EVENT_RECORD` built in native memory, so
its offsets, bounded copies, truncation flags and omission counters are proved without ETW or elevation.

### The declared load series, 2026-09-21

Two five-level series then ran on the same machine, same seed, each level evaluated against its own truth
log. The first three levels raise the workload's own rate; the last two hold that rate and shrink a bound
instead, because a machine faster than the fixture never reaches its queue or its disk by running the
fixture harder. Every constraint is recorded with the level it applies to. The volume was measured before
any capture started: `E:\` (NTFS) at about 2.0 GB/s sequential write and 8.6 GB/s read, which meets the
§12 reference device; the host has 24 logical processors and 64 GiB, which meets the reference machine.

`bench/results/capture-comparison-20260921-series` (call stacks not requested):

| Level | messages | admitted/s | queue high-water | drops | journal vs ETL | outcome |
|---|---|---|---|---|---|---|
| paced | 16 | 248 | 665 / 65,536 | 0 | 0.21 vs 1.88 MiB | headroom |
| steady | 2,048 | 4,183 | 6,718 / 65,536 | 0 | 3.37 vs 3.38 MiB | headroom |
| peak | 16,384 | 21,028 | 41,605 / 65,536 | 0 | 25.75 vs 11.12 MiB | queue pressure |
| queue-bound | 16,384 | 1,567 | 512 / 512 | 76,315 | 1.94 vs 11.25 MiB | **queue saturated** |
| disk-bound | 16,384 | 20,930 | 60,988 / 65,536 | 0 | 25.83 vs 11.38 MiB | queue pressure |

`bench/results/capture-comparison-20260921-series-stacks` (call stacks requested):

| Level | messages | admitted/s | queue high-water | drops | extended items copied / kept / replayed |
|---|---|---|---|---|---|
| paced | 16 | 132 | 632 / 65,536 | 0 | 252 / 252 / 252 |
| steady | 2,048 | 1,524 | 6,850 / 65,536 | 0 | 9,347 / 9,347 / 9,347 |
| peak | 16,384 | 9,762 | 41,206 / 65,536 | 0 | 72,179 / 72,179 / 72,179 |
| queue-bound | 16,384 | 1,752 | 512 / 512 | 59,529 | 71,991 / 12,510 / 12,510 |
| disk-bound | 16,384 | 9,765 | 43,909 / 65,536 | 0 | 72,285 / 72,285 / 72,285 |

Every level in both series replayed with an identical ordered envelope fingerprint and read its source
clock back unchanged. Seven findings follow, and none was assumed:

6. **The bounded queue behaves at its limit.** At `queue-bound` the source delivered 82,530 records, the
   queue held its 512 and InterCat counted 76,315 application drops with zero source loss and zero
   undecodable records. Bounded memory, counted drops, nothing silently lost — which is what R8 asks for.
   `queue-bound` copied 71,991 extended items in the callback but persisted 12,510: the difference is the
   records the bound dropped after their items were copied, not a fidelity gap.
7. **The unconstrained ceiling on this machine is about 21,000 admitted records/s**, at which the queue
   reaches 63% of its bound with no drops. That is a fifth of §12's 100,000/s ingest target, and the
   fixture — not the pipeline — is what limits it: the loopback workload serves its connections
   sequentially.
8. **The writer becomes the pacing stage when asked to flush often.** At `disk-bound`, 2,577 durable
   flushes kept the writer busy for 107% of the acquisition window — it was still draining after the
   workload ended — and the queue rose to 93% without dropping. Flush latency p99 stayed under 2 ms.
9. **The journal is not uniformly smaller than the ETL.** At `paced` it is 0.21 MiB against 1.88 MiB,
   because a nearly empty ETL still pays for its buffers. At `peak` the journal is 25.75 MiB against
   11.12 MiB: about twice the ETL per record, since version 0 writes a full eight-slot projection for
   every record. With call stacks requested the ETL carries whole stacks and grows to 58.75 MiB while the
   journal, which keeps a bounded 64-byte prefix per item, stays at 28.08 MiB. The size relationship is a
   consequence of the admission policy and the enablement, never a fixed ratio, and no claim should be
   made from a single load point.
10. **Callback latency improves with load.** The p99 bucket bound falls from 32 µs at `paced` to 0.5–2 µs
    at the high levels: the paced figure is dominated by the first callbacks, not by steady-state cost.
    Allocation does not improve — 99 MiB on the delivery thread for about 82,000 records, roughly 1.2 KiB
    each — so R9/R11 remains unmet at every level and pooling stays IC-011's obligation.
11. **The ETL's own loss is unknown on this build.** Reading the file session's loss counters at stop
    fails with `0x80071069`, so its provider loss is not zero, it is unreadable. The harness now reports
    it as unknown and blocks the decision on it rather than printing a default.

## Decision

No authoritative-source decision is accepted yet. The planned admitted-journal direction remains the
candidate, not a measured conclusion. `journal-probe-v0` is disposable and must never be recognized as
an `.icat` journal.

Four of the five gate inputs are now recorded. The blockers that remain are the reason this ADR stays
proposed:

| Gate input | State |
|---|---|
| Truth-to-source and source-to-replay fidelity, including extended items | Recorded: identical ordered fingerprint at every level, 72,285 of 72,285 extended items replayed at the highest |
| Provider loss, consumer-buffer loss, application drops and policy omissions kept separate | Recorded for the journal. **Partly missing for the ETL:** its provider counters cannot be read at stop on this build |
| Callback time, queue high-water, writer CPU, allocations, bytes written and flush latency | Recorded at every level of both series |
| Behavior under queue and disk saturation | Recorded: the queue reached its bound and counted 76,315 drops without loss; the writer reached 107% of an acquisition window |
| Exact Windows build, adapter version, profile, buffers, storage device and command line | Recorded, including a measured volume that meets the §12 reference device. **The build is outside the §1.3 support matrix** |

Close this ADR only after the series is repeated on a supported build with readable ETL loss counters.
Nothing else the gate asked for is outstanding.

The comparison must not call the admitted journal byte-identical to ETL: policy intentionally removes
unapproved bodies. It compares preservation of approved evidence and independent truth, while recording
the intentional omissions.

## Reproducing the runs

From an elevated Windows shell at the repository root:

```powershell
dotnet build InterCat.slnx -c Release
dotnet run --project tools/InterCat.CaptureComparison -c Release --no-build -- --output bench/results/capture-comparison-<run-id>-series
dotnet run --project tools/InterCat.CaptureComparison -c Release --no-build -- --output bench/results/capture-comparison-<run-id>-series-stacks --request-stacks true
```

`--help` prints the declared levels with their settings and intents. `--series quick` runs the paced
level alone, which is a point and says so; `--levels a,b` runs a named subset.

The tool refuses a non-elevated shell before creating a session or an output directory, refuses an
existing output directory, and exits non-zero while any decision blocker remains.

## Alternatives

- **ETL authoritative:** simpler source fidelity and standard tooling, and the series shows it costs this
  process nothing during acquisition because Windows owns the writer. Against it: metadata-only
  sanitation cannot be claimed for an unfiltered ETL, its own loss counters could not be read at stop on
  this build, it was more than twice the journal's size once call stacks were attached, and growing-file
  live access remains a separate problem.
- **Admitted journal authoritative:** applies policy before persistence, keeps one live/replay ID,
  measured identical replay with real extended items at every level, and held a counted bound under
  saturation. Against it: InterCat owns durability, framing, pooling and performance risk; the callback's
  allocation budget is missed by a wide margin; and version 0 is about twice the ETL per record when no
  extended data is attached.
- **Both silently merged:** rejected because duplicate/overlapping evidence breaks identity and
  accounting. A companion ETL must stay separately labeled.

## Reversal cost

Before journal-v1, reversal is cheap because the probe has no supported format. After journal-v1, a
change of authority affects broker ownership, identity continuity, privacy claims, recovery and import;
it requires a new ADR, fixture updates and a migration/refusal policy.
