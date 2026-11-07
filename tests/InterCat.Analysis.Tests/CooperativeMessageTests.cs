using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// The cooperative SDK's logical messages (ADR-104): a message is its channel's identity, epoch and sequence, published once
/// and consumed once by each reader; a channel's epoch is named by its first opening; a record names a message only by the
/// values it carries, and only the SDK's own provider's records are read.
/// </summary>
public sealed class CooperativeMessageTests
{
    private static readonly Guid Frames = Guid.Parse("6f1c8f4e-2b8e-4a40-9f7c-0d6a7f3e5a11");
    private static readonly Guid Audio = Guid.Parse("1d2c3b4a-5f6e-4d8c-9b0a-112233445566");

    [Fact(DisplayName = "§15: a message is its channel's epoch and sequence, published once and consumed once by each reader: three readers' consumptions of one publication are one message, each bound to its process, and the channel is named by its opening")]
    public void ThreeReadersConsumeOneMessage()
    {
        var sdk = new Sdk();
        sdk.Open(10, 100, Frames, epoch: 1, "frames", kind: 3);
        sdk.Publish(20, 100, Frames, 1, sequence: 1, length: 4_096, copied: -1, sourceTicks: 9_020);
        sdk.Open(25, 200, Frames, epoch: 1, "frames as a reader names it", kind: 3);
        sdk.Consume(30, 200, Frames, 1, sequence: 1, consumer: 1, length: 4_096, sourceTicks: 9_030);
        sdk.Consume(31, 201, Frames, 1, sequence: 1, consumer: 2, length: 4_096, sourceTicks: 9_031);
        sdk.Consume(32, 202, Frames, 1, sequence: 1, consumer: 3, length: 4_096, sourceTicks: 9_032);
        sdk.Publish(40, 100, Frames, 1, sequence: 2, length: 100, copied: 100, sourceTicks: 9_040);
        sdk.Consume(50, 200, Frames, 1, sequence: 2, consumer: 1, length: 100, sourceTicks: 9_050);
        sdk.Close(60, 100, Frames, 1);
        using var session = new TemporarySession();
        CooperativeMessageIndex index = sdk.Derive(session.Store);

        CooperativeChannel channel = Assert.Single(index.Channels);
        // Its first opening names it, however many processes open it after.
        Assert.Equal((Frames, 1L, "frames", (long?)3, 2, 1), (channel.Channel, channel.Epoch, channel.Name, channel.Kind, channel.Openings, channel.Closings));
        Assert.Equal((2, 2, 0, 0, 0), (channel.Messages, channel.Consumed, channel.Unconsumed, channel.PublicationUnseen, channel.Conflicting));
        Assert.Equal((3, 4L, 4_196L), (channel.Readers, channel.Consumptions, channel.PublishedBytes));

        CooperativeMessage[] messages = [.. index.MessagesOf(channel)];
        Assert.Equal([1L, 2L], messages.Select(message => message.Sequence));
        CooperativeMessage first = messages[0];
        Assert.Equal((CooperativeMessageState.Consumed, 1, (long?)null), (first.State, first.Publications, first.CopiedLength));
        Assert.Equal((100, (long?)4_096, (long?)9_020), (first.Publication!.ProcessId, first.Publication.Length, first.Publication.SourceTicks));
        Assert.Equal((200L, (long?)200_000), (first.Publication.Ticks, first.Publication.SessionNanoseconds));
        Assert.Equal([(1L, 200), (2L, 201), (3L, 202)], first.Consumptions.Select(consumption => (consumption.Consumer, consumption.Record.ProcessId)));
        Assert.Equal((long?)9_031, first.Consumptions[1].Record.SourceTicks);

        // Each record's process binds to the instance its lifecycle records began.
        Assert.True(first.Publication.Process.IsBound);
        Assert.All(first.Consumptions, consumption => Assert.True(consumption.Record.Process.IsBound));
        Assert.NotEqual(first.Consumptions[0].Record.Process.Instance, first.Consumptions[1].Record.Process.Instance);

        // A copy the publisher knew is kept beside the length.
        Assert.Equal((CooperativeMessageState.Consumed, (long?)100), (messages[1].State, messages[1].CopiedLength));
        Assert.Equal(0, index.WithoutMessage);
    }

