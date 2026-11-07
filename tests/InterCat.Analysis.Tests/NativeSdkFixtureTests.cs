using System.Text.Json;
using Xunit;

namespace InterCat.Analysis.Tests;

/// <summary>
/// FX-SDK-002 recorded through the product (ADR-105): a native application saying what it publishes, consumes and does
/// through the SDK's C library, <c>sdk/native/include/intercat_sdk.h</c>, captured by icat record under the opt-in sdk
/// profile and read as icat messages reads it - held here to the runs' measurement and the first run's truth log and
/// scenario.
/// </summary>
public sealed class NativeSdkFixtureTests
{
    [Fact(DisplayName = "P4: FX-SDK-002 recorded through the product: a native application's messages, said through the SDK's C library, read as a .NET application's do - every message it published at either epoch one message and its reader's consumption of each one consumption, the truth's by length and process, nothing lost")]
    public void ANativeApplicationsMessagesAreTheTruths()
    {
        using JsonDocument measurement = Measurement();
        Assert.Equal("FX-SDK-002", measurement.RootElement.GetProperty("fixture").GetString());
        JsonElement[] runs = [.. measurement.RootElement.GetProperty("runs").EnumerateArray()];
        Assert.Equal(3, runs.Length);
        Assert.All(runs, run =>
        {
            // Every publication and consumption the truth logged is one the session holds, of the same length and process.
            Assert.Equal((131, 131, 131, 131), (Int(run, "truthPublications"), Int(run, "publications"), Int(run, "matchedPublications"), Int(run, "messages")));
            Assert.Equal((130, 130, 130), (Int(run, "truthConsumptions"), Int(run, "consumptions"), Int(run, "matchedConsumptions")));

            // Each record bound to its process and carrying its clock reading; none naming no message; the channel named
            // and of the kind it was opened with.
            Assert.Equal((261, 261, 261, 0), (Int(run, "records"), Int(run, "boundRecords"), Int(run, "recordsWithSourceTicks"), Int(run, "recordsNamingNoMessage")));
            Assert.True(run.GetProperty("named").GetBoolean());
            Assert.Equal([0, 0], run.GetProperty("kinds").EnumerateArray().Select(kind => kind.GetInt32()));
            Assert.Equal([1, 1], run.GetProperty("readers").EnumerateArray().Select(readers => readers.GetInt32()));

            // The process announced its schema to the session once, read at the layout the capture knows: its library's
            // version, its last field, the native library's.
            Assert.Equal((1, 1, true), (Int(run, "announcements"), Int(run, "processesAnnounced"), run.GetProperty("announcedLibrary").GetBoolean()));

            // Nothing lost, dropped or undecodable on the way.
            JsonElement health = run.GetProperty("health");
            Assert.Equal((0, 0, 0, 0), (Int(health, "providerReportedEventLoss"), Int(health, "consumerReportedBufferLoss"),
                Int(health, "applicationDrops"), Int(health, "undecodableRecords")));
            Assert.Equal(Int(health, "observedRecords"), Int(health, "admittedRecords"));
        });

        // The first run's truth, kept beside it, logged what its counts say, and the library wrote every event it was given.
        (int published, int unread, _) = Published();
        Assert.Equal((131, 1), (published, unread));
        Assert.Equal(130, Messages("MessageReceived").Count());
        using JsonDocument scenario = Scenario();
        Assert.Equal(("native-1.0", 0), (scenario.RootElement.GetProperty("library").GetString(), Int(scenario.RootElement, "unwritten")));
    }

    [Fact(DisplayName = "R3: FX-SDK-002's message no reader read stays open, never said consumed, and the channel made again is a second epoch whose sequence starts at 1 again, as a .NET application's")]
    public void ANativeUnreadMessageStaysOpenAndARestartIsAnEpoch()
    {
        using JsonDocument measurement = Measurement();
        Assert.All(measurement.RootElement.GetProperty("runs").EnumerateArray(), run =>
        {
            JsonElement states = run.GetProperty("states");
            Assert.Equal((130, 1, 0, 0), (Int(states, "Consumed"), Int(states, "Unconsumed"), Int(states, "PublicationUnseen"), Int(states, "Conflicting")));
            Assert.Equal((1, true), (Int(run, "truthUnread"), run.GetProperty("unreadOpen").GetBoolean()));
            Assert.Equal([1L, 2L], run.GetProperty("epochs").EnumerateArray().Select(epoch => epoch.GetInt64()));
            Assert.Equal([100, 31], run.GetProperty("epochMessages").EnumerateArray().Select(messages => messages.GetInt32()));
        });

        // The truth: epoch 1 numbered from 1 to 100, epoch 2 from 1 again, its last message the one logged unread.
        (_, _, (long Epoch, long Sequence, bool Unread)[] messages) = Published();
        Assert.Equal((1L, 100L), (messages.Where(message => message.Epoch == 1).Min(message => message.Sequence), messages.Where(message => message.Epoch == 1).Max(message => message.Sequence)));
        Assert.Equal((1L, 31L), (messages.Where(message => message.Epoch == 2).Min(message => message.Sequence), messages.Where(message => message.Epoch == 2).Max(message => message.Sequence)));
        Assert.Equal((2L, 31L), messages.Where(message => message.Unread).Select(message => (message.Epoch, message.Sequence)).Single());
    }

