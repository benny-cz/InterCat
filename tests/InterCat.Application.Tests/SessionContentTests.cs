using System.Text;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// Content kept beside the journal (`contracts/content-v1.md`, ADR-036): a person's record readers state it, nothing that
/// reads metadata does, it lives and dies with its journal chunk, and each sharing preset says what it does with it.
/// </summary>
public sealed class SessionContentTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [Fact(DisplayName = "§3.7: a page for a person states each record's kept content, and a read for an export carries none")]
    public void APageStatesKeptContent()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Rows(0);
        Publish(session.Store, rows, content: (ContentHeader(recordLimit: 8), Kept(rows)));

        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        Assert.All(page.Records, record => Assert.NotNull(record.Content));
        Assert.Equal(
            [
                "Kept whole: 6 bytes of an application payload it sent, UTF-8 text.",
                "Kept in part: the first 8 of 16 bytes of an application payload it sent, UTF-8 text; the other 8 were cut "
                    + "by the 8-byte record limit.",
                "Not kept: the capture had reached its content limit when this record arrived; an application payload it "
                    + "received was 300 bytes.",
            ],
            page.Records.Select(record => RecordContent.Of(record.Observation, kept: record.Content).Describe()));

        // An empty message is kept whole too, and is said to have held nothing, not to have lost its bytes.
        SessionContentEntry first = page.Records[0].Content!;
        SessionContentEntry empty = first with { Entry = first.Entry with { Fragment = first.Fragment with { OriginalLength = 0, Kept = 0 } } };
        Assert.Equal("Kept whole: an application payload it sent, which held no bytes.",
            RecordContent.Of(page.Records[0].Observation, kept: empty).Describe());
        Assert.Equal("GET /x"u8.ToArray(), SessionContentIndex.ReadBytes(session.Store.Root, page.Records[0].Content!, 64));
        Assert.Equal("0123"u8.ToArray(), SessionContentIndex.ReadBytes(session.Store.Root, page.Records[1].Content!, 4));

        // The original record's view states it too, apart from the event's body.
        SessionRawRecordDetail raw = SessionRawRecordQuery.Read(session.Store, page.SessionId, page.Generation, page.Records[1]);
        Assert.StartsWith("Kept in part: the first 8 of 16 bytes", raw.Content!.Describe(), StringComparison.Ordinal);
        Assert.Equal(page.Records[1].Content, raw.KeptContent);

        // A whole-scope read, which an export makes, never reads a fragment.
        Assert.All(SessionEvidenceQuery.ReadScope(session.Store, 10).Records, record => Assert.Null(record.Content));
    }

    [Fact(DisplayName = "ADR-036: content kept without consent to inspect it is stated, and its bytes are never read for a person")]
    public void ContentWithoutConsentIsNeverShown()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Rows(0);
        Publish(session.Store, rows, content: (ContentHeader(recordLimit: 8, inspection: ContentInspectionV1.Disabled), Kept(rows)));

        SessionEvidenceRecord first = SessionEvidenceQuery.Read(session.Store).Records[0];
        Assert.Equal(
            "Kept whole: 6 bytes of an application payload it sent, UTF-8 text. Its bytes are not shown: the capture kept "
            + "them without consent to inspect them.",
            RecordContent.Of(first.Observation, kept: first.Content).Describe());
        Assert.Null(SessionContentIndex.ReadBytes(session.Store.Root, first.Content!, 64));

        // Kept content that could not all be read makes a record's content unknown, never none (R21).
        Assert.Equal(
            "Unknown. This session's kept content could not all be read (a chunk failed), so this record's may be in what "
            + "could not.",
            RecordContent.Of(first.Observation, problem: "a chunk failed").Describe());
    }

    [Fact(DisplayName = "ADR-036: kept content is carried by every later generation and released only with its journal chunk")]
    public void ContentLivesAndDiesWithItsJournalChunk()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] early = Rows(0);
        ObservationRowV1[] late = Rows(100);
        Publish(session.Store, early, content: (ContentHeader(recordLimit: 8), Kept(early)));
        Publish(session.Store, late, content: (ContentHeader(recordLimit: 8), Kept(late)));
        string[] Content() => [.. session.Store.Current!.Dependencies
            .Where(dependency => dependency.Kind == StoreDependencyKind.Content).Select(dependency => dependency.Name)];
        Assert.Equal(["content-0000000001.icatc", "content-0000000002.icatc"], Content());
        Assert.Equal(6, SessionEvidenceQuery.Read(session.Store).Records.Count(record => record.Content is not null));

        // Content is not a disposable derived file.
        Assert.Throws<ArgumentException>(() => session.Store.ReleaseDependencies(
            ["content-0000000001.icatc"], "free space", DateTimeOffset.UnixEpoch));

        // Releasing the first recording chunk releases the content kept of its records, in the same record.
        RetentionOutcome released = JournalRetention.Release(session.Store, early.Length, "keep the last chunk",
            DateTimeOffset.UnixEpoch);
        Assert.Equal(["content-0000000002.icatc"], Content());
        Assert.Contains("content-0000000001.icatc", released.ReleasedFiles);
        Assert.Contains("journal-0000000001.icatj", released.ReleasedFiles);
        Assert.Equal(released.ReleasedFiles, released.Manifest.Retention!.ReleasedFiles);
        SessionEvidencePage after = SessionEvidenceQuery.Read(session.Store);
        Assert.Equal(
            [false, false, false, true, true, true],
            after.Records.Select(record => record.Content is not null));
    }

    [Fact(DisplayName = "ADR-036: a session states the content it keeps in sum - records, bytes and policies - without a byte of it")]
    public void ASessionStatesItsContentInSum()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] early = Rows(0);
        ObservationRowV1[] late = Rows(100);
        Publish(session.Store, early, content: (ContentHeader(recordLimit: 8), Kept(early)));
        Publish(session.Store, late, content: (ContentHeader(recordLimit: 8), Kept(late)));

        SessionContentSummary summary = SessionContentIndex.Read(session.Store.Root, session.Store.Current!, CancellationToken.None)
            .Summarize();
        Assert.Equal((2, 6, 2, 2, 2, 28L), (summary.Chunks, summary.Records, summary.Whole, summary.Cut, summary.Omitted, summary.KeptBytes));
        Assert.Equal([new SessionContentPolicy("test-scoped-content-v1", 8, ContentInspectionV1.HexAndText)], summary.Policies);
        Assert.Null(summary.Problem);

        // A chunk that cannot be read is left out of the sums, which say so, rather than counted as holding nothing.
        string damaged = session.Store.Current!.Dependencies.First(dependency => dependency.Kind == StoreDependencyKind.Content).Name;
        using (FileStream stream = session.Store.Root.OpenOwnedFile(damaged, FileMode.Open, FileAccess.ReadWrite, FileShare.None, FileOptions.None))
        {
            stream.Position = stream.Length - 1;
            int last = stream.ReadByte();
            stream.Position = stream.Length - 1;
            stream.WriteByte((byte)(last ^ 0xFF));
        }

        SessionContentSummary partial = SessionContentIndex.Read(session.Store.Root, session.Store.Current!, CancellationToken.None)
            .Summarize();
        Assert.Equal((2, 3, 14L), (partial.Chunks, partial.Records, partial.KeptBytes));
        Assert.Contains(damaged, partial.Problem, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ADR-036: rewriting one journal's prefix is refused while content is kept beside it, and not once it is released")]
    public void APrefixRewriteIsRefusedWhileContentIsKept()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Rows(0);
        Publish(session.Store, rows, journalBatchRecords: 1, content: (ContentHeader(recordLimit: 8), Kept(rows)), finished: true);
        long generation = session.Store.Current!.Generation;

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() =>
            JournalRetention.Release(session.Store, 1, "free space", DateTimeOffset.UnixEpoch));
        Assert.Contains("keeps content", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("content release", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(generation, session.Store.Current!.Generation);

        // With its content released on its own, the journal's prefix is what the journal alone decides.
        ContentRetention.Release(session.Store, "payloads held tokens", DateTimeOffset.UnixEpoch);
        RetentionOutcome prefix = JournalRetention.Release(session.Store, 1, "free space", DateTimeOffset.UnixEpoch);
        Assert.Equal((RetentionExtentKind.JournalPrefix, 1L), (prefix.Manifest.Retention!.Kind, prefix.Manifest.Retention.ReleasedRecords));
    }

    [Fact(DisplayName = "ADR-036: kept content is released on its own once its capture has finished, and every record stays")]
    public void ContentIsReleasedOnItsOwn()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] early = Rows(0);
        ObservationRowV1[] late = Rows(100);
        Publish(session.Store, early, content: (ContentHeader(recordLimit: 8), Kept(early)));
        Publish(session.Store, late, content: (ContentHeader(recordLimit: 8), Kept(late)), finished: true);
        SessionManifestV1 before = session.Store.Current!;
        StoreDependency[] content = [.. before.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Content)];

        // Measured first, and nothing written: both chunks, every record's content they hold, and their size.
        ContentReleasePreview preview = ContentRetention.Preview(session.Store);
        Assert.Equal(["content-0000000001.icatc", "content-0000000002.icatc"], preview.Chunks);
        Assert.Equal((before.Generation, 6L, content.Sum(dependency => dependency.LengthBytes), true, true),
            (preview.Generation, preview.Records, preview.FileBytes, preview.Finished, preview.ReleasesAnything));
        Assert.Empty(preview.Unreadable);
        Assert.Same(before, session.Store.Current);

        // Released, the generation names no content and keeps everything else as it was, the boundary included.
        DateTimeOffset when = new(2026, 10, 5, 9, 30, 0, TimeSpan.Zero);
        RetentionOutcome released = ContentRetention.Release(session.Store, "the investigation is done; its payloads held tokens",
            DateTimeOffset.UnixEpoch, when);
        SessionManifestV1 after = released.Manifest;
        Assert.Equal(before.Dependencies.Where(dependency => dependency.Kind != StoreDependencyKind.Content), after.Dependencies);
        Assert.Equal(before.Boundary, after.Boundary);
        Assert.Equal(["content-0000000001.icatc", "content-0000000002.icatc"], released.ReleasedFiles);

        // Its record says what went and why, as every retention's does.
        RetentionRecord record = after.Retention!;
        Assert.Equal((RetentionExtentKind.Content, when, "the investigation is done; its payloads held tokens", 6L,
                content.Sum(dependency => dependency.LengthBytes), SessionStore.ChunkSourceDigest(content)),
            (record.Kind, record.ReleasedUtc, record.Reason, record.ReleasedRecords, record.ReleasedBytes, record.SourceDigest));
        Assert.Equal(released.ReleasedFiles, record.ReleasedFiles);
        Assert.Null(record.Validate());
        Assert.NotNull((record with { SourceDigest = string.Empty }).Validate());

        // Every record stays, with none of its content, and asking for it says when and why it went.
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        Assert.Equal(6, page.Records.Count);
        Assert.All(page.Records, evidence => Assert.Null(evidence.Content));
        SessionContentDetail detail = SessionContentQuery.Read(session.Store, page.SessionId, page.Records[0], revealBytes: true);
        Assert.Equal((false, (byte[]?)null), (detail.Available, detail.Bytes));
        Assert.Equal("Its content was released on 2026-10-05 09:30 UTC, with every record's content in the session: \"the "
            + "investigation is done; its payloads held tokens\". Every record's metadata is kept, and none of its bytes.",
            detail.UnavailableReason);

        // The released generation reopens and verifies, and there is nothing left to release.
        Assert.Equal(after.Generation, SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path)).Current!.Generation);
        Assert.False(ContentRetention.Preview(session.Store).ReleasesAnything);
        Assert.Contains("keeps no content", Assert.Throws<InvalidOperationException>(() =>
            ContentRetention.Release(session.Store, "again", DateTimeOffset.UnixEpoch)).Message, StringComparison.Ordinal);

        // A record of a source that carries its message, with none kept now, says it was released rather than never held.
        Assert.Equal("None. Its source carries the message itself, and the session keeps none of it now: a retention "
            + "released it, on its own or with its journal chunk.",
            RecordContent.Of(early[0] with { Mechanism = Mechanism.Http }).Describe());
    }

    [Fact(DisplayName = "ADR-036: a content release is refused when nothing is kept or its capture has not finished, and publishes nothing")]
    public void AContentReleaseIsRefusedUnlessItCanBeOne()
    {
        // A capture without its final publication may still be recording, and a release would strand its writer.
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Rows(0);
        Publish(session.Store, rows, content: (ContentHeader(recordLimit: 8), Kept(rows)));
        long generation = session.Store.Current!.Generation;
        Assert.Equal((true, false), (ContentRetention.Preview(session.Store).ReleasesAnything, ContentRetention.Preview(session.Store).Finished));
        Assert.Contains("has not finished", Assert.Throws<InvalidOperationException>(() =>
            ContentRetention.Release(session.Store, "done", DateTimeOffset.UnixEpoch)).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ContentRetention.Release(session.Store, " ", DateTimeOffset.UnixEpoch));
        Assert.Equal(generation, session.Store.Current!.Generation);

        // A session that keeps no content has none to release.
        using var bare = new TemporarySession();
        Publish(bare.Store, Rows(0), finished: true);
        Assert.False(ContentRetention.Preview(bare.Store).ReleasesAnything);
        Assert.Contains("keeps no content", Assert.Throws<InvalidOperationException>(() =>
            ContentRetention.Release(bare.Store, "done", DateTimeOffset.UnixEpoch)).Message, StringComparison.Ordinal);

        // A chunk whose header cannot be read is named by the measurement, which does not count its records. One whose
        // bytes changed is no longer the generation's, so nothing is published over it.
        using var damaged = new TemporarySession();
        Publish(damaged.Store, rows, content: (ContentHeader(recordLimit: 8), Kept(rows)));
        ObservationRowV1[] later = Rows(100);
        Publish(damaged.Store, later, content: (ContentHeader(recordLimit: 8), Kept(later)), finished: true);
        using (FileStream stream = damaged.Store.Root.OpenOwnedFile("content-0000000001.icatc", FileMode.Open, FileAccess.ReadWrite,
            FileShare.None, FileOptions.None))
        {
            stream.Position = 12;
            int value = stream.ReadByte();
            stream.Position = 12;
            stream.WriteByte((byte)(value ^ 0xFF));
        }

        ContentReleasePreview partial = ContentRetention.Preview(damaged.Store);
        Assert.Equal(["content-0000000001.icatc"], partial.Unreadable);
        Assert.Equal(3, partial.Records);
        long published = damaged.Store.Current!.Generation;
        Assert.Contains("content-0000000001.icatc", Assert.Throws<InvalidDataException>(() =>
            ContentRetention.Release(damaged.Store, "done", DateTimeOffset.UnixEpoch)).Message, StringComparison.Ordinal);
        Assert.Equal(published, damaged.Store.Current!.Generation);

        // What was measured is what is released: a count taken in another generation is refused.
        using var moved = new TemporarySession();
        Publish(moved.Store, rows, content: (ContentHeader(recordLimit: 8), Kept(rows)), finished: true);
        long measured = moved.Store.Current!.Generation;
        Assert.Contains("changed while its content was being measured", Assert.Throws<InvalidOperationException>(() =>
            moved.Store.ReleaseContent(measured - 1, 3, "done", DateTimeOffset.UnixEpoch)).Message, StringComparison.Ordinal);
        Assert.Equal(measured, moved.Store.Current!.Generation);
    }

    [Fact(DisplayName = "I22: a redacted package keeps no content and says so; the original package carries it and says how much")]
    public void EachPresetSaysWhatItDoesWithContent()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Rows(0);
        Publish(session.Store, rows, content: (ContentHeader(recordLimit: 8), Kept(rows)));

        OriginalEvidencePackagePreview original = OriginalEvidencePackage.Preview(session.Store);
        Assert.Equal((1, 3L), (original.ContentChunks.Count, original.ContentFragments));
        string contents = OriginalEvidencePackage.ContentsFor(original);
        Assert.Contains("It also holds the message content this capture kept: the bytes of 3 records", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("no message content", contents, StringComparison.Ordinal);
        using var copy = new TemporaryDirectory("InterCat.ContentPackage.Tests");
        OriginalEvidencePackageResult copied = OriginalEvidencePackage.Create(session.Store, copy.Path);
        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(copied.Directory));
        Assert.Equal(3, SessionEvidenceQuery.Read(reopened).Records.Count(record => record.Content is not null));

        RedactedSessionPackagePreview redacted = RedactedSessionPackage.Preview(session.Store);
        Assert.Equal(1, redacted.SourceContentChunks);
        using var package = new TemporaryDirectory("InterCat.ContentPackage.Tests");
        _ = RedactedSessionPackage.Create(session.Store, package.Path, DateTimeOffset.UnixEpoch);
        SessionStore pseudonymous = SessionStore.OpenExisting(LocalOwnedDirectory.Open(package.Path));
        Assert.DoesNotContain(pseudonymous.Current!.Dependencies, dependency => dependency.Kind == StoreDependencyKind.Content);
        Assert.All(SessionEvidenceQuery.Read(pseudonymous).Records, record => Assert.Null(record.Content));
    }

    /// <summary>Three TCP records from <paramref name="first"/>: two sends and a receive.</summary>
    private static ObservationRowV1[] Rows(long first) =>
    [
        Transfer(first + 10, ObservationKind.Send, AccountingSide.SendSide, 6, 100, (ulong)first + 1).Between(ClientEnd, ServerEnd),
        Transfer(first + 20, ObservationKind.Send, AccountingSide.SendSide, 16, 100, (ulong)first + 2).Between(ClientEnd, ServerEnd),
        Transfer(first + 30, ObservationKind.Receive, AccountingSide.ReceiveSide, 300, 100, (ulong)first + 3).Between(ClientEnd, ServerEnd),
    ];

    /// <summary>The first record's message kept whole, the second's cut by the 8-byte limit, the third's omitted by the session's.</summary>
    private static IReadOnlyList<(ContentFragmentV1 Fragment, ReadOnlyMemory<byte> Bytes)> Kept(ObservationRowV1[] rows) =>
    [
        Content(rows[0], Encoding.UTF8.GetBytes("GET /x"), 8),
        Content(rows[1], Encoding.UTF8.GetBytes("0123456789ABCDEF"), 8),
        (new ContentFragmentV1(rows[2].RawStreamId, rows[2].RawSourceEpoch, rows[2].RawRecordOrdinal,
            ContentClassificationV1.ApplicationPayload, Direction.Inbound, ContentEncodingV1.Binary,
            ContentDispositionV1.OmittedBySessionLimit, 0, 300, 0), ReadOnlyMemory<byte>.Empty),
    ];

    private sealed class TemporaryDirectory(string area) : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), area, Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A reader still holding a file lets go when the test process ends.
            }
        }
    }
}
