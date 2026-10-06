using System.Text.RegularExpressions;
using Xunit;

namespace InterCat.Architecture.Tests;

/// <summary>
/// What InterCat says to a person - a refusal, a note, a report, a source's description - speaks of the product, not of the
/// plan that schedules its work: a person cannot look up "revision 255" or "the M0 plan", and what they meant changes as
/// the plan does.
/// </summary>
public sealed partial class UserTextTests
{
    /// <summary>
    /// The tools that qualify a build - the benchmark, the fixture measurements, their workloads and probe journals - speak
    /// to the engineers who run them, in the plan's own terms.
    /// </summary>
    private static readonly string[] EngineeringTools =
    [
        "InterCat.Benchmarks/",
        "InterCat.Cli/BenchCommand.cs",
        "InterCat.Cli/MeasureCommand",
        "InterCat.Storage/Journal/JournalProbe",
        "InterCat.TestWorkloads/",
    ];

    [Fact(DisplayName = "§20.4: what InterCat says to a person names the product, never the plan's revisions, milestones or backlog items")]
    public void UserTextNamesNoPartOfThePlan()
    {
        string source = Path.Combine(RepositoryRoot(), "src");
        var found = new List<string>();
        foreach (string file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)
                || EngineeringTools.Any(tool => relative.StartsWith(tool, StringComparison.Ordinal)))
            {
                continue;
            }

            int number = 0;
            foreach (string line in File.ReadLines(file))
            {
                number++;
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                found.AddRange(Literal().Matches(line).Where(literal => Jargon().IsMatch(literal.Value))
                    .Select(literal => $"{relative}:{number}: {literal.Value}"));
            }
        }

        Assert.True(found.Count == 0, "Text a person reads cites the plan:" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    /// <summary>A string literal on one line, verbatim or interpolated.</summary>
    [GeneratedRegex(@"(?:\$@|@\$|\$|@)?""(?:[^""\\\n]|\\.)*""")]
    private static partial Regex Literal();

    /// <summary>A plan revision by number, a milestone, or a backlog item.</summary>
    [GeneratedRegex(@"\b[Rr]evision \d|\bmilestone\b|\bM\d{1,2}\b|\bIC-\d{3}")]
    private static partial Regex Jargon();

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
