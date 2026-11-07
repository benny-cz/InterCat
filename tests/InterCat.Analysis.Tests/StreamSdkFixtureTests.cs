using System.Text.Json;
using Xunit;

namespace InterCat.Analysis.Tests;

/// <summary>
/// FX-SDK-003 and FX-SDK-004 recorded through the product: a client writing framed messages to a server over a named pipe
/// or a loopback TCP connection, a few to a write, read a few hundred bytes at a time, each process saying what it
/// publishes and consumes through InterCat's cooperative SDK, captured by icat record under the opt-in sdk profile and read
/// as icat messages reads it - held here to the runs' measurement and the first run's truth logs and scenario.
/// </summary>
public sealed class StreamSdkFixtureTests
{
    [Theory(DisplayName = "P4: FX-SDK-003 and FX-SDK-004 recorded through the product: a message over a pipe or a socket is one message, never a write or a read - every message the client published at either connection's epoch the truth's by its logical length, the bytes its frame copied and its process, and the server's consumption of each one consumption, nothing lost")]
    [InlineData("FX-SDK-003", 1)]
    [InlineData("FX-SDK-004", 2)]
    public void AStreamsMessagesAreTheTruths(string fixture, int kind)
    {
        using JsonDocument measurement = Measurement(fixture);
        Assert.Equal(fixture, measurement.RootElement.GetProperty("fixture").GetString());
        JsonElement[] runs = [.. measurement.RootElement.GetProperty("runs").EnumerateArray()];
        Assert.Equal(3, runs.Length);
        Assert.All(runs, run =>
        {
            // Every publication the truth logged is one the session holds, of the same logical length, frame and process,
            // and every consumption one of the same length and process.
            Assert.Equal((131, 131, 131, 131, 131), (Int(run, "truthPublications"), Int(run, "publications"), Int(run, "matchedPublications"),
                Int(run, "matchedCopies"), Int(run, "messages")));
            Assert.Equal((130, 130, 130), (Int(run, "truthConsumptions"), Int(run, "consumptions"), Int(run, "matchedConsumptions")));

            // Each record bound to its process and carrying its clock reading; none naming no message; the channel named
            // and of the kind both sides opened it with, by each at each epoch.
            Assert.Equal((261, 261, 261, 0), (Int(run, "records"), Int(run, "boundRecords"), Int(run, "recordsWithSourceTicks"), Int(run, "recordsNamingNoMessage")));
            Assert.True(run.GetProperty("named").GetBoolean());
            Assert.Equal([kind, kind], Ints(run, "kinds"));
            Assert.Equal([1, 1], Ints(run, "readers"));
            Assert.Equal([2, 2], Ints(run, "openings"));
            Assert.Equal([2, 2], Ints(run, "closings"));

            // Each process announced its schema to the session once - though the session listened before it first used the
            // SDK - naming its library, read at the layout the capture knows.
            Assert.Equal((2, 2, true), (Int(run, "announcements"), Int(run, "processesAnnounced"), run.GetProperty("announcedLibrary").GetBoolean()));

            // Nothing lost, dropped or undecodable on the way.
            JsonElement health = run.GetProperty("health");
            Assert.Equal((0, 0, 0, 0), (Int(health, "providerReportedEventLoss"), Int(health, "consumerReportedBufferLoss"),
                Int(health, "applicationDrops"), Int(health, "undecodableRecords")));
            Assert.Equal(Int(health, "observedRecords"), Int(health, "admittedRecords"));
        });

        // The first run's truth: each message's frame its 12-byte header and its bytes, and neither a write nor a read a
        // message - a write carried several, after the handshake that opened its connection, and a message spanned reads.
        JsonElement[] sent = [.. Records(fixture, "truth-client.jsonl", "MessageSent")];
        Assert.Equal(131, sent.Length);
        Assert.All(sent, record => Assert.Equal(record.GetProperty("declaredBytes").GetInt64() + 12, record.GetProperty("completedBytes").GetInt64()));
        Assert.Equal(130, Records(fixture, "truth-server.jsonl", "MessageReceived").Count());
        using JsonDocument scenario = Scenario(fixture);
        int[] writes = Ints(scenario.RootElement, "writes");
        int[] reads = Ints(scenario.RootElement, "reads");
        int[] messages = Ints(runs[0], "epochMessages");
        Assert.All([0, 1], epoch => Assert.True(writes[epoch] - 1 < messages[epoch] && reads[epoch] > messages[epoch],
            $"Epoch {epoch + 1}: {messages[epoch]} messages, {writes[epoch]} writes, {reads[epoch]} reads."));
    }

