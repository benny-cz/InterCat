using System.Globalization;

namespace InterCat.Capture.Journal;

/// <summary>
/// A follow's counts as a person reads them, the same on the window's capture card and from icat (R18): a capture's
/// chunks are its published chunks, and a count of one is said in the singular - "1 published chunk", never "1 chunk(s)".
/// </summary>
public static class FollowText
{
    /// <summary>"1 published chunk", "3 published chunks".</summary>
    public static string Chunks(long count, CultureInfo culture) => Count(count, "published chunk", culture);

    /// <summary>"1 record", "3,210 records".</summary>
    public static string Records(long count, CultureInfo culture) => Count(count, "record", culture);

    /// <summary>How far a finish has come: "12 of 40 published chunks derived, 3,210 records so far."</summary>
    public static string Progress(FollowStep step, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(culture);
        return $"{step.DerivedChunks.ToString("N0", culture)} of {Chunks(step.EvidenceChunks, culture)} derived, "
            + $"{Records(step.DerivedRecords, culture)} so far.";
    }

    /// <summary>
    /// What a recording or a follow says once a compaction was not published (§20.1): every row was published all the
    /// same, so its session is whole, only with more segments than it needs. The window and icat say it in these words,
    /// what it means before why, since a reason can name a file by a path that fills the capture card's few lines.
    /// </summary>
    public static string CompactionFailed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return "A compaction was not published; the session is whole, with more segments than it needs, and icat compact "
            + $"coalesces them. It failed: {reason.Trim().TrimEnd('.')}.";
    }

    private static string Count(long count, string noun, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return count.ToString("N0", culture) + " " + noun + (count == 1 ? string.Empty : "s");
    }
}
