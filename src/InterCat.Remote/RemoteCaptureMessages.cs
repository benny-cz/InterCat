namespace InterCat.Remote;

/// <summary>Where a remote capture stands, in the contract's words (`remote-agent-v1` §4.4).</summary>
public enum RemoteCaptureLifecycle : ushort
{
    Starting = 1,
    Recording = 2,
    Stopping = 3,
    Closed = 4,
}

/// <summary>A capture a controller asks an agent to prepare (`remote-agent-v1` §4.4): a profile, a duration and a journal size.</summary>
public sealed record RemoteCaptureRequest(string Profile, uint Seconds, uint JournalMebibytes)
{
    internal const int MaximumProfileBytes = 64;

    internal byte[] Encode() => new RemoteFieldWriter()
        .WriteText(1, Profile, MaximumProfileBytes)
        .WriteUInt32(2, Seconds)
        .WriteUInt32(3, JournalMebibytes)
        .ToArray();

    internal static RemoteCaptureRequest Decode(byte[] payload)
    {
        RemoteFields fields = RemoteFields.Read(payload);
        return new(fields.ReadText(1, MaximumProfileBytes), fields.ReadUInt32(2), fields.ReadUInt32(3));
    }
}

/// <summary>
/// A capture an agent prepared and holds for a short while to be started (`remote-agent-v1` §4.4): the profile its host
/// will record, its bounds, and what it collects, in its broker's words - the preview a controller shows before it starts.
/// </summary>
public sealed record RemotePreparedCapture(Guid Prepared, string Profile, uint Seconds, uint JournalMebibytes, string Collects)
{
    internal const int MaximumCollectsBytes = 4 * 1024;

    internal byte[] Encode() => new RemoteFieldWriter()
        .WriteGuid(1, Prepared)
        .WriteText(2, Profile, RemoteCaptureRequest.MaximumProfileBytes)
        .WriteUInt32(3, Seconds)
        .WriteUInt32(4, JournalMebibytes)
        .WriteText(5, Collects, MaximumCollectsBytes)
        .ToArray();

    internal static RemotePreparedCapture Decode(byte[] payload)
    {
        RemoteFields fields = RemoteFields.Read(payload);
        return new(fields.ReadGuid(1), fields.ReadText(2, RemoteCaptureRequest.MaximumProfileBytes), fields.ReadUInt32(3),
            fields.ReadUInt32(4), fields.ReadText(5, MaximumCollectsBytes));
    }
}

/// <summary>A prepared capture's start, with the control lease the controller will keep renewing.</summary>
public sealed record RemoteStartRequest(Guid Prepared, ushort LeaseSeconds)
{
    internal byte[] Encode() => new RemoteFieldWriter().WriteGuid(1, Prepared).WriteUInt16(2, LeaseSeconds).ToArray();

    internal static RemoteStartRequest Decode(byte[] payload)
    {
        RemoteFields fields = RemoteFields.Read(payload);
        return new(fields.ReadGuid(1), fields.ReadUInt16(2));
    }
}

/// <summary>
/// A capture started (`remote-agent-v1` §4.4): its identity, and when the agent acknowledged the start by its own clock -
/// a control barrier, never proof that two hosts' clocks agree - and when the control lease lapses unless renewed.
/// </summary>
public sealed record RemoteCaptureStarted(Guid CaptureId, DateTimeOffset AcknowledgedUtc, DateTimeOffset LeaseExpiresUtc)
{
    internal byte[] Encode() => new RemoteFieldWriter()
        .WriteGuid(1, CaptureId)
        .WriteInt64(2, AcknowledgedUtc.ToUnixTimeMilliseconds())
        .WriteInt64(3, LeaseExpiresUtc.ToUnixTimeMilliseconds())
        .ToArray();

    internal static RemoteCaptureStarted Decode(byte[] payload)
    {
        RemoteFields fields = RemoteFields.Read(payload);
        return new(fields.ReadGuid(1), DateTimeOffset.FromUnixTimeMilliseconds(fields.ReadInt64(2)),
            DateTimeOffset.FromUnixTimeMilliseconds(fields.ReadInt64(3)));
    }
}

/// <summary>A started capture named in a status, renewal or stop.</summary>
public sealed record RemoteCaptureReference(Guid CaptureId)
{
    internal byte[] Encode() => new RemoteFieldWriter().WriteGuid(1, CaptureId).ToArray();

    internal static RemoteCaptureReference Decode(byte[] payload) => new(RemoteFields.Read(payload).ReadGuid(1));
}

/// <summary>
/// Where a remote capture stands (`remote-agent-v1` §4.4): its state, the records its host's broker admitted and what it
/// knows was lost, when its control lease lapses, whether a closed capture finished whole, why it closed where the agent
/// or its broker closed it, and - since version 1.3 - when the agent answered, by its own clock; null from an agent before
/// it.
/// </summary>
public sealed record RemoteCaptureState(
    Guid CaptureId,
    RemoteCaptureLifecycle State,
    long Records,
    long Lost,
    DateTimeOffset LeaseExpiresUtc,
    bool Finalized,
    string Reason,
    DateTimeOffset? AnsweredUtc = null)
{
    internal const int MaximumReasonBytes = 512;

    internal byte[] Encode()
    {
        RemoteFieldWriter fields = new RemoteFieldWriter()
            .WriteGuid(1, CaptureId)
            .WriteUInt16(2, (ushort)State)
            .WriteInt64(3, Records)
            .WriteInt64(4, Lost)
            .WriteInt64(5, LeaseExpiresUtc.ToUnixTimeMilliseconds())
            .WriteBoolean(6, Finalized)
            .WriteText(7, Reason, MaximumReasonBytes);
        return (AnsweredUtc is { } answered ? fields.WriteInt64(8, answered.ToUnixTimeMilliseconds()) : fields).ToArray();
    }

    internal static RemoteCaptureState Decode(byte[] payload)
    {
        RemoteFields fields = RemoteFields.Read(payload);
        ushort state = fields.ReadUInt16(2);
        return new(fields.ReadGuid(1),
            Enum.IsDefined((RemoteCaptureLifecycle)state) ? (RemoteCaptureLifecycle)state : throw new InvalidDataException($"Capture state {state} is not one version 1 defines."),
            fields.ReadInt64(3), fields.ReadInt64(4), DateTimeOffset.FromUnixTimeMilliseconds(fields.ReadInt64(5)),
            fields.ReadBoolean(6), fields.ReadText(7, MaximumReasonBytes),
            fields.Has(8) ? DateTimeOffset.FromUnixTimeMilliseconds(fields.ReadInt64(8)) : null);
    }
}
