using System.Globalization;
using System.Text.Json;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>
/// Read-only discovery and effective-plan preview. It performs schema inventory but never enables a
/// provider, so users can see broad collection, omissions and refusals before consenting to capture.
/// </summary>
internal static class ProfilesCommand
{
    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? profileId = command.TakePositional();
        string? outputPath = command.TakeOption("--output");
        string? mechanismText = command.TakeOption("--mechanism");
        string? contentSourceId = command.TakeOption("--source");
        string? maximumRecordBytesText = command.TakeOption("--max-record-bytes");
        string? maximumSessionBytesText = command.TakeOption("--max-session-bytes");
        string? retentionText = command.TakeOption("--retention");
        string? inspectionText = command.TakeOption("--inspection");
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        bool diagnosticEtl = command.TryTakeFlag("--diagnostic-etl");
        bool allowBroaderCapture = command.TryTakeFlag("--allow-broader-capture");
        var processIds = new List<int>();
        var channelSelectors = new List<string>();
        string? rawProcessId;
        while ((rawProcessId = command.TakeOption("--pid")) is not null)
        {
            if (!int.TryParse(rawProcessId, NumberStyles.None, CultureInfo.InvariantCulture, out int processId)
                || processId <= 0)
            {
                ConsoleUi.Failure($"Invalid process ID: {rawProcessId}. Use a positive decimal integer.");
                return InterCatExitCode.InvalidInvocation;
            }

            if (processIds.Contains(processId))
            {
                ConsoleUi.Failure($"Process ID {processId} was supplied more than once.");
                return InterCatExitCode.InvalidInvocation;
            }

            processIds.Add(processId);
        }

        string? channelSelector;
        while ((channelSelector = command.TakeOption("--channel")) is not null)
        {
            if (string.IsNullOrWhiteSpace(channelSelector)
                || channelSelector.Length > ContentCapturePolicyCompiler.MaximumChannelSelectorLength
                || channelSelector.Any(char.IsControl))
            {
                ConsoleUi.Failure(
                    $"Each --channel selector must contain 1 to {ContentCapturePolicyCompiler.MaximumChannelSelectorLength} printable characters.");
                return InterCatExitCode.InvalidInvocation;
            }

            if (channelSelectors.Contains(channelSelector, StringComparer.Ordinal))
            {
                ConsoleUi.Failure($"Channel selector '{channelSelector}' was supplied more than once.");
                return InterCatExitCode.InvalidInvocation;
            }

            channelSelectors.Add(channelSelector);
        }

