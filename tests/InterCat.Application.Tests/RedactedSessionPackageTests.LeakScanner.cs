using System.Text;
using Xunit;

namespace InterCat.Application.Tests;

/// <summary>
/// A redacted package's last line of defence (P17): before it is published, every file is searched, byte for byte, for
/// its source's identities, names and digests in every form a file could carry them. No package the tests build leaks
/// one, so these show the search itself finds a value wherever it lies - across the chunks it reads in, too - and that
/// text of the alphabet the package generates never meets one.
/// </summary>
public sealed partial class RedactedSessionPackageTests
{
    /// <summary>The bytes the scan reads at a time; a value can straddle the end of one read and the start of the next.</summary>
    private const int ScanChunk = 1 << 20;

    private static readonly byte[] GeneratedAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789-.|\\/"u8.ToArray();

    [Theory(DisplayName = "P17: a source's identity, name or digest is found wherever a package file holds it, across the scan's chunks too, and generated text never is")]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(20_261_031)]
    public void ALeakIsFoundWhereverItLies(int seed)
    {
        var random = new Random(seed);
        byte[] guid = new byte[16];
        random.NextBytes(guid);
        var identity = new Guid(guid);
        string digest = Convert.ToHexStringLower(SHA(random));
        var scanner = new RedactedPackageLeakScanner();
        scanner.AddIdentifier(identity, "the session's identity");
        scanner.AddText(@"C:\Users\Alice\Tools\leaky-tool.exe", "an image path");
        scanner.AddText("leaky-tool.exe", "an image name");
        scanner.AddDigest("sha256:" + digest, "a digest");

        // Every form a file could carry each value in: an identity's bytes either way round and its text in either case,
        // a name as UTF-8 where a capital or a colon sets it apart from generated text and as UTF-16 always, and a
        // digest's hexadecimal in either case.
        Span<byte> bytes = stackalloc byte[16];
        _ = identity.TryWriteBytes(bytes, bigEndian: true, out _);
        byte[] bigEndian = bytes.ToArray();
        _ = identity.TryWriteBytes(bytes, bigEndian: false, out _);
        byte[] littleEndian = bytes.ToArray();
        (byte[] Needle, string What)[] forms =
        [
            (bigEndian, "the session's identity"), (littleEndian, "the session's identity"),
            (Encoding.UTF8.GetBytes(identity.ToString("N")), "the session's identity"),
            (Encoding.UTF8.GetBytes(identity.ToString("D").ToUpperInvariant()), "the session's identity"),
            (Encoding.UTF8.GetBytes(@"C:\Users\Alice\Tools\leaky-tool.exe"), "an image path"),
            (Encoding.Unicode.GetBytes(@"C:\Users\Alice\Tools\leaky-tool.exe"), "an image path"),
            (Encoding.Unicode.GetBytes("leaky-tool.exe"), "an image name"),
            (Encoding.UTF8.GetBytes(digest), "a digest"), (Encoding.UTF8.GetBytes(digest.ToUpperInvariant()), "a digest"),
        ];

        // Text of the package's own alphabet, a little longer than one chunk, holds none of them.
        byte[] generated = new byte[ScanChunk + 4_096];
        for (int at = 0; at < generated.Length; at++)
        {
            generated[at] = GeneratedAlphabet[random.Next(GeneratedAlphabet.Length)];
        }

        Assert.Null(scanner.Find(new MemoryStream(generated), CancellationToken.None));

        // Each is found at the start, at the end, at random, and about the end of the first read: ending on its last byte,
        // straddling it, beginning on it, and beginning the next read.
        foreach ((byte[] needle, string what) in forms)
        {
            int[] places =
            [
                0, generated.Length - needle.Length, random.Next(generated.Length - needle.Length),
                ScanChunk - needle.Length, ScanChunk - (needle.Length / 2), ScanChunk - 1, ScanChunk,
            ];
            foreach (int at in places)
            {
                byte[] file = (byte[])generated.Clone();
                needle.CopyTo(file, at);
                Assert.True(scanner.Find(new MemoryStream(file), CancellationToken.None) == what,
                    $"{what} ({needle.Length} bytes) planted at byte {at} was not found.");
            }
        }

        // A name of only the generated alphabet is no leak the search can tell from a pseudonym in UTF-8, and is left to
        // the checks of every value the formats hold; its UTF-16 form, which generated text never takes, is found.
        byte[] plain = (byte[])generated.Clone();
        Encoding.UTF8.GetBytes("leaky-tool.exe").CopyTo(plain, 1_000);
        Assert.Null(scanner.Find(new MemoryStream(plain), CancellationToken.None));

        static byte[] SHA(Random random)
        {
            byte[] value = new byte[32];
            random.NextBytes(value);
            return value;
        }
    }
}
