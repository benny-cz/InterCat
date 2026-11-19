<#
.SYNOPSIS
Makes the self-signed code-signing certificate InterCat's test packages are signed with (ADR-115).

.DESCRIPTION
Writes intercat-test-signing.pfx, the certificate with its private key and no password, for Build-Installer.ps1's
-Certificate, and intercat-test-signing.cer, the certificate alone, for the machine the checks run on to trust. Nothing
touches a certificate store: trusting it is a step taken on that machine only, never on the one that builds. It is not
a release certificate, and a package it signs is not a release.

.PARAMETER Output
The folder the two files go to; outside the repository, since a private key is never committed.
#>
param([Parameter(Mandatory)] [string] $Output)
$ErrorActionPreference = 'Stop'
$folder = [IO.Path]::GetFullPath($Output)
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tracked = $folder.StartsWith($repository, [StringComparison]::OrdinalIgnoreCase) -and
    -not $folder.StartsWith((Join-Path $repository 'artifacts'), [StringComparison]::OrdinalIgnoreCase)
if ($tracked) { throw "A private key never goes where git tracks it; choose a folder outside $repository, or under its artifacts." }
$null = New-Item -ItemType Directory -Force $folder

$key = [Security.Cryptography.RSA]::Create(3072)
$request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
    'CN=InterCat Test Signing (not a release certificate)',
    $key,
    [Security.Cryptography.HashAlgorithmName]::SHA256,
    [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$codeSigning = [Security.Cryptography.OidCollection]::new()
$null = $codeSigning.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
$request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($codeSigning, $true))
$request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
    [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
$request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
$certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddYears(1))

$pfx = Join-Path $folder 'intercat-test-signing.pfx'
$cer = Join-Path $folder 'intercat-test-signing.cer'
[IO.File]::WriteAllBytes($pfx, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx))
[IO.File]::WriteAllBytes($cer, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
"certificate $cer $($certificate.Thumbprint)"
