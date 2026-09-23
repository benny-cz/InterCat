using System.Text;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.CaptureBroker;

public enum BrokerErrorCode
{
    InvalidRequest = 1,
    HelloRequired = 2,
    HelloRejected = 3,
    ProtocolStateConflict = 4,
    DuplicateCorrelationId = 5,
    CaptureUnavailable = 6,
    InternalFailure = 7,
}

public abstract record BrokerWireResponse
{
    public abstract BrokerMessageType MessageType { get; }
}

public sealed record BrokerErrorResponse(BrokerErrorCode Code, string UserMessage, bool Retryable)
    : BrokerWireResponse
{
    public override BrokerMessageType MessageType => BrokerMessageType.Error;
}

public sealed record BrokerProfileCapability(
    string ProfileId,
    string DisplayName,
    AdmissionMode Admission,
    bool CompilationAvailable,
    bool RequestPreviewAvailable,
    string Summary,
    string? UnavailableReason);

public sealed record BrokerMechanismCapability(
    Mechanism Mechanism,
    CapabilityState State,
    CapabilityTier Tier,
    string Summary,
    string? UnavailableReason);

public sealed record BrokerCapabilitiesResponse(
    DateTimeOffset ProbedAtUtc,
    string OperatingSystem,
    string BuildId,
    string Architecture,
    bool IsSupportedBuild,
    BuildSupportTier SupportTier,
    bool IsElevated,
    string AdapterVersion,
    IReadOnlyList<BrokerProfileCapability> Profiles,
    IReadOnlyList<BrokerMechanismCapability> Mechanisms) : BrokerWireResponse
{
    public override BrokerMessageType MessageType => BrokerMessageType.GetCapabilities;
}

public sealed record BrokerEffectiveSourceSummary(
    string SourceId,
    ProviderProcessScope ProcessScope,
    IReadOnlyList<int> AppliedProcessIds,
    bool CapturesOutsideRequestedProcesses,
    string Reason);

public sealed record BrokerEffectiveCaptureSummary(
    string RequestedProfileId,
    string? EffectiveProfileId,
    AdmissionMode RequestedAdmission,
    AdmissionMode? EffectiveAdmission,
    Mechanism? RequestedMechanism,
    Mechanism? EffectiveMechanism,
    IReadOnlyList<int> RequestedProcessIds,
    IReadOnlyList<int> InitialViewProcessIds,
    bool CapturesOutsideRequestedProcesses,
    bool BroaderCaptureNeedsConsent,
    bool BroaderCaptureAccepted,
    IReadOnlyList<BrokerEffectiveSourceSummary> Sources,
    string Disclosure,
    string CollectionStatement,
    BrokerCaptureQuota Quota,
    BrokerRetentionPolicy Retention,
    IReadOnlyList<string> Diagnostics,
    BrokerJournalPublication Publication = BrokerJournalPublication.OnStop,
    int PublicationIntervalMilliseconds = 0);

public sealed record BrokerPrepareCaptureResponse(
    bool Prepared,
    BrokerPrepareRefusalCode? RefusalCode,
    string? RefusalReason,
    PreparedPlanGrant? Grant,
    BrokerEffectiveCaptureSummary Summary) : BrokerWireResponse
{
    public override BrokerMessageType MessageType => BrokerMessageType.PrepareCapture;
}

public sealed record BrokerStartCaptureResponse(
    BrokerOperationCode Code,
    CaptureId? CaptureId,
    CaptureLifecycle State,
    DateTimeOffset? LeaseExpiresAtUtc,
    string? FailureReason) : BrokerWireResponse
{
    public override BrokerMessageType MessageType => BrokerMessageType.StartCapture;
}

public sealed record BrokerCaptureStatusResponse(
    CaptureId CaptureId,
    CaptureLifecycle State,
    string PlanDigest,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset LeaseExpiresAtUtc,
    BrokerStopMilestones StopMilestones,
    string? FailureReason) : BrokerWireResponse
{
    public override BrokerMessageType MessageType => BrokerMessageType.GetStatus;
}

public sealed record BrokerStopCaptureResponse(
    BrokerOperationCode Code,
    CaptureId? CaptureId,
    CaptureLifecycle State,
    BrokerStopMilestones Milestones,
    string? FailureReason) : BrokerWireResponse
{
    public override BrokerMessageType MessageType => BrokerMessageType.StopCapture;
}

public sealed record BrokerRenewOwnerLeaseResponse(
    BrokerOperationCode Code,
    CaptureId CaptureId,
    CaptureLifecycle State,
    DateTimeOffset? LeaseExpiresAtUtc,
    string? FailureReason) : BrokerWireResponse
{
    public override BrokerMessageType MessageType => BrokerMessageType.RenewOwnerLease;
}

