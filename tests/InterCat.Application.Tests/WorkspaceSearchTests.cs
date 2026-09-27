using InterCat.Application;
using InterCat.Domain;
using Xunit;

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
        Assert.Equal("Channel", pipe.Label);
        Assert.DoesNotContain("intercat-cache", pipe.Detail, StringComparison.Ordinal);
        Assert.Equal(["group.app", Api.ToString(), "chan.cachepipe"], pipe.Path);
        Assert.StartsWith("Channel between API host · PID 5120 and Cache · PID 4056", pipe.Detail, StringComparison.Ordinal);

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

    private static ProcessNode Process(int index, int processId, string name, string group, params (Mechanism, long)[] made) =>
        new(new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")), processId, name, "test", group, 0.5, 0.5,
            CoverageState.Covered)
        {
            Activity = [.. made.Select(entry => new MechanismCount(entry.Item1, entry.Item2))],
        };
}
