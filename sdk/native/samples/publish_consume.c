/*
 * FX-SDK-002: InterCat's cooperative SDK said from a native application through intercat_sdk.h (ADR-105); with --batch,
 * FX-SDK-006: the same messages said in batches of ten (ADR-106), the truth still logging each message by itself; with
 * --content, FX-SDK-008: each message published with its bytes (ADR-107), epoch 1's first five at the edges a capture keeps
 * whole, cuts at --record-limit or never hears, and the truth logging each one's length, SHA-256 and kept prefix's SHA-256
 * to content.jsonl - never a byte.
 *
 * One process opens a channel at epoch 1 and publishes 100 messages of seeded lengths, each consumed by its reader 1; makes
 * the channel again at epoch 2 for 30 more, its sequence from 1; and publishes one message no reader reads. Each epoch is
 * an operation under one of the whole run; the second completes with a failure, as its last message was left unread, and
 * is completed again and its channel closed again, which say nothing. It logs every message it published or consumed - by
 * its channel, epoch, sequence and reader, with its length - to truth-native.jsonl in the directory --truth names, as the
 * .NET truth workloads log theirs, and the run's parameters and operations to scenario.json. It reads no InterCat state.
 * It exits 3 if the library took an argument it should refuse, or said a message's content while no session listened.
 *
 * Usage: publish_consume.exe --truth <directory> [--batch | --content [--record-limit <n>]] [--wait-for-start]
 * With --wait-for-start it waits for a line on its standard input before it says anything, so a capture can name it first.
 *
 * Build: gcc -O2 -municode -I ../include -o publish_consume.exe publish_consume.c -ladvapi32 -lrpcrt4 -lbcrypt
 *    or: cl /O2 /I ..\include publish_consume.c advapi32.lib rpcrt4.lib bcrypt.lib
 */
#define INTERCAT_SDK_IMPLEMENTATION
#include "intercat_sdk.h"

#include <bcrypt.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define EPOCH_ONE_MESSAGES 100
#define EPOCH_TWO_MESSAGES 30

/* How many messages a batch says, with --batch: each epoch's messages are a whole number of batches. */
#define BATCH 10

static FILE *truth;
static FILE *contents;
static long long truth_sequence;
static LARGE_INTEGER started;
static unsigned long long created;
static char channel_text[37];
static unsigned int seed = 20261107u;
static int unwritten;
static int batched;
static int contented;
static long long record_limit = 1024;
static const char *scenario_id = "FX-SDK-002";

/* The marker each message's bytes begin with, with --content, where it fits: bytes no metadata holds. */
static const char marker[] = "FX-SDK-008:";

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

/* The SHA-256 of length bytes as 64 lowercase hex digits, through Windows' own hashing; empty where it cannot be made. */
static void sha256(const BYTE *bytes, ULONG length, char *hex)
{
    static const BYTE none = 0;
    BCRYPT_ALG_HANDLE algorithm = NULL;
    BCRYPT_HASH_HANDLE hash = NULL;
    BYTE digest[32];
    int at;
    hex[0] = '\0';
    if (!BCRYPT_SUCCESS(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, NULL, 0)))
    {
        return;
    }

    if (BCRYPT_SUCCESS(BCryptCreateHash(algorithm, &hash, NULL, 0, NULL, 0, 0))
        && BCRYPT_SUCCESS(BCryptHashData(hash, (PUCHAR)(length != 0 ? bytes : &none), length, 0))
        && BCRYPT_SUCCESS(BCryptFinishHash(hash, digest, sizeof digest, 0)))
    {
        for (at = 0; at < 32; at++)
        {
            snprintf(hex + 2 * at, 3, "%02x", digest[at]);
        }
    }

    if (hash != NULL)
    {
        BCryptDestroyHash(hash);
    }

    BCryptCloseAlgorithmProvider(algorithm, 0);
}

/* A message's bytes, with --content: its marker, epoch and sequence where they fit, and seeded noise after, so no two
 * messages hold the same bytes. The caller frees them. */
