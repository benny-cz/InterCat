# ADR-086: Win32k's message pump records a window message between processes at both ends, but not which message

- Status: accepted for M6's UI messaging work package; no catalog source records window messages and no capture has
  recorded one through the product yet
- Date: 2026-11-05
- Decision owners: InterCat maintainers
- Relates to: plan M6, §4.1, §4.2, R3, R22, P7, P16, FX-WMSG-001, `tools/InterCat.UiMessageProbe`

## Context

§4.1 asks of window messages, `WM_COPYDATA`, the clipboard and DDE mechanism-specific research, an explicit unsupported or
experimental entry until a source is validated. The inspection of revision 953
(`bench/results/ui-message-provider-inspection-20261105T163256Z`) found `Microsoft-Windows-Win32k` declaring, under its
`MessagePump` keyword, a posted message's queueing and retrieval and a sent message's start and stop at both ends - the start
at the sender naming the receiver's process and thread - each with its window, message number, parameters and the queue
message's kernel pointer. Nothing had measured what it emits.

## Measurement

Fixture `FX-WMSG-001` has a receiver make a message-only window of a class of the run's own and pump its messages, and a
sender find it by that class, send it `WM_COPYDATA` messages of seeded payloads, post it numbered messages of the
application's range and last post it a close. `tools/InterCat.UiMessageProbe --measure` ran it under an owned session
enabling `Microsoft-Windows-Win32k` at keyword `0x400000`, machine-wide, on build `10.0.26220.0-x64`. The receiver's window is
the one the sender's sends naming the receiver went to; each end is joined to the others by the queue message pointer both
carry.

| Quantity | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| `WM_COPYDATA` sent, by the truth | 32 | 128 | 8 |
| Sends started at the sender, each naming the receiver's process and one thread of it | 32 | 128 | 8 |
| Their stops at the sender, and their retrievals' starts and stops at the receiver, joined by pointer | 32, 32, 32 | 128, 128, 128 | 8, 8, 8 |
| Messages posted, by the truth, and the close after them | 32 + 1 | 8 + 1 | 128 + 1 |
| Posts queued, each raised in the sender, and retrieved at the receiver, joined by pointer | 33, 33 | 9, 9 | 129, 129 |
| Records of the two processes carrying a message number or a parameter | 0 | 0 | 0 |
| Records delivered machine-wide, and lost | 13,556, 0 | 13,083, 0 | 14,369, 0 |

## Decision

1. **A window message one process sends or posts another is observable at both ends.** Win32k's message pump records a sent
   message's start at the sender, naming the process and thread that receives it, and its stop, and the receiver's retrieval
   and its end; a posted message's queueing, raised in the poster, and its retrieval in the receiver. Each end is joined to the
   others by the queue message's kernel pointer. This is the mechanism's lead.
2. **It records which process messaged which and when, never which message.** Every record of the two processes carried zero
   for its message number and both parameters, and none carries a copied payload's size. So `WM_COPYDATA`'s bytes are not
   measured, nor inferred from a message being a copy (R3); a DDE conversation, carried by messages, is not told from any other
   message; and no record discloses a message's content (P16).
3. **Only a sent message names its receiver.** A posted message's ends are joined by their pointer alone, its queueing raised
   in the process that posted it, its retrieval in the window's.
4. **A pointer joins two ends only within a message's life.** It is a kernel address, reused once a message is retrieved, so a
   capture must number each message from its queueing or start to its retrieval's end before joining by it (R22).
5. **The clipboard is not measured here.** A workload writing the clipboard would replace whatever the machine's user holds on
   it, so its events are measured only with that user's consent.

## Consequences

- No catalog source records window messages yet. Next: inventory the message pump as the mechanism's lead, requested by no
  profile; measure its cost, every GUI thread's pumping being machine-wide; number each message's ends; and measure a capture
  through the product. With no byte measured, a tier through the product would be experimental evidence at most.
- Tests hold the three runs: every message at both ends, each send naming its receiver, each end joined by its pointer, and no
  message number or parameter in any record of the two processes.
