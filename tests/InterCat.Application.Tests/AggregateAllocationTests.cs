using System.Globalization;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// R11's aggregate loops: the queries the window runs on every interaction allocate nothing per row. Each is measured
/// warm at two session sizes, so whatever does not grow with the rows - the answer's own objects, one per bucket or
/// entity - cancels out, and only a per-row allocation is left.
/// </summary>
public sealed class AggregateAllocationTests
{
    private const int SmallRows = 10_000;
    private const int LargeRows = 30_000;

    [Fact(DisplayName = "R11: a warm projection, timeline count, focused count and interval count allocate nothing per row")]
    public void WarmAggregatesAllocateNothingPerRow()
    {
        using var small = new TemporarySession();
        Publish(small.Store, Pairs(SmallRows));
        using var large = new TemporarySession();
        Publish(large.Store, Pairs(LargeRows));

        Dictionary<string, long> atSmall = Measure(small.Store);
        Dictionary<string, long> atLarge = Measure(large.Store);

        var report = new List<string>();
        foreach ((string query, long bytes) in atLarge)
        {
            double perRow = (double)(bytes - atSmall[query]) / (LargeRows - SmallRows);
            if (perRow > 0.5)
            {
                report.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{query}: {atSmall[query]:N0} B at {SmallRows:N0} rows and {bytes:N0} B at {LargeRows:N0}, {perRow:F1} B per row"));
            }
        }

        Assert.True(report.Count == 0, string.Join(Environment.NewLine, report));
    }

    /// <summary>What one warm call of each query allocates on this thread.</summary>
    private static Dictionary<string, long> Measure(SessionStore store)
    {
        SessionOverviewBundle first = SessionOverviewProjector.Project(store);
        TimeRange extent = first.Extent!.Value;
        var owners = new TimelineFocus(null, [.. first.Nodes.Select(node => node.Id)]);
        var queries = new (string Name, Action Run)[]
        {
            ("overview projection", () => SessionOverviewProjector.Project(store)),
            ("timeline count", () => SessionTimelineQuery.Detail(store, extent, 64)),
            ("focused count", () => SessionTimelineQuery.Focused(store, extent, 64, owners)),
            ("interval count", () => SessionIntervalQuery.Count(store, extent)),
        };

        var spent = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach ((string name, Action run) in queries)
        {
            // Warm: derivations cached, readers verified, pooled buffers rented once, the code compiled.
            for (int warm = 0; warm < 3; warm++) run();
            long before = GC.GetAllocatedBytesForCurrentThread();
            run();
            spent[name] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        return spent;
    }

    /// <summary>Fifty paired loopback conversations, alternating sends and receives in runs of fifty.</summary>
    private static ObservationRowV1[] Pairs(int rows) =>
    [
        .. Enumerable.Range(0, rows).Select(index =>
        {
            int pair = index % 50;
            bool send = (index / 50) % 2 == 0;
            string client = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{40_000 + pair}");
            string server = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{8_000 + pair}");
            ObservationRowV1 row = send
                ? Transfer(10 + index, ObservationKind.Send, AccountingSide.SendSide, 64, 1_000 + pair, (ulong)(index + 1))
                    .Between(client, server)
                : Transfer(10 + index, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 5_000 + pair, (ulong)(index + 1))
                    .Between(server, client);
            return row with { SessionRelativeTicks = (10L + index) * 100 };
        }),
    ];
}
