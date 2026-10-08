using Xunit;

namespace InterCat.CaptureBroker.Tests;

/// <summary>
/// ADR-049: the broker keeps content only from processes its client could read itself, each read from the process and
/// named by its ID and start.
/// </summary>
public sealed class BrokerContentProcessesTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "P19: the broker keeps content only from processes the client could read itself - its own user's, in its own sign-in, at no higher integrity - read from each process, never the request")]
    public void OnlyTheClientsOwnProcesses()
    {
        BrokerClientIdentity client = BrokerPlanFixture.OwnerA;
        var reader = new FakeProcessReader
        {
            [100] = new(client, Started),
            [200] = new(client with { IntegrityLevel = 0x1000 }, Started.AddSeconds(5)),
            [300] = new(BrokerPlanFixture.OwnerB, Started),
            [400] = new(client with { LogonSessionId = 0x9999 }, Started),
            [500] = new(client with { IntegrityLevel = 0x3000, IsElevated = true }, Started),
            [600] = new(client with { UserSid = client.UserSid.ToLowerInvariant() }, Started),
        };

        // Its own processes, a lower-integrity one among them, each pinned by its start, in order.
        IReadOnlyList<BrokerNamedProcess>? pinned = BrokerContentProcesses.Pin(client, [200, 100, 600], reader, out string? refusal);
        Assert.Null(refusal);
        Assert.Equal([new(100, Started), new(200, Started.AddSeconds(5)), new(600, Started)], pinned!);

        // Another user's, another sign-in's, a higher integrity's, and one not running are each refused by name.
        foreach ((int processId, string why) in new[]
        {
            (300, "Process 300 runs as another user."),
            (400, "Process 400 runs in another sign-in than the one asking"),
            (500, "Process 500 runs at a higher integrity than the one asking"),
            (700, "Process 700 is not running, so its ID could be given to any process. Content is kept only from processes the "
                + "broker can show are yours."),
        })
        {
            Assert.Null(BrokerContentProcesses.Pin(client, [100, processId], reader, out refusal));
            Assert.StartsWith(why, refusal, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "R22: a process a content capture names is its ID and its start, so one whose ID now names a later process, or that is no longer the client's, refuses the start")]
    public void APinnedProcessIsItsIdAndItsStart()
    {
        BrokerClientIdentity client = BrokerPlanFixture.OwnerA;
        var reader = new FakeProcessReader { [100] = new(client, Started) };
        IReadOnlyList<BrokerNamedProcess> pinned = BrokerContentProcesses.Pin(client, [100], reader, out _)!;
        Assert.Null(BrokerContentProcesses.Verify(client, pinned, reader));

        // Its ID given to a process that started later.
        reader[100] = new(client, Started.AddMinutes(3));
        Assert.Equal("Process 100 is no longer the process the capture was prepared for: the one holding its ID started at "
            + "2026-10-08 09:03:00.000 UTC, not 2026-10-08 09:00:00.000 UTC. Prepare the capture again for the process now running.",
            BrokerContentProcesses.Verify(client, pinned, reader));

        // Exited, or another user's by then.
        reader.Remove(100);
        Assert.StartsWith("Process 100 is not running, so its ID could be given to any process.",
            BrokerContentProcesses.Verify(client, pinned, reader), StringComparison.Ordinal);
        reader[100] = new(BrokerPlanFixture.OwnerB, Started);
        Assert.StartsWith("Process 100 runs as another user.", BrokerContentProcesses.Verify(client, pinned, reader),
            StringComparison.Ordinal);
    }

    /// <summary>Processes by ID, as a test sets them; any other ID is not running.</summary>
    private sealed class FakeProcessReader : Dictionary<int, BrokerProcessReading>, IBrokerProcessReader
    {
        public BrokerProcessReading? Read(int processId, out string? problem)
        {
            problem = TryGetValue(processId, out BrokerProcessReading? reading)
                ? null
                : $"process {processId} is not running, so its ID could be given to any process";
            return reading;
        }
    }
}
