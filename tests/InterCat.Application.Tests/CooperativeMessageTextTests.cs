using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// A session's cooperative messages as `icat messages` and InterCat's window read and say them alike (R18,
/// `contracts/cooperative-messages-v1.md`): one reading of a generation, and one set of words for a channel epoch, what
/// became of a message, and the process that published or read it.
/// </summary>
public sealed class CooperativeMessageTextTests
{
    private static readonly Guid Frames = Guid.Parse("6f1c8f4e-2b8e-4a40-9f7c-0d6a7f3e5a11");

    [Fact(DisplayName = "R18: a session's cooperative messages are read once for icat messages and InterCat's window alike, and said in one set of words - an epoch counted by what became of its messages, each message with its publisher and its readers in their processes")]
    public void AMessageIsSaidInOneSetOfWords()
    {
        using var session = new TemporarySession();
        var rows = new List<ObservationRowV1>
        {
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\producer.exe" },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\reader.exe" },
        };
        var fields = new List<SourceFieldRowV1>();
        void Add(ObservationRowV1 row, params (SourceField Code, long Value)[] values)
        {
            rows.Add(row);
            fields.AddRange(values.Select(field => Field(row, field.Code, field.Value)));
        }

        // Three messages of the frames ring: one consumed by its reader, one open, and one read whose publication was not
        // recorded, by a process no record created.
        Add(SdkRecord(10, CooperativeSdk.ChannelOpened, 100, 10, Frames) with { ResourceName = "frames" },
            (SourceField.ChannelEpoch, 1), (SourceField.ChannelKind, 3));
        Add(SdkRecord(20, CooperativeSdk.MessagePublished, 100, 11, Frames, 2_048),
            (SourceField.ChannelEpoch, 1), (SourceField.MessageSequence, 1));
        Add(SdkRecord(30, CooperativeSdk.MessageConsumed, 200, 12, Frames, 2_048),
            (SourceField.ChannelEpoch, 1), (SourceField.MessageSequence, 1), (SourceField.ConsumerNumber, 1));
        Add(SdkRecord(40, CooperativeSdk.MessagePublished, 100, 13, Frames, 1),
            (SourceField.ChannelEpoch, 1), (SourceField.MessageSequence, 2));
        Add(SdkRecord(50, CooperativeSdk.MessageConsumed, 900, 14, Frames, 16),
            (SourceField.ChannelEpoch, 1), (SourceField.MessageSequence, 3), (SourceField.ConsumerNumber, 2));
        Publish(session.Store, rows, fields: fields);
        session.Store.ReleaseSegmentReaders();

        CooperativeMessageReading reading = CooperativeMessageText.Read(session.Store);
        session.Store.ReleaseSegmentReaders();
        Assert.Equal((EvidencePolicy.IncludeCorrelated, 3, 1), (reading.Policy, reading.Messages, reading.Index.Channels.Count));
        Assert.Equal([Mechanism.ApplicationSdk], reading.Coverage.Select(coverage => coverage.Mechanism));
        CooperativeChannel channel = reading.Index.Channels[0];

        // The epoch by its name and epoch, counted by what became of its messages, each only where any did.
        Assert.Equal("frames · epoch 1", CooperativeMessageText.Channel(channel));
        Assert.Equal("3 messages · 1 consumed · 1 open · 1 with no publication recorded · 2 readers · 2,049 bytes published",
            CooperativeMessageText.Counted(channel));
        Assert.Equal(("shared memory", "a pipe", "a socket", "another kind", "kind 9"), (CooperativeMessageText.Kind(3),
            CooperativeMessageText.Kind(1), CooperativeMessageText.Kind(2), CooperativeMessageText.Kind(0), CooperativeMessageText.Kind(9)));

        // Each message: what became of it, its length as its publication stated it, its publisher and its readers, each in
        // the process its record names - or the reason no instance holds a record.
        CooperativeMessage[] messages = [.. reading.Index.MessagesOf(channel)];
        Assert.Equal(
        [
            "Sequence 1 · consumed · 2,048 bytes · published by producer.exe · 100 · read by reader 1 in reader.exe · 200, 10.0 µs "
                + "after its publication was said",
            "Sequence 2 · open: no reader consumed it · 1 byte · published by producer.exe · 100 · no reader",
            "Sequence 3 · consumed, its publication not recorded · length not stated · published by no recorded process · read by "
                + "reader 2 in executable not witnessed · 900",
        ], messages.Select(message => CooperativeMessageText.Message(reading, message)));
        Assert.Equal(("reader 1 in reader.exe · 200, 10.0 µs after its publication was said", "no reader"),
            (CooperativeMessageText.Readers(reading, messages[0]), CooperativeMessageText.Readers(reading, messages[1])));
        Assert.Equal("conflicting: published or read twice", CooperativeMessageText.State(CooperativeMessageState.Conflicting));

        // An epoch's lag: at the median and from the least to the most, a consumption said first said so, never a latency.
        Assert.Equal("Its consumptions were said 10.0 µs after their publications at the median, from 10.0 µs after to 10.0 µs after.",
            CooperativeMessageText.Lag(channel));
        Assert.Equal("Its consumptions were said 5.0 µs after their publications at the median, from 3.0 µs before to 9.0 µs after; "
            + "1 consumption said before its publication was, as a reader may: each record is said after its moment, so a lag is never "
            + "a message's latency.", CooperativeMessageText.Lag(channel with { Lag = new CooperativeLag(-3_000, 5_000, 9_000, 3, 1) }));
        Assert.Equal(("No consumption follows a publication recorded once, so no lag between them is said.", "10.0 µs after", "none said"),
            (CooperativeMessageText.Lag(channel with { Lag = null }), CooperativeMessageText.MedianLag(channel.Lag),
                CooperativeMessageText.MedianLag(null)));

        // A run said in batches is one row: its first and last sequence, its batch's length together, never split among its
        // messages, and its readers; a message alone says its own length.
        CooperativeMessage run = messages[0] with
        {
            Count = 50,
            Publication = messages[0].Publication! with { Batch = 50, Length = 6_400 },
        };
        Assert.Equal(("1 to 50", "1"), (CooperativeMessageText.Sequences(run), CooperativeMessageText.Sequences(messages[0])));
        Assert.Equal("Sequences 1 to 50 · consumed · said in a batch of 50 messages, 6,400 bytes together · published by producer.exe · 100 · "
            + "read by reader 1 in reader.exe · 200, 10.0 µs after its publication was said", CooperativeMessageText.Message(reading, run));
        Assert.Equal(("said in a batch of 1 message, their length not stated", "2,048 bytes", "length not stated"),
            (CooperativeMessageText.Length(run with { Publication = run.Publication with { Batch = 1, Length = null } }),
                CooperativeMessageText.Length(messages[0]), CooperativeMessageText.Length(messages[2])));

        // A record whose binding the reading's policy does not admit says its PID and why, never the process's name.
        CooperativeMessageRecord unadmitted = messages[0].Publication! with { Process = default };
        Assert.False(unadmitted.Process.IsAdmittedUnder(reading.Policy));
        Assert.Equal("PID 100 · " + BindingText.Reason(ProcessBindingReason.NotAdmittedByPolicy), CooperativeMessageText.Process(reading, unadmitted));
    }
}
