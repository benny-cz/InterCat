using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// One process instance's RPC calls in scope, on each side (`metrics-v1` §8a): the calls it completed, counted by their
/// stop; those whose stop reported a status other than 0; and the stops paired with no start, which are stated and never
/// counted. A call is made at its client and served at its server, so each side is a count of its own and neither is a
/// share of the other.
/// </summary>
public sealed record ProcessCalls(
    long Made,
    long MadeFailed,
    long MadeUnpaired,
    long Served,
    long ServedFailed,
    long ServedUnpaired)
{
    /// <summary>A process with no RPC stop in scope.</summary>
    public static ProcessCalls None { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>Of the calls made, those whose stop carried no status, which an error count cannot call succeeded.</summary>
    public long MadeNoStatus { get; init; }

    /// <summary>Of the calls served, those whose stop carried no status.</summary>
    public long ServedNoStatus { get; init; }

    /// <summary>
    /// The value a ranking reads, with the failures and unpaired stops beside it. An error ranking reads the failed calls
    /// among those whose stop carried a status, and states those that carried none as unmeasured (R3).
    /// </summary>
    public RankedValue Of(RankingMetric metric)
    {
        switch (metric)
        {
            case RankingMetric.RpcCallsMade:
                return new(metric, Made > 0 ? Made : null, Made, MadeUnpaired) { Failed = MadeFailed };
            case RankingMetric.RpcCallsServed:
                return new(metric, Served > 0 ? Served : null, Served, ServedUnpaired) { Failed = ServedFailed };
            case RankingMetric.RpcErrors:
                long known = Made - MadeNoStatus + Served - ServedNoStatus;
                return new(metric, known > 0 ? MadeFailed + ServedFailed : null, known, MadeNoStatus + ServedNoStatus);
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric, "Only an RPC call ranking reads a process's calls.");
        }
    }

    /// <summary>Two processes' calls together: a group's, which its members partition.</summary>
    public ProcessCalls Plus(ProcessCalls other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(
            Made + other.Made,
            MadeFailed + other.MadeFailed,
            MadeUnpaired + other.MadeUnpaired,
            Served + other.Served,
            ServedFailed + other.ServedFailed,
            ServedUnpaired + other.ServedUnpaired)
        {
            MadeNoStatus = MadeNoStatus + other.MadeNoStatus,
            ServedNoStatus = ServedNoStatus + other.ServedNoStatus,
        };
    }
}

/// <summary>
/// How long a process's, or a group's, completed RPC calls in scope took, on each side (`metrics-v1` §8a): how many were
/// timed - a start and a stop both in the evidence - and the median of them by nearest rank, in session nanoseconds, as
/// `icat metric --metric duration` answers for the `ClientCall` and `ServerExecution` intervals. A median does not add, so
/// a group's is taken over its members' calls together, never from their medians.
/// </summary>
public sealed record CallTimes(long MadeTimed, long? MadeMedian, long ServedTimed, long? ServedMedian)
{
    /// <summary>A process or group with no completed call in scope.</summary>
    public static CallTimes None { get; } = new(0, null, 0, null);

    /// <summary>
    /// The value a time ranking reads: the median of the calls timed, with the stops paired with no start beside them,
    /// which are never timed (<paramref name="calls"/> states them).
    /// </summary>
    public RankedValue Of(RankingMetric metric, ProcessCalls calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        return metric switch
        {
            RankingMetric.RpcCallTime => new(metric, MadeMedian, MadeTimed, calls.MadeUnpaired),
            RankingMetric.RpcServeTime => new(metric, ServedMedian, ServedTimed, calls.ServedUnpaired),
            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Only an RPC time ranking reads call times."),
        };
    }

    internal static CallTimes Of(List<long> made, List<long> served) => new(
        made.Count, RpcCallDurations.Of(made)?.Median, served.Count, RpcCallDurations.Of(served)?.Median);
}

/// <summary>
/// Every process instance's RPC calls over one scope of one generation, and RPC's capture coverage over it. When the
/// capture did not collect RPC, <see cref="Unavailable"/> says so: its absence of calls is not a count of zero (R21).
/// </summary>
public sealed record SessionCallMeasures(
    Guid SessionId,
    long Generation,
    TimeRange? Interval,
    IReadOnlyDictionary<ProcessInstanceId, ProcessCalls> ByProcess,
    ProcessCalls Unattributed,
    MechanismCoverage Coverage) : IRankingMeasures
{
    /// <summary>Why no call can be counted over this scope; null when they can.</summary>
    public string? Unavailable => Coverage.State == CoverageState.NotCollected
        ? $"this capture did not collect RPC over this scope ({Coverage.Reason})"
        : null;

    /// <summary>Each process instance's call times; one with no completed call in scope is absent.</summary>
    public IReadOnlyDictionary<ProcessInstanceId, CallTimes> TimesByProcess { get; init; } = new Dictionary<ProcessInstanceId, CallTimes>();

    /// <summary>
    /// Each group's call times, its members' calls together, keyed as the ladder keys its groups under every grouping the
    /// window offers (<see cref="WorkspaceGrouping"/>); a group with no completed call in scope is absent.
    /// </summary>
    public IReadOnlyDictionary<string, CallTimes> TimesByGroup { get; init; } = new Dictionary<string, CallTimes>(StringComparer.Ordinal);

    /// <summary>The call times of every process the evidence policy admits a call to, together: the machine rung's rows'.</summary>
    public CallTimes TimesAttributed { get; init; } = CallTimes.None;
}

