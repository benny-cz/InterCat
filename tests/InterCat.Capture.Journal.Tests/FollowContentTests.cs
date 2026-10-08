using System.Diagnostics;
using System.Text;
using InterCat.Analysis;
using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Capture.Journal.Tests;

/// <summary>
/// A follow of evidence that kept content (content-v1 §2): the content a capture kept of a chunk's records is mirrored with
/// the chunk, byte for byte, under the generation that mirrors it, so it stays paired with its journal - found by the records
/// it belongs to, and released with them.
/// </summary>
public sealed class FollowContentTests
{
    [Fact(DisplayName = "I21: a follow mirrors the content kept beside each evidence chunk, byte for byte, beside the chunk it mirrors")]
    public async Task AFollowMirrorsEachChunksContent()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        using var derivedDirectory = new TemporaryDirectory();
        SessionStore evidence = await RecordContentEvidence(evidenceDirectory.Path, [["hello", "0123456789AB"], ["abc"], ["the last"]]);
        SessionManifestV1 source = evidence.Current!;
        StoreDependency[] kept = Of(source, StoreDependencyKind.Content);
        Assert.True(kept.Length >= 3, $"The recording kept content beside {kept.Length} chunks; three bursts keep it beside three.");

        SessionStore derived = Open(derivedDirectory.Path, source);
        FollowStep step = LiveSessionFollower.Open(evidence, derived).CatchUp();
        Assert.True(step.Finished);
        SessionManifestV1 mirror = derived.Current!;

        // Each chunk's content is the evidence's, byte for byte, named for the generation that mirrored its chunk.
        Dictionary<string, StoreDependency> mirroredChunk = Of(mirror, StoreDependencyKind.Journal)
            .ToDictionary(chunk => chunk.Digest, StringComparer.Ordinal);
        Assert.Equal(kept.Length, Of(mirror, StoreDependencyKind.Content).Length);
        foreach (StoreDependency content in kept)
        {
            StoreDependency chunk = Assert.Single(Of(source, StoreDependencyKind.Journal),
                candidate => SegmentFormatV1.GenerationOfJournal(candidate.Name) == ContentChunkV1.GenerationOf(content.Name));
            long generation = SegmentFormatV1.GenerationOfJournal(mirroredChunk[chunk.Digest].Name)!.Value;
            StoreDependency copy = Assert.Single(mirror.Dependencies, dependency => dependency.Kind == StoreDependencyKind.Content
                && dependency.Name == ContentChunkV1.FileName(generation));
            Assert.Equal((content.LengthBytes, content.Digest), (copy.LengthBytes, copy.Digest));
        }

        // The records find their content as the capture kept it: whole, or cut to the record limit with its length.
        Dictionary<ulong, (ContentFragmentV1 Fragment, byte[] Bytes)> fragments = Fragments(derived);
        Assert.Equal(Rows(derived).Select(row => row.RawRecordOrdinal).Order(), fragments.Keys.Order());
        Assert.Equal("hello"u8.ToArray(), fragments[1].Bytes);
        Assert.Equal("01234567"u8.ToArray(), fragments[2].Bytes);
        Assert.Equal((ContentDispositionV1.TruncatedByRecordLimit, 12L),
            (fragments[2].Fragment.Disposition, fragments[2].Fragment.OriginalLength!.Value));
        Assert.Equal("the last"u8.ToArray(), fragments[4].Bytes);

        // Released with its chunk: an interval release gives up the oldest chunks' content, and what stays is still paired.
        IntervalReleasePreview preview = IntervalRelease.Preview(derived, long.MaxValue);
        Assert.True(preview.ReleasesAnything, IntervalRelease.Refusal(preview));
        _ = IntervalRelease.Release(derived, preview.MostReleasingNanoseconds!.Value, "older than the retained window", DateTimeOffset.UtcNow);
        SessionManifestV1 released = derived.Current!;
        StoreDependency[] remaining = Of(released, StoreDependencyKind.Content);
        Assert.True(remaining.Length < kept.Length, "The release gave up the content of the chunks it released.");
        Assert.All(remaining, content => Assert.Contains(Of(released, StoreDependencyKind.Journal),
            chunk => SegmentFormatV1.GenerationOfJournal(chunk.Name) == ContentChunkV1.GenerationOf(content.Name)));

