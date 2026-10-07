# collector-identities-v1

Status: revision 414 (§19.5); the broker's client since revision 415; labels since revision 416
Owner: `InterCat.Storage` (`CollectorIdentitiesV1`); written by `InterCat.Capture.Recording` (`LiveRecorder`)

The processes that collected a capture, by the identity its own lifecycle records give them. InterCat's broker, the
window or `icat` that asked it to record, and `icat record` recording on its own are processes on the machine they
observe, so their own activity - a broker's journal writes, a viewer's control pipe - can appear in their own capture
(§19.5). A record names a process by its PID alone, which the machine reuses, so this file names each collector by its
PID and the moment the operating system created it, which a lifecycle record of the instance carries too. A reader
labels the instance they name as collector activity; it never removes a record for it, since an interaction with
InterCat can be what is investigated, and it never takes a process for a collector by its image name.

## 1. Publication

A live capture publishes its collectors in its first generation, as the dependency kind `CollectorIdentities` (code 11,
`contracts/store-v1.md`) named:

```text
collector-identities-<generation:D10>.json
```

Each later generation carries it unchanged, as re-derivation, compaction and checkpoints do; a follower mirroring a
broker's capture mirrors it when it first appears, and retention never releases it. A redacted package never carries it:
a collector's PID and creation time would link the package to the machine and the moment its processes started, as a
clock calibration's boot token would, so the package's leak scan looks for its digest. An original evidence package
copies it like every other file. An import, a redacted package and every capture before revision 414 carry none, and a
reader says that its collectors are not recorded rather than that it had none.

## 2. JSON contract

The UTF-8 JSON object is at most 4 KiB:

```text
{
  "contract": "collector-identities-v1",
  "captureId": <non-empty GUID: the journal's capture>,
  "processes": [
    { "role": "Broker" | "Client" | "Recorder", "processId": <integer, 1 to 2,147,483,647>,
      "createdUtc": <DateTimeOffset> (absent when it could not be read) }
  ]
}
```

- `Broker` is the broker process that owned the capture's trace session and wrote its journal; `Client` is the process
  whose authenticated request started the capture - the window or `icat capture` - as the control pipe names its client
  (`GetNamedPipeClientProcessId`, which `contracts/broker-v1.md` keeps out of every authorization decision); `Recorder`
  is `icat record`, which owns its trace session itself.
- `processes` holds 1 to 8 processes, no two naming one role and one PID. A capture recorded by a broker names its
  broker, and since revision 415 the client that asked it to record, when its control pipe named one; `icat record`
  names itself.
- `createdUtc` is the process's creation time as the operating system keeps it (`GetProcessTimes`), at its 100 ns
  resolution: the instant a lifecycle record's creation time of the same instance carries. It is absent when the process
  could not be opened to read it, as a client that exited before the capture started cannot.

Unknown members, another contract name, an empty capture identity, no process or more than 8, an unknown role, a PID
outside its range, a role and PID named twice, an empty file or one over 4 KiB are refused.

## 3. What a reader may say

A process instance is a collector's when its PID is the collector's and its creation time, from its lifecycle records,
is the one named (`collector-binding-v1`, revision 416): the start key a lifecycle record carries is the identity, and a
PID alone, or an image name, is never taken for one, so another holder of a collector's PID is never labelled. An
instance whose lifecycle records carried no creation time, or a collector whose creation time was not read, is not
labelled, and the reader keeps that collector as unfound rather than guess an instance for it. An instance named in two
roles keeps the first the file names. The records bound to a collector's instance are collector activity: counted,
listed and exported as any record is, and labelled where they are shown - its ranked row's caption ("PID 4120 · running
at start · InterCat's broker"), the inspector's explanation, its graph node's card and `icat processes`, in one set of
words (`CollectorText`).
