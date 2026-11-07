/*
 * InterCat cooperative SDK, native reference library (contracts/sdk-v1.md, ADR-102, ADR-105).
 *
 * Raises the same InterCat-Sdk events as the .NET library, InterCat.Sdk, through the Windows ETW provider API, so a
 * capture admits what a C or C++ application says exactly as it admits what a .NET one says: every event names the
 * process that raised it first, an event's text is its last field, a message is its channel's epoch and sequence, and an
 * operation's parent is its start's related activity. Nothing is raised while no session listens.
 *
 * A single header: define INTERCAT_SDK_IMPLEMENTATION in exactly one C or C++ file before including it, and link
 * advapi32 and rpcrt4. Call intercat_sdk_start once before any other call, and intercat_sdk_stop before the process ends.
 *
 * Every call that says something returns ERROR_SUCCESS - said, or nothing to say it to as no session listens -,
 * ERROR_INVALID_PARAMETER for an argument the SDK refuses, raising nothing, as InterCat.Sdk throws for one; or the error
 * EventWrite returned, the event then lost to a session listening, which counts it lost.
 */
#ifndef INTERCAT_SDK_H
#define INTERCAT_SDK_H

/* The ETW provider API is Windows Vista's and later's: a translation unit targeting an earlier version is raised to it, so
 * include this header before <windows.h>. */
#if !defined(_WIN32_WINNT) || _WIN32_WINNT < 0x0600
#undef _WIN32_WINNT
#define _WIN32_WINNT 0x0600
#endif

#include <windows.h>
#include <evntprov.h>

/* A session enabling a provider, as the Windows SDK names the code; MinGW's headers do not. */
#ifndef EVENT_CONTROL_CODE_ENABLE_PROVIDER
#define EVENT_CONTROL_CODE_ENABLE_PROVIDER 1
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* The schema this library raises: a change an earlier reader could misread bumps the major, a field added at an event's
 * end, or an event added, the minor (contracts/sdk-v1.md section 5). Minor 1 adds the batches. */
#define INTERCAT_SDK_SCHEMA_MAJOR 1
#define INTERCAT_SDK_SCHEMA_MINOR 1

/* The most messages one batch says. */
#define INTERCAT_SDK_MAXIMUM_BATCH 65536

/* Each event's descriptor as InterCat.Sdk's manifest declares it: id, version, channel, level, opcode, task, keyword. */
#define INTERCAT_SDK_EVENT_SDK_STARTED 1, 1, 0, 4, 0, 65533, 0
#define INTERCAT_SDK_EVENT_CHANNEL_OPENED 2, 1, 0, 4, 1, 0, 0
#define INTERCAT_SDK_EVENT_CHANNEL_CLOSED 3, 1, 0, 4, 2, 0, 0
#define INTERCAT_SDK_EVENT_MESSAGE_PUBLISHED 4, 1, 0, 4, 9, 0, 0
#define INTERCAT_SDK_EVENT_MESSAGE_CONSUMED 5, 1, 0, 4, 240, 0, 0
#define INTERCAT_SDK_EVENT_OPERATION_STARTED 6, 1, 0, 4, 1, 0, 0
#define INTERCAT_SDK_EVENT_OPERATION_COMPLETED 7, 1, 0, 4, 2, 0, 0
#define INTERCAT_SDK_EVENT_MESSAGES_PUBLISHED 8, 1, 0, 4, 9, 0, 0
#define INTERCAT_SDK_EVENT_MESSAGES_CONSUMED 9, 1, 0, 4, 240, 0, 0

/* What a channel carries its messages through, as the application says (contracts/sdk-v1.md section 2). */
typedef enum intercat_channel_kind
{
    INTERCAT_CHANNEL_OTHER = 0,
    INTERCAT_CHANNEL_PIPE = 1,
    INTERCAT_CHANNEL_SOCKET = 2,
    INTERCAT_CHANNEL_SHARED_MEMORY = 3
} intercat_channel_kind;

/* One process's use of a channel at one epoch, as intercat_channel_open fills it: the identity every process using the
 * channel names it by, and its incarnation, from 1 - a sequence is unique within one epoch alone. A new epoch is a new
 * channel incarnation: after the sequence wraps, or the channel is made again. */
typedef struct intercat_channel
{
    GUID id;
    LONGLONG epoch;
    volatile LONG closed;
} intercat_channel;

/* One operation of the application's own, as intercat_operation_start fills it: its identity, unique to it, and the
 * operation it is part of, the empty identity where none. */
