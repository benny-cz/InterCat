using System.Diagnostics;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Capture.Journal.Tests;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// The Desktop runner's follow-and-project step, which runs off the loop that reads status, the live preview and renews
/// the owner lease (§12, §19.3). Its real-ETW behaviour is measured by the bounded first-feedback qualification.
/// </summary>
public sealed class LiveDerivationTests
{
    [Fact(DisplayName = "§12: a live step before the broker publishes anything follows nothing and creates no session")]
    public void AStepBeforeAnyPublicationFollowsNothing() => SingleThreadedContext.Run(async () =>
    {
        using var evidence = new TemporarySession();
        using var user = new TemporarySession();
        string sessionPath = Path.Combine(user.Path, "explore");
        using var derivation = new LiveDerivation(evidence.Path, sessionPath, Stopwatch.StartNew());

        LiveDerivationStep step = await derivation.StepAsync(CancellationToken.None);

        Assert.Null(step.Follow);
        Assert.Null(step.Generation);
        Assert.Null(step.Overview);
        Assert.Null(derivation.Last);
        Assert.False(derivation.HasSession);
        Assert.False(Directory.Exists(sessionPath));
    });

    [Fact(DisplayName = "§12: halting the live derivation settles a running step and runs no later one")]
    public void HaltingSettlesARunningStepAndRunsNoLaterOne() => SingleThreadedContext.Run(async () =>
    {
        using var evidence = new TemporarySession();
        using var user = new TemporarySession();
        using var derivation = new LiveDerivation(evidence.Path, Path.Combine(user.Path, "explore"), Stopwatch.StartNew());
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        // A step cancelled by the stop request is settled, not rethrown: the runner has already said how it ended.
        Task<LiveDerivationStep> stopped = derivation.StepAsync(stop.Token);
        await derivation.HaltAsync(stopped);
        Assert.True(stopped.IsCanceled);

        // Once halted, a step does nothing, even under a token that was never cancelled.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => derivation.StepAsync(CancellationToken.None));
        Assert.Null(derivation.Last);
    });

    [Fact(DisplayName = "§20.1: the store a capture derives into is its session's shared store, so the window reads through it")]
    public void TheDerivedStoreIsTheSessionsSharedStore() => SingleThreadedContext.Run(async () =>
    {
        using var evidence = new TemporaryDirectory();
        using var user = new TemporarySession();
        using var shown = new TemporarySession();
        Publish(shown.Store, [Lifecycle(1, ObservationKind.Create, 100, 1) with { SessionRelativeTicks = 100 }]);
        _ = await EvidenceRecordings.RecordEvidence(evidence.Path, ordinals: [1, 2, 3, 4]);
        string sessionPath = Path.Combine(user.Path, "explore");

        // A registry of its own, as the window's would be: it shows a saved session when the capture begins.
        var stores = new SessionStoreRegistry(capacity: 4);
        SessionStore saved = stores.Open(shown.Path);
        foreach (string name in SessionSegments.Names(saved.Current!))
        {
            _ = SessionSegments.Open(saved, saved.Current!, name);
        }

        using var derivation = new LiveDerivation(evidence.Path, sessionPath, Stopwatch.StartNew(), stores);
        LiveDerivationStep step = await derivation.StepAsync(CancellationToken.None);

        Assert.True(step.Follow!.Finished);
        SessionStore derived = Assert.IsType<SessionStore>(derivation.Store);
        Assert.Same(derived, stores.Open(sessionPath, derived.SessionId));
        Assert.Same(derived, stores.Open(sessionPath.ToUpperInvariant()));

        // Projecting the new generation read through that store, and the saved session no longer holds readers.
        Assert.NotEqual(0, derived.SegmentReaderCache.Entries);
        Assert.Equal(0, saved.SegmentReaderCache.Entries);
    });

