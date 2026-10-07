using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Application.Tests;

/// <summary>
/// M5's demo investigation: two hosts' sessions that exchange TCP and UDP, aligned by their wall clocks, every mechanism's
/// coverage stated, generated the same way every time, and saying wherever it is read that it was generated.
/// </summary>
public sealed class DemoInvestigationTests
{
    [Fact(DisplayName = "§14: the demo holds two hosts' sessions that exchange TCP and UDP, aligned by their wall clocks, every mechanism's coverage stated")]
    public void TheDemoHoldsTwoHostsAligned()
    {
        string folder = Folder();
        try
        {
            DemoInvestigationResult made = DemoInvestigation.Create(folder);
            Assert.Equal((Path.Combine(folder, DemoInvestigation.WorkspaceFileName), Path.Combine(folder, "demo-client"),
                Path.Combine(folder, "demo-server")), (made.WorkspacePath, made.ClientSession, made.ServerSession));

            // Each session verifies whole, is published as the demo's, and its overview and title say so.
            SessionOverviewBundle client = Project(made.ClientSession, out SessionManifestV1 clientManifest);
            SessionOverviewBundle server = Project(made.ServerSession, out SessionManifestV1 serverManifest);
            Assert.All(new[] { clientManifest, serverManifest }, manifest =>
                Assert.Equal(DemoInvestigation.SourceIdentity, manifest.SourceIdentity));
            Assert.True(client.Demo && server.Demo);

            // Each is published whole, as a finished capture is: finalized, calibrated, with its derivation checkpoint.
            Assert.All(new[] { clientManifest, serverManifest }, manifest => Assert.Equal(
                [StoreDependencyKind.Index, StoreDependencyKind.CaptureFinalization, StoreDependencyKind.ClockCalibration],
                new[] { StoreDependencyKind.Index, StoreDependencyKind.CaptureFinalization, StoreDependencyKind.ClockCalibration }
                    .Where(kind => manifest.Dependencies.Any(dependency => dependency.Kind == kind))));
            Assert.StartsWith("Demo session · generation ", OverviewWorkspace.From(client).Title, StringComparison.Ordinal);

            // The client's browser and sync talk out; the server's api, db and files answer, api and db over loopback.
            Assert.Equal([("browser.exe", 73L), ("sync.exe", 20L)],
                client.Nodes.Select(node => (node.Name, node.Records)).OrderBy(node => node.Name, StringComparer.Ordinal));
            Assert.Equal([("api.exe", 121L), ("db.exe", 61L), ("files.exe", 19L)],
                server.Nodes.Select(node => (node.Name, node.Records)).OrderBy(node => node.Name, StringComparer.Ordinal));
            Assert.Empty(client.Edges);
            CommunicationEdge loopback = Assert.Single(server.Edges);
            Assert.Equal(Mechanism.Tcp, loopback.Mechanism);
            Assert.Equal(["api.exe", "db.exe"], new[] { loopback.SourceId, loopback.TargetId }
                .Select(id => server.Nodes.Single(node => node.Id == id).Name).Order(StringComparer.Ordinal));

            // Its records fall where their readings put them in its minute: the client's from its browser starting at 0.2 s
            // to its last answer at 59.03 s.
            Assert.Equal(new TimeRange(2_000_000, 590_300_001), client.Extent);

            // Each recorded a minute, the server from 0.75 s after the client by the wall clock.
            Assert.Equal((DemoInvestigation.Began, DemoInvestigation.Began.AddMilliseconds(750)), (client.Began, server.Began));
            Assert.Equal(new TimeRange(0, 600_000_000), client.Recording);
            Assert.Equal(new TimeRange(0, 600_000_000), server.Recording);

            // Every mechanism's coverage is stated: what each collected, the server's two lost records, and the rest as not
            // collected - pipes, sections, RPC and ALPC among them.
            Assert.All(new[] { Mechanism.ProcessLifecycle, Mechanism.Tcp, Mechanism.Udp }, mechanism =>
            {
                Assert.Equal(CoverageState.Covered, Coverage(client, mechanism).State);
                Assert.Equal(CoverageState.PartialGap, Coverage(server, mechanism).State);
                Assert.Contains("2 lost events", Coverage(server, mechanism).Reason, StringComparison.Ordinal);
            });
            Assert.All(new[] { Mechanism.NamedPipe, Mechanism.SharedSection, Mechanism.Rpc, Mechanism.Alpc }, mechanism =>
                Assert.Equal((CoverageState.NotCollected, CoverageState.NotCollected),
                    (Coverage(client, mechanism).State, Coverage(server, mechanism).State)));

            // Its records name the demo's own provider, never a Windows one.
            Assert.Equal(DemoInvestigation.ProviderName, KnownProviders.NameOf(DemoInvestigation.Provider));

            // The investigation holds both, names their hosts, aligns the server to the client by their wall clocks, and
            // notes what it is.
            InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(made.WorkspacePath);
            Assert.All(InvestigationWorkspace.Resolve(made.WorkspacePath, workspace), resolution =>
                Assert.Equal(WorkspaceMemberState.Present, resolution.State));
            Assert.Equal(["demo client", "demo server"], workspace.HostAliases.Select(alias => alias.Alias).Order(StringComparer.Ordinal));
            WorkspaceAlignment alignment = Assert.Single(workspace.Alignments);
            Assert.Equal((WorkspaceAlignmentMode.WallClock, serverManifest.SessionId, clientManifest.SessionId),
                (alignment.Mode, alignment.SessionId, alignment.ReferenceSessionId));
            Assert.Equal(750_000_000L, alignment.ReferenceNanoseconds - alignment.SessionNanoseconds);
            Assert.Equal(2_000_000L + 1_000, alignment.WithinNanoseconds);
            Assert.StartsWith(DemoInvestigation.Disclosure, Assert.Single(InvestigationWorkspace.NotesInForce(workspace)).Text,
                StringComparison.Ordinal);

            // Its hosts' TCP connections mirror each other, one candidate each, in time; the UDP to the host neither
            // recorded has none.
            WorkspaceCorrelationResult candidates = WorkspaceCorrelation.Candidates(made.WorkspacePath);
            Assert.Equal(2, candidates.Candidates.Count);
            Assert.All(candidates.Candidates, candidate =>
            {
                Assert.Equal((CandidateTiming.Overlapping, 0), (candidate.Timing, candidate.Alternatives));
                Assert.Equal(Mechanism.Tcp, candidate.First.Connection.Summary.Mechanism);
            });
            Assert.Empty(candidates.Unread);
        }
        finally
        {
            Remove(folder);
        }
    }

