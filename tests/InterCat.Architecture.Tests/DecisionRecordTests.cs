using System.Text.RegularExpressions;
using Xunit;

namespace InterCat.Architecture.Tests;

/// <summary>
/// The plan's table of decision records (§16) and the records in <c>docs/adr</c> describe the same set: a record nobody
/// lists is a decision a reader of the plan never finds, and a row without its record claims a decision nobody wrote.
/// </summary>
public sealed partial class DecisionRecordTests
{
    [Fact(DisplayName = "16: every decision record has its row in the plan's table and every row its record, numbered from ADR-001 without a gap")]
    public void TheTableAndTheRecordsAgree()
    {
        string root = RepositoryRoot();
        string plan = File.ReadAllText(Path.Combine(root, "docs", "design", "INTERCAT-IMPLEMENTATION-PLAN.md"));
        string[] rows = [.. RowPattern().Matches(plan).Select(match => match.Groups[1].Value)];
        string[] records =
        [
            .. Directory.EnumerateFiles(Path.Combine(root, "docs", "adr"), "ADR-*.md")
                .Select(path => RecordPattern().Match(Path.GetFileName(path)).Groups[1].Value)
                .Order(StringComparer.Ordinal),
        ];

        Assert.DoesNotContain(string.Empty, records);
        Assert.Equal(records.Distinct(StringComparer.Ordinal).Count(), records.Length);
        Assert.Equal(rows.Distinct(StringComparer.Ordinal).Count(), rows.Length);
        Assert.Equal(records, rows.Order(StringComparer.Ordinal));
        Assert.Equal(Enumerable.Range(1, records.Length).Select(number => $"ADR-{number:000}"), records);
    }

    [Fact(DisplayName = "16: every decision record the plan, the contracts, the code, the tests or the tools name exists")]
    public void EveryNamedRecordExists()
    {
        string root = RepositoryRoot();
        HashSet<string> records =
        [
            .. Directory.EnumerateFiles(Path.Combine(root, "docs", "adr"), "ADR-*.md")
                .Select(path => RecordPattern().Match(Path.GetFileName(path)).Groups[1].Value),
        ];

        var named = new HashSet<string>(StringComparer.Ordinal);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string place in new[] { "docs", "contracts", "src", "tests", "tools" })
        {
            foreach (string path in Files(Path.Combine(root, place)))
            {
                foreach (Match match in NamedPattern().Matches(File.ReadAllText(path)))
                {
                    named.Add(match.Value);
                    if (!records.Contains(match.Value))
                    {
                        missing.Add($"{Path.GetRelativePath(root, path)} names {match.Value}");
                    }
                }
            }
        }

        // The plan names its first record, so a scan that found none read nothing.
        Assert.Contains("ADR-001", records);
        Assert.Contains("ADR-001", named);
        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    // Markdown and C# beneath a folder, leaving out build output.
    private static IEnumerable<string> Files(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            if (Path.GetExtension(file) is ".md" or ".cs")
            {
                yield return file;
            }
        }

        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            if (Path.GetFileName(child) is "bin" or "obj")
            {
                continue;
            }

            foreach (string file in Files(child))
            {
                yield return file;
            }
        }
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

    [GeneratedRegex(@"^\| (ADR-[0-9]{3}) \|", RegexOptions.Multiline)]
    private static partial Regex RowPattern();

    [GeneratedRegex("^(ADR-[0-9]{3})-")]
    private static partial Regex RecordPattern();

    [GeneratedRegex(@"\bADR-[0-9]{3}\b")]
    private static partial Regex NamedPattern();
}
