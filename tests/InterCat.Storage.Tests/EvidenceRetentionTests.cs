using InterCat.Domain;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// Evidence leases and retention. The point of both is what they refuse: retention that can take a segment
/// out from under an open reader is worse than no retention, and a release that cannot be read afterwards is
/// indistinguishable from data loss.
/// </summary>
public sealed class EvidenceRetentionTests
{
    private static readonly Guid Session = Guid.Parse("c4d5e6f7-a8b9-4c0d-8e1f-2a3b4c5d6e7f");
    private static readonly CaptureId Capture = new(Guid.Parse("b2c3d4e5-f6a7-4b8c-8d9e-0f1a2b3c4d5e"));
    private static readonly ClockId Clock = new(Guid.Parse("22223333-4444-4555-8666-777788889999"));
    private static readonly Guid Provider = Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88");
    private static readonly DateTimeOffset Committed = new(2026, 9, 22, 15, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "I18: a lease acquires the generation and every dependency it names as one thing")]
    public void ALeaseAcquiresTheGenerationAndItsDependencies()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult published = Publish(session.Store, 4);

        using EvidenceLease lease = session.Store.AcquireLease(nowUtc: Committed);

        Assert.Equal(1, lease.Generation);
        Assert.Equal(EvidenceLeaseKind.Interactive, lease.Kind);
        Assert.Equal(0, lease.ReservedBytes);
        Assert.Equal("manifest-0000000001.json", lease.ManifestName);
        Assert.Equal(
            published.Manifest.Dependencies.Select(dependency => dependency.Name).Order(StringComparer.Ordinal),
            lease.Dependencies.Order(StringComparer.Ordinal));
        Assert.True(lease.Holds(published.Segments[0].Name, Committed));
        Assert.True(lease.Holds(lease.ManifestName, Committed));
        Assert.False(lease.Holds("seg-0000000009-0000.icats", Committed));
        Assert.Single(session.Store.LiveLeases(Committed));
    }

    [Fact(DisplayName = "I18: retention cannot remove evidence a live lease holds")]
    public void RetentionCannotRemoveLeasedEvidence()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult first = Publish(session.Store, 4);
        string segment = first.Segments[0].Name;
        string dictionary = first.Segments[0].DictionaryNames[0];

        using EvidenceLease lease = session.Store.AcquireLease(nowUtc: Committed);
        RetentionOutcome outcome = session.Store.ReleaseDependencies(
            [segment, dictionary],
            "superseded by a later derivation",
            Committed,
            Committed);

        // The generation has stopped naming them, which is what makes the retention visible; the bytes stay
        // until the reader that acquired them lets go.
        Assert.Equal(2, outcome.Manifest.Generation);
        Assert.Equal([segment, dictionary], outcome.ReleasedFiles);
        Assert.Empty(outcome.RemovedFiles);
        Assert.Equal([segment, dictionary], outcome.HeldByLease);
        Assert.True(outcome.AwaitingRelease);
        Assert.Equal(0, outcome.ReclaimedBytes);
        Assert.True(File.Exists(Path.Combine(session.Path, segment)));
        Assert.DoesNotContain(
            segment,
            outcome.Manifest.Dependencies.Select(dependency => dependency.Name));

        // The lease still reads exactly what it acquired, from a generation that is no longer current.
        Assert.Equal(4, SessionSegments.Open(session.Store.Root, first.Manifest, segment).RowCount);
    }

    [Fact(DisplayName = "I18: retention in a second store cannot remove another process's leased evidence")]
    public void IndependentStoreRetentionHonorsSharedLease()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult first = Publish(session.Store, 4);
        string segment = first.Segments[0].Name;
        SessionStore writer = session.Reopen();
        SessionStore reader = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path));

        using EvidenceLease lease = reader.AcquireLease(nowUtc: Committed);
        RetentionOutcome outcome = writer.ReleaseDependencies([segment], "superseded", Committed, Committed);

        Assert.Equal([segment], outcome.HeldByLease);
        Assert.Empty(outcome.RemovedFiles);
        Assert.True(File.Exists(Path.Combine(session.Path, segment)));
        Assert.Empty(writer.RemoveOrphans(Committed));
        Assert.Equal(4, SessionSegments.Open(reader.Root, lease.Manifest, segment).RowCount);

        lease.Dispose();
        Assert.Contains(segment, writer.RemoveOrphans(Committed));
        Assert.False(File.Exists(Path.Combine(session.Path, segment)));
    }

    [Fact(DisplayName = "I18: a stale reader acquires the freshly published generation")]
    public void StaleReaderAcquiresCurrentGeneration()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult first = Publish(session.Store, 4);
        SessionStore reader = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path));
        _ = session.Store.ReleaseDependencies(
            [first.Segments[0].Name], "superseded", Committed, Committed);

        using EvidenceLease lease = reader.AcquireLease(nowUtc: Committed);

        Assert.Equal(2, lease.Generation);
        Assert.Equal(2, reader.Current!.Generation);
        Assert.DoesNotContain(first.Segments[0].Name, lease.Dependencies);
    }

    [Fact(DisplayName = "I18: a viewer leases a published generation using read-only root access")]
    public void ViewerNeedsOnlyReadAccess()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult published = Publish(session.Store, 4);
        var root = new ReadOnlyOwnedDirectory(LocalOwnedDirectory.Open(session.Path));
        SessionStore viewer = SessionStore.OpenExisting(root);

        using EvidenceLease lease = viewer.AcquireLease();

        Assert.Equal(published.Manifest.Generation, lease.Generation);
        Assert.Contains(published.Segments[0].Name, lease.Dependencies);
    }

    [Fact(DisplayName = "I18: expiry releases the cross-process hold without another reader call")]
    public void ExpiryReleasesCrossProcessHold()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult first = Publish(session.Store, 4);
        string segment = first.Segments[0].Name;
        SessionStore writer = session.Reopen();
        SessionStore reader = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path));
        // Long enough that the release below still finds it live on a loaded machine; short enough to expire on its own.
        using EvidenceLease lease = reader.AcquireLease(new() { Duration = TimeSpan.FromMilliseconds(1_500) });
        RetentionOutcome outcome = writer.ReleaseDependencies([segment], "superseded", Committed);
        Assert.True(outcome.AwaitingRelease);

        Assert.True(SpinWait.SpinUntil(
            () => writer.RemoveOrphans().Contains(segment),
            TimeSpan.FromSeconds(10)));
        Assert.True(lease.IsReleased);
        Assert.False(File.Exists(Path.Combine(session.Path, segment)));
    }

    [Fact(DisplayName = "I18: released evidence goes once the last lease on it is released")]
    public void ReleasedEvidenceGoesWhenTheLastLeaseDoes()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult first = Publish(session.Store, 4);
        string segment = first.Segments[0].Name;
        EvidenceLease lease = session.Store.AcquireLease(nowUtc: Committed);

        RetentionOutcome outcome = session.Store.ReleaseDependencies(
            [segment],
            "superseded",
            Committed,
            Committed);
        Assert.True(outcome.AwaitingRelease);

        lease.Dispose();
        IReadOnlyList<string> removed = session.Store.RemoveOrphans(Committed);

        Assert.Contains(segment, removed);
        Assert.False(File.Exists(Path.Combine(session.Path, segment)));
        Assert.Empty(session.Store.LiveLeases(Committed));
    }

    [Fact(DisplayName = "I18: an expired lease holds nothing, and is not revivable")]
    public void AnExpiredLeaseHoldsNothing()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult first = Publish(session.Store, 4);
        string segment = first.Segments[0].Name;

        using EvidenceLease lease = session.Store.AcquireLease(
            new() { Duration = TimeSpan.FromSeconds(30) },
            Committed);
        DateTimeOffset later = Committed.AddMinutes(1);

        Assert.True(lease.Holds(segment, Committed));
        Assert.False(lease.Holds(segment, later));
        Assert.True(lease.HasExpiredAt(later));
        Assert.Empty(session.Store.LiveLeases(later));

        RetentionOutcome outcome = session.Store.ReleaseDependencies([segment], "expired hold", Committed, later);

        Assert.Contains(segment, outcome.RemovedFiles);
        Assert.Empty(outcome.HeldByLease);
    }

    [Fact(DisplayName = "I18: a pin that reserves less than it holds is refused, not silently promised")]
    public void APinMustReserveWhatItHolds()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult published = Publish(session.Store, 4);
        long held = published.Manifest.Dependencies.Sum(dependency => dependency.LengthBytes);

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() =>
            session.Store.AcquireLease(
                new() { Kind = EvidenceLeaseKind.Pinned, ReservedBytes = held - 1 },
                Committed));
        Assert.Contains("reserves less than it pins", refusal.Message, StringComparison.Ordinal);

        ArgumentException undeclared = Assert.Throws<ArgumentException>(() =>
            session.Store.AcquireLease(new() { Kind = EvidenceLeaseKind.Pinned }, Committed));
        Assert.Contains("indefinite pin without one", undeclared.Message, StringComparison.Ordinal);

        using EvidenceLease pin = session.Store.AcquireLease(
            new()
            {
                Kind = EvidenceLeaseKind.Pinned,
                ReservedBytes = held,
                Duration = TimeSpan.FromHours(1),
            },
            Committed);
        Assert.Equal(held, pin.ReservedBytes);
    }

    [Fact(DisplayName = "I18: a lease on a session with no generation is refused rather than empty")]
    public void ALeaseOnAnEmptySessionIsRefused()
    {
        using var session = new TemporarySession();

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() =>
            session.Store.AcquireLease(nowUtc: Committed));

        Assert.Contains("nothing to acquire", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I15: a retention generation publishes what it released and why")]
    public void ARetentionGenerationPublishesWhatItReleased()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult first = Publish(session.Store, 4);
        string segment = first.Segments[0].Name;
        long segmentBytes = first.Segments[0].LengthBytes;

        RetentionOutcome outcome = session.Store.ReleaseDependencies(
            [segment],
            "re-derived under normalizer v2",
            Committed,
            Committed);

        RetentionRecord record = Assert.IsType<RetentionRecord>(outcome.Manifest.Retention);
        Assert.Equal(RetentionExtentKind.DerivedFiles, record.Kind);
        Assert.Equal("re-derived under normalizer v2", record.Reason);
        Assert.Equal([segment], record.ReleasedFiles);
        Assert.Equal(segmentBytes, record.ReleasedBytes);
        Assert.Equal(0, record.ReleasedRecords);
        Assert.Empty(record.SourceDigest);
        Assert.Null(record.Validate());
        Assert.Null(outcome.Manifest.VerifyDigest());
        Assert.Equal(segmentBytes, outcome.ReclaimedBytes);

        // The retention generation is a generation like any other: it reopens and verifies, and the record it
        // published survives the round trip through its own file.
        SessionStore reopened = session.Reopen();
        RetentionRecord read = Assert.IsType<RetentionRecord>(reopened.Current!.Retention);
        Assert.Equal(2, reopened.Current.Generation);
        Assert.Equal(record.Kind, read.Kind);
        Assert.Equal(record.Reason, read.Reason);
        Assert.Equal(record.ReleasedFiles, read.ReleasedFiles);
        Assert.Equal(record.ReleasedBytes, read.ReleasedBytes);
        Assert.Equal(record.ReleasedRecords, read.ReleasedRecords);
        Assert.Equal(record.SourceDigest, read.SourceDigest);
        Assert.Null(reopened.Current.VerifyDigest());

        // The generation the retention superseded is unreferenced now: both pointers name the retention
        // generation, because the earlier one is missing the file retention released. Nothing can read it any
        // more, so its manifest went with the publication (store-v1 §9).
        Assert.Empty(reopened.Recovery.OrphanFiles);
        Assert.False(File.Exists(Path.Combine(session.Path, "manifest-0000000001.json")));
    }

    [Fact(DisplayName = "I15: a retention record with no stated reason is refused")]
    public void ARetentionRecordStatesItsReason()
    {
        var unexplained = new RetentionRecord(
            RetentionExtentKind.DerivedFiles,
            Committed,
            string.Empty,
            ["seg-0000000001-0000.icats"],
            10,
            0,
            string.Empty);

        Assert.Contains("indistinguishable from data loss", unexplained.Validate(), StringComparison.Ordinal);
        Assert.Contains(
            "names the digest of the journal it started from",
            new RetentionRecord(
                RetentionExtentKind.JournalPrefix,
                Committed,
                "storage",
                ["journal-0000000001.icatj"],
                10,
                5,
                string.Empty).Validate(),
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I15: a generation published before retention existed verifies unchanged")]
    public void AGenerationWithoutRetentionHashesAsItDid()
    {
        SessionManifestV1 plain = SessionManifestV1.Create(
            1,
            Session,
            Committed,
            "test",
            null,
            CommittedBoundary.None,
            [new("seg-0000000001-0000.icats", StoreDependencyKind.Segment, 10, "sha256:" + new string('a', 64))]);

        SessionManifestV1 retained = SessionManifestV1.Create(
            1,
            Session,
            Committed,
            "test",
            null,
            CommittedBoundary.None,
            [new("seg-0000000001-0000.icats", StoreDependencyKind.Segment, 10, "sha256:" + new string('a', 64))],
            RetentionRecord.ForDerivedFiles(Committed, "x", ["seg-0000000001-0000.icats"], 10));

        // The retention record is appended to the canonical text only when there is one, so this digest is the
        // one this manifest had before the field existed. It is pinned: a change to the canonical form that
        // silently invalidated every already-published manifest would fail here rather than on a user's disk.
        Assert.Null(plain.Retention);
        Assert.Null(plain.VerifyDigest());
        Assert.Equal(GoldenPlainDigest, plain.Digest);
        Assert.NotEqual(plain.Digest, retained.Digest);
        Assert.Null(retained.VerifyDigest());
    }

    [Fact(DisplayName = "I15: releasing a journal prefix publishes a shorter journal and names the extent")]
    public void ReleasingAJournalPrefixPublishesAShorterJournal()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult first = Publish(session.Store, 9, batchCapacity: 3);
        long originalBytes = first.JournalBytes;
        Assert.Equal(9, first.JournalRecords);

        JournalReleasePreview preview = JournalRetention.Preview(session.Store, firstRetainedRecordIndex: 6);
        Assert.Equal(9, preview.TotalRecords);
        Assert.Equal(6, preview.ReleasedRecords);
        Assert.Equal(3, preview.RetainedRecords);
        Assert.Equal(2, preview.ReleasedBatches);
        Assert.Equal(1, preview.RetainedBatches);
        Assert.True(preview.ReleasesAnything);
        Assert.False(preview.WouldEmptyTheJournal);

        RetentionOutcome outcome = JournalRetention.Release(
            session.Store,
            firstRetainedRecordIndex: 6,
            "the first two batches are older than the retained window",
            Committed,
            Committed);

        RetentionRecord record = Assert.IsType<RetentionRecord>(outcome.Manifest.Retention);
        Assert.Equal(RetentionExtentKind.JournalPrefix, record.Kind);
        Assert.Equal(6, record.ReleasedRecords);
        Assert.Equal(["journal-0000000001.icatj"], record.ReleasedFiles);
        Assert.True(record.ReleasedBytes > 0);
        Assert.Null(record.Validate());

        // The generation now names a shorter journal, and its committed boundary describes that one.
        StoreDependency journal = Assert.Single(
            outcome.Manifest.Dependencies,
            dependency => dependency.Kind == StoreDependencyKind.Journal);
        Assert.Equal("journal-0000000002.icatj", journal.Name);
        Assert.True(journal.LengthBytes < originalBytes);
        Assert.Equal(journal.Name, outcome.Manifest.Boundary.JournalName);
        Assert.Equal(3, outcome.Manifest.Boundary.CommittedRecords);
        Assert.Equal(journal.Digest, outcome.Manifest.Boundary.Digest);

        // The retained journal is a complete journal-v1 file, not a truncated one: it names its clock, keeps
        // its schema table and carries a terminal frame.
        using JournalV1Contents retained = JournalV1Reader.Read(
            File.ReadAllBytes(Path.Combine(session.Path, journal.Name)));
        Assert.Equal(Capture, retained.CaptureId);
        Assert.Equal(Clock, retained.SourceClock.Id);
        Assert.Single(retained.Schemas.Schemas);
        Assert.Equal(3, retained.Records.Count());
        Assert.Equal([700, 800, 900], retained.Records.Select(record => record.NativeTicks));

        SessionStore reopened = session.Reopen();
        Assert.Equal(2, reopened.Current!.Generation);
        Assert.Empty(reopened.Recovery.OrphanFiles);
    }

    [Fact(DisplayName = "I15: a journal release that would leave no evidence at all is refused")]
    public void AJournalReleaseThatEmptiesTheJournalIsRefused()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, 6, batchCapacity: 3);

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() =>
            JournalRetention.Release(session.Store, 6, "all of it", Committed, Committed));
        Assert.Contains("no admitted evidence at all", refusal.Message, StringComparison.Ordinal);

        // Nothing was published, so the session still holds exactly what it did.
        Assert.Equal(1, session.Store.Current!.Generation);
        Assert.Equal(6, session.Store.Current.Boundary.CommittedRecords);
    }

    [Fact(DisplayName = "I15: a release whose boundary falls inside the first batch releases nothing")]
    public void AReleaseInsideTheFirstBatchReleasesNothing()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, 6, batchCapacity: 3);

        JournalReleasePreview preview = JournalRetention.Preview(session.Store, 2);
        Assert.False(preview.ReleasesAnything);

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() =>
            JournalRetention.Release(session.Store, 2, "too early", Committed, Committed));
        Assert.Contains("a batch is the unit of release", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I15: a live recording releases its oldest journal chunks whole and keeps its boundary")]
    public void ALiveRecordingReleasesItsOldestChunks()
    {
        using var session = new TemporarySession();

        // Three chunks as a live recording publishes them, each a generation of its own: 4, 5 and 6 records.
        DerivedGenerationResult first = Publish(session.Store, 4, batchCapacity: 2);
        DerivedGenerationResult second = Publish(session.Store, 5, batchCapacity: 2);
        DerivedGenerationResult third = Publish(session.Store, 6, batchCapacity: 2);
        SessionManifestV1 recorded = session.Store.Current!;
        StoreDependency[] chunks = [.. recorded.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Journal)];
        Assert.Equal(3, chunks.Length);

        // A chunk is the unit: a boundary inside the second chunk releases only the first.
        JournalReleasePreview inside = JournalRetention.Preview(session.Store, 8);
        Assert.Equal((3, "chunk", 4L, 11L), (inside.JournalChunks, inside.ReleaseUnit, inside.ReleasedRecords, inside.RetainedRecords));
        Assert.Equal([first.JournalName], inside.ReleasedChunks);

        JournalReleasePreview preview = JournalRetention.Preview(session.Store, 9);
        Assert.Equal([first.JournalName, second.JournalName], preview.ReleasedChunks);
        Assert.Equal((15L, 9L, 6L), (preview.TotalRecords, preview.ReleasedRecords, preview.RetainedRecords));
        Assert.Equal((5L, 3L), (preview.ReleasedBatches, preview.RetainedBatches));
        Assert.Equal((4L, 9L), (preview.SmallestReleasingBoundary, preview.LargestReleasingBoundary));
        Assert.Equal(chunks.Sum(chunk => chunk.LengthBytes), preview.TotalBytes);
        Assert.Equal(third.JournalName, preview.JournalName);

        RetentionOutcome outcome = JournalRetention.Release(
            session.Store,
            9,
            "the first ten seconds are older than the retained window",
            Committed,
            Committed);

        // Nothing was rewritten: the generation stops naming the two oldest chunks and keeps its boundary.
        SessionManifestV1 retained = outcome.Manifest;
        Assert.Equal(third.JournalName, Assert.Single(retained.Dependencies, dependency => dependency.Kind == StoreDependencyKind.Journal).Name);
        Assert.Equal(recorded.Boundary, retained.Boundary);
        Assert.Equal(
            recorded.Dependencies.Count(dependency => dependency.Kind == StoreDependencyKind.Segment),
            retained.Dependencies.Count(dependency => dependency.Kind == StoreDependencyKind.Segment));
        RetentionRecord record = Assert.IsType<RetentionRecord>(retained.Retention);
        Assert.Null(record.Validate());
        Assert.Equal(RetentionExtentKind.JournalPrefix, record.Kind);
        Assert.Equal([first.JournalName, second.JournalName], record.ReleasedFiles);
        Assert.Equal(9, record.ReleasedRecords);
        Assert.Equal(chunks[0].LengthBytes + chunks[1].LengthBytes, record.ReleasedBytes);
        Assert.Equal(SessionStore.ChunkSourceDigest(chunks[..2]), record.SourceDigest);
        Assert.Equal([first.JournalName, second.JournalName], outcome.RemovedFiles.Order(StringComparer.Ordinal));
        Assert.False(File.Exists(Path.Combine(session.Path, first.JournalName)));

        SessionStore reopened = session.Reopen();
        Assert.Equal(retained.Generation, reopened.Current!.Generation);
        Assert.False(reopened.Recovery.RolledBackToLastKnownGood);
    }

    [Fact(DisplayName = "R16: a chunk release keeps the capture-finalization marker the last chunk published")]
    public void AChunkReleaseKeepsTheFinalizationMarker()
    {
        using var session = new TemporarySession();
        CaptureFinalizationV1 marker = new()
        {
            Contract = CaptureFinalizationV1.ContractName,
            CaptureId = Capture.Value,
            FinalizedUtc = Committed,
            ProvidersStopped = true,
            CallbacksDrained = true,
        };
        DerivedGenerationResult first = Publish(session.Store, 4, batchCapacity: 2);
        Assert.Null(CaptureFinalizationV1.Read(session.Store.Root, session.Store.Current!));
        _ = Publish(session.Store, 5, batchCapacity: 2, builder =>
        {
            builder.StageCaptureFinalization(marker);
            Assert.Throws<InvalidOperationException>(() => builder.StageCaptureFinalization(marker));
        });

        RetentionOutcome outcome = JournalRetention.Release(session.Store, 4, "the first chunk aged out", Committed, Committed);

        Assert.Equal([first.JournalName], outcome.RemovedFiles);
        Assert.Equal(marker, CaptureFinalizationV1.Read(session.Store.Root, outcome.Manifest));
        SessionStore reopened = session.Reopen();
        Assert.Equal(marker, CaptureFinalizationV1.Read(reopened.Root, reopened.Current!));
    }

    [Fact(DisplayName = "R16: a generation refuses a finalization marker naming another capture")]
    public void FinalizationForAnotherCaptureIsRefused()
    {
        using var session = new TemporarySession();
        CaptureFinalizationV1 foreign = new()
        {
            Contract = CaptureFinalizationV1.ContractName,
            CaptureId = Guid.NewGuid(),
            FinalizedUtc = Committed,
            ProvidersStopped = true,
            CallbacksDrained = true,
        };

        Assert.Throws<ArgumentException>(() =>
            Publish(session.Store, 1, beforeComplete: builder => builder.StageCaptureFinalization(foreign)));
        Assert.Throws<ArgumentException>(() =>
            Publish(session.Store, 1, beforeComplete: builder => builder.StageCaptureFinalization(foreign.Encode())));
    }

    [Fact(DisplayName = "I15: a chunk release gives up only the oldest chunks and never all of the evidence")]
    public void AChunkReleaseKeepsTheRecordingWhole()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult first = Publish(session.Store, 4, batchCapacity: 2);
        DerivedGenerationResult second = Publish(session.Store, 5, batchCapacity: 2);

        // The last chunk carried only the ledger, as a live recording's does when its final interval admitted nothing.
        DerivedGenerationResult last = Publish(session.Store, 0);

        // A chunk from the middle, or every chunk, would leave the recording with a hole or no boundary.
        Assert.Contains("the oldest chunks", Assert.Throws<ArgumentException>(() => session.Store.ReleaseJournalChunks(
            [second.JournalName], 5, "the middle", Committed, Committed)).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => session.Store.ReleaseJournalChunks(
            [first.JournalName, second.JournalName, last.JournalName], 9, "all of it", Committed, Committed));

        // Only the first chunk's end leaves records behind; the second's would leave just the empty last chunk.
        JournalReleasePreview preview = JournalRetention.Preview(session.Store, 9);
        Assert.Equal((4L, 4L), (preview.SmallestReleasingBoundary, preview.LargestReleasingBoundary));
        Assert.True(preview.WouldEmptyTheJournal);
        InvalidOperationException empty = Assert.Throws<InvalidOperationException>(() =>
            JournalRetention.Release(session.Store, 9, "everything recorded", Committed, Committed));
        Assert.Contains("no admitted evidence at all", empty.Message, StringComparison.Ordinal);
        Assert.Contains("The largest boundary this journal allows is 4.", empty.Message, StringComparison.Ordinal);

        InvalidOperationException nothing = Assert.Throws<InvalidOperationException>(() =>
            JournalRetention.Release(session.Store, 3, "too early", Committed, Committed));
        Assert.Contains("a chunk is the unit of release", nothing.Message, StringComparison.Ordinal);
        Assert.Contains("The smallest boundary this journal allows is 4.", nothing.Message, StringComparison.Ordinal);

        // Nothing was published by any refusal.
        Assert.Equal(last.Manifest.Generation, session.Store.Current!.Generation);
    }

    [Fact(DisplayName = "I15: an admitted journal is not released by dropping its name")]
    public void AJournalIsNotReleasedByDroppingItsName()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult first = Publish(session.Store, 4);

        ArgumentException refusal = Assert.Throws<ArgumentException>(() =>
            session.Store.ReleaseDependencies([first.JournalName], "shortcut", Committed, Committed));

        Assert.Contains("ADR-010 makes a journal release an extent", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I15: retention refuses a file the current generation does not hold")]
    public void RetentionRefusesAFileTheGenerationDoesNotHold()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, 4);

        ArgumentException refusal = Assert.Throws<ArgumentException>(() =>
            session.Store.ReleaseDependencies(
                ["seg-0000000009-0000.icats"],
                "not here",
                Committed,
                Committed));

        Assert.Contains("has nothing to release", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The digest a generation with these exact contents and no retention record has. It predates the
    /// retention field and must not change.
    /// </summary>
    private const string GoldenPlainDigest =
        "sha256:cce21b8b6adbfb7cbcad3e13f2cf0277d7e3d7b92cff025edb0426f80b8826c5";

    private static SourceClockDescriptor TestClock { get; } = new(
        Clock,
        HostId.Derive("evidence-retention-tests"),
        SourceClockKind.Monotonic,
        TimestampEncoding.Qpc,
        10_000_000,
        0,
        TimestampRounding.NearestEven,
        SourceClockMath.SessionTicksPerSecond * 60);

    private static SegmentIdentityV1 Identity { get; } = new()
    {
        CaptureId = Capture,
        ClockId = Clock,
        TimestampEncoding = TimestampEncoding.Qpc,
        Derivation = NormalizerContractVersion.V1,
    };

    /// <summary>
    /// Publishes a generation with one admitted record and one derived row per record, so a journal release
    /// has real batches to release and the retained file has real records to keep.
    /// </summary>
    private static DerivedGenerationResult Publish(
        SessionStore store,
        int records,
        int batchCapacity = 4_096,
        Action<DerivedGenerationBuilder>? beforeComplete = null)
    {
        using DerivedGenerationBuilder builder = DerivedGenerationBuilder.Begin(
            store,
            Identity,
            TestClock,
            Committed);
        var schemas = new JournalV1SchemaTable();
        uint schema = schemas.Intern(Provider, 10, 0, "sha256:" + new string('a', 64));
        uint policy = schemas.InternPolicy("metadata-only-admitted-projection-v1");
        builder.Journal.WriteSchemas(schemas);
        for (int index = 0; index < records; index++)
        {
            long ticks = 100L * (index + 1);
            builder.AddRow(Row(ticks, (ulong)index));
            builder.Journal.Append(Envelope(ticks, (ulong)index, schema, policy));
            if (batchCapacity < 4_096 && (index + 1) % batchCapacity == 0)
            {
                builder.Journal.FlushBatch();
            }
        }

        beforeComplete?.Invoke(builder);
        return builder.Complete(Committed);
    }

    private static ObservationRowV1 Row(long ticks, ulong ordinal) => new()
    {
        RawStreamId = 1,
        RawSourceEpoch = 1,
        RawRecordOrdinal = ordinal,
        FactKey = FactKey.Create("network-transfer"),
        ProviderId = Provider,
        EventId = 10,
        DescriptorVersion = 0,
        SchemaFingerprint = "sha256:" + new string('a', 64),
        Opcode = 10,
        NativeTicks = ticks,
        HeaderProcessId = 1_234,
        HeaderThreadId = 5_678,
        ProcessorNumber = 1,
        Mechanism = Mechanism.Tcp,
        Layer = ObservationLayer.Transport,
        Kind = ObservationKind.Send,
        Direction = Direction.Outbound,
        ByteValue = ticks,
        ByteDomain = InterCat.Domain.ByteDomain.TransportObserved,
        AccountingSide = InterCat.Domain.AccountingSide.SendSide,
        MeasurementUnit = InterCat.Domain.MeasurementUnit.Bytes,
        ByteAvailability = FieldAvailability.Present,
        StatusAvailability = FieldAvailability.NotApplicable,
        AttributionQuality = QualityLevel.Proven,
        CorrelationQuality = QualityLevel.UnknownQuality,
        MeasurementQuality = QualityLevel.Proven,
        TimingQuality = QualityLevel.Proven,
    };

    private static RecordEnvelopeV1 Envelope(long ticks, ulong ordinal, uint schema, uint policy) => new()
    {
        CaptureId = Capture,
        StreamId = 1,
        SourceEpoch = 1,
        RecordOrdinal = ordinal,
        Header = new(Provider, 10, 0, 0, 0, 10, 0, 0, 0, 0, 1_234, 5_678, Guid.Empty, Guid.Empty),
        BufferContext = new(1, 0),
        ClockId = Clock,
        TimestampEncoding = TimestampEncoding.Qpc,
        NativeTicks = ticks,
        PointerSize = 8,
        SchemaReference = schema,
        AdmissionPolicyReference = policy,
        ExtendedItems = [],
        OmittedExtendedItemCount = 0,
        Body = BodyV1.None,
    };

    private sealed class TemporarySession : IDisposable
    {
        public TemporarySession()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "InterCat.Storage.Tests.Retention",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Store = SessionStore.Open(LocalOwnedDirectory.Open(Path), Session, "retention-tests");
        }

        public string Path { get; }

        public SessionStore Store { get; private set; }

        public SessionStore Reopen()
        {
            Store = SessionStore.Open(LocalOwnedDirectory.Open(Path), Session, "retention-tests");
            return Store;
        }

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

    private sealed class ReadOnlyOwnedDirectory(IOwnedDirectory inner) : IOwnedDirectory
    {
        public string Path => inner.Path;

        public FileStream OpenOwnedFile(
            string name,
            FileMode mode,
            FileAccess access,
            FileShare share,
            FileOptions options) =>
            mode == FileMode.Open && access == FileAccess.Read
                ? inner.OpenOwnedFile(name, mode, access, share, options)
                : throw new UnauthorizedAccessException("Viewer root is read-only.");

        public void ReplaceOwnedFile(string sourceName, string destinationName) =>
            throw new UnauthorizedAccessException("Viewer root is read-only.");

        public bool RemoveOwnedFile(string name) =>
            throw new UnauthorizedAccessException("Viewer root is read-only.");
    }
}
