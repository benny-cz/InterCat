# M5 content-export review — every way kept content leaves InterCat

Date: 2026-10-27, at plan revision 787
Scope reviewed: plan M5's exit-gate item, the content-export review: every path by which content a capture kept - a
record's fragment, an HTTP part assembled from its buffers, or a decoding, a search preview or a table derived from
them - is kept, shown, copied, written or shared, and the paths that must never carry it (P15 to P18, I22).
Method: every source file outside the store's own that names a content chunk, or calls a reader, decoder or writer of
content, is a row below, with what leaves through it, who asks, the consent it needs and the tests that hold it.
`ContentExportReviewTests` finds those files in the source and fails when one has no row here, or a row names a file
that no longer does any of it, so a new path is reviewed before it ships.

## What counts as content

The bytes a reviewed content policy kept beside a journal chunk (`contracts/content-v1.md`), the parts assembled from
them, and every decoding, preview or table derived from them. A journal record never holds a source's payload: its
body is the schema-approved metadata projection alone, so a raw record's bytes reveal no content (ADR-036). Consent is
the inspection a chunk was kept under: only bytes kept under `hex-text` are ever shown, copied, saved, searched or
decoded for a person.

## Kept and mirrored

| Code | Role | What leaves through it, and how | Asked by | Consent | Tests |
| --- | --- | --- | --- | --- | --- |
| `src/InterCat.Capture.Recording/LiveRecorder.cs` | Keeps | Nothing: it writes each message a reviewed scoped policy admits beside its journal chunk, under that policy's record and session limits, and the journal record keeps its metadata projection alone | A content profile, its processes reviewed before anything is recorded | The policy's inspection, kept with every chunk | "P15: a scoped content policy keeps the content of the sources it names, and of no other"; "I21: a recording keeps each WinINet buffer beside its journal chunk, with its exchange, its place and its ends" |
| `src/InterCat.Capture.Journal/LiveSessionFollower.cs` | Mirrors | Nothing beyond the session: a follow copies each chunk, byte for byte, into the session it derives, beside the chunk it mirrors | A recording's follow | As kept | "I21: a follow mirrors the content kept beside each evidence chunk, byte for byte, beside the chunk it mirrors" |

## Read for the paths below

| Code | Role | What leaves through it, and how | Asked by | Consent | Tests |
| --- | --- | --- | --- | --- | --- |
| `src/InterCat.Application/SessionContentIndex.cs` | Indexes and reads | A fragment's bytes, to the readers below alone; its index holds no byte | The readers below | Theirs | "ADR-036: content kept without consent to inspect it is stated, and its bytes are never read for a person" |
| `src/InterCat.Application/SessionContentQuery.cs` | Reads | One record's fragment: its facts always, its bytes only when revealed and kept with consent to inspect them | The content viewer, `icat content` | `hex-text` | "ADR-036: content kept without consent to inspect it is stated, and its bytes are never read for a person" |
| `src/InterCat.Application/SessionContentPartQuery.cs` | Assembles | A part whole from its buffers, or with each gap named; bytes only as the fragment's reader gives them, and never of a part that is not whole as one | The viewer, `icat content --part`, the heads below | `hex-text` for every buffer | "I21: a part is assembled only from every buffer from its first to its last, each kept whole; else its gaps are named"; "I21: parts read together are each the part their buffer belongs to, exactly as one read alone is" |
| `src/InterCat.Application/ContentSearch.cs` | Searches | The records whose kept bytes hold a text, and the bytes around each first match - from the buffer before too, where the match began in it, kept with the same consent - only when previews are asked for, inert; a record kept without consent is counted and never read; kept to a process, only the records bound to it as the evidence policy admits | `icat content --find`, Find in kept content… | `hex-text` | "P2: content kept without consent to inspect it is never searched, and is counted; a session keeping none says so"; "P6: a content search kept to a process instance finds that instance's records alone, never those of another process given its PID again"; "P2: a content search finds a text two buffers of one HTTP part split, none counted twice across the part, and never across a gap, a cut or another part" |
| `src/InterCat.Application/ContentSearchToken.cs` | Names and reads again | Nothing: a search's token names where the bytes at the end of each HTTP part it had open are kept - a range of a row's kept bytes - never a byte, and the search it reads on only by a digest keyed by the session's segments; given back, it reads those bytes again, kept with the same consent, for the search alone | A search read on from where it ended | `hex-text` | "P16: a search reads on from its token as from where it stopped, and the token holds no byte kept nor the text looked for" |
| `src/InterCat.Application/HttpExchangeHeads.cs` | Decodes | Exchanges' heads, each read whole and decoded by the HTTP/1.1 head decoder | The conversation, the listing and the tables below | `hex-text` | "P2: a head is decoded only as its whole part, from bytes kept with consent to inspect them, and a body by no decoder" |
| `src/InterCat.Application/SessionHttpExchanges.cs` | Reads heads | A conversation's heads when asked, and a process's exchanges with their heads as a table's lines | The conversation window, Export with their heads… | `hex-text` | "P2: an exchange's conversation is read by its key, from its records alone until its heads are asked for, then each decoded whole"; "P2: a process's exchanges read as a table are the ones its rung lists, each with what its heads say, within a scope and a limit, and refused for another session" |

