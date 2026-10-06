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
/// A process's count one action from the rule behind it (§6.8): the inspector says how the selected process's records
/// were bound to it, how strongly, and what the evidence policy left out of its total. A reused PID's later holder binds
/// its records only as candidates, which the default policy counts in no process, so its row would otherwise read as a
/// quiet process.
/// </summary>
public sealed class BindingExplanationTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

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
