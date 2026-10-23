using System.Text;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// What a checkpoint keeps of the records by owner (format 1.5, revision 738): each incarnation's and each instance's records
/// by the instance they bind to and how strongly, with the transport bytes they measured. Read back, they are the counts
/// written, so a generation opened from the checkpoint ranks by bytes and peers without its rows; what contradicts the
/// records they count, or the state they describe, is refused.
/// </summary>
public sealed class OwnerCountsCheckpointTests
{
    /// <summary>The bytes of one of the activity's tallies: an instance, a mechanism, and its direct and bound records.</summary>
    private const int Tally = 4 + 2 + 8 + 8;

    private static readonly Guid Session = Guid.Parse("7a7e5f3e-0000-4000-8000-000000000738");

    [Fact(DisplayName = "I14: a checkpoint keeps every incarnation's and instance's records by owner, read back as they were counted, and one of format 1.4 keeps none")]
    public void RecordsByOwnerReadBackAsCounted()
    {
        var random = new Random(738);
        int compared = 0;
        for (int trial = 0; trial < 12; trial++)
        {
            using var session = new TemporarySession();
            foreach ((ObservationRowV1[] rows, SourceFieldRowV1[] fields) in RandomCaptures.Chunks(random))
            {
                _ = Publish(session.Store, rows, rowsPerSegment: random.Next(1, rows.Length + 1), fields: fields);
            }

            (SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = IncrementalDerivationTests.SegmentsOf(session.Store);
            ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fieldSegments);
            TransportRelationIndex relations = TransportRelationIndex.Derive(observations, processes);
            ProcessActivityIndex activity = ProcessActivityIndex.Derive(observations, processes);
            byte[] bytes = Written(processes, relations, activity);
            DerivationCheckpoint read = DerivationCheckpoint.Read(bytes, Session, TestClock);
            Assert.True(read.Relations.OwnersKept && read.Activity!.OwnersKept);
            Assert.Equal(Owners(relations), Owners(read.Relations));
            Assert.Equal(Owners(activity), Owners(read.Activity));
            Assert.Equal(relations.Relations.Select(relation => Owners(relations.OwnersOf(relation))),
                read.Relations.Relations.Select(relation => Owners(read.Relations.OwnersOf(relation))));

            DerivationCheckpoint earlier = DerivationCheckpoint.Read(EarlierCheckpoints.WithoutOwners(bytes), Session, TestClock);
            Assert.False(earlier.Relations.OwnersKept || earlier.Activity!.OwnersKept);
            Assert.Equal(EarlierCheckpoints.WithoutOwners(bytes), Written(earlier.Processes, earlier.Relations, earlier.Activity!));
            compared++;
        }

        Assert.Equal(12, compared);
    }

    [Fact(DisplayName = "I14: a checkpoint's records by owner that are not their incarnation's records, name no instance, an undefined strength or another instance, are out of order, contradict whether an incarnation names two PIDs, or do not take the bytes they say are refused")]
    public void ContradictoryRecordsByOwnerAreRefused()
    {
        byte[] bytes = TwoInstances();
        Layout layout = Layout.Of(bytes);

        // The one incarnation's one owner: its records, its instance, its strength, the transport bytes it measured - below
        // none, or beside a refused sum - and its PIDs, which name none.
        Refused(bytes, layout.FirstOwner + 4 + 1, BitConverter.GetBytes(3L), "are not its records");
        Refused(bytes, layout.FirstOwner, BitConverter.GetBytes(9), "names no instance");
        Refused(bytes, layout.FirstOwner + 4, [200], "undefined strength");
        int sums = layout.FirstOwner + 4 + 1 + 8;
        Refused(bytes, sums + 1 + 8, BitConverter.GetBytes(-1L), "below none");
        Refused(bytes, sums, [1], "beside a refusal");
        Refused(bytes, layout.Incarnation, BitConverter.GetBytes(1), "contradict whether its records name two");

        // The first instance's two owners - its lifecycle record's, directly, and its transfers', as correlated - in the
        // wrong order or held twice, and the second instance's own owner named as the first.
        byte[] reversed = [.. bytes];
        reversed[layout.InstanceOwner + Layout.Owner + 4] = (byte)RelationStrength.Direct;
        Refused(reversed, layout.InstanceOwner + 4, [(byte)RelationStrength.Correlated], "out of order");
        Refused(bytes, layout.InstanceOwner + 4, [(byte)RelationStrength.Correlated], "out of order");
        Refused(bytes, layout.SecondInstanceOwner, BitConverter.GetBytes(0), "name another instance");

        // The instance's records named for a position past the instances, or naming one in their tally; and the records
        // binding to none naming an instance, an owner written into their tally.
        Refused(bytes, layout.Instances + 4, BitConverter.GetBytes(9), "name another instance, none, or are out of order");
        Refused(bytes, layout.InstanceOwner, BitConverter.GetBytes(9), "names no instance");
        byte[] owner = [.. BitConverter.GetBytes(0), (byte)RelationStrength.Correlated, .. BitConverter.GetBytes(0L), .. new byte[1 + (9 * 8)]];
        byte[] named = [.. bytes[..(layout.Unowned + 1)], .. BitConverter.GetBytes(1), .. owner, .. bytes[(layout.Unowned + 1 + 4)..^4],
            .. BitConverter.GetBytes((uint)(layout.Length + owner.Length))];
        Assert.Contains("binding to no instance name one", Read(named).Message, StringComparison.Ordinal);

        // The section's length.
        Refused(bytes, bytes.Length - 4, BitConverter.GetBytes((uint)layout.Length + 1), "do not take the bytes");
    }

