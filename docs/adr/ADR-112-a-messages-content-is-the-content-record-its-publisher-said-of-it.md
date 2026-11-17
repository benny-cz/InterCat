# ADR-112: A message's content is the content record its publisher said of it

- Status: accepted for M7's review's last refinement; the message index binds each message's content since revision 1055,
  and `icat messages` says what was kept of it since revision 1057, and `icat content --record` reads it since revision
  1058
- Date: 2026-11-17
- Decision owners: InterCat maintainers
- Relates to: ADR-036, ADR-103, ADR-104, ADR-106, ADR-107, plan §15 M7, `contracts/cooperative-messages-v1.md`,
  `contracts/sdk-v1.md`, `contracts/content-v1.md` §5.2, R3, R20, R22

## Context

A capture keeping the SDK's content keeps each message's bytes in a record of their own, `MessageContent`, which the
process that published the message raises right after its publication, naming the message by its channel's identity,
epoch and sequence (ADR-107). The message index keys publications and consumptions into messages (ADR-104, ADR-106) and
read no content record, so a message's content was reachable only record by record, through the evidence inspector and
`icat content` - the last of the refinements M7's exit review left. And a record of the SDK that holds no bytes said its
source "carries metadata only", which since the schema's minor 2 is not so of the SDK.

## Decision

1. **A content record names its message as its publication does**: by its channel's identity, `ChannelEpoch` and
   `MessageSequence`. The index binds it to the message of that identity said alone and published once, whose one
   publication the same process raised - the same PID bound to the same process instance, so a process Windows gave the
   publisher's ID to afterwards says none of its content (R22).
2. **A content record is never a message of its own** (R3): it makes no message, channel, run or byte. One naming no such
   message - a sequence nothing published, an epoch or a channel no message holds, a batch's run, a message published more
   than once, or another process's - is counted apart, in `contentApart`; one lacking a value it is keyed by is counted in
   `withoutMessage`, as any record of the SDK is.
3. **One content record a message**: a message more than one content record names takes none of them, as a conflicting
   message chooses none of its records, and says how many named it.
4. **The rule is `cooperative-message-content-v1`** (R20), beside `cooperative-message-v2`, which keys messages as before:
   a generation keeping no content binds none, and every message, run and count is what it was.
5. **A message's bytes are the fragment kept of its content record**, read by the record's raw identity
   (`contracts/content-v1.md` §1): whole, cut to the record limit, omitted at the content limit, or released since; shown
   only where the capture kept them with consent to inspect them, and never in a listing's JSON (ADR-036). `icat messages`
   and the window's Logical messages… say it of each message, which follow.

## Consequences

- A message's content is found from the message, not by searching records: the index reads the SDK's content records in
  the pass that reads its other records, and binds each in a search of its channel epoch's entries.
- A record of the SDK that holds no bytes says why in the SDK's words: a content record keeping none had them released
  since, and every other record of the SDK says a message's bytes are said in a content record of their own, which a
  Content capture of the publishing process keeps. Another provider's records under the same mechanism still carry
  metadata only.
- Nothing changes for a capture keeping no content: its index binds nothing, and counts none apart.
- 10 mutations of the binding and of what a record of the SDK holding no bytes says are caught.

## Addendum: `icat messages` says what was kept of each message (revision 1057)

Where a capture asked for the SDK's content - the plan it retains admits the content event - `icat messages --channel`
lists beside each message what was kept of its bytes, by its content record's fragment: "8 bytes, whole", "the first
4,096 of 10,000 bytes, cut by the record limit", "none: the content limit was reached", "none kept now: released
since", or why none was said - published without its bytes, its publication not recorded, said in a batch, or named by
more than one content record. Its JSON holds each message's `content` - how many content records name it, the one
record, the fragment's disposition and its kept and original lengths, whether a person may see it - and `contentRule`
and `contentApart` at the top, never a byte. A capture that asked for none says none of it. Reading one message's
bytes from its message, and the window's listing, follow. 9 mutations are caught.

## Second addendum: `icat content --record` reads a message's bytes (revision 1058)

`icat content` names a record by its raw identity as it prints one - `--record <stream>/<epoch>/<ordinal>`, beside the
`--session-id` every form takes - in place of an evidence page's generation, segment and row: a raw identity is the
record's in every generation, so a message's content record, as `icat messages --json` names it with the session's
identity, is shown as the locator form shows a record - its facts, and its bytes only when asked, inert, and only where
the capture kept them with consent. A raw identity restarts in every capture, so another session's identity reads none
of it; a record the generation holds none of, a raw identity that is not one, and a locator beside it are refused in
words. The record is found in one uncached pass over the generation's segments, by their ordinals first.
5 mutations are caught. The window's listing follows.
