using System.Diagnostics;
using InterCat.Capture.Journal;
using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Capture.Journal.Tests;

/// <summary>
/// The live recorder publishes an owned capture's admitted records as a session a reader opens, with a live coverage
/// epoch built from the capture's own counters (IC-014). A scripted host stands in for ETW, so no elevation is needed.
/// </summary>
public sealed class LiveSessionRecorderTests
{
    private static readonly Guid Provider = Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88");
    private static readonly Guid Unrequested = Guid.Parse("11111111-2222-4333-8444-555555555555");

    [Fact(DisplayName = "R21: a live recording publishes its admitted records and what its sources delivered and lost")]
    public async Task RecordingPublishesASessionWithItsCoverage()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "live-tests");
        var host = new ScriptedHost();
        long now = Stopwatch.GetTimestamp();
        for (int index = 0; index < 3; index++)
        {
            host.Admit(new AdmittedEvent
            {
                SourceIndex = 0,
                EventId = 10,
                Version = 0,
                TimestampQpc = now + index,
                TimestampUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                RecordOrdinal = index + 1,
            });
        }

        host.Omit(new DeliveredRecord(Unrequested, 99, 0, now + 10), OmissionReason.UnrequestedProvider);

        LiveRecordingResult result = await LiveSessionRecorder.RecordAsync(
            Plan(),
            host,
            store,
            _ => host.Delivered.Task,
            DateTimeOffset.UtcNow);

        Assert.True(result.Start.Started);
        Assert.Equal(3, result.JournaledRecords);
        Assert.Equal(1, result.Generation!.Manifest.Generation);

        // The published generation reopens with its journal, rows and ledger, like an imported one.
        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory.Path));
        SessionManifestV1 manifest = reopened.Current!;
        Assert.Equal(3, manifest.Boundary.CommittedRecords);
        Assert.Equal(3, SessionSegments.Names(manifest).Sum(name => SessionSegments.Open(reopened.Root, manifest, name).RowCount));
        CoverageLedgerV1 ledger = SessionSegments.CoverageLedger(reopened.Root, manifest)!;
        CoverageEpochV1 epoch = Assert.Single(ledger.Epochs);
        Assert.Equal(CoverageAcquisition.LiveCapture, epoch.Acquisition);
        Assert.Equal("Sample-Network", Assert.Single(epoch.Collected).ProviderName);
        Assert.Equal(
            [(Provider, (int?)10, 3L, 3L, 0L), (Unrequested, (int?)null, 1L, 0L, 1L)],
            epoch.Deliveries.OrderBy(delivery => delivery.ProviderId == Unrequested)
                .Select(delivery => (delivery.ProviderId, delivery.EventId, delivery.Delivered, delivery.Admitted, delivery.Omitted)));
        Assert.Equal(
            [LossLayer.SourceSession, LossLayer.ConsumerBuffers, LossLayer.CallbackQueue, LossLayer.Storage],
            epoch.Losses.Select(loss => loss.Layer));
        Assert.All(epoch.Losses, loss => Assert.Equal(0, loss.Lost));

        // One capture per session: a second recording into it is refused before anything starts.
        await Assert.ThrowsAsync<InvalidOperationException>(() => LiveSessionRecorder.RecordAsync(
            Plan(), new ScriptedHost(), reopened, _ => Task.CompletedTask, DateTimeOffset.UtcNow));
    }

    [Fact(DisplayName = "R21: a recording published in chunks can be followed, and its last generation holds every chunk")]
    public async Task ChunkedRecordingPublishesAsItGoes()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "live-tests");
        var host = new ScriptedHost();
        long now = Stopwatch.GetTimestamp();
        for (int index = 0; index < 4; index++)
        {
            if (index == 2)
            {
                host.Pause(TimeSpan.FromMilliseconds(400));
            }

            var admitted = new AdmittedEvent
            {
                SourceIndex = 0,
                EventId = 10,
                Version = 0,
                TimestampQpc = now + index,
                TimestampUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                RecordOrdinal = index + 1,
            };
            admitted.SetSlot(0, 100 * (index + 1));
            admitted.SetSlot(1, 0x7000 + index);
            host.Admit(admitted);
        }

        LiveRecordingResult result = await LiveSessionRecorder.RecordAsync(
            Plan(withFields: true),
            host,
            store,
            _ => host.Delivered.Task,
            DateTimeOffset.UtcNow,
            publishEvery: TimeSpan.FromMilliseconds(100));

        // A generation was published while the capture was still recording, and each carried every chunk before it.
        Assert.Equal(4, result.JournaledRecords);
        Assert.InRange(result.Publications, 2, 3);
        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory.Path));
        SessionManifestV1 manifest = reopened.Current!;
        Assert.Equal(result.Publications, manifest.Dependencies.Count(dependency => dependency.Kind == StoreDependencyKind.Journal));

        // When it stopped, the recording coalesced its small publications, so it opens from one segment (ADR-026).
        Assert.Equal((1, (string?)null), (result.Compactions, result.CompactionFailure));
        Assert.Equal(result.Publications + 1, manifest.Generation);
        Assert.Equal(4, SessionSegments.Open(reopened.Root, manifest, Assert.Single(SessionSegments.Names(manifest))).RowCount);

        // The ledger describes the whole capture, so there is one, published with the last chunk.
        Assert.Single(manifest.Dependencies, dependency => dependency.Kind == StoreDependencyKind.CoverageLedger);
        Assert.Equal(4, SessionSegments.CoverageLedger(reopened.Root, manifest)!.Epochs[0].Deliveries.Sum(delivery => delivery.Admitted));

        // A journal-prefix release gives up whole chunks, so a boundary inside the first releases nothing.
        JournalReleasePreview inside = JournalRetention.Preview(reopened, 1);
        Assert.Equal((result.Publications, "chunk", false), (inside.JournalChunks, inside.ReleaseUnit, inside.ReleasesAnything));

        // A row's journal index counts its record across the chunks, so the recording's rows index 0..3 in order.
        (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) recorded = RowsOf(reopened, manifest);
        Assert.Equal([0UL, 1UL, 2UL, 3UL], recorded.Rows.Select(row => row.JournalRecordIndex!.Value));
        Assert.Equal([100L, 200L, 300L, 400L], recorded.Rows.Select(row => row.ByteValue!.Value));
        Assert.Equal([0x7000L, 0x7001L, 0x7002L, 0x7003L], recorded.Fields.Select(field => field.Value!.Value));

        // Re-derivation replays the chunks in order, as one capture, and derives exactly the rows the recording derived.
        JournalRederivationReadiness readiness = JournalRederivation.Assess(manifest);
        Assert.True(readiness.CanAttempt, readiness.Explanation);
        Assert.Contains($"{result.Publications} journal chunks", readiness.Explanation, StringComparison.Ordinal);
        JournalRederivationVerification verification = JournalRederivation.Verify(reopened);
        Assert.Equal(
            (manifest.Generation, 4L, 4L, (long)recorded.Fields.Length, result.Publications),
            (verification.SourceGeneration, verification.ReplayedRecords, verification.ObservationRows,
                verification.SourceFieldRows, verification.JournalChunks));
        JournalRederivationResult rebuilt = JournalRederivation.Rebuild(reopened, DateTimeOffset.UtcNow);
        SessionManifestV1 replaced = rebuilt.Generation.Manifest;
        Assert.Equal(manifest.Generation + 1, replaced.Generation);
        Assert.Equal(recorded.Rows, RowsOf(reopened, replaced).Rows);
        Assert.Equal(recorded.Fields, RowsOf(reopened, replaced).Fields);

        // The replacement carries every chunk, the plan and the ledger unchanged, and replaces only derived files.
        Assert.Equal(EvidenceOf(manifest), EvidenceOf(replaced));
        Assert.Equal(result.Publications, EvidenceOf(replaced).Count(dependency => dependency.Kind == StoreDependencyKind.Journal));
        Assert.Empty(SessionSegments.Names(replaced).Intersect(SessionSegments.Names(manifest), StringComparer.OrdinalIgnoreCase));
        Assert.NotNull(SessionSegments.CoverageLedger(reopened.Root, replaced));
        Assert.Equal(replaced.Generation, SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory.Path)).Current!.Generation);
    }

    [Fact(DisplayName = "I1: re-derivation refuses journal chunks that do not continue one another, and publishes nothing")]
    public async Task ChunksThatDoNotContinueEachOtherAreRefused()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "live-tests");
        var host = new ScriptedHost();
        long now = Stopwatch.GetTimestamp();

        // The second burst repeats the first's ordinals, as a chunk replayed twice would; the pause puts it in a later chunk.
        for (int index = 0; index < 4; index++)
        {
            if (index == 2)
            {
                host.Pause(TimeSpan.FromMilliseconds(600));
            }

            host.Admit(new AdmittedEvent
            {
                SourceIndex = 0,
                EventId = 10,
                Version = 0,
                TimestampQpc = now + index,
                TimestampUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                RecordOrdinal = (index % 2) + 1,
            });
        }

        LiveRecordingResult result = await LiveSessionRecorder.RecordAsync(
            Plan(),
            host,
            store,
            _ => host.Delivered.Task,
            DateTimeOffset.UtcNow,
            publishEvery: TimeSpan.FromMilliseconds(100));
        Assert.InRange(result.Publications, 2, 3);
        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory.Path));
        long published = reopened.Current!.Generation;

        InvalidDataException verifyRefused = Assert.Throws<InvalidDataException>(() => JournalRederivation.Verify(reopened));
        Assert.Contains("had already passed", verifyRefused.Message, StringComparison.Ordinal);
        InvalidDataException rebuildRefused = Assert.Throws<InvalidDataException>(() =>
            JournalRederivation.Rebuild(reopened, DateTimeOffset.UtcNow));
        Assert.Contains("not one recording", rebuildRefused.Message, StringComparison.Ordinal);
        Assert.Equal(published, SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory.Path)).Current!.Generation);
    }

    [Fact(DisplayName = "I15: after its oldest chunk is released, a recording is not re-derived without the rows of that chunk")]
    public async Task ReleasedChunksAreNotReDerivedAway()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "live-tests");
        var host = new ScriptedHost();
        long now = Stopwatch.GetTimestamp();
        for (int index = 0; index < 4; index++)
        {
            if (index == 2)
            {
                host.Pause(TimeSpan.FromMilliseconds(400));
            }

            var admitted = new AdmittedEvent
            {
                SourceIndex = 0,
                EventId = 10,
                Version = 0,
                TimestampQpc = now + index,
                TimestampUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                RecordOrdinal = index + 1,
            };
            admitted.SetSlot(0, 100 * (index + 1));
            admitted.SetSlot(1, 0x7000 + index);
            host.Admit(admitted);
        }

        LiveRecordingResult result = await LiveSessionRecorder.RecordAsync(
            Plan(withFields: true),
            host,
            store,
            _ => host.Delivered.Task,
            DateTimeOffset.UtcNow,
            publishEvery: TimeSpan.FromMilliseconds(100));
        Assert.InRange(result.Publications, 2, 3);
        SessionStore writable = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), store.Current!.SessionId, store.Current.SourceIdentity);

        // The first burst is the first chunk; releasing it keeps every row, including the two it derived.
        RetentionOutcome released = JournalRetention.Release(writable, 2, "older than the retained window", DateTimeOffset.UtcNow);
        Assert.Equal(2, released.Manifest.Retention!.ReleasedRecords);
        Assert.Equal(4, SessionSegments.Names(released.Manifest).Sum(name => SessionSegments.Open(writable.Root, released.Manifest, name).RowCount));

        // A replay could rebuild only the retained records, so re-derivation is refused rather than dropping two rows.
        JournalRederivationReadiness readiness = JournalRederivation.Assess(released.Manifest);
        Assert.False(readiness.CanAttempt);
        Assert.Contains("released 2 admitted records", readiness.Explanation, StringComparison.Ordinal);
        Assert.Contains("ADR-024", Assert.Throws<InvalidOperationException>(() => JournalRederivation.Verify(writable)).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => JournalRederivation.Rebuild(writable, DateTimeOffset.UtcNow));
        Assert.Equal(released.Manifest.Generation, writable.Current!.Generation);

        // A later generation no longer says what was released, but its rows still go back to the released records.
        string fields = SessionSegments.FieldNames(released.Manifest)[0];
        RetentionOutcome later = writable.ReleaseDependencies([fields], "source fields are not needed", DateTimeOffset.UtcNow);
        Assert.Equal(RetentionExtentKind.DerivedFiles, later.Manifest.Retention!.Kind);
        Assert.True(JournalRederivation.Assess(later.Manifest).CanAttempt);
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() =>
            JournalRederivation.Rebuild(writable, DateTimeOffset.UtcNow));
        Assert.Contains("rows go back to record 1, but its retained records begin at 3", refused.Message, StringComparison.Ordinal);
        Assert.Equal(later.Manifest.Generation, SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory.Path)).Current!.Generation);
    }

    [Fact(DisplayName = "I15: a long recording coalesces its small publications as it records and keeps every row")]
    public async Task ARecordingCompactsAsItRecords()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "live-tests");
        var host = new ScriptedHost();
        long now = Stopwatch.GetTimestamp();

        // Three bursts apart: every pause lets a small publication go out, and two of them make a compaction due.
        for (int index = 0; index < 6; index++)
        {
            if (index is 2 or 4)
            {
                host.Pause(TimeSpan.FromMilliseconds(400));
            }

            host.Admit(new AdmittedEvent
            {
                SourceIndex = 0,
                EventId = 10,
                Version = 0,
                TimestampQpc = now + index,
                TimestampUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                RecordOrdinal = index + 1,
            });
        }

        LiveRecordingResult result = await LiveSessionRecorder.RecordAsync(
            Plan(),
            host,
            store,
            _ => host.Delivered.Task,
            DateTimeOffset.UtcNow,
            publishEvery: TimeSpan.FromMilliseconds(100),
            compaction: new CompactionOptions { SmallUnitsBeforeCompaction = 2 });

        // Compactions ran while it recorded and once more when it stopped; every chunk of evidence is still named.
        Assert.Null(result.CompactionFailure);
        Assert.InRange(result.Compactions, 2, result.Publications);
        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory.Path));
        SessionManifestV1 manifest = reopened.Current!;
        Assert.Equal(result.Publications + result.Compactions, manifest.Generation);
        Assert.Equal(result.Publications, manifest.Dependencies.Count(dependency => dependency.Kind == StoreDependencyKind.Journal));

        // The finished recording opens from one segment holding all six rows, each at its place in the journal.
        SegmentReaderV1 segment = SessionSegments.Open(reopened.Root, manifest, Assert.Single(SessionSegments.Names(manifest)));
        Assert.Equal(
            [0UL, 1UL, 2UL, 3UL, 4UL, 5UL],
            Enumerable.Range(0, segment.RowCount).Select(row => segment.Row(row).JournalRecordIndex!.Value).Order());
        Assert.Equal(6, JournalRederivation.Verify(reopened).ObservationRows);
        Assert.NotNull(SessionSegments.CoverageLedger(reopened.Root, manifest));
    }

    [Fact(DisplayName = "R16: a privileged recording publishes evidence only, and a follower derives the session from it")]
    public async Task AFollowerDerivesEvidenceOnlyRecordings()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        using var derivedDirectory = new TemporaryDirectory();
        LiveRecordingResult recorded = await RecordEvidence(evidenceDirectory.Path, ordinals: [1, 2, 3, 4]);

        // The recorder derived nothing: its generations hold journal chunks, the plan and the ledger, and no rows.
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path));
        SessionManifestV1 source = evidence.Current!;
        Assert.Equal(0, recorded.Compactions);
        Assert.Empty(SessionSegments.Names(source));
        Assert.Empty(SessionSegments.FieldNames(source));
        StoreDependency[] sourceChunks = ChunksOf(source);
        Assert.Equal(recorded.Publications, sourceChunks.Length);
        Assert.InRange(sourceChunks.Length, 2, 3);

        // A follower mirrors every chunk and derives what an in-process recording derives.
        SessionStore derived = SessionStore.Open(LocalOwnedDirectory.Open(derivedDirectory.Path), source.SessionId, source.SourceIdentity);
        LiveSessionFollower follower = LiveSessionFollower.Open(evidence, derived);
        FollowStep step = follower.CatchUp();
        Assert.True(step.Finished);
        Assert.Equal((sourceChunks.Length, 4L, sourceChunks.Length, sourceChunks.Length), (step.MirroredChunks, step.DerivedRecords, step.DerivedChunks, step.EvidenceChunks));

        // Byte for byte: every chunk, the plan and the ledger are the evidence session's own.
        SessionManifestV1 mirror = derived.Current!;
        Assert.Equal(sourceChunks.Select(Measured), ChunksOf(mirror).Select(Measured));
        Assert.Equal(
            Measured(source.Dependencies.Single(dependency => dependency.Kind == StoreDependencyKind.DerivationPlan)),
            Measured(mirror.Dependencies.Single(dependency => dependency.Kind == StoreDependencyKind.DerivationPlan)));
        Assert.Equal(
            Measured(source.Dependencies.Single(dependency => dependency.Kind == StoreDependencyKind.CoverageLedger)),
            Measured(mirror.Dependencies.Single(dependency => dependency.Kind == StoreDependencyKind.CoverageLedger)));

        // Its rows are the rows a recording that derives in-process publishes, compacted when the capture stopped.
        (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) rows = RowsOf(derived, mirror);
        Assert.Single(SessionSegments.Names(mirror));
        Assert.Equal([0UL, 1UL, 2UL, 3UL], rows.Rows.Select(row => row.JournalRecordIndex!.Value));
        Assert.Equal([100L, 200L, 300L, 400L], rows.Rows.Select(row => row.ByteValue!.Value));
        Assert.Equal([0x7000L, 0x7001L, 0x7002L, 0x7003L], rows.Fields.Select(field => field.Value!.Value));
        JournalRederivationVerification replay = JournalRederivation.Verify(derived);
        Assert.Equal((4L, sourceChunks.Length), (replay.ObservationRows, replay.JournalChunks));

        // Following a finished capture again mirrors nothing.
        Assert.Equal((0, true), (follower.CatchUp().MirroredChunks, follower.CatchUp().Finished));
    }

    [Fact(DisplayName = "R16: a follower resumes where its session ends, and refuses what it does not mirror")]
    public async Task AFollowerResumesAndRefusesOtherEvidence()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        using var otherDirectory = new TemporaryDirectory();
        using var sessionDirectory = new TemporaryDirectory();
        using var derivedDirectory = new TemporaryDirectory();
        _ = await RecordEvidence(evidenceDirectory.Path, ordinals: [1, 2, 3, 4]);
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path));
        SessionManifestV1 source = evidence.Current!;

        // One chunk now, the rest from a fresh follower that reads what the session already mirrored.
        SessionStore derived = SessionStore.Open(LocalOwnedDirectory.Open(derivedDirectory.Path), source.SessionId, source.SourceIdentity);
        FollowStep first = LiveSessionFollower.Open(evidence, derived).CatchUp(maximumChunks: 1);
        Assert.Equal((1, false), (first.MirroredChunks, first.Finished));
        SessionStore reopened = SessionStore.Open(LocalOwnedDirectory.Open(derivedDirectory.Path), source.SessionId, source.SourceIdentity);
        FollowStep rest = LiveSessionFollower.Open(evidence, reopened).CatchUp();
        Assert.True(rest.Finished);
        Assert.Equal(4, rest.DerivedRecords);
        Assert.Equal([0UL, 1UL, 2UL, 3UL], RowsOf(reopened, reopened.Current!).Rows.Select(row => row.JournalRecordIndex!.Value));

        // Another capture's evidence is not what this session mirrors, so following it is refused.
        _ = await RecordEvidence(otherDirectory.Path, ordinals: [1, 2, 3, 4]);
        SessionStore other = SessionStore.OpenExisting(LocalOwnedDirectory.Open(otherDirectory.Path));
        Assert.Contains(
            "is not the evidence session's",
            Assert.Throws<InvalidDataException>(() => LiveSessionFollower.Open(other, reopened).CatchUp()).Message,
            StringComparison.Ordinal);

        // An ordinary session already has its rows, and an empty directory has published nothing to follow.
        SessionStore ordinary = SessionStore.Open(LocalOwnedDirectory.Open(sessionDirectory.Path), Guid.NewGuid(), "live-tests");
        var host = new ScriptedHost();
        host.Admit(new AdmittedEvent { SourceIndex = 0, EventId = 10, Version = 0, TimestampQpc = Stopwatch.GetTimestamp(), RecordOrdinal = 1 });
        _ = await LiveSessionRecorder.RecordAsync(Plan(), host, ordinary, _ => host.Delivered.Task, DateTimeOffset.UtcNow);
        using var emptyDirectory = new TemporaryDirectory();
        SessionStore empty = SessionStore.Open(LocalOwnedDirectory.Open(emptyDirectory.Path), Guid.NewGuid(), "live-tests");
        Assert.Throws<InvalidOperationException>(() => LiveSessionFollower.Open(
            SessionStore.OpenExisting(LocalOwnedDirectory.Open(sessionDirectory.Path)), empty).CatchUp());
        Assert.Throws<ArgumentException>(() => LiveSessionFollower.Open(
            SessionStore.OpenExisting(LocalOwnedDirectory.Open(emptyDirectory.Path)), empty));
    }

    /// <summary>Records bursts of records 400 ms apart as evidence only, publishing every 100 ms.</summary>
    private static async Task<LiveRecordingResult> RecordEvidence(string directory, int[] ordinals)
    {
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "live-tests");
        var host = new ScriptedHost();
        long now = Stopwatch.GetTimestamp();
        for (int index = 0; index < ordinals.Length; index++)
        {
            if (index == 2)
            {
                host.Pause(TimeSpan.FromMilliseconds(400));
            }

            var admitted = new AdmittedEvent
            {
                SourceIndex = 0,
                EventId = 10,
                Version = 0,
                TimestampQpc = now + index,
                TimestampUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                RecordOrdinal = ordinals[index],
            };
            admitted.SetSlot(0, 100 * (index + 1));
            admitted.SetSlot(1, 0x7000 + index);
            host.Admit(admitted);
        }

        return await LiveSessionRecorder.RecordAsync(
            Plan(withFields: true),
            host,
            store,
            _ => host.Delivered.Task,
            DateTimeOffset.UtcNow,
            publishEvery: TimeSpan.FromMilliseconds(100),
            output: LiveRecordingOutput.EvidenceOnly);
    }

    private static StoreDependency[] ChunksOf(SessionManifestV1 manifest) =>
    [
        .. manifest.Dependencies
            .Where(dependency => dependency.Kind == StoreDependencyKind.Journal)
            .OrderBy(dependency => dependency.Name, StringComparer.Ordinal),
    ];

    /// <summary>What identifies a dependency's bytes, whatever name each session gives it.</summary>
    private static (long Length, string Digest) Measured(StoreDependency dependency) => (dependency.LengthBytes, dependency.Digest);

    [Fact]
    public async Task EvidenceRecorderSignalsReadyOnlyAfterItsWriterStarts()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "ready-test");
        var host = new ScriptedHost();
        host.Admit(new AdmittedEvent
        {
            SourceIndex = 0,
            EventId = 10,
            Version = 0,
            TimestampQpc = Stopwatch.GetTimestamp(),
            RecordOrdinal = 1,
        });
        int readyCount = 0;

        LiveCaptureResult result = await LiveRecorder.RecordAsync(
            Plan(),
            host,
            store,
            _ =>
            {
                Assert.Equal(1, readyCount);
                return host.Delivered.Task;
            },
            DateTimeOffset.UtcNow,
            onReady: start =>
            {
                Assert.True(start.Started);
                Assert.Null(store.Current);
                readyCount++;
            });

        Assert.Equal(1, readyCount);
        Assert.True(result.Start.Started);
        Assert.NotNull(result.Generation);
        Assert.Equal(1, result.JournaledRecords);

        using var refusedDirectory = new TemporaryDirectory();
        SessionStore refusedStore = SessionStore.Open(
            LocalOwnedDirectory.Open(refusedDirectory.Path), Guid.NewGuid(), "ready-refusal-test");
        LiveCaptureResult refused = await LiveRecorder.RecordAsync(
            Plan(), new ScriptedHost { FailCreate = true }, refusedStore,
            _ => Task.CompletedTask, DateTimeOffset.UtcNow,
            onReady: _ => readyCount++);
        Assert.False(refused.Start.Started);
        Assert.Equal(1, readyCount);
    }

    [Fact(DisplayName = "R16: an exact journal quota stops capture and publishes a readable bounded journal")]
    public async Task JournalQuotaStopsWithFinalEvidenceAndLoss()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "quota-test");
        var host = new ScriptedHost();
        long now = Stopwatch.GetTimestamp();
        for (int index = 0; index < 10_000; index++)
        {
            host.Admit(new AdmittedEvent
            {
                SourceIndex = 0,
                EventId = 10,
                Version = 0,
                TimestampQpc = now + index,
                RecordOrdinal = index + 1,
            });
        }

        LiveCaptureResult result = await LiveRecorder.RecordAsync(
            Plan(), host, store,
            token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            DateTimeOffset.UtcNow,
            maximumJournalBytes: 1_048_576);

        Assert.True(result.Start.Started);
        Assert.True(result.JournalQuotaReached);
        Assert.NotNull(result.Stop);
        Assert.NotNull(result.Generation);
        Assert.InRange(result.Generation.JournalBytes, 1, 1_048_576);
        Assert.InRange(result.JournaledRecords, 1, 9_999);
        Assert.NotNull(store.Current);
        Assert.NotNull(result.Coverage);
    }

    [Fact(DisplayName = "R16: bounded live chunks remain followable without crossing their aggregate journal quota")]
    public async Task JournalQuotaReservesNextChunkForLivePublication()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "quota-follow-test");
        var host = new ScriptedHost();
        long now = Stopwatch.GetTimestamp();
        host.Admit(new AdmittedEvent
        {
            SourceIndex = 0,
            EventId = 10,
            Version = 0,
            TimestampQpc = now,
            RecordOrdinal = 1,
        });
        host.Pause(TimeSpan.FromSeconds(2));
        for (int index = 1; index < 10_000; index++)
        {
            host.Admit(new AdmittedEvent
            {
                SourceIndex = 0,
                EventId = 10,
                Version = 0,
                TimestampQpc = now + index,
                RecordOrdinal = index + 1,
            });
        }

        LiveCaptureResult result = await LiveRecorder.RecordAsync(
            Plan(), host, store,
            async token =>
            {
                DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(1);
                while (store.Current is null && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10), token);
                }

                Assert.NotNull(SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory.Path)).Current);
                Assert.False(host.Delivered.Task.IsCompleted);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            DateTimeOffset.UtcNow,
            publishEvery: TimeSpan.FromMilliseconds(20),
            maximumJournalBytes: 1_048_576);

        Assert.True(result.JournalQuotaReached);
        Assert.NotNull(result.Generation);
        Assert.True(result.Publications >= 2);
        StoreDependency[] journals = [.. result.Generation.Manifest.Dependencies
            .Where(dependency => dependency.Kind == StoreDependencyKind.Journal)];
        Assert.Equal(result.Publications, journals.Length);
        Assert.InRange(journals.Sum(dependency => dependency.LengthBytes), 1, 1_048_576);
        Assert.NotNull(result.Coverage);
    }

    [Fact(DisplayName = "R21: a live capture that cannot start publishes nothing and leaves its session empty")]
    public async Task ACaptureThatCannotStartPublishesNothing()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "live-tests");

        LiveRecordingResult result = await LiveSessionRecorder.RecordAsync(
            Plan(),
            new ScriptedHost { FailCreate = true },
            store,
            _ => Task.CompletedTask,
            DateTimeOffset.UtcNow);

        Assert.False(result.Start.Started);
        Assert.Null(result.Generation);
        Assert.Null(store.Current);
    }

    [Fact(DisplayName = "R21: a recording whose loss counters could not be read publishes its records and no coverage claim")]
    public async Task UnreadableLossCountersPublishNoLedger()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "live-tests");
        var host = new ScriptedHost { FailLossRead = true };
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
            Plan(), host, store, _ => host.Delivered.Task, DateTimeOffset.UtcNow);

        // The records are the capture's evidence either way; what it lost cannot be claimed, so coverage stays unknown.
        Assert.Equal(1, result.JournaledRecords);
        Assert.NotNull(result.Generation);
        Assert.Null(result.Coverage);
        Assert.Null(SessionSegments.CoverageLedger(store.Root, store.Current!));
    }

    [Fact(DisplayName = "R21: a record the full queue dropped is callback-queue loss, not a delivery of its descriptor")]
    public void QueueDropsAreLossNotDeliveries()
    {
        // A plan with no provider request, as an import's is, admitting the catalog's network source.
        OwnedSessionPlan requested = Plan();
        OwnedSessionPlan unnamed = requested with
        {
            Providers = [],
            Sources = [requested.Sources[0] with { SourceId = WindowsSourceCatalog.KernelNetworkSourceId }],
        };
        var tally = new CaptureCoverageTally(unnamed, CoverageAcquisition.LiveCapture);
        var delivered = new DeliveredRecord(Provider, 10, 0, 1_000);
        for (int index = 0; index < 3; index++)
        {
            tally.Delivered(in delivered);
        }

        tally.Admitted(Provider, 10, 0, queued: true);
        tally.Admitted(Provider, 10, 0, queued: true);
        tally.Admitted(Provider, 10, 0, queued: false);

        CoverageLedgerV1 ledger = tally.ToLedger(
        [
            new() { Layer = LossLayer.SourceSession, Lost = 0 },
            new() { Layer = LossLayer.ConsumerBuffers, Lost = 0 },
            new() { Layer = LossLayer.CallbackQueue, Lost = tally.QueueDrops },
            new() { Layer = LossLayer.Storage, Lost = 0 },
        ]);

        CoverageDeliveryV1 delivery = Assert.Single(Assert.Single(ledger.Epochs).Deliveries);
        Assert.Equal((2L, 2L), (delivery.Delivered, delivery.Admitted));
        Assert.Equal(1, tally.QueueDrops);

        // With no provider request to name it, the source catalog names the provider admission compiled.
        Assert.Equal("Microsoft-Windows-Kernel-Network", Assert.Single(ledger.Epochs[0].Collected).ProviderName);
    }

    /// <summary>A generation's observation and source-field rows, in journal order.</summary>
    private static (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) RowsOf(SessionStore store, SessionManifestV1 manifest)
    {
        ObservationRowV1[] rows =
        [
            .. SessionSegments.Names(manifest)
                .Select(name => SessionSegments.Open(store.Root, manifest, name))
                .SelectMany(segment => Enumerable.Range(0, segment.RowCount).Select(segment.Row))
                .OrderBy(row => row.JournalRecordIndex),
        ];
        SourceFieldRowV1[] fields =
        [
            .. SessionSegments.FieldNames(manifest)
                .Select(name => SessionSegments.Open(store.Root, manifest, name))
                .SelectMany(segment => Enumerable.Range(0, segment.RowCount).Select(segment.FieldRow))
                .OrderBy(field => field.RawRecordOrdinal)
                .ThenBy(field => field.NativeTicks)
                .ThenBy(field => field.Field.ToString(), StringComparer.Ordinal),
        ];
        return (rows, fields);
    }

    /// <summary>The dependencies a replacement derivation must carry unchanged: journals, the plan and the ledger.</summary>
    private static StoreDependency[] EvidenceOf(SessionManifestV1 manifest) =>
    [
        .. manifest.Dependencies
            .Where(dependency => dependency.Kind is StoreDependencyKind.Journal
                or StoreDependencyKind.DerivationPlan
                or StoreDependencyKind.CoverageLedger)
            .OrderBy(dependency => dependency.Name, StringComparer.OrdinalIgnoreCase),
    ];

    private static OwnedSessionPlan Plan(bool withFields = false)
    {
        var request = new ProviderEnablementRequest
        {
            SourceId = "etw/manifest/Sample",
            ProviderName = "Sample-Network",
            ProviderGuid = Provider,
            Level = 4,
            MatchAnyKeyword = 0x10,
            EventIdsToEnable = [10],
        };

        return new()
        {
            Identity = CaptureSessionIdentity.Create("test", 4242),
            Providers = [request],
            Sources =
            [
                new()
                {
                    SourceId = request.SourceId,
                    ProviderGuid = Provider,
                    SourceIndex = 0,
                    Events =
                    [
                        new()
                        {
                            SourceIndex = 0,
                            ProviderGuid = Provider,
                            EventId = 10,
                            Version = 0,
                            Name = "sent",
                            Mechanism = Mechanism.Tcp,
                            Layer = ObservationLayer.Transport,
                            Kind = ObservationKind.Send,
                            Direction = Direction.Outbound,
                            MinimumBodyLength = withFields ? 12 : 0,
                            SchemaFingerprint = "sha256:fixture-live-recording",
                            BodyPolicy = CaptureBodyAdmissionPolicies.MetadataOnly,
                            Slots = withFields
                                ?
                                [
                                    new("size", FieldRole.ByteCount, 0, 4, MeasurementUnit.Bytes,
                                        ByteDomain.TransportObserved, SlotTransform.None),
                                    new("connid", FieldRole.CorrelationKey, 4, 8, null, null, SlotTransform.None)
                                    {
                                        SourceField = SourceField.ConnectionId,
                                    },
                                ]
                                : [],
                            FieldReport = withFields
                                ?
                                [
                                    new("size", FieldAvailability.Present, FieldRole.ByteCount,
                                        Unit: MeasurementUnit.Bytes, ByteDomain: ByteDomain.TransportObserved),
                                    new("connid", FieldAvailability.Present, FieldRole.CorrelationKey),
                                ]
                                : [],
                        },
                    ],
                    Diagnostics = [],
                },
            ],
        };
    }

    /// <summary>A host whose one session delivers a scripted sequence of outcomes, then waits to be stopped.</summary>
    private sealed class ScriptedHost : IEtwSessionHost
    {
        private readonly List<Action<IAdmittedEventSink>> script = [];

        public bool FailCreate { get; init; }

        public bool FailLossRead { get; init; }

        /// <summary>Completes once every scripted outcome reached the sink.</summary>
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool? IsElevated => true;

        public void Admit(AdmittedEvent admitted) => script.Add(sink =>
        {
            sink.OnObserved(new DeliveredRecord(Provider, admitted.EventId, admitted.Version, admitted.TimestampQpc));
            _ = sink.Admit(admitted);
        });

        public void Pause(TimeSpan pause) => script.Add(_ => Thread.Sleep(pause));

        public void Omit(DeliveredRecord delivered, OmissionReason reason) => script.Add(sink =>
        {
            sink.OnObserved(in delivered);
            sink.OnOmitted(reason, in delivered);
        });

        public IReadOnlyList<string> ListActiveSessionNames() => [];

        public IOwnedEtwSession CreateExclusive(OwnedSessionPlan plan) =>
            FailCreate ? throw new EtwSessionException("the session could not be created in this test") : new Session(this, plan);

        private sealed class Session(ScriptedHost host, OwnedSessionPlan plan) : IOwnedEtwSession
        {
            private volatile bool stopRequested;

            public string SessionName => plan.Identity.SessionName;

            public ProviderEnablementResult Enable(ProviderEnablementRequest request) => new(request.SourceId, true, null);

            public bool TryRequestCaptureState(ProviderEnablementRequest request, out string? failureReason)
            {
                failureReason = null;
                return true;
            }

            public void Pump(EventAdmissionTable table, IAdmittedEventSink sink, CancellationToken cancellationToken)
            {
                foreach (Action<IAdmittedEventSink> step in host.script)
                {
                    step(sink);
                }

                host.Delivered.TrySetResult();
                while (!stopRequested && !cancellationToken.IsCancellationRequested)
                {
                    Thread.Sleep(5);
                }
            }

            public void RequestStopProcessing() => stopRequested = true;

            public SourceLossReading ReadLoss() =>
                host.FailLossRead ? throw new EtwSessionException("the counters could not be read in this test") : new(0, 0);

            public void StopSession()
            {
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "intercat-live-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