typedef struct intercat_operation
{
    GUID id;
    GUID parent;
    volatile LONG completed;
} intercat_operation;

/* Registers the provider, and announces the schema to a session already listening: ERROR_SUCCESS, or the registration's
 * error, after which every call raises nothing. */
ULONG intercat_sdk_start(void);

/* Unregisters the provider: no call raises anything after it. */
void intercat_sdk_stop(void);

/* Whether a session listens to the provider. */
int intercat_sdk_listened(void);

/* Opens the process's use of channel id at an epoch, from 1, of a kind and a name, saying so. */
ULONG intercat_channel_open(intercat_channel *channel, const GUID *id, const wchar_t *name, intercat_channel_kind kind, LONGLONG epoch);

/* Says the message at a sequence was published - committed where its readers can take it - with its logical length and
 * the bytes copied for it where the publisher knows them (-1 where not). */
ULONG intercat_channel_published(const intercat_channel *channel, LONGLONG sequence, LONGLONG logical_length, LONGLONG copied_length);

/* Says reader consumer consumed the message at a sequence, of its logical length: each reader of one message says so once,
 * and the message was still published once. */
ULONG intercat_channel_consumed(const intercat_channel *channel, LONGLONG sequence, LONGLONG logical_length, LONGLONG consumer);

/* Says the count consecutive messages from first on were published together - a batch, each still its own sequence,
 * published once - with their logical lengths and the bytes copied for them together (-1 where not known): one call for
 * what count calls of intercat_channel_published would say, no message's own length said. A count from 1 to
 * INTERCAT_SDK_MAXIMUM_BATCH whose run stays within the sequences a LONGLONG holds. */
ULONG intercat_channel_published_batch(const intercat_channel *channel, LONGLONG first, LONGLONG count, LONGLONG logical_length,
    LONGLONG copied_length);

/* Says reader consumer consumed the count consecutive messages from first on together - one consumption of each - with
 * their logical lengths together. */
ULONG intercat_channel_consumed_batch(const intercat_channel *channel, LONGLONG first, LONGLONG count, LONGLONG logical_length,
    LONGLONG consumer);

/* Closes the process's use of the channel's epoch, saying so once: a second close says nothing. */
ULONG intercat_channel_close(intercat_channel *channel);

/* Starts an operation of a name, under parent where it has one (NULL or the empty identity where not), saying so; its
 * identity is made for it. */
ULONG intercat_operation_start(intercat_operation *operation, const wchar_t *name, const GUID *parent);

/* Says the operation completed with its status - 0 its success, any other value a failure in the application's own
 * code - once: a second completion says nothing. */
ULONG intercat_operation_complete(intercat_operation *operation, LONGLONG status);

#ifdef INTERCAT_SDK_IMPLEMENTATION

#include <rpc.h>
#include <string.h>
#include <wchar.h>

/* {82c69482-41a7-5887-ff73-c02a5012c044}: the identity EventSource derives from the provider's name, InterCat-Sdk. */
static const GUID intercat_sdk_provider = { 0x82c69482, 0x41a7, 0x5887, { 0xff, 0x73, 0xc0, 0x2a, 0x50, 0x12, 0xc0, 0x44 } };
static const wchar_t intercat_sdk_library[] = L"native-1.1";
static REGHANDLE intercat_sdk_handle;

static void intercat_sdk_announce(void)
{
    static const EVENT_DESCRIPTOR descriptor = { INTERCAT_SDK_EVENT_SDK_STARTED };
    LONG process = (LONG)GetCurrentProcessId();
    LONG major = INTERCAT_SDK_SCHEMA_MAJOR;
    LONG minor = INTERCAT_SDK_SCHEMA_MINOR;
    LARGE_INTEGER frequency;
    EVENT_DATA_DESCRIPTOR data[5];
    QueryPerformanceFrequency(&frequency);
    EventDataDescCreate(&data[0], &process, sizeof process);
    EventDataDescCreate(&data[1], &major, sizeof major);
    EventDataDescCreate(&data[2], &minor, sizeof minor);
    EventDataDescCreate(&data[3], &frequency.QuadPart, sizeof frequency.QuadPart);
    EventDataDescCreate(&data[4], intercat_sdk_library, (ULONG)sizeof intercat_sdk_library);
    EventWrite(intercat_sdk_handle, &descriptor, 5, data);
}

