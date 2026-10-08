using System.Buffers.Binary;
using System.Text;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.IncrementalDerivationTests;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// A generation's RPC calls and their other ends, kept in an operation index (`contracts/operation-index-v1.md`): read
/// back with the instances of the segments it covers, they answer as the derivation they were written from, and bytes
/// that do not hold such a derivation are refused, never misread (P25, R20).
/// </summary>
public sealed class OperationIndexTests
{
    private static readonly Guid ServiceControl = ServiceControlInterface;
    private static readonly Guid Other = Guid.Parse("12345778-1234-abcd-ef00-0123456789ab");

    [Fact(DisplayName = "I14: an operation index reads back as the calls and other ends it was written from, bound by the instances it is read with, and writes the same bytes again")]
    public void AnIndexReadsBackAsItsDerivation()
    {
        using var session = new TemporarySession();
        Derivation derived = Derive(session.Store);
        byte[] bytes = Write(derived);

        OperationIndex read = OperationIndex.Read(bytes, derived.SessionId, TestClock, derived.Processes);
        Assert.Equal((derived.SessionId, derived.Generation), (read.SessionId, read.DerivedGeneration));
        Assert.Equal(derived.Segments, read.Segments);
        Assert.Equal(derived.FieldSegments, read.FieldSegments);
        Assert.True(read.Covers(derived.Segments, derived.FieldSegments));
        Assert.False(read.Covers([.. derived.Segments.Reverse()], derived.FieldSegments));
        Assert.False(read.Covers(derived.Segments, []));
        Assert.Same(derived.Processes, read.Calls!.Processes);
        Assert.Same(read.Calls, read.Peers!.Calls);

        // Every call state and every other end the rules give is among them, so each is read back.
        string described = Describe(derived.Peers, derived.Observations);
        Assert.Equal(described, Describe(read.Peers, derived.Observations));
        Assert.Equal(Enum.GetValues<RpcCallState>().Order(), derived.Calls.Outcomes().Select(call => call.State).Distinct().Order());
        RpcPeerState[] reached = [.. derived.Calls.Groups.SelectMany(group => Enumerable.Range(0, (int)group.Counts.Calls)
            .Select(position => derived.Peers.PeerOf(group, position, derived.Observations).State)).Distinct().Order()];
        Assert.Equal(
            [RpcPeerState.Served, RpcPeerState.NotCompleted, RpcPeerState.NoSend, RpcPeerState.NotReached],
            reached);

        // The calls are written in the canonical order of their first records, which no binding changes.
        Assert.Equal(bytes, Write(derived with { Calls = read.Calls!, Peers = read.Peers! }));

        // Without ALPC no other end is resolved, and the index says so in two counts.
        using var plain = new TemporarySession();
        Derivation unlinked = Derive(plain.Store, alpc: false);
        OperationIndex none = OperationIndex.Read(Write(unlinked), unlinked.SessionId, TestClock, unlinked.Processes);
        Assert.Equal((0L, 0L), (none.Peers!.AlpcSends, none.Peers.AlpcReceives));
        Assert.Equal(Describe(unlinked.Peers, unlinked.Observations), Describe(none.Peers, unlinked.Observations));
        Assert.All(none.Calls!.Groups, group => Assert.Equal(group.Counts.Calls, none.Peers.CountsOf(group).Unresolved[RpcPeerState.NoAlpcEvidence]));
    }

