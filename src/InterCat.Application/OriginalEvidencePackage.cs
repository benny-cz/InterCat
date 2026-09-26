using System.Security.Cryptography;
using System.Text.Json;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>Where an original evidence package is being made. Each stage reads every byte once.</summary>
public enum OriginalPackageStage
{
    Copying = 1,
    Verifying = 2,
}

/// <summary>How far one stage is: bytes done of the bytes it reads.</summary>
public sealed record OriginalPackageProgress(OriginalPackageStage Stage, long Done, long Total);

/// <summary>One file an original evidence package carries, as the generation it copies names it.</summary>
public sealed record OriginalEvidenceFile(string Name, StoreDependencyKind Kind, long LengthBytes);

/// <summary>
/// What an original evidence package of a session holds, measured without writing anything: the generation it copies,
/// every file of it, and the identities those files carry. It is what a user is shown before anything is saved (§11.3).
/// </summary>
/// <param name="HostId">The capture's host identity, as its journal records it; null when no journal names one.</param>
/// <param name="Redacted">Whether the session is itself a redacted package, whose copy is still pseudonymized.</param>
public sealed record OriginalEvidencePackagePreview(
    Guid SessionId,
    long Generation,
    DateTimeOffset CommittedUtc,
    IReadOnlyList<OriginalEvidenceFile> Files,
    long Rows,
    long SourceFieldRows,
    Guid? HostId,
    bool Redacted)
{
    /// <summary>Every byte the package holds besides its manifest and pointer.</summary>
    public long Bytes => Files.Sum(file => file.LengthBytes);

    /// <summary>The admitted journal chunks: every record as it was admitted, the raw evidence.</summary>
    public IReadOnlyList<OriginalEvidenceFile> Journals =>
        [.. Files.Where(file => file.Kind == StoreDependencyKind.Journal)];

    public bool CoverageLedger => Files.Any(file => file.Kind == StoreDependencyKind.CoverageLedger);
}

/// <summary>A published, verified original evidence package: where it is, and what its verification covered.</summary>
public sealed record OriginalEvidencePackageResult(
    string Directory,
    OriginalEvidencePackagePreview Source,
    int FilesVerified,
    long BytesVerified);

/// <summary>
/// §11.3's third sharing preset, the explicitly unredacted one: an exact copy of a session's current generation that
/// reopens as the same session in every InterCat reader (`contracts/original-evidence-package-v1.md`).
/// </summary>
/// <remarks>
/// The package holds every file the generation names, byte for byte, its manifest, a pointer naming it, and the evidence
/// lease guard a reader takes. It holds nothing else: no earlier generation, no superseded manifest, no orphan or staging
/// file, and no file the session was imported from. It is built in a private directory beside the destination, each file
/// checked against the digest its generation recorded as it is copied, then reopened and hashed again as a recipient
/// would, and only then renamed into place.
/// </remarks>
public static class OriginalEvidencePackage
{
    public const string Contract = "intercat-original-evidence-package-v1";

    public const string Warning = "Unredacted. This is the session as it was recorded, and anyone who opens it sees "
        + "everything InterCat saw. Share it only with someone who may see all of it.";

    /// <summary>What the files hold, stated before anything is saved (§11.3).</summary>
    public const string Contents = "Process and executable names and image paths; process, thread and session IDs; "
        + "parent links and start and exit times; local and remote addresses and ports; resource names, kernel object "
        + "values and RPC procedures; timestamps as recorded, with the capture's clock and host identities; and every "
        + "admitted record in the journal. InterCat records metadata only, so no message content is included.";

    private const int CopyBufferBytes = 1024 * 1024;

    /// <summary>Measures what a package of this session would hold, reading headers and the manifest only.</summary>
    public static OriginalEvidencePackagePreview Preview(SessionStore source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using EvidenceLease lease = source.AcquireLease();
        return Describe(source.Root, lease.Manifest);
    }

