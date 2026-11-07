# ADR-104: A cooperative message is its channel's epoch and sequence, as the application said it

- Status: accepted for M7's cooperative instrumentation; the derivation and `icat messages` (revision 1028)
- Date: 2026-11-07
- Decision owners: InterCat maintainers
- Relates to: ADR-030, ADR-102, ADR-103, plan §15 M7, §20.4, `contracts/sdk-v1.md`, `contracts/cooperative-messages-v1.md`, R1, R3, R18, R20

## Context

A capture admits what an instrumented application raises (ADR-103), each record a row of its own. M7 asks for logical
messages: a message published once and consumed once by each reader, a missing consumption left open, a wrapped sequence
a new epoch, and a channel opened before the capture known from its messages.

## Decision

1. **A message is keyed by its channel's identity, epoch and sequence, and a consumption by its reader** - the
   application's own numbering, carried by each record as its identifier and its source fields. A record that lacks one
   of them names no message and is counted apart, never keyed by a value not observed (R3).
2. **A record is the SDK's by its provider.** InterCat's content fixture is recorded under the same mechanism and numbers
   its events from 1 too, so the domain names the SDK's provider and events as its type declares them, held to it by a
   test, and a derivation reads the SDK's records without the library an application ships.
3. **What became of each message is said, never guessed**: consumed (published once, read by at least one reader), open
   (published once, read by none as far as anything recorded says), its publication unseen (read, with no publication
   recorded: before the capture, or lost), or conflicting (published twice, or read twice by one reader), none of its
   records chosen over another; the first recorded publication is the one it names.
4. **A channel's epoch is named by its first recorded opening**, its name and kind; every opening and closing is counted,
   and a channel opened with no message recorded is listed as one of none.
5. **Each record is bound to the process its payload names** under the process binding rule; its length and its clock
   reading are the application's word.
6. **`icat messages` lists each channel's epoch and, with `--channel`, its messages**, each one's publisher, length and
   readers, and what the capture covered of the SDK from the coverage ledger; `--json` answers `cooperative-messages-v1`.

## Consequences

- A capture of instrumented applications reads as messages: who published each, who read it, and what is still open.
- The window does not show them yet; no lag or latency between a publication and its consumptions is stated; operations
  are rows of their own, with no tree by parent; no sample application or fixture measures them yet.
- 24 mutations of the path are caught.

## Addendum: measured through the product (revision 1029)

FX-SDK-001 measures the derivation on a real capture: a shared-memory ring one producer writes and three readers read,
each process saying what it publishes and consumes through `InterCat.Sdk`, recorded by `icat record --profile sdk` and
read as `icat messages` reads it (`tools/InterCat.SdkProbe`). In three runs, nothing lost, every one of the 161 messages
the producer published at the channel's two epochs was one message and every one of the 480 consumptions one reader's,
each bound to the process that logged it with the length it logged; the one message no reader read stayed open, and the
channel made again was a second epoch whose sequence started at 1 again. The derivation's path is unchanged; the fixture's
evidence is held by its tests.
