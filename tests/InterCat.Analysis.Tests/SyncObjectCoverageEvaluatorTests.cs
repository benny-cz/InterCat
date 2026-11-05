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
