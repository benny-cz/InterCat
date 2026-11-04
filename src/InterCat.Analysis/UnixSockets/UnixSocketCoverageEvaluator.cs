using InterCat.Domain;

namespace InterCat.Analysis;

/// <summary>Settings an AF_UNIX coverage evaluation ran under, recorded so a result can be reproduced (I16).</summary>
public sealed record UnixSocketCoverageSettings
{
    /// <summary>How far an admitted row may sit from the truth record it answers.</summary>
    public TimeSpan MatchWindow { get; init; } = TimeSpan.FromMilliseconds(500);

    public required string FixtureId { get; init; }
    public required string BuildId { get; init; }
    public required bool BuildIsSupported { get; init; }
    public bool Reproduced { get; init; }
}

/// <summary>One truth operation and what the capture showed for it.</summary>
public sealed record UnixSocketOperationCoverage(
    string Role,
    int ProcessId,
    long? CallId,
    ObservationKind ExpectedKind,
    long? TruthCompletedBytes,
    int MatchedObservations,
    long? Socket,
    long? ObservedBytes,
    string? Gap);

/// <summary>What the capture followed of one truth process's sockets.</summary>
public sealed record UnixSocketProcessCoverage(
    string Role,
    int ProcessId,
    int TruthConnections,
    int SocketsWithMatchedTransfers,
    int SocketsCreatedAndClosed);

/// <summary>The evaluation result: the section 14.2 counters plus the evidence behind every one of them.</summary>
public sealed record UnixSocketCoverageResult
{
    public required MechanismMeasurement Measurement { get; init; }
    public required TierAssessment Assessment { get; init; }
    public required IReadOnlyList<UnixSocketOperationCoverage> Operations { get; init; }
    public required IReadOnlyList<UnixSocketProcessCoverage> Processes { get; init; }

    /// <summary>Bytes the workload reported as completed, by observation side.</summary>
    public required long TruthBytesSent { get; init; }

    public required long TruthBytesReceived { get; init; }

    /// <summary>Bytes every send and receive row of the workload's processes completed, matched or not.</summary>
    public required long ObservedBytesSent { get; init; }

    public required long ObservedBytesReceived { get; init; }

    /// <summary>
    /// The workload's transfer rows no truth operation matched: a stream's reader may take one message in several calls,
    /// each a row, which the truth counts once.
    /// </summary>
    public required int TransfersBeyondTruth { get; init; }

