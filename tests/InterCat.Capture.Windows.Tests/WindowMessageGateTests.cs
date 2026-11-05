using System.Globalization;
using System.Text.Json;
using Xunit;
using static InterCat.Capture.Windows.WindowMessageGate;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// The window message gate (ADR-087): it follows a message at its queue message pointer from its start to its end and numbers
/// it in the capture, follows a message begun before the capture from the first of its ends the capture saw, and numbers alone
/// a record delivered out of the order raised.
/// </summary>
public sealed class WindowMessageGateTests
{
    [Fact(DisplayName = "R22: a sent message is one message from its start to the sender's stop, its retrieval at the receiver between, and the next start at its pointer begins another")]
    public void ASentMessageEndsWithTheSendersStop()
    {
        var gate = new WindowMessageGate();
        WindowMessageIncarnation sent = gate.Read(new(SendStart, 0x1000, 10));
        Assert.Equal(new(1, MessageFirstSeen.Sent), sent);
        Assert.Equal(sent, gate.Read(new(RetrieveSendStart, 0x1000, 11)));
        Assert.Equal(sent, gate.Read(new(RetrieveSendStop, 0x1000, 12)));
        Assert.Equal(1, gate.Following);
        Assert.Equal(sent, gate.Read(new(SendStop, 0x1000, 13)));
        Assert.Equal(0, gate.Following);

        // The kernel reuses the pointer once the message is retrieved: the next message there is another's.
        Assert.Equal(new(2, MessageFirstSeen.Queued), gate.Read(new(QueuePost, 0x1000, 14)));
    }

    [Fact(DisplayName = "R22: a posted message is one message from its queueing to its retrieval, and a start at a pointer a message is followed at begins another, that one ended unseen")]
    public void APostedMessageEndsWithItsRetrieval()
    {
        var gate = new WindowMessageGate();
        WindowMessageIncarnation posted = gate.Read(new(QueuePost, 0x2000, 10));
        Assert.Equal(new(1, MessageFirstSeen.Queued), posted);
        Assert.Equal(posted, gate.Read(new(RetrievePost, 0x2000, 11)));
        Assert.Equal(0, gate.Following);

        // A message whose end never came - its window gone before retrieving it - is ended by the next start at its pointer,
        // of either kind.
        WindowMessageIncarnation unretrieved = gate.Read(new(QueuePost, 0x2000, 12));
        WindowMessageIncarnation again = gate.Read(new(QueuePost, 0x2000, 13));
        WindowMessageIncarnation next = gate.Read(new(SendStart, 0x2000, 14));
        Assert.Equal((new WindowMessageIncarnation(2, MessageFirstSeen.Queued), new WindowMessageIncarnation(3, MessageFirstSeen.Queued),
            new WindowMessageIncarnation(4, MessageFirstSeen.Sent)), (unretrieved, again, next));
        Assert.Equal(next, gate.Read(new(SendStop, 0x2000, 15)));
        Assert.Equal(0, gate.Following);
    }

