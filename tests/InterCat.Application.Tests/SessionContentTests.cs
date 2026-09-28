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

    [Fact(DisplayName = "ADR-036: rewriting one journal's prefix is refused while content is kept beside it")]
    public void APrefixRewriteIsRefusedWhileContentIsKept()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Rows(0);
        Publish(session.Store, rows, journalBatchRecords: 1, content: (ContentHeader(recordLimit: 8), Kept(rows)));
        long generation = session.Store.Current!.Generation;

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() =>
            JournalRetention.Release(session.Store, 1, "free space", DateTimeOffset.UnixEpoch));
        Assert.Contains("keeps content", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(generation, session.Store.Current!.Generation);
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
