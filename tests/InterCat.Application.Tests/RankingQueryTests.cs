using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// A ranked export names the icat metric query its rows answer (R18, I16): at the machine rung its rows are that answer's
/// groups, value for value, and its identity is the one icat metric prints for the command it names; elsewhere it says why
/// no query names them.
/// </summary>
public sealed class RankingQueryTests
{
    private static readonly DateTimeOffset Exported = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Interface = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");

    private static readonly RankingMetric[] Answered =
    [
        RankingMetric.Records, RankingMetric.BytesSent, RankingMetric.BytesReceived, RankingMetric.EndpointBytes,
        RankingMetric.ActivePeers, RankingMetric.RpcErrors, RankingMetric.RpcCallTime, RankingMetric.RpcServeTime,
    ];

    [Fact(DisplayName = "R18: the machine's rows ranked by a metric are the groups of the icat metric answer its export names, value for value")]
    public void TheMachinesRowsAreTheNamedAnswersGroups()
    {
        for (int seed = 0; seed < 12; seed++)
        {
            var random = new Random(seed);
            (List<ObservationRowV1> rows, List<SourceFieldRowV1> fields) = RandomMachine(random);
            using var session = new TemporarySession();
            Publish(session.Store, rows, rowsPerSegment: random.Next(5, 60), fields: fields);
            long end = rows.Max(row => row.NativeTicks) + 1;
            long from = random.Next(0, (int)end);
            var presentation = new TimeRange(from, from + random.Next(1, (int)end + 1));
            foreach (LaneGrouping grouping in WorkspaceGrouping.All)
            {
                foreach (RankingMetric ranking in Answered)
                {
                    foreach (TimeRange? interval in new TimeRange?[] { null, presentation })
                    {
                        string context = $"seed {seed}, {grouping}, {ranking}, interval {interval}";
                        SessionExportResult export = SessionExport.Build(session.Store,
                            new([], interval, false, ExportFormat.Json, RankBy: ranking, Grouping: grouping), Exported);

                        // The rows rank by the metric asked, or by records where nothing could rank them; either way they
                        // name the request that answers them, and it is the one asked of icat metric.
                        MetricRequest request = RankingQuery.Request(export.Context.RankedBy, grouping, interval, EvidencePolicy.IncludeCorrelated)!;
                        RankingQueryName query = export.Context.Query!;
                        Assert.True(query.Identity is not null, $"{context}: {query.Unnamed}");
                        Assert.Equal(SessionMetrics.Identify(session.Store, request)!.Token, query.Identity!.Token);
                        Assert.Equal(RankingQuery.Command(request), query.Command);

                        MetricResult answer = SessionMetrics.Evaluate(session.Store, request);
                        using JsonDocument json = JsonDocument.Parse(export.Content);
                        var keys = new HashSet<string>(StringComparer.Ordinal);
                        foreach (JsonElement row in json.RootElement.GetProperty("rows").EnumerateArray())
                        {
                            string key = row.GetProperty("key").GetString()!;
                            keys.Add(key);
                            // A process with no witnessed executable, or naming no session, is one row; the answer counts
                            // its records apart from its groups, as unattributed for that reason.
                            MetricGroup? group = answer.Groups.Concat(answer.Unattributed).SingleOrDefault(candidate => Names(candidate, key));
                            (long?, long, long) metric = group is null
                                ? (null, 0, 0)
                                : (group.Value, group.KnownContributions, group.UnknownContributions);
                            (long? Value, long Measured, long Unmeasured) shown = Value(row, export.Context.RankedBy);
                            if (group is { Kind: MetricGroupKind.Unattributed } && export.Context.RankedBy == RankingMetric.ActivePeers)
                            {
                                // The answer counts no distinct peers over records it cannot group, where the window counts
                                // the row's own: only a row of a known executable or a named session is one of its groups.
                                Assert.True((group.Value, group.KnownContributions) == (null, 0), $"{context}: {key} row {shown}, metric {metric}");
                                continue;
                            }

                            Assert.True(metric == shown, $"{context}: {key} row {shown}, metric {metric}");
                        }

                        // And every group the answer counts anything in is a row: none of them is left out of the export.
                        foreach (MetricGroup group in answer.Groups.Concat(answer.Unattributed).Where(group =>
                            group.KnownContributions + group.UnknownContributions > 0
                            && (group.Kind is MetricGroupKind.Executable or MetricGroupKind.UserSession
                                || group.Reason is ProcessBindingReason.ExecutableUnknown or ProcessBindingReason.SessionUnknown)))
                        {
                            Assert.Contains(keys, key => Names(group, key));
                        }
                    }
                }
            }
        }
    }

