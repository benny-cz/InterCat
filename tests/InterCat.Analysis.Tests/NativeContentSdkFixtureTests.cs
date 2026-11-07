using System.Text.Json;
using Xunit;

namespace InterCat.Analysis.Tests;

/// <summary>
/// FX-SDK-008 recorded through the product (ADR-107's third addendum): FX-SDK-002's native application publishing each
/// message with its bytes through intercat_sdk.h, a .NET decoy beside it doing the same, captured by icat record keeping the
/// SDK's content under a request that names the native process alone - held here to the runs' measurement and the first
/// run's truth, whose digests the sample took through Windows' own hashing, never a byte.
/// </summary>
public sealed class NativeContentSdkFixtureTests
{
    /// <summary>The record limit the runs kept content under, and the most bytes the library says as a message's content.</summary>
    private const int Limit = 1_024;

    private const int Most = 32_768;

    /// <summary>The channel's two epochs, each with one reader, opened and closed once.</summary>
    private static readonly string[] OneEach = ["readers", "openings", "closings"];

    [Fact(DisplayName = "I21: FX-SDK-008 recorded through the product keeps each message the native library said with its bytes as the truth's, at either epoch - whole where the record limit holds it, cut to the limit where not, by the SHA-256 the sample took through Windows' own hashing - and none of the one it never said with them")]
    public void ContentIsTheTruths()
    {
        // The truth: FX-SDK-002's 131 messages, epoch 1's first five at the edges - nothing, the limit and a byte past it, the
        // most and a byte past it - each said with its bytes where it holds at most the most, as the library itself said.
        Said[] said = Truth();
        Assert.Equal(131, said.Length);
        Assert.Equal((100, 31), (said.Count(message => message.Epoch == 1), said.Count(message => message.Epoch == 2)));
        Assert.Equal([0, Limit, Limit + 1, Most, Most + 1], said.Take(5).Select(message => message.Length));
        Assert.All(said, message => Assert.Equal(message.Length <= Most, message.ContentSaid));
        Assert.All(said, message => Assert.Equal(message.Length > Limit, message.PrefixSha256 is not null));
        int whole = said.Count(message => message.ContentSaid && message.Length <= Limit);
        int cut = said.Count(message => message.ContentSaid && message.Length > Limit);
        Assert.Equal((130, 27, 103), (whole + cut, whole, cut));

        using JsonDocument measurement = Measurement();
        JsonElement root = measurement.RootElement;
        Assert.Equal(("FX-SDK-008", "content", Limit), (root.GetProperty("fixture").GetString(), root.GetProperty("profile").GetString(),
            root.GetProperty("recordLimit").GetInt32()));
        JsonElement[] runs = [.. root.GetProperty("runs").EnumerateArray()];
        Assert.Equal(3, runs.Length);
        Assert.All(runs, run =>
        {
            Assert.Equal((131, 130, whole, cut, 1), (Int(run, "truthMessages"), Int(run, "truthContentsSaid"), Int(run, "truthWholeWithinLimit"),
                Int(run, "truthCutToLimit"), Int(run, "truthPastTheMost")));
            Assert.Equal((0, 0), (Int(run, "truthSaidPastTheMost"), Int(run, "truthUnsaidWithinTheMost")));

            // Each content said is one content record of its message, at its epoch, bound to the native process, whose
            // fragment is the truth's: whole, or cut to the limit with the message's length as its original.
            Assert.Equal((130, 130, 130), (Int(run, "contentRecords"), Int(run, "contentRecordsBound"), Int(run, "fragments")));
            Assert.Equal((whole, cut, 0), (Int(run, "matchedWhole"), Int(run, "matchedCut"), Int(run, "contentForUnsaid")));

            JsonElement health = run.GetProperty("health");
            Assert.True(health.GetProperty("lossFree").GetBoolean());
            Assert.Equal((0L, 0L, 0L, 0L), (Long(health, "providerReportedEventLoss"), Long(health, "consumerReportedBufferLoss"),
                Long(health, "applicationDrops"), Long(health, "undecodableRecords")));
        });
    }

