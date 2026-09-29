# clock-calibration-v1

Status: revision 255 (ADR-040)
Owner: `InterCat.Storage` (`ClockCalibrationV1`); written by `InterCat.Capture.Recording` (`LiveRecorder`)

A live capture's source clock paired with the wall clock, and the boot it ran in (§8.1, §8.2). A journal names its source
clock and capture epoch but not what wall-clock time a reading was, nor which boot of its machine it ran in; this file
records both, as evidence about the capture that no journal holds and nothing can rebuild.

## 1. Publication

A live capture publishes one calibration, in the generation that publishes its last journal chunk, as the dependency
kind `ClockCalibration` (code 10, `contracts/store-v1.md`) named:

```text
clock-calibration-<generation:D10>.json
```

Re-derivation, compaction, checkpoints and a follower mirroring a broker's capture carry it unchanged; retention never
releases it. A redacted package never carries it: its wall-clock readings date the capture and its boot token is shared
by every capture of that boot, so the package's leak scan looks for both. An original evidence package copies it like
every other file. An import, a redacted package, a capture that ended before its last publication, and every capture
before revision 255 carry none, and a reader says so rather than assuming one.

## 2. JSON contract

The UTF-8 JSON object is at most 16 KiB:

```text
{
  "contract": "clock-calibration-v1",
  "captureId": <non-empty GUID: the journal's capture>,
  "clockId": <non-empty GUID: the journal's source clock>,
  "bootToken": <non-empty GUID> (absent when none could be kept),
  "bootCount": <non-negative integer> (absent when unreadable),
  "wallClock": <text, at most 128 characters>,
  "samples": [
    { "nativeTicks": <integer>, "utc": <DateTimeOffset>, "acquisitionUncertaintyNanoseconds": <non-negative integer> }
  ]
}
```

- `samples` holds 1 to 64 samples in the order taken, their `nativeTicks` non-decreasing: a capture takes one when it
  starts and one when it stops. Since revision 272 the stop sample is taken as the capture asks its session to stop,
  before the session drains, so the last sample ends the capture's recording (`metrics-v1` §7); an earlier capture took
  it once its session had drained. A sample's `nativeTicks` is the source clock's reading midway between two reads that
  bracket one read of the wall clock, whose UTC reading is `utc`; `acquisitionUncertaintyNanoseconds` is half that
  bracket widened by the source clock's own tick, plus the wall clock's resolution. It bounds only how far apart the
  pair was taken: it says nothing of how right the wall clock was, which is a synchronization claim no sample makes.
- `wallClock` names what was read: on Windows, `GetSystemTimePreciseAsFileTime`, whose resolution is 100 ns.
- `bootToken` is a random identity of the boot the capture ran in: the first capture of a boot mints it into a volatile
  registry key, `HKEY_LOCAL_MACHINE\SOFTWARE\InterCat.Boot`, which Windows deletes when it restarts, so every capture of
  one boot of one machine names the same token and no capture of another boot or machine can. A process that cannot
  write the machine's registry mints none, and records none unless another capture of the boot already kept one.
- `bootCount` is Windows' own count of its boots, kept for people. It is no identity: a clone counts as its original did.

Unknown members, another contract name, an empty identity or token, a negative count or uncertainty, a sample without a
wall-clock reading, samples out of order, more than 64 samples, an empty file or one over 16 KiB are refused.

## 3. What a reader may say

Two samples give the wall clock's rate against the source clock over the capture, with the half-width their acquisition
allows; `icat session` states it. Two captures naming one `bootToken` ran in one boot of one machine and read one
performance counter, so their readings relate exactly through their capture epochs; captures naming none, or different
ones, relate only through their wall clocks or a person's alignment (ADR-039), whose synchronization stays unknown
until someone states it.
