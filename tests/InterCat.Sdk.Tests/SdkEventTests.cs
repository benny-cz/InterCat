using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.Json;
using System.Xml.Linq;
using InterCat.Sdk;
using Xunit;

namespace InterCat.Sdk.Tests;

/// <summary>
/// The cooperative SDK (`contracts/sdk-v1.md`, ADR-102): what an application's calls raise, field by field, through the
/// provider a capture reads - its schema announced first, every event naming its own process, a message by its channel's
/// epoch and sequence, a reader by its own identity - and nothing more than once.
/// </summary>
public sealed class SdkEventTests
{
    private static readonly Guid Frames = Guid.Parse("6f1c8f4e-2b8e-4a40-9f7c-0d6a7f3e5a11");

    [Fact(DisplayName = "§15: the SDK announces its schema and its clock's frequency as a session enables it, and each call raises its event at the schema's version, naming its own process first and the channel by its epoch and sequence")]
    public void EachCallRaisesItsEvent()
    {
        using var recorder = new Recorder();
        recorder.EnableEvents(InterCatSdkEventSource.Log, EventLevel.Verbose);
        int process = Environment.ProcessId;
        long before = Stopwatch.GetTimestamp();
        var parent = Guid.Parse("0b4f1d25-5e63-4b77-8f0e-9c1d2a3b4c5d");
        InterCatOperation render;
        using (InterCatChannel channel = InterCatChannel.Open(Frames, "frames", InterCatChannelKind.SharedMemory, epoch: 2))
        {
            channel.Published(7, 4_096, 4_096);
            channel.Consumed(7, 4_096, consumer: 3);
            render = InterCatOperation.Start("render", parent);
            render.Complete(status: 3);
        }

        EventWrittenEventArgs[] raised = recorder.Raised();

        // The schema is said before anything else the session hears from the process.
        EventWrittenEventArgs started = raised[0];
        Assert.Equal((InterCatSdkEventSource.SdkStartedId, "SdkStarted"), (started.EventId, started.EventName));
        Assert.Equal<object?>(
            [process, InterCatSdkEventSource.SchemaMajor, InterCatSdkEventSource.SchemaMinor, Stopwatch.Frequency, InterCatSdkEventSource.Library],
            started.Payload!);

        // Then each call's event, in order, each at the schema's version and naming the raising process first.
        Assert.Equal(
            [InterCatSdkEventSource.ChannelOpenedId, InterCatSdkEventSource.MessagePublishedId, InterCatSdkEventSource.MessageConsumedId,
                InterCatSdkEventSource.OperationStartedId, InterCatSdkEventSource.OperationCompletedId, InterCatSdkEventSource.ChannelClosedId],
            raised[1..].Select(written => written.EventId));
        Assert.All(raised, written =>
        {
            Assert.Equal(InterCatSdkEventSource.EventVersion, written.Version);
            Assert.Equal(process, written.Payload![0]);
        });
        Assert.Equal<object?>([process, Frames, 2L, (int)InterCatChannelKind.SharedMemory, "frames"], raised[1].Payload!);
        Assert.Equal<object?>([process, Frames, 2L, 7L, 4_096L, 4_096L], raised[2].Payload!.Take(6));
        Assert.Equal<object?>([process, Frames, 2L, 7L, 3L, 4_096L], raised[3].Payload!.Take(6));
        Assert.Equal<object?>([process, render.Id], raised[4].Payload!.Take(2));
        Assert.Equal("render", raised[4].Payload![^1]);
        Assert.Equal(parent, raised[4].RelatedActivityId);
        Assert.Equal<object?>([process, render.Id, 3L], raised[5].Payload!.Take(3));
        Assert.Equal<object?>([process, Frames, 2L], raised[6].Payload!);

        // A transition's reading is the process's own clock at it.
        Assert.All(new[] { raised[2], raised[3], raised[4], raised[5] }, written =>
            Assert.InRange((long)written.Payload![written.PayloadNames!.IndexOf("sourceTicks")]!, before, Stopwatch.GetTimestamp()));
    }

