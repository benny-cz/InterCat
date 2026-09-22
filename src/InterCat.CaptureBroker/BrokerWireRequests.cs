using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.CaptureBroker;

[Flags]
public enum BrokerProtocolFeature : ulong
{
    PreparedPlanDigest = 1UL << 0,
    OwnerLeases = 1UL << 1,
    StopMilestones = 1UL << 2,
    DurableRecovery = 1UL << 3,
}

public enum BrokerRetentionPolicy : int
{
    StopAtLimit = 1,
}

public sealed record BrokerCaptureQuota(
    int MaximumDurationSeconds,
    long MaximumJournalBytes,
    long MinimumFreeDiskBytes)
{
    public string? Validate()
    {
        if (MaximumDurationSeconds is < 1 or > 86_400)
        {
            return "Maximum capture duration must be between 1 second and 24 hours.";
        }

        if (MaximumJournalBytes is < 1_048_576 or > 1_099_511_627_776)
        {
            return "Maximum journal bytes must be between 1 MiB and 1 TiB.";
        }

        if (MinimumFreeDiskBytes is < 16_777_216 or > 1_099_511_627_776)
        {
            return "Minimum free-disk reserve must be between 16 MiB and 1 TiB.";
        }

        return MinimumFreeDiskBytes >= MaximumJournalBytes
            ? "The free-disk reserve must be smaller than the maximum journal allowance."
            : null;
    }
}

public abstract record BrokerWireRequest
{
    public abstract BrokerMessageType MessageType { get; }
}

public sealed record BrokerHelloRequest(
    Guid ClientInstanceId,
    int MinimumMajor,
    int MaximumMajor,
    BrokerProtocolFeature RequestedFeatures,
    BrokerProtocolFeature RequiredFeatures) : BrokerWireRequest
{
    public override BrokerMessageType MessageType => BrokerMessageType.Hello;
}

public sealed record BrokerGetCapabilitiesRequest : BrokerWireRequest
{
    public override BrokerMessageType MessageType => BrokerMessageType.GetCapabilities;
}

public sealed record BrokerPrepareCaptureRequest(
    string ProfileId,
    Mechanism? FocusedMechanism,
    IReadOnlyList<int> FocusedProcessIds,
    bool AllowBroaderCapture,
    bool RequestOriginalDiagnosticEtl,
    BrokerCaptureQuota Quota,
    BrokerRetentionPolicy Retention,
    ContentCaptureRequest? Content) : BrokerWireRequest
{
    public override BrokerMessageType MessageType => BrokerMessageType.PrepareCapture;

    public CaptureProfileRequest ToProfileRequest()
    {
        CaptureProfileDescriptor profile = CaptureProfileCatalog.Find(ProfileId)
            ?? throw new InvalidOperationException($"Profile '{ProfileId}' is not in the local catalog.");
        return new(
            profile.Kind,
            RequestOriginalDiagnosticEtl,
            FocusedMechanism,
            FocusedProcessIds,
            AllowBroaderCapture,
            Content);
    }
}

public sealed record BrokerStartCaptureRequest(string PreparedToken, Guid RequestId) : BrokerWireRequest
{
    public override BrokerMessageType MessageType => BrokerMessageType.StartCapture;
}

public sealed record BrokerGetStatusRequest(CaptureId CaptureId) : BrokerWireRequest
{
    public override BrokerMessageType MessageType => BrokerMessageType.GetStatus;
}

public sealed record BrokerStopCaptureRequest(CaptureId CaptureId, Guid RequestId) : BrokerWireRequest
{
    public override BrokerMessageType MessageType => BrokerMessageType.StopCapture;
}

public sealed record BrokerRenewOwnerLeaseRequest(CaptureId CaptureId) : BrokerWireRequest
{
    public override BrokerMessageType MessageType => BrokerMessageType.RenewOwnerLease;
}

/// <summary>Strict typed request codec; no runtime/domain object graph is deserialized from the wire.</summary>
public static class BrokerWireRequestCodec
{
    private static readonly IReadOnlySet<ushort> HelloFields = Set(1, 2, 3, 4, 5);
    private static readonly IReadOnlySet<ushort> PrepareFields = Set(
        1, 2, 3, 4, 5, 6, 7, 8, 9,
        20, 21, 22, 23, 24, 25, 26, 27);
    private static readonly IReadOnlySet<ushort> StartFields = Set(1, 2);
    private static readonly IReadOnlySet<ushort> CaptureFields = Set(1);
    private static readonly IReadOnlySet<ushort> StopFields = Set(1, 2);
    private static readonly IReadOnlySet<ushort> NoFields = new HashSet<ushort>();