    [Fact(DisplayName = "I14: an operation index cut short, changed, of another format, rule, session or clock, or contradicting its calls or their instances, is refused, never misread")]
    public void ADamagedIndexIsRefused()
    {
        using var session = new TemporarySession();
        Derivation derived = Derive(session.Store);
        byte[] bytes = Write(derived);
        void Refused(byte[] damaged, string reason, Guid? sessionId = null, ProcessInstanceIndex? processes = null)
        {
            InvalidDataException refused = Assert.Throws<InvalidDataException>(
                () => OperationIndex.Read(damaged, sessionId ?? derived.SessionId, TestClock, processes ?? derived.Processes));
            Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
            Assert.StartsWith("The operation index is not readable: ", refused.Message, StringComparison.Ordinal);
        }

        Refused(bytes[..^1], "ends inside a field");
        Refused([.. bytes, 0], "1 bytes follow its last field");
        Refused(Patched(bytes, 0, "ICATDCKP"u8), "does not begin as an operation index does");
        Refused(Patched(bytes, 10, [1, 0]), "format 1.1");
        Refused(Patched(bytes, 8, [2, 0]), "format 2.0");
        Refused(Replaced(bytes, RpcCallIndex.OperationRule, "rpc-call-operation-v0"), "derived under rpc-call-operation-v0");
        Refused(Replaced(bytes, RpcPeerIndex.PeerRule, "rpc-call-peer-v0"), "rpc-call-peer-v0");
        Refused(Replaced(bytes, ProcessInstanceIndex.BindingRule, ProcessInstanceIndex.BindingRule[..^1] + "0"), "and this build derives under");
        Refused(bytes, "belongs to session", sessionId: Guid.NewGuid());
        Refused(Flipped(bytes, TestClock.Id.Value.ToByteArray()), "another clock or host");
        Refused(Flipped(bytes, TestClock.HostId.Value.ToByteArray()), "another clock or host");
        Refused(Patched(bytes, Find(bytes, derived.SessionId.ToByteArray()) + 16, Int64(0)), "or from no generation");

        // The covered files: observation segments first, each named once, each with its digest.
        int named = Find(bytes, Encoding.UTF8.GetBytes(derived.Segments[0].Name));
        Refused(Patched(bytes, named - 2, [2]), "with the observation segments first");
        Refused(Patched(bytes, named - 2, [3]), "with the observation segments first");
        Refused(Patched(bytes, Find(bytes, Encoding.UTF8.GetBytes(derived.Segments[1].Name)), Encoding.UTF8.GetBytes(derived.Segments[0].Name)),
            "with the observation segments first");
        Refused(Patched(bytes, Find(bytes, Encoding.UTF8.GetBytes(derived.Segments[0].Digest)) + 7, "G"u8), "with the observation segments first");

        // The client's first call, from tick 100 to 120, is the first in canonical order, and the one served. Its fields
        // are at fixed places after its origin and ordinal: side, state and flags, then its start and its stop.
        int first = Find(bytes, Call(origin: 0, ordinal: 10, processId: 400));
        Refused(Patched(bytes, first, Int32(2)), "names no origin the index holds");
        Refused(Patched(bytes, first + 17, [(byte)RpcCallState.OpenAtCaptureEnd]), "holds a state its records contradict");
        Refused(Patched(bytes, first + 17, [(byte)RpcCallState.StartNotObserved]), "holds a state its records contradict");
        Refused(Patched(bytes, first + 18, [(byte)(bytes[first + 18] | 32)]), "holds a state its records contradict");
        Refused(Patched(bytes, first + 16, [9]), "holds a state its records contradict");
        Refused(Patched(bytes, first + 19, Int32(2)), "names no interface or segment");
        Refused(Patched(bytes, first + 31, Int32(derived.Segments.Length)), "names no interface or segment");
        Refused(Patched(bytes, first + 35, Int32(-1)), "names no interface or segment");
        Refused(Patched(bytes, first + 39, Int64(99)), "read before its start");
        Refused(Patched(bytes, first + 47, Int32(-1)), "names no segment the index holds");

        // A stop without its start, and a start still open at capture end: neither is the other, and only a start
        // carries fields, only a stop a status, even where the bytes hold them.
        int stopAlone = Find(bytes, Call(origin: 0, ordinal: 70, processId: 400));
        int open = Find(bytes, Call(origin: 1, ordinal: 73, processId: 500));
        Refused(Patched(bytes, stopAlone + 17, [(byte)RpcCallState.OpenAtCaptureEnd]), "holds a state its records contradict");
        Refused(Patched(bytes, open + 17, [(byte)RpcCallState.StartNotObserved]), "holds a state its records contradict");
        Refused(Inserted(Patched(bytes, stopAlone + 18, [(byte)(bytes[stopAlone + 18] | 4)]), stopAlone + 35, Int64(7)),
            "holds a state its records contradict");
        Refused(Inserted(Patched(bytes, open + 18, [(byte)(bytes[open + 18] | 16)]), open + 39, Int64(0)),
            "holds a state its records contradict");

        // A call moved out of its first record's order, and calls that hold another number of records than counted: the
        // count is where an index keeping no calls ends.
        Refused(Patched(Patched(bytes, first + 23, Int64(10_000)), first + 39, Int64(10_000)), "not in the canonical order of its first record");
        int counted = Write(derived, bytes.Length - 1).Length;
        Assert.Equal(derived.Calls.CallRecords, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(counted)));
        Refused(Patched(bytes, counted, Int64(derived.Calls.CallRecords + 1)), "and it counts");
        Refused(Patched(bytes, counted, Int64(-1)), "fewer than no RPC records");

