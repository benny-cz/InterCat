using System.Diagnostics;
using InterCat.Analysis;
using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Capture.Journal.Tests.EvidenceRecordings;

namespace InterCat.Capture.Journal.Tests;

/// <summary>
/// An evidence recording that releases the chunks its follow gave up (ADR-048 decision 5): whole, by its own writer between
/// two publications, never the newest, each release stating what the recording gave up in all - so what its evidence
/// holds, not everything it recorded, counts against its journal allowance, and the follow goes on numbering every record as
/// the capture did.
/// </summary>
public sealed class EvidenceReleaseRecordingTests
{
    [Fact(DisplayName = "R16: an evidence recording releases the chunks its follow gave up, never its newest, and the follow goes on numbering every record as the capture did")]
    public async Task ARecordingReleasesWhatItsFollowGaveUp()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        using var derivedDirectory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(evidenceDirectory.Path), Guid.NewGuid(), "release-tests");
        var host = new ScriptedHost();
        var script = new Script(host, store);
        long given = 0;
        LiveSessionFollower? follower = null;
        SessionStore? derived = null;

        script.Burst(2);
        script.Published(1);
        script.Burst(2);
        script.Published(2);
        script.Burst(2);
        script.Published(3);

        // The follow mirrors what was published, its session gives up all but its newest chunk, and the recording is told.
        host.Run(() =>
        {
            SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path));
            SessionManifestV1 source = evidence.Current!;
            derived = SessionStore.Open(LocalOwnedDirectory.Open(derivedDirectory.Path), source.SessionId, source.SourceIdentity);
            follower = LiveSessionFollower.Open(evidence, derived);
            _ = follower.CatchUp();
            IntervalReleasePreview preview = IntervalRelease.Preview(derived, long.MaxValue);
            _ = IntervalRelease.Release(derived, preview.MostReleasingNanoseconds!.Value, "rolling", DateTimeOffset.UtcNow);
            Interlocked.Exchange(ref given, follower.CatchUp().ReleasedChunks);
        });

        // The next publication releases them; the follow goes on from what the evidence keeps. A later publication, the
        // follow having given up nothing more, releases nothing more.
        script.Burst(2);
        host.PauseUntil(() => store.Current?.LatestRelease(RetentionExtentKind.JournalPrefix) is not null, TimeSpan.FromSeconds(30));
        host.Run(() => _ = follower!.CatchUp());
        long before = 0;
        host.Run(() => before = script.Count());
        script.Burst(2);
        host.PauseUntil(() => script.Count() > before, TimeSpan.FromSeconds(30));
        script.Burst(2);

        LiveCaptureResult result = await LiveRecorder.RecordAsync(
            Plan(withFields: true), host, store, token => host.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(90), token),
            DateTimeOffset.UtcNow,
            publishEvery: TimeSpan.FromMilliseconds(100),
            releasableChunks: () => Interlocked.Read(ref given));

        // It released exactly what its follow gave up, stating it in all, and kept the rest.
        Assert.True(given >= 2, $"The follow gave up {given} chunks.");
        Assert.Equal((given, (string?)null), ((long)result.ReleasedChunks, result.ReleaseProblem));
        SessionManifestV1 final = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path)).Current!;
        RetentionRecord release = final.LatestRelease(RetentionExtentKind.JournalPrefix)!.Record;
        Assert.Equal((LiveRecorder.FollowedReason, given), (release.Reason, release.Recording!.Chunks));
        Assert.Equal(result.Publications, (int)given + final.Dependencies.Count(dependency => dependency.Kind == StoreDependencyKind.Journal));

        // The same follower finishes; a fresh one takes the records the evidence gave up from its statement, and both
        // number every record as the capture did.
        FollowStep finished = follower!.CatchUp();
        Assert.True(finished.Finished);
        Assert.Equal((result.Publications, result.Publications, 12L), (finished.DerivedChunks, finished.EvidenceChunks, finished.DerivedRecords));
        SessionStore reopened = SessionStore.Open(LocalOwnedDirectory.Open(derivedDirectory.Path), final.SessionId, final.SourceIdentity);
        FollowStep resumed = LiveSessionFollower.Open(SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path)), reopened)
            .CatchUp();
        Assert.Equal((true, 12L, given), (resumed.Finished, resumed.DerivedRecords, (long)resumed.ReleasedChunks));
        Assert.All(Rows(reopened), row => Assert.Equal(row.RawRecordOrdinal - 1, row.JournalRecordIndex));
    }

    [Fact(DisplayName = "R16: what an evidence recording holds, not everything it recorded, counts against its journal allowance once its follow gives chunks up")]
    public async Task TheAllowanceBoundsWhatTheEvidenceHolds()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "release-quota-tests");
        var host = new ScriptedHost();
        var script = new Script(host, store);

        // Thirty bursts of 1,000 records, each published before the next: some three times the 1 MiB allowance in all - its
        // own test finds 10,000 such records overrun it - and a small part of it in any two chunks.
        for (int chunk = 1; chunk <= 30; chunk++)
        {
            script.Burst(1_000);
            script.Published(chunk);
        }

        LiveCaptureResult result = await LiveRecorder.RecordAsync(
            Plan(), host, store, token => host.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(90), token), DateTimeOffset.UtcNow,
            publishEvery: TimeSpan.FromMilliseconds(50),
            maximumJournalBytes: 1_048_576,
            releasableChunks: () => long.MaxValue,
            heldChunkLimit: 3);

        // A follow that keeps up never lets the evidence hold as many chunks as it may.
        Assert.False(result.FollowStalled);
        Assert.False(result.JournalQuotaReached);
        Assert.Equal(30_000, result.JournaledRecords);
        Assert.True(result.ReleasedChunks >= 29, $"It released {result.ReleasedChunks} chunks.");

        // What the evidence holds is within the allowance; what it recorded in all, at its records' size, is well past it.
        SessionManifestV1 final = store.Current!;
        (IReadOnlyList<JournalReleaseUnit> units, _) = JournalRetention.Units(store, final);
        long heldBytes = final.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Journal)
            .Sum(dependency => dependency.LengthBytes);
        Assert.InRange(heldBytes, 1, 1_048_576);
        double perRecord = (double)Newest(store, oldest: true).LengthBytes / units[0].Records;
        Assert.True(perRecord * 30_000 > 2 * 1_048_576, $"A record takes {perRecord:F0} journal bytes.");
        Assert.Equal(30_000, final.LatestRelease(RetentionExtentKind.JournalPrefix)!.Record.Recording!.Records
            + units.Sum(unit => unit.Records));
    }

    [Fact(DisplayName = "R16: an evidence recording whose follow stopped giving chunks up stops once it holds as many as it may, publishing what it recorded")]
    public async Task AStalledFollowStopsTheRecording()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "release-stall-tests");
        var host = new ScriptedHost();
        var script = new Script(host, store);
        for (int chunk = 1; chunk <= 3; chunk++)
        {
            script.Burst(2);
            script.Published(chunk);
        }

        script.Burst(2);

        // A stop takes a moment, as a broker's does: the capture records on a little after it was asked to stop.
        LiveCaptureResult result = await LiveRecorder.RecordAsync(
            Plan(), host, store,
            async token =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(90), token);
                }
                catch (OperationCanceledException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(300), CancellationToken.None);
                }
            },
            DateTimeOffset.UtcNow,
            publishEvery: TimeSpan.FromMilliseconds(50),
            releasableChunks: () => 0,
            heldChunkLimit: 3);

        // It stopped once a publication left it holding three: the chunk being written became its last, nothing more.
        Assert.True(result.FollowStalled);
        Assert.Equal((0, (string?)null, 4), (result.ReleasedChunks, result.ReleaseProblem, result.Publications));
        Assert.True(result.JournaledRecords >= 6, $"It journaled {result.JournaledRecords} records.");
        Assert.NotNull(CaptureFinalizationV1.Read(store.Root, store.Current!));
    }

    [Fact(DisplayName = "R16: only an evidence-only recording that publishes chunks releases those its follow gave up")]
    public async Task OnlyAPublishingEvidenceRecordingReleases()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "release-refusal-tests");
        ArgumentException once = await Assert.ThrowsAsync<ArgumentException>(() => LiveRecorder.RecordAsync(
            Plan(), new ScriptedHost(), store, _ => Task.CompletedTask, DateTimeOffset.UtcNow,
            releasableChunks: () => 1));
        Assert.Equal("releasableChunks", once.ParamName);
        Assert.Equal("heldChunkLimit", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LiveRecorder.RecordAsync(
            Plan(), new ScriptedHost(), store, _ => Task.CompletedTask, DateTimeOffset.UtcNow,
            publishEvery: TimeSpan.FromMilliseconds(50), heldChunkLimit: 3))).ParamName);
        Assert.Equal("heldChunkLimit", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LiveRecorder.RecordAsync(
            Plan(), new ScriptedHost(), store, _ => Task.CompletedTask, DateTimeOffset.UtcNow,
            publishEvery: TimeSpan.FromMilliseconds(50), releasableChunks: () => 0, heldChunkLimit: 1))).ParamName);
        Assert.Null(store.Current);
    }

    /// <summary>The newest journal chunk the evidence names, or its oldest.</summary>
    private static StoreDependency Newest(SessionStore store, bool oldest = false)
    {
        StoreDependency[] chunks =
        [
            .. store.Current!.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Journal)
                .OrderBy(dependency => dependency.Name, StringComparer.OrdinalIgnoreCase),
        ];
        return oldest ? chunks[0] : chunks[^1];
    }

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

    /// <summary>A scripted capture's records, numbered from 1, and the waits that end each burst in a chunk of its own.</summary>
    private sealed class Script(ScriptedHost host, SessionStore store)
    {
        private readonly long start = Stopwatch.GetTimestamp();
        private int ordinal;

        public void Burst(int records)
        {
            for (int index = 0; index < records; index++)
            {
                int next = ++ordinal;
                var admitted = new AdmittedEvent
                {
                    SourceIndex = 0,
                    EventId = 10,
                    Version = 0,
                    TimestampQpc = start + next,
                    TimestampUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                    RecordOrdinal = next,
                };
                admitted.SetSlot(0, 100 * next);
                admitted.SetSlot(1, 0x7000 + next);
                host.Admit(admitted);
            }
        }

        /// <summary>
        /// Waits until the recording has published <paramref name="chunks"/> chunks in all: those it holds and those its
        /// releases state they gave up.
        /// </summary>
        public void Published(int chunks) => host.PauseUntil(() => Count() >= chunks, TimeSpan.FromSeconds(30));

        /// <summary>How many chunks the recording has published so far, those it released among them.</summary>
        public long Count() => store.Current is { } current
            ? (current.LatestRelease(RetentionExtentKind.JournalPrefix)?.Record.Recording?.Chunks ?? 0)
                + current.Dependencies.Count(dependency => dependency.Kind == StoreDependencyKind.Journal)
            : 0;
    }
}
