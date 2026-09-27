using System.Text;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.IncrementalDerivationTests;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// A generation's derivation checkpoint lets opening a session build its instances and relations without reading every
/// record (`contracts/derivation-checkpoint-v1.md`, §12.1 S1). Read back, it must answer exactly as the derivation it
/// was written from, extend exactly as that derivation would, and refuse bytes it cannot read rather than guess.
/// </summary>
public sealed class DerivationCheckpointTests
{
    [Theory(DisplayName = "I14: a derivation read back from its checkpoint answers and extends as the derivation it was written from")]
    [InlineData(3)]
    [InlineData(29)]
    [InlineData(20_260_927)]
    public void ReadBackAnswersAndExtendsAsWritten(int seed)
    {
        var random = new Random(seed);
        int extended = 0;
        int activityExtended = 0;
        for (int trial = 0; trial < 10; trial++)
        {
            // Each chunk is a generation of one session, as a live capture publishes them, so every file name is unique.
            List<(ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)> chunks = RandomChunks(random);
            using var session = new TemporarySession();
            var published = new List<(SegmentReaderV1[] Observations, SegmentReaderV1[] Fields)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach ((ObservationRowV1[] rows, SourceFieldRowV1[] fieldRows) in chunks)
            {
                Publish(session.Store, rows, rowsPerSegment: random.Next(1, rows.Length + 1), fields: fieldRows);
                (SegmentReaderV1[] observationsNow, SegmentReaderV1[] fieldsNow) = SegmentsOf(session.Store);
                published.Add((
                    [.. observationsNow.Where(segment => seen.Add(segment.Published!.Name))],
                    [.. fieldsNow.Where(segment => seen.Add(segment.Published!.Name))]));
            }

            int covered = random.Next(1, published.Count + 1);
            SegmentReaderV1[] observations = [.. published.Take(covered).SelectMany(chunk => chunk.Observations)];
            SegmentReaderV1[] fields = [.. published.Take(covered).SelectMany(chunk => chunk.Fields)];
            ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
            TransportRelationIndex relations = TransportRelationIndex.Derive(observations, processes);
            ProcessActivityIndex activity = ProcessActivityIndex.Derive(observations, processes);
            byte[] bytes = Checkpoint(processes, relations, activity);

            DerivationCheckpoint read = DerivationCheckpoint.Read(bytes, Session, TestClock);
            Assert.True(read.Covers(observations, fields));
            Assert.Equal(2, read.DerivedGeneration);
            AssertSameProcesses(processes, read.Processes, observations);
            AssertSameRelations(relations, read.Relations, observations);
            Assert.NotNull(read.Activity);
            ProcessActivityTests.AssertSameActivity(activity, read.Activity, processes.Instances.Count);

            // Nothing is lost reading it back, and nothing depends on the order the segments were read in.
            Assert.Equal(bytes, Checkpoint(read.Processes, read.Relations, read.Activity));
            SegmentReaderV1[] backwards = [.. observations.Reverse()];
            ProcessInstanceIndex reversed = ProcessInstanceIndex.Derive(backwards, TestClock, [.. fields.Reverse()]);
            Assert.Equal(bytes, Checkpoint(
                reversed, TransportRelationIndex.Derive(backwards, reversed), ProcessActivityIndex.Derive(backwards, reversed)));

            if (covered == published.Count)
            {
                continue;
            }

            // The later chunks extend the read-back derivation exactly as they extend the one it was written from:
            // to a full derivation, or to a decline where a late record would change a decision already made.
            SegmentReaderV1[] allObservations = [.. published.SelectMany(chunk => chunk.Observations)];
            SegmentReaderV1[] allFields = [.. published.SelectMany(chunk => chunk.Fields)];
            Assert.False(read.Covers(allObservations, allFields));
            ProcessInstanceIndex full = ProcessInstanceIndex.Derive(allObservations, TestClock, allFields);
            TransportRelationIndex fullRelations = TransportRelationIndex.Derive(allObservations, full);
            ProcessInstanceIndex next = read.Processes.Extend(allObservations, TestClock, allFields)
                ?? throw new Xunit.Sdk.XunitException("Every covered segment is still offered, so the instances must extend.");
            AssertSameProcesses(full, next, allObservations);
            TransportRelationIndex? nextRelations = read.Relations.Extend(allObservations, next);
            ProcessInstanceIndex written = processes.Extend(allObservations, TestClock, allFields)!;
            Assert.Equal(relations.Extend(allObservations, written) is null, nextRelations is null);
            ProcessActivityIndex fullActivity = ProcessActivityIndex.Derive(allObservations, full);
            ProcessActivityIndex? nextActivity = read.Activity.Extend(allObservations, next);
            Assert.Equal(activity.Extend(allObservations, written) is null, nextActivity is null);
            if (nextActivity is not null)
            {
                activityExtended++;
                ProcessActivityTests.AssertSameActivity(fullActivity, nextActivity, full.Instances.Count);
            }

            if (nextRelations is not null)
            {
                extended++;
                AssertSameRelations(fullRelations, nextRelations, allObservations);
                if (nextActivity is not null)
                {
                    Assert.Equal(Checkpoint(full, fullRelations, fullActivity), Checkpoint(next, nextRelations, nextActivity));
                }
            }
        }

        Assert.True(extended > 0, "No trial extended a read-back checkpoint.");
        Assert.True(activityExtended > 0, "No trial extended a read-back checkpoint's counts.");
    }

