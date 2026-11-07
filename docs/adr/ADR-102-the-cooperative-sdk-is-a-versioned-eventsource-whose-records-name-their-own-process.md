# ADR-102: The cooperative SDK is a versioned EventSource whose records name their own process

- Status: accepted for M7's cooperative instrumentation; the schema and its .NET library (revision 1025)
- Date: 2026-11-07
- Decision owners: InterCat maintainers
- Relates to: ADR-030, ADR-036, plan §15 M7, §24 (`sdkSchema`), §26.1, `contracts/sdk-v1.md`, R9, R19, P27

## Context

M7 asks for a small versioned SDK schema an application raises to say what OS tracing cannot see - its channels, the
messages it publishes and consumes on them, its operations - with process binding, channel and peer identities, message
and parent identities, an explicit source clock, transitions, application lengths and status, through ETW/EventSource
or a validated local transport, and native and .NET reference libraries. InterCat already admits one application
provider: its content fixture, an EventSource in manifest format whose events name their raising process, which the
capture's catalog reads its layout from (ADR-036).

## Decision

1. **Transport: an EventSource in manifest format, `InterCat-Sdk`.** ETW needs no service, no driver and no second
   transport; a capture enables it as any manifest provider, and the provider costs an application one enabled check
   per call while no session listens. An event's text, where it has one, is its last field, so a capture reads every
   other field at a fixed offset, a bounded read (R9).
2. **Every event names the process that raised it first**, as the content fixture's do, so a record's owner is in its
   payload and no application header is read as one (ADR-030).
3. **A channel is an identity the processes using it share**, opened at an epoch; a message is its channel's epoch
   and sequence, published once and consumed once by each reader, so one write three readers take is one publication
   and three consumptions, never three writes. A wrapped sequence or a remade channel is a new epoch. Lengths are
   logical, with the bytes copied beside where the publisher knows them.
4. **An operation is an identity under an optional parent**, started and completed once with a status: 0 its success,
   any other value a failure in the application's own code. Its
   parent is its start's related activity - ETW's own link from a start to what it began under - so an event carries
   one identity of its own in its payload, which a capture copies whole, and the parent arrives in the record's header
   as every related activity does. A start or a stop opts out of the runtime's activity tracking: tracing the SDK while
   a session follows the task library's activity flow never begins, ends or changes an application's activities.
5. **Each message's and operation's transition carries the process's own clock reading**, `Stopwatch` ticks, and the
   SDK's announcement of its schema their frequency, once for the process whatever it raises: the explicit source clock
   M7 asks for, beside ETW's reading of when the event was raised.
6. **Versioning: an event's version is the schema's major; a minor adds fields at an event's end**, after its text
   where it has one, which an earlier reader keeps unread. The SDK announces its schema, its clock's frequency and its
   library version first to each session that enables it.
7. **The .NET library has no reference to InterCat**: `InterCat.Sdk` references nothing of InterCat's own (R19), so
   instrumenting an application brings in no capture, storage or analysis code. A capture's catalog reads its layout
   from its provider type, so the two cannot disagree.
8. **What an application says is an assertion** whose authenticity is limited to the application: InterCat states it
   as the application's, never as an observation of the channel.

## Consequences

- An application can say its channels, messages and operations today; no capture admits them until the capture's
  source and profile follow, and no view derives logical messages until their derivation does.
- Content, batching, a native C/C++ library and the samples M7 names are not defined at this revision.
- 19 mutations of the path are caught.