/// <summary>Strict response codec. It intentionally exposes no runtime session name or ownership token.</summary>
public static class BrokerWireResponseCodec
{
    private const int MaximumProfiles = 32;
    private const int MaximumMechanisms = 64;
    private const int MaximumSources = 32;
    private const int MaximumDiagnostics = 32;
    private static readonly IReadOnlySet<ushort> ErrorFields = Set(1, 2, 3);
    private static readonly IReadOnlySet<ushort> HelloFields = Set(1, 2, 3, 4, 5);
    private static readonly IReadOnlySet<ushort> CapabilityFields = Set(
        1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15, 16, 20, 21, 22, 23, 24);
    private static readonly IReadOnlySet<ushort> PrepareFields = Set(
        1, 2, 3, 4, 5, 6, 7, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19,
        20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34);
    private static readonly IReadOnlySet<ushort> StartFields = Set(1, 2, 3, 4, 5);
    private static readonly IReadOnlySet<ushort> StatusFields = Set(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
    private static readonly IReadOnlySet<ushort> StopFields = Set(1, 2, 3, 4, 5, 6, 7, 8, 9);
    private static readonly IReadOnlySet<ushort> LeaseFields = Set(1, 2, 3, 4, 5);

    public static BrokerWireFrame Encode(BrokerWireResponse response, Guid correlationId)
    {
        ArgumentNullException.ThrowIfNull(response);
        Validate(response);
        using var fields = new BrokerWireFieldWriter();
        switch (response)
        {
            case BrokerErrorResponse error:
                fields.WriteInt32(1, (int)error.Code);
                fields.WriteString(2, error.UserMessage);
                fields.WriteBoolean(3, error.Retryable);
                break;
            case BrokerHelloResponse hello:
                fields.WriteGuid(1, hello.ServerInstanceId);
                fields.WriteInt32(2, hello.SelectedMajor);
                fields.WriteInt32(3, hello.SelectedMinor);
                fields.WriteUInt64(4, (ulong)hello.EnabledFeatures);
                fields.WriteString(5, hello.ServerVersion);
                break;
            case BrokerCapabilitiesResponse capabilities:
                WriteCapabilities(fields, capabilities);
                break;
            case BrokerPrepareCaptureResponse prepare:
                WritePrepare(fields, prepare);
                break;
            case BrokerStartCaptureResponse start:
                WriteStart(fields, start);
                break;
            case BrokerCaptureStatusResponse status:
                WriteStatus(fields, status);
                break;
            case BrokerStopCaptureResponse stop:
                WriteStop(fields, stop);
                break;
            case BrokerRenewOwnerLeaseResponse lease:
                WriteLease(fields, lease);
                break;
            default:
                throw new NotSupportedException($"Wire response type '{response.GetType().Name}' is unsupported.");
        }

        return BrokerWireFrame.CreateResponse(
            response.MessageType,
            correlationId,
            fields.Build(),
            isError: response is BrokerErrorResponse);
    }

    public static BrokerWireResponse Decode(BrokerWireFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.ProtocolMajor != BrokerWireFrameCodec.CurrentMajor)
        {
            throw new InvalidDataException($"Broker response major version {frame.ProtocolMajor} is unsupported.");
        }

        bool error = frame.Flags == (BrokerFrameAttributes.Response | BrokerFrameAttributes.Error);
        if (frame.Flags != BrokerFrameAttributes.Response && !error)
        {
            throw new InvalidDataException("The response codec accepts response frames only.");
        }

        if (error != (frame.MessageType == BrokerMessageType.Error))
        {
            throw new InvalidDataException("Broker error attributes and message type disagree.");
        }

        BrokerWireResponse response = frame.MessageType switch
        {
            BrokerMessageType.Error => ReadError(frame.Payload.Span),
            BrokerMessageType.Hello => ReadHello(frame.Payload.Span),
            BrokerMessageType.GetCapabilities => ReadCapabilities(frame.Payload.Span),
            BrokerMessageType.PrepareCapture => ReadPrepare(frame.Payload.Span),
            BrokerMessageType.StartCapture => ReadStart(frame.Payload.Span),
            BrokerMessageType.GetStatus => ReadStatus(frame.Payload.Span),
            BrokerMessageType.StopCapture => ReadStop(frame.Payload.Span),
            BrokerMessageType.RenewOwnerLease => ReadLease(frame.Payload.Span),
            _ => throw new InvalidDataException("The broker response message type is unsupported."),
        };
        Validate(response);
        return response;
    }