    public static BrokerWireFrame Encode(BrokerWireRequest request, Guid correlationId)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var fields = new BrokerWireFieldWriter();
        switch (request)
        {
            case BrokerHelloRequest hello:
                ValidateHello(hello);
                fields.WriteGuid(1, hello.ClientInstanceId);
                fields.WriteInt32(2, hello.MinimumMajor);
                fields.WriteInt32(3, hello.MaximumMajor);
                fields.WriteUInt64(4, (ulong)hello.RequestedFeatures);
                fields.WriteUInt64(5, (ulong)hello.RequiredFeatures);
                break;
            case BrokerGetCapabilitiesRequest:
                break;
            case BrokerPrepareCaptureRequest prepare:
                ValidatePrepare(prepare);
                WritePrepare(fields, prepare);
                break;
            case BrokerStartCaptureRequest start:
                ValidateStart(start);
                fields.WriteString(1, start.PreparedToken);
                fields.WriteGuid(2, start.RequestId);
                break;
            case BrokerGetStatusRequest status:
                ValidateCaptureId(status.CaptureId);
                fields.WriteGuid(1, status.CaptureId.Value);
                break;
            case BrokerStopCaptureRequest stop:
                ValidateCaptureId(stop.CaptureId);
                ValidateRequestId(stop.RequestId);
                fields.WriteGuid(1, stop.CaptureId.Value);
                fields.WriteGuid(2, stop.RequestId);
                break;
            case BrokerRenewOwnerLeaseRequest renew:
                ValidateCaptureId(renew.CaptureId);
                fields.WriteGuid(1, renew.CaptureId.Value);
                break;
            default:
                throw new NotSupportedException(
                    $"Wire request type '{request.GetType().Name}' is not supported by protocol v1.");
        }

