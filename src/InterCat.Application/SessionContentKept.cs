using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// What a content capture's session kept, in sum and in words (content-v1 §4, ADR-036): how much of how many messages,
/// whole, cut to the record limit, or not kept once the content limit was reached - never a byte of them. icat capture
/// and the window say it in these words when a capture ends.
/// </summary>
public static class SessionContentKept
{
    /// <summary>
    /// What <paramref name="store"/>'s current generation keeps as content: none when it keeps no content chunk, null when
    /// it has published nothing or its content could not be read.
    /// </summary>
    public static SessionContentSummary? Read(SessionStore? store)
    {
        if (store is null)
        {
            return null;
        }

        try
        {
            using EvidenceLease lease = store.AcquireLease();
            SessionManifestV1 manifest = lease.Manifest;
            return manifest.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.Content)
                ? SessionContentIndex.Read(store.Root, manifest, CancellationToken.None).Summarize()
                : SessionContentIndex.Empty.Summarize();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>What a capture's content came to, in words; a summary that could not be read is said to be unknown.</summary>
    public static string Statement(SessionContentSummary? kept)
    {
        if (kept is null)
        {
            return "unknown: the session published nothing, or its content could not be read";
        }

        string sums = kept.Records == 0
            ? "none: no message of the processes named was kept"
            : $"{ByteSizeText.Of(kept.KeptBytes)} of {CountText.Of(kept.Records, "message")}: "
                + string.Create(CultureInfo.CurrentCulture,
                    $"{kept.Whole:N0} kept whole, {kept.Cut:N0} cut to the record limit, {kept.Omitted:N0} not kept once the ")
                + "content limit was reached";
        return kept.Problem is { } problem ? $"{sums}; some could not be read, so these sums leave it out: {problem}" : sums;
    }
}
