using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// The honest L0-L3 workspace for one published session generation. A session overview has no logical-operation
/// or exact-record projection yet: those rungs stay empty instead of borrowing the synthetic tour.
/// </summary>
public static class OverviewWorkspace
{
    /// <summary>What every view of a real session's overview must say about what it does and does not show.</summary>
    public const string SessionDisclosure =
        "The graph shows admitted paired TCP; a process's rung also lists its RPC calls by interface, call by call. "
        + "The timeline includes every observed row. TCP and UDP records are completed transfers with no operation "
        + "rung. Source records are one step (E) from every rung, and Enter on a record opens its original journal "
        + "entry. Byte previews stay hidden until requested.";

    /// <summary>
    /// What a view of a session whose capture collected ALPC says instead of <see cref="SessionDisclosure"/>: its graph
    /// also joins processes by RPC calls linked through ALPC to the calls that served them (`contracts/operations-v1.md`
    /// §5c).
    /// </summary>
    public const string LinkedCallsDisclosure =
        "The graph shows admitted paired TCP and RPC calls linked through ALPC to the processes that served them; a "
        + "process's rung lists its RPC calls by interface, call by call, with who served each. The timeline includes "
        + "every observed row. TCP and UDP records are completed transfers with no operation rung. Source records are one "
        + "step (E) from every rung, and Enter on a record opens its original journal entry. Byte previews stay hidden "
        + "until requested.";

    /// <summary>The disclosure a view of <paramref name="snapshot"/> states: which relationships its graph draws.</summary>
    public static string DisclosureFor(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Edges.Any(edge => edge.Mechanism == Mechanism.Rpc) ? LinkedCallsDisclosure : SessionDisclosure;
    }

    public static WorkspaceSnapshot Empty() => new(
        "Start exploring", new TimeRange(0, 1), [], [], [], [], [], [], []);

    public static WorkspaceSnapshot From(SessionOverviewBundle overview)
    {
        ArgumentNullException.ThrowIfNull(overview);
        return new WorkspaceSnapshot(
            overview.Redaction is not null ? $"Redacted package · generation {overview.Generation:N0}"
                : overview.Demo ? $"Demo session · generation {overview.Generation:N0}"
                : $"Session · generation {overview.Generation:N0}",
            overview.Extent ?? new TimeRange(0, 1),
            overview.Groups,
            overview.Nodes,
            overview.Edges,
            overview.Channels, [], [],
            overview.Timeline,
            overview.ChannelProjectionProblem,
            overview.Minimap,
            overview.Redaction)
        {
            MechanismLanes = overview.MechanismLanes,
            Clock = overview.Clock,
            CoverageLedgerPublished = overview.CoverageLedgerPublished,
            MechanismCoverage = overview.MechanismCoverage,
            RowsNoProcessHolds = overview.RowsNoProcessHolds,
            Recording = overview.Recording,
            Began = overview.Began,
            Demo = overview.Demo,
            Collectors = overview.Collectors,
        };
    }

    /// <summary>
    /// What every view of a redacted session package adds to <see cref="SessionDisclosure"/>: its values are pseudonyms
    /// and its records synthetic, so no name, id or address in it is the source machine's.
    /// </summary>
    public const string RedactedDisclosure =
        "This is a redacted session package: names, process and thread IDs, addresses, ports and identifiers are random "
        + "pseudonyms consistent only within it, and each record's original entry is a synthetic metadata record with "
        + "no payload. Times, sizes, counts and relationships are the source's.";

