# ADR-103: A capture admits the cooperative SDK's records as the application's own

- Status: accepted for M7's cooperative instrumentation; the source, its rows and the sdk profile (revision 1026)
- Date: 2026-11-07
- Decision owners: InterCat maintainers
- Relates to: ADR-030, ADR-036, ADR-102, plan §9, §15 M7, §23 (`EN-SourceField`), `contracts/sdk-v1.md`, R3, R9, R19, R22

## Context

ADR-102 defined what an instrumented application raises, `InterCat-Sdk`, and a library that raises it; no capture admitted
it. A capture admits an application provider's records only through a catalog source whose layout it knows, and only as
the rows and source fields `observation-v1` and `source-fields-v1` hold. The content fixture showed the way for a provider
InterCat defines: its manifest generated from the type that raises it and carried by the catalog (ADR-036).

## Decision

1. **A source of its own, `etw/eventsource/InterCat-Sdk`, its manifest read from the SDK's type.** The capture adapter
   references `InterCat.Sdk` for that type alone, so the two cannot disagree. What an instrumented application raises is
   no capability of Windows: the machine's capability report never lists the source, while the capability catalog names
   it as the source that records the Application SDK mechanism.
2. **Each event is an application record, `ApplicationSdk` in the Application layer, owned by the process its payload
   names** (ADR-030): the schema's announcement an inventory, a channel's opening and closing an open and a close, a
   publication a send, outbound, a consumption a receive, inbound, and an operation's start and completion a request's
   start and end.
3. **What a record says is kept where `observation-v1` keeps its kind.** A channel's or an operation's identity is the
   record's identifier, copied whole; the library's version, a channel's name and an operation's name are the record's
   name; an operation's parent is its related activity, as ETW delivers it; a message's logical length is the record's
   application payload bytes, on the publisher's side for a publication and the reader's for a consumption; a completion's
   status is the record's status. The rest are source fields of their own meaning (EN-SourceField 33 to 40): the schema's
   minor version, the clock's frequency, a channel's epoch and kind, a message's sequence, the bytes copied for it - kept
   as said, -1 included - the reader that consumed it, and the process's clock reading at each transition.
4. **The `sdk` profile asks for lifecycle and the SDK's source, metadata only,** through `icat capture` and the broker as
   every opt-in profile is, and through `icat record`. The source asks no measured cost of its own - it raises only what an
   instrumented application says - and the profile states that its cost is not yet measured.
5. **Nothing is derived from the records yet.** A message is a publication and its consumptions only once a derivation
   pairs them by channel, epoch and sequence; until then each record is a row of its own, the application's word.

## Consequences

- `icat record <dir> --profile sdk` and `icat capture --profile sdk` keep what instrumented applications say, each record
  owned by the process that raised it and readable in `icat evidence`.
- No view shows a logical message, a reader's lag or an operation tree until their derivation follows; no sample
  application or fixture measures the profile yet.
- 24 mutations of the path are caught.

## Addendum: a later schema is never misread (revision 1031)

M7 asks that old viewers retain unknown new event fields safely. The capture admits the SDK's events by the manifest of
the SDK it was built with, every field at a fixed offset before any text: a later minor version adds fields at an event's
end, after its text where it has one, so each field the capture knows stays where it was and the added one is never
read; a test compiles the source against such a schema and finds every event's layout as it was. A later major version
raises its events at a new version, which no descriptor of the capture's plan has: the capture counts such a record
undecodable rather than read it by another version's layout.
