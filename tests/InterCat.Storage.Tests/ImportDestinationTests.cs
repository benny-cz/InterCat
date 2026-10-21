using System.Text;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// §18.4: what the folder an import was told to publish into holds, read before the evidence is - a new folder, this
/// import's verified session (reused), another session, one that cannot be read, what an unfinished publication left or a
/// writer is staging now, or files no session owns - and looking writes nothing.
/// </summary>
public sealed class ImportDestinationTests : IDisposable
{
    private static readonly Guid Imported = Guid.Parse("8d4f0a52-6c1e-4b7d-9a3f-2e5c7b1d9f04");
    private static readonly Guid Recorded = Guid.Parse("1c9e7b3a-5d2f-4e8a-b6c1-0f4d2a7e9b35");
    private static readonly DateTimeOffset Committed = new(2026, 10, 21, 9, 30, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), "InterCat.Storage.Tests.ImportDestination", Guid.NewGuid().ToString("N"));

    public ImportDestinationTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact(DisplayName = "§18.4: a folder not there yet, an empty one, and one holding only a session's lock files are new, and the evidence is not hashed for them")]
    public void NothingPublishedOrStagedIsNew()
    {
        string missing = Path.Combine(root, "missing");
        Assert.Equal(ImportDestinationKind.New, ImportDestination.Inspect(missing, Unhashed).Kind);
        Assert.False(Directory.Exists(missing));

        string empty = Folder("empty");
        Assert.Equal(ImportDestinationKind.New, ImportDestination.Inspect(empty, Unhashed).Kind);

        // What a review of staged files leaves once it removed them: the lock it took to remove them.
        string locked = Folder("locked");
        foreach (string name in new[]
            { SessionStore.PublicationLockFileName, SessionStore.EvidenceLeaseLockFileName, RetentionPinsV1.LockFileName })
        {
            File.WriteAllBytes(Path.Combine(locked, name), []);
            ImportDestination destination = ImportDestination.Inspect(locked + Path.DirectorySeparatorChar, Unhashed);
            Assert.Equal(ImportDestinationKind.New, destination.Kind);
            Assert.Equal(locked, destination.Path);
            Assert.Empty(destination.Entries);
        }
    }

    [Fact(DisplayName = "§18.4: files no session owns, a folder inside, or a file where the folder would be, are named and never hashed against")]
    public void WhatNoSessionOwnsIsOccupied()
    {
        string stray = Folder("stray");
        File.WriteAllText(Path.Combine(stray, "notes.txt"), "kept");
        File.WriteAllBytes(Path.Combine(stray, SessionStore.PublicationLockFileName), []);
        File.WriteAllBytes(Path.Combine(stray, "a.bin"), [1, 2, 3]);
        ImportDestination destination = ImportDestination.Inspect(stray, Unhashed);
        Assert.Equal(ImportDestinationKind.Occupied, destination.Kind);
        Assert.Equal(["a.bin", "notes.txt"], destination.Entries);

        // A session's own pins are not a session without a generation that names them.
        string pinned = Folder("pinned");
        File.WriteAllText(Path.Combine(pinned, RetentionPinsV1.FileName), "{}");
        Assert.Equal([RetentionPinsV1.FileName], ImportDestination.Inspect(pinned, Unhashed).Entries);

        string nested = Folder("nested");
        Directory.CreateDirectory(Path.Combine(nested, "inner"));
        File.WriteAllBytes(Path.Combine(nested, SessionStore.EvidenceLeaseLockFileName), []);
        destination = ImportDestination.Inspect(nested, Unhashed);
        Assert.Equal(ImportDestinationKind.Occupied, destination.Kind);
        Assert.Equal(["inner"], destination.Entries);

        // A folder named as a session's lock is not one.
        string disguised = Folder("disguised");
        Directory.CreateDirectory(Path.Combine(disguised, SessionStore.PublicationLockFileName));
        destination = ImportDestination.Inspect(disguised, Unhashed);
        Assert.Equal(ImportDestinationKind.Occupied, destination.Kind);
        Assert.Equal([SessionStore.PublicationLockFileName], destination.Entries);

        string file = Path.Combine(root, "session.etl");
        File.WriteAllText(file, "not a folder");
        destination = ImportDestination.Inspect(file, Unhashed);
        Assert.Equal((ImportDestinationKind.Occupied, file), (destination.Kind, destination.Path));
        Assert.Equal(["session.etl"], destination.Entries);
    }

    [Fact(DisplayName = "§18.4: the folder holding this import's verified session is reused at its generation, the evidence hashed once")]
    public void ThisImportsSessionIsReused()
    {
        string folder = Folder("imported");
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(folder), Imported, "import-v1:trace.etl");
        _ = Publish(store, ("segment-0001.icats", "first rows"));
        _ = Publish(store, ("segment-0002.icats", "second rows"));
        string[] before = Listing(folder);

        int hashed = 0;
        ImportDestination destination = ImportDestination.Inspect(folder, () =>
        {
            hashed++;
            return Imported;
        });

        Assert.Equal(ImportDestinationKind.ThisImport, destination.Kind);
        Assert.Equal(1, hashed);
        Assert.Equal((Imported, 2L), (destination.Manifest!.SessionId, destination.Manifest.Generation));
        Assert.Null(destination.Reason);
        Assert.Empty(destination.Entries);
        Assert.Equal(before, Listing(folder));
    }

    [Fact(DisplayName = "§18.4: this import's session whose newest generation does not verify is reused at its last-known-good one, saying why")]
    public void ThisImportReadAtItsLastKnownGoodSaysWhy()
    {
        string folder = Folder("rolled-back");
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(folder), Imported, "import-v1:trace.etl");
        _ = Publish(store, ("segment-0001.icats", "first rows"));
        _ = Publish(store, ("segment-0002.icats", "second rows"));

        // The same length, other bytes: only hashing tells the newest generation from what it names.
        File.WriteAllText(Path.Combine(folder, "segment-0002.icats"), "SECOND ROWS");
        string[] before = Listing(folder);
        ImportDestination destination = ImportDestination.Inspect(folder, () => Imported);

        Assert.Equal(ImportDestinationKind.ThisImport, destination.Kind);
        Assert.Equal(1, destination.Manifest!.Generation);
        Assert.Contains("segment-0002.icats", destination.Reason, StringComparison.Ordinal);
        Assert.Equal(before, Listing(folder));

        // A session whose current pointer is gone is still a session: the one it keeps to fall back to names it.
        string pointerless = Folder("pointerless");
        store = SessionStore.Open(LocalOwnedDirectory.Open(pointerless), Imported, "import-v1:trace.etl");
        _ = Publish(store, ("segment-0001.icats", "first rows"));
        _ = Publish(store, ("segment-0002.icats", "second rows"));
        File.Delete(Path.Combine(pointerless, SessionPointerV1.FileName));
        destination = ImportDestination.Inspect(pointerless, () => Imported);
        Assert.Equal((ImportDestinationKind.ThisImport, 1L), (destination.Kind, destination.Manifest!.Generation));
        Assert.Contains(SessionPointerV1.FileName, destination.Reason, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§18.4: a session of another identity is another session, read without verifying it")]
    public void AnotherIdentityIsAnotherSession()
    {
        string folder = Folder("recorded");
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(folder), Recorded, "live-capture:recorded");
        _ = Publish(store, ("segment-0001.icats", "first rows"));

        // Damaged bytes of the same length: another session is never hashed, so it is still named as another session.
        File.WriteAllText(Path.Combine(folder, "segment-0001.icats"), "FIRST ROWS");
        ImportDestination destination = ImportDestination.Inspect(folder, () => Imported);

        Assert.Equal(ImportDestinationKind.AnotherSession, destination.Kind);
        Assert.Equal((Recorded, "live-capture:recorded"), (destination.Manifest!.SessionId, destination.Manifest.SourceIdentity));
        Assert.Null(destination.Reason);
    }

    [Fact(DisplayName = "§18.4: a session no generation of which verifies is unreadable, whether a file is gone or this import's bytes changed")]
    public void ASessionNothingOfWhichVerifiesIsUnreadable()
    {
        string gone = Folder("gone");
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(gone), Imported, "import-v1:trace.etl");
        _ = Publish(store, ("segment-0001.icats", "first rows"));
        File.Delete(Path.Combine(gone, "segment-0001.icats"));
        ImportDestination destination = ImportDestination.Inspect(gone, Unhashed);
        Assert.Equal(ImportDestinationKind.Unreadable, destination.Kind);
        Assert.Contains("no generation of it verifies", destination.Reason, StringComparison.Ordinal);
        Assert.Null(destination.Manifest);

        string changed = Folder("changed");
        store = SessionStore.Open(LocalOwnedDirectory.Open(changed), Imported, "import-v1:trace.etl");
        _ = Publish(store, ("segment-0001.icats", "first rows"));
        File.WriteAllText(Path.Combine(changed, "segment-0001.icats"), "FIRST ROWS");
        destination = ImportDestination.Inspect(changed, () => Imported);
        Assert.Equal(ImportDestinationKind.Unreadable, destination.Kind);
        Assert.Contains("no generation of it verifies", destination.Reason, StringComparison.Ordinal);
        Assert.Equal(Imported, destination.Manifest!.SessionId);
    }

    [Fact(DisplayName = "§18.4: what an unfinished publication staged is named with its bytes and kept; what a writer still holds is publishing")]
    public void StagedFilesAreUnfinishedOrPublishing()
    {
        string folder = Folder("staged");
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(folder), Imported, "import-v1:trace.etl");
        using (StoreStagingFile abandoned = store.Stage("journal-0000000001.icatj", StoreDependencyKind.Journal))
        {
            abandoned.Content.Write(Encoding.UTF8.GetBytes("twelve bytes"));
        }

        string[] before = Listing(folder);
        ImportDestination destination = ImportDestination.Inspect(folder, Unhashed);
        Assert.Equal(ImportDestinationKind.Unfinished, destination.Kind);
        Assert.Equal(2, destination.Entries.Count);
        Assert.All(destination.Entries, name => Assert.StartsWith(SessionStore.StagingPrefix, name, StringComparison.Ordinal));
        Assert.Equal(12, destination.StagedBytes);
        Assert.Equal(before, Listing(folder));

        using StoreStagingFile held = store.Stage("segment-0000000001.icats", StoreDependencyKind.Segment);
        held.Content.Write(Encoding.UTF8.GetBytes("four"));
        held.Content.Flush();
        destination = ImportDestination.Inspect(folder, Unhashed);
        Assert.Equal(ImportDestinationKind.Publishing, destination.Kind);
        Assert.Equal(4, destination.Entries.Count);
        Assert.Equal(16, destination.StagedBytes);
    }

    /// <summary>The import's identity, which a test that expects the evidence not to be hashed never gives.</summary>
    private static Guid Unhashed() =>
        throw new InvalidOperationException("The evidence was hashed for a folder that holds no session to compare it with.");

    private string Folder(string name)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string[] Listing(string folder) =>
        [.. Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    private static StoreCommitResult Publish(SessionStore store, params (string Name, string Content)[] files)
    {
        var staged = new List<StoreStagingFile>();
        try
        {
            foreach ((string name, string content) in files)
            {
                StoreStagingFile file = store.Stage(name, StoreDependencyKind.Segment);
                file.Content.Write(Encoding.UTF8.GetBytes(content));
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
}
