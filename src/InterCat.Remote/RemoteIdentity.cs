using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace InterCat.Remote;

/// <summary>
/// One party's key - an agent's or a controller's - in a self-signed certificate it presents in TLS (`remote-agent-v1`
/// §1). The certificate is installed in no store and trusted by no authority: the other party trusts its pin alone.
/// </summary>
public sealed class RemoteIdentity : IDisposable
{
    private static readonly Oid ServerAuthentication = new("1.3.6.1.5.5.7.3.1");
    private static readonly Oid ClientAuthentication = new("1.3.6.1.5.5.7.3.2");

    private readonly byte[] pkcs12;

    private RemoteIdentity(byte[] pkcs12)
    {
        this.pkcs12 = pkcs12;

        // SChannel cannot sign with a key that exists only in memory, so the certificate is loaded from its PKCS #12 bytes
        // into a temporary key container, which goes when the identity is disposed.
        Certificate = X509CertificateLoader.LoadPkcs12(pkcs12, password: null);
        Pin = RemotePin.Of(Certificate);
    }

    /// <summary>The certificate, with the private key SChannel signs with.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>The pin the other party knows this one by.</summary>
    public RemotePin Pin { get; }

    /// <summary>
    /// A new ECDSA P-256 key in a self-signed certificate naming <paramref name="role"/>, valid for twenty years: a pin does
    /// not expire, so neither does its certificate in any way that matters.
    /// </summary>
    public static RemoteIdentity Create(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=InterCat {role}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ServerAuthentication, ClientAuthentication], critical: false));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 made = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));
        return new(made.Export(X509ContentType.Pkcs12));
    }

    /// <summary>The identity whose key and certificate <paramref name="pkcs12"/> holds, as <see cref="ExportPkcs12"/> wrote it.</summary>
    /// <exception cref="CryptographicException">The bytes hold no certificate with its key.</exception>
    public static RemoteIdentity FromPkcs12(byte[] pkcs12)
    {
        ArgumentNullException.ThrowIfNull(pkcs12);
        var identity = new RemoteIdentity([.. pkcs12]);
        if (!identity.Certificate.HasPrivateKey)
        {
            identity.Dispose();
            throw new CryptographicException("The saved identity holds a certificate without its key.");
        }

        return identity;
    }

    /// <summary>The key and certificate as PKCS #12 bytes, without a password: kept only protected (<see cref="LocalProtection"/>).</summary>
    public byte[] ExportPkcs12() => [.. pkcs12];

    public void Dispose()
    {
        Certificate.Dispose();
        CryptographicOperations.ZeroMemory(pkcs12);
    }
}
