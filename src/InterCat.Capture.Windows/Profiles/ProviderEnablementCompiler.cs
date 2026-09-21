namespace InterCat.Capture.Windows;

/// <summary>
/// Compiles provider requests from the same admitted descriptors used by the callback. Keeping this in
/// the adapter prevents preview, live capture and comparison tooling from drifting to different filters.
/// </summary>
public static class ProviderEnablementCompiler
{
    public static IReadOnlyList<ProviderEnablementRequest> Compile(
        IReadOnlyList<SourceAdmissionPlan> plans,
        bool requestCallStacks = false,
        IReadOnlyDictionary<string, IReadOnlyList<int>>? processFilters = null)
    {
        ArgumentNullException.ThrowIfNull(plans);

        var requests = new List<ProviderEnablementRequest>(plans.Count);
        foreach (SourceAdmissionPlan plan in plans)
        {
            WindowsSourceDefinition definition = WindowsSourceCatalog.Find(plan.SourceId)
                ?? throw new InvalidOperationException(
                    $"Admission plan '{plan.SourceId}' has no source-catalog definition.");

            IReadOnlyList<int> processIds = [];
            if (processFilters?.TryGetValue(plan.SourceId, out IReadOnlyList<int>? requestedIds) == true)
            {
                if (requestedIds is null)
                {
                    throw new ArgumentException(
                        $"Source '{plan.SourceId}' has a null process filter.",
                        nameof(processFilters));
                }

                if (!definition.SupportsCaptureSideProcessFilter)
                {
                    throw new InvalidOperationException(
                        $"Source '{plan.SourceId}' cannot enforce a capture-side process filter.");
                }

                if (requestedIds.Count > 64
                    || requestedIds.Any(processId => processId <= 0)
                    || requestedIds.Distinct().Count() != requestedIds.Count)
                {
                    throw new ArgumentException(
                        $"Source '{plan.SourceId}' has an invalid process filter. IDs must be positive, "
                        + "unique, and limited to 64 entries.",
                        nameof(processFilters));
                }

                processIds = [.. requestedIds.Order()];
            }

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
                ProcessIdsToInclude = processIds,
                RequestCaptureState = definition.SupportsCaptureState,
                RequestCallStacks = requestCallStacks,
            });
        }

        if (processFilters is not null)
        {
            HashSet<string> compiled = [.. plans.Select(plan => plan.SourceId)];
            string? unused = processFilters.Keys.FirstOrDefault(sourceId => !compiled.Contains(sourceId));
            if (unused is not null)
            {
                throw new ArgumentException(
                    $"Process filter names source '{unused}', which is not in the compiled plan.",
                    nameof(processFilters));
            }
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