    /// <summary>Rows of processes the truth does not name. A capture of the whole machine sees them; they are scope, not error.</summary>
    public required int ObservationsOutsideScope { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// Compares an independent AF_UNIX truth log with the Unix-domain socket rows a capture admitted through Winsock AFD's
/// gate (ADR-073, ADR-074) and computes the section 14.2 counters. A row is its socket's owner's and is matched by owner,
/// kind and time, never by the thread that raised it. AF_UNIX names no peer, so the capture attributes none: no peer
/// attribution is counted, and each socket's peer is a membership left unresolved. It is pure and portable: the same
/// evaluation runs in a test on committed evidence (R19, R18).
/// </summary>
public static class UnixSocketCoverageEvaluator
{
    public static UnixSocketCoverageResult Evaluate(
        IReadOnlyList<TruthRecord> truth,
        IReadOnlyList<UnixSocketObservation> observations,
        UnixSocketCoverageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(truth);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(settings);

        Dictionary<int, string> roles = [];
        foreach (TruthRecord record in truth)
        {
            roles.TryAdd(record.ProcessId, record.Role);
        }

        List<UnixSocketObservation> inScope = [];
        int outsideScope = 0;
        foreach (UnixSocketObservation observation in observations
            .OrderBy(static item => item.TimestampUtcTicks)
            .ThenBy(static item => item.RecordOrdinal))
        {
            if (roles.ContainsKey(observation.OwnerProcessId))
            {
                inScope.Add(observation);
            }
            else
            {
                outsideScope++;
            }
        }

        long window = settings.MatchWindow.Ticks;
        var matched = new HashSet<long>();
        var operations = new List<UnixSocketOperationCoverage>();
        long truthBytesSent = 0;
        long truthBytesReceived = 0;
        foreach (TruthRecord record in truth
            .OrderBy(static record => record.RecordedUtc.UtcTicks)
            .ThenBy(static record => record.ProcessId)
            .ThenBy(static record => record.Sequence))
        {
            if (record.Kind is not (TruthEventKind.MessageSent or TruthEventKind.MessageReceived))
            {
                continue;
            }

            ObservationKind expected = record.Kind == TruthEventKind.MessageSent ? ObservationKind.Send : ObservationKind.Receive;
            if (expected == ObservationKind.Send)
            {
                truthBytesSent += record.CompletedBytes ?? 0;
            }
            else
            {
                truthBytesReceived += record.CompletedBytes ?? 0;
            }

            long at = record.RecordedUtc.UtcTicks;
            UnixSocketObservation? nearest = null;
            long nearestDistance = long.MaxValue;
            foreach (UnixSocketObservation candidate in inScope)
            {
                if (candidate.Kind != expected
                    || candidate.OwnerProcessId != record.ProcessId
                    || matched.Contains(candidate.RecordOrdinal))
                {
                    continue;
                }

                long distance = Math.Abs(candidate.TimestampUtcTicks - at);
                if (distance > window || distance >= nearestDistance)
                {
                    continue;
                }

                nearest = candidate;
                nearestDistance = distance;
            }

            if (nearest is not null)
            {
                matched.Add(nearest.RecordOrdinal);
            }

            operations.Add(new(
                record.Role,
                record.ProcessId,
                record.CallId,
                expected,
                record.CompletedBytes,
                nearest is null ? 0 : 1,
                nearest?.Socket,
                nearest?.Bytes,
                nearest is null
                    ? "No admitted Unix-domain socket row of this kind and owner was found inside the match window."
                    : nearest.Bytes is null
                        ? "Observed, but no byte measurement accompanied the row."
                        : null));
        }

        long observedBytesSent = 0;
        long observedBytesReceived = 0;
        int beyondTruth = 0;
        foreach (UnixSocketObservation observation in inScope)
        {
            if (observation.Kind is not (ObservationKind.Send or ObservationKind.Receive))
            {
                continue;
            }

            if (observation.Kind == ObservationKind.Send)
            {
                observedBytesSent += observation.Bytes ?? 0;
            }
            else
            {
                observedBytesReceived += observation.Bytes ?? 0;
            }

            if (!matched.Contains(observation.RecordOrdinal))
            {
                beyondTruth++;
            }
        }

        // Each truth connection is a socket of its process: one the capture followed from its create to its close, as
        // a matched transfer of that process names it. The capture names no socket's peer, so none is resolved.
        HashSet<long> created = [.. inScope.Where(static row => row.Kind == ObservationKind.Open).Select(static row => row.Socket)];
        HashSet<long> closed = [.. inScope.Where(static row => row.Kind == ObservationKind.Close).Select(static row => row.Socket)];
        var processes = new List<UnixSocketProcessCoverage>();
        long truthConnections = 0;
        long discovered = 0;
        foreach ((int processId, string role) in roles.OrderBy(static entry => entry.Value, StringComparer.Ordinal))
        {
            int connections = truth.Count(record => record.ProcessId == processId
                && record.Kind is TruthEventKind.ConnectionEstablished or TruthEventKind.ConnectionAccepted);
            HashSet<long> sockets = [.. operations
                .Where(operation => operation.ProcessId == processId && operation.Socket is not null)
                .Select(operation => operation.Socket!.Value)];
            int lived = sockets.Count(socket => created.Contains(socket) && closed.Contains(socket));
            processes.Add(new(role, processId, connections, sockets.Count, lived));
            truthConnections += connections;
            discovered += Math.Min(lived, connections);
        }

        long withObservation = operations.Count(static operation => operation.MatchedObservations > 0);
        long bound = operations.Count(static operation => operation.Socket is > 0);
        long byteMeasured = operations.Count(static operation => operation.ObservedBytes is not null);
        var measurement = new MechanismMeasurement
        {
            FixtureId = settings.FixtureId,
            BuildId = settings.BuildId,
            Reproduced = settings.Reproduced,
            MeasuredOnSupportedBuild = settings.BuildIsSupported,
            TruthOperations = operations.Count,
            TruthOperationsWithAdmittedObservation = withObservation,
            TruthOperationsBoundToResourceInstance = bound,
            PeerAttributions = 0,
            FalsePeerAttributions = 0,
            ByteEligibleOperations = operations.Count,
            ByteMeasuredOperations = byteMeasured,
            TruthResources = truthConnections,
            DiscoveredResourcesWithLifetime = discovered,
            ExpectedMemberships = truthConnections,
            ResolvedMemberships = 0,
            ClaimsBytesOrOperations = true,
        };

        var notes = new List<string>
        {
            $"Operations were matched inside a {settings.MatchWindow.TotalMilliseconds:N0} ms window on the same owner process "
            + "and observation kind; a row's owner is the process its socket's create named.",
            "AF_UNIX names no peer, and the capture attributes none: no peer attribution is counted, and each socket's peer is "
            + "left unresolved.",
            "Byte totals are reported beside the truth's as evidence, every transfer row of the workload's processes counted, "
            + "matched or not.",
        };
        if (beyondTruth > 0)
        {
            notes.Add($"{beyondTruth} of the workload's transfer rows matched no truth operation: a stream's reader may take one "
                + "message in several calls, each a row, which the truth counts once.");
        }

        if (outsideScope > 0)
        {
            notes.Add($"{outsideScope} admitted rows belonged to processes outside this fixture. A capture of the whole machine "
                + "is expected to see them; they are scope, not error.");
        }

        return new()
        {
            Measurement = measurement,
            Assessment = CoverageTierCalculator.Assess(measurement),
            Operations = operations,
            Processes = processes,
            TruthBytesSent = truthBytesSent,
            TruthBytesReceived = truthBytesReceived,
            ObservedBytesSent = observedBytesSent,
            ObservedBytesReceived = observedBytesReceived,
            TransfersBeyondTruth = beyondTruth,
            ObservationsOutsideScope = outsideScope,
            Notes = notes,
        };
    }
}
