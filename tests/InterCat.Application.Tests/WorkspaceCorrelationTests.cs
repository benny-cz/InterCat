using System.Globalization;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// Candidate joins between an investigation's captures (§8.3, M4): a connection one capture holds one end of, and another
/// capture the mirrored end, proposed where their lifetimes can overlap in the investigation's time and never established.
/// </summary>
public sealed class WorkspaceCorrelationTests : IDisposable
{
    private const string Client = "10.0.0.1:50000";
    private const string Server = "10.0.0.2:443";
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(Path.GetTempPath(), "InterCat.Application.Tests.Correlation", Guid.NewGuid().ToString("N"));

    public WorkspaceCorrelationTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact(DisplayName = "R21: a one-sided connection is a candidate join with its mirror in another capture, never where their lifetimes lie apart")]
    public void AMirroredConnectionIsACandidate()
    {
        string workspace = Workspace();
        Guid a = InvestigationWorkspace.Add(workspace, Session("client", "lab-1", ClientRows(1_000)), Now).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Session("server", "lab-2", ServerRows(5_000)), Now).SessionId;

        // Unaligned, the mirrored TCP connection is a candidate whose lifetimes cannot be compared; the loopback pair on two
        // hosts is none.
        WorkspaceCorrelationResult unaligned = WorkspaceCorrelation.Candidates(workspace);
        ConnectionCandidate candidate = Assert.Single(unaligned.Candidates);
        Assert.Equal((CandidateTiming.Unknown, 1, 0), (candidate.Timing, unaligned.LoopbackAcrossHosts, candidate.Alternatives));
        Assert.Equal((Client, Server, 100, 200), (candidate.First.Connection.Summary.LocalEndpoint, candidate.First.Connection.Summary.RemoteEndpoint,
            candidate.First.Connection.Holder.ProcessId, candidate.Second.Connection.Holder.ProcessId));
        Assert.Equal(
        [
            $"Endpoints mirror: {Client} and {Server}, TCP, each capture holding one end.",
            "Their lifetimes cannot be compared: no member is aligned, so the workspace has no time across members.",
            "The first sent 100 B in 1 measured transfer, and the second received 100 B in 1: the same bytes.",
            "The second sent 200 B in 1 measured transfer, and the first received 200 B in 1: the same bytes.",
        ], candidate.Evidence);

        // Aligned so their lifetimes meet, it is a candidate whose lifetimes overlap.
        InvestigationWorkspace.Align(workspace, b, 500_000, a, 100_000, 1_000, 10, null, Now);
        Assert.Equal(CandidateTiming.Overlapping, Assert.Single(WorkspaceCorrelation.Candidates(workspace).Candidates).Timing);

        // Aligned so they lie seconds apart, beyond any uncertainty, it is proposed nowhere; it is counted.
        InvestigationWorkspace.Align(workspace, b, 500_000, a, 10_000_000_000, 1_000, 10, null, Now);
        WorkspaceCorrelationResult apart = WorkspaceCorrelation.Candidates(workspace);
        Assert.Empty(apart.Candidates);
        Assert.Equal(1, apart.DisjointMirrors);

        // A session that moved is not compared, and says why.
        Directory.Move(Path.Combine(root, "server"), Path.Combine(root, "server-moved"));
        UnreadMember unread = Assert.Single(WorkspaceCorrelation.Candidates(workspace).Unread);
        Assert.Equal(b, unread.SessionId);
        Assert.Contains("missing", unread.Reason, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R22: a candidate with another for either connection is ambiguous, and loopback joins only one host's captures")]
    public void CandidatesSayWhenTheyAreNotTheOnlyMatch()
    {
        string workspace = Workspace();
        _ = InvestigationWorkspace.Add(workspace, Session("client", "lab-1", ClientRows(1_000)), Now);
        _ = InvestigationWorkspace.Add(workspace, Session("server", "lab-2", ServerRows(5_000)), Now);
        _ = InvestigationWorkspace.Add(workspace, Session("other", "lab-3", ServerRows(7_000)), Now);

        // The client's connection mirrors two servers' ends: two candidates, each ambiguous.
        WorkspaceCorrelationResult result = WorkspaceCorrelation.Candidates(workspace);
        ConnectionCandidate[] tcp = [.. result.Candidates.Where(candidate => candidate.First.Connection.Summary.RemoteEndpoint == Server
            || candidate.Second.Connection.Summary.RemoteEndpoint == Server)];
        Assert.Contains(tcp, candidate => candidate.First.Connection.Summary.LocalEndpoint == Client);
        Assert.All(tcp.Where(candidate => candidate.First.Connection.Summary.LocalEndpoint == Client), candidate =>
            Assert.Equal((1, true), (candidate.Alternatives, candidate.Ambiguous)));

        // Two captures of one host may hold a loopback connection's two ends.
        string sameHost = Workspace("same-host");
        _ = InvestigationWorkspace.Add(sameHost, Session(Path.Combine("same-host", "client"), "lab-1", ClientRows(1_000)), Now);
        _ = InvestigationWorkspace.Add(sameHost, Session(Path.Combine("same-host", "server"), "lab-1", ServerRows(5_000)), Now);
        WorkspaceCorrelationResult local = WorkspaceCorrelation.Candidates(sameHost);
        Assert.Equal(0, local.LoopbackAcrossHosts);
        Assert.Contains(local.Candidates, candidate => candidate.First.Connection.Summary.LocalEndpoint == "127.0.0.1:6000");
    }

    private string Workspace(string folder = "")
    {
        string path = Path.Combine(root, folder, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(path, Now);
        return path;
    }

    private string Session(string name, string host, ObservationRowV1[] rows)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "correlation-tests");
        Publish(store, rows, capture: CaptureId.New(), clock: ClockFor(ClockId.New(), host));
        store.ReleaseSegmentReaders();
        return directory;
    }

    /// <summary>Process 100 connects to the server, sends 100 bytes, receives 200 and disconnects; and holds one loopback end.</summary>
    private static ObservationRowV1[] ClientRows(long at) =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1)),
        Timed(Transfer(at, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 20).Between(Client, Server)),
        Timed(Transfer(at + 1, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 21).Between(Client, Server)),
        Timed(Transfer(at + 2, ObservationKind.Receive, AccountingSide.ReceiveSide, 200, 100, 22).Between(Client, Server)),
        Timed(Transfer(at + 4, ObservationKind.Disconnect, AccountingSide.EndpointActivity, 0, 100, 24).Between(Client, Server)),
        Timed(Transfer(at + 10, ObservationKind.Send, AccountingSide.SendSide, 10, 100, 30).Between("127.0.0.1:6000", "127.0.0.1:7000")),
    ];

    /// <summary>Process 200 accepts the client, receives its 100 bytes, sends 200 and disconnects; and holds the other loopback end.</summary>
    private static ObservationRowV1[] ServerRows(long at) =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 200, 1)),
        Timed(Transfer(at, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 20).Between(Server, Client)),
        Timed(Transfer(at + 1, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 200, 21).Between(Server, Client)),
        Timed(Transfer(at + 2, ObservationKind.Send, AccountingSide.SendSide, 200, 200, 22).Between(Server, Client)),
        Timed(Transfer(at + 4, ObservationKind.Disconnect, AccountingSide.EndpointActivity, 0, 200, 24).Between(Server, Client)),
        Timed(Transfer(at + 10, ObservationKind.Receive, AccountingSide.ReceiveSide, 10, 200, 30).Between("127.0.0.1:7000", "127.0.0.1:6000")),
    ];

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
