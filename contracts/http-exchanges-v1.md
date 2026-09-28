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
`rpc-call-operation-v1`, so a cursor never continues across a change of rule.

## 5. `icat exchanges --json`

`http-exchanges-v1`: the session and generation, `groupingRule`, `bindingRule`, the evidence policy, the HTTP records
that name no exchange, and per process its instance (or why none is admitted), its ladder key, its exchanges, how many
were recorded whole, its buffer records, request and response bytes and median duration; with `--exchanges n` or
`--pid`, its first exchanges, each with its key, number, first time, duration, completeness and four parts.

## 6. Not defined at this version

- An exchange's method, target, status or headers: they are content, and reading them is a decoder's work (§11.2's
  `DecodedFields`), which is not defined.
- Exchanges drawn as marks in the timeline, as an RPC channel's calls are.
- HTTP/2, compressed responses and asynchronous WinINet (ADR-037).
