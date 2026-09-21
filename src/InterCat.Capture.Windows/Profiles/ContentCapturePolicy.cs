using InterCat.Domain;

namespace InterCat.Capture.Windows;

/// <summary>What happens when retained content reaches its session byte cap.</summary>
public enum ContentRetentionMode
{
    StopAtLimit = 1,
}

/// <summary>
/// Inspection is a separate consent from collection. Hex/text means inert, bounded previews only; it
/// does not authorize search, decoding, reassembly, export, or active rendering.
/// </summary>
public enum ContentInspectionMode
{
    Disabled = 1,
    HexAndText = 2,
}

public enum ContentRecordLimitBehavior
{
    RetainPrefixAndRecordTruncation = 1,
}

public enum UnknownContentSchemaBehavior
{
    OmitBodyAndKeepHeaderDiagnostic = 1,
}

public enum ContentEvidenceClassification
{
    OpaqueProviderData = 1,
    TransportFragment = 2,
    ApplicationPayload = 3,
    DecodedFields = 4,
    EncryptedContent = 5,
}

/// <summary>
/// Adapter evidence required before a catalog source can compile scoped content. The catalog currently
/// contains no instance; adding one requires measured descriptor semantics and scope/impact evidence.
/// </summary>
public sealed record ValidatedContentSourceContract(
    IReadOnlyList<int> EventIds,
    IReadOnlyList<string> SourceFields,
    IReadOnlyList<ContentEvidenceClassification> Classifications,
    bool EnforcesProcessScopeBeforePersistence,
    bool EnforcesChannelScopeBeforePersistence,
    string ValidationEvidence,
    OverheadClass Overhead,
    string CaptureImpactEvidence);

/// <summary>
/// A deliberately complete content request. PID and channel values are selectors for a future start
/// attempt, not durable identities; a broker must bind them to observed lifecycle/resource epochs.
/// </summary>
public sealed record ContentCaptureRequest
{
    public required string SourceId { get; init; }
    public required Mechanism Mechanism { get; init; }
    public required IReadOnlyList<int> ProcessIds { get; init; }
    public required IReadOnlyList<string> ChannelSelectors { get; init; }
    public required int MaximumRecordBytes { get; init; }
    public required long MaximumSessionBytes { get; init; }
    public required ContentRetentionMode Retention { get; init; }
    public required ContentInspectionMode Inspection { get; init; }
}

/// <summary>
/// Read-only compilation of requested content boundaries and the adapter facts that still block them.
/// This is not a production body-admission policy and must never be passed to a capture session.
/// </summary>
public sealed record ContentCaptureDecision
{
    public required string SourceId { get; init; }
    public required Mechanism Mechanism { get; init; }
    public required IReadOnlyList<int> ProcessIds { get; init; }
    public required IReadOnlyList<string> ChannelSelectors { get; init; }
    public required int MaximumRecordBytes { get; init; }
    public required long MaximumSessionBytes { get; init; }
    public required ContentRetentionMode Retention { get; init; }
    public required ContentInspectionMode Inspection { get; init; }
    public required ContentRecordLimitBehavior RecordLimitBehavior { get; init; }
    public required UnknownContentSchemaBehavior UnknownSchemaBehavior { get; init; }
    public required IReadOnlyList<int> ApprovedEventIds { get; init; }
    public required IReadOnlyList<string> ApprovedSourceFields { get; init; }
    public required IReadOnlyList<ContentEvidenceClassification> ApprovedClassifications { get; init; }
    public required bool SourceBodyContractAvailable { get; init; }
    public required bool ScopeEnforceable { get; init; }
    public required bool CaptureImpactMeasured { get; init; }
    public string? CaptureImpactEvidence { get; init; }
    public required bool SourceEvidenceComplete { get; init; }
    public required bool AdmissionPolicyAvailable { get; init; }
    public required string AvailabilityReason { get; init; }
    public required string Disclosure { get; init; }
}

public static class ContentCapturePolicyCompiler
{
    public const int MaximumProcessIds = 64;
    public const int MaximumChannelSelectors = 64;
    public const int MaximumChannelSelectorLength = 256;
    public const int MaximumSourceIdLength = 256;
    public const int MaximumAllowedRecordBytes = 1024 * 1024;
    public const long MaximumAllowedSessionBytes = 64L * 1024 * 1024 * 1024;

