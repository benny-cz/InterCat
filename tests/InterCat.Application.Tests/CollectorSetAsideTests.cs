using System.Text.Json;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// §19.5's view filter: InterCat's own processes set aside from a snapshot's rows, groups, relationships and channels, and
/// counted beside them, with no record removed; an export of the view says what it set aside, as `icat export
/// --set-aside-collectors` does (R18), and the totals a ranking states over the rows shown leave them out.
/// </summary>
public sealed class CollectorSetAsideTests
{
    [Fact(DisplayName = "§19.5: InterCat's own processes set aside take their relationships, channels, operations and records' marks with them, and a group left with none")]
    public void SettingAsideTakesWhatTheyAreAnEndOf()
    {
        WorkspaceSnapshot tour = SyntheticWorkspace.Create();
        WorkspaceSnapshot whole = tour with
        {
            Processes = [.. tour.Processes.Select(process => process.Name switch
            {
                "Indexer" => process with { Collector = CollectorRole.Client },
                "Service broker" => process with { Collector = CollectorRole.Broker },
                _ => process,
            })],
        };

        WorkspaceSnapshot aside = WorkspaceCollectors.SetAside(whole);

        Assert.Equal(["Indexer", "Service broker"], aside.SetAside.Select(process => process.Name));
        Assert.Equal(["Browser", "API host", "Cache"], aside.Processes.Select(process => process.Name));
        // The platform group held only the two, so it goes; the application host's keeps its three.
        Assert.Equal(["group.app"], aside.Groups.Select(group => group.Key));
        // Every relationship, channel, operation and mark with an end among them goes; the rest stay as they were.
        Assert.Equal(["edge.browser-api", "edge.api-cache"], aside.Edges.Select(edge => edge.Key));
        Assert.Equal(["chan.https", "chan.health", "chan.cachepipe"], aside.Channels.Select(channel => channel.Key));
        Assert.Equal(["op.https.1", "op.https.2", "op.https.3", "op.https.4", "op.health.1", "op.health.2"],
            aside.Operations.Select(operation => operation.Key));
        Assert.Equal(["ev.1", "ev.2", "ev.3", "ev.4", "ev.5", "ev.6", "ev.7"], aside.Evidence.Select(mark => mark.Key));
        Assert.Same(whole.Timeline, aside.Timeline);
        Assert.Equal(whole.Extent, aside.Extent);

        // A snapshot holding none of InterCat's own is itself, and sets nothing aside.
        Assert.Same(tour, WorkspaceCollectors.SetAside(tour));
        Assert.Empty(tour.SetAside);
        Assert.Equal(["Indexer", "Service broker"],
            aside.Processes.Concat(aside.SetAside).Where(process => WorkspaceCollectors.Instances(aside).Contains(process.Id))
                .Select(process => process.Name));
        Assert.Empty(WorkspaceCollectors.Instances(whole));
        Assert.Equal(("graph|collectors:aside", "graph"),
            (WorkspaceCollectors.Identity("graph", setAside: true), WorkspaceCollectors.Identity("graph", setAside: false)));
    }