/* A session enabling the provider is told which schema this library raises, before any other event; a session enabled
 * before registration is told once the provider is registered, as the callback cannot write before it is. */
static void NTAPI intercat_sdk_enabled(LPCGUID source, ULONG control, UCHAR level, ULONGLONG any, ULONGLONG all,
    PEVENT_FILTER_DESCRIPTOR filter, PVOID context)
{
    (void)source;
    (void)level;
    (void)any;
    (void)all;
    (void)filter;
    (void)context;
    if (control == EVENT_CONTROL_CODE_ENABLE_PROVIDER && intercat_sdk_handle != 0)
    {
        intercat_sdk_announce();
    }
}

ULONG intercat_sdk_start(void)
{
    ULONG status = EventRegister(&intercat_sdk_provider, intercat_sdk_enabled, NULL, &intercat_sdk_handle);
    if (status != ERROR_SUCCESS)
    {
        intercat_sdk_handle = 0;
        return status;
    }

    if (EventProviderEnabled(intercat_sdk_handle, 0, 0))
    {
        intercat_sdk_announce();
    }

    return ERROR_SUCCESS;
}

void intercat_sdk_stop(void)
{
    if (intercat_sdk_handle != 0)
    {
        EventUnregister(intercat_sdk_handle);
        intercat_sdk_handle = 0;
    }
}

int intercat_sdk_listened(void)
{
    return intercat_sdk_handle != 0 && EventProviderEnabled(intercat_sdk_handle, 0, 0);
}

ULONG intercat_channel_open(intercat_channel *channel, const GUID *id, const wchar_t *name, intercat_channel_kind kind, LONGLONG epoch)
{
    static const EVENT_DESCRIPTOR descriptor = { INTERCAT_SDK_EVENT_CHANNEL_OPENED };
    LONG process;
    LONG said;
    EVENT_DATA_DESCRIPTOR data[5];
    if (channel == NULL || id == NULL || name == NULL || epoch < 1 || kind < INTERCAT_CHANNEL_OTHER || kind > INTERCAT_CHANNEL_SHARED_MEMORY)
    {
        return ERROR_INVALID_PARAMETER;
    }

    channel->id = *id;
    channel->epoch = epoch;
    channel->closed = 0;
    if (!EventEnabled(intercat_sdk_handle, &descriptor))
    {
        return ERROR_SUCCESS;
    }

    process = (LONG)GetCurrentProcessId();
    said = (LONG)kind;
    EventDataDescCreate(&data[0], &process, sizeof process);
    EventDataDescCreate(&data[1], &channel->id, sizeof channel->id);
    EventDataDescCreate(&data[2], &channel->epoch, sizeof channel->epoch);
    EventDataDescCreate(&data[3], &said, sizeof said);
    EventDataDescCreate(&data[4], name, (ULONG)((wcslen(name) + 1) * sizeof(wchar_t)));
    return EventWrite(intercat_sdk_handle, &descriptor, 5, data);
}

ULONG intercat_channel_published(const intercat_channel *channel, LONGLONG sequence, LONGLONG logical_length, LONGLONG copied_length)
{
    static const EVENT_DESCRIPTOR descriptor = { INTERCAT_SDK_EVENT_MESSAGE_PUBLISHED };
    LONG process;
    LARGE_INTEGER ticks;
    EVENT_DATA_DESCRIPTOR data[7];
    if (channel == NULL || logical_length < 0 || copied_length < -1)
    {
        return ERROR_INVALID_PARAMETER;
    }

    if (!EventEnabled(intercat_sdk_handle, &descriptor))
    {
        return ERROR_SUCCESS;
    }

    process = (LONG)GetCurrentProcessId();
    QueryPerformanceCounter(&ticks);
    EventDataDescCreate(&data[0], &process, sizeof process);
    EventDataDescCreate(&data[1], &channel->id, sizeof channel->id);
    EventDataDescCreate(&data[2], &channel->epoch, sizeof channel->epoch);
    EventDataDescCreate(&data[3], &sequence, sizeof sequence);
    EventDataDescCreate(&data[4], &logical_length, sizeof logical_length);
    EventDataDescCreate(&data[5], &copied_length, sizeof copied_length);
    EventDataDescCreate(&data[6], &ticks.QuadPart, sizeof ticks.QuadPart);
    return EventWrite(intercat_sdk_handle, &descriptor, 7, data);
}

