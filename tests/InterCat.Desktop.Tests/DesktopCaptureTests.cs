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
        Assert.Equal(reason, viewModel.EmptyReason);
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
    }

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
        Assert.Contains("TCP", review, StringComparison.Ordinal);
        Assert.Contains("10 min", review, StringComparison.Ordinal);
        Assert.Contains($"{1024:N0} MiB", review, StringComparison.Ordinal);
        Assert.Contains("2 s publication", review, StringComparison.Ordinal);
        Assert.Contains("No payload contents", review, StringComparison.Ordinal);
    }
}
