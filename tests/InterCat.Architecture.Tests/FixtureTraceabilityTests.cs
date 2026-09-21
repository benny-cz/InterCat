using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace InterCat.Architecture.Tests;

/// <summary>
/// The two derived checks of section 13.5. They make the traceability matrix honest: a contract nobody
/// asserts is reported as uncovered rather than silently trusted, and a gate cannot name a fixture that
/// does not exist.
/// </summary>
public sealed partial class FixtureTraceabilityTests
{
    private const int RuleCount = 22;
    private const int InvariantCount = 22;
    private const int ProhibitionCount = 28;

    [Fact(DisplayName = "13.5: every rule, invariant and prohibition is either asserted by a test or declared uncovered")]
    public void EveryContractIsCoveredOrDeclaredUncovered()
    {
        string root = RepositoryRoot();
        using JsonDocument index = LoadIndex(root);
        JsonElement coverage = index.RootElement.GetProperty("contractCoverage");

        HashSet<string> declaredCovered = [.. coverage.GetProperty("covered").EnumerateObject().Select(entry => entry.Name)];
        HashSet<string> declaredUncovered = [.. coverage.GetProperty("uncovered").EnumerateObject().Select(entry => entry.Name)];
        HashSet<string> asserted = AssertedContractIds(root);

        Assert.Equal(asserted.Order(StringComparer.Ordinal), declaredCovered.Order(StringComparer.Ordinal));
        Assert.Empty(declaredCovered.Intersect(declaredUncovered, StringComparer.Ordinal));

        // The ledger names the tests behind each contract item, so those names are checked as well as the
        // identifiers. A list nothing verifies drifts the moment a test is renamed or deleted, and a
        // traceability matrix that has drifted is worse than none: it asserts coverage that is not there.
        foreach (JsonProperty entry in coverage.GetProperty("covered").EnumerateObject())
        {
            IEnumerable<string> declaredTests = entry.Value.EnumerateArray().Select(test => test.GetString()!);
            Assert.Equal(
                AssertedTestNames(root)
                    .Where(name => name.StartsWith(entry.Name + ": ", StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal),
                declaredTests.Order(StringComparer.Ordinal));
        }

        foreach (JsonProperty entry in coverage.GetProperty("uncovered").EnumerateObject())
        {
            Assert.False(
                string.IsNullOrWhiteSpace(entry.Value.GetProperty("reason").GetString()),
                $"{entry.Name} is declared uncovered without a reason.");
            Assert.False(
                string.IsNullOrWhiteSpace(entry.Value.GetProperty("plannedBy").GetString()),
                $"{entry.Name} is declared uncovered without the item that will cover it.");
        }

        var expected = new List<string>();
        for (int number = 1; number <= RuleCount; number++)
        {
            expected.Add($"R{number}");
        }

        for (int number = 1; number <= InvariantCount; number++)
        {
            expected.Add($"I{number}");
        }

        for (int number = 1; number <= ProhibitionCount; number++)
        {
            expected.Add($"P{number}");
        }

        IEnumerable<string> accounted = declaredCovered.Union(declaredUncovered, StringComparer.Ordinal);
        Assert.Empty(expected.Except(accounted, StringComparer.Ordinal));
    }

    [Fact(DisplayName = "13.5: every fixture entry names artifacts and tests that exist")]
    public void EveryFixtureEntryResolves()
    {
        string root = RepositoryRoot();
        using JsonDocument index = LoadIndex(root);
        HashSet<string> testNames = AssertedTestNames(root);

        foreach (JsonElement fixture in index.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            string id = fixture.GetProperty("id").GetString()!;
            Assert.Matches("^FX-[A-Z]+-[0-9]{3}$", id);

            string truthLog = fixture.GetProperty("truth").GetProperty("log").GetString()!;
            Assert.True(
                File.Exists(Path.Combine(root, truthLog)),
                $"{id} names a truth log that does not exist: {truthLog}");

            string expected = fixture.GetProperty("expected").GetProperty("artifact").GetString()!;
            Assert.True(
                File.Exists(Path.Combine(root, expected)),
                $"{id} names an expected-result artifact that does not exist: {expected}");

            foreach (JsonElement test in fixture.GetProperty("tests").EnumerateArray())
            {
                string name = test.GetString()!;
                Assert.True(testNames.Contains(name), $"{id} names a test that does not exist: {name}");
            }
        }
    }

    private static JsonDocument LoadIndex(string root)
    {
        string path = Path.Combine(root, "fixtures", "index.json");
        Assert.True(File.Exists(path), "fixtures/index.json is the traceability matrix and must exist.");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static HashSet<string> AssertedTestNames(string root)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (Match match in DisplayNames(root))
        {
            names.Add(match.Groups[1].Value + ": " + match.Groups[2].Value);
        }

        return names;
    }

    private static HashSet<string> AssertedContractIds(string root)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (Match match in DisplayNames(root))
        {
            ids.Add(match.Groups[1].Value);
        }

        return ids;
    }

    private static List<Match> DisplayNames(string root)
    {
        var matches = new List<Match>();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            matches.AddRange(DisplayNamePattern().Matches(File.ReadAllText(path)));
        }

        return matches;
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

    [GeneratedRegex("DisplayName = \"([A-Z][0-9]+): ([^\"]*)")]
    private static partial Regex DisplayNamePattern();
}
