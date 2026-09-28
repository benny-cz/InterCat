using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InterCat.Domain;

namespace InterCat.TestWorkloads;

/// <summary>Parameters of one seeded content fixture run.</summary>
internal sealed record ContentFixtureOptions
{
    public const string ScenarioId = "FX-CONTENT-001";

    public required string TruthDirectory { get; init; }
    public int Seed { get; init; } = 20_260_928;
    public int Messages { get; init; } = 24;

    /// <summary>The longest message: past the content fixture profile's 4,096-byte limit, so some are kept in part.</summary>
    public int MaximumMessageBytes { get; init; } = 6_000;

    public int InterMessageDelayMilliseconds { get; init; } = 20;

    /// <summary>How long to wait for a capture to enable the provider, so no message is raised into nothing.</summary>
    public int ListenerWaitSeconds { get; init; } = 15;
}

/// <summary>
/// FX-CONTENT-001 (ADR-036): raises InterCat's content fixture - a seeded run of messages this process "sent" and
/// "received" on three conversations - so the content path is exercised end to end by a workload InterCat owns and by no
/// real application's messages. Half the messages are readable text and half are bytes; the first is empty and the second
/// is longer than the fixture profile's record limit, so a capture keeps whole, cut and empty messages alike. The truth
/// log records each message's conversation, direction, length and SHA-256, never its bytes.
/// </summary>
internal static class ContentFixtureScenario
{
    private static readonly JsonSerializerOptions SummaryOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(ContentFixtureOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);

        // A message raised before a capture enables the provider reaches no session; waiting for one keeps the truth log
        // and the capture about the same messages.
        DateTime deadline = DateTime.UtcNow.AddSeconds(options.ListenerWaitSeconds);
        while (!ContentFixtureEventSource.Log.IsEnabled() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        bool listened = ContentFixtureEventSource.Log.IsEnabled();
        var random = new Random(options.Seed);
        int sent = 0, received = 0;
        long bytes = 0;
        await using (var truth = new TruthLog(
            Path.Combine(options.TruthDirectory, "truth-content.jsonl"), ContentFixtureOptions.ScenarioId, "fixture"))
        {
            truth.Write(TruthEventKind.ProcessStarted);
            for (int index = 0; index < options.Messages; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] message = Message(index, options.MaximumMessageBytes, random);
                long conversation = 1 + (index % 3);
                bool outbound = index % 2 == 0;
                if (outbound)
                {
                    ContentFixtureEventSource.Log.MessageSent(Environment.ProcessId, conversation, message);
                    sent++;
                }
                else
                {
                    ContentFixtureEventSource.Log.MessageReceived(Environment.ProcessId, conversation, message);
                    received++;
                }

                bytes += message.Length;
                truth.Write(
                    outbound ? TruthEventKind.MessageSent : TruthEventKind.MessageReceived,
                    callId: conversation,
                    declaredBytes: message.Length,
                    completedBytes: message.Length,
                    resourceName: "sha256:" + Convert.ToHexStringLower(SHA256.HashData(message)));
                await Task.Delay(options.InterMessageDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }

            truth.Write(TruthEventKind.ProcessExiting);
        }

        var summary = new
        {
            scenarioId = ContentFixtureOptions.ScenarioId,
            provider = ContentFixtureEventSource.ProviderName,
            providerGuid = System.Diagnostics.Tracing.EventSource.GetGuid(typeof(ContentFixtureEventSource)),
            processId = Environment.ProcessId,
            options.Seed,
            options.Messages,
            sent,
            received,
            bytes,
            options.MaximumMessageBytes,
            listened,
            note = "Each message is one fixture event; the truth log names its length and SHA-256, never its bytes.",
        };
        await File.WriteAllTextAsync(
            Path.Combine(options.TruthDirectory, "scenario.json"),
            JsonSerializer.Serialize(summary, SummaryOptions),
            cancellationToken).ConfigureAwait(false);

        if (!listened)
        {
            await Console.Error.WriteLineAsync(
                "No capture enabled the content fixture provider within the wait; its messages reached no session.")
                .ConfigureAwait(false);
            return 3;
        }

        return 0;
    }

    /// <summary>
    /// The seeded message at <paramref name="index"/>: the first empty, the second as long as allowed, and after them text
    /// and bytes in turn, of seeded lengths.
    /// </summary>
    private static byte[] Message(int index, int maximumBytes, Random random)
    {
        int length = index switch
        {
            0 => 0,
            1 => maximumBytes,
            _ => random.Next(1, maximumBytes / 4),
        };
        if (index % 4 is 2 or 3)
        {
            byte[] noise = new byte[length];
            random.NextBytes(noise);
            return noise;
        }

        var text = new StringBuilder(length + 64);
        text.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"InterCat content fixture message {index} on conversation {1 + (index % 3)}. ");
        while (text.Length < length)
        {
            text.Append("The quick brown fox jumps over the lazy dog. ");
        }

        return Encoding.UTF8.GetBytes(text.ToString(0, length));
    }
}
