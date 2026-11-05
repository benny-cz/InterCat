# HTTP exchanges contract, version 1

Status: M3/M8 additive contract, revision 247 (ADR-037)
Owner: `InterCat.Analysis` (`HttpExchangeIndex`), `InterCat.Application` (`SessionHttpExchanges`)
Produced by: the Desktop's process and channel rungs, and
`icat exchanges <session> [--pid <id>] [--exchanges <n>] --json`; its table and `http-exchange-heads-v1`
by `--output` and `--decode` (§5f)

An HTTP exchange is a request and its response as one client process made them through WinINet, derived from WinINet's
capture records (`contracts/content-v1.md` §5.1). It is read from the records' metadata and source fields alone: which
buffers were recorded, the bytes each record measured, their places and ends. It never reads content, so it says how
large each part was and whether it was recorded whole, and never what it said; only `icat exchanges --decode` and an
exchange's conversation in the window, when asked, read heads (§5c, §5d), and only `http-exchange-heads-v1` writes
what they say (§5f).

## 1. Records

A record is a buffer when its mechanism is `Http`, its event is 2001 to 2004 - request head, request body, response head,
response body - and its source fields name its exchange (`HttpExchangeId`, 16), its place (`ContentBufferSequence`, 17)
and its ends (`ContentBufferFlags`, 18: 1 first, 2 last). Its process is the one that raised it, by
`process-binding-v7` (`contracts/entities-v1.md` §7), as since `process-binding-v4`. Its thread is whichever of that process's threads WinINet finished
the buffer's step on, for a synchronous client as for an asynchronous one, and not the thread that made the request
(FX-HTTP-005, revision 886): nothing here reads it. An HTTP record whose fields name no exchange belongs to none and is
counted as such.

## 2. Grouping (`http-exchange-v1`)

An exchange number is its client process's own count from 1 (ADR-037), so one number and one process ID can name two
exchanges in a session: a process ID used again by another process, or WinINet loaded again. A number's buffers are taken
in time, then by place, and a buffer opens another use of the number when:

- it is a request head flagged first; or
- the use so far already holds a buffer of the same part at the same place, as a use whose request head was lost would.

Nothing else ends a use. WinINet raises the empty buffer that ends a request body after the response has ended (measured
live in revision 247), so an exchange is not over when its response is. Each use is one exchange, bound to a process
instance by its first buffer. Two uses are never merged (R22). An asynchronous client's buffers are grouped as a
synchronous one's: FX-HTTP-005's 64 exchanges eight at once, their buffers raised on the client's threads and WinINet's,
are one exchange to a request, each part whole (revision 886). So are an exchange's over HTTP/2, which WinINet records
as over HTTP/1.1, its heads in HTTP/1.1's form: FX-HTTP-006's eight clients' 64 exchanges, multiplexed on one connection,
are one exchange to a request, each part whole (revision 887). So are an exchange's whose request body was written in
pieces, a buffer to a write: FX-HTTP-007's eight clients' 64 exchanges, their bodies in up to 46 buffers each, are one
exchange to a request, each body in its writes' buffers and one to end it (revision 888). A redirect WinINet follows is
two exchanges, grouped as any: WinINet makes the request to the location by itself, and its capture keeps that request
as an exchange of its own, numbered apart and begun once the redirect's response ended. FX-HTTP-009's eight clients' 64
requests, redirected by 302 and again by 307, are two exchanges to a request, each part whole (revision 890); no record
ties the two, so where heads are decoded the listing names the one that followed a redirect (§5g). So is an exchange
made through a proxy, grouped as any: over HTTP its request is kept as WinINet sent it to the proxy, its target the
whole address; over TLS WinINet first asks the proxy to CONNECT a tunnel, and keeps the CONNECT as an exchange of its
own - its head and the proxy's answer of no body - and the request it then sends through the tunnel as an exchange of
its own too, as over TLS directly. FX-HTTP-010's eight clients' 64 requests through a proxy are one exchange to a
request, and over TLS one more to each CONNECT, each part whole (revision 891; §5h). So is a challenge for credentials
and its answer: WinINet keeps the request a server challenged and the request it then sends with credentials as an
exchange each, numbered apart; FX-HTTP-011's eight clients' 64 requests, each client's first challenged, are an exchange
to each leg, each part whole (revision 892; §5i). So is a proxy's challenge, to a request sent it whole or to a CONNECT:
FX-HTTP-012's eight clients' 64 requests through a proxy that challenges are an exchange to each leg and each CONNECT,
each part whole (revision 893).