    private static void WriteCapabilities(BrokerWireFieldWriter fields, BrokerCapabilitiesResponse value)
    {
        fields.WriteInt64(1, value.ProbedAtUtc.UtcTicks);
        fields.WriteString(2, value.OperatingSystem);
        fields.WriteString(3, value.BuildId);
        fields.WriteString(4, value.Architecture);
        fields.WriteBoolean(5, value.IsSupportedBuild);
        fields.WriteInt32(6, (int)value.SupportTier);
        fields.WriteBoolean(7, value.IsElevated);
        fields.WriteString(8, value.AdapterVersion);
        fields.WriteStringList(10, [.. value.Profiles.Select(item => item.ProfileId)]);
        fields.WriteStringList(11, [.. value.Profiles.Select(item => item.DisplayName)]);
        fields.WriteInt32List(12, [.. value.Profiles.Select(item => (int)item.Admission)]);
        fields.WriteInt32List(13, [.. value.Profiles.Select(item => item.CompilationAvailable ? 1 : 0)]);
        fields.WriteInt32List(14, [.. value.Profiles.Select(item => item.RequestPreviewAvailable ? 1 : 0)]);
        fields.WriteStringList(15, [.. value.Profiles.Select(item => item.Summary)]);
        fields.WriteStringList(16, [.. value.Profiles.Select(item => item.UnavailableReason ?? string.Empty)]);
        fields.WriteInt32List(20, [.. value.Mechanisms.Select(item => (int)item.Mechanism)]);
        fields.WriteInt32List(21, [.. value.Mechanisms.Select(item => (int)item.State)]);
        fields.WriteInt32List(22, [.. value.Mechanisms.Select(item => (int)item.Tier)]);
        fields.WriteStringList(23, [.. value.Mechanisms.Select(item => item.Summary)]);
        fields.WriteStringList(24, [.. value.Mechanisms.Select(item => item.UnavailableReason ?? string.Empty)]);
    }

    private static void WritePrepare(BrokerWireFieldWriter fields, BrokerPrepareCaptureResponse value)
    {
        fields.WriteBoolean(1, value.Prepared);
        if (value.RefusalCode is not null)
        {
            fields.WriteInt32(2, (int)value.RefusalCode.Value, required: false);
            fields.WriteString(3, value.RefusalReason!, required: false);
        }

        if (value.Grant is not null)
        {
            fields.WriteString(4, value.Grant.Token, required: false);
            fields.WriteString(5, value.Grant.PlanDigest, required: false);
            fields.WriteInt64(6, value.Grant.IssuedAtUtc.UtcTicks, required: false);
            fields.WriteInt64(7, value.Grant.ExpiresAtUtc.UtcTicks, required: false);
        }

        BrokerEffectiveCaptureSummary summary = value.Summary;
        fields.WriteString(10, summary.RequestedProfileId);
        if (summary.EffectiveProfileId is not null)
        {
            fields.WriteString(11, summary.EffectiveProfileId, required: false);
        }

        fields.WriteInt32(12, (int)summary.RequestedAdmission);
        if (summary.EffectiveAdmission is not null)
        {
            fields.WriteInt32(13, (int)summary.EffectiveAdmission.Value, required: false);
        }

        if (summary.RequestedMechanism is not null)
        {
            fields.WriteInt32(14, (int)summary.RequestedMechanism.Value, required: false);
        }

        if (summary.EffectiveMechanism is not null)
        {
            fields.WriteInt32(15, (int)summary.EffectiveMechanism.Value, required: false);
        }

        fields.WriteInt32List(16, summary.RequestedProcessIds);
        fields.WriteInt32List(17, summary.InitialViewProcessIds);
        fields.WriteBoolean(18, summary.CapturesOutsideRequestedProcesses);
        fields.WriteBoolean(19, summary.BroaderCaptureNeedsConsent);
        fields.WriteBoolean(20, summary.BroaderCaptureAccepted);
        fields.WriteStringList(21, [.. summary.Sources.Select(item => item.SourceId)]);
        fields.WriteInt32List(22, [.. summary.Sources.Select(item => (int)item.ProcessScope)]);
        fields.WriteInt32Lists(23, [.. summary.Sources.Select(item => item.AppliedProcessIds)]);
        fields.WriteInt32List(24, [.. summary.Sources.Select(item => item.CapturesOutsideRequestedProcesses ? 1 : 0)]);
        fields.WriteStringList(25, [.. summary.Sources.Select(item => item.Reason)]);
        fields.WriteString(26, summary.Disclosure);
        fields.WriteString(27, summary.CollectionStatement);
        fields.WriteInt32(28, summary.Quota.MaximumDurationSeconds);
        fields.WriteInt64(29, summary.Quota.MaximumJournalBytes);
        fields.WriteInt64(30, summary.Quota.MinimumFreeDiskBytes);
        fields.WriteInt32(31, (int)summary.Retention);
        fields.WriteStringList(32, summary.Diagnostics);
        fields.WriteInt32(33, (int)summary.Publication, required: false);
        fields.WriteInt32(34, summary.PublicationIntervalMilliseconds, required: false);
    }

    private static void WriteStart(BrokerWireFieldWriter fields, BrokerStartCaptureResponse value)
    {
        fields.WriteInt32(1, (int)value.Code);
        WriteOptionalCapture(fields, 2, value.CaptureId);
        fields.WriteInt32(3, (int)value.State);
        WriteOptionalTime(fields, 4, value.LeaseExpiresAtUtc);
        WriteOptionalReason(fields, 5, value.FailureReason);
    }

