using InterCat.Domain;

namespace InterCat.Analysis;

/// <summary>Settings a named-pipe evaluation ran under, recorded so a result can be reproduced (I16).</summary>
public sealed record PipeCoverageSettings
{
    /// <summary>How far an admitted operation may sit from the truth record it answers.</summary>
    public TimeSpan MatchWindow { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The pipe path the fixture used. Only operations resolved to this name are in scope.</summary>
    public required string PipePath { get; init; }

    public required string FixtureId { get; init; }
    public required string BuildId { get; init; }
    public required bool BuildIsSupported { get; init; }
    public bool Reproduced { get; init; }
}

/// <summary>One truth operation and what the capture showed for it.</summary>
public sealed record PipeOperationCoverage(
    string Role,
    int ProcessId,
    long? CallId,
    ObservationKind ExpectedKind,
    string? ResolvedResourceName,
    long? TruthRequestedBytes,
    long? TruthCompletedBytes,
    int MatchedObservations,
    long? ObservedRequestedBytes,
    long? ObservedCompletedBytes,
    bool BoundToResourceInstance,
    string? Gap);

/// <summary>A peer pairing on one pipe instance, checked against the truth log.</summary>
public sealed record PipePeerAttributionCheck(
    string ResourceName,
    int ObservedProcessId,
    int ObservedPeerProcessId,
    bool AgreesWithTruth,
    string? Detail);

/// <summary>The evaluation result: the section 14.2 counters plus the evidence behind every one of them.</summary>
public sealed record PipeCoverageResult
{
    public required MechanismMeasurement Measurement { get; init; }
    public required TierAssessment Assessment { get; init; }
    public required IReadOnlyList<PipeOperationCoverage> Operations { get; init; }
    public required IReadOnlyList<PipePeerAttributionCheck> PeerAttributions { get; init; }

    /// <summary>Operations whose observed requested and completed sizes differ (section 21.1, row 2).</summary>
    public required int OperationsWithRequestedAboveCompleted { get; init; }

    /// <summary>Sum of observed requested bytes on fixture operations, in the RequestedIo domain only.</summary>
    public required long ObservedRequestedBytes { get; init; }

    /// <summary>Sum of observed completed bytes on fixture operations, in the CompletedIo domain only.</summary>
    public required long ObservedCompletedBytes { get; init; }

    /// <summary>Matched operations whose completion never arrived, so completed bytes stay unknown (R3).</summary>
    public required int MatchedOperationsWithoutCompletion { get; init; }

    /// <summary>Admitted pipe operations that resolved to another resource. They are scope, not error.</summary>
    public required int ObservationsOutsideScope { get; init; }

    /// <summary>Admitted operations whose resource name could not be resolved at all.</summary>
    public required int ObservationsWithUnresolvedResource { get; init; }

    /// <summary>
    /// Admitted operations issued by the fixture's own processes that no naming record could bind to a
    /// resource. They separate "the mechanism is invisible" from "the instance cannot be named" (R21).
    /// </summary>
    public required int FixtureProcessOperationsWithoutResource { get; init; }

