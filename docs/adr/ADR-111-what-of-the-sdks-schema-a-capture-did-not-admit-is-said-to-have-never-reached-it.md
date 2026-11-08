# ADR-111: What of the SDK's schema a capture did not admit is said to have never reached it

- Status: accepted for M7's later minor said to an earlier capture (revision 1052)
- Date: 2026-11-08
- Decision owners: InterCat maintainers
- Relates to: ADR-102, ADR-106, ADR-107, plan M7, `contracts/sdk-v1.md` §1 and §5, `contracts/normalizer-plan-v1.md`,
  `contracts/cooperative-messages-v1.md`, `contracts/cooperative-operations-v1.md`, R18, R21

## Context

M7's review lists, among what is not built, a capture saying that a process announces a later minor than it knows, whose
added events never reach it (`docs/reviews/M7-release-review.md`). Each process tells every session enabling the SDK which
schema it raises (`contracts/sdk-v1.md` §1), and a capture enables only the events it admits, so a later minor's added
events never reach it - measured on revision 1039 (`contracts/sdk-v1.md` §5). Until now nothing read the announcements, so
a listing of a session counted nothing of what never reached it, and said nothing of it either: an absence it could not
tell apart from silence. A capture records no minor of its own, but every generation retains the plan its capture
admitted under (`contracts/normalizer-plan-v1.md`), which names the SDK events it admitted.

## Decision

1. **The announcements are read** from the SDK's own provider's records: each process's schema major (its record's event
   version) and minor, its library and its clock's frequency, a process's equal announcements once.
2. **A minor later than this InterCat knows is said to have never reached the capture**: what it added no capture of this
   InterCat admits, and no reading of it counts. Its events are unknown here, so nothing is said of what they are.
3. **A known minor's events the capture's plan did not admit are named** as never having reached it - its batches, where
   a capture of minor 0 recorded a process of minor 1. A content event is set aside: a capture keeping no content leaves it
   out by choice, never for want of knowing it (ADR-107). Where a generation retains no plan, nothing is said of a known
   minor's events.
4. **Each is said where the SDK's records are listed**, beneath what the capture covered (R21): `icat messages` and its
   `--operations`, their documents with each announcement, and the window's Logical messages… and Operations…, in one
   set of words (R18). Another major's records the capture already counts undecodable.

## Consequences

- Revision 1052 reads the announcements (`CooperativeAnnouncements`), says what never reached the capture
  (`CooperativeSchemaText`), and carries both in the two readings, their commands, documents and windows; 19 mutations
  of the schema, the announcements, the words, the command and the windows are caught.
