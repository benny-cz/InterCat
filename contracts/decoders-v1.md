# InterCat decoders v1

Status: **implemented** on the command line (`icat content --decode`, `--part --decode` and `--decoders`, revision
774; `icat exchanges --decode`, revision 776) and in the window (the content viewer's Decode and Decode its part,
revision 775, §5a; an exchange's conversation, revision 777).

This contract fixes plan M8's decoder contract at its first version: what a decoder is, what it reads, when it reads
it, and what it says of what it read, for every decoder InterCat has. A decoder owns no bytes: it reads content the
capture kept (`content-v1`), only when a person asks, and its decoding is made again each time and never kept, so the
bytes stay the evidence and nothing derived from them goes stale. ADR-070 records the decisions, and ADR-036 and
ADR-037 the content and the source it reads.

## 1. A decoder

| Field | Meaning |
| --- | --- |
| Identity | A stable name for its format: `intercat-content-fixture`, `http1-head`. |
| Name | The decoder as a person reads it: "InterCat's HTTP/1.1 head decoder". |
| Version | A whole number. The same bytes decode to the same fields at one version; a decoder whose fields for some bytes change takes the next. |
| Reads | What it reads: one record's fragment, or one whole part (`content-v1` §4), of named sources and events, as `icat content --decoders` lists it. |

## 2. A decoding

A decoding names its decoder and version; the record asked about and, where it read a part, the part - "the request
head of exchange 7" (`content-v1` §4); the kept bytes it read, by offset in the message or the part; each field it
decoded, with its name, its value and the bytes it came from; and what it did not decode and why, as a sentence. It is
made:

- **Only when asked**: `icat content --decode`, or the viewer's Decode. Nothing decodes on its own, and search,
  rankings, metrics, exports and reports never read a decoding.
- **Only from bytes kept with consent to inspect them** (`hex-text`): bytes kept without it are never decoded, and the
  decoding says so.
- **A part only whole**: every buffer from the one flagged first to the one flagged last, kept whole. A part that is
  not whole is not decoded at all, since what it lacks could hold any line of it: a decoding never stands in for
  missing bytes (P2).
- **Within bounds**: at most the first 64 KiB of the bytes, as a view shows, and at most 250 ms, saying where either
  stopped it.
- **Inert**: its fields are text that nothing renders, runs or fetches.

A decoding is never kept: no file, JSON (`icat content --json` refuses `--decode`) or package holds one, so opening a
session with a newer decoder changes nothing stored.

## 3. The decoders at this version

| Identity | Version | Reads |
| --- | --- | --- |
| `intercat-content-fixture` | 1 | One record's message from InterCat's own content fixture: `InterCat-Fixture-Content`'s events 1 and 2 (`content-v1` §4). |
| `http1-head` | 1 | One whole HTTP/1.1 request or response head WinINet's capture kept: the part of events 2001 and 2003 (§4). |

## 4. The HTTP/1.1 head decoder

WinINet's capture keeps each request and response head as a part, in buffers, and FX-HTTP-001 to FX-HTTP-003 found
every kept part equal to the bytes on the wire (ADR-037). The decoder reads a request or response head's whole part as
HTTP/1.1 defines a head (RFC 9112 §2 to §5), line by line, each line ended by CRLF or by LF alone:

- **The first line.** A request head's is a request line: a method, a request target and an HTTP version, one space
  apart, as the fields `method`, `target` and `version`. A response head's is a status line: an HTTP version, a
  three-digit status code and a reason, one space apart, the reason empty or absent, as the fields `version`, `status`
  and, where it is not empty, `reason`. A method is a token; a target is one or more visible ASCII characters; a
  version is `HTTP/`, a digit, a dot and a digit.
- **Field lines.** Each is a field named as the line names it: a token, a colon, and its value between optional spaces
  and tabs, which are not part of it. Its bytes are the whole line, without its end.
- **The empty line** ends the head. Bytes after it are no part of a head: they are counted and not decoded.

Where a line is not what HTTP/1.1 allows there, the decoding stops at that line's start, saying which line, from which
byte, and why; nothing from it on is decoded, and the fields before it stand. Such a line is a first line that is not
a request or status line; a field line that holds no colon, names its field with nothing or with a character a name
cannot hold (whitespace before the colon is one), begins with whitespace to continue the line before it (obsolete line
folding), or holds a control character but the tab in its value; and any line holding a carriage return that ends no
line. A head that ends before its empty line is decoded as far as its whole lines go, a line no line end follows is
not decoded, and the decoding says the head may have held more. A value's byte outside visible ASCII and the space is
written `\xHH`: a field is never read as text in an encoding its bytes do not declare. A decoding that reached the
empty line says so, and only such a decoding speaks for what its head declares of its body
(`contracts/http-exchanges-v1.md` §5e).

## 5. The command line

`icat content <session> <locator> --part --decode` decodes the part a buffer belongs to with the decoder that reads
it, before its bytes, which `--reveal` or `--save` adds as without `--decode`. A buffer of a head decoded alone, with
`--decode` and no `--part`, is said to be read by no decoder alone, naming the part `--part --decode` decodes; a
body's part is said to be read by none. `icat content --decoders` lists §3's decoders and takes nothing else. A
decoding that read nothing - a part not whole, kept without consent, or holding no bytes - answers partially, as
`--reveal` does for bytes it cannot show. `icat exchanges --decode` decodes each listed exchange's request and
response heads the same way, and lists what each request asked for and what its response answered beside the exchange
(`contracts/http-exchanges-v1.md` §5c).

## 5a. The window

The content viewer offers a decoding once a record's bytes are shown: Decode, where a decoder reads the record; Decode
its part, where one reads the part its buffer belongs to. Decode its part reads the whole part, and is offered
disabled, saying why, for a part that is not whole, which is never decoded. The Decoded tab names the decoder and its
version and the bytes it read - of the record's message, or of the part - and lists each field with its bytes and what
was not decoded; a field's Show its bytes shows them in the hex view, of the whole part where the decoder read one.
Nothing is decoded until asked. An exchange's conversation (`contracts/http-exchanges-v1.md` §5d) decodes its two
heads the same way, side by side, when its Decode its heads is pressed.

## 6. Not defined at this version

- **Bodies' content**, of any content type, and compressed content: a body's length is set beside its head's
  declaration without reading it (`http-exchanges-v1` §5e), and no chunk framing reaches a decoder of WinINet's parts,
  which keeps a chunked body without it (`content-v1` §5.1). HTTP/2 and HTTP/3, which the WinINet capture measured
  here does not carry (`content-v1` §6).
- **Other mechanisms' decoders**: RPC fragments, named pipe messages and packet streams, which no source here keeps
  with the order a reconstruction needs (plan M8).
- **A conversation decoded as one**: a part decoded in the light of another, as a response's body framed by its
  request's method. Each part is decoded alone; `icat exchanges --decode` and an exchange's conversation in the window
  (§5, §5a) set an exchange's two decoded heads side by side.
- **A decoding kept, exported or searched**: JSON or columnar metadata, and content search.
- **Isolation**: a decoder runs in the process that asked, bounded by the bytes and the time above, and never in the
  capture; one whose parsing is larger than these is isolated in an unprivileged worker first (plan M8).
