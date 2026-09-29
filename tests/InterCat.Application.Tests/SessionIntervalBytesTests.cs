using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// The interval table's bytes on a real session: each column's transport bytes over the records a listing counts, which
/// are what the byte measure answers over that column's own interval (R18), those that recorded no size stated apart
/// (R3, R21).
/// </summary>
public sealed class SessionIntervalBytesTests
{
    [Fact(DisplayName = "R18: each column's bytes are what the byte measure answers over the column's own interval")]
    public void EachColumnsBytesAreTheMeasuresOverItsInterval()
    {
        for (int seed = 0; seed < 20; seed++)
        {
            var random = new Random(seed);
            List<ObservationRowV1> rows = RandomTraffic(random);
            using var session = new TemporarySession();
            Publish(session.Store, rows, rowsPerSegment: random.Next(3, 40));
            long end = rows.Max(row => row.NativeTicks) + 1;
            long start = random.Next(0, (int)end);
            var interval = new TimeRange(start, start + random.Next(1, (int)end + 1));
            int columns = random.Next(1, 12);

            SessionIntervalByteMeasures measured = SessionIntervalByteQuery.Measure(
                session.Store, interval, columns, IntervalByteScope.Whole);
            Assert.Equal((int)Math.Min(columns, interval.SpanTicks), measured.Columns.Count);
            for (int column = 0; column < measured.Columns.Count; column++)
            {
                // Session nanoseconds are 100 native ticks here, so a presentation tick is a native one.
                SessionByteMeasures expected = SessionByteRanking.Measure(session.Store, measured.IntervalOf(column));
                TransportBytes every = expected.ByProcess.Values.Aggregate(expected.Unattributed, (sum, bytes) => sum.Plus(bytes));
                Assert.True(every == measured.Columns[column], $"seed {seed}, column {column} of {measured.Columns.Count}");
                Assert.Same(measured.Columns[column], measured.For(measured.IntervalOf(column)));
            }

            // Only a column's own interval is answered: a wider or shifted one is not given a column's bytes.
            Assert.Null(measured.For(new TimeRange(interval.StartTicks - 1, interval.EndTicks)));
        }
    }

    [Fact(DisplayName = "R18: lanes measured together in one pass each hold what the byte measure answers over their mechanism's records")]
    public void LanesMeasuredTogetherEachHoldTheirMechanismsMeasure()
    {
        for (int seed = 0; seed < 20; seed++)
        {
            var random = new Random(seed);
            List<ObservationRowV1> rows = [.. RandomTraffic(random).Select(row =>
                row.Mechanism == Mechanism.Tcp && random.Next(3) == 0 ? row with { Mechanism = Mechanism.Udp } : row)];
            using var session = new TemporarySession();
            Publish(session.Store, rows, rowsPerSegment: random.Next(3, 40));
            long end = rows.Max(row => row.NativeTicks) + 1;
            long start = random.Next(0, (int)end);
            var interval = new TimeRange(start, start + random.Next(1, (int)end + 1));
            int columns = random.Next(1, 12);

            // Any lanes, in any order: one with no records of its mechanism measures none.
            Mechanism[] lanes = [.. new[] { Mechanism.Tcp, Mechanism.Udp, Mechanism.ProcessLifecycle, Mechanism.NamedPipe }
                .Where(_ => random.Next(3) > 0).DefaultIfEmpty(Mechanism.Tcp).OrderBy(_ => random.Next())];
            SessionMechanismByteMeasures measured = SessionIntervalByteQuery.MeasureByMechanism(
                session.Store, interval, columns, lanes);
            Assert.Equal(lanes, measured.Lanes.Select(lane => lane.Scope.Mechanism!.Value));
            foreach (Mechanism mechanism in lanes)
            {
                SessionIntervalByteMeasures expected = SessionIntervalByteQuery.Measure(
                    session.Store, interval, columns, new() { Mechanism = mechanism });
                SessionIntervalByteMeasures lane = measured.Of(mechanism)!;
                Assert.Equal((expected.SessionId, expected.Generation, expected.Interval, expected.Scope),
                    (lane.SessionId, lane.Generation, lane.Interval, lane.Scope));
                Assert.True(expected.Columns.SequenceEqual(lane.Columns), $"seed {seed}, {mechanism}");
            }

            Assert.Null(measured.Of(Mechanism.Alpc));
        }

        // Each lane is named once, and only a mechanism §23 defines names one.
        using var refused = new TemporarySession();
        Publish(refused.Store, TwoConnections());
        foreach (Mechanism[] lanes in new Mechanism[][] { [], [Mechanism.Tcp, Mechanism.Tcp], [(Mechanism)77] })
        {
            Assert.Throws<ArgumentException>(() =>
                SessionIntervalByteQuery.MeasureByMechanism(refused.Store, new TimeRange(1, 40), 8, lanes));
        }
    }

