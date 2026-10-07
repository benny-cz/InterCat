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
        [new("tcp:a:b", Client, Server, Mechanism.Tcp, 10, null, RelationStrength.Correlated) { Rule = RelationRule.TransportEndpoint, Evidence = ["transport:one"] }],
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

    [Fact(DisplayName = "R5: a channel row names its mechanism and direction in words, never an enumeration name")]
    public void AChannelRowNamesItsMechanismInWords()
    {
        var ladder = new DetailLadder(SyntheticWorkspace.Root(Snapshot));
        foreach (string key in new[] { "executable:C:\\APP.EXE", Client.ToString() })
        {
            LadderRow row = LadderProjection.Project(Snapshot, ladder.Current).Rows.Single(candidate => candidate.Key == key);
            Assert.True(ladder.TryDescend(LadderProjection.DescentFor(row, ladder.Current, Snapshot.Extent), out _));
        }

        // A process rung is named by its name and PID, in its crumb and its filter: a group lists many of one name.
        ProcessNode client = Snapshot.Processes.Single(process => process.Id == Client);
        Assert.Equal($"app.exe · PID {client.ProcessId}", ladder.Current.Focus?.Label);
        Assert.Equal(client.NameWithPid, ladder.Current.Filters.Single(filter => filter.Field == "process").Value);

        // The legend, the lanes and the tables call it TCP; the ranked row read "Tcp", the enumeration's own name.
        LadderRow channel = Assert.Single(LadderProjection.Project(Snapshot, ladder.Current).Rows);
        Assert.Equal("TCP · paired endpoints; direction varies by observation", channel.Detail);

        // Every other direction in the one word the timeline's lanes use too, and a code no version knows by its number.
        Assert.Equal(["TCP · outbound", "TCP · inbound", "TCP · bidirectional", "TCP · no data direction", "TCP · direction 9"],
            new[] { Direction.Outbound, Direction.Inbound, Direction.Bidirectional, Direction.DirectionNotApplicable, (Direction)9 }
                .Select(direction => Assert.Single(LadderProjection.Project(
                    Snapshot with { Channels = [Snapshot.Channels[0] with { Direction = direction }] }, ladder.Current).Rows).Detail));
    }

    [Fact(DisplayName = "§6.5: a process whose executable was not witnessed is named by its PID once")]
    public void AnUnwitnessedProcessIsNamedByItsPidOnce()
    {
        var unnamed = new ProcessNode(new(Guid.Parse("44444444-4444-4444-8444-444444444444")), 812,
            ProcessNode.PidName(812), "ActivityOnly", "executable:C:\\OTHER.EXE", 0, 0, CoverageState.UnknownCoverage);
        WorkspaceSnapshot snapshot = Snapshot with { Processes = [.. Snapshot.Processes, unnamed] };

        Assert.Equal("PID 812", unnamed.NameWithPid);
        Assert.Equal("app.exe · PID 100", snapshot.Processes[0].NameWithPid);
        Assert.Equal("Records owned by PID 812", EvidenceScopes.Resolve(snapshot,
            Rung(Filter("process", "PID 812", DetailLevel.ProcessInstance, unnamed.Id.ToString()))).Description);

        // Only the exact fallback name collapses: an executable whose name merely looks like a PID keeps its own PID.
        Assert.Equal("PID 9 · PID 812", (unnamed with { Name = "PID 9" }).NameWithPid);
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

    [Fact(DisplayName = "§6.7: a multi-selection turned into a filter reads exactly the chosen processes still present")]
    public void AChosenSetIsAFilterOfExactlyItsMembers()
    {
        // Two of the three, across two groups: neither group's membership, and never a PID.
        string key = ProcessSetFilter.KeyOf([Other, Client, Client]);
        Assert.True(ProcessSetFilter.TryParse(key, out ProcessInstanceId[] parsed));
        Assert.Equal([Client, Other], parsed);
        Assert.Equal(key, ProcessSetFilter.KeyOf([Client, Other]));

        EvidenceScope chosen = EvidenceScopes.Resolve(Snapshot,
            Rung(Filter("scope", ProcessSetFilter.Label(2), DetailLevel.Group, key)));
        Assert.Equal([Client, Other], chosen.OwnerProcesses);
        Assert.Null(chosen.ChannelKey);
        Assert.Null(chosen.Problem);
        Assert.StartsWith("Records owned by 2 selected processes", chosen.Description, StringComparison.Ordinal);

        // A member this generation no longer holds is not read, and a set with none left is no scope at all.
        var gone = new ProcessInstanceId(Guid.Parse("44444444-4444-4444-8444-444444444444"));
        Assert.Equal([Client], EvidenceScopes.Resolve(Snapshot,
            Rung(Filter("scope", "2 selected processes", DetailLevel.Group, ProcessSetFilter.KeyOf([Client, gone])))).OwnerProcesses);
        Assert.NotNull(EvidenceScopes.Resolve(Snapshot,
            Rung(Filter("scope", "1 selected process", DetailLevel.Group, ProcessSetFilter.KeyOf([gone])))).Problem);

        // An aggregate's processes, chosen by its name, read as its, counted as this generation still holds them.
        Assert.StartsWith("Records owned by the 2 processes of Rest of the machine", EvidenceScopes.Resolve(Snapshot,
            Rung(Filter("scope", "Rest of the machine", DetailLevel.Group, key))).Description, StringComparison.Ordinal);
        Assert.StartsWith("Records owned by the 1 process of No relationships", EvidenceScopes.Resolve(Snapshot,
            Rung(Filter("scope", "No relationships", DetailLevel.Group, ProcessSetFilter.KeyOf([Client, gone])))).Description,
            StringComparison.Ordinal);

        // Keys that name no set are not read as one.
        Assert.False(ProcessSetFilter.TryParse("executable:C:\\APP.EXE", out _));
        Assert.False(ProcessSetFilter.TryParse(ProcessSetFilter.Prefix, out _));
        Assert.False(ProcessSetFilter.TryParse(ProcessSetFilter.Prefix + "not-a-guid", out _));
    }

    [Fact(DisplayName = "§6.4: a lane's filters narrow the scope the latest entity filter names, and an end of anything but a paired channel is a problem stated")]
    public void ALanesFiltersNarrowTheScope()
    {
        // One mechanism's records, at the machine rung, are no longer the whole session, and its timeline focus counts them.
        EvidenceScope tcp = EvidenceScopes.Resolve(Snapshot, Rung(EvidenceScopes.MechanismFilter(Mechanism.Tcp, "test")));
        Assert.Equal((Mechanism?)Mechanism.Tcp, tcp.Mechanism);
        Assert.False(tcp.IsWholeSession);
        Assert.Equal("Every admitted record in this session · TCP records only", tcp.Description);
        Assert.Equal((Mechanism?)Mechanism.Tcp, Assert.IsType<TimelineFocus>(TimelineFocus.Of(tcp)).Mechanism);

        // One source direction of a process's records.
        ImpliedFilter process = Filter("process", "app.exe", DetailLevel.ProcessInstance, Client.ToString());
        EvidenceScope outbound = EvidenceScopes.Resolve(Snapshot, Rung(process, EvidenceScopes.DirectionFilter(Direction.Outbound, "test")));
        Assert.Equal([Client], outbound.OwnerProcesses);
        Assert.Equal((Direction?)Direction.Outbound, outbound.Direction);
        Assert.Equal("Records owned by app.exe · PID 100 · records marked outbound only", outbound.Description);
        ImpliedFilter none = EvidenceScopes.DirectionFilter(Direction.DirectionNotApplicable, "test");
        Assert.Equal((EvidenceScopes.DirectionField, "No data direction", "DirectionNotApplicable"), (none.Field, none.Value, none.Key));
        Assert.EndsWith(" · records with no data direction only", EvidenceScopes.Resolve(Snapshot, Rung(process, none)).Description,
            StringComparison.Ordinal);

        // The records made at one end of a paired channel, which its focus counts too; removing the end widens back.
        NavigationState channel = Rung(Filter("channel", "127.0.0.1:1 ↔ 127.0.0.1:2", DetailLevel.Channel, "transport:one"),
            EvidenceScopes.EndFilter(0, "127.0.0.1:1", "test"));
        EvidenceScope end = EvidenceScopes.Resolve(Snapshot, channel);
        Assert.Equal(("transport:one", (int?)0), (end.ChannelKey, end.End));
        Assert.Equal("Paired TCP channel 127.0.0.1:1 ↔ 127.0.0.1:2 · those made at 127.0.0.1:1 only", end.Description);
        Assert.Equal(0, TimelineFocus.Of(end)!.End);
        Assert.Null(EvidenceScopes.Resolve(Snapshot, channel with { Filters = channel.Filters.Take(1).ToArray() }).End);

        // An end narrows only a paired channel, and a filter naming no mechanism is said, never ignored.
        Assert.StartsWith("An end filter names one end of a paired TCP channel", EvidenceScopes.Resolve(Snapshot,
            Rung(process, EvidenceScopes.EndFilter(1, "127.0.0.1:2", "test"))).Problem, StringComparison.Ordinal);
        Assert.NotNull(EvidenceScopes.Resolve(Snapshot,
            Rung(new ImpliedFilter(EvidenceScopes.MechanismField, "Pigeon", "test") { Key = "Pigeon" })).Problem);
        Assert.Throws<ArgumentOutOfRangeException>(() => EvidenceScopes.EndFilter(2, "127.0.0.1:1", "test"));
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
