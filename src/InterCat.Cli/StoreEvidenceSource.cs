using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using InterCat.Remote;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// A capture's evidence in its host's broker root as the remote agent serves it (`remote-agent-v1` §4.5): opened as the
/// session store it is, to view - the pointer, the manifest's digest and every file's presence and length checked, no file
/// hashed - and each file listed with the SHA-256 its generation records, which the controller checks what it fetched
/// against. The manifest's own file is hashed as it is listed. Opening is read-only, and so is every read.
/// </summary>
internal sealed class StoreEvidenceSource : IRemoteEvidenceSource
{
    private const string DigestPrefix = "sha256:";
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdef");

    public RemoteEvidenceSnapshot List(string directory)
    {
        LocalOwnedDirectory owned = LocalOwnedDirectory.Open(directory);
        SessionStore store = SessionStore.OpenForViewing(owned);
        SessionManifestV1 manifest = store.Current ?? throw new InvalidDataException("The capture published nothing.");
        if (store.Recovery.RolledBackToLastKnownGood)
        {
            // What the evidence serves is the generation its pointer names, never a fallback the pointer does not name.
            throw new InvalidDataException("The capture's newest generation does not verify: " + store.Recovery.RollbackReason);
        }

        string manifestName = SessionManifestV1.FileNameFor(manifest.Generation);
        byte[] manifestBytes;
        using (FileStream stream = owned.OpenOwnedFile(manifestName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan))
        {
            manifestBytes = new byte[stream.Length];
            stream.ReadExactly(manifestBytes);
        }

        var files = new List<RemoteEvidenceFile> { new(manifestName, manifestBytes.Length, SHA256.HashData(manifestBytes)) };
        files.AddRange(manifest.Dependencies.Select(dependency => new RemoteEvidenceFile(dependency.Name, dependency.LengthBytes, Digest(dependency))));
        return new RemoteEvidenceSnapshot(manifest.Generation,
            JsonSerializer.SerializeToUtf8Bytes(SessionPointerV1.For(manifest), SessionManifestV1.Json), files);
    }

    public IRemoteEvidenceReader Open(string directory, string name) =>
        new OpenFile(LocalOwnedDirectory.Open(directory)
            .OpenOwnedFile(name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess));

    /// <summary>A file of the evidence, open read-only for a fetch's reads, each at its own offset.</summary>
    private sealed class OpenFile(FileStream stream) : IRemoteEvidenceReader
    {
        public int Read(long offset, Span<byte> into)
        {
            int read = 0;
            while (read < into.Length)
            {
                int more = RandomAccess.Read(stream.SafeFileHandle, into[read..], offset + read);
                if (more == 0)
                {
                    break;
                }

                read += more;
            }

            return read;
        }

        public void Dispose() => stream.Dispose();
    }

    private static byte[] Digest(StoreDependency dependency)
    {
        string digest = dependency.Digest;
        return digest.StartsWith(DigestPrefix, StringComparison.Ordinal) && digest.Length == DigestPrefix.Length + 64
            && digest.AsSpan(DigestPrefix.Length).ContainsAnyExcept(HexDigits) is false
                ? Convert.FromHexString(digest.AsSpan(DigestPrefix.Length))
                : throw new InvalidDataException($"'{dependency.Name}' records no SHA-256 of its bytes.");
    }
}