    [Fact(DisplayName = "R17: FX-SDK-008's content is admitted per record before it is kept - the .NET decoy beside the named native process, publishing with its bytes, left no record and no byte in the session - and no message's byte is anywhere but the content kept")]
    public void NothingIsKeptButWhatTheRequestNamed()
    {
        using JsonDocument scenario = Scenario();
        string marker = scenario.RootElement.GetProperty("marker").GetString()!;
        Assert.Equal(("FX-SDK-008:", "native", Limit, Most, true), (marker, scenario.RootElement.GetProperty("role").GetString(),
            scenario.RootElement.GetProperty("recordLimit").GetInt32(), scenario.RootElement.GetProperty("maximumContent").GetInt32(),
            scenario.RootElement.GetProperty("content").GetBoolean()));
        int marked = Truth().Count(message => message.ContentSaid && message.Length >= marker.Length);
        Assert.Equal(129, marked);

        using JsonDocument measurement = Measurement();
        Assert.All(measurement.RootElement.GetProperty("runs").EnumerateArray(), run =>
        {
            Assert.Equal((0, 0), (Int(run, "decoyRecords"), Int(run, "decoyMarkerAnywhere")));
            Assert.Equal((marked, 0), (Int(run, "markerInContent"), Int(run, "markerElsewhere")));
        });
    }

    [Fact(DisplayName = "§15: FX-SDK-008's messages and operations read as FX-SDK-002's beside their content: every publication and consumption the truth's at either epoch, the one no reader read open, each operation one start under its parent and one completion, the library native-1.2")]
    public void ItsMessagesAreFxSdk002s()
    {
        using JsonDocument scenario = Scenario();
        Assert.Equal(("FX-SDK-008", "native-1.2"), (scenario.RootElement.GetProperty("scenarioId").GetString(),
            scenario.RootElement.GetProperty("library").GetString()));
        using JsonDocument measurement = Measurement();
        Assert.All(measurement.RootElement.GetProperty("runs").EnumerateArray(), run =>
        {
            Assert.Equal((131, 131, 131, 131), (Int(run, "truthPublications"), Int(run, "publications"), Int(run, "matchedPublications"),
                Int(run, "matchedCopies")));
            Assert.Equal((130, 130, 130, 1), (Int(run, "truthConsumptions"), Int(run, "consumptions"), Int(run, "matchedConsumptions"),
                Int(run, "truthUnread")));
            Assert.Equal((130, 1), (Int(run.GetProperty("states"), "Consumed"), Int(run.GetProperty("states"), "Unconsumed")));
            Assert.True(run.GetProperty("unreadOpen").GetBoolean());
            Assert.Equal([1, 2], Ints(run, "epochs"));
            Assert.Equal([100, 31], Ints(run, "epochMessages"));
            Assert.All(OneEach, name => Assert.Equal([1, 1], Ints(run, name)));
            Assert.Equal((261, 261, 261, 0), (Int(run, "records"), Int(run, "boundRecords"), Int(run, "recordsWithSourceTicks"),
                Int(run, "recordsNamingNoMessage")));
            Assert.Equal((3, 3, 3, 3), (Int(run, "truthOperations"), Int(run, "operationStarts"), Int(run, "matchedStarts"),
                Int(run, "matchedCompletions")));
            Assert.Equal((1, true), (Int(run, "announcements"), run.GetProperty("announcedLibrary").GetBoolean()));
        });
    }

    private static Said[] Truth() =>
    [
        .. File.ReadLines(Path.Combine(Evidence(), "content.jsonl")).Select(line =>
        {
            using JsonDocument said = JsonDocument.Parse(line);
            JsonElement root = said.RootElement;
            return new Said(root.GetProperty("epoch").GetInt64(), root.GetProperty("sequence").GetInt64(), root.GetProperty("length").GetInt32(),
                root.GetProperty("prefixSha256").ValueKind == JsonValueKind.Null ? null : root.GetProperty("prefixSha256").GetString(),
                root.GetProperty("contentSaid").GetBoolean());
        }),
    ];

    private static int Int(JsonElement element, string name) => element.GetProperty(name).GetInt32();

    private static long Long(JsonElement element, string name) => element.GetProperty(name).GetInt64();

    private static int[] Ints(JsonElement element, string name) => [.. element.GetProperty(name).EnumerateArray().Select(value => value.GetInt32())];

    private static JsonDocument Measurement() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Evidence(), "measurement.json")));

    private static JsonDocument Scenario() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Evidence(), "scenario.json")));

    private static string Evidence()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "InterCat.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return Path.Combine(root.FullName, "fixtures", "FX-SDK-008", "evidence");
    }

    /// <summary>A message's content as the sample logged it: where it is, its length, its kept prefix's digest and whether it was said.</summary>
    private sealed record Said(long Epoch, long Sequence, int Length, string? PrefixSha256, bool ContentSaid);
}