A release of a session's oldest interval (`contracts/store-v1.md` §8, ADR-043, revision 435) keeps every buffer of an
exchange one of whose buffers stays, and the use before a use opened only by repeating one of its buffers, so every
exchange that stays is grouped, keyed and timed as before.

## 2a. From one live generation to the next (revision 733)

A live capture publishes a generation per chunk, and a later generation's exchanges are extended from the earlier
generation's rather than grouped again, with exactly a full grouping's result, as RPC calls are (`operations-v1` §3a):

- **What is kept.** Beside its exchanges a grouping keeps every number's last use whose response has not ended, or whose
  last buffer reads within 15 seconds of the latest buffer read (the *late window*), with whether that use holds every
  buffer of its number; and every number it has read.
- **What extends.** A generation whose segments and source-field segments begin with the same files the earlier one
  read - by name, length and digest - reads only its added segments and fields. A number the added buffers name is told
  apart again from its kept last use on, or from nothing when no buffer named it before; every other exchange stands,
  and all are bound again to the generation's instances by their first buffers' readings, as §3 binds them. A kept use
  that holds every buffer of its number takes an added buffer however late, since the number is then grouped again
  whole.
- **What declines.** Anything an extension could not add exactly is grouped in full: a buffer of a number read before
  and no longer kept; a buffer read before its number's kept last use began, while an earlier use holds some of the
  number's buffers; a segment read before that the generation no longer names, or names with another length or
  digest; and a source field naming no HTTP record the generation added. A record's source fields are published with
  it, so such a field could move a buffer already grouped.

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
A live generation keeps none: since revision 733 it extends the exchanges of the generation before it (§2a), and groups
them from every record only when nothing earlier was grouped in the same process, or an extension declines.

## 5c. What the listed heads say (revision 776)

`icat exchanges <session> --pid <id> --decode`, or `--exchanges <n> --decode`, reads each listed exchange's request
and response heads, each the whole part its buffers make (`contracts/content-v1.md` §5.1), and decodes them with the
HTTP/1.1 head decoder (`contracts/decoders-v1.md` §4). Each exchange is listed with what its request asked for - its
method and target - of which host, by its first Host field in any case, and what its response answered - its status,
and the reason its status line gave - beside whether the exchange was recorded whole. A head not whole, or kept
without consent to inspect it, is not decoded, and leaves the answer partial as `icat content --decode` does; one
whose start line is not HTTP/1.1's says nothing in its cells. Why is said beneath its process's exchanges, as is where
a head stops being HTTP/1.1, and where a body does not hold what its head declared (§5e). Every listed head is read
under one lease in one pass over the session's segments, and only the exchanges listed are read. Asked for with
`--json` or `--output`, the listed exchanges and their heads are written (§5f); `--decode` refuses a listing of no
exchanges. The figures above the exchanges are read as without `--decode`. Since revision 887 a listing with its heads
decoded says among its caveats that the version a head names is WinINet's: it keeps an exchange it made over HTTP/2
with heads in HTTP/1.1's form, so no head says which protocol carried its exchange (FX-HTTP-006,
`contracts/decoders-v1.md` §4). Since revision 890 a response that redirects is listed with the exchange that followed
it, or with its location where none listed did (§5g), and since revision 891 a listing that holds a request to a proxy
says among its caveats, once, what that means for what was kept (§5h); since revision 892 a challenge for credentials
is listed with the exchange that answered it, and a listing holding a challenge, or a request carrying credentials,
says among its caveats, once, what WinINet does with one, and that the heads then hold the credentials (§5i).

