# ADR-071: A decoding enters a document only when asked for with it, and the document says it holds content

- Status: accepted; revision 783. It builds M8's export of decoded metadata
- Date: 2026-10-24
- Amends: ADR-070 decision 2 (a decoding is never exported)
- Relates to: plan M8, §11.1, §11.2, I22, ADR-036, ADR-037, ADR-070; `contracts/http-exchanges-v1.md`,
  `contracts/decoders-v1.md`, `contracts/content-v1.md`

## Context

M8's deliverables name a JSON and columnar export of decoded metadata beside the explicit export of binary content.
ADR-070 kept every decoding out of every document: a decoding was made for a person when asked, and nothing stored or
exported held one, so a newer decoder changed nothing kept. A person who wanted to filter a busy machine's HTTP
exchanges by host, or sort them by status, in a tool of their own could only read `icat exchanges --decode` off a
terminal, while `icat content --part --save` already writes a head's bytes, which hold more than its decoding, to a
file they name. §11.1 asks that payload export have deliberate commands of its own, and since revision 782 a decoder's
version names, through its corpus, what it decodes.

## Decision

1. **Only when asked for with it.** `icat exchanges --decode`, asked with `--json` or `--output`, writes the listed
   exchanges with what their heads say. Without `--decode`, `http-exchanges-v1` holds no head's value, and nothing
   else - the share report, a redacted or original package, an evidence export, the session's store - holds a
   decoding.
2. **A document of its own, saying it holds content.** It is `http-exchange-heads-v1`, never `http-exchanges-v1`: its
   `content` says it holds what heads say, decoded from content kept with consent to inspect it, to be shared only as
   that content would be, and its `decoder` names the decoder and version that made each decoding. A file written says
   so beside it.
3. **Each decoding whole.** Every listed head's decoding is written as `decoders-v1` §2 defines one - the part, the
   bytes read, each field with its value and bytes, what was not decoded and why - with what its head declares of its
   body; a head not decoded says why, and leaves the answer partial.
4. **A table, one line per exchange.** `--format csv`, or a `.csv` path, writes the listed exchanges as a table: their
   figures and, with `--decode`, what their start lines and Host say and why a head was not decoded, each in a column
   of its own. A text cell a spreadsheet would evaluate begins with an apostrophe, as in every table InterCat writes,
   since decoded values come from the observed machine.
5. **A snapshot, never read back.** The document names its session, generation and decoder version; InterCat never
   reads it back, and a later version may decode the same bytes otherwise, as its corpus says.

## Why

- **The bytes are already the person's to save.** Content kept with consent to inspect it may be saved to a file a
  person names (`content-v1` §4); its decoding discloses no more than those bytes, so writing it under the same
  consent adds no exposure, while a document of its own keeps every metadata document free of content for whoever
  relies on that.
- **Content nobody asked for is the harm.** Requiring `--decode` with `--json` or `--output` keeps a program that
  reads `icat exchanges --json` from receiving content it never asked for, and the contract's name says what a file
  holds to whoever finds it later.
- **A version names its decodings.** A document naming the decoder's version says which decodings it holds, and a
  newer decoder changes nothing written: what ADR-070's "never kept" protected still holds, since nothing InterCat
  reads again holds a decoding.
- **Spreadsheets evaluate text.** A Host or a reason phrase is chosen by whoever sent it; written plainly, a cell
  beginning `=` would run as a formula wherever the table is opened.

## Consequences

- `icat exchanges --decode --json`, `--decode --output <file>`, `--format json|csv`, and a table of the listed
  exchanges with or without their heads, which `contracts/http-exchanges-v1.md` §5f defines.
- `decoders-v1` §2 and §6, `content-v1` §4 and ADR-070's second decision name this export as the one place a decoding
  is written; `icat content --json` still refuses `--decode`, and since revision 784 the window's Export with their
  heads… writes the table alone, of a process's exchanges in its scope, once it has said the table is content.

## Alternatives considered

- **Add the heads to `http-exchanges-v1`.** Rejected: a reader of that contract would receive content under a name
  that promised none.
- **A command of its own.** Rejected: `--decode` already names reading content and `--output` writing a file, and the
  two together are the deliberate request §11.1 asks for, beside the listing they write.
- **Only the start lines and Host, never every field.** Rejected for JSON: a person exporting heads asks what they
  said, and every field is one they may already read and save; the table keeps to what fits columns.
- **No neutralizing, since the table is the person's.** Rejected: whoever opens it later meets the formula, not the
  person who wrote it.