## Shown or copied to the person who asked

| Code | Role | What leaves through it, and how | Asked by | Consent | Tests |
| --- | --- | --- | --- | --- | --- |
| `src/InterCat.Desktop/SessionContentWindow.cs` | Shows, copies, saves | A record's kept bytes as inert hexadecimal and text, at most 64 KiB at once, its part and its decodings; the bytes shown copied as hex; a range, or a whole part, saved to a file the person names | Show the kept bytes, Show the whole part, Decode, Copy, Save | `hex-text`; a part saved only whole | "ADR-036: content kept without consent to inspect it is stated in the viewer, and never shown"; "R15: Copy and Save say why they are not offered: no content kept of the record, no byte of its message, none in it, or none read" |
| `src/InterCat.Cli/ContentCommand.cs` | Shows, saves, writes | The same on the command line, `--json` stating facts and never a byte; with `--find --output`, a table of the records listed, the bytes around each first match only with `--previews`, said beside the file to be content | `--reveal`, `--part`, `--save`, `--decode`, `--find --previews`, `--find --output` | `hex-text` | "P2: icat content shows and saves a whole HTTP part as one, and refuses --part of a record that belongs to none"; "P2: icat content --find lists the records whose kept bytes hold a text, each with its locator, and shows their bytes only with --previews"; "P6: icat content --find --process keeps to one process's records, never those of another process given its PID again"; "P2: icat content --find --output writes the records listed to a table, the bytes around each match only with --previews, saying it is content" |
| `src/InterCat.Desktop/SessionConversationWindow.cs` | Shows | An exchange's two heads, decoded when asked; nothing decoded is kept | Decode its heads | `hex-text` | "P2: C on an HTTP exchange opens its conversation, which says what each head said only once asked, and Show its buffers opens them" |
| `src/InterCat.Desktop/ContentSearchWindow.cs` | Shows, writes | The records holding a text, and the bytes around each first match only when asked, inert; Export… writes them as a table, the bytes around each match only where they were shown, and then once it has said the table is content | Find in kept content…, Show previews, Export… | `hex-text` | "P2: Find in kept content lists the records holding a text without a byte of them unless asked, and a record found opens in the content viewer"; "P6: Find in kept content keeps to the selected process's records, never those of another process given its PID again"; "P2: Find in kept content exports the records listed, asking first where the bytes around each match were shown, since the table is then content" |
| `src/InterCat.Cli/RawCommand.cs` | Shows | A raw journal record's retained bytes, which are its metadata projection alone: no content | `icat raw --reveal-bytes` | None needed: no content | "R18: icat raw says a record's facts as the window does, in words - its provider named, its lengths counted - and its bytes only when asked" |
| `src/InterCat.Desktop/SessionRawRecordWindow.cs` | Shows | The same in the window: no content | Reveal | None needed: no content | "R15: Reveal, which waits disabled while it reads, gives the record's text the keyboard back with the bytes in it" |

## Written to a file the person names

| Code | Role | What leaves through it, and how | Asked by | Consent | Tests |
| --- | --- | --- | --- | --- | --- |
| `src/InterCat.Cli/ExchangesCommand.cs` | Writes | `http-exchange-heads-v1`, or a table of the listed exchanges with their heads, only for `--decode` asked with `--json` or `--output`; a file written says beside it that it holds content | `--decode --json`, `--decode --output` | `hex-text`; a head kept without it is written undecoded, saying why | "P2: icat exchanges writes the listed exchanges' heads only when asked, as content, in JSON or as a table whose cells a spreadsheet will not evaluate" |
| `src/InterCat.Desktop/SessionEvidenceSource.cs` | Reads heads for the window | A process's exchanges in the rung's scope with their heads, for the table below | Export with their heads… | `hex-text` | "P2: a process's exchanges export with what their heads say only once the window has said the table is content, each exchange of the scope a line" |
| `src/InterCat.Desktop/WorkspaceViewModel.Http.cs` | Makes the window's table | The table `icat exchanges --decode --format csv` writes, its text cells neutralized | Export with their heads… | `hex-text`; heads kept without it undecoded | "P2: heads kept without consent to inspect them are exported undecoded, each cell saying why, and never as what they said" |
| `src/InterCat.Desktop/MainWindow.axaml.cs` | Writes, shares | The table of heads, once its prompt has said the table is content; an original package, once what it holds has been said, its message content counted | Export with their heads…, Share original session… | `hex-text` for the table; the package carries all | "P2: a process's exchanges export with what their heads say only once the window has said the table is content, each exchange of the scope a line"; "§11.3: Share original session says what it holds, then saves an exact copy in a new folder under the one chosen" |

