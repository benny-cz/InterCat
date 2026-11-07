using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InterCat.Domain;
using InterCat.Sdk;

namespace InterCat.TestWorkloads;

/// <summary>Parameters of FX-SDK-007's publisher, or of the decoy beside it.</summary>
internal sealed record SdkContentFixtureOptions
{
    public const string ScenarioId = "FX-SDK-007";

    /// <summary>The marker each message of the publisher begins with, where it fits: bytes no metadata holds.</summary>
    public const string Marker = "FX-SDK-007:";

    /// <summary>The decoy's marker, which no record of a capture naming the publisher may hold.</summary>
    public const string DecoyMarker = "FX-SDK-007-DECOY:";

    public required string TruthDirectory { get; init; }

    /// <summary>The record limit the capture keeps content under, so the truth states each longer message's kept prefix.</summary>
    public int RecordLimit { get; init; } = 4_096;

    /// <summary>How many messages of seeded lengths follow the fixed edges.</summary>
    public int Messages { get; init; } = 120;

    public int Seed { get; init; } = 20_261_107;

    /// <summary>Whether this process is the decoy: its own channel and marker, never named by the capture.</summary>
    public bool Decoy { get; init; }

    /// <summary>Whether the process waits for a line on its standard input, so a capture can name it before it says anything.</summary>
    public bool WaitForStart { get; init; }
}

/// <summary>
/// FX-SDK-007 (ADR-107's second addendum): one process publishing messages with their bytes through InterCat's SDK, each
/// consumed by its reader 1 - lengths at the edges a capture's content keeps whole, cuts or never hears (nothing, a byte,
/// the record limit and either side of it, the most the SDK says and either side of it, twice it) and then seeded ones,
/// each message's bytes its marker and sequence where they fit and seeded noise after - and logging each publication and
/// consumption as the SDK truth workloads do, and each message's length, SHA-256 and the SHA-256 of the prefix the record
/// limit keeps, with whether its content was said, to content.jsonl: never a byte. The decoy says its own messages under
/// its own marker on its own channel. It reads no InterCat state.
/// </summary>
internal static class SdkContentFixtureScenario
{
    /// <summary>The reader that consumes each message, the publisher itself.</summary>
    private const long Reader = 1;

    private static readonly JsonSerializerOptions SummaryOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(SdkContentFixtureOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        if (options.WaitForStart)
        {
            _ = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }

        string role = options.Decoy ? "decoy" : "publisher";
        string marker = options.Decoy ? SdkContentFixtureOptions.DecoyMarker : SdkContentFixtureOptions.Marker;
        var channelId = Guid.NewGuid();
        string channelName = options.Decoy ? "content-decoy" : "content-fixture";
        int limit = options.RecordLimit;
        int[] edges = [0, 1, 15, 16, limit - 1, limit, limit + 1, InterCatChannel.MaximumContent - 1, InterCatChannel.MaximumContent,
            InterCatChannel.MaximumContent + 1, 2 * InterCatChannel.MaximumContent];
        var random = new Random(options.Seed + (options.Decoy ? 1 : 0));
        int[] lengths = [.. edges, .. Enumerable.Range(0, options.Messages).Select(_ => random.Next(17, 8_193))];

        long said = 0;
        await using (var truth = new TruthLog(Path.Combine(options.TruthDirectory, $"truth-{role}.jsonl"), SdkContentFixtureOptions.ScenarioId, role))
        await using (var content = new StreamWriter(Path.Combine(options.TruthDirectory, "content.jsonl")))
        {
            truth.Write(TruthEventKind.ProcessStarted);
            using (InterCatChannel channel = InterCatChannel.Open(channelId, channelName, InterCatChannelKind.Other))
            {
                for (int at = 0; at < lengths.Length; at++)
                {
                    long sequence = at + 1;
                    byte[] message = Message(marker, options.Seed, sequence, lengths[at]);
                    bool contentSaid = channel.Published(sequence, message);
                    said += contentSaid ? 1 : 0;
                    // No copy is said beside the message's length, so none is logged.
                    truth.Write(TruthEventKind.MessageSent, declaredBytes: message.Length, resourceName: channelName,
                        message: new(channelId, 1, sequence, null));
                    channel.Consumed(sequence, message.Length, consumer: Reader);
                    truth.Write(TruthEventKind.MessageReceived, declaredBytes: message.Length, resourceName: channelName,
                        message: new(channelId, 1, sequence, Reader));
                    var line = new
                    {
                        sequence,
                        length = message.Length,
                        sha256 = Convert.ToHexStringLower(SHA256.HashData(message)),
                        prefixSha256 = message.Length > limit ? Convert.ToHexStringLower(SHA256.HashData(message.AsSpan(0, limit))) : null,
                        contentSaid,
                    };
                    await content.WriteLineAsync(JsonSerializer.Serialize(line)).ConfigureAwait(false);
                }
            }

            truth.Write(TruthEventKind.ProcessExiting);
        }

        var summary = new
        {
            scenarioId = SdkContentFixtureOptions.ScenarioId,
            role,
            channel = channelId,
            channelName,
            library = InterCatSdkEventSource.Library,
            marker,
            options.RecordLimit,
            maximumContent = InterCatChannel.MaximumContent,
            options.Seed,
            messages = lengths.Length,
            contentsSaid = said,
        };
        await File.WriteAllTextAsync(Path.Combine(options.TruthDirectory, "scenario.json"), JsonSerializer.Serialize(summary, SummaryOptions),
            cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// A message's bytes: its marker and its sequence where they fit - bytes no metadata holds, which a capture keeps only as
    /// content - and seeded noise after, so no two messages hold the same bytes.
    /// </summary>
    public static byte[] Message(string marker, int seed, long sequence, int length)
    {
        byte[] named = Encoding.ASCII.GetBytes(marker + sequence.ToString("D6", CultureInfo.InvariantCulture) + ":");
        var bytes = new byte[length];
        ulong state = unchecked(((ulong)seed * 0x9E37_79B9_7F4A_7C15UL) ^ ((ulong)sequence * 0xBF58_476D_1CE4_E5B9UL)) | 1;
        for (int at = 0; at < length; at++)
        {
            if (at < named.Length)
            {
                bytes[at] = named[at];
                continue;
            }

            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            bytes[at] = unchecked((byte)state);
        }

        return bytes;
    }
}
