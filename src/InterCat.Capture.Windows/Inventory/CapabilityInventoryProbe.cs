using System.Globalization;
using System.Runtime.InteropServices;
using InterCat.Domain;

namespace InterCat.Capture.Windows;

/// <summary>
/// Evidence a capture run produced for one source. Without it, observed health and semantic coverage stay
/// <see cref="CheckOutcome.NotAttempted"/>: a probe alone can never report a source as healthy (section 18.2).
/// </summary>
public sealed record SourceRuntimeEvidence(
    string SourceId,
    CheckOutcome Enablement,
    string EnablementDetail,
    CheckOutcome ObservedHealth,
    string ObservedHealthDetail,
    CheckOutcome SemanticCoverage,
    string SemanticCoverageDetail,
    IReadOnlyList<string> FixtureIds,
    string? UnavailableReason = null);

/// <summary>
/// Builds the machine-readable capability report of IC-002. The probe is read-only: it resolves provider
/// registration and schema metadata, compiles the admission plan those schemas allow, and records what it
/// could not prove. It never enables a provider or starts a session (section 21.2 item 1).
/// </summary>
public sealed class CapabilityInventoryProbe(IEtwMetadataSource metadata, TimeProvider? clock = null)
{
    public const string AdapterVersion = "windows-etw-inventory-0.2.0";
    public const string ReportVersion = "2";

    private readonly IEtwMetadataSource metadata =
        metadata ?? throw new ArgumentNullException(nameof(metadata));

    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    /// <summary>Describes the machine this process runs on, including its section 1.3 support tier.</summary>
    public static ProbeEnvironment DescribeEnvironment(bool? isElevated = null)
    {
        string architecture = RuntimeInformation.OSArchitecture.ToString();
        int build = System.Environment.OSVersion.Version.Build;
        string buildId = string.Create(
            CultureInfo.InvariantCulture,
            $"{System.Environment.OSVersion.Version}-{architecture.ToLowerInvariant()}");
        return new(
            RuntimeInformation.OSDescription,
            buildId,
            architecture,
            SupportedBuilds.IsSupported(build, architecture),
            isElevated ?? false,
            "local machine");
    }

