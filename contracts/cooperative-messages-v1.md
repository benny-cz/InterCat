# Cooperative messages contract, version 1

Status: M7, revision 1028 (ADR-104)
Owner: `InterCat.Analysis` (`CooperativeMessageIndex`, rule `cooperative-message-v2` since revision 1035); `icat messages`
Reads: the rows and source fields a capture admits of the `InterCat-Sdk` provider (`contracts/sdk-v1.md` §6)

## 1. Keys

A record is the SDK's by its provider, `InterCat-Sdk`. A channel's epoch is its identity (the record's identifier) and
`ChannelEpoch`; a message is its channel's epoch and `MessageSequence`; a consumption is one reader's, `ConsumerNumber`.
A channel or message record lacking a value it is keyed by names nothing and is counted in `withoutMessage`.

A batch (revision 1035, ADR-106) says a run of consecutive messages: its first `MessageSequence` and its `MessageCount`,
up to 65,536, their lengths together. Each channel epoch's sequence space is swept once over every record's run, a message
said alone a run of one: each stretch the same records cover is one entry - a message, or a run its records say alike -
and a run is split only where another record begins or ends within it. A batch with no count, a count past the bound or a
run past the last sequence a long holds names nothing and is counted in `withoutMessage`.

## 2. States

| State | Meaning |
|---|---|
| `Consumed` | Published once, read by at least one reader |
| `Unconsumed` | Published once, read by no reader as far as anything recorded says: open |
| `PublicationUnseen` | Read, with no publication recorded: published before the capture, or its record lost |
| `Conflicting` | Published more than once, or read more than once by one reader; no record is chosen over another |

A message names its first recorded publication. A copied length of -1 is the publisher's unknown and is stated as none.
A run's messages share its state, and each is counted in it; a batch's lengths are counted once in `publishedBytes`
however its run splits, and its copy is not split among its messages.

A consumption's lag (revision 1051, ADR-110) is how long after its message's publication was said it was said, in session
time: negative where the reader said it first, which the schema allows - each record is said after its moment, so a lag
is never a message's latency. A message published more than once, or whose publication was not recorded, gives its
consumptions none. An epoch's lag is the least, the median and the most over its consumption records - a batch's once,
however its run splits - with how many were said before their publication.

## 3. `icat messages --json`

One object: `contract` (`cooperative-messages-v1`), `path`, `generation`, `messageRule`, `bindingRule`, `evidencePolicy`,
`coverage` (the application SDK's, from the ledger), `withoutMessage`, `channels` and `caveats`. Each channel holds
`channel`, `epoch`, `name` and `kind` (its first opening's, null where none was recorded), `openings`, `closings`,
`messages`, `consumed`, `unconsumed`, `publicationUnseen`, `conflicting`, `readers`, `consumptions`, `publishedBytes`, and
with `--channel` or `--messages` a `list` of its first messages in sequence order: `sequence`, `state`, `publications`,
`publication`, `copiedLength` and `consumptions` (`consumer` and `record`), and since revision 1035 `count` (1, or a run's
length). Since revision 1051 each consumption holds its `lagNanoseconds` and each channel its `lag` - `leastNanoseconds`,
`medianNanoseconds`, `mostNanoseconds`, `consumptions` and `saidBefore` - each null where none is said. Since revision
1052 it holds `announcements` - each process's `processId`, `process`, `unattributed`, `major`, `minor`, `library` and
`ticksPerSecond` - and `unreached`, a sentence for each schema whose events never reached the capture (ADR-111), which
the listing says beneath what the capture covered. A record holds `processId`, `process` (the instance under the evidence policy, or null with `unattributed`
saying why), `length` (a batch's together), `batch` (its count, null for a message said alone), `sourceTicks`, `seconds` (its
session time) and its raw locator, `stream`, `sourceEpoch` and `ordinal`.

## 4. Measured

FX-SDK-001 (revision 1029): a shared-memory ring one producer writes and three readers read, recorded through the product
three times, nothing lost - every publication and every reader's consumption one of the derivation's, by length and
process, the message no reader read open, and the channel made again a second epoch. FX-SDK-002 (revision 1032) measures
a native application's, said through the SDK's C library, its operations included; FX-SDK-003 and FX-SDK-004 (revision
1033) a client's framed messages to a server over a named pipe and a loopback TCP connection, every message one message,
never a write or a read. FX-SDK-005 and FX-SDK-006 (revision 1037) say FX-SDK-001's ring and FX-SDK-002's native messages in
batches: every message one message of the run its batch said, each batch's lengths together its messages' summed, and
each run one row.

## 5. The window

InterCat's window lists them too (revision 1034): Logical messages… opens each channel epoch counted by what became of its
messages, and the chosen epoch's first 500 messages in sequence order, each with what became of it, its length, its
publisher and its readers in their processes; choosing an epoch selects together the processes that published or read its
messages. The window and `icat messages` read a generation alike and say it in one set of words (R18): a process named
under the window's evidence policy, or its PID and why no instance holds the record.