## 5d. An exchange's conversation (revision 777)

In the window, C on an exchange at a process's HTTP exchanges, or its inspector's Read its conversation, opens its
conversation: its request and its response side by side, each with what was recorded of its head and its body - their
bytes and buffers, and whether they make the part whole - with when the exchange began, how long it took and which of
its parts were not recorded whole. That much is read from the records' metadata alone, as §3 is, from the current
generation by the exchange's key, under the window's evidence policy; a generation that no longer holds the exchange
says so. Decode its heads reads its two heads, each the whole part its buffers make, from the content kept with
consent to inspect it, and decodes them as §5c does: each side then says what it said - the request's method and
target, of which host; the response's status and reason - above each field and the bytes of its head it came from, and
what was not decoded and why; and beneath each body, what its head declared of it (§5e). Decode its heads is offered
disabled, saying why, where neither head was recorded whole; a whole head kept without consent is decoded to say it
read none. Nothing decoded is kept, and no body is read. Show its buffers closes the conversation and opens the
exchange's buffers, as Enter on its row does, where C shows a buffer's content and its whole part: from the exchange
to its messages, and on to the fragments they were recorded in. Since revision 781 the way runs back as well: a
buffer's content viewer opens the conversation of the exchange it belongs to (Its exchange's conversation…), found
among its process's exchanges by its raw identity under the window's evidence policy, and from there Show its buffers
is not offered, the viewer being a dialog of InterCat's window. A record of no exchange, or of a process the policy
does not admit or no instance binds, says why it has none.

## 5e. A body's length as its head declares it (revision 778)

Where an exchange's heads are decoded (§5c, §5d), each body is set beside what its head declares of it, as RFC 9112
§6.3 has a recipient read a body's length, and compared with the bytes its buffers measured; nothing of the body's
content is read. A response to HEAD, or of status 1xx, 204 or 304, holds no body; nor, since revision 891, does a 2xx
response to CONNECT, whatever its head says of one, its connection a tunnel from the end of its head - WinINet keeps
its body as one buffer of no byte (FX-HTTP-010) - where before it was read as a body that ran until its connection
closed. A head declaring a transfer coding
is not compared: WinINet keeps a chunked response's body without its chunk framing (`contracts/content-v1.md` §5.1,
FX-HTTP-003), so that body's end is its last buffer's, and no other transfer coding, nor a request's, was measured.
Otherwise its Content-Length - one length, or a list of the same one (RFC 9110 §8.6) - is compared: a body holding all
of it agrees; one holding fewer bytes, more, or none disagrees, and so does a body where its head declares none; a
response's whose request's method was not decoded, holding none, could be a response to HEAD, and is not said to
disagree. Nor is a body in a content coding other than identity that holds some other length, though recorded (revision
859): WinINet keeps a body in the gzip coding as its client read it - coded, or decoded where the client had WinINet
decode it - under a head declaring the coded length either way, and no record says which (FX-HTTP-004), so such a body
is said to be possibly the decoded one. Since revision 889 so is a body in deflate or br, which WinINet keeps alike
(FX-HTTP-008); and a deflate body holding no byte is said to be possibly one WinINet could not decode, which it keeps as
one buffer of no byte, whole by its ends, as it kept every body in the zlib format RFC 9110 names deflate, decoding only
deflate written as RFC 1951's raw data. A body in any other coding, which was not measured, is said not to be compared.
A response declaring no length ran until its connection closed. A head not decoded to the empty
line that ends it declares nothing known of its body. A disagreement is what a body's buffers cannot show: buffers
whose places and ends make a body whole can hold fewer bytes than its head declared. The window says each body's
declaration beneath it, a disagreement in the caution colour; `icat exchanges --decode` says each disagreement beneath
its process's exchanges, and nothing of a body that agrees.

