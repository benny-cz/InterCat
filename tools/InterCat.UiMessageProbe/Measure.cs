using System.Diagnostics;
using System.Globalization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.UiMessageProbe;

/// <summary>
/// FX-WMSG-001 under an owned session of the probe's own enabling Microsoft-Windows-Win32k at its message pump keyword,
/// machine-wide: what it records of the messages a sender sent and posted to a receiver's message-only window - each sent
/// message's start and stop at the sender, naming the receiver's process and thread, and its retrieval at the receiver; each
/// posted message's queueing and retrieval - set against both processes' truth: how many of each, the window and message
/// numbers they carry, a posted message's number, and the queue message pointer that joins one end to the other.
/// </summary>
internal static class Measure
{
    // Win32k's MessagePump keyword.
    private const ulong MessagePump = 0x400000;

    // The message pump events this measures: a posted message's queueing and retrieval, a sent message's start and stop at
    // the sender and at the receiver, and a null message posted by another process.
    private const int QueuePost = 59;
    private const int SendStart = 60;
    private const int RetrievePost = 61;
    private const int RetrieveSendStart = 62;
    private const int SendStop = 70;
    private const int RetrieveSendStop = 83;
    private const int QueueNullPost = 426;

    private static readonly int[] Measured = [QueuePost, SendStart, RetrievePost, RetrieveSendStart, SendStop, RetrieveSendStop, QueueNullPost];

