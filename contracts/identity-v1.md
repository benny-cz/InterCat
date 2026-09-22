# InterCat identity and local-clock contract v1

Status: accepted foundation contract for IC-007. The canonical standalone-ETL importer remains an
IC-013 deliverable; this contract constrains it but does not claim that it exists.

## Scope and compatibility

This contract fixes the identity inputs and timestamp representations used by live capture, journal
replay and future import. It does not make a Windows numeric value globally unique, infer an exact start
from inventory presence, or make clocks from different hosts comparable.

The M0 evidence files written before this contract used `{ rawRecordId, factIndex }`. The v1 reader
accepts that shape and deterministically maps it to the v1 normalizer and a legacy fact discriminator.
New output writes the canonical shape only. This is an input compatibility path, not the format for the
future journal.

## Raw records and normalized observations

The live raw-record key is exactly:

```text
(captureId UUID, sourceStreamId u32, sourceEpoch u32, recordOrdinal u64)
```

The ordinal is assigned before parallel decoding. A source/profile discontinuity opens a new source
epoch; ordinals do not become display order.

One raw record can yield several facts. A normalized observation ID is exactly:

```text
(rawRecordId, normalizerContractVersion u32, factKey u128)
```

`normalizerContractVersion` is non-zero. `factKey` is the first 128 bits of SHA-256 over a big-endian
UTF-8 byte-length, NFC-normalized semantic discriminator, and big-endian occurrence number. A
normalizer must assign the occurrence from a canonical semantic order, never worker or dictionary
enumeration order. The same retained raw record, saved schema and normalizer version must reproduce the
same set. IC-013 supplies the multi-fact importer fixture before I2 can be marked fully covered.

New JSON writes `normalizerContractVersion` as a number and `factKey` as 32 hexadecimal characters.
Correlation and aliases never replace a raw or observation ID.

## Process instances, epochs and aliases

A process instance key includes host, boot, PID and lifecycle epoch plus exactly one of:

1. a provider start key, optionally accompanied by provider creation time;
2. an observed creation time; or
3. the raw-record key of a provisional inventory witness.

There is no PID-only constructor. An inventory row proves presence, not creation. A process ID is UUIDv8
derived from SHA-256 of the versioned canonical key. Equal PID/start values on another host or boot, or
equal PIDs in another lifecycle epoch, therefore remain distinct.

Resolution prefers an exact provider start key even when the record was delivered after a newer epoch
started. Without such evidence, time proximity does not choose an epoch. A single witnessed lifecycle may
resolve a record whose time is inside its half-open interval, and so may the **earliest** of a PID's
several lifecycles: no witnessed epoch precedes it, so a record inside it carries exactly the risk a
record of a PID with one lifecycle carries. A record inside a **later** lifecycle of a reused PID stays
unresolved, because it may be a late record of an earlier epoch and PID and time cannot rule that out
(ADR-013; `EarliestLifecycle` in `ProcessEpochResolver`). `contracts/entities-v1.md` exposes such a
record as a candidate binding that an evidence policy may admit, labelled, and that the default does not.

When stronger evidence reconciles a provisional identity, append a `ProcessAliasRevision` containing
the old ID, canonical ID, non-zero revision, reason and evidence observation. Do not mutate observations,
delete the old identity or change an earlier snapshot.

## Resource instances

A resource instance key includes host, boot, mechanism, a non-zero source instance key and lifecycle
epoch. Zero means unavailable and is rejected. Equal names are intentionally not identity inputs;
original and normalized names remain attributes/grouping keys. Address, handle, port, message ID and
similar reusable values require a lifecycle epoch and mechanism-specific evidence.

## Native, local, wall and workspace time

The four time concepts are distinct types:

- `NativeTimestamp`: raw signed ticks, `ClockId`, and original encoding;
- `SessionTimestamp`: integer nanoseconds relative to the source clock's capture epoch;
- `WallClockEstimate`: UTC ticks plus non-negative acquisition uncertainty;
- `WorkspaceTimestamp`: derived aligned nanoseconds plus alignment revision.

A source-clock descriptor records host, clock kind, timestamp encoding, ticks per second, native capture
epoch, conversion rounding and a maximum plausible session distance. Calibration samples retain native
ticks, UTC ticks and acquisition uncertainty.

QPC deltas convert with checked `Int128` intermediates to 1,000,000,000 session ticks per second. V1
uses round-to-nearest, ties-to-even. A clock/encoding mismatch, Int64 overflow or value beyond the
declared plausible distance is quarantined; raw evidence stays intact. Applying alignment returns a new
workspace value and cannot rewrite native, local or wall-clock fields. QPC values on different hosts
are never compared directly.

## Standalone ETL boundary

IC-013 must add source-content identity, import-contract version, canonical external sorting,
equal-time CPU/header tie keys, canonical admitted bytes, collision comparison and occurrence indexes
for byte-identical records. Callback delivery order and callback-local pointers are forbidden inputs.
Until that fixture exists, I1 and the full reproducibility claim in I2 remain declared uncovered.