    [Fact(DisplayName = "§19.5: the broker set aside leaves the rows, its connection to its client with it, and the session's counts stay whole")]
    public void TheBrokerSetAsideLeavesTheRows()
    {
        using var session = new TemporarySession();
        PublishCollected(session.Store);
        WorkspaceSnapshot whole = OverviewWorkspace.From(SessionOverviewProjector.Project(session.Store));
        ProcessNode broker = whole.Processes.Single(process => process.Collector == CollectorRole.Broker);

        WorkspaceSnapshot aside = WorkspaceCollectors.SetAside(whole);

        // Only the broker is InterCat's own: the client's creation time was not read, and tool.exe only took its PID later.
        Assert.Equal(broker, Assert.Single(aside.SetAside));
        Assert.Equal(["InterCat.exe · PID 7008", "tool.exe · PID 4120 #2"], aside.Processes.Select(process => process.NameWithPid));
        Assert.Equal(["InterCat.exe", "tool.exe"], aside.Groups.Select(group => group.Name));
        Assert.Single(whole.Edges);
        Assert.Empty(aside.Edges);
        Assert.Empty(aside.Channels);
        Assert.Equal(3, broker.Records);
        Assert.Equal(whole.RowsNoProcessHolds, aside.RowsNoProcessHolds);
        Assert.Same(whole.Timeline, aside.Timeline);

        // A time scope counts the rows it shows and keeps what was set aside, counted over the whole session.
        SessionIntervalCounts counts = SessionIntervalQuery.Count(session.Store, whole.Extent);
        WorkspaceSnapshot scoped = OverviewWorkspace.WithinInterval(aside, counts);
        Assert.Equal(aside.SetAside, scoped.SetAside);
        Assert.Equal(whole.RowsNoProcessHolds, scoped.RowsNoProcessHolds);
    }