    [Fact(DisplayName = "§14: the demo's sessions are the same, record for record, every time it is made, and only into a new or empty folder")]
    public void TheDemoIsTheSameEveryTime()
    {
        string first = Folder();
        string second = Folder();
        try
        {
            DemoInvestigationResult one = DemoInvestigation.Create(first);
            Directory.CreateDirectory(second);
            DemoInvestigationResult two = DemoInvestigation.Create(second);

            // The same generations, to the digest of every file they name.
            foreach ((string a, string b) in new[] { (one.ClientSession, two.ClientSession), (one.ServerSession, two.ServerSession) })
            {
                Assert.Equal(Current(a).Digest, Current(b).Digest);
            }

            // Each investigation file is one of its own, over the same sessions, aligned the same way.
            InvestigationWorkspaceFile left = InvestigationWorkspace.Read(one.WorkspacePath);
            InvestigationWorkspaceFile right = InvestigationWorkspace.Read(two.WorkspacePath);
            Assert.NotEqual(left.WorkspaceId, right.WorkspaceId);
            Assert.Equal(left.Members.Select(member => (member.SessionId, member.CaptureId, member.Path, member.ManifestDigest)),
                right.Members.Select(member => (member.SessionId, member.CaptureId, member.Path, member.ManifestDigest)));
            Assert.Equal(left.Alignments.Select(Comparable), right.Alignments.Select(Comparable));

            // A folder that holds anything is refused, and left as it was.
            string before = Current(one.ClientSession).Digest;
            string[] held = [.. Directory.EnumerateFileSystemEntries(first).Order(StringComparer.Ordinal)];
            IOException refused = Assert.Throws<IOException>(() => DemoInvestigation.Create(first));
            Assert.Contains("only into a new or empty folder", refused.Message, StringComparison.Ordinal);
            Assert.Equal(before, Current(one.ClientSession).Digest);
            Assert.Equal(held, Directory.EnumerateFileSystemEntries(first).Order(StringComparer.Ordinal));
            Assert.Equal(3, held.Length);
        }
        finally
        {
            Remove(first);
            Remove(second);
        }

        static object Comparable(WorkspaceAlignment alignment) => alignment with { RecordedUtc = default };
    }

    private static SessionOverviewBundle Project(string path, out SessionManifestV1 manifest)
    {
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(path));
        manifest = store.Current!;
        try
        {
            return SessionOverviewProjector.Project(store);
        }
        finally
        {
            store.ReleaseSegmentReaders();
        }
    }

    private static SessionManifestV1 Current(string path) =>
        SessionStore.OpenExisting(LocalOwnedDirectory.Open(path)).Current!;

    private static MechanismCoverage Coverage(SessionOverviewBundle overview, Mechanism mechanism) =>
        overview.MechanismCoverage.Single(entry => entry.Mechanism == mechanism);

    private static string Folder() => Path.Combine(Path.GetTempPath(), "intercat-demo-" + Guid.NewGuid().ToString("N"));

    private static void Remove(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
