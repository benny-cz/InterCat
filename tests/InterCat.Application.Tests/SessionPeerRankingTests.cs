using System.Globalization;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// The ranked table's peer ranking counts what `icat metric --metric active-peers` counts for every process and every
/// executable: the distinct process instances at the other end of their records, a lower bound beside the records whose
/// other end is unresolved, and unmeasured rather than zero when none resolved (R18, metrics-v1 §6.1).
/// </summary>
public sealed class SessionPeerRankingTests
{
    [Fact(DisplayName = "R18: each process's and executable's peers are what active-peers grouped by process and by executable answers")]
    public void PeersAreTheActivePeersMetrics()
    {
        for (int seed = 0; seed < 30; seed++)
        {
            var random = new Random(seed);
            List<ObservationRowV1> rows = RandomConversations(random);
            using var session = new TemporarySession();
            Publish(session.Store, rows, rowsPerSegment: random.Next(3, 40));
            long end = rows.Max(row => row.NativeTicks) + 1;
            long from = random.Next(0, (int)end);
            var presentation = new TimeRange(from, from + random.Next(1, (int)end + 1));

            foreach (TimeRange? scope in new TimeRange?[] { null, presentation })
            {
                SessionPeerMeasures measured = SessionPeerRanking.Measure(session.Store, scope);
                string context = $"seed {seed}, interval {scope}";

                // Session nanoseconds are 100 native ticks here, so a presentation tick is a native one.
                MetricResult byProcess = SessionMetrics.Evaluate(session.Store, Peers(LaneGrouping.InstanceOnly, scope));
                Dictionary<ProcessInstanceId, MetricGroup> expected = byProcess.Groups
                    .Where(group => group.Process is not null)
                    .ToDictionary(group => group.Process!.Id);
                Assert.True(expected.Keys.ToHashSet().SetEquals(measured.ByProcess.Keys), $"{context}: the processes counted differ");
                foreach ((ProcessInstanceId instance, PeerCount count) in measured.ByProcess)
                {
                    MetricGroup group = expected[instance];
                    Assert.True(
                        (group.Value, group.KnownContributions, group.UnknownContributions) == (count.Peers, count.Known, count.Unknown),
                        $"{context}: {instance} metric ({group.Value}, {group.KnownContributions}, {group.UnknownContributions}) "
                            + $"and ranking ({count.Peers}, {count.Known}, {count.Unknown})");
                }

                if (byProcess.Unavailable == MetricUnavailableReason.None)
                {
                    Assert.True(byProcess.Value == measured.WithPeers, $"{context}: metric total {byProcess.Value}, ranking {measured.WithPeers}");
                }

                // A group is its members' records together: its peers are their union, never a sum of overlapping counts.
                MetricResult byExecutable = SessionMetrics.Evaluate(session.Store, Peers(LaneGrouping.Executable, scope));
                foreach (MetricGroup group in byExecutable.Groups.Where(group => group.Executable is not null))
                {
                    PeerCount count = measured.ByGroup["executable:" + group.Executable!.ToUpperInvariant()];
                    Assert.True(
                        (group.Value, group.KnownContributions, group.UnknownContributions) == (count.Peers, count.Known, count.Unknown),
                        $"{context}: {group.Executable} metric ({group.Value}, {group.KnownContributions}, {group.UnknownContributions}) "
                            + $"and ranking ({count.Peers}, {count.Known}, {count.Unknown})");
                }
            }
        }
    }

