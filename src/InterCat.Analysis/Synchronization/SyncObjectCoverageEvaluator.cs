using InterCat.Domain;

namespace InterCat.Analysis;

/// <summary>The fixture and build a synchronization objects evaluation is for.</summary>
public sealed record SyncObjectCoverageSettings
{
    public required string FixtureId { get; init; }
    public required string BuildId { get; init; }
    public required bool BuildIsSupported { get; init; }
    public bool Reproduced { get; init; }
}

/// <summary>
/// One of a capture's synchronization handle records as a session holds it (ADR-083): its kind - a handle's creation or a
/// duplication, an open; a close; or a handle open at the stop, an inventory - the process it is bound to under
/// `process-binding-v6`, the object's number the gate wrote over its address, and the name a close and a rundown carry.
/// </summary>
public sealed record SyncObjectObservation
{
    /// <summary>The record's place in the capture, which orders its records.</summary>
    public required long Ordinal { get; init; }

    public required ObservationKind Kind { get; init; }

    /// <summary>The process the record binds to; null for one that binds to none.</summary>
    public int? ProcessId { get; init; }

    /// <summary>The object's number in the capture, never its address; null where the record carries none.</summary>
    public long? ObjectNumber { get; init; }

    /// <summary>The object's full name, which only a close and the stop's rundown carry; null for none.</summary>
    public string? Name { get; init; }
}

/// <summary>
/// One truth object and what the capture showed of it: the processes the truth says held it, the one number its closes'
/// names gave it, whether its creation and every handle's close the truth logged were seen under that number, the processes
/// the number's records are bound to, and whether it was discovered with its lifetime; or why not.
/// </summary>
public sealed record SyncObjectTruthCoverage(
    string Name,
    IReadOnlyList<int> TruthProcesses,
    long? ObjectNumber,
    bool CreationSeen,
    bool EveryCloseSeen,
    IReadOnlyList<int> MemberProcesses,
    bool DiscoveredWithLifetime,
    string? Gap);

/// <summary>The evaluation result: the section 14.2 counters plus the evidence behind every one of them.</summary>
public sealed record SyncObjectCoverageResult
{
    public required MechanismMeasurement Measurement { get; init; }
    public required TierAssessment Assessment { get; init; }
    public required IReadOnlyList<SyncObjectTruthCoverage> Objects { get; init; }

