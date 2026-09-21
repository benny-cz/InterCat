namespace InterCat.Capture.Windows;

/// <summary>
/// Compiles provider requests from the same admitted descriptors used by the callback. Keeping this in
/// the adapter prevents preview, live capture and comparison tooling from drifting to different filters.
/// </summary>
public static class ProviderEnablementCompiler
{
    public static IReadOnlyList<ProviderEnablementRequest> Compile(
        IReadOnlyList<SourceAdmissionPlan> plans,
        bool requestCallStacks = false)
    {
        ArgumentNullException.ThrowIfNull(plans);

        var requests = new List<ProviderEnablementRequest>(plans.Count);
        foreach (SourceAdmissionPlan plan in plans)
        {
            WindowsSourceDefinition definition = WindowsSourceCatalog.Find(plan.SourceId)
                ?? throw new InvalidOperationException(
                    $"Admission plan '{plan.SourceId}' has no source-catalog definition.");

            requests.Add(new()
            {
                SourceId = definition.SourceId,
                ProviderName = definition.ProviderName,
                ProviderGuid = plan.ProviderGuid,
                Level = ResolveLevel(definition.Level),
                MatchAnyKeyword = definition.MatchAnyKeyword,
                MatchAllKeyword = definition.MatchAllKeyword,
                EventIdsToEnable = [.. plan.Events.Select(item => item.EventId).Distinct().Order()],
                EventIdsToDisable = definition.DeniedEventIds,
                RequestCaptureState = definition.SupportsCaptureState,
                RequestCallStacks = requestCallStacks,
            });
        }

        return requests;
    }

    private static int ResolveLevel(string level) => level.ToLowerInvariant() switch
    {
        "win:logalways" => 0,
        "win:critical" => 1,
        "win:error" => 2,
        "win:warning" => 3,
        "win:informational" => 4,
        "win:verbose" => 5,
        _ => throw new InvalidOperationException(
            $"Source catalog level '{level}' has no reviewed ETW numeric mapping."),
    };
}
