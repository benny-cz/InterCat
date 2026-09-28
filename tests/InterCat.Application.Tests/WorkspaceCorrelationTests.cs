using System.Globalization;
using InterCat.Analysis;
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

    [Fact(DisplayName = "R22: a person accepts or rejects a candidate join as a kept revision, and re-aligning flags it for review")]
    public void APersonDecidesACandidate()
    {
        string workspace = Workspace();
        Guid a = InvestigationWorkspace.Add(workspace, Session("client", "lab-1", ClientRows(1_000)), Now).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Session("server", "lab-2", ServerRows(5_000)), Now).SessionId;
        InvestigationWorkspace.Align(workspace, b, 500_000, a, 100_000, 1_000, 10, null, Now);
        (WorkspaceJoinEnd first, WorkspaceJoinEnd second) = Assert.Single(WorkspaceCorrelation.Candidates(workspace).Candidates).Ends;

        // Accepted, under the alignments now in force: a person's join, said as one, never as evidence.
        WorkspaceJoin accepted = InvestigationWorkspace.Decide(workspace, first, second, WorkspaceJoinDecision.Accepted, " the handshake matches ", Now);
        Assert.Equal((1, "the handshake matches"), (accepted.Revision, accepted.Note));
        Assert.Equal([new WorkspaceAlignmentInForce(a, 0), new WorkspaceAlignmentInForce(b, 1)], accepted.DecidedUnder);
        ConnectionCandidate decided = Assert.Single(WorkspaceCorrelation.Candidates(workspace).Candidates);
        Assert.Equal((WorkspaceJoinDecision.Accepted, true), (decided.Decision!.Decision, decided.DecisionCurrent));
        Assert.Equal("Accepted as one connection by a person (join revision 1).", decided.Evidence[^1]);

        // Re-aligned, its timing may no longer hold: it reads as one to review.
        InvestigationWorkspace.Align(workspace, b, 500_000, a, 100_200, 1_000, 10, null, Now);
        decided = Assert.Single(WorkspaceCorrelation.Candidates(workspace).Candidates);
        Assert.False(decided.DecisionCurrent);
        Assert.EndsWith("under alignments since changed: review it.", decided.Evidence[^1], StringComparison.Ordinal);

        // Rejected replaces it; withdrawn, the pair is undecided; every revision is kept, and nothing is left to withdraw.
        _ = InvestigationWorkspace.Decide(workspace, second, first, WorkspaceJoinDecision.Rejected, null, Now);
        Assert.Equal(WorkspaceJoinDecision.Rejected, Assert.Single(WorkspaceCorrelation.Candidates(workspace).Candidates).Decision!.Decision);
        _ = InvestigationWorkspace.Decide(workspace, first, second, WorkspaceJoinDecision.Withdrawn, null, Now);
        Assert.Null(Assert.Single(WorkspaceCorrelation.Candidates(workspace).Candidates).Decision);
        Assert.Equal(3, InvestigationWorkspace.Read(workspace).Joins.Count);
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.Decide(workspace, first, second, WorkspaceJoinDecision.Withdrawn, null, Now));

        // A decision whose pair moves apart is said, never lost.
        _ = InvestigationWorkspace.Decide(workspace, first, second, WorkspaceJoinDecision.Accepted, null, Now);
        InvestigationWorkspace.Align(workspace, b, 500_000, a, 10_000_000_000, 1_000, 10, null, Now);
        WorkspaceCorrelationResult apart = WorkspaceCorrelation.Candidates(workspace);
        Assert.Empty(apart.Candidates);
        Assert.Contains("not a candidate now", Assert.Single(apart.DecidedElsewhere!).Why, StringComparison.Ordinal);

        // Nothing else is a join: one session's own connections, a key that names none, or a join in an earlier version.
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.Decide(workspace, first, first with { Key = TransportConnection.KeyPrefix + "x" }, WorkspaceJoinDecision.Accepted, null, Now));
        Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.Decide(workspace, first with { Key = "channel:1" }, second, WorkspaceJoinDecision.Accepted, null, Now));
        File.WriteAllText(workspace, File.ReadAllText(workspace).Replace("\"workspace-v4\"", "\"workspace-v3\"", StringComparison.Ordinal));
        Assert.Contains("holds no join decision", Assert.Throws<InvalidDataException>(() => InvestigationWorkspace.Read(workspace)).Message,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R21: host B receiving 0.2 ms before host A sends, within 3 ms, is no order and no latency, and no local span changes")]
    public void AReceiveBeforeItsSendWithinTheUncertaintyIsNoOrder()
    {
        // §21.1: host A sends at its 1,000,100 ns; host B receives at its 500,100 ns, which its alignment - within 3 ms, its
        // anchor on that receive - places 0.2 ms before the send.
        string workspace = Workspace();
        string client = Session("client", "lab-1", ClientRows(10_000));
        string server = Session("server", "lab-2", ServerRows(5_000));
        Guid a = InvestigationWorkspace.Add(workspace, client, Now).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, server, Now).SessionId;
        HeldConnection before = Assert.Single(SessionConnections.All(SessionStore.OpenExisting(LocalOwnedDirectory.Open(server))).Connections,
            connection => connection.Summary.RemoteEndpoint == Client);
        InvestigationWorkspace.Align(workspace, b, 500_100, a, 800_100, 3_000_000, 0, "B's receive of A's first send", Now);

        WorkspaceComparison comparison = InvestigationWorkspace.Compare(InvestigationWorkspace.Read(workspace), a, 1_000_100, b, 500_100);
        Assert.Equal((TimeOrder.Ambiguous, -200_000L, 3_000_000.0),
            (comparison.Result.Order, comparison.Result.DifferenceNanoseconds!.Value, comparison.Result.Uncertainty!.Value.HalfWidthNanoseconds));
        string statement = comparison.Statement(CultureInfo.InvariantCulture);
        Assert.Equal("Their order is ambiguous: they are 200 µs apart, within their combined uncertainty of ±3.0 ms.", statement);
        Assert.DoesNotContain("latency", statement, StringComparison.OrdinalIgnoreCase);

        // The exchange is still a candidate - its lifetimes overlap within the uncertainty - and B's own span is its own.
        Assert.Equal(CandidateTiming.Overlapping, Assert.Single(WorkspaceCorrelation.Candidates(workspace).Candidates).Timing);
        HeldConnection after = Assert.Single(SessionConnections.All(SessionStore.OpenExisting(LocalOwnedDirectory.Open(server))).Connections,
            connection => connection.Summary.RemoteEndpoint == Client);
        Assert.Equal((before.FirstNanoseconds, before.LastNanoseconds), (after.FirstNanoseconds, after.LastNanoseconds));
        Assert.Equal((500_000L, 500_400L), (after.FirstNanoseconds, after.LastNanoseconds));
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
