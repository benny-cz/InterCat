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

Since revision 306 every chunk can also be released at once, on its own: a **content release** (store-v1 §8). It
publishes a generation that names no chunk and keeps everything else - every journal, row and derived file, the plan,
the ledger, the calibration and the committed boundary - with a retention record of kind `Content` that names the
chunks, their bytes, how many records' content they held, the digest of their dependency lines, and why. The messages'
bytes go, and so does what each record kept of its message - its classification, lengths and how it was cut - since a
chunk holds both; every record and its size stay. It is refused, and nothing is published, when the generation keeps
no chunk, and when its capture has not finished - it carries no capture finalization marker - since a capture still
recording would find the session changed beneath it, and one that stopped without finishing cannot be told from it:
its content goes with its journal chunks instead. `icat retain <session> --release-content` measures the release - the
chunks, the records' content and the message bytes they hold - and performs it only with `--confirm` and a stated
`--reason`. While the release's generation is current, a record's content states when and why it went; `icat session`
states any retention record of the generation it reads.

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
  what encryption its bytes sit above where its classification says (revision 243: an application payload is the
  message as the application held it, above any encryption of its connection, which no admitted source says was or was
  not encrypted), the message's length, which bytes were kept and which are missing, that it is one fragment and not a
  reassembled whole, and the policy it was kept under. Only when the chunk's inspection is `hex-text` and a person asks
  do they show its bytes: a typed range of them, by offset in the message, at most 64 KiB at once, as inert
  hexadecimal with each byte's printable ASCII beside it and, only where the source declares text, as that text with
  every control, format, separator and private character shown as a visible mark. A person may copy the shown bytes as
  hex, or save a range as the bytes it is to a file they name, published whole or not at all. Content kept without
  that consent is never shown, copied or saved one record at a time. `icat content --json` (`content-view-v1`) states
  the facts and never a byte.
- **A part** (revision 242, M8's first step). A record whose source keeps its buffer's exchange, place and ends as
  source fields - WinINet's capture (§5.1) - is one buffer of a part: its exchange's request or response head or body.
  The viewer and `icat content` say which buffer of which part it is, which of the part's buffers were recorded and
  kept, and whether they make the whole part: every buffer from the one flagged first to the one flagged last, in
  sequence order, each kept whole. Only such a part may be shown, copied or saved as one (the viewer's toggle,
  `icat content --part`); a part missing a buffer, or holding one cut, names what it lacks and is never shown, copied
  or saved as a whole (I21, P2). Since revision 305 it is shown with its gaps in place, when a person asks: its pieces
  in order, each recorded buffer under a heading that names it, its ends and, while every piece before it has a known
  length, its bytes in the part, with its kept bytes in hex numbered from its own first byte; and a line of its own,
  beginning with `--` as no line of hex does, for each gap - buffers never recorded, before the first recorded one,
  between two or after the last; the rest of a buffer the record limit cut; a buffer recorded with none of its bytes
  kept - stating its length only where a record states it. Such a view is chosen by buffer number, shows at most
  64 KiB at once and stops at a buffer's start, offers no text view, and copies its lines as shown; it is never saved
  as a file. `content-view-v1`'s part lists the gaps, additively (`gaps`: each one's `kind` - `Cut`, `NotKept` or
  `NotRecorded` - and its `firstBuffer`, `lastBuffer`, `length` and `partOffset`, each null where not known); a whole
  part's list is empty. An exchange's number is its client process's own count from 1 (revision 244),
  so one number, event and process ID can name two exchanges in a session - a process ID used again, or WinINet loaded
  again - and a part is found among its number's buffers in time: a buffer flagged first, one after a buffer flagged
  last, or one numbered no later than the one before it opens another use, and two uses are never merged (R22).
- **An exchange** (revision 247, `contracts/http-exchanges-v1.md`). A process's HTTP exchanges are a row of its rung;
  Enter lists them, each with what was recorded of its four parts and how long it took, and Enter on one opens its
  buffers, where the viewer reads a buffer and its part. Listing exchanges reads metadata and source fields, never
  content.
- **`icat session`** states what a generation keeps in sum: how many records' messages were kept whole, cut or not
  kept, the bytes kept, and the policies with their record limits and inspection. It keeps and shows no byte.
- **Nothing else.** Search, rankings, metrics, the share report (`intercat-share-report-v1`), the detailed export,
  logs, telemetry and crash diagnostics never read one.
- **Sharing.** A redacted package (`redacted-session-v1`) keeps no chunk and no reference to one, and says so (I22).
  Since revision 250 it keeps an HTTP exchange's shape - its buffers' places and ends, and its number as a pseudonym -
  so its exchanges group as the source's do, with their sizes and timing, and none of their bytes.
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
| Kept as | `ApplicationPayload`, encoding binary, one buffer a record: a part is its buffers in sequence order, from the one flagged first to the one flagged last. A body is what its client sent or read: a chunked response's without its chunk framing, which only its head names (FX-HTTP-003) |
| Exchange | `SessionId`: the client process's own count from 1, so it names an exchange only within one run of its client; exchanges at once keep their own (FX-HTTP-003, revision 244) |
| Scope | the named processes, by the provider's process filter, each held open while the capture runs so no other process can be given its ID; a process not running is refused (revision 245); every exchange of theirs (`*`) |
| Impact | Low: a median 0.71 CPU pp and 2.5% of the workload's time (`bench/results/wininet-capture-impact-20260928T105549Z`); over TLS a median 0.74 CPU pp (`bench/results/wininet-capture-impact-tls-20260928T121427Z`) |
| Encryption | none stated: WinINet holds a message above any encryption, so over HTTPS the capture holds its plaintext, and a record does not say whether its exchange was encrypted (FX-HTTP-002, revision 243). A request says so before it records |

Qualified live on revision 239: FX-HTTP-001's 32 exchanges, recorded by `icat record` scoped to the workload, kept 229
buffers whole; regrouped by exchange and sequence, all 128 parts matched the server's truth by length and SHA-256,
every record bound to the workload under `process-binding-v4`, and nothing was lost. And on revision 243 over TLS:
FX-HTTP-002's 32 exchanges kept 229 buffers whole, all 128 parts matched the bytes the server decrypted and encrypted,
no buffer began as a TLS record does, and `icat content --part` saved a 262,144-byte response body that matched. And on
revision 244: FX-HTTP-003's 256 exchanges, eight at once with chunked responses, kept 1,540 buffers whole; matched with
their requests by path, all 1,024 parts matched the server's bodies and none the chunk framing.

## 6. What is not defined at this version

- HTTP/2, compressed responses and asynchronous WinINet are unmeasured, and so are the other client libraries, which
  raise no such records.
- Several fragments of one record, and reassembly across records: a stream's missing ranges between fragments.
- An evidence follower that mirrors content, so a broker capture could keep it.
- Content an imported file already holds (§11.1).
- A content release's record in later generations: it is its own generation's, as every retention record is, so a later
  publication leaves it behind and a record's content then says only that a retention may have released it.