    [Fact(DisplayName = "§15: a session listening before a process first uses the SDK - a capture already running as an application starts - is told the schema with the library's version, as one enabling it later is")]
    public async Task AnEarlyListenerHearsTheLibrary()
    {
        // A process of its own, whose first use of the SDK comes under a listener already listening: this one has made its
        // provider already, and a process holds one provider of a name.
        string truth = Path.Combine(Path.GetTempPath(), "sdk-first-use-" + Guid.NewGuid().ToString("N"));
        try
        {
            await RunWorkloadAsync("sdk-first-use", truth);

            // Every announcement it heard names the library, as the SDK names it - never nothing.
            using JsonDocument heard = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(truth, "first-use.json")));
            string library = heard.RootElement.GetProperty("library").GetString()!;
            Assert.Equal(InterCatSdkEventSource.Library, library);
            Assert.NotEqual(string.Empty, library);
            string?[] announced = [.. heard.RootElement.GetProperty("announced").EnumerateArray().Select(element => element.GetString())];
            Assert.NotEmpty(announced);
            Assert.All(announced, said => Assert.Equal(library, said));
        }
        finally
        {
            if (Directory.Exists(truth))
            {
                Directory.Delete(truth, recursive: true);
            }
        }
    }

    [Fact(DisplayName = "§15: a message published with its bytes is said with its content right after its publication - its whole bytes, at the schema's minor 2 - up to the most the SDK says, a longer one published without it, and a call refuses a copy no message could have")]
    public void AMessagesContentFollowsItsPublication()
    {
        using var recorder = new Recorder();
        recorder.EnableEvents(InterCatSdkEventSource.Log, EventLevel.Verbose);
        int process = Environment.ProcessId;
        byte[] within = Message(11, 300);
        byte[] past = Message(12, InterCatChannel.MaximumContent + 1);
        byte[] most = Message(13, InterCatChannel.MaximumContent);
        bool[] said;
        using (InterCatChannel channel = InterCatChannel.Open(Frames, "frames", InterCatChannelKind.Pipe, epoch: 4))
        {
            said = [channel.Published(11, within, copiedLength: 304), channel.Published(12, past), channel.Published(13, most), channel.Published(14, [])];
        }

        // Its content is said where it fits whole, never in part.
        Assert.Equal([true, false, true, true], said);
        Assert.Equal((32_768, 2), (InterCatChannel.MaximumContent, InterCatSdkEventSource.SchemaMinor));
        EventWrittenEventArgs[] raised = [.. recorder.Raised().Where(written => written.EventId is InterCatSdkEventSource.MessagePublishedId
            or InterCatSdkEventSource.MessageContentId)];
        Assert.Equal(
            [(4, 11L), (10, 11L), (4, 12L), (4, 13L), (10, 13L), (4, 14L), (10, 14L)],
            raised.Select(written => (written.EventId, (long)written.Payload![3]!)));

        // Each publication says its length, the bytes it holds, and the copy where given; each content the bytes themselves,
        // under the content keyword alone, at the schema's major.
        Assert.Equal([300L, InterCatChannel.MaximumContent + 1L, InterCatChannel.MaximumContent, 0L],
            raised.Where(written => written.EventId == InterCatSdkEventSource.MessagePublishedId).Select(written => (long)written.Payload![4]!));
        Assert.Equal<object?>([process, Frames, 4L, 11L, 300L, 304L], raised[0].Payload!.Take(6));
        Assert.Equal(-1L, raised[2].Payload![5]);
        EventWrittenEventArgs[] contents = [.. raised.Where(written => written.EventId == InterCatSdkEventSource.MessageContentId)];
        Assert.Equal(["processId", "channel", "epoch", "sequence", "content"], contents[0].PayloadNames!);
        Assert.Equal<object?>([process, Frames, 4L, 11L], contents[0].Payload!.Take(4));
        Assert.Equal([within, most, []], contents.Select(written => (byte[])written.Payload![4]!));
        Assert.All(contents, written =>
        {
            // Its keywords the SDK's Content and, past the SDK's own, the session keywords EventSource marks every event with.
            Assert.Equal(("MessageContent", (long)InterCatSdkEventSource.Keywords.Content, EventLevel.Informational, InterCatSdkEventSource.EventVersion),
                (written.EventName, (long)written.Keywords & 0xFFFF_FFFF, written.Level, written.Version));
        });

        // A copy no message could have is refused, with or without a session.
        using InterCatChannel quiet = InterCatChannel.Open(Frames, "frames", InterCatChannelKind.Socket);
        Assert.Throws<ArgumentOutOfRangeException>(() => quiet.Published(15, within, copiedLength: -2));
    }

    [Fact(DisplayName = "§15: a message's content is said only to a session asking for it - never while nothing listens, nor to one keeping no content, which hears every publication beside one asking - in a process no listener enabled before")]
    public async Task ContentIsNeverSaidUnasked()
    {
        // A process of its own: a listener enables an EventSource's keywords for the life of its process, so this one, whose
        // listeners asked for content already, cannot show what is said where none asks.
        string truth = Path.Combine(Path.GetTempPath(), "sdk-content-" + Guid.NewGuid().ToString("N"));
        try
        {
            await RunWorkloadAsync("sdk-content", truth);
            using JsonDocument heard = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(truth, "content.json")));
            JsonElement root = heard.RootElement;
            Assert.Equal(InterCatChannel.MaximumContent, root.GetProperty("maximumContent").GetInt32());

            // What each call says of itself: its content said only where a listener asked for it and the message fits.
            JsonElement[] calls = [.. root.GetProperty("calls").EnumerateArray()];
            Assert.Equal(
                [("unheard", false), ("metadata", false), ("content", true), ("past the most", false), ("the most", true), ("empty", true)],
                calls.Select(call => (call.GetProperty("name").GetString()!, call.GetProperty("said").GetBoolean())));

            // A listener keeping no content hears every publication made while it listened, its length the message's, and
            // no content - even beside one asking for it.
            Assert.Equal(
                calls.Skip(1).Select(call => (InterCatSdkEventSource.MessagePublishedId, call.GetProperty("sequence").GetInt64(), (long)call.GetProperty("length").GetInt32())),
                root.GetProperty("metadataHeard").EnumerateArray()
                    .Select(said => (said.GetProperty("id").GetInt32(), said.GetProperty("sequence").GetInt64(), said.GetProperty("logicalLength").GetInt64())));

            // One asking for content hears each publication, then each content the call said, the message's own bytes.
            Dictionary<long, JsonElement> bySequence = calls.ToDictionary(call => call.GetProperty("sequence").GetInt64());
            JsonElement[] content = [.. root.GetProperty("contentHeard").EnumerateArray()];
            Assert.Equal(
                [(4, 3L), (10, 3L), (4, 4L), (4, 5L), (10, 5L), (4, 6L), (10, 6L)],
                content.Select(said => (said.GetProperty("id").GetInt32(), said.GetProperty("sequence").GetInt64())));
            Assert.All(content.Where(said => said.GetProperty("id").GetInt32() == InterCatSdkEventSource.MessageContentId), said =>
            {
                JsonElement call = bySequence[said.GetProperty("sequence").GetInt64()];
                Assert.Equal((call.GetProperty("length").GetInt32(), call.GetProperty("sha256").GetString()),
                    (said.GetProperty("contentLength").GetInt32(), said.GetProperty("contentSha256").GetString()));
            });
        }
        finally
        {
            if (Directory.Exists(truth))
            {
                Directory.Delete(truth, recursive: true);
            }
        }
    }

    [Fact(DisplayName = "§15: a channel's close and an operation's completion are said once however often asked, a publication's unknown copy is -1, and a call refuses what no message could be")]
    public void NothingIsSaidTwice()
    {
        using (var recorder = new Recorder())
        {
            recorder.EnableEvents(InterCatSdkEventSource.Log, EventLevel.Verbose);
            InterCatChannel channel = InterCatChannel.Open(Frames, "frames", InterCatChannelKind.Pipe);
            channel.Published(1, 12);
            channel.Dispose();
            channel.Dispose();
            InterCatOperation operation = InterCatOperation.Start("flush");
            operation.Complete();
            operation.Complete(status: 5);

            EventWrittenEventArgs[] raised = recorder.Raised();
            Assert.Single(raised, written => written.EventId == InterCatSdkEventSource.ChannelClosedId);
            EventWrittenEventArgs completed = Assert.Single(raised, written => written.EventId == InterCatSdkEventSource.OperationCompletedId);
            Assert.Equal(0L, completed.Payload![2]);
            Assert.Equal(-1L, raised.Single(written => written.EventId == InterCatSdkEventSource.MessagePublishedId).Payload![5]);
            Assert.Equal(Guid.Empty, raised.Single(written => written.EventId == InterCatSdkEventSource.OperationStartedId).RelatedActivityId);
            Assert.Equal(1L, raised.Single(written => written.EventId == InterCatSdkEventSource.ChannelOpenedId).Payload![2]);
        }

        // A call refuses what no message could be, with or without a session.
        using InterCatChannel quiet = InterCatChannel.Open(Frames, "frames", InterCatChannelKind.Socket);
        quiet.Published(1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => quiet.Published(2, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => InterCatChannel.Open(Frames, "frames", InterCatChannelKind.Pipe, epoch: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => InterCatChannel.Open(Frames, "frames", (InterCatChannelKind)9));
    }

    [Fact(DisplayName = "§15: a batch says its run in one event - its first sequence, its count and its messages' lengths together, a reader's consumption of each - at the schema's minor 1, and a call refuses a batch no SDK says")]
    public void ABatchSaysItsRunInOneEvent()
    {
        using var recorder = new Recorder();
        recorder.EnableEvents(InterCatSdkEventSource.Log, EventLevel.Verbose);
        int process = Environment.ProcessId;
        long before = Stopwatch.GetTimestamp();
        using (InterCatChannel channel = InterCatChannel.Open(Frames, "frames", InterCatChannelKind.SharedMemory, epoch: 3))
        {
            channel.PublishedBatch(101, 64, logicalLength: 32_768, copiedLength: 33_024);
            channel.ConsumedBatch(101, 64, logicalLength: 32_768, consumer: 2);
            channel.PublishedBatch(165, 1, logicalLength: 0);
        }

        EventWrittenEventArgs[] raised = recorder.Raised();
        EventWrittenEventArgs[] batches = [.. raised.Where(written => written.EventId is InterCatSdkEventSource.MessagesPublishedId
            or InterCatSdkEventSource.MessagesConsumedId)];
        Assert.Equal(["MessagesPublished", "MessagesConsumed", "MessagesPublished"], batches.Select(written => written.EventName));
        Assert.Equal<object?>([process, Frames, 3L, 101L, 64L, 32_768L, 33_024L], batches[0].Payload!.Take(7));
        Assert.Equal<object?>([process, Frames, 3L, 101L, 64L, 2L, 32_768L], batches[1].Payload!.Take(7));
        Assert.Equal<object?>([process, Frames, 3L, 165L, 1L, 0L, -1L], batches[2].Payload!.Take(7));
        Assert.All(batches, written =>
        {
            Assert.Equal(InterCatSdkEventSource.EventVersion, written.Version);
            Assert.InRange((long)written.Payload![^1]!, before, Stopwatch.GetTimestamp());
        });
        Assert.InRange(InterCatSdkEventSource.SchemaMinor, 1, int.MaxValue);

        // A batch of no message or of more than the most, a negative length or copy, or a run past the last sequence a long
        // holds is refused, with or without a session.
        using InterCatChannel quiet = InterCatChannel.Open(Frames, "frames", InterCatChannelKind.Pipe);
        Assert.Throws<ArgumentOutOfRangeException>(() => quiet.PublishedBatch(1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => quiet.PublishedBatch(1, InterCatChannel.MaximumBatch + 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => quiet.ConsumedBatch(1, 2, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => quiet.PublishedBatch(1, 2, 1, copiedLength: -2));
        Assert.Throws<ArgumentOutOfRangeException>(() => quiet.ConsumedBatch(long.MaxValue, 2, 1));
        quiet.PublishedBatch(long.MaxValue - 1, 2, 1);
        quiet.ConsumedBatch(1, InterCatChannel.MaximumBatch, 0);
    }

    [Fact(DisplayName = "§15: the provider's manifest, which a capture's catalog reads, declares each of the schema's events by its name, identity, opcode and version, and each event's text as its last field")]
    public void TheManifestDeclaresTheSchema()
    {
        string manifest = EventSource.GenerateManifest(typeof(InterCatSdkEventSource), string.Empty)!;
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events";
        XElement provider = XDocument.Parse(manifest).Descendants(ns + "provider").Single();
        Assert.Equal(InterCatSdkEventSource.ProviderName, (string)provider.Attribute("name")!);

        // Event 0 is the one every EventSource declares for its own diagnostics, no event of the schema's; an event that
        // declares no opcode has Info's, the default.
        XElement[] declared = [.. provider.Descendants(ns + "event").Where(element => (int)element.Attribute("value")! != 0)];
        Assert.Equal(
            [(1, "SdkStarted", null), (2, "ChannelOpened", "win:Start"), (3, "ChannelClosed", "win:Stop"),
                (4, "MessagePublished", "win:Send"), (5, "MessageConsumed", "win:Receive"), (6, "OperationStarted", "win:Start"),
                (7, "OperationCompleted", "win:Stop"), (8, "MessagesPublished", "win:Send"), (9, "MessagesConsumed", "win:Receive"),
                (10, "MessageContent", null)],
            declared
                .Select(element => ((int)element.Attribute("value")!, (string)element.Attribute("symbol")!, (string?)element.Attribute("opcode")))
                .OrderBy(declaration => declaration.Item1));
        Assert.All(declared, element => Assert.Equal("1", (string)element.Attribute("version")!));

        // A message's content alone is under a keyword, Content, so a session hears it only by asking (ADR-107); its bytes are
        // its last field, sized by the count before them.
        Assert.Equal([(10, "Content")],
            declared.Where(element => element.Attribute("keywords") is not null)
                .Select(element => ((int)element.Attribute("value")!, (string)element.Attribute("keywords")!)));
        // Beside it, the manifest declares only the session keywords EventSource keeps for itself, past every one of the SDK's.
        Dictionary<string, ulong> keywords = provider.Descendants(ns + "keyword")
            .ToDictionary(keyword => (string)keyword.Attribute("name")!, keyword => Convert.ToUInt64((string)keyword.Attribute("mask")!, 16));
        Assert.Equal((ulong)InterCatSdkEventSource.Keywords.Content, keywords["Content"]);
        Assert.All(keywords.Where(keyword => keyword.Key != "Content"), keyword => Assert.Equal(0UL, keyword.Value & 0xFFFF_FFFFUL));
        XElement content = provider.Descendants(ns + "template")
            .Single(template => (string)template.Attribute("tid")! == (string)declared.Single(element => (int)element.Attribute("value")! == 10).Attribute("template")!);
        Assert.Equal(
            [("processId", "win:Int32", null), ("channel", "win:GUID", null), ("epoch", "win:Int64", null), ("sequence", "win:Int64", null),
                ("contentSize", "win:UInt32", null), ("content", "win:Binary", "contentSize")],
            content.Elements(ns + "data").Select(field => ((string)field.Attribute("name")!, (string)field.Attribute("inType")!, (string?)field.Attribute("length"))));

        // A capture reads each field at a fixed offset, which only a field before any text has: an event's text is its last.
        XElement[] texts =
        [
            .. declared.Select(element => provider.Descendants(ns + "template")
                    .Single(template => (string)template.Attribute("tid")! == (string)element.Attribute("template")!))
                .Where(template => template.Elements(ns + "data").Any(Text)),
        ];
        Assert.Equal(3, texts.Length);
        Assert.All(texts, template => Assert.True(Text(template.Elements(ns + "data").Last()), (string)template.Attribute("tid")!));
        Assert.All(texts, template => Assert.Single(template.Elements(ns + "data"), Text));

        static bool Text(XElement field) => (string?)field.Attribute("inType") == "win:UnicodeString";
    }

    [Fact(DisplayName = "§15: the SDK's starts and stops leave the runtime's activity tracking alone while it tracks: a start under no parent names none, each event is raised under the thread's own activity, and that activity is unchanged")]
    public void StartsLeaveActivitiesAlone()
    {
        using var tracking = new ActivityTracking();
        using var recorder = new Recorder();
        recorder.EnableEvents(InterCatSdkEventSource.Log, EventLevel.Verbose);
        Guid before = EventSource.CurrentThreadActivityId;
        using (InterCatChannel.Open(Frames, "frames", InterCatChannelKind.Pipe))
        {
            InterCatOperation outer = InterCatOperation.Start("outer");
            InterCatOperation inner = InterCatOperation.Start("inner");
            inner.Complete();
            outer.Complete();
        }

        Assert.Equal(before, EventSource.CurrentThreadActivityId);
        EventWrittenEventArgs[] raised = recorder.Raised();
        Assert.Equal(2, raised.Count(written => written.EventId == InterCatSdkEventSource.OperationStartedId));
        Assert.All(raised.Where(written => written.EventId == InterCatSdkEventSource.OperationStartedId), written =>
            Assert.Equal(Guid.Empty, written.RelatedActivityId));
        Assert.All(raised, written => Assert.Equal(before, written.ActivityId));
    }

    /// <summary>A message's bytes, made from its sequence, so no two messages here hold the same.</summary>
    private static byte[] Message(long sequence, int length)
    {
        var message = new byte[length];
        for (int index = 0; index < length; index++)
        {
            message[index] = unchecked((byte)((index * 13) + sequence));
        }

        return message;
    }

    /// <summary>Runs a workload verb of the SDK's in a process of its own, writing its truth to <paramref name="truth"/>.</summary>
    private static async Task RunWorkloadAsync(string verb, string truth)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { Path.Combine(AppContext.BaseDirectory, "InterCat.TestWorkloads.dll"), verb, "--truth", truth })
        {
            start.ArgumentList.Add(argument);
        }

        using Process workload = Process.Start(start) ?? throw new InvalidOperationException("The workload did not start.");
        Task<string> output = workload.StandardOutput.ReadToEndAsync();
        string error = await workload.StandardError.ReadToEndAsync();
        await workload.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
        Assert.True(workload.ExitCode == 0, $"The workload exited with {workload.ExitCode}: {await output} {error}");
    }

    /// <summary>
    /// Turns on the runtime's activity tracking, as a session asking the task library for its activity flow does: from then
    /// on, a start an EventSource raises without opting out begins an activity on its thread.
    /// </summary>
    private sealed class ActivityTracking : EventListener
    {
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "System.Threading.Tasks.TplEventSource")
            {
                // TasksFlowActivityIds: the keyword that turns the tracking on.
                EnableEvents(eventSource, EventLevel.Informational, (EventKeywords)0x80);
            }
        }
    }

    /// <summary>Hears the SDK's events within this process, as a session hears them through ETW.</summary>
    private sealed class Recorder : EventListener
    {
        private readonly List<EventWrittenEventArgs> raised = [];

        public EventWrittenEventArgs[] Raised()
        {
            lock (raised)
            {
                return [.. raised];
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name != InterCatSdkEventSource.ProviderName)
            {
                return;
            }

            lock (raised)
            {
                raised.Add(eventData);
            }
        }
    }
}