    [Fact(DisplayName = "§6.8: a live publication is projected under the evidence policy the window holds as it is projected")]
    public void ALivePublicationIsProjectedUnderTheWindowsPolicy() => SingleThreadedContext.Run(async () =>
    {
        using var evidence = new TemporaryDirectory();
        using var user = new TemporarySession();
        _ = await EvidenceRecordings.RecordEvidence(evidence.Path, ordinals: [1, 2, 3, 4]);

        // The policy is asked for as the publication is projected, not when the follow began.
        EvidencePolicy held = EvidencePolicy.IncludeCorrelated;
        using var derivation = new LiveDerivation(evidence.Path, Path.Combine(user.Path, "explore"), Stopwatch.StartNew(),
            new SessionStoreRegistry(capacity: 4), () => held);
        held = EvidencePolicy.IncludeCandidates;
        LiveDerivationStep step = await derivation.StepAsync(CancellationToken.None);
        Assert.Equal(EvidencePolicy.IncludeCandidates, step.Overview!.Policy);

        // With no policy given, a publication counts correlated evidence, the default.
        using var plain = new TemporarySession();
        using var defaulted = new LiveDerivation(evidence.Path, Path.Combine(plain.Path, "explore"), Stopwatch.StartNew(),
            new SessionStoreRegistry(capacity: 4));
        Assert.Equal(EvidencePolicy.IncludeCorrelated, (await defaulted.StepAsync(CancellationToken.None)).Overview!.Policy);
    });

    [Fact(DisplayName = "I14: a finished live follow publishes the session's derivation checkpoint and shows it as the next generation")]
    public void AFinishedFollowPublishesTheCheckpoint() => SingleThreadedContext.Run(async () =>
    {
        using var evidence = new TemporaryDirectory();
        using var user = new TemporarySession();
        _ = await EvidenceRecordings.RecordEvidence(evidence.Path, ordinals: [1, 2, 3, 4]);
        using var derivation = new LiveDerivation(
            evidence.Path, Path.Combine(user.Path, "explore"), Stopwatch.StartNew(), new SessionStoreRegistry(capacity: 4));

        LiveDerivationStep step = await derivation.StepAsync(CancellationToken.None);
        Assert.True(step.Follow!.Finished);
        CheckpointPublication checkpoint = Assert.IsType<CheckpointPublication>(derivation.Checkpoint);
        Assert.Equal(CheckpointOutcome.Published, checkpoint.Outcome);
        Assert.Equal(step.Follow.DerivedGeneration, checkpoint.SourceGeneration);
        Assert.Equal(checkpoint.PublishedGeneration, step.Generation);
        Assert.Equal(checkpoint.PublishedGeneration, step.Overview!.Generation);
        Assert.NotNull(DerivationCheckpoint.NamedBy(derivation.Store!.Current!));

        // Nothing more is published once the session holds everything, however often the loop steps.
        LiveDerivationStep again = await derivation.StepAsync(CancellationToken.None);
        Assert.Null(again.Generation);
        Assert.Equal(checkpoint.PublishedGeneration, derivation.Store.Current!.Generation);
    });

    [Fact(DisplayName = "S5: a live capture keeping a window releases the records read before it between mirrors, and its overview says from when it keeps every record")]
    public void ALiveCaptureKeepingAWindowReleasesBeforeIt() => SingleThreadedContext.Run(async () =>
    {
        using var evidence = new TemporaryDirectory();
        using var user = new TemporarySession();

        // Three chunks of records a second apart: at 0 and 1 s, 2 and 3 s, 4 and 5 s.
        _ = await EvidenceRecordings.RecordEvidence(evidence.Path, ordinals: [1, 2, 3, 4, 5, 6], bursts: [2, 4],
            qpcStep: Stopwatch.Frequency);
        var rolling = new RollingRetention(new RollingRetentionPolicy(TimeSpan.FromSeconds(1)));
        using var derivation = new LiveDerivation(evidence.Path, Path.Combine(user.Path, "explore"), Stopwatch.StartNew(),
            new SessionStoreRegistry(capacity: 4), rolling: rolling);

        // The step mirrors every chunk, then releases the two read wholly before the newest second, by the follow's own
        // writer, and projects what the session keeps: the overview says from when it keeps every record.
        LiveDerivationStep step = await derivation.StepAsync(CancellationToken.None);
        Assert.True(step.Follow!.Finished);
        IntervalReleaseResult released = Assert.IsType<IntervalReleaseResult>(step.Rolling?.Released);
        Assert.Equal(2, released.Preview.ReleasedUnits);
        Assert.Same(rolling, derivation.Rolling);
        Assert.Equal(1, rolling.Releases);
        SessionManifestV1 kept = derivation.Store!.Current!;
        Assert.Equal("rolling retention keeps the last second", kept.LatestRelease(RetentionExtentKind.Interval)!.Record.Reason);
        Assert.Equal(SessionGrowth.Retained(kept), step.Overview!.Retained);
        Assert.StartsWith("Kept from ", step.Overview.Retained, StringComparison.Ordinal);
        Assert.EndsWith("(rolling retention keeps the last second).", step.Overview.Retained, StringComparison.Ordinal);
        Assert.Equal("Keeps the last second of session time: 1 release so far.", DesktopCaptureRunner.Keeps(rolling));

        // A capture that keeps every record says nothing of a window, and releases nothing.
        using var whole = new TemporarySession();
        using var everything = new LiveDerivation(evidence.Path, Path.Combine(whole.Path, "explore"), Stopwatch.StartNew(),
            new SessionStoreRegistry(capacity: 4));
        LiveDerivationStep all = await everything.StepAsync(CancellationToken.None);
        Assert.Null(all.Rolling);
        Assert.Null(everything.Rolling);
        Assert.Null(all.Overview!.Retained);
        Assert.Null(DesktopCaptureRunner.Keeps(null));
    });

