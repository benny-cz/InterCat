using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>A process a broker content capture kept content from: its ID, what it ran, and the start the broker pinned.</summary>
internal sealed record CaptureContentProcessDocument(int ProcessId, string? Image, DateTimeOffset StartedUtc);

/// <summary>What a broker content capture was asked to keep and what its session holds of it (ADR-049, content-v1 §4).</summary>
internal sealed record CaptureContentDocument
{
    public required string SourceId { get; init; }

    public required IReadOnlyList<CaptureContentProcessDocument> Processes { get; init; }

    public required IReadOnlyList<string> Channels { get; init; }

    public required int MaximumRecordBytes { get; init; }

    public required long MaximumSessionBytes { get; init; }

    public required ContentInspectionMode Inspection { get; init; }

    /// <summary>What the session keeps of it, in sum; null when it published nothing or its content could not be read.</summary>
    public SessionContentSummary? Kept { get; init; }
}

/// <summary>What icat capture says of the content a capture kept, from the session it derived, naming no byte of it.</summary>
internal static class CaptureContent
{
    /// <summary>
    /// The capture's content as its review stated it and as <paramref name="derived"/> keeps it; null for a capture that
    /// keeps no content.
    /// </summary>
    public static CaptureContentDocument? Describe(
        BrokerEffectiveCaptureSummary summary,
        IReadOnlyDictionary<int, SeenProcess> seen,
        SessionStore? derived)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(seen);
        if (summary.Content is not { } content)
        {
            return null;
        }

        return new()
        {
            SourceId = content.SourceId,
            Processes =
            [
                .. summary.RequestedProcessIds.Zip(content.ProcessStartsUtc)
                    .Select(process => new CaptureContentProcessDocument(
                        process.First, seen.GetValueOrDefault(process.First)?.Image, process.Second)),
            ],
            Channels = content.ChannelSelectors,
            MaximumRecordBytes = content.MaximumRecordBytes,
            MaximumSessionBytes = content.MaximumSessionBytes,
            Inspection = content.Inspection,
            Kept = Kept(derived),
        };
    }

    /// <summary>What a capture's content came to, in words: how much of how many messages, whole, cut or not kept.</summary>
    public static string Statement(SessionContentSummary? kept)
    {
        if (kept is null)
        {
            return "unknown: the session published nothing, or its content could not be read";
        }

        string sums = kept.Records == 0
            ? "none: no message of the processes named was kept"
            : $"{ConsoleUi.Bytes(kept.KeptBytes)} of {CountText.Of(kept.Records, "message")}: {ConsoleUi.Count(kept.Whole)} "
                + $"kept whole, {ConsoleUi.Count(kept.Cut)} cut to the record limit, {ConsoleUi.Count(kept.Omitted)} not kept "
                + "once the content limit was reached";
        return kept.Problem is { } problem ? $"{sums}; some could not be read, so these sums leave it out: {problem}" : sums;
    }

    private static SessionContentSummary? Kept(SessionStore? derived)
    {
        if (derived is null)
        {
            return null;
        }

        try
        {
            using EvidenceLease lease = derived.AcquireLease();
            SessionManifestV1 manifest = lease.Manifest;
            return manifest.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.Content)
                ? SessionContentIndex.Read(derived.Root, manifest, CancellationToken.None).Summarize()
                : SessionContentIndex.Empty.Summarize();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            return null;
        }
    }
}
