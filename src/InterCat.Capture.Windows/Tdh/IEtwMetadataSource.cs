namespace InterCat.Capture.Windows;

/// <summary>A provider the machine's registry reports as registered. Registration is not coverage (R21).</summary>
public sealed record RegisteredProvider(string Name, Guid ProviderGuid);

/// <summary>The outcome of a schema read: exactly one of the two values is present.</summary>
public sealed record ManifestReadResult(string? ManifestXml, string? FailureReason)
{
    public static ManifestReadResult Success(string manifestXml) => new(manifestXml, null);

    public static ManifestReadResult Failure(string reason) => new(null, reason);
}

/// <summary>
/// Read-only access to ETW registration and schema metadata. The adapter depends on this interface
/// so that inventory logic is tested without Windows, a registered provider, or elevation (R19).
/// </summary>
public interface IEtwMetadataSource
{
    /// <summary>False when this host cannot supply metadata at all; <see cref="UnavailableReason"/> says why.</summary>
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    /// <summary>Resolves a provider by its registered name. Returns null when it is not registered.</summary>
    RegisteredProvider? TryResolveProvider(string providerName);

    /// <summary>Number of providers the machine reports as published, recorded as inventory breadth evidence.</summary>
    int CountPublishedProviders();

    ManifestReadResult TryReadManifest(Guid providerGuid);
}
