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
