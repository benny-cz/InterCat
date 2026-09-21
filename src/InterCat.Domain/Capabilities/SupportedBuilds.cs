namespace InterCat.Domain;

/// <summary>Support tier of a Windows build (section 1.3). A build outside the matrix is untested, never "probably working".</summary>
public enum BuildSupportTier
{
    Primary = 1,
    Secondary = 2,
    Candidate = 3,
    Untested = 4,
}

/// <summary>One supported build candidate from section 1.3.</summary>
public sealed record SupportedBuild(string Name, int BuildNumber, string Architecture, BuildSupportTier Tier);

/// <summary>
/// The section 1.3 support matrix. A build is supported only when its fixture corpus passes on it, so this
/// list states candidacy; fixture evidence, not membership, is what promotes a claim (section 13.4).
/// </summary>
public static class SupportedBuilds
{
    public static IReadOnlyList<SupportedBuild> All { get; } =
    [
        new("Windows 11 24H2 x64", 26100, "X64", BuildSupportTier.Primary),
        new("Windows 11 23H2 x64", 22631, "X64", BuildSupportTier.Secondary),
        new("Windows Server 2025 x64", 26100, "X64", BuildSupportTier.Secondary),
        new("Windows Server 2022 x64", 20348, "X64", BuildSupportTier.Candidate),
        new("Windows 11 24H2 ARM64", 26100, "Arm64", BuildSupportTier.Candidate),
    ];

    /// <summary>Resolves the tier of a build number and architecture, or <see cref="BuildSupportTier.Untested"/>.</summary>
    public static BuildSupportTier TierOf(int buildNumber, string architecture)
    {
        BuildSupportTier best = BuildSupportTier.Untested;
        foreach (SupportedBuild candidate in All)
        {
            if (candidate.BuildNumber != buildNumber
                || !string.Equals(candidate.Architecture, architecture, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (candidate.Tier < best)
            {
                best = candidate.Tier;
            }
        }

        return best;
    }

    public static bool IsSupported(int buildNumber, string architecture) =>
        TierOf(buildNumber, architecture) != BuildSupportTier.Untested;
}
