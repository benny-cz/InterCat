/*
 * FX-SDK-002: InterCat's cooperative SDK said from a native application through intercat_sdk.h (ADR-105); with --batch,
 * FX-SDK-006: the same messages said in batches of ten (ADR-106), the truth still logging each message by itself.
 *
 * One process opens a channel at epoch 1 and publishes 100 messages of seeded lengths, each consumed by its reader 1; makes
 * the channel again at epoch 2 for 30 more, its sequence from 1; and publishes one message no reader reads. Each epoch is
 * an operation under one of the whole run; the second completes with a failure, as its last message was left unread, and
 * is completed again and its channel closed again, which say nothing. It logs every message it published or consumed - by
 * its channel, epoch, sequence and reader, with its length - to truth-native.jsonl in the directory --truth names, as the
 * .NET truth workloads log theirs, and the run's parameters and operations to scenario.json. It reads no InterCat state.
 * It exits 3 if the library took an argument it should refuse.
 *
 * Usage: publish_consume.exe --truth <directory> [--batch]
 *
 * Build: gcc -O2 -municode -I ../include -o publish_consume.exe publish_consume.c -ladvapi32 -lrpcrt4
 *    or: cl /O2 /I ..\include publish_consume.c advapi32.lib rpcrt4.lib
 */
#define INTERCAT_SDK_IMPLEMENTATION
#include "intercat_sdk.h"

#include <stdio.h>
#include <string.h>

#define EPOCH_ONE_MESSAGES 100
#define EPOCH_TWO_MESSAGES 30

/* How many messages a batch says, with --batch: each epoch's messages are a whole number of batches. */
#define BATCH 10

static FILE *truth;
static long long truth_sequence;
static LARGE_INTEGER started;
static unsigned long long created;
static char channel_text[37];
static unsigned int seed = 20261107u;
static int unwritten;
static int batched;
static const char *scenario_id = "FX-SDK-002";

/* The next seeded length, from 32 to 4,096 bytes. */
static long long next_length(void)
{
    seed = seed * 1103515245u + 12345u;
    return 32 + (long long)((seed >> 8) % 4065u);
}

static void format_guid(const GUID *guid, char *text)
{
    snprintf(text, 37, "%08lx-%04x-%04x-%02x%02x-%02x%02x%02x%02x%02x%02x", (unsigned long)guid->Data1, guid->Data2, guid->Data3,
        guid->Data4[0], guid->Data4[1], guid->Data4[2], guid->Data4[3], guid->Data4[4], guid->Data4[5], guid->Data4[6], guid->Data4[7]);
}

/* What the library said of a call: a refusal is this sample's fault, a write that failed the session's, counted. */
static void said(ULONG status)
{
    if (status != ERROR_SUCCESS)
    {
        unwritten++;
    }
}

/* One truth record of a message, as the .NET truth workloads write a TruthRecord: call names the batch it was said in, by
 * its first sequence, or is 0 where it was said alone. */
static void log_message(const char *kind, long long epoch, long long sequence, long long consumer, long long length, int unread,
    long long call)
{
    LARGE_INTEGER now;
    SYSTEMTIME utc;
    char reader[24];
    char completed[24];
    char batch[24];
    QueryPerformanceCounter(&now);
    GetSystemTime(&utc);

    /* A consumption names its reader; a publication names the bytes it copied, which here are its length. */
    if (consumer > 0)
    {
        snprintf(reader, sizeof reader, "%lld", consumer);
        snprintf(completed, sizeof completed, "null");
    }
    else
    {
        snprintf(reader, sizeof reader, "null");
        snprintf(completed, sizeof completed, "%lld", length);
    }

    if (call > 0)
    {
        snprintf(batch, sizeof batch, "%lld", call);
    }
    else
    {
        snprintf(batch, sizeof batch, "null");
    }

    fprintf(truth,
        "{\"scenarioId\":\"%s\",\"role\":\"native\",\"sequence\":%lld,\"kind\":\"%s\",\"processId\":%lu,"
        "\"processStartFileTime\":%llu,\"monotonicTicks\":%lld,\"recordedUtc\":\"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ\","
        "\"callId\":%s,\"localPort\":null,\"remotePort\":null,\"resourceName\":\"fx-sdk-native\",\"declaredBytes\":%lld,"
        "\"completedBytes\":%s,\"status\":%s,\"message\":{\"channel\":\"%s\",\"epoch\":%lld,\"sequence\":%lld,\"consumer\":%s}}\n",
        scenario_id, ++truth_sequence, kind, GetCurrentProcessId(), created, now.QuadPart - started.QuadPart, utc.wYear, utc.wMonth,
        utc.wDay, utc.wHour, utc.wMinute, utc.wSecond, utc.wMilliseconds, batch, length, completed, unread ? "\"unread\"" : "null",
        channel_text, epoch, sequence, reader);
}

