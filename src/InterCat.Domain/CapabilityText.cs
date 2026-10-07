using System.Globalization;

namespace InterCat.Domain;

/// <summary>
/// A capability's state, a mechanism's tier and a source's overhead as InterCat names them to a person (R5): "permission
/// denied", "traffic visualization", "moderate", as §14.2 names the tiers, where the enumerations run their words
/// together. A value this version does not know is named by its number, never guessed.
/// </summary>
public static class CapabilityText
{
    /// <summary>A source's or a mechanism's capability state: "available", "disabled by the profile".</summary>
    public static string State(CapabilityState state) => state switch
    {
        CapabilityState.Available => "available",
        CapabilityState.Experimental => "experimental",
        CapabilityState.Unsupported => "unsupported",
        CapabilityState.PermissionDenied => "permission denied",
        CapabilityState.DisabledByProfile => "disabled by the profile",
        CapabilityState.SchemaUnknown => "schema unknown",
        CapabilityState.ProviderFailed => "provider failed",
        _ => string.Create(CultureInfo.InvariantCulture, $"state {(int)state}"),
    };

    /// <summary>A mechanism's measured tier: "traffic visualization", "topology only", "experimental evidence".</summary>
    public static string Tier(CapabilityTier tier) => tier switch
    {
        CapabilityTier.TrafficVisualization => "traffic visualization",
        CapabilityTier.TopologyOnly => "topology only",
        CapabilityTier.ExperimentalEvidence => "experimental evidence",
        CapabilityTier.Unsupported => "unsupported",
        _ => string.Create(CultureInfo.InvariantCulture, $"tier {(int)tier}"),
    };

    /// <summary>A source's measured overhead class: "unmeasured", "low", "moderate", "high".</summary>
    public static string Overhead(OverheadClass overhead) => overhead switch
    {
        OverheadClass.Unmeasured => "unmeasured",
        OverheadClass.Low => "low",
        OverheadClass.Moderate => "moderate",
        OverheadClass.High => "high",
        _ => string.Create(CultureInfo.InvariantCulture, $"overhead {(int)overhead}"),
    };
}