    [Fact(DisplayName = "I14: a checkpoint cut short, changed, of another format, rule, session or clock is refused, never misread")]
    public void DamagedOrForeignCheckpointsAreRefused()
    {
        List<(ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)> chunks = RandomChunks(new Random(7));
        using var session = new TemporarySession();
        Publish(session.Store, [.. chunks.SelectMany(chunk => chunk.Rows)], fields: [.. chunks.SelectMany(chunk => chunk.Fields)]);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(session.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
        TransportRelationIndex relations = TransportRelationIndex.Derive(observations, processes);
        ProcessActivityIndex activity = ProcessActivityIndex.Derive(observations, processes);
        byte[] bytes = Checkpoint(processes, relations, activity);
        _ = DerivationCheckpoint.Read(bytes, Session, TestClock);

        // A minor-0 checkpoint, written before revision 166, ends before the counts: it is read without them, and the
        // instances and relations it holds answer as before.
        byte[] older = EarlierCheckpoints.MinorZero(bytes, observations, processes, activity);
        DerivationCheckpoint minorZero = DerivationCheckpoint.Read(older, Session, TestClock);
        Assert.Null(minorZero.Activity);
        Assert.True(minorZero.Covers(observations, fields));
        AssertSameProcesses(processes, minorZero.Processes, observations);
        AssertSameRelations(relations, minorZero.Relations, observations);
        _ = Assert.Throws<InvalidDataException>(() => DerivationCheckpoint.Read((byte[])[.. older[..^1]], Session, TestClock));

        for (int length = 0; length < bytes.Length; length++)
        {
            _ = Assert.Throws<InvalidDataException>(() => DerivationCheckpoint.Read(bytes.AsMemory(0, length), Session, TestClock));
        }

        _ = Assert.Throws<InvalidDataException>(() => DerivationCheckpoint.Read((byte[])[.. bytes, 0], Session, TestClock));

        // A changed byte is refused or reads as some checkpoint, and no other exception escapes. Most changes to a value,
        // such as a reading or an ordinal, still parse: the digest its generation records refuses those (§4).
        int refused = 0;
        for (int index = 0; index < bytes.Length; index++)
        {
            byte[] changed = [.. bytes];
            changed[index] ^= 0xFF;
            try
            {
                _ = DerivationCheckpoint.Read(changed, Session, TestClock);
            }
            catch (InvalidDataException)
            {
                refused++;
            }
        }

        Assert.InRange(refused, 1, bytes.Length);

        byte[] newer = [.. bytes];
        newer[10] = 2;
        Assert.Contains("format 1.2", Assert.Throws<InvalidDataException>(() => DerivationCheckpoint.Read(newer, Session, TestClock)).Message,
            StringComparison.Ordinal);
        byte[] rule = [.. bytes];
        int at = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(ProcessInstanceIndex.BindingRule));
        rule[at + ProcessInstanceIndex.BindingRule.Length - 1] = (byte)'9';
        Assert.Contains("derived under process-binding-v9", Assert.Throws<InvalidDataException>(
            () => DerivationCheckpoint.Read(rule, Session, TestClock)).Message, StringComparison.Ordinal);
        byte[] counted = [.. bytes];
        int countAt = bytes.AsSpan().LastIndexOf(Encoding.ASCII.GetBytes(ProcessActivityIndex.CountRule));
        counted[countAt + ProcessActivityIndex.CountRule.Length - 1] = (byte)'9';
        Assert.Contains("made under process-activity-v9", Assert.Throws<InvalidDataException>(
            () => DerivationCheckpoint.Read(counted, Session, TestClock)).Message, StringComparison.Ordinal);
        Assert.Contains("belongs to session", Assert.Throws<InvalidDataException>(
            () => DerivationCheckpoint.Read(bytes, Guid.NewGuid(), TestClock)).Message, StringComparison.Ordinal);
        _ = Assert.Throws<InvalidDataException>(() => DerivationCheckpoint.Read(bytes, Session, ClockFor(Clock, "another-host")));
        _ = Assert.Throws<InvalidDataException>(
            () => DerivationCheckpoint.Read(bytes, Session, ClockFor(new ClockId(Guid.NewGuid()), "session-metrics-tests")));
    }

