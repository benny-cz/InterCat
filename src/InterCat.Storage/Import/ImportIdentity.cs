using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace InterCat.Storage;

/// <summary>
/// What kind of evidence an import was given. The kind decides how records are identified, not just how
/// they are read: an InterCat journal carries reproducible acquisition ordinals, and a standalone ETL
/// does not.
/// </summary>
public enum ImportSourceKind
{
    /// <summary>An InterCat journal. Its record identities are preserved, never recomputed (§18.4).</summary>
    JournalV1 = 1,

    /// <summary>
    /// A diagnostic ETL recorded outside InterCat. Callback delivery order is not reproducible, so its
    /// records are identified by canonical key and counted by multiplicity.
    /// </summary>
    StandaloneEtl = 2,
}

/// <summary>
/// What an import kept of the original evidence. It is part of import identity: importing metadata only
/// and importing original contents are different derived sessions over the same bytes, and presenting
/// one as the other would claim evidence that was never retained (§18.4).
/// </summary>
public enum RetainedEvidencePolicy
{
    MetadataOnly = 1,
    ApprovedMetadataAndContent = 2,
}

/// <summary>
/// The identity of the bytes an import was given, independent of what was done with them. It is computed
/// before any persistent identity is assigned, so a repeated import of the same file is recognised as
/// the same source rather than re-derived.
/// </summary>
public readonly record struct ImportSourceIdentity
{
    private ImportSourceIdentity(ImportSourceKind kind, long length, string contentDigest)
    {
        Kind = kind;
        Length = length;
        ContentDigest = contentDigest;
    }

    public ImportSourceKind Kind { get; }

    public long Length { get; }

    /// <summary>`sha256:` and 64 lowercase hexadecimal characters over the source bytes.</summary>
    public string ContentDigest { get; }

    public static ImportSourceIdentity Of(ImportSourceKind kind, ReadOnlySpan<byte> content)
    {
        RequireKind(kind);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(content, digest);
        return new(kind, content.Length, CanonicalImportContract.Render(digest));
    }

    /// <summary>
    /// Hashes a source from a stream, so a multi-gigabyte ETL is identified without being held in memory.
    /// </summary>
    public static ImportSourceIdentity Of(ImportSourceKind kind, Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        RequireKind(kind);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long length = 0;
        int read;
        while ((read = content.Read(buffer)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            length += read;
        }

        return new(kind, length, CanonicalImportContract.Render(hash.GetHashAndReset()));
    }

    public static ImportSourceIdentity OfFile(ImportSourceKind kind, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(Path.GetFullPath(path));
        return Of(kind, stream);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Kind}:{Length}:{ContentDigest}");

    private static void RequireKind(ImportSourceKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "An import source is a journal or a standalone ETL; an unnamed kind is refused.");
        }
    }
}

/// <summary>
/// The identity of one derived import: which bytes, read under which import contract, keeping which
/// evidence, normalized by which contract version. Changing any of them produces a different import
/// rather than silently reusing an existing one.
/// </summary>
public sealed record ImportIdentity
{
    private ImportIdentity(
        ImportSourceIdentity source,
        RetainedEvidencePolicy retainedEvidence,
        uint importContractVersion,
        uint normalizerContractVersion,
        string digest)
    {
        Source = source;
        RetainedEvidence = retainedEvidence;
        ImportContractVersion = importContractVersion;
        NormalizerContractVersion = normalizerContractVersion;
        Digest = digest;
    }

    public ImportSourceIdentity Source { get; }

    public RetainedEvidencePolicy RetainedEvidence { get; }

    public uint ImportContractVersion { get; }

    public uint NormalizerContractVersion { get; }

    /// <summary>`sha256:` and 64 lowercase hexadecimal characters over the canonical identity inputs.</summary>
    public string Digest { get; }

    public static ImportIdentity Create(
        ImportSourceIdentity source,
        RetainedEvidencePolicy retainedEvidence,
        uint normalizerContractVersion = 1,
        uint importContractVersion = CanonicalImportContract.Version)
    {
        if (!Enum.IsDefined(retainedEvidence))
        {
            throw new ArgumentOutOfRangeException(
                nameof(retainedEvidence),
                retainedEvidence,
                "An import declares which evidence it retained; an unnamed policy is refused.");
        }

        ArgumentOutOfRangeException.ThrowIfZero(normalizerContractVersion);
        ArgumentOutOfRangeException.ThrowIfZero(importContractVersion);
        if (string.IsNullOrEmpty(source.ContentDigest))
        {
            throw new ArgumentException("An import identity needs a hashed source.", nameof(source));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(CanonicalImportContract.IdentityDomain));
        Span<byte> numbers = stackalloc byte[4];
        Append(hash, numbers, importContractVersion);
        Append(hash, numbers, (uint)source.Kind);
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(length, source.Length);
        hash.AppendData(length);
        hash.AppendData(Encoding.UTF8.GetBytes(source.ContentDigest));
        Append(hash, numbers, (uint)retainedEvidence);
        Append(hash, numbers, normalizerContractVersion);
        return new(
            source,
            retainedEvidence,
            importContractVersion,
            normalizerContractVersion,
            CanonicalImportContract.Render(hash.GetHashAndReset()));
    }

    public override string ToString() => Digest;

    private static void Append(IncrementalHash hash, Span<byte> scratch, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(scratch, value);
        hash.AppendData(scratch);
    }
}

/// <summary>The frozen names and versions of the canonical import contract (`contracts/import-v1.md`).</summary>
public static class CanonicalImportContract
{
    public const uint Version = 1;

    public const string IdentityDomain = "InterCat.Import.Identity.v1";

    public const string RecordKeyDomain = "InterCat.Import.CanonicalRecordKey.v1";

    internal static string Render(ReadOnlySpan<byte> digest) =>
        string.Concat("sha256:", Convert.ToHexStringLower(digest));
}
