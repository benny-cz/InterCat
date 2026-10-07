using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// §19.5's labels (`collector-binding-v1`): the process instance a capture's collector is - its PID, and the creation time
/// its lifecycle records carry - is labelled as InterCat's own wherever it is described, and no other is, not even another
/// holder of its PID; a collector no instance is stays unfound rather than guessed.
/// </summary>
public sealed class CollectorBindingTests
{
    [Fact(DisplayName = "§19.5: the instance whose PID and creation time a collector names is labelled InterCat's own, and no other holder of its PID is")]
    public void TheCollectorsInstanceIsLabelled()
    {
        using var session = new TemporarySession();
        PublishCollected(session.Store);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);

        // The broker is the first holder of PID 4120, created when the collectors say; the later holder is not it.
        ProcessNode broker = Assert.Single(overview.Nodes, node => node.Collector is not null);
        Assert.Equal((4120, CollectorRole.Broker, 1), (broker.ProcessId, broker.Collector, broker.PidHolder));
        Assert.Equal("PID 4120 #1 · created during capture · InterCat's broker", broker.Caption);
        Assert.Contains("It is InterCat's broker, which recorded this capture, as the capture's collectors name it by PID and "
            + "creation time (collector-binding-v1): its records are InterCat's own activity, counted as any process's are.",
            ProcessBindingText.Explain(broker), StringComparison.Ordinal);
        ProcessNode later = Assert.Single(overview.Nodes, node => node.ProcessId == 4120 && node.PidHolder == 2);
        Assert.Null(later.Collector);
        Assert.Equal("PID 4120 #2 · created during capture", later.Caption);
        Assert.DoesNotContain("InterCat", ProcessBindingText.Explain(later), StringComparison.Ordinal);

        // The client, whose creation time was not read, is no instance, nor is a recorder of the broker's PID created when no
        // process holding it was: each stays unfound, never matched by its PID alone.
        Assert.True(overview.Collectors.Recorded);
        Assert.Equal([(CollectorRole.Client, 7008), (CollectorRole.Recorder, 4120)],
            overview.Collectors.Unfound.Select(unfound => (unfound.Role, unfound.ProcessId)));
        Assert.Null(Assert.Single(overview.Nodes, node => node.ProcessId == 7008).Collector);

        // A capture that names no collector labels nothing, and says it recorded none.
        using var unnamed = new TemporarySession();
        PublishCollected(unnamed.Store, named: false);
        SessionOverviewBundle plain = SessionOverviewProjector.Project(unnamed.Store);
        Assert.All(plain.Nodes, node => Assert.Null(node.Collector));
        Assert.Equal((false, 0), (plain.Collectors.Recorded, plain.Collectors.Unfound.Count));
    }
}
