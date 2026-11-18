using System.Globalization;
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
/// §6.6's encoding channels walked in one session (R14): each meaning drawn by hue, intensity, border, hatch or outline
/// keeps its redundant channel, which is never a colour - a mechanism its glyph and its name, a magnitude its number,
/// evidence quality its words, a coverage defect and an unmeasured value their words, a direction its mark and its words,
/// and a selection its stated name.
/// </summary>
public sealed class EncodingChannelTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [Fact(DisplayName = "R14: every §6.6 meaning keeps its redundant channel in one session - glyph and name, number, quality words, coverage and unmeasured words, direction mark and words, stated selection")]
    public void EveryMeaningKeepsItsRedundantChannel() => SingleThreadedContext.Run(async () =>
    {
        // A client that opened its connection to a server and sent it sized bytes; a blind process whose sends recorded
        // no size; and a capture that lost records at its source, so its coverage falls short.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
            Timed(Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\blind.exe" }),
            Timed(Transfer(5, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 5).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(6, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 6).Between(ServerEnd, ClientEnd)),
            Timed(Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 500, 100, 10).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 500, 200, 11).Between(ServerEnd, ClientEnd)),
            Timed(Transfer(12, ObservationKind.Send, AccountingSide.SendSide, null, 300, 12).Between("127.0.0.1:51000", ServerEnd)),
            Timed(Transfer(13, ObservationKind.Receive, AccountingSide.ReceiveSide, null, 200, 13).Between(ServerEnd, "127.0.0.1:51000")),
        ],
            coverage: TransportLedger(tcp: true, udp: false, lost: 3));
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 200);
        await workspace.TimelineDetailReady;
        CultureInfo culture = CultureInfo.CurrentCulture;

        // Mechanism: each family's hue is keyed by a glyph of its own and its name.
        Assert.NotEmpty(workspace.Legend);
        Assert.All(workspace.Legend, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Glyph) || string.IsNullOrWhiteSpace(entry.Label)));
        Assert.Equal(workspace.Legend.Count, workspace.Legend.Select(entry => entry.Glyph).Distinct().Count());

        // Magnitude: a bar's height is its number in the hover card - a TCP lane's bar, whose card names its mechanism in
        // words too - and a ranked row's bar is its number in the table.
        MechanismTimelineLane tcp = workspace.Snapshot.MechanismLanes.Single(lane => lane.Mechanism == Mechanism.Tcp);
        TimelineBucket busiest = tcp.Buckets.MaxBy(bucket => bucket.ObservationCount)!;
        Assert.True(busiest.ObservationCount > 0);
        HoverCard card = workspace.DescribeTimelineHover(busiest, 1_000, lane: Mechanism.Tcp);
        Assert.Contains(string.Create(culture, $"{busiest.ObservationCount:N0} "), card.Lines[0], StringComparison.Ordinal);
        Assert.Contains("TCP", card.Title + " " + string.Join(" ", card.Lines), StringComparison.Ordinal);
        Assert.All(workspace.RungRows, row => Assert.True(
            long.TryParse(row.Observations, NumberStyles.AllowThousands, culture, out _), $"{row.Label} ranks {row.Observations}"));

        // Evidence quality: a relationship's border is its strength in words, and the words' tooltip says why.
        Assert.NotEmpty(workspace.Relationships);
        Assert.All(workspace.Relationships, row => Assert.False(
            string.IsNullOrWhiteSpace(row.Evidence) || string.IsNullOrWhiteSpace(row.Explanation), row.Name));

        // Coverage defect: the hatch is the coverage said in words, short where the ledger counted a loss.
        Assert.True(workspace.ScopeCoverageLimited);
        Assert.False(string.IsNullOrWhiteSpace(workspace.ScopeCoverage));
        Assert.Contains(card.Lines, line => line.StartsWith("Coverage: ", StringComparison.Ordinal));

        // Direction: the client opened its connection, which its relationship's mark, the mark's tooltip and its spoken
        // name each say; one whose opening was not seen has no mark.
        RelationshipRow opened = workspace.Relationships.Single(row => row.Source.Contains("client", StringComparison.Ordinal)
            || row.Target.Contains("client", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(opened.Direction));
        Assert.Contains(opened.Direction, opened.Name, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(opened.DirectionLine));
        Assert.False(string.IsNullOrWhiteSpace(opened.SpokenDirection));

        // Unmeasured value: under a byte ranking the blind process's bytes are said to be unmeasured, and the graph keys the
        // unmeasured band, never a zero.
        workspace.RankBy = RankingMetric.BytesSent;
        await workspace.RankingReady;
        Assert.True(workspace.GraphDrawsUnmeasured);
        RelationshipRow blind = workspace.Relationships.Single(row => row.Source.Contains("blind", StringComparison.Ordinal)
            || row.Target.Contains("blind", StringComparison.Ordinal));
        Assert.Equal("no size measured", blind.KnownBytes);

        // Selection: the outline and locator are the selection stated in the header.
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        workspace.SelectedProcess = client;
        Assert.Equal(client.Name, workspace.SelectionTitle);
    });

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
