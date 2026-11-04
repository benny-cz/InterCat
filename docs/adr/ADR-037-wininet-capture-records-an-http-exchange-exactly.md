# ADR-037: WinINet's capture provider records an HTTP exchange exactly, for the processes a capture names

- Status: accepted as M3's content-capable source; measured in the lab (revision 237); in the catalog, its records bound
  to their client as `process-binding-v4` (revision 238); admitted under a Content request, through `icat record`
  (revision 239); HTTPS measured, and kept as its plaintext (revision 243); exchanges at once and chunked responses
  measured, and an exchange's number bound to its use in time (revision 244); its scope held to the named processes
  themselves (revision 245); a process's exchanges on its rung (revision 247, `contracts/http-exchanges-v1.md`);
  asynchronous clients measured, and the thread each record is raised on (revision 886); HTTP/2 measured, its heads kept
  in HTTP/1.1's form (revision 887); a request body written in pieces measured (revision 888); responses in deflate and
  br measured (revision 889); redirects WinINet follows measured, each leg an exchange of its own (revision 890);
  requests through a proxy measured, a CONNECT an exchange of its own (revision 891)
- Date: 2026-09-28
- Decision owners: InterCat maintainers
- Relates to: §3.7, §11, §11.1, §11.2, M3, M8, I21, R21, R22, ADR-030, ADR-036, FX-HTTP-001, FX-HTTP-002,
  FX-HTTP-003, FX-HTTP-005, FX-HTTP-006, FX-HTTP-007, FX-HTTP-008, FX-HTTP-009, FX-HTTP-010, `contracts/content-v1.md`,
  `contracts/decoders-v1.md`, `contracts/http-exchanges-v1.md`,
  `tools/InterCat.WinInetProbe`,
  `bench/results/wininet-capture-feasibility-20260928T102426Z`,
  `bench/results/wininet-capture-feasibility-tls-20260928T120408Z`,
  `bench/results/wininet-capture-interleaved-20260928T123007Z`, `bench/results/wininet-capture-chunked-20260928T123023Z`,
  `bench/results/wininet-capture-numbering-20260928T123112Z`,
  `bench/results/wininet-capture-async-paced-eight-chunked-20261103T210230Z`,
  `bench/results/wininet-capture-sync-paced-20261103T204528Z`,
  `bench/results/wininet-capture-http2-20261103T214430Z`,
  `bench/results/wininet-capture-http2-paced-eight-20261103T215057Z`,
  `bench/results/wininet-capture-written-20261103T223930Z`,
  `bench/results/wininet-capture-written-eight-20261103T223954Z`,
  `bench/results/wininet-capture-br-decoded-20261104T003209Z`,
  `bench/results/wininet-capture-deflate-decoded-20261104T003223Z`,
  `bench/results/wininet-capture-deflate-raw-decoded-20261104T003232Z`,
  `bench/results/wininet-capture-redirect-302-eight-20261104T013021Z`,
  `bench/results/wininet-capture-redirect-307-eight-20261104T013026Z`,
  `bench/results/wininet-capture-proxy-http-eight-20261104T023404Z`,
  `bench/results/wininet-capture-proxy-tls-eight-20261104T023409Z`

## Context

M3 asks for at least one validated content-capable source or import path, and M8 for a source with validated
direction, byte-range and message identity, and length semantics. Revision 233 found none among the sources InterCat
admits: the kernel's network events carry a transfer's size, RPC's a call's interface and procedure, ALPC's a message
id. Packet captures carry frames, but not of loopback traffic, and a frame is a transport fragment of a stream they do
not reassemble. Revision 235 carried content end to end from InterCat's own fixture, which proves the path and not a
source.

Windows' HTTP client library, WinINet, has a capture provider of its own, Microsoft-Windows-WinINet-Capture
(`a70ff94f-570b-4979-ba5c-e59c9feab61b`). Its registered schema declares four events, 2001 to 2004 - "the WinINet request
header / request payload / response header / response payload buffer captured" - each carrying `SessionId`,
`SequenceNumber`, `Flags`, `PayloadByteLength` (four 32-bit fields) and then that many bytes of `Payload`: the layout
revision 235's content slot admits. On this workstation the provider is loaded by the user's browser, cloud clients and
chat application, so whatever it records is someone's HTTP traffic, headers included.

