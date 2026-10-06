using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// R21 in the inspector: beneath its time scope the card states what the capture covered there, mechanism by mechanism, so
/// a count of none where the capture delivered no reading, or lost some, is never read as no activity.
/// </summary>
public sealed class ScopeCoverageTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";
    private const string Collected = "process lifecycle and TCP";
    private const string Lost = "the session reported 1 lost event, which may be any mechanism's";

    [Fact(DisplayName = "R21: the inspector states the capture's coverage over its time scope, so a count of none where the capture saw nothing is not read as no activity")]
    public void TheInspectorStatesItsScopesCoverage() => SingleThreadedContext.Run(async () =>
    {
        // The capture delivered readings from 0 to 20 and from 40 to 60, and none between; its second epoch lost an event.
        using var session = new TemporarySession();
        Publish(session.Store, Rows(), coverage: new CoverageLedgerV1
        {
            Contract = CoverageLedgerV1.ContractName,
            Epochs = [Epoch(1, 0, 20, processes: 2, lost: 0), Epoch(2, 40, 60, processes: 0, lost: 1)],
        });
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(process => process.ProcessId == 100);

        // Over the whole session each mechanism is as complete as its worst epoch, and what the capture did not collect is
        // said too: no count of it was possible.
        Assert.Equal($"Coverage: a partial gap, not extrapolated, for {Collected}: {Lost} · no other mechanism collected",
            workspace.ScopeCoverage);
        Assert.True(workspace.ScopeCoverageLimited);

        // Brushed where the capture delivered nothing, the client made no record there, and the card says that is not
        // proof of inactivity. Until the range is counted, its coverage is said to be read, not carried from elsewhere.
        workspace.SelectProcess(client.Id);
        workspace.SelectInterval(new TimeRange(25, 35));
        Assert.Equal("Reading this range's coverage…", workspace.ScopeCoverage);
        Assert.False(workspace.ScopeCoverageLimited);
        await workspace.IntervalReady;
        Assert.StartsWith("No own record admitted", workspace.EvidenceSummary, StringComparison.Ordinal);
        Assert.Equal("Coverage unknown: outside the readings the capture's sources delivered, so a count of none here is "
            + "not proof of inactivity", workspace.ScopeCoverage);
        Assert.True(workspace.ScopeCoverageLimited);

        // E lists no record there, and says where the scope's coverage is stated.
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Contains("not proof of inactivity: the inspector states this scope's coverage under its time scope",
            workspace.EmptyReason, StringComparison.Ordinal);
        Assert.True(workspace.Ascend());

        // Within the first epoch the same counts are covered, and within the second they carry its loss. A new range is
        // read, not stated with the coverage of the range counted before it.
        workspace.SelectInterval(new TimeRange(8, 15));
        Assert.Equal("Reading this range's coverage…", workspace.ScopeCoverage);
        await workspace.IntervalReady;
        Assert.StartsWith("1 own record", workspace.EvidenceSummary, StringComparison.Ordinal);
        Assert.Equal($"Coverage: covered for {Collected} · no other mechanism collected", workspace.ScopeCoverage);
        Assert.False(workspace.ScopeCoverageLimited);
        workspace.SelectInterval(new TimeRange(45, 55));
        await workspace.IntervalReady;
        Assert.Equal($"Coverage: a partial gap, not extrapolated, for {Collected}: {Lost} · no other mechanism collected",
            workspace.ScopeCoverage);

        // The visible range is a scope too, and fitting the timeline gives the whole session's back.
        workspace.ClearSelection();
        await workspace.IntervalReady;
        workspace.ShowVisibleRange(new TimeRange(25, 35));
        await workspace.IntervalReady;
        Assert.StartsWith("Coverage unknown: outside the readings", workspace.ScopeCoverage, StringComparison.Ordinal);
        workspace.ShowVisibleRange(workspace.WholeSnapshot.Extent);
        await workspace.IntervalReady;
        Assert.Null(workspace.ScopeInterval);
        Assert.StartsWith("Coverage: a partial gap", workspace.ScopeCoverage, StringComparison.Ordinal);

        // A generation that publishes no ledger has judged nothing, which is said plainly, not as a limit.
        using var legacy = new TemporarySession();
        Publish(legacy.Store, Rows());
        using WorkspaceViewModel unjudged = Open(legacy);
        const string NoLedger = "Coverage unknown: this generation publishes no coverage ledger, so a count of none here is "
            + "not proof of inactivity";
        Assert.Equal(NoLedger, unjudged.ScopeCoverage);
        Assert.False(unjudged.ScopeCoverageLimited);
        unjudged.SelectInterval(new TimeRange(25, 35));
        await unjudged.IntervalReady;
        Assert.Equal(NoLedger, unjudged.ScopeCoverage);
    });

    [Fact(DisplayName = "R21: a scope's coverage is said mechanism by mechanism, with the fact behind any state short of covered")]
    public void AScopesCoverageIsSaidMechanismByMechanism()
    {
        // An import that collected TCP, UDP and process creations and delivered no UDP record: a file cannot show whether
        // its session recorded UDP, so UDP's quiet is unknown rather than covered.
        CoverageLedgerV1 quiet = Import(Delivered(NetworkProvider, 10, 0, 2), Delivered(ProcessProvider, 1, 4, 2));
        Assert.Equal("Coverage: covered for process lifecycle and TCP · unknown for UDP: its 1 admitted descriptor delivered "
            + "nothing, and a file cannot show whether its session recorded them · no other mechanism collected",
            WorkspaceRowBuilder.DescribeScopeCoverage(SessionCoverage.ByMechanism(quiet)));
        Assert.True(WorkspaceRowBuilder.IsCoverageShort(SessionCoverage.ByMechanism(quiet)));

        // A TCP record that could not be decoded leaves TCP alone short, and says why.
        CoverageLedgerV1 undecodable = Import(
            Delivered(NetworkProvider, 10, 0, 3) with
            {
                Admitted = 2,
                Undecodable = new Dictionary<UndecodableReason, long> { [UndecodableReason.BodyShorterThanSchema] = 1 },
            },
            Delivered(NetworkProvider, 42, 0, 1),
            Delivered(ProcessProvider, 1, 4, 2));
        Assert.Equal("Coverage: covered for process lifecycle and UDP · a partial gap, not extrapolated, for TCP: 1 of its "
            + "records could not be decoded · no other mechanism collected",
            WorkspaceRowBuilder.DescribeScopeCoverage(SessionCoverage.ByMechanism(undecodable)));

        // Past the file's readings nothing is known of any mechanism, which is said once.
        Assert.Equal("Coverage unknown: outside the readings the capture's sources delivered, so a count of none here is "
            + "not proof of inactivity",
            WorkspaceRowBuilder.DescribeScopeCoverage(SessionCoverage.ByMechanism(quiet, new TimeRange(70, 80))));

        // Every collected mechanism delivering, three are named as a sentence lists them.
        CoverageLedgerV1 delivered = Import(
            Delivered(NetworkProvider, 10, 0, 2), Delivered(NetworkProvider, 42, 0, 1), Delivered(ProcessProvider, 1, 4, 2));
        Assert.Equal("Coverage: covered for process lifecycle, TCP and UDP · no other mechanism collected",
            WorkspaceRowBuilder.DescribeScopeCoverage(SessionCoverage.ByMechanism(delivered)));
        Assert.False(WorkspaceRowBuilder.IsCoverageShort(SessionCoverage.ByMechanism(delivered)));

        // Unknown for different reasons is said for each, and a state this version never derives in its own words.
        Assert.Equal("Coverage: unknown for UDP: one reason · unknown for RPC: another",
            WorkspaceRowBuilder.DescribeScopeCoverage(
            [
                new(Mechanism.Rpc, CoverageState.UnknownCoverage, "another"),
                new(Mechanism.Udp, CoverageState.UnknownCoverage, "one reason"),
            ]));
        Assert.Equal("Coverage: reduced fidelity for TCP: sampled",
            WorkspaceRowBuilder.DescribeScopeCoverage([new(Mechanism.Tcp, CoverageState.ReducedFidelity, "sampled")]));

        // Every mechanism covered leaves nothing to add; nothing collected is short, and nothing judged says nothing.
        MechanismCoverage[] covered = [new(Mechanism.Tcp, CoverageState.Covered, "2 records"), new(Mechanism.Udp, CoverageState.Covered, "1 record")];
        Assert.Equal("Coverage: covered for TCP and UDP", WorkspaceRowBuilder.DescribeScopeCoverage(covered));
        Assert.False(WorkspaceRowBuilder.IsCoverageShort(covered));
        MechanismCoverage[] none = [new(Mechanism.Tcp, CoverageState.NotCollected, "no admitted descriptor records it")];
        Assert.Equal("Coverage: no mechanism was collected here, so a count of none here is not proof of inactivity",
            WorkspaceRowBuilder.DescribeScopeCoverage(none));
        Assert.True(WorkspaceRowBuilder.IsCoverageShort(none));
        Assert.Equal(string.Empty, WorkspaceRowBuilder.DescribeScopeCoverage([]));
        Assert.False(WorkspaceRowBuilder.IsCoverageShort([]));
    }

    [Fact(DisplayName = "R21: a publication's counts stand in with their coverage, never styled as the new generation's limit")]
    public void AStandInKeepsItsCoverageInPlainInk() => SingleThreadedContext.Run(async () =>
    {
        // A capture publishes generations without a ledger while it records, and its last with one.
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel recording = Open(session);
        recording.ShowVisibleRange(new TimeRange(8, 15));
        await recording.IntervalReady;
        const string NoLedger = "Coverage unknown: this generation publishes no coverage ledger, so a count of none here is "
            + "not proof of inactivity";
        Assert.Equal(NoLedger, recording.ScopeCoverage);
        Assert.False(recording.ScopeCoverageLimited);

        Publish(session.Store, [Timed(Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 7).Between(ClientEnd, ServerEnd))],
            coverage: new CoverageLedgerV1
            {
                Contract = CoverageLedgerV1.ContractName,
                Epochs = [Epoch(1, 0, 60, processes: 2, lost: 1)],
            });
        using WorkspaceViewModel stopped = Open(session);
        stopped.AdoptScope(recording.CarryScope());

        // Until its own counts are read, the last publication shows the previous one's, and their coverage with them, in
        // plain ink: the words are that generation's, which judged nothing, not this one's limit.
        Assert.StartsWith("Ranking within the visible ", stopped.RankingScopeText, StringComparison.Ordinal);
        Assert.Equal(NoLedger, stopped.ScopeCoverage);
        Assert.False(stopped.ScopeCoverageLimited);
        await stopped.IntervalReady;
        Assert.Equal($"Coverage: a partial gap, not extrapolated, for {Collected}: {Lost} · no other mechanism collected",
            stopped.ScopeCoverage);
        Assert.True(stopped.ScopeCoverageLimited);
    });

    [Fact(DisplayName = "R21: a range that could not be counted has no coverage said for it, and the tour states none")]
    public void AnUncountedRangeHasNoCoverageSaid() => SingleThreadedContext.Run(async () =>
    {
        // The evidence source names another session, so the range's count is refused, and the card keeps the whole
        // session's counts; no coverage is claimed for the range it could not count.
        using var session = new TemporarySession();
        Publish(session.Store, Rows(), coverage: new CoverageLedgerV1
        {
            Contract = CoverageLedgerV1.ContractName,
            Epochs = [Epoch(1, 0, 60, processes: 2, lost: 0)],
        });
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, Guid.NewGuid(), overview.Generation));
        Assert.Equal($"Coverage: covered for {Collected} · no other mechanism collected", workspace.ScopeCoverage);
        workspace.SelectInterval(new TimeRange(8, 15));
        await workspace.IntervalReady;
        Assert.StartsWith("Could not rank within", workspace.RankingScopeText, StringComparison.Ordinal);
        Assert.Equal("Coverage unknown: this range could not be counted", workspace.ScopeCoverage);
        Assert.False(workspace.ScopeCoverageLimited);

        // Shown with no source to count a range from, a brushed range has no coverage said for it either.
        using var unsourced = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity);
        Assert.Equal($"Coverage: covered for {Collected} · no other mechanism collected", unsourced.ScopeCoverage);
        unsourced.SelectInterval(new TimeRange(8, 15));
        Assert.Equal(string.Empty, unsourced.ScopeCoverage);
        Assert.False(unsourced.ShowsScopeCoverage);

        // The tour illustrates, and no capture's coverage stands behind it.
        using var tour = new WorkspaceViewModel();
        Assert.Equal(string.Empty, tour.ScopeCoverage);
        Assert.False(tour.ScopeCoverageLimited);
    });

    /// <summary>
    /// A client, PID 100, and a server, PID 200, created at 5 and 6, exchange one message at 10-11 and another at 50-51.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Timed(Lifecycle(5, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
        Timed(Lifecycle(6, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
        Timed(Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 3).Between(ClientEnd, ServerEnd)),
        Timed(Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 4).Between(ServerEnd, ClientEnd)),
        Timed(Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 5).Between(ClientEnd, ServerEnd)),
        Timed(Transfer(51, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 6).Between(ServerEnd, ClientEnd)),
    ];

    /// <summary>
    /// A live epoch between two delivered readings that collected TCP and process creations: its two TCP records, the
    /// creations it delivered, and the events its session reported lost.
    /// </summary>
    private static CoverageEpochV1 Epoch(int number, long first, long last, long processes, long lost) => new()
    {
        Epoch = number,
        Acquisition = CoverageAcquisition.LiveCapture,
        FirstDeliveredNativeTicks = first,
        LastDeliveredNativeTicks = last,
        Collected =
        [
            new CoverageCollectedV1 { ProviderId = NetworkProvider, ProviderName = "network", EventId = 10, Version = 0, Mechanism = Mechanism.Tcp },
            new CoverageCollectedV1 { ProviderId = ProcessProvider, ProviderName = "process", EventId = 1, Version = 4, Mechanism = Mechanism.ProcessLifecycle },
        ],
        Deliveries =
        [
            new CoverageDeliveryV1 { ProviderId = NetworkProvider, EventId = 10, Version = 0, Delivered = 2, Admitted = 2, Omitted = 0 },
            .. processes == 0 ? Array.Empty<CoverageDeliveryV1>() : [new CoverageDeliveryV1
            {
                ProviderId = ProcessProvider,
                EventId = 1,
                Version = 4,
                Delivered = processes,
                Admitted = processes,
                Omitted = 0,
            }],
        ],
        Losses =
        [
            new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = lost },
            new CoverageLossV1 { Layer = LossLayer.ConsumerBuffers, Lost = 0 },
            new CoverageLossV1 { Layer = LossLayer.CallbackQueue, Lost = 0 },
            new CoverageLossV1 { Layer = LossLayer.Storage, Lost = 0 },
        ],
    };

    /// <summary>An imported file's one epoch, which collected TCP, UDP and process creations and lost nothing.</summary>
    private static CoverageLedgerV1 Import(params CoverageDeliveryV1[] deliveries) => new()
    {
        Contract = CoverageLedgerV1.ContractName,
        Epochs = [new CoverageEpochV1
        {
            Epoch = 1,
            Acquisition = CoverageAcquisition.EtlImport,
            FirstDeliveredNativeTicks = 0,
            LastDeliveredNativeTicks = 60,
            Collected =
            [
                new CoverageCollectedV1 { ProviderId = NetworkProvider, ProviderName = "network", EventId = 10, Version = 0, Mechanism = Mechanism.Tcp },
                new CoverageCollectedV1 { ProviderId = NetworkProvider, ProviderName = "network", EventId = 42, Version = 0, Mechanism = Mechanism.Udp },
                new CoverageCollectedV1 { ProviderId = ProcessProvider, ProviderName = "process", EventId = 1, Version = 4, Mechanism = Mechanism.ProcessLifecycle },
            ],
            Deliveries = deliveries,
            Losses = [new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = 0 }],
        }],
    };

    private static CoverageDeliveryV1 Delivered(Guid provider, int eventId, int version, long records) => new()
    {
        ProviderId = provider,
        EventId = eventId,
        Version = version,
        Delivered = records,
        Admitted = records,
        Omitted = 0,
    };

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
