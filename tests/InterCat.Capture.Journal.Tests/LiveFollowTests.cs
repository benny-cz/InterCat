using InterCat.Storage;
using Xunit;
using static InterCat.Capture.Journal.Tests.EvidenceRecordings;

namespace InterCat.Capture.Journal.Tests;

/// <summary>
/// The follow of a capture still recording rides out a moment when a file cannot be read or written - another program
/// holding one, as a scanner can a file just written - rather than end the capture over it (§9, ADR-027). A follower moves
/// on only past a chunk it published, so the pass is tried again as it was; what was mirrored stays mirrored, once.
/// </summary>
public sealed class LiveFollowTests
{
    private static readonly TimeSpan[] Brief = [TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5)];

    [Fact(DisplayName = "§9: a follow rides out a chunk another program holds for a moment, mirroring every chunk once")]
    public async Task AFollowRidesOutAHeldChunk()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        using var derivedDirectory = new TemporaryDirectory();
        (SessionStore evidence, SessionStore derived, StoreDependency[] chunks) = await Recorded(evidenceDirectory, derivedDirectory);

        // Another program holds the second chunk as the follow reaches it, and lets go while the follow pauses.
        FileStream? held = Hold(evidenceDirectory.Path, chunks[1]);
        var failures = new List<Exception>();
        var follow = new LiveFollow(token => LiveSessionFollower.Open(evidence, derived, cancellationToken: token), Brief,
            exception =>
            {
                failures.Add(exception);
                held?.Dispose();
                held = null;
            });
        FollowStep step = follow.CatchUp();

        Assert.IsAssignableFrom<IOException>(Assert.Single(failures));
        Assert.True(step.Finished);
        Assert.Equal((chunks.Length, 4L), (step.DerivedChunks, step.DerivedRecords));
        Assert.Equal(4, JournalRederivation.Verify(derived).ObservationRows);
    }

    [Fact(DisplayName = "§9: a failure that outlasts every pause ends the follow, saying so, and what was mirrored stays to go on from")]
    public async Task AFailureThatOutlastsThePausesEndsTheFollow()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        using var derivedDirectory = new TemporaryDirectory();
        (SessionStore evidence, SessionStore derived, StoreDependency[] chunks) = await Recorded(evidenceDirectory, derivedDirectory);
        int failures = 0;
        var follow = new LiveFollow(token => LiveSessionFollower.Open(evidence, derived, cancellationToken: token),
            [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)], _ => failures++);
        using (Hold(evidenceDirectory.Path, chunks[1]))
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            IOException failed = Assert.Throws<IOException>(() => follow.CatchUp());
            Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(200),
                "The follow pauses before each try.");
            Assert.EndsWith(". It failed again each time the follow tried it, 2 more times over 0.2 s.", failed.Message,
                StringComparison.Ordinal);
            Assert.IsAssignableFrom<IOException>(failed.InnerException);
            Assert.StartsWith(failed.InnerException!.Message.TrimEnd('.'), failed.Message, StringComparison.Ordinal);
        }

        // The first chunk was published before the second was reached, and the same follow goes on from it.
        Assert.Equal(2, failures);
        Assert.Equal(1, LiveSessionFollower.Progress(derived.Current).Chunks);
        FollowStep step = follow.CatchUp();
        Assert.Equal((chunks.Length - 1, chunks.Length, 4L), (step.MirroredChunks, step.DerivedChunks, step.DerivedRecords));
        Assert.Equal(4, JournalRederivation.Verify(derived).ObservationRows);
    }

    [Fact(DisplayName = "§9: a follow stopped while it pauses stops at once, not once the pause is over")]
    public async Task AFollowStoppedWhileItPausesStopsAtOnce()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        using var derivedDirectory = new TemporaryDirectory();
        (SessionStore evidence, SessionStore derived, StoreDependency[] chunks) = await Recorded(evidenceDirectory, derivedDirectory);
        using var stop = new CancellationTokenSource();
        var follow = new LiveFollow(token => LiveSessionFollower.Open(evidence, derived, cancellationToken: token),
            [TimeSpan.FromSeconds(30)], _ => stop.Cancel());
        using (Hold(evidenceDirectory.Path, chunks[1]))
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            _ = Assert.Throws<OperationCanceledException>(() => follow.CatchUp(cancellationToken: stop.Token));
            Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10),
                "A stop does not wait out the pause.");
        }

        // What was mirrored before it stopped stays.
        Assert.Equal(1, LiveSessionFollower.Progress(derived.Current).Chunks);
    }

    [Fact(DisplayName = "§9: a failure that is not about reading or writing a file ends the follow at once, untried again")]
    public async Task AFailureOfTheEvidenceItselfIsNotTriedAgain()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        using var otherDirectory = new TemporaryDirectory();
        using var derivedDirectory = new TemporaryDirectory();
        (SessionStore evidence, SessionStore derived, _) = await Recorded(evidenceDirectory, derivedDirectory);
        _ = LiveSessionFollower.Open(evidence, derived).CatchUp(maximumChunks: 1);

        // Another recording's evidence is refused as such, not ridden out as a file held for a moment.
        _ = await RecordEvidence(otherDirectory.Path, ordinals: [1, 2, 3, 4]);
        SessionStore other = SessionStore.OpenExisting(LocalOwnedDirectory.Open(otherDirectory.Path));
        int failures = 0;
        var follow = new LiveFollow(token => LiveSessionFollower.Open(other, derived, cancellationToken: token), Brief,
            _ => failures++);
        _ = Assert.Throws<InvalidDataException>(() => follow.CatchUp());
        Assert.Equal(0, failures);
    }

    /// <summary>A finalized recording of four records in two or three chunks, and an empty session to follow it into.</summary>
    private static async Task<(SessionStore Evidence, SessionStore Derived, StoreDependency[] Chunks)> Recorded(
        TemporaryDirectory evidenceDirectory, TemporaryDirectory derivedDirectory)
    {
        _ = await RecordEvidence(evidenceDirectory.Path, ordinals: [1, 2, 3, 4]);
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path));
        SessionManifestV1 source = evidence.Current!;
        StoreDependency[] chunks = [.. source.Dependencies
            .Where(dependency => dependency.Kind == StoreDependencyKind.Journal)
            .OrderBy(dependency => dependency.Name, StringComparer.Ordinal)];
        Assert.InRange(chunks.Length, 2, 3);
        return (evidence, SessionStore.Open(LocalOwnedDirectory.Open(derivedDirectory.Path), source.SessionId, source.SourceIdentity),
            chunks);
    }

    /// <summary>Holds a file as another program can, so no one else opens it until the hold is let go.</summary>
    private static FileStream Hold(string directory, StoreDependency file) =>
        new(Path.Combine(directory, file.Name), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
}