## 5f. The listed exchanges as a table, and their heads written when asked (revision 783)

`icat exchanges --decode`, asked with `--json` or `--output`, writes `http-exchange-heads-v1` (ADR-071): the
`http-exchanges-v1` document of §5 under that name, with `content`, saying it holds what heads say, decoded from
content kept with consent to inspect it, to be shared only as that content would be; `decoder`, the decoder that read
them - its `id`, `name`, `version` and what it `reads`; and on each listed exchange `heads`: `asked`, `host` and
`answered` as §5c lists them; its `request` and `response` decodings, each null where no buffer of that head was
recorded, else its `part`, the bytes it `read` (`first` and `last`, null where it read none), its `fields` (each
`name`, `value`, and `first` and `last` byte in the head), `notDecoded` and `reachedEnd` (`contracts/decoders-v1.md`
§2); and `requestBody` and `responseBody`, what each head declares of its body (§5e: `statement` and `contradicts`),
null where the head was not decoded; and since revision 890 `redirect`, where its response redirects (§5g) - its
`status`, its `location` as its head gave it, the `target` and `host` a request there names, each null where it could
not be resolved, and `followedBy`, the listed exchange that followed it, by `key` and `number`, null where none listed
did - null where it does not; since revision 891 `proxy`, how its request went to a proxy (§5h) - its `kind`,
`request` or `tunnel`, and its `statement` - null where it went to none; and since revision 892 `challenge`, where its
response challenges for credentials (§5i) - its `status`, the `field` that makes it, its `challenges` as given, the
`answer` field a request answering it carries, and `answeredBy`, the listed exchange that answered it, null where none
listed did - and `credentials`, where its request carries them - their `field`, their `scheme` and a `statement`,
never their value - each null where there is none. A file written says beside it that it holds content. Without
`--decode` none of these is written, not even as null: `http-exchanges-v1` never holds a head's value.

`--output` writes JSON; `--format csv`, or a path ending `.csv`, writes the listed exchanges as a table instead, one
line per exchange in the order listed after a header naming its columns: `session_id`, `generation`, `pid`, `process`,
`process_key`, `unattributed`, `exchange_key`, `exchange`, `first_seconds`, `duration_ns`, `recorded_whole`, and for
each part - `request_head`, `request_body`, `response_head`, `response_body` - its `_buffers`, `_bytes` and `_whole`.
With `--decode` it adds `decoder`, `decoder_version`, `method`, `target`, `request_version`, `host`, `status`,
`reason`, `response_version`, `request_head_decoded_to_end` and `response_head_decoded_to_end`,
`request_head_not_decoded` and `response_head_not_decoded`, where a head no buffer of which was recorded says so,
each body's `_declared` statement and `_contradicts`, and since revision 890 `redirect_location`, where a response
redirects, and `redirect_followed_by`, the number of the listed exchange that followed it (§5g), since revision 891
`proxy`, the kind of its going to a proxy (§5h), and since revision 892 `challenge`, its challenges as given,
`challenge_answered_by`, the number of the listed exchange that answered it, and `credentials`, the scheme of the
credentials its request carries (§5i). Numbers are
invariant, flags `true` or `false`, and what is not known is empty. A cell holding a comma, a quote or a line end is
quoted, its quotes doubled; a text cell beginning
with `=`, `+`, `-`, `@`, a tab or a carriage return begins with an apostrophe, so a spreadsheet shows it rather than
evaluates it. `--format` needs `--output`, and a table needs listed exchanges: `--pid`, or `--exchanges` with a
number. The figures are read as without `--decode`, and nothing written is read back.

