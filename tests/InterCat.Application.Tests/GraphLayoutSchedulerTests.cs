using InterCat.Application;
using InterCat.Domain;
using Xunit;

namespace InterCat.Application.Tests;

public sealed class GraphLayoutSchedulerTests
{
    [Fact(DisplayName = "R7: a late obsolete layout cannot replace a newer graph even if its worker ignores cancellation")]
    public async Task LateObsoleteLayoutIsDiscarded()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new GraphLayoutScheduler(async (request, _) =>
        {
            if (request.GraphIdentity == "older")
            {
                firstEntered.SetResult();
                await releaseFirst.Task;
            }

            return EmptyResult(request.GraphIdentity);
        });

        Task<GraphLayoutResult?> older = scheduler.RequestAsync("older", [], [], []);
        await firstEntered.Task;
        Task<GraphLayoutResult?> newer = scheduler.RequestAsync("newer", [], [], []);
        Assert.Equal("newer", (await newer)!.GraphIdentity);

        releaseFirst.SetResult();
        Assert.Null(await older);
    }

    [Fact(DisplayName = "R7: a worker result naming another graph identity is refused")]
    public async Task WrongIdentityIsNotPublished()
    {
        using var scheduler = new GraphLayoutScheduler((_, _) => Task.FromResult(EmptyResult("other")));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await scheduler.RequestAsync("requested", [], [], []));
    }

    [Fact(DisplayName = "R7: a disposed scheduler cannot publish its in-flight layout")]
    public async Task DisposalInvalidatesTheWorker()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new GraphLayoutScheduler(async (request, _) =>
        {
            entered.SetResult();
            await release.Task;
            return EmptyResult(request.GraphIdentity);
        });

        Task<GraphLayoutResult?> pending = scheduler.RequestAsync("graph", [], [], []);
        await entered.Task;
        scheduler.Dispose();
        release.SetResult();
        Assert.Null(await pending);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await scheduler.RequestAsync("later", [], [], []));
    }

    [Fact(DisplayName = "R7: a submitted layout sees frozen graph and pin inputs rather than later gesture edits")]
    public async Task InputsAreFrozenAtSubmission()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GraphLayoutRequest? seen = null;
        using var scheduler = new GraphLayoutScheduler(async (request, _) =>
        {
            entered.SetResult();
            await release.Task;
            seen = request;
            return EmptyResult(request.GraphIdentity);
        });
        ProcessInstanceId id = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var nodes = new List<ProcessNode>
        {
            new(id, 1, "one", "test", "group", 0.2, 0.3, CoverageState.Covered),
        };
        var pins = new Dictionary<ProcessInstanceId, GraphPoint> { [id] = new(0.1, 0.2) };

        Task<GraphLayoutResult?> pending = scheduler.RequestAsync("graph", [], nodes, [], pins: pins);
        await entered.Task;
        nodes.Clear();
        pins[id] = new(0.9, 0.8);
        release.SetResult();
        Assert.NotNull(await pending);
        Assert.Single(seen!.Nodes);
        Assert.Equal(new GraphPoint(0.1, 0.2), seen.Pins![id]);
    }

    [Fact(DisplayName = "R7: caller cancellation propagates instead of looking like a superseded query")]
    public async Task CallerCancellationPropagates()
    {
        using var scheduler = new GraphLayoutScheduler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return EmptyResult("graph");
        });
        using var cancellation = new CancellationTokenSource();
        Task<GraphLayoutResult?> pending = scheduler.RequestAsync("graph", [], [], [],
            cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }

    private static GraphLayoutResult EmptyResult(string identity) =>
        new(identity, new Dictionary<ProcessInstanceId, GraphPoint>(), 0, 0);
}
