using InterCat.Application;
using InterCat.Domain;
using Xunit;

namespace InterCat.Application.Tests;

public sealed class EvidenceScopeTests
{
    private static readonly ProcessInstanceId Client = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly ProcessInstanceId Server = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));
    private static readonly ProcessInstanceId Other = new(Guid.Parse("33333333-3333-4333-8333-333333333333"));

    private static readonly WorkspaceSnapshot Snapshot = new(
        "session",
        new TimeRange(0, 100),
        [new("executable:C:\\APP.EXE", "C:\\app.exe", LaneGrouping.Executable),
            new("executable:C:\\OTHER.EXE", "C:\\other.exe", LaneGrouping.Executable)],
        [
            new(Client, 100, "app.exe", "Created", "executable:C:\\APP.EXE", 0, 0, CoverageState.UnknownCoverage),
            new(Server, 200, "app.exe", "Created", "executable:C:\\APP.EXE", 0, 0, CoverageState.UnknownCoverage),
            new(Other, 300, "other.exe", "Created", "executable:C:\\OTHER.EXE", 0, 0, CoverageState.UnknownCoverage),
        ],
        [new("tcp:a:b", Client, Server, Mechanism.Tcp, 10, null, RelationStrength.Correlated)],
        [new("transport:one", "tcp:a:b", "127.0.0.1:1 ↔ 127.0.0.1:2", Mechanism.Tcp, Direction.UnknownDirection, 10, null,
            CoverageState.UnknownCoverage)],
        [], [], []);

    [Fact]
    public void TheLatestEntityFilterDecidesAndRemovingOneWidensToTheNext()
    {
        NavigationState channel = Rung(
            Filter("group", "C:\\app.exe", DetailLevel.Group, "executable:C:\\APP.EXE"),
            Filter("process", "app.exe", DetailLevel.ProcessInstance, Client.ToString()),
            Filter("channel", "127.0.0.1:1 ↔ 127.0.0.1:2", DetailLevel.Channel, "transport:one"),
            Filter("scope", "127.0.0.1:1 ↔ 127.0.0.1:2", DetailLevel.Channel, "transport:one"));

        EvidenceScope scope = EvidenceScopes.Resolve(Snapshot, channel);
        Assert.Equal("transport:one", scope.ChannelKey);
        Assert.Empty(scope.OwnerProcesses);
        Assert.Equal("tcp:a:b", scope.ContributingEdgeKey);
        Assert.Null(scope.Interval);
        Assert.Null(scope.Problem);

        EvidenceScope process = EvidenceScopes.Resolve(Snapshot, channel with { Filters = channel.Filters.Take(2).ToArray() });
        Assert.Null(process.ChannelKey);
        Assert.Equal([Client], process.OwnerProcesses);
        Assert.Contains("app.exe · PID 100", process.Description, StringComparison.Ordinal);

        EvidenceScope group = EvidenceScopes.Resolve(Snapshot, channel with { Filters = channel.Filters.Take(1).ToArray() });
        Assert.Equal([Client, Server], group.OwnerProcesses);
        Assert.Contains("2 instances", group.Description, StringComparison.Ordinal);

        EvidenceScope whole = EvidenceScopes.Resolve(Snapshot, channel with { Filters = [] });
        Assert.True(whole.IsWholeSession);
        Assert.StartsWith("Every admitted record", whole.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AMachineStepReadsTheWholeSessionAndABrushNarrowsItsTime()
    {
        NavigationState rung = Rung(Filter("scope", "Whole machine", DetailLevel.Machine, null)) with
        {
            Viewport = new TimeRange(10, 20),
        };

        EvidenceScope scope = EvidenceScopes.Resolve(Snapshot, rung);
        Assert.True(scope.IsWholeSession);
        Assert.Equal(new TimeRange(10, 20), scope.Interval);
        Assert.Null(EvidenceScopes.Resolve(Snapshot, rung with { Viewport = Snapshot.Extent }).Interval);
    }

    [Fact]
    public void AScopeThatCannotBeReadSaysSoInsteadOfBecomingTheWholeSession()
    {
        EvidenceScope emptyGroup = EvidenceScopes.Resolve(Snapshot,
            Rung(Filter("scope", "gone.exe", DetailLevel.Group, "executable:GONE")));
        Assert.NotNull(emptyGroup.Problem);
        Assert.Empty(emptyGroup.OwnerProcesses);

        EvidenceScope badProcess = EvidenceScopes.Resolve(Snapshot,
            Rung(Filter("scope", "?", DetailLevel.ProcessInstance, "not-a-guid")));
        Assert.NotNull(badProcess.Problem);

        EvidenceScope operation = EvidenceScopes.Resolve(Snapshot,
            Rung(Filter("scope", "send", DetailLevel.Operation, "operation:1")));
        Assert.Contains("no logical operations", operation.Problem, StringComparison.Ordinal);

        // A label-only filter is not an entity scope and is skipped.
        Assert.True(EvidenceScopes.Resolve(Snapshot, Rung(new ImpliedFilter("note", "x", "y"))).IsWholeSession);
    }

    [Fact]
    public void AnExplicitScopeStepCarriesItsKeyAndLevelIntoTheFilterBar()
    {
        NavigationState machine = Rung() with { Level = DetailLevel.Machine, Focus = null, Filters = [] };
        LadderDescent descent = LadderProjection.EvidenceDescentFor(machine, Snapshot.Extent,
            new(DetailLevel.Channel, "transport:one", "the channel"), "chosen in a list");

        ImpliedFilter filter = Assert.Single(descent.AddedFilters);
        Assert.Equal("scope", filter.Field);
        Assert.Equal("transport:one", filter.Key);
        Assert.Equal(DetailLevel.Channel, filter.Level);
        Assert.Equal(new LadderTarget(DetailLevel.Evidence, "transport:one", "the channel"), descent.Target);

        ImpliedFilter machineStep = Assert.Single(LadderProjection.EvidenceDescentFor(machine, Snapshot.Extent).AddedFilters);
        Assert.Equal(DetailLevel.Machine, machineStep.Level);
        Assert.Null(machineStep.Key);
    }

    private static NavigationState Rung(params ImpliedFilter[] filters) => new()
    {
        Level = DetailLevel.Evidence,
        Focus = new LadderTarget(DetailLevel.Evidence, "focus", "Focus"),
        Viewport = Snapshot.Extent,
        Lanes = LaneGrouping.Endpoint,
        GraphFocusKey = null,
        Filters = filters,
    };

    private static ImpliedFilter Filter(string field, string value, DetailLevel level, string? key) =>
        new(field, value, "test") { Key = key, Level = level };
}
