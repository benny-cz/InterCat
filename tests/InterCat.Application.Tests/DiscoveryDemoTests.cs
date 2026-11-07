using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Application.Tests;

/// <summary>
/// The discovery demo: one generated host whose processes share named synchronization objects and a section, by its handles
/// and by its views - found as a capture's resources are, generated the same way every time, and saying it was generated.
/// </summary>
public sealed class DiscoveryDemoTests
{
    [Fact(DisplayName = "§14: the discovery demo's host shares named objects and a section, by its handles and by its views, talks over AF_UNIX sockets and sends window messages, which its views find as a capture's - hubs, listed resources, their ends and traffic, each process's records and the resource-topology basis - and says it was generated")]
    public void TheDiscoveryDemoHoldsSharedResources()
    {
        string folder = Folder();
        try
        {
            DiscoveryDemoResult made = DiscoveryDemo.Create(folder);
            Assert.Equal((Path.Combine(folder, DiscoveryDemo.WorkspaceFileName), Path.Combine(folder, DiscoveryDemo.SessionFolder)),
                (made.WorkspacePath, made.Session));

            // Published whole as the demo's, finalized, calibrated and checkpointed, and its overview says so.
            SessionOverviewBundle overview = Project(made.Session, out SessionManifestV1 manifest, out SessionStore store);
            Assert.Equal(DemoInvestigation.SourceIdentity, manifest.SourceIdentity);
            Assert.True(overview.Demo);
            Assert.All(new[] { StoreDependencyKind.Index, StoreDependencyKind.CaptureFinalization, StoreDependencyKind.ClockCalibration },
                kind => Assert.Contains(manifest.Dependencies, dependency => dependency.Kind == kind));
            Assert.Equal(["editor.exe", "indexer.exe", "preview.exe"],
                overview.Nodes.Select(node => node.Name).Order(StringComparer.Ordinal));
            ProcessInstanceId Id(string name) => overview.Nodes.Single(node => node.Name == name).Id;

            // The graph's hubs: the event the editor shares with the indexer, the semaphore and the section - by its handles and
            // by its views, never joined - it shares with the preview.
            (string Key, string Label, string[] Holders)[] hubs =
            [
                ("resource:hub:mapped-section:3", "Mapped section 3", ["editor.exe", "preview.exe"]),
                ("resource:hub:object:1", "IndexReady", ["editor.exe", "indexer.exe"]),
                ("resource:hub:object:3", "PreviewSlots", ["editor.exe", "preview.exe"]),
                ("resource:hub:section:7", "PreviewFrames", ["editor.exe", "preview.exe"]),
            ];
            (string Key, string Label, string[] Holders)[] drawn =
            [
                .. overview.ResourceLinks.OrderBy(link => link.Key, StringComparer.Ordinal).Select(link => (link.Key, link.Label,
                    link.Holders.Select(holder => overview.Nodes.Single(node => node.Id == holder).Name).Order(StringComparer.Ordinal).ToArray())),
            ];
            Assert.Equal(hubs, drawn, new HubComparer());
            Assert.Equal(4_194_304L, overview.ResourceLinks.Single(link => link.Key == "resource:hub:mapped-section:3").LargestView);

            // The indexer holds the event by the handle the editor duplicated into it, which it closes: its records say how
            // each process came to hold what it held, as a capture's do.
            SessionResources derived = SessionResources.Of(store);
            ResourceInstance indexReady = derived.Instances.Single(resource => resource is { Mechanism: Mechanism.Synchronization, Number: 1 });
            ResourceHolder indexer = indexReady.Holders.Single(holder => holder.Process == Id("indexer.exe"));
            ResourceHolder editor = indexReady.Holders.Single(holder => holder.Process == Id("editor.exe"));
            Assert.Equal((0, 1, 1, 0), (indexer.HandlesMade, indexer.HandlesDuplicated, indexer.HandlesClosed, indexer.HeldAtStop));
            Assert.Equal((1, 0, 0, 1), (editor.HandlesMade, editor.HandlesDuplicated, editor.HandlesClosed, editor.HeldAtStop));

            // Listed: those and the mutex the editor alone held and the view the indexer mapped before the capture, never the
            // editor's module; each end as its last record says it.
            Dictionary<string, ResourceLink> listed = overview.Resources.ToDictionary(link => link.Key, StringComparer.Ordinal);
            Assert.Equal(
                ["resource:hub:mapped-section:3", "resource:hub:mapped-section:5", "resource:hub:object:1", "resource:hub:object:2",
                    "resource:hub:object:3", "resource:hub:section:7", "resource:hub:socket:11", "resource:hub:socket:12",
                    "resource:hub:socket:13"],
                listed.Keys.Order(StringComparer.Ordinal));

            // The sockets are never hubs: the indexer's listening one carried nothing - idle - and the editor's and the
            // indexer's accepted one carried the five requests and their answers, the last answer's size unmeasured.
            Assert.True(listed["resource:hub:socket:11"].Traffic!.Idle);
            Assert.Equal(new ResourceTraffic(5, 5, 2_560, 16_384, 1), listed["resource:hub:socket:13"].Traffic);
            Assert.Equal(new ResourceTraffic(5, 5, 20_480, 2_560, 0), listed["resource:hub:socket:12"].Traffic);
            Assert.Equal((Id("editor.exe"), Id("indexer.exe")),
                (Assert.Single(listed["resource:hub:socket:13"].Holders), Assert.Single(listed["resource:hub:socket:12"].Holders)));

            // Each process's records of each mechanism: the sockets' calls, and the window messages the editor sent the
            // preview's window - four ends each - and the two the preview posted back, two ends each.
            long Records(string name, Mechanism mechanism) =>
                overview.Nodes.Single(node => node.Name == name).Activity.Where(count => count.Mechanism == mechanism).Sum(count => count.Records);
            Assert.Equal((13L, 16L), (Records("editor.exe", Mechanism.UnixDomainSocket), Records("indexer.exe", Mechanism.UnixDomainSocket)));
            Assert.Equal((8L, 8L, 0L), (Records("editor.exe", Mechanism.WindowMessage), Records("preview.exe", Mechanism.WindowMessage),
                Records("indexer.exe", Mechanism.WindowMessage)));
            Assert.Equal((ResourceEnd.HeldAtStop, true, @"\Sessions\1\BaseNamedObjects"),
                (listed["resource:hub:object:2"].End, listed["resource:hub:object:2"].MadeInCapture, listed["resource:hub:object:2"].Namespace));
            Assert.Equal((ResourceEnd.HeldAtStop, ResourceEnd.Closed, ResourceEnd.Closed),
                (listed["resource:hub:object:1"].End, listed["resource:hub:object:3"].End, listed["resource:hub:section:7"].End));
            Assert.Equal((false, ResourceEnd.Closed, Id("indexer.exe")),
                (listed["resource:hub:mapped-section:5"].MadeInCapture, listed["resource:hub:mapped-section:5"].End,
                    Assert.Single(listed["resource:hub:mapped-section:5"].Holders)));

            // The resource-topology basis answers over them: ten resources, the module's and the sockets among them, its capacity
            // said apart; and the editor's and the preview's mappings measured, the indexer's begun before the capture stated.
            MetricResult resources = SessionMetrics.Evaluate(store, new MetricRequest { Basis = AnalysisBasis.ResourceTopology, Metric = Metric.ActiveChannels });
            Assert.Equal(10L, resources.Value);
            MetricResult capacity = SessionMetrics.Evaluate(store, new MetricRequest { Basis = AnalysisBasis.ResourceTopology, Metric = Metric.MappingCapacity });
            Assert.Equal(4_194_304L + 1_048_576 + 131_072, capacity.Value);
            MetricResult lifetimes = SessionMetrics.Evaluate(store, new MetricRequest
            {
                Basis = AnalysisBasis.ResourceTopology,
                Metric = Metric.Duration,
                DurationInterval = DurationInterval.MappingLifetime,
            });
            Assert.Equal(((long?)45_800_000_000L, 2L, 1L), (lifetimes.Value, lifetimes.KnownContributions, lifetimes.UnknownContributions));
            store.ReleaseSegmentReaders();

            // Its capture collected each mechanism it holds, nothing lost, and no other.
            Assert.All(new[] { Mechanism.ProcessLifecycle, Mechanism.Synchronization, Mechanism.SharedSection, Mechanism.UnixDomainSocket,
                Mechanism.WindowMessage }, mechanism =>
                Assert.Equal(CoverageState.Covered, overview.MechanismCoverage.Single(entry => entry.Mechanism == mechanism).State));
            Assert.All(new[] { Mechanism.Tcp, Mechanism.NamedPipe, Mechanism.ComCall }, mechanism =>
                Assert.Equal(CoverageState.NotCollected, overview.MechanismCoverage.Single(entry => entry.Mechanism == mechanism).State));

            // The investigation holds it, names its host and notes what it is.
            InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(made.WorkspacePath);
            Assert.Equal(WorkspaceMemberState.Present, Assert.Single(InvestigationWorkspace.Resolve(made.WorkspacePath, workspace)).State);
            Assert.Equal("demo workstation", Assert.Single(workspace.HostAliases).Alias);
            Assert.StartsWith(DemoInvestigation.Disclosure, Assert.Single(InvestigationWorkspace.NotesInForce(workspace)).Text,
                StringComparison.Ordinal);
        }
        finally
        {
            Remove(folder);
        }
    }

