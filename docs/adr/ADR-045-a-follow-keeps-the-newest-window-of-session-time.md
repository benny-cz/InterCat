# ADR-045: A follow keeps the newest window of session time

- Status: accepted; revision 439
- Date: 2026-10-08
- Relates to: §12.1 S5, S6, §20.2, IC-016a, ADR-024, ADR-027, ADR-043, ADR-044, `contracts/store-v1.md` §8

## Context

§20.2 asks for explicit rolling retention, and S5 for the point at which it begins evicting to be visible. An interval
release (ADR-043) is the mechanism, and since ADR-044 a follow goes on across releases of the session it derives into,
so the follower's own writer can publish one between two mirrors. What remained was a policy: what to keep, when to
release, and who says so.

A release reads every row of the session several times and rewrites the publications holding a row it gives up
(ADR-043). It works in whole chunks, so the boundary it reaches can lie well before the one asked for, and the unit
holding the asked-for boundary stays.

## Decision

1. **A window of session time, not of bytes or records.** A policy keeps the newest stretch of session time, one
   second to a day long: every record read in that stretch is retained. Session time is what a person reads the
   timeline in, and what a release's boundary is stated in.
2. **A quarter past the window before anything goes.** The session holds its window and a quarter more past its oldest
   record, or past its latest release's boundary, before the records read before its newest window are released. Each
   release then gives up about a quarter of the window, a quarter apart in session time, rather than a chunk at every
   publication.
3. **A quarter more after each attempt.** After a release, or one refused because the oldest chunk reaches into the
   window or everything before it is kept as evidence, nothing is asked again until the session has grown by another
   quarter. Planning reads every row, and the unit that refused does not change until later records arrive.
4. **The follow runs it, between mirrors, by its own writer.** `icat follow --keep-last <seconds>` and `icat capture
   --keep-last <seconds>` run the policy after each pass that mirrored chunks. A release published by any other writer
   would fail the follow's next mirror (ADR-024).
5. **It says what it does.** Each release's reason is "rolling retention keeps the last N seconds/minutes/hours", which
   `icat session` and the window state. On a release the command prints the window's size-line sentence: from when the
   session keeps every record, and what went before. Its document states the window, the releases made and the latest
   boundary.

## Consequences

- The session a follow derives stays near its window and a quarter, plus what later records rest on, however long the
  capture records.
- The broker's evidence still keeps every record until its quota or duration stops the capture, so a capture cannot
  outlast its journal quota yet. Releasing the evidence needs the broker to know what its follower has mirrored
  (ADR-027), and the follower to accept evidence that released chunks it holds.
- The window offers no rolling capture yet. Its timeline would show the released time as a long partial gap before the
  window, which it should instead start at.
- A rolling session's coverage before its newest boundary is a partial gap, as every released interval's is
  (ADR-043 decision 7).

## Alternatives considered

- **A byte budget.** Rejected for now: a person cannot read bytes on a timeline, and a release's cost and boundary are
  set by chunks of session time.
- **Release at every publication.** Rejected: each release reads every row and rewrites a publication, so the cost
  would grow with the window at every chunk.
- **Run the policy in the broker.** Rejected: the broker holds no rows to release (ADR-027).
