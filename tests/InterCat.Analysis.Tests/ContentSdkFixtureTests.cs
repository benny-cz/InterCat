using System.Text.Json;
using Xunit;

namespace InterCat.Analysis.Tests;

/// <summary>
/// FX-SDK-007 recorded through the product (ADR-107's second addendum): one process publishing messages with their bytes
/// through InterCat's cooperative SDK, a decoy beside it doing the same, captured by icat record keeping the SDK's content
/// under a request that names the publisher alone, each run's content read as the product's viewer reads it - held here to
/// the runs' measurement and the first run's truth, which holds each message's length and SHA-256 digests, never a byte.
/// </summary>
public sealed class ContentSdkFixtureTests
{
    /// <summary>The record limit the runs kept content under, and the most bytes the SDK says as a message's content.</summary>
    private const int Limit = 4_096;

    private const int Most = 32_768;

    /// <summary>What the channel was at its one epoch: one epoch, one reader, opened and closed once.</summary>
    private static readonly string[] OneEach = ["epochs", "readers", "openings", "closings"];

    [Fact(DisplayName = "I21: FX-SDK-007 recorded through the product keeps each message the SDK said with its bytes as the truth's - whole where the record limit holds it, cut to the limit where not, its bytes or its kept prefix the truth's by SHA-256 - and none of a message the SDK never said with them")]
    public void ContentIsTheTruths()
    {
        // The truth: 131 messages - the edges first, nothing, a byte, either side of the limit and of the most, twice the
        // most - each said with its bytes where it holds at most the most, and a kept prefix's digest where it is longer
        // than the limit.
        Said[] said = Truth();
        Assert.Equal([0, 1, 15, 16, Limit - 1, Limit, Limit + 1, Most - 1, Most, Most + 1, 2 * Most], said.Take(11).Select(message => message.Length));
        Assert.Equal(131, said.Length);
        Assert.All(said, message => Assert.Equal(message.Length <= Most, message.ContentSaid));
        Assert.All(said, message => Assert.Equal(message.Length > Limit, message.PrefixSha256 is not null));
        int whole = said.Count(message => message.ContentSaid && message.Length <= Limit);
        int cut = said.Count(message => message.ContentSaid && message.Length > Limit);
        Assert.Equal((129, 54, 75), (whole + cut, whole, cut));

        using JsonDocument measurement = Measurement();
        JsonElement root = measurement.RootElement;
        Assert.Equal(("FX-SDK-007", "content", Limit), (root.GetProperty("fixture").GetString(), root.GetProperty("profile").GetString(),
            root.GetProperty("recordLimit").GetInt32()));
        JsonElement[] runs = [.. root.GetProperty("runs").EnumerateArray()];
        Assert.Equal(3, runs.Length);
        Assert.All(runs, run =>
        {
            Assert.Equal((131, 129, whole, cut, 2), (Int(run, "truthMessages"), Int(run, "truthContentsSaid"), Int(run, "truthWholeWithinLimit"),
                Int(run, "truthCutToLimit"), Int(run, "truthPastTheMost")));

            // Each content said is one content record of its message, bound to the publisher, whose fragment is the truth's:
            // whole, its bytes the message's, or cut to the limit, its original length the message's and its bytes its prefix.
            Assert.Equal((129, 129, 129), (Int(run, "contentRecords"), Int(run, "contentRecordsBound"), Int(run, "fragments")));
            Assert.Equal((whole, cut, 0), (Int(run, "matchedWhole"), Int(run, "matchedCut"), Int(run, "contentForUnsaid")));

            // And the capture lost nothing, so none is missing for want of a record.
            JsonElement health = run.GetProperty("health");
            Assert.True(health.GetProperty("lossFree").GetBoolean());
            Assert.Equal((0L, 0L, 0L, 0L), (Long(health, "providerReportedEventLoss"), Long(health, "consumerReportedBufferLoss"),
                Long(health, "applicationDrops"), Long(health, "undecodableRecords")));
        });
    }

