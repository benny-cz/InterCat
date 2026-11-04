using System.Globalization;
using System.Text.Json;
using InterCat.Domain;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// The Unix-domain sockets work package (plan M6, §4.2): Winsock AFD is inventoried as their lead, which FX-UNIX-001 measured
/// through a lab probe, and nothing claims more for it - no profile requests it and no build carries a tier for them until
/// a fixture measures one through the product (P27). The fixture's ledger holds each record on the workload's sockets, each
/// the socket's from its create to the next create at its address (R22), and counts the truth's every send and receive.
/// </summary>
public sealed class UnixDomainSocketLeadTests
{
    [Fact(DisplayName = "P27: Unix-domain sockets have an inventoried source that no profile requests, its cost measured, and no tier on any build until a fixture measures one through it")]
    public void UnixDomainSocketsHaveALeadAndNoTier()
    {
        WindowsSourceDefinition afd = WindowsSourceCatalog.Find(WindowsSourceCatalog.WinsockAfdSourceId)!;
        Assert.Equal("Microsoft-Windows-Winsock-AFD", afd.ProviderName);
        Assert.Equal([Mechanism.UnixDomainSocket], afd.Mechanisms);
        Assert.Empty(afd.AdmittedEvents);
        Assert.Equal((SourceContractStatus.Experimental, false, 0UL),
            (afd.ContractStatus, afd.SupportsCaptureSideProcessFilter, afd.MatchAnyKeyword));
        Assert.Contains(afd.Notes, note => note.Contains("FX-UNIX-001", StringComparison.Ordinal));

        // Its collection's cost is measured, and Moderate: collected machine-wide, as its process filter does not hold.
        Assert.Equal(OverheadClass.Moderate, afd.Overhead);
        Assert.True(File.Exists(Path.Combine(RepositoryRoot(), afd.OverheadEvidence!)), afd.OverheadEvidence);
        using (JsonDocument impact = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), afd.OverheadEvidence!))))
        {
            Assert.Equal(afd.Overhead.ToString(), impact.RootElement.GetProperty("overhead").GetString());
            Assert.Equal(0, impact.RootElement.GetProperty("eventsLost").GetInt64());
        }

        // No profile requests it, and no other source claims the mechanism.
        Assert.DoesNotContain(CaptureProfileCatalog.All,
            profile => profile.Sources.Any(source => source.SourceId == WindowsSourceCatalog.WinsockAfdSourceId));
        Assert.All(WindowsSourceCatalog.All.Where(source => source.SourceId != WindowsSourceCatalog.WinsockAfdSourceId),
            source => Assert.DoesNotContain(Mechanism.UnixDomainSocket, source.Mechanisms));

        // The build carries no tier for them, though the fixture measured the source.
        Assert.DoesNotContain(MeasuredCoverage.Entries, entry => entry.Mechanism == Mechanism.UnixDomainSocket);
    }

    [Fact(DisplayName = "R22: FX-UNIX-001's AFD records, each its socket's from its create to the next create at its address, count the truth's every send and receive at both ends")]
    public void TheLedgerCountsTheTruth()
    {
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", "FX-UNIX-001", "evidence");
        LedgerRecord[] ledger =
        [
            .. File.ReadLines(Path.Combine(evidence, "records.csv")).Skip(1).Select(line => line.Split(',')).Select(field => new LedgerRecord(
                double.Parse(field[0], CultureInfo.InvariantCulture), field[1], field[2], int.Parse(field[4], CultureInfo.InvariantCulture),
                int.Parse(field[5], CultureInfo.InvariantCulture), int.Parse(field[6], CultureInfo.InvariantCulture),
                int.Parse(field[7], CultureInfo.InvariantCulture), int.Parse(field[8], CultureInfo.InvariantCulture),
                field[9].Length == 0 ? null : long.Parse(field[9], CultureInfo.InvariantCulture))),
        ];
        Assert.Equal(["client", "server"], ledger.Select(record => record.Owner).Distinct().Order(StringComparer.Ordinal));

        // A socket begins at its create: its first record is the create's entry, and it is created once.
        foreach (IGrouping<int, LedgerRecord> socket in ledger.GroupBy(record => record.Socket))
        {
            LedgerRecord first = socket.First();
            Assert.Equal((1000, 0), (first.Event, first.Phase));
            Assert.Single(socket, record => record is { Event: 1000, Phase: 0 });
            Assert.Single(socket.Select(record => (record.Owner, record.Family)).Distinct());
        }

        foreach ((string owner, string truthFile, int readsPerMessage) in new[] { ("server", "truth-server.jsonl", 2), ("client", "truth-client.jsonl", 1) })
        {
            (int sent, long bytesSent, int received, long bytesReceived, int connections) = Truth(Path.Combine(evidence, truthFile));
            LedgerRecord[] own = [.. ledger.Where(record => record.Owner == owner && record.Family == 1)];

            // A send is four records, two entries and two exits, each with its length; one location counts its calls.
            int[] sendsAtEachLocation = [.. new[] { (0, 3047), (0, 3056), (1, 3024), (1, 3051) }
                .Select(at => own.Count(record => record is { Event: 1003 } && (record.Phase, record.Location) == at))];
            Assert.All(sendsAtEachLocation, count => Assert.Equal(sent, count));
            Assert.Equal(bytesSent, own.Where(record => record is { Event: 1003, Phase: 1, Location: 3051 }).Sum(record => record.Length!.Value));

            // A receive is an entry and an exit; the server reads each message as its length and then its body.
            LedgerRecord[] reads = [.. own.Where(record => record is { Event: 1004, Phase: 0 })];
            Assert.Equal(readsPerMessage * received, reads.Length);
            Assert.Equal(reads.Length, own.Count(record => record is { Event: 1004, Phase: 1 }));
            Assert.Equal(bytesReceived, reads.Sum(record => record.Length!.Value));

            // Every connection the truth logged: each client socket's connect, each accepted socket's accept.
            Assert.Equal(connections, own.Count(record => owner == "client" ? record is { Event: 1018, Phase: 0 } : record is { Event: 1027, Phase: 1 }));

            // A completion is raised on the thread that finished it, the peer's often: a record's header names no owner.
            Assert.Contains(own, record => record.RaisedBy != owner);
        }
    }

    private static (int Sent, long BytesSent, int Received, long BytesReceived, int Connections) Truth(string path)
    {
        int sent = 0, received = 0, connections = 0;
        long bytesSent = 0, bytesReceived = 0;
        foreach (string line in File.ReadLines(path))
        {
            using JsonDocument record = JsonDocument.Parse(line);
            string kind = record.RootElement.GetProperty("kind").GetString()!;
            long completed = record.RootElement.GetProperty("completedBytes") is { ValueKind: JsonValueKind.Number } value ? value.GetInt64() : 0;
            switch (kind)
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

    /// <summary>One ledger row: its time, owner, the process whose thread raised it, and the record's event, phase and fields.</summary>
    private sealed record LedgerRecord(double Milliseconds, string Owner, string RaisedBy, int Event, int Phase, int Location, int Socket,
        int Family, long? Length);
}
