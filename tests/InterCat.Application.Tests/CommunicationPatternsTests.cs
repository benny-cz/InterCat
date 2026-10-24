using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// Recurring communication patterns (`contracts/patterns-v1.md`, plan M12): each TCP connection the capture saw open, each
/// UDP flow and each RPC call grouped by its mechanism, its service port or interface and procedure, and where its server
/// end is, each end in its role, and what no pattern holds counted apart.
/// </summary>
public sealed class CommunicationPatternsTests
{
    private const int Browser = PatternBrowser, OtherBrowser = PatternOtherBrowser, Server = PatternServer, Resolver = PatternResolver;

    /// <summary>When the browsers' three connections to the server began.</summary>
    private static readonly long[] LocalTimes = [1_000, 1_100, 8_000];

    [Fact(DisplayName = "P6: a pattern groups occurrences by mechanism, service port or interface and procedure, and where the server is, never by a process or address")]
    public void APatternGroupsOccurrencesBySignature()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = PatternRows();
        Publish(session.Store, rows, fields: fields, coverage: TransportLedger(tcp: true, udp: true));

        PatternReading reading = CommunicationPatterns.Read(session.Store);
        ProcessInstanceId Of(int pid) => reading.Processes.Values.Single(node => node.ProcessId == pid).Id;
        Assert.Equal(
            [
                ("rpc:367abb81-9844-35f1-ad32-98f038001003:7", 3L, 3L),
                ("tcp:here:8080", 3L, 0L),
                ("tcp:elsewhere:443", 2L, 0L),
                ("udp:elsewhere:53", 2L, 0L),
                ("udp:here:5353", 2L, 0L),
                ("tcp:here:3389", 1L, 0L),
                ("tcp:here:9000", 1L, 0L),
                ("udp:here:161", 1L, 0L),
            ],
            reading.Patterns.Select(pattern => (pattern.Key, pattern.Occurrences, pattern.Served)));

        // Connections between two of this machine's processes: the end that accepted them serves, whichever connected.
        CommunicationPattern local = reading.Patterns[1];
        Assert.Equal((Mechanism.Tcp, PatternPlace.Here, (ushort?)8080, true), (local.Mechanism, local.Place, local.Port, local.Recurring));
        Assert.Equal([(Of(Browser), 2L), (Of(OtherBrowser), 1L)], local.Clients.Select(end => (end.Process!.Value, end.Occurrences)));
        Assert.Equal([(PatternRole.Server, Of(Server), 3L)], local.Servers.Select(end => (end.Role, end.Process!.Value, end.Occurrences)));
        Assert.Equal((1_000L, 8_000L), (local.FirstTicks, local.LastTicks));
        Assert.Equal(LocalTimes.Select(at => CommunicationPatterns.PartOf(reading.Span!.Value, at)).Distinct().Count(), local.Tenths);
        Assert.Equal(3, local.Tenths);

        // To other hosts, each host by its address, whichever it is; one host connecting in is a client here.
        CommunicationPattern outbound = reading.Patterns[2];
        Assert.Equal((PatternPlace.Elsewhere, (ushort?)443), (outbound.Place, outbound.Port));
        Assert.Equal(["142.250.1.1", "142.250.1.2"], outbound.Servers.Select(end => end.Address!));
        Assert.All(outbound.Servers, end => Assert.Null(end.Process));
        Assert.Equal(["10.0.0.5"], reading.Patterns[5].Clients.Select(end => end.Address!));
        Assert.Equal([Of(Server)], reading.Patterns[5].Servers.Select(end => end.Process!.Value));
        Assert.False(reading.Patterns[5].Recurring);

        // A connection to a port on this machine no record holds is served here, by an end no record holds.
        CommunicationPattern unseen = reading.Patterns[6];
        Assert.Equal((PatternPlace.Here, (ushort?)9000), (unseen.Place, unseen.Port));
        Assert.True(Assert.Single(unseen.Servers).Unseen);
        Assert.Equal([Of(PatternAgent)], unseen.Clients.Select(end => end.Process!.Value));