ULONG intercat_channel_consumed(const intercat_channel *channel, LONGLONG sequence, LONGLONG logical_length, LONGLONG consumer)
{
    static const EVENT_DESCRIPTOR descriptor = { INTERCAT_SDK_EVENT_MESSAGE_CONSUMED };
    LONG process;
    LARGE_INTEGER ticks;
    EVENT_DATA_DESCRIPTOR data[7];
    if (channel == NULL || logical_length < 0)
    {
        return ERROR_INVALID_PARAMETER;
    }

    if (!EventEnabled(intercat_sdk_handle, &descriptor))
    {
        return ERROR_SUCCESS;
    }

    process = (LONG)GetCurrentProcessId();
    QueryPerformanceCounter(&ticks);
    EventDataDescCreate(&data[0], &process, sizeof process);
    EventDataDescCreate(&data[1], &channel->id, sizeof channel->id);
    EventDataDescCreate(&data[2], &channel->epoch, sizeof channel->epoch);
    EventDataDescCreate(&data[3], &sequence, sizeof sequence);
    EventDataDescCreate(&data[4], &consumer, sizeof consumer);
    EventDataDescCreate(&data[5], &logical_length, sizeof logical_length);
    EventDataDescCreate(&data[6], &ticks.QuadPart, sizeof ticks.QuadPart);
    return EventWrite(intercat_sdk_handle, &descriptor, 7, data);
}

ULONG intercat_channel_close(intercat_channel *channel)
{
    static const EVENT_DESCRIPTOR descriptor = { INTERCAT_SDK_EVENT_CHANNEL_CLOSED };
    LONG process;
    EVENT_DATA_DESCRIPTOR data[3];
    if (channel == NULL)
    {
        return ERROR_INVALID_PARAMETER;
    }

    if (InterlockedExchange(&channel->closed, 1) != 0 || !EventEnabled(intercat_sdk_handle, &descriptor))
    {
        return ERROR_SUCCESS;
    }

    process = (LONG)GetCurrentProcessId();
    EventDataDescCreate(&data[0], &process, sizeof process);
    EventDataDescCreate(&data[1], &channel->id, sizeof channel->id);
    EventDataDescCreate(&data[2], &channel->epoch, sizeof channel->epoch);
    return EventWrite(intercat_sdk_handle, &descriptor, 3, data);
}

ULONG intercat_operation_start(intercat_operation *operation, const wchar_t *name, const GUID *parent)
{
    static const EVENT_DESCRIPTOR descriptor = { INTERCAT_SDK_EVENT_OPERATION_STARTED };
    static const GUID none = { 0, 0, 0, { 0, 0, 0, 0, 0, 0, 0, 0 } };
    LONG process;
    LARGE_INTEGER ticks;
    EVENT_DATA_DESCRIPTOR data[4];
    RPC_STATUS made;
    if (operation == NULL || name == NULL)
    {
        return ERROR_INVALID_PARAMETER;
    }

    /* An identity unique to this machine is enough: an operation is named within the captures of the host it ran on. */
    made = UuidCreate(&operation->id);
    if (made != RPC_S_OK && made != RPC_S_UUID_LOCAL_ONLY)
    {
        return (ULONG)made;
    }

    operation->parent = parent != NULL ? *parent : none;
    operation->completed = 0;
    if (!EventEnabled(intercat_sdk_handle, &descriptor))
    {
        return ERROR_SUCCESS;
    }

    process = (LONG)GetCurrentProcessId();
    QueryPerformanceCounter(&ticks);
    EventDataDescCreate(&data[0], &process, sizeof process);
    EventDataDescCreate(&data[1], &operation->id, sizeof operation->id);
    EventDataDescCreate(&data[2], &ticks.QuadPart, sizeof ticks.QuadPart);
    EventDataDescCreate(&data[3], name, (ULONG)((wcslen(name) + 1) * sizeof(wchar_t)));

    /* The parent is the start's related activity, as InterCat.Sdk raises it: the empty identity names none. */
    if (memcmp(&operation->parent, &none, sizeof none) != 0)
    {
        return EventWriteTransfer(intercat_sdk_handle, &descriptor, NULL, &operation->parent, 4, data);
    }

    return EventWrite(intercat_sdk_handle, &descriptor, 4, data);
}

