using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Domain;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// The window's RPC peers capture (ADR-035): asked for in place of Explore, kept as Explore keeps its records, and read for
/// what it admits from the plan its recorder retains, so a live session's graph draws its calls' other ends as they come.
/// </summary>
public sealed class RpcPeersCaptureTests
{
    [Fact(DisplayName = "§9.4: the window asks the broker for RPC peers in place of Explore when RPC calls are to be linked, keeping a window as Explore does")]
    public void TheWindowAsksForRpcPeers()
    {
        BrokerPrepareCaptureRequest explore = DesktopCaptureRunner.RequestFor(new CaptureRunOptions());
        Assert.Equal(("explore", 600), (explore.ProfileId, explore.Quota.MaximumDurationSeconds));
        Assert.Equal("Explore", DesktopCaptureRunner.NameOf(new CaptureRunOptions()));

        CaptureRunOptions linked = new() { LinksRpcCalls = true };
        BrokerPrepareCaptureRequest peers = DesktopCaptureRunner.RequestFor(linked);
        Assert.Equal(("rpc-peers", (Mechanism?)null, 0, false, (ContentCaptureRequest?)null),
            (peers.ProfileId, peers.FocusedMechanism, peers.FocusedProcessIds.Count, peers.AllowBroaderCapture, peers.Content));
        Assert.Equal((600, BrokerRetentionPolicy.StopAtLimit, BrokerJournalPublication.Live, (int?)null),
            (peers.Quota.MaximumDurationSeconds, peers.Retention, peers.Publication, peers.KeptWindowSeconds));
        Assert.Equal("The RPC peers capture", DesktopCaptureRunner.NameOf(linked));

        // Named as itself wherever Explore would be, within a sentence too; a content capture records its own profile.
        Assert.Equal(("Explore", "the RPC peers capture", "the content capture", "the content capture"),
            (DesktopCaptureRunner.Called(false, false), DesktopCaptureRunner.Called(false, true),
                DesktopCaptureRunner.Called(true, false), DesktopCaptureRunner.Called(true, true)));
        Assert.Equal("The content capture", DesktopCaptureRunner.NameOf(linked with
        {
            Content = new([], 4_096, 16L * 1_024 * 1_024, ContentInspectionMode.Disabled, 600),
        }));

        // Kept to the last ten minutes, it records until stopped and the broker releases what the session gave up.
        BrokerPrepareCaptureRequest rolling = DesktopCaptureRunner.RequestFor(CaptureKeepChoice.All[1].Options(null) with
        {
            LinksRpcCalls = true,
        });
        Assert.Equal(("rpc-peers", CaptureKeepChoice.UntilStoppedSeconds, BrokerRetentionPolicy.ReleaseFollowed, (int?)600),
            (rolling.ProfileId, rolling.Quota.MaximumDurationSeconds, rolling.Retention, rolling.KeptWindowSeconds));
    }

    [Fact(DisplayName = "§7.4: what a capture admits is read from the normalizer plan its recorder retains, ALPC's classic descriptors included")]
    public void WhatACaptureAdmitsIsReadFromItsPlan()
    {
        // RPC peers' plan as its recorder encodes it: lifecycle and RPC calls from their manifests, and ALPC's send and
        // receive, classic descriptors of one class told apart by opcode (`normalizer-plan-v1`).
        Guid alpc = Guid.Parse("45d8cccd-539f-4b72-a8b7-5c683142609a");
        JournalNormalizationPlanV1 plan = JournalNormalizationPlanV1.FromSources(
        [
            Source(WindowsSourceCatalog.KernelProcessSourceId, Event(0, Guid.Parse("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716"), 1,
                null, Mechanism.ProcessLifecycle)),
            Source(WindowsSourceCatalog.RpcSourceId, Event(1, Guid.Parse("6ad52b32-d609-4be9-ae07-ce8dae937e39"), 5, null,
                Mechanism.Rpc)),
            Source(WindowsSourceCatalog.KernelAlpcSourceId, Event(2, alpc, 0, 33, Mechanism.Alpc),
                Event(2, alpc, 0, 34, Mechanism.Alpc)),
        ]);

        using var session = new TemporarySession();
        Publish(session.Store, [Lifecycle(10, ObservationKind.Create, 100, 1)], plan: plan.Encode());
        Assert.Equal(new HashSet<Mechanism> { Mechanism.ProcessLifecycle, Mechanism.Rpc, Mechanism.Alpc },
            CaptureAdmissions.Of(session.Store.Root, session.Store.Current!));
    }

    private static SourceAdmissionPlan Source(string sourceId, params AdmittedEventPlan[] events) => new()
    {
        SourceId = sourceId,
        ProviderGuid = events[0].ProviderGuid,
        SourceIndex = events[0].SourceIndex,
        Events = events,
        Diagnostics = [],
    };

    private static AdmittedEventPlan Event(int source, Guid provider, int eventId, int? opcode, Mechanism mechanism) => new()
    {
        SourceIndex = source,
        ProviderGuid = provider,
        EventId = eventId,
        Opcode = opcode,
        Version = opcode is null ? 0 : 2,
        Name = "fixture",
        Mechanism = mechanism,
        Layer = ObservationLayer.Transport,
        Kind = ObservationKind.Discovery,
        Direction = Direction.DirectionNotApplicable,
        MinimumBodyLength = 0,
        SchemaFingerprint = $"sha256:fixture-{source}",
        BodyPolicy = CaptureBodyAdmissionPolicies.MetadataOnly,
        Slots = [],
        FieldReport = [],
    };
}
