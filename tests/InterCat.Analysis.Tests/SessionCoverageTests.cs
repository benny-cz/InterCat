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
