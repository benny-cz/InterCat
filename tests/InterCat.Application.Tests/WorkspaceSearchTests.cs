using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>§6.7's search: metadata only, ranked by how well each entity matches, bounded, and each hit a ladder path.</summary>
public sealed class WorkspaceSearchTests
{
    private static readonly ProcessInstanceId Browser = new(Guid.Parse("5eb7465f-3dd6-4df8-8577-472fa43aab10"));
    private static readonly ProcessInstanceId Api = new(Guid.Parse("76447f1d-1cfc-4815-b775-cbdb8327f624"));
    private static readonly ProcessInstanceId Cache = new(Guid.Parse("25ecf72c-7d72-4421-943e-eb6d66cd2fe8"));

    [Fact(DisplayName = "§6.7: a search finds a process by PID or name first, then what only contains the text")]
    public void ASearchRanksExactMatchesFirst()
    {
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create();

        // An exact PID is the first hit, and its path descends through the process's group to the process.
        SearchHit byPid = WorkspaceSearch.Find(snapshot, "8204").Hits[0];
        Assert.Equal((SearchHitKind.Process, Browser.ToString()), (byPid.Kind, byPid.Key));
        Assert.Equal("Browser · PID 8204", byPid.Label);
        Assert.Equal(["group.app", Browser.ToString()], byPid.Path);

        // A name that is the whole query outranks a channel whose endpoint only contains it, whatever the case.
        SearchResult cache = WorkspaceSearch.Find(snapshot, "CACHE");
        Assert.Equal((SearchHitKind.Process, Cache.ToString()), (cache.Hits[0].Kind, cache.Hits[0].Key));
        SearchHit pipe = Assert.Single(cache.Hits, hit => hit.Kind == SearchHitKind.Channel);
        Assert.Equal("API host · PID 5120 and Cache · PID 4056", pipe.Label);
        Assert.Equal("Named pipe channel whose endpoint the search names", pipe.Detail);
        Assert.DoesNotContain("intercat-cache", pipe.Label + pipe.Detail, StringComparison.Ordinal);
        Assert.Equal(["group.app", Api.ToString(), "chan.cachepipe"], pipe.Path);

        // A group is found by name and descends one rung.
        SearchHit group = Assert.Single(WorkspaceSearch.Find(snapshot, "platform").Hits);
        Assert.Equal((SearchHitKind.Group, "group.platform"), (group.Kind, group.Key));
        Assert.Equal(["group.platform"], group.Path);
        Assert.Equal("Service container · 2 processes", group.Detail);
    }