    private static void WriteStatus(BrokerWireFieldWriter fields, BrokerCaptureStatusResponse value)
    {
        fields.WriteGuid(1, value.CaptureId.Value);
        fields.WriteInt32(2, (int)value.State);
        fields.WriteString(3, value.PlanDigest);
        fields.WriteInt64(4, value.CreatedAtUtc.UtcTicks);
        fields.WriteInt64(5, value.UpdatedAtUtc.UtcTicks);
        fields.WriteInt64(6, value.LeaseExpiresAtUtc.UtcTicks);
        WriteMilestones(fields, 7, value.StopMilestones);
        WriteOptionalReason(fields, 12, value.FailureReason);
    }

    private static void WriteStop(BrokerWireFieldWriter fields, BrokerStopCaptureResponse value)
    {
        fields.WriteInt32(1, (int)value.Code);
        WriteOptionalCapture(fields, 2, value.CaptureId);
        fields.WriteInt32(3, (int)value.State);
        WriteMilestones(fields, 4, value.Milestones);
        WriteOptionalReason(fields, 9, value.FailureReason);
    }

    private static void WriteLease(BrokerWireFieldWriter fields, BrokerRenewOwnerLeaseResponse value)
    {
        fields.WriteInt32(1, (int)value.Code);
        fields.WriteGuid(2, value.CaptureId.Value);
        fields.WriteInt32(3, (int)value.State);
        WriteOptionalTime(fields, 4, value.LeaseExpiresAtUtc);
        WriteOptionalReason(fields, 5, value.FailureReason);
    }

    private static BrokerErrorResponse ReadError(ReadOnlySpan<byte> payload)
    {
        BrokerWireFieldSet fields = BrokerWireFieldSet.Parse(payload, ErrorFields);
        return new((BrokerErrorCode)fields.RequiredInt32(1), fields.RequiredString(2, 512), fields.RequiredBoolean(3));
    }

    private static BrokerHelloResponse ReadHello(ReadOnlySpan<byte> payload)
    {
        BrokerWireFieldSet fields = BrokerWireFieldSet.Parse(payload, HelloFields);
        return new(
            fields.RequiredGuid(1),
            fields.RequiredInt32(2),
            fields.RequiredInt32(3),
            (BrokerProtocolFeature)fields.RequiredUInt64(4),
            fields.RequiredString(5, 128));
    }

    private static BrokerCapabilitiesResponse ReadCapabilities(ReadOnlySpan<byte> payload)
    {
        BrokerWireFieldSet fields = BrokerWireFieldSet.Parse(payload, CapabilityFields);
        IReadOnlyList<string> profileIds = fields.RequiredStringList(10, MaximumProfiles, 64);
        IReadOnlyList<string> profileNames = fields.RequiredStringList(11, MaximumProfiles, 128);
        IReadOnlyList<int> admissions = fields.RequiredInt32List(12, MaximumProfiles);
        IReadOnlyList<int> compilation = fields.RequiredInt32List(13, MaximumProfiles);
        IReadOnlyList<int> previews = fields.RequiredInt32List(14, MaximumProfiles);
        IReadOnlyList<string> profileSummaries = fields.RequiredStringList(15, MaximumProfiles, 512);
        IReadOnlyList<string> profileReasons = fields.RequiredStringList(16, MaximumProfiles, 512);
        RequireParallel(profileIds.Count, profileNames.Count, admissions.Count, compilation.Count, previews.Count, profileSummaries.Count, profileReasons.Count);
        BrokerProfileCapability[] profiles = new BrokerProfileCapability[profileIds.Count];
        for (int index = 0; index < profiles.Length; index++)
        {
            profiles[index] = new(
                profileIds[index],
                profileNames[index],
                (AdmissionMode)admissions[index],
                ReadListBoolean(compilation[index]),
                ReadListBoolean(previews[index]),
                profileSummaries[index],
                EmptyToNull(profileReasons[index]));
        }

        IReadOnlyList<int> mechanisms = fields.RequiredInt32List(20, MaximumMechanisms);
        IReadOnlyList<int> states = fields.RequiredInt32List(21, MaximumMechanisms);
        IReadOnlyList<int> tiers = fields.RequiredInt32List(22, MaximumMechanisms);
        IReadOnlyList<string> mechanismSummaries = fields.RequiredStringList(23, MaximumMechanisms, 512);
        IReadOnlyList<string> mechanismReasons = fields.RequiredStringList(24, MaximumMechanisms, 512);
        RequireParallel(mechanisms.Count, states.Count, tiers.Count, mechanismSummaries.Count, mechanismReasons.Count);
        BrokerMechanismCapability[] mechanismItems = new BrokerMechanismCapability[mechanisms.Count];
        for (int index = 0; index < mechanismItems.Length; index++)
        {
            mechanismItems[index] = new(
                (Mechanism)mechanisms[index],
                (CapabilityState)states[index],
                (CapabilityTier)tiers[index],
                mechanismSummaries[index],
                EmptyToNull(mechanismReasons[index]));
        }

        return new(
            ReadTime(fields.RequiredInt64(1), 1),
            fields.RequiredString(2, 512),
            fields.RequiredString(3, 128),
            fields.RequiredString(4, 32),
            fields.RequiredBoolean(5),
            (BuildSupportTier)fields.RequiredInt32(6),
            fields.RequiredBoolean(7),
            fields.RequiredString(8, 128),
            profiles,
            mechanismItems);
    }

