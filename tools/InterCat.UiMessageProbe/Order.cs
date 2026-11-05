using System.Diagnostics;
using System.Globalization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.UiMessageProbe;

/// <summary>
/// Whether a session delivers a window message's ends in the order they were raised, before any gate numbers them (ADR-086):
/// FX-WMSG-001 under an owned session enabling Microsoft-Windows-Win32k at its message pump keyword machine-wide, with the
/// product's buffer size, each record kept with the order it was delivered in, the time it was raised and the processor it
/// was raised on. A capture's gates decide records one at a time as they arrive (ADR-073), so a gate numbering a message by
/// its queue message pointer, from its start to its end, meets the message's ends in the order they are delivered: a send's
/// start at the sender, its retrieval's start and stop at the receiver and the sender's stop, or a post's queueing and its
/// retrieval, raised microseconds apart and often on two processors, whose buffers a session hands over one at a time.
/// </summary>
internal static class Order
{
    private const ulong MessagePump = 0x400000;

    // The buffer size, in kilobytes, every owned session of the product's is created with (OwnedSessionPlan).
    private const int ProductBufferKilobytes = 128;

    private const int QueuePost = 59;
    private const int SendStart = 60;
    private const int RetrievePost = 61;
    private const int RetrieveSendStart = 62;
    private const int SendStop = 70;
    private const int RetrieveSendStop = 83;

    // A send's ends in the order they are raised - its start at the sender, its retrieval's start and stop at the receiver,
    // the sender's stop - and a post's, its queueing and its retrieval.
    private static readonly int[] SendEnds = [SendStart, RetrieveSendStart, RetrieveSendStop, SendStop];
    private static readonly int[] PostEnds = [QueuePost, RetrievePost];

    public static async Task<Dictionary<string, object?>> RunAsync(string workload, string output, int copies, int posts, int bytes)
    {
        string truth = Path.Combine(output, "truth");
        var records = new List<Record>();
        int lost;
        string sessionName = "InterCat-ui-order-" + Guid.NewGuid().ToString("N");
        using (var session = new TraceEventSession(sessionName) { StopOnDispose = true, BufferQuantumKB = ProductBufferKilobytes })
        {
            session.EnableProvider("Microsoft-Windows-Win32k", TraceEventLevel.Informational, MessagePump);
            session.Source.Dynamic.All += data =>
            {
                if (data.ProviderName != "Microsoft-Windows-Win32k")
                {
                    return;
                }

                int id = (int)data.ID;
                bool end = SendEnds.Contains(id) || PostEnds.Contains(id);
                records.Add(new(records.Count, id, data.TimeStampRelativeMSec, data.ProcessID, data.ProcessorNumber, end ? Pointer(data) : null));
            };

            Task processing = Task.Run(() => session.Source.Process());
            await Task.Delay(2_000).ConfigureAwait(false);
            using (Process started = Process.Start(new ProcessStartInfo(workload,
                ["window-message", "--truth", truth, "--copies", copies.ToString(CultureInfo.InvariantCulture),
                    "--posts", posts.ToString(CultureInfo.InvariantCulture), "--bytes", bytes.ToString(CultureInfo.InvariantCulture)])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!)
            {
                await started.WaitForExitAsync().ConfigureAwait(false);
                if (started.ExitCode != 0)
                {
                    throw new InvalidOperationException($"The truth workload exited with {started.ExitCode}.");
                }
            }

            await Task.Delay(2_000).ConfigureAwait(false);
            lost = session.EventsLost;
            session.Stop();
            await processing.ConfigureAwait(false);
        }

        using var scenario = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(truth, "scenario.json")));
        int receiver = scenario.RootElement.GetProperty("receiverProcessId").GetInt32();
        int sender = scenario.RootElement.GetProperty("senderProcessId").GetInt32();
        bool Ours(Record record) => record.Process == sender || record.Process == receiver;

        // The stream's order: each record delivered after one raised later than it, and by how much it was late.
        double latest = double.MinValue;
        long late = 0;
        long lateOfOurs = 0;
        double lateness = 0;
        foreach (Record record in records)
        {
            if (record.Raised < latest)
            {
                late++;
                lateOfOurs += Ours(record) ? 1 : 0;
                lateness = Math.Max(lateness, latest - record.Raised);
            }
            else
            {
                latest = record.Raised;
            }
        }

        // Each of the workload's messages, by its pointer in the order its records were raised: a send's start or a post's
        // queueing begins a life at the pointer, and every other end joins the life the pointer has then; an end at a pointer
        // with no life yet is a life of its own.
        Record[] ours = [.. records.Where(record => record.Pointer is not null && Ours(record))];
        var lives = new List<List<Record>>();
        var lifeOf = new Dictionary<long, int>();
        int pointersReused = 0;
        foreach (IGrouping<long, Record> atPointer in ours.GroupBy(static record => record.Pointer!.Value))
        {
            List<Record>? life = null;
            int livesHere = 0;
            foreach (Record record in atPointer.OrderBy(static record => record.Raised).ThenBy(static record => record.Ordinal))
            {
                if (life is null || record.Id is SendStart or QueuePost)
                {
                    life = [];
                    lives.Add(life);
                    livesHere++;
                }

                life.Add(record);
                lifeOf[record.Ordinal] = lives.Count - 1;
            }

            pointersReused += livesHere > 1 ? 1 : 0;
        }

