using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// S3 in the window: a session's first view does not wait for its RPC calls to be followed through every segment's ALPC
/// records. Its graph draws the rest and says the calls are still being followed, and draws their edges once they are,
/// keeping what was chosen meanwhile; a following that fails says why, and opening another session stops it.
/// </summary>
public sealed class RpcCallsFollowingWindowTests
{
    private const string Brief = "RPC calls still being followed";
    private const string Sentence = "Which process called which over RPC is still being followed through ALPC";

    [AvaloniaFact(DisplayName = "S3: a session's first view draws its graph without waiting for its RPC calls, says they are still being followed, and draws their edges once they are, keeping the process chosen and the policy chosen meanwhile")]
    public async Task AFirstViewDrawsTheCallsOnceFollowed()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(), fields: LinkedRpcCalls().Fields, coverage: RpcLedger(alpc: true));
        // Each policy's following is held apart, so which ends first is the test's to say.
        using var correlated = new SemaphoreSlim(0);
        using var candidates = new SemaphoreSlim(0);
        var correlatedFollowed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int followed = 0;
        var window = new MainWindow { Width = 1_400, Height = 900 };
        window.RpcCallsFollower = (store, policy, stopped) =>
        {
            Interlocked.Increment(ref followed);
            (policy == EvidencePolicy.IncludeCorrelated ? correlated : candidates).Wait(stopped);
            SessionOverviewBundle projected = SessionOverviewProjector.Project(store, policy, cancellationToken: stopped);
            if (policy == EvidencePolicy.IncludeCorrelated)
            {
                correlatedFollowed.TrySetResult();
            }

            return projected;
        };
        window.Show();
        Assert.True(await window.OpenSessionAsync(session.Path));
        await window.SessionFilesChecked;
        Dispatch();

        // The first view draws the connection between the two processes, and says their calls are to come.
        var first = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await first.LayoutReady;
        Assert.True(first.WholeSnapshot.RpcCallsPending);
        Assert.Equal([Mechanism.Tcp], first.WholeSnapshot.Edges.Select(edge => edge.Mechanism));
        Assert.Contains(" · paired TCP and UDP · " + Brief, first.GraphSummary, StringComparison.Ordinal);
        Assert.Contains(Sentence, first.GraphScope, StringComparison.Ordinal);
        Assert.DoesNotContain("paired TCP and UDP only", first.GraphSummary, StringComparison.Ordinal);

        // Chosen meanwhile, the caller stays chosen; counting candidates meanwhile counts them in the view that follows.
        first.SelectedRung = first.RungRows.Single(row => row.Label == "caller.exe");
        Assert.True(await window.ChooseEvidencePolicyAsync(EvidencePolicy.IncludeCandidates));
        Dispatch();
        var chosen = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.True(chosen.WholeSnapshot.RpcCallsPending);
        Assert.Equal("caller.exe", chosen.SelectedRung?.Label);

        // The following under the policy the session opened with ends first: that view is no longer shown, so the view
        // shown stays as it is, its policy too.
        correlated.Release();
        await correlatedFollowed.Task.WaitAsync(TimeSpan.FromMinutes(1));
        for (int turn = 0; turn < 20; turn++)
        {
            Dispatch();
            await Task.Delay(10);
        }

        Assert.Same(chosen, window.DataContext);

        // Followed under the policy chosen, the calls' edge is drawn, the words that they were to come go, and the
        // choices stand.
        candidates.Release();
        await window.RpcCallsFollowed;
        Dispatch();
        var drawn = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await drawn.LayoutReady;
        Assert.Equal((false, EvidencePolicy.IncludeCandidates), (drawn.WholeSnapshot.RpcCallsPending, drawn.EvidencePolicy));
        Assert.Equal([Mechanism.Tcp, Mechanism.Rpc], drawn.WholeSnapshot.Edges.Select(edge => edge.Mechanism).Order());
        Assert.DoesNotContain(Brief, drawn.GraphSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentence, drawn.GraphScope, StringComparison.Ordinal);
        Assert.Contains(" · paired TCP, UDP and RPC calls", drawn.GraphSummary, StringComparison.Ordinal);
        Assert.Equal("caller.exe", drawn.SelectedRung?.Label);
        Assert.Equal(2, followed);

        // The calls are followed once for the session: another policy draws them at once.
        Assert.True(await window.ChooseEvidencePolicyAsync(EvidencePolicy.IncludeCorrelated));
        Dispatch();
        var again = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.False(again.WholeSnapshot.RpcCallsPending);
        Assert.Contains(again.WholeSnapshot.Edges, edge => edge.Mechanism == Mechanism.Rpc);
        Assert.Equal(2, followed);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "S3: an empty graph whose RPC calls are still being followed says so beneath it; a following that fails says why, and the view stays as it was")]
    public async Task AFailedFollowingSaysWhy()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] linked, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        Publish(session.Store, [.. linked.Select(Timed)], fields: fields, coverage: RpcLedger(alpc: true));
        using var release = new SemaphoreSlim(0);
        var window = new MainWindow { Width = 1_400, Height = 900 };
        window.RpcCallsFollower = (_, _, stopped) =>
        {
            release.Wait(stopped);
            throw new IOException("Another program holds a segment.");
        };
        window.Show();
        Assert.True(await window.OpenSessionAsync(session.Path));
        await window.SessionFilesChecked;
        Dispatch();
        var shown = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await shown.LayoutReady;

        // Nothing relates two processes but their calls, so the graph says so beneath it, and that the calls are to come.
        Assert.Empty(shown.WholeSnapshot.Edges);
        Assert.EndsWith(Sentence + "; those edges are drawn when it is done.", shown.GraphEmptyExplanation, StringComparison.Ordinal);
        Assert.Contains(Brief, shown.GraphSummary, StringComparison.Ordinal);
        Assert.True(window.GetControl<TextBlock>("GraphEmptyText").IsEffectivelyVisible);

        release.Release();
        await window.RpcCallsFollowed;
        Dispatch();
        Assert.Same(shown, window.DataContext);
        Assert.Equal("Its RPC calls could not be followed through ALPC, so the graph draws none of their edges: Another program "
            + "holds a segment.", window.GetControl<TextBlock>("CaptureDetail").Text);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "S3: opening another session stops following the RPC calls of the one before it, which changes nothing it shows")]
    public async Task OpeningAnotherStopsTheFollowing()
    {
        using var session = new TemporarySession();
        using var other = new TemporarySession();
        Publish(session.Store, Rows(), fields: LinkedRpcCalls().Fields, coverage: RpcLedger(alpc: true));
        Publish(other.Store, [Timed(Lifecycle(5, ObservationKind.Create, 700, 1) with { ResourceName = @"C:\Tools\other.exe" })]);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new MainWindow { Width = 1_400, Height = 900 };
        window.RpcCallsFollower = (_, _, token) =>
        {
            started.TrySetResult(token);
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The following was not stopped.");
        };
        window.Show();
        Assert.True(await window.OpenSessionAsync(session.Path));
        Task following = window.RpcCallsFollowed;
        CancellationToken token = await started.Task.WaitAsync(TimeSpan.FromMinutes(1));
        Assert.False(token.IsCancellationRequested);
        Assert.True(await window.OpenSessionAsync(other.Path));
        Assert.True(token.IsCancellationRequested);
        await following;
        Dispatch();
        var shown = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.Equal(["other.exe"], shown.RungRows.Select(row => row.Label));
        Assert.False(shown.WholeSnapshot.RpcCallsPending);
        window.Close();
    }

    /// <summary>The linked calls between a caller and the service control manager, and one TCP connection between them.</summary>
    private static ObservationRowV1[] Rows()
    {
        (ObservationRowV1[] linked, _) = LinkedRpcCalls();
        return
        [
            .. linked.Select(row => Timed(row.Mechanism != Mechanism.ProcessLifecycle ? row
                : row with { ResourceName = row.OwnerProcessId == 400 ? @"C:\Tools\caller.exe" : @"C:\Windows\System32\services.exe" })),
            Timed(Transfer(130, ObservationKind.Send, AccountingSide.SendSide, 32, 400, 70).Between("127.0.0.1:50000", "127.0.0.1:8080")),
            Timed(Transfer(131, ObservationKind.Receive, AccountingSide.ReceiveSide, 32, 1_960, 71).Between("127.0.0.1:8080", "127.0.0.1:50000")),
        ];
    }

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
