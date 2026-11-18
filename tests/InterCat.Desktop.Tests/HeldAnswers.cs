using InterCat.Desktop;

namespace InterCat.Desktop.Tests;

/// <summary>
/// Holds back, once armed, the next answer each named step of a workspace reads - after it is read, before the step can
/// apply it - until released, as a busy machine delivers an answer late, after what was asked since (§13.2's async
/// publication). Installed as <see cref="LaterTurn.Holding"/>, it flows with the test that installed it and no other.
/// </summary>
internal sealed class HeldAnswers
{
    private readonly Lock gate = new();
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<string> armed = [];
    private int held;

    /// <summary>Holds this test's answers from here on, each as it was armed.</summary>
    public static HeldAnswers Install()
    {
        var answers = new HeldAnswers();
        LaterTurn.Holding.Value = answers.Answer;
        return answers;
    }

    /// <summary>Holds the next answer each step named here reads, by the name of the method that awaits it.</summary>
    public void Arm(params string[] askers)
    {
        lock (gate)
        {
            armed.UnionWith(askers);
        }
    }

    /// <summary>Whether <paramref name="count"/> armed answers were read and are held, waiting a while for them.</summary>
    public bool Reached(int count = 1) => SpinWait.SpinUntil(
        () =>
        {
            lock (gate)
            {
                return held >= count;
            }
        },
        TimeSpan.FromSeconds(30));

    /// <summary>Lets every held answer go, each to land on a later turn of the thread that asked.</summary>
    public void Release() => released.TrySetResult();

    private Task Answer(string asker)
    {
        lock (gate)
        {
            if (!armed.Remove(asker))
            {
                return Task.CompletedTask;
            }

            held++;
        }

        return released.Task;
    }
}

/// <summary>What a workspace's panes answer, in words, so a raced workspace can be compared whole with one answered in time.</summary>
internal static class WorkspaceAnswers
{
    public static string Of(WorkspaceViewModel workspace) => string.Join(
        "\n",
        [
            $"rung {workspace.TimelineRungCaption}",
            $"scope {workspace.ScopeInterval} · {workspace.RankingScopeText}",
            .. workspace.RungRows.Select(row => $"row {row.Key} · {row.Label} · {row.Observations} · {row.Detail}"),
            .. workspace.Relationships.Select(row => $"relationship {row.Source} > {row.Target} · {row.Mechanism} · {row.ObservationCount}"),
            .. workspace.GraphDisplay.Edges.Select(edge => $"edge {edge.Key} · {edge.ObservationCount}"),
            $"focus {(workspace.TimelineFocusBuckets is { } focus ? string.Join(",", focus.Select(bucket => bucket.ObservationCount)) : "none")}",
            $"export {workspace.DescribeExport(DateTimeOffset.UnixEpoch).Interval}",
        ]);

    /// <summary>
    /// Waits until nothing the workspace reads is under way and no answer awaits its turn: each read's readiness, again
    /// after every turn that may have started another.
    /// </summary>
    public static async Task Settled(WorkspaceViewModel workspace)
    {
        for (int round = 0; round < 50; round++)
        {
            // A read a test still holds would never settle: say so rather than wait for ever.
            Task[] ready = Ready(workspace);
            await Task.WhenAll(ready).WaitAsync(TimeSpan.FromSeconds(60));
            await Task.Yield();
            if (ready.SequenceEqual(Ready(workspace)))
            {
                return;
            }
        }

        throw new InvalidOperationException("The workspace kept reading.");
    }

    private static Task[] Ready(WorkspaceViewModel workspace) =>
        [workspace.IntervalReady, workspace.RpcReady, workspace.EvidenceReady, workspace.TimelineDetailReady];
}
