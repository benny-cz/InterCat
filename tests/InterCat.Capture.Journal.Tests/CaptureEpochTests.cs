using System.Diagnostics;
using InterCat.Analysis;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Capture.Journal.Tests.EvidenceRecordings;

namespace InterCat.Capture.Journal.Tests;

/// <summary>
/// A capture's configuration is fixed when it starts (P13, §9.3). A provider its session would not enable is said as the
/// capture starts, and what the capture publishes does not count its descriptors as collected: its mechanism reads as not
/// collected rather than as covered and quiet, and the capture's one coverage epoch speaks only for what was enabled
/// (`contracts/coverage-v2.md` §2, §4, R21).
/// </summary>
public sealed class CaptureEpochTests
{
    private const string PipeSource = "etw/manifest/SamplePipe";
    private static readonly Guid PipeProvider = Guid.Parse("4f1c2a33-6b7d-4e8f-9a0b-1c2d3e4f5a6b");

    [Fact(DisplayName = "P13: a provider refused as a capture starts is said, and the capture's one coverage epoch does not count it collected")]
    public async Task ARefusedProviderIsSaidAndNotCountedCollected()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "epoch-tests");
        var host = new ScriptedHost { Refused = new HashSet<string>([PipeSource], StringComparer.Ordinal) };
        host.Admit(new AdmittedEvent
        {
            SourceIndex = 0,
            EventId = 10,
            Version = 0,
            TimestampQpc = Stopwatch.GetTimestamp(),
            TimestampUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
            RecordOrdinal = 1,
        });

        LiveRecordingResult result = await LiveSessionRecorder.RecordAsync(
            TwoProviders(), host, store, _ => host.Delivered.Task, DateTimeOffset.UtcNow);

        // The capture started on the provider that was enabled, and says which one was refused and why.
        Assert.True(result.Start.Started);
        Assert.Equal(
            [("etw/manifest/Sample", true), (PipeSource, false)],
            result.Start.Providers.Select(provider => (provider.SourceId, provider.Enabled)));
        Assert.Equal($"{PipeSource}: access is denied in this test", Assert.Single(result.Start.Degradations));

        // It publishes one epoch, which collects only the enabled provider's descriptors: the refused provider's mechanism
        // is not collected, never covered with nothing recorded, and the enabled one's is covered.
        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory.Path));
        CoverageLedgerV1 ledger = SessionSegments.CoverageLedger(reopened.Root, reopened.Current!)!;
        CoverageEpochV1 epoch = Assert.Single(ledger.Epochs);
        Assert.Equal(CoverageAcquisition.LiveCapture, epoch.Acquisition);
        Assert.Equal([Provider], epoch.Collected.Select(descriptor => descriptor.ProviderId).Distinct());
        Assert.Equal(CoverageState.NotCollected, SessionCoverage.Of(ledger, Mechanism.NamedPipe).State);
        Assert.Equal(CoverageState.Covered, SessionCoverage.Of(ledger, Mechanism.Tcp).State);
    }

    /// <summary>The recordings' plan, with a pipe provider beside its network one.</summary>
    private static OwnedSessionPlan TwoProviders()
    {
        OwnedSessionPlan plan = Plan();
        var pipe = new ProviderEnablementRequest
        {
            SourceId = PipeSource,
            ProviderName = "Sample-Pipe",
            ProviderGuid = PipeProvider,
            Level = 4,
            MatchAnyKeyword = 0x20,
            EventIdsToEnable = [20],
        };

        return plan with
        {
            Providers = [.. plan.Providers, pipe],
            Sources =
            [
                .. plan.Sources,
                new()
                {
                    SourceId = PipeSource,
                    ProviderGuid = PipeProvider,
                    SourceIndex = 1,
                    Events =
                    [
                        new()
                        {
                            SourceIndex = 1,
                            ProviderGuid = PipeProvider,
                            EventId = 20,
                            Version = 0,
                            Name = "written",
                            Mechanism = Mechanism.NamedPipe,
                            Layer = ObservationLayer.Transport,
                            Kind = ObservationKind.Send,
                            Direction = Direction.Outbound,
                            MinimumBodyLength = 0,
                            SchemaFingerprint = "sha256:fixture-refused-pipe",
                            BodyPolicy = CaptureBodyAdmissionPolicies.MetadataOnly,
                            Slots = [],
                            FieldReport = [],
                        },
                    ],
                    Diagnostics = [],
                },
            ],
        };
    }
}