        return BrokerWireFrame.CreateRequest(request.MessageType, correlationId, fields.Build());
    }

    public static BrokerWireRequest Decode(BrokerWireFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.ProtocolMajor != BrokerWireFrameCodec.CurrentMajor)
        {
            throw new InvalidDataException(
                $"Broker frame major version {frame.ProtocolMajor} is unsupported. Send Hello using major version 1.");
        }

        if (frame.Flags != BrokerFrameAttributes.Request)
        {
            throw new InvalidDataException("The request codec accepts request frames only.");
        }

        BrokerWireRequest request = frame.MessageType switch
        {
            BrokerMessageType.Hello => ReadHello(frame.Payload.Span),
            BrokerMessageType.GetCapabilities => ReadCapabilities(frame.Payload.Span),
            BrokerMessageType.PrepareCapture => ReadPrepare(frame.Payload.Span),
            BrokerMessageType.StartCapture => ReadStart(frame.Payload.Span),
            BrokerMessageType.GetStatus => ReadStatus(frame.Payload.Span),
            BrokerMessageType.StopCapture => ReadStop(frame.Payload.Span),
            BrokerMessageType.RenewOwnerLease => ReadRenew(frame.Payload.Span),
            _ => throw new InvalidDataException(
                $"Message type {frame.MessageType} is not a broker request command."),
        };
        return request;
    }

    private static BrokerHelloRequest ReadHello(ReadOnlySpan<byte> payload)
    {
        BrokerWireFieldSet fields = BrokerWireFieldSet.Parse(payload, HelloFields);
        var request = new BrokerHelloRequest(
            fields.RequiredGuid(1),
            fields.RequiredInt32(2),
            fields.RequiredInt32(3),
            (BrokerProtocolFeature)fields.RequiredUInt64(4),
            (BrokerProtocolFeature)fields.RequiredUInt64(5));
        ValidateHello(request);
        return request;
    }

    private static BrokerGetCapabilitiesRequest ReadCapabilities(ReadOnlySpan<byte> payload)
    {
        _ = BrokerWireFieldSet.Parse(payload, NoFields);
        return new();
    }

    private static BrokerPrepareCaptureRequest ReadPrepare(ReadOnlySpan<byte> payload)
    {
        BrokerWireFieldSet fields = BrokerWireFieldSet.Parse(payload, PrepareFields);
        string profileId = fields.RequiredString(1, 64);
        int? focusedMechanism = fields.OptionalInt32(2);
        IReadOnlyList<int> focusedProcessIds = fields.OptionalInt32List(3, 64);
        bool anyContentField = Enumerable.Range(20, 8).Any(field => fields.Contains((ushort)field));
        bool allContentFields = Enumerable.Range(20, 8).All(field => fields.Contains((ushort)field));
        if (anyContentField != allContentFields)
        {
            throw new InvalidDataException("A Content request must supply its complete typed field group.");
        }

        ContentCaptureRequest? content = allContentFields && fields.RequiredString(20, 256) is string contentSource
            ? new()
            {
                SourceId = contentSource,
                Mechanism = (Mechanism)fields.RequiredInt32(21),
                ProcessIds = fields.OptionalInt32List(22, 64),
                ChannelSelectors = fields.OptionalStringList(23, 32, 256),
                MaximumRecordBytes = fields.RequiredInt32(24),
                MaximumSessionBytes = fields.RequiredInt64(25),
                Retention = (ContentRetentionMode)fields.RequiredInt32(26),
                Inspection = (ContentInspectionMode)fields.RequiredInt32(27),
            }
            : null;
        var request = new BrokerPrepareCaptureRequest(
            profileId,
            focusedMechanism is null ? null : (Mechanism)focusedMechanism.Value,
            focusedProcessIds,
            fields.RequiredBoolean(4),
            fields.RequiredBoolean(5),
            new(
                fields.RequiredInt32(6),
                fields.RequiredInt64(7),
                fields.RequiredInt64(8)),
            (BrokerRetentionPolicy)fields.RequiredInt32(9),
            content);
        ValidatePrepare(request);
        return request;
    }

    private static BrokerStartCaptureRequest ReadStart(ReadOnlySpan<byte> payload)
    {
        BrokerWireFieldSet fields = BrokerWireFieldSet.Parse(payload, StartFields);
        var request = new BrokerStartCaptureRequest(fields.RequiredString(1, 64), fields.RequiredGuid(2));
        ValidateStart(request);
        return request;
    }

    private static BrokerGetStatusRequest ReadStatus(ReadOnlySpan<byte> payload)
    {
        BrokerWireFieldSet fields = BrokerWireFieldSet.Parse(payload, CaptureFields);
        var request = new BrokerGetStatusRequest(new(fields.RequiredGuid(1)));
        ValidateCaptureId(request.CaptureId);
        return request;
    }

    private static BrokerStopCaptureRequest ReadStop(ReadOnlySpan<byte> payload)
    {
        BrokerWireFieldSet fields = BrokerWireFieldSet.Parse(payload, StopFields);
        var request = new BrokerStopCaptureRequest(new(fields.RequiredGuid(1)), fields.RequiredGuid(2));
        ValidateCaptureId(request.CaptureId);
        ValidateRequestId(request.RequestId);
        return request;
    }

    private static BrokerRenewOwnerLeaseRequest ReadRenew(ReadOnlySpan<byte> payload)
    {
        BrokerWireFieldSet fields = BrokerWireFieldSet.Parse(payload, CaptureFields);
        var request = new BrokerRenewOwnerLeaseRequest(new(fields.RequiredGuid(1)));
        ValidateCaptureId(request.CaptureId);
        return request;
    }

    private static void WritePrepare(BrokerWireFieldWriter fields, BrokerPrepareCaptureRequest request)
    {
        fields.WriteString(1, request.ProfileId);
        if (request.FocusedMechanism is not null)
        {
            fields.WriteInt32(2, (int)request.FocusedMechanism.Value, required: false);
        }

        if (request.FocusedProcessIds.Count > 0)
        {
            fields.WriteInt32List(3, request.FocusedProcessIds, required: false);
        }

        fields.WriteBoolean(4, request.AllowBroaderCapture);
        fields.WriteBoolean(5, request.RequestOriginalDiagnosticEtl);
        fields.WriteInt32(6, request.Quota.MaximumDurationSeconds);
        fields.WriteInt64(7, request.Quota.MaximumJournalBytes);
        fields.WriteInt64(8, request.Quota.MinimumFreeDiskBytes);
        fields.WriteInt32(9, (int)request.Retention);

        if (request.Content is not null)
        {
            fields.WriteString(20, request.Content.SourceId);
            fields.WriteInt32(21, (int)request.Content.Mechanism);
            fields.WriteInt32List(22, request.Content.ProcessIds);
            fields.WriteStringList(23, request.Content.ChannelSelectors);
            fields.WriteInt32(24, request.Content.MaximumRecordBytes);
            fields.WriteInt64(25, request.Content.MaximumSessionBytes);
            fields.WriteInt32(26, (int)request.Content.Retention);
            fields.WriteInt32(27, (int)request.Content.Inspection);
        }
    }

    private static void ValidateHello(BrokerHelloRequest request)
    {
        if (request.ClientInstanceId == Guid.Empty
            || request.MinimumMajor is < 1 or > ushort.MaxValue
            || request.MaximumMajor < request.MinimumMajor
            || request.MaximumMajor > ushort.MaxValue
            || (request.RequiredFeatures & ~request.RequestedFeatures) != 0)
        {
            throw new InvalidDataException(
                "Hello requires a client ID, a valid protocol range, known feature bits, and required features included in requested features.");
        }
    }

    private static void ValidatePrepare(BrokerPrepareCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Quota);
        ArgumentNullException.ThrowIfNull(request.FocusedProcessIds);
        CaptureProfileDescriptor? profile = string.IsNullOrWhiteSpace(request.ProfileId)
            ? null
            : CaptureProfileCatalog.Find(request.ProfileId);
        if (profile is null || !string.Equals(profile.Id, request.ProfileId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("PrepareCapture requires a canonical catalog profile ID.");
        }

        string? quotaProblem = request.Quota.Validate();
        if (quotaProblem is not null)
        {
            throw new InvalidDataException(quotaProblem);
        }

        if (request.Retention != BrokerRetentionPolicy.StopAtLimit)
        {
            throw new InvalidDataException("Stop-at-limit is the only broker retention policy in protocol v1.");
        }

        bool validProcessIds = request.FocusedProcessIds.Count <= 64
            && request.FocusedProcessIds.All(processId => processId > 0)
            && request.FocusedProcessIds.Distinct().Count() == request.FocusedProcessIds.Count;
        if (!validProcessIds)
        {
            throw new InvalidDataException("Focused process IDs must be positive, unique, and limited to 64.");
        }

        if (profile.Kind == CaptureProfileKind.FocusedTransport)
        {
            if (request.FocusedMechanism != Mechanism.Tcp
                || (request.AllowBroaderCapture && request.FocusedProcessIds.Count == 0)
                || request.Content is not null)
            {
                throw new InvalidDataException(
                    "Focused transport requires TCP, uses broader-capture consent only with selected PIDs, and cannot carry a Content request.");
            }
        }
        else if (request.FocusedMechanism is not null
            || request.FocusedProcessIds.Count > 0
            || request.AllowBroaderCapture)
        {
            throw new InvalidDataException(
                "Mechanism, process focus and broader-capture consent apply only to Focused transport.");
        }

        if (profile.Kind == CaptureProfileKind.Content)
        {
            string? contentProblem = ContentCapturePolicyCompiler.Validate(request.Content);
            if (contentProblem is not null)
            {
                throw new InvalidDataException(contentProblem);
            }
        }
        else if (request.Content is not null)
        {
            throw new InvalidDataException("Content scope and limits apply only to the Content profile.");
        }
    }

    private static void ValidateStart(BrokerStartCaptureRequest request)
    {
        if (!PreparedPlanRegistry.TryFingerprint(request.PreparedToken, out _))
        {
            throw new InvalidDataException("StartCapture requires a well-formed prepared-plan token.");
        }

        ValidateRequestId(request.RequestId);
    }

    private static void ValidateCaptureId(CaptureId captureId)
    {
        if (captureId.Value == Guid.Empty)
        {
            throw new InvalidDataException("A non-empty capture ID is required.");
        }
    }

    private static void ValidateRequestId(Guid requestId)
    {
        if (requestId == Guid.Empty)
        {
            throw new InvalidDataException("A non-empty request ID is required.");
        }
    }

    private static HashSet<ushort> Set(params ushort[] values) => new(values);
}

