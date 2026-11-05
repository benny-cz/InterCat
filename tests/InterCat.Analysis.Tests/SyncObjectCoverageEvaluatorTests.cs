using InterCat.Analysis;
using InterCat.Domain;
using Xunit;

namespace InterCat.Analysis.Tests;

/// <summary>
/// Named synchronization objects' coverage against a truth log (ADR-082, ADR-083): each truth object is the one number its
/// closes' names carry, discovered with its lifetime where that number has its creation and every close the truth logged,
/// bound to exactly the processes that held it; a signal or a wait is in no record and no record carries a size, so the
/// mechanism claims neither bytes nor operations and reaches topology at most.
/// </summary>
public sealed class SyncObjectCoverageEvaluatorTests
{
    private const int Creator = 100, Opener = 200, Other = 300;
    private const string Run = @"Local\InterCat-FX-SYNC-001-0123456789abcdef";
    private static readonly string[] Kinds = ["event", "semaphore", "mutex"];
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "P27: a synchronization objects evaluation finds each truth object as the one number its closes name, from its creation to every close in the processes that held it, and is topology only - it claims no byte and no operation")]
    public void EachObjectIsTheNumberItsClosesName()
    {
        SyncObjectCoverageResult result = SyncObjectCoverageEvaluator.Evaluate(Truth(), [.. Observed(), .. Elsewhere()], Settings());

        Assert.Equal(3, result.Objects.Count);
        Assert.All(result.Objects, item => Assert.True(item.DiscoveredWithLifetime, item.Gap));
        Assert.Equal([1L, 2L, 3L], result.Objects.Select(item => item.ObjectNumber!.Value).Order());
        Assert.All(result.Objects, item => Assert.Equal([Creator, Opener], item.MemberProcesses));
        MechanismMeasurement measured = result.Measurement;
        Assert.Equal((3L, 3L, 6L, 6L, 6L, 0L), (measured.TruthResources, measured.DiscoveredResourcesWithLifetime, measured.ExpectedMemberships,
            measured.ResolvedMemberships, measured.PeerAttributions, measured.FalsePeerAttributions));

        // Its opens and closes are seen in their own processes under their objects' numbers, but they are no traffic: no
        // byte is eligible and the mechanism claims none, so the tier is topology.
        Assert.Equal((12L, 12L, 12L, 0L, false), (measured.TruthOperations, measured.TruthOperationsWithAdmittedObservation,
            measured.TruthOperationsBoundToResourceInstance, measured.ByteEligibleOperations, measured.ClaimsBytesOrOperations));
        Assert.Equal(CapabilityTier.TopologyOnly, result.Assessment.Tier);
        Assert.Equal(2, result.ObservationsOutsideScope);
    }

    [Fact(DisplayName = "P7: a process the truth says never held an object, bound to its number, is a false membership, and the object is not discovered")]
    public void AStrangerBoundToAnObjectIsAFalseMembership()
    {
        SyncObjectObservation stranger = new() { Ordinal = 99, Kind = ObservationKind.Open, ProcessId = Other, ObjectNumber = 1 };
        SyncObjectCoverageResult result = SyncObjectCoverageEvaluator.Evaluate(Truth(), [.. Observed(), stranger], Settings());

        SyncObjectTruthCoverage first = result.Objects.Single(item => item.ObjectNumber == 1);
        Assert.False(first.DiscoveredWithLifetime);
        Assert.Equal([Creator, Opener, Other], first.MemberProcesses);
        Assert.Contains("never held it", first.Gap, StringComparison.Ordinal);
        Assert.Equal((2L, 7L, 1L), (result.Measurement.DiscoveredResourcesWithLifetime, result.Measurement.PeerAttributions,
            result.Measurement.FalsePeerAttributions));
        Assert.Equal(CapabilityTier.ExperimentalEvidence, result.Assessment.Tier);
    }

    [Fact(DisplayName = "R22: an object its closes name under two numbers is no one object, and one whose creation or a logged close was not seen under its number is not discovered with its lifetime")]
    public void AnObjectSplitOrCutIsNotDiscovered()
    {
        SyncObjectObservation[] observed =
        [
            .. Observed().Where(observation => !(observation.ObjectNumber == 1 && observation is { Kind: ObservationKind.Close, ProcessId: Opener })
                && !(observation.ObjectNumber == 3 && observation is { Kind: ObservationKind.Open, ProcessId: Creator })
                && !(observation.ObjectNumber == 2 && observation is { Kind: ObservationKind.Close, ProcessId: Opener })),

            // The opener's handle to the semaphore, made before the capture, numbered alone at its close.
            new() { Ordinal = 98, Kind = ObservationKind.Close, ProcessId = Opener, ObjectNumber = 7, Name = Kernel("semaphore") },
        ];
        SyncObjectCoverageResult result = SyncObjectCoverageEvaluator.Evaluate(Truth(), observed, Settings());

        Assert.All(result.Objects, item => Assert.False(item.DiscoveredWithLifetime));
        SyncObjectTruthCoverage semaphore = result.Objects.Single(item => item.Name.EndsWith("-semaphore", StringComparison.Ordinal));
        Assert.Equal(((long?)null, "Its closes named it under 2 numbers, so no one number is the object."), (semaphore.ObjectNumber, semaphore.Gap));
        Assert.Contains("No creation", result.Objects.Single(item => item.Name.EndsWith("-mutex", StringComparison.Ordinal)).Gap, StringComparison.Ordinal);
        Assert.Contains("Not every close", result.Objects.Single(item => item.Name.EndsWith("-event", StringComparison.Ordinal)).Gap, StringComparison.Ordinal);
        Assert.Equal(0, result.Measurement.DiscoveredResourcesWithLifetime);
        Assert.Equal(CapabilityTier.ExperimentalEvidence, result.Assessment.Tier);
    }

    [Fact(DisplayName = "R22: a number the closes of two truth objects name is neither's - the capture joined two objects the truth says were apart, so neither is discovered and their opens and closes bind to no object of their own")]
    public void ANumberOfTwoObjectsIsNeithers()
    {
        // The capture followed the event's number on to the semaphore: both objects' records carry it.
        SyncObjectObservation[] joined = [.. Observed().Select(observation => observation.ObjectNumber == 2 ? observation with { ObjectNumber = 1 } : observation)];
        SyncObjectCoverageResult result = SyncObjectCoverageEvaluator.Evaluate(Truth(), joined, Settings());

        SyncObjectTruthCoverage[] apart = [.. result.Objects.Where(item => item.ObjectNumber == 1)];
        Assert.Equal(2, apart.Length);
        Assert.All(apart, item => Assert.Equal((false, "Its number is another truth object's too, so the number is no one object."),
            (item.DiscoveredWithLifetime, item.Gap)));
        Assert.True(result.Objects.Single(item => item.ObjectNumber == 3).DiscoveredWithLifetime);

        // Their opens and closes are observed, but bound to no object of their own: only the mutex's are.
        MechanismMeasurement measured = result.Measurement;
        Assert.Equal((12L, 12L, 4L, 1L), (measured.TruthOperations, measured.TruthOperationsWithAdmittedObservation,
            measured.TruthOperationsBoundToResourceInstance, measured.DiscoveredResourcesWithLifetime));
        Assert.Equal(CapabilityTier.ExperimentalEvidence, result.Assessment.Tier);
    }

    [Fact(DisplayName = "P27: a capture with no record of the truth's processes discovers nothing, its records scope, and the mechanism is unsupported; a kernel name names an object only by its whole name after the namespace")]
    public void NothingOfTheTruthIsUnsupported()
    {
        SyncObjectCoverageResult result = SyncObjectCoverageEvaluator.Evaluate(Truth(), Elsewhere(), Settings());
        Assert.Equal((0L, 0L, 2), (result.Measurement.DiscoveredResourcesWithLifetime, result.Measurement.TruthOperationsWithAdmittedObservation,
            result.ObservationsOutsideScope));
        Assert.Equal(CapabilityTier.Unsupported, result.Assessment.Tier);

        Assert.True(SyncObjectCoverageEvaluator.Names(Kernel("event"), Run + "-event"));
        Assert.False(SyncObjectCoverageEvaluator.Names(Kernel("event") + "2", Run + "-event"));
        Assert.False(SyncObjectCoverageEvaluator.Names(@"\Sessions\1\BaseNamedObjects\XInterCat-FX-SYNC-001-0123456789abcdef-event", Run + "-event"));
        Assert.False(SyncObjectCoverageEvaluator.Names(null, Run + "-event"));
    }

    [Fact(DisplayName = "P27: FX-SYNC-001's handle records through the product reproduce its measured tier - each named object the one number its closes name, from the creator's creation to both processes' closes, bound to exactly the two processes, and topology only")]
    public void CommittedEvidenceReproducesItsTier()
    {
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", "FX-SYNC-001", "evidence");
        string product = Path.Combine(evidence, "product");
        TruthRecord[] truth = [.. ReadTruth(Path.Combine(product, "truth-creator.jsonl")), .. ReadTruth(Path.Combine(product, "truth-opener.jsonl"))];
        Dictionary<string, int> processes = truth.GroupBy(record => record.Role).ToDictionary(group => group.Key, group => group.First().ProcessId);

        // The committed ledger holds the workload's records, and other processes' on its objects, by role - never a PID (P16).
        SyncObjectObservation[] records = [.. ReadLedger(Path.Combine(product, "records.csv"), processes)];
        SyncObjectCoverageResult result = SyncObjectCoverageEvaluator.Evaluate(truth, records, Settings());

        using System.Text.Json.JsonDocument measurement = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "measurement.json")));
        System.Text.Json.JsonElement stated = measurement.RootElement.GetProperty("throughTheProduct").GetProperty("runs")[0].GetProperty("evaluation");
        MechanismMeasurement measured = result.Measurement;
        Assert.Equal(
            (stated.GetProperty("truthObjects").GetInt64(), stated.GetProperty("discoveredWithLifetime").GetInt64(),
                stated.GetProperty("expectedMemberships").GetInt64(), stated.GetProperty("resolvedMemberships").GetInt64(),
                stated.GetProperty("falseMemberships").GetInt64(), stated.GetProperty("truthOperations").GetInt64(), stated.GetProperty("observed").GetInt64()),
            (measured.TruthResources, measured.DiscoveredResourcesWithLifetime, measured.ExpectedMemberships, measured.ResolvedMemberships,
                measured.FalsePeerAttributions, measured.TruthOperations, measured.TruthOperationsWithAdmittedObservation));
        Assert.Equal(stated.GetProperty("recordsOfOtherProcessesOnItsObjects").GetInt32(), result.ObservationsOutsideScope);
        Assert.Equal(stated.GetProperty("gaps").EnumerateArray().Select(gap => gap.GetString()), result.Assessment.Gaps);
        Assert.Equal(Enum.Parse<CapabilityTier>(stated.GetProperty("tier").GetString()!), result.Assessment.Tier);

        // Every one of them: three objects, each one number from the creator's creation to both processes' closes and bound to
        // exactly the two, its closes carrying its whole kernel name in the session's namespace; every handle open and close
        // the truth logged seen in its own process; no byte, signal or wait claimed, so the tier is topology.
        Assert.Equal((3L, 3L, 6L, 6L, 0L), (measured.TruthResources, measured.DiscoveredResourcesWithLifetime, measured.ExpectedMemberships,
            measured.ResolvedMemberships, measured.FalsePeerAttributions));
        Assert.Equal((12L, 12L, 0L, false), (measured.TruthOperations, measured.TruthOperationsWithAdmittedObservation,
            measured.ByteEligibleOperations, measured.ClaimsBytesOrOperations));
        int[] holders = [.. processes.Values.Order()];
        Assert.All(result.Objects, item => Assert.Equal(holders, item.MemberProcesses));
        Assert.All(result.Objects, item => Assert.Contains(records, record => record.Kind == ObservationKind.Close
            && record.ObjectNumber == item.ObjectNumber && record.Name is { } name && name.StartsWith(@"\Sessions\", StringComparison.Ordinal)
            && name.EndsWith(@"\" + item.Name[(item.Name.IndexOf('\\', StringComparison.Ordinal) + 1)..], StringComparison.Ordinal)));
        Assert.Equal(CapabilityTier.TopologyOnly, result.Assessment.Tier);
    }

    private static IEnumerable<TruthRecord> ReadTruth(string path)
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return File.ReadLines(path).Where(line => line.Length > 0).Select(line => System.Text.Json.JsonSerializer.Deserialize<TruthRecord>(line, options)!);
    }

    // A ledger's records as the evaluator reads a session's: each role of the truth its process, each other process one of its
    // own the truth never names, and a name naming none of the run's objects ("named") one that names no object.
    private static IEnumerable<SyncObjectObservation> ReadLedger(string path, Dictionary<string, int> processes) =>
        File.ReadLines(path).Skip(1).Where(line => line.Length > 0).Select(line => line.Split(',')).Select(field => new SyncObjectObservation
        {
            Ordinal = long.Parse(field[0], System.Globalization.CultureInfo.InvariantCulture),
            Kind = Enum.Parse<ObservationKind>(field[3]),
            ProcessId = field[2].Length == 0 ? null
                : processes.TryGetValue(field[2], out int process) ? process
                : -int.Parse(field[2]["other-".Length..], System.Globalization.CultureInfo.InvariantCulture),
            ObjectNumber = field[5].Length == 0 ? null : long.Parse(field[5], System.Globalization.CultureInfo.InvariantCulture),
            Name = field[6].Length == 0 ? null : field[6],
        });

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }

    private static SyncObjectCoverageSettings Settings() => new()
    {
        FixtureId = "FX-SYNC-001",
        BuildId = "10.0.26220.0-x64",
        BuildIsSupported = true,
        Reproduced = true,
    };

    // FX-SYNC-001's truth: the creator makes the three objects, the opener opens each by its name, and each closes its own.
    private static TruthRecord[] Truth()
    {
        var records = new List<TruthRecord>();
        long sequence = 0;
        foreach (string kind in Kinds)
        {
            records.Add(Record("creator", Creator, ++sequence, TruthEventKind.ObjectCreated, kind));
            records.Add(Record("opener", Opener, ++sequence, TruthEventKind.ObjectOpened, kind));
            records.Add(Record("opener", Opener, ++sequence, TruthEventKind.ObjectSignaled, kind));
        }

        foreach (string kind in Kinds)
        {
            records.Add(Record("creator", Creator, ++sequence, TruthEventKind.HandleClosed, kind));
            records.Add(Record("opener", Opener, ++sequence, TruthEventKind.HandleClosed, kind));
        }

        return [.. records];
    }

    // The capture's records of the three objects, numbered 1 to 3: each creation, each open, and each close naming the object.
    private static SyncObjectObservation[] Observed() =>
    [
        .. Kinds.SelectMany((kind, index) => new SyncObjectObservation[]
        {
            new() { Ordinal = (index * 10) + 1, Kind = ObservationKind.Open, ProcessId = Creator, ObjectNumber = index + 1 },
            new() { Ordinal = (index * 10) + 2, Kind = ObservationKind.Open, ProcessId = Opener, ObjectNumber = index + 1 },
            new() { Ordinal = (index * 10) + 3, Kind = ObservationKind.Close, ProcessId = Creator, ObjectNumber = index + 1, Name = Kernel(kind) },
            new() { Ordinal = (index * 10) + 4, Kind = ObservationKind.Close, ProcessId = Opener, ObjectNumber = index + 1, Name = Kernel(kind) },
        }),
    ];

    // Another process's handles to an object of its own, which a machine-wide capture sees.
    private static SyncObjectObservation[] Elsewhere() =>
    [
        new() { Ordinal = 50, Kind = ObservationKind.Open, ProcessId = Other, ObjectNumber = 9 },
        new() { Ordinal = 51, Kind = ObservationKind.Close, ProcessId = Other, ObjectNumber = 9, Name = @"\Sessions\1\BaseNamedObjects\Elsewhere" },
    ];

    private static string Kernel(string kind) => @"\Sessions\1\BaseNamedObjects\InterCat-FX-SYNC-001-0123456789abcdef-" + kind;

    private static TruthRecord Record(string role, int process, long sequence, TruthEventKind kind, string objectKind) => new()
    {
        ScenarioId = "FX-SYNC-001",
        Role = role,
        Sequence = sequence,
        Kind = kind,
        ProcessId = process,
        ProcessStartFileTime = 0,
        MonotonicTicks = sequence,
        RecordedUtc = Start.AddMilliseconds(sequence),
        Status = objectKind,
        ResourceName = Run + "-" + objectKind,
    };
}
