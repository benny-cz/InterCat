using InterCat.Domain;

namespace InterCat.Analysis;

/// <summary>Settings a coverage evaluation ran under, recorded so a result can be reproduced (I16).</summary>
public sealed record TcpCoverageSettings
{
    /// <summary>How far an admitted observation may sit from the truth record it answers.</summary>
    public TimeSpan MatchWindow { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The loopback address both processes used, as a host-order IPv4 value.</summary>
    public uint LoopbackAddress { get; init; } = 0x7F00_0001;

    public required string FixtureId { get; init; }
    public required string BuildId { get; init; }
    public required bool BuildIsSupported { get; init; }
    public bool Reproduced { get; init; }
}

/// <summary>One truth operation and what the capture did or did not show for it.</summary>
public sealed record OperationCoverage(
    string Role,
    int ProcessId,
    long? CallId,
    ObservationKind ExpectedKind,
    FlowKey Flow,
    long? DeclaredBytes,
    long? CompletedBytes,
    int MatchedObservations,
    long? ObservedBytes,
    bool BoundToFlowInstance,
    string? Gap);

/// <summary>A peer pairing the capture proposed, checked against the truth log.</summary>
public sealed record PeerAttributionCheck(
    FlowKey Flow,
    int ObservedProcessId,
    int ObservedPeerProcessId,
    bool AgreesWithTruth,
    string? Detail);

/// <summary>The evaluation result: the section 14.2 counters plus the evidence behind every one of them.</summary>
public sealed record TcpCoverageResult
{
    public required MechanismMeasurement Measurement { get; init; }
    public required TierAssessment Assessment { get; init; }
    public required IReadOnlyList<OperationCoverage> Operations { get; init; }
    public required IReadOnlyList<PeerAttributionCheck> PeerAttributions { get; init; }

    /// <summary>Truth bytes the workload reported as completed, by observation side.</summary>
    public required long TruthBytesSent { get; init; }

    public required long TruthBytesReceived { get; init; }

    /// <summary>Observed transport bytes on the same flows. Compared as evidence, never as a threshold.</summary>
    public required long ObservedBytesSent { get; init; }

    public required long ObservedBytesReceived { get; init; }