    [Fact(DisplayName = "I14: a checkpoint's records by owner of an instance are the records counted for it, at the strengths it binds them at, and every instance has both; otherwise the checkpoint is refused")]
    public void AnInstancesRecordsByOwnerAreItsCountedRecords()
    {
        byte[] bytes = TwoInstances();
        Layout layout = Layout.Of(bytes);

        // The first instance's lifecycle record counted among its records, its transfers one more than its counts hold, or
        // bound as candidates though PID 700 was never reused; the second's lifecycle record bound as conflicting; and records
        // binding to none in an instance's.
        int transfers = layout.InstanceOwner + Layout.Owner;
        Refused(bytes, layout.InstanceOwner + 4 + 1, BitConverter.GetBytes(1L), "are not the records counted for it");
        Refused(bytes, transfers + 4 + 1, BitConverter.GetBytes(3L), "are not the records counted for it");
        Refused(bytes, transfers + 4, [(byte)RelationStrength.Candidate], "are not the records counted for it");
        Refused(bytes, layout.SecondInstanceOwner + 4, [(byte)RelationStrength.Conflicting], "are not the records counted for it");
        Refused(bytes, layout.InstanceOwner + (2 * Layout.Owner), BitConverter.GetBytes(1L), "are not the records counted for it");

        // The second instance given no records by owner: its entry gone from the section, which is that much shorter.
        int entry = layout.SecondInstanceOwner - 1 - 4 - 4;
        byte[] dropped = [.. bytes[..layout.Instances], .. BitConverter.GetBytes(1), .. bytes[(layout.Instances + 4)..entry],
            .. bytes[layout.Unowned..^4], .. BitConverter.GetBytes((uint)(layout.Length - (layout.Unowned - entry)))];
        Assert.Contains("whose records are counted has none by owner", Read(dropped).Message, StringComparison.Ordinal);

        // The second instance's one tally, of its lifecycle record, gone from the counts, which then count none of its records.
        int tallies = Tallies(bytes);
        Assert.Equal(3, BitConverter.ToInt32(bytes, tallies));
        int third = tallies + 4 + (2 * Tally);
        byte[] uncounted = [.. bytes[..tallies], .. BitConverter.GetBytes(2), .. bytes[(tallies + 4)..third], .. bytes[(third + Tally)..]];
        Assert.Contains("has no records counted for it", Read(uncounted).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A checkpoint of two instances: PID 700's, which sends and receives over one connection to another host, and PID
    /// 800's, whose creation is its only record.
    /// </summary>
    private static byte[] TwoInstances()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 700, 1),
            Lifecycle(2, ObservationKind.Create, 800, 4),
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 100, 700, 2).Between("192.168.1.10:52000", "203.0.113.1:443"),
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, null, 700, 3).Between("192.168.1.10:52000", "203.0.113.1:443"),
        ]);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = IncrementalDerivationTests.SegmentsOf(session.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
        return Written(processes, TransportRelationIndex.Derive(observations, processes), ProcessActivityIndex.Derive(observations, processes));
    }

    private static byte[] Written(ProcessInstanceIndex processes, TransportRelationIndex relations, ProcessActivityIndex activity)
    {
        using var written = new MemoryStream();
        _ = DerivationCheckpoint.Write(written, Session, 2, processes, relations, activity);
        return written.ToArray();
    }

    private static void Refused(byte[] bytes, int at, byte[] value, string reason)
    {
        byte[] changed = [.. bytes];
        value.CopyTo(changed, at);
        Assert.Contains(reason, Read(changed).Message, StringComparison.Ordinal);
    }

    private static InvalidDataException Read(byte[] bytes) =>
        Assert.Throws<InvalidDataException>(() => DerivationCheckpoint.Read(bytes, Session, TestClock));

    /// <summary>
    /// Where the activity's tallies of each instance's records by mechanism begin, at their count: after the rule they
    /// were counted under, the records naming no owner and each PID's span.
    /// </summary>
    private static int Tallies(byte[] bytes)
    {
        byte[] rule = Encoding.UTF8.GetBytes(ProcessActivityIndex.CountRule);
        int spans = bytes.AsSpan().IndexOf(rule) + rule.Length + 8;
        return spans + 4 + (BitConverter.ToInt32(bytes, spans) * (4 + (3 * 8)));
    }

    private static string Owners(TransportRelationIndex relations) => string.Join(Environment.NewLine,
        relations.OwnersByPeer()!.Select(entry => $"{entry.Peer.Instance}/{entry.Peer.Strength}: {Owners(entry.Owners)}").Order());

    private static string Owners(ProcessActivityIndex activity) => Owners(activity.Owners());

    private static string Owners(IReadOnlyList<RecordOwners>? owners) => owners is null ? "none" : string.Join("; ", owners
        .Select(owner => $"{owner.Instance}/{owner.Strength} {owner.Records} {owner.Bytes.SentBytes},{owner.Bytes.SentMeasured},"
            + $"{owner.Bytes.SentUnmeasured} {owner.Bytes.ReceivedBytes},{owner.Bytes.ReceivedMeasured},{owner.Bytes.ReceivedUnmeasured} "
            + $"{owner.Bytes.OtherBytes},{owner.Bytes.OtherMeasured},{owner.Bytes.OtherUnmeasured} {owner.Bytes.Refused}")
        .Order());

    /// <summary>
    /// Where the records by owner of a checkpoint holding one incarnation and two instances lie: the incarnation's PIDs, its
    /// first owner, the count of instances, each instance's first owner, the tally of records binding to none, and the
    /// section's length.
    /// </summary>
    private sealed record Layout(
        int Incarnation, int FirstOwner, int Instances, int InstanceOwner, int SecondInstanceOwner, int Unowned, int Length)
    {
        public const int Owner = 4 + 1 + 8 + Sums;
        private const int Sums = 1 + (9 * 8);

        public static Layout Of(byte[] bytes)
        {
            int length = (int)BitConverter.ToUInt32(bytes, bytes.Length - 4);
            int at = bytes.Length - 4 - length;
            int incarnation = at;
            at = Skip(bytes, at + 4 + (4 * BitConverter.ToInt32(bytes, at)), out int firstOwner);
            int instances = at;
            at = Skip(bytes, at + 4 + 4, out int instanceOwner);
            at = Skip(bytes, at + 4, out int secondInstanceOwner);
            return new(incarnation, firstOwner, instances, instanceOwner, secondInstanceOwner, at, length);
        }

        /// <summary>Passes over one tally starting at <paramref name="at"/>, noting where its first owner lies.</summary>
        private static int Skip(byte[] bytes, int at, out int firstOwner)
        {
            int owners = BitConverter.ToInt32(bytes, at + 1);
            firstOwner = at + 1 + 4;
            at = firstOwner + (owners * Owner) + 8;
            return at + 1 + (bytes[at] == 1 ? Sums : 0);
        }
    }
}
