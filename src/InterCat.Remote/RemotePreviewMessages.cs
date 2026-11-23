using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace InterCat.Remote;

/// <summary>
/// A capture request previewed on an agent's host (`remote-agent-v1` §4.10): a capture as <see cref="RemoteCaptureRequest"/>
/// asks for one - its profile, duration and journal - and, for a focused or content profile, the focus or content scope
/// `icat profiles` takes, each in the command line's words. Nothing in it names a path, a command or an address, and
/// previewing it starts nothing.
/// </summary>
public sealed record RemotePreviewRequest(string Profile, uint Seconds, uint JournalMebibytes)
{
    /// <summary>The most processes a preview names.</summary>
    public const int MostProcesses = 64;

    /// <summary>The most channel selectors a preview names.</summary>
    public const int MostChannels = 64;

    /// <summary>The longest channel selector, in bytes.</summary>
    public const int MostChannelBytes = 1_024;

    private const int MostNameBytes = 32;
    private const int MostSourceBytes = 256;

    /// <summary>The mechanism of a focus or a content scope, in the command line's word; null where none is asked.</summary>
    public string? Mechanism { get; init; }

    /// <summary>The processes of the agent's host it is focused on, or keeps the content of.</summary>
    public IReadOnlyList<int> ProcessIds { get; init; } = [];

    /// <summary>Whether a focus consents to collecting beyond its processes where a source must.</summary>
    public bool AllowBroaderCapture { get; init; }

    /// <summary>The source whose content it keeps; null where it keeps none.</summary>
    public string? ContentSource { get; init; }

    public IReadOnlyList<string> Channels { get; init; } = [];

    public uint? MaximumRecordBytes { get; init; }

    public long? MaximumSessionBytes { get; init; }

    /// <summary>What happens when its content reaches its limit, in the command line's word.</summary>
    public string? Retention { get; init; }

    /// <summary>How its content may be inspected, in the command line's word.</summary>
    public string? Inspection { get; init; }

    internal byte[] Encode()
    {
        RemoteFieldWriter writer = new RemoteFieldWriter()
            .WriteText(1, Profile, RemoteCaptureRequest.MaximumProfileBytes)
            .WriteUInt32(2, Seconds)
            .WriteUInt32(3, JournalMebibytes);
        if (Mechanism is { } mechanism)
        {
            writer.WriteText(4, mechanism, MostNameBytes);
        }

        if (ProcessIds.Count > 0)
        {
            if (ProcessIds.Count > MostProcesses)
            {
                throw new InvalidOperationException($"A preview names at most {MostProcesses} processes.");
            }

            byte[] processes = new byte[ProcessIds.Count * 4];
            for (int at = 0; at < ProcessIds.Count; at++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(processes.AsSpan(at * 4, 4), ProcessIds[at]);
            }

            writer.WriteBytes(5, processes);
        }

        if (AllowBroaderCapture)
        {
            writer.WriteBoolean(6, true);
        }

        if (ContentSource is { } source)
        {
            writer.WriteText(7, source, MostSourceBytes);
        }

        if (Channels.Count > 0)
        {
            writer.WriteBytes(8, Packed(Channels));
        }

        if (MaximumRecordBytes is { } recordBytes)
        {
            writer.WriteUInt32(9, recordBytes);
        }

        if (MaximumSessionBytes is { } sessionBytes)
        {
            writer.WriteInt64(10, sessionBytes);
        }

        if (Retention is { } retention)
        {
            writer.WriteText(11, retention, MostNameBytes);
        }

        if (Inspection is { } inspection)
        {
            writer.WriteText(12, inspection, MostNameBytes);
        }

        return writer.ToArray();
    }

