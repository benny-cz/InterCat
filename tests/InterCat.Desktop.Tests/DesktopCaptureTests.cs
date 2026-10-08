using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Domain;
using Xunit;

namespace InterCat.Desktop.Tests;

public sealed class DesktopCaptureTests
{
    [Fact]
    public void WindowsDesktopBuildStagesACompleteSeparateBroker()
    {
        if (!OperatingSystem.IsWindows()) return;

        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "InterCat.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string output = Path.Combine(root.FullName, "src", "InterCat.Desktop", "bin", configuration, "net10.0");
        foreach (string file in new[]
        {
            "InterCat.CaptureBroker.exe", "InterCat.CaptureBroker.dll",
            "InterCat.CaptureBroker.deps.json", "InterCat.CaptureBroker.runtimeconfig.json",
            Path.Combine("amd64", "KernelTraceControl.dll"),
        })
        {
            Assert.True(File.Exists(Path.Combine(output, file)), $"Missing staged broker payload: {file}");
        }
    }

    [Fact]
    public void BoundedChannelRungExplainsWhyItCannotShowACompleteSet()
    {
        const string reason = "4,097 paired channels exceed the 4,096-channel overview bound; use a scoped query.";
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create() with
        {
            Channels = [], Operations = [], Evidence = [], ChannelProjectionProblem = reason,
        };
        using var viewModel = new WorkspaceViewModel(snapshot, "session:one:generation:1");
        viewModel.SelectedRung = viewModel.RungRows[0];
        Assert.True(viewModel.Descend());
        viewModel.SelectedRung = viewModel.RungRows[0];
        Assert.True(viewModel.Descend());

        Assert.True(viewModel.IsEmptyRung);
        Assert.StartsWith(reason, viewModel.EmptyReason, StringComparison.Ordinal);
        Assert.Contains("Browse paired channels", viewModel.EmptyReason, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstRunIsEmptyEvidenceNotTheSyntheticTour()
    {
        WorkspaceSnapshot empty = OverviewWorkspace.Empty();
        using var viewModel = new WorkspaceViewModel(empty, "empty-workspace");

        Assert.Empty(empty.Processes);
        Assert.Empty(empty.Edges);
        Assert.Empty(empty.Timeline);
        Assert.Contains("No live capture", viewModel.WorkspaceDisclosure, StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic", empty.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void OneActionExploreUsesBoundedLiveEvidenceOnlyDefaults()
    {
        BrokerPrepareCaptureRequest request = DesktopCaptureRunner.ExploreRequest();

        Assert.Equal("explore", request.ProfileId);
        Assert.Null(request.FocusedMechanism);
        Assert.Empty(request.FocusedProcessIds);
        Assert.False(request.AllowBroaderCapture);
        Assert.False(request.RequestOriginalDiagnosticEtl);
        Assert.Null(request.Content);
        Assert.Equal(BrokerJournalPublication.Live, request.Publication);
        Assert.Equal(600, request.Quota.MaximumDurationSeconds);
        Assert.Equal(1_024L * 1_024 * 1_024, request.Quota.MaximumJournalBytes);
        Assert.Equal(1_024L * 1_024 * 1_024, request.Quota.MinimumFreeDiskBytes);
        Assert.Null(request.Quota.Validate());

        // Every record of the capture is kept by default; a capture that keeps a window has the broker release its own
        // copy of what the session gave up (ADR-048 decision 5), which its review says.
        Assert.Equal(BrokerRetentionPolicy.StopAtLimit, request.Retention);
        Assert.Null(request.KeptWindowSeconds);
        BrokerPrepareCaptureRequest rolling = DesktopCaptureRunner.ExploreRequest(86_400, TimeSpan.FromMinutes(10));
        Assert.Equal((BrokerRetentionPolicy.ReleaseFollowed, BrokerJournalPublication.Live, 86_400, (int?)600),
            (rolling.Retention, rolling.Publication, rolling.Quota.MaximumDurationSeconds, rolling.KeptWindowSeconds));

        // It publishes as often as its window needs, not its day-long limit: every 2 s for ten minutes, where a day takes 85.
        Assert.Equal((2_000, 85_000), (
            BrokerJournalPublicationPolicy.IntervalMilliseconds(rolling.Publication, 86_400, rolling.KeptWindowSeconds),
            BrokerJournalPublicationPolicy.IntervalMilliseconds(rolling.Publication, 86_400)));
    }

    [Fact(DisplayName = "P19: the window asks for a content capture of the processes a person chose, every exchange of theirs within the limits chosen")]
    public void AContentCaptureAsksForTheChosenProcesses()
    {
        DateTimeOffset started = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
        var choice = new ContentCaptureChoice(
            [new(4_242, "client", started), new(5_150, null, started.AddSeconds(1))],
            64 * 1024, 16L * 1024 * 1024, ContentInspectionMode.Disabled, 3_600);
        BrokerPrepareCaptureRequest request = DesktopCaptureRunner.ContentRequest(choice);
        Assert.Equal(("content", (Mechanism?)null, 0, false, false), (request.ProfileId, request.FocusedMechanism,
            request.FocusedProcessIds.Count, request.AllowBroaderCapture, request.RequestOriginalDiagnosticEtl));
        Assert.Equal((BrokerRetentionPolicy.StopAtLimit, BrokerJournalPublication.Live, (int?)null),
            (request.Retention, request.Publication, request.KeptWindowSeconds));
        Assert.Equal(new BrokerCaptureQuota(3_600, 1L << 30, 1L << 30), request.Quota);

        // Every exchange of the processes chosen, by the one content source a person can name, within the chosen limits.
        ContentCaptureRequest content = request.Content!;
        Assert.Equal((ContentSources.WinInetCapture, Mechanism.Http, 64 * 1024, 16L * 1024 * 1024, ContentRetentionMode.StopAtLimit,
                ContentInspectionMode.Disabled),
            (content.SourceId, content.Mechanism, content.MaximumRecordBytes, content.MaximumSessionBytes, content.Retention,
                content.Inspection));
        Assert.Equal([4_242, 5_150], content.ProcessIds);
        Assert.Equal([ContentCaptureRequest.EveryChannel], content.ChannelSelectors);

        // Each process as the window saw it, by its ID, which the broker's pinned starts are compared with.
        Assert.Equal([4_242, 5_150], choice.Seen.Keys.Order());
        Assert.Same(choice.Processes[1], choice.Seen[5_150]);
    }

    [Fact(DisplayName = "R22: the window starts a content capture only after its review, with a plan prepared again that keeps what was reviewed")]
    public void AContentCaptureStartsOnlyAfterItsReview() => SingleThreadedContext.Run(async () =>
    {
        DateTimeOffset started = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
        var choice = new ContentCaptureChoice([new(4_242, "client", started)], 64 * 1024, 16L * 1024 * 1024,
            ContentInspectionMode.Disabled, 600);
        BrokerPrepareCaptureResponse prepared = Prepared(started, "first");
        var reports = new List<CaptureUiUpdate>();
        int preparedAgain = 0;
        Task<BrokerPrepareCaptureResponse> Again(BrokerPrepareCaptureResponse response)
        {
            preparedAgain++;
            return Task.FromResult(response);
        }

        // Without a review, which only a test omits, the plan prepared starts as it is.
        Assert.Same(prepared, await DesktopCaptureRunner.ReviewContentAsync(prepared, choice, null, () => Again(prepared), reports.Add));
        Assert.Equal((0, 0), (reports.Count, preparedAgain));

        // A process the broker pinned that is not the one chosen - its ID passed to another between - is refused unreviewed.
        bool reviewed = false;
        Assert.Null(await DesktopCaptureRunner.ReviewContentAsync(Prepared(started.AddSeconds(1), "other"), choice,
            _ => Task.FromResult(reviewed = true), () => Again(prepared), reports.Add));
        Assert.False(reviewed);
        Assert.Equal((CaptureUiPhase.Unavailable, "The content capture is unavailable here"), (reports[^1].Phase, reports[^1].Headline));
        Assert.StartsWith("Process 4242 is not the one named: ", reports[^1].Detail, StringComparison.Ordinal);
        Assert.EndsWith(" Nothing was recorded.", reports[^1].Detail, StringComparison.Ordinal);

        // A person who does not start it after its review starts nothing, and nothing is prepared again.
        reports.Clear();
        Assert.Null(await DesktopCaptureRunner.ReviewContentAsync(prepared, choice, summary => Task.FromResult(false),
            () => Again(prepared), reports.Add));
        Assert.Equal([(CaptureUiPhase.Starting, "Review what the content capture keeps"), (CaptureUiPhase.Complete, "Content capture not started")],
            reports.Select(update => (update.Phase, update.Headline)));
        Assert.Equal(0, preparedAgain);

        // Started, it is prepared again and starts with the new plan, once that keeps what was reviewed.
        BrokerPrepareCaptureResponse fresh = Prepared(started, "second");
        BrokerEffectiveCaptureSummary? shown = null;
        Assert.Same(fresh, await DesktopCaptureRunner.ReviewContentAsync(prepared, choice,
            summary => Task.FromResult((shown = summary) is not null), () => Again(fresh), reports.Add));
        Assert.Same(prepared.Summary, shown);
        Assert.Equal(1, preparedAgain);

        // A plan that changed since its review, or that the broker no longer prepares, starts nothing and says why.
        foreach ((BrokerPrepareCaptureResponse again, string why) in new[]
        {
            (Prepared(started.AddMilliseconds(5), "third"), "What the broker would record changed since you reviewed it: process 4242 "
                + "is now the one that started at "),
            (new BrokerPrepareCaptureResponse(false, BrokerPrepareRefusalCode.ContentProcessRefused,
                "Process 4242 runs as another user. Content is kept only from your own processes.", null, prepared.Summary),
                "What the broker would record changed since you reviewed it: Process 4242 runs as another user. Content is kept "
                + "only from your own processes. Nothing was recorded; choose the processes again to review it anew."),
        })
        {
            Assert.Null(await DesktopCaptureRunner.ReviewContentAsync(prepared, choice, _ => Task.FromResult(true), () => Again(again),
                reports.Add));
            Assert.Equal((CaptureUiPhase.Unavailable, "The content capture changed since its review"), (reports[^1].Phase, reports[^1].Headline));
            Assert.StartsWith(why, reports[^1].Detail, StringComparison.Ordinal);
        }
    });

    /// <summary>A content capture of process 4242 prepared from the start <paramref name="started"/>, under a grant named so.</summary>
    private static BrokerPrepareCaptureResponse Prepared(DateTimeOffset started, string token) => new(
        true,
        null,
        null,
        new PreparedPlanGrant(token, "sha256:" + new string('a', 64), started, started.AddSeconds(30)),
        new BrokerEffectiveCaptureSummary(
            "content", "content", AdmissionMode.ScopedContent, AdmissionMode.ScopedContent, null, null, [4_242], [4_242],
            true, false, false,
            [new BrokerEffectiveSourceSummary(ContentSources.WinInetCapture, ProviderProcessScope.ProcessFiltered, [4_242], false, "Content.")],
            "Content is kept only from the named processes.", "WinINet keeps each HTTP exchange's messages.",
            new BrokerCaptureQuota(600, 1L << 30, 1L << 30), BrokerRetentionPolicy.StopAtLimit, [], BrokerJournalPublication.Live,
            2_000, null,
            new BrokerEffectiveContentSummary(ContentSources.WinInetCapture, [started], 64 * 1024, 16L * 1024 * 1024,
                ContentInspectionMode.Disabled, [ContentCaptureRequest.EveryChannel])));

    [Fact]
    public void EffectiveCaptureReviewStatesSourceScopeAndLimits()
    {
        var summary = new BrokerEffectiveCaptureSummary(
            "explore", "explore", AdmissionMode.MetadataOnly, AdmissionMode.MetadataOnly,
            null, null, [], [], false, false, false,
            [new BrokerEffectiveSourceSummary("TCP", ProviderProcessScope.WholeMachineFilterUnavailable, [], false, "System-wide")],
            "No payload contents.", "Process lifecycle and TCP transport events.",
            DesktopCaptureRunner.ExploreRequest().Quota, BrokerRetentionPolicy.StopAtLimit, [],
            BrokerJournalPublication.Live, 2_000);

        string review = DesktopCaptureRunner.Describe(summary);
        Assert.DoesNotContain("releasing what the session gave up", review, StringComparison.Ordinal);
        Assert.Contains("up to 10 min / 1 GiB journal, releasing what the session gave up · ",
            DesktopCaptureRunner.Describe(summary with { Retention = BrokerRetentionPolicy.ReleaseFollowed }), StringComparison.Ordinal);
        Assert.Contains("TCP", review, StringComparison.Ordinal);
        Assert.Contains("10 min", review, StringComparison.Ordinal);
        Assert.Contains("up to 10 min / 1 GiB journal", review, StringComparison.Ordinal);
        Assert.Contains($"first view within {0.5:0.#} s, then every 2 s", review, StringComparison.Ordinal);
        Assert.Contains("No payload contents", review, StringComparison.Ordinal);

        // Its limits are the effective quota's. One the broker releases for keeping a window holds the window, and for one
        // lease renewal and one publication after its session gives chunks up, those too (§12.1 S5).
        var keep = new RollingRetentionPolicy(TimeSpan.FromMinutes(10));
        CaptureLimits all = DesktopCaptureRunner.LimitsOf(summary, keep);
        Assert.Equal((TimeSpan.FromMinutes(10), 1L << 30, 1L << 30, (CaptureWindow?)null),
            (all.MaximumDuration, all.MaximumJournalBytes, all.MinimumFreeDiskBytes, all.Window));
        CaptureWindow window = DesktopCaptureRunner.LimitsOf(summary with { Retention = BrokerRetentionPolicy.ReleaseFollowed }, keep)
            .Window!;
        Assert.Equal((keep, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(750), TimeSpan.FromSeconds(762)),
            (window.Policy, window.Lag, window.SessionHolds, window.EvidenceHolds));
        Assert.Null(DesktopCaptureRunner.LimitsOf(summary with { Retention = BrokerRetentionPolicy.ReleaseFollowed }, null).Window);
    }
}
