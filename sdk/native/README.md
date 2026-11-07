# InterCat cooperative SDK: native library

`include/intercat_sdk.h` lets a C or C++ application say what operating-system tracing cannot see of it - the channels
it carries messages through, each message it publishes and each reader's consumption of it, and its operations - exactly
as the .NET library, `InterCat.Sdk`, says it: the same events of the same provider, which a capture under the `sdk`
profile admits alike. What it says is the application's own assertion. The schema is `contracts/sdk-v1.md`; why the
library is shaped so is ADR-105.

## Use

Define `INTERCAT_SDK_IMPLEMENTATION` in exactly one C or C++ file before including the header, and link `advapi32` and
`rpcrt4`. Nothing is raised while no session listens.

```c
#define INTERCAT_SDK_IMPLEMENTATION
#include "intercat_sdk.h"

intercat_sdk_start();

intercat_channel ring;
intercat_channel_open(&ring, &ring_id, L"telemetry", INTERCAT_CHANNEL_SHARED_MEMORY, 1);
intercat_channel_published(&ring, sequence, length, copied);   /* once, when committed where readers can take it */
intercat_channel_consumed(&ring, sequence, length, reader);    /* once by each reader */
intercat_channel_close(&ring);

intercat_operation work;
intercat_operation_start(&work, L"flush", NULL);                /* or under a parent's identity */
intercat_operation_complete(&work, 0);                          /* 0 success; any other value a failure */

intercat_sdk_stop();
```

A channel's identity is the one every process using it names alike; its epoch starts at 1, and a channel made again,
or a sequence that wraps, opens the next. A message's boundaries are logical: a ring buffer's publish and consume, never
each load or store of its memory.

Every call that says something returns `ERROR_SUCCESS` - said, or no session listening to say it to -,
`ERROR_INVALID_PARAMETER` for an argument the SDK refuses, raising nothing, or the error `EventWrite` returned, the
event then lost to a session that counts it lost. A second close of a channel or completion of an operation says nothing.

## Sample

`samples/publish_consume.c` publishes and consumes on one channel at two epochs, under three operations, and logs what it
said beside it as the fixture FX-SDK-002's truth:

```sh
gcc -O2 -municode -I include -o publish_consume.exe samples/publish_consume.c -ladvapi32 -lrpcrt4
cl /O2 /W4 /I include samples\publish_consume.c advapi32.lib rpcrt4.lib
publish_consume.exe --truth <directory>
```
