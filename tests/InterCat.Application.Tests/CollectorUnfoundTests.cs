using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// §19.5: a collector the capture names that no process instance is - its creation time unread, or carried by no lifecycle
/// record of its PID - is said where a person would take a process holding that PID for it, in one set of words for the
/// inspector, `icat processes` and `icat overview` (R5, R18), and no process is labelled as it.
/// </summary>
public sealed class CollectorUnfoundTests
{
    [Fact(DisplayName = "§19.5: a collector no process instance is is said in the explanation of each process holding its PID, and of no other")]
    public void AnUnfoundCollectorIsSaidWhereItsPidIsHeld()
    {
        Assert.Equal("The capture names the InterCat process that asked to record as PID 7008, but its creation time was not "
            + "read, so no process can be shown to be it, and none is labelled as it (collector-binding-v1).",
            CollectorText.Unfound(new() { Role = CollectorRole.Client, ProcessId = 7008 }));
        Assert.Equal("The capture names icat record as PID 4120, but no lifecycle record of PID 4120 carries the creation time "
            + "it names, so no process here is it, and none is labelled as it (collector-binding-v1).",
            CollectorText.Unfound(new() { Role = CollectorRole.Recorder, ProcessId = 4120, CreatedUtc = CollectorBrokerCreated }));
        Assert.StartsWith("The capture names InterCat's broker as PID 12345, but",
            CollectorText.Unfound(new() { Role = CollectorRole.Broker, ProcessId = 12345 }), StringComparison.Ordinal);
        Assert.Equal(("Broker", "Its client", "Recorder"), (CollectorText.RoleName(CollectorRole.Broker),
            CollectorText.RoleName(CollectorRole.Client), CollectorText.RoleName(CollectorRole.Recorder)));

        // The capture names its client, whose creation time was not read, and a recorder of the broker's PID created when
        // no process holding it was: neither is an instance, and the snapshot keeps them, set aside or not.
        using var session = new TemporarySession();
        PublishCollected(session.Store);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        WorkspaceSnapshot snapshot = OverviewWorkspace.From(overview);
        Assert.Same(overview.Collectors, snapshot.Collectors);
        Assert.Same(overview.Collectors, WorkspaceCollectors.SetAside(snapshot).Collectors);
        Assert.Equal([CollectorRole.Client, CollectorRole.Recorder], snapshot.Collectors.Unfound.Select(collector => collector.Role));
        string client = CollectorText.Unfound(snapshot.Collectors.Unfound[0]);
        string recorder = CollectorText.Unfound(snapshot.Collectors.Unfound[1]);

        // The process holding the client's PID is said not to be taken for it; each holder of the broker's PID, the broker
        // itself among them, is said not to be the recorder; and a process explained without the capture's collectors is
        // explained as before.
        ProcessNode Pid(int pid, int holder = 1) => snapshot.Processes.Single(node => node.ProcessId == pid && node.PidHolder == holder);
        string explained = ProcessBindingText.Explain(Pid(7008), snapshot.Collectors.Unfound);
        Assert.Contains(" " + client + " Coverage over the session: ", explained, StringComparison.Ordinal);
        Assert.DoesNotContain(recorder, explained, StringComparison.Ordinal);
        Assert.Contains(recorder, ProcessBindingText.Explain(Pid(4120, 2), snapshot.Collectors.Unfound), StringComparison.Ordinal);
        Assert.DoesNotContain(client, ProcessBindingText.Explain(Pid(4120, 2), snapshot.Collectors.Unfound), StringComparison.Ordinal);
        string broker = ProcessBindingText.Explain(Pid(4120), snapshot.Collectors.Unfound);
        Assert.True(broker.IndexOf("It is InterCat's broker", StringComparison.Ordinal) < broker.IndexOf(recorder, StringComparison.Ordinal));
        Assert.Equal(ProcessBindingText.Explain(Pid(7008)), ProcessBindingText.Explain(Pid(7008), []));
        Assert.DoesNotContain("The capture names", ProcessBindingText.Explain(Pid(7008)), StringComparison.Ordinal);

        // A capture naming none has none to say.
        using var unnamed = new TemporarySession();
        PublishCollected(unnamed.Store, named: false);
        WorkspaceSnapshot plain = OverviewWorkspace.From(SessionOverviewProjector.Project(unnamed.Store));
        Assert.False(plain.Collectors.Recorded);
        Assert.Empty(plain.Collectors.Unfound);
        Assert.False(SyntheticWorkspace.Create().Collectors.Recorded);
    }
}
