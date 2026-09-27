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

    [Fact(DisplayName = "§6.6: the legend keys what the panes draw: not an empty column's placeholder, and every lane with records")]
    public void TheLegendKeysOnlyWhatIsDrawn()
    {
        // An empty column carries the unknown mechanism as a placeholder and draws nothing, so nothing is keyed
        // "Unknown". UDP never dominates a column but draws in its own lane, so it is keyed.
        var snapshot = new WorkspaceSnapshot("Session", new TimeRange(0, 30), [], [], [], [], [], [],
        [
            new TimelineBucket(new TimeRange(0, 10), 9, null, Mechanism.ProcessLifecycle, CoverageState.Covered),
            new TimelineBucket(new TimeRange(10, 20), 0, null, Mechanism.UnknownMechanism, CoverageState.Covered),
            new TimelineBucket(new TimeRange(20, 30), 4, null, Mechanism.Tcp, CoverageState.Covered),
        ])
        {
            MechanismLanes =
            [
                new(Mechanism.ProcessLifecycle, [new(new TimeRange(0, 10), 9, null, Mechanism.ProcessLifecycle, CoverageState.Covered)]),
                new(Mechanism.Tcp, [new(new TimeRange(20, 30), 3, null, Mechanism.Tcp, CoverageState.Covered)]),
                new(Mechanism.Udp, [new(new TimeRange(20, 30), 1, null, Mechanism.Udp, CoverageState.Covered)]),
                new(Mechanism.Rpc, [new(new TimeRange(20, 30), 0, null, Mechanism.Rpc, CoverageState.Covered)]),
            ],
        };

        string[] keyed = [.. WorkspaceRowBuilder.Legend(snapshot, ThemeMode.Dark).Select(entry => entry.Label)];
        Assert.Equal(["Lifecycle", "TCP", "UDP"], keyed);

        // A column whose records are of no known mechanism does draw, and is keyed as such.
        WorkspaceSnapshot unknown = snapshot with
        {
            Timeline = [new TimelineBucket(new TimeRange(0, 10), 2, null, Mechanism.UnknownMechanism, CoverageState.Covered)],
            MechanismLanes = [],
        };
        Assert.Equal(["Unknown"], WorkspaceRowBuilder.Legend(unknown, ThemeMode.Dark).Select(entry => entry.Label));
    }
}