    [Fact(DisplayName = "R15: a lane's bytes are its own records': a process's, one direction of them, a channel end's, a mechanism's")]
    public void ALanesBytesAreItsOwnRecords()
    {
        using var session = new TemporarySession();
        Publish(session.Store, TwoConnections());
        WorkspaceSnapshot whole = OverviewWorkspace.From(SessionOverviewProjector.Project(session.Store));
        ProcessNode client = whole.Processes.Single(node => node.ProcessId == 100);
        ProcessNode server = whole.Processes.Single(node => node.ProcessId == 200);
        Channel busy = whole.Channels.Single(channel => channel.Name.Contains(":50000", StringComparison.Ordinal));
        Channel large = whole.Channels.Single(channel => channel.Name.Contains(":50001", StringComparison.Ordinal));

        // The overview's own columns, one tick each here, as the table lists them at the whole extent.
        TimeRange extent = whole.Extent;
        int columns = whole.Timeline.Count;
        TransportBytes At(IntervalByteScope scope, long tick) =>
            SessionIntervalByteQuery.Measure(session.Store, extent, columns, scope).For(new TimeRange(tick, tick + 1))!;
        TransportBytes Sent(long bytes) => new(bytes, 1, 0, 0, 0, 0);
        TransportBytes Received(long bytes) => new(0, 0, 0, bytes, 1, 0);

        // Every record: the client's send at 10, the server's receive at 11, and a send at 35 that recorded no size,
        // counted apart rather than as zero.
        Assert.Equal(Sent(100), At(IntervalByteScope.Whole, 10));
        Assert.Equal(Received(100), At(IntervalByteScope.Whole, 11));
        Assert.Equal(new TransportBytes(0, 0, 1, 0, 0, 0), At(IntervalByteScope.Whole, 35));
        Assert.Equal(TransportBytes.None, At(IntervalByteScope.Whole, 1));

        // A process's own records: the client holds its send and none of the server's receive.
        Assert.Equal(Sent(100), At(new() { Owner = client.Id }, 10));
        Assert.Equal(TransportBytes.None, At(new() { Owner = client.Id }, 11));
        Assert.Equal(Received(100), At(new() { Owner = server.Id }, 11));

        // One source direction of them: the client's send is outbound, and nothing of it inbound.
        Assert.Equal(Sent(100), At(new() { Owner = client.Id, Direction = Direction.Outbound }, 10));
        Assert.Equal(TransportBytes.None, At(new() { Owner = client.Id, Direction = Direction.Inbound }, 10));

        // A channel and its ends: the busy channel's first end is the server's port 8080, which sorts before the client's
        // 50000 and received; its second is the client's, which sent.
        Assert.Equal(Sent(100), At(new() { ChannelKey = busy.Key }, 10));
        Assert.Equal(Sent(100), At(new() { ChannelKey = busy.Key, End = 1 }, 10));
        Assert.Equal(TransportBytes.None, At(new() { ChannelKey = busy.Key, End = 0 }, 10));
        Assert.Equal(Received(100), At(new() { ChannelKey = busy.Key, End = 0 }, 11));
        Assert.Equal(TransportBytes.None, At(new() { ChannelKey = large.Key }, 10));
        Assert.Equal(Sent(5_000), At(new() { ChannelKey = large.Key }, 30));

        // A mechanism's lane: TCP's records carry the transfers, and the lifecycle's no size at all.
        Assert.Equal(Sent(100), At(new() { Mechanism = Mechanism.Tcp }, 10));
        Assert.Equal(TransportBytes.None, At(new() { Mechanism = Mechanism.ProcessLifecycle }, 10));
    }

