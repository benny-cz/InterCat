using InterCat.Domain;
using Xunit;

namespace InterCat.CaptureBroker.Tests;

/// <summary>
/// What a client says of a content capture's processes (ADR-049, R22): each by what it runs and the start the broker pinned,
/// a process whose ID passed to another refused before the capture starts, and a plan that changed after its review not
/// started.
/// </summary>
public sealed class BrokerContentReviewTests
{
    private const string WinInet = ContentSources.WinInetCapture;
    private static readonly DateTimeOffset Started = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<int, SeenProcess> Nothing = [];

    [Fact(DisplayName = "R22: a content capture's review names each process by what it runs and the start the broker pinned, and refuses one whose ID passed to another")]
    public void TheReviewNamesEachProcessByItsStart()
    {
        BrokerEffectiveCaptureSummary summary = Summary(Kept(["*"], ContentInspectionMode.HexAndText,
            Started, Started.AddSeconds(5.25)), 100, 200);
        var seen = new Dictionary<int, SeenProcess>
        {
            [100] = new(100, "notepad", Started),
            [200] = new(200, null, null),
        };
        Assert.Equal(
        [
            ("Content", $"HTTP messages WinINet raises ({WinInet}), every channel of the processes below"),
            ("Process 100", "notepad, started 2026-10-08 09:00:00.000"),
            ("Process 200", "its image not readable, started 2026-10-08 09:00:05.250"),
            ("Limits", "at most 4 KiB a record and 64 KiB in all; the capture stops when what it keeps reaches that"),
            ("Inspection", "its bytes may be shown as hex and text when you ask"),
        ], BrokerContentReview.Lines(summary, Mechanism.Http, seen, TimeZoneInfo.Utc));

        // Each process seen is the one pinned, and one whose start could not be read here rests on the broker's check.
        Assert.Null(BrokerContentReview.Mismatch(summary, seen, TimeZoneInfo.Utc));

        // A process whose ID passed to another between - later or earlier - is refused, saying both starts.
        foreach (DateTimeOffset other in new[] { Started.AddMilliseconds(1), Started.AddTicks(-1) })
        {
            seen[100] = new(100, "notepad", other);
            Assert.Equal("Process 100 is not the one named: the broker prepared the process that started at 2026-10-08 "
                + $"09:00:00.000, but the one named here started at {other:yyyy-MM-dd HH:mm:ss.fff}, so its ID passed to another "
                + "process between. Name the process now running, or the one you meant if it still runs under another ID.",
                BrokerContentReview.Mismatch(summary, seen, TimeZoneInfo.Utc));
        }

        // Other channels are named, content never shown says so, and a capture keeping no content has nothing to review.
        Assert.Equal($"HTTP messages WinINet raises ({WinInet}), the channel api of the processes below", BrokerContentReview.Lines(
            Summary(Kept(["api"], ContentInspectionMode.Disabled, Started), 100), Mechanism.Http, Nothing, TimeZoneInfo.Utc)[0].Value);
        BrokerEffectiveCaptureSummary named = Summary(Kept(["api", "upload"], ContentInspectionMode.Disabled, Started), 100);
        IReadOnlyList<(string Label, string Value)> lines = BrokerContentReview.Lines(named, Mechanism.Http,
            new Dictionary<int, SeenProcess>(), TimeZoneInfo.Utc);
        Assert.Equal($"HTTP messages WinINet raises ({WinInet}), the channels api, upload of the processes below", lines[0].Value);
        Assert.Equal("HTTP messages of etw/other, the channel api of the processes below", BrokerContentReview.Lines(
            Summary(Kept(["api"], ContentInspectionMode.Disabled, Started) with { SourceId = "etw/other" }, 100), Mechanism.Http,
            Nothing, TimeZoneInfo.Utc)[0].Value);
        Assert.Equal(("Process 100", "its image not readable, started 2026-10-08 09:00:00.000"), lines[1]);
        Assert.Equal(("Inspection", "its bytes are never shown"), lines[^1]);
        Assert.Empty(BrokerContentReview.Lines(Summary(null), Mechanism.Http, seen, TimeZoneInfo.Utc));
        Assert.Null(BrokerContentReview.Mismatch(Summary(null), seen, TimeZoneInfo.Utc));
    }

    [Fact(DisplayName = "R22: a process is seen by what it runs and when it started, and one not running is refused in every client's words")]
    public void AProcessIsSeenByWhatItRunsAndItsStart()
    {
        SeenProcess self = BrokerContentReview.See(Environment.ProcessId)!;
        Assert.Equal(Environment.ProcessId, self.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(self.Image));
        Assert.InRange(self.StartedUtc!.Value, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);
        Assert.Null(BrokerContentReview.See(int.MaxValue));
        Assert.Equal("its image not readable, its start not readable",
            BrokerContentReview.Describe(new(7, null, null), TimeZoneInfo.Utc));
        Assert.Equal("Process 7 is not running, so nothing was recorded: a content request names running processes, which the "
            + "capture holds open so their IDs stay theirs. Task Manager's Details tab lists running processes with their IDs.",
            BrokerContentReview.NotRunning(7));
    }