    private static BrokerPrepareCaptureResponse ReadPrepare(ReadOnlySpan<byte> payload)
    {
        BrokerWireFieldSet fields = BrokerWireFieldSet.Parse(payload, PrepareFields);
        bool prepared = fields.RequiredBoolean(1);
        int? refusalCode = fields.OptionalInt32(2);
        string? refusalReason = fields.OptionalString(3, 512);
        string? token = fields.OptionalString(4, 64);
        string? digest = fields.OptionalString(5, 80);
        long? issued = fields.OptionalInt64(6);
        long? expires = fields.OptionalInt64(7);
        bool anyGrant = token is not null || digest is not null || issued is not null || expires is not null;
        bool allGrant = token is not null && digest is not null && issued is not null && expires is not null;
        if (anyGrant != allGrant)
        {
            throw new InvalidDataException("A prepared-plan grant field group must be complete.");
        }

        PreparedPlanGrant? grant = allGrant
            ? new(token!, digest!, ReadTime(issued!.Value, 6), ReadTime(expires!.Value, 7))
            : null;
        IReadOnlyList<string> sourceIds = fields.RequiredStringList(21, MaximumSources, 256);
        IReadOnlyList<int> scopes = fields.RequiredInt32List(22, MaximumSources);
        IReadOnlyList<IReadOnlyList<int>> appliedProcessIds = fields.RequiredInt32Lists(23, MaximumSources, 64);
        IReadOnlyList<int> outsideFlags = fields.RequiredInt32List(24, MaximumSources);
        IReadOnlyList<string> reasons = fields.RequiredStringList(25, MaximumSources, 512);
        RequireParallel(sourceIds.Count, scopes.Count, appliedProcessIds.Count, outsideFlags.Count, reasons.Count);
        BrokerEffectiveSourceSummary[] sources = new BrokerEffectiveSourceSummary[sourceIds.Count];
        for (int index = 0; index < sources.Length; index++)
        {
            sources[index] = new(
                sourceIds[index],
                (ProviderProcessScope)scopes[index],
                appliedProcessIds[index],
                ReadListBoolean(outsideFlags[index]),
                reasons[index]);
        }

        var summary = new BrokerEffectiveCaptureSummary(
            fields.RequiredString(10, 64),
            fields.OptionalString(11, 64),
            (AdmissionMode)fields.RequiredInt32(12),
            fields.OptionalInt32(13) is int effectiveAdmission ? (AdmissionMode)effectiveAdmission : null,
            fields.OptionalInt32(14) is int requestedMechanism ? (Mechanism)requestedMechanism : null,
            fields.OptionalInt32(15) is int effectiveMechanism ? (Mechanism)effectiveMechanism : null,
            fields.RequiredInt32List(16, 64),
            fields.RequiredInt32List(17, 64),
            fields.RequiredBoolean(18),
            fields.RequiredBoolean(19),
            fields.RequiredBoolean(20),
            sources,
            fields.RequiredString(26, 1024),
            fields.RequiredString(27, 2048),
            new(fields.RequiredInt32(28), fields.RequiredInt64(29), fields.RequiredInt64(30)),
            (BrokerRetentionPolicy)fields.RequiredInt32(31),
            fields.RequiredStringList(32, MaximumDiagnostics, 512),
            fields.OptionalInt32(33) is int publication
                ? (BrokerJournalPublication)publication
                : BrokerJournalPublication.OnStop,
            fields.OptionalInt32(34) ?? 0);
        return new(
            prepared,
            refusalCode is null ? null : (BrokerPrepareRefusalCode)refusalCode.Value,
            refusalReason,
            grant,
            summary);
    }

    private static BrokerStartCaptureResponse ReadStart(ReadOnlySpan<byte> payload)
    {
        BrokerWireFieldSet fields = BrokerWireFieldSet.Parse(payload, StartFields);
        return new(
            (BrokerOperationCode)fields.RequiredInt32(1),
            ReadOptionalCapture(fields, 2),
            (CaptureLifecycle)fields.RequiredInt32(3),
            ReadOptionalTime(fields.OptionalInt64(4), 4),
            fields.OptionalString(5, 512));
    }

    private static BrokerCaptureStatusResponse ReadStatus(ReadOnlySpan<byte> payload)
    {
        BrokerWireFieldSet fields = BrokerWireFieldSet.Parse(payload, StatusFields);
        return new(
            new(fields.RequiredGuid(1)),
            (CaptureLifecycle)fields.RequiredInt32(2),
            fields.RequiredString(3, 80),
            ReadTime(fields.RequiredInt64(4), 4),
            ReadTime(fields.RequiredInt64(5), 5),
            ReadTime(fields.RequiredInt64(6), 6),
            ReadMilestones(fields, 7),
            fields.OptionalString(12, 512));
    }