## Shared as a package

| Code | Role | What leaves through it, and how | Asked by | Consent | Tests |
| --- | --- | --- | --- | --- | --- |
| `src/InterCat.Application/OriginalEvidencePackage.cs` | Packages | Every chunk the generation names, as the evidence it is, kept with consent to inspect it or not, its contents said before saving with how many records' bytes and how many bytes; no file a session was imported from | `icat package --original`, Share original session…, an investigation's package | Carried as kept; no reader shows a byte kept without `hex-text` | "I22: a redacted package keeps no content and says so; the original package carries it and says how much" |
| `src/InterCat.Cli/PackageCommand.cs` | Packages | The same on the command line, its contents and its warning said before anything is written | `icat package --original` | As above | "I22: a redacted package keeps no content and says so; the original package carries it and says how much" |
| `src/InterCat.Application/InvestigationPackage.cs` | Packages | An investigation's sessions as original packages, saying before it is saved how much message content they kept | `icat workspace package`, an investigation's Package | As above | "I22: an investigation's package says how much message content its sessions kept before it is saved, and one keeping none adds nothing" |

## Paths that never carry content

None of their code names a content chunk or calls a reader of content - a file that did would need a row above, which
`ContentExportReviewTests` holds - and each is held by tests of its own:

- **A redacted package** keeps no chunk and no reference to one, and says so: "I22: no source name, identity, reading,
  locator or body byte reaches a package file"; "P17: a package labelled redacted carries none of its source's
  evidence: no journal, segment or plan of it".
- **Sharing reports and exports of rows** - the metadata-only report, a shared comparison, the evidence and ranking
  exports - are written from rows and their metadata, from fixed allowlists, and read no chunk.
- **`http-exchanges-v1` and `content-view-v1`** hold no head's value and no byte: a listing's figures come from
  records' metadata, and `icat content --json` refuses `--reveal`, `--save` and `--decode` - "R18: icat pin, channels,
  exchanges, checkpoint and profiles refuse what they cannot read, naming what they expect"; "P2: icat exchanges
  writes the listed exchanges' heads only when asked, as content, in JSON or as a table whose cells a spreadsheet will
  not evaluate".
- **A support bundle** holds versions, capabilities and each session's counters, and nothing a record holds: "P16:
  icat support writes versions, capabilities and each session's counters, and no name, endpoint, host or path".
- **Logs, telemetry and crash diagnostics**: InterCat sends nothing anywhere - "P16: no product code opens a
  connection, sends a request or looks up a name, and it is built only on reviewed packages" - and the elevated broker
  reaches no content view or decoder: "P18: the elevated broker can reach no importer, content view or UI, and its own
  code opens no ETL file".
- **The window's metadata search** finds names, PIDs, endpoints and addresses, and its snippets omit endpoint strings;
  it reads no chunk.

## Findings

- **Search previews and P16.** P16 forbids payload bytes in search snippets: those are the metadata search's, which
  hold none. The content search's previews are M8's deliberate deliverable, off until asked for, written inertly, and
  kept nowhere; they are listed above as the path they are.
- **An original package carries content kept without consent to inspect it**, as it carries all evidence. No InterCat
  reader shows such bytes, but the files hold them; the package says how much content it holds before it is saved, and
  its warning says anyone who opens it sees everything InterCat saw.
- **A decoder's failure is shown and written where its decoding is** (revision 785), under the same consent; its words
  are the decoder's, and may quote the bytes it failed on.
- **An investigation's package said how much content its sessions kept, but no test held it.** This review adds that
  test: "I22: an investigation's package says how much message content its sessions kept before it is saved, and one
  keeping none adds nothing".
- **`icat content --json`'s refusal of bytes was held by no test.** This review adds the refusal to "R18: icat pin,
  channels, exchanges, checkpoint and profiles refuse what they cannot read, naming what they expect".

## Verdict

The content-export review passes: every path by which kept content leaves InterCat is one a person asks for, reads
only what was kept with consent to inspect it - or, for an exact copy of the evidence, says how much content it
carries before it is saved - and says what it holds; every path meant to carry none reads no chunk.
`ContentExportReviewTests` keeps this table whole as the code changes.
