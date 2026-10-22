# ADR-057: A view reads one published generation at a time, asks only for its latest request, and keeps what it shows until the newer answer is read

- Status: accepted; revision 729. It records how snapshots are published (ADR-021, ADR-022, ADR-027) and how the window
  schedules its queries, as the code has done since the timeline's zoomed detail and the live follow were built, and
  corrects §19.3's suggested gesture cadence to the one the window uses
- Date: 2026-10-22
- Relates to: §6.2, §6.8, §10.5, §12.1, §19.3, §20.1, R7, R18, I16, P25, ADR-015, ADR-021, ADR-022, ADR-027, ADR-053

## Context

A live capture publishes a new generation every couple of seconds while the person pans, zooms, brushes and reads
lists. Each of those asks a question of the evidence, some of them costly. If every pointer move launched a query, the
machine would spend its time on answers nobody waits for; if an answer could land on a view that had moved on, the
window would show a count for one interval under another's label; and if a new publication replaced what a pane shows
before its own answer was read, numbers would flicker or mix generations. §19.3 asked for a coalescing scheduler, a
latest-request slot, cancellation of superseded work and cadences to benchmark. No record said what the window does.

## Decision

1. **A snapshot is a published generation.** A live recording publishes evidence a chunk at a time, and the follower
   derives each into a session generation (ADR-021, ADR-022, ADR-027): a manifest naming immutable segments and
   indexes, read under a lease that keeps its files until it lets go. Every answer names the generation and the query
   it came from (I16, ADR-015), and a cursor continues only within the same query's meaning.
2. **Each kind of question has one latest-request slot.** The timeline's detail, the highlight, a lane's calls, the
   interval table, an evidence page and the others each hold the cancellation of their latest request. A new request
   cancels the one before it, and an answer is applied only when its request is still the latest; one that arrives late
   is dropped, never drawn under a newer label. A graph layout likewise applies only to the graph identity it was
   computed for (R7, ADR-053).
3. **A gesture redraws, and a rest asks.** While the viewport moves, the timeline redraws what it holds at the display's
   rate, re-projected to the new transform. It asks for the viewport's own count once the viewport has rested 150 ms
   (§6.2); a step or a "go to" asks at once. No query starts on a pointer move. The coarse cells stay on screen until the
   answer arrives (P25).
4. **A newer generation does not blank a pane.** When a publication arrives, each pane keeps what it shows - its rows,
   its bytes, the row chosen in it - until its own answer for the new generation is read, and then says which
   generation it shows. A paused or held view keeps its generation until the person lets it go.
5. **Work runs off the UI thread.** Queries, derivations and layouts run on background threads; their answers return to
   the UI thread to be applied. A count that reads rows counts segments side by side and sums after; a layout runs on
   one thread (ADR-053).
6. **The cadences are the publication's.** A live view is as fresh as its follow's publications, at most one every two
   seconds under the `Live` policy (ADR-022), and each is read once; the window does not re-query between them.

## Why

- **One slot per kind, not one scheduler.** Each pane's question is independent, and the newest of each is the only one
  worth answering; a slot per kind cancels exactly what a newer request supersedes, where one queue would cancel a list's
  page because the timeline moved.
- **A rest of 150 ms rather than a coalescing window of 30 to 50.** §19.3 suggested coalescing gesture requests over 30
  to 50 ms, to be benchmarked. A count at the viewport's own resolution during a pan is stale before it is drawn; the
  redrawn cached cells already follow the gesture, and asking once the viewport rests spends the count on the view the
  person stopped at.
- **Keep until read.** A pane that blanks while its new answer is read flickers every two seconds through a live
  capture; revisions 617 to 626 made each pane keep what it showed across a publication until its own answer is read,
  and say which generation it shows.

## Consequences

- A late or superseded answer is never drawn, so a slow query costs time but never a wrong figure.
- No query has priority over another: §19.3 asks that evidence pages and visible gestures precede background
  refinement and that ingestion keep capacity, but the window has no background refinement yet, and the follower's
  derivation and the window's queries share the thread pool. If a background refinement is added, it needs that
  priority.
- §19.3's suggested 4 Hz analytic snapshots and 1 Hz ranking updates are not reached by design: a live view changes
  with each publication, which comes at most every two seconds.

## Alternatives considered

- **A query per pointer move, coalesced over 30 to 50 ms.** Rejected above.
- **One global scheduler with priorities.** Not taken while every question is answered within its gate (§12) and
  nothing runs in the background but the follow; it becomes the way to keep a background refinement from delaying a
  gesture.
- **Replacing a pane's content as soon as a generation is published.** Rejected above.
