# ADR-087: A window message gate follows a message at its pointer from its start to its end

- Status: accepted for M6's UI messaging work package; since revision 959 Win32k's six message events admit through it,
  a record delivered out of order omitted as such (its addendum); since revision 960 a window message's records bind to
  the process that raised them, as `process-binding-v7` (its second addendum); since revision 962 the opt-in window
  messages profile requests the message pump, stating its cost (its third addendum)
- Date: 2026-11-05
- Decision owners: InterCat maintainers
- Relates to: ADR-073, ADR-083, ADR-086, plan M6, §4.2, R22, P7, P12, FX-WMSG-001

## Context

ADR-086 made Win32k's message pump window messages' lead and inventoried it (its second addendum). It records a sent
message's start and stop at the sender and its retrieval's start and stop at the receiver, and a posted message's queueing
and its retrieval, each end joined to the others only by the queue message's kernel pointer, which the kernel reuses once
the message is retrieved, so a capture must number each message from its queueing or start to its retrieval's end before
joining by it (its fourth decision). A capture admits records one at a time as they arrive (ADR-073), and every window on
the machine pumps messages, thousands of records a second with no messaging workload of the capture's own. A session was
measured delivering its records in the order they were raised, every message's ends included, though most were raised on
two processors and the runs reused hundreds of pointers (ADR-086's third addendum).

## Decision

1. **A message is followed at its pointer from its start to its end.** `WindowMessageGate` reads a message's six ends. A
   send's start begins a sent message at its pointer, and a post's queueing a posted one, whatever the pointer held before
   ended, seen or not. The receiver's retrieval's start and stop join the sent message at their pointer, and the sender's
   stop ends it; a post's retrieval ends the posted message. Each message is numbered in the capture, never named by its
   pointer (R22).
2. **A message begun before the capture is its own.** An end at a pointer no followed message of its kind holds is of a
   message begun before the capture: it begins one there, numbered apart, which its later ends join until a start begins
   another, as a message begins only with a start and its ends arrive in the order raised. A message's last end met alone,
   the sender's stop or a post's retrieval, is numbered alone. An end of the other kind is never the message at its pointer:
   that message ended unseen.
3. **The order it numbers by is held to each capture.** A record raised before one delivered ahead of it is numbered alone
   and counted, never joined to the message it might have been an end of, so a capture whose delivery is ever out of order
   says so instead of numbering by it.
4. **Bounded.** The gate follows at most 65,536 messages; beyond them it gives up the oldest it still follows, counted, whose
   later ends are then a message's begun before. A message already ended holds no place. It holds back no record, and it
   reads on the capture's callback, so it allocates nothing per record: both its tables are sized once (P12).
5. **What this does not decide.** No profile compiles the gate yet. Wiring it needs Win32k's admitted events and fields - a
   send's start naming its receiver's process, never a thread, a window or a pointer - the records bound to the process that
   raised them, normalized as window message observations by the gate's numbers, and an opt-in profile stating the cost
   (ADR-086's first addendum).

## Consequences

- Tests hold the gate on synthetic records - a send's four ends and the pointer's next message, a post's two and a post
  never retrieved ended by the next start of either kind, a message begun before the capture followed from its first end,
  an end of the other kind, a record delivered out of order, the bound and the beginnings it never gives up, and its
  allocations - and on FX-WMSG-001's ledger, a run's 194 ends as they were delivered, read through it: 32 sends and 33 posts,
  each send's four ends one message and each post's two another, numbered apart though the run reused 11 pointers, none
  given up and none out of order. Eleven mutations of the gate are caught.

## Addendum: what Win32k admits through the gate (revision 959)

The fifth decision left Win32k's admitted events and fields to wire. The message pump now admits its six message events
at version 0, each compiled from the layout this build registers for it, all of them window message records of the
application layer:

- a post's queueing (59), a send raised in the poster, and its retrieval (61), a receive raised in the receiver;
- a send's start (60), a request's start raised in the sender and naming the process that receives it, and its stop
  (70), the request's end;
- the receiver's retrieval of a sent message (62), a request's start inbound, and its end (83).

Each keeps the queue message's pointer only for the gate to read: `WindowMessageRecordGate` writes the message's number
over it, a source field of its own, `WindowMessageNumber`, so no admitted record keeps a pointer (R22). A send's start
keeps the process it names as `PeerProcessId`, never its owner; no window, thread, message number or parameter is
admitted. Window messages are a fact family of their own, `window-message`, before any capture admitted one.

A record the gate numbers alone as delivered out of the order raised (the third decision) is omitted instead, as
`OutOfOrder`, a gate omission of its own: the coverage ledger counts it by that reason, a capture's health reports it,
`icat record` says it, and the mechanism's coverage is a partial gap. A schema without an end's pointer refuses the
source, as the gate decides by it; one without a send's receiving process, which no rule decides by, admits it, the
receiver unknown (R3). No binding rule binds a window message record to the process that raised it yet, and no profile
requests the source.

Tests hold the layout this machine registers, the compiled slots, the gate's admissions and its omission on compiled
records, an admitted record's row, the refusals, the gate on the callback's path, and the omission's count, coverage and
words. Thirteen mutations are caught.

## Addendum: a window message's records bind to the process that raised them (revision 960)

The fifth decision asked for the records bound to the process that raised them. A window message record's payload names
no owner: a send's start names the process that receives it, which is the message's other end, never the record's
own. FX-WMSG-001 had every message of three runs raised at both ends in the process that sent, posted or retrieved it:
a send's start and stop in the sender, its retrieval's start and stop in the receiver, a post's queueing in the poster
and its retrieval in the receiver (ADR-086). So ADR-030's header binding now reaches the `WindowMessage` mechanism:
each end belongs to the process that raised it.

What binds changes, so the rule is `process-binding-v7`, as the entities contract's section 7 requires. A checkpoint
derived under version 2 to 6 is read as version 7's wherever every record named its owner, and one counting a record
without an owner is derived again, its refusal naming window messages. The entities, checkpoint, operations, operation
index, metrics, relations, query identity, HTTP exchange and COM call contracts name the new rule; the grouped answer's
caveat names window messages among the records a header binds, and the Desktop's explanations say version 7.

Tests pin the mechanisms a header binds, bind a session's window message records - a send's start and stop to the
sender, its retrieval to the receiver, a post's queueing to the poster and its retrieval to the receiver - bind an
admitted record's row to the process that raised it, never the receiver it names, and read a version 6 checkpoint; five
mutations are caught. No profile requests the source yet.

## Addendum: the opt-in window messages profile (revision 962)

The fifth decision's last requirement was an opt-in profile stating the cost. `window-messages` requires Kernel-Process
and Win32k's message pump, so where Win32k is refused - a schema without an end's pointer (the first addendum) - it does
not start rather than record lifecycle and no message. It takes no focus, as Explore takes none, and `icat capture` and
`icat record` take it as they take the other opt-in profiles; a capture asking for it enables Win32k at its message pump
keyword, at the informational level.

Its class is its sources' at most, Moderate for Kernel-Process. Its statement names what ADR-086's first addendum
measured: the message pump's collection alone added a median 0.36 and 0.28 CPU points, Low, but a message-bound workload
ran 8.3 and 5.2% lower in throughput, past the 5% target. That is why the profile is opt-in and Explore never admits the
source, which the source's catalog notes now say. Nothing has measured the profile whole yet, and no build carries a tier
for window messages.

Tests compile the profile with and without Win32k, hold the lead's one profile, and check both commands' profile lists,
help and preview; the catalog now counts ten profiles. Five mutations are caught.
