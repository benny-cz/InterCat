using System.Text;
using System.Security.Cryptography;
using InterCat.Domain;
using Xunit;

namespace InterCat.Storage.Tests;

public sealed class CoverageLedgerTests
{
    private static readonly Guid Provider = Guid.Parse("9a111111-2222-4333-8444-555555555555");

    [Fact(DisplayName = "R21: a coverage ledger round-trips every outcome and refuses unknown members")]
    public void RoundTripAndUnknownMemberRefusal()
    {
        CoverageLedgerV1 original = Example();
        byte[] bytes = original.Encode();
        CoverageLedgerV1 decoded = CoverageLedgerV1.Decode(bytes);

        CoverageEpochV1 epoch = Assert.Single(decoded.Epochs);
        Assert.Equal((10L, 20L), (epoch.FirstDeliveredNativeTicks, epoch.LastDeliveredNativeTicks));
        CoverageDeliveryV1 delivery = Assert.Single(epoch.Deliveries);
        Assert.Equal((3L, 1L, 1L), (delivery.Delivered, delivery.Admitted, delivery.Omitted));
        Assert.Equal(1, delivery.Undecodable![UndecodableReason.BodyShorterThanSchema]);
        Assert.Equal(2, Assert.Single(epoch.Losses).Lost);

        string json = Encoding.UTF8.GetString(bytes);
        byte[] extra = Encoding.UTF8.GetBytes(json.Replace("\"contract\":", "\"surprise\":true,\"contract\":", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => CoverageLedgerV1.Decode(extra));
    }

    [Fact(DisplayName = "R21: coverage outcomes and descriptor bounds refuse overflow rather than wrapping")]
    public void InvalidArithmeticAndDescriptorBoundsRefuse()
    {
        CoverageLedgerV1 original = Example();
        CoverageEpochV1 epoch = Assert.Single(original.Epochs);
        CoverageDeliveryV1 delivery = Assert.Single(epoch.Deliveries);
        CoverageLedgerV1 overflow = original with
        {
            Epochs = [epoch with
            {
                Deliveries = [delivery with
                {
                    Delivered = long.MaxValue,
                    Admitted = long.MaxValue,
                    Omitted = long.MaxValue,
                }],
            }],
        };
        Assert.Throws<InvalidDataException>(() => overflow.Encode());

        CoverageLedgerV1 tooMany = original with
        {
            Epochs = [epoch with
            {
                Collected = [.. Enumerable.Range(0, CoverageLedgerV1.MaximumDescriptors + 1).Select(index =>
                    new CoverageCollectedV1
                    {
                        ProviderId = Provider,
                        ProviderName = "source",
                        EventId = index,
                        Version = 1,
                        Mechanism = Mechanism.Tcp,
                    })],
            }],
        };
        Assert.Throws<InvalidDataException>(() => tooMany.Encode());
    }

    [Fact(DisplayName = "R21: an absent source-loss counter is never interpreted as a reported zero")]
    public void MissingLossCounterRefuses()
    {
        CoverageLedgerV1 original = Example();
        CoverageEpochV1 epoch = Assert.Single(original.Epochs);
        CoverageLedgerV1 unknownLoss = original with { Epochs = [epoch with { Losses = [] }] };
        Assert.Throws<InvalidDataException>(() => unknownLoss.Encode());
    }

    [Fact(DisplayName = "R21: an unidentified ETL provider is counted only as a whole-provider policy omission")]
    public void EmptyProviderGuidIsOnlyAnUnrequestedBucket()
    {
        CoverageLedgerV1 original = Example();
        CoverageEpochV1 epoch = Assert.Single(original.Epochs);
        CoverageLedgerV1 omitted = original with { Epochs = [epoch with { Deliveries =
        [
            .. epoch.Deliveries,
            new CoverageDeliveryV1
            {
                ProviderId = Guid.Empty,
                Delivered = 1,
                Admitted = 0,
                Omitted = 1,
                Omission = OmissionReason.UnrequestedProvider,
            },
        ] }] };
        Assert.Equal(2, Assert.Single(CoverageLedgerV1.Decode(omitted.Encode()).Epochs).Deliveries.Count);

        CoverageLedgerV1 unidentifiedDescriptor = omitted with { Epochs = [epoch with { Deliveries =
        [new CoverageDeliveryV1
        {
            ProviderId = Guid.Empty,
            EventId = 9,
            Version = 1,
            Delivered = 1,
            Admitted = 0,
            Omitted = 1,
            Omission = OmissionReason.DescriptorNotAdmitted,
        }] }] };
        Assert.Throws<InvalidDataException>(() => unidentifiedDescriptor.Encode());
    }

    [Fact(DisplayName = "I18: a published coverage ledger survives reopen and cannot be released as a derived index")]
    public void PublishedCoverageIsReadBackAndProtectedFromRetention()
    {
        string root = Path.Combine(Path.GetTempPath(), "InterCat.Storage.Tests.Coverage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Guid sessionId = Guid.NewGuid();
            SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(root), sessionId, "coverage-test");
            string name = DerivedGenerationBuilder.CoverageLedgerFileName(1);
            using StoreStagingFile staged = store.Stage(name, StoreDependencyKind.CoverageLedger);
            staged.Content.Write(Example().Encode());
            _ = staged.Complete();
            byte[] journalBytes = Encoding.UTF8.GetBytes("journal");
            const string journalName = "journal-0000000001.icatj";
            using StoreStagingFile journal = store.Stage(journalName, StoreDependencyKind.Journal);
            journal.Content.Write(journalBytes);
            _ = journal.Complete();
            using StoreStagingFile plan = store.Stage("normalizer-plan-0000000001.json", StoreDependencyKind.DerivationPlan);
            plan.Content.Write(Encoding.UTF8.GetBytes("retained plan"));
            _ = plan.Complete();
            var boundary = new CommittedBoundary(
                journalName, journalBytes.Length, 1,
                "sha256:" + Convert.ToHexStringLower(SHA256.HashData(journalBytes)));
            _ = store.Commit([staged, journal, plan], boundary, DateTimeOffset.UtcNow);

            SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(root));
            CoverageLedgerV1 ledger = SessionSegments.CoverageLedger(reopened.Root, reopened.Current!)!;
            Assert.Equal(3, Assert.Single(Assert.Single(ledger.Epochs).Deliveries).Delivered);
            ArgumentException refusal = Assert.Throws<ArgumentException>(() =>
                reopened.ReleaseDependencies([name], "try to release coverage", DateTimeOffset.UtcNow));
            Assert.Contains("cannot release", refusal.Message, StringComparison.Ordinal);
            Assert.NotNull(SessionSegments.CoverageLedger(reopened.Root, reopened.Current!));

            _ = reopened.CommitReplacingDerived([], 1, boundary, DateTimeOffset.UtcNow);
            SessionStore replaced = SessionStore.OpenExisting(LocalOwnedDirectory.Open(root));
            Assert.Equal(2, replaced.Current!.Generation);
            Assert.Contains(replaced.Current.Dependencies, dependency => dependency.Name == name);
            Assert.Equal(3, Assert.Single(Assert.Single(SessionSegments.CoverageLedger(replaced.Root, replaced.Current)!.Epochs).Deliveries).Delivered);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CoverageLedgerV1 Example() => new()
    {
        Contract = CoverageLedgerV1.ContractName,
        Epochs =
        [
            new CoverageEpochV1
            {
                Epoch = 1,
                Acquisition = CoverageAcquisition.EtlImport,
                FirstDeliveredNativeTicks = 10,
                LastDeliveredNativeTicks = 20,
                Collected = [new CoverageCollectedV1
                {
                    ProviderId = Provider,
                    ProviderName = "network source",
                    EventId = 10,
                    Version = 1,
                    Mechanism = Mechanism.Tcp,
                }],
                Deliveries = [new CoverageDeliveryV1
                {
                    ProviderId = Provider,
                    EventId = 10,
                    Version = 1,
                    Delivered = 3,
                    Admitted = 1,
                    Omitted = 1,
                    Omission = OmissionReason.DescriptorDenied,
                    Undecodable = new Dictionary<UndecodableReason, long>
                    {
                        [UndecodableReason.BodyShorterThanSchema] = 1,
                    },
                }],
                Losses = [new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = 2 }],
            },
        ],
    };
}