/// <summary>
/// Reads each process's RPC calls for the ranked table: what `icat metric --basis logical-operations --metric
/// operations-completed --group-by process` answers for every instance, split by the side each call was made on (R18).
/// A call belongs to the instance its first record binds to under the evidence policy; one the policy does not admit, or
/// that binds to none, is counted apart as unattributed and never given to a process.
/// </summary>
public static class SessionCallRanking
{
    /// <summary>
    /// Counts the whole retained capture, or <paramref name="interval"/> in 100-nanosecond presentation ticks, whose bounds
    /// are placed on the source clock as the command line places a time. A call is in scope when its stop's reading is.
    /// The instances a view sets aside (<paramref name="setAside"/>, §19.5) keep their own calls, but no group's or the
    /// machine rung's times take theirs, since their rows are not shown.
    /// </summary>
    public static SessionCallMeasures Measure(
        SessionStore store,
        TimeRange? interval,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        IReadOnlySet<ProcessInstanceId>? setAside = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));

        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
            ?? throw new InvalidDataException("This generation names no source clock, so its RPC calls cannot be derived.");
        // The instances and the calls the derivation or the checkpoint holds are read without opening a segment (P25); the
        // calls are paired over every segment once per generation, since a call's request and response can lie apart.
        var generation = new GenerationSegments(store, manifest, clock);
        ProcessInstanceIndex processes = generation.Processes(cancellationToken);
        TimeRange? native = interval is { } presentation ? RankingScope.NativeInterval(clock, presentation) : null;
        MechanismCoverage coverage = interval is not null && native is null
            ? new(Mechanism.Rpc, CoverageState.UnknownCoverage, "no source reading falls in this interval")
            : SessionCoverage.Of(SessionSegments.CoverageLedger(store.Root, manifest), Mechanism.Rpc, native);

        // One slot per instance, then one for every call no instance takes under the policy. An interval no reading can
        // fall in holds no call, and none is derived for it. Each completed call is timed as the duration metric times it:
        // its stop's session nanoseconds less its start's.
        int instances = processes.Instances.Count;
        var tally = new long[(instances + 1) * Cells];
        var made = new List<long>?[instances];
        var served = new List<long>?[instances];
        if (interval is null || native is not null)
        {
            RpcCallIndex calls = generation.RpcCalls(cancellationToken);
            foreach (RpcCallOutcome call in calls.Outcomes())
            {
                if (call.Stop is not { } stop || native is { } range && !range.Contains(stop.NativeTicks))
                {
                    continue;
                }

                int slot = call.Process.IsAdmittedUnder(policy) ? call.Process.Instance : instances;
                int side = call.Side == RpcCallSide.Client ? 0 : 4;
                int cell = (slot * Cells) + side;
                if (call.State == RpcCallState.Completed)
                {
                    tally[cell]++;
                    tally[cell + 1] += call.Status is { } status && status != 0 ? 1 : 0;
                    tally[cell + 3] += call.Status is null ? 1 : 0;
                    if (slot < instances)
                    {
                        long took = (long)SourceClockMath.SessionNanoseconds(clock, stop.NativeTicks)
                            - (long)SourceClockMath.SessionNanoseconds(clock, call.Start!.Value.NativeTicks);
                        List<long>?[] times = call.Side == RpcCallSide.Client ? made : served;
                        (times[slot] ??= []).Add(took);
                    }
                }
                else
                {
                    tally[cell + 2]++;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var byProcess = new Dictionary<ProcessInstanceId, ProcessCalls>();
        var timesByProcess = new Dictionary<ProcessInstanceId, CallTimes>();
        var groupMade = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var groupServed = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        List<long> allMade = [];
        List<long> allServed = [];
        for (int instance = 0; instance < instances; instance++)
        {
            ProcessCalls counted = Of(tally, instance);
            if (counted != ProcessCalls.None)
            {
                byProcess[processes.Instances[instance].Id] = counted;
            }

            if (made[instance] is null && served[instance] is null)
            {
                continue;
            }

            List<long> ownMade = made[instance] ?? [];
            List<long> ownServed = served[instance] ?? [];
            timesByProcess[processes.Instances[instance].Id] = CallTimes.Of(ownMade, ownServed);
            if (setAside?.Contains(processes.Instances[instance].Id) == true)
            {
                continue;
            }

            foreach (LaneGrouping grouping in WorkspaceGrouping.All)
            {
                string group = WorkspaceGrouping.KeyOf(processes.Instances[instance], grouping);
                AddTo(groupMade, group, ownMade);
                AddTo(groupServed, group, ownServed);
            }

            allMade.AddRange(ownMade);
            allServed.AddRange(ownServed);
        }

        return new(manifest.SessionId, manifest.Generation, interval, byProcess, Of(tally, instances), coverage)
        {
            TimesByProcess = timesByProcess,
            TimesByGroup = groupMade.Keys.Union(groupServed.Keys, StringComparer.Ordinal).ToDictionary(
                group => group,
                group => CallTimes.Of(groupMade.GetValueOrDefault(group) ?? [], groupServed.GetValueOrDefault(group) ?? []),
                StringComparer.Ordinal),
            TimesAttributed = CallTimes.Of(allMade, allServed),
        };

        static void AddTo(Dictionary<string, List<long>> groups, string group, List<long> times)
        {
            if (times.Count == 0) return;
            if (!groups.TryGetValue(group, out List<long>? kept)) groups[group] = kept = [];
            kept.AddRange(times);
        }
    }

    /// <summary>Per slot: made, failed, unpaired and without status; then the same served.</summary>
    private const int Cells = 8;

    private static ProcessCalls Of(long[] tally, int slot)
    {
        int cell = slot * Cells;
        return new(tally[cell], tally[cell + 1], tally[cell + 2], tally[cell + 4], tally[cell + 5], tally[cell + 6])
        {
            MadeNoStatus = tally[cell + 3],
            ServedNoStatus = tally[cell + 7],
        };
    }
}
