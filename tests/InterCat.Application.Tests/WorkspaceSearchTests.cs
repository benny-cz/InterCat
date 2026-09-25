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
}
