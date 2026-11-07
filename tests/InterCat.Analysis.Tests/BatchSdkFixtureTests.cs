using System.Text.Json;
using Xunit;

namespace InterCat.Analysis.Tests;

/// <summary>
/// FX-SDK-005 and FX-SDK-006 recorded through the product (ADR-106): the ring of FX-SDK-001 and the native application of
/// FX-SDK-002 saying their messages in batches - each a run of messages in one call of the cooperative SDK - captured by
/// icat record under the opt-in sdk profile and read as icat messages reads it, each run held to the truth's messages one by
/// one, the truth logging every message by itself.
/// </summary>
public sealed class BatchSdkFixtureTests
{
    [Theory(DisplayName = "P4: FX-SDK-005 and FX-SDK-006 recorded through the product: a batch's messages are each one message of the run it said, never one message nor estimated - every message and every reader's consumption of it the truth's, by process, each batch's lengths together the truth's lengths of its messages summed, nothing lost")]
    [InlineData("FX-SDK-005", 161, 480, 21, 20, 160, 81, 3)]
    [InlineData("FX-SDK-006", 131, 130, 14, 13, 130, 27, 0)]
    public void ABatchsMessagesAreTheTruths(string fixture, int published, int consumed, int rows, int runs, int batched, int records, int kind)
    {
        using JsonDocument measurement = Measurement(fixture);
        Assert.Equal(fixture, measurement.RootElement.GetProperty("fixture").GetString());
        JsonElement[] measured = [.. measurement.RootElement.GetProperty("runs").EnumerateArray()];
        Assert.Equal(3, measured.Length);
        Assert.All(measured, run =>
        {
            // Every message of every run is the truth's, published once by the process that logged it, each batch's lengths
            // together its messages' summed; every reader's consumption of each the truth's alike.
            Assert.Equal((published, published, published, published), (Int(run, "truthPublications"), Int(run, "publications"),
                Int(run, "matchedPublications"), Int(run, "messages")));
            Assert.Equal((consumed, consumed, consumed), (Int(run, "truthConsumptions"), Int(run, "consumptions"), Int(run, "matchedConsumptions")));

            // The batches are runs, each one row of its record, never spread into messages no record tells apart; the one
            // message said alone keeps the bytes it copied.
            Assert.Equal((rows, runs, batched, 1), (Int(run, "rows"), Int(run, "runs"), Int(run, "messagesInBatches"), Int(run, "matchedCopies")));
            Assert.Equal((records, records, records, 0), (Int(run, "records"), Int(run, "boundRecords"), Int(run, "recordsWithSourceTicks"),
                Int(run, "recordsNamingNoMessage")));
            Assert.True(run.GetProperty("named").GetBoolean());
            Assert.Equal([kind, kind], Ints(run, "kinds"));

            // Nothing lost, dropped or undecodable on the way.
            JsonElement health = run.GetProperty("health");
            Assert.Equal((0, 0, 0, 0), (Int(health, "providerReportedEventLoss"), Int(health, "consumerReportedBufferLoss"),
                Int(health, "applicationDrops"), Int(health, "undecodableRecords")));
            Assert.Equal(Int(health, "observedRecords"), Int(health, "admittedRecords"));
        });

        // The truth logged every message by itself, each with the batch it was said in as its call, and the last alone.
        JsonElement[] sent = [.. Records(fixture, "MessageSent")];
        Assert.Equal(published, sent.Length);
        Assert.Equal((published - 1, 1), (sent.Count(record => record.GetProperty("callId").ValueKind == JsonValueKind.Number),
            sent.Count(record => record.GetProperty("callId").ValueKind == JsonValueKind.Null)));
    }

    [Theory(DisplayName = "R3: FX-SDK-005's and FX-SDK-006's message said alone that no reader read stays open beside their batches, and the channel made again is a second epoch whose sequence starts at 1 again")]
    [InlineData("FX-SDK-005", 160, 120, 41, 3)]
    [InlineData("FX-SDK-006", 130, 100, 31, 1)]
    public void AnUnreadMessageStaysOpenBesideTheBatches(string fixture, int consumedMessages, int first, int second, int readers)
    {
        using JsonDocument measurement = Measurement(fixture);
        Assert.All(measurement.RootElement.GetProperty("runs").EnumerateArray(), run =>
        {
            JsonElement states = run.GetProperty("states");
            Assert.Equal((consumedMessages, 1, 0, 0), (Int(states, "Consumed"), Int(states, "Unconsumed"), Int(states, "PublicationUnseen"), Int(states, "Conflicting")));
            Assert.Equal((1, true), (Int(run, "truthUnread"), run.GetProperty("unreadOpen").GetBoolean()));
            Assert.Equal([1L, 2L], run.GetProperty("epochs").EnumerateArray().Select(epoch => epoch.GetInt64()));
            Assert.Equal([first, second], Ints(run, "epochMessages"));
            Assert.Equal([readers, readers], Ints(run, "readers"));
        });
    }

    [Fact(DisplayName = "R3: FX-SDK-006's native operations and announcement read as FX-SDK-002's: each operation one start under its parent and one completion with its status, and the library's schema announced once, read at the layout the capture knows")]
    public void TheNativeBatchesKeepTheirOperations()
    {
        using JsonDocument measurement = Measurement("FX-SDK-006");
        Assert.All(measurement.RootElement.GetProperty("runs").EnumerateArray(), run =>
        {
            Assert.Equal((3, 4, 3, 3, 3, 3), (Int(run, "truthOperations"), Int(run, "truthCompletionCalls"), Int(run, "operationStarts"),
                Int(run, "matchedStarts"), Int(run, "operationCompletions"), Int(run, "matchedCompletions")));
            Assert.Equal((1, 1, true), (Int(run, "announcements"), Int(run, "processesAnnounced"), run.GetProperty("announcedLibrary").GetBoolean()));
        });
        using JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(Evidence("FX-SDK-006"), "scenario.json")));
        Assert.Equal(("native-1.1", 10, 0), (scenario.RootElement.GetProperty("library").GetString(), Int(scenario.RootElement, "batch"),
            Int(scenario.RootElement, "unwritten")));
    }

    private static int Int(JsonElement element, string name) => element.GetProperty(name).GetInt32();

    private static int[] Ints(JsonElement element, string name) => [.. element.GetProperty(name).EnumerateArray().Select(value => value.GetInt32())];

    private static JsonDocument Measurement(string fixture) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Evidence(fixture), "measurement.json")));

    /// <summary>The publisher's truth log's records of one kind that name a cooperative message.</summary>
    private static IEnumerable<JsonElement> Records(string fixture, string kind)
    {
        string log = fixture == "FX-SDK-005" ? "truth-producer.jsonl" : "truth-native.jsonl";
        foreach (string line in File.ReadLines(Path.Combine(Evidence(fixture), log)))
        {
            using JsonDocument record = JsonDocument.Parse(line);
            if (record.RootElement.GetProperty("kind").GetString() == kind && record.RootElement.TryGetProperty("message", out _))
            {
                yield return record.RootElement.Clone();
            }
        }
    }

    private static string Evidence(string fixture)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "InterCat.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return Path.Combine(root.FullName, "fixtures", fixture, "evidence");
    }
}
