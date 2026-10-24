# M8 release review — conversation reconstruction and payload decoders

Date: 2026-10-24, at plan revision 774
Scope reviewed: M8's first shippable part, HTTP/1.1 heads decoded from WinINet's whole parts under a decoder contract
(`icat content --part --decode`, `contracts/decoders-v1.md`, ADR-070)
Method: §15.2 asks, before a post-v1 milestone is committed for shipment, for the user problem, the exact new evidence
obtainable, the supported environments, the expected overhead, the deployment burden and the exit fixtures. Each is
answered below for what revision 774 ships, and M8's exit tests are checked against named tests; what is not built is
listed as not built. M8 begins before M6, M7 and M9, whose sources need Windows to build and qualify, since the plan
lets a validated existing content source start it.

## The user problem

A person who kept their own processes' HTTP messages (Keep: Your processes' messages) sees which process talked to
which host, and each exchange's parts and sizes, but reads what a request asked for and what its response answered
only as hexadecimal. A head holds it: the method and target, the status, the host, the content's type and length.

## The new evidence obtainable

None is collected. A head part already kept with consent to inspect it decodes into its start line's fields and its
field lines, each with the bytes it came from; a head that is not whole, or not what HTTP/1.1 allows, says so instead
of being decoded past it.

## Supported environments

Wherever a session opens: the command line, on any session holding WinINet's content, which a capture keeps on Windows
through its content profile (ADR-037). The window decodes one record's fragment, not yet a part.

## Expected overhead

None on a capture: a decoding is made when asked, from bytes already kept, reading at most 64 KiB in at most 250 ms.

## Deployment burden

None beyond the command line already shipped.

## M8's exit tests

| Exit test | Verdict | Evidence |
| --- | --- | --- |
| Fragmented, malformed and truncated conversations never become falsely complete | Met for heads | "P2: a head is decoded only as its whole part, from bytes kept with consent to inspect them, and a body by no decoder"; "P2: a head HTTP/1.1 does not allow, or one that ends early, decodes only as far as its bytes show, and says why the rest does not"; since revision 242, "I21: a part is assembled only from every buffer from its first to its last, each kept whole; else its gaps are named" |
| Correlation remains correct under concurrent calls and direction changes | Met for parts | A part is one exchange's, one event's and one process's buffers, a number used again told apart in time: "R22: an exchange number used again in one process ID is two parts, told apart in time, never one" |
| Decoder timeout or crash does not stop capture | Met by design | A decoder runs only when asked, in the command line or the window, never in the capture: "P12: the capture callback reaches no query, store, file, network, name or symbol lookup, decoder or wait, and only reviewed locks"; within its time budget: "P2: a head decoding reads at most what a view shows at once, within its time budget, and says where it stopped" |
| Opening a source with a new decoder changes a derived revision only | Met, more strictly | A decoding is never kept, so a new decoder changes nothing stored (`decoders-v1` §2) |
| Content search and export respect scope and redaction | Not built | No content search exists; a decoding is never exported or packaged, and a redacted package keeps no content |

## Not built, in M8's order

- The window's decoding of a part, and a conversation inspector: a request with its response, navigated from fragment
  to message to operation.
- Bodies and their content codings; RPC fragments, pipe messages and packet streams, which no source here keeps in
  order.
- Decoder management beyond the list and the version each decoding names; isolation in unprivileged workers.
- Bounded content search, metadata export of decodings, and a decoder fixture corpus beyond the tests' heads.

## Verdict

HTTP/1.1 head decoding is ready to ship on the command line: it decodes only a whole head kept with consent, only when
asked, stops where the bytes stop being HTTP/1.1, and keeps nothing. M8 is not complete; the parts listed above
follow, the window's decoding of a part first, since the person this product serves works in the window.

## Addendum, revision 775

The window decodes a part (`contracts/decoders-v1.md` §5a): a buffer of a whole HTTP head offers Decode its part,
which reads the head whole, lists its fields with their bytes in the part, and shows a field's bytes in the part's hex
view; a head that is not whole offers it disabled, saying why. The window is now among the supported environments for
heads; of the parts not built, the conversation inspector comes next, and nothing else above changes.

## Addendum, revision 776

`icat exchanges --decode` lists what each listed exchange's heads say (`contracts/http-exchanges-v1.md` §5c): what its
request asked for, of which host, and what its response answered, each head read whole with the HTTP/1.1 head decoder,
every listed head in one pass over the session. A head not whole, or kept without consent, is not decoded, the listing
says why beneath, and the answer is partial; the listing writes no JSON and no file. This is the command line's view
of a conversation, an exchange's two heads side by side, each decoded alone; the window's conversation inspector comes
next, and nothing else above changes.

