using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.CaptureBroker;

/// <summary>Version identifiers for the broker boundary. Only the in-process prepare handoff exists today.</summary>
public static class BrokerProtocol
{
    public const int Version = 1;
    public const string PreparedPlanDigestAlgorithm = "sha256";
}

/// <summary>The capture-affecting runtime identity a locally compiled plan must still match at prepare time.</summary>
public sealed record BrokerRuntimeIdentity(
    string BuildId,
    string Architecture,
    string AdapterVersion)
{
    public static BrokerRuntimeIdentity Current()
    {
        ProbeEnvironment environment = CapabilityInventoryProbe.DescribeEnvironment();
        return new(environment.BuildId, environment.Architecture, CapabilityInventoryProbe.AdapterVersion);
    }
}

public enum BrokerPrepareRefusalCode
{
    InvalidRuntimeIdentity = 1,
    PlanNotStartable = 2,
    UnsupportedBuild = 3,
    RuntimeBuildMismatch = 4,
    RuntimeArchitectureMismatch = 5,
    RuntimeAdapterMismatch = 6,
    EffectivePlanMismatch = 7,
    UnsupportedAdmissionPolicy = 8,
    UnsupportedContentCapture = 9,
    UnsupportedOriginalEvidence = 10,
    InvalidCapturePlan = 11,
    InvalidOperationalLimits = 12,
}

public sealed record BrokerPrepareRefusal(BrokerPrepareRefusalCode Code, string Message);

/// <summary>A typed prepare outcome. A refusal never produces a partly usable plan.</summary>
public sealed class BrokerPrepareResult
{
    private BrokerPrepareResult(PreparedCapturePlan? preparedPlan, BrokerPrepareRefusal? refusal)
    {
        PreparedPlan = preparedPlan;
        Refusal = refusal;
    }

    public PreparedCapturePlan? PreparedPlan { get; }
    public BrokerPrepareRefusal? Refusal { get; }
    public bool IsPrepared => PreparedPlan is not null;

    internal static BrokerPrepareResult Prepared(PreparedCapturePlan plan) => new(plan, null);

    internal static BrokerPrepareResult Refused(BrokerPrepareRefusalCode code, string message) =>
        new(null, new(code, message));
}

/// <summary>
/// Deep-frozen, capture-affecting output of the local prepare step. The digest names the exact semantics;
/// it is not a bearer token, authentication proof, lease, or permission to start a session.
/// </summary>
public sealed class PreparedCapturePlan
{
    internal PreparedCapturePlan(
        DateTimeOffset compiledAtUtc,
        string buildId,
        string architecture,
        string adapterVersion,
        string requestedProfileId,
        string effectiveProfileId,
        AdmissionMode requestedAdmission,
        AdmissionMode effectiveAdmission,
        BrokerCaptureQuota quota,
        BrokerRetentionPolicy retention,
        BrokerJournalPublication publication,
        CompiledBodyAdmissionPolicy bodyPolicy,
        ImmutableArray<SourceAdmissionPlan> sources,
        ImmutableArray<ProviderEnablementRequest> providers,
        CaptureScopeDecision scope,
        bool preserveExtendedData,
        bool requestCallStacks,
        string digest)
    {
        ProtocolVersion = BrokerProtocol.Version;
        CompiledAtUtc = compiledAtUtc;
        BuildId = buildId;
        Architecture = architecture;
        AdapterVersion = adapterVersion;
        RequestedProfileId = requestedProfileId;
        EffectiveProfileId = effectiveProfileId;
        RequestedAdmission = requestedAdmission;
        EffectiveAdmission = effectiveAdmission;
        Quota = quota;
        Retention = retention;
        Publication = publication;
        PublicationInterval = BrokerJournalPublicationPolicy.Interval(publication, quota.MaximumDurationSeconds);
        BodyPolicy = bodyPolicy;
        Sources = sources;
        Providers = providers;
        Scope = scope;
        PreserveExtendedData = preserveExtendedData;
        RequestCallStacks = requestCallStacks;
        Digest = digest;
    }

