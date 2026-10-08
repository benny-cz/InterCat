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

    [Fact(DisplayName = "R5: a ledger's acquisition, losses and omissions each read in one set of words, the ones its coverage reasons use")]
    public void ALedgersFactsReadInOneSetOfWords()
    {
        Assert.Equal(["ETL import", "live capture"], Enum.GetValues<CoverageAcquisition>().Select(CoverageLedgerText.Acquisition));
        Assert.Equal(["a provider the capture did not request", "not in the profile's allowlist", "denied by the profile"],
            Enum.GetValues<OmissionReason>().Select(CoverageLedgerText.Omission));
        Assert.Equal(
        [
            "the session reported 1 lost event, which may be any mechanism's",
            "the consumer lost 2 buffers of unknown size",
            "InterCat's full queue dropped 3 records",
            "4 admitted records could not be stored",
        ], Enum.GetValues<LossLayer>().Select((layer, index) =>
            CoverageLedgerText.Loss(new CoverageLossV1 { Layer = layer, Lost = index + 1 }, CoverageAcquisition.LiveCapture)));

        // An imported file reported its own losses; a reported zero is said, never left out (R21).
        Assert.Equal("the file reported 0 lost events, which may be any mechanism's", CoverageLedgerText.Loss(
            new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = 0 }, CoverageAcquisition.EtlImport));

        // A value this version does not know is named by its number, never guessed.
        Assert.Equal("acquisition 9", CoverageLedgerText.Acquisition((CoverageAcquisition)9));
        Assert.Equal("omission 9", CoverageLedgerText.Omission((OmissionReason)9));
        Assert.Equal("1,500 records lost at layer 9", CoverageLedgerText.Loss(
            new CoverageLossV1 { Layer = (LossLayer)9, Lost = 1_500 }, CoverageAcquisition.LiveCapture));

        // A coverage reason says a loss in the same words.
        CoverageLedgerV1 ledger = Ledger(CoverageAcquisition.EtlImport);
        CoverageLossV1 lost = new() { Layer = LossLayer.SourceSession, Lost = 1_200 };
        CoverageLedgerV1 lossy = ledger with { Epochs = [Assert.Single(ledger.Epochs) with { Losses = [lost] }] };
        Assert.Equal(CoverageLedgerText.Loss(lost, CoverageAcquisition.EtlImport), SessionCoverage.Of(lossy, Mechanism.Tcp).Reason);
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

    [Fact(DisplayName = "R5: a mechanism's coverage is said in one sentence form wherever a layer states it, never as an enumeration's name")]
    public void AMechanismsCoverageIsSaidInWords()
    {
        // Each state in its own words, the mechanism as a sentence names it and the fact that decided it last.
        Assert.Equal("TCP was covered over the selected interval: 2 records from its 1 admitted descriptor, and nothing was "
            + "reported lost.", SessionCoverage.Sentence(new(Mechanism.Tcp, CoverageState.Covered,
                "2 records from its 1 admitted descriptor, and nothing was reported lost"), "the selected interval"));
        Assert.Equal("UDP was captured at reduced fidelity over this scope: sampled.",
            SessionCoverage.Sentence(new(Mechanism.Udp, CoverageState.ReducedFidelity, "sampled"), "this scope"));
        Assert.Equal("Process lifecycle has a partial gap over this scope, not extrapolated: the session reported 1 lost "
            + "event, which may be any mechanism's.", SessionCoverage.Sentence(new(Mechanism.ProcessLifecycle,
                CoverageState.PartialGap, "the session reported 1 lost event, which may be any mechanism's"), "this scope"));
        Assert.Equal("RPC was not collected over the session: no admitted descriptor records it.", SessionCoverage.Sentence(
            new(Mechanism.Rpc, CoverageState.NotCollected, "no admitted descriptor records it"), "the session"));
        Assert.Equal("HTTP's coverage over this scope is unknown: outside the readings the capture's sources delivered.",
            SessionCoverage.Sentence(new(Mechanism.Http, CoverageState.UnknownCoverage,
                "outside the readings the capture's sources delivered"), "this scope"));

        // A lane names a lifecycle in one word; a sentence names it as the process's or the thread's.
        Assert.Equal(("Process", "process lifecycle"),
            (MechanismText.Name(Mechanism.ProcessLifecycle), MechanismText.InSentence(Mechanism.ProcessLifecycle)));
        Assert.Equal(("Thread", "thread lifecycle"),
            (MechanismText.Name(Mechanism.ThreadLifecycle), MechanismText.InSentence(Mechanism.ThreadLifecycle)));
        Assert.Equal("TCP", MechanismText.InSentence(Mechanism.Tcp));

        // An answer over logical operations states RPC's coverage, the only one its calls rest on, in the same words.
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = TestSessions.LinkedRpcCalls();
        TestSessions.Publish(session.Store, rows, fields: fields, coverage: TestSessions.RpcLedger(alpc: false));
        MetricResult completed = SessionMetrics.Evaluate(session.Store, new MetricRequest
        {
            Basis = AnalysisBasis.LogicalOperations,
            Metric = Metric.OperationsCompleted,
        });
        Assert.Contains("Every operation here is an RPC call, so only RPC's coverage applies. RPC's coverage over the selected "
            + "scope is unknown: its 1 admitted descriptor delivered nothing, and a file cannot show whether its session "
            + "recorded them.", completed.Caveats);
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

    [Fact(DisplayName = "R21: a scope reaching before an interval release's boundary is a partial gap that says so, never covered")]
    public void AReleasedIntervalIsAPartialGap()
    {
        CoverageLedgerV1 ledger = Ledger(CoverageAcquisition.LiveCapture) with { ReleasedBefore = new LedgerRelease(15, 1_500) };
        const string Released = "the records read before 0.0000015 s were released by retention, but for those kept as the evidence of later ones";

        // Before the boundary, or across it, a covered mechanism is a partial gap for that reason; wholly after it, as the
        // epochs say. A worse state stays: a release makes nothing better known.
        MechanismCoverage across = SessionCoverage.Of(ledger, Mechanism.Tcp, new TimeRange(10, 16));
        Assert.Equal((CoverageState.PartialGap, Released), (across.State, across.Reason));
        Assert.Equal(CoverageState.Covered, SessionCoverage.Of(ledger, Mechanism.Tcp, new TimeRange(15, 21)).State);
        Assert.Equal((CoverageState.PartialGap, Released), (SessionCoverage.Of(ledger, Mechanism.Tcp).State, SessionCoverage.Of(ledger, Mechanism.Tcp).Reason));
        Assert.Equal(CoverageState.NotCollected, SessionCoverage.Of(ledger, Mechanism.NamedPipe, new TimeRange(10, 16)).State);
        Assert.Equal(CoverageState.UnknownCoverage, SessionCoverage.Of(ledger, Mechanism.Tcp, new TimeRange(9, 16)).State);
        Assert.Equal([CoverageState.PartialGap, CoverageState.Covered],
            SessionCoverage.CaptureStates(ledger, [new TimeRange(10, 16), new TimeRange(15, 21)]));
        Assert.Equal(CoverageState.PartialGap, SessionCoverage.Capture(ledger));
        Assert.Equal(CoverageState.Covered, SessionCoverage.Capture(ledger with { ReleasedBefore = null }));

        // A loss in the same scope is said after the release.
        CoverageEpochV1 epoch = Assert.Single(ledger.Epochs);
        CoverageLedgerV1 lossy = ledger with { Epochs = [epoch with { Losses = [.. epoch.Losses.Select(loss => loss with { Lost = loss.Layer == LossLayer.SourceSession ? 3 : 0 })] }] };
        MechanismCoverage both = SessionCoverage.Of(lossy, Mechanism.Tcp);
        Assert.Equal(CoverageState.PartialGap, both.State);
        Assert.StartsWith(Released + "; ", both.Reason, StringComparison.Ordinal);
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
