using System.Globalization;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// A deterministic five-process workspace with data at every rung of the section 3.2 ladder. It is
/// synthetic and says so: nothing here was captured, and no number in it is evidence about Windows.
/// </summary>
public static class SyntheticWorkspace
{
    private const long Second = 10_000_000;

    public static WorkspaceSnapshot Create()
    {
        var groups = new[]
        {
            new ProcessGroup("group.app", "Application host", LaneGrouping.Executable),
            new ProcessGroup("group.platform", "Platform services", LaneGrouping.ServiceContainer),
        };

        ProcessNode browser = Node("5eb7465f-3dd6-4df8-8577-472fa43aab10", 8204, "Browser", "Client", "group.app", 0.16, 0.28);
        ProcessNode api = Node("76447f1d-1cfc-4815-b775-cbdb8327f624", 5120, "API host", "Service", "group.app", 0.49, 0.18);
        ProcessNode cache = Node("25ecf72c-7d72-4421-943e-eb6d66cd2fe8", 4056, "Cache", "Worker", "group.app", 0.78, 0.38);
        ProcessNode indexer = Node("d7ab57e0-f196-4cd8-b22a-6a9ca0c4439b", 9132, "Indexer", "Worker", "group.platform", 0.35, 0.72);
        ProcessNode broker = Node("fc8f5d21-315a-49f8-992d-f99f15f28292", 1180, "Service broker", "System", "group.platform", 0.72, 0.76);

        var processes = new[] { browser, api, cache, indexer, broker };
        var edges = new[]
        {
            new CommunicationEdge("edge.browser-api", browser.Id, api.Id, Mechanism.Tcp, 312, 6_420_000, RelationStrength.Direct),
            new CommunicationEdge("edge.api-cache", api.Id, cache.Id, Mechanism.NamedPipe, 186, null, RelationStrength.Candidate),
            new CommunicationEdge("edge.api-indexer", api.Id, indexer.Id, Mechanism.Rpc, 94, 820_000, RelationStrength.Correlated),
            new CommunicationEdge("edge.indexer-broker", indexer.Id, broker.Id, Mechanism.Alpc, 221, null, RelationStrength.Candidate),
            new CommunicationEdge("edge.cache-broker", cache.Id, broker.Id, Mechanism.SharedSection, 2, null, RelationStrength.Unresolved),
        };

        var channels = new[]
        {
            new Channel("chan.https", "edge.browser-api", "127.0.0.1:44310 → 127.0.0.1:8443", Mechanism.Tcp, Direction.Outbound, 248, 5_180_000, CoverageState.Covered),
            new Channel("chan.health", "edge.browser-api", "127.0.0.1:44311 → 127.0.0.1:8443", Mechanism.Tcp, Direction.Outbound, 64, 1_240_000, CoverageState.Covered),
            new Channel("chan.cachepipe", "edge.api-cache", @"\\.\pipe\intercat-cache", Mechanism.NamedPipe, Direction.Bidirectional, 186, null, CoverageState.NotCollected),
            new Channel("chan.indexrpc", "edge.api-indexer", "367abb81-9844-35f1-ad32-98f038001003", Mechanism.Rpc, Direction.Outbound, 94, 820_000, CoverageState.ReducedFidelity),
            new Channel("chan.brokeralpc", "edge.indexer-broker", @"\RPC Control\brokerport", Mechanism.Alpc, Direction.Bidirectional, 221, null, CoverageState.PartialGap),
            new Channel("chan.section", "edge.cache-broker", @"\BaseNamedObjects\intercat-shm", Mechanism.SharedSection, Direction.DirectionNotApplicable, 2, null, CoverageState.UnknownCoverage),
        };

        var operations = new List<ChannelOperation>
        {
            Operation("op.https.1", "chan.https", ObservationKind.Connect, Direction.Outbound, 1, 2, null, null, OperationState.Completed),
            Operation("op.https.2", "chan.https", ObservationKind.Send, Direction.Outbound, 2, 3, 4_096, 4_096, OperationState.Completed),
            Operation("op.https.3", "chan.https", ObservationKind.Receive, Direction.Inbound, 3, 5, 65_536, 32_768, OperationState.Completed),
            Operation("op.https.4", "chan.https", ObservationKind.Send, Direction.Outbound, 11, 12, 8_192, null, OperationState.Started),
            Operation("op.health.1", "chan.health", ObservationKind.Connect, Direction.Outbound, 6, 6, null, null, OperationState.Completed),
            Operation("op.health.2", "chan.health", ObservationKind.Send, Direction.Outbound, 6, 7, 512, 512, OperationState.Completed),
            Operation("op.indexrpc.1", "chan.indexrpc", ObservationKind.RequestStart, Direction.Outbound, 8, 9, null, null, OperationState.Completed),
            Operation("op.indexrpc.2", "chan.indexrpc", ObservationKind.RequestStart, Direction.Outbound, 14, 15, null, null, OperationState.OpenAtBoundary),
            Operation("op.brokeralpc.1", "chan.brokeralpc", ObservationKind.Send, Direction.Outbound, 17, 18, null, null, OperationState.Ambiguous),
            Operation("op.section.1", "chan.section", ObservationKind.Map, Direction.DirectionNotApplicable, 20, 20, null, null, OperationState.OrphanCompletion),
        };

        var evidence = new List<EvidenceMark>
        {
            Mark("ev.1", "op.https.1", 10_001, "Microsoft-Windows-Kernel-Network", 12, "TCP connect accepted, 127.0.0.1:44310"),
            Mark("ev.2", "op.https.2", 20_002, "Microsoft-Windows-Kernel-Network", 10, "TCP send, 4,096 B on 127.0.0.1:44310"),
            Mark("ev.3", "op.https.3", 30_003, "Microsoft-Windows-Kernel-Network", 11, "TCP receive, 32,768 B of 65,536 requested"),
            Mark("ev.4", "op.https.3", 30_004, "Microsoft-Windows-Kernel-Network", 11, "TCP receive continuation, same flow instance"),
            Mark("ev.5", "op.https.4", 110_005, "Microsoft-Windows-Kernel-Network", 10, "TCP send started, no completion observed"),
            Mark("ev.6", "op.health.1", 60_006, "Microsoft-Windows-Kernel-Network", 12, "TCP connect accepted, 127.0.0.1:44311"),
            Mark("ev.7", "op.health.2", 60_007, "Microsoft-Windows-Kernel-Network", 10, "TCP send, 512 B on 127.0.0.1:44311"),
            Mark("ev.8", "op.indexrpc.1", 80_008, "Microsoft-Windows-RPC", 5, "RPC call start, interface 367abb81"),
            Mark("ev.9", "op.indexrpc.1", 90_009, "Microsoft-Windows-RPC", 6, "RPC call end, paired by activity id"),
            Mark("ev.10", "op.indexrpc.2", 140_010, "Microsoft-Windows-RPC", 5, "RPC call start, open at capture boundary"),
            Mark("ev.11", "op.brokeralpc.1", 170_011, "Microsoft-Windows-Kernel-Process", 1, "ALPC port reference, peer unresolved"),
            Mark("ev.12", "op.section.1", 200_012, "Microsoft-Windows-Kernel-Process", 1, "Section mapped, no creating open observed"),
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
            groups,
            processes,
            edges,
            channels,
            operations,
            evidence,
            timeline);
    }

    /// <summary>The navigation state a workspace opens at: the whole machine, whole extent, no filter.</summary>
    public static NavigationState Root(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new()
        {
            Level = DetailLevel.Machine,
            Focus = null,
            Viewport = snapshot.Extent,
            Lanes = LaneGrouping.Mechanism,
            GraphFocusKey = null,
            Filters = [],
        };
    }

    private static ProcessNode Node(
        string id,
        int processId,
        string name,
        string role,
        string groupKey,
        double x,
        double y) =>
        new(new(Guid.Parse(id)), processId, name, role, groupKey, x, y, CoverageState.Covered);

    private static ChannelOperation Operation(
        string key,
        string channelKey,
        ObservationKind kind,
        Direction direction,
        int startSecond,
        int endSecond,
        long? requested,
        long? completed,
        OperationState state) =>
        new(
            key,
            channelKey,
            kind,
            direction,
            new(startSecond * Second, (endSecond + 1) * Second),
            requested,
            completed,
            state);

    private static EvidenceMark Mark(
        string key,
        string operationKey,
        long nativeTicks,
        string provider,
        int eventId,
        string summary) =>
        new(key, operationKey, nativeTicks, provider, eventId, string.Create(CultureInfo.InvariantCulture, $"{summary}"));
}