## Measurement

`tools/InterCat.WinInetProbe` read the schema as the product's inventory reads it, then ran FX-HTTP-001: seeded HTTP/1.1
POSTs through WinINet to a server the workload runs on loopback, which logs the exact bytes of every request and
response head and body by length and SHA-256, never the bytes. Bodies ran from empty to 256 KiB in both directions. One
uniquely named real-time session of the probe's own enabled the provider with a process filter
(`EVENT_FILTER_TYPE_PID`) naming the workload alone, while a decoy ran the same exchange at the same time outside it.
The captured bytes were compared in memory and never written. Four runs: two of 16 requests with bodies to 96 KiB,
and two of 64 with bodies to 256 KiB, the last beside the decoy.

- **Every part is exact.** In every run, each of the four parts - request head, request body, response head, response
  body - concatenated from its buffers in order was the wire's, by length and SHA-256, for every exchange: 16 of 16 and
  64 of 64. No record was lost, and no buffer's `PayloadByteLength` differed from the bytes it carried.
- **Direction and part are the event.** 2001 and 2002 are what the client sent, 2003 and 2004 what it read.
- **Identity and order are stated.** One `SessionId` per exchange, none shared by two; `SequenceNumber` numbers a part's
  buffers from 0 in order, and `Flags` marks the first (1) and the last (2), or both (3) - 255 of 255 parts in each
  64-request run.
- **Boundaries are stated.** A head is always one buffer. A body ends with its last-flagged buffer, which was empty
  after every non-empty body; an empty request body raised no record, an empty response body one empty buffer.
- **Buffers stay under ETW's limit.** A sent body is cut into buffers of at most 64,511 bytes, 6 at most for 256 KiB; a
  read body arrives in the application's own reads, here 16 KiB, 17 at most.
- **The client raised every record, and the filter holds.** 444 of 444 records were raised in the workload's process;
  the decoy completed its 64 exchanges alongside and none of its records reached the session.

## Measurement over TLS (revision 243)

FX-HTTP-002 is the same exchange over TLS 1.2 or 1.3. The loopback server presents a self-signed certificate made in
memory for the run and installed in no store; the client tells WinINet to accept that certificate's errors for its own
requests alone; and the server's truth is the bytes above the encryption, what it decrypted and what it encrypted. The
probe ran 16 exchanges with bodies to 96 KiB beside a decoy, as before.

- **The capture holds the plaintext.** Every part, 64 of 64, matched the server's decrypted bytes by length and SHA-256,
  and no buffer began as a TLS record does. WinINet raises these records above the encryption: the bytes the client
  handed it to send, and the bytes it read back decrypted.
- **Nothing in a record says the exchange was encrypted.** The layout, the flags (the same 1, 2 and 3 at a part's
  ends), the numbering and the buffering (sent bodies in buffers of at most 64,511 bytes, read bodies in the
  application's 16 KiB reads) were plain HTTP's, and a request head names neither a scheme nor what its port means.
- **The client raised every record, and the filter holds**, as before: 99 of 99 records were the workload's, none of
  the decoy's 16 exchanges reached the session, and nothing was lost.
- **The impact stays Low.** Seven pairs of 4,096 exchanges with bodies to 128 KiB, as revision 239's, now over TLS: a
  median 0.74 CPU pp, and no measurable cost to the workload's time (a median of -0.89%, within the machine's noise);
  179,277 records and 640 MB copied, nothing lost (`bench/results/wininet-capture-impact-tls-20260928T121427Z`).

## Measurement of exchanges at once, and of chunked responses (revision 244)

FX-HTTP-003 runs eight WinINet clients in one process, each on its own connection handle and taking every eighth
request, with WinINet's per-server connection limit raised to eight for that process alone; and its server can answer
in chunked transfer coding, in seeded chunks of 1 to 9,000 bytes, logging each response body twice - the body a client
reads, and the framed bytes the wire carried. The probe matched each exchange with its request by the path its head
names, not by time.

