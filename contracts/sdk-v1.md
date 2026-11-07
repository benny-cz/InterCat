# Cooperative SDK contract, version 1

Status: M7, revision 1025 (ADR-102)
Owner: `InterCat.Sdk` (`InterCatSdkEventSource`, `InterCatChannel`, `InterCatOperation`); natively,
`sdk/native/include/intercat_sdk.h` (revision 1032, ADR-105)
Read by: a capture's `etw/eventsource/InterCat-Sdk` source, through the `sdk` profile (revision 1026, ADR-103)

An application references `InterCat.Sdk` to say what OS tracing cannot see of it: the channels it carries messages
through, each message it publishes and each reader's consumption of it, and its operations. What it says is an
assertion whose authenticity is the application's (ADR-102).

## 1. Transport

The provider is an EventSource in manifest format named `InterCat-Sdk`, its identity derived from the name. Every event
is Informational, raised at the schema's major version, and names first the process that raised it: a record's owner is
its payload's, never its header's. An event's text, where it has one, is its last field, so a capture reads every other
field at a fixed offset, a bounded read (R9). While no session enables the provider, a call costs an enabled check and
raises nothing. Before any other event, the SDK tells each session that enables it which schema it raises - a session
already listening as a process first uses the SDK, as a capture running as an application starts, as one enabling it
later (revision 1033):

| Event | Id | Fields |
|---|---|---|
| `SdkStarted` | 1 | `processId` (int32), `schemaMajor` (int32), `schemaMinor` (int32), `ticksPerSecond` (int64, the frequency of the process's clock), `library` (the SDK's version) |

A C or C++ application raises the same events through the SDK's native library, `sdk/native/include/intercat_sdk.h`
(revision 1032, ADR-105): one header raising them through ETW's provider API under the same provider, each event's
descriptor and fields the manifest's, held to it by a test. It holds a channel and an operation as `InterCat.Sdk` does,
refuses what that throws for - returning `ERROR_INVALID_PARAMETER` and raising nothing - and announces the schema alike,
its library `native-1.0` and its clock `QueryPerformanceCounter`'s. FX-SDK-002 measures it through the product. Since
minor 1 (revision 1036) it says batches too, `intercat_channel_published_batch` and `intercat_channel_consumed_batch`,
its library `native-1.1`.

## 2. Channels and messages

