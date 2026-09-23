# capture-finalization-v1

`capture-finalization-v1` is the durable last-publication marker for a live capture. A complete `journal-v1`
file proves that one immutable journal chunk committed; it does **not** prove that the chunk was the capture's
last chunk. That distinction matters after a recorder/broker crash once live publication is enabled.

## 1. Publication rule

A started live capture publishes exactly one finalization marker, and only in the generation that publishes its
last journal chunk. The writer stages it only after the owned capture session's stop operation has returned and
before that final generation is committed. Intermediate live generations never carry it.

The dependency kind is `CaptureFinalization` (code 7 in `contracts/store-v1.md`) and the file name is:

```text
capture-finalization-<generation:D10>.json
```

The marker is evidence about capture shutdown, not a derivation of admitted records. Re-derivation, compaction,
mirroring and retention therefore carry it unchanged. Derived-file retention may not release it.

## 2. JSON contract

The UTF-8 JSON object is at most 4 KiB and uses these fields:

```text
{
  "contract": "capture-finalization-v1",
  "captureId": <non-empty GUID>,
  "finalizedUtc": <DateTimeOffset>,
  "providersStopped": <boolean>,
  "callbacksDrained": <boolean>
}
```

`finalizedUtc` is provenance for the final publication; it is not a source timestamp and never orders captured
records. `providersStopped` and `callbacksDrained` record exactly what the capture stop operation had proved before
the marker was staged. `false` is evidence that the milestone was not proved, not an instruction to infer failure.

Unknown JSON members, an unknown contract name, an empty capture ID, a default timestamp, an empty file or a file
larger than 4 KiB are refused.

## 3. Restart verification

A recovery path may promote `JournalFinalized` only after it has all of the following:

1. the protected capture directory was reopened without creating it;
2. the current store manifest and every dependency verify;
3. the manifest's session/capture identity and prepared-plan source identity match durable broker ownership;
4. exactly one `CaptureFinalization` dependency decodes and names that capture;
5. the manifest's committed boundary names a journal dependency with the same length and digest; and
6. replay of that boundary journal reaches a valid terminal frame and yields the committed record count.

When those checks succeed, `callbacksDrained=true` in the marker is also durable evidence for the callback-drain
milestone. Provider shutdown is still confirmed independently from the broker's owned ETW session identity; the
marker does not authorize stopping any session.

For sessions written before this contract existed, a verified `CoverageLedger` dependency may serve as a legacy
last-publication signal because the old live recorder published that ledger only with its final generation. Such a
legacy signal does not recover callback-drain evidence, and a capture whose old loss counters were unreadable may
therefore remain honestly partial after restart.