    [Fact(DisplayName = "R22: the ends of a message begun before the capture are one message numbered apart, from the first of its ends the capture saw to its last, and an end of the other kind at a pointer is never the message there")]
    public void AMessageBegunBeforeTheCaptureIsItsOwn()
    {
        var gate = new WindowMessageGate();

        // A send started before the capture: its retrieval's stop and the sender's stop are one message of its own.
        WindowMessageIncarnation unstarted = gate.Read(new(RetrieveSendStop, 0x3000, 10));
        Assert.Equal(new(1, MessageFirstSeen.Unstarted), unstarted);
        Assert.Equal(1, gate.Following);
        Assert.Equal(unstarted, gate.Read(new(SendStop, 0x3000, 11)));
        Assert.Equal(0, gate.Following);

        // A post queued before the capture: its retrieval, its last end, is numbered alone and follows nothing.
        Assert.Equal(new(2, MessageFirstSeen.Unstarted), gate.Read(new(RetrievePost, 0x4000, 12)));
        Assert.Equal(0, gate.Following);

        // A send's end at a pointer a posted message is followed at is not that message's: the post there ended unseen.
        WindowMessageIncarnation posted = gate.Read(new(QueuePost, 0x5000, 13));
        WindowMessageIncarnation send = gate.Read(new(RetrieveSendStart, 0x5000, 14));
        Assert.Equal((new WindowMessageIncarnation(3, MessageFirstSeen.Queued), new WindowMessageIncarnation(4, MessageFirstSeen.Unstarted)), (posted, send));
        Assert.Equal(send, gate.Read(new(SendStop, 0x5000, 15)));
        Assert.Equal(new(5, MessageFirstSeen.Unstarted), gate.Read(new(RetrievePost, 0x5000, 16)));
        Assert.Equal(0, gate.Following);

        // Only a message's ends are read.
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.Read(new(426, 0x6000, 17)));
    }

    [Fact(DisplayName = "R22: a record raised before one the session delivered ahead of it is numbered alone and counted, and leaves the message it might have joined as it was")]
    public void ARecordOutOfOrderIsNumberedAlone()
    {
        var gate = new WindowMessageGate();
        WindowMessageIncarnation sent = gate.Read(new(SendStart, 0x1000, 20));
        Assert.Equal(new(2, MessageFirstSeen.OutOfOrder), gate.Read(new(RetrieveSendStart, 0x1000, 19)));
        Assert.Equal(1, gate.DeliveredOutOfOrder);

        // The message goes on as it was; a record raised at the same time as the latest is in order.
        Assert.Equal(sent, gate.Read(new(RetrieveSendStop, 0x1000, 21)));
        Assert.Equal(sent, gate.Read(new(SendStop, 0x1000, 21)));
        Assert.Equal((0, 1L), (gate.Following, gate.DeliveredOutOfOrder));
    }

    [Fact(DisplayName = "R22: the gate follows no more messages than its bound, giving up the oldest it still follows - never one already ended - whose later end is then numbered as a message's begun before")]
    public void TheGateGivesUpTheOldestBeyondItsBound()
    {
        var gate = new WindowMessageGate(capacity: 2);
        WindowMessageIncarnation first = gate.Read(new(QueuePost, 0x1000, 1));
        WindowMessageIncarnation second = gate.Read(new(SendStart, 0x2000, 2));
        WindowMessageIncarnation third = gate.Read(new(QueuePost, 0x3000, 3));
        Assert.Equal((2, 1L), (gate.Following, gate.GivenUp));

        // The first, given up, ends as a message begun before the gate followed it; the others end as followed.
        Assert.Equal(new(4, MessageFirstSeen.Unstarted), gate.Read(new(RetrievePost, 0x1000, 4)));
        Assert.Equal(third, gate.Read(new(RetrievePost, 0x3000, 5)));
        Assert.Equal(second, gate.Read(new(SendStop, 0x2000, 6)));
        Assert.NotEqual(first, third);
        Assert.Equal((0, 1L), (gate.Following, gate.GivenUp));

        // A message already ended holds no place: beyond the bound, an ended one's beginning gives nothing up.
        var ended = new WindowMessageGate(capacity: 2);
        ended.Read(new(QueuePost, 0x1000, 1));
        ended.Read(new(RetrievePost, 0x1000, 2));
        ended.Read(new(SendStart, 0x2000, 3));
        ended.Read(new(QueuePost, 0x3000, 4));
        Assert.Equal((2, 0L), (ended.Following, ended.GivenUp));

        // Nor does an ended one's at a pointer a later message is followed at: the message there now is that one, not it.
        var reused = new WindowMessageGate(capacity: 2);
        reused.Read(new(QueuePost, 0x1000, 1));
        reused.Read(new(RetrievePost, 0x1000, 2));
        WindowMessageIncarnation later = reused.Read(new(QueuePost, 0x1000, 3));
        reused.Read(new(SendStart, 0x2000, 4));
        Assert.Equal((2, 0L), (reused.Following, reused.GivenUp));
        Assert.Equal(later, reused.Read(new(RetrievePost, 0x1000, 5)));

        // A message begun at the oldest's own pointer ends it as any start does, and gives nothing up.
        var one = new WindowMessageGate(capacity: 1);
        one.Read(new(QueuePost, 0x1000, 1));
        WindowMessageIncarnation replaced = one.Read(new(QueuePost, 0x1000, 2));
        Assert.Equal(new(2, MessageFirstSeen.Queued), replaced);
        Assert.Equal((1, 0L), (one.Following, one.GivenUp));
        Assert.Equal(replaced, one.Read(new(RetrievePost, 0x1000, 3)));
    }

    [Fact(DisplayName = "P12: the window message gate, which reads its records on the capture's callback, allocates nothing per record, its tables sized once to its bound")]
    public void TheGateAllocatesNothingPerRecord()
    {
        // A thousand sends and a thousand posts at a few hundred reused pointers, each from its start to its end, and every
        // tenth post never retrieved, so the bound gives up the oldest: read once to run every path, then again, raised
        // later, counting what the gate allocated.
        WindowMessageRecord[] first = Records(0);
        WindowMessageRecord[] again = Records(first.Length);
        var gate = new WindowMessageGate(capacity: 64);
        Read(gate, first);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Read(gate, again);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, allocated);
        Assert.True(gate.GivenUp > 0);
        Assert.Equal(0, gate.DeliveredOutOfOrder);

        static WindowMessageRecord[] Records(long from)
        {
            var records = new List<WindowMessageRecord>();
            for (int message = 0; message < 1_000; message++)
            {
                ulong pointer = 0x10_0000 + ((ulong)(message % 300) * 0x40);
                records.Add(new(SendStart, pointer, from + records.Count));
                records.Add(new(RetrieveSendStart, pointer, from + records.Count));
                records.Add(new(RetrieveSendStop, pointer, from + records.Count));
                records.Add(new(SendStop, pointer, from + records.Count));
                ulong posted = pointer + 8;
                records.Add(new(QueuePost, posted + ((ulong)message * 0x1_0000), from + records.Count));
                if (message % 10 != 0)
                {
                    records.Add(new(RetrievePost, posted + ((ulong)message * 0x1_0000), from + records.Count));
                }
            }

            return [.. records];
        }

        static void Read(WindowMessageGate gate, WindowMessageRecord[] records)
        {
            foreach (WindowMessageRecord record in records)
            {
                gate.Read(in record);
            }
        }
    }

    [Fact(DisplayName = "R22: FX-WMSG-001's message ends, read through the gate as delivered, are its 32 sends and 33 posts - each send's start and stop at the sender and its retrieval at the receiver one message, each post's queueing and retrieval another - numbered apart, though the run reused its pointers")]
    public void FxWmsg001sEndsAreItsMessages()
    {
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", "FX-WMSG-001", "evidence");
        string[][] rows = [.. File.ReadLines(Path.Combine(evidence, "ledger-order.csv")).Skip(1).Select(line => line.Split(','))];
        var gate = new WindowMessageGate();
        var messages = new Dictionary<long, List<(string Role, int Event)>>();
        var firstSeen = new Dictionary<long, MessageFirstSeen>();
        foreach (string[] field in rows)
        {
            // The ledger's pointer numbers stand for the pointers it never states, and its times are the session's, in ticks
            // from the first end of the run.
            var record = new WindowMessageRecord(int.Parse(field[2], CultureInfo.InvariantCulture),
                ulong.Parse(field[3], CultureInfo.InvariantCulture), long.Parse(field[4], CultureInfo.InvariantCulture));
            WindowMessageIncarnation incarnation = gate.Read(record);
            messages.TryAdd(incarnation.Number, []);
            messages[incarnation.Number].Add((field[1], record.EventId));
            firstSeen.TryAdd(incarnation.Number, incarnation.FirstSeen);
        }

        // The run the ledger is of: FX-WMSG-001's 32 copies and 32 posts, and the close, every end delivered in the order raised.
        using (JsonDocument report = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "ledger-order.json"))))
        {
            JsonElement r = report.RootElement;
            Assert.Equal((32, 32, 0), (r.GetProperty("truth").GetProperty("copiesSent").GetInt32(), r.GetProperty("truth").GetProperty("messagesPosted").GetInt32(),
                r.GetProperty("eventsLost").GetInt32()));
            Assert.Equal((rows.Length, messages.Count, 0L), (r.GetProperty("workloadEnds").GetInt32(), r.GetProperty("workloadMessageLives").GetInt32(),
                r.GetProperty("workloadRecordsDeliveredAfterOneRaisedLater").GetInt64()));
        }

        // Each send one message: its start and stop at the sender, its retrieval's start and stop at the receiver; each post,
        // the close among them, its queueing at the sender and its retrieval at the receiver.
        (string, int)[] send = [("receiver", RetrieveSendStart), ("receiver", RetrieveSendStop), ("sender", SendStart), ("sender", SendStop)];
        (string, int)[] post = [("receiver", RetrievePost), ("sender", QueuePost)];
        Assert.Equal(194, rows.Length);
        Assert.Equal(32, messages.Values.Count(ends => ends.Order().SequenceEqual(send)));
        Assert.Equal(33, messages.Values.Count(ends => ends.Order().SequenceEqual(post)));
        Assert.Equal(65, messages.Count);
        Assert.Equal((32, 33), (firstSeen.Values.Count(seen => seen == MessageFirstSeen.Sent), firstSeen.Values.Count(seen => seen == MessageFirstSeen.Queued)));

        // The run reused its pointers, and the gate ended every message it began, giving none up and meeting none out of order.
        Assert.True(rows.Select(field => field[3]).Distinct(StringComparer.Ordinal).Count() < messages.Count);
        Assert.Equal((0, 0L, 0L), (gate.Following, gate.GivenUp, gate.DeliveredOutOfOrder));
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }
}