    [Fact(DisplayName = "S5: a live capture a pin holds past its window says so, and must stop once the session holds more than the pin allows")]
    public void APinStopsALiveCaptureOnceOutgrown() => SingleThreadedContext.Run(async () =>
    {
        using var evidence = new TemporaryDirectory();
        using var user = new TemporarySession();
        _ = await EvidenceRecordings.RecordEvidence(evidence.Path, ordinals: [1, 2, 3, 4, 5, 6], bursts: [2, 4],
            qpcStep: Stopwatch.Frequency);
        SessionManifestV1 source = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidence.Path)).Current!;

        // A pin from half a second allowing a kilobyte, placed when the session was small, which it has since outgrown.
        string sessionPath = Path.Combine(user.Path, "explore");
        Directory.CreateDirectory(sessionPath);
        var pin = new RetentionPin
        {
            Id = Guid.NewGuid(),
            FromNanoseconds = 500_000_000,
            AllowanceBytes = 1_024,
            PlacedUtc = DateTimeOffset.UtcNow,
            Reason = "the first burst",
        };
        File.WriteAllText(Path.Combine(sessionPath, RetentionPinsV1.FileName), System.Text.Json.JsonSerializer.Serialize(
            new RetentionPinsV1 { FormatVersion = RetentionPinsV1.CurrentFormatVersion, SessionId = source.SessionId, Pins = [pin] },
            SessionManifestV1.Json));
        var rolling = new RollingRetention(new RollingRetentionPolicy(TimeSpan.FromSeconds(1)));
        using var derivation = new LiveDerivation(evidence.Path, sessionPath, Stopwatch.StartNew(),
            new SessionStoreRegistry(capacity: 4), rolling: rolling);

        // The release due before the newest second is held at the pin, which releases nothing before it, and the session
        // holds more than the pin allows: the step says the capture must stop, and why.
        LiveDerivationStep step = await derivation.StepAsync(CancellationToken.None);
        RollingStep held = Assert.IsType<RollingStep>(step.Rolling);
        Assert.Null(held.Released);
        Assert.Equal((pin.Id, true), (held.HeldBy!.Id, held.NewlyHeld));
        // The size is measured as the step ran, before the finished session's checkpoint was published beside it.
        Assert.StartsWith($"The pin from 0.500 s (the first burst) allows this session {ByteSizeText.Of(1_024)}, and it holds ",
            held.Stop, StringComparison.Ordinal);
        Assert.EndsWith(": the records the pin keeps cannot be released, and the session cannot grow past what it allows.",
            held.Stop, StringComparison.Ordinal);
        Assert.Equal(held.Stop, rolling.Stopped);
        Assert.Equal(RetentionPinText.HoldsWindow(held.HeldBy, rolling.Policy), DesktopCaptureRunner.Keeps(rolling));
        Assert.StartsWith("To record on, remove the pin", DesktopCaptureRunner.PinGoOn, StringComparison.Ordinal);
    });
}
