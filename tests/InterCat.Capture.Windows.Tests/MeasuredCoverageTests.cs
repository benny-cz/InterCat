using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Domain;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// Per-build coverage (§13.5, §14.2, M3's exit gate): the fixture index's measured tiers, the coverage the product embeds
/// from them and the page that publishes them agree, and a capability report claims a tier only on the build its fixture
/// measured (P27).
/// </summary>
public sealed partial class MeasuredCoverageTests
{
    [Fact(DisplayName = "P27: a fixture's tiers are tiers, agree with what its results say, and belong to its mechanism")]
    public void FixtureTiersAgreeWithTheirResults()
    {
        using JsonDocument index = Index();
        foreach (JsonElement fixture in index.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            string id = fixture.GetProperty("id").GetString()!;
            bool hasMechanism = fixture.TryGetProperty("mechanism", out JsonElement mechanism);
            Assert.True(!hasMechanism || Enum.TryParse(mechanism.GetString(), out Mechanism _), $"{id} names no mechanism InterCat defines.");
            foreach (JsonElement entry in Environments(fixture))
            {
                if (!entry.TryGetProperty("tier", out JsonElement tier))
                {
                    continue;
                }

                Assert.True(hasMechanism, $"{id} measures a tier but names no mechanism it is of.");
                Assert.True(Enum.TryParse(tier.GetString(), out CapabilityTier _), $"{id}: '{tier.GetString()}' is no tier.");

                // What the result says in words and the tier it states agree wherever the words name one.
                Match said = TierSaid().Match(entry.GetProperty("result").GetString()!);
                Assert.True(!said.Success || said.Groups[1].Value == tier.GetString(),
                    $"{id} on {entry.GetProperty("build").GetString()}: its result says tier {said.Groups[1].Value}, its tier is {tier.GetString()}.");
            }
        }
    }

    [Fact(DisplayName = "P27: the coverage a build carries, and the page that publishes it, are the index's latest evidence of each mechanism on each build")]
    public void EmbeddedCoverageIsTheIndexs()
    {
        using JsonDocument index = Index();
        MeasuredCoverageEntry[] projected = Project(index);
        Assert.Equal(projected, MeasuredCoverage.Entries);

        string page = Render(projected);
        string published = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "PER-BUILD-COVERAGE.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.True(page == published, "docs/PER-BUILD-COVERAGE.md is not the index's coverage; it should read:\n" + page);
    }

    [Fact(DisplayName = "P27: a build's coverage names a tier measured on it, and names another build's evidence as another's")]
    public void CoverageIsPerBuild()
    {
        const string build = "10.0.26220.0-x64";
        Assert.Equal((CapabilityTier.TrafficVisualization, "FX-TCP-002"),
            (MeasuredCoverage.On(Mechanism.Tcp, build)!.Tier, MeasuredCoverage.On(Mechanism.Tcp, build)!.Fixture));
        Assert.Null(MeasuredCoverage.On(Mechanism.Tcp, "10.0.99999.0-x64"));
        Assert.Contains(MeasuredCoverage.Elsewhere(Mechanism.Tcp, "10.0.99999.0-x64"), entry => entry.Build == build);
        Assert.Null(MeasuredCoverage.On(Mechanism.SharedSection, build));

        // A build carrying no coverage file claims no tier at all.
        Assert.Empty(MeasuredCoverage.Read(null));
    }

    /// <summary>The latest environment entry with a tier of each mechanism on each build; a later entry wins a tie of dates.</summary>
    private static MeasuredCoverageEntry[] Project(JsonDocument index)
    {
        var latest = new Dictionary<(Mechanism, string), MeasuredCoverageEntry>();
        foreach (JsonElement fixture in index.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            if (!fixture.TryGetProperty("mechanism", out JsonElement mechanism))
            {
                continue;
            }

            foreach (JsonElement entry in Environments(fixture))
            {
                string build = entry.GetProperty("build").GetString()!;
                if (!entry.TryGetProperty("tier", out JsonElement tier) || build == "portable")
                {
                    continue;
                }

                var candidate = new MeasuredCoverageEntry(
                    Enum.Parse<Mechanism>(mechanism.GetString()!),
                    build,
                    Enum.Parse<CapabilityTier>(tier.GetString()!),
                    fixture.GetProperty("id").GetString()!,
                    entry.GetProperty("date").GetString()!,
                    entry.GetProperty("supportTier").GetString()!);
                var key = (candidate.Mechanism, build);
                if (!latest.TryGetValue(key, out MeasuredCoverageEntry? known) || string.CompareOrdinal(candidate.Date, known.Date) >= 0)
                {
                    latest[key] = candidate;
                }
            }
        }

        return [.. latest.Values
            .OrderBy(entry => entry.Mechanism.ToString(), StringComparer.Ordinal)
            .ThenBy(entry => entry.Build, StringComparer.Ordinal)];
    }

    private static string Render(IReadOnlyList<MeasuredCoverageEntry> entries)
    {
        var page = new StringBuilder();
        page.Append("# Per-build coverage\n\n")
            .Append("Generated from `fixtures/index.json` - each fixture's mechanism and each environment entry's measured tier - and\n")
            .Append("checked against it by `MeasuredCoverageTests`; edit the index, not this page. For each build, a mechanism's tier is\n")
            .Append("its latest fixture evidence there. A mechanism no fixture measured on a build has no tier on it: `icat capabilities`\n")
            .Append("says so, and never borrows another build's tier (P27).\n");
        foreach (string build in entries.Select(entry => entry.Build).Distinct().Order(StringComparer.Ordinal))
        {
            page.Append('\n').Append("## ").Append(build).Append("\n\n")
                .Append("| Mechanism | Tier | Fixture | Measured | Build support |\n")
                .Append("|---|---|---|---|---|\n");
            foreach (MeasuredCoverageEntry entry in entries.Where(entry => entry.Build == build))
            {
                page.Append("| ").Append(entry.Mechanism).Append(" | ").Append(entry.Tier).Append(" | ").Append(entry.Fixture)
                    .Append(" | ").Append(entry.Date).Append(" | ").Append(entry.SupportTier).Append(" |\n");
            }
        }

        return page.ToString();
    }

    private static JsonElement[] Environments(JsonElement fixture) =>
        fixture.GetProperty("environment") is { ValueKind: JsonValueKind.Array } list ? [.. list.EnumerateArray()] : [fixture.GetProperty("environment")];

    private static JsonDocument Index() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "fixtures", "index.json")));

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }

    [GeneratedRegex(@"\bTier (\w+)")]
    private static partial Regex TierSaid();
}
