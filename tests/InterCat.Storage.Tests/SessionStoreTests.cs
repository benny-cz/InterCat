using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace InterCat.Storage.Tests;

public sealed class SessionStoreTests
{
    private static readonly Guid Session = Guid.Parse("3b1f5f9c-7a2d-4c31-9f0e-11d2c3b4a5e6");
    private static readonly DateTimeOffset Committed = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "I15: a published generation names every dependency with its length and digest")]
    public void APublishedGenerationNamesEveryDependency()
    {
        using var session = new TemporarySession();

        StoreCommitResult result = Publish(session.Store, ("segment-0001.icats", "first"), ("dict-0001.icatd", "second"));

        StoreDependency segment = Assert.Single(
            result.Manifest.Dependencies,
            dependency => dependency.Name == "segment-0001.icats");
        Assert.Equal(1, result.Manifest.Generation);
        Assert.Null(result.Manifest.PreviousGeneration);
        Assert.Equal(0, result.PreviousGeneration);
        Assert.Equal(Session, result.Manifest.SessionId);
        Assert.Equal(5, segment.LengthBytes);
        Assert.Equal(Digest("first"), segment.Digest);
        Assert.Null(result.Manifest.VerifyDigest());
        Assert.Equal(["segment-0001.icats", "dict-0001.icatd"], result.PublishedFiles);
    }

    [Fact(DisplayName = "I15: a later generation carries its predecessor's dependencies forward")]
    public void ALaterGenerationCarriesItsPredecessorForward()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));

        StoreCommitResult second = Publish(session.Store, ("segment-0002.icats", "second"));

        Assert.Equal(2, second.Manifest.Generation);
        Assert.Equal(1, second.Manifest.PreviousGeneration);
        Assert.Equal(
            ["segment-0001.icats", "segment-0002.icats"],
            second.Manifest.Dependencies.Select(dependency => dependency.Name).Order(StringComparer.Ordinal));
    }

    [Fact(DisplayName = "I15: a published name is immutable, so republishing it is refused")]
    public void APublishedNameIsImmutable()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));

        using StoreStagingFile staged = Stage(session.Store, "segment-0001.icats", "different");
        _ = staged.Complete();

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() =>
            session.Store.Commit([staged], CommittedBoundary.None, Committed));

        Assert.Contains("immutable", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(1, session.Store.Current!.Generation);
    }

    [Fact(DisplayName = "I15: a stale store instance cannot overwrite a generation another writer published")]
    public void AStaleWriterMustReopenBeforePublishing()
    {
        using var session = new TemporarySession();
        SessionStore first = session.Store;
        SessionStore stale = session.Reopen();
        StoreCommitResult published = Publish(first, ("segment-0001.icats", "first"));

        using StoreStagingFile staged = Stage(stale, "segment-0001.icats", "second");
        _ = staged.Complete();
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() =>
            stale.Commit([staged], CommittedBoundary.None, Committed));

        Assert.Contains("changed after this store instance opened", refusal.Message, StringComparison.Ordinal);
        Assert.Equal("first", File.ReadAllText(Path.Combine(session.Path, "segment-0001.icats")));
        Assert.Equal(published.Manifest.Digest, session.Reopen().Current!.Digest);
    }

    [Fact(DisplayName = "I15: taking a fresh reader lease does not authorize a stale writer")]
    public void ReaderRefreshDoesNotAuthorizeStaleWriter()
    {
        using var session = new TemporarySession();
        SessionStore writer = session.Store;
        SessionStore stale = session.Reopen();
        _ = Publish(writer, ("segment-0001.icats", "first"));

        using EvidenceLease lease = stale.AcquireLease();
        Assert.Equal(1, lease.Generation);
        using StoreStagingFile staged = Stage(stale, "segment-0002.icats", "second");
        _ = staged.Complete();

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() =>
            stale.Commit([staged], CommittedBoundary.None, Committed));
        Assert.Contains("changed after this store instance opened", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(1, session.Reopen().Current!.Generation);
    }

    [Fact(DisplayName = "I18: stale retention cannot release files from a newer generation")]
    public void AStaleRetentionMustReopenBeforePublishing()
    {
        using var session = new TemporarySession();
        SessionStore first = session.Store;
        _ = Publish(first, ("segment-0001.icats", "first"));
        SessionStore stale = session.Reopen();
        _ = Publish(first, ("segment-0002.icats", "second"));

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() =>
            stale.ReleaseDependencies(["segment-0001.icats"], "stale cleanup", Committed));

        Assert.Contains("changed after this store instance opened", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(2, session.Reopen().Current!.Generation);
        Assert.True(File.Exists(Path.Combine(session.Path, "segment-0001.icats")));
        Assert.True(File.Exists(Path.Combine(session.Path, "segment-0002.icats")));
    }

    [Fact(DisplayName = "I15: an exclusive publication lock prevents a second writer from committing")]
    public void PublicationLockIsExclusive()
    {
        using var session = new TemporarySession();
        using StoreStagingFile staged = Stage(session.Store, "segment-0001.icats", "first");
        _ = staged.Complete();
        using (FileStream held = LocalOwnedDirectory.Open(session.Path).OpenOwnedFile(
            SessionStore.PublicationLockFileName,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            FileOptions.None))
        {
            IOException refusal = Assert.Throws<IOException>(() =>
                session.Store.Commit([staged], CommittedBoundary.None, Committed));
            Assert.Contains("exclusive publication lock", refusal.Message, StringComparison.Ordinal);
            Assert.Null(session.Store.Current);
        }

        Assert.Equal(1, session.Store.Commit([staged], CommittedBoundary.None, Committed).Manifest.Generation);
        Assert.Empty(session.Reopen().Recovery.OrphanFiles);
    }

    [Fact(DisplayName = "I15: an orphan with a generation's final name is never overwritten")]
    public void AnOrphanPublishedNameIsImmutableToo()
    {
        using var session = new TemporarySession();
        session.WriteRaw("segment-0001.icats", "orphan");
        using StoreStagingFile staged = Stage(session.Store, "segment-0001.icats", "new");
        _ = staged.Complete();

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() =>
            session.Store.Commit([staged], CommittedBoundary.None, Committed));
        Assert.Contains("already exists", refusal.Message, StringComparison.Ordinal);
        Assert.Equal("orphan", File.ReadAllText(Path.Combine(session.Path, "segment-0001.icats")));
        Assert.Null(session.Store.Current);
        Assert.Throws<ArgumentException>(() =>
            session.Store.Stage(SessionPointerV1.FileName, StoreDependencyKind.Index));

        string manifestName = SessionManifestV1.FileNameFor(1);
        session.WriteRaw(manifestName, "orphan manifest");
        using StoreStagingFile another = Stage(session.Store, "segment-0002.icats", "new");
        _ = another.Complete();
        StoreCommitResult recovered = session.Store.Commit([another], CommittedBoundary.None, Committed);
        Assert.Equal(2, recovered.Manifest.Generation);
        Assert.Equal("orphan manifest", File.ReadAllText(Path.Combine(session.Path, manifestName)));
    }

    [Fact(DisplayName = "I15: a staged generation is refused if its number becomes occupied before commit")]
    public void StagedGenerationCannotSilentlyChangeNumber()
    {
        using var session = new TemporarySession();
        long planned = session.Store.NextGeneration;
        using StoreStagingFile staged = Stage(session.Store, "seg-0000000001-0000.icats", "first");
        _ = staged.Complete();
        session.WriteRaw(SessionManifestV1.FileNameFor(planned), "interrupted writer");

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() =>
            session.Store.Commit([staged], CommittedBoundary.None, Committed,
                expectedGeneration: planned));

        Assert.Contains("staged, but generation 2", refusal.Message, StringComparison.Ordinal);
        Assert.Null(session.Store.Current);
        Assert.Equal(2, session.Store.NextGeneration);
        Assert.False(File.Exists(Path.Combine(session.Path, staged.PublishedName)));
    }

    [Fact(DisplayName = "I15: an incomplete staged file is never published")]
    public void AnIncompleteStagedFileIsNeverPublished()
    {
        using var session = new TemporarySession();
        using StoreStagingFile staged = session.Store.Stage("segment-0001.icats", StoreDependencyKind.Segment);
        staged.Content.Write("not flushed"u8);

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() =>
            session.Store.Commit([staged], CommittedBoundary.None, Committed));

        Assert.Contains("not completed", refusal.Message, StringComparison.Ordinal);
        Assert.Null(session.Store.Current);
    }

    [Fact(DisplayName = "I15: a reopened session acquires the generation its pointer names")]
    public void AReopenedSessionAcquiresItsGeneration()
    {
        using var session = new TemporarySession();
        StoreCommitResult first = Publish(session.Store, ("segment-0001.icats", "first"));

        SessionStore reopened = session.Reopen();

        Assert.Equal(first.Manifest.Digest, reopened.Current!.Digest);
        Assert.Equal(first.Manifest.Generation, reopened.Current.Generation);
        Assert.Equal(
            first.Manifest.Dependencies,
            reopened.Current.Dependencies);
        Assert.Equal(1, reopened.Recovery.Generation);
        Assert.False(reopened.Recovery.RolledBackToLastKnownGood);
        Assert.Null(reopened.Recovery.RollbackReason);
        Assert.Empty(reopened.Recovery.OrphanFiles);
    }

    [Fact(DisplayName = "I15: a crash before the manifest leaves the previous generation and an orphan file")]
    public void ACrashBeforeTheManifestKeepsThePreviousGeneration()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));

        // Step 3 interrupted: the file is published, but no manifest references it yet.
        session.WriteRaw("segment-0002.icats", "second");

        SessionStore reopened = session.Reopen();

        Assert.Equal(1, reopened.Current!.Generation);
        Assert.Equal(["segment-0002.icats"], reopened.Recovery.OrphanFiles);
        Assert.Equal(6, reopened.Recovery.OrphanBytes);
        Assert.False(reopened.Recovery.RolledBackToLastKnownGood);
        Assert.True(File.Exists(Path.Combine(session.Path, "segment-0002.icats")));
    }

    [Fact(DisplayName = "I15: a crash before the pointer leaves the previous generation and an orphan manifest")]
    public void ACrashBeforeThePointerKeepsThePreviousGeneration()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));
        StoreCommitResult second = Publish(session.Store, ("segment-0002.icats", "second"));

        // Step 4 interrupted: generation 2 is fully written, but the pointer still names generation 1.
        session.WriteRaw(
            SessionPointerV1.FileName,
            JsonSerializer.Serialize(
                SessionPointerV1.For(session.ManifestOf(1)),
                SessionManifestV1.Json));

        SessionStore reopened = session.Reopen();

        Assert.Equal(1, reopened.Current!.Generation);
        Assert.Contains(SessionManifestV1.FileNameFor(2), reopened.Recovery.OrphanFiles);
        Assert.Equal(2, second.Manifest.Generation);
    }

    [Fact(DisplayName = "I15: a torn pointer rolls back to the retained last-known-good")]
    public void ATornPointerRollsBackToLastKnownGood()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));
        _ = Publish(session.Store, ("segment-0002.icats", "second"));

        session.WriteRaw(SessionPointerV1.FileName, "{ \"formatVersion\": 1, \"generation\":");

        SessionStore reopened = session.Reopen();

        Assert.Equal(1, reopened.Current!.Generation);
        Assert.True(reopened.Recovery.RolledBackToLastKnownGood);
        Assert.Contains("unreadable", reopened.Recovery.RollbackReason!, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I15: confirmed pointer repair preserves the damaged bytes and skips an orphan generation")]
    public void ConfirmedPointerRepairPreservesEvidenceAndCanPublishAgain()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));
        _ = Publish(session.Store, ("segment-0002.icats", "second"));
        const string damaged = "{ \"formatVersion\": 1, \"generation\":";
        session.WriteRaw(SessionPointerV1.FileName, damaged);
        SessionStore recovered = session.Reopen();
        Assert.True(recovered.Recovery.RolledBackToLastKnownGood);

        PointerRepairOutcome outcome = recovered.RepairCurrentPointer();

        Assert.True(outcome.Repaired);
        Assert.Equal(1, outcome.VerifiedGeneration);
        Assert.NotNull(outcome.RollbackReason);
        Assert.Equal(damaged, File.ReadAllText(Path.Combine(session.Path, outcome.DamagedPointerBackup!)));
        SessionStore healthy = session.Reopen();
        Assert.False(healthy.Recovery.RolledBackToLastKnownGood);
        Assert.Equal(1, healthy.Current!.Generation);
        Assert.Equal(3, healthy.NextGeneration);
        StoreCommitResult next = Publish(healthy, ("segment-0003.icats", "third"));
        Assert.Equal(3, next.Manifest.Generation);
        Assert.Equal(1, next.Manifest.PreviousGeneration);
        Assert.True(File.Exists(Path.Combine(session.Path, SessionManifestV1.FileNameFor(2))));
        Assert.Equal(3, session.Reopen().Current!.Generation);
    }

    [Fact(DisplayName = "I15: pointer repair is a no-op when the current generation verifies")]
    public void PointerRepairDoesNotRewriteAHealthySession()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));
        string pointer = File.ReadAllText(Path.Combine(session.Path, SessionPointerV1.FileName));

        PointerRepairOutcome outcome = session.Store.RepairCurrentPointer();

        Assert.False(outcome.Repaired);
        Assert.Null(outcome.DamagedPointerBackup);
        Assert.Equal(pointer, File.ReadAllText(Path.Combine(session.Path, SessionPointerV1.FileName)));
    }

    [Fact(DisplayName = "I15: pointer repair refuses an unbounded damaged pointer without replacing it")]
    public void PointerRepairRefusesOversizedDamagedPointer()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));
        _ = Publish(session.Store, ("segment-0002.icats", "second"));
        string path = Path.Combine(session.Path, SessionPointerV1.FileName);
        File.WriteAllBytes(path, new byte[1024 * 1024 + 1]);
        SessionStore recovered = session.Reopen();

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() =>
            recovered.RepairCurrentPointer());

        Assert.Contains("exceeds 1 MiB", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(1024 * 1024 + 1, new FileInfo(path).Length);
        Assert.True(session.Reopen().Recovery.RolledBackToLastKnownGood);
    }

    [Fact(DisplayName = "I15: a missing current pointer is repaired without inventing damaged bytes")]
    public void MissingCurrentPointerCanBeRepaired()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));
        _ = Publish(session.Store, ("segment-0002.icats", "second"));
        File.Delete(Path.Combine(session.Path, SessionPointerV1.FileName));
        SessionStore recovered = session.Reopen();

        PointerRepairOutcome outcome = recovered.RepairCurrentPointer();

        Assert.True(outcome.Repaired);
        Assert.Null(outcome.DamagedPointerBackup);
        Assert.Equal(1, outcome.VerifiedGeneration);
        Assert.False(session.Reopen().Recovery.RolledBackToLastKnownGood);
    }

    [Fact(DisplayName = "I15: a stale recovery preview cannot repoint a newly published generation")]
    public void StaleRecoveryPreviewCannotRepair()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));
        _ = Publish(session.Store, ("segment-0002.icats", "second"));
        session.WriteRaw(SessionPointerV1.FileName, "broken");
        SessionStore stale = session.Reopen();
        SessionStore repaired = session.Reopen();
        _ = repaired.RepairCurrentPointer();
        _ = Publish(repaired, ("segment-0003.icats", "third"));

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() =>
            stale.RepairCurrentPointer());
        Assert.Contains("changed after this store was opened", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(3, session.Reopen().Current!.Generation);
    }

    [Fact(DisplayName = "I15: a dependency that changed under a generation fails it rather than being read")]
    public void AChangedDependencyFailsItsGeneration()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));
        _ = Publish(session.Store, ("segment-0002.icats", "second"));

        // A rename is not a power-failure guarantee: the name is there and the contents are not.
        // The same length with different bytes, so it is the digest and not the size that catches it.
        session.WriteRaw("segment-0002.icats", "SECOND");

        SessionStore reopened = session.Reopen();

        Assert.Equal(1, reopened.Current!.Generation);
        Assert.True(reopened.Recovery.RolledBackToLastKnownGood);
        Assert.Contains("segment-0002.icats", reopened.Recovery.RollbackReason!, StringComparison.Ordinal);
        Assert.Contains("computes", reopened.Recovery.RollbackReason!, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I15: a writer re-hashes a dependency whose file was written after it measured it")]
    public void AWriterReHashesAFileWrittenSinceItMeasuredIt()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));
        _ = Publish(session.Store, ("segment-0002.icats", "second"));

        // This writer hashed both files when it published them. A write since - the same length, so only the digest
        // can tell - moves the file's last-write time, and that is what sends it back to be hashed.
        string path = Path.Combine(session.Path, "segment-0002.icats");
        DateTime measured = File.GetLastWriteTimeUtc(path);
        session.WriteRaw("segment-0002.icats", "SECOND");
        File.SetLastWriteTimeUtc(path, measured.AddSeconds(1));

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() =>
            Publish(session.Store, ("segment-0003.icats", "third")));
        Assert.Contains("failed verification", refused.Message, StringComparison.Ordinal);
        Assert.Equal(2, session.ManifestOf(2).Generation);
        Assert.False(File.Exists(Path.Combine(session.Path, SessionManifestV1.FileNameFor(3))));
    }

    [Fact(DisplayName = "I15: a fresh reader hashes every dependency, so a change that kept a file's length and time is found")]
    public void AFreshReaderHashesEveryDependency()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));
        _ = Publish(session.Store, ("segment-0002.icats", "second"));
        string path = Path.Combine(session.Path, "segment-0002.icats");
        DateTime measured = File.GetLastWriteTimeUtc(path);
        session.WriteRaw("segment-0002.icats", "SECOND");
        File.SetLastWriteTimeUtc(path, measured);

        // Nothing this reader holds says the file was measured, so its bytes are hashed and the change is found.
        SessionStore reader = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path));
        Assert.True(reader.Recovery.RolledBackToLastKnownGood);
        Assert.Equal(1, reader.Current!.Generation);
        Assert.Contains("computes", reader.Recovery.RollbackReason!, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I15: a lease on the generation this instance verified opens none of its dependencies")]
    public void ARepeatedLeaseOpensNoDependency()
    {
        using var session = new TemporarySession();
        var counting = new CountingDirectory(LocalOwnedDirectory.Open(session.Path));
        SessionStore store = SessionStore.Open(counting, Session, "test-source");
        _ = Publish(store, ("segment-0001.icats", "first"));

        // A commit reads back what it adds. What it carries forward, this instance already hashed, so one listing
        // confirms its length and last-write time instead of an open per file (ADR-025, revision 129).
        counting.Opened.Clear();
        StoreCommitResult second = Publish(store, ("segment-0002.icats", "second"));
        Assert.Contains("segment-0002.icats", counting.Opened);
        Assert.DoesNotContain("segment-0001.icats", counting.Opened);

        // The pointer still names the generation this instance verified: neither its manifest nor any dependency is
        // opened, however many a long live session names.
        counting.Opened.Clear();
        using (EvidenceLease lease = store.AcquireLease())
        {
            Assert.Same(second.Manifest, lease.Manifest);
        }

        Assert.DoesNotContain(SessionManifestV1.FileNameFor(2), counting.Opened);
        Assert.DoesNotContain(counting.Opened, name => second.Manifest.Dependencies.Any(dependency => dependency.Name == name));
    }

    [Fact(DisplayName = "I15: a lease still finds a dependency truncated, rewritten or removed since it was measured")]
    public void ALeaseFindsADependencyChangedSinceItWasMeasured()
    {
        // Shorter than measured: the listing disagrees, the file is opened, and its generation fails. The lease falls
        // back to the retained last-known-good, as it always has.
        using (var truncated = new TemporarySession())
        {
            _ = Publish(truncated.Store, ("segment-0001.icats", "first"));
            _ = Publish(truncated.Store, ("segment-0002.icats", "second"));
            using (EvidenceLease lease = truncated.Store.AcquireLease())
            {
                Assert.Equal(2, lease.Manifest.Generation);
            }

            truncated.WriteRaw("segment-0002.icats", "sec");
            using (EvidenceLease lease = truncated.Store.AcquireLease())
            {
                Assert.Equal(1, lease.Manifest.Generation);
            }
        }

        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));
        _ = Publish(session.Store, ("segment-0002.icats", "second"));
        using (EvidenceLease lease = session.Store.AcquireLease())
        {
            Assert.Equal(2, lease.Manifest.Generation);
        }

        // The same length, written since: its time moved, so it is hashed again rather than confirmed.
        string path = Path.Combine(session.Path, "segment-0002.icats");
        DateTime measured = File.GetLastWriteTimeUtc(path);
        session.WriteRaw("segment-0002.icats", "SECOND");
        File.SetLastWriteTimeUtc(path, measured.AddSeconds(1));
        using (EvidenceLease lease = session.Store.AcquireLease())
        {
            Assert.Equal(1, lease.Manifest.Generation);
        }

        // Gone: the listing has no entry, and the open that follows says so.
        File.Delete(Path.Combine(session.Path, "segment-0001.icats"));
        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => session.Store.AcquireLease());
        Assert.Contains("segment-0001.icats", refused.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I15: a manifest whose contents no longer match its digest is refused")]
    public void AManifestThatDoesNotMatchItsDigestIsRefused()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));
        string manifestName = SessionManifestV1.FileNameFor(1);
        string tampered = File.ReadAllText(Path.Combine(session.Path, manifestName))
            .Replace("\"committedRecords\": 0", "\"committedRecords\": 9", StringComparison.Ordinal)
            .Replace("\"generation\": 1", "\"generation\": 1 ", StringComparison.Ordinal);

        session.WriteRaw(manifestName, tampered);

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(session.Reopen);
        Assert.Contains("no complete generation", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I15: opening reports a staging file without deleting another writer's work")]
    public void AnInterruptedStagingFileIsPreservedOnOpen()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));
        string staging = $"{SessionStore.StagingPrefix}{Guid.NewGuid():N}{SessionStore.StagingSuffix}";
        session.WriteRaw(staging, "half written");

        SessionStore reopened = session.Reopen();

        Assert.Empty(reopened.Recovery.RemovedStagingFiles);
        Assert.Contains(staging, reopened.Recovery.OrphanFiles);
        Assert.True(File.Exists(Path.Combine(session.Path, staging)));
        Assert.DoesNotContain(staging, reopened.RemoveOrphans());
        Assert.Equal(1, reopened.Current!.Generation);
    }

    [Fact(DisplayName = "I15: staging cleanup defers an active owner and removes it after abandonment")]
    public void StagingCleanupRequiresReleasedOwner()
    {
        using var session = new TemporarySession();
        StoreStagingFile staged = Stage(session.Store, "segment-0001.icats", "unfinished");
        _ = staged.Complete();

        StagingCleanupReport preview = session.Store.PreviewStagingCleanup();
        Assert.Contains(staged.StagingName, preview.ActiveFiles);
        Assert.Empty(preview.AbandonedFiles);
        Assert.Empty(session.Store.CleanupAbandonedStaging().RemovedFiles);
        Assert.True(File.Exists(Path.Combine(session.Path, staged.StagingName)));

        staged.Dispose();
        StagingCleanupReport abandoned = session.Store.PreviewStagingCleanup();
        Assert.Contains(staged.StagingName, abandoned.AbandonedFiles);
        StagingCleanupReport removed = session.Store.CleanupAbandonedStaging(abandoned.CandidateDigest);
        Assert.Contains(staged.StagingName, removed.RemovedFiles);
        Assert.Contains(staged.OwnershipName, removed.RemovedFiles);
        Assert.False(File.Exists(Path.Combine(session.Path, staged.StagingName)));
        Assert.False(File.Exists(Path.Combine(session.Path, staged.OwnershipName)));
    }

    [Fact(DisplayName = "I15: staging cleanup refuses a preview whose candidate set changed")]
    public void StagingCleanupBindsTheReviewedSet()
    {
        using var session = new TemporarySession();
        StoreStagingFile staged = Stage(session.Store, "segment-0001.icats", "first");
        _ = staged.Complete();
        StagingCleanupReport before = session.Store.PreviewStagingCleanup();
        Assert.Empty(before.AbandonedFiles);
        staged.Dispose();

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() =>
            session.Store.CleanupAbandonedStaging(before.CandidateDigest));

        Assert.Contains("changed after the preview", refusal.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(session.Path, staged.StagingName)));
        Assert.True(File.Exists(Path.Combine(session.Path, staged.OwnershipName)));
    }

    [Fact(DisplayName = "I15: published staging clears its ownership marker")]
    public void PublishedStagingLeavesNoOwnershipMarker()
    {
        using var session = new TemporarySession();
        using StoreStagingFile staged = Stage(session.Store, "segment-0001.icats", "first");
        _ = staged.Complete();
        _ = session.Store.Commit([staged], CommittedBoundary.None, Committed);

        Assert.False(File.Exists(Path.Combine(session.Path, staged.OwnershipName)));
        Assert.Empty(session.Store.PreviewStagingCleanup().MarkerOnlyFiles);
        Assert.Empty(session.Reopen().Recovery.OrphanFiles);
    }

    [Fact(DisplayName = "I15: a publication interrupted at its pointer leaves only staging cleanup can remove")]
    public void InterruptedPointerWriteLeavesMarkedStaging()
    {
        string path = Path.Combine(Path.GetTempPath(), "InterCat.Storage.Tests.Pointer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            Guid sessionId = Guid.NewGuid();
            var failing = new PointerFailingDirectory(LocalOwnedDirectory.Open(path));
            SessionStore store = SessionStore.Open(failing, sessionId, "pointer-tests");
            using StoreStagingFile staged = Stage(store, "segment-0001.icats", "first");
            _ = staged.Complete();

            // A writer killed between writing the pointer's staging file and renaming it leaves it behind.
            Assert.Throws<IOException>(() => store.Commit([staged], CommittedBoundary.None, Committed));
            staged.Dispose();

            SessionStore reopened = SessionStore.Open(LocalOwnedDirectory.Open(path), sessionId, "pointer-tests");
            StagingCleanupReport cleanup = reopened.CleanupAbandonedStaging();

            Assert.Empty(cleanup.UnmarkedFiles);
            Assert.Empty(Directory.GetFiles(path, SessionStore.StagingPrefix + "*"));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact(DisplayName = "I15: staging cleanup leaves unmarked legacy files for manual review")]
    public void StagingCleanupSkipsUnmarkedLegacyFile()
    {
        using var session = new TemporarySession();
        string staging = $"{SessionStore.StagingPrefix}{Guid.NewGuid():N}{SessionStore.StagingSuffix}";
        session.WriteRaw(staging, "legacy");

        StagingCleanupReport report = session.Store.CleanupAbandonedStaging();

        Assert.Contains(staging, report.UnmarkedFiles);
        Assert.Empty(report.RemovedFiles);
        Assert.True(File.Exists(Path.Combine(session.Path, staging)));
    }

    [Fact(DisplayName = "I15: marker-only cleanup keeps an interrupted published dependency")]
    public void MarkerOnlyCleanupKeepsPublishedOrphan()
    {
        using var session = new TemporarySession();
        StoreStagingFile staged = Stage(session.Store, "segment-0001.icats", "first");
        _ = staged.Complete();
        session.Store.Root.ReplaceOwnedFile(staged.StagingName, staged.PublishedName);
        staged.Dispose();

        StagingCleanupReport report = session.Store.CleanupAbandonedStaging();

        Assert.Contains(staged.OwnershipName, report.MarkerOnlyFiles);
        Assert.Contains(staged.OwnershipName, report.RemovedFiles);
        Assert.True(File.Exists(Path.Combine(session.Path, staged.PublishedName)));
    }

    [Fact(DisplayName = "I15: disposed staging cannot publish after abandoning its ownership")]
    public void DisposedStagingCannotPublish()
    {
        using var session = new TemporarySession();
        StoreStagingFile staged = Stage(session.Store, "segment-0001.icats", "first");
        _ = staged.Complete();
        staged.Dispose();

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() =>
            session.Store.Commit([staged], CommittedBoundary.None, Committed));

        Assert.Contains("no longer has an active owner", refusal.Message, StringComparison.Ordinal);
        Assert.Null(session.Store.Current);
    }

    [Fact(DisplayName = "I15: an orphan is reported on open and removed only when that is asked for")]
    public void AnOrphanIsReportedOnOpenAndRemovedOnlyWhenAsked()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));
        session.WriteRaw("segment-0009.icats", "orphaned");

        SessionStore reopened = session.Reopen();
        Assert.Equal(["segment-0009.icats"], reopened.Recovery.OrphanFiles);
        Assert.True(File.Exists(Path.Combine(session.Path, "segment-0009.icats")));

        IReadOnlyList<string> removed = reopened.RemoveOrphans();

        Assert.Equal(["segment-0009.icats"], removed);
        Assert.False(File.Exists(Path.Combine(session.Path, "segment-0009.icats")));
        Assert.Equal(1, reopened.Current!.Generation);
    }

    [Fact(DisplayName = "I15: an empty session is empty, not a refusal")]
    public void AnEmptySessionIsEmpty()
    {
        using var session = new TemporarySession();

        Assert.Null(session.Store.Current);
        Assert.Equal(0, session.Store.Recovery.Generation);
        Assert.Empty(session.Store.Recovery.OrphanFiles);
    }

    [Fact(DisplayName = "I15: a session is never opened as another session")]
    public void ASessionIsNeverOpenedAsAnother()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() =>
            SessionStore.Open(LocalOwnedDirectory.Open(session.Path), Guid.NewGuid()));

        Assert.Contains("never opened as another", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I15: a committed boundary states which admitted evidence a generation derives from")]
    public void ACommittedBoundaryStatesItsEvidence()
    {
        using var session = new TemporarySession();
        var boundary = new CommittedBoundary("capture-0001.icatj", 4_096, 512, Digest("journal"));

        using StoreStagingFile staged = Stage(session.Store, "segment-0001.icats", "first");
        _ = staged.Complete();
        StoreCommitResult result = session.Store.Commit([staged], boundary, Committed);

        Assert.Equal(boundary, result.Manifest.Boundary);
        Assert.True(result.Manifest.Boundary.IsDeclared);
        Assert.False(CommittedBoundary.None.IsDeclared);
        Assert.Equal(boundary, session.Reopen().Current!.Boundary);
        Assert.Equal(result.Manifest.Digest, session.Store.Current!.Digest);
    }

    [Theory(DisplayName = "I15: a manifest this reader cannot read exactly is refused, never read at a guess")]
    [InlineData(2, 1, "format 2")]
    [InlineData(1, 0, "between 1 and")]
    public void AnUnreadableManifestIsRefused(int formatVersion, long generation, string reason)
    {
        var manifest = new SessionManifestV1
        {
            FormatVersion = formatVersion,
            Generation = generation,
            SessionId = Session,
            CommittedUtc = Committed,
            SourceIdentity = "test",
            PreviousGeneration = null,
            Boundary = CommittedBoundary.None,
            Dependencies = [],
            Digest = "sha256:" + new string('0', 64),
        };

        Assert.Contains(reason, manifest.Validate(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I15: a manifest naming a dependency outside its root is refused")]
    public void AManifestNamingADependencyOutsideItsRootIsRefused()
    {
        var manifest = new SessionManifestV1
        {
            FormatVersion = 1,
            Generation = 1,
            SessionId = Session,
            CommittedUtc = Committed,
            SourceIdentity = "test",
            PreviousGeneration = null,
            Boundary = CommittedBoundary.None,
            Dependencies = [new(@"..\escape.icats", StoreDependencyKind.Segment, 1, Digest("x"))],
            Digest = "sha256:" + new string('0', 64),
        };

        Assert.Contains("not an owned file name", manifest.Validate(), StringComparison.Ordinal);
    }

    private static StoreCommitResult Publish(SessionStore store, params (string Name, string Content)[] files)
    {
        var staged = new List<StoreStagingFile>();
        try
        {
            foreach ((string name, string content) in files)
            {
                StoreStagingFile file = Stage(store, name, content);
                _ = file.Complete();
                staged.Add(file);
            }

            return store.Commit(staged, CommittedBoundary.None, Committed);
        }
        finally
        {
            foreach (StoreStagingFile file in staged)
            {
                file.Dispose();
            }
        }
    }

    private static StoreStagingFile Stage(SessionStore store, string name, string content)
    {
        StoreStagingFile staged = store.Stage(
            name,
            name.EndsWith(".icatd", StringComparison.Ordinal)
                ? StoreDependencyKind.Dictionary
                : StoreDependencyKind.Segment);
        staged.Content.Write(Encoding.UTF8.GetBytes(content));
        return staged;
    }

    private static string Digest(string content) =>
        string.Concat("sha256:", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))));

    private sealed class TemporarySession : IDisposable
    {
        public TemporarySession()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "InterCat.Storage.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Store = SessionStore.Open(LocalOwnedDirectory.Open(Path), Session, "test-source");
        }

        public string Path { get; }

        public SessionStore Store { get; private set; }

        public SessionStore Reopen()
        {
            Store = SessionStore.Open(LocalOwnedDirectory.Open(Path), Session, "test-source");
            return Store;
        }

        public SessionManifestV1 ManifestOf(long generation) =>
            JsonSerializer.Deserialize<SessionManifestV1>(
                File.ReadAllText(System.IO.Path.Combine(Path, SessionManifestV1.FileNameFor(generation))),
                SessionManifestV1.Json)!;

        public void WriteRaw(string name, string content) =>
            File.WriteAllText(System.IO.Path.Combine(Path, name), content);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Records every file opened through it; everything else, its listing included, is the real directory's.</summary>
    private sealed class CountingDirectory(IOwnedDirectory inner) : IOwnedDirectory
    {
        public List<string> Opened { get; } = [];

        public string Path => inner.Path;

        public FileStream OpenOwnedFile(string name, FileMode mode, FileAccess access, FileShare share, FileOptions options)
        {
            Opened.Add(name);
            return inner.OpenOwnedFile(name, mode, access, share, options);
        }

        public void ReplaceOwnedFile(string sourceName, string destinationName) =>
            inner.ReplaceOwnedFile(sourceName, destinationName);

        public bool RemoveOwnedFile(string name) => inner.RemoveOwnedFile(name);

        public IReadOnlyDictionary<string, OwnedFileFacts>? DescribeOwnedFiles() => inner.DescribeOwnedFiles();
    }

    /// <summary>Fails the current-pointer rename the way a killed writer would stop there; everything else is real.</summary>
    private sealed class PointerFailingDirectory(IOwnedDirectory inner) : IOwnedDirectory
    {
        public string Path => inner.Path;

        public FileStream OpenOwnedFile(string name, FileMode mode, FileAccess access, FileShare share, FileOptions options) =>
            inner.OpenOwnedFile(name, mode, access, share, options);

        public void ReplaceOwnedFile(string sourceName, string destinationName)
        {
            if (destinationName == SessionPointerV1.FileName)
            {
                throw new IOException("Simulated interruption before the pointer was replaced.");
            }

            inner.ReplaceOwnedFile(sourceName, destinationName);
        }

        public bool RemoveOwnedFile(string name) => inner.RemoveOwnedFile(name);
    }
}
