using System.Globalization;
using System.Text.Json;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// The AF_UNIX gate (ADR-073): of every socket call Winsock AFD delivers, it admits one record a call or step of each socket a
/// capture saw created AF_UNIX, from its create to its close, owned by the process its create names - never by a reused
/// address alone (R22), nor by the thread that raised a completion.
/// </summary>
public sealed class UnixSocketGateTests
{
    private const ulong Listener = 0xA0, Socket = 0xB0, Accepted = 0xC0, Other = 0xD0;

    [Fact(DisplayName = "R22: the AF_UNIX gate follows a socket from its create to its close, owned by the process its create names, and none of another family at its address")]
    public void ASocketIsItsCreateToItsClose()
    {
        var gate = new UnixSocketGate();

        // A record naming no endpoint names no socket, even a create of the family.
        Assert.Equal(AfdGateOutcome.NoUnixSocket, gate.Read(Create(0, family: 1, madeFor: 30)).Outcome);

        // Another family's socket at the address: neither it nor its calls are admitted.
        Assert.Equal(AfdGateOutcome.NoUnixSocket, gate.Read(Create(Socket, family: 2, madeFor: 30)).Outcome);
        Assert.Equal(AfdGateOutcome.NoUnixSocket, gate.Read(Call(1003, 1, 3051, Socket, raisedBy: 30)).Outcome);

        // An AF_UNIX socket at the same address, reused: owned by the process its create names, whichever thread raises a call.
        (AfdGateOutcome created, UnixSocketIncarnation? socket) = gate.Read(Create(Socket, family: 1, madeFor: 30, raisedBy: 31));
        Assert.Equal((AfdGateOutcome.Admitted, 30), (created, socket!.Value.Owner));
        Assert.Equal((AfdGateOutcome.Admitted, socket), gate.Read(Call(1003, 1, 3051, Socket, raisedBy: 40)));
        Assert.Equal((AfdGateOutcome.NotCounted, socket), gate.Read(Call(1003, 1, 3024, Socket, raisedBy: 40)));
        Assert.Equal((AfdGateOutcome.NotCounted, socket), gate.Read(Call(1003, 0, 3047, Socket)));
        Assert.Equal((AfdGateOutcome.NotCounted, socket), gate.Read(Call(1004, 0, 4107, Socket)));
        Assert.Equal((AfdGateOutcome.Admitted, socket), gate.Read(Call(1004, 1, 4123, Socket, raisedBy: 40)));
        Assert.Equal((AfdGateOutcome.NotCounted, socket), gate.Read(Call(3003, 3, 9000, Socket)));

        // Its close, after its cleanup, ends it: a late indication at the address is still its, counting nothing, until a
        // create names the address again.
        Assert.Equal((AfdGateOutcome.NotCounted, socket), gate.Read(Call(1002, 1, 2003, Socket)));
        Assert.Equal((AfdGateOutcome.Admitted, socket), gate.Read(Call(1001, 1, 2001, Socket)));
        Assert.Equal(0, gate.Following);
        Assert.Equal((AfdGateOutcome.NotCounted, socket), gate.Read(Call(3006, 3, 12001, Socket)));
        UnixSocketIncarnation again = gate.Read(Create(Socket, family: 1, madeFor: 32)).Socket!.Value;
        Assert.NotEqual(socket!.Value.Number, again.Number);

        // A create of another family at its address ends it unseen.
        Assert.Equal(AfdGateOutcome.NoUnixSocket, gate.Read(Create(Socket, family: 23, madeFor: 33)).Outcome);
        Assert.Equal(AfdGateOutcome.NoUnixSocket, gate.Read(Call(1004, 1, 4116, Socket)).Outcome);

        // An accept's new socket, raised on the peer's thread, is its listener's owner's.
        UnixSocketIncarnation listening = gate.Read(Create(Listener, family: 1, madeFor: 50)).Socket!.Value;
        Assert.Equal((AfdGateOutcome.Admitted, listening), gate.Read(Call(1027, 1, 6101, Listener, raisedBy: 60) with { AcceptEndpoint = Accepted }));
        Assert.Equal((AfdGateOutcome.Admitted, 50), (gate.Read(Call(1004, 1, 4116, Accepted)).Outcome, gate.Read(Call(1004, 1, 4116, Accepted)).Socket!.Value.Owner));
    }

    [Fact(DisplayName = "R22: the AF_UNIX gate follows no more sockets than it may, says of a create beyond them that it was not followed, and remembers only the last few it saw closed")]
    public void TheGateHoldsABoundedNumberOfSockets()
    {
        var gate = new UnixSocketGate(capacity: 2);
        Assert.Equal(AfdGateOutcome.Admitted, gate.Read(Create(Listener, 1, 70)).Outcome);
        Assert.Equal(AfdGateOutcome.Admitted, gate.Read(Create(Socket, 1, 70)).Outcome);
        Assert.Equal(AfdGateOutcome.Untracked, gate.Read(Create(Other, 1, 70)).Outcome);
        Assert.Equal(AfdGateOutcome.NoUnixSocket, gate.Read(Call(1003, 1, 3051, Other)).Outcome);
        Assert.Equal(2, gate.Following);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new UnixSocketGate(capacity: 0));