    /// <summary>
    /// Admitted records of any kind issued by the fixture's own processes. It is the control for a negative
    /// result: without it, "nothing was observed" cannot be told apart from "the source was never live" (R21).
    /// </summary>
    public required int ControlRecordsFromFixtureProcesses { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// Compares an independent named-pipe truth log with admitted file operations and computes the
/// section 14.2 counters. Requested and completed bytes stay in separate domains throughout (P3, I6),
/// and a completion is paired by I/O request identity rather than by time proximity (P8).
/// </summary>
public static class PipeCoverageEvaluator
{
    public static PipeCoverageResult Evaluate(
        IReadOnlyList<TruthRecord> truth,
        IReadOnlyList<PipeOperationObservation> observations,
        PipeCoverageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(truth);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(settings);

        List<PipeOperationObservation> ordered = [.. observations.OrderBy(static item => item.TimestampUtcTicks)
            .ThenBy(static item => item.Id.RawRecordId.RecordOrdinal)];

        Dictionary<ulong, string> namesByFileObject = [];
        Dictionary<ulong, string> namesByFileKey = [];
        foreach (PipeOperationObservation observation in ordered)
        {
            if (observation.ResourceName is null)
            {
                continue;
            }

            if (observation.FileObject != 0)
            {
                namesByFileObject[observation.FileObject] = observation.ResourceName;
            }

            if (observation.FileKey != 0)
            {
                namesByFileKey[observation.FileKey] = observation.ResourceName;
            }
        }

        // A completion carries the transferred size and is joined by the I/O request that produced it.
        Dictionary<ulong, PipeOperationObservation> pendingByIrp = [];
        Dictionary<ObservationId, PipeOperationObservation> completionByOperation = [];
        foreach (PipeOperationObservation observation in ordered)
        {
            if (observation.Kind is ObservationKind.Send or ObservationKind.Receive && observation.IrpKey != 0)
            {
                pendingByIrp[observation.IrpKey] = observation;
                continue;
            }

            if (observation.Kind == ObservationKind.RequestEnd
                && observation.IrpKey != 0
                && pendingByIrp.Remove(observation.IrpKey, out PipeOperationObservation? started))
            {
                completionByOperation[started.Id] = observation;
            }
        }

        HashSet<int> fixtureProcesses = [];
        foreach (TruthRecord record in truth)
        {
            fixtureProcesses.Add(record.ProcessId);
        }

        int controlRecords = 0;
        foreach (PipeOperationObservation observation in ordered)
        {
            if (fixtureProcesses.Contains(observation.ProcessId))
            {
                controlRecords++;
            }
        }

        var transfers = new List<(PipeOperationObservation Operation, string? Name, long? Completed, long? Status)>();
        int outsideScope = 0;
        int unresolved = 0;
        int fixtureProcessUnresolved = 0;
        foreach (PipeOperationObservation observation in ordered)
        {
            if (observation.Kind is not (ObservationKind.Send or ObservationKind.Receive))
            {
                continue;
            }

            string? name = ResolveName(observation, namesByFileObject, namesByFileKey);
            if (name is null)
            {
                unresolved++;
                if (fixtureProcesses.Contains(observation.ProcessId))
                {
                    fixtureProcessUnresolved++;
                }

                continue;
            }

            if (!string.Equals(name, settings.PipePath, StringComparison.OrdinalIgnoreCase))
            {
                outsideScope++;
                continue;
            }

            completionByOperation.TryGetValue(observation.Id, out PipeOperationObservation? completion);
            transfers.Add((observation, name, completion?.CompletedBytes, completion?.Status));
        }

        var operations = new List<PipeOperationCoverage>();
        var matched = new HashSet<ObservationId>();
        long window = settings.MatchWindow.Ticks;
        long observedRequested = 0;
        long observedCompleted = 0;
        int requestedAboveCompleted = 0;
        int matchedWithoutCompletion = 0;
        HashSet<int> truthProcesses = [];

        foreach (TruthRecord record in truth
            .OrderBy(static record => record.RecordedUtc.UtcTicks)
            .ThenBy(static record => record.ProcessId)
            .ThenBy(static record => record.Sequence))
        {
            truthProcesses.Add(record.ProcessId);
            if (record.Kind is not (TruthEventKind.MessageSent or TruthEventKind.MessageReceived))
            {
                continue;
            }

            ObservationKind expected = record.Kind == TruthEventKind.MessageSent
                ? ObservationKind.Send
                : ObservationKind.Receive;
            long at = record.RecordedUtc.UtcTicks;

            (PipeOperationObservation Operation, string? Name, long? Completed, long? Status)? best = null;
            long bestDistance = long.MaxValue;
            foreach ((PipeOperationObservation operation, string? name, long? completed, long? status) in transfers)
            {
                if (operation.Kind != expected
                    || operation.ProcessId != record.ProcessId
                    || matched.Contains(operation.Id))
                {
                    continue;
                }

                long distance = Math.Abs(operation.TimestampUtcTicks - at);
                if (distance > window || distance >= bestDistance)
                {
                    continue;
                }

                best = (operation, name, completed, status);
                bestDistance = distance;
            }

            int matchCount = 0;
            long? requestedBytes = null;
            long? completedBytes = null;
            bool bound = false;
            string? resolvedName = null;
            if (best is not null)
            {
                matchCount = 1;
                matched.Add(best.Value.Operation.Id);
                requestedBytes = best.Value.Operation.RequestedBytes;
                completedBytes = best.Value.Completed;
                resolvedName = best.Value.Name;
                bound = best.Value.Operation.FileObject != 0 && resolvedName is not null;
                observedRequested += requestedBytes ?? 0;
                observedCompleted += completedBytes ?? 0;
                if (completedBytes is null)
                {
                    matchedWithoutCompletion++;
                }
                else if (requestedBytes is not null && requestedBytes > completedBytes)
                {
                    requestedAboveCompleted++;
                }
            }

            operations.Add(new(
                record.Role,
                record.ProcessId,
                record.CallId,
                expected,
                resolvedName,
                record.DeclaredBytes,
                record.CompletedBytes,
                matchCount,
                requestedBytes,
                completedBytes,
                bound,
                matchCount == 0
                    ? "No admitted pipe operation of this kind resolved to this pipe inside the match window."
                    : requestedBytes is null
                        ? "Observed, but the descriptor carried no requested size."
                        : completedBytes is null
                            ? "Observed with a requested size; no completion paired, so completed bytes stay unknown."
                            : null));
        }

        (long resources, long discovered, long memberships, long resolvedMemberships, List<PipePeerAttributionCheck> peers) =
            EvaluateTopology(ordered, namesByFileObject, namesByFileKey, truthProcesses, settings);

        long withObservation = 0;
        long boundOperations = 0;
        long byteMeasured = 0;
        foreach (PipeOperationCoverage operation in operations)
        {
            if (operation.MatchedObservations > 0)
            {
                withObservation++;
            }

            if (operation.BoundToResourceInstance)
            {
                boundOperations++;
            }

            if (operation.ObservedRequestedBytes is not null || operation.ObservedCompletedBytes is not null)
            {
                byteMeasured++;
            }
        }

        long falsePeers = 0;
        foreach (PipePeerAttributionCheck peer in peers)
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
            TruthOperations = operations.Count,
            TruthOperationsWithAdmittedObservation = withObservation,
            TruthOperationsBoundToResourceInstance = boundOperations,
            PeerAttributions = peers.Count,
            FalsePeerAttributions = falsePeers,
            ByteEligibleOperations = operations.Count,
            ByteMeasuredOperations = byteMeasured,
            TruthResources = resources,
            DiscoveredResourcesWithLifetime = discovered,
            ExpectedMemberships = memberships,
            ResolvedMemberships = resolvedMemberships,
            ClaimsBytesOrOperations = true,
        };

        var notes = new List<string>
        {
            $"Operations were matched inside a {settings.MatchWindow.TotalMilliseconds:N0} ms window on the same "
            + "process, resolved pipe name and operation kind.",
            "Requested bytes come from the operation descriptor and completed bytes from its paired completion. "
            + "They are separate byte domains and are never summed or substituted (P3, I6).",
            "A pipe name is resolved only through a create this capture observed; an operation on a pipe opened "
            + "earlier stays unresolved rather than being attributed by name similarity (R22).",
        };

        if (unresolved > 0)
        {
            notes.Add($"{unresolved} admitted operations had no observed create, so their resource stayed unresolved.");
        }

        notes.Add(controlRecords > 0
            ? $"Control: this source admitted {controlRecords} records from the fixture's own processes during the "
                + "same capture, so a missing pipe operation is an absent record rather than an inactive source."
            : "Control: this source admitted no record at all from the fixture's own processes, so the measurement "
                + "cannot distinguish an absent mechanism from an inactive source.");
        notes.Add(fixtureProcessUnresolved > 0
            ? $"{fixtureProcessUnresolved} of those operations were issued by this fixture's own processes: the "
                + "activity is visible but the instance it belongs to is not named by this source."
            : "No operation from this fixture's own processes reached this source without a name, so the gap is "
                + "not a naming gap.");

        if (outsideScope > 0)
        {
            notes.Add(
                $"{outsideScope} admitted operations resolved to other files or pipes. Whole-machine file capture "
                + "is expected to see unrelated activity; it is scope, not error.");
        }

        return new()
        {
            Measurement = measurement,
            Assessment = CoverageTierCalculator.Assess(measurement),
            Operations = operations,
            PeerAttributions = peers,
            OperationsWithRequestedAboveCompleted = requestedAboveCompleted,
            ObservedRequestedBytes = observedRequested,
            ObservedCompletedBytes = observedCompleted,
            MatchedOperationsWithoutCompletion = matchedWithoutCompletion,
            ObservationsOutsideScope = outsideScope,
            ObservationsWithUnresolvedResource = unresolved,
            FixtureProcessOperationsWithoutResource = fixtureProcessUnresolved,
            ControlRecordsFromFixtureProcesses = controlRecords,
            Notes = notes,
        };
    }

    private static string? ResolveName(
        PipeOperationObservation observation,
        Dictionary<ulong, string> byFileObject,
        Dictionary<ulong, string> byFileKey)
    {
        if (observation.ResourceName is not null)
        {
            return observation.ResourceName;
        }

        if (observation.FileObject != 0 && byFileObject.TryGetValue(observation.FileObject, out string? name))
        {
            return name;
        }

        return observation.FileKey != 0 && byFileKey.TryGetValue(observation.FileKey, out string? keyed) ? keyed : null;
    }

    private static (long Resources, long Discovered, long Memberships, long Resolved, List<PipePeerAttributionCheck> Peers)
        EvaluateTopology(
            IReadOnlyList<PipeOperationObservation> ordered,
            Dictionary<ulong, string> namesByFileObject,
            Dictionary<ulong, string> namesByFileKey,
            HashSet<int> truthProcesses,
            PipeCoverageSettings settings)
    {
        bool sawOpen = false;
        bool sawClose = false;
        HashSet<int> processesOnPipe = [];
        foreach (PipeOperationObservation observation in ordered)
        {
            string? name = ResolveName(observation, namesByFileObject, namesByFileKey);
            if (name is null || !string.Equals(name, settings.PipePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sawOpen |= observation.Kind == ObservationKind.Open;
            sawClose |= observation.Kind == ObservationKind.Close;
            processesOnPipe.Add(observation.ProcessId);
        }

        var peers = new List<PipePeerAttributionCheck>();
        List<int> members = [.. processesOnPipe.Order()];
        for (int first = 0; first < members.Count; first++)
        {
            for (int second = first + 1; second < members.Count; second++)
            {
                bool agrees = truthProcesses.Contains(members[first]) && truthProcesses.Contains(members[second]);
                peers.Add(new(
                    settings.PipePath,
                    members[first],
                    members[second],
                    agrees,
                    agrees
                        ? "Both processes observed on this pipe instance are the processes the truth log names."
                        : "A process observed on this pipe instance is not one the truth log names."));
            }
        }

        long resolvedMemberships = 0;
        foreach (int member in members)
        {
            if (truthProcesses.Contains(member))
            {
                resolvedMemberships++;
            }
        }

        return (1, sawOpen && sawClose ? 1 : 0, 2, resolvedMemberships, peers);
    }
}
