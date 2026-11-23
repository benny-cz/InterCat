using System.Text.Json.Nodes;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// An investigation collected from a capture across several hosts (`contracts/workspace-v27.md` §9, plan M11): it keeps
/// every host's part - collected, not collected yet or recorded nothing - and the clock readings its controller took, its
/// latest revision in force and every revision kept, and refuses one that contradicts itself.
/// </summary>
public sealed class InvestigationCoordinationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Pin = new('a', 64);
    private readonly string root = Path.Combine(Path.GetTempPath(), "InterCat.Application.Tests.Coordination", Guid.NewGuid().ToString("N"));

    public InvestigationCoordinationTests() => Directory.CreateDirectory(root);

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

    [Fact(DisplayName = "§9: an investigation keeps the capture across hosts it was collected from - each host's part and its clock readings - its latest revision in force, every revision kept, nothing recorded again where nothing changed")]
    public void AnInvestigationKeepsItsCaptureAcrossHosts()
    {
        string workspace = Path.Combine(root, "case" + InvestigationWorkspace.Extension);
        _ = InvestigationWorkspace.Create(workspace, Now);
        (Guid first, Guid second) = (Guid.NewGuid(), Guid.NewGuid());
        _ = InvestigationWorkspace.Add(workspace, Made("alpha", first, "lab-1"), Now);
        WorkspaceClockReading start = new(Now, Now.AddMilliseconds(5_001), Now.AddMilliseconds(3));
        WorkspaceCoordination coordination = Coordination(
            Host("lab-1", first, WorkspaceHostPart.Collected) with
            {
                SessionId = first,
                Start = start,
                End = new(Now.AddMinutes(1), Now.AddMinutes(1).AddMilliseconds(5_001), Now.AddMinutes(1).AddMilliseconds(2)),
            },
            Host("lab-2", second, WorkspaceHostPart.NotCollected) with { Stage = "collect", Reason = "Its agent could not be reached." },
            Host("lab-3", null, WorkspaceHostPart.NotRecorded) with { Stage = "connect", Reason = "No agent answered as one." });

        WorkspaceCoordination recorded = InvestigationWorkspace.RecordCoordination(workspace, coordination, Now)!;
        Assert.Equal((1, Now), (recorded.Revision, recorded.RecordedUtc));
        Assert.Null(InvestigationWorkspace.RecordCoordination(workspace, coordination, Now.AddMinutes(1)));

        // Collected again later: the second host's session joins as a new revision in force, and the first is kept.
        _ = InvestigationWorkspace.Add(workspace, Made("beta", second, "lab-2"), Now.AddMinutes(2));
        WorkspaceCoordination collected = coordination with
        {
            Hosts = [coordination.Hosts[0], coordination.Hosts[1] with { Part = WorkspaceHostPart.Collected, SessionId = second, Stage = null, Reason = null },
                coordination.Hosts[2]],
        };
        Assert.Equal(2, InvestigationWorkspace.RecordCoordination(workspace, collected, Now.AddMinutes(3))!.Revision);

        InvestigationWorkspaceFile read = InvestigationWorkspace.Read(workspace);
        Assert.Equal(InvestigationWorkspace.Contract, read.Contract);
        Assert.Equal([1, 2], read.Coordinations.Select(revision => revision.Revision));
        WorkspaceCoordination inForce = Assert.Single(InvestigationWorkspace.CoordinationsInForce(read));
        Assert.Equal(collected.Hosts, inForce.Hosts);
        Assert.Equal((start, Now.AddMinutes(1)), (inForce.Hosts[0].Start, inForce.Hosts[0].End!.SentUtc));
        Assert.Equal(coordination.Hosts, read.Coordinations[0].Hosts);
    }

    [Fact(DisplayName = "§9: a capture across hosts that contradicts itself is refused whole, and a version 25 file holds none yet reads without one, written as version 26")]
    public void ACaptureAcrossHostsThatContradictsItselfIsRefused()
    {
        string workspace = Path.Combine(root, "case" + InvestigationWorkspace.Extension);
        _ = InvestigationWorkspace.Create(workspace, Now);
        Guid first = Guid.NewGuid();
        _ = InvestigationWorkspace.Add(workspace, Made("alpha", first, "lab-1"), Now);
        WorkspaceCoordinatedHost host = Host("lab-1", first, WorkspaceHostPart.Collected) with { SessionId = first };
        WorkspaceCoordination good = Coordination(host);
        (WorkspaceCoordination Wrong, string Said)[] wrongs =
        [
            (good with { Hosts = [] }, "names no capture or profile, or no host"),
            (good with { Profile = " " }, "names no capture or profile, or no host"),
            (good with { Hosts = [host with { SessionId = Guid.NewGuid() }] }, "that is no member"),
            (good with { Hosts = [host with { Part = WorkspaceHostPart.NotRecorded, SessionId = null, Reason = "none" }] }, "recorded nothing yet names its capture"),
            (good with { Hosts = [host with { Part = WorkspaceHostPart.NotCollected, SessionId = null }] }, "says no reason"),
            (good with { Hosts = [host with { Stage = "lunch" }] }, "which is no stage"),
            (good with { Hosts = [host with { Agent = "abc" }] }, "without its name, identity or agent's pin"),
            (good with { Hosts = [host, host with { MachineName = "lab-9" }] }, "names one capture for two hosts"),
            (good with { Hosts = [host with { End = new(Now, Now, Now) }] }, "out of order"),
            (good with { Hosts = [host with { Start = new(Now, Now, Now.AddSeconds(-1)) }] }, "out of order"),
        ];
        foreach ((WorkspaceCoordination wrong, string said) in wrongs)
        {
            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => InvestigationWorkspace.RecordCoordination(workspace, wrong, Now));
            Assert.Contains(said, refused.Message, StringComparison.Ordinal);
        }

        Assert.Empty(InvestigationWorkspace.Read(workspace).Coordinations);

        // A version 25 file holds none; one without any reads, and the next change writes it as version 26.
        _ = InvestigationWorkspace.RecordCoordination(workspace, good, Now);
        string text = File.ReadAllText(workspace);
        File.WriteAllText(workspace, text.Replace($"\"{InvestigationWorkspace.Contract}\"", $"\"{InvestigationWorkspace.TwentyFifthContract}\"", StringComparison.Ordinal));
        InvalidDataException old = Assert.Throws<InvalidDataException>(() => InvestigationWorkspace.Read(workspace));
        Assert.Contains("a workspace-v25 file holds no capture across hosts", old.Message, StringComparison.Ordinal);
        JsonObject earlier = JsonNode.Parse(text)!.AsObject();
        _ = earlier.Remove("coordinations");
        earlier["contract"] = InvestigationWorkspace.TwentyFifthContract;
        File.WriteAllText(workspace, earlier.ToJsonString());
        Assert.Equal(InvestigationWorkspace.TwentyFifthContract, InvestigationWorkspace.Read(workspace).Contract);
        _ = InvestigationWorkspace.RecordCoordination(workspace, good, Now);
        Assert.Equal(InvestigationWorkspace.Contract, InvestigationWorkspace.Read(workspace).Contract);
    }

    /// <summary>A published session of its own capture on <paramref name="host"/>, named <paramref name="sessionId"/>.</summary>
    private string Made(string name, Guid sessionId, string host)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), sessionId, "coordination-tests");
        Publish(store, Rows(100), capture: CaptureId.New(), clock: ClockFor(new ClockId(Guid.NewGuid()), host));
        store.ReleaseSegmentReaders();
        return directory;
    }

    private static ObservationRowV1[] Rows(long ticks) =>
    [
        Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)ticks)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = ticks * 100 },
    ];

    private static WorkspaceCoordination Coordination(params WorkspaceCoordinatedHost[] hosts) => new()
    {
        Revision = 0,
        CoordinationId = Guid.NewGuid(),
        StartedUtc = Now,
        Profile = "explore",
        Hosts = hosts,
        RecordedUtc = Now,
    };

    private static WorkspaceCoordinatedHost Host(string name, Guid? capture, WorkspaceHostPart part) => new()
    {
        MachineName = name,
        HostId = Guid.NewGuid(),
        Agent = Pin,
        CaptureId = capture,
        Part = part,
    };
}