    [Fact(DisplayName = "§6.7: a search is bounded and says how many matched, and blank text finds nothing")]
    public void ASearchIsBoundedAndHonestAboutTheRest()
    {
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create();
        SearchResult all = WorkspaceSearch.Find(snapshot, "e");
        Assert.True(all.Matched > 2);
        SearchResult two = WorkspaceSearch.Find(snapshot, "e", limit: 2);
        Assert.Equal(2, two.Hits.Count);
        Assert.Equal(all.Matched, two.Matched);
        Assert.Equal(all.Hits.Take(2).Select(hit => hit.Key), two.Hits.Select(hit => hit.Key));

        Assert.Empty(WorkspaceSearch.Find(snapshot, "   ").Hits);
        Assert.Equal(0, WorkspaceSearch.Find(snapshot, null).Matched);
        Assert.Empty(WorkspaceSearch.Find(snapshot, "no such name").Hits);
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkspaceSearch.Find(snapshot, "e", limit: 0));
    }

    [Fact(DisplayName = "§6.2: a process is found by the rule its search hit is ranked by, with no limit on how many")]
    public void AProcessIsFoundByItsHitsRule()
    {
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create();

        // Every query finds exactly the processes it lists hits for, whatever the hits' limit leaves unlisted.
        foreach (string query in new[] { "8204", "82", "CACHE", "browser", "e", "platform", " 5120 ", "no such name", "" })
        {
            string[] hits = [.. WorkspaceSearch.Find(snapshot, query, limit: 1_000).Hits
                .Where(hit => hit.Kind == SearchHitKind.Process).Select(hit => hit.Key).Order(StringComparer.Ordinal)];
            string[] found = [.. snapshot.Processes
                .Where(process => WorkspaceSearch.Finds(process, snapshot.HostLinks.Where(link => link.Process == process.Id), query))
                .Select(process => process.Id.ToString()).Order(StringComparer.Ordinal)];
            Assert.Equal(hits, found);
        }

        // A number finds its PID and the PIDs it begins, and not a PID it is only inside.
        ProcessNode browser = snapshot.Processes.Single(process => process.Id == Browser);
        Assert.True(WorkspaceSearch.Finds(browser, [], "8204"));
        Assert.True(WorkspaceSearch.Finds(browser, [], "82"));
        Assert.False(WorkspaceSearch.Finds(browser, [], "204"));
        Assert.False(WorkspaceSearch.Finds(browser, [], "  "));
        Assert.False(WorkspaceSearch.Finds(browser, [], null));
    }

    [Fact(DisplayName = "§6.7: an executable is found by its image path, below a match on its name")]
    public void AnExecutableIsFoundByItsPath()
    {
        ProcessGroup tools = new("tools", "run.exe", LaneGrouping.Executable, @"C:\Tools\run.exe");
        ProcessGroup other = new("other", "runtime-tools.exe", LaneGrouping.Executable, @"C:\Other\runtime-tools.exe");
        ProcessNode process = new(new ProcessInstanceId(Guid.Parse("00000000-0000-0000-0000-000000000001")), 42, "run.exe",
            "test", tools.Key, 0.5, 0.5, CoverageState.Covered);
        WorkspaceSnapshot snapshot = new("Search", new TimeRange(0, 10), [tools, other], [process], [], [], [], [], []);

        SearchResult result = WorkspaceSearch.Find(snapshot, "tools");
        Assert.Equal(["other", "tools"], result.Hits.Select(hit => hit.Key));
        Assert.EndsWith(@"C:\Tools\run.exe", result.Hits[1].Detail, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§6.7: a name that is the query and an extension matches exactly, and hits rank and read by their own records")]
    public void AnExecutableNamedByTheQueryIsExact()
    {
        // As in a live session: searching "chrome" meant chrome.exe, not the helper whose name merely begins with it.
        ProcessGroup helper = new("helper", "chrome-native-host.exe", LaneGrouping.Executable, @"C:\Users\x\chrome-native-host.exe");
        ProcessGroup chrome = new("chrome", @"chrome.exe (Google\Chrome\Application)", LaneGrouping.Executable,
            @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        ProcessNode quiet = Process(1, 101, "chrome.exe", chrome.Key, (Mechanism.ProcessLifecycle, 1));
        ProcessNode busy = Process(2, 102, "chrome.exe", chrome.Key, (Mechanism.Udp, 900), (Mechanism.Tcp, 40));
        ProcessNode host = Process(3, 103, "chrome-native-host.exe", helper.Key, (Mechanism.Tcp, 5_000));
        WorkspaceSnapshot snapshot = new("Search", new TimeRange(0, 10), [helper, chrome], [quiet, busy, host], [], [], [], [], []);

        SearchResult result = WorkspaceSearch.Find(snapshot, "chrome");
        Assert.Equal(
            [(SearchHitKind.Group, chrome.Key, 941L), (SearchHitKind.Process, busy.Id.ToString(), 940L),
                (SearchHitKind.Process, quiet.Id.ToString(), 1L), (SearchHitKind.Group, helper.Key, 5_000L),
                (SearchHitKind.Process, host.Id.ToString(), 5_000L)],
            result.Hits.Select(hit => (hit.Kind, hit.Key, hit.ObservationCount)));

        // The whole file name, in any case, is exact too; a folder in the group's label is not part of its name.
        Assert.Equal(chrome.Key, WorkspaceSearch.Find(snapshot, "CHROME.EXE").Hits[0].Key);
        Assert.Equal(helper.Key, WorkspaceSearch.Find(snapshot, "chrome-native-host").Hits[0].Key);
        Assert.Equal(chrome.Key, Assert.Single(WorkspaceSearch.Find(snapshot, "Application").Hits).Key);
    }

    [Fact(DisplayName = "§6.7: a process is found by an address its connections to other hosts ran to, whole or up to a dot or colon, its hit saying what reached it and never the address")]
    public void AProcessIsFoundByAnAddressItReached()
    {
        ProcessGroup apps = new("apps", "app.exe", LaneGrouping.Executable);
        ProcessNode browser = Process(1, 300, "browser.exe", apps.Key, (Mechanism.Tcp, 50));
        ProcessNode updater = Process(2, 310, "updater.exe", apps.Key, (Mechanism.Tcp, 20));
        ProcessNode resolver = Process(3, 320, "resolver.exe", apps.Key, (Mechanism.Udp, 10));
        ProcessNode quiet = Process(4, 330, "quiet.exe", apps.Key, (Mechanism.ProcessLifecycle, 1));
        HostLink[] links =
        [
            new(browser.Id, Mechanism.Tcp, [new("142.250.1.1", 2, 30), new("20.42.65.1", 1, 5)], RelationStrength.Direct),
            new(browser.Id, Mechanism.Udp, [new("142.250.1.1", 1, 9)], RelationStrength.Direct),
            new(updater.Id, Mechanism.Tcp, [new("142.250.1.10", 1, 4), new("::ffff:20.42.65.1", 1, 2)], RelationStrength.Direct),
            new(resolver.Id, Mechanism.Udp, [new("2001:db8::1", 3, 3)], RelationStrength.Direct),
        ];
        WorkspaceSnapshot snapshot = new("Search", new TimeRange(0, 10), [apps], [browser, updater, resolver, quiet], [], [], [],
            [], [])
        {
            HostLinks = links,
        };

        // An address whole finds the processes that reached it, its hit saying what reached it and never the address (P16),
        // and descending to the process, whose rung lists each connection; it does not find 142.250.1.10, which it only
        // begins.
        SearchHit hit = Assert.Single(WorkspaceSearch.Find(snapshot, "142.250.1.1").Hits);
        Assert.Equal((SearchHitKind.Process, browser.Id.ToString()), (hit.Kind, hit.Key));
        Assert.Equal("Process instance of app.exe · 2 TCP connections and 1 UDP flow to 1 matching address", hit.Detail);
        Assert.Equal([apps.Key, browser.Id.ToString()], hit.Path);
        Assert.DoesNotContain("142.250", hit.Label + hit.Detail, StringComparison.Ordinal);

        // Up to a dot it finds every address it begins, the busier process first, below a whole address; an IPv4-mapped
        // address is found by its IPv4 address too, and IPv6 in either case.
        SearchResult subnet = WorkspaceSearch.Find(snapshot, "142.250");
        Assert.Equal([browser.Id.ToString(), updater.Id.ToString()], subnet.Hits.Select(found => found.Key));
        Assert.Equal("Process instance of app.exe · 1 TCP connection to 1 matching address", subnet.Hits[1].Detail);
        Assert.Equal([updater.Id.ToString(), browser.Id.ToString()],
            WorkspaceSearch.Find(snapshot, "142.250.1.10").Hits.Concat(WorkspaceSearch.Find(snapshot, "142.250.1.1").Hits)
                .Select(found => found.Key));
        Assert.Equal([browser.Id.ToString(), updater.Id.ToString()],
            WorkspaceSearch.Find(snapshot, "20.42.65.1").Hits.Select(found => found.Key));
        Assert.Equal(resolver.Id.ToString(), Assert.Single(WorkspaceSearch.Find(snapshot, "2001:DB8::1").Hits).Key);
        Assert.Equal("Process instance of app.exe · 3 UDP flows to 1 matching address",
            Assert.Single(WorkspaceSearch.Find(snapshot, "2001:db8:").Hits).Detail);

        // A process that reached two addresses a query begins says so; where one address is the query, it alone is said,
        // though the query begins another: 2001:db8:: is an address whole and begins 2001:db8::1.
        Assert.Equal("Process instance of app.exe · 3 TCP connections to 2 matching addresses",
            WorkspaceSearch.Find(new("Search", new TimeRange(0, 10), [apps], [browser], [], [], [], [], [])
            {
                HostLinks = [new(browser.Id, Mechanism.Tcp, [new("10.0.0.1", 1, 1), new("10.0.0.2", 2, 2)], RelationStrength.Direct)],
            }, "10.0.0").Hits[0].Detail);
        Assert.Equal("Process instance of app.exe · 1 TCP connection to 1 matching address",
            WorkspaceSearch.Find(new("Search", new TimeRange(0, 10), [apps], [browser], [], [], [], [], [])
            {
                HostLinks = [new(browser.Id, Mechanism.Tcp, [new("2001:db8::", 1, 1), new("2001:db8::1", 2, 2)], RelationStrength.Direct)],
            }, "2001:db8::").Hits[0].Detail);

        // A query with neither dot nor colon names no address, and one only inside an address finds nothing.
        Assert.Empty(WorkspaceSearch.Find(snapshot, "142").Hits);
        Assert.Empty(WorkspaceSearch.Find(snapshot, ".1.1").Hits);
        Assert.Empty(WorkspaceSearch.Find(snapshot, "2001:d").Hits);

        // A process is found by the rule its hit is ranked by, its connections to other hosts given.
        foreach (string query in new[] { "142.250.1.1", "142.250", "142.250.", "20.42.65.1", "2001:DB8::1", "2001:db8:", "142", ".1.1", "e" })
        {
            Assert.Equal(
                [.. WorkspaceSearch.Find(snapshot, query, limit: 1_000).Hits.Where(found => found.Kind == SearchHitKind.Process)
                    .Select(found => found.Key).Order(StringComparer.Ordinal)],
                snapshot.Processes.Where(process => WorkspaceSearch.Finds(process, links.Where(link => link.Process == process.Id), query))
                    .Select(process => process.Id.ToString()).Order(StringComparer.Ordinal));
        }

        Assert.Throws<ArgumentNullException>(() => WorkspaceSearch.Finds(quiet, null!, "142.250"));

        // Found both by name and by an address, a process ranks by the better, and says what reached it when that is the
        // address: the relay that reached 10.0.0.1 ranks above the busier tool whose name only begins with it.
        ProcessNode relay = Process(5, 340, "relay-10.0.0.1.exe", apps.Key, (Mechanism.Tcp, 1));
        ProcessNode tool = Process(6, 350, "10.0.0.1-tool.exe", apps.Key, (Mechanism.Tcp, 1_000));
        SearchResult both = WorkspaceSearch.Find(new("Search", new TimeRange(0, 10), [apps], [relay, tool], [], [], [], [], [])
        {
            HostLinks = [new(relay.Id, Mechanism.Tcp, [new("10.0.0.1", 1, 1)], RelationStrength.Direct)],
        }, "10.0.0.1");
        Assert.Equal([relay.Id.ToString(), tool.Id.ToString()], both.Hits.Select(found => found.Key));
        Assert.Equal(["Process instance of app.exe · 1 TCP connection to 1 matching address", "Process instance of app.exe · no relationship"],
            both.Hits.Select(found => found.Detail));
    }

    [Fact(DisplayName = "§6.7: a process a name finds says what tells it apart from its program's other instances - its relationships and the connections it made and accepted - and its program only where its name does not say it")]
    public void AProcessANameFindsSaysWhatTellsItApart()
    {
        ProcessGroup host = new(@"executable:C:\WINDOWS\SYSTEM32\SVCHOST.EXE", "svchost.exe", LaneGrouping.Executable);
        ProcessGroup unknown = new(ProcessGroup.UnwitnessedExecutableKey, "Executable not witnessed", LaneGrouping.Executable);
        ProcessNode busy = Process(1, 1008, "svchost.exe", host.Key, (Mechanism.Tcp, 40)) with { ConnectionsMade = 3, ConnectionsAccepted = 1 };
        ProcessNode one = Process(2, 1004, "svchost.exe", host.Key, (Mechanism.Tcp, 20)) with { ConnectionsMade = 1 };
        ProcessNode quiet = Process(3, 1016, "svchost.exe", host.Key, (Mechanism.Tcp, 10));
        ProcessNode server = Process(5, 1020, "svchost.exe", host.Key, (Mechanism.Tcp, 5)) with { ConnectionsAccepted = 11 };
        ProcessNode unnamed = Process(4, 812, "PID 812", unknown.Key, (Mechanism.Tcp, 5));
        CommunicationEdge Edge(ProcessNode source, ProcessNode target) => new($"tcp:{source.ProcessId}:{target.ProcessId}", source.Id,
            target.Id, Mechanism.Tcp, 2, null, RelationStrength.Correlated) { Rule = RelationRule.TransportEndpoint, Evidence = ["e"] };
        WorkspaceSnapshot snapshot = new("Search", new TimeRange(0, 10), [host, unknown], [busy, one, quiet, unnamed, server],
            [Edge(busy, one), Edge(unnamed, busy), Edge(server, server)], [], [], [], [])
        {
            ConnectionOpensRecorded = true,
        };

        // The busiest first, each by its relationships and the connections it made and accepted, each count where there are
        // some; one that made and accepted none says only its relationships, and one connected to itself counts that once.
        Assert.Equal(
            ["2 relationships · 3 connections made, 1 accepted", "1 relationship · 1 connection made", "No relationship",
                "1 relationship · 11 connections accepted"],
            WorkspaceSearch.Find(snapshot, "svchost").Hits.Where(hit => hit.Kind == SearchHitKind.Process).Select(hit => hit.Detail));

        // Its program said where its name does not say it.
        Assert.Equal("Process instance of Executable not witnessed · 1 relationship",
            Assert.Single(WorkspaceSearch.Find(snapshot, "812").Hits).Detail);

        // A session that records no connection's open says nothing of connections made or accepted.
        Assert.Equal("2 relationships", WorkspaceSearch.Find(snapshot with { ConnectionOpensRecorded = false }, "1008").Hits[0].Detail);
    }

    [Fact(DisplayName = "§6.7: a process is found by an endpoint it reached, an address whole at one of its ports, or by a port alone at any address, its hit never repeating either")]
    public void AProcessIsFoundByAnEndpointOrAPortItReached()
    {
        ProcessGroup apps = new("apps", "app.exe", LaneGrouping.Executable);
        ProcessNode browser = Process(1, 300, "browser.exe", apps.Key, (Mechanism.Tcp, 50));
        ProcessNode updater = Process(2, 310, "updater.exe", apps.Key, (Mechanism.Tcp, 20));
        ProcessNode resolver = Process(3, 320, "resolver.exe", apps.Key, (Mechanism.Udp, 10));
        ProcessNode mapped = Process(4, 330, "mapped.exe", apps.Key, (Mechanism.Tcp, 5));
        HostLink[] links =
        [
            new(browser.Id, Mechanism.Tcp, [new("142.250.1.1", 2, 30) { Ports = [443] }, new("20.42.65.1", 1, 5) { Ports = [80] }],
                RelationStrength.Direct),
            new(updater.Id, Mechanism.Tcp, [new("142.250.1.1", 1, 4) { Ports = [8443] }, new("2001:db8::1", 1, 2) { Ports = [443] }],
                RelationStrength.Direct),
            new(resolver.Id, Mechanism.Udp, [new("8.8.8.8", 3, 3) { Ports = [53] }], RelationStrength.Direct),
            new(mapped.Id, Mechanism.Tcp, [new("::ffff:142.250.1.1", 1, 1) { Ports = [443] }], RelationStrength.Direct),
        ];
        WorkspaceSnapshot snapshot = new("Search", new TimeRange(0, 10), [apps], [browser, updater, resolver, mapped], [], [], [],
            [], [])
        {
            HostLinks = links,
        };
        string[] Found(string query) => [.. WorkspaceSearch.Find(snapshot, query).Hits.Select(hit => hit.Key)];

        // An address with a port, as netstat writes an endpoint, finds the processes that reached it there and no other
        // port - an IPv4-mapped address by its IPv4 address too - each hit saying over which transport, never the endpoint.
        Assert.Equal([browser.Id.ToString(), mapped.Id.ToString()], Found("142.250.1.1:443"));
        SearchHit endpoint = WorkspaceSearch.Find(snapshot, "142.250.1.1:443").Hits[0];
        Assert.Equal("Process instance of app.exe · TCP connections to the endpoint searched", endpoint.Detail);
        Assert.DoesNotContain("443", endpoint.Label + endpoint.Detail, StringComparison.Ordinal);
        Assert.Equal([updater.Id.ToString()], Found("142.250.1.1:8443"));
        Assert.Empty(Found("142.250.1.1:80"));

        // An IPv6 address is written in brackets before its port, in either case.
        Assert.Equal([updater.Id.ToString()], Found("[2001:DB8::1]:443"));

        // A port alone, after a colon, finds every process that reached any address at it, below an endpoint's rank.
        Assert.Equal([browser.Id.ToString(), updater.Id.ToString(), mapped.Id.ToString()], Found(":443"));
        Assert.Equal("Process instance of app.exe · UDP flows to 1 address at the port searched",
            Assert.Single(WorkspaceSearch.Find(snapshot, ":53").Hits).Detail);
        Assert.Equal([browser.Id.ToString()], Found(":80"));

        // An endpoint names its address whole; what is no port names an address as before, or nothing.
        Assert.Empty(Found("142.250:443"));
        Assert.Empty(Found(":65536"));
        Assert.Empty(Found(":443x"));
        Assert.Equal([updater.Id.ToString()], Found("2001:db8::1"));

        // The rule a process is found by is the one its hit is ranked by, and the one icat hosts names a host by.
        foreach (string query in new[] { "142.250.1.1:443", "142.250.1.1:8443", "[2001:db8::1]:443", ":443", ":53", "142.250:443", ":443x" })
        {
            Assert.Equal(
                [.. WorkspaceSearch.Find(snapshot, query, limit: 1_000).Hits.Select(found => found.Key).Order(StringComparer.Ordinal)],
                snapshot.Processes.Where(process => WorkspaceSearch.Finds(process, links.Where(link => link.Process == process.Id), query))
                    .Select(process => process.Id.ToString()).Order(StringComparer.Ordinal));
        }

        Assert.True(WorkspaceSearch.NamesHost(new HostAddress("142.250.1.1", 1, 1) { Ports = [80, 443] }, " 142.250.1.1:443 "));
        Assert.False(WorkspaceSearch.NamesHost(new HostAddress("142.250.1.1", 1, 1) { Ports = [80] }, "142.250.1.1:443"));
        Assert.True(WorkspaceSearch.NamesHost(new HostAddress("142.250.1.1", 1, 1), "142.250"));
        Assert.False(WorkspaceSearch.NamesHost(new HostAddress("142.250.1.1", 1, 1), "142"));
        Assert.True(WorkspaceSearch.NamesPort(":443"));
        Assert.True(WorkspaceSearch.NamesPort("[::1]:443"));
        Assert.False(WorkspaceSearch.NamesPort("::1"));
        Assert.False(WorkspaceSearch.NamesPort("142.250.1.1"));
    }

    [Fact(DisplayName = "§6.7: a port alone also finds the processes that accepted connections at that port of their own, the hit saying from how many addresses, and an endpoint never does")]
    public void APortAloneFindsTheProcessesThatAcceptedAtIt()
    {
        ProcessGroup apps = new("apps", "app.exe", LaneGrouping.Executable);
        ProcessNode service = Process(1, 300, "service.exe", apps.Key, (Mechanism.Tcp, 50));
        ProcessNode browser = Process(2, 310, "browser.exe", apps.Key, (Mechanism.Tcp, 20));
        ProcessNode both = Process(3, 320, "both.exe", apps.Key, (Mechanism.Tcp, 10));
        HostLink[] links =
        [
            // service.exe accepted connections from two hosts at its own port 3389, each from a port the host chose.
            new(service.Id, Mechanism.Tcp,
                [new("10.0.0.5", 1, 2) { Accepted = 1, AcceptedPorts = [3389] }, new("10.0.0.6", 1, 2) { Accepted = 1, AcceptedPorts = [3389] }],
                RelationStrength.Direct),
            // browser.exe reached another host's 3389.
            new(browser.Id, Mechanism.Tcp, [new("10.0.0.7", 1, 2) { Ports = [3389], Connected = 1 }], RelationStrength.Direct),
            // both.exe reached one host's 3389 and accepted another's connection at its own.
            new(both.Id, Mechanism.Tcp,
                [new("10.0.0.8", 1, 2) { Ports = [3389], Connected = 1 }, new("10.0.0.9", 1, 2) { Accepted = 1, AcceptedPorts = [3389] }],
                RelationStrength.Direct),
        ];
        WorkspaceSnapshot snapshot = new("Search", new TimeRange(0, 10), [apps], [service, browser, both], [], [], [], [], [])
        {
            HostLinks = links,
        };

        // Each is found, its hit saying whether its connections ran to the port, arrived at its own or both.
        SearchResult result = WorkspaceSearch.Find(snapshot, ":3389");
        Assert.Equal(
            [
                (service.Id.ToString(), "Process instance of app.exe · TCP connections accepted at the port searched, from 2 addresses"),
                (browser.Id.ToString(), "Process instance of app.exe · TCP connections to 1 address at the port searched"),
                (both.Id.ToString(), "Process instance of app.exe · TCP connections at the port searched, to or from 2 addresses"),
            ],
            result.Hits.Select(hit => (hit.Key, hit.Detail)));
        Assert.True(WorkspaceSearch.FindsAddress(links, ":3389"));

        // An endpoint names the port at the host, which an accepted connection's was not.
        Assert.Empty(WorkspaceSearch.Find(snapshot, "10.0.0.5:3389").Hits);
        Assert.Equal([both.Id.ToString()], WorkspaceSearch.Find(snapshot, "10.0.0.8:3389").Hits.Select(hit => hit.Key));
        var accepted = new HostAddress("10.0.0.5", 1, 2) { Accepted = 1, AcceptedPorts = [3389] };
        Assert.True(WorkspaceSearch.NamesHost(accepted, ":3389"));
        Assert.False(WorkspaceSearch.NamesHost(accepted, "10.0.0.5:3389"));
        Assert.False(WorkspaceSearch.NamesHost(accepted, ":50000"));

        // The rule a process is found by is the one its hit is ranked by.
        foreach (string query in new[] { ":3389", "10.0.0.5:3389", "10.0.0.8:3389" })
        {
            Assert.Equal(
                [.. WorkspaceSearch.Find(snapshot, query, limit: 1_000).Hits.Select(found => found.Key).Order(StringComparer.Ordinal)],
                snapshot.Processes.Where(process => WorkspaceSearch.Finds(process, links.Where(link => link.Process == process.Id), query))
                    .Select(process => process.Id.ToString()).Order(StringComparer.Ordinal));
        }
    }

    [Fact(DisplayName = "§6.7: a port alone finds the processes that accepted connections at that port of their own whoever connected, as their cards say it, by the rule that marks them")]
    public void APortAloneFindsTheProcessesThatListenedAtIt()
    {
        // chrome.exe accepted its own machine's connections at 9222, which no host link holds; its caller made one to it, and
        // a name that holds the port's digits finds nothing by them. sshd.exe, busier, accepted another host's at 9222.
        ProcessGroup apps = new("apps", "app.exe", LaneGrouping.Executable);
        ProcessNode listener = Process(1, 3000, "chrome.exe", apps.Key, (Mechanism.Tcp, 50)) with { AcceptedPorts = [443, 9222] };
        ProcessNode caller = Process(2, 3028, "chrome.exe", apps.Key, (Mechanism.Tcp, 20));
        ProcessNode named = Process(3, 4100, "9222.exe", apps.Key, (Mechanism.Tcp, 5));
        ProcessNode remote = Process(4, 5000, "sshd.exe", apps.Key, (Mechanism.Tcp, 80)) with { AcceptedPorts = [9222] };
        HostLink[] links =
            [new(remote.Id, Mechanism.Tcp, [new("10.0.0.5", 1, 2) { Accepted = 1, AcceptedPorts = [9222] }], RelationStrength.Direct)];
        WorkspaceSnapshot snapshot = new("Search", new TimeRange(0, 10), [apps], [listener, caller, named, remote], [], [], [], [], [])
        {
            HostLinks = links,
        };

        // Its hit says it accepted connections there, never the port, ranked as one found by another host's connections at it,
        // the busier first.
        SearchResult result = WorkspaceSearch.Find(snapshot, ":9222");
        Assert.Equal(
            [
                (remote.Id.ToString(), "Process instance of app.exe · TCP connections accepted at the port searched, from 1 address"),
                (listener.Id.ToString(), "Process instance of app.exe · TCP connections accepted at the port searched"),
            ],
            result.Hits.Select(hit => (hit.Key, hit.Detail)));
        Assert.Equal([listener.Id.ToString()], WorkspaceSearch.Find(snapshot, ":443").Hits.Select(hit => hit.Key));

        // Another port, an endpoint, which names the address at the other end, or a PID's digits find no listener by its port.
        Assert.Empty(WorkspaceSearch.Find(snapshot, ":9223").Hits);
        Assert.Empty(WorkspaceSearch.Find(snapshot, "127.0.0.1:9222").Hits);
        Assert.DoesNotContain(WorkspaceSearch.Find(snapshot, "9222").Hits, hit => hit.Key == listener.Id.ToString());

        // Found by its name or PID first, it is said apart as its name finds it.
        Assert.Equal("Process instance of app.exe · no relationship", WorkspaceSearch.Find(snapshot, "3000").Hits.Single().Detail);

        // The rule a process is found by is the one its hit is ranked by, which marks it in the graph and its lanes.
        foreach (string query in new[] { ":9222", ":443", ":9223", "127.0.0.1:9222", "9222" })
        {
            Assert.Equal(
                [.. WorkspaceSearch.Find(snapshot, query, limit: 1_000).Hits.Where(found => found.Kind == SearchHitKind.Process)
                    .Select(found => found.Key).Order(StringComparer.Ordinal)],
                snapshot.Processes.Where(process => WorkspaceSearch.Finds(process, links.Where(link => link.Process == process.Id), query))
                    .Select(process => process.Id.ToString()).Order(StringComparer.Ordinal));
        }
    }

    [Fact(DisplayName = "§6.7: a channel found by its endpoint is named by the processes at its ends, who connected to whom, never by the endpoint")]
    public void AChannelHitIsNamedByWhoConnectedToWhom()
    {
        // The client connected to the server's 8080, and sent to its 9090 over a connection the capture saw open neither way.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 110 },
            Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 3)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 200 },
            Transfer(11, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 4)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 210 },
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 4, 100, 5)
                .Between("127.0.0.1:53100", "127.0.0.1:9090") with { SessionRelativeTicks = 300 },
            Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 4, 200, 6)
                .Between("127.0.0.1:9090", "127.0.0.1:53100") with { SessionRelativeTicks = 310 },
        ]);
        WorkspaceSnapshot snapshot = OverviewWorkspace.From(SessionOverviewProjector.Project(session.Store));

        SearchHit tcp = Assert.Single(WorkspaceSearch.Find(snapshot, "50000").Hits, hit => hit.Kind == SearchHitKind.Channel);
        Assert.Equal(("client.exe · PID 100 → server.exe · PID 200", "TCP channel whose endpoint the search names"), (tcp.Label, tcp.Detail));
        SearchHit unseen = Assert.Single(WorkspaceSearch.Find(snapshot, "53100").Hits, hit => hit.Kind == SearchHitKind.Channel);
        Assert.Matches("^(client|server)\\.exe · PID [12]00 and (client|server)\\.exe · PID [12]00$", unseen.Label);
        Assert.DoesNotContain("53100", unseen.Label + unseen.Detail, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§6.7: a bare number of at most five digits up to 65535 names the port it would be typed after a colon, and nothing else does")]
    public void ABareNumberNamesAPort()
    {
        Assert.Equal((ushort)5353, WorkspaceSearch.BarePort("5353"));
        Assert.Equal((ushort)443, WorkspaceSearch.BarePort(" 443 "));
        Assert.Equal((ushort)443, WorkspaceSearch.BarePort("00443"));
        Assert.Equal((ushort)0, WorkspaceSearch.BarePort("0"));
        Assert.Equal(ushort.MaxValue, WorkspaceSearch.BarePort("65535"));
        foreach (string? text in new[] { null, "", " ", "65536", "123456", "000443", "443a", "a443", ":443", "-443", "+443", "4 43", "142.250" })
        {
            Assert.Null(WorkspaceSearch.BarePort(text));
        }
    }

    private static ProcessNode Process(int index, int processId, string name, string group, params (Mechanism, long)[] made) =>
        new(new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")), processId, name, "test", group, 0.5, 0.5,
            CoverageState.Covered)
        {
            Activity = [.. made.Select(entry => new MechanismCount(entry.Item1, entry.Item2))],
        };
}