/* A message published at the epoch the sample opened its channel at - its own count, never read back from the library -
 * and, unless it is left unread, consumed by reader 1. */
static void publish(const intercat_channel *channel, long long epoch, long long sequence, int unread)
{
    long long length = next_length();
    said(intercat_channel_published(channel, sequence, length, length));
    log_message("MessageSent", epoch, sequence, 0, length, unread, 0);
    if (!unread)
    {
        said(intercat_channel_consumed(channel, sequence, length, 1));
        log_message("MessageReceived", epoch, sequence, 1, length, 0, 0);
    }
}

/* A batch of BATCH messages from first on, published in one call - their lengths and copies together - and consumed by
 * reader 1 in one call; the truth logs each message by itself, with the batch as its call. */
static void publish_batch(const intercat_channel *channel, long long epoch, long long first)
{
    long long lengths[BATCH];
    long long total = 0;
    int at;
    for (at = 0; at < BATCH; at++)
    {
        lengths[at] = next_length();
        total += lengths[at];
    }

    said(intercat_channel_published_batch(channel, first, BATCH, total, total));
    for (at = 0; at < BATCH; at++)
    {
        log_message("MessageSent", epoch, first + at, 0, lengths[at], 0, first);
    }

    said(intercat_channel_consumed_batch(channel, first, BATCH, total, 1));
    for (at = 0; at < BATCH; at++)
    {
        log_message("MessageReceived", epoch, first + at, 1, lengths[at], 0, first);
    }
}

/* Each message of an epoch, from 1 to count: alone, or in batches of BATCH. */
static void publish_epoch(const intercat_channel *channel, long long epoch, long long count)
{
    long long sequence;
    for (sequence = 1; sequence <= count; sequence += batched ? BATCH : 1)
    {
        if (batched)
        {
            publish_batch(channel, epoch, sequence);
        }
        else
        {
            publish(channel, epoch, sequence, 0);
        }
    }
}

/* Every argument InterCat.Sdk throws for, the library refuses, raising nothing. */
static int refuses(void)
{
    static const GUID id = { 0x1, 0, 0, { 0, 0, 0, 0, 0, 0, 0, 0 } };
    intercat_channel channel;
    intercat_operation operation;
    memset(&channel, 0, sizeof channel);
    return intercat_channel_open(&channel, &id, L"refused", INTERCAT_CHANNEL_OTHER, 0) == ERROR_INVALID_PARAMETER
        && intercat_channel_open(&channel, &id, L"refused", (intercat_channel_kind)4, 1) == ERROR_INVALID_PARAMETER
        && intercat_channel_open(&channel, &id, NULL, INTERCAT_CHANNEL_OTHER, 1) == ERROR_INVALID_PARAMETER
        && intercat_channel_open(&channel, NULL, L"refused", INTERCAT_CHANNEL_OTHER, 1) == ERROR_INVALID_PARAMETER
        && intercat_channel_published(&channel, 1, -1, -1) == ERROR_INVALID_PARAMETER
        && intercat_channel_published(&channel, 1, 1, -2) == ERROR_INVALID_PARAMETER
        && intercat_channel_consumed(&channel, 1, -1, 1) == ERROR_INVALID_PARAMETER
        && intercat_channel_close(NULL) == ERROR_INVALID_PARAMETER
        && intercat_operation_start(&operation, NULL, NULL) == ERROR_INVALID_PARAMETER
        && intercat_operation_complete(NULL, 0) == ERROR_INVALID_PARAMETER
        && intercat_channel_published_batch(&channel, 1, 0, 1, -1) == ERROR_INVALID_PARAMETER
        && intercat_channel_published_batch(&channel, 1, INTERCAT_SDK_MAXIMUM_BATCH + 1, 1, -1) == ERROR_INVALID_PARAMETER
        && intercat_channel_published_batch(&channel, 1, 2, -1, -1) == ERROR_INVALID_PARAMETER
        && intercat_channel_published_batch(&channel, 1, 2, 1, -2) == ERROR_INVALID_PARAMETER
        && intercat_channel_consumed_batch(&channel, MAXLONGLONG, 2, 1, 1) == ERROR_INVALID_PARAMETER
        && intercat_channel_consumed_batch(NULL, 1, 2, 1, 1) == ERROR_INVALID_PARAMETER;
}

/* One operation as scenario.json names it: the identity the library made for it, the parent the sample started it under
 * or null - as the sample passed it, never read back from the library - its name, its status and how many times the
 * sample completed it. */
