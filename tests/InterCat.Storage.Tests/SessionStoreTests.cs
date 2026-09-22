using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace InterCat.Storage.Tests;

public sealed class SessionStoreTests
{
    private static readonly Guid Session = Guid.Parse("3b1f5f9c-7a2d-4c31-9f0e-11d2c3b4a5e6");
    private static readonly DateTimeOffset Committed = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "I15: a published generation names every dependency with its length and digest")]
    public void APublishedGenerationNamesEveryDependency()
    {
        using var session = new TemporarySession();

        StoreCommitResult result = Publish(session.Store, ("segment-0001.icats", "first"), ("dict-0001.icatd", "second"));

        StoreDependency segment = Assert.Single(
            result.Manifest.Dependencies,
            dependency => dependency.Name == "segment-0001.icats");
        Assert.Equal(1, result.Manifest.Generation);
        Assert.Null(result.Manifest.PreviousGeneration);
        Assert.Equal(0, result.PreviousGeneration);
        Assert.Equal(Session, result.Manifest.SessionId);
        Assert.Equal(5, segment.LengthBytes);
        Assert.Equal(Digest("first"), segment.Digest);
        Assert.Null(result.Manifest.VerifyDigest());
        Assert.Equal(["segment-0001.icats", "dict-0001.icatd"], result.PublishedFiles);
    }

    [Fact(DisplayName = "I15: a later generation carries its predecessor's dependencies forward")]
    public void ALaterGenerationCarriesItsPredecessorForward()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));

        StoreCommitResult second = Publish(session.Store, ("segment-0002.icats", "second"));

        Assert.Equal(2, second.Manifest.Generation);
        Assert.Equal(1, second.Manifest.PreviousGeneration);
        Assert.Equal(
            ["segment-0001.icats", "segment-0002.icats"],
            second.Manifest.Dependencies.Select(dependency => dependency.Name).Order(StringComparer.Ordinal));
    }

    [Fact(DisplayName = "I15: a published name is immutable, so republishing it is refused")]
    public void APublishedNameIsImmutable()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));

        using StoreStagingFile staged = Stage(session.Store, "segment-0001.icats", "different");
        _ = staged.Complete();

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() =>
            session.Store.Commit([staged], CommittedBoundary.None, Committed));

        Assert.Contains("immutable", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(1, session.Store.Current!.Generation);
    }

    [Fact(DisplayName = "I15: an incomplete staged file is never published")]
    public void AnIncompleteStagedFileIsNeverPublished()
    {
        using var session = new TemporarySession();
        using StoreStagingFile staged = session.Store.Stage("segment-0001.icats", StoreDependencyKind.Segment);
        staged.Content.Write("not flushed"u8);

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() =>
            session.Store.Commit([staged], CommittedBoundary.None, Committed));

        Assert.Contains("not completed", refusal.Message, StringComparison.Ordinal);
        Assert.Null(session.Store.Current);
    }

    [Fact(DisplayName = "I15: a reopened session acquires the generation its pointer names")]
    public void AReopenedSessionAcquiresItsGeneration()
    {
        using var session = new TemporarySession();
        StoreCommitResult first = Publish(session.Store, ("segment-0001.icats", "first"));

        SessionStore reopened = session.Reopen();

        Assert.Equal(first.Manifest.Digest, reopened.Current!.Digest);
        Assert.Equal(first.Manifest.Generation, reopened.Current.Generation);
        Assert.Equal(
            first.Manifest.Dependencies,
            reopened.Current.Dependencies);
        Assert.Equal(1, reopened.Recovery.Generation);
        Assert.False(reopened.Recovery.RolledBackToLastKnownGood);
        Assert.Null(reopened.Recovery.RollbackReason);
        Assert.Empty(reopened.Recovery.OrphanFiles);
    }

    [Fact(DisplayName = "I15: a crash before the manifest leaves the previous generation and an orphan file")]
    public void ACrashBeforeTheManifestKeepsThePreviousGeneration()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));

        // Step 3 interrupted: the file is published, but no manifest references it yet.
        session.WriteRaw("segment-0002.icats", "second");

        SessionStore reopened = session.Reopen();

        Assert.Equal(1, reopened.Current!.Generation);
        Assert.Equal(["segment-0002.icats"], reopened.Recovery.OrphanFiles);
        Assert.Equal(6, reopened.Recovery.OrphanBytes);
        Assert.False(reopened.Recovery.RolledBackToLastKnownGood);
        Assert.True(File.Exists(Path.Combine(session.Path, "segment-0002.icats")));
    }

    [Fact(DisplayName = "I15: a crash before the pointer leaves the previous generation and an orphan manifest")]
    public void ACrashBeforeThePointerKeepsThePreviousGeneration()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));
        StoreCommitResult second = Publish(session.Store, ("segment-0002.icats", "second"));

        // Step 4 interrupted: generation 2 is fully written, but the pointer still names generation 1.
        session.WriteRaw(
            SessionPointerV1.FileName,
            JsonSerializer.Serialize(
                SessionPointerV1.For(session.ManifestOf(1)),
                SessionManifestV1.Json));

        SessionStore reopened = session.Reopen();

        Assert.Equal(1, reopened.Current!.Generation);
        Assert.Contains(SessionManifestV1.FileNameFor(2), reopened.Recovery.OrphanFiles);
        Assert.Equal(2, second.Manifest.Generation);
    }

    [Fact(DisplayName = "I15: a torn pointer rolls back to the retained last-known-good")]
    public void ATornPointerRollsBackToLastKnownGood()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));
        _ = Publish(session.Store, ("segment-0002.icats", "second"));

        session.WriteRaw(SessionPointerV1.FileName, "{ \"formatVersion\": 1, \"generation\":");

        SessionStore reopened = session.Reopen();

        Assert.Equal(1, reopened.Current!.Generation);
        Assert.True(reopened.Recovery.RolledBackToLastKnownGood);
        Assert.Contains("unreadable", reopened.Recovery.RollbackReason!, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I15: a dependency that changed under a generation fails it rather than being read")]
    public void AChangedDependencyFailsItsGeneration()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));
        _ = Publish(session.Store, ("segment-0002.icats", "second"));

        // A rename is not a power-failure guarantee: the name is there and the contents are not.
        // The same length with different bytes, so it is the digest and not the size that catches it.
        session.WriteRaw("segment-0002.icats", "SECOND");

        SessionStore reopened = session.Reopen();

        Assert.Equal(1, reopened.Current!.Generation);
        Assert.True(reopened.Recovery.RolledBackToLastKnownGood);
        Assert.Contains("segment-0002.icats", reopened.Recovery.RollbackReason!, StringComparison.Ordinal);
        Assert.Contains("computes", reopened.Recovery.RollbackReason!, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I15: a manifest whose contents no longer match its digest is refused")]
    public void AManifestThatDoesNotMatchItsDigestIsRefused()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));
        string manifestName = SessionManifestV1.FileNameFor(1);
        string tampered = File.ReadAllText(Path.Combine(session.Path, manifestName))
            .Replace("\"committedRecords\": 0", "\"committedRecords\": 9", StringComparison.Ordinal)
            .Replace("\"generation\": 1", "\"generation\": 1 ", StringComparison.Ordinal);

        session.WriteRaw(manifestName, tampered);

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(session.Reopen);
        Assert.Contains("no complete generation", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I15: an interrupted staging file is removed on the next open")]
    public void AnInterruptedStagingFileIsRemovedOnOpen()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));
        string staging = $"{SessionStore.StagingPrefix}{Guid.NewGuid():N}{SessionStore.StagingSuffix}";
        session.WriteRaw(staging, "half written");

        SessionStore reopened = session.Reopen();

        Assert.Equal([staging], reopened.Recovery.RemovedStagingFiles);
        Assert.False(File.Exists(Path.Combine(session.Path, staging)));
        Assert.Equal(1, reopened.Current!.Generation);
    }

    [Fact(DisplayName = "I15: an orphan is reported on open and removed only when that is asked for")]
    public void AnOrphanIsReportedOnOpenAndRemovedOnlyWhenAsked()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));
        session.WriteRaw("segment-0009.icats", "orphaned");

        SessionStore reopened = session.Reopen();
        Assert.Equal(["segment-0009.icats"], reopened.Recovery.OrphanFiles);
        Assert.True(File.Exists(Path.Combine(session.Path, "segment-0009.icats")));

        IReadOnlyList<string> removed = reopened.RemoveOrphans();

        Assert.Equal(["segment-0009.icats"], removed);
        Assert.False(File.Exists(Path.Combine(session.Path, "segment-0009.icats")));
        Assert.Equal(1, reopened.Current!.Generation);
    }

    [Fact(DisplayName = "I15: an empty session is empty, not a refusal")]
    public void AnEmptySessionIsEmpty()
    {
        using var session = new TemporarySession();

        Assert.Null(session.Store.Current);
        Assert.Equal(0, session.Store.Recovery.Generation);
        Assert.Empty(session.Store.Recovery.OrphanFiles);
    }

    [Fact(DisplayName = "I15: a session is never opened as another session")]
    public void ASessionIsNeverOpenedAsAnother()
    {
        using var session = new TemporarySession();
        _ = Publish(session.Store, ("segment-0001.icats", "first"));

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() =>
            SessionStore.Open(LocalOwnedDirectory.Open(session.Path), Guid.NewGuid()));

        Assert.Contains("never opened as another", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I15: a committed boundary states which admitted evidence a generation derives from")]
    public void ACommittedBoundaryStatesItsEvidence()
    {
        using var session = new TemporarySession();
        var boundary = new CommittedBoundary("capture-0001.icatj", 4_096, 512, Digest("journal"));

        using StoreStagingFile staged = Stage(session.Store, "segment-0001.icats", "first");
        _ = staged.Complete();
        StoreCommitResult result = session.Store.Commit([staged], boundary, Committed);

        Assert.Equal(boundary, result.Manifest.Boundary);
        Assert.True(result.Manifest.Boundary.IsDeclared);
        Assert.False(CommittedBoundary.None.IsDeclared);
        Assert.Equal(boundary, session.Reopen().Current!.Boundary);
        Assert.Equal(result.Manifest.Digest, session.Store.Current!.Digest);
    }

    [Theory(DisplayName = "I15: a manifest this reader cannot read exactly is refused, never read at a guess")]
    [InlineData(2, 1, "format 2")]
    [InlineData(1, 0, "between 1 and")]
    public void AnUnreadableManifestIsRefused(int formatVersion, long generation, string reason)
    {
        var manifest = new SessionManifestV1
        {
            FormatVersion = formatVersion,
            Generation = generation,
            SessionId = Session,
            CommittedUtc = Committed,
            SourceIdentity = "test",
            PreviousGeneration = null,
            Boundary = CommittedBoundary.None,
            Dependencies = [],
            Digest = "sha256:" + new string('0', 64),
        };

        Assert.Contains(reason, manifest.Validate(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I15: a manifest naming a dependency outside its root is refused")]
    public void AManifestNamingADependencyOutsideItsRootIsRefused()
    {
        var manifest = new SessionManifestV1
        {
            FormatVersion = 1,
            Generation = 1,
            SessionId = Session,
            CommittedUtc = Committed,
            SourceIdentity = "test",
            PreviousGeneration = null,
            Boundary = CommittedBoundary.None,
            Dependencies = [new(@"..\escape.icats", StoreDependencyKind.Segment, 1, Digest("x"))],
            Digest = "sha256:" + new string('0', 64),
        };

        Assert.Contains("not an owned file name", manifest.Validate(), StringComparison.Ordinal);
    }

    private static StoreCommitResult Publish(SessionStore store, params (string Name, string Content)[] files)
    {
        var staged = new List<StoreStagingFile>();
        try
        {
            foreach ((string name, string content) in files)
            {
                StoreStagingFile file = Stage(store, name, content);
                _ = file.Complete();
                staged.Add(file);
            }

            return store.Commit(staged, CommittedBoundary.None, Committed);
        }
        finally
        {
            foreach (StoreStagingFile file in staged)
            {
                file.Dispose();
            }
        }
    }

    private static StoreStagingFile Stage(SessionStore store, string name, string content)
    {
        StoreStagingFile staged = store.Stage(
            name,
            name.EndsWith(".icatd", StringComparison.Ordinal)
                ? StoreDependencyKind.Dictionary
                : StoreDependencyKind.Segment);
        staged.Content.Write(Encoding.UTF8.GetBytes(content));
        return staged;
    }

    private static string Digest(string content) =>
        string.Concat("sha256:", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))));

    private sealed class TemporarySession : IDisposable
    {
        public TemporarySession()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "InterCat.Storage.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Store = SessionStore.Open(LocalOwnedDirectory.Open(Path), Session, "test-source");
        }

        public string Path { get; }

        public SessionStore Store { get; private set; }

        public SessionStore Reopen()
        {
            Store = SessionStore.Open(LocalOwnedDirectory.Open(Path), Session, "test-source");
            return Store;
        }

        public SessionManifestV1 ManifestOf(long generation) =>
            JsonSerializer.Deserialize<SessionManifestV1>(
                File.ReadAllText(System.IO.Path.Combine(Path, SessionManifestV1.FileNameFor(generation))),
                SessionManifestV1.Json)!;

        public void WriteRaw(string name, string content) =>
            File.WriteAllText(System.IO.Path.Combine(Path, name), content);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