    public int ProtocolVersion { get; }
    public DateTimeOffset CompiledAtUtc { get; }
    public string BuildId { get; }
    public string Architecture { get; }
    public string AdapterVersion { get; }
    public string RequestedProfileId { get; }
    public string EffectiveProfileId { get; }
    public AdmissionMode RequestedAdmission { get; }
    public AdmissionMode EffectiveAdmission { get; }
    public BrokerCaptureQuota Quota { get; }
    public BrokerRetentionPolicy Retention { get; }
    public BrokerJournalPublication Publication { get; }

    /// <summary>How often the runtime publishes journal chunks; null publishes once, when the capture stops.</summary>
    public TimeSpan? PublicationInterval { get; }
    public CompiledBodyAdmissionPolicy BodyPolicy { get; }
    public ImmutableArray<SourceAdmissionPlan> Sources { get; }
    public ImmutableArray<ProviderEnablementRequest> Providers { get; }
    public CaptureScopeDecision Scope { get; }
    public bool PreserveExtendedData { get; }
    public bool RequestCallStacks { get; }
    public string Digest { get; }
}

/// <summary>
/// Freezes the result of a profile compilation performed inside the broker process. This type is not a
/// wire deserializer: accepting a client-supplied effective plan would move allowlist decisions across
/// the privilege boundary and is deliberately outside this contract.
/// </summary>
public static class BrokerPrepareCompiler
{
    private const int MaximumSources = 32;
    private const int MaximumEventsPerSource = 4096;

    public static BrokerPrepareResult Prepare(
        EffectiveCapturePlan plan,
        BrokerCaptureQuota quota,
        BrokerRetentionPolicy retention,
        BrokerRuntimeIdentity? runtime = null,
        BrokerJournalPublication publication = BrokerJournalPublication.OnStop)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(quota);
        runtime ??= BrokerRuntimeIdentity.Current();

        BrokerPrepareResult? refusal = Validate(plan, quota, retention, runtime);
        if (refusal is null && !Enum.IsDefined(publication))
        {
            refusal = Refused(
                BrokerPrepareRefusalCode.InvalidOperationalLimits,
                "Journal publication must be OnStop or Live.");
        }

        if (refusal is not null)
        {
            return refusal;
        }

        CompiledBodyAdmissionPolicy bodyPolicy = Clone(plan.BodyPolicy!);
        ImmutableArray<SourceAdmissionPlan> sources =
        [
            .. plan.Sources
                .OrderBy(source => source.SourceIndex)
                .ThenBy(source => source.SourceId, StringComparer.Ordinal)
                .Select(source => Clone(source, bodyPolicy)),
        ];
        ImmutableArray<ProviderEnablementRequest> providers =
        [
            .. plan.Providers
                .OrderBy(provider => provider.SourceId, StringComparer.Ordinal)
                .Select(Clone),
        ];
        CaptureScopeDecision scope = Clone(plan.Scope);
        string digest = PreparedPlanDigest.Compute(
            plan.CompiledAtUtc,
            plan.Environment.BuildId,
            plan.Environment.Architecture,
            plan.AdapterVersion,
            plan.RequestedProfileId,
            plan.EffectiveProfileId!,
            plan.RequestedAdmission,
            plan.EffectiveAdmission!.Value,
            quota,
            retention,
            publication,
            bodyPolicy,
            sources,
            providers,
            scope,
            plan.PreserveExtendedData,
            plan.RequestCallStacks);