    [Fact(DisplayName = "§15: a publication no reader consumed stays open, a consumption whose publication was not recorded says so, and a message published twice or consumed twice by one reader conflicts, none of its records chosen")]
    public void EachMessageSaysWhatBecameOfIt()
    {
        var sdk = new Sdk();
        sdk.Publish(10, 100, Frames, 1, sequence: 1, length: 8);
        sdk.Consume(20, 200, Frames, 1, sequence: 2, consumer: 1, length: 8);
        sdk.Publish(30, 100, Frames, 1, sequence: 3, length: 8);
        sdk.Publish(31, 100, Frames, 1, sequence: 3, length: 9);
        sdk.Publish(40, 100, Frames, 1, sequence: 4, length: 8);
        sdk.Consume(41, 200, Frames, 1, sequence: 4, consumer: 1, length: 8);
        sdk.Consume(42, 200, Frames, 1, sequence: 4, consumer: 1, length: 8);
        using var session = new TemporarySession();
        CooperativeMessageIndex index = sdk.Derive(session.Store);

        Assert.Equal(
            [
                (1L, CooperativeMessageState.Unconsumed, 1, 0), (2L, CooperativeMessageState.PublicationUnseen, 0, 1),
                (3L, CooperativeMessageState.Conflicting, 2, 0), (4L, CooperativeMessageState.Conflicting, 1, 2),
            ],
            index.Messages.Select(message => (message.Sequence, message.State, message.Publications, message.Consumptions.Count)));
        Assert.Null(index.Messages[1].Publication);

        // A conflicting message keeps its first publication as the one it names, choosing none over another.
        Assert.Equal((long?)8, index.Messages[2].Publication!.Length);

        // A channel no opening was recorded of is known from its messages alone.
        CooperativeChannel channel = Assert.Single(index.Channels);
        Assert.Equal(((string?)null, (long?)null, 0), (channel.Name, channel.Kind, channel.Openings));
        Assert.Equal((4, 0, 1, 1, 2), (channel.Messages, channel.Consumed, channel.Unconsumed, channel.PublicationUnseen, channel.Conflicting));
    }

    [Fact(DisplayName = "§15: a channel made again or a wrapped sequence is a new epoch: the same sequence in another epoch, or of another channel, is another message, and each channel's epoch is its own")]
    public void AnotherEpochIsAnotherChannel()
    {
        var sdk = new Sdk();
        sdk.Open(5, 100, Frames, epoch: 1, "frames", kind: 3);
        sdk.Publish(10, 100, Frames, 1, sequence: 1, length: 8);
        sdk.Open(15, 100, Frames, epoch: 2, "frames again", kind: 3);
        sdk.Publish(20, 100, Frames, 2, sequence: 1, length: 16);
        sdk.Publish(30, 100, Audio, 1, sequence: 1, length: 32);
        sdk.Open(40, 300, Audio, epoch: 7, "idle", kind: 1);
        using var session = new TemporarySession();
        CooperativeMessageIndex index = sdk.Derive(session.Store);

        Assert.Equal(
            [(Audio, 1L, (string?)null, 1), (Audio, 7L, "idle", 0), (Frames, 1L, "frames", 1), (Frames, 2L, "frames again", 1)],
            index.Channels.Select(channel => (channel.Channel, channel.Epoch, channel.Name, channel.Messages)));
        Assert.Equal([32L, 8L, 16L], index.Messages.Select(message => message.Publication!.Length!.Value));
        Assert.Empty(index.MessagesOf(index.Channels[1]));
    }

    [Fact(DisplayName = "R3: a record whose channel, epoch, sequence or reader the capture did not keep names no message and is counted apart, and the content fixture's records, numbered from 1 too, are not the SDK's")]
    public void ARecordNamesAMessageOnlyByWhatItCarries()
    {
        var sdk = new Sdk();
        sdk.Publish(10, 100, Frames, 1, sequence: 1, length: 8);
        sdk.Publish(11, 100, channel: null, 1, sequence: 2, length: 8);
        sdk.Publish(12, 100, Frames, epoch: null, sequence: 3, length: 8);
        sdk.Publish(13, 100, Frames, 1, sequence: null, length: 8);
        sdk.Consume(14, 200, Frames, 1, sequence: 1, consumer: null, length: 8);
        sdk.Open(15, 100, channel: null, epoch: 1, "lost", kind: 0);
        sdk.Fixture(16, 100, eventId: CooperativeSdk.ChannelOpened);
        sdk.Fixture(17, 100, eventId: CooperativeSdk.MessagePublished);
        using var session = new TemporarySession();
        CooperativeMessageIndex index = sdk.Derive(session.Store);

        Assert.Equal(5, index.WithoutMessage);
        CooperativeMessage only = Assert.Single(index.Messages);
        Assert.Equal((1L, CooperativeMessageState.Unconsumed), (only.Sequence, only.State));
        Assert.Equal((Frames, 1L), (Assert.Single(index.Channels).Channel, index.Channels[0].Epoch));
    }