        // UDP by its service port alone, the end at it serving; RPC by interface and procedure, made and served.
        CommunicationPattern resolved = reading.Patterns[3];
        Assert.Equal((Mechanism.Udp, PatternPlace.Elsewhere, (ushort?)53), (resolved.Mechanism, resolved.Place, resolved.Port));
        Assert.Equal([(Of(Resolver), 2L)], resolved.Clients.Select(end => (end.Process!.Value, end.Occurrences)));
        Assert.Equal([("8.8.8.8", 2L)], resolved.Servers.Select(end => (end.Address!, end.Occurrences)));
        CommunicationPattern served = reading.Patterns[4];
        Assert.Equal((Mechanism.Udp, PatternPlace.Here, (ushort?)5353), (served.Mechanism, served.Place, served.Port));
        Assert.Equal([(Of(Resolver), 2L)], served.Clients.Select(end => (end.Process!.Value, end.Occurrences)));
        Assert.Equal([(Of(Server), 2L)], served.Servers.Select(end => (end.Process!.Value, end.Occurrences)));
        CommunicationPattern snmp = reading.Patterns[7];
        Assert.Equal((Mechanism.Udp, PatternPlace.Here, (ushort?)161), (snmp.Mechanism, snmp.Place, snmp.Port));
        Assert.Equal(["10.0.0.9"], snmp.Clients.Select(end => end.Address!));
        Assert.Equal([Of(Server)], snmp.Servers.Select(end => end.Process!.Value));
        CommunicationPattern called = reading.Patterns[0];
        Assert.Equal((Mechanism.Rpc, ServiceControlInterface, 7L, (ushort?)null), (called.Mechanism, called.Interface!.Value,
            called.Procedure!.Value, called.Port));
        Assert.Equal([400], called.Clients.Select(end => reading.Processes[end.Process!.Value].ProcessId));
        Assert.Equal([1_960], called.Servers.Select(end => reading.Processes[end.Process!.Value].ProcessId));
        Assert.Equal((3L, 3), (called.Weight, called.Clients.Concat(called.Servers).Sum(end => (int)end.Occurrences) / 2));

        // What no pattern holds is counted: connections seen open at neither end, between two processes or to another host,
        // and flows between two dynamic ports.
        Assert.Equal((2L, 2L, 0L, 0L), (reading.UnopenedConnections, reading.DynamicFlows, reading.CallsWithoutInterface,
            reading.NotAdmitted));
        Assert.Equal([Mechanism.Tcp, Mechanism.Udp, Mechanism.Rpc], reading.Coverage.Select(coverage => coverage.Mechanism));