        return BrokerPrepareResult.Prepared(new(
            plan.CompiledAtUtc,
            plan.Environment.BuildId,
            plan.Environment.Architecture,
            plan.AdapterVersion,
            plan.RequestedProfileId,
            plan.EffectiveProfileId!,
            plan.RequestedAdmission,
            plan.EffectiveAdmission.Value,
            quota,
            retention,
            publication,
            bodyPolicy,
            sources,
            providers,
            scope,
            plan.PreserveExtendedData,
            plan.RequestCallStacks,
            digest));
    }

    private static BrokerPrepareResult? Validate(
        EffectiveCapturePlan plan,
        BrokerCaptureQuota quota,
        BrokerRetentionPolicy retention,
        BrokerRuntimeIdentity runtime)
    {
        if (string.IsNullOrWhiteSpace(runtime.BuildId)
            || string.IsNullOrWhiteSpace(runtime.Architecture)
            || string.IsNullOrWhiteSpace(runtime.AdapterVersion))
        {
            return Refused(
                BrokerPrepareRefusalCode.InvalidRuntimeIdentity,
                "The broker runtime identity is incomplete; no capture was prepared.");
        }

        string? quotaProblem = quota.Validate();
        if (quotaProblem is not null || retention != BrokerRetentionPolicy.StopAtLimit)
        {
            return Refused(
                BrokerPrepareRefusalCode.InvalidOperationalLimits,
                quotaProblem ?? "Stop-at-limit is the only broker retention policy in protocol v1.");
        }

        if (!plan.CanStart)
        {
            return Refused(
                BrokerPrepareRefusalCode.PlanNotStartable,
                "The effective plan is blocked. Resolve its capability, scope, or consent diagnostics before preparing capture.");
        }

        if (plan.Environment is null
            || plan.Sources is null
            || plan.Providers is null
            || plan.SourceDecisions is null
            || plan.Scope is null
            || plan.OriginalEvidence is null)
        {
            return Refused(BrokerPrepareRefusalCode.InvalidCapturePlan, "The effective plan is incomplete.");
        }

        if (!plan.Environment.IsSupportedBuild)
        {
            return Refused(
                BrokerPrepareRefusalCode.UnsupportedBuild,
                $"Build '{plan.Environment.BuildId}' is outside the measured support matrix; no capture was prepared.");
        }

        if (!string.Equals(plan.Environment.BuildId, runtime.BuildId, StringComparison.Ordinal))
        {
            return Refused(
                BrokerPrepareRefusalCode.RuntimeBuildMismatch,
                $"The plan targets build '{plan.Environment.BuildId}', but the broker is running on '{runtime.BuildId}'. Re-probe and review the effective plan.");
        }

        if (!string.Equals(plan.Environment.Architecture, runtime.Architecture, StringComparison.Ordinal))
        {
            return Refused(
                BrokerPrepareRefusalCode.RuntimeArchitectureMismatch,
                $"The plan targets architecture '{plan.Environment.Architecture}', but the broker is running on '{runtime.Architecture}'. Re-probe and review the effective plan.");
        }

        if (!string.Equals(plan.AdapterVersion, runtime.AdapterVersion, StringComparison.Ordinal))
        {
            return Refused(
                BrokerPrepareRefusalCode.RuntimeAdapterMismatch,
                $"The plan uses adapter '{plan.AdapterVersion}', but the broker uses '{runtime.AdapterVersion}'. Re-probe and review the effective plan.");
        }

        if (plan.Content is not null)
        {
            return Refused(
                BrokerPrepareRefusalCode.UnsupportedContentCapture,
                "Broker protocol v1 does not prepare content capture. Request-preview consent is not capture authorization.");
        }

        if (plan.OriginalEvidence.Requested || plan.OriginalEvidence.WillStart)
        {
            return Refused(
                BrokerPrepareRefusalCode.UnsupportedOriginalEvidence,
                "Broker protocol v1 does not prepare a companion diagnostic ETL or other original evidence.");
        }

        if (string.IsNullOrWhiteSpace(plan.RequestedProfileId)
            || string.IsNullOrWhiteSpace(plan.EffectiveProfileId)
            || !string.Equals(plan.RequestedProfileId, plan.EffectiveProfileId, StringComparison.Ordinal)
            || plan.EffectiveAdmission is null
            || plan.RequestedAdmission != plan.EffectiveAdmission)
        {
            return Refused(
                BrokerPrepareRefusalCode.EffectivePlanMismatch,
                "Requested and effective profile/admission identities must match exactly; the broker never applies a fallback.");
        }

        string? profileProblem = ValidateProfile(plan);
        if (profileProblem is not null)
        {
            return Refused(BrokerPrepareRefusalCode.EffectivePlanMismatch, profileProblem);
        }

        if (plan.BodyPolicy is null)
        {
            return Refused(
                BrokerPrepareRefusalCode.UnsupportedAdmissionPolicy,
                "The effective plan has no compiled body-admission policy.");
        }

        try
        {
            CaptureBodyAdmissionPolicies.EnsureSupported(plan.BodyPolicy);
        }
        catch (NotSupportedException exception)
        {
            return Refused(BrokerPrepareRefusalCode.UnsupportedAdmissionPolicy, exception.Message);
        }

        if (plan.Sources.Count is < 1 or > MaximumSources
            || plan.Providers.Count != plan.Sources.Count)
        {
            return Refused(
                BrokerPrepareRefusalCode.InvalidCapturePlan,
                $"A prepared capture requires 1-{MaximumSources} sources and exactly one provider request per source.");
        }

        string? sourceProblem = ValidateSources(plan.Sources, plan.BodyPolicy);
        if (sourceProblem is not null)
        {
            return Refused(BrokerPrepareRefusalCode.InvalidCapturePlan, sourceProblem);
        }

        string? scopeProblem = ValidateScope(plan);
        if (scopeProblem is not null)
        {
            return Refused(BrokerPrepareRefusalCode.InvalidCapturePlan, scopeProblem);
        }

        string? decisionProblem = ValidateSourceDecisions(plan);
        if (decisionProblem is not null)
        {
            return Refused(BrokerPrepareRefusalCode.InvalidCapturePlan, decisionProblem);
        }

        string? providerProblem = ValidateProviders(plan);
        return providerProblem is null
            ? null
            : Refused(BrokerPrepareRefusalCode.InvalidCapturePlan, providerProblem);
    }

    private static string? ValidateProfile(EffectiveCapturePlan plan)
    {
        CaptureProfileDescriptor? profile = CaptureProfileCatalog.Find(plan.EffectiveProfileId!);
        if (profile is null
            || !profile.CompilationAvailable
            || profile.Admission != plan.EffectiveAdmission
            || profile.PreserveExtendedData != plan.PreserveExtendedData
            || profile.RequestCallStacks != plan.RequestCallStacks)
        {
            return $"Effective profile '{plan.EffectiveProfileId}' does not match a startable catalog profile.";
        }

        HashSet<string> admitted = [.. plan.Sources.Select(source => source.SourceId)];
        if (profile.Sources.Any(requirement => requirement.Required && !admitted.Contains(requirement.SourceId))
            || admitted.Any(sourceId => !profile.Sources.Any(requirement =>
                string.Equals(requirement.SourceId, sourceId, StringComparison.Ordinal))))
        {
            return $"Effective profile '{profile.Id}' does not contain its exact required/allowlisted source set.";
        }

        return null;
    }

    private static string? ValidateSources(
        IReadOnlyList<SourceAdmissionPlan> sources,
        CompiledBodyAdmissionPolicy bodyPolicy)
    {
        if (sources.Any(source => source is null))
        {
            return "A source admission entry is null.";
        }

        if (sources.Select(source => source.SourceId).Distinct(StringComparer.Ordinal).Count() != sources.Count
            || sources.Select(source => source.SourceIndex).Distinct().Count() != sources.Count)
        {
            return "Source IDs and source indexes must each be unique.";
        }

        try
        {
            _ = new EventAdmissionTable(sources);
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }

        foreach (SourceAdmissionPlan source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.SourceId)
                || source.SourceIndex < 0
                || source.ProviderGuid == Guid.Empty
                || source.Events is null
                || source.Events.Count is < 1 or > MaximumEventsPerSource)
            {
                return $"Source '{source.SourceId}' has an invalid identity or event count.";
            }

            foreach (AdmittedEventPlan item in source.Events)
            {
                if (item is null
                    || item.SourceIndex != source.SourceIndex
                    || item.ProviderGuid != source.ProviderGuid
                    || item.EventId is < 0 or > ushort.MaxValue
                    || item.Version is < 0 or > byte.MaxValue
                    || string.IsNullOrWhiteSpace(item.Name)
                    || item.MinimumBodyLength < 0
                    || item.PointerSize is not (4 or 8)
                    || !IsSha256Identity(item.SchemaFingerprint)
                    || item.Slots is null
                    || item.Slots.Count > AdmissionPlanCompiler.MaximumSlots
                    || !BodyPoliciesEqual(bodyPolicy, item.BodyPolicy))
                {
                    return $"Source '{source.SourceId}' contains an invalid admitted descriptor.";
                }

                foreach (AdmittedSlotPlan slot in item.Slots)
                {
                    int minimumWidth = slot.Kind switch
                    {
                        AdmittedSlotKind.ResourceName => 2,
                        AdmittedSlotKind.AnsiResourceName => 1,
                        AdmittedSlotKind.ResourceNameAfterSid => AdmissionPlanCompiler.MinimumSidLength,
                        AdmittedSlotKind.Identifier => 16,
                        _ => slot.Width,
                    };
                    bool widthValid = slot.Kind switch
                    {
                        AdmittedSlotKind.Numeric => slot.Width is > 0 and <= AdmissionPlanCompiler.MaximumSlotWidth,
                        AdmittedSlotKind.ResourceName
                            or AdmittedSlotKind.AnsiResourceName
                            or AdmittedSlotKind.ResourceNameAfterSid => slot.Width == 0,
                        AdmittedSlotKind.Identifier => slot.Width == 16,
                        _ => false,
                    };
                    if (string.IsNullOrWhiteSpace(slot.FieldName)
                        || slot.Offset < 0
                        || !widthValid
                        || item.MinimumBodyLength < slot.Offset + minimumWidth
                        || !Enum.IsDefined(slot.Role)
                        || !Enum.IsDefined(slot.Transform)
                        || !Enum.IsDefined(slot.Kind)
                        || (slot.Unit is not null && !Enum.IsDefined(slot.Unit.Value))
                        || (slot.ByteDomain is not null && !Enum.IsDefined(slot.ByteDomain.Value)))
                    {
                        return $"Descriptor {source.ProviderGuid:D}/{item.EventId}/v{item.Version} contains an invalid admitted slot.";
                    }
                }
            }
        }

        return null;
    }

    private static string? ValidateScope(EffectiveCapturePlan plan)
    {
        CaptureScopeDecision scope = plan.Scope;
        if (!ValidProcessIds(scope.RequestedProcessIds)
            || !ValidProcessIds(scope.InitialViewProcessIds)
            || !scope.RequestedProcessIds.Order().SequenceEqual(scope.InitialViewProcessIds.Order()))
        {
            return "Requested and initial-view process IDs must be the same unique set of at most 64 positive IDs.";
        }

        if (scope.RequestedMechanism != scope.EffectiveMechanism)
        {
            return "Requested and effective mechanisms must match exactly; the broker never applies transport fallback.";
        }

        if (scope.BroaderCaptureNeedsConsent && !scope.BroaderCaptureAccepted)
        {
            return "Broader capture was identified but not accepted.";
        }

        if (scope.CapturesOutsideRequestedProcesses
            && scope.RequestedProcessIds.Count > 0
            && (!scope.BroaderCaptureNeedsConsent || !scope.BroaderCaptureAccepted))
        {
            return "A process-focused plan that captures outside the requested PIDs requires explicit broader-capture consent.";
        }

        if (scope.Sources is null
            || scope.Sources.Count != plan.Sources.Count
            || scope.Sources.Select(source => source.SourceId).Distinct(StringComparer.Ordinal).Count() != scope.Sources.Count)
        {
            return "Every admitted source must have exactly one provider-scope decision.";
        }

        bool aggregateOutside = scope.Sources.Any(source => source.CapturesOutsideRequestedProcesses);
        bool consentExpected = scope.RequestedProcessIds.Count > 0 && aggregateOutside;
        if (scope.CapturesOutsideRequestedProcesses != aggregateOutside
            || scope.BroaderCaptureNeedsConsent != consentExpected
            || scope.BroaderCaptureAccepted != consentExpected)
        {
            return "Aggregate process scope and broader-capture consent do not match the per-source decisions.";
        }

        Dictionary<string, ProviderEnablementRequest> providers;
        try
        {
            providers = plan.Providers.ToDictionary(provider => provider.SourceId, StringComparer.Ordinal);
        }
        catch (ArgumentException)
        {
            return "Provider source IDs must be unique.";
        }

        foreach (ProviderScopeDecision sourceScope in scope.Sources)
        {
            if (!providers.TryGetValue(sourceScope.SourceId, out ProviderEnablementRequest? provider)
                || !ValidProcessIds(sourceScope.AppliedProcessIds)
                || !sourceScope.AppliedProcessIds.Order().SequenceEqual(provider.ProcessIdsToInclude.Order()))
            {
                return $"Source '{sourceScope.SourceId}' has a provider request that differs from its effective scope.";
            }

            bool filtered = sourceScope.ProcessScope == ProviderProcessScope.ProcessFiltered;
            bool expectedOutside = scope.RequestedProcessIds.Count > 0 && !filtered;
            if (filtered != (provider.ProcessIdsToInclude.Count > 0)
                || sourceScope.CapturesOutsideRequestedProcesses != expectedOutside
                || !Enum.IsDefined(sourceScope.ProcessScope))
            {
                return $"Source '{sourceScope.SourceId}' has an inconsistent process-filter decision.";
            }
        }

        return null;
    }

    private static string? ValidateSourceDecisions(EffectiveCapturePlan plan)
    {
        if (plan.SourceDecisions.Any(decision => decision is null)
            || plan.SourceDecisions.Any(decision => decision.State == ProfileSourceDecisionState.Blocking))
        {
            return "A startable plan cannot contain a blocking or null source decision.";
        }

        string[] included =
        [
            .. plan.SourceDecisions
                .Where(decision => decision.State == ProfileSourceDecisionState.Included)
                .Select(decision => decision.SourceId)
                .Order(StringComparer.Ordinal),
        ];
        string[] admitted = [.. plan.Sources.Select(source => source.SourceId).Order(StringComparer.Ordinal)];
        return included.SequenceEqual(admitted, StringComparer.Ordinal)
            ? null
            : "Included source decisions and admitted source plans do not identify the same sources.";
    }

    private static string? ValidateProviders(EffectiveCapturePlan plan)
    {
        Dictionary<string, IReadOnlyList<int>> filters = plan.Scope.Sources
            .Where(source => source.ProcessScope == ProviderProcessScope.ProcessFiltered)
            .ToDictionary(source => source.SourceId, source => source.AppliedProcessIds, StringComparer.Ordinal);

        IReadOnlyList<ProviderEnablementRequest> expected;
        try
        {
            expected = ProviderEnablementCompiler.Compile(plan.Sources, plan.RequestCallStacks, filters);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return $"The source plans cannot produce an allowlisted provider request: {exception.Message}";
        }

        Dictionary<string, ProviderEnablementRequest> supplied;
        try
        {
            supplied = plan.Providers.ToDictionary(provider => provider.SourceId, StringComparer.Ordinal);
        }
        catch (ArgumentException)
        {
            return "Provider source IDs must be unique.";
        }

        foreach (ProviderEnablementRequest required in expected)
        {
            if (!supplied.TryGetValue(required.SourceId, out ProviderEnablementRequest? actual)
                || !ProvidersEqual(required, actual))
            {
                return $"Provider request '{required.SourceId}' differs from the adapter allowlist.";
            }
        }

        return null;
    }

    private static bool ProvidersEqual(ProviderEnablementRequest left, ProviderEnablementRequest right) =>
        string.Equals(left.SourceId, right.SourceId, StringComparison.Ordinal)
        && string.Equals(left.ProviderName, right.ProviderName, StringComparison.Ordinal)
        && left.ProviderGuid == right.ProviderGuid
        && left.Level == right.Level
        && left.MatchAnyKeyword == right.MatchAnyKeyword
        && left.MatchAllKeyword == right.MatchAllKeyword
        && left.RequestCaptureState == right.RequestCaptureState
        && left.RequestCallStacks == right.RequestCallStacks
        && left.EventIdsToEnable.Order().SequenceEqual(right.EventIdsToEnable.Order())
        && left.EventIdsToDisable.Order().SequenceEqual(right.EventIdsToDisable.Order())
        && left.ProcessIdsToInclude.Order().SequenceEqual(right.ProcessIdsToInclude.Order());

    private static bool BodyPoliciesEqual(CompiledBodyAdmissionPolicy left, CompiledBodyAdmissionPolicy? right) =>
        right is not null
        && string.Equals(left.PolicyId, right.PolicyId, StringComparison.Ordinal)
        && left.Mode == right.Mode
        && left.RetainedBody == right.RetainedBody
        && left.MaximumRetainedBodyBytes == right.MaximumRetainedBodyBytes
        && left.RetainsOriginalSourceBytes == right.RetainsOriginalSourceBytes
        && left.PermittedExtendedDataTypes.Order().SequenceEqual(right.PermittedExtendedDataTypes.Order());

    private static bool ValidProcessIds(IReadOnlyList<int>? processIds) =>
        processIds is not null
        && processIds.Count <= 64
        && processIds.All(processId => processId > 0)
        && processIds.Distinct().Count() == processIds.Count;

    private static bool IsSha256Identity(string? value)
    {
        const string prefix = "sha256:";
        return value is not null
            && value.Length == prefix.Length + 64
            && value.StartsWith(prefix, StringComparison.Ordinal)
            && value.AsSpan(prefix.Length).ToString().All(Uri.IsHexDigit);
    }

    private static BrokerPrepareResult Refused(BrokerPrepareRefusalCode code, string message) =>
        BrokerPrepareResult.Refused(code, message);

    private static CompiledBodyAdmissionPolicy Clone(CompiledBodyAdmissionPolicy policy) => policy with
    {
        PermittedExtendedDataTypes = [.. policy.PermittedExtendedDataTypes.Order()],
    };

    private static SourceAdmissionPlan Clone(SourceAdmissionPlan source, CompiledBodyAdmissionPolicy bodyPolicy) => source with
    {
        Events =
        [
            .. source.Events
                .OrderBy(item => item.EventId)
                .ThenBy(item => item.Version)
                .Select(item => item with
                {
                    BodyPolicy = bodyPolicy,
                    Slots = [.. item.Slots],
                    FieldReport = [.. item.FieldReport.Select(field => field with { })],
                }),
        ],
        Diagnostics = [.. source.Diagnostics],
    };

    private static ProviderEnablementRequest Clone(ProviderEnablementRequest provider) => provider with
    {
        EventIdsToEnable = [.. provider.EventIdsToEnable.Distinct().Order()],
        EventIdsToDisable = [.. provider.EventIdsToDisable.Distinct().Order()],
        ProcessIdsToInclude = [.. provider.ProcessIdsToInclude.Order()],
    };

    private static CaptureScopeDecision Clone(CaptureScopeDecision scope) => scope with
    {
        RequestedProcessIds = [.. scope.RequestedProcessIds.Order()],
        InitialViewProcessIds = [.. scope.InitialViewProcessIds.Order()],
        Sources =
        [
            .. scope.Sources
                .OrderBy(source => source.SourceId, StringComparer.Ordinal)
                .Select(source => source with { AppliedProcessIds = [.. source.AppliedProcessIds.Order()] }),
        ],
    };
}

