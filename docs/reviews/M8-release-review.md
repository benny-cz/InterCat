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

## Addendum, revision 785

A decoder's failure is contained (`contracts/decoders-v1.md` §2): a decoder that throws on some bytes decodes nothing
and says it failed and how, as a decoding of nothing under its name and version, so the content viewer, a
conversation, `icat content --decode`, `icat exchanges --decode` and an export of heads each say why instead of
stopping. Decoders still run in the process that asked, never in the capture; M8's exit review comes next, and nothing
else above changes.

## Exit review, revision 786

Date: 2026-10-27, at plan revision 786
Method: §15.2's questions are answered again for M8 as built; each implementation item and deliverable of plan M8 is
checked against what ships and the tests that hold it, and each exit test against named tests, all of which passed in
revision 785's suites; what M8 does not do is listed as its limits.

### The user problem, answered

A person who kept their own processes' HTTP messages sees, beside which process talked to which host, what each
exchange asked for and what it was answered. In the window an exchange's conversation sets its request and response
side by side and decodes their heads when asked, a buffer leads to its exchange, a text is found in kept content, and
a process's exchanges export with what their heads say; the command line says the same. Nothing is decoded unasked,
nothing decoded is kept, and a conversation is never shown more complete than its buffers and its heads' declarations
make it.

### Implementation and deliverables

| Item | Built | Where, and the tests that hold it |
| --- | --- | --- |
| A decoder contract: input classification, protocol and version, framing rules, resource budgets and output provenance | Revision 774 | `decoders-v1` §1 to §4, ADR-070; "P2: a whole HTTP request or response head decodes into its start line and its field lines, each with its bytes, linked to its part and the decoder's version"; "P2: a head decoding reads at most what a view shows at once, within its time budget, and says where it stopped" |
| Reconstruction where the source supplies its order | Revisions 242, 305 and 778 | A WinINet part is assembled from its buffers only whole, its gaps named in place (`content-v1` §4); WinINet keeps a chunked body without its framing (FX-HTTP-003), so a body is set beside the length its head declares instead (`http-exchanges-v1` §5e); "I21: a part is assembled only from every buffer from its first to its last, each kept whole; else its gaps are named"; "P2: a body is set beside the length its head declares, and said to disagree with it only where its recorded bytes do" |
| Missing-range, duplicate, out-of-order, retransmitted and truncated states | Revisions 242, 244 and 305 | A part's buffers never recorded, not kept or cut by the record limit are each named; a buffer numbered no later than the one before it opens another use and is never merged: "R22: an exchange number used again in one process ID is two parts, told apart in time, never one". A WinINet buffer is one API call's, so none is a duplicate or a retransmission of another: those states belong to packet streams, which no source here keeps |
| The first real decoder chosen by validated fixtures and user value | Revision 774 | HTTP/1.1 heads from WinINet's parts, which FX-HTTP-001 to FX-HTTP-003 measured equal to the wire |
| Decoders isolated; decoded metadata kept apart from content | Revisions 774, 783 and 785 | A decoder runs only where a person asks, in their unprivileged window or command line and never in the capture ("P12: the capture callback reaches no query, store, file, network, name or symbol lookup, decoder or wait, and only reviewed locks"), and its failure is contained: "P2: a decoder that fails on its bytes decodes nothing and says it failed and how, stopping nothing that asked it". A decoding is kept nowhere InterCat reads, and written only to a document asked for with it (ADR-070, ADR-071). A separate worker waits on a larger decoder (Limits) |
| Conversation inspector | Revision 777 | "P2: C on an HTTP exchange opens its conversation, which says what each head said only once asked, and Show its buffers opens them" |
| Fragment-to-message-to-operation navigation | Revisions 777 and 781 | "I21: a buffer leads to the exchange it belongs to, by its raw identity under the evidence policy, and a record of none says why"; "P2: a buffer's content viewer opens its exchange's conversation, which offers no buffers from there, and a record of no exchange offers none" |
| Decoder manager and version pinning | Revisions 774 and 782 | `icat content --decoders` lists every decoder, its version and what it reads, and each decoding names its decoder and version; "P2: every decoder InterCat lists has a corpus at its version, and decodes each of its cases exactly as the corpus says". No decoder runs unasked, so none is switched on or off |
| Bounded content search, previews off until asked | Revisions 779 and 780 | "P2: a content search lists the records whose kept bytes hold a text, how often and where first, and shows no byte unless previews are asked for"; "P2: Find in kept content lists the records holding a text without a byte of them unless asked, and a record found opens in the content viewer" |
| JSON and columnar metadata export; explicit binary content export | Revisions 783 and 784; 236 and 242 | "P2: icat exchanges writes the listed exchanges' heads only when asked, as content, in JSON or as a table whose cells a spreadsheet will not evaluate"; "P2: a process's exchanges export with what their heads say only once the window has said the table is content, each exchange of the scope a line"; "P2: icat content shows and saves a whole HTTP part as one, and refuses --part of a record that belongs to none" |
| Decoder fixture corpus | Revision 782 | `fixtures/decoders` (`decoders-v1` §3a); "P2: every decoder InterCat lists has a corpus at its version, and decodes each of its cases exactly as the corpus says" |

