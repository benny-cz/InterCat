# InterCat content v1

Status: **implemented** in plan revision 234 (ADR-036): the chunk format, its publication and lifetime in the store,
and its readers. No capture writes one yet; the capture path is the next slice, and the viewer follows it.

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

- **The viewer and `icat raw`** state a record's fragment: its classification, direction, encoding, lengths and
  disposition. They show its bytes only when the chunk's inspection is `hex-text` and a person asks, bounded and inert.
- **Nothing else.** Search, rankings, metrics, the share report (`intercat-share-report-v1`), the detailed export,
  logs, telemetry and crash diagnostics never read one.
- **Sharing.** A redacted package (`redacted-session-v1`) keeps no chunk and no reference to one, and says so (I22).
  The original evidence package (`original-evidence-package-v1`) carries every chunk as the evidence it is, and states
  how many fragments and bytes it carries before it saves.

## 5. What is not defined at this version

- A capture that writes chunks. The next slice admits a controlled fixture provider's bytes under a scoped content
  profile.
- Several fragments of one record, and reassembly across records: a stream's missing ranges between fragments.
- Releasing content alone while keeping the metadata, which needs a retention kind of its own.
- Content an imported file already holds (§11.1).
