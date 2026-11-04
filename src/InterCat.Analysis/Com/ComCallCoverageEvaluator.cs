using System.Globalization;
using InterCat.Domain;

namespace InterCat.Analysis;

/// <summary>Settings a COM call coverage evaluation ran under, recorded so a result can be reproduced (I16).</summary>
public sealed record ComCallCoverageSettings
{
    /// <summary>How far a COM call's start may sit from the truth record of the call it answers.</summary>
    public TimeSpan MatchWindow { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The interface the workload called its object through: FX-COM-001's IDispatch.</summary>
    public required Guid ExpectedInterface { get; init; }

    /// <summary>The method the workload called on it, by its number in the interface: FX-COM-001's Invoke.</summary>
    public required long ExpectedMethod { get; init; }

    public required string FixtureId { get; init; }
    public required string BuildId { get; init; }
    public required bool BuildIsSupported { get; init; }
    public bool Reproduced { get; init; }
}

/// <summary>
/// One COM call as a coverage evaluation reads it (ADR-079, ADR-080): a call `com-call-link-v1` paired from COM-Perf's records -
/// its end, the process that raised it and the one its start names at its other end, its interface, method and HRESULT, its
/// start and stop on the wall clock its capture's calibration reads - with the RPC call carrying it, the call at its other
/// end, and what the RPC call carrying a client call found at its own other end under `rpc-call-peer-v2`.
/// </summary>
public sealed record ComCallObservation
{
    /// <summary>The call's position among its capture's COM calls, which names it here and at its other end.</summary>
    public required long Ordinal { get; init; }

    public required ComCallSide Side { get; init; }

    public required ComCallState State { get; init; }

    /// <summary>The process that raised its records (`process-binding-v5`).</summary>
    public required int ProcessId { get; init; }

    /// <summary>The process its start names at its other end; null without a start or where it named none.</summary>
    public int? PeerProcessId { get; init; }

    public Guid? Interface { get; init; }

    public long? Method { get; init; }

    /// <summary>Its start, on the wall clock its capture's calibration reads it at; null without one.</summary>
    public long? StartUtcTicks { get; init; }

    public long? StopUtcTicks { get; init; }

    /// <summary>The HRESULT its stop reported; null without a stop.</summary>
    public long? Hresult { get; init; }

    public ComCarrierState Carrier { get; init; }

    public ComLinkState Link { get; init; }

    /// <summary>The ordinal of the call at its other end; null where none is linked.</summary>
    public long? OtherEnd { get; init; }

    /// <summary>The process that raised the call at its other end; null where none is linked.</summary>
    public int? OtherEndProcessId { get; init; }

    /// <summary>For a client call an RPC call carries, that RPC call's other end under `rpc-call-peer-v2`; null otherwise.</summary>
    public RpcPeerState? CarrierPeer { get; init; }

    /// <summary>Whether the RPC call carrying it reached its other end through the COM call it carries, rather than ALPC.</summary>
    public bool CarrierLinkedThroughCom { get; init; }

    /// <summary>The process of the RPC call that served the RPC call carrying it; null where none did.</summary>
    public int? CarrierServedByProcessId { get; init; }
}

/// <summary>One truth call and what the capture showed of it, at its client's end and at its server's.</summary>
public sealed record ComCallTruthCoverage(
    long? CallId,
    int ClientProcessId,
    long? TruthHresult,
    long? ClientCall,
    bool PairedByRank,
    ComCallState? State,
    long? Hresult,
    ComCarrierState? Carrier,
    ComLinkState? Link,
    int? LinkedProcessId,
    long? ServedCall,
    bool LinkedToItsServedCall,
    bool CarrierServedThroughCom,
    string? Gap);

/// <summary>The evaluation result: the section 14.2 counters plus the evidence behind every one of them.</summary>
public sealed record ComCallCoverageResult
{
    public required MechanismMeasurement Measurement { get; init; }
    public required TierAssessment Assessment { get; init; }
    public required IReadOnlyList<ComCallTruthCoverage> Calls { get; init; }

