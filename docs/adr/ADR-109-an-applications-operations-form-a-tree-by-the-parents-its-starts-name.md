# ADR-109: An application's operations form a tree by the parents its starts name

- Status: accepted for M7's operation tree; the derivation (revision 1048) and `icat messages --operations` (revision
  1049); the window follows
- Date: 2026-11-08
- Decision owners: InterCat maintainers
- Relates to: ADR-102, ADR-104, ADR-105, ADR-108, plan M7, `contracts/sdk-v1.md` §3 and §9,
  `contracts/cooperative-operations-v1.md`, R1, R3, R18, R20, R21

## Context

M7's review lists an operation tree by parent among what is not built (`docs/reviews/M7-release-review.md`). The SDK says
an operation's start - its identity, its name, the operation it was started under, which ETW carries as the record's
related activity - and its completion with a status (`contracts/sdk-v1.md` §3). The SDK makes the identity in the call
that says the start, so a parent may be another process's operation whose identity the application carried there
(FX-SDK-003, FX-SDK-004, ADR-108). Until now only the SDK probe's fixture evaluation and the shared-operation markers read
these records; no listing places an operation under its parent.

## Decision

1. **An operation is its identity**, started once and completed once in the process that holds it, as the schema says.
   Each start and completion of the SDK's provider is keyed by the identity it carries; a record whose identity or process
   the capture did not keep names no operation, and is counted apart rather than keyed by a value never observed (R3).
2. **What became of it is what its records say, never a guess.** `Completed`: one start and one completion, in one
   process. `Open`: started, its completion recorded nowhere - running when the capture ended, or its record lost.
   `StartUnseen`: completed with no start recorded - started before the capture, or its start's record lost. `Conflicting`:
   started or completed more than once, or in two processes, which breaks the schema's word, so none of its records is
   chosen over another; its first start still places it.
3. **It is placed under the parent its first start names.** A start under no parent - ETW's empty link - is a root, and so
   is an operation never recorded starting, whose parent is unknown rather than none. A parent this generation never
   recorded - another capture's, or one begun before - is said as such, the operations under it listed under it. Parents
   naming each other round, which no start can, since each identity is made before it is passed on, are reached by no
   root and listed apart.
4. **The tree is walked depth first**, each root in the order first recorded, then everything under it, each operation's
   children in the order first recorded; then each unrecorded parent's operations; then whatever no root reaches.
5. **A completed operation ran from its start's record to its completion's**, in session time - the capture's one clock
   for both records - the application's own clock readings kept beside them.

## Consequences

- Revision 1048 builds the derivation (`CooperativeOperationIndex`, rule `cooperative-operation-v1`), held by tests of a
  tree across two processes and of every state and place; 21 mutations of the derivation are caught.
- Revision 1049's `icat messages --operations` lists the tree (`contracts/cooperative-operations-v1.md`): each operation a
  line indented by its depth - its name, its process and what became of it - those under a parent the capture never
  recorded headed by it, the first 500 as text and every one in its document, and what the capture covered of the SDK
  said from the ledger (R21). 16 mutations of the words and the command are caught.
- The window shows it in the same words (R18); the lag between a publication and its consumptions stays a refinement of
  its own.