    [Theory(DisplayName = "R3: FX-SDK-003's and FX-SDK-004's message the server read and never consumed stays open, never said consumed, and the client's second connection is a second epoch whose sequence starts at 1 again")]
    [InlineData("FX-SDK-003")]
    [InlineData("FX-SDK-004")]
    public void ARereadMessageStaysOpenAndAReconnectionIsAnEpoch(string fixture)
    {
        using JsonDocument measurement = Measurement(fixture);
        Assert.All(measurement.RootElement.GetProperty("runs").EnumerateArray(), run =>
        {
            JsonElement states = run.GetProperty("states");
            Assert.Equal((130, 1, 0, 0), (Int(states, "Consumed"), Int(states, "Unconsumed"), Int(states, "PublicationUnseen"), Int(states, "Conflicting")));
            Assert.Equal((1, true), (Int(run, "truthUnread"), run.GetProperty("unreadOpen").GetBoolean()));
            Assert.Equal([1L, 2L], run.GetProperty("epochs").EnumerateArray().Select(epoch => epoch.GetInt64()));
            Assert.Equal([100, 31], Ints(run, "epochMessages"));
        });

        // The truth: epoch 1 numbered from 1 to 100, epoch 2 from 1 again, its last message the one logged unread - which
        // the server read whole and never consumed.
        (long Epoch, long Sequence, bool Unread)[] messages =
        [
            .. Records(fixture, "truth-client.jsonl", "MessageSent").Select(record =>
                (record.GetProperty("message").GetProperty("epoch").GetInt64(), record.GetProperty("message").GetProperty("sequence").GetInt64(),
                    record.GetProperty("status").ValueKind == JsonValueKind.String && record.GetProperty("status").GetString() == "unread")),
        ];
        Assert.Equal((1L, 100L), (messages.Where(message => message.Epoch == 1).Min(message => message.Sequence), messages.Where(message => message.Epoch == 1).Max(message => message.Sequence)));
        Assert.Equal((1L, 31L), (messages.Where(message => message.Epoch == 2).Min(message => message.Sequence), messages.Where(message => message.Epoch == 2).Max(message => message.Sequence)));
        Assert.Equal((2L, 31L), messages.Where(message => message.Unread).Select(message => (message.Epoch, message.Sequence)).Single());
        using JsonDocument scenario = Scenario(fixture);
        Assert.Equal(1, Int(scenario.RootElement, "readNeverConsumed"));
    }

    [Theory(DisplayName = "R3: FX-SDK-003's and FX-SDK-004's operations recorded through the product: each connection a client's operation under none, never the empty identity, and the server's under it, as the connection's first frame named it - each one start owned by its side's process, its parent the start's related activity, and one completion with its status")]
    [InlineData("FX-SDK-003")]
    [InlineData("FX-SDK-004")]
    public void AServersOperationIsUnderTheClients(string fixture)
    {
        // The scenario: each connection a client's operation under none and the server's under it, the second failing as it
        // left a message unconsumed.
        using JsonDocument scenario = Scenario(fixture);
        JsonElement[] operations = [.. scenario.RootElement.GetProperty("operations").EnumerateArray()];
        Assert.Equal(["connection-1", "connection-2", "serve-1", "serve-2"], operations.Select(operation => operation.GetProperty("name").GetString()));
        Assert.Equal(["client", "client", "server", "server"], operations.Select(operation => operation.GetProperty("role").GetString()));
        Assert.All(operations[..2], operation => Assert.Equal(JsonValueKind.Null, operation.GetProperty("parent").ValueKind));
        Assert.Equal(operations[0].GetProperty("operation").GetGuid(), operations[2].GetProperty("parent").GetGuid());
        Assert.Equal(operations[1].GetProperty("operation").GetGuid(), operations[3].GetProperty("parent").GetGuid());
        Assert.Equal([0L, 0L, 0L, 1L], operations.Select(operation => operation.GetProperty("status").GetInt64()));

        using JsonDocument measurement = Measurement(fixture);
        Assert.All(measurement.RootElement.GetProperty("runs").EnumerateArray(), run =>
        {
            Assert.Equal((4, 4), (Int(run, "truthOperations"), Int(run, "truthCompletionCalls")));
            Assert.Equal((4, 4, 4, 4), (Int(run, "operationStarts"), Int(run, "matchedStarts"), Int(run, "operationCompletions"), Int(run, "matchedCompletions")));
        });
    }

    private static int Int(JsonElement element, string name) => element.GetProperty(name).GetInt32();

    private static int[] Ints(JsonElement element, string name) => [.. element.GetProperty(name).EnumerateArray().Select(value => value.GetInt32())];

    private static JsonDocument Measurement(string fixture) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Evidence(fixture), "measurement.json")));

    private static JsonDocument Scenario(string fixture) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Evidence(fixture), "scenario.json")));

    /// <summary>A truth log's records of one kind that name a cooperative message.</summary>
    private static IEnumerable<JsonElement> Records(string fixture, string log, string kind)
    {
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
