namespace InterCat.Storage;

/// <summary>What releasing a session's kept content would give up, measured before anything is written.</summary>
/// <param name="Generation">The generation measured.</param>
/// <param name="Chunks">The content chunks a release gives up, by name.</param>
/// <param name="Records">How many records' content the chunks that could be read hold.</param>
/// <param name="FileBytes">How many bytes the chunks take beside the journal.</param>
/// <param name="Finished">Whether the session's capture has finished, which a release needs.</param>
/// <param name="Unreadable">The chunks whose records could not be counted; a release gives them up too.</param>
public sealed record ContentReleasePreview(
    long Generation,
    IReadOnlyList<string> Chunks,
    long Records,
    long FileBytes,
    bool Finished,
    IReadOnlyList<string> Unreadable)
{
    /// <summary>Whether the session keeps any content to release.</summary>
    public bool ReleasesAnything => Chunks.Count > 0;
}

/// <summary>
/// Releases a session's kept content on its own (`contracts/content-v1.md` §2): every content chunk goes, with the
/// messages' bytes and each record's content facts, and every journal, row and derived file stays. Content is restricted
/// evidence, so a person may give it up and keep what the capture recorded about every message; the release is
/// published as a retention record that says what went and why, like any other.
/// </summary>
public static class ContentRetention
{
    /// <summary>Measures what a content release would give up. Nothing is written and nothing is published.</summary>
    public static ContentReleasePreview Preview(SessionStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        SessionManifestV1 manifest = store.Current
            ?? throw new InvalidOperationException("This session has published no generation to retain from.");
        StoreDependency[] chunks =
        [
            .. manifest.Dependencies
                .Where(dependency => dependency.Kind == StoreDependencyKind.Content)
                .OrderBy(dependency => dependency.Name, StringComparer.OrdinalIgnoreCase),
        ];
        long records = 0;
        var unreadable = new List<string>();
        foreach (StoreDependency chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using FileStream stream = store.Root.OpenOwnedFile(
                    chunk.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
                records += ContentChunkV1.ReadHeader(stream).Fragments;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                // A chunk that cannot be read is still evidence the generation names, and a release gives it up whole.
                unreadable.Add(chunk.Name);
            }
        }

        return new(
            manifest.Generation,
            [.. chunks.Select(chunk => chunk.Name)],
            records,
            chunks.Sum(chunk => chunk.LengthBytes),
            manifest.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.CaptureFinalization),
            unreadable);
    }

    /// <summary>
    /// Releases every content chunk of the current generation and publishes the retention generation, with the stated
    /// <paramref name="reason"/>. A session that keeps no content, or whose capture has not finished, is refused and
    /// nothing is published.
    /// </summary>
    public static RetentionOutcome Release(
        SessionStore store,
        string reason,
        DateTimeOffset committedUtc,
        DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ContentReleasePreview preview = Preview(store, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return store.ReleaseContent(preview.Generation, preview.Records, reason, committedUtc, nowUtc);
    }
}
