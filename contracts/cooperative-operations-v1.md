# Cooperative operations contract, version 1

Status: M7, revision 1049 (ADR-109)
Owner: `InterCat.Analysis` (`CooperativeOperationIndex`, rule `cooperative-operation-v1`, revision 1048); `icat messages
--operations`
Reads: the rows and source fields a capture admits of the `InterCat-Sdk` provider (`contracts/sdk-v1.md` §3)

## 1. Keys

A record is the SDK's by its provider, `InterCat-Sdk`. An operation is its identity, the record's identifier, which the
SDK makes in the call that says its start (`contracts/sdk-v1.md` §3): `OperationStarted` says its start, its name and -
as the record's related activity, ETW's own link - the operation it was started under; `OperationCompleted` says its
completion and its status. A record lacking its identity, or whose process the capture did not keep, names no operation
and is counted in `withoutOperation`. Each record's process is bound as a message's is (`contracts/cooperative-messages-v1.md`).

## 2. States

| State | Meaning |
|---|---|
| `Completed` | Started once and completed once, both in one process |
| `Open` | Started, its completion recorded nowhere: running when the capture ended, or its record lost |
| `StartUnseen` | Completed, with no start recorded: started before the capture, or its start's record lost |
| `Conflicting` | Started or completed more than once, or in two processes; no record is chosen over another |

An operation names its first recorded start - its name and its parent - and its first recorded completion's status: 0 its
success, any other value a failure in the application's own code. A completed operation ran from its start's record to
its completion's, in session time; the application's own clock readings are kept beside them.

## 3. The tree

Each operation is placed under the parent its first start names. One started under no parent - ETW's empty link - is a
root, and so is one never recorded starting, whose parent is unknown rather than none. A parent the generation never
recorded - another capture's, or one begun before the capture - is said as such, the operations started under it listed
under it. Parents naming each other round, which no start can, are reached by no root and listed apart.

| Place | Meaning |
|---|---|
| `Root` | Started under no parent, or never recorded starting |
| `Child` | Under an operation the generation recorded |
| `UnderUnrecorded` | Started under an operation the generation never recorded |
| `Circular` | Under parents naming each other round, or under one of them |

The tree is walked depth first: each root in the order first recorded, then everything under it, each operation's
children in the order first recorded; then each unrecorded parent's operations, in the order the parent was first named;
then whatever no root reaches.

## 4. `icat messages --operations --json`

One object: `contract` (`cooperative-operations-v1`), `path`, `generation`, `operationRule`, `bindingRule`,
`evidencePolicy`, `coverage` (the application SDK's, from the ledger), `withoutOperation`, `counts` (`operations`,
`completed`, `failed` - completed with a status other than 0 - `open`, `startUnseen` and `conflicting`),
`unrecordedParents`, `operations` and `caveats`. `operations` holds every operation in the tree's order, each with
`operation`, `parent`, `depth`, `place`, `name`, `state`, `starts`, `completions`, `status`, `durationNanoseconds`, and its
`start` and `completion` records - each `processId`, `process` (the instance under the evidence policy, or null with
`unattributed` saying why), `sourceTicks`, `seconds` (its session time) and its raw locator, `stream`, `sourceEpoch` and
`ordinal` - null where none was recorded.

Without `--json`, `icat messages --operations` writes the tree as lines, each indented by its depth: the operation's
name, its process and what became of it, in the words the window says them in (R18); the first 500, then how many more
the JSON holds. What the capture covered of the SDK is said from its coverage ledger, never from a count of none (R21).

## 5. Not defined at this version

- The tree in InterCat's window.
- One operation's subtree asked for by its identity.
- The lag between a publication and its consumptions.
