# ADR-087: A window message gate follows a message at its pointer from its start to its end

- Status: accepted for M6's UI messaging work package; no profile compiles the gate yet
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
