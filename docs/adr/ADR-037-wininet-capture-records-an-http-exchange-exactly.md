# ADR-037: WinINet's capture provider records an HTTP exchange exactly, for the processes a capture names

- Status: accepted as M3's content-capable source; measured in the lab (revision 237); in the catalog, its records bound
  to their client as `process-binding-v4` (revision 238); admitted under a Content request, through `icat record`
  (revision 239); HTTPS measured, and kept as its plaintext (revision 243); exchanges at once and chunked responses
  measured, and an exchange's number bound to its use in time (revision 244); its scope held to the named processes
  themselves (revision 245); a process's exchanges on its rung (revision 247, `contracts/http-exchanges-v1.md`)
- Date: 2026-09-28
- Decision owners: InterCat maintainers
- Relates to: §3.7, §11, §11.1, §11.2, M3, M8, I21, R21, R22, ADR-030, ADR-036, FX-HTTP-001, FX-HTTP-002,
  FX-HTTP-003, `contracts/content-v1.md`, `tools/InterCat.WinInetProbe`,
  `bench/results/wininet-capture-feasibility-20260928T102426Z`,
  `bench/results/wininet-capture-feasibility-tls-20260928T120408Z`,
  `bench/results/wininet-capture-interleaved-20260928T123007Z`, `bench/results/wininet-capture-chunked-20260928T123023Z`,
  `bench/results/wininet-capture-numbering-20260928T123112Z`

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
   `process-binding-v4`, under a new mechanism, `Http`.
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

## Consequences

- M3's content-capable source is a Windows source with every semantic M8 names measured: direction, byte ranges within
  a message, message identity and length, and completeness. It is scoped to named processes before anything is kept.
- HTTPS is measured (revision 243): WinINet holds the bytes before encryption and after decryption, so a capture holds
  an encrypted exchange's plaintext, and the request that keeps it says so first.
- Exchanges at once in one process, and chunked responses, are measured (revision 244): each exchange keeps its own
  number, and a chunked body is kept as its client read it.
- Not measured: asynchronous WinINet; HTTP/2; compressed responses; redirects, proxies and authentication. Each is
  measured before a profile claims it.
- The scope is the named processes themselves (revision 245): the provider's filter holds their IDs by number, and the
  capture holds each process open while it runs, so a named process's exit ends its content and no process started
  after it can take its ID.
- WinINet is one client library among several. .NET's HTTP client, WinHTTP and browsers' own stacks do not raise these
  records, so their exchanges stay without content, and the statement of what a record holds says which source could.
