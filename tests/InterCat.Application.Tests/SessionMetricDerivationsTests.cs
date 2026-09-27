using System.Globalization;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// `icat metric` answers from the derivation a finished session's checkpoint holds instead of deriving its processes,
/// relations and calls from every segment again; the answer must be the same one, and a derivation of another generation
/// must never be used (§12.1 S1, I12).
/// </summary>
public sealed class SessionMetricDerivationsTests
{
    private static readonly Guid ServiceControl = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");

    [Fact(DisplayName = "§12: a metric answered from a checkpoint's derivation is the one derived from every segment")]
    public void CheckpointedAnswersAreDerivedAnswers()
    {
        for (int seed = 0; seed < 12; seed++)
        {
            var random = new Random(seed);
            List<ObservationRowV1> rows = RandomTraffic(random);
            using var session = new TemporarySession();
            Publish(session.Store, rows, rowsPerSegment: random.Next(3, 40));
            _ = SessionCheckpoints.Publish(session.Store, DateTimeOffset.UnixEpoch);
            MetricDerivations derived = SessionMetricDerivations.For(session.Store)
                ?? throw new InvalidOperationException("A published session has a derivation.");
            long end = rows.Max(row => row.NativeTicks) + 1;
            long from = random.Next(0, (int)end);
            var interval = new TimeRange(from, from + random.Next(1, (int)end + 1));

            // The answer takes the relations and calls from the derivation it is given, never derives its own beside it.
            int relationsAsked = 0, callsAsked = 0;
            MetricDerivations counted = derived with
            {
                Relations = cancellation => { relationsAsked++; return derived.Relations(cancellation); },
                Calls = cancellation => { callsAsked++; return derived.Calls(cancellation); },
            };
            foreach (MetricRequest request in Requests(session.Store, interval))
            {
                string plain = Described(SessionMetrics.Evaluate(session.Store, request));
                string fast = Described(SessionMetrics.Evaluate(session.Store, request, new() { Derivations = counted }));
                Assert.True(plain == fast, $"seed {seed}, {request.Metric} by {request.Grouping}:\n{plain}\n{fast}");
            }

            Assert.True(relationsAsked > 0 && callsAsked > 0, $"seed {seed}: relations asked {relationsAsked}, calls asked {callsAsked}");

            // A later generation is never answered from an earlier one's derivation: it derives its own.
            Publish(session.Store, [.. RandomTraffic(random)
                .Where(row => row.Mechanism != Mechanism.ProcessLifecycle)
                .Select(row => row with { NativeTicks = row.NativeTicks + end, RawRecordOrdinal = row.RawRecordOrdinal + 100_000 })]);
            MetricRequest peers = new() { Basis = AnalysisBasis.SourceObservations, Metric = Metric.ActivePeers, Grouping = LaneGrouping.InstanceOnly };
            Assert.Equal(
                Described(SessionMetrics.Evaluate(session.Store, peers)),
                Described(SessionMetrics.Evaluate(session.Store, peers, new() { Derivations = derived })));
        }
    }

