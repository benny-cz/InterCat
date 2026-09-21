# ADR-008: Authoritative journal versus ETL evidence gate

- Status: proposed; the elevated comparison has run, the decision is blocked on a saturation series and a supported build
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

## Decision

No authoritative-source decision is accepted yet. The planned admitted-journal direction remains the
candidate, not a measured conclusion. `journal-probe-v0` is disposable and must never be recognized as
an `.icat` journal.

Two of the five gate inputs are now recorded; the two blockers that remain are the reason this ADR stays
proposed:

| Gate input | State |
|---|---|
| Truth-to-source and source-to-replay fidelity, including extended items | Recorded: identical ordered fingerprint, 190 of 190 extended items replayed |
| Provider loss, consumer-buffer loss, application drops and policy omissions kept separate | Recorded: four independent counters, never summed |
| Callback time, queue high-water, writer CPU, allocations, bytes written and flush latency | Recorded, at one load point only |
| Behavior under queue and disk saturation | **Missing.** One load point is not a curve; the queue never exceeded 1.1% of capacity |
| Exact Windows build, adapter version, profile, buffers, storage device and command line | Build, profile, buffers and settings recorded. The build is outside the §1.3 support matrix, and the reference storage device of §12 is not yet the measured one |

Close this ADR only after a declared load series on the reference machine reaches queue and disk
saturation and is repeated on a supported build.

The comparison must not call the admitted journal byte-identical to ETL: policy intentionally removes
unapproved bodies. It compares preservation of approved evidence and independent truth, while recording
the intentional omissions. The 6–7× size difference measured above is the policy working, not a fidelity
loss: the ETL is unfiltered original evidence from every process on the machine.

## Reproducing the runs

From an elevated Windows shell at the repository root:

```powershell
dotnet build InterCat.slnx -c Release
dotnet run --project tools/InterCat.CaptureComparison -c Release --no-build -- --output bench/results/capture-comparison-<run-id>
dotnet run --project tools/InterCat.CaptureComparison -c Release --no-build -- --output bench/results/capture-comparison-<run-id>-stacks --request-stacks true
```

The tool refuses a non-elevated shell before creating a session or an output directory, refuses an
existing output directory, and exits non-zero while any decision blocker remains.

## Alternatives

- **ETL authoritative:** simpler source fidelity and standard tooling, and the measured runs show it
  costs this process nothing during acquisition because Windows owns the writer. Against it:
  metadata-only sanitation cannot be claimed for an unfiltered ETL, the file was 6–7× larger for the same
  fixture, and growing-file live access remains a separate problem.
- **Admitted journal authoritative:** applies policy before persistence, keeps one live/replay ID, and
  measured identical replay with real extended items. Against it: InterCat owns durability, framing,
  pooling and performance risk, and the callback's allocation budget is not met yet.
- **Both silently merged:** rejected because duplicate/overlapping evidence breaks identity and
  accounting. A companion ETL must stay separately labeled.

## Reversal cost

Before journal-v1, reversal is cheap because the probe has no supported format. After journal-v1, a
change of authority affects broker ownership, identity continuity, privacy claims, recovery and import;
it requires a new ADR, fixture updates and a migration/refusal policy.
