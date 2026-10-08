using System.Text.Json;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// What a recording's chunk releases gave up in all (ADR-048): each states the chunks and records it and every chunk release
/// before it gave up, so a reader places the chunks a session still holds among the recording's without the ones that are
/// gone - and states nothing once a release before it gave up records it did not count.
/// </summary>
public sealed partial class EvidenceRetentionTests
{
    [Fact(DisplayName = "I15: each chunk release states the chunks and records its recording gave up in all, which every later generation carries")]
    public void AChunkReleaseStatesWhatItsRecordingGaveUpInAll()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult first = Publish(session.Store, 4, batchCapacity: 2);
        DerivedGenerationResult second = Publish(session.Store, 5, batchCapacity: 2);
        DerivedGenerationResult third = Publish(session.Store, 6, batchCapacity: 2);
        _ = Publish(session.Store, 7, batchCapacity: 2);

        // The first release states its own chunk and records; the next adds its own to them.
        RetentionRecord once = session.Store.ReleaseJournalChunks(
            [first.JournalName], 4, "older than the retained window", Committed, Committed).Manifest.Retention!;
        Assert.Equal(new ReleasedRecording(1, 4), once.Recording);
        SessionManifestV1 twice = session.Store.ReleaseJournalChunks(
            [second.JournalName, third.JournalName], 11, "older than the retained window", Committed, Committed).Manifest;
        RetentionRecord stated = twice.Retention!;
        Assert.Equal(new ReleasedRecording(3, 15), stated.Recording);

        // A later generation carries the statement, which the manifest's file and digest hold.
        _ = Publish(session.Store, 1);
        SessionManifestV1 reopened = session.Reopen().Current!;
        Assert.Null(reopened.VerifyDigest());
        Assert.Equal(new ReleasedRecording(3, 15), reopened.LatestRelease(RetentionExtentKind.JournalPrefix)!.Record.Recording);
        Assert.Contains("\"recording\": {", File.ReadAllText(Path.Combine(session.Path, SessionManifestV1.FileNameFor(reopened.Generation))),
            StringComparison.Ordinal);
        Assert.NotEqual(twice.Digest, Restated(twice, stated with { Recording = new(3, 16) }).Digest);
        Assert.Equal(twice.Digest, Restated(twice, stated).Digest);

        // A record that states none is the shape it always was, in its file and its digest.
        Assert.DoesNotContain("recording", JsonSerializer.Serialize(stated with { Recording = null }, SessionManifestV1.Json),
            StringComparison.Ordinal);
        Assert.Equal((stated with { Recording = null }).CanonicalForm + "|recording|3|15", stated.CanonicalForm);

        // Only a chunk release states it: at least one chunk, no fewer than no records, and no fewer than it gave up itself.
        Assert.Equal(
            "Only a release of a recording's oldest chunks states the chunks the recording gave up in all.",
            (stated with { Kind = RetentionExtentKind.Content }).Validate());
        Assert.Contains("at least one chunk", (stated with { Recording = new(0, 15) }).Validate(), StringComparison.Ordinal);
        Assert.Contains("at least one chunk", (stated with { Recording = new(3, -1) }).Validate(), StringComparison.Ordinal);
        Assert.Equal(
            "A chunk release states fewer records given up in all than it gave up itself.",
            (stated with { Recording = new(3, 10) }).Validate());
        Assert.Null((stated with { Recording = new(1, 11) }).Validate());
        Assert.Throws<ArgumentException>(() => Restated(twice, stated with { Recording = new(3, 10) }));
    }

    [Fact(DisplayName = "I15: a chunk release states nothing in all once an interval release or a journal rewrite gave up records before it")]
    public void AChunkReleaseAfterAnUncountedReleaseStatesNothingInAll()
    {
        // A single journal rewritten, then recorded on in chunks: the rewrite gave up part of a journal, no whole chunk, so
        // no count of the recording's chunks follows from it, and none from a chunk release after it.
        using var rewritten = new TemporarySession();
        _ = Publish(rewritten.Store, 6, batchCapacity: 2);
        RetentionOutcome rewrite = JournalRetention.Release(rewritten.Store, 2, "the first batch aged out", Committed, Committed);
        Assert.Null(rewrite.Manifest.Retention!.Recording);
        string retained = Assert.Single(rewrite.Manifest.Dependencies, dependency => dependency.Kind == StoreDependencyKind.Journal).Name;
        _ = Publish(rewritten.Store, 3);
        _ = Publish(rewritten.Store, 3);
        RetentionRecord after = rewritten.Store.ReleaseJournalChunks(
            [retained], 4, "older than the retained window", Committed, Committed).Manifest.Retention!;
        Assert.Equal((RetentionExtentKind.JournalPrefix, 4L), (after.Kind, after.ReleasedRecords));
        Assert.Null(after.Recording);

        // Nor does a rewrite state a count of its own.
        long generation = rewritten.Store.Current!.Generation;
        using (StoreStagingFile staged = rewritten.Store.Stage(
            SegmentFormatV1.JournalFileName(rewritten.Store.NextGeneration), StoreDependencyKind.Journal))
        {
            Assert.Contains("states no chunks given up in all", Assert.Throws<ArgumentException>(() => rewritten.Store.ReleaseJournalPrefix(
                staged, rewritten.Store.Current!.Boundary, rewrite.Manifest.Retention! with { Recording = new(1, 2) }, Committed)).Message,
                StringComparison.Ordinal);
        }

        Assert.Equal(generation, rewritten.Store.Current!.Generation);

        // A recording that released an interval gave its chunks up uncounted too.
        using var interval = new TemporarySession();
        DerivedGenerationResult first = Publish(interval.Store, 4, batchCapacity: 2);
        DerivedGenerationResult second = Publish(interval.Store, 5, batchCapacity: 2);
        _ = Publish(interval.Store, 6, batchCapacity: 2);
        SessionManifestV1 recorded = interval.Store.Current!;
        string[] derived =
        [
            .. recorded.Dependencies
                .Where(dependency => dependency.Kind is StoreDependencyKind.Segment or StoreDependencyKind.Dictionary
                    && SessionSegments.PublishingGeneration(dependency.Name) == first.Manifest.Generation)
                .Select(dependency => dependency.Name),
        ];
        _ = interval.Store.CommitIntervalRelease(
            [], derived, new() { Chunks = [first.JournalName], Records = 4 }, new ReleasedInterval(450, 4, 0),
            "older than the retained window", recorded.Generation, Committed, nowUtc: Committed);
        RetentionRecord chunk = interval.Store.ReleaseJournalChunks(
            [second.JournalName], 5, "older than the retained window", Committed, Committed).Manifest.Retention!;
        Assert.Equal((RetentionExtentKind.JournalPrefix, 5L), (chunk.Kind, chunk.ReleasedRecords));
        Assert.Null(chunk.Recording);
    }

    /// <summary>The same generation restated with another retention record, digested afresh.</summary>
    private static SessionManifestV1 Restated(SessionManifestV1 manifest, RetentionRecord retention) =>
        SessionManifestV1.Create(
            manifest.Generation,
            manifest.SessionId,
            manifest.CommittedUtc,
            manifest.SourceIdentity,
            manifest.PreviousGeneration,
            manifest.Boundary,
            manifest.Dependencies,
            retention,
            manifest.EarlierReleases);
}
