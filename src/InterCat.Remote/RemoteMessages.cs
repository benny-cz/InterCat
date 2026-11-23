namespace InterCat.Remote;

/// <summary>
/// What an agent says to a <see cref="RemoteMessageType.Hello"/> (`remote-agent-v1` §4.1): the protocol version it speaks
/// and whether the controller's key is enrolled. Only an enrolled controller learns which host it reached and what it may
/// ask for there.
/// </summary>
public sealed record RemoteHello(ushort Major, ushort Minor, bool Enrolled, Guid? HostId, RemoteGrants Grants)
{
    internal byte[] Encode()
    {
        var writer = new RemoteFieldWriter().WriteUInt16(1, Major).WriteUInt16(2, Minor).WriteBoolean(3, Enrolled);
        if (Enrolled)
        {
            writer.WriteGuid(4, HostId ?? throw new InvalidOperationException("An enrolled controller is told the agent's host."))
                .WriteUInt16(5, (ushort)Grants);
        }

        return writer.ToArray();
    }

    internal static RemoteHello Decode(byte[] payload)
    {
        RemoteFields fields = RemoteFields.Read(payload);
        bool enrolled = fields.ReadBoolean(3);
        return new(fields.ReadUInt16(1), fields.ReadUInt16(2), enrolled,
            enrolled ? fields.ReadGuid(4) : null,
            enrolled ? GrantsFrom(fields.ReadUInt16(5)) : RemoteGrants.None);
    }

    internal static RemoteGrants GrantsFrom(ushort value) =>
        ((RemoteGrants)value & ~RemoteGrantsExtensions.Defined) == 0
            ? (RemoteGrants)value
            : throw new InvalidDataException($"Grants 0x{value:x4} name one version 1 does not define.");
}

/// <summary>What an agent says when it admits an enrollment (`remote-agent-v1` §4.2): the host and the grants now held.</summary>
public sealed record RemoteEnrolled(Guid HostId, RemoteGrants Grants)
{
    internal byte[] Encode() => new RemoteFieldWriter().WriteGuid(1, HostId).WriteUInt16(2, (ushort)Grants).ToArray();

    internal static RemoteEnrolled Decode(byte[] payload)
    {
        RemoteFields fields = RemoteFields.Read(payload);
        return new(fields.ReadGuid(1), RemoteHello.GrantsFrom(fields.ReadUInt16(2)));
    }
}

/// <summary>
/// An agent's host as it describes itself to an enrolled controller (`remote-agent-v1` §4.3): its identity, name, operating
/// system and InterCat's version, and what of a capture it serves - in version 1.0, none yet.
/// </summary>
public sealed record RemoteInventory(Guid HostId, string MachineName, string OperatingSystem, string InterCatVersion, bool ServesCapture)
{
    internal const int MaximumTextBytes = 256;

    internal byte[] Encode() => new RemoteFieldWriter()
        .WriteGuid(1, HostId)
        .WriteText(2, MachineName, MaximumTextBytes)
        .WriteText(3, OperatingSystem, MaximumTextBytes)
        .WriteText(4, InterCatVersion, MaximumTextBytes)
        .WriteBoolean(5, ServesCapture)
        .ToArray();

    internal static RemoteInventory Decode(byte[] payload)
    {
        RemoteFields fields = RemoteFields.Read(payload);
        return new(fields.ReadGuid(1), fields.ReadText(2, MaximumTextBytes), fields.ReadText(3, MaximumTextBytes),
            fields.ReadText(4, MaximumTextBytes), fields.ReadBoolean(5));
    }
}

/// <summary>An agent's refusal (`remote-agent-v1` §6): why, in a code, and in words a person reads.</summary>
public sealed record RemoteRefusal(RemoteErrorCode Code, string Message)
{
    internal const int MaximumMessageBytes = 512;

    internal byte[] Encode() => new RemoteFieldWriter().WriteUInt16(1, (ushort)Code).WriteText(2, Message, MaximumMessageBytes).ToArray();

    internal static RemoteRefusal Decode(byte[] payload)
    {
        RemoteFields fields = RemoteFields.Read(payload);
        return new((RemoteErrorCode)fields.ReadUInt16(1), fields.ReadText(2, MaximumMessageBytes));
    }
}

/// <summary>An agent refused a request, or could not be reached as the agent the controller expects.</summary>
public sealed class RemoteAgentException : Exception
{
    public RemoteAgentException(RemoteErrorCode? code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;

    public RemoteAgentException()
    {
    }

    public RemoteAgentException(string message)
        : base(message)
    {
    }

    public RemoteAgentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The agent's reason, or null where the agent itself was not the one expected or did not answer as one.</summary>
    public RemoteErrorCode? Code { get; }

    /// <summary>The pin of the key that answered where it was not the agent's expected; empty otherwise.</summary>
    public RemotePin Presented { get; init; }
}
