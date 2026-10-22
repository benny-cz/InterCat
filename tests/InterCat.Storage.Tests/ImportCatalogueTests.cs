using System.Text;
using System.Text.Json;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// §18.4 and contracts/import-v1.md §8.1: the catalogue of completed imports lists where each import's session is, the most
/// recently recorded first and found by the file's length; recording an import replaces its folder's entry and forgets the
/// folders said to hold it no longer; it keeps the newest thousand; a file it cannot read as a catalogue of this contract
/// lists nothing and is never replaced; and writers recording at once all stay listed.
/// </summary>
public sealed class ImportCatalogueTests : IDisposable
{
    private static readonly DateTimeOffset Recorded = new(2026, 10, 21, 9, 30, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string root = Path.Combine(Path.GetTempPath(), "InterCat.Storage.Tests.ImportCatalogue", Guid.NewGuid().ToString("N"));

    public ImportCatalogueTests() => Directory.CreateDirectory(root);

    private string Catalogue => Path.Combine(root, "kept", ImportCatalogue.FileName);

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

    [Fact(DisplayName = "§8.1: a catalogue not there yet lists nothing; an import recorded is listed, the newest first, found by its file's length, and its folder's entry replaced when it is recorded again")]
    public void RecordedImportsAreListedNewestFirst()
    {
        ImportCatalogue empty = ImportCatalogue.Read(Catalogue);
        Assert.Empty(empty.Entries);
        Assert.Null(empty.Problem);
        Assert.Equal(Catalogue, empty.Path);
        Assert.False(Directory.Exists(Path.GetDirectoryName(Catalogue)));

        // A folder is kept as a full path without a trailing separator, whichever way it was written.
        ImportCatalogueEntry first = Entry("a", 4_096, 1) with { Path = Path.Combine(root, "a") + Path.DirectorySeparatorChar };
        ImportCatalogue written = ImportCatalogue.Update(Catalogue, first, []);
        Assert.Null(written.Problem);
        Assert.Equal([first with { Path = Path.Combine(root, "a") }], written.Entries);
        using (JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Catalogue)))
        {
            Assert.Equal("import-catalogue-v1", document.RootElement.GetProperty("contract").GetString());
            JsonElement listed = document.RootElement.GetProperty("imports")[0];
            Assert.Equal(first.ImportDigest, listed.GetProperty("importDigest").GetString());
            Assert.Equal(first.SessionId.ToString("N"), listed.GetProperty("sessionId").GetString());
            Assert.Equal(Path.Combine(root, "a"), listed.GetProperty("path").GetString());
            Assert.Equal(first.SourcePath, listed.GetProperty("sourcePath").GetString());
            Assert.Equal(4_096, listed.GetProperty("sourceLength").GetInt64());
            Assert.Equal(first.RecordedUtc, listed.GetProperty("recordedUtc").GetDateTimeOffset());
        }

        ImportCatalogueEntry second = Entry("b", 100, 2);
        _ = ImportCatalogue.Update(Catalogue, second, []);
        ImportCatalogue read = ImportCatalogue.Read(Catalogue);
        Assert.Equal([second.Path, Path.Combine(root, "a")], read.Entries.Select(entry => entry.Path));
        Assert.Equal([Path.Combine(root, "a")], read.OfLength(4_096).Select(entry => entry.Path));
        Assert.Equal([second], read.OfLength(100));
        Assert.Empty(read.OfLength(5));

        // Recorded again - another import into that folder, or the same one reused - the folder is listed once, newest.
        ImportCatalogueEntry again = Entry("a", 2_048, 3);
        Assert.Equal([again, second], ImportCatalogue.Update(Catalogue, again, []).Entries);
        Assert.Equal([again, second], ImportCatalogue.Read(Catalogue).Entries);

