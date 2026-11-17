using System.Text.Json;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat messages says what a capture kept of each message's bytes (ADR-112): by the content record its publisher said of it -
/// whole, cut by the record limit, omitted at the content limit, released since, or why none was said - beside each message
/// and in its JSON, and never a byte of them.
/// </summary>
public sealed class MessagesContentTests : IDisposable
{
    private static readonly Guid Frames = Guid.Parse("6f1c8f4e-2b8e-4a40-9f7c-0d6a7f3e5a11");

    private readonly TemporarySession asked = new();
    private readonly TemporarySession unasked = new();
    private ulong firstContent;

    public MessagesContentTests()
    {
        foreach ((TemporarySession session, bool content) in new[] { (asked, true), (unasked, false) })
        {
            var rows = new List<ObservationRowV1>
            {
                Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\publisher.exe" },
            };
            var fields = new List<SourceFieldRowV1>();
            var kept = new List<(ContentFragmentV1 Fragment, ReadOnlyMemory<byte> Bytes)>();
            ulong ordinal = 10;
            ObservationRowV1 Said(long ticks, ushort eventId, long sequence, long? length = null, long? count = null)
            {
                ObservationRowV1 row = SdkRecord(ticks, eventId, 100, ++ordinal, Frames, length);
                rows.Add(row);
                fields.Add(Field(row, SourceField.ChannelEpoch, 1));
                fields.Add(Field(row, SourceField.MessageSequence, sequence));
                if (count is { } many)
                {
                    fields.Add(Field(row, SourceField.MessageCount, many));
                }

                return row;
            }

            // 1 kept whole, 2 cut by the record limit, 3 omitted at the content limit, 4 released since, 5 said without its
            // bytes, 6 and 7 a batch, and content naming a message nothing published.
            _ = Said(10, CooperativeSdk.MessagePublished, 1, 8);
            ObservationRowV1 whole = Said(11, CooperativeSdk.MessageContent, 1);
            kept.Add(Content(whole, "12345678"u8.ToArray(), 4_096, ContentEncodingV1.Binary));
            firstContent = whole.RawRecordOrdinal;
            _ = Said(20, CooperativeSdk.MessagePublished, 2, 10_000);
            kept.Add(Content(Said(21, CooperativeSdk.MessageContent, 2), new byte[10_000], 4_096, ContentEncodingV1.Binary));
            _ = Said(30, CooperativeSdk.MessagePublished, 3, 64);
            kept.Add((Content(Said(31, CooperativeSdk.MessageContent, 3), new byte[64], 4_096, ContentEncodingV1.Binary).Fragment with
            {
                Disposition = ContentDispositionV1.OmittedBySessionLimit,
                Kept = 0,
            }, ReadOnlyMemory<byte>.Empty));
            _ = Said(40, CooperativeSdk.MessagePublished, 4, 8);
            _ = Said(41, CooperativeSdk.MessageContent, 4);
            _ = Said(50, CooperativeSdk.MessagePublished, 5, 8);
            _ = Said(60, CooperativeSdk.MessagesPublished, 6, 16, count: 2);
            _ = Said(70, CooperativeSdk.MessageContent, 99);

            // 8: two content records name it, none chosen; 9: consumed, its publication not recorded.
            _ = Said(80, CooperativeSdk.MessagePublished, 8, 8);
            _ = Said(81, CooperativeSdk.MessageContent, 8);
            _ = Said(82, CooperativeSdk.MessageContent, 8);
            ObservationRowV1 consumed = Said(90, CooperativeSdk.MessageConsumed, 9, 8);
            fields.Add(Field(consumed, SourceField.ConsumerNumber, 1));

            // The plan the capture admitted under: the SDK's events, its content's only where the capture asked for it.
            ushort[] admitted = [.. Enumerable.Range(2, content ? 9 : 8).Select(static id => (ushort)id)];
            (ContentChunkHeaderV1 Header, IReadOnlyList<(ContentFragmentV1 Fragment, ReadOnlyMemory<byte> Bytes)> Fragments)? chunk =
                content ? (ContentHeader(recordLimit: 4_096), kept) : null;
            _ = Publish(session.Store, [.. rows], fields: fields, plan: Plan(admitted), content: chunk);
            session.Store.ReleaseSegmentReaders();
        }
    }

    public void Dispose()
    {
        asked.Dispose();
        unasked.Dispose();
    }

