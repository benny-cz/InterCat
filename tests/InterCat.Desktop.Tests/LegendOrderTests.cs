using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Desktop.Theme;
using InterCat.Domain;
using Xunit;

namespace InterCat.Desktop.Tests;

/// <summary>§6.6: the legend is a key to what the panes draw, so it reads in the order the timeline draws its lanes.</summary>
public sealed class LegendOrderTests
{
    [Fact(DisplayName = "§6.6: the legend keys mechanisms in the order the lanes draw them, whichever the first bucket shows")]
    public void TheLegendFollowsTheLanes()
    {
        // The first bucket is mostly UDP, as a live capture's often is; the lanes still run by mechanism code.
        var snapshot = new WorkspaceSnapshot("Session", new TimeRange(0, 30), [], [], [], [], [], [],
        [
            new TimelineBucket(new TimeRange(0, 10), 5, null, Mechanism.Udp, CoverageState.Covered),
            new TimelineBucket(new TimeRange(10, 20), 5, null, Mechanism.Tcp, CoverageState.Covered),
            new TimelineBucket(new TimeRange(20, 30), 5, null, Mechanism.ProcessLifecycle, CoverageState.Covered),
        ]);

        Assert.Equal(
            [.. new[] { Mechanism.ProcessLifecycle, Mechanism.Tcp, Mechanism.Udp }.Select(mechanism =>
                LegendEntry.For(mechanism, ThemeMode.Dark).Label)],
            WorkspaceRowBuilder.Legend(snapshot, ThemeMode.Dark).Select(entry => entry.Label));
    }
}