        // Two interfaces and two origins follow the counts: neither is named twice.
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(counted + 16)));
        Refused(Patched(bytes, counted + 36, bytes.AsSpan(counted + 20, 16)), "is named twice");
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(counted + 52)));
        Refused(Patched(bytes, counted + 80, bytes.AsSpan(counted + 56, 24)), "an origin of call records is named twice");

        // The other ends follow the calls: the first call's is the server call that served it, and a served call that
        // names no server call, or one whose state says it was not served while it names one, contradicts the rule.
        int peers = bytes.Length - (5 * (int)derived.Calls.Totals.Calls);
        Assert.Equal((byte)RpcPeerState.Served, bytes[peers]);
        Refused(Patched(bytes, peers, [(byte)RpcPeerState.NoSend]), "contradicts the call it names");
        Refused(Patched(bytes, peers + 1, Int32(-1)), "contradicts the call it names");
        Refused(Patched(bytes, peers + 1, Int32(0)), "contradicts the call it names");

        // Two client calls naming each other, their server calls left unreached, are no link: a link joins a client call
        // to a server call.
        Assert.Equal(
            [(byte)RpcPeerState.Served, (byte)RpcPeerState.Served, (byte)RpcPeerState.Served, (byte)RpcPeerState.Served],
            new[] { bytes[peers], bytes[peers + 5], bytes[peers + (5 * 4)], bytes[peers + (5 * 5)] });
        byte[] clients = Patched(Patched(bytes, peers + 1, Int32(4)), peers + (5 * 4) + 1, Int32(0));
        clients = Patched(Patched(clients, peers + 5, [(byte)RpcPeerState.NotReached]), peers + 6, Int32(-1));
        clients = Patched(Patched(clients, peers + (5 * 5), [(byte)RpcPeerState.NotReached]), peers + (5 * 5) + 1, Int32(-1));
        Refused(clients, "contradicts the call it names");
        Refused(Patched(bytes, peers + 1, Int32((int)derived.Calls.Totals.Calls)), "is not one the peer rule gives");
        Refused(Patched(bytes, peers, [(byte)RpcPeerState.NoAlpcEvidence]), "is not one the peer rule gives");
        Refused(Patched(bytes, peers, [(byte)RpcPeerState.NotCompleted]), "contradicts the call it names");

        // In canonical order: the client's three calls each before the server call it made, so the second server call,
        // which nothing reached, is the fourth; the third client call, and the server call that served it, the fifth and
        // sixth. Naming the sixth from the first is no link, since the sixth names the fifth; a server call is served
        // exactly when it holds its client, and not reached otherwise; and a client call names no other end unless it
        // was completed, nor a reason for having none unless it was completed.
        Assert.Equal((byte)RpcPeerState.NotReached, bytes[peers + (5 * 3)]);
        Refused(Patched(bytes, peers + 1, Int32(5)), "contradicts the call it names");
        Refused(Patched(bytes, peers + (5 * 3) + 1, Int32(0)), "contradicts the call it names");
        Refused(Patched(bytes, peers + (5 * 3), [(byte)RpcPeerState.Served]), "contradicts the call it names");
        Refused(Patched(bytes, peers + (5 * 3), [(byte)RpcPeerState.NoSend]), "contradicts the call it names");
        int unsent = peers + (5 * StateAt(bytes, peers, RpcPeerState.NoSend));
        Refused(Patched(bytes, unsent, [(byte)RpcPeerState.NotReached]), "contradicts the call it names");
        Refused(Patched(bytes, unsent, [(byte)RpcPeerState.NotCompleted]), "contradicts the call it names");
        Refused(Patched(bytes, peers + (5 * StateAt(bytes, peers, RpcPeerState.NotCompleted)), [(byte)RpcPeerState.NoSend]),
            "contradicts the call it names");
        Refused(Patched(bytes, peers - 16, Int64(-1)), "fewer than no ALPC records");

        // Instances that hold none of its PIDs are not the ones it was derived with: its calls bind to nothing they hold.
        using var stranger = new TemporarySession();
        Publish(stranger.Store, [Lifecycle(1, ObservationKind.Create, 7, 1)]);
        Refused(bytes, "does not occur in the evidence", processes: ProcessInstanceIndex.Derive(SegmentsOf(stranger.Store).Observations, TestClock));
        Assert.Throws<ArgumentException>(
            () => OperationIndex.Read(bytes, derived.SessionId, ClockFor(new ClockId(Guid.NewGuid()), "another"), derived.Processes));
    }

    [Fact(DisplayName = "I14: calls that would take more than an operation index may hold are not kept, and the index says so")]
    public void CallsBeyondTheBoundAreNotKept()
    {
        using var session = new TemporarySession();
        Derivation derived = Derive(session.Store);
        byte[] whole = Write(derived);
        Assert.Equal(whole, Write(derived, whole.Length));
        byte[] bounded = Write(derived, whole.Length - 1);
        OperationIndex read = OperationIndex.Read(bounded, derived.SessionId, TestClock, derived.Processes);
        Assert.Null(read.Calls);
        Assert.Null(read.Peers);
        Assert.True(read.Covers(derived.Segments, derived.FieldSegments));
        Assert.Equal(0, bounded[^1]);
        Assert.Equal(whole[..(bounded.Length - 1)], bounded[..^1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => Write(derived, (long)OperationIndex.MaximumBytes + 1));
        Assert.Throws<ArgumentException>(() => Write(derived with { Segments = [.. derived.Segments.Reverse()] }));
        Assert.Throws<ArgumentException>(() => Write(derived with { FieldSegments = [derived.Segments[0]] }));
        Assert.Throws<ArgumentException>(() => Write(derived with { FieldSegments = [derived.FieldSegments[0] with { Kind = StoreDependencyKind.Index }] }));
        Assert.Throws<ArgumentException>(() => Write(derived with { Calls = read.Calls ?? OperationIndex.Read(whole, derived.SessionId, TestClock, derived.Processes).Calls! }));
    }

    /// <summary>One generation's calls and other ends, derived from its segments, with what an index of them covers.</summary>
    private sealed record Derivation(
        Guid SessionId,
        long Generation,
        SegmentReaderV1[] Observations,
        StoreDependency[] Segments,
        StoreDependency[] FieldSegments,
        ProcessInstanceIndex Processes,
        RpcCallIndex Calls,
        RpcPeerIndex Peers);

    /// <summary>
    /// The linked calls, a stop without its start, a start and a stop without an activity id, a call still open at
    /// capture end, an id reused before its stop, a call to no interface and one carrying its protocol, cut five records to
    /// a segment, so a call's start and stop can lie in different segments. The second process's records come from a
    /// second raw stream.
    /// </summary>
    private static Derivation Derive(SessionStore store, bool alpc = true)
    {
        (ObservationRowV1[] linked, SourceFieldRowV1[] linkedFields) = LinkedRpcCalls();
        ObservationRowV1[] more =
        [
            Lifecycle(3, ObservationKind.Create, 500, 3),
            RpcCall(400, ObservationKind.RequestEnd, Direction.Outbound, 400, 70, Activity(4), status: 0),
            RpcCall(410, ObservationKind.RequestStart, Direction.Outbound, 400, 71, null, ServiceControl),
            RpcCall(415, ObservationKind.RequestEnd, Direction.Outbound, 400, 72, null, status: 3),
            RpcCall(420, ObservationKind.RequestStart, Direction.Outbound, 500, 73, Activity(5), Other) with { RawStreamId = 2 },
            RpcCall(430, ObservationKind.RequestStart, Direction.Outbound, 500, 74, Activity(6), Other) with { RawStreamId = 2 },
            RpcCall(440, ObservationKind.RequestStart, Direction.Outbound, 500, 75, Activity(6), Other) with { RawStreamId = 2 },
            RpcCall(450, ObservationKind.RequestEnd, Direction.Outbound, 500, 76, Activity(6), status: 0) with { RawStreamId = 2 },
            RpcCall(460, ObservationKind.RequestEnd, Direction.Outbound, 500, 77, Activity(6), status: 0) with { RawStreamId = 2 },
            RpcCall(470, ObservationKind.RequestStart, Direction.Inbound, 500, 78, Activity(7)) with { RawStreamId = 2 },
            RpcCall(480, ObservationKind.RequestEnd, Direction.Inbound, 500, 79, Activity(7), status: 5) with { RawStreamId = 2 },
        ];
        ObservationRowV1[] rows = [.. linked.Where(row => alpc || row.Mechanism != Mechanism.Alpc), .. more];
        SourceFieldRowV1[] fields =
        [
            .. linkedFields.Where(field => alpc || field.Field != SourceField.AlpcMessageId),
            Field(more[5], SourceField.RpcProtocolSequence, 10),
        ];
        Publish(store, rows, rowsPerSegment: 5, fields: fields);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = SegmentsOf(store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fieldSegments);
        RpcCallIndex calls = RpcCallIndex.Derive(observations, fieldSegments, processes, TestClock);
        return new(
            store.Current!.SessionId,
            store.Current.Generation,
            observations,
            [.. observations.Select(segment => segment.Published!)],
            [.. fieldSegments.Select(segment => segment.Published!)],
            processes,
            calls,
            RpcPeerIndex.Derive(calls, observations, fieldSegments));
    }

    private static byte[] Write(Derivation derived, long maximumBytes = OperationIndex.MaximumBytes)
    {
        using var written = new MemoryStream();
        long length = OperationIndex.Write(written, derived.SessionId, derived.Generation, derived.Segments, derived.FieldSegments,
            derived.Calls, derived.Peers, maximumBytes);
        Assert.Equal(written.Length, length);
        return written.ToArray();
    }

    /// <summary>Everything the calls and their other ends answer, as one comparable text.</summary>
    private static string Describe(RpcPeerIndex peers, IReadOnlyList<SegmentReaderV1> segments)
    {
        RpcCallIndex calls = peers.Calls;
        var lines = new List<string>
        {
            $"{calls.CallRecords} {calls.OtherRpcRecords} {calls.Totals} {string.Join(",", calls.SegmentNames)}",
            $"{peers.AlpcSends} {peers.AlpcReceives}",
            string.Join(";", calls.Outcomes()),
            string.Join(";", peers.Links()),
            string.Join(";", peers.LinkedRecords(_ => true)),
        };
        foreach (RpcCallGroup group in calls.Groups)
        {
            RpcPeerCounts counts = peers.CountsOf(group);
            lines.Add(group.ToString());
            lines.Add(string.Join(";", calls.RecordsOf(group)));
            lines.Add(string.Join(";", calls.SpansOf(group)));
            lines.Add(string.Join(";", calls.OutcomesOf(group)));
            lines.Add(string.Join(";", calls.CallsOf(group, segments, 0, int.MaxValue)));
            lines.Add(string.Join(";", calls.SpansOf(group).Select(span =>
                calls.PositionOf(group, span.Stream, span.Epoch, span.Ordinal, span.FactKey))));
            lines.Add($"{counts.Calls} {counts.Served} " + string.Join(",", counts.Unresolved.OrderBy(pair => pair.Key)));
            lines.Add(string.Join(";", peers.PeersOf(group)));
            for (int position = 0; position < group.Counts.Calls; position++)
            {
                lines.Add($"{peers.PeerOf(group, position, segments)} {peers.OtherCallOf(group, position)?.Position}");
            }
        }

        return string.Join("\n", lines);
    }

    private static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 9);

    /// <summary>The start of a call as the index holds it: its origin's number, its first record's ordinal and its PID.</summary>
    private static byte[] Call(int origin, ulong ordinal, int processId)
    {
        byte[] call = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(call, origin);
        BinaryPrimitives.WriteUInt64LittleEndian(call.AsSpan(4), ordinal);
        BinaryPrimitives.WriteInt32LittleEndian(call.AsSpan(12), processId);
        return call;
    }

    private static byte[] Inserted(byte[] bytes, int offset, byte[] inserted) => [.. bytes[..offset], .. inserted, .. bytes[offset..]];

    private static byte[] Int32(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Int64(long value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes;
    }

    /// <summary>The place in canonical order of the first call whose other end is <paramref name="state"/>.</summary>
    private static int StateAt(byte[] bytes, int peers, RpcPeerState state)
    {
        for (int place = 0; peers + (5 * place) < bytes.Length; place++)
        {
            if (bytes[peers + (5 * place)] == (byte)state)
            {
                return place;
            }
        }

        throw new InvalidOperationException($"No call's other end is {state}.");
    }

    private static int Find(byte[] bytes, byte[] pattern)
    {
        int at = bytes.AsSpan().IndexOf(pattern);
        Assert.True(at >= 0, "The pattern is not in the index.");
        Assert.Equal(-1, bytes.AsSpan(at + 1).IndexOf(pattern));
        return at;
    }

    private static byte[] Patched(byte[] bytes, int offset, ReadOnlySpan<byte> with)
    {
        byte[] patched = [.. bytes];
        with.CopyTo(patched.AsSpan(offset));
        return patched;
    }

    /// <summary>The bytes with one string replaced by another of the same length, where it occurs once.</summary>
    private static byte[] Replaced(byte[] bytes, string from, string to)
    {
        Assert.Equal(from.Length, to.Length);
        return Patched(bytes, Find(bytes, Encoding.UTF8.GetBytes(from)), Encoding.UTF8.GetBytes(to));
    }

    /// <summary>The bytes with the first byte of a pattern that occurs once changed.</summary>
    private static byte[] Flipped(byte[] bytes, byte[] pattern)
    {
        int at = Find(bytes, pattern);
        return Patched(bytes, at, [(byte)(bytes[at] ^ 0xFF)]);
    }
}
