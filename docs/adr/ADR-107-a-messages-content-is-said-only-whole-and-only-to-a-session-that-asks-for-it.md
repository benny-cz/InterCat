# ADR-107: A message's content is said only whole, and only to a session that asks for it

- Status: accepted for M7's content opt-in; both libraries say a message's content at the schema's minor 2, and a capture
  keeping no content never asks for it (revision 1039); a capture keeping it under a request, its fixtures and its cost
  follow
- Date: 2026-11-07
- Decision owners: InterCat maintainers
- Relates to: ADR-036, ADR-037, ADR-102, ADR-103, ADR-105, plan §15 M7, §11.2, `contracts/sdk-v1.md`,
  `contracts/content-v1.md`, I21, R17

## Context

M7 asks for optional bounded content in the SDK's schema and an explicit opt-in to it, beside size and count metadata that
never requires it. InterCat keeps content only under a reviewed scoped policy, of a source whose content contract is
validated, and a fragment is a message whole, its prefix cut by the record limit, or omitted at the session limit: a
prefix is never shown as a whole message, and a content field's length is the message's (`content-v1`, I21). Saying
content costs an application: a capture that keeps none should cost it nothing more, and what one session asks for must
not change what another hears. EventSource raises an event wherever any session's keywords enable it; ETW then delivers it
only to the sessions whose own keywords match.

## Decision

1. **A message's content is an event of its own, 10 `MessageContent`**, raised right after the message's publication,
   which is raised as before: `processId`, `channel`, `epoch`, `sequence`, then the bytes, sized by the count before them.
   A publication that carried its content would be filtered from a session keeping none, and lost to it, whenever
   another session asked for content.
2. **Content is under a keyword of its own, `Content` (0x1)**, which no other event has, and a session hears a content
   only by asking for it. An EventSource marks every event it raises with keywords of its own, 0xF000_0000_0000, so a
   session asking for keywords hears the SDK's events only by asking for those too: a capture keeping no content asks for
   them and every keyword the SDK may define but content's, 0xF000_FFFF_FFFE, and its applications build no content for
   it. Measured on this revision, a workload publishing 50 messages with their bytes under each: a session asking for
   0xFFFF_FFFE heard none of the SDK's events; under 0xF000_FFFF_FFFE every publication and no content, the SDK saying
   none; under 0xF000_FFFF_FFFF every content too. A session asking for no keyword asks for all, content included.
3. **Content is said only whole**, at most 32,768 bytes: a longer message is published without it, since `content-v1`
   states no prefix a source cut. A capture's record limit may still cut it, and says so.
4. **Both libraries say it**: `InterCatChannel.Published(sequence, bytes, copied)`, its logical length the bytes', says
   whether its content was said; natively `intercat_channel_published_content`, which refuses what the .NET call throws
   for and sets `said`. The schema's minor is 2 and the native library `native-1.2`. A capture of an earlier minor
   enables only the events it admits, so it never receives a content.
5. **Opting in is the application's and the capture's both**: an application says the content of the messages it
   publishes with their bytes, and a session hears it only by asking. What it says is its assertion of its own message's
   bytes, `content-v1`'s `ApplicationPayload`.

## Consequences

- A capture under the `sdk` profile reads an application that says content as before: every publication, and no content
  built for it.
- No capture keeps the SDK's content yet: its admission under a scoped content request - the content contract, the
  process scope, the measured impact and fixtures of both libraries - follows (`contracts/sdk-v1.md` §9).
- A capture built before an event was added enables only the events it admits, so that event's records never reach it:
  measured here, a capture admitting the SDK's events 1 to 9 received none of 50 contents another session made the SDK
  raise, and counted none as not admitted. What revision 1036 said of a capture of an earlier minor - that it counts a
  batch's records undecodable - is so corrected: they are missing from it, never misread.
- 22 mutations of the content event, its keyword and call, the schema's minor, the `sdk` profile's keywords and the
  native header and call are caught. A content's count field is read only through ETW, as a listener in the process
  reads the bytes by their own size, and the native call's bound and fields only by a session: the fixture recording
  content through the product holds them, as FX-SDK-006 held the native batches' fields. An ETW reader found every
  content of this revision's measure its count and then the bytes sent.

## Addendum: a capture keeps the SDK's content under a request (revision 1040)

A capture keeps what an application says of its messages' bytes only under a scoped content request naming the SDK's
source and its processes, as WinINet's are kept (`contracts/content-v1.md` §5.2). The content event is compiled only
where the capture keeps the source's content - a descriptor the catalog marks content-only - and the provider is asked
for content's keyword only where an admitted descriptor holds a content field, so the `sdk` profile still asks for
neither. Its record is an inventory of the message's bytes, not a send, so no send is counted twice. The source holds
its process scope by the session's process filter, measured: a decoy publishing with its bytes beside a named burst
reached the session with no record and was never asked to say anything. Keeping it costs a median 0.016 CPU percentage
points and 0.1% of a burst's time - 100,000 messages of 256 bytes at 50,000 calls a second, every one of
700,000 contents read and none lost (`bench/results/sdk-content-impact-20261107T203409Z`): Low. `icat record --mechanism sdk` and `icat profiles`
take the request; recorded live through the product, a burst's 200 messages kept whole. 18 mutations of the
compiler, the requests' keywords, the catalog, the domain and the command line are caught, the product catching
revision 1039's mutation of a content's count field, which only a reader of ETW sees. A fixture holding it to a truth,
and the broker and the window asking for it, follow.

## Second addendum: FX-SDK-007 holds the SDK's content to a truth (revision 1041)

FX-SDK-007 records one process publishing 131 messages with their bytes through `InterCat.Sdk` - the edges a capture keeps
whole, cuts or never hears, then seeded lengths - and a decoy doing the same beside it, through the product: `icat record`
keeping the SDK's content under a request naming the publisher alone, at a record limit of 4,096 bytes. Its truth names
each message's length, SHA-256 and kept prefix's SHA-256, never a byte. In three runs, nothing lost, every publication and
consumption was the truth's, and each of the 129 messages the SDK said with its bytes one content record whose fragment was
the truth's: 54 whole, 75 cut to the limit with the message's length as their original; the two longer than 32,768
bytes had none; the decoy left no record and no byte; and the publisher's marker was in no file but the content chunk.
The probe holds this through the product's own readers - `icat messages`' derivation and the content viewer's index - so no
product path changes, and no mutation pass is run: the native library's content, whose bound and fields only a session
reads, is held by the next fixture.

## Third addendum: FX-SDK-008 holds the native library's content to a truth (revision 1042)

FX-SDK-008 records FX-SDK-002's native sample publishing its 131 messages at two epochs with their bytes through
`intercat_channel_published_content` - epoch 1's first five at the edges a capture keeps whole, cuts or never hears - and a
.NET decoy beside it, through the product, at a record limit of 1,024 bytes. The sample's truth names each message's
length and digests, taken through Windows' own hashing apart from the probe's, never a byte. In three runs, nothing lost,
its messages and operations were FX-SDK-002's, and each of the 130 contents the library said was the truth's: 27 whole, 103 cut
to the limit; the one past the most had none, and the library said it said none; the decoy left nothing, and no byte
was outside the content kept. The library's content call is so held through the product: 7 mutations of its bound,
its fields and its publication, each compiled into the sample and recorded once, are caught. Its scenario now names the
library's version as the header declares it, which revision 1039 left naming `native-1.1`.