    [Fact(DisplayName = "§14: the discovery demo's session is the same, record for record, every time it is made, and only into a new or empty folder")]
    public void TheDiscoveryDemoIsTheSameEveryTime()
    {
        string first = Folder();
        string second = Folder();
        try
        {
            DiscoveryDemoResult one = DiscoveryDemo.Create(first);
            DiscoveryDemoResult two = DiscoveryDemo.Create(second);
            Assert.Equal(Current(one.Session).Digest, Current(two.Session).Digest);
            Assert.NotEqual(InvestigationWorkspace.Read(one.WorkspacePath).WorkspaceId, InvestigationWorkspace.Read(two.WorkspacePath).WorkspaceId);

            // A folder that holds anything is refused, and left as it was.
            string[] held = [.. Directory.EnumerateFileSystemEntries(first).Order(StringComparer.Ordinal)];
            IOException refused = Assert.Throws<IOException>(() => DiscoveryDemo.Create(first));
            Assert.Contains("only into a new or empty folder", refused.Message, StringComparison.Ordinal);
            Assert.Equal(held, Directory.EnumerateFileSystemEntries(first).Order(StringComparer.Ordinal));
            Assert.Equal(2, held.Length);
        }
        finally
        {
            Remove(first);
            Remove(second);
        }
    }

    private static SessionOverviewBundle Project(string path, out SessionManifestV1 manifest, out SessionStore store)
    {
        store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(path));
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

    private static string Folder() => Path.Combine(Path.GetTempPath(), "intercat-discovery-" + Guid.NewGuid().ToString("N"));

    private static void Remove(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // A hub as the test reads it: its key, its label and its holders' names, compared by value.
    private sealed class HubComparer : IEqualityComparer<(string Key, string Label, string[] Holders)>
    {
        public bool Equals((string Key, string Label, string[] Holders) x, (string Key, string Label, string[] Holders) y) =>
            x.Key == y.Key && x.Label == y.Label && x.Holders.SequenceEqual(y.Holders);

        public int GetHashCode((string Key, string Label, string[] Holders) obj) => HashCode.Combine(obj.Key, obj.Label);
    }
}
