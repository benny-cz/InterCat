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

    /// <summary>How many calls a second the burst paces itself to, each message's two on its schedule; 0 for as fast as the SDK takes them.</summary>
    public int CallsPerSecond { get; init; }
}

/// <summary>
/// The cooperative SDK's cost as an application pays it (M7's overhead guide): one process opens a channel and, on one
/// thread, publishes and consumes each message of a burst as fast as the SDK takes the calls, then reports how long they took
/// and whether a session listened. With no session enabling the provider each call costs its enabled check; with one, it
/// writes an event. It touches no shared memory and reads no InterCat state: the report is the SDK's cost alone.
/// </summary>
internal static class SdkBurstScenario
{
    private static readonly JsonSerializerOptions ReportOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(SdkBurstOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        bool listened = InterCatSdkEventSource.Log.IsEnabled();

        // A paced burst holds each message to its place on the schedule, spinning until it is due; an unpaced one never waits.
        double ticksPerMessage = options.CallsPerSecond == 0 ? 0 : 2.0 * Stopwatch.Frequency / options.CallsPerSecond;
        long started = Stopwatch.GetTimestamp();
        using (InterCatChannel channel = InterCatChannel.Open(Guid.NewGuid(), "sdk-burst", InterCatChannelKind.Other))
        {
            for (long sequence = 1; sequence <= options.Messages; sequence++)
            {
                long due = started + (long)((sequence - 1) * ticksPerMessage);
                while (Stopwatch.GetTimestamp() < due)
                {
                    Thread.SpinWait(16);
                }

                channel.Published(sequence, 256, copiedLength: 256);
                channel.Consumed(sequence, 256, consumer: 1);
            }
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        long calls = 2L * options.Messages;
        var report = new
        {
            scenarioId = SdkBurstOptions.ScenarioId,
            listened,
            options.Messages,
            targetCallsPerSecond = options.CallsPerSecond,
            calls,
            events = listened ? calls + 2 : 0,
            elapsedNanoseconds = (long)(elapsed.TotalMilliseconds * 1_000_000),
            nanosecondsPerCall = elapsed.TotalMilliseconds * 1_000_000 / calls,
            callsPerSecond = calls / elapsed.TotalSeconds,
        };
        await File.WriteAllTextAsync(Path.Combine(options.TruthDirectory, "burst.json"), JsonSerializer.Serialize(report, ReportOptions), cancellationToken)
            .ConfigureAwait(false);
        return 0;
    }
}
