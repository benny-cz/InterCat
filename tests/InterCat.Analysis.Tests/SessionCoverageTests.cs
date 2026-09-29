using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Analysis.Tests;

public sealed class SessionCoverageTests
{
    private static readonly Guid Provider = Guid.Parse("9a111111-2222-4333-8444-555555555555");

    [Fact(DisplayName = "R21: a legacy generation cannot turn silence into a covered zero")]
    public void LegacyIsUnknown()
    {
        Assert.Equal(CoverageState.UnknownCoverage, SessionCoverage.Of(null, Mechanism.Tcp).State);
        Assert.All(SessionCoverage.ByMechanism(null), item => Assert.Equal(CoverageState.UnknownCoverage, item.State));
    }

    [Fact(DisplayName = "R21: an import distinguishes delivered, quiet, and uncollected mechanisms")]
    public void ImportStatesStayDistinct()
    {
        CoverageLedgerV1 ledger = Ledger(CoverageAcquisition.EtlImport);

        Assert.Equal(CoverageState.Covered, SessionCoverage.Of(ledger, Mechanism.Tcp).State);
        Assert.Equal(CoverageState.UnknownCoverage, SessionCoverage.Of(ledger, Mechanism.Rpc).State);
        Assert.Equal(CoverageState.NotCollected, SessionCoverage.Of(ledger, Mechanism.NamedPipe).State);
    }

    [Fact(DisplayName = "R21: source loss and undecodable records make coverage partial; policy omissions do not")]
    public void LossIsNotPolicyOmission()
    {
        CoverageLedgerV1 ledger = Ledger(CoverageAcquisition.EtlImport);
        CoverageEpochV1 epoch = Assert.Single(ledger.Epochs);
        CoverageDeliveryV1 delivery = Assert.Single(epoch.Deliveries);
        CoverageLedgerV1 omitted = ledger with { Epochs = [epoch with { Deliveries = [delivery with
        {
            Delivered = 2,
            Admitted = 1,
            Omitted = 1,
            Omission = OmissionReason.DescriptorDenied,
        }] }] };
        Assert.Equal(CoverageState.Covered, SessionCoverage.Of(omitted, Mechanism.Tcp).State);

        CoverageLedgerV1 lost = omitted with { Epochs = [epoch with
        {
            Deliveries = Assert.Single(omitted.Epochs).Deliveries,
            Losses = [new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = 1 }],
        }] };
        Assert.Equal(CoverageState.PartialGap, SessionCoverage.Of(lost, Mechanism.Tcp).State);

