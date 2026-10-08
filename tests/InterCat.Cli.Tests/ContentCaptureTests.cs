using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Capture.Windows;
using InterCat.CaptureBroker;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat capture's content request (ADR-049): taken in icat record's words, reviewed by what each process runs and when the
/// broker pinned its start, refused when a process's ID passed to another, and summed when it ends.
/// </summary>
public sealed class ContentCaptureTests
{
    private const string WinInet = "etw/manifest/Microsoft-Windows-WinINet-Capture";
    private static readonly DateTimeOffset Started = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<int, SeenProcess> Nothing = [];

    [Fact(DisplayName = "R18: icat capture takes a content request in icat record's words, and refuses what a content capture does not take")]
    public void AContentRequestInRecordsWords()
    {
        string[] words =
        [
            "--source", WinInet, "--pid", "4242", "--pid", "5150, 6000", "--channel", "*", "--max-record-bytes", "4096",
            "--max-session-bytes", "65536", "--inspection", "hex-text",
        ];
        BrokerPrepareCaptureRequest request = Parse("content", "http", words, out string? problem)!;
        Assert.Null(problem);
        Assert.Equal(("content", (Mechanism?)null, 0, false), (request.ProfileId, request.FocusedMechanism,
            request.FocusedProcessIds.Count, request.AllowBroaderCapture));
        Assert.Equal((BrokerRetentionPolicy.StopAtLimit, BrokerJournalPublication.Live, (int?)null),
            (request.Retention, request.Publication, request.KeptWindowSeconds));
        Assert.Equal(new BrokerCaptureQuota(CaptureRequests.DefaultSeconds, 1L << 30, 1L << 30), request.Quota);

        // The same words make the same request icat record compiles, one --pid or several separated by commas.
        ContentCaptureRequest content = request.Content!;
        Assert.Equal([4242, 5150, 6000], content.ProcessIds);
        Assert.Null(Options(words).Compile(Mechanism.Http, out ContentCaptureRequest? recorded));
        Assert.Equal((recorded!.SourceId, recorded.Mechanism, recorded.MaximumRecordBytes, recorded.MaximumSessionBytes,
                recorded.Retention, recorded.Inspection),
            (content.SourceId, content.Mechanism, content.MaximumRecordBytes, content.MaximumSessionBytes, content.Retention,
                content.Inspection));
        Assert.Equal(recorded.ProcessIds, content.ProcessIds);
        Assert.Equal(recorded.ChannelSelectors, content.ChannelSelectors);
        Assert.NotNull(Options([.. words, "--channel", "api"]).Compile(Mechanism.Http, out ContentCaptureRequest? refused));
        Assert.Null(refused);

        // What a content capture does not take is refused before Windows is asked for approval, each in its own words.
        foreach ((string profile, string? mechanism, string[] given, bool keepLast, string refusal) in new (string, string?, string[], bool, string)[]
        {
            ("content", null, words, false, "content takes --mechanism http"),
            ("content", "http", [.. words, "--allow"], false, "content takes --mechanism http"),
            ("content", "http", words, true, "--keep-last keeps a window of a session's records"),
            ("content", "http", words[..^2], false, "--profile content needs --source, --mechanism, at least one --pid"),
            ("content", "http", [.. words[..2], "--pid", "42x", .. words[4..]], false, "--pid takes positive process IDs"),
            ("content", "http", [.. words[..^1], "everything"], false, "--inspection is hex-text"),
            ("content", "http", [.. words[..^2], "--inspection", "disabled", "--retention", "forever"], false,
                "--retention is stop-at-limit"),
            ("content", "http", [.. words, "--channel", "api"], false, "The channel selector '*' names every channel"),
            ("content", "tcp", words, false, $"Content source '{WinInet}' does not describe TCP."),
            ("explore", null, ["--source", WinInet], false, "--source, --channel, the byte limits, --inspection and --retention"),
            ("explore", null, ["--channel", "*"], false, "--source, --channel, the byte limits, --inspection and --retention"),
            ("explore", null, ["--max-record-bytes", "1"], false, "--source, --channel, the byte limits, --inspection"),
            ("focused-transport", "tcp", ["--max-session-bytes", "1"], false, "--source, --channel, the byte limits, --inspection"),
            ("focused-transport", "tcp", ["--inspection", "disabled"], false, "--source, --channel, the byte limits, --inspection"),
            ("explore", null, ["--retention", "stop-at-limit"], false, "--source, --channel, the byte limits, --inspection"),
            ("explore", null, ["--pid", "4242"], false, "focused-transport takes --mechanism tcp"),
            ("focused-transport", "http", [], false, "focused-transport takes --mechanism tcp"),
            ("rpc-peers", null, [], false, "--profile is explore, focused-transport or content; 'rpc-peers' is not one."),
        })
        {
            bool broader = given.Contains("--allow");
            Assert.Null(CaptureRequests.Parse(profile, mechanism, broader, null, null, null, keepLast,
                Options([.. given.Where(word => word != "--allow")]), out problem));
            Assert.StartsWith(refusal, problem, StringComparison.Ordinal);
        }

        // A focused capture names its processes as it did, separated by commas or one to a --pid.
        foreach (string[] pids in new[] { new[] { "--pid", "10,20" }, ["--pid", "10", "--pid", "20"] })
        {
            BrokerPrepareCaptureRequest focused = Parse("focused-transport", "tcp", pids, out problem)!;
            Assert.Null(problem);
            Assert.Equal((Mechanism.Tcp, (ContentCaptureRequest?)null), (focused.FocusedMechanism!.Value, focused.Content));
            Assert.Equal([10, 20], focused.FocusedProcessIds);
        }
    }

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
            ("Content", $"HTTP messages of {WinInet}, every channel of the processes below"),
            ("Process 100", "notepad, started 2026-10-08 09:00:00.000"),
            ("Process 200", "its image not readable, started 2026-10-08 09:00:05.250"),
            ("Limits", "at most 4 KiB a record and 64 KiB in all; the capture stops when what it keeps reaches that"),
            ("Inspection", "its bytes may be shown as hex and text when you ask"),
        ], ContentProcessReview.Lines(summary, Mechanism.Http, seen, TimeZoneInfo.Utc));

        // Each process seen is the one pinned, and one whose start could not be read here rests on the broker's check.
        Assert.Null(ContentProcessReview.Mismatch(summary, seen, TimeZoneInfo.Utc));

        // A process whose ID passed to another between - later or earlier - is refused, saying both starts.
        foreach (DateTimeOffset other in new[] { Started.AddMilliseconds(1), Started.AddTicks(-1) })
        {
            seen[100] = new(100, "notepad", other);
            Assert.Equal("Process 100 is not the one named: the broker prepared the process that started at 2026-10-08 "
                + $"09:00:00.000, but the one named here started at {other:yyyy-MM-dd HH:mm:ss.fff}, so its ID passed to another "
                + "process between. Name the process now running, or the one you meant if it still runs under another ID.",
                ContentProcessReview.Mismatch(summary, seen, TimeZoneInfo.Utc));
        }

        // Other channels are named, content never shown says so, and a capture keeping no content has nothing to review.
        Assert.Equal($"HTTP messages of {WinInet}, the channel api of the processes below", ContentProcessReview.Lines(
            Summary(Kept(["api"], ContentInspectionMode.Disabled, Started), 100), Mechanism.Http, Nothing, TimeZoneInfo.Utc)[0].Value);
        BrokerEffectiveCaptureSummary named = Summary(Kept(["api", "upload"], ContentInspectionMode.Disabled, Started), 100);
        IReadOnlyList<(string Label, string Value)> lines = ContentProcessReview.Lines(named, Mechanism.Http,
            new Dictionary<int, SeenProcess>(), TimeZoneInfo.Utc);
        Assert.Equal($"HTTP messages of {WinInet}, the channels api, upload of the processes below", lines[0].Value);
        Assert.Equal(("Process 100", "its image not readable, started 2026-10-08 09:00:00.000"), lines[1]);
        Assert.Equal(("Inspection", "its bytes are never shown"), lines[^1]);
        Assert.Empty(ContentProcessReview.Lines(Summary(null), Mechanism.Http, seen, TimeZoneInfo.Utc));
        Assert.Null(ContentProcessReview.Mismatch(Summary(null), seen, TimeZoneInfo.Utc));
    }

    [Fact(DisplayName = "R22: a process is seen by what it runs and when it started, and one not running is refused in every command's words")]
    public void AProcessIsSeenByWhatItRunsAndItsStart()
    {
        SeenProcess self = ContentProcessReview.See(Environment.ProcessId)!;
        Assert.Equal(Environment.ProcessId, self.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(self.Image));
        Assert.InRange(self.StartedUtc!.Value, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);
        Assert.Null(ContentProcessReview.See(int.MaxValue));
        Assert.Equal("its image not readable, its start not readable",
            ContentProcessReview.Describe(new(7, null, null), TimeZoneInfo.Utc));
        Assert.Equal("Process 7 is not running, so nothing was recorded: a content request names running processes, which the "
            + "capture holds open so their IDs stay theirs. Task Manager's Details tab lists running processes with their IDs.",
            ContentProcessReview.NotRunning(7));
    }

    [Fact(DisplayName = "ADR-036: a broker content capture says how much of how many messages its session kept, and never a byte of them")]
    public void WhatACaptureKeptIsSummed()
    {
        BrokerEffectiveCaptureSummary summary = Summary(Kept(["*"], ContentInspectionMode.HexAndText, Started), 4_242);
        using var kept = new TemporarySession();
        ObservationRowV1[] rows = [Body(10, 1), Body(12, 2), Body(14, 3)];
        Publish(kept.Store, rows, finished: true, content: (ContentHeader(recordLimit: 8),
        [
            Content(rows[0], "head"u8.ToArray(), 8, ContentEncodingV1.Binary),
            Content(rows[1], "a long tail"u8.ToArray(), 8, ContentEncodingV1.Binary),
        ]));
        kept.Store.ReleaseSegmentReaders();
        CaptureContentDocument document = CaptureContent.Describe(summary,
            new Dictionary<int, SeenProcess> { [4_242] = new(4_242, "client", Started) }, kept.Store)!;
        Assert.Equal((WinInet, 4096, 65536L, ContentInspectionMode.HexAndText),
            (document.SourceId, document.MaximumRecordBytes, document.MaximumSessionBytes, document.Inspection));
        Assert.Equal([new CaptureContentProcessDocument(4_242, "client", Started)], document.Processes);
        Assert.Equal((2, 1, 1, 0, 12L), (document.Kept!.Records, document.Kept.Whole, document.Kept.Cut, document.Kept.Omitted,
            document.Kept.KeptBytes));
        Assert.Equal("12 B of 2 messages: 1 kept whole, 1 cut to the record limit, 0 not kept once the content limit was reached",
            CaptureContent.Statement(document.Kept));

        // A session that kept none says so, and one that published nothing says that is unknown, not none.
        using var none = new TemporarySession();
        Publish(none.Store, rows, finished: true);
        none.Store.ReleaseSegmentReaders();
        Assert.Equal("none: no message of the processes named was kept",
            CaptureContent.Statement(CaptureContent.Describe(summary, Nothing, none.Store)!.Kept));
        using var empty = new TemporarySession();
        Assert.Null(CaptureContent.Describe(summary, Nothing, empty.Store)!.Kept);
        Assert.Equal("unknown: the session published nothing, or its content could not be read", CaptureContent.Statement(null));
        Assert.Equal("12 B of 2 messages: 1 kept whole, 1 cut to the record limit, 0 not kept once the content limit was reached; "
            + "some could not be read, so these sums leave it out: chunk 3 is damaged",
            CaptureContent.Statement(document.Kept with { Problem = "chunk 3 is damaged" }));
        Assert.Null(CaptureContent.Describe(Summary(null), Nothing, kept.Store));
    }

    private static BrokerPrepareCaptureRequest? Parse(string profile, string? mechanism, string[] words, out string? problem) =>
        CaptureRequests.Parse(profile, mechanism, false, null, null, null, false, Options(words), out problem);

    private static ContentRequestOptions Options(string[] words) => ContentRequestOptions.Take(new CommandLine(words));

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
        [],
        "Content is kept only from the named processes.",
        "What is kept.",
        new BrokerCaptureQuota(60, 1L << 30, 1L << 30),
        BrokerRetentionPolicy.StopAtLimit,
        [],
        BrokerJournalPublication.Live,
        2_000,
        null,
        content);

    private static ObservationRowV1 Body(long ticks, ulong ordinal) =>
        Transfer(ticks, ObservationKind.Receive, AccountingSide.ReceiveSide, 1, null, ordinal) with
        {
            Mechanism = Mechanism.Http,
            Layer = ObservationLayer.Application,
            EventId = 2004,
            HeaderProcessId = 4_242,
            Direction = Direction.Inbound,
            ByteDomain = ByteDomain.ApplicationPayload,
        };
}