    [Fact(DisplayName = "R3: FX-SDK-002's operations recorded through the product: each one start, its parent the start's related activity and a root's none, never the empty identity, and one completion with the status it first completed with - a second completion, and a channel's second close, say nothing")]
    public void ANativeOperationIsSaidOnce()
    {
        // The scenario: a root operation under none, each epoch's under it, the second epoch's completed twice, failing first;
        // the second epoch's channel closed twice.
        using JsonDocument scenario = Scenario();
        JsonElement[] operations = [.. scenario.RootElement.GetProperty("operations").EnumerateArray()];
        Assert.Equal(["publish-consume", "epoch-1", "epoch-2"], operations.Select(operation => operation.GetProperty("name").GetString()));
        Assert.Equal(JsonValueKind.Null, operations[0].GetProperty("parent").ValueKind);
        Assert.All(operations[1..], operation => Assert.Equal(operations[0].GetProperty("operation").GetGuid(), operation.GetProperty("parent").GetGuid()));
        Assert.Equal([0L, 0L, 1L], operations.Select(operation => operation.GetProperty("status").GetInt64()));
        Assert.Equal([1, 1, 2], operations.Select(operation => operation.GetProperty("completions").GetInt32()));
        Assert.Equal([1, 2], scenario.RootElement.GetProperty("closeCalls").EnumerateArray().Select(calls => calls.GetInt32()));

        using JsonDocument measurement = Measurement();
        Assert.All(measurement.RootElement.GetProperty("runs").EnumerateArray(), run =>
        {
            // Three operations, four completions called: three starts and three completions recorded, each as said.
            Assert.Equal((3, 4), (Int(run, "truthOperations"), Int(run, "truthCompletionCalls")));
            Assert.Equal((3, 3, 3, 3), (Int(run, "operationStarts"), Int(run, "matchedStarts"), Int(run, "operationCompletions"), Int(run, "matchedCompletions")));

            // Each epoch opened once and closed once, its second close saying nothing.
            Assert.Equal([1, 1], run.GetProperty("openings").EnumerateArray().Select(openings => openings.GetInt32()));
            Assert.Equal([1, 1], run.GetProperty("closings").EnumerateArray().Select(closings => closings.GetInt32()));
        });
    }

    private static int Int(JsonElement element, string name) => element.GetProperty(name).GetInt32();

    private static JsonDocument Measurement() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Evidence(), "measurement.json")));

    private static JsonDocument Scenario() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Evidence(), "scenario.json")));

    /// <summary>The truth: every message the sample logged it published, by epoch and sequence, and whether it logged it unread.</summary>
    private static (int Published, int Unread, (long Epoch, long Sequence, bool Unread)[] Messages) Published()
    {
        (long, long, bool)[] messages =
        [
            .. Messages("MessageSent").Select(record =>
                (record.GetProperty("message").GetProperty("epoch").GetInt64(), record.GetProperty("message").GetProperty("sequence").GetInt64(),
                    record.GetProperty("status").ValueKind == JsonValueKind.String && record.GetProperty("status").GetString() == "unread")),
        ];
        return (messages.Length, messages.Count(message => message.Item3), messages);
    }

    /// <summary>The truth log's records of one kind that name a cooperative message.</summary>
    private static IEnumerable<JsonElement> Messages(string kind)
    {
        foreach (string line in File.ReadLines(Path.Combine(Evidence(), "truth-native.jsonl")))
        {
            using JsonDocument record = JsonDocument.Parse(line);
            if (record.RootElement.GetProperty("kind").GetString() == kind && record.RootElement.TryGetProperty("message", out _))
            {
                yield return record.RootElement.Clone();
            }
        }
    }

    private static string Evidence()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "InterCat.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return Path.Combine(root.FullName, "fixtures", "FX-SDK-002", "evidence");
    }
}