public sealed record BrokerHelloResponse(
    Guid ServerInstanceId,
    int SelectedMajor,
    int SelectedMinor,
    BrokerProtocolFeature EnabledFeatures,
    string ServerVersion);

public sealed record BrokerHelloNegotiation(BrokerHelloResponse? Response, string? Refusal)
{
    public bool Accepted => Response is not null;
}

public static class BrokerHelloNegotiator
{
    public const BrokerProtocolFeature SupportedFeatures =
        BrokerProtocolFeature.PreparedPlanDigest
        | BrokerProtocolFeature.OwnerLeases
        | BrokerProtocolFeature.StopMilestones
        | BrokerProtocolFeature.DurableRecovery;

    public static BrokerHelloNegotiation Negotiate(
        BrokerHelloRequest request,
        Guid serverInstanceId,
        string serverVersion)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverVersion);
        if (serverInstanceId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty server instance ID is required.", nameof(serverInstanceId));
        }

        try
        {
            // Encode performs the same strict validation used at the wire boundary without duplicating
            // a second public validation surface.
            _ = BrokerWireRequestCodec.Encode(request, Guid.NewGuid());
        }
        catch (InvalidDataException exception)
        {
            return new(null, exception.Message);
        }

        if (request.MinimumMajor > BrokerWireFrameCodec.CurrentMajor
            || request.MaximumMajor < BrokerWireFrameCodec.CurrentMajor)
        {
            return new(
                null,
                $"No common broker protocol major version exists; this broker supports {BrokerWireFrameCodec.CurrentMajor}.");
        }

        BrokerProtocolFeature unavailable = request.RequiredFeatures & ~SupportedFeatures;
        if (unavailable != 0)
        {
            return new(null, $"Required broker feature bits 0x{(ulong)unavailable:x} are unavailable.");
        }

        BrokerProtocolFeature enabled = request.RequestedFeatures & SupportedFeatures;
        return new(
            new(
                serverInstanceId,
                BrokerWireFrameCodec.CurrentMajor,
                BrokerWireFrameCodec.CurrentMinor,
                enabled,
                serverVersion),
            null);
    }
}
