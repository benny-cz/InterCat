using InterCat.Domain;

namespace InterCat.Analysis;

/// <summary>Settings an RPC evaluation ran under, recorded so a result can be reproduced (I16).</summary>
public sealed record RpcCoverageSettings
{
    public TimeSpan MatchWindow { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The interface the workload called through the documented Windows API.</summary>
    public required Guid ExpectedInterface { get; init; }

    public required int ClientProcessId { get; init; }

    /// <summary>The process hosting the server the workload called, recorded by the workload, never guessed.</summary>
    public int ExpectedServerProcessId { get; init; }

    public required string FixtureId { get; init; }
    public required string BuildId { get; init; }
    public required bool BuildIsSupported { get; init; }
    public bool Reproduced { get; init; }
}

/// <summary>One truth call and what the capture showed for it.</summary>
public sealed record RpcCallCoverage(
    long? CallId,
    int ProcessId,
    int MatchedStarts,
    Guid? ObservedInterface,
    long? ProcedureNumber,
    long? Protocol,
    bool BoundToInterface,
    bool CompletionPaired,
    long? Status,
    string? Gap);

/// <summary>A client-to-server pairing the capture proposed, checked against the workload's record.</summary>
public sealed record RpcPeerAttributionCheck(
    Guid ActivityId,
    int ClientProcessId,
    int ServerProcessId,
    bool AgreesWithTruth,
    string? Detail);

/// <summary>The evaluation result: the section 14.2 counters plus the evidence behind every one of them.</summary>
public sealed record RpcCoverageResult
{
    public required MechanismMeasurement Measurement { get; init; }
    public required TierAssessment Assessment { get; init; }
    public required IReadOnlyList<RpcCallCoverage> Calls { get; init; }
    public required IReadOnlyList<RpcPeerAttributionCheck> PeerAttributions { get; init; }

    /// <summary>Client calls that were paired with a completion through the activity id.</summary>
    public required int CompletionsPaired { get; init; }

    /// <summary>Server-side call records observed at all, whatever process they belong to.</summary>
    public required int ServerSideRecords { get; init; }

    /// <summary>Records from the client process that belong to another interface. Scope, not error.</summary>
    public required int ObservationsOutsideScope { get; init; }

