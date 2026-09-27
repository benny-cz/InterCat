using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.IncrementalDerivationTests;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// Each process instance's own records (`process-activity-v1`, revision 166): exactly the rows whose owner binds to it as
/// the evidence policy admits, each counted once, and extended chunk by chunk exactly as a count of every chunk at once.
/// The ranked table orders processes by them, so a wrong count is a wrong ranking.
/// </summary>
public sealed class ProcessActivityTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [Theory(DisplayName = "R22: an instance's records are exactly the rows its owner binding gives it, under each policy")]
    [InlineData(4)]
    [InlineData(31)]
    [InlineData(20_260_927)]
    public void RecordsAreTheRowsEachOwnerBindingGives(int seed)
    {
        var random = new Random(seed);
        for (int trial = 0; trial < 10; trial++)
        {
            List<(ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)> chunks = RandomChunks(random);
            ObservationRowV1[] rows = [.. chunks.SelectMany(chunk => chunk.Rows)];
            using var session = new TemporarySession();
            Publish(session.Store, rows, rowsPerSegment: random.Next(1, rows.Length + 1),
                fields: [.. chunks.SelectMany(chunk => chunk.Fields)]);
            (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(session.Store);
            ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
            ProcessActivityIndex activity = ProcessActivityIndex.Derive(observations, processes);

            foreach (EvidencePolicy policy in Enum.GetValues<EvidencePolicy>())
            {
                var expected = new Dictionary<(int Instance, Mechanism Mechanism), long>();
                foreach (SegmentReaderV1 segment in observations)
                {
                    ProcessBinding[] owners = processes.OwnersOf(segment);
                    SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
                    for (int row = 0; row < segment.RowCount; row++)
                    {
                        if (owners[row].IsAdmittedUnder(policy))
                        {
                            (int, Mechanism) key = (owners[row].Instance, (Mechanism)mechanisms.UnsignedAt(row)!.Value);
                            expected[key] = expected.GetValueOrDefault(key) + 1;
                        }
                    }
                }

                for (int instance = 0; instance < processes.Instances.Count; instance++)
                {
                    (Mechanism Mechanism, long Records)[] counted = [.. expected
                        .Where(entry => entry.Key.Instance == instance)
                        .Select(entry => (entry.Key.Mechanism, entry.Value))
                        .OrderByDescending(entry => entry.Value)
                        .ThenBy(entry => entry.Mechanism)];
                    Assert.Equal(counted, activity.MechanismsOf(instance, policy).ToArray());
                    Assert.Equal(counted.Sum(entry => entry.Records), activity.RecordsOf(instance, policy));
                }
            }

            // Every row is counted once: by the instance it binds to, as a row naming no owner, or as one naming a PID at
            // a reading no instance of it held. A lifecycle record is its instance's under every policy.
            long bound = Enumerable.Range(0, processes.Instances.Count)
                .Sum(instance => activity.RecordsOf(instance, EvidencePolicy.AllIncludingConflicting));
            Assert.Equal(rows.Length, bound + activity.RecordsWithoutOwner + activity.RecordsNotBound);
            Assert.Equal(rows.Count(row => row.OwnerProcessId is null), activity.RecordsWithoutOwner);
            for (int instance = 0; instance < processes.Instances.Count; instance++)
            {
                long direct = activity.RecordsOf(instance, EvidencePolicy.DirectOnly);
                long correlated = activity.RecordsOf(instance, EvidencePolicy.IncludeCorrelated);
                long candidates = activity.RecordsOf(instance, EvidencePolicy.IncludeCandidates);
                Assert.True(direct <= correlated && correlated <= candidates,
                    $"A wider policy admitted fewer records: {direct}, {correlated}, {candidates}.");
            }
        }
    }

    [Theory(DisplayName = "I14: each instance's records extended chunk by chunk equal a count of every chunk at once")]
    [InlineData(6)]
    [InlineData(19)]
    [InlineData(20_260_927)]
    public void ExtendingEqualsCountingAgain(int seed)
    {
        var random = new Random(seed);
        int extended = 0;
        int declined = 0;
        for (int trial = 0; trial < 10; trial++)
        {
            List<(ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)> chunks = RandomChunks(random);
            var sessions = new List<TemporarySession>();
            try
            {
                var observations = new List<SegmentReaderV1>();
                var fields = new List<SegmentReaderV1>();
                ProcessInstanceIndex? processes = null;
                ProcessActivityIndex? activity = null;
                foreach ((ObservationRowV1[] rows, SourceFieldRowV1[] fieldRows) in chunks)
                {
                    var session = new TemporarySession();
                    sessions.Add(session);
                    Publish(session.Store, rows, rowsPerSegment: random.Next(1, rows.Length + 1), fields: fieldRows);
                    (SegmentReaderV1[] chunkObservations, SegmentReaderV1[] chunkFields) = SegmentsOf(session.Store);
                    observations.AddRange(chunkObservations);
                    fields.AddRange(chunkFields);

                    ProcessInstanceIndex fullProcesses = ProcessInstanceIndex.Derive(observations, TestClock, fields);
                    ProcessActivityIndex fullActivity = ProcessActivityIndex.Derive(observations, fullProcesses);
                    if (processes is null)
                    {
                        (processes, activity) = (fullProcesses, fullActivity);
                        continue;
                    }

                    ProcessInstanceIndex next = processes.Extend(observations, TestClock, fields)
                        ?? throw new Xunit.Sdk.XunitException("Every segment was carried, so the instances must extend.");
                    ProcessActivityIndex? nextActivity = activity!.Extend(observations, next);
                    if (nextActivity is null)
                    {
                        declined++;
                        nextActivity = ProcessActivityIndex.Derive(observations, next);
                    }
                    else
                    {
                        extended++;
                    }

                    AssertSameActivity(fullActivity, nextActivity, fullProcesses.Instances.Count);
                    (processes, activity) = (next, nextActivity);
                }
            }
            finally
            {
                foreach (TemporarySession session in sessions)
                {
                    session.Dispose();
                }
            }
        }

        // Most chunks extend; a chunk whose late records would move an earlier record to another instance declines.
        Assert.True(extended > declined, $"{extended} extensions and {declined} declined.");
    }

    [Fact(DisplayName = "I14: counts decline to extend, for a full count, when a record already counted would bind otherwise")]
    public void ExtensionDeclinesWhatWouldMoveACountedRecord()
    {
        ObservationRowV1[] connected =
        [
            Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 1).Between(ClientEnd, ServerEnd),
            Transfer(11, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 2).Between(ServerEnd, ClientEnd),
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 10, 100, 3).Between(ClientEnd, ServerEnd),
            Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 10, 200, 4).Between(ServerEnd, ClientEnd),
        ];

        // A creation of the client's PID, witnessed late at a reading before its records already counted, leaves those
        // records bound to no instance: they would move, so the counts decline.
        Assert.Null(Extended(connected, [Lifecycle(15, ObservationKind.Create, 100, 5)]).Activity);

        // After the records already counted, another process's creation or a later record changes nothing counted.
        Assert.NotNull(Extended(connected, [Lifecycle(30, ObservationKind.Create, 300, 6)]).Activity);
        Assert.NotNull(Extended(connected,
            [Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 1, 100, 7).Between(ClientEnd, ServerEnd)]).Activity);

        // A capture-end rundown re-identifies the activity-only server as a rundown-witnessed instance: every record binds
        // to it alike, under a new identity, so its counts move to it and the rundown is its own lifecycle record.
        (ProcessInstanceIndex processes, ProcessActivityIndex? activity) =
            Extended(connected, [Lifecycle(40, ObservationKind.Inventory, 200, 8)]);
        int server = processes.Instances.ToList().FindIndex(instance => instance.ProcessId == 200);
        Assert.Equal(ProcessWitness.Rundown, processes.Instances[server].Witness);
        Assert.Equal([(Mechanism.Tcp, 2L), (Mechanism.ProcessLifecycle, 1L)],
            activity!.MechanismsOf(server, EvidencePolicy.IncludeCorrelated).ToArray());

        // A segment already counted that a later generation no longer names leaves nothing to extend from.
        using var first = new TemporarySession();
        Publish(first.Store, connected);
        using var second = new TemporarySession();
        Publish(second.Store, [Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 1, 100, 9).Between(ClientEnd, ServerEnd)]);
        (SegmentReaderV1[] before, SegmentReaderV1[] beforeFields) = SegmentsOf(first.Store);
        (SegmentReaderV1[] later, SegmentReaderV1[] laterFields) = SegmentsOf(second.Store);
        ProcessInstanceIndex derived = ProcessInstanceIndex.Derive(before, TestClock, beforeFields);
        Assert.Null(ProcessActivityIndex.Derive(before, derived).Extend(later, ProcessInstanceIndex.Derive(later, TestClock, laterFields)));
    }

    [Fact(DisplayName = "R22: a record of a PID's later instance counts only when candidates are admitted, as its binding says")]
    public void ALaterInstancesRecordsNeedCandidates()
    {
        // PID 100 exits and is created again: a record inside the second lifetime could be a late record of the first
        // holder, so it binds as a candidate, while the second instance's own creation binds directly.
        ObservationRowV1[] rows =
        [
            Lifecycle(10, ObservationKind.Create, 100, 1),
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 10, 100, 2).Between(ClientEnd, ServerEnd),
            Lifecycle(30, ObservationKind.Exit, 100, 3),
            Lifecycle(40, ObservationKind.Create, 100, 4),
            Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 10, 100, 5).Between(ClientEnd, ServerEnd),
            Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 10, null, 6).Between(ClientEnd, ServerEnd),
        ];
        using var session = new TemporarySession();
        Publish(session.Store, rows);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(session.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
        ProcessActivityIndex activity = ProcessActivityIndex.Derive(observations, processes);
        int firstHolder = processes.Instances.ToList().FindIndex(instance => instance.ProcessId == 100 && instance.LifecycleEpoch == 1);
        int secondHolder = processes.Instances.ToList().FindIndex(instance => instance.ProcessId == 100 && instance.LifecycleEpoch == 2);

        Assert.Equal([(Mechanism.ProcessLifecycle, 2L), (Mechanism.Tcp, 1L)],
            activity.MechanismsOf(firstHolder, EvidencePolicy.IncludeCorrelated).ToArray());
        Assert.Equal([(Mechanism.ProcessLifecycle, 2L)], activity.MechanismsOf(firstHolder, EvidencePolicy.DirectOnly).ToArray());
        Assert.Equal([(Mechanism.ProcessLifecycle, 1L)], activity.MechanismsOf(secondHolder, EvidencePolicy.IncludeCorrelated).ToArray());
        Assert.Equal([(Mechanism.ProcessLifecycle, 1L), (Mechanism.Tcp, 1L)],
            activity.MechanismsOf(secondHolder, EvidencePolicy.IncludeCandidates).ToArray());
        Assert.Equal(1, activity.RecordsWithoutOwner);
        Assert.Equal(0, activity.RecordsNotBound);
    }

    /// <summary>Asserts two counts over the same instances, in the same order, answer alike under every policy.</summary>
    internal static void AssertSameActivity(ProcessActivityIndex expected, ProcessActivityIndex actual, int instances)
    {
        Assert.Equal(
            (expected.RecordsWithoutOwner, expected.RecordsNotBound),
            (actual.RecordsWithoutOwner, actual.RecordsNotBound));
        foreach (EvidencePolicy policy in Enum.GetValues<EvidencePolicy>())
        {
            for (int instance = 0; instance < instances; instance++)
            {
                Assert.Equal(expected.MechanismsOf(instance, policy).ToArray(), actual.MechanismsOf(instance, policy).ToArray());
            }
        }
    }

    /// <summary>The instances and counts of <paramref name="before"/> extended by a chunk holding <paramref name="added"/>.</summary>
    private static (ProcessInstanceIndex Processes, ProcessActivityIndex? Activity) Extended(
        ObservationRowV1[] before, ObservationRowV1[] added)
    {
        using var first = new TemporarySession();
        Publish(first.Store, before);
        using var second = new TemporarySession();
        Publish(second.Store, added);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(first.Store);
        (SegmentReaderV1[] addedObservations, SegmentReaderV1[] addedFields) = SegmentsOf(second.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
        ProcessActivityIndex activity = ProcessActivityIndex.Derive(observations, processes);
        SegmentReaderV1[] all = [.. observations, .. addedObservations];
        SegmentReaderV1[] allFields = [.. fields, .. addedFields];
        ProcessInstanceIndex extended = processes.Extend(all, TestClock, allFields)!;
        ProcessActivityIndex? extendedActivity = activity.Extend(all, extended);
        if (extendedActivity is not null)
        {
            AssertSameActivity(ProcessActivityIndex.Derive(all, extended), extendedActivity, extended.Instances.Count);
        }

        return (extended, extendedActivity);
    }
}