        if (processIds.Count > 64)
        {
            ConsoleUi.Failure("A profile preview accepts at most 64 --pid values.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (channelSelectors.Count > ContentCapturePolicyCompiler.MaximumChannelSelectors)
        {
            ConsoleUi.Failure(
                $"A content preview accepts at most {ContentCapturePolicyCompiler.MaximumChannelSelectors} --channel values.");
            return InterCatExitCode.InvalidInvocation;
        }

        int? maximumRecordBytes = ParsePositiveInt(maximumRecordBytesText, "--max-record-bytes");
        if (maximumRecordBytesText is not null && maximumRecordBytes is null)
        {
            return InterCatExitCode.InvalidInvocation;
        }

        long? maximumSessionBytes = ParsePositiveLong(maximumSessionBytesText, "--max-session-bytes");
        if (maximumSessionBytesText is not null && maximumSessionBytes is null)
        {
            return InterCatExitCode.InvalidInvocation;
        }

        ContentRetentionMode? retention = ParseContentRetention(retentionText);
        if (retentionText is not null && retention is null)
        {
            ConsoleUi.Failure($"Unknown content retention: {retentionText}. Use stop-at-limit.");
            return InterCatExitCode.InvalidInvocation;
        }

        ContentInspectionMode? inspection = ParseContentInspection(inspectionText);
        if (inspectionText is not null && inspection is null)
        {
            ConsoleUi.Failure($"Unknown content inspection: {inspectionText}. Use disabled or hex-text.");
            return InterCatExitCode.InvalidInvocation;
        }

        Mechanism? mechanism = ParseMechanism(mechanismText);
        if (mechanismText is not null && mechanism is null)
        {
            ConsoleUi.Failure($"Unknown mechanism: {mechanismText}. Known names are tcp, udp, and rpc; availability is checked per profile.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure($"Unknown or incomplete option: {unknown}");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        string? fullPath = outputPath is null ? null : Path.GetFullPath(outputPath);
        if (fullPath is not null && File.Exists(fullPath) && !overwrite)
        {
            ConsoleUi.Failure($"{fullPath} already exists. Pass --overwrite to replace it.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (profileId is null)
        {
            if (diagnosticEtl
                || mechanism is not null
                || processIds.Count > 0
                || allowBroaderCapture
                || HasContentOptions())
            {
                ConsoleUi.Failure("Profile options require one profile name. List profiles without additional options.");
                return InterCatExitCode.InvalidInvocation;
            }

            IReadOnlyList<CaptureProfileDescriptor> profiles = CaptureProfileCatalog.All;
            string catalogPayload = JsonSerializer.Serialize(profiles, JsonContracts.Indented);
            if (json)
            {
                Console.Out.WriteLine(catalogPayload);
            }
            else
            {
                RenderCatalog(profiles);
            }

            await WriteAsync(fullPath, catalogPayload, cancellationToken).ConfigureAwait(false);
            return InterCatExitCode.Success;
        }

        CaptureProfileDescriptor? selected = CaptureProfileCatalog.Find(profileId);
        if (selected is null)
        {
            ConsoleUi.Failure($"Unknown capture profile: {profileId}");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        ContentCaptureRequest? contentRequest = null;
        if (selected.Kind == CaptureProfileKind.FocusedTransport)
        {
            if (HasContentOptions())
            {
                ConsoleUi.Failure("Content source, channel, budget, retention and inspection options apply only to content.");
                return InterCatExitCode.InvalidInvocation;
            }

            if (mechanism is null)
            {
                ConsoleUi.Failure("focused-transport requires --mechanism tcp.");
                PrintHelp();
                return InterCatExitCode.InvalidInvocation;
            }

            if (allowBroaderCapture && processIds.Count == 0)
            {
                ConsoleUi.Failure("--allow-broader-capture is meaningful only with at least one --pid selection.");
                return InterCatExitCode.InvalidInvocation;
            }
        }
        else if (selected.Kind == CaptureProfileKind.Content)
        {
            if (allowBroaderCapture)
            {
                ConsoleUi.Failure("Content scope never accepts --allow-broader-capture; an unenforceable content scope is refused.");
                return InterCatExitCode.InvalidInvocation;
            }

            if (mechanism is null
                || contentSourceId is null
                || processIds.Count == 0
                || channelSelectors.Count == 0
                || maximumRecordBytes is null
                || maximumSessionBytes is null
                || retention is null
                || inspection is null)
            {
                ConsoleUi.Failure(
                    "content requires --source, --mechanism, at least one --pid and --channel, both byte limits, --retention, and --inspection.");
                PrintHelp();
                return InterCatExitCode.InvalidInvocation;
            }

            contentRequest = new()
            {
                SourceId = contentSourceId,
                Mechanism = mechanism.Value,
                ProcessIds = processIds,
                ChannelSelectors = channelSelectors,
                MaximumRecordBytes = maximumRecordBytes.Value,
                MaximumSessionBytes = maximumSessionBytes.Value,
                Retention = retention.Value,
                Inspection = inspection.Value,
            };

            string? contentRefusal = ContentCapturePolicyCompiler.Validate(contentRequest);
            if (contentRefusal is not null)
            {
                ConsoleUi.Failure(contentRefusal);
                return InterCatExitCode.InvalidInvocation;
            }
        }
        else if (mechanism is not null || processIds.Count > 0 || allowBroaderCapture || HasContentOptions())
        {
            ConsoleUi.Failure("Mechanism, process and content-scope options apply only to focused-transport or content.");
            return InterCatExitCode.InvalidInvocation;
        }

        ConsoleUi.Progress(
            selected.RequestPreviewAvailable
                ? $"Compiling the requested {selected.DisplayName} settings from local schemas. No capture is started."
                : $"Checking the declared availability of {selected.DisplayName}. No capture is started.");
        var host = new TraceEventSessionHost();
        ProbeEnvironment environment = CapabilityInventoryProbe.DescribeEnvironment(host.IsElevated);
        var inventory = new CapabilityInventoryProbe(new TdhEtwMetadataSource());
        EffectiveCapturePlan plan = CaptureProfileCompiler.Compile(
            new(
                selected.Kind,
                diagnosticEtl,
                FocusedMechanism: selected.Kind == CaptureProfileKind.FocusedTransport ? mechanism : null,
                FocusedProcessIds: selected.Kind == CaptureProfileKind.FocusedTransport ? processIds : null,
                AllowBroaderCapture: allowBroaderCapture,
                Content: contentRequest),
            inventory,
            environment);
        cancellationToken.ThrowIfCancellationRequested();

        string payload = JsonSerializer.Serialize(ToDocument(plan), JsonContracts.Indented);
        if (json)
        {
            Console.Out.WriteLine(payload);
        }
        else
        {
            Render(plan);
        }

        await WriteAsync(fullPath, payload, cancellationToken).ConfigureAwait(false);
        if (!plan.CanStart)
        {
            ConsoleUi.Warn("The requested profile cannot start with these exact settings; no fallback was applied.");
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        return plan.SourceDecisions.Any(decision => decision.State == ProfileSourceDecisionState.Omitted)
            || plan.Diagnostics.Count > 0
                ? InterCatExitCode.PartialResultSuccess
                : InterCatExitCode.Success;

        bool HasContentOptions() =>
            contentSourceId is not null
            || channelSelectors.Count > 0
            || maximumRecordBytesText is not null
            || maximumSessionBytesText is not null
            || retentionText is not null
            || inspectionText is not null;
    }

    private static void RenderCatalog(IReadOnlyList<CaptureProfileDescriptor> profiles)
    {
        ConsoleUi.Heading("Capture profiles");
        var rows = new List<IReadOnlyList<string>>(profiles.Count);
        foreach (CaptureProfileDescriptor profile in profiles)
        {
            rows.Add(
            [
                profile.Id,
                profile.Admission.ToString(),
                profile.CompilationAvailable
                    ? "capture preview"
                    : profile.RequestPreviewAvailable ? "request preview only" : "not available",
                profile.Summary,
            ]);
        }

        ConsoleUi.Table(["Profile", "Admission", "State", "Intent or refusal"], rows);
        ConsoleUi.Note("Preview a profile before capture: icat profiles explore");
        ConsoleUi.Note("Focused TCP preview: icat profiles focused-transport --mechanism tcp [--pid <id>]");
        ConsoleUi.Note("Content request preview: icat profiles content --help");
        ConsoleUi.Note("No profile command enables a provider or starts a capture.");
    }

    private static void Render(EffectiveCapturePlan plan)
    {
        ConsoleUi.Heading("Requested and effective profile");
        ConsoleUi.Field("Requested profile", plan.RequestedProfileId);
        ConsoleUi.Field("Effective profile", plan.EffectiveProfileId ?? "none — request is blocked");
        ConsoleUi.Field("Requested admission", plan.RequestedAdmission.ToString());
        ConsoleUi.Field("Effective admission", plan.EffectiveAdmission?.ToString() ?? "none");
        ConsoleUi.Field("Can start exactly", plan.CanStart ? "yes" : "no");
        ConsoleUi.Field("Build", plan.Environment.BuildId);
        ConsoleUi.Field("Adapter", plan.AdapterVersion);
        ConsoleUi.Field("Call stacks", plan.RequestCallStacks ? "requested" : "not requested");
        ConsoleUi.Field("Extended metadata", plan.PreserveExtendedData ? "bounded approved types" : "not retained");
        ConsoleUi.Line();
        ConsoleUi.Note(plan.CollectionStatement);
        if (plan.BodyPolicy is not null)
        {
            ConsoleUi.Note($"Body policy {plan.BodyPolicy.PolicyId}: {plan.BodyPolicy.Summary}");
        }

        if (plan.Content is null)
        {
            ConsoleUi.Heading("Capture scope");
            string mechanism = plan.Scope.EffectiveMechanism?.ToString()
                ?? (plan.Scope.RequestedMechanism is null
                    ? "profile-defined"
                    : $"{plan.Scope.RequestedMechanism} requested — not available");
            ConsoleUi.Field("Mechanism", mechanism);
            ConsoleUi.Field(
                "Process focus",
                plan.Scope.RequestedProcessIds.Count == 0
                    ? "all processes"
                    : string.Join(", ", plan.Scope.RequestedProcessIds));
            bool scopeEvaluated = plan.Scope.EffectiveMechanism is not null || plan.Scope.Sources.Count > 0;
            ConsoleUi.Field(
                "Broader collection",
                scopeEvaluated ? plan.Scope.CapturesOutsideRequestedProcesses ? "yes" : "no" : "not evaluated — no capture plan");
            ConsoleUi.Field(
                "Broader consent",
                !scopeEvaluated
                    ? "not applicable"
                    : !plan.Scope.BroaderCaptureNeedsConsent
                        ? "not needed"
                        : plan.Scope.BroaderCaptureAccepted ? "accepted" : "required before start");
            ConsoleUi.Note(plan.Scope.Disclosure);
            if (plan.Scope.Sources.Count > 0)
            {
                var scopeRows = new List<IReadOnlyList<string>>(plan.Scope.Sources.Count);
                foreach (ProviderScopeDecision source in plan.Scope.Sources)
                {
                    scopeRows.Add(
                    [
                        source.SourceId,
                        source.ProcessScope.ToString(),
                        source.AppliedProcessIds.Count == 0 ? "none" : string.Join(",", source.AppliedProcessIds),
                        source.CapturesOutsideRequestedProcesses ? "yes" : "no",
                        source.Reason,
                    ]);
                }

                ConsoleUi.Table(["Source", "Capture scope", "Provider PID filter", "Broader", "Reason"], scopeRows);
            }
        }

        if (plan.Content is not null)
        {
            ContentCaptureDecision content = plan.Content;
            ConsoleUi.Heading("Content request boundaries");
            ConsoleUi.Field("Source", content.SourceId);
            ConsoleUi.Field("Mechanism", content.Mechanism.ToString());
            ConsoleUi.Field("Process selectors", string.Join(", ", content.ProcessIds));
            ConsoleUi.Field("Channel selectors", string.Join(", ", content.ChannelSelectors));
            ConsoleUi.Field("Per-record limit", ConsoleUi.Bytes(content.MaximumRecordBytes));
            ConsoleUi.Field("Session limit", ConsoleUi.Bytes(content.MaximumSessionBytes));
            ConsoleUi.Field("At record limit", "retain prefix; record original length and truncation");
            ConsoleUi.Field("At session limit", "stop before exceeding the limit");
            ConsoleUi.Field("Unknown schema", "omit body; keep header and diagnostic");
            ConsoleUi.Field(
                "Inspection",
                content.Inspection == ContentInspectionMode.Disabled ? "disabled" : "bounded inert hex/text only");
            ConsoleUi.Field("Approved events", content.ApprovedEventIds.Count == 0 ? "none" : string.Join(", ", content.ApprovedEventIds));
            ConsoleUi.Field("Approved body fields", content.ApprovedSourceFields.Count == 0 ? "none" : string.Join(", ", content.ApprovedSourceFields));
            ConsoleUi.Field("Body contract", content.SourceBodyContractAvailable ? "available" : "unavailable");
            ConsoleUi.Field("Scope enforcement", content.ScopeEnforceable ? "validated" : "not validated");
            ConsoleUi.Field("Capture impact", content.CaptureImpactMeasured ? "measured" : "unmeasured");
            if (content.CaptureImpactEvidence is not null)
            {
                ConsoleUi.Field("Impact evidence", content.CaptureImpactEvidence);
            }

            ConsoleUi.Field("Source evidence", content.SourceEvidenceComplete ? "complete" : "incomplete");
            ConsoleUi.Field("Admission policy", content.AdmissionPolicyAvailable ? "available" : "not compiled");
            ConsoleUi.Note(content.Disclosure);
            ConsoleUi.Note(content.AvailabilityReason);
        }

        ConsoleUi.Heading("Source decisions");
        if (plan.SourceDecisions.Count == 0)
        {
            ConsoleUi.Note("No source was compiled because the profile itself is unavailable.");
        }
        else
        {
            var rows = new List<IReadOnlyList<string>>(plan.SourceDecisions.Count);
            foreach (ProfileSourceDecision decision in plan.SourceDecisions)
            {
                rows.Add(
                [
                    decision.SourceId,
                    decision.Required ? "required" : "optional",
                    decision.State.ToString(),
                    decision.Overhead.ToString(),
                    decision.Reason,
                ]);
            }

            ConsoleUi.Table(["Source", "Need", "Decision", "Overhead", "Reason"], rows);
            foreach (ProfileSourceDecision decision in plan.SourceDecisions.Where(item => item.OverheadEvidence is not null))
            {
                ConsoleUi.Note($"{decision.SourceId}: overhead evidence {decision.OverheadEvidence}");
            }
        }

        ConsoleUi.Heading("Original evidence");
        ConsoleUi.Field("Requested", plan.OriginalEvidence.Requested ? "yes" : "no");
        ConsoleUi.Field("Will start", plan.OriginalEvidence.WillStart ? "yes" : "no");
        ConsoleUi.Note(plan.OriginalEvidence.StorageBoundary);
        ConsoleUi.Note(plan.OriginalEvidence.Warning);

        if (plan.Diagnostics.Count > 0)
        {
            ConsoleUi.Heading("Diagnostics");
            foreach (string diagnostic in plan.Diagnostics)
            {
                ConsoleUi.Bullet(diagnostic);
            }
        }

        ConsoleUi.Note(string.Create(
            CultureInfo.CurrentCulture,
            $"Compiled {plan.Sources.Count} source plan(s) and {plan.Providers.Count} provider request(s)."));
    }

    private static async Task WriteAsync(string? fullPath, string payload, CancellationToken cancellationToken)
    {
        if (fullPath is null)
        {
            return;
        }

        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fullPath, payload, cancellationToken).ConfigureAwait(false);
        ConsoleUi.Success($"Profile preview written to {fullPath}");
    }

    private static ProfilePreviewDocument ToDocument(EffectiveCapturePlan plan) => new()
    {
        SchemaVersion = "1",
        CompiledAtUtc = plan.CompiledAtUtc,
        Environment = plan.Environment,
        AdapterVersion = plan.AdapterVersion,
        RequestedProfileId = plan.RequestedProfileId,
        EffectiveProfileId = plan.EffectiveProfileId,
        RequestedAdmission = plan.RequestedAdmission,
        EffectiveAdmission = plan.EffectiveAdmission,
        CanStart = plan.CanStart,
        CollectionStatement = plan.CollectionStatement,
        BodyPolicy = plan.BodyPolicy,
        Scope = plan.Scope,
        Content = plan.Content,
        Sources = plan.SourceDecisions,
        Providers =
        [
            .. plan.Providers.Select(provider => new ProviderRequestPreview(
                provider.SourceId,
                provider.ProviderName,
                provider.ProviderGuid,
                provider.Level,
                $"0x{provider.MatchAnyKeyword:X16}",
                $"0x{provider.MatchAllKeyword:X16}",
                provider.EventIdsToEnable,
                provider.EventIdsToDisable,
                provider.ProcessIdsToInclude,
                provider.RequestCaptureState,
                provider.RequestCallStacks)),
        ],
        OriginalEvidence = plan.OriginalEvidence,
        PreserveExtendedData = plan.PreserveExtendedData,
        RequestCallStacks = plan.RequestCallStacks,
        Diagnostics = plan.Diagnostics,
    };

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat profiles [profile] [--diagnostic-etl] [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("icat profiles focused-transport --mechanism tcp [--pid <id> ...]");
        ConsoleUi.Line("              [--allow-broader-capture] [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("icat profiles content --source <source-id> --mechanism <name> --pid <id> ...");
        ConsoleUi.Line("              --channel <selector> ... --max-record-bytes <bytes>");
        ConsoleUi.Line("              --max-session-bytes <bytes> --retention stop-at-limit");
        ConsoleUi.Line("              --inspection <disabled|hex-text> [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line();
        ConsoleUi.Line("  With no profile, lists every capture intent and whether it is available.");
        ConsoleUi.Line("  With a profile, compiles requested/effective sources, body policy and omissions");
        ConsoleUi.Line("  from local schemas. This is read-only and starts no capture.");
        ConsoleUi.Line("  --diagnostic-etl requests separate original evidence; unsupported requests block");
        ConsoleUi.Line("  instead of silently falling back to a sanitized journal.");
        ConsoleUi.Line("  --pid requests process focus. If any provider must collect more broadly, the");
        ConsoleUi.Line("  preview blocks until --allow-broader-capture records explicit acknowledgement.");
        ConsoleUi.Line("  Content requires source/process/channel scope and explicit byte limits. Its request");
        ConsoleUi.Line("  contract can be reviewed, but capture remains blocked until a payload adapter is validated.");
    }

    private static Mechanism? ParseMechanism(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "tcp" => Mechanism.Tcp,
            "udp" => Mechanism.Udp,
            "rpc" => Mechanism.Rpc,
            _ => null,
        };
    }

    private static int? ParsePositiveInt(string? value, string option)
    {
        if (value is null)
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
        {
            return parsed;
        }

        ConsoleUi.Failure($"{option} requires a positive decimal integer.");
        return null;
    }

    private static long? ParsePositiveLong(string? value, string option)
    {
        if (value is null)
        {
            return null;
        }

        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) && parsed > 0)
        {
            return parsed;
        }

        ConsoleUi.Failure($"{option} requires a positive decimal integer.");
        return null;
    }

    private static ContentRetentionMode? ParseContentRetention(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null => null,
            "stop-at-limit" => ContentRetentionMode.StopAtLimit,
            _ => null,
        };

    private static ContentInspectionMode? ParseContentInspection(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null => null,
            "disabled" => ContentInspectionMode.Disabled,
            "hex-text" => ContentInspectionMode.HexAndText,
            _ => null,
        };

    private sealed record ProviderRequestPreview(
        string SourceId,
        string ProviderName,
        Guid ProviderGuid,
        int Level,
        string MatchAnyKeyword,
        string MatchAllKeyword,
        IReadOnlyList<int> EventIdsToEnable,
        IReadOnlyList<int> EventIdsToDisable,
        IReadOnlyList<int> ProcessIdsToInclude,
        bool RequestCaptureState,
        bool RequestCallStacks);

    private sealed record ProfilePreviewDocument
    {
        public required string SchemaVersion { get; init; }
        public required DateTimeOffset CompiledAtUtc { get; init; }
        public required ProbeEnvironment Environment { get; init; }
        public required string AdapterVersion { get; init; }
        public required string RequestedProfileId { get; init; }
        public string? EffectiveProfileId { get; init; }
        public required AdmissionMode RequestedAdmission { get; init; }
        public AdmissionMode? EffectiveAdmission { get; init; }
        public required bool CanStart { get; init; }
        public required string CollectionStatement { get; init; }
        public CompiledBodyAdmissionPolicy? BodyPolicy { get; init; }
        public required CaptureScopeDecision Scope { get; init; }
        public ContentCaptureDecision? Content { get; init; }
        public required IReadOnlyList<ProfileSourceDecision> Sources { get; init; }
        public required IReadOnlyList<ProviderRequestPreview> Providers { get; init; }
        public required OriginalEvidenceDecision OriginalEvidence { get; init; }
        public required bool PreserveExtendedData { get; init; }
        public required bool RequestCallStacks { get; init; }
        public required IReadOnlyList<string> Diagnostics { get; init; }
    }
}
