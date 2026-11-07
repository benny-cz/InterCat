# Cooperative SDK contract, version 1

Status: M7, revision 1025 (ADR-102)
Owner: `InterCat.Sdk` (`InterCatSdkEventSource`, `InterCatChannel`, `InterCatOperation`)
Read by: a capture's `etw/eventsource/InterCat-Sdk` source, through the `sdk` profile (revision 1026, ADR-103)

An application references `InterCat.Sdk` to say what OS tracing cannot see of it: the channels it carries messages
through, each message it publishes and each reader's consumption of it, and its operations. What it says is an
assertion whose authenticity is the application's (ADR-102).

## 1. Transport

The provider is an EventSource in manifest format named `InterCat-Sdk`, its identity derived from the name. Every event
is Informational, raised at the schema's major version, and names first the process that raised it: a record's owner is
its payload's, never its header's. An event's text, where it has one, is its last field, so a capture reads every other
field at a fixed offset, a bounded read (R9). While no session enables the provider, a call costs an enabled check and
raises nothing. Before any other event, the SDK tells each session that enables it which schema it raises:

| Event | Id | Fields |
|---|---|---|
| `SdkStarted` | 1 | `processId` (int32), `schemaMajor` (int32), `schemaMinor` (int32), `ticksPerSecond` (int64, the frequency of the process's clock), `library` (the SDK's version) |

## 2. Channels and messages

| Event | Id | Opcode | Fields |
|---|---|---|---|
| `ChannelOpened` | 2 | Start | `processId`, `channel` (GUID), `epoch` (int64, from 1), `kind` (0 other, 1 pipe, 2 socket, 3 shared memory), `name` |
| `ChannelClosed` | 3 | Stop | `processId`, `channel`, `epoch` |
| `MessagePublished` | 4 | Send | `processId`, `channel`, `epoch`, `sequence` (int64), `logicalLength`, `copiedLength` (-1 where unknown), `sourceTicks` |
| `MessageConsumed` | 5 | Receive | `processId`, `channel`, `epoch`, `sequence`, `consumer` (the reader's identity), `logicalLength`, `sourceTicks` |

A channel is the identity every process using it names alike. A message is its channel's epoch and sequence: published
once, when committed where readers can take it, and consumed once by each reader, so a message three readers consume
is one publication and three consumptions, never three writes. A sequence is unique within its epoch: one that wraps,
or a channel made again, opens a new epoch. Its logical length is the application's; the bytes copied are beside it
where the publisher knows them. A publication with no consumption stays open - not consumed as far as anything said -
and a channel opened before a session enabled the provider is known from its messages alone. Boundaries are logical:
a ring buffer's publish and consume, never each load or store of its memory.

## 3. Operations

| Event | Id | Opcode | Fields |
|---|---|---|---|
| `OperationStarted` | 6 | Start | `processId`, `operation` (GUID), `sourceTicks`, `name` |
| `OperationCompleted` | 7 | Stop | `processId`, `operation`, `status` (int64: 0 success, any other a failure in the application's own code), `sourceTicks` |

An operation's parent, where it has one, is its start's related activity, which ETW carries in the record's header; a
start under no parent has none. An operation completes once; a second completion says nothing. No start or stop of the
SDK's begins or ends one of the runtime's tracked activities, so the activity an event is raised under is its thread's
own.

## 4. Clock

`sourceTicks` is the raising process's own monotonic reading (`Stopwatch`) at a message's or an operation's transition,
in the frequency the process's `SdkStarted` states - so an operation raised on no channel is read as a message is: the
application's time of a commit, a consumption, a start or a completion, beside ETW's reading of when the event
was raised, which may be later where the application batches.

## 5. Versions

An event's version is the schema's major. A minor adds fields at an event's end only - after its text, where it has
one - which a reader of an earlier minor keeps unread; a major is a change an earlier reader could misread, raised at a
new event version, which such a reader admits as unknown rather than misreads (plan §24 `sdkSchema`).

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

An operation's parent is the record's related activity. A copied length is kept as the publisher said it, -1 included.

## 7. Not defined at this version

- A derivation of logical messages from what a capture admits: a publication paired with its consumptions.
- Content carried by an event, bounded and opt-in; batching; a native C/C++ library.
