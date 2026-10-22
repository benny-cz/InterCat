using System.Text.Json;
using System.Text.RegularExpressions;

namespace InterCat.Architecture.Tests;

/// <summary>
/// What a fixture index must hold (§13.5, ADR-051), as the problems found, each a sentence naming the entry and what is
/// wrong. The repository's own index must have none; tests hand it indexes with each problem to show it is found.
/// </summary>
internal static partial class FixtureIndexChecks
{
    /// <summary>
    /// What an index is checked against: which files exist, the fixture directories, the tests named for a contract, the
    /// plan's milestones and backlog items, and each fixture ID that something names, with the first file to name it.
    /// </summary>
    internal sealed record Repository(
        Func<string, bool> FileExists,
        IReadOnlySet<string> FixtureDirectories,
        IReadOnlySet<string> TestNames,
        IReadOnlySet<string> Milestones,
        IReadOnlySet<string> BacklogItems,
        IReadOnlyDictionary<string, string> NamedFixtures);

    // Each kind of contract a fixture covers, under the key that lists it.
    private static readonly (string Key, char Letter)[] CoverKeys = [("rules", 'R'), ("invariants", 'I'), ("prohibitions", 'P')];

    // The files an entry names, by the member that holds each path, and how a problem names it.
    private static readonly (string Field, string Member, string What)[] Artifacts =
        [("truth", "log", "a truth log"), ("expected", "artifact", "an expected-result artifact")];

    internal static List<string> Problems(JsonElement index, Repository repository)
    {
        var problems = new List<string>();
        var retired = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement entry in Items(index, "retired"))
        {
            string id = Text(entry, "id");
            if (!IdPattern().IsMatch(id))
            {
                problems.Add($"Retired number {id} is not of the form FX-<AREA>-<nnn>.");
            }

            if (!retired.Add(id))
            {
                problems.Add($"{id} is retired twice.");
            }

            if (Text(entry, "reason").Length == 0 || !entry.TryGetProperty("revision", out JsonElement revision)
                || revision.ValueKind != JsonValueKind.Number)
            {
                problems.Add($"{id} is retired without the revision and the reason.");
            }

            if (repository.FixtureDirectories.Contains(id))
            {
                problems.Add($"{id} is retired, yet fixtures/{id} is still there.");
            }
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement fixture in Items(index, "fixtures"))
        {
            string id = Text(fixture, "id");
            if (!IdPattern().IsMatch(id))
            {
                problems.Add($"{id} is not of the form FX-<AREA>-<nnn>.");
            }

            if (!declared.Add(id))
            {
                problems.Add($"{id} is declared twice.");
            }

            if (retired.Contains(id))
            {
                problems.Add($"{id} is retired, and a retired number is never used again.");
            }

            if (!repository.FixtureDirectories.Contains(id))
            {
                problems.Add($"{id} has no directory under fixtures/.");
            }

            foreach ((string field, string member, string what) in Artifacts)
            {
                string path = fixture.TryGetProperty(field, out JsonElement part) && part.ValueKind == JsonValueKind.Object
                    ? Text(part, member)
                    : string.Empty;
                if (path.Length == 0 || !repository.FileExists(path))
                {
                    problems.Add($"{id} names {what} that does not exist: {path}");
                }
            }

            string milestone = Text(fixture, "milestone");
            if (!repository.Milestones.Contains(milestone))
            {
                problems.Add($"{id} serves milestone {milestone}, which the plan does not define.");
            }

            foreach (string item in Strings(fixture, "backlog"))
            {
                if (!repository.BacklogItems.Contains(item))
                {
                    problems.Add($"{id} names backlog item {item}, which the plan does not list.");
                }
            }

            // A fixture covers exactly the contracts its tests are named for: a claim no test asserts is decoration, and a
            // test whose contract it leaves out understates what it holds.
            var asserted = new HashSet<string>(StringComparer.Ordinal);
            foreach (string test in Strings(fixture, "tests"))
            {
                if (!repository.TestNames.Contains(test))
                {
                    problems.Add($"{id} names a test that does not exist: {test}");
                    continue;
                }

                asserted.Add(test[..test.IndexOf(':', StringComparison.Ordinal)]);
            }

            var covers = new HashSet<string>(StringComparer.Ordinal);
            bool claims = fixture.TryGetProperty("covers", out JsonElement claimed) && claimed.ValueKind == JsonValueKind.Object;
            foreach ((string key, char letter) in CoverKeys)
            {
                foreach (string contract in claims ? Strings(claimed, key) : [])
                {
                    if (contract.Length < 2 || contract[0] != letter)
                    {
                        problems.Add($"{id} lists {contract} among its {key}.");
                    }

                    covers.Add(contract);
                }
            }

            foreach (string contract in covers.Except(asserted, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                problems.Add($"{id} claims {contract}, which none of its tests asserts.");
            }

            foreach (string contract in asserted.Except(covers, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                problems.Add($"{id} lists a test asserting {contract}, which it does not claim.");
            }
        }

        foreach (string directory in repository.FixtureDirectories.Order(StringComparer.Ordinal))
        {
            if (!declared.Contains(directory) && !retired.Contains(directory))
            {
                problems.Add($"fixtures/{directory} is declared by no entry.");
            }
        }

        foreach ((string id, string where) in repository.NamedFixtures.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!declared.Contains(id) && !retired.Contains(id))
            {
                problems.Add($"{where} names {id}, which the index neither holds nor has retired.");
            }
        }

        return problems;
    }

    /// <summary>Every fixture ID a text names, in the form decision 1 of ADR-051 gives.</summary>
    internal static IEnumerable<string> NamedIn(string text) =>
        NamePattern().Matches(text).Select(match => match.Value);

    private static JsonElement[] Items(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement items) && items.ValueKind == JsonValueKind.Array
            ? [.. items.EnumerateArray()]
            : [];

    private static IEnumerable<string> Strings(JsonElement element, string name) =>
        Items(element, name).Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : item.ToString());

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : string.Empty;

    [GeneratedRegex("^FX-[A-Z]+-[0-9]{3}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"\bFX-[A-Z]+-[0-9]{3}\b")]
    private static partial Regex NamePattern();
}
