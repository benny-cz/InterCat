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
    public async Task AStepBeforeAnyPublicationFollowsNothing()
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
    }

    [Fact(DisplayName = "§12: halting the live derivation settles a running step and runs no later one")]
    public async Task HaltingSettlesARunningStepAndRunsNoLaterOne()
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
    }

    [Fact(DisplayName = "§20.1: the store a capture derives into is its session's shared store, so the window reads through it")]
    public async Task TheDerivedStoreIsTheSessionsSharedStore()
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
    }

    [Fact(DisplayName = "I14: a finished live follow publishes the session's derivation checkpoint and shows it as the next generation")]
    public async Task AFinishedFollowPublishesTheCheckpoint()
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
    }
}
