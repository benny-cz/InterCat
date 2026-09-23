# journal-v1: the authoritative admitted-evidence format

Status: frozen by IC-011 on the evidence of ADR-008 and ADR-009.
Format major 1, minor 0. Magic `ICJ1`.
Golden corpus: `fixtures/FX-JOURNAL-002/golden/journal-v1.icatj`.

This document is the contract. Every offset, order and width below is binding: changing one is a new
format major and an ADR, and `InterCat.Storage.Tests` fails on the golden corpus first if one is changed
by accident. The disposable `journal-probe-v0` that IC-009 used to reach this decision is not this format
and must never be recognized as one.

## What a journal is

One journal file is **one capture**. The capture identity lives in the file header, not in every record,
because every record in the file shares it. A record from another capture belongs in another journal, and
the writer refuses it rather than relabelling it. A capture may span several files: a live recording completes a
chunk with every publication, each a complete journal of its own with the same capture, clock and schema table, and
its record ordinals continue from the chunk before (`contracts/store-v1.md` §7).

A journal holds InterCat's **admitted** evidence. It is not a byte-identical replacement for an ETL and
not a guarantee that ETW lost nothing: policy omissions and source loss are separate counters and neither
is inferred from the other (§18.1, §9.3).

## Byte order

Every fixed-width field is little-endian, **except GUIDs, which are big-endian** so that a hexadecimal
dump reads in the order the identifier is written. Text is UTF-8 with a 16-bit byte length, bounded at
512 bytes. A byte run is a 32-bit length followed by that many bytes.

## File layout

```text
FileHeader
SourceClockFrame          exactly one, before any batch
SchemaTableFrame          exactly one, before any batch
RecordBatchFrame*         zero or more
TerminalFrame             exactly one, last
```

A reader that reaches the end of the file without a terminal frame refuses it as an interrupted capture.
A partial trailing batch is never committed and never read (§20.1).

The clock frame is not optional and is not repeatable. A journal that names no clock is refused rather
than read, because its records' native readings would then be presented against a clock the file does not
name, which is the conversion I8 forbids; a journal that names two is refused because every record's
reading would be ambiguous about which one produced it. A batch before the clock frame is refused for the
same reason.

### FileHeader — 64 bytes

| Offset | Width | Field |
|---|---|---|
| 0 | 4 | magic `ICJ1` (0x314A4349 little-endian) |
| 4 | 2 | format major, 1 |
| 6 | 2 | format minor, 0 |
| 8 | 8 | feature flags, 0. A reader refuses any flag it does not implement |
| 16 | 16 | capture id (big-endian GUID) |
| 32 | 8 | creation time, UTC ticks |
| 40 | 32 | SHA-256 of bytes 0..39 |

### Frame

```text
kind        u32   1 SourceClock, 2 SchemaTable, 3 RecordBatch, 4 Terminal
length      i32   payload bytes, 0..128 MiB
payload     length bytes
checksum    32    SHA-256 of payload
```

The length precedes the payload so a reader can skip a frame it will not use; the checksum follows it so
a truncated write cannot produce a frame that verifies. An unknown kind is refused, never skipped: a
reader that skipped frames could not say what it had read.

### SourceClockFrame payload

The §8.1 source clock descriptor, written once per journal. A journal whose records name a clock the file
does not describe is incomplete evidence, so this frame precedes every batch.

```text
clockId                 16   big-endian GUID
hostId                  16   big-endian GUID
kind                    u8   EN-SourceClockKind
encoding                u8   EN-TimestampEncoding
ticksPerSecond          i64
captureEpochNativeTicks i64
rounding                u8   EN-TimestampRounding
maximumAbsoluteSessionNanoseconds i64
```

### SchemaTableFrame payload

The schema and policy tables records reference by number. They travel with the journal because the
viewing machine may not have the recording machine's manifests (§18.3).