    public static string? Validate(ContentCaptureRequest? request)
    {
        if (request is null)
        {
            return "Content requires an explicit source, mechanism, process/channel scope, byte budgets, retention, and inspection choice.";
        }

        if (string.IsNullOrWhiteSpace(request.SourceId)
            || request.SourceId.Length > MaximumSourceIdLength
            || request.SourceId.Any(char.IsControl))
        {
            return $"Content source IDs must contain 1 to {MaximumSourceIdLength} characters.";
        }

        WindowsSourceDefinition? source = WindowsSourceCatalog.Find(request.SourceId);
        if (source is null)
        {
            return "The requested content source is not in the adapter catalog.";
        }

        if (!source.Mechanisms.Contains(request.Mechanism))
        {
            return $"Content source '{request.SourceId}' does not describe {request.Mechanism}.";
        }

        if (request.ProcessIds is null
            || request.ProcessIds.Count is < 1 or > MaximumProcessIds
            || request.ProcessIds.Any(processId => processId <= 0)
            || request.ProcessIds.Distinct().Count() != request.ProcessIds.Count)
        {
            return $"Content requires 1 to {MaximumProcessIds} unique positive process IDs.";
        }

        if (request.ChannelSelectors is null
            || request.ChannelSelectors.Count is < 1 or > MaximumChannelSelectors)
        {
            return $"Content requires 1 to {MaximumChannelSelectors} mechanism-specific channel selectors.";
        }

        if (request.ChannelSelectors.Any(selector =>
            string.IsNullOrWhiteSpace(selector)
            || selector.Length > MaximumChannelSelectorLength
            || selector.Any(char.IsControl)))
        {
            return $"Each content channel selector must contain 1 to {MaximumChannelSelectorLength} characters.";
        }

        if (request.ChannelSelectors.Distinct(StringComparer.Ordinal).Count() != request.ChannelSelectors.Count)
        {
            return "A content channel selector may appear only once.";
        }

        if (request.MaximumRecordBytes is < 1 or > MaximumAllowedRecordBytes)
        {
            return $"The per-record content limit must be between 1 and {MaximumAllowedRecordBytes} bytes.";
        }

        if (request.MaximumSessionBytes < request.MaximumRecordBytes
            || request.MaximumSessionBytes > MaximumAllowedSessionBytes)
        {
            return $"The content session limit must be at least the per-record limit and at most {MaximumAllowedSessionBytes} bytes.";
        }

        if (request.Retention != ContentRetentionMode.StopAtLimit)
        {
            return "StopAtLimit is the only content retention behavior with a bounded request contract.";
        }

        if (request.Inspection is not ContentInspectionMode.Disabled and not ContentInspectionMode.HexAndText)
        {
            return "Content inspection must be Disabled or HexAndText.";
        }

        return null;
    }