internal static class PreparedPlanDigest
{
    public static string Compute(
        DateTimeOffset compiledAtUtc,
        string buildId,
        string architecture,
        string adapterVersion,
        string requestedProfileId,
        string effectiveProfileId,
        AdmissionMode requestedAdmission,
        AdmissionMode effectiveAdmission,
        BrokerCaptureQuota quota,
        BrokerRetentionPolicy retention,
        BrokerJournalPublication publication,
        CompiledBodyAdmissionPolicy bodyPolicy,
        ImmutableArray<SourceAdmissionPlan> sources,
        ImmutableArray<ProviderEnablementRequest> providers,
        CaptureScopeDecision scope,
        bool preserveExtendedData,
        bool requestCallStacks)
    {
        using var stream = new MemoryStream(4096);
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true))
        {
            WriteString(writer, "InterCat.Broker.PreparedCapturePlan");
            writer.Write(BrokerProtocol.Version);
            writer.Write(compiledAtUtc.UtcTicks);
            WriteString(writer, buildId);
            WriteString(writer, architecture);
            WriteString(writer, adapterVersion);
            WriteString(writer, requestedProfileId);
            WriteString(writer, effectiveProfileId);
            writer.Write((int)requestedAdmission);
            writer.Write((int)effectiveAdmission);
            writer.Write(quota.MaximumDurationSeconds);
            writer.Write(quota.MaximumJournalBytes);
            writer.Write(quota.MinimumFreeDiskBytes);
            writer.Write((int)retention);
            writer.Write((int)publication);
            WriteBodyPolicy(writer, bodyPolicy);
            writer.Write(preserveExtendedData);
            writer.Write(requestCallStacks);
            WriteScope(writer, scope);

            writer.Write(sources.Length);
            foreach (SourceAdmissionPlan source in sources)
            {
                WriteString(writer, source.SourceId);
                WriteGuid(writer, source.ProviderGuid);
                writer.Write(source.SourceIndex);
                writer.Write(source.Events.Count);
                foreach (AdmittedEventPlan item in source.Events)
                {
                    writer.Write(item.SourceIndex);
                    WriteGuid(writer, item.ProviderGuid);
                    writer.Write(item.EventId);
                    writer.Write(item.Version);
                    WriteString(writer, item.Name);
                    writer.Write((int)item.Mechanism);
                    writer.Write((int)item.Kind);
                    writer.Write((int)item.Direction);
                    writer.Write(item.MinimumBodyLength);
                    writer.Write(item.PointerSize);
                    WriteString(writer, item.SchemaFingerprint);
                    WriteBodyPolicy(writer, item.BodyPolicy);
                    writer.Write(item.Slots.Count);
                    foreach (AdmittedSlotPlan slot in item.Slots)
                    {
                        WriteString(writer, slot.FieldName);
                        writer.Write((int)slot.Role);
                        writer.Write(slot.Offset);
                        writer.Write(slot.Width);
                        WriteNullableEnum(writer, slot.Unit);
                        WriteNullableEnum(writer, slot.ByteDomain);
                        writer.Write((int)slot.Transform);
                        writer.Write((int)slot.Kind);
                    }
                }
            }

            writer.Write(providers.Length);
            foreach (ProviderEnablementRequest provider in providers)
            {
                WriteString(writer, provider.SourceId);
                WriteString(writer, provider.ProviderName);
                WriteGuid(writer, provider.ProviderGuid);
                writer.Write(provider.Level);
                writer.Write(provider.MatchAnyKeyword);
                writer.Write(provider.MatchAllKeyword);
                WriteIntegers(writer, provider.EventIdsToEnable);
                WriteIntegers(writer, provider.EventIdsToDisable);
                WriteIntegers(writer, provider.ProcessIdsToInclude);
                writer.Write(provider.RequestCaptureState);
                writer.Write(provider.RequestCallStacks);
            }
        }

        byte[] digest = SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        return BrokerProtocol.PreparedPlanDigestAlgorithm + ":" + Convert.ToHexStringLower(digest);
    }

    private static void WriteScope(BinaryWriter writer, CaptureScopeDecision scope)
    {
        WriteNullableEnum(writer, scope.RequestedMechanism);
        WriteNullableEnum(writer, scope.EffectiveMechanism);
        WriteIntegers(writer, scope.RequestedProcessIds);
        WriteIntegers(writer, scope.InitialViewProcessIds);
        writer.Write(scope.CapturesOutsideRequestedProcesses);
        writer.Write(scope.BroaderCaptureNeedsConsent);
        writer.Write(scope.BroaderCaptureAccepted);
        writer.Write(scope.Sources.Count);
        foreach (ProviderScopeDecision source in scope.Sources)
        {
            WriteString(writer, source.SourceId);
            writer.Write((int)source.ProcessScope);
            WriteIntegers(writer, source.AppliedProcessIds);
            writer.Write(source.CapturesOutsideRequestedProcesses);
        }
    }

    private static void WriteBodyPolicy(BinaryWriter writer, CompiledBodyAdmissionPolicy bodyPolicy)
    {
        WriteString(writer, bodyPolicy.PolicyId);
        writer.Write((int)bodyPolicy.Mode);
        writer.Write((int)bodyPolicy.RetainedBody);
        writer.Write(bodyPolicy.MaximumRetainedBodyBytes);
        writer.Write(bodyPolicy.RetainsOriginalSourceBytes);
        writer.Write(bodyPolicy.PermittedExtendedDataTypes.Count);
        foreach (ushort type in bodyPolicy.PermittedExtendedDataTypes)
        {
            writer.Write(type);
        }
    }

    private static void WriteIntegers(BinaryWriter writer, IReadOnlyList<int> values)
    {
        writer.Write(values.Count);
        foreach (int value in values)
        {
            writer.Write(value);
        }
    }

    private static void WriteNullableEnum<T>(BinaryWriter writer, T? value)
        where T : struct, Enum
    {
        writer.Write(value.HasValue);
        if (value.HasValue)
        {
            writer.Write(Convert.ToInt32(value.Value, CultureInfo.InvariantCulture));
        }
    }

    private static void WriteGuid(BinaryWriter writer, Guid value) =>
        WriteString(writer, value.ToString("N", CultureInfo.InvariantCulture));

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}
