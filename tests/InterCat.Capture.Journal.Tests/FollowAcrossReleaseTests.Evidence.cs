using InterCat.Storage;
using Xunit;
using static InterCat.Capture.Journal.Tests.EvidenceRecordings;

namespace InterCat.Capture.Journal.Tests;

/// <summary>
/// A follow across releases of the evidence it follows (ADR-048): the evidence gives up its oldest chunks whole, stating
/// how many of the capture's chunks and records it gave up in all, so the chunks the derived session holds are still found
/// among the evidence's by their bytes and the statement places them - whether the follow goes on or a fresh one resumes.
/// </summary>
public sealed partial class FollowAcrossReleaseTests
{
    [Fact(DisplayName = "R16: a follower goes on after the evidence releases chunks its session gave up, numbering every record as the capture did")]
    public async Task AFollowGoesOnAcrossTheEvidencesReleases()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        using var wholeDirectory = new TemporaryDirectory();
        using var liveDirectory = new TemporaryDirectory();
        using var resumedDirectory = new TemporaryDirectory();
        using var aheadDirectory = new TemporaryDirectory();
        _ = await RecordEvidence(evidenceDirectory.Path, ordinals: [1, 2, 3, 4, 5, 6, 7, 8], bursts: [2, 4, 6]);
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path));
        SessionManifestV1 source = evidence.Current!;
        int chunks = ChunksOf(source);
        Assert.True(chunks >= 4, $"The recording published {chunks} chunks; four bursts publish at least four.");
        Dictionary<ulong, ulong> indexOf = IndicesOf(evidence, wholeDirectory.Path, source);
        long[] held = [.. JournalRetention.Units(evidence, source).Units.Select(unit => unit.Records)];

        // One follow mirrors three chunks and its session releases the two older; a second mirrors three alike and stops;
        // a third mirrors two and releases the older.
        SessionStore live = Open(liveDirectory.Path, source);
        LiveSessionFollower follower = LiveSessionFollower.Open(evidence, live);
        _ = follower.CatchUp(maximumChunks: 3);
        ReleaseOldest(live);
        SessionStore ahead = Open(aheadDirectory.Path, source);
        _ = LiveSessionFollower.Open(evidence, ahead).CatchUp(maximumChunks: 3);
        ReleaseOldest(ahead);
        SessionStore resumed = Open(resumedDirectory.Path, source);
        _ = LiveSessionFollower.Open(evidence, resumed).CatchUp(maximumChunks: 2);
        ReleaseOldest(resumed);

        // The evidence releases its oldest chunk, which every session gave up, stating what it gave up in all. The capture
        // is still counted whole, and each session's chunks where they lie in it.
        SessionManifestV1 once = ReleaseEvidence(evidenceDirectory.Path, 1);
        Assert.Equal(new ReleasedRecording(1, held[0]), once.Retention!.Recording);
        Assert.Equal((chunks, true), LiveSessionFollower.Progress(once));
        Assert.Equal((3, 3, 2), (LiveSessionFollower.Followed(once, live.Current), LiveSessionFollower.Followed(once, ahead.Current),
            LiveSessionFollower.Followed(once, resumed.Current)));

        // A follower opened afresh on a session that holds the evidence's oldest chunk counts what the evidence gave up from
        // its statement; one opened on a session that gave up more reads the chunk between from the evidence. Both go on
        // numbering every record as the capture did.
        foreach (string directory in new[] { resumedDirectory.Path, aheadDirectory.Path })
        {
            SessionStore reopened = Open(directory, source);
            FollowStep step = LiveSessionFollower.Open(evidence, reopened).CatchUp();
            Assert.True(step.Finished);
            Assert.Equal((chunks, chunks, 8L), (step.DerivedChunks, step.EvidenceChunks, step.DerivedRecords));
            Assert.All(Rows(reopened), row => Assert.Equal(indexOf[row.RawRecordOrdinal], row.JournalRecordIndex));
            Assert.Equal(chunks, LiveSessionFollower.Followed(once, reopened.Current));
        }

        // A second release adds its chunk and records to the first's; the follower that mirrored before either goes on.
        SessionManifestV1 twice = ReleaseEvidence(evidenceDirectory.Path, 1);
        Assert.Equal(new ReleasedRecording(2, held[0] + held[1]), twice.Retention!.Recording);
        Assert.Equal(chunks - 2, ChunksOf(twice));
        Assert.Equal(3, LiveSessionFollower.Followed(twice, live.Current));
        FollowStep rest = follower.CatchUp();
        Assert.True(rest.Finished);
        Assert.Equal((chunks, chunks, chunks - 3, 8L), (rest.DerivedChunks, rest.EvidenceChunks, rest.MirroredChunks, rest.DerivedRecords));
        Assert.All(Rows(live), row => Assert.Equal(indexOf[row.RawRecordOrdinal], row.JournalRecordIndex));
        Assert.Contains(Rows(live), row => indexOf[row.RawRecordOrdinal] == 7);

        // The session that holds the capture from its second chunk now holds one the evidence released too: it is placed by
        // the evidence's oldest, its second, and a follower opened on it counts that chunk among the records stated.
        SessionStore whole = Open(resumedDirectory.Path, source);
        Assert.Equal(chunks, LiveSessionFollower.Followed(twice, whole.Current));
        FollowStep again = LiveSessionFollower.Open(evidence, whole).CatchUp();
        Assert.Equal((chunks, chunks, 0, 8L), (again.DerivedChunks, again.EvidenceChunks, again.MirroredChunks, again.DerivedRecords));
    }

    [Fact(DisplayName = "R16: a follower goes on from chunks its session holds that the evidence released, and refuses a session that holds none of what the evidence keeps")]
    public async Task AFollowerPlacesChunksTheEvidenceReleased()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        using var wholeDirectory = new TemporaryDirectory();
        using var heldDirectory = new TemporaryDirectory();
        using var laggingDirectory = new TemporaryDirectory();
        using var freshDirectory = new TemporaryDirectory();
        _ = await RecordEvidence(evidenceDirectory.Path, ordinals: [1, 2, 3, 4, 5, 6], bursts: [2, 4]);
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path));
        SessionManifestV1 source = evidence.Current!;
        int chunks = ChunksOf(source);
        Dictionary<ulong, ulong> indexOf = IndicesOf(evidence, wholeDirectory.Path, source);

        // Two sessions that released nothing: one holds the capture's first two chunks, the other its first alone.
        SessionStore held = Open(heldDirectory.Path, source);
        _ = LiveSessionFollower.Open(evidence, held).CatchUp(maximumChunks: 2);
        SessionStore lagging = Open(laggingDirectory.Path, source);
        _ = LiveSessionFollower.Open(evidence, lagging).CatchUp(maximumChunks: 1);

        // The evidence releases its first chunk though both still hold it. The session that holds the evidence's oldest
        // chunk too is placed by it, and goes on from its own chunks, the released one counted once.
        SessionManifestV1 released = ReleaseEvidence(evidenceDirectory.Path, 1);
        Assert.Equal(2, LiveSessionFollower.Followed(released, held.Current));
        SessionStore reopened = Open(heldDirectory.Path, source);
        FollowStep step = LiveSessionFollower.Open(evidence, reopened).CatchUp();
        Assert.True(step.Finished);
        Assert.Equal((chunks, chunks, chunks - 2, 6L), (step.DerivedChunks, step.EvidenceChunks, step.MirroredChunks, step.DerivedRecords));
        Assert.All(Rows(reopened), row => Assert.Equal(indexOf[row.RawRecordOrdinal], row.JournalRecordIndex));

        // The session that holds none of what the evidence keeps cannot be placed, and one that mirrored nothing would
        // begin after records it never held.
        Assert.Contains("released every chunk this session holds", Assert.Throws<InvalidDataException>(
            () => LiveSessionFollower.Open(evidence, Open(laggingDirectory.Path, source))).Message, StringComparison.Ordinal);
        LiveSessionFollower fresh = LiveSessionFollower.Open(evidence, Open(freshDirectory.Path, source));
        Assert.Contains("released the capture's first 1 chunk(s) before this session mirrored any", Assert.Throws<InvalidDataException>(
            () => fresh.CatchUp()).Message, StringComparison.Ordinal);
        Assert.Null(SessionStore.OpenExisting(LocalOwnedDirectory.Open(freshDirectory.Path)).Current);
    }

    [Fact(DisplayName = "R16: evidence that released chunks without stating what it gave up in all, or stating less than its chunks show, is not followed")]
    public async Task EvidenceThatReleasedChunksUnstatedIsNotFollowed()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        using var heldDirectory = new TemporaryDirectory();
        _ = await RecordEvidence(evidenceDirectory.Path, ordinals: [1, 2, 3, 4, 5, 6], bursts: [2, 4]);
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path));
        SessionManifestV1 source = evidence.Current!;
        int chunks = ChunksOf(source);
        _ = LiveSessionFollower.Open(evidence, Open(heldDirectory.Path, source)).CatchUp(maximumChunks: 3);
        ReleasedRecording stated = ReleaseEvidence(evidenceDirectory.Path, 2).Retention!.Recording!;
        Assert.Equal(2, stated.Chunks);

        // As stated, the session's third chunk is the evidence's oldest, and the session is placed by it.
        FollowStep placed = LiveSessionFollower.Open(evidence, Open(heldDirectory.Path, source)).CatchUp(maximumChunks: 1);
        Assert.Equal((Math.Min(4, chunks), chunks), (placed.DerivedChunks, placed.EvidenceChunks));

        // Stating one chunk where the session's chunks show two were given up places the evidence's oldest where it is not.
        Restate(evidenceDirectory.Path, stated with { Chunks = 1 });
        Assert.Contains("nor any later chunk of it", Assert.Throws<InvalidDataException>(() => LiveSessionFollower.Open(
            SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path)), Open(heldDirectory.Path, source))).Message,
            StringComparison.Ordinal);

        // A release published before chunk releases stated their counts says nothing of where the evidence's chunks lie.
        Restate(evidenceDirectory.Path, recording: null);
        Assert.Contains("released its oldest records without stating how many", Assert.Throws<InvalidDataException>(
            () => LiveSessionFollower.Open(SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path)),
                Open(heldDirectory.Path, source))).Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§3.1: a crashed follow is finished from evidence that released chunks its session gave up, counting them as followed")]
    public async Task ACrashedFollowAcrossTheEvidencesReleaseIsFinished()
    {
        using var root = new TemporaryDirectory();
        string evidencePath = Path.Combine(root.Path, "capture");
        Directory.CreateDirectory(evidencePath);
        _ = await RecordEvidence(evidencePath, ordinals: [1, 2, 3, 4, 5, 6], bursts: [2, 4]);
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidencePath));
        SessionManifestV1 source = evidence.Current!;
        int published = ChunksOf(source);

        // The viewer followed two chunks, its session released the older, the evidence released it too, and it crashed.
        string session = Path.Combine(root.Path, "explore-1");
        Directory.CreateDirectory(session);
        SessionStore crashed = Open(session, source);
        _ = LiveSessionFollower.Open(evidence, crashed).CatchUp(maximumChunks: 2);
        ReleaseOldest(crashed);
        _ = ReleaseEvidence(evidencePath, 1);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        LiveFollowTicket.For(source.SessionId, evidencePath, session, now, now.AddSeconds(30)).Hold().Dispose();

        // The card counts the capture's chunks whole and both the session followed, though neither session holds the first.
        LiveFollowTicket ticket = Assert.Single(LiveFollowTicket.FindInterrupted(root.Path));
        InterruptedFollow found = InterruptedFollow.Assess(ticket, now);
        Assert.Equal(InterruptedFollowState.Finishable, found.State);
        Assert.Equal((2, published, published - 2), (found.SessionChunks, found.EvidenceChunks, found.MissingChunks));

        InterruptedFollowResult finished = InterruptedFollow.Finish(ticket, nowUtc: now);
        Assert.True(finished.Completed);
        Assert.Equal((published, published, 6L), (finished.Step.DerivedChunks, finished.Step.EvidenceChunks, finished.Step.DerivedRecords));
        Assert.Equal(InterruptedFollowState.Complete, InterruptedFollow.Assess(ticket, now).State);
    }

    /// <summary>What a follow that releases nothing derives: each record's journal index, by its ordinal.</summary>
    private static Dictionary<ulong, ulong> IndicesOf(SessionStore evidence, string directory, SessionManifestV1 source)
    {
        SessionStore whole = Open(directory, source);
        Assert.True(LiveSessionFollower.Open(evidence, whole).CatchUp().Finished);
        return Rows(whole).ToDictionary(row => row.RawRecordOrdinal, row => row.JournalRecordIndex!.Value);
    }

    /// <summary>
    /// Releases the evidence's oldest chunks whole, as the broker releases those its follow gave up, with the records they
    /// held, which the evidence's own units count. Returns the generation that released them.
    /// </summary>
    private static SessionManifestV1 ReleaseEvidence(string directory, int count)
    {
        SessionManifestV1 recorded = SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory)).Current!;
        SessionStore writer = SessionStore.Open(LocalOwnedDirectory.Open(directory), recorded.SessionId, recorded.SourceIdentity);
        (IReadOnlyList<JournalReleaseUnit> units, bool chunked) = JournalRetention.Units(writer, writer.Current!);
        Assert.True(chunked);
        JournalReleaseUnit[] released = [.. units.Take(count)];
        return writer.ReleaseJournalChunks(
            [.. released.Select(unit => unit.Journal)],
            released.Sum(unit => unit.Records),
            "its follow gave these up",
            DateTimeOffset.UtcNow).Manifest;
    }

    /// <summary>
    /// Rewrites the evidence's current generation to state <paramref name="recording"/> for its chunk release, digested
    /// afresh and named by the pointer: a release as one published before the statement existed, or one changed by hand.
    /// </summary>
    private static void Restate(string directory, ReleasedRecording? recording)
    {
        SessionPointerV1 pointer = System.Text.Json.JsonSerializer.Deserialize<SessionPointerV1>(
            File.ReadAllText(Path.Combine(directory, SessionPointerV1.FileName)), SessionManifestV1.Json)!;
        SessionManifestV1 manifest = System.Text.Json.JsonSerializer.Deserialize<SessionManifestV1>(
            File.ReadAllText(Path.Combine(directory, pointer.ManifestName)), SessionManifestV1.Json)!;
        SessionManifestV1 restated = SessionManifestV1.Create(
            manifest.Generation,
            manifest.SessionId,
            manifest.CommittedUtc,
            manifest.SourceIdentity,
            manifest.PreviousGeneration,
            manifest.Boundary,
            manifest.Dependencies,
            manifest.Retention! with { Recording = recording },
            manifest.EarlierReleases);
        File.WriteAllText(Path.Combine(directory, pointer.ManifestName), System.Text.Json.JsonSerializer.Serialize(restated, SessionManifestV1.Json));
        File.WriteAllText(
            Path.Combine(directory, SessionPointerV1.FileName),
            System.Text.Json.JsonSerializer.Serialize(SessionPointerV1.For(restated), SessionManifestV1.Json));
    }
}
