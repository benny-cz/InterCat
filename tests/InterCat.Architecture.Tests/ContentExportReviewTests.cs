using System.Text.RegularExpressions;
using Xunit;

namespace InterCat.Architecture.Tests;

/// <summary>
/// M5's content-export review (`docs/reviews/M5-content-export-review.md`): every source file outside the store's own that
/// names a content chunk, or calls a reader, decoder or writer of content, has its row there, saying what leaves through
/// it, who asks and the consent it needs. A file without its row is a path nobody reviewed, and a row whose file no longer
/// does any of it has gone stale.
/// </summary>
public sealed partial class ContentExportReviewTests
{
    [Fact(DisplayName = "P16: every source file that reads kept content has its row in the content-export review, and every row's file still does")]
    public void EveryContentPathIsReviewed()
    {
        string root = RepositoryRoot();
        string review = File.ReadAllText(Path.Combine(root, "docs", "reviews", "M5-content-export-review.md"));
        string[] rows = [.. RowPattern().Matches(review).Select(match => match.Groups[1].Value)];
        Assert.Equal(rows.Distinct(StringComparer.Ordinal).Count(), rows.Length);
        string[] reading =
        [
            .. Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .Where(path => !path.Contains("/obj/", StringComparison.Ordinal) && !path.Contains("/bin/", StringComparison.Ordinal)
                    && !path.StartsWith("src/InterCat.Storage/", StringComparison.Ordinal))
                .Where(path => File.ReadLines(Path.Combine(root, path))
                    .Any(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && ContentPattern().IsMatch(line))),
        ];
        string[] unreviewed = [.. reading.Except(rows, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] stale = [.. rows.Except(reading, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        Assert.True(unreviewed.Length == 0 && stale.Length == 0,
            $"Not in the review: {string.Join(", ", unreviewed)}. In the review, reading no content: {string.Join(", ", stale)}.");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }

    /// <summary>A row of the review's tables, by the source file its first cell names.</summary>
    [GeneratedRegex(@"^\| `(src/[^`]+\.cs)` \|", RegexOptions.Multiline)]
    private static partial Regex RowPattern();

    /// <summary>
    /// A content chunk named, or a call to what reads, decodes, searches or writes kept content, or packages it as evidence.
    /// </summary>
    [GeneratedRegex(@"\bContentChunk\w*\b|\b(?:SessionContentQuery\.(?:Read|ReadAt)|SessionContentPartQuery\.(?:Read|ReadMany)"
        + @"|SessionContentIndex\.ReadBytes|ContentSearch\.Find|ContentDecoders\.(?:Decode|DecodePart)|HttpExchangeHeadsQuery\.(?:Read|ReadAt)"
        + @"|SessionHttpExchanges\.(?:Conversation|Table)|HttpExchangeTable\.Csv|OriginalEvidencePackage\.\w+|SessionRawRecordQuery\.\w+)\(")]
    private static partial Regex ContentPattern();
}