### M8's exit tests

| Exit test | Verdict | Evidence |
| --- | --- | --- |
| Fragmented, malformed and truncated conversations never become falsely complete | Met | "I21: a part is assembled only from every buffer from its first to its last, each kept whole; else its gaps are named"; "P2: a head HTTP/1.1 does not allow, or one that ends early, decodes only as far as its bytes show, and says why the rest does not"; "P2: a head decoded to the empty line that ends it says so, and a head not decoded declares nothing of its body"; "P2: a body is set beside the length its head declares, and said to disagree with it only where its recorded bytes do" |
| Correlation remains correct under concurrent calls and direction changes | Met for WinINet's parts | FX-HTTP-003 measured 256 exchanges of eight clients in one process, 254 begun before an earlier one ended, every part equal to the server's; a part is one event's buffers, so one direction's; "I21: parts read together are each the part their buffer belongs to, exactly as one read alone is"; "R22: an exchange number used again in one process ID is two parts, told apart in time, never one" |
| Decoder timeout or crash does not stop capture | Met | No decoder runs in the capture ("P12: the capture callback reaches no query, store, file, network, name or symbol lookup, decoder or wait, and only reviewed locks"); a decoding stops at its budget ("P2: a head decoding reads at most what a view shows at once, within its time budget, and says where it stopped"), and a decoder's failure stops nothing that asked it ("P2: a decoder that fails on its bytes decodes nothing and says it failed and how, stopping nothing that asked it") |
| Opening a source with a new decoder changes a derived revision only | Met, more strictly | A decoding is never kept, so a new decoder changes nothing stored (`decoders-v1` §2); its version is pinned by its corpus, and an export names it and is never read back (ADR-071) |
| Content search and export respect scope and redaction | Met | "P2: content kept without consent to inspect it is never searched, and is counted; a session keeping none says so"; "P2: heads kept without consent to inspect them are exported undecoded, each cell saying why, and never as what they said"; an export holds only the exchanges listed or in the rung's scope, and `http-exchanges-v1` none of their heads; "I22: a redacted package keeps no content and says so; the original package carries it and says how much" |

### Supported environments, overhead and deployment

Every view runs wherever a session opens: the command line wherever .NET 10 runs, and the window, on any session
holding WinINet's content, which a capture keeps on Windows through its content profile (ADR-037). No decoding, search
or export adds overhead to a capture: each reads content already kept, when a person asks, within its bounds. Nothing
is deployed beyond the command line and the window.

### Limits

- Only HTTP/1.1 heads through WinINet are decoded. Bodies' content, coded or compressed bodies, HTTP/2 and HTTP/3, RPC
  fragments, pipe messages and packet streams have no decoder or reconstruction, since no source here keeps them with
  the order reconstruction needs; they wait on M6, M7 and M9's sources, under the same contract.
- Decoders run in the process that asked. An exception is contained, but a failure the process cannot survive would
  end the window or the command; a decoder whose parsing is larger than these bounded ones is isolated in an
  unprivileged worker first (ADR-070).
- A search reads each record's bytes on their own, so a match split across two buffers is not found; its budgets are
  fixed, its narrowing is to a process ID rather than an instance, and what it finds is not exported.
- A response's body is judged without its request's method where that request's head was not decoded, and a coded
  body's length is never compared.
- The window lists no decoders of its own: a decoding names its decoder and version where it is shown, and `icat
  content --decoders` lists them all.

### Verdict

M8's exit gate is met for HTTP through WinINet, the content source this build qualifies. Every deliverable is built on
the command line and in the window, and every exit test holds by the tests named above. The limits are limits of what
M8 claims, each said where it applies; other mechanisms' reconstruction and a decoder worker come with the sources and
decoders M6, M7 and M9 bring, which M8's contract already takes.

## Addendum, revision 788

A search narrows by process instance (`contracts/content-v1.md` §4), which lifts the limit above that its narrowing
was to a process ID. `icat content --find --process` names a process by a PID one process alone held, else by its
instance id, refusing a reused PID with each of its holders, and keeps to the records bound to it as the evidence
policy admits, counting those the policy leaves out; the window's Find in kept content… keeps to the process selected
there the same way, under the window's policy. `--pid` still keeps to the records that carry a PID, and says it is
whichever process held it. Nothing else above changes.

## Addendum, revision 789

A search finds a text two buffers of one HTTP part split (`contracts/content-v1.md` §4), which lifts the limit above
that a match split across two buffers was not found. A buffer is searched with the end of the buffer right before it
in its part, where that one was kept whole, of one encoding and, in UTF-16, ending at a code unit's end; the match is
listed on the later buffer, begun that many bytes before its first byte, its preview taking the bytes of both, and
matches are counted from the part's start, none overlapping another across it. A match across three buffers, or across
a buffer not recorded, not kept or cut, is still not found, as the sentence every answer carries says. Nothing else
above changes.
