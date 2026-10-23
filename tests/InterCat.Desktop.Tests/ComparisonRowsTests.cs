using System.Globalization;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// The window's comparison rows (`contracts/comparison-v1.md` §8): each row in `icat compare`'s words, and each reason a
/// row's values cannot be compared said once beneath the rows, every row naming it by its number.
/// </summary>
public sealed class ComparisonRowsTests
{
    private const long Second = 10_000_000;
    private static readonly ProcessInstanceId Client = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));
    private static readonly ProcessInstanceId Server = new(Guid.Parse("00000000-0000-0000-0000-000000000002"));
    private static readonly ProcessInstanceId Service = new(Guid.Parse("00000000-0000-0000-0000-000000000003"));
    private static readonly MechanismCoverage Covered = new(Mechanism.Tcp, CoverageState.Covered, "covered");

    [Fact]
    public void EachRowReadsAsTheCommandLineStatesItAndEachReasonIsNumberedOnce()
    {
        CultureInfo culture = CultureInfo.InvariantCulture;
        CommunicationEdge connected = Edge("tcp:1:2", Client, Server) with { SourceConnected = 1, AcceptedPorts = [8080] };
        CommunicationEdge unopened = Edge("udp:1:3", Client, Service) with { Mechanism = Mechanism.Udp };
        var link = new HostLink(Service, Mechanism.Tcp, [new HostAddress("10.0.0.5", 1, 4) { Accepted = 1, AcceptedPorts = [3389] }],
            RelationStrength.Direct);
        IntervalComparison comparison = new(Guid.NewGuid(), 3, ComparisonMetric.Records, EvidencePolicy.IncludeCorrelated,
            new TimeRange(0, 10 * Second), new TimeRange(10 * Second, 20 * Second), [Covered], [Covered],
            [
                new ComparedRelationship(Measure(10), Measure(20), ComparisonChange.Increased, null) { Relationship = connected },
                new ComparedRelationship(Measure(4), Measure(2), ComparisonChange.NotComparable, "A.") { Relationship = unopened },
                new ComparedRelationship(Measure(0), Measure(4), ComparisonChange.NotComparable, "B.")
                {
                    Host = new OtherHostReach(link, link.Addresses[0]),
                },
                new ComparedRelationship(Measure(1), Measure(1), ComparisonChange.NotComparable, "A.") { Relationship = connected },
                new ComparedRelationship(Measure(20), Measure(10), ComparisonChange.Decreased, null)
                {
                    Relationship = connected with { TargetConnected = 2 },
                },
            ])
        {
            Processes = new Dictionary<ProcessInstanceId, ProcessNode>
            {
                [Client] = Node(Client, 100, "client.exe"),
                [Server] = Node(Server, 200, "server.exe"),
                [Service] = Node(Service, 300, "svchost.exe"),
            },
        };

        (IReadOnlyList<ComparisonRow> rows, IReadOnlyList<string> reasons) = ComparisonRows.Of(comparison, culture);
        Assert.Equal(["A.", "B."], reasons);
        Assert.Equal(
            [
                "client.exe · PID 100 → server.exe · PID 200 | TCP at 8080 | 10 · 1.00/s | 20 · 2.00/s | up 1.00/s (+100%)",
                "client.exe · PID 100 ↔ svchost.exe · PID 300 | UDP | 4 · 0.40/s | 2 · 0.20/s | not comparable [1]",
                "10.0.0.5 → svchost.exe · PID 300 | TCP at 3389 | 0 | 4 · 0.40/s | not comparable [2]",
                "client.exe · PID 100 → server.exe · PID 200 | TCP at 8080 | 1 · 0.10/s | 1 · 0.10/s | not comparable [1]",
                "client.exe · PID 100 ⇄ server.exe · PID 200 | TCP at 8080 | 20 · 2.00/s | 10 · 1.00/s | down 1.00/s (-50%)",
            ],
            rows.Select(row => $"{row.Ends} | {row.Over} | {row.BaselineValue} | {row.ComparedValue} | {row.ChangeCell}"));
        Assert.Equal((null, (string?)null), (rows[0].ReasonNumber, rows[0].Reason));
        Assert.Equal("10.0.0.5 → svchost.exe · PID 300 over TCP at 3389: 0 in the baseline, 4 · 0.40/s in the compared interval; "
            + "not comparable: B.", rows[2].AccessibleName);

        // By bytes, a lower bound is marked, and said in words to a screen reader.
        IntervalComparison bytes = comparison with
        {
            Metric = ComparisonMetric.BytesSent,
            Relationships =
            [
                new ComparedRelationship(new(2, 100, 1, 0, Covered), new(4, 400, 1, 1, Covered), ComparisonChange.Increased, null)
                {
                    Relationship = connected,
                    FromLowerBound = true,
                },
            ],
        };
        ComparisonRow bound = Assert.Single(ComparisonRows.Of(bytes, culture).Rows);
        Assert.Equal(("≥400 B · ≥40 B/s", "up at least 30 B/s"), (bound.ComparedValue, bound.ChangeCell));
        Assert.Contains("at least 400 B · at least 40 B/s in the compared interval", bound.AccessibleName, StringComparison.Ordinal);

        // A row whose reason was not given is said not to be of one measurement, never left without one.
        IntervalComparison unexplained = comparison with
        {
            Relationships = [new ComparedRelationship(Measure(1), Measure(2), ComparisonChange.NotComparable, null) { Relationship = connected }],
        };
        Assert.Equal(["Its two values are not of the same measurement."], ComparisonRows.Of(unexplained, culture).Reasons);
    }

    [Fact(DisplayName = "R22: a comparison's row by executable opens the executable's rung where the window has one, and says why not")]
    public void AnExecutablesRowOpensItsRungWhereTheWindowHasOne() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = TerminalSessions();
        Publish(session.Store, rows, fields: fields);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var source = new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity, source);
        await workspace.LayoutReady;
        string client = "executable:" + @"C:\Tools\client.exe".ToUpperInvariant();
        string elsewhere = "executable:" + @"C:\Tools\worker.exe".ToUpperInvariant();
        ComparedRelationship Reach(string executable) => new(Measure(1), Measure(1), ComparisonChange.Unchanged, null)
        {
            ExecutableHost = new ExecutableReach($"executable-hosts:tcp:{executable}@10.0.0.5", executable, Mechanism.Tcp, "10.0.0.5"),
        };
        ComparedRelationship Pair(string first, string second) => new(Measure(1), Measure(1), ComparisonChange.Unchanged, null)
        {
            Executables = new ExecutableRelationship($"executables:tcp:{first}|{second}", first, second, Mechanism.Tcp),
        };

        // An executable a process of this session ran is shown, and a pair either of whose executables one did; one no
        // process ran - another capture's - is not, and says why. A process's row is always shown.
        const string NoneRan = "No process of the session shown ran this executable.";
        Assert.Equal(((string?)null, NoneRan, (string?)null, NoneRan),
            (workspace.WhyNotShown(Reach(client)), workspace.WhyNotShown(Reach(elsewhere)), workspace.WhyNotShown(Pair(elsewhere, client)),
                workspace.WhyNotShown(Pair(elsewhere, elsewhere))));
        Assert.Null(workspace.WhyNotShown(new ComparedRelationship(Measure(1), Measure(1), ComparisonChange.Unchanged, null)
        {
            Relationship = overview.Edges[0],
        }));

        // A host's row opens its executable's rung, and a pair the executable that ran here, client.exe, its processes listed;
        // one no process ran opens nothing.
        Assert.False(workspace.OpenCompared(Reach(elsewhere)));
        Assert.True(workspace.OpenCompared(Reach(client)));
        Assert.True(workspace.OpenCompared(Pair(elsewhere, client)));
        Assert.Contains(workspace.Crumbs, crumb => crumb.Label.Contains("client.exe", StringComparison.Ordinal));

        // Grouped by terminal session, the window has no executable's rung to open, and says how to get one.
        using var bySession = new WorkspaceViewModel(WorkspaceGrouping.Regroup(OverviewWorkspace.From(overview), LaneGrouping.UserSession),
            WorkspaceGrouping.Identity(overview.GraphIdentity, LaneGrouping.UserSession), source);
        await bySession.LayoutReady;
        Assert.StartsWith("The window groups processes by terminal session", bySession.WhyNotShown(Reach(client)), StringComparison.Ordinal);
        Assert.False(bySession.OpenCompared(Reach(client)));
    });

    private static ComparedMeasure Measure(long records) => new(records, records, records, 0, Covered);

    private static CommunicationEdge Edge(string key, ProcessInstanceId source, ProcessInstanceId target) =>
        new(key, source, target, Mechanism.Tcp, 1, null, RelationStrength.Direct)
        {
            Rule = RelationRule.TransportEndpoint,
            Evidence = [key],
        };

    private static ProcessNode Node(ProcessInstanceId id, int pid, string name) =>
        new(id, pid, name, "test", name, 0.5, 0.5, CoverageState.Covered);
}