    [Fact(DisplayName = "I16: a ranked export names its generation's manifest and the query icat metric prints, in JSON and CSV")]
    public void ARankedExportNamesItsManifestAndQuery()
    {
        using var session = new TemporarySession();
        (List<ObservationRowV1> rows, List<SourceFieldRowV1> fields) = RandomMachine(new Random(3));
        Publish(session.Store, rows, fields: fields);
        SessionManifestV1 manifest = session.Store.Current!;
        var interval = new TimeRange(20, 400);

        SessionExportResult json = SessionExport.Build(session.Store,
            new([], interval, false, ExportFormat.Json, RankBy: RankingMetric.BytesSent), Exported);
        using (JsonDocument document = JsonDocument.Parse(json.Content))
        {
            JsonElement root = document.RootElement;
            Assert.Equal(manifest.Digest, root.GetProperty("context").GetProperty("manifest").GetString());
            JsonElement query = root.GetProperty("query");
            MetricRequest request = new()
            {
                Basis = AnalysisBasis.SourceObservations,
                Metric = Metric.BytesSent,
                ByteDomain = ByteDomain.TransportObserved,
                AccountingSide = AccountingSide.SendSide,
                Grouping = LaneGrouping.Executable,
                Interval = interval,
            };
            QueryIdentity identity = SessionMetrics.Identify(session.Store, request)!;
            Assert.Equal(identity.Token, query.GetProperty("identity").GetString());
            Assert.Equal(identity.CanonicalSpecification, query.GetProperty("canonical").GetString());
            Assert.Equal(
                "icat metric <directory> --metric bytes-sent --byte-domain transport-observed --side send-side --group-by executable --interval 20:400",
                query.GetProperty("command").GetString());
            Assert.Equal(JsonValueKind.Null, query.GetProperty("unnamed").ValueKind);
        }

        // A CSV, which carries no caveat, keeps the manifest and the identity in its last two columns of every line.
        SessionExportResult csv = SessionExport.Build(session.Store,
            new([], interval, false, ExportFormat.Csv, RankBy: RankingMetric.BytesSent), Exported);
        string[] lines = csv.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.EndsWith(",manifest,query_identity", lines[0].TrimEnd('\r'), StringComparison.Ordinal);
        Assert.All(lines.Skip(1), line => Assert.EndsWith(
            $",{manifest.Digest},{json.Context.Query!.Identity!.Token}", line.TrimEnd('\r'), StringComparison.Ordinal));

        // The other end of each RPC call: a duration's command writes its basis, interval, cohort and statistic out.
        SessionExportResult timed = SessionExport.Build(session.Store,
            new([], null, false, ExportFormat.Json, RankBy: RankingMetric.RpcServeTime, Grouping: LaneGrouping.UserSession), Exported);
        Assert.Equal(
            "icat metric <directory> --basis logical-operations --metric duration --duration server-execution "
                + "--cohort completed-in-range --statistic median --group-by session",
            timed.Context.Query!.Command);

        // A redacted report names neither: its context is pseudonymized, and a digest or an identity would name the session.
        SessionExportResult shared = SessionExport.Build(session.Store,
            new([], null, false, ExportFormat.Json, Redacted: true, RankBy: RankingMetric.BytesSent), Exported);
        Assert.DoesNotContain(manifest.Digest, shared.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(json.Context.Query!.Identity!.Hash, shared.Content, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I16: rows no one icat metric request answers name no query and say why, and an evidence export names its manifest")]
    public void RowsNoRequestAnswersSayWhy()
    {
        using var session = new TemporarySession();
        (List<ObservationRowV1> rows, List<SourceFieldRowV1> fields) = RandomMachine(new Random(5));
        Publish(session.Store, rows, fields: fields);
        string digest = session.Store.Current!.Digest;

        foreach ((RankingMetric ranking, string says) in new[]
        {
            (RankingMetric.RpcCallsMade, "no request counts either side alone"),
            (RankingMetric.RpcCallsServed, "no request counts either side alone"),
            (RankingMetric.ConnectionsMade, "icat processes --rank-by connections-made"),
            (RankingMetric.ConnectionsAccepted, "icat processes --rank-by connections-accepted"),
        })
        {
            SessionExportResult export = SessionExport.Build(session.Store, new([], null, false, ExportFormat.Json, RankBy: ranking), Exported);
            Assert.Equal(ranking, export.Context.RankedBy);
            Assert.Null(export.Context.Query!.Identity);
            Assert.Null(export.Context.Query.Command);
            Assert.Contains(says, export.Context.Query.Unnamed, StringComparison.Ordinal);
            using JsonDocument json = JsonDocument.Parse(export.Content);
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("query").GetProperty("identity").ValueKind);
            Assert.Equal(export.Context.Query.Unnamed, json.RootElement.GetProperty("query").GetProperty("unnamed").GetString());
            Assert.Equal(digest, json.RootElement.GetProperty("context").GetProperty("manifest").GetString());
        }

        // A group's rows are its processes, which no request groups alone.
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        string program = overview.Groups.First(group => group.Key != ProcessGroup.UnwitnessedExecutableKey).Key;
        SessionExportResult group = SessionExport.Build(session.Store,
            new([program], null, false, ExportFormat.Json, RankBy: RankingMetric.BytesSent), Exported);
        Assert.StartsWith("Only the machine's rows are the groups of one icat metric request; these rows are one group's processes",
            group.Context.Query!.Unnamed, StringComparison.Ordinal);

        // An interval that holds no reading of the clock names no icat metric interval.
        ExportContext empty = SessionExport.Build(session.Store,
            new([], null, false, ExportFormat.Json, RankBy: RankingMetric.BytesSent), Exported).Context with
        {
            Interval = new TimeRange(long.MaxValue - 2, long.MaxValue - 1),
        };
        Assert.Contains("No reading of the capture's clock falls in the interval",
            RankingQuery.Name(session.Store, empty, EvidencePolicy.IncludeCorrelated).Unnamed, StringComparison.Ordinal);

        // Rows read from a generation the session has since moved past are not named by the newer one's identity.
        ExportContext machine = SessionExport.Build(session.Store,
            new([], null, false, ExportFormat.Json, RankBy: RankingMetric.BytesSent), Exported).Context;
        Assert.NotNull(machine.Query!.Identity);
        Publish(session.Store, [Lifecycle(9_000, ObservationKind.Create, 900, 90_000) with { SessionRelativeTicks = 900_000 }]);
        Assert.Equal(machine.Generation + 1, session.Store.Current!.Generation);
        RankingQueryName moved = RankingQuery.Name(session.Store, machine, EvidencePolicy.IncludeCorrelated);
        Assert.Null(moved.Identity);
        Assert.Equal("The session published a newer generation than these rows were read from; icat metric answers that one now.", moved.Unnamed);
        Assert.Null(SessionMetrics.Identify(session.Store, RankingQuery.Request(RankingMetric.BytesSent, LaneGrouping.Executable, null,
            EvidencePolicy.IncludeCorrelated)!, machine.Generation!.Value));

        // A view of no session names no query, and an evidence export names the manifest of the generation it read.
        Assert.Equal("This view is of no session, so no query names its rows.",
            RankingQuery.Name(session.Store, machine with { Generation = null }, EvidencePolicy.IncludeCorrelated).Unnamed);
        SessionExportResult evidence = SessionExport.Build(session.Store, new([], null, true, ExportFormat.Csv), Exported);
        Assert.Equal(session.Store.Current.Digest, evidence.Context.Manifest);
        Assert.Null(evidence.Context.Query);
        string[] lines = evidence.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.EndsWith(",manifest,query_identity", lines[0].TrimEnd('\r'), StringComparison.Ordinal);
        Assert.All(lines.Skip(1), line => Assert.EndsWith($",{session.Store.Current.Digest},{evidence.Context.EvidenceQuery!.Identity}",
            line.TrimEnd('\r'), StringComparison.Ordinal));
    }

    [Fact(DisplayName = "§19.5: a view that sets InterCat's own processes aside names no query, since icat metric counts them")]
    public void ASetAsideViewNamesNoQuery()
    {
        using var session = new TemporarySession();
        PublishCollected(session.Store);
        SessionExportResult aside = SessionExport.Build(session.Store,
            new([], null, false, ExportFormat.Json, RankBy: RankingMetric.BytesSent, SetAsideCollectors: true), Exported);
        Assert.True(aside.Context.SetAsideProcesses > 0);
        Assert.Equal("The view sets InterCat's own processes aside, and icat metric counts them, so no query answers these rows exactly.",
            aside.Context.Query!.Unnamed);
        Assert.NotNull(SessionExport.Build(session.Store,
            new([], null, false, ExportFormat.Json, RankBy: RankingMetric.BytesSent), Exported).Context.Query!.Identity);
    }

    [Fact(DisplayName = "R18: the request a ranking names is the one icat metric is asked, its policy written out where not the default")]
    public void TheRequestIsTheOneIcatMetricIsAsked()
    {
        var interval = new TimeRange(-5, 70);
        MetricRequest peers = RankingQuery.Request(RankingMetric.ActivePeers, LaneGrouping.UserSession, interval, EvidencePolicy.IncludeCandidates)!;
        Assert.Equal((AnalysisBasis.SourceObservations, Metric.ActivePeers, LaneGrouping.UserSession, interval, EvidencePolicy.IncludeCandidates),
            (peers.Basis, peers.Metric, peers.Grouping, peers.Interval, peers.EvidencePolicy));
        Assert.Null(peers.Check());
        Assert.Equal("icat metric <directory> --metric active-peers --group-by session --interval -5:70 --evidence-policy include-candidates",
            RankingQuery.Command(peers));
        Assert.Equal("icat metric <directory> --metric endpoint-activity-bytes --byte-domain transport-observed --group-by executable",
            RankingQuery.Command(RankingQuery.Request(RankingMetric.EndpointBytes, LaneGrouping.Executable, null, EvidencePolicy.IncludeCorrelated)!));
        Assert.Equal("icat metric <directory> --basis logical-operations --metric errors --group-by executable --evidence-policy direct-only",
            RankingQuery.Command(RankingQuery.Request(RankingMetric.RpcErrors, LaneGrouping.Executable, null, EvidencePolicy.DirectOnly)!));
        Assert.Equal("icat metric <directory> --metric observations --group-by process",
            RankingQuery.Command(new MetricRequest { Basis = AnalysisBasis.SourceObservations, Metric = Metric.Observations, Grouping = LaneGrouping.InstanceOnly }));

        foreach (RankingMetric ranking in Enum.GetValues<RankingMetric>())
        {
            MetricRequest? request = RankingQuery.Request(ranking, LaneGrouping.Executable, null, EvidencePolicy.IncludeCorrelated);
            Assert.True((request is null) == (RankingQuery.Unanswered(ranking) is not null), ranking.ToString());
            Assert.True(request?.Check() is null, ranking.ToString());
        }

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RankingQuery.Request(RankingMetric.BytesSent, LaneGrouping.InstanceOnly, null, EvidencePolicy.IncludeCorrelated));
        Assert.Throws<ArgumentOutOfRangeException>(() => RankingQuery.Command(new MetricRequest
        {
            Basis = AnalysisBasis.SourceObservations, Metric = Metric.Observations, Grouping = LaneGrouping.Mechanism,
        }));
    }

    /// <summary>Whether a group of an answer is the one a machine rung's row of this key shows.</summary>
    private static bool Names(MetricGroup group, string key) => group.Kind switch
    {
        MetricGroupKind.Executable => key == "executable:" + group.Executable!.ToUpperInvariant(),
        MetricGroupKind.UserSession => key == WorkspaceGrouping.SessionKey(group.TerminalSession),
        MetricGroupKind.Unattributed => (group.Reason, key) is (ProcessBindingReason.ExecutableUnknown, ProcessGroup.UnwitnessedExecutableKey)
            or (ProcessBindingReason.SessionUnknown, WorkspaceGrouping.UnrecordedSessionKey),
        _ => false,
    };

    /// <summary>What an exported row's ranking holds, as an answer's group states it: its value, measured and unmeasured.</summary>
    private static (long?, long, long) Value(JsonElement row, RankingMetric ranking)
    {
        if (ranking == RankingMetric.Records)
        {
            long records = row.GetProperty("observations").GetInt64();
            return records == 0 ? (null, 0, 0) : (records, records, 0);
        }

        JsonElement ranked = row.GetProperty("ranked");
        return ranked.ValueKind == JsonValueKind.Null
            ? (null, 0, 0)
            : (ranked.GetProperty("value").ValueKind == JsonValueKind.Null ? null : ranked.GetProperty("value").GetInt64(),
                ranked.GetProperty("measured").GetInt64(), ranked.GetProperty("unmeasured").GetInt64());
    }

    /// <summary>
    /// A random machine: two instances of one program in different terminal sessions, a service in session 0, a process
    /// whose executable was never witnessed and one naming no session, a PID that is sometimes reused; TCP exchanges among
    /// them, some seen at one end, some unmeasured or owned by no process; and RPC calls between them, some unpaired.
    /// </summary>
    private static (List<ObservationRowV1> Rows, List<SourceFieldRowV1> Fields) RandomMachine(Random random)
    {
        (int Pid, string? Image, long? Session)[] processes =
        [
            (100, @"C:\Apps\client.exe", 1), (101, @"C:\Apps\client.exe", 2), (200, @"C:\Apps\server.exe", 1),
            (300, @"C:\Windows\svc.exe", 0), (400, null, 1), (500, @"C:\Apps\tool.exe", null),
        ];
        var rows = new List<ObservationRowV1>();
        var fields = new List<SourceFieldRowV1>();
        ulong ordinal = 1;
        foreach ((int pid, string? image, long? terminal) in processes)
        {
            ObservationRowV1 create = Lifecycle(0, ObservationKind.Create, pid, ordinal++);
            create = image is null ? create : create with { ResourceName = image };
            rows.Add(create);
            if (terminal is { } named) fields.Add(Field(create, SourceField.ProcessSessionId, named));
        }

        if (random.Next(2) == 0)
        {
            // PID 100 exits and is reused, so its later records bind as candidates the default policy leaves out.
            rows.Add(Lifecycle(300, ObservationKind.Exit, 100, ordinal++));
            rows.Add(Lifecycle(310, ObservationKind.Create, 100, ordinal++) with { ResourceName = @"C:\Apps\client.exe" });
        }

        int[] pids = [.. processes.Select(process => process.Pid)];
        long ticks = 1;
        for (int index = random.Next(10, 60); index > 0; index--)
        {
            ticks += random.Next(1, 12);
            int client = pids[random.Next(pids.Length)];
            int server = pids[random.Next(pids.Length)];
            string near = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{50_000 + random.Next(0, 9)}");
            string far = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{8_000 + (server % 5)}");
            long? size = random.Next(6) == 0 ? null : random.Next(1, 4_000);
            int? sender = random.Next(9) == 0 ? null : client;
            rows.Add(Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, size, sender, ordinal++).Between(near, far));
            if (random.Next(4) != 0)
            {
                rows.Add(Transfer(ticks + 1, ObservationKind.Receive, AccountingSide.ReceiveSide, size, server, ordinal++).Between(far, near));
            }
        }

        for (int call = random.Next(0, 25); call > 0; call--)
        {
            int caller = pids[random.Next(pids.Length)];
            int callee = pids[random.Next(pids.Length)];
            long start = random.Next(1, (int)ticks + 1);
            long took = random.Next(0, 30);
            var activity = new Guid(1_000 + call, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 2);
            long? status = random.Next(4) switch { 0 => null, 1 => 1_722, _ => 0 };
            if (random.Next(6) != 0)
            {
                rows.Add(RpcCall(start, ObservationKind.RequestStart, Direction.Outbound, caller, ordinal++, activity, Interface));
            }

            rows.Add(RpcCall(start + took, ObservationKind.RequestEnd, Direction.Outbound, caller, ordinal++, activity, status: status));
            var served = new Guid(5_000 + call, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 2);
            rows.Add(RpcCall(start + 1, ObservationKind.RequestStart, Direction.Inbound, callee, ordinal++, served, Interface));
            rows.Add(RpcCall(start + 1 + random.Next(0, Math.Max(1, (int)took)), ObservationKind.RequestEnd, Direction.Inbound, callee,
                ordinal++, served, status: status));
        }

        return ([.. rows
            .OrderBy(row => row.NativeTicks)
            .ThenBy(row => row.RawRecordOrdinal)
            .Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })], fields);
    }
}