    public CapabilityReport Probe(
        ProbeEnvironment environment,
        IReadOnlyList<SourceRuntimeEvidence>? runtimeEvidence = null,
        IReadOnlyDictionary<Mechanism, MechanismMeasurement>? measurements = null)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var diagnostics = new List<string>();
        if (!metadata.IsAvailable)
        {
            diagnostics.Add(metadata.UnavailableReason ?? "ETW metadata is unavailable on this host.");
        }
        else
        {
            int published = metadata.CountPublishedProviders();
            diagnostics.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"The machine reports {published} published ETW providers. Registration breadth is not coverage (R21)."));
        }

        Dictionary<string, SourceRuntimeEvidence> evidenceById = new(StringComparer.Ordinal);
        foreach (SourceRuntimeEvidence evidence in runtimeEvidence ?? [])
        {
            evidenceById[evidence.SourceId] = evidence;
        }

        var sources = new List<SourceCapability>(WindowsSourceCatalog.All.Count);
        int sourceIndex = 0;
        foreach (WindowsSourceDefinition definition in WindowsSourceCatalog.All)
        {
            evidenceById.TryGetValue(definition.SourceId, out SourceRuntimeEvidence? evidence);
            sources.Add(ProbeSource(definition, sourceIndex++, evidence, diagnostics));
        }

        return new()
        {
            ReportVersion = ReportVersion,
            ProbedAtUtc = clock.GetUtcNow(),
            Environment = environment,
            AdapterVersion = AdapterVersion,
            ProbeOnly = runtimeEvidence is null || runtimeEvidence.Count == 0,
            Sources = sources,
            Mechanisms = RollUpMechanisms(sources, measurements, environment),
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// Compiles the admission plans a capture would use for the named sources, refusing any source whose
    /// schema cannot back it. The same compilation feeds the capability report, so a report and a capture
    /// can never disagree about which fields exist.
    /// </summary>
    public IReadOnlyList<SourceAdmissionPlan> CompilePlans(
        IReadOnlyList<string> sourceIds,
        out IReadOnlyList<string> refusals)
    {
        ArgumentNullException.ThrowIfNull(sourceIds);

        var plans = new List<SourceAdmissionPlan>(sourceIds.Count);
        var problems = new List<string>();
        int index = 0;
        foreach (string sourceId in sourceIds)
        {
            WindowsSourceDefinition? definition = WindowsSourceCatalog.Find(sourceId);
            if (definition is null)
            {
                problems.Add($"{sourceId}: not in the source catalog.");
                continue;
            }

            RegisteredProvider? provider = definition.Kind == SourceKind.ManifestProvider
                ? metadata.TryResolveProvider(definition.ProviderName)
                : null;
            if (provider is null)
            {
                problems.Add($"{sourceId}: the provider is not registered on this machine.");
                continue;
            }

            ManifestReadResult manifest = metadata.TryReadManifest(provider.ProviderGuid);
            if (manifest.ManifestXml is null)
            {
                problems.Add($"{sourceId}: {manifest.FailureReason}");
                continue;
            }

            ProviderSchema schema;
            try
            {
                schema = ManifestParser.Parse(manifest.ManifestXml);
            }
            catch (InvalidDataException exception)
            {
                problems.Add($"{sourceId}: the manifest could not be interpreted: {exception.Message}");
                continue;
            }

            SourceAdmissionPlan plan = AdmissionPlanCompiler.Compile(definition, schema, index++);
            plans.Add(plan);
            foreach (string diagnostic in plan.Diagnostics)
            {
                problems.Add(diagnostic);
            }
        }

        refusals = problems;
        return plans;
    }

    private SourceCapability ProbeSource(
        WindowsSourceDefinition definition,
        int sourceIndex,
        SourceRuntimeEvidence? evidence,
        List<string> diagnostics)
    {
        var checks = new List<CapabilityCheck>(4);
        RegisteredProvider? provider = null;
        ProviderSchema? schema = null;
        SourceAdmissionPlan? plan = null;
        string? unavailableReason = null;
        CapabilityState state;

        if (definition.Kind != SourceKind.ManifestProvider)
        {
            unavailableReason =
                "This source is not a manifest provider, so the provider registry cannot confirm it. "
                + definition.FilteringNotes;
            checks.Add(new(
                CapabilityCheckKind.Registration,
                CheckOutcome.NotAttempted,
                "Registration is inapplicable to a kernel flag group.",
                unavailableReason));
            state = CapabilityState.Unsupported;
        }
        else if (!metadata.IsAvailable)
        {
            unavailableReason = metadata.UnavailableReason ?? "ETW metadata is unavailable on this host.";
            checks.Add(new(
                CapabilityCheckKind.Registration,
                CheckOutcome.NotAttempted,
                "The host cannot read the provider registry.",
                unavailableReason));
            state = CapabilityState.Unsupported;
        }
        else
        {
            provider = metadata.TryResolveProvider(definition.ProviderName);
            if (provider is null)
            {
                unavailableReason = $"Provider '{definition.ProviderName}' is not registered on this machine.";
                checks.Add(new(
                    CapabilityCheckKind.Registration,
                    CheckOutcome.Failed,
                    "The provider registry has no entry for this name.",
                    unavailableReason));
                state = CapabilityState.Unsupported;
            }
            else
            {
                checks.Add(new(
                    CapabilityCheckKind.Registration,
                    CheckOutcome.Passed,
                    $"Registered as {provider.ProviderGuid:B}. Registration proves nothing about emission (R21)."));

                ManifestReadResult manifest = metadata.TryReadManifest(provider.ProviderGuid);
                if (manifest.ManifestXml is null)
                {
                    unavailableReason = manifest.FailureReason;
                    state = CapabilityState.SchemaUnknown;
                    diagnostics.Add($"{definition.SourceId}: {manifest.FailureReason}");
                }
                else
                {
                    try
                    {
                        schema = ManifestParser.Parse(manifest.ManifestXml);
                        plan = AdmissionPlanCompiler.Compile(definition, schema, sourceIndex);
                        foreach (string diagnostic in plan.Diagnostics)
                        {
                            diagnostics.Add($"{definition.SourceId}: {diagnostic}");
                        }

                        state = CapabilityState.Experimental;
                    }
                    catch (InvalidDataException exception)
                    {
                        unavailableReason = $"The manifest could not be interpreted: {exception.Message}";
                        state = CapabilityState.SchemaUnknown;
                        diagnostics.Add($"{definition.SourceId}: {unavailableReason}");
                    }
                }
            }
        }

        if (evidence is null)
        {
            checks.Add(new(
                CapabilityCheckKind.Enablement,
                CheckOutcome.NotAttempted,
                "This report was produced without starting a capture, so enablement was not exercised."));
            checks.Add(new(
                CapabilityCheckKind.ObservedHealth,
                CheckOutcome.NotAttempted,
                "No capture ran, so no event health was observed. Absence here is not an observed zero (R21, P1)."));
            checks.Add(new(
                CapabilityCheckKind.SemanticCoverage,
                CheckOutcome.NotAttempted,
                "No truth workload was measured against this source."));
        }
        else
        {
            checks.Add(new(
                CapabilityCheckKind.Enablement,
                evidence.Enablement,
                evidence.EnablementDetail,
                evidence.Enablement == CheckOutcome.Failed ? evidence.UnavailableReason : null));
            checks.Add(new(CapabilityCheckKind.ObservedHealth, evidence.ObservedHealth, evidence.ObservedHealthDetail));
            checks.Add(new(
                CapabilityCheckKind.SemanticCoverage,
                evidence.SemanticCoverage,
                evidence.SemanticCoverageDetail));

            if (evidence.Enablement == CheckOutcome.Failed)
            {
                state = evidence.UnavailableReason?.Contains("denied", StringComparison.OrdinalIgnoreCase) == true
                    ? CapabilityState.PermissionDenied
                    : CapabilityState.ProviderFailed;
                unavailableReason ??= evidence.UnavailableReason;
            }
            else if (evidence.ObservedHealth == CheckOutcome.Passed && state == CapabilityState.Experimental)
            {
                state = CapabilityState.Available;
            }
        }

        var events = new List<EventCapability>();
        if (plan is not null && schema is not null)
        {
            foreach (AdmittedEventPlan admitted in plan.Events)
            {
                ProviderSchemaEvent? descriptor = schema.FindEvent(admitted.EventId, admitted.Version);
                events.Add(new(
                    admitted.EventId,
                    admitted.Version,
                    descriptor?.OpcodeValue ?? 0,
                    descriptor?.TaskName,
                    descriptor?.Keywords ?? [],
                    admitted.FieldReport));
            }
        }

        return new()
        {
            SourceId = definition.SourceId,
            DisplayName = definition.DisplayName,
            Kind = definition.Kind,
            ProviderGuid = provider?.ProviderGuid.ToString("D", CultureInfo.InvariantCulture),
            Mechanisms = definition.Mechanisms,
            State = state,
            UnavailableReason = state == CapabilityState.Available ? null : unavailableReason ?? DefaultReason(state),
            RequiredPrivilege = definition.RequiredPrivilege,
            Admission = AdmissionMode.MetadataOnly,
            Enablement = new(
                definition.Level,
                ToHex(definition.MatchAnyKeyword),
                ToHex(definition.MatchAllKeyword),
                definition.RequestedKeywords,
                definition.SupportsCaptureSideProcessFilter,
                definition.FilteringNotes),
            Schema = schema is null
                ? null
                : new(
                    schema.ProviderName,
                    schema.SchemaFingerprint,
                    schema.Events.Count,
                    CollectVersions(schema)),
            Events = events,
            Checks = checks,
            StartupBehaviour = definition.StartupBehaviour,
            ContractStatus = definition.ContractStatus,
            Overhead = OverheadClass.Unmeasured,
            FixtureIds = evidence?.FixtureIds ?? [],
            Notes = definition.Notes,
        };
    }

    private static string DefaultReason(CapabilityState state) => state switch
    {
        CapabilityState.Experimental =>
            "The schema was read, but no capture has demonstrated emission, field semantics or health.",
        CapabilityState.SchemaUnknown => "The provider schema could not be read on this machine.",
        CapabilityState.Unsupported => "No usable source was found for this mechanism on this machine.",
        CapabilityState.PermissionDenied => "Enablement was denied.",
        CapabilityState.ProviderFailed => "The provider refused enablement.",
        CapabilityState.DisabledByProfile => "The active profile does not request this source.",
        _ => "Unavailable.",
    };

    private static IReadOnlyList<string> CollectVersions(ProviderSchema schema)
    {
        SortedSet<string> versions = new(StringComparer.Ordinal);
        foreach (ProviderSchemaEvent declared in schema.Events)
        {
            versions.Add(declared.Version.ToString(CultureInfo.InvariantCulture));
        }

        return [.. versions];
    }

    private static string ToHex(ulong value) =>
        "0x" + value.ToString("X16", CultureInfo.InvariantCulture);

    private static List<MechanismCapability> RollUpMechanisms(
        IReadOnlyList<SourceCapability> sources,
        IReadOnlyDictionary<Mechanism, MechanismMeasurement>? measurements,
        ProbeEnvironment environment)
    {
        Dictionary<Mechanism, List<SourceCapability>> byMechanism = [];
        Dictionary<Mechanism, List<SourceCapability>> omittedByProfile = [];

        foreach (WindowsSourceDefinition definition in WindowsSourceCatalog.All)
        {
            SourceCapability? capability = null;
            foreach (SourceCapability candidate in sources)
            {
                if (string.Equals(candidate.SourceId, definition.SourceId, StringComparison.Ordinal))
                {
                    capability = candidate;
                }
            }

            if (capability is null)
            {
                continue;
            }

            foreach (Mechanism mechanism in definition.Mechanisms)
            {
                if (!byMechanism.TryGetValue(mechanism, out List<SourceCapability>? list))
                {
                    byMechanism[mechanism] = list = [];
                }

                list.Add(capability);
            }

            foreach (Mechanism mechanism in definition.MechanismsOmittedByProfile)
            {
                if (!omittedByProfile.TryGetValue(mechanism, out List<SourceCapability>? list))
                {
                    omittedByProfile[mechanism] = list = [];
                }

                list.Add(capability);
            }
        }

        var results = new List<MechanismCapability>();
        SortedSet<Mechanism> all = [.. byMechanism.Keys, .. omittedByProfile.Keys];
        foreach (Mechanism mechanism in all)
        {
            byMechanism.TryGetValue(mechanism, out List<SourceCapability>? covering);
            covering ??= [];

            CapabilityState state = CapabilityState.Unsupported;
            var sourceIds = new List<string>(covering.Count);
            string? reason = null;
            foreach (SourceCapability source in covering)
            {
                sourceIds.Add(source.SourceId);
                if (source.State < state)
                {
                    state = source.State;
                    reason = source.UnavailableReason;
                }
            }

            if (covering.Count == 0 && omittedByProfile.TryGetValue(mechanism, out List<SourceCapability>? omitted))
            {
                state = CapabilityState.DisabledByProfile;
                reason = "A registered source could serve this mechanism, but the M0 plan does not request it.";
                foreach (SourceCapability source in omitted)
                {
                    sourceIds.Add(source.SourceId);
                }
            }

            MechanismMeasurement? measurement = null;
            measurements?.TryGetValue(mechanism, out measurement);
            TierAssessment? assessment = measurement is null ? null : CoverageTierCalculator.Assess(measurement);

            results.Add(new()
            {
                Mechanism = mechanism,
                State = state,
                Tier = assessment?.Tier ?? CapabilityTier.Unsupported,
                Coverage = measurement is null ? CoverageState.UnknownCoverage : CoverageState.ReducedFidelity,
                Summary = Summarize(mechanism, state, assessment, environment),
                SourceIds = sourceIds,
                UnavailableReason = state == CapabilityState.Available ? null : reason,
                Measurement = measurement,
                TierAssessment = assessment,
                FixtureIds = measurement is null ? [] : [measurement.FixtureId],
            });
        }

        return results;
    }

    private static string Summarize(
        Mechanism mechanism,
        CapabilityState state,
        TierAssessment? assessment,
        ProbeEnvironment environment)
    {
        if (assessment is null)
        {
            return state switch
            {
                CapabilityState.Experimental =>
                    $"{mechanism}: a registered source with a readable schema and a compiled admission plan. No capture has measured it.",
                CapabilityState.DisabledByProfile =>
                    $"{mechanism}: reachable from a registered source, but not requested by the M0 capture plan.",
                CapabilityState.SchemaUnknown =>
                    $"{mechanism}: the candidate source is registered but its schema could not be read here.",
                _ => $"{mechanism}: no validated source on this machine.",
            };
        }

        string buildNote = environment.IsSupportedBuild
            ? $"measured on supported build {environment.BuildId}"
            : $"measured on untested build {environment.BuildId}";
        return $"{mechanism}: tier {assessment.Tier}, {buildNote}.";
    }
}