    private static BrokerStopCaptureResponse ReadStop(ReadOnlySpan<byte> payload)
    {
        BrokerWireFieldSet fields = BrokerWireFieldSet.Parse(payload, StopFields);
        return new(
            (BrokerOperationCode)fields.RequiredInt32(1),
            ReadOptionalCapture(fields, 2),
            (CaptureLifecycle)fields.RequiredInt32(3),
            ReadMilestones(fields, 4),
            fields.OptionalString(9, 512));
    }

    private static BrokerRenewOwnerLeaseResponse ReadLease(ReadOnlySpan<byte> payload)
    {
        BrokerWireFieldSet fields = BrokerWireFieldSet.Parse(payload, LeaseFields);
        return new(
            (BrokerOperationCode)fields.RequiredInt32(1),
            new(fields.RequiredGuid(2)),
            (CaptureLifecycle)fields.RequiredInt32(3),
            ReadOptionalTime(fields.OptionalInt64(4), 4),
            fields.OptionalString(5, 512));
    }

    private static void Validate(BrokerWireResponse response)
    {
        switch (response)
        {
            case BrokerErrorResponse error:
                RequireEnum(error.Code, nameof(error.Code));
                RequireText(error.UserMessage, 512, nameof(error.UserMessage));
                break;
            case BrokerHelloResponse hello:
                if (hello.ServerInstanceId == Guid.Empty
                    || hello.SelectedMajor != BrokerWireFrameCodec.CurrentMajor
                    || hello.SelectedMinor < 0
                    || hello.SelectedMinor > ushort.MaxValue
                    || (hello.EnabledFeatures & ~BrokerHelloNegotiator.SupportedFeatures) != 0)
                {
                    throw new InvalidDataException("The Hello response contains invalid negotiated values.");
                }

                RequireText(hello.ServerVersion, 128, nameof(hello.ServerVersion));
                break;
            case BrokerCapabilitiesResponse capabilities:
                ValidateCapabilities(capabilities);
                break;
            case BrokerPrepareCaptureResponse prepare:
                ValidatePrepare(prepare);
                break;
            case BrokerStartCaptureResponse start:
                ValidateOperation(start.Code, start.CaptureId, start.State, start.LeaseExpiresAtUtc, start.FailureReason);
                break;
            case BrokerCaptureStatusResponse status:
                ValidateCapture(status.CaptureId);
                RequireDigest(status.PlanDigest);
                RequireEnum(status.State, nameof(status.State));
                ValidateTimes(status.CreatedAtUtc, status.UpdatedAtUtc, status.LeaseExpiresAtUtc);
                RequireText(status.FailureReason, 512, nameof(status.FailureReason), optional: true);
                break;
            case BrokerStopCaptureResponse stop:
                ValidateOperation(stop.Code, stop.CaptureId, stop.State, null, stop.FailureReason);
                break;
            case BrokerRenewOwnerLeaseResponse lease:
                ValidateCapture(lease.CaptureId);
                ValidateOperation(lease.Code, lease.CaptureId, lease.State, lease.LeaseExpiresAtUtc, lease.FailureReason);
                break;
            default:
                throw new NotSupportedException($"Wire response type '{response.GetType().Name}' is unsupported.");
        }
    }

    private static void ValidateCapabilities(BrokerCapabilitiesResponse value)
    {
        RequireTime(value.ProbedAtUtc, nameof(value.ProbedAtUtc));
        RequireText(value.OperatingSystem, 512, nameof(value.OperatingSystem));
        RequireText(value.BuildId, 128, nameof(value.BuildId));
        RequireText(value.Architecture, 32, nameof(value.Architecture));
        RequireEnum(value.SupportTier, nameof(value.SupportTier));
        RequireText(value.AdapterVersion, 128, nameof(value.AdapterVersion));
        RequireCount(value.Profiles, MaximumProfiles, nameof(value.Profiles));
        RequireCount(value.Mechanisms, MaximumMechanisms, nameof(value.Mechanisms));
        if (value.Profiles.Select(item => item.ProfileId).Distinct(StringComparer.Ordinal).Count() != value.Profiles.Count
            || value.Mechanisms.Select(item => item.Mechanism).Distinct().Count() != value.Mechanisms.Count)
        {
            throw new InvalidDataException("Capability profile and mechanism identifiers must be unique.");
        }

        foreach (BrokerProfileCapability profile in value.Profiles)
        {
            RequireText(profile.ProfileId, 64, nameof(profile.ProfileId));
            RequireText(profile.DisplayName, 128, nameof(profile.DisplayName));
            RequireEnum(profile.Admission, nameof(profile.Admission));
            RequireText(profile.Summary, 512, nameof(profile.Summary));
            RequireText(profile.UnavailableReason, 512, nameof(profile.UnavailableReason), optional: true);
        }

        foreach (BrokerMechanismCapability mechanism in value.Mechanisms)
        {
            RequireEnum(mechanism.Mechanism, nameof(mechanism.Mechanism));
            RequireEnum(mechanism.State, nameof(mechanism.State));
            RequireEnum(mechanism.Tier, nameof(mechanism.Tier));
            RequireText(mechanism.Summary, 512, nameof(mechanism.Summary));
            RequireText(mechanism.UnavailableReason, 512, nameof(mechanism.UnavailableReason), optional: true);
        }
    }

