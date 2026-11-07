# M7 release review — cooperative instrumentation and shared-memory semantics

Date: 2026-11-07, at plan revision 1043
Scope reviewed: M7 as built through revision 1042: the cooperative SDK's schema (`contracts/sdk-v1.md`, ADR-102) and its
.NET and native libraries (ADR-105), a capture's admission of what they say (ADR-103), the logical messages and batches
read from it (`contracts/cooperative-messages-v1.md`, ADR-104, ADR-106), and a message's content, kept only under a
request (ADR-107)
Method: §15.2 asks, before a post-v1 milestone is committed for shipment, for the user problem, the exact new evidence
obtainable, the supported environments, the expected overhead, the deployment burden and the exit fixtures; the
commitment review (revision 1025) answered them for what M7 was to ship. Each is answered again below for what revision
1042 ships, M7's exit tests and deliverables are checked against named tests and fixtures, and what is not built is listed
as not built.

## The user problem

OS tracing sees a process's handles, views and transfers, never what an application means by them: how many messages
went through a shared-memory ring, which reader took which, which of its operations a request was part of. A section
two processes map is topology at most (ADR-092); its traffic is invisible to every driverless source.

## The new evidence obtainable

What an application says of itself through the SDK, each an assertion of the application's, stated as such: its channels
by a shared identity and epoch, each message's publication and each reader's consumption by sequence, with logical
lengths and the bytes copied, runs of messages said in batches, its operations with their parents and status, and its own
clock at each transition. Only when a person asks for it, under a request naming the processes, a message's bytes - kept
as restricted content beside the journal, never in it.

## Supported environments

Applications on .NET 10 referencing `InterCat.Sdk`, and C or C++ applications including `sdk/native/include/intercat_sdk.h`
(ETW's provider API; built with gcc 10.3 and MSVC). Captures through `icat record` and `icat capture` - content through
`icat record` alone - on the builds the fixtures measured, Windows 10.0.26220 x64.

## Expected overhead

Published (`contracts/sdk-v1.md` §8): a call costs a median 48.5 ns with no session listening and 960.6 ns recorded, a
capture keeping every call up to 100,000 a second; in batches of 64, 2.5 and 27.8 ns a message, every message kept up to
1,600,000 a second. Keeping content: Low, a median 0.016 CPU percentage points (`contracts/content-v1.md` §5.2). Past what
a capture keeps, it says what it lost.

## Deployment burden

None for InterCat: no service and no driver. An application takes one library, or one header, with no other InterCat
dependency.

## M7's exit tests

| Exit test | Verdict | Evidence |
| --- | --- | --- |
| Producer/consumer and multi-reader shared-memory fixtures match independent truth counts | Met | FX-SDK-001, a ring one producer writes and three readers read, three runs through the product: "P4: FX-SDK-001 recorded through the product: a message three readers consume is one message and three consumptions, never three ..."; its batches, FX-SDK-005: "P4: FX-SDK-005 and FX-SDK-006 recorded through the product: a batch's messages are each one message of the run it said, never one message nor estimated ..." |
| Wrapping sequence IDs and process restart create new epochs | Met | "R3: FX-SDK-001's message no reader read stays open, never said consumed, and the ring made again is a second epoch whose sequence starts at 1 again, never one message with the first's"; FX-SDK-002 to FX-SDK-006 alike, a stream's second connection a second epoch (FX-SDK-003, FX-SDK-004) |
| Missing consumption remains unknown/open | Met | The same R3 tests: each fixture's message no reader read stays open, never said consumed |
| Uninstrumented memory access remains unmeasured | Met | FX-SECT-001: "P27: a sections evaluation finds each truth section twice ... and is topology only: it claims no byte and no operation"; no source records a load or store, and the SDK says only what an application says |
| SDK-disabled overhead and enabled throughput are published | Met | `contracts/sdk-v1.md` §8, held to its bench results: "§15: the cooperative SDK's published cost ..."; "§15: a batch's call costs about what a single message's does ..."; "R21: a capture of a burst it could not keep whole says it lost records, never that it kept every call - and paced at up to 100,000 calls a second it kept every one" |
| Old viewers retain unknown new event fields safely | Met | "§24: a capture keeps a later minor version's added field unread and safe - each event's known fields at the offsets it knows, a longer record read no further - and holds no descriptor of a later major version, whose records stay undecodable, never misread"; an event a later minor adds never reaches an earlier capture, which enables only the events it admits (measured, revision 1039) |

## M7's deliverables

| Deliverable | Built | Where |
| --- | --- | --- |
| A documented SDK and version negotiation | Yes | `contracts/sdk-v1.md` §1 and §5: each process announces its schema, clock and library to every session enabling the provider; ADR-102, ADR-103's addendum |
| Sample pipe, socket and shared-memory applications | Yes | FX-SDK-003 (a named pipe), FX-SDK-004 (a loopback socket), FX-SDK-001 (a shared-memory ring), FX-SDK-002 (the native sample) |
| Source adapters | Yes | `etw/eventsource/InterCat-Sdk` under the opt-in `sdk` profile (ADR-103), and under a content request (ADR-107) |
| A shared-resource view with measured logical messages | Yes | `icat messages` and the window's Logical messages… (`contracts/cooperative-messages-v1.md`, ADR-104) |
| An overhead guide | Yes | `contracts/sdk-v1.md` §8 |
| Explicit content opt-in integration | Yes | ADR-107: a message's content said only to a session that asks, kept only under a request naming its processes, at a measured cost; FX-SDK-007 and FX-SDK-008 hold both libraries' content to their truths ("I21: FX-SDK-007 recorded through the product keeps each message the SDK said with its bytes as the truth's ...", "R17: FX-SDK-007's content is admitted per record before it is kept ...", and FX-SDK-008's alike) |
| Size and count metadata without content capture | Yes | Every fixture above but FX-SDK-007 and FX-SDK-008 records metadata alone |

## Not built, in M7's order

- The SDK's content asked for through the broker or the window: `icat record` alone asks for it.
- A message's content from `icat messages` or the window's Logical messages…: it is read from the evidence inspector and
  `icat content`, one record at a time.
- An operation tree by parent, and the lag between a publication and its consumptions (`contracts/sdk-v1.md` §9).
- A capture saying that a process announces a later minor than it knows, whose added events never reach it.

## Verdict

M7's exit gate is met: every exit test holds against fixtures recorded through the product on this build, and every
deliverable is built. What an application says through the SDK is read as its own assertion, never as an observation of
the channel beneath it, and its content is kept only when a person asks. The parts listed above follow as M7's
refinements; none is an exit test.
