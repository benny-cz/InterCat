using System.Text.Json;
using Xunit;

namespace InterCat.Analysis.Tests;

/// <summary>
/// The cost of the cooperative SDK's batches (ADR-106's third addendum): the same bursts as the SDK's published cost, each
/// call a batch of 64 messages, with no session listening and while icat record records them under the opt-in sdk profile,
/// measured by tools/InterCat.SdkProbe --overhead --batch - held here to the result the repository keeps.
/// </summary>
public sealed class SdkBatchOverheadTests
{
    [Fact(DisplayName = "§15: a batch's call costs about what a single message's does - tens of nanoseconds with no session listening, about a microsecond recorded - so each of its 64 messages, published and consumed in two calls, costs a thirty-second of one")]
    public void ABatchsCostIsSharedByItsMessages()
    {
        using JsonDocument result = Result();
        JsonElement root = result.RootElement;
        Assert.Equal((64, 5, 400_000L), (root.GetProperty("batch").GetInt32(), root.GetProperty("rounds").GetInt32(), root.GetProperty("callsPerBurst").GetInt64()));
        JsonElement disabled = root.GetProperty("disabled");
        JsonElement enabled = root.GetProperty("enabled");
        Assert.All(disabled.GetProperty("bursts").EnumerateArray(), burst => Assert.False(burst.GetProperty("listened").GetBoolean()));
        Assert.All(enabled.GetProperty("bursts").EnumerateArray(), burst => Assert.True(burst.GetProperty("listened").GetBoolean()));

        double quiet = disabled.GetProperty("medianNanosecondsPerCall").GetDouble();
        double recorded = enabled.GetProperty("medianNanosecondsPerCall").GetDouble();
        Assert.InRange(quiet, 1, 300);
        Assert.InRange(recorded, 200, 10_000);
        Assert.True(recorded > 5 * quiet, $"A recorded call ({recorded:0.0} ns) costs as little as a quiet one ({quiet:0.0} ns).");

        // Each message's share: a batch's two calls - its publication and its consumption - over its 64 messages.
        Assert.InRange(disabled.GetProperty("medianNanosecondsPerMessage").GetDouble(), quiet / 32 * 0.95, quiet / 32 * 1.05);
        Assert.InRange(enabled.GetProperty("medianNanosecondsPerMessage").GetDouble(), recorded / 32 * 0.95, recorded / 32 * 1.05);
    }

    [Fact(DisplayName = "R21: a capture of batches it could not keep whole says it lost records, never that it kept every message - and paced at 50,000 calls a second, 1,600,000 messages, it kept every one")]
    public void ACaptureSaysWhatItLostOfBatches()
    {
        using JsonDocument result = Result();
        JsonElement root = result.RootElement;
        JsonElement[] recorded =
        [
            .. root.GetProperty("enabled").GetProperty("bursts").EnumerateArray(),
            .. root.GetProperty("paced").GetProperty("bursts").EnumerateArray(),
        ];
        Assert.NotEmpty(recorded);
        Assert.All(recorded, burst =>
        {
            // Whether it kept every message is what its own health says: lossless only where it holds them all.
            long kept = burst.GetProperty("recordedMessageEvents").GetInt64();
            long said = burst.GetProperty("messageEvents").GetInt64();
            Assert.Equal(kept == said, burst.GetProperty("health").GetProperty("lossFree").GetBoolean());
            Assert.True(kept <= said);
        });

        // Paced, each burst held to its rate, and every message kept up to 50,000 calls - 1,600,000 messages - a second.
        JsonElement paced = root.GetProperty("paced");
        Assert.Equal((50_000, 1_600_000L), (paced.GetProperty("highestRateKeptWhole").GetInt32(), paced.GetProperty("highestMessageRateKeptWhole").GetInt64()));
        Assert.All(paced.GetProperty("bursts").EnumerateArray(), burst =>
        {
            int target = burst.GetProperty("targetCallsPerSecond").GetInt32();
            Assert.InRange(burst.GetProperty("callsPerSecond").GetDouble(), target * 0.99, target * 1.01);
            Assert.InRange(burst.GetProperty("messagesPerSecond").GetDouble(), target * 32 * 0.99, target * 32 * 1.01);
            Assert.Equal(target <= 50_000, burst.GetProperty("recordedMessageEvents").GetInt64() == burst.GetProperty("messageEvents").GetInt64());
        });
    }

    private static JsonDocument Result()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "InterCat.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        string[] results = [.. Directory.EnumerateDirectories(Path.Combine(root.FullName, "bench", "results"), "sdk-batch-overhead-*").Order(StringComparer.Ordinal)];
        Assert.NotEmpty(results);
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(results[^1], "overhead.json")));
    }
}