    /// <summary>The calls the truth's server logged serving, and those matched to a server COM call of its process.</summary>
    public required int TruthCallsServed { get; init; }

    public required int ServedCallsObserved { get; init; }

    /// <summary>The matched client calls whose start and stop paired, and of them those whose HRESULT is the truth's.</summary>
    public required int CallsCompleted { get; init; }

    public required int HresultsAgreeing { get; init; }

    /// <summary>The matched client calls one RPC call carries.</summary>
    public required int CallsCarried { get; init; }

    /// <summary>The matched client calls linked to the server COM call matched to the same truth call.</summary>
    public required int LinkedToTheirServedCall { get; init; }

    /// <summary>
    /// The matched client calls whose carrying RPC call was served through the COM call it carries, by an RPC call of the
    /// truth's server: the same two ends `rpc-call-peer-v2` relates (ADR-079).
    /// </summary>
    public required int CarriersServedThroughCom { get; init; }

    /// <summary>
    /// The workload's COM calls no truth record matched: calls the truth does not log, such as a method's name looked up
    /// through IDispatch before it is called, or COM's own calls on the object's interfaces and references.
    /// </summary>
    public required int CallsBeyondTruth { get; init; }

    /// <summary>COM calls of processes the truth does not name. A capture of the whole machine sees them; they are scope, not error.</summary>
    public required int ObservationsOutsideScope { get; init; }

    /// <summary>The truth records matched to the call of their end of the same rank, at both ends.</summary>
    public required int CallsPairedByRank { get; init; }