    /// <summary>
    /// Builds, verifies and publishes a package at <paramref name="destination"/>, which must not exist and must not
    /// overlap the source. Nothing appears under that name unless the complete package verified.
    /// </summary>
    public static OriginalEvidencePackageResult Create(
        SessionStore source,
        string destination,
        IProgress<OriginalPackageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        string sourcePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Root.Path));
        StringComparison paths = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (target.Equals(sourcePath, paths)
            || target.StartsWith(sourcePath + Path.DirectorySeparatorChar, paths)
            || sourcePath.StartsWith(target + Path.DirectorySeparatorChar, paths))
        {
            throw new ArgumentException(
                "An original evidence package is written to its own new directory, outside the session it copies.",
                nameof(destination));
        }

        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException($"{target} already exists. A package is written only to a new directory.");
        }

        string parent = Path.GetDirectoryName(target)
            ?? throw new ArgumentException("The destination has no parent directory.", nameof(destination));

        // The lease keeps every file of the generation where it is while it is copied, whatever retention does meanwhile.
        using EvidenceLease lease = source.AcquireLease(new EvidenceLeaseRequest { Duration = TimeSpan.FromHours(4) });
        SessionManifestV1 manifest = lease.Manifest;
        OriginalEvidencePackagePreview preview = Describe(source.Root, manifest);
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, $"{Path.GetFileName(target)}.partial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            long total = preview.Bytes;
            long copied = 0;
            byte[] buffer = new byte[CopyBufferBytes];
            foreach (StoreDependency dependency in manifest.Dependencies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                copied = Copy(source.Root, staging, dependency, buffer, copied, total, progress, cancellationToken);
            }

            // The manifest is copied as it is, and the pointer names it as the source's did; there is no last-known-good
            // generation to point back to, because the package holds one generation.
            string manifestName = SessionManifestV1.FileNameFor(manifest.Generation);
            File.WriteAllBytes(Path.Combine(staging, manifestName), ReadOwned(source.Root, manifestName));
            File.WriteAllText(
                Path.Combine(staging, SessionPointerV1.FileName),
                JsonSerializer.Serialize(SessionPointerV1.For(manifest), SessionManifestV1.Json));
            File.WriteAllBytes(Path.Combine(staging, SessionStore.EvidenceLeaseLockFileName), []);

            (int files, long bytes) = Verify(staging, manifest, total, progress);
            Directory.Move(staging, target);
            return new(target, preview, files, bytes);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    private static OriginalEvidencePackagePreview Describe(IOwnedDirectory root, SessionManifestV1 manifest)
    {
        long rows = SessionSegments.Names(manifest).Sum(name => (long)SessionSegments.DeclaredRowCount(root, manifest, name));
        long fields = SessionSegments.FieldNames(manifest)
            .Sum(name => (long)SessionSegments.DeclaredRowCount(root, manifest, name));
        return new(
            manifest.SessionId,
            manifest.Generation,
            manifest.CommittedUtc,
            [.. manifest.Dependencies.Select(dependency => new OriginalEvidenceFile(dependency.Name, dependency.Kind, dependency.LengthBytes))],
            rows,
            fields,
            SessionSegments.SourceClock(root, manifest)?.HostId.Value,
            manifest.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.RedactionPolicy));
    }

    /// <summary>
    /// Copies one file, hashing what it copies: a file whose bytes are not the ones its generation recorded is refused
    /// here, before any package can hold it.
    /// </summary>
    private static long Copy(
        IOwnedDirectory root,
        string staging,
        StoreDependency dependency,
        byte[] buffer,
        long copied,
        long total,
        IProgress<OriginalPackageProgress>? progress,
        CancellationToken cancellationToken)
    {
        using FileStream input = root.OpenOwnedFile(
            dependency.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        if (input.Length != dependency.LengthBytes)
        {
            throw new InvalidDataException(
                $"'{dependency.Name}' is {input.Length} bytes where its generation recorded {dependency.LengthBytes}.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var output = new FileStream(
            Path.Combine(staging, dependency.Name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.None))
        {
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
                copied += read;
                progress?.Report(new(OriginalPackageStage.Copying, copied, total));
            }

            output.Flush(flushToDisk: true);
        }

        string digest = string.Concat("sha256:", Convert.ToHexStringLower(hash.GetHashAndReset()));
        return string.Equals(digest, dependency.Digest, StringComparison.Ordinal)
            ? copied
            : throw new InvalidDataException(
                $"'{dependency.Name}' computes {digest} where its generation recorded {dependency.Digest}, so it is not "
                + "copied into a package.");
    }

    /// <summary>
    /// Reopens the package as a recipient would: the pointer, the manifest's digest and every file's length and digest,
    /// hashed by a fresh store. It must be the very generation copied, with nothing rolled back and nothing unreferenced.
    /// </summary>
    private static (int Files, long Bytes) Verify(
        string staging,
        SessionManifestV1 manifest,
        long total,
        IProgress<OriginalPackageProgress>? progress)
    {
        progress?.Report(new(OriginalPackageStage.Verifying, 0, total));
        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(staging));
        string? problem = reopened.Recovery.RolledBackToLastKnownGood
            ? $"it rolled back: {reopened.Recovery.RollbackReason}"
            : reopened.Current is not { } current || current.Generation != manifest.Generation
                || !string.Equals(current.Digest, manifest.Digest, StringComparison.Ordinal)
                ? "it does not open at the generation it copies"
                : reopened.Recovery.OrphanFiles.Count > 0
                    ? $"it holds files its generation does not name: {string.Join(", ", reopened.Recovery.OrphanFiles)}"
                    : null;
        if (problem is not null)
        {
            throw new InvalidDataException($"The package did not verify when it was reopened: {problem}.");
        }

        progress?.Report(new(OriginalPackageStage.Verifying, total, total));
        return (manifest.Dependencies.Count + 1, total);
    }

    private static byte[] ReadOwned(IOwnedDirectory root, string name)
    {
        using FileStream stream = root.OpenOwnedFile(name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None);
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void TryDelete(string staging)
    {
        try
        {
            Directory.Delete(staging, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A package that did not verify never appears under its name; a leftover private directory beside it is
            // named as partial and holds nothing the source does not.
        }
    }
}
