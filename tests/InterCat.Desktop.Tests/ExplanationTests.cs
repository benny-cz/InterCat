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
/// A ranked row's numbers one action from the rule behind them (§6.8): the inspector says how the selected process's
/// records were bound to it and what the evidence policy left out of its total, how an executable group was formed, and
/// how a channel's two ends were paired. A reused PID's later holder binds its records only as candidates, which the
/// default policy counts in no process, so its row, and its group's, would otherwise read as quieter than they were.
/// </summary>
public sealed class ExplanationTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";
    private const string OtherClientEnd = "127.0.0.1:50001";

    [Fact(DisplayName = "§6.8: the inspector says how a selected process's records were bound to it, and what a reused PID's later holder left out of its total")]
    public void TheInspectorExplainsHowAProcesssRecordsAreBound()
    {
        // PID 100 is held three times. Its second holder sends twice, each a candidate that could be a late record of the
        // first holder; its third makes no record but its creation. PID 200 is held once.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Timed(Lifecycle(5, ObservationKind.Create, 200, 1) with { ResourceName = @"C:\Tools\server.exe" }),
            Timed(Lifecycle(10, ObservationKind.Create, 100, 2) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 3).Between(ClientEnd, ServerEnd)),
            Timed(Lifecycle(30, ObservationKind.Exit, 100, 4)),
            Timed(Lifecycle(40, ObservationKind.Create, 100, 5) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 6).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(55, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 7).Between(ServerEnd, ClientEnd)),
            Timed(Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 8).Between(ClientEnd, ServerEnd)),
            Timed(Lifecycle(70, ObservationKind.Exit, 100, 9)),
            Timed(Lifecycle(80, ObservationKind.Create, 100, 10) with { ResourceName = @"C:\Tools\client.exe" }),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        ProcessNode Holder(int pid, int holder) =>
            workspace.Snapshot.Processes.Single(node => node.ProcessId == pid && node.PidHolder == holder);
        const string Rule = "process-binding, version 4";
        const string Coverage = " Coverage over the session: unknown.";

        // Nothing selected, nothing to explain.
        Assert.Equal(string.Empty, workspace.BindingExplanation);

        // A PID held once binds every record naming it while it ran, as does the first of a reused PID's holders.
        workspace.SelectProcess(Holder(200, 1).Id);
        Assert.Equal($"Each record naming PID 200 while it ran is its own, correlated by {Rule}: no other process held PID 200 "
            + "in this capture." + Coverage, workspace.BindingExplanation);
        workspace.SelectProcess(Holder(100, 1).Id);
        Assert.Equal($"Each record naming PID 100 while it ran is its own, correlated by {Rule}: it was the first of 3 processes "
            + "to hold PID 100 in this capture." + Coverage, workspace.BindingExplanation);

        // The second holder counts its creation and exit alone, and says how many records it left out and why.
        ProcessNode second = Holder(100, 2);
        Assert.Equal(2, second.Records);
        workspace.SelectProcess(second.Id);
        Assert.Equal("PID 100 was held by 3 processes in this capture, and this was the 2nd. A record naming it while this one "
            + $"ran could be a late record of an earlier one, so {Rule}, binds it here only as a candidate. The evidence policy "
            + "counts no candidate, so its total counts only its lifecycle records, leaving out 2 records bound to it over the "
            + "session." + Coverage, workspace.BindingExplanation);

        // Brushed to an interval that holds one of those sends, it still speaks for the session, as it says.
        workspace.SelectInterval(new TimeRange(4_500, 5_500));
        Assert.Contains("leaving out 2 records bound to it over the session.", workspace.BindingExplanation, StringComparison.Ordinal);

        // The third holder bound none: nothing is left out of its total.
        workspace.SelectProcess(Holder(100, 3).Id);
        Assert.Equal("PID 100 was held by 3 processes in this capture, and this was the 3rd. A record naming it while this one "
            + $"ran could be a late record of an earlier one, so {Rule}, binds it here only as a candidate. None is left out of "
            + "its total." + Coverage, workspace.BindingExplanation);

        // A group is not one process, and the tour's processes were bound by no rule.
        workspace.ClearSelection();
        Assert.Equal(string.Empty, workspace.BindingExplanation);
        workspace.SelectedRung = workspace.RungRows[0];
        Assert.NotNull(workspace.SelectedGroup);
        Assert.Equal(string.Empty, workspace.BindingExplanation);
        using var tour = new WorkspaceViewModel(SyntheticWorkspace.Create(), "synthetic-tour-v1");
        tour.SelectProcess(tour.Snapshot.Processes[0].Id);
        Assert.Equal(string.Empty, tour.BindingExplanation);
    }

    [Fact(DisplayName = "§6.8: a selected group says how its processes were grouped, and what its total leaves out of a reused PID's later holder")]
    public void ASelectedGroupSaysHowItWasFormed()
    {
        // client.exe's PID is reused, and its second holder's two sends are candidates; server.exe is held once; PID 700
        // names no executable, in any record.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Timed(Lifecycle(5, ObservationKind.Create, 200, 1) with { ResourceName = @"C:\Tools\server.exe" }),
            Timed(Lifecycle(10, ObservationKind.Create, 100, 2) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Lifecycle(30, ObservationKind.Exit, 100, 3)),
            Timed(Lifecycle(40, ObservationKind.Create, 100, 4) with { ResourceName = @"C:\Tools\CLIENT.EXE" }),
            Timed(Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 5).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(55, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 6).Between(ServerEnd, ClientEnd)),
            Timed(Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 7).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(70, ObservationKind.Send, AccountingSide.SendSide, 8, 700, 8).Between("127.0.0.1:50009", "127.0.0.1:9999")),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        const string Bound = " Each member's own records are bound to it by process-binding, version 4.";
        const string Coverage = " Coverage over the session: unknown.";

        // Both holders of PID 100 ran one executable, named in two cases, and the group's total leaves out the second's sends.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "client.exe");
        Assert.Equal(("How its processes are grouped", "Grouped by the executable its members' records name, compared "
            + "without case." + Bound + " The evidence policy counts no candidate, so its total leaves out 2 records bound to "
            + "1 later holder of a reused PID among them." + Coverage), (workspace.ExplanationHeading, workspace.Explanation));
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "server.exe");
        Assert.Equal("Grouped by the executable its members' records name, compared without case." + Bound + Coverage,
            workspace.Explanation);

        // The process whose records name no executable is grouped apart, under no guessed name.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "Executable not witnessed");
        Assert.Equal("Grouped here because no record names the executable its members ran: none is given a guessed name."
            + Bound + Coverage, workspace.Explanation);

        // The tour's groups illustrate, and no rule formed them.
        using var tour = new WorkspaceViewModel(SyntheticWorkspace.Create(), "synthetic-tour-v1");
        tour.SelectedRung = tour.RungRows[0];
        Assert.NotNull(tour.SelectedGroup);
        Assert.Equal(string.Empty, tour.Explanation);
    }

    [Fact(DisplayName = "§6.8: what a reused PID's later holder left out is offered as candidates, and counted so it is said and read everywhere")]
    public async Task CandidatesAreOfferedAndCounted()
    {
        // client.exe's PID is reused, and its second holder's two sends are candidates; server.exe is held once.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Timed(Lifecycle(5, ObservationKind.Create, 200, 1) with { ResourceName = @"C:\Tools\server.exe" }),
            Timed(Lifecycle(10, ObservationKind.Create, 100, 2) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Lifecycle(30, ObservationKind.Exit, 100, 3)),
            Timed(Lifecycle(40, ObservationKind.Create, 100, 4) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 5).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(55, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 6).Between(ServerEnd, ClientEnd)),
            Timed(Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 7).Between(ClientEnd, ServerEnd)),
        ]);

        // Counting correlated evidence, the later holder and its group offer the records they left out; others do not.
        SessionOverviewBundle correlated = SessionOverviewProjector.Project(session.Store);
        using (var workspace = new WorkspaceViewModel(OverviewWorkspace.From(correlated), correlated.GraphIdentity,
            new SessionEvidenceSource(session.Path, correlated.SessionId, correlated.Generation, correlated.Policy)))
        {
            Assert.Equal((EvidencePolicy.IncludeCorrelated, false, string.Empty),
                (workspace.EvidencePolicy, workspace.CountsCandidates, workspace.EvidencePolicyNote));
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "client.exe");
            Assert.True(workspace.OffersCandidates);
            workspace.SelectProcess(workspace.Snapshot.Processes.Single(node => node.PidHolder == 2).Id);
            Assert.True(workspace.OffersCandidates);
            workspace.SelectProcess(workspace.Snapshot.Processes.Single(node => node.ProcessId == 100 && node.PidHolder == 1).Id);
            Assert.False(workspace.OffersCandidates);
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "server.exe");
            Assert.False(workspace.OffersCandidates);
            Assert.DoesNotContain(workspace.DescribeExport(DateTimeOffset.UnixEpoch).Caveats,
                caveat => caveat.StartsWith("Counted with candidates", StringComparison.Ordinal));
        }

        // Counted with candidates, the later holder's total includes them and says so, as its group's does, the rail says
        // what is counted, an export says it too, and its rung's records hold its sends.
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store, EvidencePolicy.IncludeCandidates);
        var source = new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation, overview.Policy);
        using var counted = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity, source);
        Assert.True(counted.CountsCandidates);
        Assert.Equal("Counting candidates: a reused PID's later holder also counts records naming its PID that may be an "
            + "earlier holder's", counted.EvidencePolicyNote);
        Assert.Contains(counted.DescribeExport(DateTimeOffset.UnixEpoch).Caveats,
            caveat => caveat.StartsWith("Counted with candidates", StringComparison.Ordinal));
        counted.SelectedRung = counted.RungRows.Single(row => row.Label == "client.exe");
        Assert.False(counted.OffersCandidates);
        Assert.Contains(" The evidence policy counts candidates, so its total includes 2 records bound to 1 later holder of a "
            + "reused PID among them.", counted.Explanation, StringComparison.Ordinal);
        ProcessNode later = counted.Snapshot.Processes.Single(node => node.PidHolder == 2);
        Assert.Equal(3, later.Records);
        counted.SelectProcess(later.Id);
        Assert.Contains("binds it here only as a candidate. The evidence policy counts candidates, so its total includes the 2 "
            + "records bound to it over the session.", counted.Explanation, StringComparison.Ordinal);
        Assert.False(counted.OffersCandidates);
        SessionEvidencePage records = await source.ReadScopeAsync(
            new EvidenceScope("Records owned by the later holder", null, [later.Id], null, null), 100, CancellationToken.None);
        Assert.Equal(3, records.Records.Count);
    }

    [Fact(DisplayName = "§6.8: a channel chosen among a process's rows says how its ends were paired, whether the capture saw it open and close, and its key")]
    public void AChosenChannelSaysHowItWasPaired()
    {
        // The client connects to the server twice. The capture sees the first connection open and close at both ends,
        // and neither for the second.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
            Timed(Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 3).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(11, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 4).Between(ServerEnd, ClientEnd)),
            Timed(Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 5).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(13, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 6).Between(ServerEnd, ClientEnd)),
            Timed(Transfer(14, ObservationKind.Disconnect, AccountingSide.EndpointActivity, 0, 100, 7).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(15, ObservationKind.Disconnect, AccountingSide.EndpointActivity, 0, 200, 8).Between(ServerEnd, ClientEnd)),
            Timed(Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 9).Between(OtherClientEnd, ServerEnd)),
            Timed(Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 10).Between(ServerEnd, OtherClientEnd)),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        Channel whole = workspace.Snapshot.Channels.Single(channel => channel.Name.Contains("50000", StringComparison.Ordinal));
        Channel unseen = workspace.Snapshot.Channels.Single(channel => channel.Name.Contains("50001", StringComparison.Ordinal));
        Assert.Equal((true, true, false, false), (whole.OpenWitnessed, whole.CloseWitnessed, unseen.OpenWitnessed, unseen.CloseWitnessed));
        const string Paired = "Each end's records bind to one process and name the other end: paired by "
            + "transport-endpoint-relation, version 4, correlated. ";

        // The client's rung lists both channels; the one chosen is explained, with the key E lists its records by.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "client.exe");
        Assert.True(workspace.Descend());
        workspace.SelectedRung = workspace.RungRows.Single();
        Assert.True(workspace.Descend());
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == whole.Key);
        Assert.Equal(("How it was paired", Paired + "Opened and closed in the capture. Coverage over the session: unknown. "
            + $"E lists its records at both ends, by its key {whole.Key}."), (workspace.ExplanationHeading, workspace.Explanation));
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == unseen.Key);
        Assert.Equal(Paired + "Open before the capture and after it. Coverage over the session: unknown. "
            + $"E lists its records at both ends, by its key {unseen.Key}.", workspace.Explanation);

        // A pairing that rests on a candidate end says so, which the window's policy never admits; a channel no rule paired,
        // as the tour's, has nothing to explain.
        Assert.StartsWith("Each end's records bind to one process and name the other end: paired by transport-endpoint-relation, "
            + "version 4, as a candidate, since one end's records bind to their process only as candidates. ",
            WorkspaceRowBuilder.ExplainPairing(whole with { Strength = RelationStrength.Candidate }), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => WorkspaceRowBuilder.ExplainPairing(whole with { Rule = null }));
        using (var tour = new WorkspaceViewModel(SyntheticWorkspace.Create(), "synthetic-tour-v1"))
        {
            while (tour.RungRows.Count > 0 && !tour.Snapshot.Channels.Any(channel => channel.Key == tour.RungRows[0].Key))
            {
                tour.SelectedRung = tour.RungRows[0];
                Assert.True(tour.Descend());
            }

            tour.SelectedRung = tour.RungRows[0];
            Assert.NotNull(tour.DescribedRow);
            Assert.Equal(string.Empty, tour.Explanation);
        }

        // On its own rung the inspector describes the process it was opened from, and the explanation follows it.
        Assert.True(workspace.Descend());
        Assert.StartsWith("Channel", workspace.Crumbs[^1].Label, StringComparison.Ordinal);
        Assert.Equal("client.exe", workspace.SelectionTitle);
        Assert.Equal("How its records are counted", workspace.ExplanationHeading);
        Assert.StartsWith("Each record naming PID 100 while it ran is its own", workspace.Explanation, StringComparison.Ordinal);
    }

    /// <summary>Positions whose ordinal suffix differs from their last digit's, and the ones around them.</summary>
    private static readonly int[] Positions = [1, 2, 3, 4, 10, 11, 12, 13, 21, 22, 23, 101, 111, 112];

    [Fact(DisplayName = "§6.8: a position reads as a sentence says it, from 1st to 111th")]
    public void APositionReadsAsASentenceSaysIt() =>
        Assert.Equal(
            ["1st", "2nd", "3rd", "4th", "10th", "11th", "12th", "13th", "21st", "22nd", "23rd", "101st", "111th", "112th"],
            Positions.Select(Spoken.Ordinal));

    /// <summary>A row whose session time is its reading in workspace ticks.</summary>
    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