    /// <summary>Control: admitted records from the fixture's own process, whatever their interface.</summary>
    public required int ControlRecordsFromFixtureProcess { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// Compares a local RPC truth log with admitted call records and computes the section 14.2 counters.
/// This source exposes no size on any descriptor, so no byte value is produced and the byte criterion is
/// reported as not measured rather than as a measured zero (R3, P1, P2).
/// </summary>
public static class RpcCoverageEvaluator
{
    public static RpcCoverageResult Evaluate(
        IReadOnlyList<TruthRecord> truth,
        IReadOnlyList<RpcCallObservation> observations,
        RpcCoverageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(truth);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(settings);

        List<RpcCallObservation> ordered = [.. observations
            .OrderBy(static item => item.TimestampUtcTicks)
            .ThenBy(static item => item.Id.RawRecordId.RecordOrdinal)];

        // A completion carries only a status, so the activity id is the only link back to its call (P8).
        Dictionary<Guid, RpcCallObservation> pendingByActivity = [];
        Dictionary<ObservationId, RpcCallObservation> completionByCall = [];
        foreach (RpcCallObservation observation in ordered)
        {
            if (observation.Kind == ObservationKind.RequestStart && observation.ActivityId != Guid.Empty)
            {
                pendingByActivity[observation.ActivityId] = observation;
                continue;
            }

            if (observation.Kind == ObservationKind.RequestEnd
                && observation.ActivityId != Guid.Empty
                && pendingByActivity.Remove(observation.ActivityId, out RpcCallObservation? started))
            {
                completionByCall[started.Id] = observation;
            }
        }

        var clientStarts = new List<RpcCallObservation>();
        var serverStarts = new List<RpcCallObservation>();
        int outsideScope = 0;
        int control = 0;
        foreach (RpcCallObservation observation in ordered)
        {
            if (observation.ProcessId == settings.ClientProcessId)
            {
                control++;
            }

            if (observation.Kind != ObservationKind.RequestStart)
            {
                continue;
            }

            if (observation.Direction == Direction.Inbound)
            {
                serverStarts.Add(observation);
                continue;
            }

            if (observation.ProcessId != settings.ClientProcessId)
            {
                continue;
            }

            if (observation.InterfaceUuid == settings.ExpectedInterface)
            {
                clientStarts.Add(observation);
            }
            else
            {
                outsideScope++;
            }
        }

        var calls = new List<RpcCallCoverage>();
        var matched = new HashSet<ObservationId>();
        long window = settings.MatchWindow.Ticks;
        int completionsPaired = 0;

        foreach (TruthRecord record in truth
            .OrderBy(static record => record.RecordedUtc.UtcTicks)
            .ThenBy(static record => record.Sequence))
        {
            if (record.Kind != TruthEventKind.CallIssued)
            {
                continue;
            }

            long at = record.RecordedUtc.UtcTicks;
            RpcCallObservation? best = null;
            long bestDistance = long.MaxValue;
            foreach (RpcCallObservation candidate in clientStarts)
            {
                if (matched.Contains(candidate.Id))
                {
                    continue;
                }

                long distance = Math.Abs(candidate.TimestampUtcTicks - at);
                if (distance > window || distance >= bestDistance)
                {
                    continue;
                }

                best = candidate;
                bestDistance = distance;
            }

            bool paired = false;
            long? status = null;
            if (best is not null)
            {
                matched.Add(best.Id);
                paired = completionByCall.TryGetValue(best.Id, out RpcCallObservation? completion);
                status = completion?.Status;
                if (paired)
                {
                    completionsPaired++;
                }
            }

            calls.Add(new(
                record.CallId,
                record.ProcessId,
                best is null ? 0 : 1,
                best?.InterfaceUuid,
                best?.ProcedureNumber,
                best?.Protocol,
                best?.InterfaceUuid == settings.ExpectedInterface,
                paired,
                status,
                best is null
                    ? "No admitted client call to this interface was found inside the match window."
                    : paired
                        ? null
                        : "Observed, but no completion paired through the activity id, so the status stays unknown."));
        }

        List<RpcPeerAttributionCheck> peers = EvaluatePeers(clientStarts, serverStarts, matched, settings);

        long withObservation = 0;
        long bound = 0;
        foreach (RpcCallCoverage call in calls)
        {
            if (call.MatchedStarts > 0)
            {
                withObservation++;
            }

            if (call.BoundToInterface)
            {
                bound++;
            }
        }

        long falsePeers = 0;
        foreach (RpcPeerAttributionCheck peer in peers)
        {
            if (!peer.AgreesWithTruth)
            {
                falsePeers++;
            }
        }

        var measurement = new MechanismMeasurement
        {
            FixtureId = settings.FixtureId,
            BuildId = settings.BuildId,
            Reproduced = settings.Reproduced,
            MeasuredOnSupportedBuild = settings.BuildIsSupported,
            TruthOperations = calls.Count,
            TruthOperationsWithAdmittedObservation = withObservation,
            TruthOperationsBoundToResourceInstance = bound,
            PeerAttributions = peers.Count,
            FalsePeerAttributions = falsePeers,

            // No descriptor of this source carries a size, so no operation is byte eligible. The criterion
            // is reported as not measured, which is different from measuring zero bytes (R3, P1).
            ByteEligibleOperations = 0,
            ByteMeasuredOperations = 0,

            TruthResources = 1,

            // An interface has no create or destroy record here, so no lifetime can be shown for it.
            DiscoveredResourcesWithLifetime = 0,
            ExpectedMemberships = 2,
            ResolvedMemberships = ResolveMemberships(clientStarts, serverStarts, settings),
            ClaimsBytesOrOperations = true,
        };

        var notes = new List<string>
        {
            $"Calls were matched inside a {settings.MatchWindow.TotalMilliseconds:N0} ms window on the client "
            + "process and the expected interface, and completions were paired by activity id, not by time (P8).",
            "This source carries no size on any descriptor, so RPC yields operations and never a byte volume. "
            + "An RPC annotation over a transport therefore adds no bytes to it (I11, P4).",
            "An interface is not a lifetime: no create or destroy record exists for it, so the resource "
            + "discovery criterion cannot be satisfied from this source alone.",
            control > 0
                ? $"Control: this source admitted {control} records from the fixture's own process during the "
                    + "same capture."
                : "Control: this source admitted no record from the fixture's own process, so the measurement "
                    + "cannot distinguish an absent mechanism from an inactive source.",
        };

        if (outsideScope > 0)
        {
            notes.Add(
                $"{outsideScope} client calls targeted other interfaces. A process makes RPC calls Windows "
                + "initiates as well as the ones the fixture asked for; that is scope, not error.");
        }

        notes.Add(serverStarts.Count > 0
            ? $"{serverStarts.Count} server-side call records were observed, so both sides of a local call can reach the capture."
            : "No server-side call record was observed, so only the calling side of a local call is visible here.");

        if (serverStarts.Count > 0 && peers.Count == 0)
        {
            notes.Add(
                "No client call could be paired with a server call: the two sides carry different activity "
                + "ids in this capture, so a peer is left unresolved rather than inferred from timing (P7, P8).");
        }

        return new()
        {
            Measurement = measurement,
            Assessment = CoverageTierCalculator.Assess(measurement),
            Calls = calls,
            PeerAttributions = peers,
            CompletionsPaired = completionsPaired,
            ServerSideRecords = serverStarts.Count,
            ObservationsOutsideScope = outsideScope,
            ControlRecordsFromFixtureProcess = control,
            Notes = notes,
        };
    }

    private static List<RpcPeerAttributionCheck> EvaluatePeers(
        List<RpcCallObservation> clientStarts,
        List<RpcCallObservation> serverStarts,
        HashSet<ObservationId> matched,
        RpcCoverageSettings settings)
    {
        Dictionary<Guid, RpcCallObservation> serverByActivity = [];
        foreach (RpcCallObservation server in serverStarts)
        {
            if (server.ActivityId != Guid.Empty)
            {
                serverByActivity[server.ActivityId] = server;
            }
        }

        var peers = new List<RpcPeerAttributionCheck>();
        foreach (RpcCallObservation client in clientStarts)
        {
            if (!matched.Contains(client.Id)
                || client.ActivityId == Guid.Empty
                || !serverByActivity.TryGetValue(client.ActivityId, out RpcCallObservation? server)
                || server.ProcessId == client.ProcessId)
            {
                continue;
            }

            bool agrees = settings.ExpectedServerProcessId != 0
                && server.ProcessId == settings.ExpectedServerProcessId;
            peers.Add(new(
                client.ActivityId,
                client.ProcessId,
                server.ProcessId,
                agrees,
                agrees
                    ? "The observed server process is the one the workload recorded as the callee."
                    : "The observed server process is not the one the workload recorded as the callee."));
        }

        return peers;
    }

    private static long ResolveMemberships(
        List<RpcCallObservation> clientStarts,
        List<RpcCallObservation> serverStarts,
        RpcCoverageSettings settings)
    {
        long resolved = clientStarts.Count > 0 ? 1 : 0;
        foreach (RpcCallObservation server in serverStarts)
        {
            if (server.InterfaceUuid == settings.ExpectedInterface)
            {
                resolved++;
                break;
            }
        }

        return resolved;
    }
}
