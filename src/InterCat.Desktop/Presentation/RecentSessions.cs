using System.Globalization;
using System.Text.Json;
using InterCat.Application;
using InterCat.Capture.Journal;
using InterCat.Storage;

namespace InterCat.Desktop.Presentation;

/// <summary>One session the user saved, as the rail lists it while no session is open.</summary>
public sealed record RecentSessionRow(
    string Path,
    string Title,
    string Detail,
    DateTimeOffset SavedUtc) : IAccessibleRow
{
    public string AccessibleName => $"{Title}, {Detail}. Press Enter to open it.";
}

/// <summary>
/// The sessions in the user's session folder, newest first, so a saved capture is one gesture away rather than a path
/// to remember (§3.1). Listing reads each session's current pointer, its manifest and its segments' fixed-size headers,
/// and nothing else: it never verifies or hashes a session, which opening one still does in full. A directory that is
/// not a readable session is left out rather than listed with a guess.
/// </summary>
internal static class RecentSessions
{
    /// <summary>How many sessions the rail lists; older ones stay a folder away through Open saved session.</summary>
    public const int MaximumRows = 8;

    private const long MaximumPointerBytes = 64 * 1024;
    private const long MaximumManifestBytes = 16L * 1024 * 1024;

    public static IReadOnlyList<RecentSessionRow> Find(
        string root,
        DateTimeOffset nowUtc,
        TimeZoneInfo zone,
        CultureInfo culture,
        int maximum = MaximumRows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var found = new List<RecentSessionRow>();
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                if (Describe(directory, nowUtc, zone, culture) is { } row)
                {
                    found.Add(row);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed further keeps what was found so far.
        }

        return [.. found.OrderByDescending(row => row.SavedUtc).ThenBy(row => row.Path, StringComparer.Ordinal).Take(maximum)];
    }

    private static RecentSessionRow? Describe(string directory, DateTimeOffset nowUtc, TimeZoneInfo zone, CultureInfo culture)
    {
        try
        {
            if (Read<SessionPointerV1>(Path.Combine(directory, SessionPointerV1.FileName), MaximumPointerBytes) is not { } pointer
                || pointer.Validate() is not null
                || OwnedFileName.Validate(pointer.ManifestName) is not null
                || Read<SessionManifestV1>(Path.Combine(directory, pointer.ManifestName), MaximumManifestBytes) is not { } manifest
                || manifest.Validate() is not null
                || manifest.Generation != pointer.Generation)
            {
                return null;
            }

            long records = 0;
            foreach (string segment in SessionSegments.Names(manifest))
            {
                records += RowsOf(Path.Combine(directory, segment));
            }

            long bytes = manifest.Dependencies.Sum(dependency => dependency.LengthBytes);
            string name = Path.GetFileName(directory);
            string title = manifest.SourceIdentity == RedactedSessionPackage.Contract ? "Redacted session package"
                : name.StartsWith("explore-", StringComparison.OrdinalIgnoreCase) ? "Explore capture"
                : name;
            string unfinished = File.Exists(LiveFollowTicket.PathFor(directory)) ? " · not finished saving" : string.Empty;
            string detail = $"saved {Moments.When(manifest.CommittedUtc, nowUtc, zone, culture)} · "
                + $"{records.ToString("N0", culture)} {(records == 1 ? "record" : "records")} · {Size(bytes, culture)}"
                + unfinished;
            return new(directory, title, detail, manifest.CommittedUtc);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A segment's row count from its checksummed header alone.</summary>
    private static int RowsOf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[SegmentFormatV1.HeaderLength];
        stream.ReadExactly(header);
        return SegmentReaderV1.DeclaredRowCount(header);
    }

    private static T? Read<T>(string path, long maximumBytes)
        where T : class
    {
        var file = new FileInfo(path);
        return !file.Exists || file.Length is 0 || file.Length > maximumBytes
            ? null
            : JsonSerializer.Deserialize<T>(File.ReadAllText(path), SessionManifestV1.Json);
    }

    /// <summary>A size on disk as a person reads it: "812 KB", "4.2 MB", "37 MB", "1.3 GB".</summary>
    private static string Size(long bytes, CultureInfo culture) => bytes switch
    {
        < 1_000_000 => string.Create(culture, $"{Math.Max(1, bytes / 1_000):N0} KB"),
        < 10_000_000 => string.Create(culture, $"{bytes / 1_000_000d:N1} MB"),
        < 1_000_000_000 => string.Create(culture, $"{bytes / 1_000_000d:N0} MB"),
        _ => string.Create(culture, $"{bytes / 1_000_000_000d:N1} GB"),
    };
}