    /// <summary>The synchronization records of processes the truth does not name: a machine-wide capture sees them; they are scope.</summary>
    public required int ObservationsOutsideScope { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// Compares an independent truth log of named synchronization objects - each object a process created or opened by its name,
/// and each handle it closed - with a capture's synchronization handle records (ADR-082, ADR-083), and computes the section
/// 14.2 counters. A truth object is the one number the capture's closes naming it carry: a close is the only handle record a
/// creation's name reaches, and the gate numbers an object from the first handle it saw made to the close of the last. It is
/// discovered with its lifetime where that number has the creator's creation and every close the truth logged, each in the
/// process that logged it, and its members - the processes its records bind to - are exactly the truth's: a process the
/// truth says never held it is a false membership, and the object is not discovered. Its handle opens and closes are the
/// truth's operations; a signal or a wait is in no record, so none is counted, and no record carries a size, so the
/// mechanism claims neither bytes nor operations and can reach no tier past topology (P27). It is pure and portable: the
/// same evaluation runs in a test on committed evidence (R19, R18).
/// </summary>
public static class SyncObjectCoverageEvaluator
{
    public static SyncObjectCoverageResult Evaluate(
        IReadOnlyList<TruthRecord> truth,
        IReadOnlyList<SyncObjectObservation> observations,
        SyncObjectCoverageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(truth);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(settings);

        // Each truth object by its name: who created it, who held a handle to it, and how many each opened and closed.
        TruthRecord[] handles = [.. truth.Where(static record => record.ResourceName is not null
            && record.Kind is TruthEventKind.ObjectCreated or TruthEventKind.ObjectOpened or TruthEventKind.HandleClosed)];
        HashSet<int> processes = [.. truth.Select(static record => record.ProcessId)];
        SyncObjectObservation[] inScope = [.. observations.Where(observation => observation.ProcessId is { } process && processes.Contains(process))
            .OrderBy(static observation => observation.Ordinal)];
        int outsideScope = observations.Count - inScope.Length;

        var objects = new List<SyncObjectTruthCoverage>();
        long truthOperations = 0;
        long observedOperations = 0;
        long expectedMemberships = 0;
        long resolvedMemberships = 0;
        long attributions = 0;
        long falseAttributions = 0;
        foreach (IGrouping<string, TruthRecord> named in handles.GroupBy(static record => record.ResourceName!, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            int[] holders = [.. named.Where(static record => record.Kind != TruthEventKind.HandleClosed).Select(static record => record.ProcessId)
                .Distinct().Order()];
            int[] creators = [.. named.Where(static record => record.Kind == TruthEventKind.ObjectCreated).Select(static record => record.ProcessId).Distinct()];
            Dictionary<(int Process, bool Close), int> logged = named.GroupBy(static record => (record.ProcessId, record.Kind == TruthEventKind.HandleClosed))
                .ToDictionary(static group => group.Key, static group => group.Count());
            truthOperations += named.Count();
            expectedMemberships += holders.Length;

            // The object's number: the one its closes' names carry, each naming it after its namespace.
            long[] numbers = [.. inScope.Where(observation => observation.Kind == ObservationKind.Close && Names(observation.Name, named.Key)
                && observation.ObjectNumber is not null).Select(static observation => observation.ObjectNumber!.Value).Distinct().Order()];
            if (numbers.Length != 1)
            {
                objects.Add(new(named.Key, holders, null, false, false, [], false, numbers.Length == 0
                    ? "No close of a handle to it named it under a number of its own."
                    : $"Its closes named it under {numbers.Length} numbers, so no one number is the object."));
                continue;
            }

            long number = numbers[0];
            SyncObjectObservation[] records = [.. observations.Where(observation => observation.ObjectNumber == number)];
            int[] members = [.. records.Where(static observation => observation.ProcessId is not null).Select(static observation => observation.ProcessId!.Value)
                .Distinct().Order()];
            int[] falseMembers = [.. members.Except(holders)];
            attributions += members.Length;
            falseAttributions += falseMembers.Length;
            resolvedMemberships += members.Intersect(holders).Count();

            // Each process's logged opens and closes against the records of the number bound to it, an open or a close.
            foreach (((int process, bool close), int count) in logged)
            {
                int seen = records.Count(observation => observation.ProcessId == process
                    && (close ? observation.Kind == ObservationKind.Close : observation.Kind == ObservationKind.Open));
                observedOperations += Math.Min(count, seen);
            }

            bool creation = creators.Length == 1 && records.Any(observation => observation.Kind == ObservationKind.Open && observation.ProcessId == creators[0]);
            bool everyClose = logged.Where(static pair => pair.Key.Close).All(pair => records.Count(observation =>
                observation.Kind == ObservationKind.Close && observation.ProcessId == pair.Key.Process) >= pair.Value);
            bool discovered = creation && everyClose && falseMembers.Length == 0 && holders.All(members.Contains);
            string? gap = discovered ? null
                : falseMembers.Length > 0 ? "Its number is bound to a process the truth says never held it."
                : !creation ? "No creation of its first handle, in the process that created it, carried its number."
                : !everyClose ? "Not every close the truth logged was seen, in its own process, under its number."
                : "Not every process that held it is bound to its number.";
            objects.Add(new(named.Key, holders, number, creation, everyClose, members, discovered, gap));
        }

        int discoveredObjects = objects.Count(static item => item.DiscoveredWithLifetime);
        var measurement = new MechanismMeasurement
        {
            FixtureId = settings.FixtureId,
            BuildId = settings.BuildId,
            Reproduced = settings.Reproduced,
            MeasuredOnSupportedBuild = settings.BuildIsSupported,

            // A handle's open or close the truth logged, seen in its own process under its object's number: observed and bound
            // to the object, its resource instance.
            TruthOperations = truthOperations,
            TruthOperationsWithAdmittedObservation = observedOperations,
            TruthOperationsBoundToResourceInstance = observedOperations,
            PeerAttributions = attributions,
            FalsePeerAttributions = falseAttributions,

            // No handle record carries a size, so no operation is byte eligible: not measured, never a measured zero (R3, P1).
            ByteEligibleOperations = 0,
            ByteMeasuredOperations = 0,
            TruthResources = objects.Count,
            DiscoveredResourcesWithLifetime = discoveredObjects,
            ExpectedMemberships = expectedMemberships,
            ResolvedMemberships = resolvedMemberships,

            // The handle group shows which processes share an object, never a signal, a wait or a byte.
            ClaimsBytesOrOperations = false,
        };

        var notes = new List<string>
        {
            $"{discoveredObjects} of {objects.Count} truth objects were one number each from the creator's creation to every close the "
            + "truth logged, bound to exactly the processes that held them.",
            $"{observedOperations} of {truthOperations} truth handle opens and closes were seen in their own processes under their "
            + "object's number; a signal or a wait is in no record, so none is an operation here.",
            $"{outsideScope} synchronization records were of processes the truth does not name: a machine-wide capture's scope, not error.",
        };

        return new()
        {
            Measurement = measurement,
            Assessment = CoverageTierCalculator.Assess(measurement),
            Objects = objects,
            ObservationsOutsideScope = outsideScope,
            Notes = notes,
        };
    }

    /// <summary>Whether a record's kernel name names a truth object: its name after its namespace, a path's last parts.</summary>
    public static bool Names(string? kernelName, string truthName)
    {
        ArgumentException.ThrowIfNullOrEmpty(truthName);
        if (kernelName is null)
        {
            return false;
        }

        string part = truthName[(truthName.IndexOf('\\', StringComparison.Ordinal) + 1)..];
        return kernelName.EndsWith("\\" + part, StringComparison.Ordinal);
    }
}
