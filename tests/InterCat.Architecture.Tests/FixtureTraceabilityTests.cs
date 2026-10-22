using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace InterCat.Architecture.Tests;

/// <summary>
/// The checks of section 13.5 and ADR-051. They make the traceability matrix honest: a contract nobody
/// asserts is reported as uncovered rather than silently trusted, a fixture claims only what its tests
/// assert, and nothing cites as evidence a fixture that does not exist.
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

    [Fact(DisplayName = "13.5: the fixture index resolves whole - each entry's directory, artifacts, tests, milestone and backlog, covers exactly its tests' contracts, and every fixture ID named elsewhere is held or retired")]
    public void TheFixtureIndexResolves()
    {
        string root = RepositoryRoot();
        using JsonDocument index = LoadIndex(root);
        string plan = File.ReadAllText(Path.Combine(root, "docs", "design", "INTERCAT-IMPLEMENTATION-PLAN.md"));
        var repository = new FixtureIndexChecks.Repository(
            path => File.Exists(Path.Combine(root, path)),
            Directory.EnumerateDirectories(Path.Combine(root, "fixtures"))
                .Select(Path.GetFileName)
                .Where(name => name!.StartsWith("FX-", StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal)!,
            AssertedTestNames(root),
            MilestonePattern().Matches(plan).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal),
            BacklogPattern().Matches(plan).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal),
            NamedFixtures(root));

        // The plan's milestones and backlog are read from it, so an empty read would pass every entry vacuously.
        Assert.Contains("M0", repository.Milestones);
        Assert.Contains("IC-003", repository.BacklogItems);
        Assert.Contains("FX-TCP-001", repository.NamedFixtures.Keys);

        List<string> problems = FixtureIndexChecks.Problems(index.RootElement, repository);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Theory(DisplayName = "13.5: each way a fixture index can be wrong is found, and named with the entry it is in")]
    [InlineData("none", "")]
    [InlineData("malformed", "FX-tcp-1 is not of the form FX-<AREA>-<nnn>.")]
    [InlineData("duplicate", "FX-TCP-001 is declared twice.")]
    [InlineData("reused", "FX-TCP-001 is retired, and a retired number is never used again.")]
    [InlineData("retired-malformed", "Retired number FX-IMPORT-1 is not of the form FX-<AREA>-<nnn>.")]
    [InlineData("retired-twice", "FX-IMPORT-001 is retired twice.")]
    [InlineData("retired-unexplained", "FX-IMPORT-001 is retired without the revision and the reason.")]
    [InlineData("retired-unnumbered", "FX-IMPORT-001 is retired without the revision and the reason.")]
    [InlineData("retired-kept", "FX-IMPORT-001 is retired, yet fixtures/FX-IMPORT-001 is still there.")]
    [InlineData("no-directory", "FX-TCP-001 has no directory under fixtures/.")]
    [InlineData("undeclared-directory", "fixtures/FX-UDP-001 is declared by no entry.")]
    [InlineData("no-truth", "FX-TCP-001 names a truth log that does not exist: fixtures/FX-TCP-001/evidence/truth.jsonl")]
    [InlineData("no-expected", "FX-TCP-001 names an expected-result artifact that does not exist: fixtures/FX-TCP-001/evidence/verification.json")]
    [InlineData("unknown-test", "FX-TCP-001 names a test that does not exist: R8: a capture that never ran")]
    [InlineData("unknown-milestone", "FX-TCP-001 serves milestone M99, which the plan does not define.")]
    [InlineData("unknown-backlog", "FX-TCP-001 names backlog item IC-999, which the plan does not list.")]
    [InlineData("unasserted-claim", "FX-TCP-001 claims R9, which none of its tests asserts.")]
    [InlineData("unclaimed-test", "FX-TCP-001 lists a test asserting I14, which it does not claim.")]
    [InlineData("misfiled", "FX-TCP-001 lists R8 among its invariants.")]
    [InlineData("named-unknown", "src/Probe.cs names FX-UDP-001, which the index neither holds nor has retired.")]
    public void EveryWayAnIndexCanBeWrongIsFound(string variant, string problem)
    {
        JsonNode index = JsonNode.Parse(SyntheticIndex)!;
        JsonNode fixture = index["fixtures"]![0]!;
        JsonNode retired = index["retired"]![0]!;
        HashSet<string> directories = new(StringComparer.Ordinal) { "FX-TCP-001" };
        Dictionary<string, string> named = new(StringComparer.Ordinal) { ["FX-TCP-001"] = "src/A.cs", ["FX-IMPORT-001"] = "bench/b.json" };
        string missing = string.Empty;
        switch (variant)
        {
            case "malformed": fixture["id"] = "FX-tcp-1"; break;
            case "duplicate": index["fixtures"]!.AsArray().Add(fixture.DeepClone()); break;
            case "reused": retired["id"] = "FX-TCP-001"; break;
            case "retired-malformed": retired["id"] = "FX-IMPORT-1"; break;
            case "retired-twice": index["retired"]!.AsArray().Add(retired.DeepClone()); break;
            case "retired-unexplained": retired["reason"] = string.Empty; break;
            case "retired-unnumbered": retired.AsObject().Remove("revision"); break;
            case "retired-kept": directories.Add("FX-IMPORT-001"); break;
            case "no-directory": directories.Clear(); break;
            case "undeclared-directory": directories.Add("FX-UDP-001"); break;
            case "no-truth": missing = "fixtures/FX-TCP-001/evidence/truth.jsonl"; break;
            case "no-expected": missing = "fixtures/FX-TCP-001/evidence/verification.json"; break;
            case "unknown-test": fixture["tests"]!.AsArray().Add("R8: a capture that never ran"); break;
            case "unknown-milestone": fixture["milestone"] = "M99"; break;
            case "unknown-backlog": fixture["backlog"]!.AsArray().Add("IC-999"); break;
            case "unasserted-claim": fixture["covers"]!["rules"]!.AsArray().Add("R9"); break;
            case "unclaimed-test": fixture["tests"]!.AsArray().Add("I14: one record answers one operation"); break;
            case "misfiled": fixture["covers"]!["invariants"]!.AsArray().Add("R8"); break;
            case "named-unknown": named["FX-UDP-001"] = "src/Probe.cs"; break;
        }

        var repository = new FixtureIndexChecks.Repository(
            path => path != missing,
            directories,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "R8: a capture records its own counters",
                "P16: evidence holds only its own flows",
                "I14: one record answers one operation",
            },
            new HashSet<string>(StringComparer.Ordinal) { "M0", "M1" },
            new HashSet<string>(StringComparer.Ordinal) { "IC-003" },
            named);

        using JsonDocument document = JsonDocument.Parse(index.ToJsonString());
        List<string> problems = FixtureIndexChecks.Problems(document.RootElement, repository);

        if (problem.Length == 0)
        {
            Assert.Empty(problems);
        }
        else
        {
            Assert.Contains(problem, problems);
        }
    }

    // A valid index of one fixture and one retired number, which each variant breaks in one way.
    private const string SyntheticIndex = """
        {
          "fixtures": [
            {
              "id": "FX-TCP-001",
              "milestone": "M0",
              "backlog": ["IC-003"],
              "covers": { "rules": ["R8"], "invariants": [], "prohibitions": ["P16"] },
              "tests": ["R8: a capture records its own counters", "P16: evidence holds only its own flows"],
              "truth": { "log": "fixtures/FX-TCP-001/evidence/truth.jsonl" },
              "expected": { "artifact": "fixtures/FX-TCP-001/evidence/verification.json" }
            }
          ],
          "retired": [ { "id": "FX-IMPORT-001", "revision": 723, "reason": "Machine data, never committed." } ]
        }
        """;

    // Every fixture ID named where a fixture is cited as evidence: code, tests, tools, contracts, decisions, capability
    // reports and benchmark results. The plan's own examples of the form are not evidence and are not read.
    private static Dictionary<string, string> NamedFixtures(string root)
    {
        string[] places = ["src", "tools", "tests", "contracts", Path.Combine("docs", "adr"), "capabilities", "bench"];
        string[] extensions = [".cs", ".md", ".json", ".jsonl", ".ps1", ".txt", ".csv"];
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string place in places)
        {
            foreach (string path in SourceFiles(Path.Combine(root, place)).Order(StringComparer.Ordinal))
            {
                if (!extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (string id in FixtureIndexChecks.NamedIn(File.ReadAllText(path)))
                {
                    named.TryAdd(id, Path.GetRelativePath(root, path).Replace('\\', '/'));
                }
            }
        }

        return named;
    }

    // A folder's files, leaving out build output and hidden folders.
    private static IEnumerable<string> SourceFiles(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            yield return file;
        }

        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            string name = Path.GetFileName(child);
            if (name is "bin" or "obj" || name.StartsWith('.'))
            {
                continue;
            }

            foreach (string file in SourceFiles(child))
            {
                yield return file;
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

    [GeneratedRegex("^### (M[0-9]+) ", RegexOptions.Multiline)]
    private static partial Regex MilestonePattern();

    [GeneratedRegex(@"^\| (IC-[0-9]+[a-z]?) \|", RegexOptions.Multiline)]
    private static partial Regex BacklogPattern();
}
