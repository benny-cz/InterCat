# Cooperative messages contract, version 1

Status: M7, revision 1028 (ADR-104)
Owner: `InterCat.Analysis` (`CooperativeMessageIndex`, rule `cooperative-message-v1`); `icat messages`
Reads: the rows and source fields a capture admits of the `InterCat-Sdk` provider (`contracts/sdk-v1.md` §6)

## 1. Keys

A record is the SDK's by its provider, `InterCat-Sdk`. A channel's epoch is its identity (the record's identifier) and
`ChannelEpoch`; a message is its channel's epoch and `MessageSequence`; a consumption is one reader's, `ConsumerNumber`.
A channel or message record lacking a value it is keyed by names nothing and is counted in `withoutMessage`.

## 2. States

| State | Meaning |
|---|---|
| `Consumed` | Published once, read by at least one reader |
| `Unconsumed` | Published once, read by no reader as far as anything recorded says: open |
| `PublicationUnseen` | Read, with no publication recorded: published before the capture, or its record lost |
| `Conflicting` | Published more than once, or read more than once by one reader; no record is chosen over another |

A message names its first recorded publication. A copied length of -1 is the publisher's unknown and is stated as none.

## 3. `icat messages --json`

One object: `contract` (`cooperative-messages-v1`), `path`, `generation`, `messageRule`, `bindingRule`, `evidencePolicy`,
`coverage` (the application SDK's, from the ledger), `withoutMessage`, `channels` and `caveats`. Each channel holds
`channel`, `epoch`, `name` and `kind` (its first opening's, null where none was recorded), `openings`, `closings`,
`messages`, `consumed`, `unconsumed`, `publicationUnseen`, `conflicting`, `readers`, `consumptions`, `publishedBytes`, and
with `--channel` or `--messages` a `list` of its first messages in sequence order: `sequence`, `state`, `publications`,
`publication`, `copiedLength` and `consumptions` (`consumer` and `record`). A record holds `processId`, `process` (the
instance under the evidence policy, or null with `unattributed` saying why), `length`, `sourceTicks`, `seconds` (its
session time) and its raw locator, `stream`, `sourceEpoch` and `ordinal`.

## 4. Measured

FX-SDK-001 (revision 1029): a shared-memory ring one producer writes and three readers read, recorded through the product
three times, nothing lost - every publication and every reader's consumption one of the derivation's, by length and
process, the message no reader read open, and the channel made again a second epoch. FX-SDK-002 (revision 1032) measures
a native application's, said through the SDK's C library, its operations included; FX-SDK-003 and FX-SDK-004 (revision
1033) a client's framed messages to a server over a named pipe and a loopback TCP connection, every message one message,
never a write or a read.

## 5. The window

InterCat's window lists them too (revision 1034): Logical messages… opens each channel epoch counted by what became of its
messages, and the chosen epoch's first 500 messages in sequence order, each with what became of it, its length, its
publisher and its readers in their processes; choosing an epoch selects together the processes that published or read its
messages. The window and `icat messages` read a generation alike and say it in one set of words (R18): a process named
under the window's evidence policy, or its PID and why no instance holds the record.
