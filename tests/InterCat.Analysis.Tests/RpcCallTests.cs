using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.IncrementalDerivationTests;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// RPC calls: each start paired with its stop through the activity id, on one side of one process, never by time and
/// never across sides (`contracts/operations-v1.md`, ADR-031).
/// </summary>
public sealed class RpcCallTests
{
    private static readonly Guid ServiceControl = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");
    private static readonly Guid LocalSecurity = Guid.Parse("12345778-1234-abcd-ef00-0123456789ab");

    [Fact(DisplayName = "P8: an RPC call is its start and the stop of the same activity, never the nearest stop in time")]
    public void ACallIsPairedByItsActivity()
    {
        Guid first = Activity(1);
        Guid second = Activity(2);
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(10, ObservationKind.Create, 400, 1),
            RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 2, first, ServiceControl),
            RpcCall(110, ObservationKind.RequestStart, Direction.Outbound, 400, 3, second, ServiceControl),
            RpcCall(120, ObservationKind.RequestEnd, Direction.Outbound, 400, 4, second, status: 0),
            RpcCall(150, ObservationKind.RequestEnd, Direction.Outbound, 400, 5, first, status: 1_753),
        ]);
        (RpcCallIndex calls, SegmentReaderV1[] segments) = Derive(session.Store);

        Assert.Equal((4L, 0L), (calls.CallRecords, calls.OtherRpcRecords));
        Assert.Equal(Counts(calls: 2, started: 2, completed: 2, failed: 1), calls.Totals);
        RpcCallGroup group = Assert.Single(calls.Groups);
        Assert.Equal((400, RpcCallSide.Client, (Guid?)ServiceControl), (group.ProcessId, group.Side, group.Interface));
        Assert.Equal(new ProcessBinding(0, RelationStrength.Correlated, ProcessBindingReason.Bound), group.Process);

        // One tick of the test clock is 100 ns. The later start's stop came first, and pairs with it alone.
        Assert.Equal(new RpcCallDurations(2, 1_000, 1_000, 5_000, 5_000), group.Durations);
        IReadOnlyList<RpcCall> described = calls.CallsOf(group, segments, 0, 10);
        Assert.Equal([first, second], described.Select(call => call.ActivityId!.Value));
        Assert.Equal([(2UL, 5UL), (3UL, 4UL)], described.Select(call => (Ordinal(call.Start!), Ordinal(call.Stop!))));
        Assert.Equal([1_753L, 0L], described.Select(call => call.Status!.Value));
        Assert.Equal([5_000L, 1_000L], described.Select(call => call.DurationNanoseconds!.Value));
        Assert.All(described, call => Assert.Equal(RpcCallState.Completed, call.State));
        Assert.Equal(described[0].Start!.Observation, described[0].Identity);
        Assert.Equal([second], calls.CallsOf(group, segments, 1, 10).Select(call => call.ActivityId!.Value));
    }

    [Fact(DisplayName = "P8: a start still open at capture end is censored, and a stop without its start is given none")]
    public void OpenAndUnstartedCallsAreStatedNotGuessed()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(10, ObservationKind.Create, 400, 1),
            RpcCall(100, ObservationKind.RequestEnd, Direction.Outbound, 400, 2, Activity(1), status: 0),
            RpcCall(200, ObservationKind.RequestStart, Direction.Outbound, 400, 3, Activity(2), ServiceControl),
            RpcCall(300, ObservationKind.RequestStart, Direction.Outbound, 400, 4, activity: null, ServiceControl),
            RpcCall(310, ObservationKind.RequestEnd, Direction.Outbound, 400, 5, activity: null, status: 0),
        ]);
        (RpcCallIndex calls, SegmentReaderV1[] segments) = Derive(session.Store);

        // A record with no activity id pairs with nothing, even with the stop that follows it on the same side.
        Assert.Equal(Counts(calls: 4, started: 2, open: 1, startNotObserved: 1, noActivityId: 2), calls.Totals);
        RpcCall[] described = [.. calls.Groups.SelectMany(group => calls.CallsOf(group, segments, 0, 10))];
        Assert.Equal(
            [
                (RpcCallState.StartNotObserved, false, true),
                (RpcCallState.NoActivityId, false, true),
                (RpcCallState.OpenAtCaptureEnd, true, false),
                (RpcCallState.NoActivityId, true, false),
            ],
            described
                .OrderBy(call => call.Start is null ? 0 : 1)
                .ThenBy(call => (call.Start ?? call.Stop)!.NativeTicks)
                .Select(call => (call.State, call.Start is not null, call.Stop is not null)));
        Assert.All(described, call => Assert.Null(call.DurationNanoseconds));
        Assert.All(calls.Groups, group => Assert.Null(group.Durations));

        // A stop names no interface, so calls without a start are grouped apart from the interface's own.
        Assert.Equal([null, ServiceControl], calls.Groups.Select(group => group.Interface).Order());
    }

    [Fact(DisplayName = "P8: an activity id reused before its stop leaves those calls ambiguous, and the key pairs again after")]
    public void AReusedActivityIsAmbiguousUntilItsCallsClose()
    {
        Guid reused = Activity(7);
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(10, ObservationKind.Create, 400, 1),
            RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 2, reused, ServiceControl),
            RpcCall(110, ObservationKind.RequestStart, Direction.Outbound, 400, 3, reused, ServiceControl),
            RpcCall(120, ObservationKind.RequestEnd, Direction.Outbound, 400, 4, reused, status: 0),
            RpcCall(130, ObservationKind.RequestEnd, Direction.Outbound, 400, 5, reused, status: 0),
            RpcCall(200, ObservationKind.RequestStart, Direction.Outbound, 400, 6, reused, ServiceControl),
            RpcCall(210, ObservationKind.RequestEnd, Direction.Outbound, 400, 7, reused, status: 0),
            RpcCall(300, ObservationKind.RequestStart, Direction.Outbound, 400, 8, reused, ServiceControl),
            RpcCall(310, ObservationKind.RequestStart, Direction.Outbound, 400, 9, reused, ServiceControl),
            RpcCall(320, ObservationKind.RequestEnd, Direction.Outbound, 400, 10, reused, status: 0),
        ]);
        (RpcCallIndex calls, SegmentReaderV1[] segments) = Derive(session.Store);

        // Nested or overlapped, the first run's stops cannot be told apart; the second call is whole again; the last
        // run ends with one of its two starts still open, and which one is as unknowable.
        Assert.Equal(Counts(calls: 8, started: 5, completed: 1, ambiguous: 7), calls.Totals);
        RpcCall paired = calls.Groups.SelectMany(group => calls.CallsOf(group, segments, 0, 20))
            .Single(call => call.State == RpcCallState.Completed);
        Assert.Equal((6UL, 7UL), (Ordinal(paired.Start!), Ordinal(paired.Stop!)));
    }

    [Fact(DisplayName = "P7: a client call and a server call are never paired, even when they carry one activity id")]
    public void SidesAreNeverPaired()
    {
        Guid shared = Activity(3);
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(10, ObservationKind.Create, 400, 1),
            Lifecycle(11, ObservationKind.Create, 1_960, 2),
            RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 3, shared, ServiceControl),
            RpcCall(105, ObservationKind.RequestStart, Direction.Inbound, 400, 4, shared, ServiceControl),
            RpcCall(110, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 5, shared, status: 0),
        ]);
        (RpcCallIndex calls, _) = Derive(session.Store);

        Assert.Equal(Counts(calls: 3, started: 2, open: 2, startNotObserved: 1), calls.Totals);
        Assert.Equal(
            [(400, RpcCallSide.Client), (400, RpcCallSide.Server), (1_960, RpcCallSide.Server)],
            calls.Groups.Select(group => (group.ProcessId, group.Side)).Order());
    }

    [Fact(DisplayName = "R22: a call binds to its process by its first record's reading, and outside every lifetime says why")]
    public void ACallBindsByItsFirstRecord()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(10, ObservationKind.Create, 400, 1),
            RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 2, Activity(1), ServiceControl),
            Lifecycle(150, ObservationKind.Exit, 400, 3, exitCode: 0),
            RpcCall(160, ObservationKind.RequestEnd, Direction.Outbound, 400, 4, Activity(1), status: 0),
            RpcCall(170, ObservationKind.RequestEnd, Direction.Outbound, 400, 5, Activity(2), status: 0),
        ]);
        (RpcCallIndex calls, _) = Derive(session.Store);

        // The completed call binds by its start, inside the lifetime; the stop with no start binds by its own reading.
        Assert.Equal(
            [
                (RpcCallState.Completed, ProcessBindingReason.Bound),
                (RpcCallState.StartNotObserved, ProcessBindingReason.AfterExit),
            ],
            calls.Groups.Select(group => (
                group.Counts.Completed > 0 ? RpcCallState.Completed : RpcCallState.StartNotObserved,
                group.Process.Reason)).OrderBy(pair => pair.Item1));
    }

    [Theory(DisplayName = "I14: calls do not depend on how their records were cut into segments or delivered")]
    [InlineData(5)]
    [InlineData(23)]
    [InlineData(20_260_927)]
    public void CallsDoNotDependOnTheCut(int seed)
    {
        var random = new Random(seed);
        for (int trial = 0; trial < 8; trial++)
        {
            (ObservationRowV1[] rows, SourceFieldRowV1[] fields, RpcCallCounts expected) = RandomCalls(random);
            using var whole = new TemporarySession();
            Publish(whole.Store, rows, rowsPerSegment: rows.Length, fields: fields);
            using var cut = new TemporarySession();
            ObservationRowV1[] delivered = [.. rows.OrderBy(_ => random.Next())];
            Publish(cut.Store, delivered, rowsPerSegment: random.Next(1, rows.Length + 1), fields: fields);

            (RpcCallIndex left, SegmentReaderV1[] leftSegments) = Derive(whole.Store);
            (RpcCallIndex right, SegmentReaderV1[] rightSegments) = Derive(cut.Store);
            Assert.Equal(expected, left.Totals);
            Assert.Equal(left.Totals, right.Totals);
            Assert.Equal(
                left.Groups.Select(group => (group.ProcessId, group.Process, group.Side, group.Interface, group.Counts, group.Durations)),
                right.Groups.Select(group => (group.ProcessId, group.Process, group.Side, group.Interface, group.Counts, group.Durations)));
            for (int index = 0; index < left.Groups.Count; index++)
            {
                Assert.Equal(
                    left.CallsOf(left.Groups[index], leftSegments, 0, int.MaxValue).Select(Comparable),
                    right.CallsOf(right.Groups[index], rightSegments, 0, int.MaxValue).Select(Comparable));
            }
        }
    }

    [Fact(DisplayName = "P8: FX-RPC-001's calls pair as measured - 62 client calls by activity id, 61 served ones")]
    public void CommittedEvidencePairsAsMeasured()
    {
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", "FX-RPC-001", "evidence", "observations.jsonl");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        RpcCallObservation[] observations =
        [
            .. File.ReadAllLines(evidence)
                .Where(line => line.Length > 0)
                .Select(line => JsonSerializer.Deserialize<RpcCallObservation>(line, options)!),
        ];
        var rows = new List<ObservationRowV1>();
        var fields = new List<SourceFieldRowV1>();
        foreach (RpcCallObservation observation in observations)
        {
            ObservationRowV1 row = RpcCall(
                observation.SourceTicks,
                observation.Kind,
                observation.Direction,
                observation.ProcessId,
                observation.Id.RawRecordId.RecordOrdinal,
                observation.ActivityId == Guid.Empty ? null : observation.ActivityId,
                observation.InterfaceUuid,
                observation.Status) with
            {
                RawStreamId = observation.Id.RawRecordId.StreamId,
                RawSourceEpoch = observation.Id.RawRecordId.SourceEpoch,
                FactKey = observation.Id.FactKey,
                HeaderThreadId = observation.ThreadId,
            };
            rows.Add(row);
            if (observation.ProcedureNumber is { } procedure)
            {
                fields.Add(Field(row, SourceField.RpcProcedureNumber, procedure));
            }

            if (observation.Protocol is { } protocol)
            {
                fields.Add(Field(row, SourceField.RpcProtocolSequence, protocol));
            }
        }

        using var session = new TemporarySession();
        Publish(session.Store, rows, fields: fields);
        (RpcCallIndex calls, SegmentReaderV1[] segments) = Derive(session.Store);

        // The client's one call without an id is its thread's first, to the local security interface: its start and
        // stop are two unpaired calls. Every other call paired, and none failed.
        Assert.Equal(248L, calls.CallRecords);
        RpcCallGroup[] client = [.. calls.Groups.Where(group => group.Side == RpcCallSide.Client)];
        Assert.All(client, group => Assert.Equal(63_748, group.ProcessId));
        Assert.Equal(
            [
                ((Guid?)null, Counts(calls: 1, noActivityId: 1)),
                (LocalSecurity, Counts(calls: 3, started: 3, completed: 2, noActivityId: 1)),
                (ServiceControl, Counts(calls: 60, started: 60, completed: 60)),
            ],
            client.Select(group => (group.Interface, group.Counts)).OrderBy(pair => pair.Interface));
        RpcCallGroup served = Assert.Single(calls.Groups, group => group.Side == RpcCallSide.Server);
        Assert.Equal((1_960, (Guid?)ServiceControl), (served.ProcessId, served.Interface));
        Assert.Equal(Counts(calls: 61, started: 61, completed: 61), served.Counts);

        // Each call keeps the procedure and protocol its start carried. The durations are the readings' own, at the
        // capture's 10 MHz: the distributions below were computed from the evidence file apart from this code. A client
        // call spans the transport, and the part the host served is shorter.
        RpcCallGroup scm = client.Single(group => group.Interface == ServiceControl);
        Assert.All(calls.CallsOf(scm, segments, 0, 100), call => Assert.True(call.Procedure is not null && call.Protocol is not null));
        Assert.Equal(new RpcCallDurations(60, 17_200, 25_600, 97_900, 176_200), scm.Durations);
        Assert.Equal(new RpcCallDurations(61, 1_800, 4_800, 61_000, 456_600), served.Durations);
    }

    private static (RpcCallIndex Calls, SegmentReaderV1[] Segments) Derive(SessionStore store)
    {
        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
        return (RpcCallIndex.Derive(observations, fields, processes, TestClock), observations);
    }

    private static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1);

    private static ulong Ordinal(RpcCallRecord record) => record.Observation.RawRecordId.RecordOrdinal;

    private static RpcCallCounts Counts(
        long calls,
        long started = 0,
        long completed = 0,
        long failed = 0,
        long open = 0,
        long startNotObserved = 0,
        long noActivityId = 0,
        long ambiguous = 0) => new()
    {
        Calls = calls,
        Started = started,
        Completed = completed,
        Failed = failed,
        OpenAtCaptureEnd = open,
        StartNotObserved = startNotObserved,
        NoActivityId = noActivityId,
        Ambiguous = ambiguous,
    };

    private static object Comparable(RpcCall call) => (
        call.Identity,
        call.State,
        call.Start?.Observation,
        call.Stop?.Observation,
        call.ActivityId,
        call.Interface,
        call.Procedure,
        call.Status,
        call.DurationNanoseconds);

    /// <summary>
    /// Calls of three processes on both sides, each with its own activity id, some missing a record or an id. The
    /// counts are what the records themselves say, so a derivation that mispairs any of them misses them.
    /// </summary>
    private static (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields, RpcCallCounts Expected) RandomCalls(Random random)
    {
        int[] pids = [400, 500, 1_960];
        var rows = new List<ObservationRowV1>
        {
            Lifecycle(1, ObservationKind.Create, 400, 1),
            Lifecycle(2, ObservationKind.Create, 500, 2),
        };
        var fields = new List<SourceFieldRowV1>();
        ulong ordinal = 10;
        long calls = 0, started = 0, completed = 0, failed = 0, open = 0, startNotObserved = 0, noActivityId = 0;
        int count = random.Next(4, 40);
        for (int index = 0; index < count; index++)
        {
            int pid = pids[random.Next(pids.Length)];
            Direction direction = random.Next(2) == 0 ? Direction.Outbound : Direction.Inbound;
            Guid? activity = random.Next(10) == 0 ? null : Activity(1_000 + index);
            long start = 100 + random.Next(0, 5_000);
            long stop = start + random.Next(1, 400);
            long status = random.Next(4) == 0 ? 5 : 0;
            int shape = random.Next(6);
            bool hasStart = shape != 0;
            bool hasStop = shape != 1;
            if (hasStart)
            {
                ObservationRowV1 row = RpcCall(start, ObservationKind.RequestStart, direction, pid, ++ordinal, activity,
                    random.Next(3) == 0 ? LocalSecurity : ServiceControl);
                rows.Add(row);
                fields.Add(Field(row, SourceField.RpcProcedureNumber, random.Next(0, 64)));
            }

            if (hasStop)
            {
                rows.Add(RpcCall(stop, ObservationKind.RequestEnd, direction, pid, ++ordinal, activity, status: status));
            }

            started += hasStart ? 1 : 0;
            if (activity is null)
            {
                calls += (hasStart ? 1 : 0) + (hasStop ? 1 : 0);
                noActivityId += (hasStart ? 1 : 0) + (hasStop ? 1 : 0);
                continue;
            }

            calls++;
            completed += hasStart && hasStop ? 1 : 0;
            failed += hasStart && hasStop && status != 0 ? 1 : 0;
            open += hasStart && !hasStop ? 1 : 0;
            startNotObserved += !hasStart ? 1 : 0;
        }

        return ([.. rows], [.. fields], new()
        {
            Calls = calls,
            Started = started,
            Completed = completed,
            Failed = failed,
            OpenAtCaptureEnd = open,
            StartNotObserved = startNotObserved,
            NoActivityId = noActivityId,
            Ambiguous = 0,
        });
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