    private static IEnumerable<MetricRequest> Requests(SessionStore store, TimeRange interval)
    {
        MetricRequest records = new() { Basis = AnalysisBasis.SourceObservations, Metric = Metric.Observations, Grouping = LaneGrouping.InstanceOnly };
        yield return records;
        yield return records with { Grouping = LaneGrouping.Executable, Interval = interval };
        yield return new()
        {
            Basis = AnalysisBasis.SourceObservations,
            Metric = Metric.BytesSent,
            ByteDomain = ByteDomain.TransportObserved,
            AccountingSide = AccountingSide.SendSide,
            Grouping = LaneGrouping.Executable,
        };
        yield return new() { Basis = AnalysisBasis.SourceObservations, Metric = Metric.ActivePeers, Grouping = LaneGrouping.InstanceOnly };
        yield return new() { Basis = AnalysisBasis.SourceObservations, Metric = Metric.ActiveChannels, Interval = interval };
        yield return new() { Basis = AnalysisBasis.LogicalOperations, Metric = Metric.OperationsCompleted, Grouping = LaneGrouping.InstanceOnly };
        yield return new()
        {
            Basis = AnalysisBasis.LogicalOperations,
            Metric = Metric.Duration,
            DurationInterval = DurationInterval.ClientCall,
            Cohort = OperationCohort.CompletedInRange,
            Statistic = DurationStatistic.Median,
            Grouping = LaneGrouping.Executable,
        };

        // A focus names an instance of this generation, as a person picks one from its process grouping.
        MetricResult grouped = SessionMetrics.Evaluate(store, records);
        if (grouped.Groups.FirstOrDefault(group => group.Process is not null)?.Process is { } focus)
        {
            yield return new()
            {
                Basis = AnalysisBasis.SourceObservations,
                Metric = Metric.ActivePeers,
                Owner = focus.Id,
            };
        }
    }

    /// <summary>What an answer says, in one comparable text: its value, contributions and every group's.</summary>
    private static string Described(MetricResult result) => string.Join('\n',
    [
        string.Create(CultureInfo.InvariantCulture,
            $"{result.Unavailable} value {result.Value} known {result.KnownContributions} unknown {result.UnknownContributions}"),
        .. result.Groups.Select(group => string.Create(CultureInfo.InvariantCulture,
            $"{group.Kind} {group.Process?.Id} {group.Executable} {group.Reason} {group.Value} {group.KnownContributions} {group.UnknownContributions}")),
    ]);

    /// <summary>
    /// Paired and one-sided TCP among four processes, two of them one executable, some records naming no owner, and RPC
    /// calls made and served with and without their starts.
    /// </summary>
    private static List<ObservationRowV1> RandomTraffic(Random random)
    {
        int[] pids = [100, 101, 200, 300];
        string[] images = [@"C:\Tools\app.exe", @"C:\Tools\app.exe", @"C:\Tools\db.exe", @"C:\Tools\svc.exe"];
        var rows = new List<ObservationRowV1>();
        ulong ordinal = 1;
        for (int index = 0; index < pids.Length; index++)
        {
            rows.Add(Lifecycle(0, ObservationKind.Create, pids[index], ordinal++) with
            {
                ResourceName = images[index],
                SessionRelativeTicks = 0,
            });
        }

        long ticks = 1;
        for (int index = 0; index < random.Next(3, 30); index++)
        {
            ticks += random.Next(1, 5);
            int client = pids[random.Next(pids.Length)];
            int server = pids[random.Next(pids.Length)];
            string near = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{50_000 + random.Next(0, 9)}");
            string far = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{8_000 + (server % 5)}");
            rows.Add(Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, random.Next(0, 900), random.Next(9) == 0 ? null : client, ordinal++)
                .Between(near, far) with { SessionRelativeTicks = ticks * 100 });
            if (random.Next(4) != 0)
            {
                rows.Add(Transfer(ticks + 1, ObservationKind.Receive, AccountingSide.ReceiveSide, 32, server, ordinal++)
                    .Between(far, near) with { SessionRelativeTicks = (ticks + 1) * 100 });
                ticks++;
            }

            if (random.Next(3) == 0)
            {
                Guid activity = new(1_000 + index, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 2);
                Direction side = random.Next(2) == 0 ? Direction.Outbound : Direction.Inbound;
                if (random.Next(5) != 0)
                {
                    rows.Add(RpcCall(ticks, ObservationKind.RequestStart, side, client, ordinal++, activity, ServiceControl)
                        with { SessionRelativeTicks = ticks * 100 });
                }

                ticks += random.Next(1, 6);
                rows.Add(RpcCall(ticks, ObservationKind.RequestEnd, side, client, ordinal++, activity, status: 0)
                    with { SessionRelativeTicks = ticks * 100 });
            }
        }

        return rows;
    }
}