- **Exchanges at once stay apart.** 256 exchanges eight at once, 805 of their buffers following another exchange's:
  each exchange kept one `SessionId` of its own, each part was numbered from 0 and flagged at its ends, and all 1,024
  parts matched. 2,048 exchanges eight at once did the same, 8,192 of 8,192.
- **An exchange's number is its client process's own count.** The 2,048 exchanges were numbered 1 to 2,048: WinINet
  counts a process's exchanges from 1. No number recurred within the process, but every client process counts from 1,
  so a number names an exchange only within one run of its client.
- **A chunked body is kept as its client read it.** All 64 chunked response bodies matched the body the client read,
  and none the framed bytes: WinINet raises a read buffer after taking the framing away, and only the response head,
  which says `Transfer-Encoding: chunked`, shows the body was chunked.
- **Through `icat record`** (FX-HTTP-003: 256 exchanges eight at once, chunked): 1,540 buffers kept whole, 254 of the
  exchanges begun before an earlier one ended; all 1,024 parts matched and none the framing, and `icat content --part`
  saved a 98,304-byte chunked body from 8 buffers that matched.

## Measurement of asynchronous clients (revision 886)

FX-HTTP-005 makes the exchange through WinINet asynchronously: its client opens its session with `INTERNET_FLAG_ASYNC`,
and each send and read WinINet does not finish at once fails with `ERROR_IO_PENDING` and completes later on one of
WinINet's threads, which the status callback reports; the bytes it sends and the buffer it reads into stay where they
are until then. The server can pace a response, waiting 5 ms before each 16 KiB of its body, so a read finds no byte
waiting and waits for it. The probe ran it nine times beside a decoy - 16 exchanges, or 64 eight at once, paced or
not, chunked, over TLS and in gzip decoded by WinINet - and once paced and synchronous, as a control; the workload names
the threads that made its requests.

- **The records are a synchronous client's.** Every part of every exchange matched the server's truth, 64 of 64 and 256
  of 256; every part was numbered from 0 and flagged at its ends; sent bodies were cut into buffers of at most 64,511
  bytes and read bodies came in the client's reads of 16 KiB; nothing was lost, and none of the decoy's records reached
  the session. Every send waited for WinINet; a read finished at once where its bytes were waiting, and waited where the
  server's pacing held them back - 70 of 133 reads in a run eight at once.
- **A record is raised on whichever thread finished its step.** Every head and every sent body's bytes were raised on
  one of WinINet's own threads, never on the thread that made the request; a read's bytes on the reading thread where
  they were waiting, and on one of WinINet's where the read waited for them; the empty buffer that ends a request body -
  raised after its response ended, 63 of 64 eight at once - and the one that ends a response, on the thread that read
  the response's end. The synchronous control split its records between the threads exactly as its asynchronous twin
  did, 31 and 68 of 99: a synchronous call is WinINet's work on its own threads, waited for. A record's header thread is
  therefore not the thread that made the request, in either mode.
- **Through `icat record`** (FX-HTTP-005: 64 exchanges one at a time, paced and not, and 64 eight at once with chunked
  responses, 56 of them begun before an earlier one ended): every record bound to the workload, every exchange recorded
  whole, every head decoded to its end, and every part `icat content --part` saved matched the server's bytes, 254 of 254
  that held any; no body was said to disagree with its head. The ledger of the run eight at once, each buffer's place,
  ends, length and kind of thread but never its bytes, is kept with the fixture and grouped by a test as a session's
  records are.

## Measurement over HTTP/2 (revision 887)

FX-HTTP-006 makes the exchange over HTTP/2: its client enables HTTP/2 on its WinINet session, and its server - Kestrel
on loopback, since HTTP/2 frames every message and compresses its fields - offers HTTP/2 and HTTP/1.1 by ALPN over TLS
with the run's certificate. Both said every request went over HTTP/2. HTTP/2 carries no head as bytes - the request
line travels as pseudo-header fields, and every field compressed - so the server's truth names each request's fields
as it decoded them, and each body as it read and wrote it. The probe ran it seven times beside a decoy: 16 exchanges,
paced or not, asynchronous, in gzip read coded and decoded, and 64 eight at once.