    private static void ValidatePrepare(BrokerPrepareCaptureResponse value)
    {
        if (value.Prepared != (value.Grant is not null)
            || value.Prepared == (value.RefusalCode is not null)
            || value.Prepared == (value.RefusalReason is not null))
        {
            throw new InvalidDataException("A prepare response must contain exactly one complete grant or refusal.");
        }

        if (value.Grant is not null)
        {
            if (!PreparedPlanRegistry.TryFingerprint(value.Grant.Token, out _)
                || value.Grant.ExpiresAtUtc <= value.Grant.IssuedAtUtc)
            {
                throw new InvalidDataException("The prepared-plan grant is invalid.");
            }

            RequireDigest(value.Grant.PlanDigest);
            RequireTime(value.Grant.IssuedAtUtc, nameof(value.Grant.IssuedAtUtc));
            RequireTime(value.Grant.ExpiresAtUtc, nameof(value.Grant.ExpiresAtUtc));
        }
        else
        {
            RequireEnum(value.RefusalCode!.Value, nameof(value.RefusalCode));
            RequireText(value.RefusalReason, 512, nameof(value.RefusalReason));
        }

        BrokerEffectiveCaptureSummary summary = value.Summary
            ?? throw new InvalidDataException("A prepare response requires an effective summary.");
        RequireText(summary.RequestedProfileId, 64, nameof(summary.RequestedProfileId));
        RequireText(summary.EffectiveProfileId, 64, nameof(summary.EffectiveProfileId), optional: true);
        RequireEnum(summary.RequestedAdmission, nameof(summary.RequestedAdmission));
        if (summary.EffectiveAdmission is not null)
        {
            RequireEnum(summary.EffectiveAdmission.Value, nameof(summary.EffectiveAdmission));
        }

        if (summary.RequestedMechanism is not null)
        {
            RequireEnum(summary.RequestedMechanism.Value, nameof(summary.RequestedMechanism));
        }

        if (summary.EffectiveMechanism is not null)
        {
            RequireEnum(summary.EffectiveMechanism.Value, nameof(summary.EffectiveMechanism));
        }

        ValidateProcessIds(summary.RequestedProcessIds);
        ValidateProcessIds(summary.InitialViewProcessIds);
        RequireCount(summary.Sources, MaximumSources, nameof(summary.Sources));
        if (summary.Sources.Select(item => item.SourceId).Distinct(StringComparer.Ordinal).Count() != summary.Sources.Count)
        {
            throw new InvalidDataException("Effective source identifiers must be unique.");
        }

        foreach (BrokerEffectiveSourceSummary source in summary.Sources)
        {
            RequireText(source.SourceId, 256, nameof(source.SourceId));
            RequireEnum(source.ProcessScope, nameof(source.ProcessScope));
            ValidateProcessIds(source.AppliedProcessIds);
            RequireText(source.Reason, 512, nameof(source.Reason));
        }

        RequireText(summary.Disclosure, 1024, nameof(summary.Disclosure));
        RequireText(summary.CollectionStatement, 2048, nameof(summary.CollectionStatement));
        RequireCount(summary.Diagnostics, MaximumDiagnostics, nameof(summary.Diagnostics));
        foreach (string diagnostic in summary.Diagnostics)
        {
            RequireText(diagnostic, 512, "diagnostic");
        }

        string? quotaProblem = summary.Quota?.Validate();
        if (quotaProblem is not null || summary.Retention != BrokerRetentionPolicy.StopAtLimit)
        {
            throw new InvalidDataException(quotaProblem ?? "The response retention policy is unsupported.");
        }

        if (!Enum.IsDefined(summary.Publication)
            || summary.PublicationIntervalMilliseconds != BrokerJournalPublicationPolicy.IntervalMilliseconds(
                summary.Publication, summary.Quota!.MaximumDurationSeconds))
        {
            throw new InvalidDataException(
                "The response journal publication interval is not the one its policy compiles to.");
        }

        if (value.Prepared && (summary.EffectiveProfileId is null || summary.EffectiveAdmission is null))
        {
            throw new InvalidDataException("A prepared response requires an effective profile and admission mode.");
        }
    }

    private static void ValidateOperation(
        BrokerOperationCode code,
        CaptureId? captureId,
        CaptureLifecycle state,
        DateTimeOffset? lease,
        string? failure)
    {
        RequireEnum(code, nameof(code));
        RequireEnum(state, nameof(state));
        if (captureId is not null)
        {
            ValidateCapture(captureId.Value);
        }

        if (lease is not null)
        {
            RequireTime(lease.Value, nameof(lease));
        }

        RequireText(failure, 512, nameof(failure), optional: true);
    }

