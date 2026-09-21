namespace InterCat.Domain;

/// <summary>Support tier of a Windows build (section 1.3). A build outside the matrix is untested, never "probably working".</summary>
public enum BuildSupportTier
{
    Primary = 1,
    Secondary = 2,
    Candidate = 3,
    Untested = 4,
}

/// <summary>
/// One supported build from section 1.3. A release is listed by its exact build numbers, never by a range,
/// because a range would silently accept a build nobody has run the fixture corpus on.
/// </summary>
public sealed record SupportedBuild(string Name, int BuildNumber, string Architecture, BuildSupportTier Tier)
{
    /// <summary>
    /// True when this entry is a pre-release servicing branch of the named release rather than its
    /// retail build. It is supported, and every result says which of the two it was (ADR-007).
    /// </summary>
    public bool IsPrerelease { get; init; }
}

/// <summary>What the matrix says about one build: its tier, the release it belongs to, and its branch.</summary>
public readonly record struct BuildSupport(BuildSupportTier Tier, string? ReleaseName, bool IsPrerelease)
{
    public bool IsSupported => Tier != BuildSupportTier.Untested;

    /// <summary>The support statement a report prints. It never says "probably".</summary>
    public string Describe() => Tier switch
    {
        BuildSupportTier.Untested => "untested: this build is not in the section 1.3 matrix",
        _ when IsPrerelease => $"{ReleaseName}, pre-release servicing branch, {Tier} tier",
        _ => $"{ReleaseName}, {Tier} tier",
    };
}

/// <summary>
/// The section 1.3 support matrix. A build is supported only when its fixture corpus passes on it, so this
/// list states candidacy; fixture evidence, not membership, is what promotes a claim (section 13.4).
/// </summary>
public static class SupportedBuilds
{
    public static IReadOnlyList<SupportedBuild> All { get; } =
    [
        new("Windows 11 25H2 x64", 26200, "X64", BuildSupportTier.Primary),
        new("Windows 11 25H2 x64", 26220, "X64", BuildSupportTier.Primary) { IsPrerelease = true },
        new("Windows 11 24H2 x64", 26100, "X64", BuildSupportTier.Primary),
        new("Windows 11 23H2 x64", 22631, "X64", BuildSupportTier.Secondary),
        new("Windows Server 2025 x64", 26100, "X64", BuildSupportTier.Secondary),
        new("Windows Server 2022 x64", 20348, "X64", BuildSupportTier.Candidate),
        new("Windows 11 24H2 ARM64", 26100, "Arm64", BuildSupportTier.Candidate),
    ];

    /// <summary>
    /// Resolves what the matrix says about a build. When a build number appears more than once, the best
    /// tier wins and a retail entry is preferred over a pre-release one at the same tier.
    /// </summary>
    public static BuildSupport Resolve(int buildNumber, string architecture)
    {
        var best = new BuildSupport(BuildSupportTier.Untested, null, false);
        foreach (SupportedBuild candidate in All)
        {
            if (candidate.BuildNumber != buildNumber
                || !string.Equals(candidate.Architecture, architecture, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool better = candidate.Tier < best.Tier
                || (candidate.Tier == best.Tier && best.IsPrerelease && !candidate.IsPrerelease);
            if (better)
            {
                best = new(candidate.Tier, candidate.Name, candidate.IsPrerelease);
            }
        }

        return best;
    }

    /// <summary>Resolves the tier of a build number and architecture, or <see cref="BuildSupportTier.Untested"/>.</summary>
    public static BuildSupportTier TierOf(int buildNumber, string architecture) =>
        Resolve(buildNumber, architecture).Tier;

    public static bool IsSupported(int buildNumber, string architecture) =>
        Resolve(buildNumber, architecture).IsSupported;
}
