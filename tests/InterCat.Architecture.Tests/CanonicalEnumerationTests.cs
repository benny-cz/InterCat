using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace InterCat.Architecture.Tests;

/// <summary>
/// One canonical enumeration per semantic dimension, defined in the plan's §23 (R5): the plan's table or list and the
/// domain's enumeration name the same members by the same codes, so a member added to one is never missing from the other.
/// </summary>
public sealed partial class CanonicalEnumerationTests
{
    [Fact(DisplayName = "R5: the plan's EN-Mechanism table names every mechanism the domain defines, each by its code, and none it does not")]
    public void TheMechanismTableIsTheEnumeration()
    {
        string root = RepositoryRoot();
        (int Code, string Name)[] defined = Members(root, "public enum Mechanism");
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

    [Fact(DisplayName = "R5: the plan's EN-SourceField list names every source field the domain defines, each by its code, and none it does not")]
    public void TheSourceFieldListIsTheEnumeration()
    {
        string root = RepositoryRoot();
        (int Code, string Name)[] defined = Members(root, "public enum SourceField");
        Assert.Contains((1, "ProcessStartSequence"), defined);

        string plan = File.ReadAllText(Path.Combine(root, "docs", "design", "INTERCAT-IMPLEMENTATION-PLAN.md"));
        int list = plan.IndexOf("**`EN-SourceField`**:", StringComparison.Ordinal);
        Assert.True(list >= 0, "§23 no longer defines EN-SourceField.");

        // The list is one line: each member's code and name, then what the members are.
        string line = plan[list..plan.IndexOf('\n', list)];
        (int Code, string Name)[] listed =
        [
            .. ItemPattern().Matches(line).Select(item => (int.Parse(item.Groups["code"].Value, CultureInfo.InvariantCulture), item.Groups["name"].Value)),
        ];
        Assert.Equal(defined, listed.OrderBy(item => item.Code));
    }

    // An enumeration's members as the domain declares them, each with its code, in the order of their codes.
    private static (int Code, string Name)[] Members(string root, string declaration)
    {
        string source = File.ReadAllText(Path.Combine(root, "src", "InterCat.Domain", "Enumerations.cs"));
        int start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The domain declares no '{declaration}'.");
        int open = source.IndexOf('{', start);
        int close = source.IndexOf('}', open);
        return
        [
            .. MemberPattern().Matches(source[open..close])
                .Select(member => (int.Parse(member.Groups["code"].Value, CultureInfo.InvariantCulture), member.Groups["name"].Value))
                .OrderBy(member => member.Item1),
        ];
    }

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z]+)\s*=\s*(?<code>\d+),", RegexOptions.Multiline)]
    private static partial Regex MemberPattern();

    [GeneratedRegex(@"^\| (?<code>\d+) \| `(?<name>[A-Za-z]+)` \|")]
    private static partial Regex RowPattern();

    [GeneratedRegex(@"(?<code>\d+) `(?<name>[A-Za-z]+)`")]
    private static partial Regex ItemPattern();

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
