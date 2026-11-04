using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace InterCat.Architecture.Tests;

/// <summary>
/// One canonical enumeration per semantic dimension, defined in the plan's §23 (R5): the plan's table and the domain's
/// enumeration name the same members by the same codes, so a mechanism added to one is never missing from the other.
/// </summary>
public sealed partial class CanonicalEnumerationTests
{
    [Fact(DisplayName = "R5: the plan's EN-Mechanism table names every mechanism the domain defines, each by its code, and none it does not")]
    public void TheMechanismTableIsTheEnumeration()
    {
        string root = RepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "src", "InterCat.Domain", "Enumerations.cs"));
        int start = source.IndexOf("public enum Mechanism", StringComparison.Ordinal);
        Assert.True(start >= 0, "The domain defines no Mechanism enumeration.");
        int open = source.IndexOf('{', start);
        int close = source.IndexOf('}', open);
        (int Code, string Name)[] defined =
        [
            .. MemberPattern().Matches(source[open..close])
                .Select(member => (int.Parse(member.Groups["code"].Value, CultureInfo.InvariantCulture), member.Groups["name"].Value))
                .OrderBy(member => member.Item1),
        ];
        Assert.Contains(defined, member => member.Name == "UnknownMechanism");

        string plan = File.ReadAllText(Path.Combine(root, "docs", "design", "INTERCAT-IMPLEMENTATION-PLAN.md"));
        int table = plan.IndexOf("**`EN-Mechanism`**", StringComparison.Ordinal);
        Assert.True(table >= 0, "§23 no longer defines EN-Mechanism.");

        // The table's rows follow its heading and end at the first line that is no row.
        var rows = new List<(int Code, string Name)>();
        bool inTable = false;
        foreach (string line in plan[table..].Split('\n').Skip(1))
        {
            if (!line.StartsWith('|'))
            {
                if (inTable)
                {
                    break;
                }

                continue;
            }

            inTable = true;
            if (RowPattern().Match(line) is { Success: true } row)
            {
                rows.Add((int.Parse(row.Groups["code"].Value, CultureInfo.InvariantCulture), row.Groups["name"].Value));
            }
        }

        Assert.Equal(defined, rows.OrderBy(row => row.Code));
    }

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z]+)\s*=\s*(?<code>\d+),", RegexOptions.Multiline)]
    private static partial Regex MemberPattern();

    [GeneratedRegex(@"^\| (?<code>\d+) \| `(?<name>[A-Za-z]+)` \|")]
    private static partial Regex RowPattern();

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }
}
