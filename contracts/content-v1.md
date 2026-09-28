# InterCat content v1

Status: **implemented** in plan revisions 234 to 239 (ADR-036, ADR-037): the chunk format, its publication and lifetime
in the store, its readers and viewer (§4), and the capture paths that write one (§5): InterCat's own content fixture,
and WinINet's HTTP exchanges for the processes a request names.

A content chunk holds the content a capture kept of the records of one journal chunk: the bytes a validated source
recorded as a message's content, each with what a person needs to read it honestly (§11.2, I21). It is restricted
evidence beside the journal, never inside it: the journal record keeps its metadata projection unchanged, and nothing
that reads metadata reads a chunk.

## 1. A fragment

A record has at most one fragment, linked by its raw identity (`contracts/identity-v1.md`) within the chunk's capture.
A fragment states:

| Field | What it says |
|---|---|
| record | the admitted record's stream, source epoch and ordinal |
| classification | `EN-ContentClassification`: 1 `OpaqueProviderData`, 2 `TransportFragment`, 3 `ApplicationPayload`, 4 `DecodedFields`, 5 `EncryptedContent` |
| direction | the record's: 0 unknown, 1 outbound (the record's owner sent it), 2 inbound (it received it) |
| encoding | as the source declares it: 1 binary, 2 UTF-8, 3 UTF-16LE. Never guessed from the bytes |
| disposition | how the bytes were kept: 1 whole, 2 truncated by the per-record limit, 3 omitted by the session limit |
| offset | where the fragment begins in its message, when the source states it; otherwise none |
| original | the message's length, when the source exposes it; otherwise none |
| kept | how many bytes the fragment holds, and the bytes |

A fragment covers `[offset, offset + kept)` of its message, from 0 when the source states no offset. Of a message whose
original length is known, the rest is missing: after a truncation, the bytes past what was kept; after an omission, all
of them. Missing bytes are stated and never padded, and a prefix is never shown as a whole message (I21).

## 2. Files and publication

The dependency kind is `Content` (store-v1 code 9), under the name:

```text
content-<generation:D10>.icatc
```

A chunk is published in the generation that publishes the journal chunk whose records it holds content for, so the two
share a generation number. A generation names at most one chunk of its own, and carries every earlier one as it carries
a journal: an additive generation, an index publication, a re-derivation and a compaction each carry every chunk.

A chunk is released with its journal chunk: releasing a recording's journal chunk releases the chunk of the same
generation in the same retention record. Releasing a chunk by name alone is refused, and so is a journal-prefix release
that rewrites one journal while any chunk is kept, since it would leave content its records no longer have. A follower
refuses an evidence session that names a chunk.

A viewer lists a chunk at open without hashing it, as it lists a journal (store-v1 §6): its header and each fragment
carry their own checksums, and a reader checks a fragment before interpreting a byte of it.

## 3. Bytes

Little-endian throughout, with derivation-checkpoint-v1's `str8` and `guid`.

```text
chunk    = "ICATCNT1" (8 ASCII bytes), major u16 = 1, minor u16 = 0,
           capture guid,                        ; the capture whose records the fragments belong to
           policy str8,                         ; the content admission policy, 1..128 bytes of printable ASCII
           recordLimit i32 (1..1,048,576),      ; the per-record limit the fragments were kept under
           inspection u8 (1 disabled, 2 hex-text),
           count u32,
           headerChecksum u32,                  ; CRC-32C of every header byte above
           fragment{count}
fragment = stream u32, epoch u32, ordinal u64,  ; ascending by (stream, epoch, ordinal), each record once
           classification u8, direction u8, encoding u8, disposition u8,
           offset i64 (-1 none, else >= 0),
           original i64 (-1 none, else >= 0),
           kept i32, bytes{kept},
           checksum u32                         ; CRC-32C of this fragment's bytes above
```

A reader refuses a chunk whose magic or version is not this build's, whose capture is not the session's, whose policy
is empty, too long or not printable ASCII, whose record limit or inspection is outside its codes, or whose header
checksum disagrees. It refuses a fragment:

- that is out of order or names a record twice, or whose checksum disagrees;
- whose classification, direction, encoding or disposition is outside its codes;
- that keeps more than the record limit, or a negative count;
- kept whole with an original length that is not its kept length;
- truncated without keeping the record limit, or without an original length longer than what it keeps;
- omitted while keeping a byte;
- with an offset or original length below -1;

and it refuses bytes after the last fragment. A refused chunk is not read in part: a reader states the refusal.

## 4. Who reads a fragment

- **The evidence inspector and `icat raw`** state a record's fragment in one sentence: its classification, direction,
  encoding, lengths and disposition.
- **The content viewer and `icat content`** (revision 236) read one record's fragment afresh, found by its raw identity
  in the current generation under a lease. They state its facts first: what it is and its source, its declared encoding,
  the message's length, which bytes were kept and which are missing, that it is one fragment and not a reassembled
  whole, and the policy it was kept under. Only when the chunk's inspection is `hex-text` and a person asks do they
  show its bytes: a typed range of them, by offset in the message, at most 64 KiB at once, as inert hexadecimal with
  each byte's printable ASCII beside it and, only where the source declares text, as that text with every control,
  format, separator and private character shown as a visible mark. A person may copy the shown bytes as hex, or save a
  range as the bytes it is to a file they name, published whole or not at all. Content kept without that consent is
  never shown, copied or saved one record at a time. `icat content --json` (`content-view-v1`) states the facts and
  never a byte.
