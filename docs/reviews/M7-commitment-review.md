# M7 commitment review — cooperative instrumentation

Date: 2026-11-07, at plan revision 1025
Scope reviewed: M7's first part, the SDK's schema and its .NET library (`contracts/sdk-v1.md`, ADR-102)
Method: §15.2 asks, before a post-v1 milestone is committed for shipment, for the user problem, the exact new evidence
obtainable, the supported environments, the expected overhead, the deployment burden and the exit fixtures. Each is
answered below for what M7 is to ship, and what revision 1025 builds of it; what is not built is listed as not built.

## The user problem

OS tracing sees a process's handles, views and transfers, never what an application means by them: how many messages
went through a shared-memory ring, which reader took which, which of its operations a request was part of. A section
two processes map is topology at most (ADR-092); its traffic is invisible to every driverless source.

## The new evidence obtainable

What an application says of itself, through the SDK: its channels by a shared identity and epoch, each message's
publication and each reader's consumption by sequence, logical lengths and the bytes copied, its operations with
parents and status, and its own clock at each transition - each an assertion of the application's, stated as such.

## Supported environments

An application on .NET 10 referencing `InterCat.Sdk`; any Windows build whose ETW delivers an EventSource's events.
A native C/C++ library is not built.

## Expected overhead

While no session enables the provider, one enabled check per call. Enabled, one ETW event per transition, written in
place with no allocation; its cost and throughput are to be measured and published, as M7's exit tests ask.

## Deployment burden

None for InterCat: no service, no driver. An application takes one library with no other InterCat dependency.

## Exit fixtures

Not built: producer/consumer and multi-reader shared-memory fixtures against independent truth counts, wrapped
sequences and restarts as new epochs, missing consumption left open, the SDK's disabled overhead and enabled
throughput. Revision 1025 builds the schema and the library, held by tests of what each call raises.
