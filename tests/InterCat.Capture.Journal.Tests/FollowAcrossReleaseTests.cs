using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Capture.Journal.Tests.EvidenceRecordings;

namespace InterCat.Capture.Journal.Tests;

/// <summary>
/// A follow across releases of the session it derives into (ADR-044): the chunks the session still holds are found among
/// the evidence's by their bytes, so the follow goes on from them, counting the capture's chunks and numbering every record
/// as the capture did, whether the same follower goes on or a fresh one resumes.
/// </summary>
public sealed class FollowAcrossReleaseTests
{
    [Fact(DisplayName = "R16: a follower goes on across releases of the session it derives into, numbering every record as the capture did")]
    public async Task AFollowGoesOnAcrossItsSessionsReleases()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        using var wholeDirectory = new TemporaryDirectory();
        using var liveDirectory = new TemporaryDirectory();
        using var resumedDirectory = new TemporaryDirectory();
        _ = await RecordEvidence(evidenceDirectory.Path, ordinals: [1, 2, 3, 4, 5, 6], bursts: [2, 4]);
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path));
        SessionManifestV1 source = evidence.Current!;
        int chunks = ChunksOf(source);
        Assert.True(chunks >= 3, $"The recording published {chunks} chunks; three bursts publish at least three.");

        // What a follow that releases nothing derives: each record's journal index, by its ordinal.
        SessionStore whole = Open(wholeDirectory.Path, source);
        Assert.True(LiveSessionFollower.Open(evidence, whole).CatchUp().Finished);
        Dictionary<ulong, ulong> indexOf = Rows(whole).ToDictionary(row => row.RawRecordOrdinal, row => row.JournalRecordIndex!.Value);
        Assert.Equal(6, indexOf.Count);

        // A follow mirrors two chunks, its session releases the older, and the same follower goes on: what it mirrors next
        // continues the capture's numbering, and it counts the capture's chunks, the released one with them.
        SessionStore live = Open(liveDirectory.Path, source);
        LiveSessionFollower follower = LiveSessionFollower.Open(evidence, live);
        Assert.Equal(2, follower.CatchUp(maximumChunks: 2).DerivedChunks);
        ReleaseOldest(live);
        Assert.Equal(1, ChunksOf(live.Current!));
        Assert.Equal(2, LiveSessionFollower.Followed(source, live.Current));
        FollowStep rest = follower.CatchUp();
        Assert.True(rest.Finished);
        Assert.Equal((chunks, chunks, chunks - 2), (rest.DerivedChunks, rest.EvidenceChunks, rest.MirroredChunks));
        Assert.Equal(chunks - 1, ChunksOf(live.Current!));
        Assert.All(Rows(live), row => Assert.Equal(indexOf[row.RawRecordOrdinal], row.JournalRecordIndex));
        Assert.Contains(Rows(live), row => indexOf[row.RawRecordOrdinal] == 5);

        // A follower opened afresh on a session that released its oldest chunk reads that chunk from the evidence, and goes
        // on alike.
        SessionStore resumed = Open(resumedDirectory.Path, source);
        _ = LiveSessionFollower.Open(evidence, resumed).CatchUp(maximumChunks: 2);
        ReleaseOldest(resumed);
        SessionStore reopened = Open(resumedDirectory.Path, source);
        FollowStep after = LiveSessionFollower.Open(evidence, reopened).CatchUp();
        Assert.True(after.Finished);
        Assert.Equal((chunks, chunks), (after.DerivedChunks, after.EvidenceChunks));
        Assert.All(Rows(reopened), row => Assert.Equal(indexOf[row.RawRecordOrdinal], row.JournalRecordIndex));
        Assert.Equal(chunks, LiveSessionFollower.Followed(source, reopened.Current));

        // A released chunk read back from the evidence is checked as a mirror checks it: changed bytes are refused, never
        // counted, even where the file still lists as the evidence's reader last measured it.
        string path = Path.Combine(evidenceDirectory.Path, SourceChunks(source)[0].Name);
        DateTime written = File.GetLastWriteTimeUtc(path);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, written);
        Assert.StartsWith("The evidence chunk", Assert.Throws<InvalidDataException>(
            () => LiveSessionFollower.Open(evidence, Open(resumedDirectory.Path, source))).Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R16: a follower refuses a session that begins after the evidence's first chunk with no release stated, or whose chunks leave the evidence's order")]
    public async Task AFollowerRefusesARunTheEvidenceDidNotRecord()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        using var laterDirectory = new TemporaryDirectory();
        using var skippedDirectory = new TemporaryDirectory();
        _ = await RecordEvidence(evidenceDirectory.Path, ordinals: [1, 2, 3, 4, 5, 6], bursts: [2, 4]);
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path));
        SessionManifestV1 source = evidence.Current!;
        StoreDependency[] chunks = SourceChunks(source);

        // A session holding the evidence's second chunk first, which no release of it gave up the first of.
        SessionStore later = Open(laterDirectory.Path, source);
        MirrorByHand(evidence, later, chunks[1]);
        Assert.Contains("states no release of the chunks before it", Assert.Throws<InvalidDataException>(
            () => LiveSessionFollower.Open(evidence, later)).Message, StringComparison.Ordinal);

        // One holding the first and the third: its second chunk is not the evidence's second.
        SessionStore skipped = Open(skippedDirectory.Path, source);
        MirrorByHand(evidence, skipped, chunks[0], chunks[2]);
        Assert.Contains("is not the evidence session's", Assert.Throws<InvalidDataException>(
            () => LiveSessionFollower.Open(evidence, skipped)).Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§3.1: a crashed follow whose session released its oldest chunk counts that chunk as followed, and is finished from the evidence")]
    public async Task ACrashedFollowAcrossAReleaseIsFinished()
    {
        using var root = new TemporaryDirectory();
        string evidencePath = Path.Combine(root.Path, "capture");
        Directory.CreateDirectory(evidencePath);
        _ = await RecordEvidence(evidencePath, ordinals: [1, 2, 3, 4, 5, 6], bursts: [2, 4]);
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidencePath));
        SessionManifestV1 source = evidence.Current!;
        int published = ChunksOf(source);

        // The viewer followed two chunks, its session released the older, and it crashed: its ticket stays beside it.
        string session = Path.Combine(root.Path, "explore-1");
        Directory.CreateDirectory(session);
        SessionStore crashed = Open(session, source);
        _ = LiveSessionFollower.Open(evidence, crashed).CatchUp(maximumChunks: 2);
        ReleaseOldest(crashed);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        LiveFollowTicket.For(source.SessionId, evidencePath, session, now, now.AddSeconds(30)).Hold().Dispose();

        // The card counts both chunks as followed, the released one with the one the session holds.
        LiveFollowTicket ticket = Assert.Single(LiveFollowTicket.FindInterrupted(root.Path));
        InterruptedFollow found = InterruptedFollow.Assess(ticket, now);
        Assert.Equal(InterruptedFollowState.Finishable, found.State);
        Assert.Equal((2, published, published - 2), (found.SessionChunks, found.EvidenceChunks, found.MissingChunks));

        InterruptedFollowResult finished = InterruptedFollow.Finish(ticket, nowUtc: now);
        Assert.True(finished.Completed);
        Assert.Equal((published, published, 6L), (finished.Step.DerivedChunks, finished.Step.EvidenceChunks, finished.Step.DerivedRecords));
        Assert.Equal(InterruptedFollowState.Complete, InterruptedFollow.Assess(ticket, now).State);
    }

    [Fact(DisplayName = "R16: evidence that released its own oldest records is not followed, since it no longer says where the capture's journal began")]
    public async Task EvidenceThatReleasedRecordsIsNotFollowed()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        using var derivedDirectory = new TemporaryDirectory();
        _ = await RecordEvidence(evidenceDirectory.Path, ordinals: [1, 2, 3, 4, 5, 6], bursts: [2, 4]);
        SessionManifestV1 recorded = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path)).Current!;
        SessionStore writer = SessionStore.Open(LocalOwnedDirectory.Open(evidenceDirectory.Path), recorded.SessionId, recorded.SourceIdentity);
        JournalReleasePreview preview = JournalRetention.Preview(writer, 3);
        Assert.True(preview.ReleasesAnything, preview.ToString());
        _ = JournalRetention.Release(writer, 3, "older than the retained window", DateTimeOffset.UtcNow);

        // A follow numbered from its first chunk now would give its records the indices of the ones it released.
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path));
        LiveSessionFollower follower = LiveSessionFollower.Open(evidence, Open(derivedDirectory.Path, recorded));
        Assert.Contains("released its oldest records", Assert.Throws<InvalidDataException>(() => follower.CatchUp()).Message,
            StringComparison.Ordinal);
        Assert.Null(SessionStore.OpenExisting(LocalOwnedDirectory.Open(derivedDirectory.Path)).Current);
    }

    /// <summary>Releases every chunk of the session but its newest, as rolling retention would: by interval (ADR-043).</summary>
    private static void ReleaseOldest(SessionStore store)
    {
        IntervalReleasePreview preview = IntervalRelease.Preview(store, long.MaxValue);
        Assert.True(preview.ReleasesAnything, IntervalRelease.Refusal(preview));
        _ = IntervalRelease.Release(store, preview.MostReleasingNanoseconds!.Value, "older than the retained window", DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Copies evidence chunks into a session by hand, each in a generation of its own with no rows: chunks no follow would
    /// leave there.
    /// </summary>
    private static void MirrorByHand(SessionStore evidence, SessionStore derived, params StoreDependency[] chunks)
    {
        foreach (StoreDependency chunk in chunks)
        {
            using FileStream stream = evidence.Root.OpenOwnedFile(
                chunk.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
            (CaptureId capture, SourceClockDescriptor clock) = JournalV1Reader.ReadSourceClock(stream);
            var identity = new SegmentIdentityV1
            {
                CaptureId = capture,
                ClockId = clock.Id,
                TimestampEncoding = clock.Encoding,
                Derivation = ObservationNormalizerV1.ContractVersion,
            };
            using DerivedGenerationBuilder builder = DerivedGenerationBuilder.BeginMirror(derived, identity, clock, stream);
            _ = builder.CompleteMirror(0, DateTimeOffset.UtcNow);
        }
    }

    private static StoreDependency[] SourceChunks(SessionManifestV1 manifest) =>
    [
        .. manifest.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Journal)
            .OrderBy(dependency => dependency.Name, StringComparer.OrdinalIgnoreCase),
    ];

    private static SessionStore Open(string directory, SessionManifestV1 source) =>
        SessionStore.Open(LocalOwnedDirectory.Open(directory), source.SessionId, source.SourceIdentity);

    private static int ChunksOf(SessionManifestV1 manifest) =>
        manifest.Dependencies.Count(dependency => dependency.Kind == StoreDependencyKind.Journal);

    private static List<ObservationRowV1> Rows(SessionStore store)
    {
        SessionManifestV1 manifest = store.Current!;
        var rows = new List<ObservationRowV1>();
        foreach (string name in SessionSegments.Names(manifest))
        {
            SegmentReaderV1 segment = SessionSegments.Open(store.Root, manifest, name);
            for (int row = 0; row < segment.RowCount; row++)
            {
                rows.Add(segment.Row(row));
            }
        }

        store.ReleaseSegmentReaders();
        return rows;
    }
}