- **`icat session`** states what a generation keeps in sum: how many records' messages were kept whole, cut or not
  kept, the bytes kept, and the policies with their record limits and inspection. It keeps and shows no byte.
- **Nothing else.** Search, rankings, metrics, the share report (`intercat-share-report-v1`), the detailed export,
  logs, telemetry and crash diagnostics never read one.
- **Sharing.** A redacted package (`redacted-session-v1`) keeps no chunk and no reference to one, and says so (I22).
  The original evidence package (`original-evidence-package-v1`) carries every chunk as the evidence it is, and states
  how many fragments and bytes it carries before it saves.

## 5. Writing a chunk

A capture keeps content only under a reviewed scoped content policy, which names the sources whose content it keeps,
and only of a source whose catalog entry carries a validated content contract naming the events and fields that hold
it; every other source of the same capture keeps metadata only. At this version there are two: InterCat's own content
fixture, under `scoped-content-fixture-v1`, and WinINet's capture of HTTP exchanges under a request (§5.1). The
fixture:

| | |
|---|---|
| Provider | `InterCat-Fixture-Content`, `22f47a10-5a4c-5c08-9600-f69a24d5bfdd`, raised only by the FX-CONTENT-001 workload |
| Events | 1 a message the process sent, 2 one it received, both version 1 |
| Layout | `processId` i32 (the raising process, which names itself), `conversation` i64, `messageSize` u32, then `messageSize` bytes of `message`; read from the type that raises it |
| Kept as | `ApplicationPayload`, encoding binary (the fixture declares no text encoding), direction by event, offset 0 |
| Limits | 4,096 bytes a record; 16 MiB a session; inspection `hex-text` |
| Recorded by | `icat record --profile content-fixture`, never through the broker, whose evidence follower refuses content (§2) |

A content field is a message's bytes sized by a fixed length field before them, as the provider's manifest declares
(`length=`). The capture copies at most the record limit of them in the callback, with the length the field states as
the original. A message longer than the record limit is kept truncated. Once keeping a message would pass the session
limit, its fragment is written as omitted, with its length, and the capture stops: a record arriving before the stop
takes effect is omitted in turn, never kept past the limit. Each publication writes the fragments of the records its
journal chunk holds.

Qualified live on revision 235 against the workload's truth log, which names each message's length and SHA-256 and
never its bytes: 24 messages kept 23 whole and 1 truncated to 4,096 bytes; 8,000 unpaced messages stopped the capture at
16,773,163 kept bytes, 2,151 whole, 3,015 truncated and 2,834 omitted. In both, every fragment's length and direction was
the truth's, every whole message's SHA-256 matched, nothing was reported lost, and no message byte was found in a
journal or segment file.

### 5.1 A Content request (revision 239, ADR-037)

`icat record --profile content` compiles a bounded request - a source, its mechanism, the processes, the channels, a
per-record and a session limit, stop-at-limit, and the consent to inspect - into `scoped-content-request-v1`, which
keeps that one source's content within the request's limits. It compiles only for a source whose content contract
holds its process scope before persistence and whose capture impact is measured, and only when the request names what
the source can hold: a source that cannot select channels before anything is kept is requested with the one selector
`*`, every channel of the named processes, and any other selector is refused. The source's provider is enabled for
the named processes alone, by the session's process filter; lifecycle stays whole-machine metadata. The broker
previews such a request and never starts it, since its evidence follower does not mirror content.

| | |
|---|---|
| Source | `etw/manifest/Microsoft-Windows-WinINet-Capture`, events 2001 to 2004: a request head and body sent, a response head and body received |
| Layout | `SessionId`, `SequenceNumber`, `Flags`, `PayloadByteLength` (u32 each), then that many bytes of `Payload`; the first three are kept as source fields 16 to 18 |
| Kept as | `ApplicationPayload`, encoding binary, one buffer a record: a part is its buffers in sequence order, from the one flagged first to the one flagged last |
| Scope | the named processes, by the provider's process filter; every exchange of theirs (`*`) |
| Impact | Low: a median 0.71 CPU pp and 2.5% of the workload's time (`bench/results/wininet-capture-impact-20260928T105549Z`) |

Qualified live on revision 239: FX-HTTP-001's 32 exchanges, recorded by `icat record` scoped to the workload, kept 229
buffers whole; regrouped by exchange and sequence, all 128 parts matched the server's truth by length and SHA-256,
every record bound to the workload under `process-binding-v4`, and nothing was lost.

## 6. What is not defined at this version

- A part reassembled from its buffers for a person: the viewer shows one record's buffer, and a part's other buffers are
  its exchange's other records (M8). HTTPS and HTTP/2 through WinINet are unmeasured, and so are the other client
  libraries, which raise no such records.
- Several fragments of one record, and reassembly across records: a stream's missing ranges between fragments.
- An evidence follower that mirrors content, so a broker capture could keep it.
- Releasing content alone while keeping the metadata, which needs a retention kind of its own.
- Content an imported file already holds (§11.1).
