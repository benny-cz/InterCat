# ADR-070: A decoder reads a whole part only when asked, stops where its format does not hold, and is never kept

- Status: accepted; revision 774. It starts plan M8 with the HTTP/1.1 head decoder; decision 2 amended by ADR-071
  (revision 783)
- Date: 2026-10-24
- Relates to: plan M8, §11.2, I21, P2, ADR-036, ADR-037; `contracts/decoders-v1.md`, `contracts/content-v1.md`

## Context

Plan M8 turns kept fragments into navigable conversations without making them look more complete than they are.
Revision 242 reassembled an HTTP exchange's part from WinINet's buffers for a person, and revision 455 proved a
decoding's model - fields linked to the bytes they came from and to the decoder's version - on InterCat's own fixture.
The plan asks for a decoder contract first, a first real decoder chosen by validated fixtures and user value rather
than by name, and exit tests in which fragmented, malformed and truncated conversations never become falsely complete.
WinINet's capture is a validated content source: FX-HTTP-001 to FX-HTTP-003 found every kept part equal to the wire,
so a head part is exactly what the process sent or read.

## Decision

1. **A decoder contract** (`decoders-v1`): a decoder has an identity, a name, a version and what it reads; a decoding
   names them, the record and the part it read, the bytes read, each field with its bytes, and what it did not decode
   and why.
2. **Only when asked, only with consent, bounded, never kept**: a decoding is made when a person asks, from bytes kept
   with consent to inspect them, within 64 KiB and 250 ms, and is never kept, exported or searched.
3. **A part is decoded only whole.** A part missing a buffer, or holding one cut, is not decoded at all.
4. **The first real decoder reads HTTP/1.1 heads** as RFC 9112 defines them, from WinINet's request and response head
   parts. A line HTTP/1.1 does not allow there stops the decoding at its start, and nothing from it on is decoded.
5. **Values are inert**: a byte outside visible ASCII and the space is written as \xHH.
6. **In process, bounded**: decoders run in the process that asked, never in the capture; isolation in an unprivileged
   worker comes with a decoder whose parsing is larger than these.

## Why

- **A head says what an exchange is**: the method and target asked for, the status answered, the host, and the
  content's type and length; and WinINet's parts are measured equal to the wire, so its fields are the evidence
  itself.
- **A missing buffer could hold any line.** A head decoded around a gap could say a request had no Host it had, or
  give a field the gap cut short as whole: a partial decoding would read as a complete one (P2).
- **Stopping where the format does not hold** keeps every field said one the bytes hold as HTTP/1.1 reads them; a
  lenient parser's guess about a malformed line would be the decoder's words, not the evidence's.
- **Nothing kept, nothing stale**: a new decoder version changes nothing stored, and nothing decoded leaves the
  machine except as a person copies it.
- **A small bounded parser** reading at most 64 KiB of managed memory adds no more to what a session exposes than the
  hexadecimal view beside it.

## Consequences

- `icat content --part --decode`, a buffer's word on which part to decode, and `icat content --decoders`.
- Bodies, other mechanisms, conversations, exports, search and isolation are not defined at this version
  (`decoders-v1` §6); revision 775 builds the window's decoding of a part (`decoders-v1` §5a). Revision 776 sets each
  listed exchange's two decoded heads side by side (`icat exchanges --decode`), each still decoded alone. Revision 777
  does the same in the window, in an exchange's conversation. Revision 778 sets each body beside the length its head
  declares (`http-exchanges-v1` §5e), reading none of it. Revision 783 writes the listed exchanges' heads to a
  document asked for with them, which says it holds content (ADR-071, `http-exchanges-v1` §5f).

## Alternatives considered

- **Decode around gaps, marking them.** Rejected (Why).
- **Read obsolete line folding, and a bare carriage return as a space, as RFC 9112 lets some recipients.** Rejected:
  either rewrites what the bytes say before saying it.
- **Keep decodings beside the content, for search.** Rejected at this version: content search needs its own consent
  and scope (plan M8).
- **Begin with RPC, or with a length-prefixed sample protocol.** Rejected: no source here keeps RPC fragments with
  their order, and the fixture decoder already proved the contract's model on synthetic content; the first real
  decoder is chosen by validated fixtures and user value.