Since revision 784 the window writes the same table, with the heads, from a process's HTTP exchanges: Export with
their heads… says first what the table holds and that it is content, read from content kept with consent to inspect
it, and only then asks for its file. It holds every exchange the rung lists in its scope, in the order listed, as an
export of the rung does, up to the records an evidence export holds, and says when that limit left exchanges out and
how many heads were not decoded. It writes no JSON: the command line's document is the one a program reads.

## 5g. Where a response redirects (revision 890)

Where an exchange's heads are decoded (§5c, §5d, §5f), a response of status 301, 302, 303, 307 or 308 whose head was
decoded to its end and names one Location redirects there. Its location is resolved as a request made there names it
(RFC 3986 §5): an absolute address names its own target and host; any other is resolved against its request's target
and the host its request named - a reference to a path needing the request's target, a reference beginning with a
slash only the host - and what cannot be resolved is null. A head naming no Location, or several, redirects nowhere
known, and so does a response of any other status.

WinINet follows a redirect by itself, and its capture keeps the request it makes to the location as an exchange of its
own, numbered apart and begun once the redirect's response ended (FX-HTTP-009, §2): a POST answered 301, 302 or 303 it
asks there again as a GET of no body, and one answered 307 or 308 with the same method and body, but only once its
client confirms the redirect, failing the send with ERROR_HTTP_REDIRECT_NEEDS_CONFIRMATION until then. No record ties
the two exchanges, so a listing names as the one that followed a redirect the first exchange listed after it, begun
once its response ended, that asked for its location of its host: its request's target and host resolved as a
location is, its dot segments and its scheme's own port aside, and the target alone where a host is known of neither.
FX-HTTP-009's 128 redirects, eight clients at once, were each followed so by its own request there. Where two
redirects send their requests to one address at once, each names the first to ask for it once it ended, which may be
the other's; one that no listed exchange followed - one its client did not confirm, or whose follow-up is past the
listing - names none.

`icat exchanges --decode` says, beside a redirect's answer, the exchange that followed it, or its location where none
listed did, and says among its caveats, once, what WinINet does with a redirect and how its follow-up is found; its
JSON and its table give each redirect (§5f). The window's conversation says, beneath a redirect's answer, where it
redirects and what WinINet does with a redirect; it reads one exchange, and names no follow-up.

## 5h. Requests through a proxy (revision 891)

Where an exchange's heads are decoded (§5c, §5d, §5f), a request whose target is an absolute HTTP or HTTPS address
went to a proxy, as only a request to one names its target so (RFC 9112 §3.2.2): its `kind` is `request`, and it says
it asked a proxy for that address, which WinINet keeps as it sent it there. A CONNECT asked a proxy to open a tunnel
(RFC 9110 §9.3.6): its `kind` is `tunnel`, and it says so, and that WinINet keeps the requests it then sends through the
tunnel as over TLS directly, each an exchange of its own that no record ties to the CONNECT. Any other request went to
no proxy that is known - a request sent through a tunnel is in origin form, as one sent to its origin is - and a request
line not decoded says nothing of one.

FX-HTTP-010 measured both, with the workload's own server as the proxy: over HTTP WinINet sent each request to the proxy
in absolute form, with Proxy-Connection and Pragma fields, and its capture kept the head as sent; over TLS WinINet asked
the proxy to CONNECT before every request, naming HTTP/1.0 and declaring an empty body, and the capture kept the CONNECT
as an exchange of its own, its answer's body as one buffer of no byte (§5e), numbered apart from the request it opened
the tunnel for - eight clients' requests took the first numbers, their CONNECTs the next - though each CONNECT was
recorded before its request.

`icat exchanges --decode` lists a request to a proxy by what it asked for - its whole address, or the host and port a
CONNECT named - and says among its caveats, once, what going to a proxy means for what was kept; its JSON and its table
give each request's kind (§5f). The window's conversation says, beneath a request's head, how it went to a proxy and
what was kept.

## 5i. Challenges for credentials, and credentials carried (revision 892)