static BYTE *message_bytes(long long epoch, long long sequence, long long length)
{
    char named[48];
    int prefix = snprintf(named, sizeof named, "%s%lld-%06lld:", marker, epoch, sequence);
    unsigned long long state = (((unsigned long long)epoch * 0x9E3779B97F4A7C15ull) ^ ((unsigned long long)sequence * 0xBF58476D1CE4E5B9ull)) | 1ull;
    BYTE *bytes = (BYTE *)malloc(length > 0 ? (size_t)length : 1);
    long long at;
    if (bytes == NULL)
    {
        return NULL;
    }

    for (at = 0; at < length; at++)
    {
        if (at < prefix)
        {
            bytes[at] = (BYTE)named[at];
            continue;
        }

        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        bytes[at] = (BYTE)state;
    }

    return bytes;
}

/* What the truth says of a message's content, with --content: its length, its SHA-256 and that of the prefix the record
 * limit keeps where it is longer, and whether the library said its content - never a byte. */
static void log_content(long long epoch, long long sequence, const BYTE *bytes, long long length, int content_said)
{
    char whole[65];
    char prefix[65];
    sha256(bytes, (ULONG)length, whole);
    if (length > record_limit)
    {
        sha256(bytes, (ULONG)record_limit, prefix);
        fprintf(contents, "{\"epoch\":%lld,\"sequence\":%lld,\"length\":%lld,\"sha256\":\"%s\",\"prefixSha256\":\"%s\",\"contentSaid\":%s}\n",
            epoch, sequence, length, whole, prefix, content_said ? "true" : "false");
    }
    else
    {
        fprintf(contents, "{\"epoch\":%lld,\"sequence\":%lld,\"length\":%lld,\"sha256\":\"%s\",\"prefixSha256\":null,\"contentSaid\":%s}\n",
            epoch, sequence, length, whole, content_said ? "true" : "false");
    }
}

/* A message's length: seeded, and with --content, epoch 1's first five at the edges a capture keeps whole, cuts or never
 * hears - nothing, the record limit and a byte past it, the most the library says and a byte past it. */
static long long length_of(long long epoch, long long sequence)
{
    long long length = next_length();
    if (contented && epoch == 1)
    {
        switch (sequence)
        {
        case 1: return 0;
        case 2: return record_limit;
        case 3: return record_limit + 1;
        case 4: return INTERCAT_SDK_MAXIMUM_CONTENT;
        case 5: return INTERCAT_SDK_MAXIMUM_CONTENT + 1;
        default: break;
        }
    }

    return length;
}

/* A message published at the epoch the sample opened its channel at - its own count, never read back from the library -
 * and, unless it is left unread, consumed by reader 1; with --content, published with its bytes. */
