using System.Text.Json;
using Xunit;

namespace InterCat.Analysis.Tests;

/// <summary>
/// The cooperative SDK's published cost (M7's overhead guide, ADR-102's addendum): bursts of an application's publications and
/// consumptions on one thread, with no session listening and while icat record records them under the opt-in sdk profile,
/// measured by tools/InterCat.SdkProbe - held here to the result the repository keeps.
/// </summary>
public sealed class SdkOverheadTests
{
    [Fact(DisplayName = "§15: the cooperative SDK's published cost: a call with no session listening costs tens of nanoseconds, its enabled check alone, and about a microsecond while a capture records it")]
    public void TheSdksCostIsPublished()
    {
        using JsonDocument result = Result();
        JsonElement root = result.RootElement;
        JsonElement disabled = root.GetProperty("disabled");
        JsonElement enabled = root.GetProperty("enabled");
        Assert.All(disabled.GetProperty("bursts").EnumerateArray(), burst => Assert.False(burst.GetProperty("listened").GetBoolean()));
        Assert.All(enabled.GetProperty("bursts").EnumerateArray(), burst => Assert.True(burst.GetProperty("listened").GetBoolean()));

        double quiet = disabled.GetProperty("medianNanosecondsPerCall").GetDouble();
        double recorded = enabled.GetProperty("medianNanosecondsPerCall").GetDouble();
        Assert.InRange(quiet, 1, 200);
        Assert.InRange(recorded, 200, 10_000);
        Assert.True(recorded > 5 * quiet, $"A recorded call ({recorded:0.0} ns) costs as little as a quiet one ({quiet:0.0} ns).");
        Assert.Equal(5, root.GetProperty("rounds").GetInt32());
    }

    [Fact(DisplayName = "R21: a capture of a burst it could not keep whole says it lost records, never that it kept every call - and paced at up to 100,000 calls a second it kept every one")]
    public void ACaptureSaysWhatItLost()
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
            // Whether it kept every call is what its own health says: lossless only where it holds them all.
            bool whole = burst.GetProperty("recordedCalls").GetInt64() == burst.GetProperty("calls").GetInt64();
            Assert.Equal(whole, burst.GetProperty("health").GetProperty("lossFree").GetBoolean());
            Assert.True(burst.GetProperty("recordedCalls").GetInt64() <= burst.GetProperty("calls").GetInt64());
        });

        // Paced, each burst held to its rate, and every call kept up to 100,000 a second.
        JsonElement paced = root.GetProperty("paced");
        Assert.Equal(100_000, paced.GetProperty("highestRateKeptWhole").GetInt32());
        Assert.All(paced.GetProperty("bursts").EnumerateArray(), burst =>
        {
            int target = burst.GetProperty("targetCallsPerSecond").GetInt32();
            Assert.InRange(burst.GetProperty("callsPerSecond").GetDouble(), target * 0.99, target * 1.01);
            Assert.Equal(target <= 100_000, burst.GetProperty("recordedCalls").GetInt64() == burst.GetProperty("calls").GetInt64());
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
        string[] results = [.. Directory.EnumerateDirectories(Path.Combine(root.FullName, "bench", "results"), "sdk-overhead-*").Order(StringComparer.Ordinal)];
        Assert.NotEmpty(results);
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(results[^1], "overhead.json")));
    }
}