- **The bodies are as over HTTP/1.1.** Every body was the server's: each request's as it read it, each response's as
  the client read it - the coded bytes or the body before its coding, as FX-HTTP-004 found over HTTP/1.1 - in the same
  buffers, cut at 64,511 bytes sent and read in the client's 16 KiB reads, every part numbered from 0 and flagged at its
  ends, nothing lost and none of the decoy's records in the session.
- **The heads are HTTP/1.1's form, which no wire carried.** A request head is a request line naming HTTP/1.1 above the
  fields WinINet sent, named as it names them, with no Connection field, which HTTP/2 forbids; a response head is a
  status line naming HTTP/1.1 and no reason, which HTTP/2 does not carry, above the fields received, named in lower case
  as HTTP/2 names them. Each request head held exactly the fields the server decoded, 64 of 64. No record says the
  exchange went over HTTP/2.
- **Exchanges multiplexed stay apart.** Eight clients, each on a connection handle of its own, sent their 64 exchanges
  over one HTTP/2 connection, by the server's account: each kept its own number, 222 buffers followed another
  exchange's, and every part matched.
- **Through `icat record`** (FX-HTTP-006: 64 exchanges one at a time, and 64 eight at once on one connection): every
  record bound to the workload, every exchange recorded whole, every head decoded to its end - 128 of 128 naming
  HTTP/1.1, every response's with no reason - and every body `icat content --part` saved matched the server's bytes,
  126 of 126 that held any; no body was said to disagree with its head.

## Measurement of a request body written in pieces (revision 888)

Every client measured before handed `HttpSendRequest` its body whole. FX-HTTP-007's client writes it after the head
instead: `HttpSendRequestEx` sends the head and declares the body's length, `InternetWriteFile` writes the body in
seeded pieces of 1 to n bytes, and `HttpEndRequest` ends the request; the client logs each piece by length and SHA-256,
in order. The probe ran it six times beside a decoy: pieces of up to 80,000 bytes of bodies to 256 KiB and of up to
2,000 bytes, each synchronously and asynchronously, the first over HTTP/2 too, and pieces of up to 4,096 bytes eight
clients at once.

- **A buffer to a write.** Each body's buffers that hold bytes were the pieces its client wrote, by length and in
  order - 424 of them in the run eight at once, 103 in one body - and a piece past 64,511 bytes was cut there, as a body
  handed over whole is; every body then ended with an empty buffer flagged last, as before. Every part was numbered from
  0 and flagged at its ends, every part matched the server's truth, nothing was lost and none of the decoy's records
  reached the session.
- **Through `icat record`** (FX-HTTP-007: 64 exchanges with bodies to 256 KiB in pieces of up to 80,000 bytes, and 64
  eight at once in pieces of up to 4,096): every record bound to the workload, every exchange recorded whole, each of
  the 63 bodies of each run in as many buffers as its writes made and one to end it, and every part `icat content
  --part` saved matched the server's bytes, 254 of 254 that held any.

## Measurement of responses in deflate and br (revision 889)

FX-HTTP-004 measured gzip alone. FX-HTTP-008's server codes every response body in br, Brotli, or in deflate - as the
zlib format RFC 9110 names deflate, or as the raw data of RFC 1951 that servers which read deflate so write under the
same name - declared by Content-Encoding beside the coded length; the client accepts the coding and reads each body as
it came, or has WinINet decode it first. The probe ran each of the five ways, br chunked too, beside a decoy.

- **As its client read it, as gzip's.** Read coded, every body kept was the coded bytes, 16 of 16 in each coding; where
  the client had WinINet decode it, every br body and every raw deflate body kept was the body before its coding, 16 of
  16, under a head kept as the wire carried it, declaring the coded length. No record says which the client did.
- **A body WinINet cannot decode is kept as no byte.** WinINet decodes deflate only as raw data: asked to decode the
  zlib format, every read failed with `ERROR_INTERNET_DECODING_FAILED` (12175), and the capture kept each response body
  as one buffer of no byte, flagged first and last - whole by its places and ends, and alike to a body decoded to
  nothing.
