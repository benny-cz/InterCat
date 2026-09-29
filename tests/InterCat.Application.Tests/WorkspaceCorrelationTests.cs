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

    [Fact(DisplayName = "I16: candidate joins and the merged time name the generation of each capture they read, with its manifest")]
    public void WorkspaceResultsNameTheirSnapshotVector()
    {
        string workspace = Workspace();
        string client = Session("client", "lab-1", ClientRows(1_000));
        string server = Session("server", "lab-2", ServerRows(5_000));
        Guid a = InvestigationWorkspace.Add(workspace, client, Now).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, server, Now).SessionId;

        // Each compared session's capture at the generation read, with that generation's manifest, ordered by capture.
        SnapshotEntry[] expected =
        [
            .. new[] { client, server }.Select(path =>
            {
                SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(path));
                SessionManifestV1 manifest = store.Current!;
                return new SnapshotEntry(SessionSegments.Source(store.Root, manifest)!.Value.Capture, manifest.Generation, manifest.Digest);
            }).OrderBy(entry => entry.CaptureId.ToString(), StringComparer.Ordinal),
        ];
        Assert.Equal(expected, WorkspaceCorrelation.Candidates(workspace).Snapshot);

        // The merged time names the sessions it placed: none before an alignment, both after one.
        Assert.Empty(InvestigationTimeline.Read(workspace, 10).Snapshot);
        InvestigationWorkspace.Align(workspace, b, 500_000, a, 100_000, 1_000, 10, null, Now);
        Assert.Equal(expected, InvestigationTimeline.Read(workspace, 10).Snapshot);
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

    [Fact(DisplayName = "R22: loopback joins two identities' captures only while a person confirms they are one host, and says so")]
    public void LoopbackJoinsConfirmedHostsAndSaysSo()
    {
        string workspace = Workspace();
        _ = InvestigationWorkspace.Add(workspace, Session("client", "lab-1", ClientRows(1_000)), Now);
        _ = InvestigationWorkspace.Add(workspace, Session("server", "lab-2", ServerRows(5_000)), Now);
        Guid[] hosts = [.. InvestigationWorkspace.Read(workspace).Members.Select(member => member.HostId)];
        Assert.Equal(1, WorkspaceCorrelation.Candidates(workspace).LoopbackAcrossHosts);

        // Confirmed one host - a machine renamed between its captures - the loopback connection's two ends are a candidate,
        // which says it rests on that confirmation.
        InvestigationWorkspace.ConfirmOneHost(workspace, hosts[0], hosts[1], "renamed between the captures", Now);
        WorkspaceCorrelationResult confirmed = WorkspaceCorrelation.Candidates(workspace);
        ConnectionCandidate loopback = Assert.Single(confirmed.Candidates, candidate => candidate.First.Connection.Summary.LocalEndpoint.StartsWith("127.", StringComparison.Ordinal));
        Assert.Equal(0, confirmed.LoopbackAcrossHosts);
        Assert.Contains(WorkspaceCorrelation.OneHostByConfirmation, loopback.Evidence);
        Assert.DoesNotContain(WorkspaceCorrelation.OneHostByConfirmation,
            Assert.Single(confirmed.Candidates, candidate => candidate != loopback).Evidence);

        // Withdrawn, they are two hosts again, and no loopback end joins across them.
        InvestigationWorkspace.WithdrawOneHost(workspace, hosts[1], hosts[0], Now);
        Assert.Equal(1, WorkspaceCorrelation.Candidates(workspace).LoopbackAcrossHosts);
    }

    [Fact(DisplayName = "R22: a known address translation lets mirrored endpoints meet through it, and the candidate rests on it")]
    public void AKnownTranslationLetsEndpointsMeet()
    {
        string workspace = Workspace();
        _ = InvestigationWorkspace.Add(workspace, Session("client", "lab-1", ClientRows(1_000, "203.0.113.7:8443")), Now);
        _ = InvestigationWorkspace.Add(workspace, Session("server", "lab-2", ServerRows(5_000)), Now);

        // The client dials a port forward's public endpoint: as seen, the server's end mirrors nothing.
        Assert.Empty(WorkspaceCorrelation.Candidates(workspace).Candidates);

        // A person states the forward, and the two ends are a candidate that says it rests on that statement.
        WorkspaceAddressTranslation stated = InvestigationWorkspace.StateTranslation(workspace, "203.0.113.7:8443", " 10.0.0.2:443 ",
            "the router forwards 8443 to the server", Now);
        ConnectionCandidate forwarded = Assert.Single(WorkspaceCorrelation.Candidates(workspace).Candidates);
        Assert.Equal((1, "10.0.0.2:443"), (stated.Revision, stated.Is));
        Assert.Equal([stated], forwarded.Translations);
        Assert.Equal("Endpoints mirror through a known translation: the first holds 10.0.0.1:50000 to 203.0.113.7:8443, and the "
            + "second 10.0.0.2:443 to 10.0.0.1:50000, TCP.", forwarded.Evidence[0]);
        Assert.Equal("A person stated that 203.0.113.7:8443 is 10.0.0.2:443 (translation revision 1); the join rests on that statement.",
            forwarded.Evidence[1]);

        // An address alone keeps its ports, so a forward from 8443 to 443 is no translation of the address; withdrawn, it is gone.
        InvestigationWorkspace.WithdrawTranslation(workspace, "10.0.0.2:443", "203.0.113.7:8443", Now);
        InvestigationWorkspace.StateTranslation(workspace, "203.0.113.7", "10.0.0.2", null, Now);
        Assert.Empty(WorkspaceCorrelation.Candidates(workspace).Candidates);
        Assert.Equal(3, InvestigationWorkspace.Read(workspace).AddressTranslations.Count);

        // Loopback, one endpoint twice, an endpoint and an address alone, and no endpoint at all are no translation.
        Assert.Contains("loopback", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.StateTranslation(workspace, "127.0.0.1:80", "10.0.0.2:80", null, Now)).Message, StringComparison.Ordinal);
        Assert.Contains("itself", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.StateTranslation(workspace, "10.0.0.2:443", "10.0.0.2:443", null, Now)).Message, StringComparison.Ordinal);
        Assert.Contains("or neither has", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.StateTranslation(workspace, "203.0.113.7:8443", "10.0.0.2", null, Now)).Message, StringComparison.Ordinal);
        Assert.Contains("is no endpoint", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.StateTranslation(workspace, "the router", "10.0.0.2", null, Now)).Message, StringComparison.Ordinal);
        Assert.Contains("already", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.StateTranslation(workspace, "10.0.0.2", "203.0.113.7", null, Now)).Message, StringComparison.Ordinal);
        Assert.Contains("none to withdraw", Assert.Throws<InvalidOperationException>(() =>
            InvestigationWorkspace.WithdrawTranslation(workspace, "203.0.113.7:8443", "10.0.0.2:443", Now)).Message, StringComparison.Ordinal);
        Assert.Equal("[2001:db8::7]:443", InvestigationWorkspace.CanonicalEndpoint(" [2001:DB8:0::7]:443 "));
        Assert.Null(InvestigationWorkspace.CanonicalEndpoint("10.0.0.1:0"));

        // An earlier version's file holds no translation.
        File.WriteAllText(workspace, File.ReadAllText(workspace).Replace($"\"{InvestigationWorkspace.Contract}\"",
            $"\"{InvestigationWorkspace.SeventhContract}\"", StringComparison.Ordinal));
        Assert.Contains("holds no address translation", Assert.Throws<InvalidDataException>(() =>
            InvestigationWorkspace.Read(workspace)).Message, StringComparison.Ordinal);
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
        File.WriteAllText(workspace, File.ReadAllText(workspace).Replace($"\"{InvestigationWorkspace.Contract}\"", "\"workspace-v3\"", StringComparison.Ordinal));
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
    private static ObservationRowV1[] ClientRows(long at, string server = Server) =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1)),
        Timed(Transfer(at, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 20).Between(Client, server)),
        Timed(Transfer(at + 1, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 21).Between(Client, server)),
        Timed(Transfer(at + 2, ObservationKind.Receive, AccountingSide.ReceiveSide, 200, 100, 22).Between(Client, server)),
        Timed(Transfer(at + 4, ObservationKind.Disconnect, AccountingSide.EndpointActivity, 0, 100, 24).Between(Client, server)),
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