    [Fact(DisplayName = "I14: a field a later chunk repeats is refused after a checkpoint, as by a derivation of every chunk")]
    public void ARepeatedFieldIsRefusedAfterACheckpoint()
    {
        ObservationRowV1 created = Lifecycle(10, ObservationKind.Create, 100, 1);
        SourceFieldRowV1 sequence = Field(created, SourceField.ProcessStartSequence, 5_000);
        using var session = new TemporarySession();
        Publish(session.Store, [created], fields: [sequence]);
        (SegmentReaderV1[] first, SegmentReaderV1[] firstFields) = SegmentsOf(session.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(first, TestClock, firstFields);
        DerivationCheckpoint read = DerivationCheckpoint.Read(
            Checkpoint(processes, TransportRelationIndex.Derive(first, processes), ProcessActivityIndex.Derive(first, processes)),
            Session, TestClock);

        // The checkpoint keeps which fields each record carried, so a later chunk carrying one again is caught.
        Publish(session.Store, [Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 2)], fields: [sequence]);
        (SegmentReaderV1[] all, SegmentReaderV1[] allFields) = SegmentsOf(session.Store);
        _ = Assert.Throws<InvalidDataException>(() => ProcessInstanceIndex.Derive(all, TestClock, allFields));
        _ = Assert.Throws<InvalidDataException>(() => processes.Extend(all, TestClock, allFields));
        _ = Assert.Throws<InvalidDataException>(() => read.Processes.Extend(all, TestClock, allFields));
    }

    [Fact(DisplayName = "I14: only instances and the relations derived with them are written, and a generation names one checkpoint")]
    public void OnlyMatchingDerivationsAreWritten()
    {
        List<(ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)> chunks = RandomChunks(new Random(11));
        using var session = new TemporarySession();
        Publish(session.Store, [.. chunks.SelectMany(chunk => chunk.Rows)], fields: [.. chunks.SelectMany(chunk => chunk.Fields)]);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(session.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
        ProcessInstanceIndex other = ProcessInstanceIndex.Derive(observations, TestClock, fields);
        TransportRelationIndex relations = TransportRelationIndex.Derive(observations, other);
        using var destination = new MemoryStream();
        _ = Assert.Throws<ArgumentException>(() => DerivationCheckpoint.Write(
            destination, Session, 2, processes, relations, ProcessActivityIndex.Derive(observations, processes)));

        // Counts made with other instances, or from other segments than the relations, are refused too.
        TransportRelationIndex matching = TransportRelationIndex.Derive(observations, processes);
        _ = Assert.Throws<ArgumentException>(() => DerivationCheckpoint.Write(
            destination, Session, 2, processes, matching, ProcessActivityIndex.Derive(observations, other)));
        _ = Assert.Throws<ArgumentException>(() => DerivationCheckpoint.Write(
            destination, Session, 2, processes, matching, ProcessActivityIndex.Derive(observations[..^1], processes)));
        Assert.Equal(0, destination.Length);

        Assert.Equal("derivation-checkpoint-0000000012.bin", DerivationCheckpoint.FileNameFor(12));
        StoreDependency checkpoint = new(DerivationCheckpoint.FileNameFor(12), StoreDependencyKind.Index, 10, "sha256:" + new string('0', 64));
        Assert.True(DerivationCheckpoint.IsCheckpoint(checkpoint));
        Assert.False(DerivationCheckpoint.IsCheckpoint(checkpoint with { Kind = StoreDependencyKind.Segment }));
        Assert.False(DerivationCheckpoint.IsCheckpoint(checkpoint with { Name = "derivation-checkpoint-12.bin" }));
    }

    private static byte[] Checkpoint(
        ProcessInstanceIndex processes, TransportRelationIndex relations, ProcessActivityIndex activity)
    {
        using var destination = new MemoryStream();
        long written = DerivationCheckpoint.Write(destination, Session, 2, processes, relations, activity);
        Assert.Equal(written, destination.Length);
        return destination.ToArray();
    }
}