    /// <summary>A generation of the SDK's records, as a capture admits them, with the source fields each carries.</summary>
    private sealed class Sdk
    {
        private static readonly Guid FixtureProvider = Guid.Parse("2d8f2e6c-6e0a-5a0a-9c48-1f1d0e5d7c11");

        private readonly List<ObservationRowV1> rows = [];
        private readonly List<SourceFieldRowV1> fields = [];
        private readonly HashSet<int> processes = [];
        private ulong ordinal = 1_000;

        public void Open(long ticks, int process, Guid? channel, long? epoch, string name, long kind)
        {
            ObservationRowV1 row = Record(ticks, CooperativeSdk.ChannelOpened, process, channel) with { ResourceName = name };
            Add(row, (SourceField.ChannelEpoch, epoch), (SourceField.ChannelKind, kind));
        }

        public void Close(long ticks, int process, Guid? channel, long? epoch) =>
            Add(Record(ticks, CooperativeSdk.ChannelClosed, process, channel), (SourceField.ChannelEpoch, epoch));

        public void Publish(long ticks, int process, Guid? channel, long? epoch, long? sequence, long length, long copied = -1, long? sourceTicks = null) =>
            Add(Record(ticks, CooperativeSdk.MessagePublished, process, channel, length),
                (SourceField.ChannelEpoch, epoch), (SourceField.MessageSequence, sequence), (SourceField.CopiedLength, copied),
                (SourceField.SourceTicks, sourceTicks));

        public void Consume(long ticks, int process, Guid? channel, long? epoch, long? sequence, long? consumer, long length, long? sourceTicks = null) =>
            Add(Record(ticks, CooperativeSdk.MessageConsumed, process, channel, length),
                (SourceField.ChannelEpoch, epoch), (SourceField.MessageSequence, sequence), (SourceField.ConsumerNumber, consumer),
                (SourceField.SourceTicks, sourceTicks));

        /// <summary>A record of InterCat's content fixture, recorded under the same mechanism and numbered from 1 too.</summary>
        public void Fixture(long ticks, int process, ushort eventId) =>
            Add(Record(ticks, eventId, process, Frames, 8) with { ProviderId = FixtureProvider }, (SourceField.ChannelEpoch, 1), (SourceField.MessageSequence, 9));

        public CooperativeMessageIndex Derive(SessionStore store)
        {
            // Each process began before its records, so each binds to the instance it began.
            ObservationRowV1[] lifecycle = [.. processes.Order().Select((process, at) => Lifecycle(1 + at, ObservationKind.Create, process, (ulong)(1 + at)))];
            _ = TestSessions.Publish(store, [.. lifecycle, .. rows.OrderBy(row => row.NativeTicks)], fields: fields);
            (SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = IncrementalDerivationTests.SegmentsOf(store);
            ProcessInstanceIndex instances = ProcessInstanceIndex.Derive(observations, TestClock, fieldSegments);
            return CooperativeMessageIndex.Derive(observations, fieldSegments, instances);
        }

        private ObservationRowV1 Record(long ticks, ushort eventId, int process, Guid? channel, long? length = null)
        {
            _ = processes.Add(process);
            return SdkRecord(ticks * 10, eventId, process, ++ordinal, channel, length);
        }

        private void Add(ObservationRowV1 row, params (SourceField Code, long? Value)[] carried)
        {
            rows.Add(row);
            foreach ((SourceField code, long? value) in carried)
            {
                if (value is { } kept)
                {
                    fields.Add(Field(row, code, kept));
                }
            }
        }
    }
}
