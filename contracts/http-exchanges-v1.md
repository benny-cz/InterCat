# HTTP exchanges contract, version 1

Status: M3/M8 additive contract, revision 247 (ADR-037)
Owner: `InterCat.Analysis` (`HttpExchangeIndex`), `InterCat.Application` (`SessionHttpExchanges`)
Produced by: the Desktop's process and channel rungs, and
`icat exchanges <session> [--pid <id>] [--exchanges <n>] --json`

An HTTP exchange is a request and its response as one client process made them through WinINet, derived from WinINet's
capture records (`contracts/content-v1.md` §5.1). It is read from the records' metadata and source fields alone: which
buffers were recorded, the bytes each record measured, their places and ends. It never reads content, so it says how
large each part was and whether it was recorded whole, and never what it said.

## 1. Records

A record is a buffer when its mechanism is `Http`, its event is 2001 to 2004 - request head, request body, response head,
response body - and its source fields name its exchange (`HttpExchangeId`, 16), its place (`ContentBufferSequence`, 17)
and its ends (`ContentBufferFlags`, 18: 1 first, 2 last). Its process is the one that raised it, by
`process-binding-v4` (`contracts/entities-v1.md` §7). An HTTP record whose fields name no exchange belongs to none and is
counted as such.

## 2. Grouping (`http-exchange-v1`)

An exchange number is its client process's own count from 1 (ADR-037), so one number and one process ID can name two
exchanges in a session: a process ID used again by another process, or WinINet loaded again. A number's buffers are taken
in time, then by place, and a buffer opens another use of the number when:

- it is a request head flagged first; or
- the use so far already holds a buffer of the same part at the same place, as a use whose request head was lost would.

Nothing else ends a use. WinINet raises the empty buffer that ends a request body after the response has ended (measured
live in revision 247), so an exchange is not over when its response is. Each use is one exchange, bound to a process
instance by its first buffer. Two uses are never merged (R22).

A release of a session's oldest interval (`contracts/store-v1.md` §8, ADR-043, revision 435) keeps every buffer of an
exchange one of whose buffers stays, and the use before a use opened only by repeating one of its buffers, so every
exchange that stays is grouped, keyed and timed as before.

## 3. An exchange

| Field | Meaning |
|---|---|
| number, process | the exchange number and the process ID and instance that raised it |
| four parts | for each part: its buffers, the bytes their records measured, and whether it was recorded whole - numbered from 0 in order, the first flagged first and the last flagged last |
| complete | its request head and response were recorded whole, and its request body when it had one. WinINet raises no record for an empty request body, so a request without one lacks nothing |
| first, last | the session times of its first and last buffers |
| duration | from its first buffer to the buffer that ended its response, when that end was recorded; none otherwise |

Within an interval an exchange counts where its last buffer falls, so each exchange is counted in exactly one place in
time.

## 4. On the ladder

A process that raised HTTP records has one row for them on its rung, `http:{instance}`, beside its paired channels and
RPC channels: its exchanges, how many were recorded whole, the bytes of their messages and the median duration. Its bytes
are HTTP messages, `ApplicationPayload`, which never rank with transport bytes (P3). Enter lists the exchanges a page at
a time in reading order, each `http:{instance}/{stream}.{epoch}.{ordinal}` by its first buffer's record, leading with its
duration. Enter on an exchange opens its buffers as evidence, where C opens a buffer's content and its whole part
(`content-v1` §4); E at the channel rung reads every buffer of the process's exchanges.

An evidence page scoped by an HTTP key names `http-exchange-v1` in its query identity, as one scoped by an RPC key names
`rpc-call-operation-v1`, so a cursor never continues across a change of rule. Since revision 249 the timeline beside such
a page counts exactly its records apart - an exchange's buffers, or all of a process's - as it does an RPC channel's or
call's.

Since revision 290 the timeline at a process's exchanges also draws them as a lane under the machine's records, as an
RPC channel's calls are drawn. An exchange runs from its first recorded buffer to the one that ended its response; one
whose response's end was not recorded runs to its last buffer, and one whose first buffer has no session time is not
placed. A whole exchange is drawn in the HTTP hue, and one any part of which was not recorded whole faint. Past the
lane's budget of 4,000 exchanges in view, the lane draws density columns instead, each the exchanges running in it with
the share not recorded whole faint on top; no exchange is dropped. A bar's card states how long the exchange took,
its number, the bytes of its two messages and how it was recorded; a click selects its row when the row is listed.

## 5. `icat exchanges --json`

`http-exchanges-v1`: the session and generation, `groupingRule`, `bindingRule`, the evidence policy, the HTTP records
that name no exchange, and per process its instance (or why none is admitted), its ladder key, its exchanges, how many
were recorded whole, its buffer records, request and response bytes and median duration; with `--exchanges n` or
`--pid`, its first exchanges, each with its key, number, first time, duration, completeness and four parts. Since
revision 373 it states what the capture covered of HTTP over the session (`coverage`, each entry a mechanism, its
state and the fact behind it), read from the coverage ledger (`coverage-v2` §4), so a listing of none says whether an
exchange could have been seen at all; its report says it in the words `icat metric` uses.

## 5a. In a redacted package (revision 250)

A redacted package keeps each HTTP buffer's place and ends as they were, and its exchange number as a pseudonym of a
namespace of its own - the same for every buffer of one exchange, and never a number the source held - so the package's
exchanges group exactly as the source's, with their parts' sizes and timing and none of their content
(`contracts/redacted-session-v1.md` §4). A package made before revision 250 withheld the three fields, and its HTTP
records name no exchange.

## 5b. Kept with a finished session (revision 441)

A finished session's operation index keeps its exchanges (`contracts/operation-index-v1.md` minor 1): each with its
parts, times and buffers' places, without its process binding, which a reopen makes from the checkpoint's instances as
this grouping does. A reopen's first HTTP listing therefore groups no buffer, and answers as grouping every segment does.
A live generation keeps none, and groups its exchanges from every record once.

## 6. Not defined at this version

- An exchange's method, target, status or headers: they are content, and reading them is a decoder's work (§11.2's
  `DecodedFields`), which is not defined.
- HTTP/2, compressed responses and asynchronous WinINet (ADR-037).
