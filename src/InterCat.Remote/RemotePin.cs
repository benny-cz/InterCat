using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace InterCat.Remote;

/// <summary>
/// How a remote agent and a controller know each other (`remote-agent-v1` §1): the SHA-256 of a party's public key, its
/// certificate's DER SubjectPublicKeyInfo, written as 64 lowercase hexadecimal digits. A certificate's chain, names and
/// dates are not what is trusted; a pin is.
/// </summary>
public readonly record struct RemotePin
{
    /// <summary>The bytes of a pin.</summary>
    public const int Length = 32;

    private RemotePin(string hex) => Hex = hex;

    /// <summary>The pin as 64 lowercase hexadecimal digits.</summary>
    public string Hex { get; }

    /// <summary>The pin of <paramref name="certificate"/>'s public key.</summary>
    public static RemotePin Of(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (certificate is X509Certificate2 full)
        {
            return Of(full.PublicKey.ExportSubjectPublicKeyInfo());
        }

        using X509Certificate2 loaded = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return Of(loaded.PublicKey.ExportSubjectPublicKeyInfo());
    }

    /// <summary>The pin of a DER SubjectPublicKeyInfo.</summary>
    public static RemotePin Of(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        new(Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo)));

    /// <summary>A pin from its 32 bytes.</summary>
    public static RemotePin FromBytes(ReadOnlySpan<byte> bytes) =>
        bytes.Length == Length
            ? new(Convert.ToHexStringLower(bytes))
            : throw new ArgumentException($"A pin is {Length} bytes.", nameof(bytes));

    /// <summary>Reads a pin written as 64 hexadecimal digits, either case.</summary>
    public static bool TryParse(string? text, out RemotePin pin)
    {
        pin = default;
        if (text is not { Length: Length * 2 } || !text.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        pin = new(text.ToLowerInvariant());
        return true;
    }

    /// <summary>The pin's 32 bytes.</summary>
    public byte[] ToBytes() => Convert.FromHexString(Hex ?? throw new InvalidOperationException("The pin is empty."));

    /// <summary>Whether this pin was ever set: the default pin names no key.</summary>
    public bool IsEmpty => Hex is null;

    /// <summary>The first sixteen digits, enough for a person to tell two parties apart in a listing.</summary>
    public string Abbreviated => IsEmpty ? string.Empty : Hex[..16];

    public override string ToString() => Hex ?? string.Empty;
}