    [Fact(DisplayName = "R15: a scope no listing names, or one this generation cannot resolve, is refused rather than measured as nothing")]
    public void AScopeNoListingNamesIsRefused()
    {
        using var session = new TemporarySession();
        Publish(session.Store, TwoConnections());
        var interval = new TimeRange(1, 40);
        ProcessInstanceId someone = new(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        foreach (IntervalByteScope scope in new IntervalByteScope[]
        {
            new() { Direction = Direction.Outbound },
            new() { End = 0 },
            new() { ChannelKey = "channel", End = 2 },
            new() { Owner = someone, Mechanism = Mechanism.Tcp },
            new() { Owner = new ProcessInstanceId(Guid.Empty) },
            new() { ChannelKey = " " },
        })
        {
            Assert.NotNull(scope.Validate());
            Assert.Throws<ArgumentException>(() => SessionIntervalByteQuery.Measure(session.Store, interval, 8, scope));
        }

        // A process or channel this generation does not hold is the evidence rung's refusal, never zero bytes.
        Assert.Throws<InvalidOperationException>(() =>
            SessionIntervalByteQuery.Measure(session.Store, interval, 8, new() { Owner = someone }));
        Assert.Throws<InvalidOperationException>(() =>
            SessionIntervalByteQuery.Measure(session.Store, interval, 8, new() { ChannelKey = "no such channel" }));
        Assert.Null(IntervalByteScope.Whole.Validate());
    }

    /// <summary>
    /// The client sends three 100-byte messages on one connection and a 5,000-byte one on another, and once more on it
    /// without recording a size; the server receives the four that were measured.
    /// </summary>
    private static ObservationRowV1[] TwoConnections() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
        Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
        .. Enumerable.Range(0, 3).SelectMany(index => new[]
        {
            Timed(Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 100, 100, (ulong)(10 + (2 * index)))
                .Between("127.0.0.1:50000", "127.0.0.1:8080")),
            Timed(Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 200, (ulong)(11 + (2 * index)))
                .Between("127.0.0.1:8080", "127.0.0.1:50000")),
        }),
        Timed(Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 5_000, 100, 30).Between("127.0.0.1:50001", "127.0.0.1:8080")),
        Timed(Transfer(31, ObservationKind.Receive, AccountingSide.ReceiveSide, 5_000, 200, 31).Between("127.0.0.1:8080", "127.0.0.1:50001")),
        Timed(Transfer(35, ObservationKind.Send, AccountingSide.SendSide, null, 100, 35).Between("127.0.0.1:50001", "127.0.0.1:8080")),
    ];

    /// <summary>Random sends and receives, some unmeasured and some of an owner no lifecycle names, from reused PIDs.</summary>
    private static List<ObservationRowV1> RandomTraffic(Random random)
    {
        var rows = new List<ObservationRowV1>
        {
            Lifecycle(0, ObservationKind.Create, 100, 1),
            Lifecycle(0, ObservationKind.Create, 200, 2),
        };
        ulong ordinal = 10;
        long ticks = 1;
        if (random.Next(2) == 0)
        {
            // PID 100 exits and is reused, so a later record of it binds as a candidate the default policy excludes.
            rows.Add(Lifecycle(50, ObservationKind.Exit, 100, ++ordinal));
            rows.Add(Lifecycle(60, ObservationKind.Create, 100, ++ordinal));
        }

        int count = random.Next(5, 80);
        for (int index = 0; index < count; index++)
        {
            ticks += random.Next(0, 5);
            bool send = random.Next(2) == 0;
            int? owner = random.Next(10) switch
            {
                0 => null,
                1 => 300,
                < 6 => 100,
                _ => 200,
            };
            long? bytes = random.Next(6) == 0 ? null : random.Next(0, 5_000);
            rows.Add(Transfer(
                ticks,
                send ? ObservationKind.Send : ObservationKind.Receive,
                send ? AccountingSide.SendSide : AccountingSide.ReceiveSide,
                bytes,
                owner,
                ++ordinal) with { SessionRelativeTicks = ticks * 100 });
        }

        return rows;
    }

    /// <summary>A row whose session time is its reading in workspace ticks.</summary>
    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
