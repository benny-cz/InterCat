# ADR-086: Win32k's message pump records a window message between processes at both ends, but not which message

- Status: accepted for M6's UI messaging work package; no catalog source records window messages and no capture has
  recorded one through the product yet; since revision 955 collecting the message pump's cost is measured, Low in the
  processor time it adds but a message-bound workload past §12's throughput target (its addendum); since revision 956 it
  is inventoried as the mechanism's lead, requested by no profile and admitting no event (its second addendum); since
  revision 957 a session is measured delivering a message's ends in the order they were raised, so a gate may number a
  message by its pointer as records arrive (its third addendum); since revision 958 a window message gate numbers each
  message at its pointer from its start to its end (ADR-087)
- Date: 2026-11-05
- Decision owners: InterCat maintainers
- Relates to: plan M6, §4.1, §4.2, §12, R3, R22, P7, P16, FX-WMSG-001, `tools/InterCat.UiMessageProbe`

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

## Addendum: what collecting the message pump costs (revision 955)

The first consequence asked for the message pump's cost, every GUI thread's pumping being machine-wide.
`tools/InterCat.UiMessageProbe --measure --impact` ran a message-bound workload - FX-WMSG-001 with `--copies 20000
--posts 20000 --bytes 64`, its sender sending its receiver's window 20,000 `WM_COPYDATA` messages and posting it 20,000
more, about two and a half seconds - in alternating pairs, once with no session and once while an owned session of its own
collected `Microsoft-Windows-Win32k` at keyword `0x400000` machine-wide, its consumer only counting the records by when each
was raised, as the object manager's, COM-Perf's and AFD's costs were measured (ADR-082's, ADR-077's and ADR-072's
addenda). A pair run first and not kept warmed the consumer. The machine ran a virtual machine and other applications
beside the measurement, and its busy share with no session moved between 15 and 26% from trial to trial, far more than the
session added; so each trial also accounted the processor time of the workload's processes, through a job holding them,
and of the probe's own, the consumer's: what the session added, apart from anything else the machine ran. Nine pairs a
series, nothing lost:

| Series | Processor time added | Busy share | Workload's own time | Workload slower by | Throughput regression | Records a second; in the second before |
|---|---|---|---|---|---|---|
| `bench/results/window-message-impact-20261105T175136Z` | 0.36 pp: Low | 1.56 pp | 5.3% more | 9.0% | 8.3% | about 52,000; about 3,100 |
| `bench/results/window-message-impact-20261105T175321Z` | 0.28 pp: Low | none | 4.6% more | 5.4% | 5.2% | about 53,000; about 3,100 |

A message raises its records in the threads that send, post and retrieve it - a send four, at both ends, a post two - so
the time is the messages' own: a workload messaging as fast as it can spends about a twentieth more processor time, and its
throughput fell past §12's 5% target in both series' medians, the second's by little. The consumer took about half a
microsecond a record. In the second before each workload ran, the machine's other windows raised about 3,100 records: what
a capture collecting the keyword receives with no messaging workload of its own, well within §12's 100,000 records a second.

1. Collecting the message pump is Low in the processor time it adds, stated beside the regression a message-bound workload
   measured, and only an opt-in profile saying so may admit it; Explore never does.
2. What remains before a profile admits it: inventorying it as the mechanism's lead, numbering each message's ends from its
   queueing or start to its retrieval's end, and a capture through the product.

A test holds the two series to their class, the workload's time and regression, and their rates; fourteen alterations of
them are caught.

## Addendum: the message pump inventoried as window messages' lead (revision 956)

The first decision named the message pump the mechanism's lead, and the first addendum measured its cost. It now enters
the source catalog as window messages' lead, as COM-Perf entered as COM calls' and the handle group as named
synchronization objects' (ADR-077's third addendum, ADR-082's second): `etw/manifest/Microsoft-Windows-Win32k`, the
manifest provider at its `MessagePump` keyword, `0x400000`, at the informational level its six message events are declared
at, experimental, requested by no profile and admitting no event.

- **It admits no event.** A message's ends are joined by a kernel address reused once the message is retrieved, so a
  capture must number each message from its queueing or start to its retrieval's end before any record is admitted (the
  fourth decision).
- **Its measured notes are what `icat capabilities` states of it:** every message at both ends, a send naming its
  receiver; no record naming which message; a pointer joining two ends only within a message's life; and the machine's
  other windows' records, about 3,100 a second with no messaging workload of the capture's own.
- **Its cost is Low, stated beside its regression.** The first addendum's second series is its evidence, and its notes
  say that a message-bound workload ran 8.3 and 5.2% lower in throughput, so only an opt-in profile stating it may admit
  it, never Explore. Whether a session's process filter holds it is not measured, so it is stated as having none.

No other source claims window messages, and no build carries a tier for them: no fixture has measured them through the
product. A test holds the lead, its absence from every profile and the absence of a tier; nine mutations of the catalog
entry are caught.

## Addendum: a session delivers a message's ends in the order they were raised (revision 957)

The fourth decision asked a capture to number each message from its queueing or start to its retrieval's end before
joining by its pointer. A capture's gates decide records one at a time as they arrive (ADR-073), so such a gate meets a
message's ends in the order a session delivers them, and a message's ends are raised microseconds apart, often on two
processors, whose buffers a session hands over apart. `tools/InterCat.UiMessageProbe --measure --order` ran FX-WMSG-001
under an owned session with the product's buffer size, 128 KB, at the source's level and keyword, and kept each record
with the order it was delivered in, the time it was raised and the processor it was raised on. Three runs, nothing lost:

| Quantity | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| `WM_COPYDATA` sent, and messages posted, by the truth | 32, 32 | 2,048, 2,048 | 20,000, 20,000 |
| Records delivered machine-wide, and those delivered after one raised later | 18,298, 0 | 29,143, 0 | 148,340, 0 |
| Sends with every end, delivered in the order raised, and raised on two processors | 32, 32, 31 | 2,048, 2,048, 940 | 20,000, 20,000, 11,807 |
| Posts and the close with both ends, delivered in the order raised, and raised on two processors | 33, 33, 31 | 2,049, 2,049, 1,544 | 20,001, 20,001, 17,547 |
| Pointers the run's messages used more than once | 5 | 74 | 542 |
| Records a gate following a message by its pointer as they arrive numbers apart from its life | 0 | 0 | 0 |

1. **A session delivers its records in the order they were raised.** Not one of 195,781 records, the machine's other
   windows' among them, arrived after one raised later, though most messages' ends were raised on two processors. So a
   send's start arrives before the receiver's retrieval, the retrieval before the sender's stop, and a post's queueing
   before its retrieval.
2. **So a gate may number a message by its pointer as records arrive.** A send's start or a post's queueing begins a
   message at its pointer, an end joins the message its pointer has, and a send's stop or a post's retrieval ends it. A
   pointer the runs used hundreds of times over was another message's at each start, and the simulated gate numbered every
   record as its message's life is (R22).
3. **The order is measured, not promised.** It is this build's, with the product's buffer size, at these rates. A gate
   relying on it is to count each record raised before one delivered ahead of it, so a capture whose delivery is ever out
   of order says so instead of numbering by it.

A test holds the three runs; thirteen alterations of them are caught.
