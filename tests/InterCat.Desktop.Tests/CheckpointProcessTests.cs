using System.Diagnostics;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Capture.Journal.Tests;
using InterCat.Desktop;
using InterCat.Storage;
using Xunit;

namespace InterCat.Desktop.Tests;

/// <summary>
/// A finished capture's derivation checkpoint, published in a process of its own (revision 884): the Desktop launches
/// itself to publish it, so it reads every segment within a heap of its own rather than beside all the window holds, and
/// the window's store reads the generation it published. A checkpoint only saves time, so a child that cannot start, ends
/// without saying what it published, or is cancelled fails nothing.
/// </summary>
public sealed class CheckpointProcessTests
{
    /// <summary>The Desktop as these tests' output holds it, launched as the window launches itself.</summary>
    private static CheckpointLaunchTarget Desktop => new(
        Path.Combine(AppContext.BaseDirectory, "InterCat.Desktop.exe"), [CheckpointLaunchTarget.DesktopArgument]);

    [Fact(DisplayName = "§12: a finished capture's checkpoint is published by the Desktop launched as a process of its own, and the window's store shows the generation it published")]
    public void AFinishedCapturesCheckpointIsPublishedApart() => SingleThreadedContext.Run(async () =>
    {
        using var evidence = new TemporaryDirectory();
        using var user = new TemporarySession();
        _ = await EvidenceRecordings.RecordEvidence(evidence.Path, ordinals: [1, 2, 3, 4]);
        string sessionPath = Path.Combine(user.Path, "explore");
        using var derivation = new LiveDerivation(evidence.Path, sessionPath, Stopwatch.StartNew(),
            new SessionStoreRegistry(capacity: 4), checkpointer: Desktop);

        LiveDerivationStep step = await derivation.StepAsync(CancellationToken.None);

        Assert.True(step.Follow!.Finished);
        CheckpointPublication published = Assert.IsType<CheckpointPublication>(derivation.Checkpoint);
        Assert.Equal((CheckpointOutcome.Published, (string?)null), (published.Outcome, published.Reason));
        Assert.Equal(published.PublishedGeneration, derivation.Store!.Current!.Generation);
        Assert.Equal(published.PublishedGeneration, step.Generation);
        Assert.NotNull(step.Overview);
        Assert.NotNull(OperationIndex.NamedBy(derivation.Store.Current));

        // Launched again, the child finds the generation current, as icat checkpoint does.
        CheckpointPublication again = CheckpointProcess.Publish(Desktop, sessionPath, CancellationToken.None);
        Assert.Equal(CheckpointOutcome.AlreadyCurrent, again.Outcome);

        // Given a child that cannot start, the capture publishes none rather than one of its own, and shows what it
        // followed: the checkpoint is the child's, never this process's.
        using var unstarted = new TemporarySession();
        string elsewhere = Path.Combine(unstarted.Path, "explore");
        using var refused = new LiveDerivation(evidence.Path, elsewhere, Stopwatch.StartNew(), new SessionStoreRegistry(capacity: 4),
            checkpointer: new(Path.Combine(unstarted.Path, "missing.exe"), []));
        LiveDerivationStep shown = await refused.StepAsync(CancellationToken.None);
        Assert.Equal(CheckpointOutcome.Refused, refused.Checkpoint!.Outcome);
        Assert.Null(OperationIndex.NamedBy(refused.Store!.Current!));
        Assert.Equal(refused.Store.Current!.Generation, shown.Generation);
    });

    [Fact(DisplayName = "§12: a checkpoint's child says what it published as one line of JSON, the window reads it back whole, and a directory holding no session is nothing to derive")]
    public void AChildSaysWhatItPublished()
    {
        using var session = new TemporarySession();
        TestSessions.Publish(session.Store, [TestSessions.Lifecycle(1, Domain.ObservationKind.Create, 100, 1)]);
        using var said = new StringWriter();
        Assert.Equal(0, CheckpointProcess.Run(session.Path, said, CancellationToken.None));
        string line = Assert.Single(said.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.Contains("\"outcome\":\"Published\"", line, StringComparison.Ordinal);

        // The window reads what the Desktop's own child says, the same report.
        CheckpointPublication current = CheckpointProcess.Publish(Desktop, session.Path, CancellationToken.None);
        Assert.Equal((CheckpointOutcome.AlreadyCurrent, SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path)).Current!.Generation),
            (current.Outcome, current.SourceGeneration));

        using var empty = new TemporaryDirectory();
        using var nothing = new StringWriter();
        Assert.Equal(0, CheckpointProcess.Run(empty.Path, nothing, CancellationToken.None));
        Assert.Contains("\"outcome\":\"NothingToDerive\"", nothing.ToString(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§12: a checkpoint's child that cannot start, or ends without saying what it published, publishes nothing and says why, and a cancelled one is ended")]
    public void AChildThatSaysNothingPublishesNothing()
    {
        using var session = new TemporarySession();
        TestSessions.Publish(session.Store, [TestSessions.Lifecycle(1, Domain.ObservationKind.Create, 100, 1)]);
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);

        CheckpointPublication missing = CheckpointProcess.Publish(
            new(Path.Combine(session.Path, "missing.exe"), []), session.Path, CancellationToken.None);
        Assert.Equal(CheckpointOutcome.Refused, missing.Outcome);
        Assert.StartsWith("Its process could not be started: ", missing.Reason, StringComparison.Ordinal);

        CheckpointPublication silent = CheckpointProcess.Publish(
            new(Path.Combine(system, "cmd.exe"), ["/c", "exit", "3"]), session.Path, CancellationToken.None);
        Assert.Equal(CheckpointOutcome.Refused, silent.Outcome);
        Assert.Equal("Its process ended with exit code 3 before saying what it published.", silent.Reason);

        // Cancelled, the Desktop's child is ended before it publishes, and the session stays as it was: the child left to
        // run would publish within a second or two.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        _ = Assert.ThrowsAny<OperationCanceledException>(() => CheckpointProcess.Publish(Desktop, session.Path, cancelled.Token));
        for (long until = Environment.TickCount64 + 3_000; Environment.TickCount64 < until; Thread.Sleep(100))
        {
            Assert.Null(OperationIndex.NamedBy(SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path)).Current!));
        }
    }
}
