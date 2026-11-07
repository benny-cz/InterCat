# ADR-108: Two captures are ordered by the operations one started under the other's

- Status: accepted for §8.2's shared-marker alignment; the orderings and the bound they set (revision 1044); the
  workspace's mode, its command, the window and a qualification follow
- Date: 2026-11-08
- Decision owners: InterCat maintainers
- Relates to: ADR-039, ADR-040, ADR-102, ADR-105, plan §8.2, M4, M7, `contracts/workspace-v24.md` §5 and §9,
  `contracts/sdk-v1.md` §3, R3, I9

## Context

Plan §8.2 offers three ways to align two captures' clocks: their recorded wall clocks, shared marker or activity evidence,
and a person's statement. The first and the last exist (ADR-039, ADR-040); the second waited on a marker both captures
record, which M7's cooperative SDK can now give. §8.2 bounds what such a marker may be: a send and its receipt order two
clocks only when each record is known to be taken before, or after, the transfer it records; a shared identity does not
make two records simultaneous; and an offset fitted from a guessed pairing cannot then confirm that pairing.

A message of the SDK's is no such marker: its publication is said after its commit, so a reader may consume it, and say
so, before the publication is said.

## Decision

1. **The marker is an operation started under an operation the other capture recorded starting.** The SDK makes an
   operation's identity in the very call that says its start, before returning it (`InterCatOperation.Start`,
   `intercat_operation_start`), so no process holds the identity before its start was said, and a start under it is said
   after its holder received it. Each such pair is a record of one capture known to be taken after a record of the other,
   by the SDK's own word and an explicit identity - never a measured delay or a guessed pairing.
2. **Each pair bounds the offset from one side.** At the member's instant m of a start under the reference's parent at r,
   the reference's clock reads past r, so the offset is at least r - m; at the reference's start under the member's parent,
   it is at most r - m. The tightest bound each way sets the offset, midway, and its half-width.
3. **Both ways are needed.** Pairs one way bound the offset from one side, which orders nothing, and are refused rather
   than given a default other side (R3). Pairs that leave no offset contradict one another - a clock stepped, drifted
   faster than stated, or a record was misread - and are refused too.
4. **A stated drift bound holds them together.** Each pair is true of its own instant; a person's bound on how fast the two
   clocks drift apart widens each by the drift over its distance from the anchor, midway between the tightest pairs, as a
   wall-clock alignment's drift is stated (ADR-040).
5. **A parent's earliest start is the one held.** No start under an operation precedes its first start, so where a
   capture recorded one start twice, the earliest is used; starts under an operation of their own capture order nothing
   between two.

## Consequences

- Revision 1044 builds the orderings from two captures' records (`SharedOperationMarkers`) and the bound they set
  (`MarkerBound`), held by tests of both ways, the drift, one way alone and contradiction; 19 mutations of the bound and
  the orderings are caught.
- The workspace's shared-marker mode, its command and the window's alignment by it follow, and a qualification of two
  captures of one host, whose same-boot alignment is exact, against which the bound is held.