Where an exchange's heads are decoded (§5c, §5d, §5f), a 401 response whose head, decoded to its end, names
WWW-Authenticate challenges its client for credentials, which a request answers in an Authorization field; a 407
naming Proxy-Authenticate challenges for a proxy's, answered in Proxy-Authorization (RFC 9110 §11.6, §11.7). Its
challenges are the field's values as given, every one; a head naming none, or one empty, challenges for nothing known.
A request whose head names Authorization or Proxy-Authorization with a value carries credentials: said by the field and
the scheme, the value's first token, never the value - though the decoded head beside it holds the value, as a decoding
holds every field's. Basic credentials (RFC 7617) are a user name and password, encoded, not encrypted, and are said to
be readable by anyone reading the heads.

FX-HTTP-011 measured that WinINet answers a Basic challenge by itself where its client gave it credentials when it
connected, and sends the request again once a client that gives them only when challenged sets them and sends it; its
capture keeps the request challenged and the one sent with credentials as an exchange each, numbered apart, the answer
begun once the challenge's response ended. Once a challenge was answered WinINet sent the credentials with every later
request to that server unasked: of eight clients' 64 requests, only each client's first was challenged. No record ties
a challenge to its answer, so a listing names as its answer the first exchange listed after it, begun once its response
ended, that asked for its target of its host carrying the field that answers it - its target and host resolved as a
redirect's location is (§5g), a CONNECT's by the host and port it named - and each of FX-HTTP-011's 16 challenges was
answered so by its own request.

FX-HTTP-012 measured a proxy's challenge (revision 893): WinINet met it as a server's, answering a 407 by itself where
its client gave it the proxy's credentials, or once a client that gave them only when challenged sent the request
again, and sending them in Proxy-Authorization with every later request or CONNECT unasked; over HTTP the proxy
challenged a request sent it whole, over TLS a CONNECT, and a request through a tunnel carried no credentials. Its
capture kept each challenge and its answer as an exchange each, so a challenged CONNECT is answered, in a listing, by
the first CONNECT after it to the same host and port carrying the proxy's credentials. A client sending a request
again over TLS once challenged met WinINet's check of the new tunnel's certificate anew, and the client's reading of
the 407's body raised one buffer of no byte under the request's own number, which is an exchange holding nothing else.

`icat exchanges --decode` says, beside a challenge's answer, the listed exchange that answered it, and says among its
caveats, once each, what WinINet does with a challenge and how its answer is found, and that heads holding credentials
are to be shared only as those credentials would be; its JSON and its table give each challenge and each request's
credentials (§5f). The window's conversation says, beneath a response, the challenge it makes and what WinINet does with
one, and beneath a request, the credentials it carries; it reads one exchange, and names no answer.

## 6. Not defined at this version

- An exchange's method, target, status or headers in `http-exchanges-v1`: they are content, which only a decoding
  asked for reads, and only `http-exchange-heads-v1` writes, asked for with them (§5c, §5d, §5f,
  `contracts/decoders-v1.md` §2).
- How WinINet's capture keeps a body in a content coding other than gzip, deflate and br (§5e), and an exchange past
  authentication in a scheme other than Basic - NTLM and Negotiate, which take several exchanges and the user's own
  credentials, among them. A proxy's Basic challenge is measured since revision 893 (FX-HTTP-012, §5i). Asynchronous
  WinINet is measured since revision 886
  (FX-HTTP-005), HTTP/2 since revision 887 (FX-HTTP-006), whose heads no record tells from HTTP/1.1's, a request body
  written in pieces since revision 888 (FX-HTTP-007), deflate and br since revision 889 (FX-HTTP-008), redirects
  WinINet follows since revision 890 (FX-HTTP-009, §5g), requests through a proxy since revision 891 (FX-HTTP-010,
  §5h), and Basic authentication since revision 892 (FX-HTTP-011, §5i).
