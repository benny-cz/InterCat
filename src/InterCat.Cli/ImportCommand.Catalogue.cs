using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// The catalogue of completed imports as icat import keeps it (contracts/import-v1.md §8.1): looked in before a file is
/// imported into a new folder, so a folder elsewhere that holds its import is reused, and written once an import is
/// published or reused. It is a hint: a catalogue that cannot be read or written is warned about and never stops an import.
/// </summary>
internal static partial class ImportCommand
{
    /// <summary>
    /// The folder the catalogue lists as holding this file's import, which verified, or null; and the folders it lists for
    /// this import that no longer hold it, which are forgotten when the catalogue is next written.
    /// </summary>
    private sealed record CatalogueLookup(ImportDestination? Reused, IReadOnlyList<string> Gone);

    /// <summary>The catalogue an import reads and records in, warning about it at most once.</summary>
    private sealed class CatalogueKeeping(string? path)
    {
        private bool warned;

        /// <summary>The catalogue's file; null where there is none to keep.</summary>
        public string? Path { get; } = path;

        /// <summary>
        /// Looks for this file's import in a folder other than <paramref name="into"/>, the new folder it was told to
        /// publish into. The file is hashed only when the catalogue lists an import of a file its length, and a listed
        /// folder is reused only once every file its generation names verified.
        /// </summary>
        public CatalogueLookup LookUp(string source, string into, Func<ImportIdentity> identityOf)
        {
            if (Path is null)
            {
                return new(null, []);
            }

            ImportCatalogue catalogue = ImportCatalogue.Read(Path);
            if (catalogue.Problem is { } problem)
            {
                Warn(problem);
                return new(null, []);
            }

            IReadOnlyList<ImportCatalogueEntry> sameLength = catalogue.OfLength(new FileInfo(source).Length);
            if (sameLength.Count == 0)
            {
                return new(null, []);
            }

            ConsoleUi.Progress(
                $"Hashing {source}: the catalogue of imports {Path} lists {CountText.Of(sameLength.Count, "import")} of a "
                + "file its length.");
            ImportIdentity identity = identityOf();
            var gone = new List<string>();
            foreach (ImportCatalogueEntry entry in sameLength.Where(entry =>
                entry.ImportDigest == identity.Digest && entry.SessionId == identity.CaptureId.Value))
            {
                // The folder this import was told to publish into is new, so the session once there is gone.
                if (ImportCatalogue.PathComparer.Equals(entry.Path, into))
                {
                    gone.Add(entry.Path);
                    continue;
                }

                ConsoleUi.Progress($"Verifying {entry.Path}, which the catalogue of imports lists as holding this file's import.");
                ImportDestination listed = ImportDestination.Inspect(entry.Path, () => identity.CaptureId.Value);
                switch (listed.Kind)
                {
                    case ImportDestinationKind.ThisImport:
                        return new(listed, gone);
                    case ImportDestinationKind.Unreadable:
                        // It may yet be recovered, so it stays listed.
                        ConsoleUi.Warn(
                            $"The catalogue of imports lists {entry.Path} as holding this file's import, but it cannot be "
                            + $"reused. {listed.Reason} icat recover \"{entry.Path}\" reviews it.");
                        break;
                    default:
                        // Removed, emptied, or holding something else now: whatever it holds, it is not this import.
                        gone.Add(entry.Path);
                        break;
                }
            }

            return new(null, gone);
        }

        /// <summary>
        /// Records that <paramref name="folder"/> holds the import of <paramref name="identity"/>, forgetting the folders
        /// of <paramref name="gone"/>. A catalogue that cannot be read or written is warned about, and the import stands.
        /// </summary>
        public void Record(ImportIdentity identity, string folder, string source, IReadOnlyList<string> gone)
        {
            if (Path is null)
            {
                return;
            }

            try
            {
                ImportCatalogue written = ImportCatalogue.Update(
                    Path,
                    new()
                    {
                        ImportDigest = identity.Digest,
                        SessionId = identity.CaptureId.Value,
                        Path = folder,
                        SourcePath = source,
                        SourceLength = identity.Source.Length,
                        RecordedUtc = DateTimeOffset.UtcNow,
                    },
                    gone);
                if (written.Problem is { } problem)
                {
                    Warn(problem);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ConsoleUi.Warn(
                    $"{folder} could not be recorded in the catalogue of imports {Path}: {exception.Message} An import of "
                    + "this file into another folder will read the file again rather than reuse this one.");
            }
        }

        /// <summary>Says once why the catalogue is not read, and that it is left as it is.</summary>
        private void Warn(string problem)
        {
            if (warned)
            {
                return;
            }

            warned = true;
            ConsoleUi.Warn(
                $"The catalogue of imports is not read: {problem.TrimEnd('.')}. It is left as it is, so no other folder is looked in "
                + "for this file's import and none is recorded in it; remove it, or pass --catalogue <file>, to keep a "
                + "new one.");
        }
    }
}
