using System.Buffers.Binary;
using System.Text;

namespace InterCat.Application;

/// <summary>
/// Looks for source values, byte for byte, in the files of a redacted session package before it is published. It is
/// the second line of defence: every text value and reference the package's formats can hold is already checked against
/// the pseudonyms that were issued, and this scan finds a source value anywhere else - a reserved field, a stale buffer,
/// a file nobody expected.
/// </summary>
/// <remarks>
/// A needle is scanned only when it cannot occur in text the package generates itself, so a match is a leak and never a
/// coincidence with a pseudonym. Generated text uses lowercase letters, digits and <c>- . | \ /</c> only; a needle with
/// any other byte (a capital, a space, a colon, a NUL of UTF-16, anything non-ASCII) qualifies, and so does a 32- or
/// 36-character identifier or a digest, which no pseudonym is long enough to contain. Everything shorter than
/// <see cref="MinimumLength"/> bytes is left to the structural checks, because a short needle meets arbitrary binary data.
/// </remarks>
internal sealed class RedactedPackageLeakScanner
{
    public const int MinimumLength = 6;
    private const int ChunkBytes = 1 << 20;
    private const int FilterBits = 1 << 22;

    private readonly Dictionary<ulong, List<(byte[] Bytes, string What)>> byPrefix = [];
    private readonly ulong[] filter = new ulong[FilterBits / 64];
    private int longest = MinimumLength;

    public int Count { get; private set; }

    /// <summary>An identifier in every form a file could carry it: big- and little-endian bytes, and its text forms.</summary>
    public void AddIdentifier(Guid identifier, string what)
    {
        if (identifier == Guid.Empty) return;
        Span<byte> bytes = stackalloc byte[16];
        _ = identifier.TryWriteBytes(bytes, bigEndian: true, out _);
        AddBytes(bytes, what);
        _ = identifier.TryWriteBytes(bytes, bigEndian: false, out _);
        AddBytes(bytes, what);
        foreach (string format in new[] { "N", "D" })
        {
            string text = identifier.ToString(format);
            AddBytes(Encoding.UTF8.GetBytes(text), what);
            AddBytes(Encoding.UTF8.GetBytes(text.ToUpperInvariant()), what);
        }
    }

    /// <summary>
    /// A source text value, as UTF-8 and UTF-16LE, when it is distinctive enough to be found without meeting a pseudonym.
    /// </summary>
    public void AddText(string text, string what)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        if (IsDistinctive(utf8) && !IsInFormatMagic(utf8)) AddBytes(utf8, what);

        // UTF-16 carries a NUL beside every ASCII byte, which generated text never does. Six characters keep a chance
        // match against binary columns out of reach.
        if (text.Length >= 6) AddBytes(Encoding.Unicode.GetBytes(text), what);
    }

    /// <summary>The magic strings the package's binary formats begin with; a needle inside one is not a leak.</summary>
    private static bool IsInFormatMagic(ReadOnlySpan<byte> needle) =>
        "ICATSEG1"u8.IndexOf(needle) >= 0 || "ICATDIC1"u8.IndexOf(needle) >= 0 || "ICJ1"u8.IndexOf(needle) >= 0;

    /// <summary>
    /// A `sha256:` digest: its 64 hexadecimal characters in either case. The prefix is left out, because it is also how
    /// the package names its own files' digests.
    /// </summary>
    public void AddDigest(string digest, string what)
    {
        ArgumentNullException.ThrowIfNull(digest);
        string hex = digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..] : digest;
        if (hex.Length < 32) return;
        AddBytes(Encoding.UTF8.GetBytes(hex.ToLowerInvariant()), what);
        AddBytes(Encoding.UTF8.GetBytes(hex.ToUpperInvariant()), what);
    }

    /// <summary>Whether generated text could contain these bytes: only when every byte is in its alphabet.</summary>
    internal static bool IsDistinctive(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 32 && IsHexLike(bytes) || bytes.IndexOfAnyExcept(GeneratedAlphabet) >= 0;

    private static readonly System.Buffers.SearchValues<byte> GeneratedAlphabet = System.Buffers.SearchValues.Create(
        "abcdefghijklmnopqrstuvwxyz0123456789-.|\\/"u8);

    private static readonly System.Buffers.SearchValues<byte> HexAlphabet = System.Buffers.SearchValues.Create(
        "0123456789abcdefABCDEF-"u8);

    private static bool IsHexLike(ReadOnlySpan<byte> bytes) => bytes.IndexOfAnyExcept(HexAlphabet) < 0;

    private void AddBytes(ReadOnlySpan<byte> needle, string what)
    {
        if (needle.Length < MinimumLength) return;
        ulong key = Prefix(needle);
        if (!byPrefix.TryGetValue(key, out List<(byte[] Bytes, string What)>? bucket))
        {
            bucket = [];
            byPrefix[key] = bucket;
        }

        foreach ((byte[] existing, _) in bucket)
        {
            if (needle.SequenceEqual(existing)) return;
        }

        bucket.Add((needle.ToArray(), what));
        int bit = FilterBit(key);
        filter[bit >> 6] |= 1UL << (bit & 63);
        longest = Math.Max(longest, needle.Length);
        Count++;
    }

    /// <summary>What the first needle found in the stream is, or null when none is. The stream is read once, in chunks.</summary>
    public string? Find(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (Count == 0) return null;
        int overlap = longest - 1;
        byte[] buffer = new byte[ChunkBytes + overlap];
        int carried = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.ReadAtLeast(buffer.AsSpan(carried), buffer.Length - carried, throwOnEndOfStream: false);
            int filled = carried + read;
            bool end = carried + read < buffer.Length;

            // A needle starting before `limit` lies wholly inside what has been read; later starts wait for the next chunk.
            int limit = end ? filled - MinimumLength + 1 : filled - overlap;
            ReadOnlySpan<byte> data = buffer.AsSpan(0, filled);
            for (int at = 0; at < limit; at++)
            {
                ulong key = Prefix(data[at..]);
                int bit = FilterBit(key);
                if ((filter[bit >> 6] & (1UL << (bit & 63))) == 0 || !byPrefix.TryGetValue(key, out var bucket))
                {
                    continue;
                }

                foreach ((byte[] needle, string what) in bucket)
                {
                    if (at + needle.Length <= filled && data.Slice(at, needle.Length).SequenceEqual(needle))
                    {
                        return what;
                    }
                }
            }

            if (end) return null;
            carried = filled - limit;
            buffer.AsSpan(limit, carried).CopyTo(buffer);
        }
    }

    private static ulong Prefix(ReadOnlySpan<byte> bytes) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes) | ((ulong)BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) << 32);

    private static int FilterBit(ulong key) => (int)((key * 0x9E37_79B9_7F4A_7C15UL) >> 42);
}
