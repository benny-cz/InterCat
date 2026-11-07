using System.Diagnostics;
using System.Text.Json;
using InterCat.Sdk;

namespace InterCat.TestWorkloads;

/// <summary>Parameters of one burst of the cooperative SDK's calls.</summary>
internal sealed record SdkBurstOptions
{
    public const string ScenarioId = "SDK-BURST";

    public required string TruthDirectory { get; init; }

    /// <summary>How many messages the burst publishes and consumes, each one publication and one consumption.</summary>
    public int Messages { get; init; } = 1_000_000;

    /// <summary>
    /// How many calls a second the burst paces itself to, each step's two on its schedule - a message's, or a batch's - 0 for
    /// as fast as the SDK takes them.
    /// </summary>
    public int CallsPerSecond { get; init; }

    /// <summary>
    /// How many messages each call says: 1, each message published and consumed by itself; more, each step a batch published
    /// in one call and consumed in one (ADR-106), the burst's messages a whole number of batches.
    /// </summary>
    public int Batch { get; init; } = 1;

    /// <summary>
    /// Whether each message is published with its bytes, its content said where a session asks for it (ADR-107); messages
    /// said one by one only.
    /// </summary>
    public bool Content { get; init; }

    /// <summary>
    /// Whether the burst waits for a line on its standard input before it says anything: a session scoping itself to this
    /// process names it first, as a capture keeping content must, and then lets it start.
    /// </summary>
    public bool WaitForStart { get; init; }
}

/// <summary>
/// The cooperative SDK's cost as an application pays it (M7's overhead guide): one process opens a channel and, on one
/// thread, publishes and consumes each message of a burst - or each batch of them - as fast as the SDK takes the calls, then
/// reports how long they took and whether a session listened. With no session enabling the provider each call costs its
/// enabled check; with one, it writes an event. It touches no shared memory and reads no InterCat state: the report is the
/// SDK's cost alone.
/// </summary>
internal static class SdkBurstScenario
{
    /// <summary>The logical length each message states, so a batch's together is this times its count.</summary>
    private const int MessageBytes = 256;

    private static readonly JsonSerializerOptions ReportOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(SdkBurstOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        if (options.WaitForStart)
        {
            _ = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }

        bool listened = InterCatSdkEventSource.Log.IsEnabled();

        // A paced burst holds each step to its place on the schedule, spinning until it is due; an unpaced one never waits.
        int batch = options.Batch;
        byte[] message = new byte[MessageBytes];
        for (int index = 0; index < message.Length; index++)
        {
            message[index] = unchecked((byte)(index * 7));
        }

        long contentSaid = 0;
        double ticksPerStep = options.CallsPerSecond == 0 ? 0 : 2.0 * Stopwatch.Frequency / options.CallsPerSecond;
        long started = Stopwatch.GetTimestamp();
        using (InterCatChannel channel = InterCatChannel.Open(Guid.NewGuid(), "sdk-burst", InterCatChannelKind.Other))
        {
            long step = 0;
            for (long sequence = 1; sequence <= options.Messages; sequence += batch, step++)
            {
                long due = started + (long)(step * ticksPerStep);
                while (Stopwatch.GetTimestamp() < due)
                {
                    Thread.SpinWait(16);
                }

                if (options.Content)
                {
                    contentSaid += channel.Published(sequence, message, copiedLength: MessageBytes) ? 1 : 0;
                    channel.Consumed(sequence, MessageBytes, consumer: 1);
                }
                else if (batch == 1)
                {
                    channel.Published(sequence, MessageBytes, copiedLength: MessageBytes);
                    channel.Consumed(sequence, MessageBytes, consumer: 1);
                }
                else
                {
                    channel.PublishedBatch(sequence, batch, (long)MessageBytes * batch, copiedLength: (long)MessageBytes * batch);
                    channel.ConsumedBatch(sequence, batch, (long)MessageBytes * batch, consumer: 1);
                }
            }
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        long calls = 2L * options.Messages / batch;
        double nanoseconds = elapsed.TotalMilliseconds * 1_000_000;
        var report = new
        {
            scenarioId = SdkBurstOptions.ScenarioId,
            listened,
            options.Messages,
            batch,
            content = options.Content,
            contentSaid,
            targetCallsPerSecond = options.CallsPerSecond,
            calls,
            events = listened ? calls + 2 : 0,
            elapsedNanoseconds = (long)nanoseconds,
            nanosecondsPerCall = nanoseconds / calls,
            callsPerSecond = calls / elapsed.TotalSeconds,
            nanosecondsPerMessage = nanoseconds / options.Messages,
            messagesPerSecond = options.Messages / elapsed.TotalSeconds,
        };
        await File.WriteAllTextAsync(Path.Combine(options.TruthDirectory, "burst.json"), JsonSerializer.Serialize(report, ReportOptions), cancellationToken)
            .ConfigureAwait(false);
        return 0;
    }
}
