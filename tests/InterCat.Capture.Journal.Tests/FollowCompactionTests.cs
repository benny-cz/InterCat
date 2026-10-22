using InterCat.Storage;
using Xunit;

namespace InterCat.Capture.Journal.Tests;

/// <summary>
/// §20.1's live compaction in a follow, as a recording's: a compaction that fails - as one writing its coalesced copy on
/// a nearly full disk does first - leaves the follow's session published and whole, says why with every step after, and
/// never ends the follow, which mirrors every chunk on and compacts nothing more.
/// </summary>
public sealed class FollowCompactionTests
{
    [Fact(DisplayName = "§20.1: a follow whose compaction fails says why and mirrors every chunk on, compacting nothing more")]
    public async Task AFailedCompactionNeverEndsAFollow()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        using var derivedDirectory = new TemporaryDirectory();
        _ = await EvidenceRecordings.RecordEvidence(evidenceDirectory.Path, ordinals: [.. Enumerable.Range(1, 12)], bursts: [2, 4, 6, 8, 10]);
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path));
        SessionManifestV1 source = evidence.Current!;
        int chunks = LiveSessionFollower.Progress(source).Chunks;
        Assert.True(chunks >= 6, $"The recording published {chunks} chunks; six bursts publish six.");

        SessionStore derived = SessionStore.Open(LocalOwnedDirectory.Open(derivedDirectory.Path), source.SessionId, source.SourceIdentity);
        LiveSessionFollower follower = LiveSessionFollower.Open(
            evidence, derived, compaction: new CompactionOptions { SmallUnitsBeforeCompaction = 2 });
        Assert.Null(follower.CatchUp(maximumChunks: 1).CompactionFailure);

        // Another program holds the first chunk's segment, so the compaction the second chunk makes due cannot read it.
        derived.ReleaseSegmentReaders();
        string segment = Path.Combine(derivedDirectory.Path, SessionSegments.Names(derived.Current!)[0]);
        FollowStep failed;
        using (new FileStream(segment, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            failed = follower.CatchUp(maximumChunks: 1);
        }

        Assert.Equal((1, 2, 0, 2L), (failed.MirroredChunks, failed.DerivedChunks, failed.Compactions, failed.DerivedGeneration));
        Assert.False(string.IsNullOrWhiteSpace(failed.CompactionFailure));

        // The follow goes on to the capture's end, each chunk in a generation of its own, the compactions due along the way
        // and at its end left undone; the session is whole, and replays as it was published.
        FollowStep last = follower.CatchUp();
        Assert.True(last.Finished);
        Assert.Equal((chunks, 0, chunks, failed.CompactionFailure),
            (last.DerivedChunks, last.Compactions, (int)last.DerivedGeneration!.Value, last.CompactionFailure));
        Assert.Equal(12L, last.DerivedRecords);
        Assert.Equal(12, JournalRederivation.Verify(derived).ObservationRows);
    }
}
