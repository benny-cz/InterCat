using System.Text.Json;
using InterCat.Domain;

namespace InterCat.Capture.Windows;

/// <summary>
/// One mechanism's latest fixture evidence on one build (§14.2, §13.5): the tier its fixture measured there, which fixture,
/// and when.
/// </summary>
public sealed record MeasuredCoverageEntry(
    Mechanism Mechanism,
    string Build,
    CapabilityTier Tier,
    string Fixture,
    string Date,
    string SupportTier);

/// <summary>
/// The per-build coverage this build of InterCat carries (`docs/PER-BUILD-COVERAGE.md`): the fixture index's measured tiers,
/// projected by the traceability tests into a file the product embeds, so a capability report can say what was measured on
/// its own build without claiming what was measured on another (P27).
/// </summary>
public static class MeasuredCoverage
{
    private const string ResourceName = "InterCat.Capture.Windows.measured-coverage.json";

    private static readonly Lazy<IReadOnlyList<MeasuredCoverageEntry>> Loaded = new(Load);

    /// <summary>Every mechanism's latest evidence on every build the fixtures measured.</summary>
    public static IReadOnlyList<MeasuredCoverageEntry> Entries => Loaded.Value;

    /// <summary>The evidence of <paramref name="mechanism"/> on exactly <paramref name="build"/>; null when none measured it there.</summary>
    public static MeasuredCoverageEntry? On(Mechanism mechanism, string build) =>
        Entries.FirstOrDefault(entry => entry.Mechanism == mechanism && string.Equals(entry.Build, build, StringComparison.Ordinal));

    /// <summary>The evidence of <paramref name="mechanism"/> on builds other than <paramref name="build"/>, the latest first.</summary>
    public static IReadOnlyList<MeasuredCoverageEntry> Elsewhere(Mechanism mechanism, string build) =>
        [.. Entries.Where(entry => entry.Mechanism == mechanism && !string.Equals(entry.Build, build, StringComparison.Ordinal))
            .OrderByDescending(entry => entry.Date, StringComparer.Ordinal)];

    /// <summary>Reads the embedded coverage; a build without it carries none, which leaves every tier unmeasured.</summary>
    public static IReadOnlyList<MeasuredCoverageEntry> Read(Stream? stream)
    {
        if (stream is null)
        {
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(stream);
        var entries = new List<MeasuredCoverageEntry>();
        foreach (JsonElement entry in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            entries.Add(new(
                Enum.Parse<Mechanism>(entry.GetProperty("mechanism").GetString()!),
                entry.GetProperty("build").GetString()!,
                Enum.Parse<CapabilityTier>(entry.GetProperty("tier").GetString()!),
                entry.GetProperty("fixture").GetString()!,
                entry.GetProperty("date").GetString()!,
                entry.GetProperty("supportTier").GetString()!));
        }

        return entries;
    }

    private static IReadOnlyList<MeasuredCoverageEntry> Load()
    {
        using Stream? stream = typeof(MeasuredCoverage).Assembly.GetManifestResourceStream(ResourceName);
        return Read(stream);
    }
}
