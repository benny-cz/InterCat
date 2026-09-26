using System.Text.Json;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// A finished session publishes its derivation checkpoint, and opening it again builds its processes and relationships
/// from the checkpoint instead of from every record (`contracts/derivation-checkpoint-v1.md`, §12.1 S1). What the
/// window shows must be exactly what a derivation from every segment shows, and a checkpoint that cannot be used costs
/// time and a caveat, never an answer.
/// </summary>
[Collection(SharedDerivationCache.Name)]
public sealed class DerivationCheckpointOverviewTests
{
    [Fact(DisplayName = "I14: a session reopened from its derivation checkpoint projects exactly as a derivation of every segment")]
    public void AReopenedSessionProjectsFromItsCheckpoint()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        foreach (ObservationRowV1[] chunk in IncrementalOverviewTests.LiveChunks())
        {
            Publish(session.Store, chunk);
        }

        long written = session.Store.Current!.Generation;
        SessionOverviewBundle full = SessionOverviewProjector.Project(session.Store);
        string derived = Comparable(full);
        CheckpointPublication publication = SessionCheckpoints.Publish(session.Store, Committed);
        Assert.Equal((CheckpointOutcome.Published, written, written + 1), (publication.Outcome, publication.SourceGeneration, publication.PublishedGeneration));
        Assert.True(publication.Bytes > 0);
        Assert.Equal(CheckpointOutcome.AlreadyCurrent, SessionCheckpoints.Publish(session.Store, Committed).Outcome);

        // A fresh viewer, with nothing derived in memory, takes both derivations from the checkpoint as they stand.
        SessionDerivationCache.Clear();
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        SessionOverviewBundle overview = SessionOverviewProjector.Project(reopened);
        SessionDerivation slot = SessionDerivationCache.For(reopened.Current!);
        Assert.Equal((true, true, null), (slot.ProcessesFromCheckpoint, slot.RelationsFromCheckpoint, slot.CheckpointProblem));
        Assert.Equal(written + 1, overview.Generation);
        Assert.Equal(derived, Comparable(overview));
        Assert.Equal(full.Caveats, overview.Caveats);

        // A generation that adds segments names no checkpoint: it is derived, until its writer publishes one of its own.
        Publish(session.Store, [Transfer(900, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1_000)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 90_000 }]);
        SessionDerivationCache.Clear();
        SessionOverviewBundle added = SessionOverviewProjector.Project(session.Store);
        slot = SessionDerivationCache.For(session.Store.Current!);
        Assert.Equal((false, false), (slot.ProcessesFromCheckpoint, slot.RelationsFromCheckpoint));
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);
        SessionDerivationCache.Clear();
        Assert.Equal(Comparable(added), Comparable(SessionOverviewProjector.Project(session.Store)));
        slot = SessionDerivationCache.For(session.Store.Current!);
        Assert.Equal((true, true), (slot.ProcessesFromCheckpoint, slot.RelationsFromCheckpoint));
    }

    [Fact(DisplayName = "I14: a checkpoint that cannot be read is derived around, the overview says why, and publishing replaces it")]
    public void AnUnreadableCheckpointIsDerivedAround()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        foreach (ObservationRowV1[] chunk in IncrementalOverviewTests.LiveChunks())
        {
            Publish(session.Store, chunk);
        }

        SessionOverviewBundle full = SessionOverviewProjector.Project(session.Store);
        string derived = Comparable(full);

        // Its generation records its digest, so only a checkpoint written wrong - by another format, say - reads this far.
        long next = session.Store.NextGeneration;
        using (StoreStagingFile staged = session.Store.Stage(DerivationCheckpoint.FileNameFor(next), StoreDependencyKind.Index))
        {
            staged.Content.Write("ICATDCKP not a checkpoint"u8);
            _ = staged.Complete();
            _ = session.Store.CommitIndex([staged], session.Store.Current!.Generation, Committed, next);
        }

        SessionDerivationCache.Clear();
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        SessionDerivation slot = SessionDerivationCache.For(session.Store.Current!);
        Assert.Equal((false, false), (slot.ProcessesFromCheckpoint, slot.RelationsFromCheckpoint));
        Assert.Contains("not readable", slot.CheckpointProblem, StringComparison.Ordinal);
        Assert.Equal(derived, Comparable(overview));
        string caveat = Assert.Single(overview.Caveats.Except(full.Caveats));
        Assert.Equal(full.Caveats, overview.Caveats.Where(line => line != caveat));
        Assert.Contains(slot.CheckpointProblem!, caveat, StringComparison.Ordinal);
        Assert.Contains("same result", caveat, StringComparison.Ordinal);

        // Publishing replaces the unreadable checkpoint, and the next open uses the new one without a caveat.
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);
        Assert.Single(session.Store.Current!.Dependencies, dependency => dependency.Kind == StoreDependencyKind.Index);
        SessionDerivationCache.Clear();
        SessionOverviewBundle replaced = SessionOverviewProjector.Project(session.Store);
        Assert.Equal(full.Caveats, replaced.Caveats);
        Assert.Equal(derived, Comparable(replaced));
    }

    [Fact(DisplayName = "I14: nothing is published for a session with no generation or no records, and a stale writer is refused")]
    public void NothingToDeriveAndAStaleWriterPublishNothing()
    {
        SessionDerivationCache.Clear();
        using var empty = new TemporarySession();
        Assert.Equal(CheckpointOutcome.NothingToDerive, SessionCheckpoints.Publish(empty.Store, Committed).Outcome);

        using var session = new TemporarySession();
        Publish(session.Store, IncrementalOverviewTests.LiveChunks()[0]);

        // Another writer publishes after this one opened: the checkpoint would name a generation that is no longer current.
        SessionStore stale = SessionStore.Open(LocalOwnedDirectory.Open(session.Path), TestSessions.Session, "metric-tests");
        Publish(session.Store, IncrementalOverviewTests.LiveChunks()[1]);
        CheckpointPublication refused = SessionCheckpoints.Publish(stale, Committed);
        Assert.Equal(CheckpointOutcome.Refused, refused.Outcome);
        Assert.Null(refused.PublishedGeneration);
        Assert.NotNull(refused.Reason);
        Assert.Null(DerivationCheckpoint.NamedBy(session.Store.Current!));
    }

    /// <summary>
    /// What an overview shows, without the generation and identity that differ between two of the same records, and
    /// without its caveats, which each test compares itself.
    /// </summary>
    private static string Comparable(SessionOverviewBundle overview) =>
        JsonSerializer.Serialize(overview with { Generation = 0, GraphIdentity = string.Empty, Caveats = [] });
}
