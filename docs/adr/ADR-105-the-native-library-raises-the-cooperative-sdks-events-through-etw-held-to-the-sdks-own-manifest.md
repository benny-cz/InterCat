# ADR-105: The native library raises the cooperative SDK's events through ETW, held to the SDK's own manifest

- Status: accepted for M7's cooperative instrumentation; the library, its sample and FX-SDK-002 (revision 1032)
- Date: 2026-11-07
- Decision owners: InterCat maintainers
- Relates to: ADR-102, ADR-103, ADR-104, plan §15 M7, `contracts/sdk-v1.md`, R3, R9, P4

## Context

M7 asks for native C/C++ and .NET reference libraries and samples. The .NET library raises the schema through an
EventSource in manifest format (ADR-102), and a capture reads its layout from the SDK's type (ADR-103). A C or C++
application has no EventSource: what it raises must be exactly what the capture already admits, or its records would
be read by a layout they do not have.

## Decision

1. **One C header, `sdk/native/include/intercat_sdk.h`, raises the same provider's events through ETW's provider API**
   (`EventRegister`, `EventWrite`, `EventWriteTransfer`): the provider's identity is the one EventSource derives from
   `InterCat-Sdk`, each event's descriptor - id, version, level, opcode, task and keyword - is the SDK's manifest's, and
   each field is written in the manifest's order, the process first and a text last. A test reads the header against the
   manifest a capture embeds, so neither moves without the other; the capture is unchanged, and admits a native
   application's records as a .NET one's.
2. **It holds a channel and an operation as `InterCat.Sdk` does**: a channel opened at an epoch says its messages under
   it and closes once; an operation's identity is made for it, its parent is its start's related activity, and it
   completes once. A second close or completion says nothing.
3. **What `InterCat.Sdk` throws for, the native library refuses**, returning `ERROR_INVALID_PARAMETER` and raising
   nothing: an epoch below 1, a kind the SDK names none for, no name or identity, a negative length, a copy below -1.
   Otherwise a call returns `ERROR_SUCCESS` - said, or no session listening to say it to - or the error `EventWrite`
   returned, the event then lost to a session that counts it lost.
4. **The schema is announced as `InterCat.Sdk` announces it**: to a session enabling the provider, before any other
   event, and once the provider is registered to a session already listening - its library `native-1.0`, its clock
   `QueryPerformanceCounter`'s, at the frequency it announces.
5. **It references nothing of InterCat's and builds with the platform's own compilers**: one header, defined once with
   `INTERCAT_SDK_IMPLEMENTATION`, linking `advapi32` and `rpcrt4`, compiling as C and as C++ with gcc and MSVC at their
   highest warning levels.

## Consequences

- FX-SDK-002 measures it through the product: its sample, `sdk/native/samples/publish_consume.c`, publishes 131
  messages at a channel's two epochs, 130 consumed by its reader and one left unread, under three operations, recorded by
  `icat record --profile sdk` three times. Nothing was lost: every message was one message and every consumption one
  reader's, by length and process; the unread message stayed open; the channel made again was a second epoch; each
  operation was one start under its parent and one completion with the status it first completed with, its second
  completion and its channel's second close saying nothing; and its schema was announced once a run, read at the
  capture's layout.
- 19 mutations of the library are caught: by the header's test against the manifest, or by FX-SDK-002 recorded
  through the product once with the sample compiled against the mutated header.
- The repository's build does not compile the library: the suite holds the header's agreement with the manifest on every
  build, and its behaviour is the fixture's, measured again when the header changes.
- Not provided: a C++ wrapper owning a channel or an operation, a library to link rather than a header, batching and
  content.
