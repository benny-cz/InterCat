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

/// <summary>One truth operation and what the capture showed for it: matched by its rank, by nearness, or not at all.</summary>
public sealed record UnixSocketOperationCoverage(
    string Role,
    int ProcessId,
    long Sequence,
    long? CallId,
    ObservationKind ExpectedKind,
    long? TruthCompletedBytes,
    int MatchedObservations,
    long? Socket,
    long? ObservedBytes,
    bool PairedByRank,
    string? Gap);

/// <summary>
/// What the capture followed of one truth process's sockets. When its connections never overlap in its own order, as a
/// churn's do, each operation's connection is the one whose records hold it, and <see cref="ConnectionsOnTheirOwnSocket"/>
/// counts the connections whose matched operations all landed on one socket no other connection's operation is on; null
/// when they overlap, as concurrent connections do, and an operation's connection is not known from the truth (R22).
/// </summary>
public sealed record UnixSocketProcessCoverage(
    string Role,
    int ProcessId,
    int TruthConnections,
    int SocketsWithMatchedTransfers,
    int SocketsCreatedAndClosed,
    int? ConnectionsOnTheirOwnSocket);

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

    /// <summary>The truth operations matched to the row of their owner and kind of the same rank, their bytes the row's.</summary>
    public required int OperationsPairedByRank { get; init; }

    /// <summary>
    /// How far the capture's calibrated clock read ahead of the truth's: the median distance of the operations paired by
    /// rank to their rows. The operations matched by nearness are matched on the truth's time moved by it; zero when none
    /// paired by rank.
    /// </summary>
    public required TimeSpan ClockOffset { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// Compares an independent AF_UNIX truth log with the Unix-domain socket rows a capture admitted through Winsock AFD's
/// gate (ADR-073, ADR-074) and computes the section 14.2 counters. A row is its socket's owner's, never the thread's that
/// raised it. Where a process's truth operations of one kind are as many as its rows of that kind, and the rows taken in
/// order carry exactly the bytes the operations completed in the truth's order, each operation is the row of its rank: the
/// truth's wall clock, which can read a fraction of a millisecond out of order (FX-UNIX-002), then decides nothing. Every
/// other operation is matched to its owner's nearest row of its kind, on the truth's time moved by the offset the ranked
/// pairs measured. AF_UNIX names no peer, so the capture attributes none: no peer attribution is counted, and each
/// socket's peer is a membership left unresolved. It is pure and portable: the same evaluation runs in a test on committed
/// evidence (R19, R18).
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

        TruthRecord[] messages = [.. truth.Where(static record => record.Kind is TruthEventKind.MessageSent or TruthEventKind.MessageReceived)];

        // Each process's operations of one kind against its rows of that kind, both in their own order: paired by rank
        // when they are as many, every pair's bytes agree and every pair lies inside the window, which no clock reorders.
        long window = settings.MatchWindow.Ticks;
        var paired = new Dictionary<TruthRecord, UnixSocketObservation>(ReferenceEqualityComparer.Instance);
        var matched = new HashSet<long>();
        List<long> rankedDistances = [];
        foreach (IGrouping<(int Process, ObservationKind Kind), TruthRecord> group in messages.GroupBy(record => (record.ProcessId, KindOf(record))))
        {
            TruthRecord[] operations = [.. group.OrderBy(static record => record.Sequence)];
            UnixSocketObservation[] rows = [.. inScope.Where(row => row.OwnerProcessId == group.Key.Process && row.Kind == group.Key.Kind)];
            if (operations.Length != rows.Length
                || operations.Where((operation, index) => operation.CompletedBytes is null || operation.CompletedBytes != rows[index].Bytes
                    || Math.Abs(rows[index].TimestampUtcTicks - operation.RecordedUtc.UtcTicks) > window).Any())
            {
                continue;
            }

            for (int index = 0; index < operations.Length; index++)
            {
                paired[operations[index]] = rows[index];
                matched.Add(rows[index].RecordOrdinal);
                rankedDistances.Add(rows[index].TimestampUtcTicks - operations[index].RecordedUtc.UtcTicks);
            }
        }

        rankedDistances.Sort();
        long offset = rankedDistances.Count == 0 ? 0 : rankedDistances[rankedDistances.Count / 2];

        var coverage = new List<UnixSocketOperationCoverage>();
        long truthBytesSent = 0;
        long truthBytesReceived = 0;
        foreach (TruthRecord record in messages
            .OrderBy(static record => record.RecordedUtc.UtcTicks)
            .ThenBy(static record => record.ProcessId)
            .ThenBy(static record => record.Sequence))
        {
            ObservationKind expected = KindOf(record);
            if (expected == ObservationKind.Send)
            {
                truthBytesSent += record.CompletedBytes ?? 0;
            }
            else
            {
                truthBytesReceived += record.CompletedBytes ?? 0;
            }

            bool byRank = paired.TryGetValue(record, out UnixSocketObservation? nearest);
            if (!byRank)
            {
                long at = record.RecordedUtc.UtcTicks + offset;
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
            }

            coverage.Add(new(
                record.Role,
                record.ProcessId,
                record.Sequence,
                record.CallId,
                expected,
                record.CompletedBytes,
                nearest is null ? 0 : 1,
                nearest?.Socket,
                nearest?.Bytes,
                byRank,
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
            HashSet<long> sockets = [.. coverage
                .Where(operation => operation.ProcessId == processId && operation.Socket is not null)
                .Select(operation => operation.Socket!.Value)];
            int lived = sockets.Count(socket => created.Contains(socket) && closed.Contains(socket));
            processes.Add(new(role, processId, connections, sockets.Count, lived, OnTheirOwnSocket(truth, coverage, processId)));
            truthConnections += connections;
            discovered += Math.Min(lived, connections);
        }

        long withObservation = coverage.Count(static operation => operation.MatchedObservations > 0);
        long bound = coverage.Count(static operation => operation.Socket is > 0);
        long byteMeasured = coverage.Count(static operation => operation.ObservedBytes is not null);
        int byRankCount = coverage.Count(static operation => operation.PairedByRank);
        var measurement = new MechanismMeasurement
        {
            FixtureId = settings.FixtureId,
            BuildId = settings.BuildId,
            Reproduced = settings.Reproduced,
            MeasuredOnSupportedBuild = settings.BuildIsSupported,
            TruthOperations = coverage.Count,
            TruthOperationsWithAdmittedObservation = withObservation,
            TruthOperationsBoundToResourceInstance = bound,
            PeerAttributions = 0,
            FalsePeerAttributions = 0,
            ByteEligibleOperations = coverage.Count,
            ByteMeasuredOperations = byteMeasured,
            TruthResources = truthConnections,
            DiscoveredResourcesWithLifetime = discovered,
            ExpectedMemberships = truthConnections,
            ResolvedMemberships = 0,
            ClaimsBytesOrOperations = true,
        };

        var notes = new List<string>
        {
            $"{byRankCount} of {coverage.Count} operations were the row of their owner and kind of the same rank, their bytes "
            + "the row's; the rest were matched to their owner's nearest row of their kind inside a "
            + $"{settings.MatchWindow.TotalMilliseconds:N0} ms window, a row's owner being the process its socket's create named.",
            $"The capture's calibrated clock read {TimeSpan.FromTicks(offset).TotalMilliseconds:F3} ms ahead of the truth's, the "
            + "median distance of the ranked pairs; the operations matched by nearness were matched on the truth's time moved by it.",
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
            Operations = coverage,
            Processes = processes,
            TruthBytesSent = truthBytesSent,
            TruthBytesReceived = truthBytesReceived,
            ObservedBytesSent = observedBytesSent,
            ObservedBytesReceived = observedBytesReceived,
            TransfersBeyondTruth = beyondTruth,
            ObservationsOutsideScope = outsideScope,
            OperationsPairedByRank = byRankCount,
            ClockOffset = TimeSpan.FromTicks(offset),
            Notes = notes,
        };
    }

    private static ObservationKind KindOf(TruthRecord record) =>
        record.Kind == TruthEventKind.MessageSent ? ObservationKind.Send : ObservationKind.Receive;

    /// <summary>
    /// How many of a process's connections are their own socket, when no two of them overlap in the process's own order of
    /// records - its sequence, which no clock reorders: every matched operation between a connection's open and close on
    /// one socket, which no other connection's operation is on. Null when a connection is not known whole or two overlap,
    /// so an operation's connection is not known.
    /// </summary>
    private static int? OnTheirOwnSocket(IReadOnlyList<TruthRecord> truth, List<UnixSocketOperationCoverage> operations, int processId)
    {
        var spans = new List<(long Connection, long From, long To)>();
        foreach (TruthRecord opened in truth.Where(record => record.ProcessId == processId
            && record.Kind is TruthEventKind.ConnectionEstablished or TruthEventKind.ConnectionAccepted))
        {
            TruthRecord? ended = truth.FirstOrDefault(record => record.ProcessId == processId
                && record.Kind == TruthEventKind.ConnectionClosed && record.CallId == opened.CallId
                && record.Sequence > opened.Sequence);
            if (opened.CallId is not { } connection || ended is null)
            {
                return null;
            }

            spans.Add((connection, opened.Sequence, ended.Sequence));
        }

        spans.Sort(static (left, right) => left.From.CompareTo(right.From));
        if (spans.Count == 0 || spans.Zip(spans.Skip(1)).Any(static pair => pair.Second.From < pair.First.To))
        {
            return null;
        }

        var socketsOf = new Dictionary<long, HashSet<long>>();
        foreach (UnixSocketOperationCoverage operation in operations)
        {
            if (operation.ProcessId != processId || operation.Socket is not { } socket)
            {
                continue;
            }

            foreach ((long connection, long from, long to) in spans)
            {
                if (operation.Sequence > from && operation.Sequence < to)
                {
                    if (!socketsOf.TryGetValue(connection, out HashSet<long>? sockets))
                    {
                        socketsOf[connection] = sockets = [];
                    }

                    sockets.Add(socket);
                    break;
                }
            }
        }

        Dictionary<long, int> connectionsOn = socketsOf.Values.SelectMany(static sockets => sockets)
            .GroupBy(static socket => socket).ToDictionary(static group => group.Key, static group => group.Count());
        return spans.Count(span => socketsOf.TryGetValue(span.Connection, out HashSet<long>? sockets)
            && sockets.Count == 1 && connectionsOn[sockets.Single()] == 1);
    }
}
