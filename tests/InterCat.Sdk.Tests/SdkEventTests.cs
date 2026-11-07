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
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in new[] { Path.Combine(AppContext.BaseDirectory, "InterCat.TestWorkloads.dll"), "sdk-first-use", "--truth", truth })
            {
                start.ArgumentList.Add(argument);
            }

            using Process workload = Process.Start(start) ?? throw new InvalidOperationException("The workload did not start.");
            Task<string> output = workload.StandardOutput.ReadToEndAsync();
            string error = await workload.StandardError.ReadToEndAsync();
            await workload.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
            Assert.True(workload.ExitCode == 0, $"The workload exited with {workload.ExitCode}: {await output} {error}");

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
                (7, "OperationCompleted", "win:Stop")],
            declared
                .Select(element => ((int)element.Attribute("value")!, (string)element.Attribute("symbol")!, (string?)element.Attribute("opcode")))
                .OrderBy(declaration => declaration.Item1));
        Assert.All(declared, element => Assert.Equal("1", (string)element.Attribute("version")!));

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
