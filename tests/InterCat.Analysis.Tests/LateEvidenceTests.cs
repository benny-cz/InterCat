using System.Security.Cryptography;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// Evidence that arrives in a later generation can complete what an earlier one left open: a connection's other end
/// recorded a chunk later. The later generation is a new revision that answers with it; a generation held from before
/// still answers exactly as it did, under the same identity, and no record it read changes its identity (I17, I1).
/// </summary>
public sealed class LateEvidenceTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [Fact(DisplayName = "I17: late evidence publishes a new revision, and a generation held from before answers as it did, under the same identity")]
    public void LateEvidenceLeavesAHeldGenerationAsItWas()
    {
        using var session = new TemporarySession();

        // The first chunk holds the client's side of a loopback connection: it connected and sent, and nothing recorded
        // so far holds the other end.
        Publish(session.Store,
        [
            Lifecycle(5, ObservationKind.Inventory, 200, 1),
            Lifecycle(10, ObservationKind.Create, 100, 2),
            Transfer(20, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 3).Between(ClientEnd, ServerEnd),
            Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 4).Between(ClientEnd, ServerEnd),
        ]);
        (ProcessInstanceId client, ProcessInstanceId server) = Instances(session.Store);
        MetricRequest between = Request(Metric.Observations) with { Between = new([client], [server]) };
        MetricRequest owned = Request(Metric.Observations) with { Owner = client };
        var listed = new MetricEvaluationOptions { EvidenceLimit = 20 };

        using EvidenceLease held = session.Store.AcquireLease(new EvidenceLeaseRequest { Duration = TimeSpan.FromMinutes(10) });
        MetricResult before = SessionMetrics.Evaluate(session.Store, held, between, listed);
        MetricResult ownedBefore = SessionMetrics.Evaluate(session.Store, held, owned, listed);
        Assert.Equal(1, before.Generation);
        Assert.Equal(0, before.Value);
        Assert.Equal(
            [(ProcessBindingReason.PeerNotObserved, 2L)],
            before.UnresolvedCounterparts.Select(entry => (entry.Key, entry.Value)));
        Assert.Equal([10L, 20, 30], ownedBefore.Evidence.Select(item => item.Observation.NativeTicks).Order());

        // The next chunk holds the server's side, recorded later in the stream though one of its records is older than the
        // client's send. The generation it publishes is a new revision: the client's records now have their other end.
        Publish(session.Store,
        [
            Transfer(21, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 5).Between(ServerEnd, ClientEnd),
            Transfer(31, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 200, 6).Between(ServerEnd, ClientEnd),
            Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 40, 200, 7).Between(ServerEnd, ClientEnd),
            Transfer(41, ObservationKind.Receive, AccountingSide.ReceiveSide, 40, 100, 8).Between(ClientEnd, ServerEnd),
        ]);
        Assert.Equal((client, server), Instances(session.Store));
        MetricResult current = SessionMetrics.Evaluate(session.Store, between, listed);
        Assert.Equal(2, current.Generation);
        Assert.Equal(6, current.Value);
        Assert.Empty(current.UnresolvedCounterparts);
        Assert.Equal([20L, 21, 30, 31, 40, 41], current.Evidence.Select(item => item.Observation.NativeTicks).Order());
        Assert.NotEqual(before.Identity!.Hash, current.Identity!.Hash);

        // The generation held from before answers exactly as it did, and names the identity it named then.
        MetricResult again = SessionMetrics.Evaluate(session.Store, held, between, listed);
        Assert.Equal(before.Generation, again.Generation);
        Assert.Equal(before.Value, again.Value);
        Assert.Equal(before.Identity, again.Identity);
        Assert.Equal(before.UnresolvedCounterparts, again.UnresolvedCounterparts);
        Assert.Equal(before.Caveats, again.Caveats);
        Assert.Empty(again.Evidence);

        // A record the earlier generation read keeps its identity, its segment and its row in the later one.
        MetricResult ownedAgain = SessionMetrics.Evaluate(session.Store, held, owned, listed);
        MetricResult ownedNow = SessionMetrics.Evaluate(session.Store, owned, listed);
        Assert.Equal(Located(ownedBefore), Located(ownedAgain));
        Assert.Equal([10L, 20, 30, 41], ownedNow.Evidence.Select(item => item.Observation.NativeTicks).Order());
        Assert.Subset(Located(ownedNow).ToHashSet(), Located(ownedBefore).ToHashSet());

        // Nothing the later publication did touched what the held generation names: its manifest and every file it names
        // are where they were, byte for byte.
        Assert.True(File.Exists(Path.Combine(session.Path, held.ManifestName)), held.ManifestName);
        foreach (StoreDependency dependency in held.Manifest.Dependencies)
        {
            Assert.Equal(dependency.Digest, Digest(Path.Combine(session.Path, dependency.Name)));
        }
    }

    [Fact(DisplayName = "I17: an answer over a held generation is refused once its lease lets go, for another session's lease, and where no generation was published")]
    public void AHeldGenerationIsReadOnlyWhileItsLeaseHoldsIt()
    {
        using var session = new TemporarySession();
        MetricRequest count = Request(Metric.Observations);

        // A folder in which no generation was published answers nothing, and says why.
        Assert.Equal(SessionStore.NoGeneration, Assert.Throws<NoSessionException>(() => SessionMetrics.Evaluate(session.Store, count)).Message);
        Publish(session.Store, [Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 100, 100)]);

        EvidenceLease released = session.Store.AcquireLease();
        Assert.Equal(1, SessionMetrics.Evaluate(session.Store, released, count).Value);
        released.Dispose();
        InvalidOperationException letGo = Assert.Throws<InvalidOperationException>(
            () => SessionMetrics.Evaluate(session.Store, released, count));
        Assert.Contains("no longer holds it", letGo.Message, StringComparison.Ordinal);

        // A lease past its term holds nothing either, though nothing has released it yet.
        using EvidenceLease expired = session.Store.AcquireLease(nowUtc: DateTimeOffset.UtcNow.AddHours(-1));
        Assert.False(expired.IsReleased);
        Assert.Throws<InvalidOperationException>(() => SessionMetrics.Evaluate(session.Store, expired, count));

        // A lease names the session it holds a generation of, and no other session's store answers over it.
        string otherPath = Path.Combine(Path.GetTempPath(), "InterCat.Analysis.Tests.Metrics", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherPath);
        try
        {
            SessionStore other = SessionStore.Open(LocalOwnedDirectory.Open(otherPath), Guid.NewGuid(), "late-evidence-tests");
            Publish(other, [Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 100, 100)]);
            using EvidenceLease foreign = other.AcquireLease();
            ArgumentException refused = Assert.Throws<ArgumentException>(
                () => SessionMetrics.Evaluate(session.Store, foreign, count));
            Assert.Equal("lease", refused.ParamName);
        }
        finally
        {
            try
            {
                Directory.Delete(otherPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static (ProcessInstanceId Client, ProcessInstanceId Server) Instances(SessionStore store)
    {
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(Segments(store), TestClock);
        return (
            processes.Instances.Single(instance => instance.ProcessId == 100).Id,
            processes.Instances.Single(instance => instance.ProcessId == 200).Id);
    }

    private static IEnumerable<(ObservationId Id, string Segment, int Row)> Located(MetricResult result) =>
        result.Evidence.Select(item => (item.ObservationId, item.Segment, item.Row)).OrderBy(item => item.Row).ThenBy(item => item.Segment, StringComparer.Ordinal);

    private static string Digest(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static MetricRequest Request(Metric metric) => new()
    {
        Basis = AnalysisBasis.SourceObservations,
        Metric = metric,
    };
}