    [Fact(DisplayName = "R22: the processes a person may choose are this sign-in's, each with the start a client can read, never the client itself")]
    public void TheProcessesAPersonMayChoose()
    {
        IReadOnlyList<SeenProcess> running = BrokerContentReview.Running();
        Assert.DoesNotContain(running, process => process.ProcessId == Environment.ProcessId);

        // None of another session's: a service's, or the container's first process here, which sits in a session of its own.
        using (var current = System.Diagnostics.Process.GetCurrentProcess())
        {
            int session = current.SessionId;
            Assert.All(running, process => Assert.True(SessionOf(process.ProcessId) is not { } other || other == session,
                $"Process {process.ProcessId} is listed from session {SessionOf(process.ProcessId)}, not {session}."));
        }

        Assert.All(running, process => Assert.NotNull(process.StartedUtc));
        Assert.Equal(running.OrderBy(process => process.Image, StringComparer.OrdinalIgnoreCase).ThenBy(process => process.StartedUtc)
            .ThenBy(process => process.ProcessId), running);

        // Another process of this session, started here, is among them by its ID and start.
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            OperatingSystem.IsWindows() ? "cmd.exe" : "sleep", OperatingSystem.IsWindows() ? "/c ping -n 30 127.0.0.1 > nul" : "30")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        try
        {
            SeenProcess seen = Assert.Single(BrokerContentReview.Running(), process => process.ProcessId == child.Id);
            SeenProcess again = BrokerContentReview.See(child.Id)!;
            Assert.Equal(again.Image, seen.Image);
            Assert.InRange((again.StartedUtc!.Value - seen.StartedUtc!.Value).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }

    [Fact(DisplayName = "R22: a content capture whose plan changed between its review and its start is not started, naming what changed")]
    public void APlanThatChangedAfterItsReviewIsNamed()
    {
        BrokerEffectiveCaptureSummary reviewed = Summary(Kept(["*"], ContentInspectionMode.HexAndText, Started, Started.AddSeconds(1)),
            100, 200);
        Assert.Null(BrokerContentReview.Changed(reviewed, reviewed with { }, TimeZoneInfo.Utc));
        foreach ((BrokerEffectiveCaptureSummary now, string changed) in new (BrokerEffectiveCaptureSummary, string)[]
        {
            (Summary(Kept(["*"], ContentInspectionMode.HexAndText, Started), 100), "the processes it names"),
            (Summary(Kept(["*"], ContentInspectionMode.HexAndText, Started, Started.AddSeconds(2)), 100, 200),
                "process 200 is now the one that started at 2026-10-08 09:00:02.000, not 2026-10-08 09:00:01.000"),
            (Summary(Kept(["*"], ContentInspectionMode.Disabled, Started, Started.AddSeconds(1)), 100, 200),
                "what it keeps of the messages"),
            (Summary(Kept(["api"], ContentInspectionMode.HexAndText, Started, Started.AddSeconds(1)), 100, 200),
                "what it keeps of the messages"),
            (reviewed with { Content = reviewed.Content! with { MaximumRecordBytes = 8 } }, "what it keeps of the messages"),
            (reviewed with { Content = reviewed.Content! with { MaximumSessionBytes = 1 << 20 } }, "what it keeps of the messages"),
            (reviewed with { Content = reviewed.Content! with { SourceId = "etw/other" } }, "what it keeps of the messages"),
            (reviewed with { Content = null }, "whether it keeps content"),
            (reviewed with { CollectionStatement = "Something else is kept." }, "what it collects"),
            (reviewed with { Disclosure = "Another scope." }, "what it collects"),
            (reviewed with { Sources = [reviewed.Sources[1]] }, "what it collects"),
            (reviewed with { Quota = reviewed.Quota with { MaximumDurationSeconds = 60 } }, "its limits"),
            (reviewed with { Retention = BrokerRetentionPolicy.ReleaseFollowed }, "its limits"),
        })
        {
            Assert.Equal(changed, BrokerContentReview.Changed(reviewed, now, TimeZoneInfo.Utc));
        }

        Assert.Equal("whether it keeps content", BrokerContentReview.Changed(reviewed with { Content = null }, reviewed, TimeZoneInfo.Utc));
    }

    /// <summary>The session a process runs in, or null once it has exited.</summary>
    private static int? SessionOf(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.SessionId;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static BrokerEffectiveContentSummary Kept(
        IReadOnlyList<string> channels,
        ContentInspectionMode inspection,
        params DateTimeOffset[] starts) =>
        new(WinInet, starts, 4096, 65536, inspection, channels);

    private static BrokerEffectiveCaptureSummary Summary(BrokerEffectiveContentSummary? content, params int[] processIds) => new(
        "content",
        "content",
        AdmissionMode.ScopedContent,
        AdmissionMode.ScopedContent,
        null,
        null,
        processIds,
        processIds,
        true,
        false,
        false,
        [new("etw/kernel/process", ProviderProcessScope.WholeMachineRequiredContext, [], true, "Lifecycle."),
            new(WinInet, ProviderProcessScope.ProcessFiltered, processIds, false, "Content.")],
        "Content is kept only from the named processes.",
        "What is kept.",
        new BrokerCaptureQuota(600, 1L << 30, 1L << 30),
        BrokerRetentionPolicy.StopAtLimit,
        [],
        BrokerJournalPublication.Live,
        2_000,
        null,
        content);
}