    [Fact(DisplayName = "R17: FX-SDK-007's content is admitted per record before it is kept - the decoy beside the named publisher, publishing with its bytes, left no record and no byte in the session - and no message's byte is anywhere but the content kept")]
    public void NothingIsKeptButWhatTheRequestNamed()
    {
        // Every message of at least the marker's length begins with it, and the decoy's with its own.
        using JsonDocument scenario = Scenario();
        string marker = scenario.RootElement.GetProperty("marker").GetString()!;
        Assert.Equal(("FX-SDK-007:", "publisher", Limit, Most), (marker, scenario.RootElement.GetProperty("role").GetString(),
            scenario.RootElement.GetProperty("recordLimit").GetInt32(), scenario.RootElement.GetProperty("maximumContent").GetInt32()));
        int marked = Truth().Count(message => message.ContentSaid && message.Length >= marker.Length);

        using JsonDocument measurement = Measurement();
        Assert.All(measurement.RootElement.GetProperty("runs").EnumerateArray(), run =>
        {
            // The decoy's records never reached the capture, nor its marker the session's files.
            Assert.Equal((0, 0), (Int(run, "decoyRecords"), Int(run, "decoyMarkerAnywhere")));

            // The publisher's marker is in its content kept, once a message that holds it, and in no other file: no journal,
            // segment, manifest or ledger holds a byte of a message.
            Assert.Equal((marked, 0), (Int(run, "markerInContent"), Int(run, "markerElsewhere")));
        });
        Assert.Equal(127, marked);
    }

    [Fact(DisplayName = "§15: FX-SDK-007's messages read as any SDK fixture's beside their content: every publication and consumption the truth's by its length, copy and process, each record bound and carrying its clock, one epoch, nothing left open")]
    public void ItsMessagesAreTheTruths()
    {
        Assert.Equal(131, Records("MessageSent").Count());
        Assert.Equal(131, Records("MessageReceived").Count());
        using JsonDocument measurement = Measurement();
        Assert.All(measurement.RootElement.GetProperty("runs").EnumerateArray(), run =>
        {
            Assert.Equal((131, 131, 131, 131, 131), (Int(run, "truthPublications"), Int(run, "publications"), Int(run, "matchedPublications"),
                Int(run, "matchedCopies"), Int(run, "messages")));
            Assert.Equal((131, 131, 131), (Int(run, "truthConsumptions"), Int(run, "consumptions"), Int(run, "matchedConsumptions")));
            Assert.Equal((262, 262, 262, 0), (Int(run, "records"), Int(run, "boundRecords"), Int(run, "recordsWithSourceTicks"),
                Int(run, "recordsNamingNoMessage")));
            Assert.Equal((131, 0), (Int(run.GetProperty("states"), "Consumed"), Int(run.GetProperty("states"), "Unconsumed")));
            Assert.All(OneEach, name => Assert.Equal([1], Ints(run, name)));
            Assert.True(run.GetProperty("named").GetBoolean());
            Assert.Equal((1, true), (Int(run, "announcements"), run.GetProperty("announcedLibrary").GetBoolean()));
        });
    }

    private static Said[] Truth() =>
    [
        .. File.ReadLines(Path.Combine(Evidence(), "content.jsonl")).Select(line =>
        {
            using JsonDocument said = JsonDocument.Parse(line);
            JsonElement root = said.RootElement;
            return new Said(root.GetProperty("sequence").GetInt64(), root.GetProperty("length").GetInt32(), root.GetProperty("sha256").GetString()!,
                root.GetProperty("prefixSha256").ValueKind == JsonValueKind.Null ? null : root.GetProperty("prefixSha256").GetString(),
                root.GetProperty("contentSaid").GetBoolean());
        }),
    ];

    /// <summary>The publisher's truth log's records of one kind that name a cooperative message.</summary>
    private static IEnumerable<JsonElement> Records(string kind)
    {
        foreach (string line in File.ReadLines(Path.Combine(Evidence(), "truth-publisher.jsonl")))
        {
            using JsonDocument record = JsonDocument.Parse(line);
            if (record.RootElement.GetProperty("kind").GetString() == kind && record.RootElement.TryGetProperty("message", out _))
            {
                yield return record.RootElement.Clone();
            }
        }
    }

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
        return Path.Combine(root.FullName, "fixtures", "FX-SDK-007", "evidence");
    }

    /// <summary>A message's content as the publisher logged it: its length, its digests and whether it was said, never a byte.</summary>
    private sealed record Said(long Sequence, int Length, string Sha256, string? PrefixSha256, bool ContentSaid);
}