        // The first record's row may stay as identity evidence later records rest on; its content went with its chunk.
        Dictionary<ulong, (ContentFragmentV1 Fragment, byte[] Bytes)> retained = Fragments(derived);
        Assert.DoesNotContain(1UL, retained.Keys);
        Assert.Subset(Rows(derived).Select(row => row.RawRecordOrdinal).ToHashSet(), retained.Keys.ToHashSet());
        Assert.Equal("the last"u8.ToArray(), retained[4].Bytes);
    }

    [Fact(DisplayName = "I21: a follow refuses content that changed after it was published, and evidence that released the content it kept")]
    public async Task AFollowRefusesContentItCannotMirrorWhole()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        using var changedDirectory = new TemporaryDirectory();
        using var releasedDirectory = new TemporaryDirectory();
        SessionStore evidence = await RecordContentEvidence(evidenceDirectory.Path, [["hello"], ["world"]]);
        SessionManifestV1 source = evidence.Current!;

        // Content whose bytes changed is refused as a changed chunk is, and nothing is mirrored in its generation.
        string path = Path.Combine(evidenceDirectory.Path, Of(source, StoreDependencyKind.Content)[0].Name);
        DateTime written = File.GetLastWriteTimeUtc(path);
        byte[] original = File.ReadAllBytes(path);
        byte[] changed = [.. original];
        changed[^1] ^= 0xFF;
        File.WriteAllBytes(path, changed);
        File.SetLastWriteTimeUtc(path, written);
        SessionStore derived = Open(changedDirectory.Path, source);
        Assert.Contains("does not match what generation", Assert.Throws<InvalidDataException>(
            () => LiveSessionFollower.Open(evidence, derived).CatchUp()).Message, StringComparison.Ordinal);
        Assert.Null(derived.Current);
        File.WriteAllBytes(path, original);
        File.SetLastWriteTimeUtc(path, written);

        // Evidence that released its content would leave its records' content gone without saying so: it is not followed.
        _ = ContentRetention.Release(evidence, "kept no longer", DateTimeOffset.UtcNow);
        Assert.Contains("released the content its capture kept", Assert.Throws<InvalidDataException>(
            () => LiveSessionFollower.Open(evidence, Open(releasedDirectory.Path, evidence.Current!)).CatchUp()).Message,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I21: mirrored content is staged once, beside a mirrored chunk, and only of that chunk's capture")]
    public async Task MirroredContentIsStagedOnlyBesideItsChunk()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        using var otherDirectory = new TemporaryDirectory();
        using var derivedDirectory = new TemporaryDirectory();
        SessionStore evidence = await RecordContentEvidence(evidenceDirectory.Path, [["hello"]]);
        SessionStore other = await RecordContentEvidence(otherDirectory.Path, [["other"]]);
        SessionManifestV1 source = evidence.Current!;
        StoreDependency chunk = Of(source, StoreDependencyKind.Journal)[0];
        SessionStore derived = Open(derivedDirectory.Path, source);

        using FileStream journal = evidence.Root.OpenOwnedFile(
            chunk.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        (CaptureId capture, SourceClockDescriptor clock) = JournalV1Reader.ReadSourceClock(journal);
        var identity = new SegmentIdentityV1
        {
            CaptureId = capture,
            ClockId = clock.Id,
            TimestampEncoding = clock.Encoding,
            Derivation = ObservationNormalizerV1.ContractVersion,
        };
        using FileStream content = Content(evidence, source);
        using FileStream othersContent = Content(other, other.Current!);
        using (DerivedGenerationBuilder mirror = DerivedGenerationBuilder.BeginMirror(derived, identity, clock, journal))
        {
            // Another capture's content is refused whole; this capture's is staged once.
            Assert.Throws<InvalidDataException>(() => mirror.StageMirroredContent(othersContent));
            mirror.StageMirroredContent(content);
            Assert.Throws<InvalidOperationException>(() => mirror.StageMirroredContent(content));
        }

        // A generation that writes its own journal stages its own content, never a copy.
        using DerivedGenerationBuilder written = DerivedGenerationBuilder.Begin(derived, identity, clock, DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() => written.StageMirroredContent(content));
    }

    /// <summary>
    /// Records the content fixture's messages as evidence only, publishing every 100 ms, a chunk for each burst: each burst
    /// after the first waits for the ones before it to be published. Every message is kept, whole or cut to 8 bytes.
    /// </summary>
    private static async Task<SessionStore> RecordContentEvidence(string directory, string[][] bursts)
    {
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "content-follow-tests");
        OwnedSessionPlan plan = ContentRecordingTests.Plan(
            CaptureBodyAdmissionPolicies.ScopedContentFixture(8, 4_096, ContentInspectionMode.HexAndText));
        var host = new ScriptedHost();
        long now = Stopwatch.GetTimestamp();
        int ordinal = 0;
        for (int burst = 0; burst < bursts.Length; burst++)
        {
            if (burst > 0)
            {
                int published = burst;
                host.PauseUntil(
                    () => store.Current is { } current
                        && current.Dependencies.Count(dependency => dependency.Kind == StoreDependencyKind.Journal) >= published,
                    TimeSpan.FromSeconds(30));
            }

            foreach (string message in bursts[burst])
            {
                ordinal++;
                ContentRecordingTests.AdmitMessage(host, plan, Encoding.UTF8.GetBytes(message), now + ordinal, ordinal);
            }
        }

        LiveRecordingResult result = await LiveSessionRecorder.RecordAsync(
            plan,
            host,
            store,
            _ => host.Delivered.Task,
            DateTimeOffset.UtcNow,
            publishEvery: TimeSpan.FromMilliseconds(100),
            output: LiveRecordingOutput.EvidenceOnly);
        Assert.Equal((ordinal, false), ((int)result.ContentFragments, result.ContentLimitReached));
        return SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory));
    }

    private static FileStream Content(SessionStore store, SessionManifestV1 manifest) => store.Root.OpenOwnedFile(
        Of(manifest, StoreDependencyKind.Content)[0].Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None);

    /// <summary>Every fragment the session's content chunks keep, by its record's ordinal, with the bytes it holds.</summary>
    private static Dictionary<ulong, (ContentFragmentV1 Fragment, byte[] Bytes)> Fragments(SessionStore store)
    {
        CaptureId capture;
        using (FileStream journal = store.Root.OpenOwnedFile(Of(store.Current!, StoreDependencyKind.Journal)[0].Name,
            FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None))
        {
            capture = JournalV1Reader.ReadSourceClock(journal).CaptureId;
        }

        var fragments = new Dictionary<ulong, (ContentFragmentV1, byte[])>();
        foreach (StoreDependency chunk in Of(store.Current!, StoreDependencyKind.Content))
        {
            using FileStream stream = store.Root.OpenOwnedFile(chunk.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None);
            ContentChunkContentsV1 contents = ContentChunkV1.Read(stream, capture);
            foreach (ContentChunkEntryV1 entry in contents.Entries)
            {
                fragments.Add(entry.Fragment.RecordOrdinal, (entry.Fragment, ContentChunkV1.ReadBytes(stream, entry, 64)));
            }
        }

        return fragments;
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

        return rows;
    }

    private static StoreDependency[] Of(SessionManifestV1 manifest, StoreDependencyKind kind) =>
    [
        .. manifest.Dependencies.Where(dependency => dependency.Kind == kind)
            .OrderBy(dependency => dependency.Name, StringComparer.OrdinalIgnoreCase),
    ];

    private static SessionStore Open(string directory, SessionManifestV1 source) =>
        SessionStore.Open(LocalOwnedDirectory.Open(directory), source.SessionId, source.SourceIdentity);
}