- **Through `icat record`** (FX-HTTP-008: 64 exchanges each, decoded by WinINet in br, raw deflate and the zlib
  format): every exchange recorded whole and every head decoded to its end; each br and raw deflate body `icat content
  --part` saved was the body before its coding, 63 of 63 that held any, and each zlib body was kept as no byte. So the
  comparison revision 778 added sets a body in deflate or br beside its head as one in gzip: one holding its declared
  length agrees, one holding another is not said to disagree, and a deflate body holding no byte is said to be
  possibly one WinINet could not decode.

## Measurement of redirects (revision 890)

Every server measured before answered each request at once. FX-HTTP-009's server answers each first with a redirect of
no body - 301, 302, 303, 307 or 308 - to the request's path moved, and logs each leg apart; WinINet follows it by
itself. The probe ran each status, 307 over TLS and asynchronously too, and 302 and 307 eight clients at once, beside a
decoy.

- **Two exchanges, each of its own.** The request the server redirected and its redirect - a head and an empty body,
  whole - were kept under one exchange number, and the request WinINet made to the location and its answer under
  another, every time: numbered apart, 3 to 15 later eight clients at once, and begun once the redirect's response had
  ended. No record ties the two. Every part of both legs matched the server's truth of its leg, nothing was lost and
  none of the decoy's records reached the session.
- **What WinINet asks there.** After a POST answered 301, 302 or 303 it asked the location with GET and no body,
  keeping the request's other fields in their order. After 307 or 308 it failed the send with
  `ERROR_HTTP_REDIRECT_NEEDS_CONFIRMATION` (12168), as its dialog would ask a person; the client confirmed it by sending
  the request again, and WinINet made the same POST, body and all, to the location - the client's own field then sent
  after WinINet's.
- **Through `icat record`** (FX-HTTP-009: 64 requests eight at once, redirected by 302 and again by 307): 128
  exchanges each, every one recorded whole, every head decoded to its end, every part as long as the server's truth of
  its leg, and every part `icat content --part` saved matching the server's bytes, 382 of 382 and 445 of 445 that held
  any. So the grouping needs nothing new; and since no record ties a redirect to the request made there, a listing of
  decoded heads names as its follow-up the first exchange listed after it, begun once its response ended, that asked
  for its location of its host - in both runs, each of the 128 redirects its own request there.

## Measurement through a proxy (revision 891)

Every client measured before reached its server directly. FX-HTTP-010's client names a proxy for its WinINet session -
the workload's own loopback server, which is the origin too - and asks for `fx.invalid`, a host no resolver knows, so
every request goes to the proxy. The probe ran it over HTTP and over TLS, asynchronously too, and both eight clients at
once, beside a decoy.

- **Over HTTP, as sent to the proxy.** Each request head was kept as WinINet sent it there: its target the whole
  address, `POST http://fx.invalid/fx/n`, with Proxy-Connection and Pragma fields where a direct request has Connection
  and Cache-Control. Every part matched what the proxy received.
- **Over TLS, a CONNECT of its own.** Before every request WinINet asked the proxy to CONNECT a tunnel to
  `fx.invalid:443`, its request line naming HTTP/1.0 and its head an empty body, and the capture kept each CONNECT as an
  exchange of its own: its head, the proxy's answer of no field, and one buffer of no byte for the answer's body, each
  whole. The
  request WinINet then sent through the tunnel was kept as over TLS directly, in origin form - another exchange, under a
  number of its own, which no record ties to the CONNECT. Eight clients at once, their requests took the first numbers
  and their CONNECTs the next, though each CONNECT was recorded before the request it opened the tunnel for.
- **Through `icat record`** (FX-HTTP-010: 64 requests eight at once, over HTTP and over TLS): every exchange recorded
  whole, 64 and 128 of them, every head decoded to its end, and every part `icat content --part` saved matching the
  server's bytes, 254 of 254 that held any in each, and each CONNECT's head and answer its log, 128 of 128. The grouping
  needs nothing new. Revision 778's comparison read a CONNECT's answer, which declares no length, as a body that ran
  until its connection closed; a 2xx answer to CONNECT holds no body, its connection a tunnel from its head's end (RFC
  9112 §6.3), and is now said so.