    [Fact(DisplayName = "§6.1: peers rank the rows by the processes at the other end, and a row whose records resolved none follows")]
    public void PeersRankTheRows()
    {
        // hub.exe talks to three clients; client.exe's two instances each talk to it; lone.exe's sends reached no one.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\hub.exe" },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\client.exe" },
            Lifecycle(3, ObservationKind.Create, 201, 3) with { ResourceName = @"C:\Tools\client.exe" },
            Lifecycle(4, ObservationKind.Create, 300, 4) with { ResourceName = @"C:\Tools\other.exe" },
            Lifecycle(5, ObservationKind.Create, 400, 5) with { ResourceName = @"C:\Tools\lone.exe" },
            .. Conversation(200, 50_000, 100, 8_080, 10, 10),
            .. Conversation(201, 50_001, 100, 8_080, 20, 20),
            .. Conversation(300, 50_002, 100, 8_080, 30, 30),
            Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 8, 400, 40).Between("127.0.0.1:50003", "127.0.0.1:7070")
                with { SessionRelativeTicks = 4_000 },
        ]);
        SessionPeerMeasures measured = SessionPeerRanking.Measure(session.Store, null);
        WorkspaceSnapshot counted = OverviewWorkspace.WithPeers(OverviewWorkspace.From(SessionOverviewProjector.Project(session.Store)), measured);
        LadderView view = LadderProjection.Project(counted, SyntheticWorkspace.Root(counted), RankingMetric.ActivePeers);

        // client.exe's two instances together have one peer, hub.exe; lone.exe resolved none and is unmeasured, not zero.
        Assert.Equal(
            [("hub.exe", (long?)3), ("client.exe", 1), ("other.exe", 1), ("lone.exe", null)],
            view.Rows.Select(row => (row.Label, row.Ranked!.Value)));
        Assert.Equal((0L, 1L), (view.Rows[^1].Ranked!.Measured, view.Rows[^1].Ranked!.Unmeasured));
        Assert.Equal(4, measured.WithPeers);
    }

    private static MetricRequest Peers(LaneGrouping grouping, TimeRange? scope) => new()
    {
        Basis = AnalysisBasis.SourceObservations,
        Metric = Metric.ActivePeers,
        Grouping = grouping,
        Interval = scope,
    };

    /// <summary>A send from one end and its receive at the other: one exchange of a paired TCP connection.</summary>
    private static ObservationRowV1[] Conversation(int client, int clientPort, int server, int serverPort, long ticks, ulong ordinal)
    {
        string near = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{clientPort}");
        string far = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{serverPort}");
        return
        [
            Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 64, client, (ordinal * 2) + 100).Between(near, far)
                with { SessionRelativeTicks = ticks * 100 },
            Transfer(ticks + 1, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, server, (ordinal * 2) + 101).Between(far, near)
                with { SessionRelativeTicks = (ticks + 1) * 100 },
        ];
    }

    /// <summary>
    /// Random conversations among five processes, two of them instances of one executable: most paired at both ends, some
    /// seen at one end only, some naming no owner, and a process that talks to itself.
    /// </summary>
    private static List<ObservationRowV1> RandomConversations(Random random)
    {
        int[] pids = [100, 101, 200, 300, 400];
        string[] images = [@"C:\Tools\app.exe", @"C:\Tools\app.exe", @"C:\Tools\db.exe", @"C:\Tools\cache.exe", @"C:\Tools\web.exe"];
        var rows = new List<ObservationRowV1>();
        ulong ordinal = 1;
        for (int index = 0; index < pids.Length; index++)
        {
            rows.Add(Lifecycle(0, ObservationKind.Create, pids[index], ordinal++) with { ResourceName = images[index] });
        }

        long ticks = 1;
        int count = random.Next(3, 40);
        for (int index = 0; index < count; index++)
        {
            ticks += random.Next(1, 5);
            int client = pids[random.Next(pids.Length)];
            int server = random.Next(8) == 0 ? client : pids[random.Next(pids.Length)];
            string near = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{50_000 + random.Next(0, 12)}");
            string far = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{8_000 + (server % 7)}");
            int? sender = random.Next(10) == 0 ? null : client;
            rows.Add(Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 32, sender, ordinal++).Between(near, far)
                with { SessionRelativeTicks = ticks * 100 });
            if (random.Next(5) != 0)
            {
                // The other end's record, unless the capture saw only one side.
                rows.Add(Transfer(ticks + 1, ObservationKind.Receive, AccountingSide.ReceiveSide, 32, server, ordinal++).Between(far, near)
                    with { SessionRelativeTicks = (ticks + 1) * 100 });
                ticks++;
            }
        }

        return rows;
    }
}
