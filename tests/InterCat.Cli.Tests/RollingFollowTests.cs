using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Domain;
using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>What `icat capture --keep-last` projects its stop from, as the window does (R18, §12.1 S5).</summary>
public sealed class RollingFollowTests
{
    [Fact(DisplayName = "R18: icat capture projects a capture's stop from the limits the window does - the effective quota, and the window the broker releases for")]
    public void ACapturesLimitsAreTheWindows()
    {
        var summary = new BrokerEffectiveCaptureSummary(
            "explore", "explore", AdmissionMode.MetadataOnly, AdmissionMode.MetadataOnly,
            null, null, [], [], false, false, false,
            [new BrokerEffectiveSourceSummary("TCP", ProviderProcessScope.WholeMachineFilterUnavailable, [], false, "System-wide")],
            "No payload contents.", "Process lifecycle and TCP transport events.",
            new BrokerCaptureQuota(86_400, 2L << 30, 1L << 30), BrokerRetentionPolicy.ReleaseFollowed, [],
            BrokerJournalPublication.Live, 2_000);
        var keep = new RollingRetentionPolicy(TimeSpan.FromMinutes(10));

        // Its window, and what its session gave up for one lease renewal and one publication after.
        CaptureLimits limits = RollingFollow.Limits(summary, keep, TimeSpan.FromSeconds(10));
        Assert.Equal((TimeSpan.FromDays(1), 2L << 30, 1L << 30), (limits.MaximumDuration, limits.MaximumJournalBytes,
            limits.MinimumFreeDiskBytes));
        Assert.Equal((keep, TimeSpan.FromSeconds(12)), (limits.Window!.Policy, limits.Window.Lag));

        // A broker that keeps everything it records, or a follow keeping every record, holds no window.
        Assert.Null(RollingFollow.Limits(summary with { Retention = BrokerRetentionPolicy.StopAtLimit }, keep, TimeSpan.FromSeconds(10))
            .Window);
        Assert.Null(RollingFollow.Limits(summary, null, TimeSpan.FromSeconds(10)).Window);
    }
}