## Decision

1. **The source contract, for its admission to implement.** An exchange is a `SessionId`; a part is its event, which
   also gives its direction; its bytes are its buffers in `SequenceNumber` order, from the buffer flagged first to the
   one flagged last; each buffer's length is its `PayloadByteLength`. A part without its last-flagged buffer is
   incomplete and is said to be, never shown as whole (I21).
2. **Scoped before persistence, always.** The provider is enabled only for the processes a capture names, by the
   session's process filter, which keeps every other process's records out of the session itself. A request without a
   process scope is refused: unscoped, the capture would record every WinINet client's requests on the machine,
   cookies and authorization headers among them. The filter holds process IDs, so the capture holds each named process
   open while its session lives: Windows gives no process an ID while a handle to the process that had it is open, so a
   named process that exits keeps its ID to the end and no other process's records can enter through it. A process
   that is not running, or has exited, when the provider is enabled is refused (R22, revision 245).
3. **What the bytes are.** Heads and bodies are `ApplicationPayload`: the HTTP messages as the client sent and read
   them, as WinINet held them. Their encoding is binary, as the schema declares it; a head is ASCII by HTTP's own rules,
   which is a reading of the bytes, not a declaration of the source, and is not claimed (content-v1 §1).
4. **Whose records they are.** The records name no process; each is raised in the client process that made the
   exchange - 444 of 444 here. Binding them by their header's process is ADR-030's rule reaching a new mechanism, which
   changes what `process-binding-v3` bound (`contracts/entities-v1.md` §7); revision 238 binds them so, as
   `process-binding-v4`, under a new mechanism, `Http`. Their header's thread is not the client's: WinINet raises each
   on whichever of the process's threads finished the step it records, for a synchronous client as for an asynchronous
   one (revision 886), so no grouping reads a record's thread, and none is said to be the thread that made a request.
5. **Admitted under a request.** Revision 239 measured its overhead - Low, a median 0.71 CPU pp and 2.5% of the
   workload's time over seven pairs - and admits it under a bounded Content request that names its processes and, since
   the source cannot select channels, every channel of theirs (`*`). `icat record --profile content` records it; the
   broker never starts one, since its follower does not mirror content. (Since revision 444 a follow mirrors it,
   ADR-047, and `icat record --evidence-only` keeps it; the broker still prepares the metadata-only policy alone.)
6. **What a record says of encryption: only what it knows.** A record's bytes are the application's message above any
   encryption of its connection, and are stated so; a record is never said to have crossed the wire in the clear, nor
   encrypted, since nothing in it says which. Because an HTTPS exchange is kept as its plaintext, headers, cookies and
   authorization included, a request says so before anything is recorded (§11.1), beside its process scope. The
   viewer says the same of every application payload (revision 243).
7. **An exchange is its number within one run of its client, told apart in time.** Since the number restarts with
   each client process, it names nothing across processes, nor across a process ID used again by another process, or
   WinINet loaded again in one. InterCat finds a part among the buffers of its number, event and process ID and tells
   two uses of the number apart in time: a buffer flagged first, one after a buffer flagged last, or one numbered no
   later than the buffer before it opens another use. A part is its record's use, never two merged into one (R22,
   revision 244). A body is what its client read: a chunked body without its framing.
8. **A process's exchanges are a row of its rung** (revision 247, `http-exchanges-v1`). Grouped from buffers' source
   fields and metadata alone, a use of a number opens at a request head flagged first, or at a buffer that repeats a
   place the use already holds; nothing else ends it, since WinINet raises the empty buffer that ends a request body
   after the response has ended (a live content capture of 96 exchanges read as 191 before this was known). An
   exchange's duration runs from its first buffer to the one that ended its response.