        CoverageLedgerV1 undecodable = omitted with { Epochs = [epoch with { Deliveries = [delivery with
        {
            Delivered = 2,
            Admitted = 1,
            Omitted = 0,
            Undecodable = new Dictionary<UndecodableReason, long>
            {
                [UndecodableReason.BodyShorterThanSchema] = 1,
            },
        }] }] };
        Assert.Equal(CoverageState.PartialGap, SessionCoverage.Of(undecodable, Mechanism.Tcp).State);
    }

    [Fact(DisplayName = "R21: live quiet enablement is covered, unlike a quiet imported file")]
    public void LiveQuietIsCovered()
    {
        CoverageLedgerV1 ledger = Ledger(CoverageAcquisition.LiveCapture);
        Assert.Equal(CoverageState.Covered, SessionCoverage.Of(ledger, Mechanism.Rpc).State);
    }

    [Fact(DisplayName = "R21: an unknown descriptor version cannot silently preserve covered mechanisms")]
    public void UnassignedDecodeLossAffectsEveryCollectedMechanism()
    {
        CoverageLedgerV1 ledger = Ledger(CoverageAcquisition.EtlImport);
        CoverageEpochV1 epoch = Assert.Single(ledger.Epochs);
        CoverageLedgerV1 unknownVersion = ledger with { Epochs = [epoch with { Deliveries =
        [
            .. epoch.Deliveries,
            new CoverageDeliveryV1
            {
                ProviderId = Provider,
                EventId = 10,
                Version = 2,
                Delivered = 1,
                Admitted = 0,
                Omitted = 0,
                Undecodable = new Dictionary<UndecodableReason, long>
                {
                    [UndecodableReason.UnknownDescriptorVersion] = 1,
                },
            },
        ] }] };

        Assert.Equal(CoverageState.PartialGap, SessionCoverage.Of(unknownVersion, Mechanism.Tcp).State);
        Assert.Equal(CoverageState.UnknownCoverage, SessionCoverage.Of(unknownVersion, Mechanism.Rpc).State);
        Assert.Equal(CoverageState.NotCollected, SessionCoverage.Of(unknownVersion, Mechanism.NamedPipe).State);
    }

    [Fact(DisplayName = "R21: interval coverage never extends beyond delivered readings or over an epoch gap")]
    public void IntervalRequiresCompleteSpan()
    {
        CoverageLedgerV1 ledger = Ledger(CoverageAcquisition.EtlImport);
        Assert.Equal(CoverageState.Covered, SessionCoverage.Of(ledger, Mechanism.Tcp, new TimeRange(10, 21)).State);
        Assert.Equal(CoverageState.UnknownCoverage, SessionCoverage.Of(ledger, Mechanism.Tcp, new TimeRange(9, 21)).State);
        Assert.Equal(CoverageState.UnknownCoverage, SessionCoverage.Of(ledger, Mechanism.Tcp, new TimeRange(10, 22)).State);

        CoverageEpochV1 first = Assert.Single(ledger.Epochs);
        CoverageLedgerV1 gap = ledger with { Epochs = [first, first with
        {
            Epoch = 2,
            FirstDeliveredNativeTicks = 30,
            LastDeliveredNativeTicks = 40,
        }] };
        Assert.Equal(CoverageState.UnknownCoverage, SessionCoverage.Of(gap, Mechanism.Tcp, new TimeRange(10, 41)).State);
    }

    [Fact(DisplayName = "R21: a live epoch speaks for every reading between its recorded start and stop, not only its delivered ones")]
    public void ALiveEpochSpeaksForItsRecording()
    {
        // Delivered between 10 and 20: before and after them, nothing says the capture was listening.
        CoverageLedgerV1 ledger = Ledger(CoverageAcquisition.LiveCapture);
        CoverageEpochV1 epoch = Assert.Single(ledger.Epochs);
        Assert.Equal(CoverageState.UnknownCoverage, SessionCoverage.Of(ledger, Mechanism.Tcp, new TimeRange(0, 30)).State);

        // Recorded from its epoch reading, 0, to its stop, 29: all of that is covered, and nothing beyond it.
        CoverageLedgerV1 recorded = ledger with { Epochs = [epoch with { RecordedFromNativeTicks = 0, RecordedToNativeTicks = 29 }] };
        Assert.Equal(CoverageState.Covered, SessionCoverage.Of(recorded, Mechanism.Tcp, new TimeRange(0, 30)).State);
        Assert.Equal(CoverageState.Covered, SessionCoverage.Of(recorded, Mechanism.Tcp, new TimeRange(25, 30)).State);
        Assert.Equal(CoverageState.UnknownCoverage, SessionCoverage.Of(recorded, Mechanism.Tcp, new TimeRange(25, 31)).State);
        Assert.Equal([CoverageState.Covered, CoverageState.UnknownCoverage],
            SessionCoverage.CaptureStates(recorded, [new TimeRange(0, 5), new TimeRange(30, 35)]));

        // A live epoch that delivered nothing still speaks for its recording: its quiet sources were enabled.
        CoverageLedgerV1 quiet = recorded with { Epochs = [Assert.Single(recorded.Epochs) with
        {
            FirstDeliveredNativeTicks = null,
            LastDeliveredNativeTicks = null,
            Deliveries = [],
        }] };
        Assert.Equal(CoverageState.Covered, SessionCoverage.Of(quiet, Mechanism.Tcp, new TimeRange(0, 30)).State);
    }

    [Fact(DisplayName = "R21: an interval ending at maximum ticks is evaluated without overflow")]
    public void MaximumNativeTickDoesNotOverflow()
    {
        CoverageLedgerV1 ledger = Ledger(CoverageAcquisition.EtlImport);
        CoverageEpochV1 epoch = Assert.Single(ledger.Epochs);
        CoverageLedgerV1 atMax = ledger with { Epochs = [epoch with
        {
            FirstDeliveredNativeTicks = long.MaxValue - 1,
            LastDeliveredNativeTicks = long.MaxValue,
        }] };
        Assert.Equal(CoverageState.Covered, SessionCoverage.Of(
            atMax, Mechanism.Tcp, new TimeRange(long.MaxValue - 1, long.MaxValue)).State);
    }

    [Fact(DisplayName = "§18.3: a classic class's coverage counts only the opcodes it admitted, each as a descriptor of its own")]
    public void AClassicClassIsCoveredByTheOpcodesItAdmitted()
    {
        Guid alpc = Guid.Parse("45d8cccd-539f-4b72-a8b7-5c683142609a");
        CoverageLedgerV1 ledger = Ledger(CoverageAcquisition.LiveCapture);
        CoverageEpochV1 epoch = Assert.Single(ledger.Epochs);
        CoverageLedgerV1 classic = ledger with { Epochs = [epoch with
        {
            Collected =
            [
                .. epoch.Collected,
                .. Enumerable.Range(33, 2).Select(opcode => new CoverageCollectedV1
                {
                    ProviderId = alpc, ProviderName = "Kernel ALPC", EventId = 0, Version = 2, Opcode = opcode, Mechanism = Mechanism.Alpc,
                }),
            ],
            Deliveries =
            [
                .. epoch.Deliveries,
                new CoverageDeliveryV1 { ProviderId = alpc, EventId = 0, Version = 2, Opcode = 33, Delivered = 5, Admitted = 5, Omitted = 0 },
                new CoverageDeliveryV1 { ProviderId = alpc, EventId = 0, Version = 2, Opcode = 34, Delivered = 4, Admitted = 4, Omitted = 0 },
                new CoverageDeliveryV1
                {
                    ProviderId = alpc, EventId = 0, Version = 2, Opcode = 36, Delivered = 7, Admitted = 0, Omitted = 7,
                    Omission = OmissionReason.DescriptorNotAdmitted,
                },
            ],
        }] };

        MechanismCoverage coverage = SessionCoverage.Of(classic, Mechanism.Alpc);
        Assert.Equal(CoverageState.Covered, coverage.State);
        Assert.Equal("9 records from its 2 admitted descriptors, and nothing was reported lost", coverage.Reason);
    }

    private static CoverageLedgerV1 Ledger(CoverageAcquisition acquisition) => new()
    {
        Contract = CoverageLedgerV1.ContractName,
        Epochs = [new CoverageEpochV1
        {
            Epoch = 1,
            Acquisition = acquisition,
            FirstDeliveredNativeTicks = 10,
            LastDeliveredNativeTicks = 20,
            Collected =
            [
                new CoverageCollectedV1 { ProviderId = Provider, ProviderName = "network", EventId = 10, Version = 1, Mechanism = Mechanism.Tcp },
                new CoverageCollectedV1 { ProviderId = Provider, ProviderName = "network", EventId = 11, Version = 1, Mechanism = Mechanism.Rpc },
            ],
            Deliveries = [new CoverageDeliveryV1
            {
                ProviderId = Provider,
                EventId = 10,
                Version = 1,
                Delivered = 1,
                Admitted = 1,
                Omitted = 0,
            }],
            Losses = acquisition == CoverageAcquisition.EtlImport
                ? [new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = 0 }]
                :
                [
                    new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = 0 },
                    new CoverageLossV1 { Layer = LossLayer.ConsumerBuffers, Lost = 0 },
                    new CoverageLossV1 { Layer = LossLayer.CallbackQueue, Lost = 0 },
                    new CoverageLossV1 { Layer = LossLayer.Storage, Lost = 0 },
                ],
        }],
    };
}
