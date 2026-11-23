using System.Buffers.Text;
using System.Security.Cryptography;

namespace InterCat.Remote;

/// <summary>
/// What an agent's administrator gives a controller to enroll it once (`remote-agent-v1` §2): the agent's pin and a
/// one-time secret. The controller checks the agent's key against the pin before it sends the secret, so a party between
/// them never learns it; the agent keeps only the secret's hash, consumes it on the first enrollment it admits, and lets it
/// lapse after its lifetime.
/// </summary>
public sealed class EnrollmentToken
{
    /// <summary>The token's text begins so: a person can tell it from a pin or a key.</summary>
    public const string Prefix = "icat-enroll-1.";

    /// <summary>The bytes of the secret.</summary>
    public const int SecretLength = 32;

    private const byte Version = 1;
    private const int CheckLength = 4;
    private const int PayloadLength = 1 + RemotePin.Length + SecretLength;

    private readonly byte[] secret;

    private EnrollmentToken(RemotePin agent, byte[] secret)
    {
        Agent = agent;
        this.secret = secret;
    }

    /// <summary>The pin of the agent the token enrolls with.</summary>
    public RemotePin Agent { get; }

    /// <summary>The one-time secret, sent only to the agent the pin names.</summary>
    public ReadOnlySpan<byte> Secret => secret;

    /// <summary>A new token for the agent known by <paramref name="agent"/>, its secret from the system's random source.</summary>
    public static EnrollmentToken Create(RemotePin agent)
    {
        if (agent.IsEmpty)
        {
            throw new ArgumentException("A token names the agent's pin.", nameof(agent));
        }

        return new(agent, RandomNumberGenerator.GetBytes(SecretLength));
    }

    /// <summary>The hash an agent keeps of a secret, and compares a presented secret's with.</summary>
    public static byte[] HashOf(ReadOnlySpan<byte> secret) => SHA256.HashData(secret);

    /// <summary>
    /// Reads a token's text; false, with why, where it is not one version 1 writes or was not copied whole - its check, the
    /// first four bytes of the SHA-256 of what precedes it, tells a token cut short or altered from one never made.
    /// </summary>
    public static bool TryParse(string? text, out EnrollmentToken? token, out string? problem)
    {
        token = null;
        problem = null;
        string trimmed = text?.Trim() ?? string.Empty;
        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
        {
            problem = $"An enrollment token begins with \"{Prefix}\".";
            return false;
        }

        ReadOnlySpan<char> body = trimmed.AsSpan(Prefix.Length);
        byte[] bytes = new byte[PayloadLength + CheckLength];
        if (Base64Url.GetMaxDecodedLength(body.Length) < bytes.Length
            || !Base64Url.TryDecodeFromChars(body, bytes, out int written)
            || written != bytes.Length)
        {
            problem = "The enrollment token was not copied whole.";
            return false;
        }

        Span<byte> check = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes.AsSpan(0, PayloadLength), check);
        if (!check[..CheckLength].SequenceEqual(bytes.AsSpan(PayloadLength, CheckLength)))
        {
            problem = "The enrollment token was not copied whole: its check does not match.";
            return false;
        }

        if (bytes[0] != Version)
        {
            problem = $"The enrollment token is of version {bytes[0]}, which this InterCat does not read.";
            return false;
        }

        token = new(RemotePin.FromBytes(bytes.AsSpan(1, RemotePin.Length)), bytes.AsSpan(1 + RemotePin.Length, SecretLength).ToArray());
        return true;
    }

    /// <summary>The token's text: its prefix, then its version, pin, secret and check in unpadded base64url.</summary>
    public override string ToString()
    {
        byte[] bytes = new byte[PayloadLength + CheckLength];
        bytes[0] = Version;
        Agent.ToBytes().CopyTo(bytes.AsSpan(1));
        secret.CopyTo(bytes.AsSpan(1 + RemotePin.Length));
        SHA256.HashData(bytes.AsSpan(0, PayloadLength)).AsSpan(0, CheckLength).CopyTo(bytes.AsSpan(PayloadLength));
        return Prefix + Base64Url.EncodeToString(bytes);
    }
}
