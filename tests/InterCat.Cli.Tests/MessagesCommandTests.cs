using System.Text.Json;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat messages (ADR-104): what applications instrumented with InterCat's SDK said of their channels, each channel's epoch
/// named by its opening and each message by its sequence, with what became of it and who read it, in words and as a document.
/// </summary>
public sealed class MessagesCommandTests : IDisposable
{
    private static readonly Guid Frames = Guid.Parse("6f1c8f4e-2b8e-4a40-9f7c-0d6a7f3e5a11");

    private readonly TemporarySession session = new();

    public MessagesCommandTests()
    {
        // A producer opens the frames ring and publishes two frames; two readers consume the first, none the second.
        var rows = new List<ObservationRowV1>
        {
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\producer.exe" },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\reader.exe" },
            Lifecycle(3, ObservationKind.Create, 201, 3) with { ResourceName = @"C:\Tools\reader.exe" },
        };
        var fields = new List<SourceFieldRowV1>();
        void Add(ObservationRowV1 row, params (SourceField Code, long Value)[] carried)
        {
            rows.Add(row);
            fields.AddRange(carried.Select(field => Field(row, field.Code, field.Value)));
        }

        Add(SdkRecord(10, CooperativeSdk.ChannelOpened, 100, 10, Frames) with { ResourceName = "frames" },
            (SourceField.ChannelEpoch, 1), (SourceField.ChannelKind, 3));
        Add(SdkRecord(20, CooperativeSdk.MessagePublished, 100, 11, Frames, 4_096),
            (SourceField.ChannelEpoch, 1), (SourceField.MessageSequence, 1), (SourceField.CopiedLength, -1));
        Add(SdkRecord(30, CooperativeSdk.MessageConsumed, 200, 12, Frames, 4_096),
            (SourceField.ChannelEpoch, 1), (SourceField.MessageSequence, 1), (SourceField.ConsumerNumber, 1));
        Add(SdkRecord(31, CooperativeSdk.MessageConsumed, 201, 13, Frames, 4_096),
            (SourceField.ChannelEpoch, 1), (SourceField.MessageSequence, 1), (SourceField.ConsumerNumber, 2));
        Add(SdkRecord(40, CooperativeSdk.MessagePublished, 100, 14, Frames, 512),
            (SourceField.ChannelEpoch, 1), (SourceField.MessageSequence, 2), (SourceField.CopiedLength, 512));
        _ = Publish(session.Store, rows, fields: fields);
        session.Store.ReleaseSegmentReaders();
    }

    public void Dispose() => session.Dispose();

    [Fact(DisplayName = "R18: icat messages lists each channel's epoch by the name its opening gives it, with its messages counted by what became of them, and says what the capture covered of the SDK")]
    public async Task ItListsEachChannel()
    {
        (InterCatExitCode code, string output) = await Run("messages", session.Path);
        Assert.Equal(InterCatExitCode.Success, code);
        string words = System.Text.RegularExpressions.Regex.Replace(output, @"\s+", " ");
        Assert.Contains("COOPERATIVE MESSAGES", words, StringComparison.Ordinal);
        Assert.Contains("Messages 2 in 1 channel epoch", words, StringComparison.Ordinal);
        Assert.Contains($"{Frames} 1 frames shared memory 2 1 1 0 0 2 4,608 11.0 µs after", words, StringComparison.Ordinal);
        Assert.Contains("Application SDK's coverage over the session is unknown: this generation publishes no coverage ledger.",
            words, StringComparison.Ordinal);
        Assert.DoesNotContain("the first", words, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R18: icat messages --channel lists a channel's messages - each one's publisher, length and readers, each reader in its process - and says what became of each in words")]
    public async Task ItListsAChannelsMessages()
    {
        (InterCatExitCode code, string output) = await Run("messages", session.Path, "--channel", Frames.ToString());
        Assert.Equal(InterCatExitCode.Success, code);
        string words = System.Text.RegularExpressions.Regex.Replace(output, @"\s+", " ");
        Assert.Contains("frames, epoch 1: the first 2 of 2 messages", words, StringComparison.Ordinal);
        Assert.Contains("Its consumptions were said 11.0 µs after their publications at the median, from 10.0 µs after to 11.0 µs after.",
            words, StringComparison.Ordinal);
        Assert.Contains("1 consumed producer.exe · 100 4,096 reader 1 in reader.exe · 200, 10.0 µs after its publication was said, "
            + "reader 2 in reader.exe · 201, 11.0 µs after its publication was said", words, StringComparison.Ordinal);
        Assert.Contains("2 open: no reader consumed it producer.exe · 100 512 no reader", words, StringComparison.Ordinal);

        // Another channel's identity lists none, and says so.
        (_, string none) = await Run("messages", session.Path, "--channel", Guid.Empty.ToString());
        Assert.Contains("No channel of this identity holds a message or an opening.", none, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R18: icat messages --json is the cooperative-messages-v1 document: each channel's epoch, and with --channel its messages, each record's process and the copy its publisher knew")]
    public async Task ItsDocumentHoldsTheMessages()
    {
        (InterCatExitCode code, string output) = await Run("messages", session.Path, "--channel", Frames.ToString(), "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.Equal(("cooperative-messages-v1", "cooperative-message-v2", 0L),
            (root.GetProperty("contract").GetString(), root.GetProperty("messageRule").GetString(), root.GetProperty("withoutMessage").GetInt64()));
        JsonElement channel = Assert.Single(root.GetProperty("channels").EnumerateArray());
        Assert.Equal(("frames", "shared memory", 2, 1, 1, 2), (channel.GetProperty("name").GetString(), channel.GetProperty("kind").GetString(),
            channel.GetProperty("messages").GetInt32(), channel.GetProperty("consumed").GetInt32(), channel.GetProperty("unconsumed").GetInt32(),
            channel.GetProperty("readers").GetInt32()));
        JsonElement[] messages = [.. channel.GetProperty("list").EnumerateArray()];
        Assert.Equal(["Consumed", "Unconsumed"], messages.Select(message => message.GetProperty("state").GetString()));
        Assert.Equal(JsonValueKind.Null, messages[0].GetProperty("copiedLength").ValueKind);
        Assert.Equal(512, messages[1].GetProperty("copiedLength").GetInt64());
        JsonElement publication = messages[0].GetProperty("publication");
        Assert.Equal((100, @"C:\Tools\producer.exe"), (publication.GetProperty("processId").GetInt32(),
            publication.GetProperty("process").GetProperty("imagePath").GetString()));
        Assert.Equal([1L, 2L], messages[0].GetProperty("consumptions").EnumerateArray().Select(consumption => consumption.GetProperty("consumer").GetInt64()));

        // Each consumption's lag after its publication was said, and the epoch's spread of them.
        Assert.Equal([10_000L, 11_000L], messages[0].GetProperty("consumptions").EnumerateArray()
            .Select(consumption => consumption.GetProperty("lagNanoseconds").GetInt64()));
        JsonElement lag = channel.GetProperty("lag");
        Assert.Equal((10_000L, 11_000L, 11_000L, 2L, 0L), (lag.GetProperty("leastNanoseconds").GetInt64(), lag.GetProperty("medianNanoseconds").GetInt64(),
            lag.GetProperty("mostNanoseconds").GetInt64(), lag.GetProperty("consumptions").GetInt64(), lag.GetProperty("saidBefore").GetInt64()));

        // A message said alone is one sequence, its record no batch.
        Assert.Equal((1L, JsonValueKind.Null), (messages[0].GetProperty("count").GetInt64(), publication.GetProperty("batch").ValueKind));
    }

    [Fact(DisplayName = "R18: icat messages lists a run of messages said in batches as one row - its first and last sequence, its batch's length together and its readers - counts each of its messages, and its document says the run's count and each record's batch")]
    public async Task ItListsARunAsOneRow()
    {
        using var batched = new TemporarySession();
        var rows = new List<ObservationRowV1>
        {
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\producer.exe" },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\reader.exe" },
        };
        var fields = new List<SourceFieldRowV1>();
        void Add(ObservationRowV1 row, params (SourceField Code, long Value)[] carried)
        {
            rows.Add(row);
            fields.AddRange(carried.Select(field => Field(row, field.Code, field.Value)));
        }

        // Fifty frames published in one batch and read in one batch by reader 1.
        Add(SdkRecord(10, CooperativeSdk.ChannelOpened, 100, 10, Frames) with { ResourceName = "frames" },
            (SourceField.ChannelEpoch, 1), (SourceField.ChannelKind, 3));
        Add(SdkRecord(20, CooperativeSdk.MessagesPublished, 100, 11, Frames, 6_400),
            (SourceField.ChannelEpoch, 1), (SourceField.MessageSequence, 1), (SourceField.MessageCount, 50));
        Add(SdkRecord(30, CooperativeSdk.MessagesConsumed, 200, 12, Frames, 6_400),
            (SourceField.ChannelEpoch, 1), (SourceField.MessageSequence, 1), (SourceField.MessageCount, 50), (SourceField.ConsumerNumber, 1));
        _ = Publish(batched.Store, rows, fields: fields);
        batched.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output) = await Run("messages", batched.Path, "--channel", Frames.ToString());
        Assert.Equal(InterCatExitCode.Success, code);
        string words = System.Text.RegularExpressions.Regex.Replace(output, @"\s+", " ");
        Assert.Contains("Messages 50 in 1 channel epoch", words, StringComparison.Ordinal);
        Assert.Contains("frames, epoch 1: the first 1 of 1 rows of its 50 messages, a run said in batches one row", words, StringComparison.Ordinal);
        Assert.Contains("1 to 50 consumed producer.exe · 100 6,400 in a batch of 50 reader 1 in reader.exe · 200", words, StringComparison.Ordinal);

        (_, string json) = await Run("messages", batched.Path, "--channel", Frames.ToString(), "--json");
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement channel = Assert.Single(document.RootElement.GetProperty("channels").EnumerateArray());
        Assert.Equal((50L, 50L, 6_400L), (channel.GetProperty("messages").GetInt64(), channel.GetProperty("consumed").GetInt64(),
            channel.GetProperty("publishedBytes").GetInt64()));
        JsonElement run = Assert.Single(channel.GetProperty("list").EnumerateArray());
        Assert.Equal((1L, 50L, 50L, 50L), (run.GetProperty("sequence").GetInt64(), run.GetProperty("count").GetInt64(),
            run.GetProperty("publication").GetProperty("batch").GetInt64(),
            run.GetProperty("consumptions")[0].GetProperty("record").GetProperty("batch").GetInt64()));
    }

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
