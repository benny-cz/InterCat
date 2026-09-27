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

    /// <summary>The value a ranking reads, with the failures and unpaired stops beside it.</summary>
    public RankedValue Of(RankingMetric metric) => metric switch
    {
        RankingMetric.RpcCallsMade => new(metric, Made > 0 ? Made : null, Made, MadeUnpaired) { Failed = MadeFailed },
        RankingMetric.RpcCallsServed => new(metric, Served > 0 ? Served : null, Served, ServedUnpaired) { Failed = ServedFailed },
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Only an RPC call ranking reads a process's calls."),
    };

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
            ServedUnpaired + other.ServedUnpaired);
    }
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
    /// </summary>
    public static SessionCallMeasures Measure(
        SessionStore store,
        TimeRange? interval,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));

        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
            ?? throw new InvalidDataException("This generation names no source clock, so its RPC calls cannot be derived.");
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
        SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
        SessionDerivation derivation = SessionDerivationCache.For(manifest);
        ProcessInstanceIndex processes = derivation.Processes(store.Root, segments, clock, fields, cancellationToken);
        TimeRange? native = interval is { } presentation ? RankingScope.NativeInterval(clock, presentation) : null;
        MechanismCoverage coverage = interval is not null && native is null
            ? new(Mechanism.Rpc, CoverageState.UnknownCoverage, "no source reading falls in this interval")
            : SessionCoverage.Of(SessionSegments.CoverageLedger(store.Root, manifest), Mechanism.Rpc, native);

        // One slot per instance, then one for every call no instance takes under the policy. An interval no reading can
        // fall in holds no call, and none is derived for it.
        int instances = processes.Instances.Count;
        var tally = new long[(instances + 1) * 6];
        if (interval is null || native is not null)
        {
            RpcCallIndex calls = derivation.RpcCalls(store.Root, segments, clock, fields, cancellationToken);
            foreach (RpcCallOutcome call in calls.Outcomes())
            {
                if (call.Stop is not { } stop || native is { } range && !range.Contains(stop.NativeTicks))
                {
                    continue;
                }

                int slot = call.Process.IsAdmittedUnder(policy) ? call.Process.Instance : instances;
                int side = call.Side == RpcCallSide.Client ? 0 : 3;
                int cell = (slot * 6) + side;
                if (call.State == RpcCallState.Completed)
                {
                    tally[cell]++;
                    tally[cell + 1] += call.Status is { } status && status != 0 ? 1 : 0;
                }
                else
                {
                    tally[cell + 2]++;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var byProcess = new Dictionary<ProcessInstanceId, ProcessCalls>();
        for (int instance = 0; instance < instances; instance++)
        {
            ProcessCalls counted = Of(tally, instance);
            if (counted != ProcessCalls.None)
            {
                byProcess[processes.Instances[instance].Id] = counted;
            }
        }

        return new(manifest.SessionId, manifest.Generation, interval, byProcess, Of(tally, instances), coverage);
    }

    private static ProcessCalls Of(long[] tally, int slot)
    {
        int cell = slot * 6;
        return new(tally[cell], tally[cell + 1], tally[cell + 2], tally[cell + 3], tally[cell + 4], tally[cell + 5]);
    }
}
