# ADR-106: A batch is a run of messages, counted exactly, never spread into messages no record tells apart

- Status: accepted for M7's batching; the derivation, `icat messages` and the window (revision 1035); the SDK's batch
  events and the capture's admission of them follow
- Date: 2026-11-07
- Decision owners: InterCat maintainers
- Relates to: ADR-102, ADR-103, ADR-104, plan §15 M7, §12, `contracts/sdk-v1.md`, `contracts/cooperative-messages-v1.md`, R3, R8, R18, R20

## Context

A capture keeps every call of the cooperative SDK up to 100,000 a second and says what it lost past that (`sdk-v1` §8).
M7's fallback asks, where a path is faster than that, for bounded batching with explicit semantics, and never an
estimate of individual messages. A batch exists to say far more messages a second than single events can; a derivation
that made one object of each of its messages would grow with the messages, past the analysis budget (§12, R8).

## Decision

1. **A batch is one record of a run of consecutive messages of one channel's epoch**: its first sequence and its count,
   up to 65,536 (`MessageCount`, source field 41); their logical lengths, and the bytes copied for them, together. A
   publication batch publishes each message of its run once; a consumption batch is one reader's consumption of each.
2. **The derivation keys a batch as a run**: it sweeps each channel epoch's sequence space once over every record's
   run - a message said alone a run of one - and each stretch the same records cover is one entry, its state as one
   message's. A run is split only where another record begins or ends within it; a stretch no record covers is no
   message. The index grows with the records, never with the messages a batch says.
3. **Counts are exact over runs**: each message of a run is counted, and each reader's consumption of each; a batch's
   lengths are counted once however its run splits, and its copy is never split among its messages.
4. **A batch names no message where it cannot**: one with no count, a count below one or above 65,536, or a run past the
   last sequence a long holds is counted apart, never keyed by a value not observed (R3).
5. **The rule is `cooperative-message-v2`** (R20): a generation without batches is keyed exactly as by v1.
6. **A run is said as one row**, in the words `icat messages` and the window share (R18): its first and last sequence and
   its batch's length together; the document says each entry's `count` and each record's `batch`.

## Consequences

- A capture of an application that batches reads as exactly as one that does not, in bounded memory.
- No source raises a batch yet: the SDK's batch events, the capture's admission of them, a measured fixture and their
  cost follow; until then a batch record is read only from test generations.
- 20 mutations of the keying, the sweep, the counts, the words, the command line and the window are caught.

## Addendum: the SDK raises batches, and a capture admits them (revision 1036)

The schema's minor 1 adds a batch's two events, `MessagesPublished` (8, Send) and `MessagesConsumed` (9, Receive): each
names its process first, then its channel, epoch, first sequence and count, the messages' lengths together and, of a
publication, the bytes copied for them together, every field at a fixed offset. `InterCat.Sdk` raises them through
`PublishedBatch` and `ConsumedBatch`, the native library through `intercat_channel_published_batch` and
`intercat_channel_consumed_batch`, each refusing a batch of no message or more than 65,536, a negative length, a copy
below -1 or a run past the last sequence a long holds. A capture admits each as an application record: its count the
source field `MessageCount`, its first sequence `MessageSequence`, its lengths together its application payload. A
capture built before minor 1 counts their records undecodable, never misreads them. The header's descriptors and
version stay the manifest's, which a test holds. 19 mutations of the events, the calls, their refusals, the
admission and the header are caught; a batch recorded through the product follows.

## Addendum: batches measured through the product (revision 1037)

FX-SDK-005 says FX-SDK-001's ring in batches of eight through `InterCat.Sdk`, and FX-SDK-006 FX-SDK-002's native
application in batches of ten through the C library, each workload's truth logging every message by itself
(`tools/InterCat.SdkProbe --batch`). In three runs of each, nothing lost, every message was one message of the run its
batch said and every reader's consumption of it one consumption, by process; each batch's lengths together were the
truth's lengths of its messages summed; the 161 and 131 messages were 21 and 14 rows, never spread into messages no record
tells apart; and the message each said alone, which no reader read, stayed open. The native library's batch calls, whose
fields no unit test reads, are held by FX-SDK-006. The cost of saying messages in batches is measured next.
