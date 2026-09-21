using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.Capture.Windows;

/// <summary>
/// The live metadata source. It reads the provider registry and TDH manifest metadata only; it never
/// enables a provider, starts a session, or emits an event (IC-002).
/// </summary>
public sealed class TdhEtwMetadataSource : IEtwMetadataSource
{
    public bool IsAvailable => OperatingSystem.IsWindows();

    public string? UnavailableReason =>
        IsAvailable ? null : "ETW provider metadata is available only on Windows.";

    public RegisteredProvider? TryResolveProvider(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        if (!IsAvailable)
        {
            return null;
        }

        try
        {
            Guid guid = TraceEventProviders.GetProviderGuidByName(providerName);
            return guid == Guid.Empty ? null : new RegisteredProvider(providerName, guid);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    public int CountPublishedProviders()
    {
        if (!IsAvailable)
        {
            return 0;
        }

        try
        {
            int count = 0;
            foreach (Guid _ in TraceEventProviders.GetPublishedProviders())
            {
                count++;
            }

            return count;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return 0;
        }
    }

    public ManifestReadResult TryReadManifest(Guid providerGuid)
    {
        if (!IsAvailable)
        {
            return ManifestReadResult.Failure(UnavailableReason!);
        }

        try
        {
            string manifest = RegisteredTraceEventParser.GetManifestForRegisteredProvider(providerGuid);
            return string.IsNullOrWhiteSpace(manifest)
                ? ManifestReadResult.Failure("TDH returned an empty manifest for this provider.")
                : ManifestReadResult.Success(manifest);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ManifestReadResult.Failure($"TDH manifest metadata is unavailable: {exception.Message}");
        }
    }
}