    [Fact(DisplayName = "R18: icat messages says what a capture kept of each message's bytes - whole, cut by the record limit, omitted at the content limit, released, or why none was said - beside each message and in its JSON, and never a byte of them")]
    public async Task ItSaysWhatWasKeptOfEachMessage()
    {
        (InterCatExitCode code, string output) = await Run("messages", asked.Path, "--channel", Frames.ToString());
        Assert.Equal(InterCatExitCode.Success, code);
        string words = System.Text.RegularExpressions.Regex.Replace(output, @"\s+", " ");
        Assert.Contains("1 content record named no message its own process published once", words, StringComparison.Ordinal);
        Assert.Contains("Content kept", words, StringComparison.Ordinal);
        foreach (string row in new[]
        {
            "1 open: no reader consumed it publisher.exe · 100 8 no reader 8 bytes, whole",
            "2 open: no reader consumed it publisher.exe · 100 10,000 no reader the first 4,096 of 10,000 bytes, cut by the record limit",
            "3 open: no reader consumed it publisher.exe · 100 64 no reader none: the content limit was reached",
            "4 open: no reader consumed it publisher.exe · 100 8 no reader none kept now: released since",
            "5 open: no reader consumed it publisher.exe · 100 8 no reader none: published without its bytes",
        })
        {
            Assert.Contains(row, words, StringComparison.Ordinal);
        }

        Assert.Contains("none: a batch says no message's bytes", words, StringComparison.Ordinal);
        Assert.Contains("8 open: no reader consumed it publisher.exe · 100 8 no reader unknown: 2 content records name it, none chosen",
            words, StringComparison.Ordinal);
        Assert.Contains("none: its publication was not recorded", words, StringComparison.Ordinal);

        // The JSON holds each message's content record and its fragment's facts, never a byte of it.
        (_, string json) = await Run("messages", asked.Path, "--channel", Frames.ToString(), "--json");
        Assert.DoesNotContain("12345678", json, StringComparison.Ordinal);
        Assert.DoesNotContain("MTIzNDU2Nzg", json, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(("cooperative-message-content-v1", 1), (root.GetProperty("contentRule").GetString(), root.GetProperty("contentApart").GetInt32()));
        JsonElement[] messages = [.. Assert.Single(root.GetProperty("channels").EnumerateArray()).GetProperty("list").EnumerateArray()];
        Assert.Equal(
            [
                (1, "Whole", (int?)8, (long?)8, (bool?)true),
                (1, "TruncatedByRecordLimit", 4_096, 10_000, true),
                (1, "OmittedBySessionLimit", 0, 64, true),
                (1, null, null, null, null),
                (0, null, null, null, null),
                (0, null, null, null, null),
                (2, null, null, null, null),
                (0, null, null, null, null),
            ],
            messages.Select(static message => message.GetProperty("content")).Select(static content => (
                content.GetProperty("contentRecords").GetInt32(),
                content.GetProperty("disposition").GetString(),
                content.GetProperty("keptBytes").ValueKind == JsonValueKind.Null ? (int?)null : content.GetProperty("keptBytes").GetInt32(),
                content.GetProperty("originalBytes").ValueKind == JsonValueKind.Null ? (long?)null : content.GetProperty("originalBytes").GetInt64(),
                content.GetProperty("inspectable").ValueKind == JsonValueKind.Null ? (bool?)null : content.GetProperty("inspectable").GetBoolean())));
        JsonElement first = messages[0].GetProperty("content");
        Assert.Equal(("8 bytes, whole", firstContent), (first.GetProperty("said").GetString(), first.GetProperty("record").GetProperty("ordinal").GetUInt64()));
        Assert.Equal(JsonValueKind.Null, messages[4].GetProperty("content").GetProperty("record").ValueKind);
        Assert.Equal(JsonValueKind.Null, messages[6].GetProperty("content").GetProperty("record").ValueKind);

        // A capture that asked for no content of the SDK's says none of it, in the listing or its JSON.
        (_, string plain) = await Run("messages", unasked.Path, "--channel", Frames.ToString());
        Assert.DoesNotContain("Content kept", plain, StringComparison.Ordinal);
        (_, string plainJson) = await Run("messages", unasked.Path, "--channel", Frames.ToString(), "--json");
        using JsonDocument unaskedDocument = JsonDocument.Parse(plainJson);
        Assert.Equal((JsonValueKind.Null, 0), (unaskedDocument.RootElement.GetProperty("contentRule").ValueKind,
            unaskedDocument.RootElement.GetProperty("contentApart").GetInt32()));
        Assert.All(Assert.Single(unaskedDocument.RootElement.GetProperty("channels").EnumerateArray()).GetProperty("list").EnumerateArray(),
            static message => Assert.Equal(JsonValueKind.Null, message.GetProperty("content").ValueKind));
    }

    /// <summary>The normalizer plan a capture admitting <paramref name="events"/> of the SDK's retains.</summary>
    private static byte[] Plan(params ushort[] events) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        contract = "normalizer-plan-v1",
        descriptors = events.Select(id => new { providerGuid = CooperativeSdk.ProviderId, eventId = id, mechanism = (int)Mechanism.ApplicationSdk }),
    });

    private static async Task<(InterCatExitCode Code, string Output)> Run(params string[] args)
    {
        TextWriter output = Console.Out;
        using var answered = new StringWriter();
        Console.SetOut(answered);
        try
        {
            InterCatExitCode code = await Icat.RunAsync(args, CancellationToken.None);
            return (code, answered.ToString());
        }
        finally
        {
            Console.SetOut(output);
        }
    }
}