        // Nothing staged is left beside it: the catalogue, and the lock writers take.
        Assert.Equal(
            [ImportCatalogue.FileName, ImportCatalogue.FileName + ".lock"],
            Directory.EnumerateFiles(Path.GetDirectoryName(Catalogue)!).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact(DisplayName = "§8.1: the folders said to hold an import no longer are forgotten, whichever way they are written; others stay")]
    public void FoldersNoLongerHoldingAnImportAreForgotten()
    {
        foreach (string name in new[] { "a", "b", "c", "d" })
        {
            _ = ImportCatalogue.Update(Catalogue, Entry(name, 4_096, name[0]), []);
        }

        ImportCatalogue forgotten = ImportCatalogue.Update(
            Catalogue, null, [Path.Combine(root, "b") + Path.DirectorySeparatorChar, Path.Combine(root, "d"), Path.Combine(root, "x")]);
        Assert.Equal([Path.Combine(root, "c"), Path.Combine(root, "a")], forgotten.Entries.Select(entry => entry.Path));

        // A folder recorded is listed even where it is also said to be gone: it holds the import now.
        ImportCatalogueEntry recorded = Entry("a", 4_096, 'z');
        Assert.Equal(
            [Path.Combine(root, "a"), Path.Combine(root, "c")],
            ImportCatalogue.Update(Catalogue, recorded, [Path.Combine(root, "a")]).Entries.Select(entry => entry.Path));

        // Folders are compared as the system compares them.
        IReadOnlyList<ImportCatalogueEntry> cased = ImportCatalogue.Update(Catalogue, null, [Path.Combine(root, "C")]).Entries;
        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, cased.Count);
    }

    [Fact(DisplayName = "§8.1: the catalogue keeps the newest thousand imports; an older one is imported again")]
    public void TheNewestThousandAreKept()
    {
        // A thousand recorded already, the oldest first in the file: it is read newest first whatever its order.
        WriteCatalogue(Enumerable.Range(0, ImportCatalogue.MaximumEntries).Select(index => Listed(Entry($"f{index}", index, index))));
        ImportCatalogue full = ImportCatalogue.Read(Catalogue);
        Assert.Equal(ImportCatalogue.MaximumEntries, full.Entries.Count);
        Assert.Equal(Path.Combine(root, "f999"), full.Entries[0].Path);
        Assert.Equal(Path.Combine(root, "f0"), full.Entries[^1].Path);

        IReadOnlyList<ImportCatalogueEntry> kept = ImportCatalogue.Update(Catalogue, Entry("newest", 7, 5_000), []).Entries;
        Assert.Equal(ImportCatalogue.MaximumEntries, kept.Count);
        Assert.Equal(Path.Combine(root, "newest"), kept[0].Path);
        Assert.Equal(Path.Combine(root, "f1"), kept[^1].Path);
        Assert.Equal(kept, ImportCatalogue.Read(Catalogue).Entries);
    }

    [Theory(DisplayName = "§8.1: a file that is not a catalogue of this contract lists nothing, says why, and is never replaced")]
    [InlineData("not json at all", "is not a catalogue of imports: ")]
    [InlineData("[]", "is not a catalogue of imports: ")]
    [InlineData("{}", "is not a catalogue of imports: it names no contract")]
    [InlineData("null", "is not a catalogue of imports: it names no contract")]
    [InlineData("{\"contract\":\"import-catalogue-v2\",\"imports\":[]}", "is a catalogue of import-catalogue-v2, which this InterCat does not read")]
    [InlineData("{\"contract\":\"import-catalogue-v1\",\"imports\":{}}", "is not a catalogue of imports: ")]
    [InlineData("", "is not a catalogue of imports: ")]
    public void WhatIsNotACatalogueIsLeftAsItIs(string content, string problem)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Catalogue)!);
        File.WriteAllText(Catalogue, content);
        ImportCatalogue read = ImportCatalogue.Read(Catalogue);
        Assert.Empty(read.Entries);
        Assert.StartsWith($"{Catalogue} {problem}", read.Problem, StringComparison.Ordinal);

        ImportCatalogue written = ImportCatalogue.Update(Catalogue, Entry("a", 1, 1), [Path.Combine(root, "b")]);
        Assert.Equal(read.Problem, written.Problem);
        Assert.Empty(written.Entries);
        Assert.Equal(content, File.ReadAllText(Catalogue));
    }

    [Fact(DisplayName = "§8.1: a file larger than a catalogue grows is not read as one, and is never replaced")]
    public void ALargerFileIsNotACatalogue()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Catalogue)!);
        using (FileStream stream = File.Create(Catalogue))
        {
            stream.SetLength(ImportCatalogue.MaximumBytes + 1);
        }

        ImportCatalogue read = ImportCatalogue.Read(Catalogue);
        Assert.Empty(read.Entries);
        Assert.Equal($"{Catalogue} is larger than a catalogue of imports grows, so it is not one", read.Problem);
        Assert.Equal(read.Problem, ImportCatalogue.Update(Catalogue, Entry("a", 1, 1), []).Problem);
        Assert.Equal(ImportCatalogue.MaximumBytes + 1, new FileInfo(Catalogue).Length);

        // A file of exactly the most it reads is read.
        WriteCatalogue([Listed(Entry("a", 1, 1))]);
        using (FileStream stream = File.OpenWrite(Catalogue))
        {
            stream.Seek(0, SeekOrigin.End);
            stream.Write(Encoding.UTF8.GetBytes(new string(' ', (int)(ImportCatalogue.MaximumBytes - stream.Length))));
        }

        Assert.Equal(ImportCatalogue.MaximumBytes, new FileInfo(Catalogue).Length);
        Assert.Equal([Path.Combine(root, "a")], ImportCatalogue.Read(Catalogue).Entries.Select(entry => entry.Path));
    }

    [Fact(DisplayName = "§8.1: an entry that does not say what it must is passed over, and dropped when the catalogue is written again")]
    public void EntriesNotSayingWhatTheyMustArePassedOver()
    {
        object valid = Listed(Entry("valid", 4_096, 1));
        WriteCatalogue(
        [
            valid,
            Listed(Entry("no-digest", 1, 2)) with { ImportDigest = "" },
            Listed(Entry("bad-session", 1, 3)) with { SessionId = "not-a-guid" },
            Listed(Entry("dashed-session", 1, 4)) with { SessionId = Guid.NewGuid().ToString("D") },
            Listed(Entry("relative", 1, 5)) with { Path = "relative" },
            Listed(Entry("no-path", 1, 6)) with { Path = null },
            Listed(Entry("no-source", 1, 7)) with { SourcePath = null },
            Listed(Entry("negative", 1, 8)) with { SourceLength = -1 },
            null,
        ]);
        Assert.Equal([Path.Combine(root, "valid")], ImportCatalogue.Read(Catalogue).Entries.Select(entry => entry.Path));
        Assert.Null(ImportCatalogue.Read(Catalogue).Problem);

        _ = ImportCatalogue.Update(Catalogue, null, []);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Catalogue));
        Assert.Equal(1, document.RootElement.GetProperty("imports").GetArrayLength());
    }

    [Fact(DisplayName = "§8.1: imports recording at once all stay listed, and a reader meanwhile meets a whole catalogue every time")]
    public async Task WritersRecordingAtOnceAllStayListed()
    {
        const int Writers = 24;
        using var stop = new CancellationTokenSource();
        var begun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> reading = Task.Run(() =>
        {
            int reads = 0;
            do
            {
                ImportCatalogue read = ImportCatalogue.Read(Catalogue);
                begun.TrySetResult();
                Assert.Null(read.Problem);
                reads++;
            }
            while (!stop.IsCancellationRequested);
            return reads;
        });

        // The writers begin once the reader has, and each reads the catalogue too while the others write it.
        await begun.Task;
        await Task.WhenAll(Enumerable.Range(0, Writers).Select(index => Task.Run(() =>
        {
            _ = ImportCatalogue.Update(Catalogue, Entry($"w{index}", index, index), []);
            Assert.Null(ImportCatalogue.Read(Catalogue).Problem);
        })));
        stop.Cancel();
        Assert.True(await reading >= 1);
        Assert.Equal(
            Enumerable.Range(0, Writers).Select(index => Path.Combine(root, $"w{index}")).Order(StringComparer.Ordinal),
            ImportCatalogue.Read(Catalogue).Entries.Select(entry => entry.Path).Order(StringComparer.Ordinal));
    }

    [Fact(DisplayName = "§8.1: a writer waits for the one holding the catalogue's lock, then records")]
    public async Task AWriterWaitsForTheLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Catalogue)!);
        Task<ImportCatalogue> writing;
        using (new FileStream(Catalogue + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            writing = Task.Run(() => ImportCatalogue.Update(Catalogue, Entry("waited", 1, 1), []));
            await Task.Delay(300);
            Assert.False(writing.IsCompleted);
            Assert.False(File.Exists(Catalogue));
        }

        Assert.Equal([Path.Combine(root, "waited")], (await writing).Entries.Select(entry => entry.Path));
    }

    [Fact(DisplayName = "§8.1: the catalogue is kept in the user's own InterCat folder")]
    public void TheCatalogueIsTheUsersOwn()
    {
        string userData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(
            string.IsNullOrWhiteSpace(userData) ? null : Path.Combine(userData, "InterCat", "import-catalogue-v1.json"),
            ImportCatalogue.DefaultPath());
    }

    private ImportCatalogueEntry Entry(string folder, long length, int minute) => new()
    {
        ImportDigest = "sha256:" + Convert.ToHexStringLower(Encoding.UTF8.GetBytes(folder.PadRight(32, '0')[..32])),
        SessionId = new Guid(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(folder))[..16]),
        Path = Path.Combine(root, folder),
        SourcePath = Path.Combine(root, folder + ".etl"),
        SourceLength = length,
        RecordedUtc = Recorded.AddMinutes(minute),
    };

    private static ListedEntry Listed(ImportCatalogueEntry entry) => new(
        entry.ImportDigest, entry.SessionId.ToString("N"), entry.Path, entry.SourcePath, entry.SourceLength, entry.RecordedUtc);

    private void WriteCatalogue(IEnumerable<object?> imports)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Catalogue)!);
        File.WriteAllText(
            Catalogue,
            JsonSerializer.Serialize(new { contract = "import-catalogue-v1", imports }, Web));
    }

    /// <summary>An entry as the file holds it, so a test can write one that does not say what it must.</summary>
    private sealed record ListedEntry(
        string? ImportDigest, string? SessionId, string? Path, string? SourcePath, long SourceLength, DateTimeOffset RecordedUtc);
}