    /// <summary>
    /// How far the capture's calibrated clock read ahead of the truth's: the median distance of the records paired by rank
    /// to their calls' starts. The records matched by nearness are matched on the truth's time moved by it.
    /// </summary>
    public required TimeSpan ClockOffset { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// Compares an independent COM truth log - each call its client issued and completed, and each call its server served - with
/// the COM calls `com-call-link-v1` derived from a capture (ADR-079), and computes the section 14.2 counters (ADR-080). A truth
/// call is its client's COM call on the interface and method the workload called, raised in the client's process: where an
/// end's truth records are as many as its calls and each pair lies inside the window, each record is the call of its rank,
/// so the truth's wall clock decides nothing; every other record is matched to its end's nearest call inside the window, on
/// the truth's time moved by the offset the ranked pairs measured. A call names its interface and method and never its
/// object - COM-Perf's IPID and OID are not admitted - so, as an RPC call's (RpcCoverageEvaluator), a call is bound to the
/// interface it names, which has no lifetime of its own. Its peer is the process of the server call its link reached, never
/// the process its start names. A COM call carries no size, so no byte value is produced and the byte criterion is not
/// measured rather than a measured zero (R3, P1). It is pure and portable: the same evaluation runs in a test on committed
/// evidence (R19, R18).
/// </summary>
public static class ComCallCoverageEvaluator
{
    public static ComCallCoverageResult Evaluate(
        IReadOnlyList<TruthRecord> truth,
        IReadOnlyList<ComCallObservation> observations,
        ComCallCoverageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(truth);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(settings);

        TruthRecord[] issued = [.. truth.Where(static record => record.Kind == TruthEventKind.CallIssued).OrderBy(static record => record.Sequence)];
        TruthRecord[] served = [.. truth.Where(static record => record.Kind == TruthEventKind.CallServed).OrderBy(static record => record.Sequence)];
        int client = issued.Length > 0 ? issued[0].ProcessId : 0;
        int server = served.Length > 0 ? served[0].ProcessId : 0;
        Dictionary<long, long?> completions = [];
        foreach (TruthRecord record in truth)
        {
            if (record is { Kind: TruthEventKind.CallCompleted, CallId: { } completed })
            {
                completions[completed] = HresultOf(record.Status);
            }
        }

        // Each end's calls on the interface and method the workload called, in the order they started.
        HashSet<int> processes = [.. truth.Select(static record => record.ProcessId)];
        List<ComCallObservation> inScope = [];
        int outsideScope = 0;
        foreach (ComCallObservation observation in observations.OrderBy(static call => call.StartUtcTicks ?? call.StopUtcTicks).ThenBy(static call => call.Ordinal))
        {
            if (processes.Contains(observation.ProcessId))
            {
                inScope.Add(observation);
            }
            else
            {
                outsideScope++;
            }
        }

        ComCallObservation[] clientCalls = [.. inScope.Where(call => call.Side == ComCallSide.Client && call.ProcessId == client && Called(call, settings))];
        ComCallObservation[] serverCalls = [.. inScope.Where(call => call.Side == ComCallSide.Server && call.ProcessId == server && Called(call, settings))];

        long window = settings.MatchWindow.Ticks;
        List<long> rankedDistances = [];
        Dictionary<TruthRecord, ComCallObservation> clientByRank = ByRank(issued, clientCalls, window, rankedDistances);
        Dictionary<TruthRecord, ComCallObservation> serverByRank = ByRank(served, serverCalls, window, rankedDistances);
        rankedDistances.Sort();
        long offset = rankedDistances.Count == 0 ? 0 : rankedDistances[rankedDistances.Count / 2];
        var matched = new HashSet<long>();
        Dictionary<TruthRecord, ComCallObservation> clientMatches = Nearest(issued, clientCalls, clientByRank, window, offset, matched);
        Dictionary<TruthRecord, ComCallObservation> serverMatches = Nearest(served, serverCalls, serverByRank, window, offset, matched);
        Dictionary<long, ComCallObservation> servedCallOf = [];
        foreach ((TruthRecord record, ComCallObservation call) in serverMatches)
        {
            if (record.CallId is { } id)
            {
                servedCallOf.TryAdd(id, call);
            }
        }

        var calls = new List<ComCallTruthCoverage>(issued.Length);
        foreach (TruthRecord record in issued.OrderBy(static record => record.RecordedUtc.UtcTicks).ThenBy(static record => record.Sequence))
        {
            clientMatches.TryGetValue(record, out ComCallObservation? call);
            ComCallObservation? servedCall = record.CallId is { } id ? servedCallOf.GetValueOrDefault(id) : null;
            long? truthHresult = record.CallId is { } callId ? completions.GetValueOrDefault(callId) : null;
            bool linked = call is { Link: ComLinkState.Linked, OtherEndProcessId: not null };
            calls.Add(new(
                record.CallId,
                record.ProcessId,
                truthHresult,
                call?.Ordinal,
                clientByRank.ContainsKey(record),
                call?.State,
                call?.Hresult,
                call?.Carrier,
                call?.Link,
                linked ? call!.OtherEndProcessId : null,
                servedCall?.Ordinal,
                linked && servedCall is not null && call!.OtherEnd == servedCall.Ordinal,
                call is { Carrier: ComCarrierState.Carried, CarrierPeer: RpcPeerState.Served, CarrierLinkedThroughCom: true }
                    && call.CarrierServedByProcessId == server,
                call is null
                    ? "No COM call of the client's process on the interface and method the workload called was found inside the match window."
                    : call.State != ComCallState.Completed
                        ? $"Observed, but its start and stop did not pair ({call.State}), so its HRESULT stays unknown."
                        : !linked
                            ? $"Observed, but its other end is not linked ({call.Link})."
                            : null));
        }

        long withObservation = calls.Count(static call => call.ClientCall is not null);
        long attributed = calls.Count(static call => call.LinkedProcessId is not null);
        long falsePeers = calls.Count(call => call.LinkedProcessId is { } linked && linked != server);
        int servedObserved = serverMatches.Count;
        var measurement = new MechanismMeasurement
        {
            FixtureId = settings.FixtureId,
            BuildId = settings.BuildId,
            Reproduced = settings.Reproduced,
            MeasuredOnSupportedBuild = settings.BuildIsSupported,
            TruthOperations = calls.Count,
            TruthOperationsWithAdmittedObservation = withObservation,

            // A matched call names the interface and method the workload called: it is bound to the interface, the one
            // resource a COM call names, as an RPC call is; its object is not named.
            TruthOperationsBoundToResourceInstance = withObservation,
            PeerAttributions = attributed,
            FalsePeerAttributions = falsePeers,

            // No COM call record carries a size, so no call is byte eligible: the criterion is not measured, which is
            // different from measuring zero bytes (R3, P1).
            ByteEligibleOperations = 0,
            ByteMeasuredOperations = 0,

            // The interface has no create or destroy record, so no lifetime can be shown for it; its two ends are its
            // memberships, each resolved where a call was observed at it.
            TruthResources = 1,
            DiscoveredResourcesWithLifetime = 0,
            ExpectedMemberships = 2,
            ResolvedMemberships = (withObservation > 0 ? 1 : 0) + (servedObserved > 0 ? 1 : 0),
            ClaimsBytesOrOperations = true,
        };

        int completedCalls = calls.Count(static call => call.State == ComCallState.Completed);
        int agreeing = calls.Count(static call => call.State == ComCallState.Completed && call.TruthHresult is { } expected && call.Hresult == expected);
        int carried = calls.Count(static call => call.Carrier == ComCarrierState.Carried);
        int linkedToServed = calls.Count(static call => call.LinkedToItsServedCall);
        int carriersServed = calls.Count(static call => call.CarrierServedThroughCom);
        int beyondTruth = inScope.Count(call => !matched.Contains(call.Ordinal));
        int byRank = clientByRank.Count + serverByRank.Count;
        var notes = new List<string>
        {
            $"{byRank} of {issued.Length + served.Length} truth records were the call of their end of the same rank, at the "
            + "client its client calls and at the server its server calls on the interface and method the workload called; the rest "
            + $"were matched to their end's nearest such call inside a {settings.MatchWindow.TotalMilliseconds.ToString("N0", CultureInfo.InvariantCulture)} ms window.",
            $"The capture's calibrated clock read {TimeSpan.FromTicks(offset).TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} ms ahead "
            + "of the truth's, the median distance of the ranked pairs; the records matched by nearness were matched on the "
            + "truth's time moved by it.",
            "A COM call names its interface and method, never its object: it is bound to the interface, which has no create "
            + "or destroy record, so the resource discovery criterion cannot be satisfied from this source.",
            "A COM call carries no size, so COM yields calls and never a byte volume; the RPC call carrying it adds none either "
            + "(P4, I11).",
            $"{completedCalls} of {calls.Count} truth calls were a client call whose start and stop paired, {agreeing} of them "
            + "with the HRESULT the client logged; "
            + $"{carried} were carried by one RPC call, and {carriersServed} of those RPC calls were served through it by an RPC "
            + "call of the truth's server.",
            $"{servedObserved} of {served.Length} calls the server logged serving were a server call of its process on the "
            + $"interface and method, and {linkedToServed} client calls were linked to the server call of their own truth call.",
        };
        if (beyondTruth > 0)
        {
            notes.Add($"{beyondTruth} of the workload's COM calls matched no truth record: calls the truth does not log, such as "
                + "the client's lookup of a method's name through IDispatch before it calls the method, and COM's own calls "
                + "asking the object for an interface and adding and releasing its references.");
        }

        if (outsideScope > 0)
        {
            notes.Add($"{outsideScope} COM calls belonged to processes outside this fixture. A capture of the whole machine is "
                + "expected to see them; they are scope, not error.");
        }

        return new()
        {
            Measurement = measurement,
            Assessment = CoverageTierCalculator.Assess(measurement),
            Calls = calls,
            TruthCallsServed = served.Length,
            ServedCallsObserved = servedObserved,
            CallsCompleted = completedCalls,
            HresultsAgreeing = agreeing,
            CallsCarried = carried,
            LinkedToTheirServedCall = linkedToServed,
            CarriersServedThroughCom = carriersServed,
            CallsBeyondTruth = beyondTruth,
            ObservationsOutsideScope = outsideScope,
            CallsPairedByRank = byRank,
            ClockOffset = TimeSpan.FromTicks(offset),
            Notes = notes,
        };
    }

    /// <summary>
    /// A status the workload logged, as the HRESULT a COM call's stop reports it: its 32 bits read unsigned, as the capture
    /// keeps them - S_OK or S_FALSE by name, or its value in hexadecimal or decimal, a negative one as its bits; null for
    /// none or one read as neither.
    /// </summary>
    public static long? HresultOf(string? status) => status switch
    {
        null => null,
        "S_OK" => 0,
        "S_FALSE" => 1,
        _ when status.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(status.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint hex) => hex,
        _ when int.TryParse(status, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value) => unchecked((uint)value),
        _ when uint.TryParse(status, NumberStyles.None, CultureInfo.InvariantCulture, out uint value) => value,
        _ => null,
    };

    private static bool Called(ComCallObservation call, ComCallCoverageSettings settings) =>
        call.Interface == settings.ExpectedInterface && call.Method == settings.ExpectedMethod && call.StartUtcTicks is not null;

    // An end's truth records against its calls, both in their own order: paired by rank when they are as many and every
    // pair lies inside the window, which no clock reorders.
    private static Dictionary<TruthRecord, ComCallObservation> ByRank(
        TruthRecord[] records,
        ComCallObservation[] calls,
        long window,
        List<long> distances)
    {
        var paired = new Dictionary<TruthRecord, ComCallObservation>(ReferenceEqualityComparer.Instance);
        if (records.Length == 0
            || records.Length != calls.Length
            || records.Where((record, index) => Math.Abs(calls[index].StartUtcTicks!.Value - record.RecordedUtc.UtcTicks) > window).Any())
        {
            return paired;
        }

        for (int index = 0; index < records.Length; index++)
        {
            paired[records[index]] = calls[index];
            distances.Add(calls[index].StartUtcTicks!.Value - records[index].RecordedUtc.UtcTicks);
        }

        return paired;
    }

    // Every record paired by rank keeps its call; each other is its end's nearest call no record holds yet, inside the
    // window, on the truth's time moved by the ranked pairs' offset.
    private static Dictionary<TruthRecord, ComCallObservation> Nearest(
        TruthRecord[] records,
        ComCallObservation[] calls,
        Dictionary<TruthRecord, ComCallObservation> byRank,
        long window,
        long offset,
        HashSet<long> matched)
    {
        var matches = new Dictionary<TruthRecord, ComCallObservation>(byRank, ReferenceEqualityComparer.Instance);
        foreach (ComCallObservation call in byRank.Values)
        {
            matched.Add(call.Ordinal);
        }

        foreach (TruthRecord record in records.OrderBy(static record => record.RecordedUtc.UtcTicks).ThenBy(static record => record.Sequence))
        {
            if (matches.ContainsKey(record))
            {
                continue;
            }

            long at = record.RecordedUtc.UtcTicks + offset;
            ComCallObservation? nearest = null;
            long nearestDistance = long.MaxValue;
            foreach (ComCallObservation candidate in calls)
            {
                long distance = Math.Abs(candidate.StartUtcTicks!.Value - at);
                if (matched.Contains(candidate.Ordinal) || distance > window || distance >= nearestDistance)
                {
                    continue;
                }

                nearest = candidate;
                nearestDistance = distance;
            }

            if (nearest is not null)
            {
                matches[record] = nearest;
                matched.Add(nearest.Ordinal);
            }
        }

        return matches;
    }
}