    /// <summary>
    /// The same workspace ranked within an analysis interval: every process, edge and channel keeps its identity and
    /// position and carries only the records inside the interval, so the ladder's totals and ranking follow the brush
    /// while the graph and timeline keep their shape (plan §6.4). One with nothing in the interval counts zero, not
    /// "absent", beside each mechanism's coverage there, which says whether a zero could be seen at all (R21).
    /// </summary>
    public static WorkspaceSnapshot WithinInterval(WorkspaceSnapshot snapshot, SessionIntervalCounts counts)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(counts);
        // A row counted within the interval is as complete as the capture was there, not over the whole session.
        return snapshot with
        {
            Processes = [.. snapshot.Processes.Select(process => process with
            {
                Activity = counts.ProcessRecords.GetValueOrDefault(process.Id) ?? [],
                Coverage = counts.CaptureCoverage ?? process.Coverage,
            })],
            Edges = [.. snapshot.Edges.Select(edge => edge with
            {
                ObservationCount = counts.EdgeRecords.GetValueOrDefault(edge.Key),
            })],
            Channels = [.. snapshot.Channels.Select(channel => channel with
            {
                ObservationCount = counts.ChannelRecords.GetValueOrDefault(channel.Key),
                Coverage = counts.TcpCoverage ?? channel.Coverage,
            })],
            RowsNoProcessHolds = snapshot.RowsNoProcessHolds is null
                ? null
                : counts.ObservedRows - counts.ProcessRecords.Values.Sum(records => records.Sum(entry => entry.Records)),
            MechanismCoverage = counts.MechanismCoverage,
        };
    }

    /// <summary>
    /// The same workspace with each process's transport bytes over the measured scope, and each channel end's, for a
    /// ranking by bytes (<see cref="SessionByteRanking"/>); a process or end the measurement found no send or receive
    /// record of holds none.
    /// </summary>
    public static WorkspaceSnapshot WithBytes(WorkspaceSnapshot snapshot, SessionByteMeasures measures)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(measures);
        Dictionary<string, CommunicationEdge> edges = snapshot.Edges.ToDictionary(edge => edge.Key, StringComparer.Ordinal);
        return snapshot with
        {
            Processes = [.. snapshot.Processes.Select(process => process with
            {
                Bytes = measures.ByProcess.GetValueOrDefault(process.Id) ?? TransportBytes.None,
            })],
            Channels = [.. snapshot.Channels.Select(channel => edges.TryGetValue(channel.EdgeKey, out CommunicationEdge? edge)
                ? channel with
                {
                    EndBytes = new[] { edge.SourceId, edge.TargetId }.Distinct().ToDictionary(
                        end => end,
                        end => measures.ByChannelEnd.GetValueOrDefault(new ChannelEnd(channel.Key, end)) ?? TransportBytes.None),
                }
                : channel)],
        };
    }

    /// <summary>
    /// The same workspace with each process's RPC calls over the measured scope, for a ranking by calls
    /// (<see cref="SessionCallRanking"/>); a process with no RPC stop in scope holds none. Measures that could not count
    /// a call, because the capture did not collect RPC, give no process a value.
    /// </summary>
    /// <summary>
    /// The same workspace with each process's and group's distinct peers over the measured scope, for a ranking by peers
    /// (<see cref="SessionPeerRanking"/>); one with no record that has another end holds none.
    /// </summary>
    public static WorkspaceSnapshot WithPeers(WorkspaceSnapshot snapshot, SessionPeerMeasures measures)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(measures);
        return snapshot with
        {
            Processes = [.. snapshot.Processes.Select(process => process with
            {
                Peers = measures.ByProcess.GetValueOrDefault(process.Id) ?? PeerCount.None,
            })],
            Groups = [.. snapshot.Groups.Select(group => group with
            {
                Peers = measures.ByGroup.GetValueOrDefault(group.Key) ?? PeerCount.None,
            })],
        };
    }

    public static WorkspaceSnapshot WithCalls(WorkspaceSnapshot snapshot, SessionCallMeasures measures)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(measures);
        return measures.Unavailable is not null ? snapshot : snapshot with
        {
            Processes = [.. snapshot.Processes.Select(process => process with
            {
                Calls = measures.ByProcess.GetValueOrDefault(process.Id) ?? ProcessCalls.None,
                CallTimes = measures.TimesByProcess.GetValueOrDefault(process.Id) ?? CallTimes.None,
            })],
            Groups = [.. snapshot.Groups.Select(group => group with
            {
                CallTimes = measures.TimesByGroup.GetValueOrDefault(group.Key) ?? CallTimes.None,
            })],
        };
    }
}
