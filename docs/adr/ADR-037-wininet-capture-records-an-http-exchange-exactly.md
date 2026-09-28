# ADR-037: WinINet's capture provider records an HTTP exchange exactly, for the processes a capture names

- Status: accepted as M3's content-capable source; measured in the lab (revision 237) and admitted to no profile yet
- Date: 2026-09-28
- Decision owners: InterCat maintainers
- Relates to: §3.7, §11, §11.2, M3, M8, I21, R21, ADR-030, ADR-036, FX-HTTP-001, `contracts/content-v1.md`,
  `tools/InterCat.WinInetProbe`, `bench/results/wininet-capture-feasibility-20260928T102426Z`

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

## Decision

1. **The source contract, for its admission to implement.** An exchange is a `SessionId`; a part is its event, which
   also gives its direction; its bytes are its buffers in `SequenceNumber` order, from the buffer flagged first to the
   one flagged last; each buffer's length is its `PayloadByteLength`. A part without its last-flagged buffer is
   incomplete and is said to be, never shown as whole (I21).
2. **Scoped before persistence, always.** The provider is enabled only for the processes a capture names, by the
   session's process filter, which keeps every other process's records out of the session itself. A request without a
   process scope is refused: unscoped, the capture would record every WinINet client's requests on the machine,
   cookies and authorization headers among them.
3. **What the bytes are.** Heads and bodies are `ApplicationPayload`: the HTTP messages as the client sent and read
   them, as WinINet held them. Their encoding is binary, as the schema declares it; a head is ASCII by HTTP's own rules,
   which is a reading of the bytes, not a declaration of the source, and is not claimed (content-v1 §1).
4. **Whose records they are.** The records name no process; each is raised in the client process that made the
   exchange - 444 of 444 here. Binding them by their header's process is ADR-030's rule reaching a new mechanism, which
   changes what `process-binding-v3` binds (`contracts/entities-v1.md` §7); the admission decides that rule's revision.
5. **Not yet a profile.** Like ADR-034's spike, this is a measurement: no profile admits the provider until its
   admission slice implements items 1 to 4 and measures its overhead.

## Consequences

- M3's content-capable source is a Windows source with every semantic M8 names measured: direction, byte ranges within
  a message, message identity and length, and completeness. It is scoped to named processes before anything is kept.
- Not measured: HTTPS, where WinINet holds the bytes before encryption and after decryption, so a capture would hold
  plaintext of an encrypted exchange; asynchronous WinINet; HTTP/2; chunked and compressed responses; redirects,
  proxies and authentication; several exchanges at once in one process; and the provider's overhead. Each is measured
  before a profile claims it.
- WinINet is one client library among several. .NET's HTTP client, WinHTTP and browsers' own stacks do not raise these
  records, so their exchanges stay without content, and the statement of what a record holds says which source could.