static void log_operation(FILE *scenario, const intercat_operation *operation, const GUID *under, const char *name, long long status,
    int completions, int last)
{
    char id[37];
    char named[37];
    char parent[40];
    format_guid(&operation->id, id);
    if (under == NULL)
    {
        snprintf(parent, sizeof parent, "null");
    }
    else
    {
        format_guid(under, named);
        snprintf(parent, sizeof parent, "\"%s\"", named);
    }

    fprintf(scenario, "    { \"operation\": \"%s\", \"parent\": %s, \"name\": \"%s\", \"status\": %lld, \"completions\": %d }%s\n", id,
        parent, name, status, completions, last ? "" : ",");
}

int wmain(int argc, wchar_t **argv)
{
    const wchar_t *directory = NULL;
    wchar_t path[MAX_PATH];
    FILETIME creation, exited, kernel, user;
    GUID id;
    GUID root;
    intercat_channel channel;
    intercat_operation run, first, second;
    FILE *scenario;
    for (int at = 1; at < argc; at++)
    {
        if (wcscmp(argv[at], L"--truth") == 0 && at + 1 < argc)
        {
            directory = argv[++at];
        }
        else if (wcscmp(argv[at], L"--batch") == 0)
        {
            batched = 1;
            scenario_id = "FX-SDK-006";
        }
    }

    if (directory == NULL)
    {
        fwprintf(stderr, L"--truth <directory> is required: a workload always records its own oracle.\n");
        return 2;
    }

    CreateDirectoryW(directory, NULL);
    if (UuidCreate(&id) != RPC_S_OK || intercat_sdk_start() != ERROR_SUCCESS)
    {
        return 1;
    }

    if (!refuses())
    {
        return 3;
    }

    format_guid(&id, channel_text);
    GetProcessTimes(GetCurrentProcess(), &creation, &exited, &kernel, &user);
    created = ((unsigned long long)creation.dwHighDateTime << 32) | creation.dwLowDateTime;
    QueryPerformanceCounter(&started);
    swprintf(path, MAX_PATH, L"%ls\\truth-native.jsonl", directory);
    if (_wfopen_s(&truth, path, L"w") != 0)
    {
        return 1;
    }

    /* The run's operation, whose identity the library makes: each epoch's is started under it, as the sample keeps it. */
    said(intercat_operation_start(&run, L"publish-consume", NULL));
    root = run.id;

    /* Epoch 1: every message published and consumed by reader 1. */
    said(intercat_operation_start(&first, L"epoch-1", &root));
    said(intercat_channel_open(&channel, &id, L"fx-sdk-native", INTERCAT_CHANNEL_OTHER, 1));
    publish_epoch(&channel, 1, EPOCH_ONE_MESSAGES);

    said(intercat_channel_close(&channel));
    said(intercat_operation_complete(&first, 0));

    /* Epoch 2: the channel made again, its sequence from 1, and at its end one message no reader reads - a failure of the
     * epoch's operation, whose second completion says nothing. */
    said(intercat_operation_start(&second, L"epoch-2", &root));
    said(intercat_channel_open(&channel, &id, L"fx-sdk-native", INTERCAT_CHANNEL_OTHER, 2));
    publish_epoch(&channel, 2, EPOCH_TWO_MESSAGES);

    publish(&channel, 2, EPOCH_TWO_MESSAGES + 1, 1);
    said(intercat_channel_close(&channel));
    said(intercat_channel_close(&channel));
    said(intercat_operation_complete(&second, 1));
    said(intercat_operation_complete(&second, 0));
    said(intercat_operation_complete(&run, 0));
    fclose(truth);

    swprintf(path, MAX_PATH, L"%ls\\scenario.json", directory);
    if (_wfopen_s(&scenario, path, L"w") != 0)
    {
        return 1;
    }

    fprintf(scenario,
        "{\n  \"scenarioId\": \"%s\",\n  \"channel\": \"%s\",\n  \"channelName\": \"fx-sdk-native\",\n"
        "  \"messages\": %d,\n  \"restartMessages\": %d,\n  \"batch\": %d,\n  \"seed\": 20261107,\n  \"library\": \"native-1.1\",\n"
        "  \"closeCalls\": [1, 2],\n  \"unwritten\": %d,\n  \"operations\": [\n",
        scenario_id, channel_text, EPOCH_ONE_MESSAGES, EPOCH_TWO_MESSAGES, batched ? BATCH : 1, unwritten);
    log_operation(scenario, &run, NULL, "publish-consume", 0, 1, 0);
    log_operation(scenario, &first, &root, "epoch-1", 0, 1, 0);
    log_operation(scenario, &second, &root, "epoch-2", 1, 2, 1);
    fprintf(scenario, "  ]\n}\n");
    fclose(scenario);
    intercat_sdk_stop();
    return 0;
}
