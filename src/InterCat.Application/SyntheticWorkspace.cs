using InterCat.Domain;

namespace InterCat.Application;

public static class SyntheticWorkspace
{
    private const long Second = 10_000_000;

    public static WorkspaceSnapshot Create()
    {
        ProcessNode browser = Node("5eb7465f-3dd6-4df8-8577-472fa43aab10", 8204, "Browser", "Client", 0.16, 0.28);
        ProcessNode api = Node("76447f1d-1cfc-4815-b775-cbdb8327f624", 5120, "API host", "Service", 0.49, 0.18);
        ProcessNode cache = Node("25ecf72c-7d72-4421-943e-eb6d66cd2fe8", 4056, "Cache", "Worker", 0.78, 0.38);
        ProcessNode indexer = Node("d7ab57e0-f196-4cd8-b22a-6a9ca0c4439b", 9132, "Indexer", "Worker", 0.35, 0.72);
        ProcessNode broker = Node("fc8f5d21-315a-49f8-992d-f99f15f28292", 1180, "Service broker", "System", 0.72, 0.76);

        var processes = new[] { browser, api, cache, indexer, broker };
        var edges = new[]
        {
            new CommunicationEdge(browser.Id, api.Id, Mechanism.Tcp, 312, 6_420_000, RelationStrength.Direct),
            new CommunicationEdge(api.Id, cache.Id, Mechanism.NamedPipe, 186, null, RelationStrength.Candidate),
            new CommunicationEdge(api.Id, indexer.Id, Mechanism.Rpc, 94, 820_000, RelationStrength.Correlated),
            new CommunicationEdge(indexer.Id, broker.Id, Mechanism.Alpc, 221, null, RelationStrength.Candidate),
            new CommunicationEdge(cache.Id, broker.Id, Mechanism.SharedSection, 2, null, RelationStrength.Unresolved),
        };

        var bucketCounts = new[] { 3, 5, 7, 18, 42, 57, 31, 16, 13, 22, 61, 78, 50, 24, 14, 9, 27, 43, 29, 12, 8, 5, 4, 2 };
        var mechanisms = new[] { Mechanism.Tcp, Mechanism.NamedPipe, Mechanism.Rpc, Mechanism.Alpc };
        var timeline = new TimelineBucket[bucketCounts.Length];
        for (int index = 0; index < bucketCounts.Length; index++)
        {
            int count = bucketCounts[index];
            long start = index * Second;
            CoverageState coverage = index is 15 or 16 ? CoverageState.PartialGap : CoverageState.Covered;
            timeline[index] = new(
                new(start, start + Second),
                count,
                coverage == CoverageState.Covered ? count * 18_432L : null,
                mechanisms[index % mechanisms.Length],
                coverage);
        }

        return new(
            "Synthetic discovery tour",
            new(0, bucketCounts.Length * Second),
            processes,
            edges,
            timeline);
    }

    private static ProcessNode Node(string id, int processId, string name, string role, double x, double y) =>
        new(new(Guid.Parse(id)), processId, name, role, x, y, CoverageState.Covered);
}