ULONG intercat_operation_complete(intercat_operation *operation, LONGLONG status)
{
    static const EVENT_DESCRIPTOR descriptor = { INTERCAT_SDK_EVENT_OPERATION_COMPLETED };
    LONG process;
    LARGE_INTEGER ticks;
    EVENT_DATA_DESCRIPTOR data[4];
    if (operation == NULL)
    {
        return ERROR_INVALID_PARAMETER;
    }

    if (InterlockedExchange(&operation->completed, 1) != 0 || !EventEnabled(intercat_sdk_handle, &descriptor))
    {
        return ERROR_SUCCESS;
    }

    process = (LONG)GetCurrentProcessId();
    QueryPerformanceCounter(&ticks);
    EventDataDescCreate(&data[0], &process, sizeof process);
    EventDataDescCreate(&data[1], &operation->id, sizeof operation->id);
    EventDataDescCreate(&data[2], &status, sizeof status);
    EventDataDescCreate(&data[3], &ticks.QuadPart, sizeof ticks.QuadPart);
    return EventWrite(intercat_sdk_handle, &descriptor, 4, data);
}

/* Whether a batch is one the SDK says: a count from 1 to the most, a length not negative, a run within a LONGLONG. */
static int intercat_sdk_batch(LONGLONG first, LONGLONG count, LONGLONG logical_length)
{
    return count >= 1 && count <= INTERCAT_SDK_MAXIMUM_BATCH && logical_length >= 0 && first <= MAXLONGLONG - (count - 1);
}

ULONG intercat_channel_published_batch(const intercat_channel *channel, LONGLONG first, LONGLONG count, LONGLONG logical_length,
    LONGLONG copied_length)
{
    static const EVENT_DESCRIPTOR descriptor = { INTERCAT_SDK_EVENT_MESSAGES_PUBLISHED };
    LONG process;
    LARGE_INTEGER ticks;
    EVENT_DATA_DESCRIPTOR data[8];
    if (channel == NULL || !intercat_sdk_batch(first, count, logical_length) || copied_length < -1)
    {
        return ERROR_INVALID_PARAMETER;
    }

    if (!EventEnabled(intercat_sdk_handle, &descriptor))
    {
        return ERROR_SUCCESS;
    }

    process = (LONG)GetCurrentProcessId();
    QueryPerformanceCounter(&ticks);
    EventDataDescCreate(&data[0], &process, sizeof process);
    EventDataDescCreate(&data[1], &channel->id, sizeof channel->id);
    EventDataDescCreate(&data[2], &channel->epoch, sizeof channel->epoch);
    EventDataDescCreate(&data[3], &first, sizeof first);
    EventDataDescCreate(&data[4], &count, sizeof count);
    EventDataDescCreate(&data[5], &logical_length, sizeof logical_length);
    EventDataDescCreate(&data[6], &copied_length, sizeof copied_length);
    EventDataDescCreate(&data[7], &ticks.QuadPart, sizeof ticks.QuadPart);
    return EventWrite(intercat_sdk_handle, &descriptor, 8, data);
}

ULONG intercat_channel_consumed_batch(const intercat_channel *channel, LONGLONG first, LONGLONG count, LONGLONG logical_length,
    LONGLONG consumer)
{
    static const EVENT_DESCRIPTOR descriptor = { INTERCAT_SDK_EVENT_MESSAGES_CONSUMED };
    LONG process;
    LARGE_INTEGER ticks;
    EVENT_DATA_DESCRIPTOR data[8];
    if (channel == NULL || !intercat_sdk_batch(first, count, logical_length))
    {
        return ERROR_INVALID_PARAMETER;
    }

    if (!EventEnabled(intercat_sdk_handle, &descriptor))
    {
        return ERROR_SUCCESS;
    }

    process = (LONG)GetCurrentProcessId();
    QueryPerformanceCounter(&ticks);
    EventDataDescCreate(&data[0], &process, sizeof process);
    EventDataDescCreate(&data[1], &channel->id, sizeof channel->id);
    EventDataDescCreate(&data[2], &channel->epoch, sizeof channel->epoch);
    EventDataDescCreate(&data[3], &first, sizeof first);
    EventDataDescCreate(&data[4], &count, sizeof count);
    EventDataDescCreate(&data[5], &consumer, sizeof consumer);
    EventDataDescCreate(&data[6], &logical_length, sizeof logical_length);
    EventDataDescCreate(&data[7], &ticks.QuadPart, sizeof ticks.QuadPart);
    return EventWrite(intercat_sdk_handle, &descriptor, 8, data);
}

#endif /* INTERCAT_SDK_IMPLEMENTATION */

#ifdef __cplusplus
}
#endif

#endif /* INTERCAT_SDK_H */
