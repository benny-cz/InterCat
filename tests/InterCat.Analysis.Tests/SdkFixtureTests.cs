using System.Text.Json;
using Xunit;

namespace InterCat.Analysis.Tests;

/// <summary>
/// FX-SDK-001 recorded through the product (ADR-104's addendum): a ring one producer writes and three readers read, each
/// saying what it publishes and consumes through InterCat's cooperative SDK, captured by icat record under the opt-in sdk
/// profile and read as icat messages reads it - held here to the runs' measurement and the first run's truth logs.
/// </summary>
public sealed class SdkFixtureTests
{
    [Fact(DisplayName = "P4: FX-SDK-001 recorded through the product: a message three readers consume is one message and three consumptions, never three - every message the ring's producer published at either epoch and every reader's consumption of it the truth's, by length and process, nothing lost")]
    public void EveryMessageIsTheTruths()
    {
        using JsonDocument measurement = Measurement();
        JsonElement[] runs = [.. measurement.RootElement.GetProperty("runs").EnumerateArray()];
        Assert.Equal(3, runs.Length);
        Assert.All(runs, run =>
        {
            // Every publication and consumption the truth logged is one the session holds, of the same length and process.
            Assert.Equal((161, 161, 161, 161), (Int(run, "truthPublications"), Int(run, "publications"), Int(run, "matchedPublications"), Int(run, "messages")));
            Assert.Equal((480, 480, 480), (Int(run, "truthConsumptions"), Int(run, "consumptions"), Int(run, "matchedConsumptions")));

            // Each record bound to its process and carrying its clock reading; none naming no message; the channel named as opened.
            Assert.Equal((641, 641, 641, 0), (Int(run, "records"), Int(run, "boundRecords"), Int(run, "recordsWithSourceTicks"), Int(run, "recordsNamingNoMessage")));
            Assert.True(run.GetProperty("named").GetBoolean());
            Assert.Equal([3, 3], run.GetProperty("readers").EnumerateArray().Select(readers => readers.GetInt32()));
            Assert.Equal([4, 4], run.GetProperty("openings").EnumerateArray().Select(openings => openings.GetInt32()));
            Assert.Equal([4, 4], run.GetProperty("closings").EnumerateArray().Select(closings => closings.GetInt32()));

            // Nothing lost, dropped or undecodable on the way.
            JsonElement health = run.GetProperty("health");
            Assert.Equal((0, 0, 0, 0), (Int(health, "providerReportedEventLoss"), Int(health, "consumerReportedBufferLoss"),
                Int(health, "applicationDrops"), Int(health, "undecodableRecords")));
            Assert.Equal(Int(health, "observedRecords"), Int(health, "admittedRecords"));
        });

        // The first run's truth, kept beside it, logged what its counts say: each publication once, each reader's consumption once.
        (int published, int unread, _) = Published();
        Assert.Equal((161, 1), (published, unread));
        Assert.Equal(480, Directory.EnumerateFiles(Evidence(), "truth-reader-*.jsonl").Sum(file => Messages(file, "MessageReceived").Count()));
    }

    [Fact(DisplayName = "R3: FX-SDK-001's message no reader read stays open, never said consumed, and the ring made again is a second epoch whose sequence starts at 1 again, never one message with the first's")]
    public void AnUnreadMessageStaysOpenAndARestartIsAnEpoch()
    {
        using JsonDocument measurement = Measurement();
        Assert.All(measurement.RootElement.GetProperty("runs").EnumerateArray(), run =>
        {
            JsonElement states = run.GetProperty("states");
            Assert.Equal((160, 1, 0, 0), (Int(states, "Consumed"), Int(states, "Unconsumed"), Int(states, "PublicationUnseen"), Int(states, "Conflicting")));
            Assert.Equal((1, true), (Int(run, "truthUnread"), run.GetProperty("unreadOpen").GetBoolean()));
            Assert.Equal([1L, 2L], run.GetProperty("epochs").EnumerateArray().Select(epoch => epoch.GetInt64()));
            Assert.Equal([120, 41], run.GetProperty("epochMessages").EnumerateArray().Select(messages => messages.GetInt32()));
        });

        // The producer's truth: epoch 1 numbered from 1 to 120, epoch 2 from 1 again, its last message the one logged unread.
        (_, _, (long Epoch, long Sequence, bool Unread)[] messages) = Published();
        Assert.Equal((1L, 120L), (messages.Where(message => message.Epoch == 1).Min(message => message.Sequence), messages.Where(message => message.Epoch == 1).Max(message => message.Sequence)));
        Assert.Equal((1L, 41L), (messages.Where(message => message.Epoch == 2).Min(message => message.Sequence), messages.Where(message => message.Epoch == 2).Max(message => message.Sequence)));
        Assert.Equal((2L, 41L), messages.Where(message => message.Unread).Select(message => (message.Epoch, message.Sequence)).Single());
    }

    private static int Int(JsonElement element, string name) => element.GetProperty(name).GetInt32();

    private static JsonDocument Measurement() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Evidence(), "measurement.json")));

    /// <summary>The producer's truth: every message it logged it published, by epoch and sequence, and whether it logged it unread.</summary>
    private static (int Published, int Unread, (long Epoch, long Sequence, bool Unread)[] Messages) Published()
    {
        (long, long, bool)[] messages =
        [
            .. Messages(Path.Combine(Evidence(), "truth-producer.jsonl"), "MessageSent").Select(record =>
                (record.GetProperty("message").GetProperty("epoch").GetInt64(), record.GetProperty("message").GetProperty("sequence").GetInt64(),
                    record.GetProperty("status").ValueKind == JsonValueKind.String && record.GetProperty("status").GetString() == "unread")),
        ];
        return (messages.Length, messages.Count(message => message.Item3), messages);
    }

    /// <summary>A truth log's records of one kind that name a cooperative message.</summary>
    private static IEnumerable<JsonElement> Messages(string file, string kind)
    {
        foreach (string line in File.ReadLines(file))
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
        return Path.Combine(root.FullName, "fixtures", "FX-SDK-001", "evidence");
    }
}