| Event | Id | Opcode | Fields |
|---|---|---|---|
| `ChannelOpened` | 2 | Start | `processId`, `channel` (GUID), `epoch` (int64, from 1), `kind` (0 other, 1 pipe, 2 socket, 3 shared memory), `name` |
| `ChannelClosed` | 3 | Stop | `processId`, `channel`, `epoch` |
| `MessagePublished` | 4 | Send | `processId`, `channel`, `epoch`, `sequence` (int64), `logicalLength`, `copiedLength` (-1 where unknown), `sourceTicks` |
| `MessageConsumed` | 5 | Receive | `processId`, `channel`, `epoch`, `sequence`, `consumer` (the reader's identity), `logicalLength`, `sourceTicks` |
| `MessagesPublished` | 8 | Send | `processId`, `channel`, `epoch`, `firstSequence`, `count` (1 to 65,536), `logicalLength` and `copiedLength` (the messages' together, -1 where unknown), `sourceTicks` |
| `MessagesConsumed` | 9 | Receive | `processId`, `channel`, `epoch`, `firstSequence`, `count`, `consumer`, `logicalLength` (together), `sourceTicks` |

A channel is the identity every process using it names alike. A message is its channel's epoch and sequence: published
once, when committed where readers can take it, and consumed once by each reader, so a message three readers consume
is one publication and three consumptions, never three writes. A sequence is unique within its epoch: one that wraps,
or a channel made again, opens a new epoch. Its logical length is the application's; the bytes copied are beside it
where the publisher knows them. A publication with no consumption stays open - not consumed as far as anything said -
and a channel opened before a session enabled the provider is known from its messages alone. Boundaries are logical:
a ring buffer's publish and consume, never each load or store of its memory. A batch (minor 1, revision 1036, ADR-106)
says a run of up to 65,536 consecutive messages in one event - `PublishedBatch` and `ConsumedBatch` - each still its own
sequence, published once and consumed once a reader, their lengths and copies said together and never one by one: one
call for what as many single calls would say, for an application faster than a capture keeps single events. Over a byte stream - a pipe or a socket - a
message is the application's frame: a write may carry several and a message may span reads, and the bytes its frame
copied into the stream are beside its logical length (revision 1033, FX-SDK-003 and FX-SDK-004).

## 3. Operations

| Event | Id | Opcode | Fields |
|---|---|---|---|
| `OperationStarted` | 6 | Start | `processId`, `operation` (GUID), `sourceTicks`, `name` |
| `OperationCompleted` | 7 | Stop | `processId`, `operation`, `status` (int64: 0 success, any other a failure in the application's own code), `sourceTicks` |

An operation's parent, where it has one, is its start's related activity, which ETW carries in the record's header; a
start under no parent has none. An operation completes once; a second completion says nothing. No start or stop of the
SDK's begins or ends one of the runtime's tracked activities, so the activity an event is raised under is its thread's
own.

A parent may be another process's operation, its identity carried by the application itself - as a stream's first
frame carries the client's, under which the server starts its own (FX-SDK-003, FX-SDK-004): an assertion of both
applications, whose authenticity is theirs.

## 4. Clock

`sourceTicks` is the raising process's own monotonic reading (`Stopwatch`) at a message's or an operation's transition,
in the frequency the process's `SdkStarted` states - so an operation raised on no channel is read as a message is: the
application's time of a commit, a consumption, a start or a completion, beside ETW's reading of when the event
was raised, which may be later where the application batches.

## 5. Versions

An event's version is the schema's major. A minor adds fields at an event's end only - after its text, where it has
one - which a reader of an earlier minor keeps unread; a major is a change an earlier reader could misread, raised at a
new event version, which such a reader admits as unknown rather than misreads (plan §24 `sdkSchema`). A minor may add
events as well, as minor 1 adds the batches: a capture built before them holds no descriptor of them, and counts their
records undecodable, never reads them as another event.

A capture holds this to its own schema, the SDK's it was built with (revision 1031): a later minor's record is read at the
offsets the capture knows, each known field where it was and its added field past them unread, never what the record
carries further; a later major's record names a version no descriptor of the capture's plan has, and is counted
undecodable, never read by another version's layout.

## 6. Capture

A capture admits each event as an application record, `ApplicationSdk` in the Application layer, owned by the process its
payload names (ADR-103):

| Event | Kind | Identifier | Name | Bytes | Source fields |
|---|---|---|---|---|---|
| `SdkStarted` | Inventory | - | `library` | - | `SdkSchemaMinor`, `SourceClockFrequency` |
| `ChannelOpened` | Open | `channel` | `name` | - | `ChannelEpoch`, `ChannelKind` |
| `ChannelClosed` | Close | `channel` | - | - | `ChannelEpoch` |
| `MessagePublished` | Send, outbound | `channel` | - | `logicalLength`, application payload, send side | `ChannelEpoch`, `MessageSequence`, `CopiedLength`, `SourceTicks` |
| `MessageConsumed` | Receive, inbound | `channel` | - | `logicalLength`, application payload, receive side | `ChannelEpoch`, `MessageSequence`, `ConsumerNumber`, `SourceTicks` |
| `OperationStarted` | RequestStart | `operation` | `name` | - | `SourceTicks` |
| `OperationCompleted` | RequestEnd | `operation` | - | - | `SourceTicks`; `status` is the record's status |
| `MessagesPublished` | Send, outbound | `channel` | - | `logicalLength` together, application payload, send side | `ChannelEpoch`, `MessageSequence` (its first), `MessageCount`, `CopiedLength`, `SourceTicks` |
| `MessagesConsumed` | Receive, inbound | `channel` | - | `logicalLength` together, application payload, receive side | `ChannelEpoch`, `MessageSequence` (its first), `MessageCount`, `ConsumerNumber`, `SourceTicks` |

An operation's parent is the record's related activity. A copied length is kept as the publisher said it, -1 included.

## 7. Messages

What a capture admits is read as logical messages (revision 1028, ADR-104, `contracts/cooperative-messages-v1.md`): each its
channel's epoch and sequence, its publication and each reader's consumption, said consumed, open, its publication unseen,
or conflicting; `icat messages` lists them.

## 8. Cost

Measured on one machine by `tools/InterCat.SdkProbe --overhead` (revision 1030, `bench/results/sdk-overhead-20261107T144235Z/overhead.json`), each burst one thread
publishing and consuming as fast as the SDK takes the calls, five rounds:

| While | A call costs (median) | Calls a second, one thread |
|---|---|---|
| No session listens | 48.5 ns - its argument checks and the enabled check | about 20,604,219 |
| `icat record --profile sdk` records it | 960.6 ns - an ETW event written | about 1,041,052 |

A capture keeps what it can take. Paced, it kept every call up to 100,000 calls a second - 50,000
messages published and consumed - for four seconds; at 200,000 it kept
498,107 of 800,000, and unpaced 73 to 82%. Past what it
keeps, the capture says it lost records and is never read as whole: a count it states is never estimated from what it
kept. An application raising more than that from one capture's view says its logical boundaries - a message, never each
byte - or is measured in a narrower window; batching is not offered at this version.

## 9. Not defined at this version

- An operation tree by parent, and the lag between a publication and its consumptions.
- Content carried by an event, bounded and opt-in.