    private static void ValidateTimes(DateTimeOffset created, DateTimeOffset updated, DateTimeOffset lease)
    {
        RequireTime(created, nameof(created));
        RequireTime(updated, nameof(updated));
        RequireTime(lease, nameof(lease));
        if (updated < created)
        {
            throw new InvalidDataException("Capture status timestamps are inconsistent.");
        }
    }

    private static void WriteMilestones(BrokerWireFieldWriter fields, ushort firstField, BrokerStopMilestones value)
    {
        fields.WriteBoolean(firstField, value.Requested);
        fields.WriteBoolean((ushort)(firstField + 1), value.ProvidersStopped);
        fields.WriteBoolean((ushort)(firstField + 2), value.CallbacksDrained);
        fields.WriteBoolean((ushort)(firstField + 3), value.JournalFinalized);
        fields.WriteBoolean((ushort)(firstField + 4), value.AnalysisFinalized);
    }

    private static BrokerStopMilestones ReadMilestones(BrokerWireFieldSet fields, ushort firstField) =>
        new(
            fields.RequiredBoolean(firstField),
            fields.RequiredBoolean((ushort)(firstField + 1)),
            fields.RequiredBoolean((ushort)(firstField + 2)),
            fields.RequiredBoolean((ushort)(firstField + 3)),
            fields.RequiredBoolean((ushort)(firstField + 4)));

    private static void WriteOptionalCapture(BrokerWireFieldWriter fields, ushort fieldId, CaptureId? value)
    {
        if (value is not null)
        {
            fields.WriteGuid(fieldId, value.Value.Value, required: false);
        }
    }

    private static CaptureId? ReadOptionalCapture(BrokerWireFieldSet fields, ushort fieldId) =>
        fields.OptionalGuid(fieldId) is Guid value ? new(value) : null;

    private static void WriteOptionalTime(BrokerWireFieldWriter fields, ushort fieldId, DateTimeOffset? value)
    {
        if (value is not null)
        {
            fields.WriteInt64(fieldId, value.Value.UtcTicks, required: false);
        }
    }

    private static DateTimeOffset? ReadOptionalTime(long? ticks, ushort fieldId) =>
        ticks is null ? null : ReadTime(ticks.Value, fieldId);

    private static void WriteOptionalReason(BrokerWireFieldWriter fields, ushort fieldId, string? value)
    {
        if (value is not null)
        {
            fields.WriteString(fieldId, value, required: false);
        }
    }

    private static DateTimeOffset ReadTime(long ticks, ushort fieldId)
    {
        try
        {
            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException($"Timestamp field {fieldId} is outside the UTC tick range.", exception);
        }
    }

    private static bool ReadListBoolean(int value) => value switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException("A Boolean list value must be zero or one."),
    };

    private static void RequireParallel(int expected, params int[] counts)
    {
        if (counts.Any(count => count != expected))
        {
            throw new InvalidDataException("Parallel broker response lists have different lengths.");
        }
    }

    private static void RequireCount<T>(IReadOnlyCollection<T>? value, int maximum, string name)
    {
        if (value is null || value.Count > maximum)
        {
            throw new InvalidDataException($"{name} exceeds its response count bound.");
        }
    }

    private static void ValidateProcessIds(IReadOnlyList<int>? processIds)
    {
        if (processIds is null
            || processIds.Count > 64
            || processIds.Any(value => value <= 0)
            || processIds.Distinct().Count() != processIds.Count)
        {
            throw new InvalidDataException("Response process IDs must be positive, unique, and limited to 64.");
        }
    }

    private static void ValidateCapture(CaptureId captureId)
    {
        if (captureId.Value == Guid.Empty)
        {
            throw new InvalidDataException("A response capture ID cannot be empty.");
        }
    }

    private static void RequireDigest(string value)
    {
        if (value.Length != 71
            || !value.StartsWith("sha256:", StringComparison.Ordinal)
            || value[7..].Any(character => !char.IsAsciiHexDigitLower(character)))
        {
            throw new InvalidDataException("A prepared-plan digest is malformed.");
        }
    }

    private static void RequireTime(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException($"{name} must be UTC.");
        }
    }

    private static void RequireText(
        string? value,
        int maximumBytes,
        string name,
        bool optional = false)
    {
        if (value is null)
        {
            if (optional)
            {
                return;
            }

            throw new InvalidDataException($"{name} is required.");
        }

        if ((!optional && value.Length == 0)
            || Encoding.UTF8.GetByteCount(value) > maximumBytes
            || value.Any(char.IsControl))
        {
            throw new InvalidDataException($"{name} is empty, too long, or contains a control character.");
        }
    }

    private static void RequireEnum<T>(T value, string name) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new InvalidDataException($"{name} has an unsupported enum value.");
        }
    }

    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;

    private static HashSet<ushort> Set(params ushort[] values) => new(values);
}