    public static async Task<Dictionary<string, object?>> RunAsync(string workload, string output, int copies, int posts, int bytes,
        string? dump = null)
    {
        string truth = Path.Combine(output, "truth");
        var records = new List<Record>();
        long delivered = 0;
        int lost;
        string sessionName = "InterCat-ui-messages-" + Guid.NewGuid().ToString("N");
        using (var session = new TraceEventSession(sessionName) { StopOnDispose = true, BufferSizeMB = 512 })
        {
            session.EnableProvider("Microsoft-Windows-Win32k", TraceEventLevel.Verbose, MessagePump);
            session.Source.Dynamic.All += data =>
            {
                if (data.ProviderName != "Microsoft-Windows-Win32k")
                {
                    return;
                }

                delivered++;
                int id = (int)data.ID;
                if (!Measured.Contains(id))
                {
                    return;
                }

                records.Add(new(id, data.TimeStamp, data.ProcessID, data.ThreadID, Number(data, "hwnd"), Number(data, "message"),
                    Number(data, "WParam"), Number(data, "pqmsg"), Number(data, "pidReceiver"), Number(data, "tidReceiver"),
                    Number(data, "SourceProcessId"), Number(data, "DestinationHwnd"))
                {
                    // Every field as the parser named and typed it, for a local diagnosis only.
                    Payload = string.Join(';', data.PayloadNames.Select((name, index) => $"{name}={data.PayloadValue(index)} ({data.PayloadValue(index)?.GetType().Name})")),
                });
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
        System.Text.Json.JsonElement root = scenario.RootElement;
        int receiver = root.GetProperty("receiverProcessId").GetInt32();
        int sender = root.GetProperty("senderProcessId").GetInt32();
        long copyData = root.GetProperty("copyDataMessage").GetInt64();
        long posted = root.GetProperty("postedMessage").GetInt64();

        // The receiver's window, as the sender's sent messages name it: the one window its sends that name the receiver went to.
        Record[] sentStarts = [.. records.Where(record => record.Id == SendStart && record.Process == sender)];
        long[] windows = [.. sentStarts.Where(record => record.Receiver == receiver).Select(static record => record.Window ?? 0).Distinct()];
        long window = windows.Length == 1 ? windows[0] : 0;
        bool ToWindow(Record record) => window != 0 && record.Window == window;

        // Each end joined to the other by the queue message's kernel pointer, which both carry.
        HashSet<long> Pointers(IEnumerable<Record> ends) => [.. ends.Where(static record => record.Queue is not null).Select(static record => record.Queue!.Value)];
        Record[] sentToTheWindow = [.. sentStarts.Where(ToWindow)];
        Record[] sentStops = [.. records.Where(record => record.Id == SendStop && record.Process == sender)];
        Record[] retrievedSends = [.. records.Where(record => record.Id == RetrieveSendStart && record.Process == receiver && ToWindow(record))];
        Record[] retrievedSendStops = [.. records.Where(record => record.Id == RetrieveSendStop && record.Process == receiver)];
        Record[] queued = [.. records.Where(record => record.Id == QueuePost && ToWindow(record))];
        Record[] retrievedPosts = [.. records.Where(record => record.Id == RetrievePost && record.Process == receiver && ToWindow(record))];
        HashSet<long> sentPointers = Pointers(sentToTheWindow);
        HashSet<long> queuedPointers = Pointers(queued);
        Record[] ofTheWorkload = [.. records.Where(record => record.Process == sender || record.Process == receiver)];

        string Role(int process) => process == sender ? "sender" : process == receiver ? "receiver" : "other";

        // A local diagnosis of the two processes' records with every field, never part of a committed result.
        if (dump is not null)
        {
            File.WriteAllLines(dump, records.Where(record => record.Process == sender || record.Process == receiver)
                .Select(record => $"{record.Id},{Role(record.Process)},{record.Payload}"));
        }

        return new()
        {
            ["question"] = "Whether Microsoft-Windows-Win32k, at its message pump keyword, records the messages one process sent "
                + "and posted to another's window - each sent message's start and stop at the sender naming the receiver, its "
                + "retrieval at the receiver, each posted message's queueing and retrieval - with the window, the message, a "
                + "posted message's number and the pointer joining its two ends.",
            ["scenario"] = "FX-WMSG-001",
            ["truth"] = new Dictionary<string, object?>
            {
                ["copiesSent"] = copies,
                ["messagesPosted"] = posts,
                ["copyDataMessage"] = copyData,
                ["postedMessage"] = posted,
            },
            ["recordsDelivered"] = delivered,
            ["eventsLost"] = lost,
            ["measuredRecordsByEventAndRole"] = records.GroupBy(record => $"{record.Id} by {Role(record.Process)}")
                .OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count()),
            ["receiversWindowFound"] = window != 0,
            ["sentStartsAtTheSender"] = sentStarts.Length,
            ["sentStartsNamingTheReceiversProcess"] = sentStarts.Count(record => record.Receiver == receiver),
            ["sentStartsNamingOneReceiverThread"] = sentToTheWindow.Select(static record => record.ReceiverThread).Distinct().Count() == 1,
            ["sentStopsAtTheSenderJoinedToAStart"] = sentStops.Count(record => record.Queue is { } queue && sentPointers.Contains(queue)),
            ["sendsRetrievedAtTheReceiverJoinedToAStart"] = retrievedSends.Count(record => record.Queue is { } queue && sentPointers.Contains(queue)),
            ["retrievalStopsAtTheReceiverJoinedToAStart"] = retrievedSendStops.Count(record => record.Queue is { } queue && sentPointers.Contains(queue)),
            ["postsQueuedToTheWindow"] = queued.Length,
            ["postsQueuedRaisedIn"] = queued.GroupBy(record => Role(record.Process)).ToDictionary(group => group.Key, group => group.Count()),
            ["postsRetrievedAtTheReceiver"] = retrievedPosts.Length,
            ["postsRetrievedJoinedToTheirQueueingByPointer"] = retrievedPosts.Count(record => record.Queue is { } queue && queuedPointers.Contains(queue)),
            ["workloadRecordsCarryingAMessageNumber"] = ofTheWorkload.Count(static record => record.Message is not null and not 0),
            ["workloadRecordsCarryingAParameter"] = ofTheWorkload.Count(static record => record.WParam is not null and not 0),
        };
    }

    private static long? Number(TraceEvent data, string field)
    {
        int index = data.PayloadIndex(field);
        return index < 0 ? null : data.PayloadValue(index) switch
        {
            ulong value => unchecked((long)value),
            long value => value,
            uint value => value,
            int value => value,
            ushort value => value,
            short value => value,
            byte value => value,
            _ => null,
        };
    }

    /// <summary>One message pump record: its event, time, process, thread, window, message, first parameter, queue pointer and named processes.</summary>
    private sealed record Record(int Id, DateTime Utc, int Process, int Thread, long? Window, long? Message, long? WParam, long? Queue,
        long? Receiver, long? ReceiverThread, long? Source, long? Destination)
    {
        public string Payload { get; init; } = string.Empty;
    }
}