## Addendum, revision 777

The window's conversation inspector (`contracts/http-exchanges-v1.md` §5d): C on an exchange opens its request and
response side by side, what was recorded of each read from the records alone, and Decode its heads decodes both heads
whole when asked, each field with the bytes of its head it came from; Show its buffers goes on to the exchange's
buffers, where C shows a buffer's content and its part, so an exchange leads to its messages and they to their
fragments. Of the deliverables not built, the framing of bodies - by Content-Length or chunked transfer coding, so a
conversation says whether a response held all its framing declared, never whole by its buffers' flags alone - comes
next; then bounded content search, export of decoded metadata, the decoder fixture corpus and isolation. Nothing else
above changes.

## Addendum, revision 778

Bodies are set beside the length their heads declare (`contracts/http-exchanges-v1.md` §5e), reading none of their
content: a body holding fewer bytes than its Content-Length, though its buffers make it whole, is said to disagree, so
a truncated conversation - the exit test's - is never shown complete on its buffers' word alone. Planning it found
that a chunk-framing decoder, the plan's natural next step, would have been wrong for this source: FX-HTTP-003
measured that WinINet keeps a chunked body without its framing, so no chunk reaches a decoder and a chunked body's end
is its last buffer's. Compressed bodies stay unmeasured and uncompared. Bounded content search comes next; nothing
else above changes.

## Addendum, revision 779

Bounded content search (`contracts/content-v1.md` §4): `icat content --find` searches the bytes kept with consent to
inspect them, each record's on its own, within 256 MiB, 10 seconds and a number of records listed, saying what it did
not search and leaving the answer partial where it skipped any; it shows no byte unless `--previews` asks, and then as
inert text. The exit test's search that respects scope and redaction holds for it: consent bounds what it reads,
`--pid` what it lists, and a redacted package keeps no chunk to search. It says plainly what it cannot find: a match
split across two buffers. The window's search of kept content comes next; nothing else above changes.

## Addendum, revision 780

The window searches kept content (`contracts/content-v1.md` §4): Find in kept content… (Ctrl+Shift+F) lists the
records holding a text, as `icat content --find` does, with the bytes around each match only when asked, and opens a
record found in the content viewer, so a text leads to its record and the record to its part. Of the deliverables not
built, the step from a buffer's part to its exchange's conversation, which closes the navigation from a fragment to
its message to its operation, comes next, then the decoder fixture corpus; export of decoded metadata needs a
consented export contract first, and other mechanisms' reconstruction and the isolation of decoders need Windows
sources or a parser larger than the bounded ones here. Nothing else above changes.

## Addendum, revision 781

A buffer's content viewer opens its exchange's conversation (`contracts/http-exchanges-v1.md` §5d), which closes the
deliverable's navigation both ways: an exchange leads to its buffers and to what its heads say, and a buffer to its
part and to its exchange, each found by raw identity and under the window's evidence policy. The decoder fixture
corpus comes next, then M8's exit review; nothing else above changes.

## Addendum, revision 782

Each decoder has a fixture corpus at its version (`contracts/decoders-v1.md` §3a): `fixtures/decoders` holds the
HTTP/1.1 head decoder's 24 heads and the content fixture decoder's 5 messages, each with the decoding that version
makes of it, and a test decodes every case and requires one corpus for each listed decoder at its version and no
other, so a decoder whose decoding of a case changes cannot keep its version. The deliverables' decoder fixture corpus
is built, and with it version pinning: a decoding names its version, and the version names the corpus that says what
it decodes. M8's exit review comes next; nothing else above changes.

## Addendum, revision 783

Decoded metadata is exported when asked (`contracts/http-exchanges-v1.md` §5f, ADR-071): `icat exchanges --decode`,
with `--json` or `--output`, writes the listed exchanges and what their heads say as `http-exchange-heads-v1`, a
document that says it holds content and names the decoder version that made each decoding, or with `--format csv` as a
table whose cells a spreadsheet will not evaluate. The exit test's export that respects scope and redaction holds for
it: it reads only the exchanges listed, of the process asked for, from content kept with consent; `http-exchanges-v1`,
the share report and every package still hold no decoding. The window's export of a process's exchanges with their
heads comes next, then M8's exit review; nothing else above changes.

## Addendum, revision 784

The window exports decoded metadata too (`contracts/http-exchanges-v1.md` §5f): at a process's HTTP exchanges, Export
with their heads… says first what its table holds and that it is content, and only then asks for a file, which holds
every exchange the rung lists in its scope with what its heads say, as `icat exchanges --decode --format csv` writes
them. Every deliverable M8 can build on the sources here is built; its exit review comes next, and nothing else above
changes.