    /// <summary>Admitted observations that fell outside every truth flow. They are scope, not error.</summary>
    public required int ObservationsOutsideScope { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// Compares an independent TCP or UDP truth log with admitted observations and computes the section 14.2 counters.
/// Observation flows are the owner's own pair, read through each descriptor's measured orientation. It is pure and
/// portable: the same evaluation runs in a test with synthetic inputs (R19, R18).
/// </summary>
public static class TcpCoverageEvaluator
{
    public static TcpCoverageResult Evaluate(
        IReadOnlyList<TruthRecord> truth,
        IReadOnlyList<NetworkTransferObservation> observations,
        TcpCoverageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(truth);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(settings);

        long window = settings.MatchWindow.Ticks;
        Dictionary<FlowKey, List<NetworkTransferObservation>> byFlow = [];
        foreach (NetworkTransferObservation observation in observations)
        {
            if (!byFlow.TryGetValue(observation.Flow, out List<NetworkTransferObservation>? list))
            {
                byFlow[observation.Flow] = list = [];
            }

            list.Add(observation);
        }

        var operations = new List<OperationCoverage>();
        HashSet<FlowKey> truthFlows = [];
        HashSet<int> truthProcesses = [];
        Dictionary<FlowKey, int> truthFlowOwner = [];
        long truthBytesSent = 0;
        long truthBytesReceived = 0;
        long observedBytesSent = 0;
        long observedBytesReceived = 0;
        var matchedObservationIds = new HashSet<ObservationId>();

        foreach (TruthRecord record in truth
            .OrderBy(static record => record.RecordedUtc.UtcTicks)
            .ThenBy(static record => record.ProcessId)
            .ThenBy(static record => record.Sequence))
        {
            if (record.LocalPort is null || record.RemotePort is null)
            {
                continue;
            }

            var flow = new FlowKey(
                settings.LoopbackAddress,
                record.LocalPort.Value,
                settings.LoopbackAddress,
                record.RemotePort.Value);
            truthFlows.Add(flow);
            truthProcesses.Add(record.ProcessId);
            truthFlowOwner[flow] = record.ProcessId;

            if (record.Kind is not (TruthEventKind.MessageSent or TruthEventKind.MessageReceived))
            {
                continue;
            }

            ObservationKind expected = record.Kind == TruthEventKind.MessageSent
                ? ObservationKind.Send
                : ObservationKind.Receive;
            if (record.Kind == TruthEventKind.MessageSent)
            {
                truthBytesSent += record.CompletedBytes ?? 0;
            }
            else
            {
                truthBytesReceived += record.CompletedBytes ?? 0;
            }

            long at = record.RecordedUtc.UtcTicks;
            int matched = 0;
            long? observedBytes = null;
            bool boundToInstance = false;
            if (byFlow.TryGetValue(flow, out List<NetworkTransferObservation>? candidates))
            {
                NetworkTransferObservation? nearest = null;
                Int128 nearestDistance = Int128.MaxValue;
                foreach (NetworkTransferObservation candidate in candidates)
                {
                    if (candidate.Kind != expected
                        || candidate.OwnerProcessId != record.ProcessId
                        || matchedObservationIds.Contains(candidate.Id))
                    {
                        continue;
                    }

                    Int128 distance = Int128.Abs((Int128)candidate.TimestampUtcTicks - at);
                    if (distance > window
                        || distance > nearestDistance
                        || (distance == nearestDistance
                            && nearest is not null
                            && candidate.Id.RawRecordId.RecordOrdinal >= nearest.Id.RawRecordId.RecordOrdinal))
                    {
                        continue;
                    }

                    nearest = candidate;
                    nearestDistance = distance;
                }

                if (nearest is not null)
                {
                    matched = 1;
                    matchedObservationIds.Add(nearest.Id);
                    boundToInstance = nearest.Flow.LocalPort != 0 && nearest.Flow.RemotePort != 0;
                    observedBytes = nearest.ByteCount;
                }
            }

            // An operation seen only with its endpoints mirrored means the descriptor's declared orientation is wrong,
            // which is a different finding from an operation the capture never saw.
            bool mirroredOnly = matched == 0
                && byFlow.TryGetValue(flow.Mirror(), out List<NetworkTransferObservation>? mirrored)
                && mirrored.Any(candidate => candidate.Kind == expected
                    && candidate.OwnerProcessId == record.ProcessId
                    && Int128.Abs((Int128)candidate.TimestampUtcTicks - at) <= window);
            operations.Add(new(
                record.Role,
                record.ProcessId,
                record.CallId,
                expected,
                flow,
                record.DeclaredBytes,
                record.CompletedBytes,
                matched,
                observedBytes,
                boundToInstance,
                matched == 0
                    ? mirroredOnly
                        ? "Observed only with its endpoints mirrored: the descriptor's measured orientation does not hold here."
                        : "No admitted observation of this kind was found for this flow inside the match window."
                    : observedBytes is null
                        ? "Observed, but no byte measurement accompanied the record."
                        : null));
        }

        foreach (KeyValuePair<FlowKey, List<NetworkTransferObservation>> entry in byFlow)
        {
            if (!truthFlows.Contains(entry.Key))
            {
                continue;
            }

            foreach (NetworkTransferObservation observation in entry.Value)
            {
                if (observation.ByteCount is null)
                {
                    continue;
                }

                if (observation.Kind == ObservationKind.Send)
                {
                    observedBytesSent += observation.ByteCount.Value;
                }
                else if (observation.Kind == ObservationKind.Receive)
                {
                    observedBytesReceived += observation.ByteCount.Value;
                }
            }
        }

        int outsideScope = 0;
        foreach (NetworkTransferObservation observation in observations)
        {
            if (!truthFlows.Contains(observation.Flow))
            {
                outsideScope++;
            }
        }

        List<PeerAttributionCheck> peers = EvaluatePeers(byFlow, truthFlows, truthFlowOwner);
        (long resources, long discovered, long memberships, long resolved) = EvaluateTopology(byFlow, truthFlows, truth);

        long withObservation = 0;
        long bound = 0;
        long byteEligible = 0;
        long byteMeasured = 0;
        foreach (OperationCoverage operation in operations)
        {
            byteEligible++;
            if (operation.MatchedObservations > 0)
            {
                withObservation++;
            }

            if (operation.BoundToFlowInstance)
            {
                bound++;
            }

            if (operation.ObservedBytes is not null)
            {
                byteMeasured++;
            }
        }

        long falsePeers = 0;
        foreach (PeerAttributionCheck peer in peers)
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
            TruthOperationsBoundToResourceInstance = bound,
            PeerAttributions = peers.Count,
            FalsePeerAttributions = falsePeers,
            ByteEligibleOperations = byteEligible,
            ByteMeasuredOperations = byteMeasured,
            TruthResources = resources,
            DiscoveredResourcesWithLifetime = discovered,
            ExpectedMemberships = memberships,
            ResolvedMemberships = resolved,
            ClaimsBytesOrOperations = true,
        };

        var notes = new List<string>
        {
            $"Operations were matched inside a {settings.MatchWindow.TotalMilliseconds:N0} ms window on the same flow, "
            + "owner process and observation kind.",
            "Transport byte totals are reported beside truth totals as evidence; they are different byte domains "
            + "and are never summed or substituted (I6, P3).",
        };

        if (outsideScope > 0)
        {
            notes.Add(
                $"{outsideScope} admitted observations belonged to flows outside this fixture. Whole-machine capture "
                + "is expected to see unrelated traffic; it is scope, not error.");
        }

        return new()
        {
            Measurement = measurement,
            Assessment = CoverageTierCalculator.Assess(measurement),
            Operations = operations,
            PeerAttributions = peers,
            TruthBytesSent = truthBytesSent,
            TruthBytesReceived = truthBytesReceived,
            ObservedBytesSent = observedBytesSent,
            ObservedBytesReceived = observedBytesReceived,
            ObservationsOutsideScope = outsideScope,
            Notes = notes,
        };
    }

    /// <summary>
    /// Returns only observations on endpoint pairs declared by the independent truth log. Raw whole-machine
    /// observations outside the fixture scope can contain unrelated endpoints and must never be persisted as
    /// a shareable fixture artifact (section 13.6, P16).
    /// </summary>
    public static IReadOnlyList<NetworkTransferObservation> SelectFixtureEvidence(
        IReadOnlyList<TruthRecord> truth,
        IReadOnlyList<NetworkTransferObservation> observations,
        TcpCoverageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(truth);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(settings);

        HashSet<FlowKey> truthFlows = [];
        foreach (TruthRecord record in truth)
        {
            if (record.LocalPort is null || record.RemotePort is null)
            {
                continue;
            }

            truthFlows.Add(new(
                settings.LoopbackAddress,
                record.LocalPort.Value,
                settings.LoopbackAddress,
                record.RemotePort.Value));
        }

        var scoped = new List<NetworkTransferObservation>();
        foreach (NetworkTransferObservation observation in observations)
        {
            if (truthFlows.Contains(observation.Flow))
            {
                scoped.Add(observation);
            }
        }

        return scoped;
    }

    private static List<PeerAttributionCheck> EvaluatePeers(
        Dictionary<FlowKey, List<NetworkTransferObservation>> byFlow,
        HashSet<FlowKey> truthFlows,
        Dictionary<FlowKey, int> truthFlowOwner)
    {
        var peers = new List<PeerAttributionCheck>();
        foreach (FlowKey flow in truthFlows)
        {
            if (!byFlow.TryGetValue(flow, out List<NetworkTransferObservation>? near)
                || !byFlow.TryGetValue(flow.Mirror(), out List<NetworkTransferObservation>? far))
            {
                continue;
            }

            int? nearOwner = FirstOwner(near);
            int? farOwner = FirstOwner(far);
            if (nearOwner is null || farOwner is null || nearOwner == farOwner)
            {
                continue;
            }

            bool agrees = truthFlowOwner.TryGetValue(flow, out int truthNear)
                && truthFlowOwner.TryGetValue(flow.Mirror(), out int truthFar)
                && truthNear == nearOwner.Value
                && truthFar == farOwner.Value;

            peers.Add(new(
                flow,
                nearOwner.Value,
                farOwner.Value,
                agrees,
                agrees
                    ? "Both endpoints of this connection were attributed to the processes the truth log names."
                    : "The observed endpoint owners do not match the truth log for this connection."));
        }

        return peers;
    }

    private static (long Resources, long Discovered, long Memberships, long Resolved) EvaluateTopology(
        Dictionary<FlowKey, List<NetworkTransferObservation>> byFlow,
        HashSet<FlowKey> truthFlows,
        IReadOnlyList<TruthRecord> truth)
    {
        HashSet<FlowKey> established = [];
        foreach (TruthRecord record in truth)
        {
            if (record.Kind == TruthEventKind.ConnectionEstablished
                && record.LocalPort is not null
                && record.RemotePort is not null)
            {
                established.Add(new(0, record.LocalPort.Value, 0, record.RemotePort.Value));
            }
        }

        long discovered = 0;
        long resolved = 0;
        foreach (FlowKey flow in truthFlows)
        {
            bool portsMatchAnEstablishedConnection = established.Contains(new(0, flow.LocalPort, 0, flow.RemotePort));
            if (!portsMatchAnEstablishedConnection)
            {
                continue;
            }

            bool sawLifecycle = false;
            if (byFlow.TryGetValue(flow, out List<NetworkTransferObservation>? near))
            {
                foreach (NetworkTransferObservation observation in near)
                {
                    sawLifecycle |= observation.Kind
                        is ObservationKind.Connect or ObservationKind.Accept or ObservationKind.Disconnect;
                }
            }

            if (sawLifecycle)
            {
                discovered++;
            }

            if (byFlow.ContainsKey(flow) && byFlow.ContainsKey(flow.Mirror()))
            {
                resolved += 2;
            }
            else if (byFlow.ContainsKey(flow))
            {
                resolved += 1;
            }
        }

        long resources = established.Count;
        return (resources, discovered, resources * 2, resolved);
    }

    private static int? FirstOwner(List<NetworkTransferObservation> observations)
    {
        foreach (NetworkTransferObservation observation in observations)
        {
            if (observation.OwnerProcessId is not null)
            {
                return observation.OwnerProcessId;
            }
        }

        return null;
    }
}