        // A send whose four ends, or a post whose two, each arrived once.
        List<Record>[] sends = [.. lives.Where(life => life.Count == SendEnds.Length && SendEnds.All(id => life.Count(record => record.Id == id) == 1))];
        List<Record>[] postsSeen = [.. lives.Where(life => life.Count == PostEnds.Length && PostEnds.All(id => life.Count(record => record.Id == id) == 1))];
        static Record End(List<Record> life, int id) => life.Single(record => record.Id == id);
        static bool Delivered(List<Record> life, int[] ends)
        {
            for (int at = 1; at < ends.Length; at++)
            {
                if (End(life, ends[at]).Ordinal < End(life, ends[at - 1]).Ordinal)
                {
                    return false;
                }
            }

            return true;
        }

        static bool TwoProcessors(List<Record> life) => life.Select(static record => record.Processor).Distinct().Count() > 1;

        // A gate following a message by its pointer as records arrive - a send's start or a post's queueing begins a message
        // at its pointer, any other end joins the message the pointer has then, a send's stop or a post's retrieval ends it,
        // an end at a pointer no message has is numbered alone - set against each message's life: the records it would
        // number with another life's, or apart from their own life's.
        var gate = new Dictionary<long, int>();
        var numberOf = new Dictionary<long, int>();
        int numbers = 0;
        foreach (Record record in ours)
        {
            long pointer = record.Pointer!.Value;
            if (record.Id is SendStart or QueuePost || !gate.TryGetValue(pointer, out int number))
            {
                number = ++numbers;
                if (record.Id is SendStart or QueuePost)
                {
                    gate[pointer] = number;
                }
            }
            else if (record.Id is SendStop or RetrievePost)
            {
                gate.Remove(pointer);
            }

            numberOf[record.Ordinal] = number;
        }

        // A record is numbered as its life is when every record of its gate number is of its life and every record of its life
        // has its gate number.
        var lifesNumbers = lives.Select(life => life.Select(record => numberOf[record.Ordinal]).Distinct().ToArray()).ToArray();
        var numbersLives = ours.GroupBy(record => numberOf[record.Ordinal]).ToDictionary(group => group.Key, group => group.Select(record => lifeOf[record.Ordinal]).Distinct().Count());
        int misnumbered = ours.Count(record => lifesNumbers[lifeOf[record.Ordinal]].Length != 1 || numbersLives[numberOf[record.Ordinal]] != 1);
        int livesMisnumbered = lifesNumbers.Select((found, life) => found.Length != 1 || numbersLives[found[0]] != 1).Count(static wrong => wrong);

        return new()
        {
            ["question"] = "Whether a session with the product's buffer size delivers a window message's ends in the order they were "
                + "raised - a send's start, its retrieval's start and stop at the receiver and the sender's stop; a post's queueing "
                + "and its retrieval - and what a gate following a message by its pointer as records arrive would number wrongly.",
            ["scenario"] = "FX-WMSG-001",
            ["truth"] = new Dictionary<string, object?> { ["copiesSent"] = copies, ["messagesPosted"] = posts, ["bytes"] = bytes },
            ["bufferKilobytes"] = ProductBufferKilobytes,
            ["recordsDelivered"] = records.Count,
            ["eventsLost"] = lost,
            ["recordsDeliveredAfterOneRaisedLater"] = late,
            ["workloadRecordsDeliveredAfterOneRaisedLater"] = lateOfOurs,
            ["latestDeliveryMilliseconds"] = Math.Round(lateness, 3),
            ["workloadEnds"] = ours.Length,
            ["workloadMessageLives"] = lives.Count,
            ["pointersWithMoreThanOneLife"] = pointersReused,
            ["sendsWithEveryEnd"] = sends.Length,
            ["sendsDeliveredInTheOrderRaised"] = sends.Count(life => Delivered(life, SendEnds)),
            ["sendsWithTheReceiversRetrievalDeliveredAfterTheSendersStop"] = sends.Count(life => End(life, RetrieveSendStart).Ordinal > End(life, SendStop).Ordinal),
            ["sendsWithAnEndDeliveredBeforeTheirStart"] = sends.Count(life => life.Any(record => record.Ordinal < End(life, SendStart).Ordinal)),
            ["sendsRaisedOnTwoProcessors"] = sends.Count(TwoProcessors),
            ["postsWithBothEnds"] = postsSeen.Length,
            ["postsDeliveredInTheOrderRaised"] = postsSeen.Count(life => Delivered(life, PostEnds)),
            ["postsRaisedOnTwoProcessors"] = postsSeen.Count(TwoProcessors),
            ["livesWithoutEveryEnd"] = lives.Count - sends.Length - postsSeen.Length,
            ["recordsAPointerGateWouldMisnumber"] = misnumbered,
            ["livesAPointerGateWouldMisnumber"] = livesMisnumbered,
        };
    }

    private static long? Pointer(TraceEvent data)
    {
        int index = data.PayloadIndex("pqmsg");
        return index < 0 ? null : data.PayloadValue(index) switch
        {
            ulong value => unchecked((long)value),
            long value => value,
            uint value => value,
            int value => value,
            _ => null,
        };
    }

    /// <summary>One message pump record: the order it was delivered in, its event, when it was raised, its process and processor, and a message end's pointer.</summary>
    private sealed record Record(int Ordinal, int Id, double Raised, int Process, int Processor, long? Pointer);
}
