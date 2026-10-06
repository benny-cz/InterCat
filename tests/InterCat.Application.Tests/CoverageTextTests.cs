using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// R21's words for a scope's coverage, which the inspector states beneath its time scope and <c>icat evidence</c> beside
/// its records: each mechanism's state with the fact behind any state short of covered, never one rolled-up state.
/// </summary>
public sealed class CoverageTextTests
{
    [Fact(DisplayName = "R21: a scope's coverage is said mechanism by mechanism, with the fact behind any state short of covered")]
    public void AScopesCoverageIsSaidMechanismByMechanism()
    {
        // An import that collected TCP, UDP and process creations and delivered no UDP record: a file cannot show whether
        // its session recorded UDP, so UDP's quiet is unknown rather than covered.
        CoverageLedgerV1 quiet = Import(Delivered(NetworkProvider, 10, 0, 2), Delivered(ProcessProvider, 1, 4, 2));
        Assert.Equal("Coverage: covered for process lifecycle and TCP · unknown for UDP: its 1 admitted descriptor delivered "
            + "nothing, and a file cannot show whether its session recorded them · no other mechanism collected",
            CoverageText.Describe(SessionCoverage.ByMechanism(quiet)));
        Assert.True(CoverageText.IsShort(SessionCoverage.ByMechanism(quiet)));

        // A TCP record that could not be decoded leaves TCP alone short, and says why.
        CoverageLedgerV1 undecodable = Import(
            Delivered(NetworkProvider, 10, 0, 3) with
            {
                Admitted = 2,
                Undecodable = new Dictionary<UndecodableReason, long> { [UndecodableReason.BodyShorterThanSchema] = 1 },
            },
            Delivered(NetworkProvider, 42, 0, 1),
            Delivered(ProcessProvider, 1, 4, 2));
        Assert.Equal("Coverage: covered for process lifecycle and UDP · a partial gap, not extrapolated, for TCP: 1 of its "
            + "records could not be decoded · no other mechanism collected",
            CoverageText.Describe(SessionCoverage.ByMechanism(undecodable)));

        // Past the file's readings nothing is known of any mechanism, which is said once.
        Assert.Equal("Coverage unknown: outside the readings the capture's sources delivered, so a count of none here is "
            + "not proof of inactivity",
            CoverageText.Describe(SessionCoverage.ByMechanism(quiet, new TimeRange(70, 80))));

        // Every collected mechanism delivering, three are named as a sentence lists them.
        CoverageLedgerV1 delivered = Import(
            Delivered(NetworkProvider, 10, 0, 2), Delivered(NetworkProvider, 42, 0, 1), Delivered(ProcessProvider, 1, 4, 2));
        Assert.Equal("Coverage: covered for process lifecycle, TCP and UDP · no other mechanism collected",
            CoverageText.Describe(SessionCoverage.ByMechanism(delivered)));
        Assert.False(CoverageText.IsShort(SessionCoverage.ByMechanism(delivered)));

        // Unknown for different reasons is said for each, and a state this version never derives in its own words.
        Assert.Equal("Coverage: unknown for UDP: one reason · unknown for RPC: another",
            CoverageText.Describe(
            [
                new(Mechanism.Rpc, CoverageState.UnknownCoverage, "another"),
                new(Mechanism.Udp, CoverageState.UnknownCoverage, "one reason"),
            ]));
        Assert.Equal("Coverage: reduced fidelity for TCP: sampled",
            CoverageText.Describe([new(Mechanism.Tcp, CoverageState.ReducedFidelity, "sampled")]));

        // Every mechanism covered leaves nothing to add; nothing collected is short, and nothing judged says nothing.
        MechanismCoverage[] covered = [new(Mechanism.Tcp, CoverageState.Covered, "2 records"), new(Mechanism.Udp, CoverageState.Covered, "1 record")];
        Assert.Equal("Coverage: covered for TCP and UDP", CoverageText.Describe(covered));
        Assert.False(CoverageText.IsShort(covered));
        MechanismCoverage[] none = [new(Mechanism.Tcp, CoverageState.NotCollected, "no admitted descriptor records it")];
        Assert.Equal("Coverage: no mechanism was collected here, so a count of none here is not proof of inactivity",
            CoverageText.Describe(none));
        Assert.True(CoverageText.IsShort(none));
        Assert.Equal(string.Empty, CoverageText.Describe([]));
        Assert.False(CoverageText.IsShort([]));
    }

    /// <summary>An imported file's one epoch, which collected TCP, UDP and process creations and lost nothing.</summary>
    private static CoverageLedgerV1 Import(params CoverageDeliveryV1[] deliveries) => new()
    {
        Contract = CoverageLedgerV1.ContractName,
        Epochs = [new CoverageEpochV1
        {
            Epoch = 1,
            Acquisition = CoverageAcquisition.EtlImport,
            FirstDeliveredNativeTicks = 0,
            LastDeliveredNativeTicks = 60,
            Collected =
            [
                new CoverageCollectedV1 { ProviderId = NetworkProvider, ProviderName = "network", EventId = 10, Version = 0, Mechanism = Mechanism.Tcp },
                new CoverageCollectedV1 { ProviderId = NetworkProvider, ProviderName = "network", EventId = 42, Version = 0, Mechanism = Mechanism.Udp },
                new CoverageCollectedV1 { ProviderId = ProcessProvider, ProviderName = "process", EventId = 1, Version = 4, Mechanism = Mechanism.ProcessLifecycle },
            ],
            Deliveries = deliveries,
            Losses = [new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = 0 }],
        }],
    };

    private static CoverageDeliveryV1 Delivered(Guid provider, int eventId, int version, long records) => new()
    {
        ProviderId = provider,
        EventId = eventId,
        Version = version,
        Delivered = records,
        Admitted = records,
        Omitted = 0,
    };
}
