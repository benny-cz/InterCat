using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using static InterCat.Desktop.Tests.WorkspaceAnswers;

namespace InterCat.Desktop.Tests;

/// <summary>
/// §13.2's async publication property (R6, R7): an answer that lands late - read, then held while the person brushed
/// again, descended, removed a filter or went to another process - never mixes the scope it was asked for with the one on
/// screen. Each race is compared whole with the same gestures answered in time.
/// </summary>
public sealed class AsyncPublicationTests
{
    private static readonly TimeRange Brush = new(10, 30);

    [Fact(DisplayName = "R7: a brush's count that lands after a later brush's was shown is never applied: the ranking, the table, the graph and an export answer the later brush")]
    public void ACountLandingAfterALaterBrushIsNeverApplied() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, EvidenceRungTests.Rows());

        // Brushed wide, then narrow before the wide brush's count could land.
        async Task<string> Brushed(HeldAnswers? held)
        {
            using WorkspaceViewModel workspace = Open(session);
            await Settled(workspace);
            held?.Arm("LoadIntervalAsync");
            workspace.SelectInterval(new TimeRange(10, 50));
            Task late = workspace.IntervalReady;
            if (held is null) await Settled(workspace);
            else Assert.True(held.Reached());
            workspace.SelectInterval(Brush);
            await Settled(workspace);
            Assert.True(held is null || !late.IsCompleted, "The wide brush's count landed before it was let go.");
            held?.Release();
            await late;
            await Settled(workspace);
            Assert.Equal("20", workspace.RungRows.Single().Observations);
            return Of(workspace);
        }

        string inTime = await Brushed(null);
        Assert.Equal(inTime, await Brushed(HeldAnswers.Install()));
    });

    [Fact(DisplayName = "R7: a brush's count that lands after a filter was added or removed answers the rung on screen, as it would have in time")]
    public void ACountLandingAfterTheFiltersChangedAnswersTheRungShown() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, EvidenceRungTests.Rows());

        // Brushed, then the rung changed while the count was read: the machine's one group descended into, a filter added,
        // or, brushed in that group, its filter removed.
        async Task<string> Changed(bool remove, HeldAnswers? held)
        {
            using WorkspaceViewModel workspace = Open(session);
            await Settled(workspace);
            string group = workspace.RungRows.Single().Key;
            if (remove)
            {
                Descend(workspace, group);
                await Settled(workspace);
            }

            held?.Arm("LoadIntervalAsync");
            workspace.SelectInterval(Brush);
            Task counted = workspace.IntervalReady;
            if (held is null) await Settled(workspace);
            else Assert.True(held.Reached());
            if (remove) RemoveFilter(workspace, "group");
            else Descend(workspace, group);
            held?.Release();
            await counted;
            await Settled(workspace);
            return Of(workspace);
        }

        string added = await Changed(remove: false, null);
        string removed = await Changed(remove: true, null);
        Assert.Contains($"scope {Brush} · Ranked within", added, StringComparison.Ordinal);
        Assert.Contains($"scope {Brush} · Ranked within", removed, StringComparison.Ordinal);
        Assert.Equal(added, await Changed(remove: false, HeldAnswers.Install()));
        Assert.Equal(removed, await Changed(remove: true, HeldAnswers.Install()));
    });

    [Fact(DisplayName = "R7: a process's channels and timeline read before its rung was left are never shown: the next process's rung lists and draws its own")]
    public void ReadsForARungLeftAreNeverShown() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, RpcScopeTests.Calls());

        // At the caller's rung, brushed - its channels read again for the brush - and its timeline asked for a narrower
        // view; then its rung left, its process filter with it, for the server's, whose own reads land first.
        async Task<string> Left(HeldAnswers? held)
        {
            using WorkspaceViewModel workspace = Open(session);
            ProcessNode caller = workspace.Snapshot.Processes.Single(node => node.ProcessId == 400);
            ProcessNode server = workspace.Snapshot.Processes.Single(node => node.ProcessId == 1_960);
            Descend(workspace, caller.GroupKey);
            Descend(workspace, caller.Id.ToString());
            workspace.RequestTimelineDetail(new TimeRange(0, 500), 50);
            await Settled(workspace);

            held?.Arm("LoadRpcChannelsAsync", "LoadTimelineDetailAsync");
            workspace.SelectInterval(new TimeRange(110, 250));
            await workspace.IntervalReady;
            Task channels = workspace.RpcReady;
            workspace.RequestTimelineDetail(new TimeRange(100, 400), 50);
            Task drawn = workspace.TimelineDetailReady;
            if (held is null) await Settled(workspace);
            else Assert.True(held.Reached(2));

            Assert.True(workspace.Ascend());
            Assert.True(workspace.Ascend());
            Descend(workspace, server.GroupKey);
            Descend(workspace, server.Id.ToString());
            await Settled(workspace);
            Assert.True(held is null || !(channels.IsCompleted || drawn.IsCompleted), "The caller's reads landed before they were let go.");
            held?.Release();
            await channels;
            await drawn;
            await Settled(workspace);
            Assert.True(workspace.TimelineShowsFocus);
            return Of(workspace);
        }

        string inTime = await Left(null);
        Assert.Contains("row ", inTime, StringComparison.Ordinal);
        Assert.Equal(inTime, await Left(HeldAnswers.Install()));
    });

    [Fact(DisplayName = "R7: a call's records that land after the call's filter was removed are never listed: E lists its channel's calls, as it would have in time")]
    public void RecordsForARemovedFilterAreNeverListed() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, RpcScopeTests.Calls());

        // E for the caller's first call to svcctl, whose filter is removed before its records land.
        async Task<string> Widened(HeldAnswers? held)
        {
            using WorkspaceViewModel workspace = Open(session);
            ProcessNode caller = workspace.Snapshot.Processes.Single(node => node.ProcessId == 400);
            Descend(workspace, caller.GroupKey);
            Descend(workspace, caller.Id.ToString());
            await Settled(workspace);
            Descend(workspace, workspace.RungRows.Single(row => row.Label.StartsWith("svcctl", StringComparison.Ordinal)).Key);
            await Settled(workspace);

            held?.Arm("LoadEvidenceAsync");
            Descend(workspace, workspace.RungRows[0].Key);
            Task listed = workspace.EvidenceReady;
            if (held is null) await Settled(workspace);
            else Assert.True(held.Reached());
            RemoveFilter(workspace, "scope");
            await Settled(workspace);
            Assert.True(held is null || !listed.IsCompleted, "The call's records landed before they were let go.");
            held?.Release();
            await listed;
            await Settled(workspace);
            Assert.Equal(6, workspace.RungRows.Count);
            return Of(workspace);
        }

        string inTime = await Widened(null);
        Assert.Equal(inTime, await Widened(HeldAnswers.Install()));
    });

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    private static void Descend(WorkspaceViewModel workspace, string key)
    {
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
        Assert.True(workspace.Descend());
    }

    private static void RemoveFilter(WorkspaceViewModel workspace, string field)
    {
        workspace.SelectedFilter = workspace.Filters.First(filter => filter.Field == field);
        Assert.True(workspace.RemoveSelectedFilter());
    }
}