    internal static RemotePreviewRequest Decode(byte[] payload)
    {
        RemoteFields fields = RemoteFields.Read(payload);
        int[] processes = [];
        if (fields.Has(5))
        {
            byte[] bytes = fields.ReadBytesUpTo(5, MostProcesses * 4);
            if (bytes.Length == 0 || bytes.Length % 4 != 0)
            {
                throw new InvalidDataException("A preview's processes are each 4 bytes.");
            }

            processes = [.. Enumerable.Range(0, bytes.Length / 4).Select(at => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at * 4, 4)))];
        }

        return new(fields.ReadText(1, RemoteCaptureRequest.MaximumProfileBytes), fields.ReadUInt32(2), fields.ReadUInt32(3))
        {
            Mechanism = fields.Has(4) ? fields.ReadText(4, MostNameBytes) : null,
            ProcessIds = processes,
            AllowBroaderCapture = fields.Has(6) && fields.ReadBoolean(6),
            ContentSource = fields.Has(7) ? fields.ReadText(7, MostSourceBytes) : null,
            Channels = fields.Has(8) ? Unpacked(fields.ReadBytesUpTo(8, MostChannels * (2 + MostChannelBytes))) : [],
            MaximumRecordBytes = fields.Has(9) ? fields.ReadUInt32(9) : null,
            MaximumSessionBytes = fields.Has(10) ? fields.ReadInt64(10) : null,
            Retention = fields.Has(11) ? fields.ReadText(11, MostNameBytes) : null,
            Inspection = fields.Has(12) ? fields.ReadText(12, MostNameBytes) : null,
        };
    }

    /// <summary>The channel selectors, each its length (uint16) and its UTF-8 bytes.</summary>
    private static byte[] Packed(IReadOnlyList<string> channels)
    {
        if (channels.Count > MostChannels)
        {
            throw new InvalidOperationException($"A preview names at most {MostChannels} channel selectors.");
        }

        var packed = new List<byte>();
        foreach (string channel in channels)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(channel);
            if (bytes.Length == 0 || bytes.Length > MostChannelBytes)
            {
                throw new InvalidOperationException($"A channel selector is 1 to {MostChannelBytes:N0} bytes.");
            }

            byte[] length = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)bytes.Length);
            packed.AddRange(length);
            packed.AddRange(bytes);
        }

        return [.. packed];
    }

    private static string[] Unpacked(byte[] packed)
    {
        var channels = new List<string>();
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        int at = 0;
        while (at < packed.Length)
        {
            if (packed.Length - at < 2 || channels.Count == MostChannels)
            {
                throw new InvalidDataException($"A preview's channel selectors are at most {MostChannels}, each its length and its bytes.");
            }

            int length = BinaryPrimitives.ReadUInt16LittleEndian(packed.AsSpan(at, 2));
            at += 2;
            if (length == 0 || length > MostChannelBytes || packed.Length - at < length)
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                    $"A preview's channel selector is 1 to {MostChannelBytes:N0} bytes, within the field."));
            }

            try
            {
                channels.Add(strict.GetString(packed, at, length));
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("A preview's channel selector is not UTF-8.", exception);
            }

            at += length;
        }

        return [.. channels];
    }
}

/// <summary>
/// What an agent says of a capture request previewed on its host (`remote-agent-v1` §4.10): whether it would prepare the
/// request as asked, and why not, in its words - and its host's preview, `capture-profile-preview-v1`'s JSON, compiled
/// from the host's own provider schemas; null where the request was refused before anything was compiled.
/// </summary>
public sealed record RemoteCapturePreview(bool Served, string? Reason, string? Preview)
{
    /// <summary>The longest reason, in bytes.</summary>
    internal const int MostReasonBytes = 512;

    /// <summary>The largest preview, in bytes: one field's.</summary>
    public const int MostPreviewBytes = 60 * 1024;

    internal byte[] Encode()
    {
        RemoteFieldWriter writer = new RemoteFieldWriter().WriteBoolean(1, Served);
        if (Reason is { } reason)
        {
            writer.WriteText(2, Fitted(reason), MostReasonBytes);
        }

        if (Preview is { } preview)
        {
            writer.WriteText(3, preview, MostPreviewBytes);
        }

        return writer.ToArray();
    }

    internal static RemoteCapturePreview Decode(byte[] payload)
    {
        RemoteFields fields = RemoteFields.Read(payload);
        return new(fields.ReadBoolean(1), fields.Has(2) ? fields.ReadText(2, MostReasonBytes) : null,
            fields.Has(3) ? fields.ReadText(3, MostPreviewBytes) : null);
    }

    /// <summary><paramref name="reason"/> whole where it fits, and otherwise cut at a character within the bytes a reason holds.</summary>
    private static string Fitted(string reason)
    {
        if (Encoding.UTF8.GetByteCount(reason) <= MostReasonBytes)
        {
            return reason;
        }

        int keep = reason.Length;
        while (keep > 0 && Encoding.UTF8.GetByteCount(reason.AsSpan(0, keep)) > MostReasonBytes - 3)
        {
            keep--;
        }

        if (keep > 0 && char.IsHighSurrogate(reason[keep - 1]))
        {
            keep--;
        }

        return reason[..keep] + "…";
    }
}

/// <summary>
/// Compiles a capture request into its host's preview (`capture-profile-preview-v1`), reading the host's own provider
/// schemas and starting nothing, and says whether the agent's host would record it as asked whatever the controller is
/// granted: an agent records only metadata profiles that take no focus, and keeps no content.
/// </summary>
public interface IRemoteCapturePreview
{
    RemotePreviewed Preview(RemotePreviewRequest request);
}

/// <summary>
/// A request's preview on the agent's host: the catalog's profile it names, null where it names none; its
/// `capture-profile-preview-v1` JSON, null where the request was refused before anything was compiled; whether it asks to
/// keep content; and why the host would not record it as asked, null where it would.
/// </summary>
public sealed record RemotePreviewed(string? Profile, string? Preview, bool KeepsContent, string? Refusal);