        // Of the sockets it saw closed it remembers the last few, for the indications that arrive after a close: a socket
        // closed before them is of none, as one it never followed.
        var closing = new UnixSocketGate();
        for (ulong endpoint = 1; endpoint <= UnixSocketGate.ClosedRemembered + 10; endpoint++)
        {
            Assert.Equal(AfdGateOutcome.Admitted, closing.Read(Create(endpoint, 1, 70)).Outcome);
            Assert.Equal(AfdGateOutcome.Admitted, closing.Read(Call(1001, 1, 2001, endpoint)).Outcome);
        }

        Assert.Equal((0, UnixSocketGate.ClosedRemembered), (closing.Following, closing.Remembered));
        Assert.Equal(AfdGateOutcome.NoUnixSocket, closing.Read(Call(3006, 3, 12001, 10)).Outcome);
        Assert.Equal(AfdGateOutcome.NotCounted, closing.Read(Call(3006, 3, 12001, 11)).Outcome);
    }

    [Theory(DisplayName = "R22: FX-UNIX-001's records through the AF_UNIX gate are one a call - each send and receive with the bytes it moved, each connect, accept and close - of every AF_UNIX socket and of no other")]
    [InlineData("", 2)]
    [InlineData("oversized-reads", 0)]
    public void TheFixtureCountsOneRecordACall(string run, int serverReadsPerMessage)
    {
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", "FX-UNIX-001", "evidence", run);
        var processes = new Dictionary<string, int>(StringComparer.Ordinal) { ["server"] = 10, ["client"] = 20, ["another process"] = 30 };
        var gate = new UnixSocketGate();
        var admitted = new List<(string Owner, int Event, long Length)>();
        var families = new Dictionary<int, int>();
        foreach (string[] field in File.ReadLines(Path.Combine(evidence, "records.csv")).Skip(1).Select(line => line.Split(',')))
        {
            // The ledger numbers each socket by its incarnation, never by its address: the number stands for its address.
            int socket = int.Parse(field[7], CultureInfo.InvariantCulture);
            int family = int.Parse(field[8], CultureInfo.InvariantCulture);
            families.TryAdd(socket, family);
            var record = new AfdSocketRecord(
                int.Parse(field[4], CultureInfo.InvariantCulture), uint.Parse(field[5], CultureInfo.InvariantCulture),
                uint.Parse(field[6], CultureInfo.InvariantCulture), (ulong)socket, 0, (uint)family, processes[field[1]], processes[field[2]]);
            (AfdGateOutcome outcome, UnixSocketIncarnation? owned) = gate.Read(record);

            // Every record of an AF_UNIX socket is read as its socket's, and no record of another family's.
            Assert.Equal(family == 1, outcome != AfdGateOutcome.NoUnixSocket);
            if (outcome == AfdGateOutcome.Admitted)
            {
                Assert.Equal(processes[field[1]], owned!.Value.Owner);
                admitted.Add((field[1], record.EventId, field[9].Length == 0 ? 0 : long.Parse(field[9], CultureInfo.InvariantCulture)));
            }
        }

        Assert.Equal(0, gate.Following);
        foreach ((string owner, string file) in new[] { ("server", "truth-server.jsonl"), ("client", "truth-client.jsonl") })
        {
            (int sent, long bytesSent, int received, long bytesReceived, int connections) = Truth(Path.Combine(evidence, file));
            (string Owner, int Event, long Length)[] own = [.. admitted.Where(record => record.Owner == owner)];
            Assert.Equal((sent, bytesSent), (own.Count(record => record.Event == 1003), own.Where(record => record.Event == 1003).Sum(record => record.Length)));
            Assert.Equal(bytesReceived, own.Where(record => record.Event == 1004).Sum(record => record.Length));
            int readsPerMessage = owner == "server" ? serverReadsPerMessage : 1;
            if (readsPerMessage > 0)
            {
                Assert.Equal(readsPerMessage * received, own.Count(record => record.Event == 1004));
            }

            Assert.Equal(connections, own.Count(record => owner == "client" ? record.Event == 1017 : record.Event == 1027));
            Assert.Equal(own.Count(record => record.Event == 1000), own.Count(record => record.Event == 1001));
        }

        Assert.Equal(families.Count(entry => entry.Value == 1), admitted.Count(record => record.Event == 1000));
    }

    private static AfdSocketRecord Create(ulong endpoint, uint family, int madeFor, int raisedBy = 0) =>
        new(1000, 0, 1006, endpoint, 0, family, madeFor, raisedBy == 0 ? madeFor : raisedBy);

    private static AfdSocketRecord Call(int eventId, uint phase, uint location, ulong endpoint, int raisedBy = 30) =>
        new(eventId, phase, location, endpoint, 0, 0, 0, raisedBy);

    private static (int Sent, long BytesSent, int Received, long BytesReceived, int Connections) Truth(string path)
    {
        int sent = 0, received = 0, connections = 0;
        long bytesSent = 0, bytesReceived = 0;
        foreach (string line in File.ReadLines(path))
        {
            using JsonDocument record = JsonDocument.Parse(line);
            long completed = record.RootElement.GetProperty("completedBytes") is { ValueKind: JsonValueKind.Number } value ? value.GetInt64() : 0;
            switch (record.RootElement.GetProperty("kind").GetString())
            {
                case "MessageSent":
                    sent++;
                    bytesSent += completed;
                    break;
                case "MessageReceived":
                    received++;
                    bytesReceived += completed;
                    break;
                case "ConnectionEstablished" or "ConnectionAccepted":
                    connections++;
                    break;
            }
        }

        return (sent, bytesSent, received, bytesReceived, connections);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }
}