    public static ContentCaptureDecision Compile(ContentCaptureRequest request)
    {
        string? refusal = Validate(request);
        if (refusal is not null)
        {
            throw new ArgumentException(refusal, nameof(request));
        }

        WindowsSourceDefinition source = WindowsSourceCatalog.Find(request.SourceId)!;
        ValidatedContentSourceContract? contract = source.ContentContract;
        if (contract is not null)
        {
            ValidateCatalogContract(source, contract);
        }
        bool impactMeasured = contract is not null
            && contract.Overhead != OverheadClass.Unmeasured
            && !string.IsNullOrWhiteSpace(contract.CaptureImpactEvidence);
        bool scopeEnforceable = contract?.EnforcesProcessScopeBeforePersistence == true
            && contract.EnforcesChannelScopeBeforePersistence;
        bool evidenceComplete = contract is not null && scopeEnforceable && impactMeasured;
        var blockers = new List<string>(3);
        if (contract is null)
        {
            blockers.Add("no approved payload descriptor/body contract; content-producing events remain denied");
        }

        if (!scopeEnforceable)
        {
            blockers.Add("process/channel scope is not proven enforceable");
        }

        if (!impactMeasured)
        {
            blockers.Add("capture impact is unmeasured");
        }

        string availability = evidenceComplete
            ? $"Source '{source.SourceId}' has complete content-source evidence, but the production scoped-content admission compiler is not implemented. No provider request or production admission policy was compiled."
            : $"Source '{source.SourceId}' is unavailable for scoped content: {string.Join("; ", blockers)}. "
                + "No provider request or production admission policy was compiled.";
        string inspection = request.Inspection == ContentInspectionMode.Disabled
            ? "Content preview inspection remains disabled."
            : "Separate inspection consent allows bounded inert hex/text previews only; it does not authorize search, decoding, reassembly, or export.";
        string disclosure =
            $"Requested behavior (not active while this request is blocked): each record may retain at most {request.MaximumRecordBytes} bytes; "
            + "a longer body keeps only its prefix with original length and truncation recorded. The capture must "
            + $"stop before retained content exceeds {request.MaximumSessionBytes} bytes. Unknown schemas and "
            + $"out-of-scope bodies are omitted before persistence. {inspection}";

        return new()
        {
            SourceId = source.SourceId,
            Mechanism = request.Mechanism,
            ProcessIds = [.. request.ProcessIds.Order()],
            ChannelSelectors = [.. request.ChannelSelectors.Order(StringComparer.Ordinal)],
            MaximumRecordBytes = request.MaximumRecordBytes,
            MaximumSessionBytes = request.MaximumSessionBytes,
            Retention = request.Retention,
            Inspection = request.Inspection,
            RecordLimitBehavior = ContentRecordLimitBehavior.RetainPrefixAndRecordTruncation,
            UnknownSchemaBehavior = UnknownContentSchemaBehavior.OmitBodyAndKeepHeaderDiagnostic,
            ApprovedEventIds = contract is null ? [] : [.. contract.EventIds.Distinct().Order()],
            ApprovedSourceFields = contract is null ? [] : [.. contract.SourceFields.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            ApprovedClassifications = contract is null ? [] : [.. contract.Classifications.Distinct().Order()],
            SourceBodyContractAvailable = contract is not null,
            ScopeEnforceable = scopeEnforceable,
            CaptureImpactMeasured = impactMeasured,
            CaptureImpactEvidence = impactMeasured ? contract!.CaptureImpactEvidence : null,
            SourceEvidenceComplete = evidenceComplete,
            AdmissionPolicyAvailable = false,
            AvailabilityReason = availability,
            Disclosure = disclosure,
        };
    }

    private static void ValidateCatalogContract(
        WindowsSourceDefinition source,
        ValidatedContentSourceContract contract)
    {
        bool invalidEvents = contract.EventIds is null
            || contract.EventIds.Count == 0
            || contract.EventIds.Any(eventId => eventId is < 0 or > ushort.MaxValue)
            || contract.EventIds.Distinct().Count() != contract.EventIds.Count;
        bool invalidFields = contract.SourceFields is null
            || contract.SourceFields.Count == 0
            || contract.SourceFields.Any(field =>
                string.IsNullOrWhiteSpace(field) || field.Length > 128 || field.Any(char.IsControl))
            || contract.SourceFields.Distinct(StringComparer.Ordinal).Count() != contract.SourceFields.Count;
        bool invalidClassifications = contract.Classifications is null
            || contract.Classifications.Count == 0
            || contract.Classifications.Any(classification => !Enum.IsDefined(classification))
            || contract.Classifications.Distinct().Count() != contract.Classifications.Count;
        if (invalidEvents
            || invalidFields
            || invalidClassifications
            || !Enum.IsDefined(contract.Overhead)
            || string.IsNullOrWhiteSpace(contract.ValidationEvidence)
            || contract.ValidationEvidence.Length > 512
            || contract.ValidationEvidence.Any(char.IsControl)
            || contract.CaptureImpactEvidence is null
            || contract.CaptureImpactEvidence.Length > 512
            || contract.CaptureImpactEvidence.Any(char.IsControl)
            || (contract.Overhead == OverheadClass.Unmeasured
                ? !string.IsNullOrWhiteSpace(contract.CaptureImpactEvidence)
                : string.IsNullOrWhiteSpace(contract.CaptureImpactEvidence)))
        {
            throw new InvalidOperationException(
                $"Source '{source.SourceId}' has an invalid validated-content catalog contract.");
        }
    }
}