    [Fact(DisplayName = "§19.5: an export of a view with InterCat's own set aside counts them beside its rows, in JSON, CSV and the redacted report")]
    public void AnExportCountsWhatItSetAside()
    {
        using var session = new TemporarySession();
        PublishCollected(session.Store);
        const string Caveat = "InterCat's own processes are set aside from these rows: 1 process, with 3 records over the whole "
            + "session. No record is removed: the evidence still lists theirs.";
        Assert.Equal(Caveat, CollectorText.SetAsideCaveat(1, 3));
        Assert.Equal("InterCat's own processes are set aside from these rows: 2 processes, with 1,234 records over the whole "
            + "session. No record is removed: the evidence still lists theirs.", CollectorText.SetAsideCaveat(2, 1_234));

        SessionExportResult shown = SessionExport.Build(session.Store, new([], null, false, ExportFormat.Json),
            DateTimeOffset.UnixEpoch);
        SessionExportResult aside = SessionExport.Build(session.Store,
            new([], null, false, ExportFormat.Json, SetAsideCollectors: true), DateTimeOffset.UnixEpoch);

        // Shown, the broker's group is a row and nothing is said; set aside, it is not, and the export counts it.
        Assert.Equal((3, 0, 0L), (shown.Rows, shown.Context.SetAsideProcesses, shown.Context.SetAsideRecords));
        Assert.DoesNotContain(Caveat, shown.Context.Caveats);
        Assert.Equal((2, 1, 3L), (aside.Rows, aside.Context.SetAsideProcesses, aside.Context.SetAsideRecords));
        Assert.Contains(Caveat, aside.Context.Caveats);
        using (JsonDocument json = JsonDocument.Parse(aside.Content))
        {
            JsonElement root = json.RootElement;
            Assert.Equal((1, 3L), (root.GetProperty("setAside").GetProperty("processes").GetInt32(),
                root.GetProperty("setAside").GetProperty("records").GetInt64()));
            Assert.DoesNotContain(root.GetProperty("rows").EnumerateArray(),
                row => row.GetProperty("label").GetString() == "intercat-broker.exe");
            Assert.Contains(root.GetProperty("context").GetProperty("caveats").EnumerateArray(),
                caveat => caveat.GetString() == Caveat);
        }

        using (JsonDocument json = JsonDocument.Parse(shown.Content))
        {
            Assert.Equal(0, json.RootElement.GetProperty("setAside").GetProperty("processes").GetInt32());
        }

        // A CSV, which carries no caveat, ends each line with the counts.
        string[] csv = SessionExport.Build(session.Store,
            new([], null, false, ExportFormat.Csv, SetAsideCollectors: true), DateTimeOffset.UnixEpoch).Content.Split('\n',
            StringSplitOptions.RemoveEmptyEntries);
        Assert.EndsWith(",row_present,scope_coverage,set_aside_processes,set_aside_records", csv[0].TrimEnd('\r'), StringComparison.Ordinal);
        Assert.Equal(3, csv.Length);
        Assert.All(csv.Skip(1), line => Assert.EndsWith(",1,3", line.TrimEnd('\r'), StringComparison.Ordinal));

        // The redacted report carries the two counts, and nothing that names the broker.
        SessionExportResult redacted = SessionExport.Build(session.Store,
            new([], null, false, ExportFormat.Json, Redacted: true, SetAsideCollectors: true), DateTimeOffset.UnixEpoch);
        using (JsonDocument json = JsonDocument.Parse(redacted.Content))
        {
            Assert.Equal((1, 3L), (json.RootElement.GetProperty("setAside").GetProperty("processes").GetInt32(),
                json.RootElement.GetProperty("setAside").GetProperty("records").GetInt64()));
        }

        Assert.DoesNotContain("broker", redacted.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4120", redacted.Content, StringComparison.Ordinal);
        string[] shared = SessionExport.Build(session.Store,
            new([], null, false, ExportFormat.Csv, Redacted: true, SetAsideCollectors: true), DateTimeOffset.UnixEpoch).Content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.EndsWith(",scope_coverage,set_aside_processes,set_aside_records", shared[0].TrimEnd('\r'), StringComparison.Ordinal);
        Assert.All(shared.Skip(1), line => Assert.EndsWith(",1,3", line.TrimEnd('\r'), StringComparison.Ordinal));

        // Evidence is never set aside: an evidence export lists every record, InterCat's own included.
        SessionExportResult evidence = SessionExport.Build(session.Store,
            new([], null, true, ExportFormat.Json, SetAsideCollectors: true), DateTimeOffset.UnixEpoch);
        Assert.Equal((6, 0), (evidence.Rows, evidence.Context.SetAsideProcesses));

        // A capture naming no collector has nothing to set aside, and its export says nothing of it.
        using var unnamed = new TemporarySession();
        PublishCollected(unnamed.Store, named: false);
        SessionExportResult none = SessionExport.Build(unnamed.Store,
            new([], null, false, ExportFormat.Json, SetAsideCollectors: true), DateTimeOffset.UnixEpoch);
        Assert.Equal((3, 0), (none.Rows, none.Context.SetAsideProcesses));
        Assert.DoesNotContain(none.Context.Caveats, caveat => caveat.StartsWith("InterCat's own", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "§19.5: a group whose member is set aside as InterCat's own ranks without it: an export's call times and peers leave it out")]
    public void AGroupRanksWithoutItsMemberSetAside()
    {
        // Two caller.exe instances: PID 101 is the window that asked for the capture, created when the capture says; PID 100
        // is another. 100 made calls of 1, 1 and 9 ticks and 101 calls of 5 and 7, and 100 sent to 101 over loopback.
        DateTimeOffset created = CollectorBrokerCreated;
        ObservationRowV1 other = Lifecycle(1, ObservationKind.Create, 100, 1) with
        {
            ResourceName = @"C:\Tools\caller.exe", SessionRelativeTicks = 100,
        };
        ObservationRowV1 window = Lifecycle(2, ObservationKind.Create, 101, 2) with
        {
            ResourceName = @"C:\Tools\caller.exe", SessionRelativeTicks = 200,
        };
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            other, window,
            .. Call(100, 10, 11, 1), .. Call(100, 20, 21, 2), .. Call(100, 30, 39, 3),
            .. Call(101, 40, 45, 4), .. Call(101, 50, 57, 5),
            Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 20)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 6_000 },
            Transfer(61, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 101, 21)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 6_100 },
        ], fields: [Field(window, SourceField.ProcessCreateTime, created.ToFileTime())],
            collectors: new CollectorIdentitiesV1
            {
                Contract = CollectorIdentitiesV1.ContractName,
                CaptureId = Capture.Value,
                Processes = [new() { Role = CollectorRole.Client, ProcessId = 101, CreatedUtc = created }],
            });

        // Shown, caller.exe's row is both instances': all five calls' median, and each the other's peer. Set aside, it is the
        // other's alone: its three calls' median, and the window, its peer.
        Assert.Equal((500L, 5L), Ranked(RankingMetric.RpcCallTime, aside: false));
        Assert.Equal((100L, 3L), Ranked(RankingMetric.RpcCallTime, aside: true));
        Assert.Equal(2L, Ranked(RankingMetric.ActivePeers, aside: false).Value);
        Assert.Equal(1L, Ranked(RankingMetric.ActivePeers, aside: true).Value);

        (long Value, long Measured) Ranked(RankingMetric metric, bool aside)
        {
            SessionExportResult export = SessionExport.Build(session.Store,
                new([], null, false, ExportFormat.Json, RankBy: metric, SetAsideCollectors: aside), DateTimeOffset.UnixEpoch);
            using JsonDocument json = JsonDocument.Parse(export.Content);
            JsonElement ranked = Assert.Single(json.RootElement.GetProperty("rows").EnumerateArray()).GetProperty("ranked");
            return (ranked.GetProperty("value").GetInt64(), ranked.GetProperty("measured").GetInt64());
        }

        static ObservationRowV1[] Call(int pid, long start, long stop, int number) =>
        [
            RpcCall(start, ObservationKind.RequestStart, Direction.Outbound, pid, (ulong)(100 + (2 * number)), Activity(number),
                ServiceControl),
            RpcCall(stop, ObservationKind.RequestEnd, Direction.Outbound, pid, (ulong)(101 + (2 * number)), Activity(number),
                status: 0),
        ];
    }

    private static readonly Guid ServiceControl = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");

    private static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 3);

    [Fact(DisplayName = "§19.5: a peer ranking's groups and its processes with a peer leave out the instances a view sets aside, which keep their own")]
    public void PeersLeaveOutWhatIsSetAside()
    {
        using var session = new TemporarySession();
        PublishCollected(session.Store);
        WorkspaceSnapshot whole = OverviewWorkspace.From(SessionOverviewProjector.Project(session.Store));
        ProcessNode broker = whole.Processes.Single(process => process.Collector == CollectorRole.Broker);
        const string BrokerGroup = @"executable:C:\PROGRAM FILES\INTERCAT\INTERCAT-BROKER.EXE";
        const string ClientGroup = @"executable:C:\PROGRAM FILES\INTERCAT\INTERCAT.EXE";

        SessionPeerMeasures all = SessionPeerRanking.Measure(session.Store, null);
        SessionPeerMeasures aside = SessionPeerRanking.Measure(session.Store, null, setAside: new HashSet<ProcessInstanceId> { broker.Id });

        // Each keeps its own peers, the broker too.
        Assert.Equal(all.ByProcess, aside.ByProcess);
        Assert.Equal(new PeerCount(1, 2, 0), aside.ByProcess[broker.Id]);
        // Only the client is among the rows shown with a peer, though the broker is still the peer of its client's group.
        Assert.Equal((2L, 1L), (all.WithPeers, aside.WithPeers));
        Assert.True(all.ByGroup.ContainsKey(BrokerGroup));
        Assert.False(aside.ByGroup.ContainsKey(BrokerGroup));
        Assert.Equal(new PeerCount(1, 2, 0), aside.ByGroup[ClientGroup]);
        // In the one terminal-session group both were members of, each was the other's peer; now only the broker is one.
        Assert.Equal((2L, 1L), (all.ByGroup["session:unknown"].Peers!.Value, aside.ByGroup["session:unknown"].Peers!.Value));
        Assert.DoesNotContain(aside.ByGroup.Keys, key => key.StartsWith("collectors", StringComparison.Ordinal));

        // The export ranked by peers reads them so too.
        SessionExportResult export = SessionExport.Build(session.Store,
            new([], null, false, ExportFormat.Json, RankBy: RankingMetric.ActivePeers, SetAsideCollectors: true),
            DateTimeOffset.UnixEpoch);
        Assert.Equal((RankingMetric.ActivePeers, 2, 1), (export.Context.RankedBy, export.Rows, export.Context.SetAsideProcesses));
    }
}
