using System.Globalization;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// Causal navigation (`contracts/links-v1.md`, plan M12): the links a generation records - a process started another, a
/// process's calls were served by another, an end connected to another - each by its rule and strength; only those the
/// evidence policy admits are followed, step by step or as a chain, and processes created near one another in time are
/// listed apart and never linked by that alone.
/// </summary>
public sealed class EvidenceLinksTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Fact(DisplayName = "P8: a known dependency is traversable link by link, and processes created and calling at the same moment are never linked")]
    public void AKnownDependencyIsTraversable()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkRows();
        Publish(session.Store, rows, fields: fields, coverage: TransportLedger(tcp: true, udp: true));
        LinkReading reading = EvidenceLinks.Read(session.Store);
        ProcessInstanceId Of(int pid, int holder = 1) =>
            reading.Processes.Values.Single(node => node.ProcessId == pid && node.PidHolder == holder).Id;
        string End(LinkEnd end) => end.Process is { } id ? reading.Processes[id].PidLabel : end.Address ?? "unseen";

        // Every link the evidence records, whatever its strength: a start by the parent's start key, one by its PID alone, one
        // by a reused PID's later holder; two calls served through ALPC; and the connections whose opening the capture saw.
        Assert.Equal(
            [
                "Called PID 400 > PID 1960 Correlated x2 svcctl",
                "Connected 10.0.0.5 > PID 4000 Correlated x1 :3389",
                "Connected PID 400 > 93.184.216.34 Correlated x1 :443",
                "Connected PID 400 > PID 4000 Correlated x1 :8080",
                "Connected PID 400 > unseen Correlated x1 :9100",
                "Connected PID 4000 > PID 4000 Correlated x1 :7000",
                "Started PID 300 > PID 400 Direct x1",
                "Started PID 300 > PID 430 Correlated x1",
                "Started PID 77 #2 > PID 420 Candidate x1",
            ],
            reading.Links.Select(link => $"{link.Kind} {End(link.From)} > {End(link.To)} {link.Strength} x{link.Occurrences}"
                    + (link.Interface is { } uuid ? " " + RpcInterfaceNames.Of(uuid)!.Value.Name : string.Empty)
                    + (link.Port is { } port ? $" :{port}" : string.Empty))
                .Order(StringComparer.Ordinal));

        // The dependency is traversable: shell.exe started app.exe, whose calls services.exe served, and whose connection
        // server.exe accepted - a chain as strong as its weakest link.
        IReadOnlyList<EvidenceLink> chain = EvidenceLinks.Chain(reading, Of(LinkShell), Of(LinkServices))!;
        Assert.Equal([(LinkKind.Started, Of(LinkShell), Of(LinkApp)), (LinkKind.Called, Of(LinkApp), Of(LinkServices))],
            chain.Select(link => (link.Kind, link.From.Process!.Value, link.To.Process!.Value)));
        Assert.Equal(RelationStrength.Correlated, EvidenceLinks.Weakest(chain));
        Assert.Equal([LinkKind.Started, LinkKind.Connected],
            EvidenceLinks.Chain(reading, Of(LinkShell), Of(LinkServer))!.Select(link => link.Kind));
        Assert.Equal([LinkKind.Called], EvidenceLinks.Chain(reading, Of(LinkApp), Of(LinkServices))!.Select(link => link.Kind));

        // A chain follows links where they lead, never back against them.
        Assert.Null(EvidenceLinks.Chain(reading, Of(LinkServices), Of(LinkShell)));
        Assert.Null(EvidenceLinks.Chain(reading, Of(LinkServices), Of(LinkShell), everyLink: true));

        // app.exe's links: what led to it and what it led to, each kind in turn, processes before hosts and the unseen end.
        StepLinks app = EvidenceLinks.Of(reading, Of(LinkApp));
        Assert.Equal([(LinkKind.Started, "PID 300")], app.LedToIt.Select(link => (link.Kind, End(link.From))));
        Assert.Equal(["PID 1960", "PID 4000", "93.184.216.34", "unseen"], app.ItLedTo.Select(link => End(link.To)));
        Assert.Empty(app.NotFollowed);

        // A process connected to itself led to itself: it is among what it led to, never also among what led to it, and no
        // chain runs through it.
        StepLinks server = EvidenceLinks.Of(reading, Of(LinkServer));
        Assert.Equal([("PID 400", "PID 4000"), ("10.0.0.5", "PID 4000")], server.LedToIt.Select(link => (End(link.From), End(link.To))));
        Assert.Equal([("PID 4000", "PID 4000")], server.ItLedTo.Select(link => (End(link.From), End(link.To))));

        // other.exe was created two readings after app.exe and made a call at the same moment as app.exe's first: near in
        // time, linked to nothing - listed apart, never followed, and no chain joins them, however the evidence policy reads.
        StepLinks other = EvidenceLinks.Of(reading, Of(LinkOther));
        Assert.Empty(other.LedToIt.Concat(other.ItLedTo).Concat(other.NotFollowed));
        Assert.Contains(other.Near, near => near.Process == Of(LinkApp) && near.OffsetTicks == -2);
        Assert.Equal(
            [(Of(LinkOther), 2L), (Of(LinkSibling), 3L), (Of(LinkReused), 4L), (Of(LinkReused, 2), 6L), (Of(LinkReusedChild), 7L)],
            app.Near.Select(near => (near.Process, near.OffsetTicks)));
        foreach (ProcessInstanceId end in new[] { Of(LinkApp), Of(LinkServices), Of(LinkShell) })
        {
            Assert.Null(EvidenceLinks.Chain(reading, Of(LinkOther), end, everyLink: true));
            Assert.Null(EvidenceLinks.Chain(reading, end, Of(LinkOther), everyLink: true));
        }

        // A process created two seconds later is not near anyone; one whose creation was not witnessed is near no one.
        Assert.Empty(EvidenceLinks.Of(reading, Of(LinkLate)).Near);
        Assert.DoesNotContain(app.Near, near => near.Process == Of(LinkLate));
        StepLinks shell = EvidenceLinks.Of(reading, Of(LinkShell));
        Assert.Equal((false, 0), (shell.CreationWitnessed, shell.Near.Count));
        Assert.Equal([Of(LinkApp), Of(LinkSibling)], shell.ItLedTo.Select(link => link.To.Process!.Value));

        // A start named by a reused PID's later holder is a candidate: listed, not followed, and chained only when asked to
        // follow every link; the policy that follows it does.
        StepLinks child = EvidenceLinks.Of(reading, Of(LinkReusedChild));
        Assert.Empty(child.LedToIt);

        // A process linked to it, followed or not, is never also near it: new.exe started child.exe a reading before.
        Assert.Equal([Of(LinkReused), Of(LinkSibling), Of(LinkOther), Of(LinkApp)], child.Near.Select(near => near.Process));
        Assert.Equal([(LinkKind.Started, RelationStrength.Candidate)], child.NotFollowed.Select(link => (link.Kind, link.Strength)));
        Assert.Null(EvidenceLinks.Chain(reading, Of(LinkReused, 2), Of(LinkReusedChild)));
        Assert.Single(EvidenceLinks.Chain(reading, Of(LinkReused, 2), Of(LinkReusedChild), everyLink: true)!);
        Assert.Equal(EvidencePolicy.IncludeCandidates, EvidenceLinks.Following(RelationStrength.Candidate));
        LinkReading candidates = EvidenceLinks.Read(session.Store, EvidencePolicy.IncludeCandidates);
        Assert.Single(EvidenceLinks.Of(candidates, Of(LinkReusedChild)).LedToIt);

        // Admitting direct links alone, only the start key's link is followed; the rest are listed as not followed.
        LinkReading direct = EvidenceLinks.Read(session.Store, EvidencePolicy.DirectOnly);
        StepLinks strict = EvidenceLinks.Of(direct, Of(LinkApp));
        Assert.Equal((1, 0, 4), (strict.LedToIt.Count, strict.ItLedTo.Count, strict.NotFollowed.Count));
        Assert.Null(EvidenceLinks.Chain(direct, Of(LinkShell), Of(LinkServices)));
        Assert.Equal(RelationStrength.Correlated, EvidenceLinks.Weakest(EvidenceLinks.Chain(direct, Of(LinkShell), Of(LinkServices), everyLink: true)!));
        Assert.Equal(EvidencePolicy.IncludeCorrelated, EvidenceLinks.Following(RelationStrength.Correlated));
        Assert.Equal([RelationStrength.Direct], EvidenceLinks.Followed(EvidencePolicy.DirectOnly));
        Assert.Equal([RelationStrength.Direct, RelationStrength.Correlated], EvidenceLinks.Followed(EvidencePolicy.IncludeCorrelated));
        Assert.Equal(RelationStrength.Direct, EvidenceLinks.Weakest([]));
    }

    [Fact(DisplayName = "P8: a process created within a second of another and linked to it by nothing is near it, the nearest first, and none a second and a tick away")]
    public void NearnessIsASecondEitherWay()
    {
        ProcessInstanceId step = ProcessInstanceId.New(), before = ProcessInstanceId.New(), after = ProcessInstanceId.New(),
            past = ProcessInstanceId.New(), linked = ProcessInstanceId.New(), same = ProcessInstanceId.New(),
            seldom = ProcessInstanceId.New(), often = ProcessInstanceId.New();
        var reading = new LinkReading(Guid.NewGuid(), 1, EvidencePolicy.IncludeCorrelated, [],
            [
                new EvidenceLink(LinkKind.Started, LinkEnd.Of(step), LinkEnd.Of(linked), RelationStrength.Candidate),
                new EvidenceLink(LinkKind.Called, LinkEnd.Of(step), LinkEnd.Of(seldom), RelationStrength.Correlated) { Occurrences = 1 },
                new EvidenceLink(LinkKind.Called, LinkEnd.Of(step), LinkEnd.Of(often), RelationStrength.Correlated) { Occurrences = 3 },
            ])
        {
            Processes = new Dictionary<ProcessInstanceId, ProcessNode>(),
            Created = new Dictionary<ProcessInstanceId, long>
            {
                [step] = 50_000_000,
                [before] = 50_000_000 - EvidenceLinks.NearTicks,
                [after] = 50_000_000 + 3,
                [past] = 50_000_000 + EvidenceLinks.NearTicks + 1,
                [linked] = 50_000_000 + 1,
                [same] = 50_000_000,
            },
            Unlinked = new Dictionary<ProcessInstanceId, UnlinkedActivity>(),
        };

        StepLinks links = EvidenceLinks.Of(reading, step);
        Assert.Equal([(same, 0L), (after, 3L), (before, -EvidenceLinks.NearTicks)], links.Near.Select(near => (near.Process, near.OffsetTicks)));
        Assert.Equal([LinkKind.Started], links.NotFollowed.Select(link => link.Kind));

        // Links of one kind, the most held first, whatever the other ends are.
        Assert.Equal([often, seldom], links.ItLedTo.Select(link => link.To.Process!.Value));
        Assert.Equal(10_000_000L, EvidenceLinks.NearTicks);
    }

    [Fact(DisplayName = "R21: what of a process links it to nothing is counted by why, never read as none")]
    public void WhatLinksToNothingIsCounted()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkRows();
        Publish(session.Store, rows, fields: fields, coverage: TransportLedger(tcp: true, udp: true));
        LinkReading reading = EvidenceLinks.Read(session.Store);
        UnlinkedActivity Unlinked(int pid) =>
            EvidenceLinks.Of(reading, reading.Processes.Values.Single(node => node.ProcessId == pid && node.PidHolder == 1).Id).Unlinked;

        // app.exe's connection at 9000 was seen open at neither end, its UDP flow has no opening, and one of its calls had no
        // ALPC send to follow; server.exe holds the other end of that connection.
        UnlinkedActivity app = Unlinked(LinkApp);
        Assert.Equal((2L, 2L, 0L, (int?)null), (app.UnopenedConnections, app.Flows, app.CallsNotReached, app.UnheldParent));
        Assert.Equal([(RpcPeerState.NoSend, 1L)], app.UnresolvedCalls.Select(entry => (entry.Key, entry.Value)));
        Assert.Equal((1L, 1L), (Unlinked(LinkServer).UnopenedConnections, Unlinked(LinkServer).Flows));

        // other.exe names a parent no process of the capture is; services.exe served two calls no linked client reached.
        Assert.Equal(12, Unlinked(LinkOther).UnheldParent);
        Assert.Equal(2L, Unlinked(LinkServices).CallsNotReached);
        Assert.True(Unlinked(LinkShell).IsEmpty);
        Assert.Same(UnlinkedActivity.None, Unlinked(LinkShell));
        Assert.False(app.IsEmpty);
        Assert.Equal([Mechanism.ProcessLifecycle, Mechanism.Tcp, Mechanism.Rpc, Mechanism.Alpc],
            reading.Coverage.Select(coverage => coverage.Mechanism));
    }

    [Fact(DisplayName = "R18: a link, its evidence, what is near and what links to nothing are said in one set of words, the command line's and the window's")]
    public void LinksAreSaidInOneSetOfWords()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkRows();
        Publish(session.Store, rows, fields: fields, coverage: TransportLedger(tcp: true, udp: true));
        LinkReading reading = EvidenceLinks.Read(session.Store, EvidencePolicy.IncludeCandidates);
        ProcessInstanceId Of(int pid, int holder = 1) =>
            reading.Processes.Values.Single(node => node.ProcessId == pid && node.PidHolder == holder).Id;
        StepLinks app = EvidenceLinks.Of(reading, Of(LinkApp));
        StepLinks server = EvidenceLinks.Of(reading, Of(LinkServer));
        StepLinks shell = EvidenceLinks.Of(reading, Of(LinkShell));

        // What the other end is to the process, and the evidence, from its side.
        Assert.Equal(
            [
                ("its parent", "shell.exe · PID 300", "the child's creation record names the parent by its start key", "direct"),
            ],
            app.LedToIt.Select(link => Said(link, Of(LinkApp))));
        Assert.Equal(
            [
                ("served its calls", "services.exe · PID 1960", "2 calls to svcctl (Service Control Manager), each reaching the call "
                    + "that served it through its one ALPC message", "correlated"),
                ("it connected to", "server.exe · PID 4000", "1 connection to port 8080 that it made, as the capture saw it open",
                    "correlated"),
                ("it connected to", "93.184.216.34", "1 connection to port 443 at that address that it made; who is at an address "
                    + "is not observed", "correlated"),
                ("it connected to", "an end on this machine no record holds", "1 connection to port 9100 on this machine that it "
                    + "made; no record holds the other end", "correlated"),
            ],
            app.ItLedTo.Select(link => Said(link, Of(LinkApp))));
        Assert.Equal(
            [
                ("connected to it", "app.exe · PID 400", "1 connection to its port 8080 that the other end made, as the capture saw "
                    + "it open", "correlated"),
                ("connected to it", "10.0.0.5", "1 connection to its port 3389 that it accepted from that address; who is at an "
                    + "address is not observed", "correlated"),
            ],
            server.LedToIt.Select(link => Said(link, Of(LinkServer))));
        Assert.Equal(
            [
                ("its child", "app.exe · PID 400", "the child's creation record names the parent by its start key", "direct"),
                ("its child", "sibling.exe · PID 430", "the child's creation record names the parent's PID alone, which the parent "
                    + "held when the child was created", "correlated"),
            ],
            shell.ItLedTo.Select(link => Said(link, Of(LinkShell))));
        Assert.Equal(
            [
                ("its parent", "new.exe · PID 77 #2", "the child's creation record names the parent's PID alone, which the parent "
                    + "held when the child was created, after another process had held it", "candidate"),
            ],
            EvidenceLinks.Of(reading, Of(LinkReusedChild)).LedToIt.Select(link => Said(link, Of(LinkReusedChild))));
        Assert.Equal(("called it", "app.exe · PID 400"),
            EvidenceLinks.Of(reading, Of(LinkServices)).LedToIt.Select(link => (LinkText.Role(link, Of(LinkServices)),
                LinkText.Named(reading, LinkText.Other(link, Of(LinkServices))))).Single());

        // A connection to its port from an end no record holds, and several of one kind.
        var unseen = new EvidenceLink(LinkKind.Connected, LinkEnd.UnseenEnd, LinkEnd.Of(Of(LinkServer)), RelationStrength.Correlated)
        {
            Occurrences = 3,
            Port = 7000,
        };
        Assert.Equal("3 connections to its port 7000 that it accepted from an end on this machine no record holds",
            LinkText.Evidence(unseen, Of(LinkServer), Invariant));
        Assert.Equal("3 connections to port 8080 that it made, as the capture saw them open",
            LinkText.Evidence(app.ItLedTo[1] with { Occurrences = 3 }, Of(LinkApp), Invariant));
        Assert.Equal("1 call to svcctl (Service Control Manager), reaching the call that served it through its one ALPC message",
            LinkText.Evidence(app.ItLedTo[0] with { Occurrences = 1 }, Of(LinkApp), Invariant));

        // A chain's links as sentences, from the process each leaves.
        Assert.Equal(["shell.exe · PID 300 started app.exe · PID 400", "app.exe · PID 400's calls were served by services.exe · PID 1960",
            "app.exe · PID 400 connected to server.exe · PID 4000"],
            [LinkText.Step(reading, app.LedToIt[0]), LinkText.Step(reading, app.ItLedTo[0]), LinkText.Step(reading, app.ItLedTo[1])]);

        // Near in time, before or after, or at the same reading; an end the overview did not name.
        Assert.Equal(["created 0.2 µs after it", "created 0.2 µs before it", "created at the same reading as it"],
            new[] { 2L, -2L, 0L }.Select(offset => LinkText.Near(new NearProcess(Of(LinkOther), offset), Invariant)));
        Assert.Equal("a process the overview did not name", LinkText.Named(reading, LinkEnd.Of(ProcessInstanceId.New())));

        // What links to nothing, in one sentence: a single reason said alone, several each counted.
        Assert.Equal("Its creation record names parent PID 12, and no process of the capture is linked as that parent; "
            + "1 call whose other end is unresolved: no ALPC send on its thread during the call.",
            LinkText.Unlinked(EvidenceLinks.Of(reading, Of(LinkOther)).Unlinked));
        Assert.Equal("2 TCP connections whose opening no record shows, so neither end is known to have made them; 2 UDP "
            + "flows, which nothing opens; 3 calls whose other end is unresolved: 2 several ALPC sends during the call and 1 no ALPC "
            + "send on its thread during the call.",
            LinkText.Unlinked(app.Unlinked with
            {
                UnresolvedCalls = new Dictionary<RpcPeerState, long> { [RpcPeerState.NoSend] = 1, [RpcPeerState.SeveralSends] = 2 },
            }));
        Assert.Equal("2 calls it served that no linked client call reached: a client may be remote, not use ALPC, or "
            + "not be in the capture.", LinkText.Unlinked(EvidenceLinks.Of(reading, Of(LinkServices)).Unlinked));
        Assert.Null(LinkText.Unlinked(UnlinkedActivity.None));
        Assert.Equal("1 TCP connection whose opening no record shows, so neither end is known to have made them; 1 UDP flow, "
            + "which nothing opens.", LinkText.Unlinked(new UnlinkedActivity { UnopenedConnections = 1, Flows = 1 }));

        // Coverage beside a field already labelled so, without its own label.
        Assert.Equal("covered for TCP · no other mechanism collected", CoverageText.Unlabelled(
            [new MechanismCoverage(Mechanism.Tcp, CoverageState.Covered, "fine"), new MechanismCoverage(Mechanism.Rpc, CoverageState.NotCollected, "none")]));
        Assert.Equal("unknown: no ledger, so a count of none here is not proof of inactivity",
            CoverageText.Unlabelled([new MechanismCoverage(Mechanism.Tcp, CoverageState.UnknownCoverage, "no ledger")]));

        (string, string, string, string) Said(EvidenceLink link, ProcessInstanceId step) => (LinkText.Role(link, step),
            LinkText.Named(reading, LinkText.Other(link, step)), LinkText.Evidence(link, step, Invariant), LinkText.Strength(link.Strength));
    }
}