9. **What a head says of its protocol: only WinINet's form** (revision 887). WinINet keeps an exchange it made over
   HTTP/2 with heads in HTTP/1.1's form, so the version a head names is WinINet's, and nothing kept says which protocol
   carried an exchange. A decoded head's version is never said to be its exchange's protocol: wherever heads are
   decoded - `icat content --part --decode`, `icat exchanges --decode`, the content viewer and an exchange's
   conversation - InterCat says so beside them (`contracts/decoders-v1.md` §4).
10. **A redirect's follow-up is named by what it asked for** (revision 890). WinINet keeps the request it makes to a
   redirect's location as an exchange of its own, which nothing kept ties to the redirect, and its number is no guide.
   Where heads are decoded, a response that redirects names its location, resolved as a request there names it, and a
   listing names the first exchange listed after it, begun once its response ended, that asked for that location of
   its host as the one that followed it - an inference from what was asked and when, said as such, never a record's
   claim; one no listed exchange followed names none (`contracts/http-exchanges-v1.md` §5g).
11. **A request to a proxy says so, by its own request line** (revision 891). Only a request to a proxy names its
   target's whole address (RFC 9112 §3.2.2), and a CONNECT asks a proxy for a tunnel (RFC 9110 §9.3.6), so where heads
   are decoded each says it went to a proxy, and how, and a listing says once what that means for what was kept: a
   CONNECT is an exchange of its own, and a request through its tunnel one of its own too, kept as over TLS directly,
   which no record ties to it - so nothing says which tunnel a request went through. A request in origin form says
   nothing of a proxy (`contracts/http-exchanges-v1.md` §5h).

## Consequences

- M3's content-capable source is a Windows source with every semantic M8 names measured: direction, byte ranges within
  a message, message identity and length, and completeness. It is scoped to named processes before anything is kept.
- HTTPS is measured (revision 243): WinINet holds the bytes before encryption and after decryption, so a capture holds
  an encrypted exchange's plaintext, and the request that keeps it says so first.
- Exchanges at once in one process, and chunked responses, are measured (revision 244): each exchange keeps its own
  number, and a chunked body is kept as its client read it.
- Responses in the gzip content coding are measured (revision 859, FX-HTTP-004): a coded body is kept as its client read
  it, coded where the client read the coded bytes and decoded where it had WinINet decode it, under the head the wire
  carried, which declares the coded length either way; no record says which the client did.
- Asynchronous clients are measured (revision 886, FX-HTTP-005): their exchanges are recorded as a synchronous
  client's, and in either a record is raised on whichever of the client's threads WinINet finished its step on.
- HTTP/2 is measured (revision 887, FX-HTTP-006): its bodies are kept as over HTTP/1.1 and its heads in HTTP/1.1's
  form, so a head's version is WinINet's and InterCat says so where heads are decoded; exchanges multiplexed on one
  connection keep their own numbers.
- A request body written in pieces is measured (revision 888, FX-HTTP-007): it is kept a buffer to a write, a write past
  64,511 bytes cut there, and ended by an empty buffer, as a body handed over whole is.
- Responses in deflate and br are measured (revision 889, FX-HTTP-008): each is kept as its client read it, as gzip's
  is, and a zlib-format deflate body WinINet cannot decode is kept as no byte.
- Redirects WinINet follows are measured (revision 890, FX-HTTP-009): each leg is an exchange of its own, the request
  made to the location numbered apart and begun once the redirect's response ended, and a listing of decoded heads
  names the one that followed a redirect.
- Requests through a proxy are measured (revision 891, FX-HTTP-010): one over HTTP is kept as WinINet sent it to the
  proxy, in absolute form, and over TLS the CONNECT before it is an exchange of its own, its answer of no body, and the
  request through the tunnel one of its own too.
- Not measured: content codings other than gzip, deflate and br; authentication. Each is measured before a profile
  claims it.
- The scope is the named processes themselves (revision 245): the provider's filter holds their IDs by number, and the
  capture holds each process open while it runs, so a named process's exit ends its content and no process started
  after it can take its ID.
- WinINet is one client library among several. .NET's HTTP client, WinHTTP and browsers' own stacks do not raise these
  records, so their exchanges stay without content, and the statement of what a record holds says which source could.