static void publish(const intercat_channel *channel, long long epoch, long long sequence, int unread)
{
    long long length = length_of(epoch, sequence);
    if (contented)
    {
        int content_said = 0;
        BYTE *bytes = message_bytes(epoch, sequence, length);
        if (bytes == NULL)
        {
            unwritten++;
            return;
        }

        said(intercat_channel_published_content(channel, sequence, bytes, (size_t)length, length, &content_said));
        log_content(epoch, sequence, bytes, length, content_said);
        free(bytes);
    }
    else
    {
        said(intercat_channel_published(channel, sequence, length, length));
    }

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

/* Every argument InterCat.Sdk throws for, the library refuses, raising nothing and saying no content. */
static int refuses(void)
{
    static const GUID id = { 0x1, 0, 0, { 0, 0, 0, 0, 0, 0, 0, 0 } };
    static const BYTE bytes[1] = { 7 };
    intercat_channel channel;
    intercat_operation operation;
    int said = 1;
    memset(&channel, 0, sizeof channel);
    if (intercat_channel_published_content(NULL, 1, bytes, sizeof bytes, -1, &said) != ERROR_INVALID_PARAMETER || said != 0
        || (said = 1, intercat_channel_published_content(&channel, 1, NULL, 1, -1, &said)) != ERROR_INVALID_PARAMETER || said != 0
        || (said = 1, intercat_channel_published_content(&channel, 1, bytes, sizeof bytes, -2, &said)) != ERROR_INVALID_PARAMETER
        || said != 0)
    {
        return 0;
    }

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

/* While no session listens, a message's content is never said: nothing asks for it. Under a session, it is that session's
 * to hear or not, which a capture of it states. */
static int unheard(void)
{
    static const GUID id = { 0x2, 0, 0, { 0, 0, 0, 0, 0, 0, 0, 0 } };
    static const BYTE bytes[3] = { 1, 2, 3 };
    intercat_channel channel;
    int said = 1;
    memset(&channel, 0, sizeof channel);
    channel.id = id;
    channel.epoch = 1;
    return intercat_sdk_listened()
        || (intercat_channel_published_content(&channel, 1, bytes, sizeof bytes, -1, &said) == ERROR_SUCCESS && said == 0
            && intercat_channel_published_content(&channel, 2, NULL, 0, -1, NULL) == ERROR_SUCCESS);
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
    int waiting = 0;
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
        else if (wcscmp(argv[at], L"--content") == 0)
        {
            contented = 1;
            scenario_id = "FX-SDK-008";
        }
        else if (wcscmp(argv[at], L"--record-limit") == 0 && at + 1 < argc)
        {
            record_limit = _wtoi64(argv[++at]);
        }
        else if (wcscmp(argv[at], L"--wait-for-start") == 0)
        {
            waiting = 1;
        }
    }

    if (directory == NULL || (batched && contented) || record_limit < 1 || record_limit > 1048576)
    {
        fwprintf(stderr, L"--truth <directory> is required: a workload always records its own oracle. --content says messages one by "
            L"one, never in batches, and --record-limit is from 1 to 1,048,576 bytes.\n");
        return 2;
    }

    CreateDirectoryW(directory, NULL);
    if (UuidCreate(&id) != RPC_S_OK || intercat_sdk_start() != ERROR_SUCCESS)
    {
        return 1;
    }

    if (!refuses() || !unheard())
    {
        return 3;
    }

    /* With --wait-for-start, a capture names this process before it says anything: it starts once told to. */
    if (waiting)
    {
        char line[16];
        if (fgets(line, sizeof line, stdin) == NULL)
        {
            return 1;
        }
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

    swprintf(path, MAX_PATH, L"%ls\\content.jsonl", directory);
    if (contented && _wfopen_s(&contents, path, L"w") != 0)
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
    if (contented)
    {
        fclose(contents);
    }

    swprintf(path, MAX_PATH, L"%ls\\scenario.json", directory);
    if (_wfopen_s(&scenario, path, L"w") != 0)
    {
        return 1;
    }

    /* The library is the header's own version, as it announces it. */
    fprintf(scenario,
        "{\n  \"scenarioId\": \"%s\",\n  \"role\": \"native\",\n  \"channel\": \"%s\",\n  \"channelName\": \"fx-sdk-native\",\n"
        "  \"messages\": %d,\n  \"restartMessages\": %d,\n  \"batch\": %d,\n  \"seed\": 20261107,\n  \"library\": \"%ls\",\n"
        "  \"content\": %s,\n  \"marker\": \"%s\",\n  \"recordLimit\": %lld,\n  \"maximumContent\": %d,\n"
        "  \"closeCalls\": [1, 2],\n  \"unwritten\": %d,\n  \"operations\": [\n",
        scenario_id, channel_text, EPOCH_ONE_MESSAGES, EPOCH_TWO_MESSAGES, batched ? BATCH : 1, intercat_sdk_library,
        contented ? "true" : "false", marker, record_limit, INTERCAT_SDK_MAXIMUM_CONTENT, unwritten);
    log_operation(scenario, &run, NULL, "publish-consume", 0, 1, 0);
    log_operation(scenario, &first, &root, "epoch-1", 0, 1, 0);
    log_operation(scenario, &second, &root, "epoch-2", 1, 2, 1);
    fprintf(scenario, "  ]\n}\n");
    fclose(scenario);
    intercat_sdk_stop();
    return 0;
}