```text
schemaCount  i32
  reference    u32   1-based; 0 in a record means "no schema"
  providerId   16
  eventId      u16
  version      u8
  fingerprint  text
policyCount  i32
  reference    u32   1-based
  policyId     text
```

### RecordBatchFrame payload

```text
recordCount  i32   1..1,000,000
first        RawRecordId minus its capture id
last         RawRecordId minus its capture id
records[recordCount]
```

The first and last identities let a reader say what a batch covers without decoding it (§20.1). They are
checked against the records on read; a batch whose declaration disagrees with its contents is refused.

### Record

```text
streamId                u32
sourceEpoch             u32
recordOrdinal           u64
providerId              16
eventId                 u16
version                 u8
channel                 u8
level                   u8
opcode                  u8
task                    u16
keyword                 u64
headerFlags             u16
eventProperty           u16
processId               i32
threadId                i32
activityId              16
relatedActivityId       16
processorNumber         u16
loggerId                u16
clockId                 16
timestampEncoding       u8
nativeTicks             i64
pointerSize             u8
schemaReference         u32    0 = none
admissionPolicyReference u32
extendedItemCount       u8     0..64
omittedExtendedItemCount u16
  type           u16
  flags          u16    the linkage bit, not a general flags word
  originalLength u16
  bytes          byte run
bodyClassification      u8
bodyDisposition         u8
bodyOriginalLength      i32
bodyBytes               byte run
recordChecksum          u32    CRC-32C of every byte of this record above
```

## Why each of those fields is here

- **The identity four** (`captureId`, `streamId`, `sourceEpoch`, `recordOrdinal`) are the raw record
  identity of `contracts/identity-v1.md`. Replaying a journal preserves them; re-importing reuses them
  after integrity validation (I1, §18.4).
- **The header fields** are what Windows puts in `EVENT_HEADER`, minus the values that are meaningless
  outside the callback. `UserContext` and other callback pointers are never serialized.
- **The buffer context** says which processor delivered the record and from which logger. Equal-time
  records from different CPUs can arrive in an unpredictable order, and the processor is part of telling
  them apart (§18.4).
- **`pointerSize`** travels with the record because a reader on another architecture needs it to
  interpret the fields, and the recording machine's width is not the reading machine's (§18.1).
- **`timestampEncoding` and `clockId`** keep the native reading in its original encoding. A converted
  value is never stored in place of it (I8, ADR-006).
- **`originalLength` on an extended item and on the body** is what makes a truncation visible. A prefix
  is never presented as a whole (I21).
- **`bodyDisposition`** is why a body is absent. Unknown-body suppression is a visible policy omission,
  not unexplained parser loss (§18.2, I13).
- **`omittedExtendedItemCount`** distinguishes a record that carried no extended items from one whose
  items were refused or did not fit. Zero kept items is not evidence of zero items (R3, R21).
- **`recordChecksum`** is per record, not only per batch. A batch checksum can say only that something in
  the batch is wrong; a per-record check says which record, so a reader can quarantine one and keep the
  rest.

## Buffer ownership

Every buffer has a single owner at a time: callback, then queue, then journal writer, then returned pool
(§18.1). Ownership is an explicit disposable lease, `EnvelopeBuffer`, not a convention:

- A lease is rented from the shared array pool and **copies** the callback's bytes. Nothing in an
  envelope points into callback memory.
- A returned lease refuses every read, so a use-after-return raises at the mistake rather than yielding
  another owner's bytes later.
- Disposing twice is safe and is not a second return. Returning one array twice would hand one buffer to
  two owners, which is the failure the lease exists to prevent.
- Appending a record passes its ownership to the writer. Writing a batch returns every buffer in it, and
  disposing a writer mid-batch returns what it was holding.

## What this format does not do

Durability, crash recovery and the committed-boundary protocol of §20.1 are **IC-016's**, and
`JournalV1Writer` implements none of them: it produces a file. A caller that needs a commit protocol
builds one on top of this contract. Segments, manifests, dictionaries and compaction are §20.1's derived
store and are not this format.