        // Admitting only direct relations, the correlated ones are counted as not admitted, never dropped silently.
        PatternReading direct = CommunicationPatterns.Read(session.Store, EvidencePolicy.DirectOnly);
        Assert.Equal(EvidencePolicy.DirectOnly, direct.Policy);
        Assert.Empty(direct.Patterns);
        Assert.Equal((22L, 0L, 0L), (direct.NotAdmitted, direct.UnopenedConnections, direct.DynamicFlows));
        Assert.Equal(reading.Patterns.Sum(pattern => pattern.Occurrences + pattern.Served) + reading.UnopenedConnections
            + reading.DynamicFlows, direct.Patterns.Sum(pattern => pattern.Occurrences + pattern.Served) + direct.UnopenedConnections
            + direct.DynamicFlows + direct.NotAdmitted);
    }

    [Fact(DisplayName = "R21: a pattern's signature and what it leaves out are said in words, and a UDP flow's service port is the one outside the dynamic range")]
    public void APatternIsExplained()
    {
        Assert.Equal(((ushort)443, true), CommunicationPatterns.ServicePort(443, 50_000));
        Assert.Equal(((ushort)53, false), CommunicationPatterns.ServicePort(51_000, 53));
        Assert.Equal(((ushort)137, true), CommunicationPatterns.ServicePort(137, 138));
        Assert.Equal(((ushort)80, false), CommunicationPatterns.ServicePort(8080, 80));
        Assert.Equal(((ushort)137, true), CommunicationPatterns.ServicePort(137, 137));
        Assert.Equal(((ushort)49_151, true), CommunicationPatterns.ServicePort(49_151, 49_152));
        Assert.Null(CommunicationPatterns.ServicePort(50_000, 60_000));
        Assert.Null(CommunicationPatterns.ServicePort(null, 80));
        Assert.Null(CommunicationPatterns.ServicePort(80, null));
        Assert.True(CommunicationPatterns.IsDynamic(49_152));
        Assert.False(CommunicationPatterns.IsDynamic(49_151));

        var span = new TimeRange(100, 200);
        Assert.Equal([0, 0, 0, 1, 9, 9, 9], new long[] { 50, 100, 109, 110, 199, 200, 500 }.Select(at => CommunicationPatterns.PartOf(span, at)));

        CommunicationPattern Pattern(Mechanism mechanism, PatternPlace place, ushort? port, Guid? rpcInterface = null, long? procedure = null) =>
            new("key", mechanism, place, port, rpcInterface, procedure) { Occurrences = 2, Clients = [], Servers = [] };
        Assert.Equal("TCP at port 8080 here", CommunicationPatterns.Describe(Pattern(Mechanism.Tcp, PatternPlace.Here, 8080)));
        Assert.Equal("UDP to port 53 at other hosts", CommunicationPatterns.Describe(Pattern(Mechanism.Udp, PatternPlace.Elsewhere, 53)));
        Assert.Equal("RPC calls to svcctl (Service Control Manager), procedure 7",
            CommunicationPatterns.Describe(Pattern(Mechanism.Rpc, PatternPlace.Here, null, ServiceControlInterface, 7)));
        Assert.Equal("RPC calls to svcctl (Service Control Manager), no procedure recorded",
            CommunicationPatterns.Describe(Pattern(Mechanism.Rpc, PatternPlace.Here, null, ServiceControlInterface)));
        Assert.StartsWith("TCP connections a process of this machine accepted at this port",
            CommunicationPatterns.Explain(Pattern(Mechanism.Tcp, PatternPlace.Here, 8080)), StringComparison.Ordinal);
        Assert.StartsWith("TCP connections this machine's processes opened to this port at other hosts",
            CommunicationPatterns.Explain(Pattern(Mechanism.Tcp, PatternPlace.Elsewhere, 443)), StringComparison.Ordinal);
        Assert.Contains("the one outside the dynamic range 49152 to 65535, the lower where both are",
            CommunicationPatterns.Explain(Pattern(Mechanism.Udp, PatternPlace.Elsewhere, 53)), StringComparison.Ordinal);
        Assert.StartsWith("RPC calls whose starts named this interface and procedure",
            CommunicationPatterns.Explain(Pattern(Mechanism.Rpc, PatternPlace.Here, null, ServiceControlInterface, 7)),
            StringComparison.Ordinal);

        // One that occurred once is no recurrence; RPC recurs by the more of its calls made and served.
        Assert.False((Pattern(Mechanism.Tcp, PatternPlace.Here, 80) with { Occurrences = 1 }).Recurring);
        Assert.True((Pattern(Mechanism.Rpc, PatternPlace.Here, null, ServiceControlInterface, 7) with { Occurrences = 0, Served = 2 }).Recurring);
        Assert.Equal(5, (Pattern(Mechanism.Rpc, PatternPlace.Here, null) with { Occurrences = 3, Served = 5 }).Weight);
    }

    [Fact(DisplayName = "R18: a pattern's occurrences, who took each role and why a session holds none are said in one set of words, the command line's and the window's")]
    public void APatternIsSaidInOneSetOfWords()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = PatternRows();
        Publish(session.Store, rows, fields: fields, coverage: TransportLedger(tcp: true, udp: true));
        PatternReading reading = CommunicationPatterns.Read(session.Store);
        CommunicationPattern called = reading.Patterns[0], local = reading.Patterns[1], resolved = reading.Patterns[3];

        // Each end's occurrences in its role's unit.
        Assert.Equal(["1 call made", "3 calls made", "1 call served", "3 calls served"],
            new[] { (PatternRole.Client, 1L), (PatternRole.Client, 3L), (PatternRole.Server, 1L), (PatternRole.Server, 3L) }
                .Select(each => PatternText.Count(called, each.Item1, each.Item2)));
        Assert.Equal(["1 connection", "2 connections", "1 flow", "2 flows"],
        [
            PatternText.Count(local, PatternRole.Server, 1), PatternText.Count(local, PatternRole.Client, 2),
            PatternText.Count(resolved, PatternRole.Client, 1), PatternText.Count(resolved, PatternRole.Server, 2),
        ]);

        // Who took a role: its ends; none; or, for an RPC pattern's servers, that no call of it was recorded served here.
        Assert.Equal(("2 processes of 1 program", "1 process of 1 program", "1 host"), (PatternText.Took(reading, local, PatternRole.Client),
            PatternText.Took(reading, local, PatternRole.Server), PatternText.Took(reading, resolved, PatternRole.Server)));
        Assert.Equal("none recorded: no call of it was recorded served on this machine",
            PatternText.Took(reading, called with { Servers = [] }, PatternRole.Server));
        Assert.Equal(("none", "none"), (PatternText.Took(reading, called with { Clients = [] }, PatternRole.Client),
            PatternText.Took(reading, local with { Servers = [] }, PatternRole.Server)));

        // A session with none says so, and where the evidence policy left some out, which policy and where they are counted.
        Assert.Equal("This session holds no pattern: no TCP connection the capture saw open, UDP flow with a service port or RPC "
            + "call naming its interface is in it; the coverage above says whether one could have been seen.",
            PatternText.None(reading with { Patterns = [] }));
        PatternReading direct = CommunicationPatterns.Read(session.Store, EvidencePolicy.DirectOnly);
        Assert.Equal("This session holds no pattern the direct-only evidence policy admits; what it did not admit is counted "
            + "beneath, and the coverage above says whether any could have been seen.", PatternText.None(direct));
        Assert.Equal("In no pattern: 22 the direct-only evidence policy did not admit.",
            PatternText.Apart(direct, System.Globalization.CultureInfo.InvariantCulture));
    }
}
