using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace InterCat.Capture.Windows;

/// <summary>
/// Reads, from TDH, the layout this machine registers for a classic kernel event class, such as ALPC's (ADR-035). A
/// classic event has no manifest: its schema is the class's registration, which TDH resolves from a record's header
/// alone - the class GUID, the opcode and the version. So the layout is read before capture starts, from a synthetic
/// header with a zeroed body, never guessed and never decoded from a delivered record in the callback (§18.3, R9).
/// </summary>
public static partial class TdhClassicSchemaReader
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorInsufficientBuffer = 122;

    // EVENT_RECORD: an 80-byte EVENT_HEADER, the buffer context, then the body's length and pointer.
    private const int EventRecordLength = 112;
    private const int HeaderFlagsOffset = 4;
    private const int ProviderIdOffset = 24;
    private const int DescriptorVersionOffset = 42;
    private const int DescriptorOpcodeOffset = 45;
    private const int UserDataLengthOffset = 86;
    private const int UserDataOffset = 96;
    private const ushort ClassicHeader = 0x0100;
    private const ushort Header32Bit = 0x0020;
    private const ushort Header64Bit = 0x0040;
    private const int SyntheticBodyLength = 256;

    // TRACE_EVENT_INFO, as TdhFieldShapeReader reads it for a manifest event.
    private const int DecodingSourceOffset = 48;
    private const int TaskNameOffsetOffset = 68;
    private const int OpcodeNameOffsetOffset = 72;
    private const int PropertyCountOffset = 100;
    private const int TopLevelPropertyCountOffset = 104;
    private const int PropertyArrayOffset = 112;
    private const int PropertyInfoLength = 24;
    private const int PropertyStruct = 0x1;
    private const int PropertyParamLength = 0x2;
    private const int PropertyParamCount = 0x4;
    private const int PropertyParamFixedLength = 0x8;
    private const int PropertyParamFixedCount = 0x20;

    /// <summary>TDH's decoding source for a class registered in WMI, which classic kernel events are.</summary>
    public const int DecodingSourceWbem = 1;

    /// <summary>
    /// The class's schema for the opcodes asked for, one event each at <paramref name="version"/>; null when TDH
    /// describes none of them. An opcode TDH cannot describe is left out, and a plan that needs it reports it missing.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static ProviderSchema? Read(Guid classGuid, string className, int version, IReadOnlyList<int> opcodes, int pointerSize = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        ArgumentNullException.ThrowIfNull(opcodes);
        if (pointerSize is not (4 or 8)) throw new ArgumentOutOfRangeException(nameof(pointerSize));

        var events = new List<ProviderSchemaEvent>(opcodes.Count);
        foreach (int opcode in opcodes.Distinct().Order())
        {
            if (opcode is < 0 or > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(opcodes));
            if (ReadEvent(classGuid, version, opcode, pointerSize) is { } described) events.Add(described);
        }

        return events.Count == 0
            ? null
            : new ProviderSchema(className, classGuid, Fingerprint(classGuid, version, pointerSize, events),
                new Dictionary<string, ulong>(), events);
    }

    /// <summary>
    /// One classic event from TDH's TRACE_EVENT_INFO for it: its top-level fields in order, each with the width its type
    /// or a fixed length gives it. A field whose length or count another field gives, or a structure, is variable, and
    /// every field after it is unreachable at a fixed offset. Null when the buffer is not a class's decoding.
    /// </summary>
    public static ProviderSchemaEvent? Parse(ReadOnlySpan<byte> traceEventInfo, int opcode, int version)
    {
        if (traceEventInfo.Length < PropertyArrayOffset) return null;
        int source = Int32(traceEventInfo, DecodingSourceOffset);
        int properties = Int32(traceEventInfo, PropertyCountOffset);
        int topLevel = Int32(traceEventInfo, TopLevelPropertyCountOffset);
        if (source != DecodingSourceWbem || topLevel < 0 || topLevel > properties
            || PropertyArrayOffset + ((long)properties * PropertyInfoLength) > traceEventInfo.Length)
        {
            return null;
        }

        var fields = new List<ProviderSchemaField>(topLevel);
        for (int index = 0; index < topLevel; index++)
        {
            int info = PropertyArrayOffset + (index * PropertyInfoLength);
            int flags = Int32(traceEventInfo, info);
            string? name = Text(traceEventInfo, Int32(traceEventInfo, info + 4));
            ushort inType = UInt16(traceEventInfo, info + 8);
            ushort count = UInt16(traceEventInfo, info + 16);
            ushort length = UInt16(traceEventInfo, info + 18);
            if (string.IsNullOrEmpty(name)) return null;
            bool variable = (flags & (PropertyStruct | PropertyParamLength | PropertyParamCount)) != 0
                || ((flags & PropertyParamFixedCount) != 0 && count != 1);
            string type = variable ? "classic:variable" : InTypeName(inType);
            string? fixedLength = !variable && inType == 14 && ((flags & PropertyParamFixedLength) != 0 || length > 0)
                ? length.ToString(CultureInfo.InvariantCulture)
                : null;
            fields.Add(ProviderSchemaField.Create(name, type, fixedLength));
        }

        return new ProviderSchemaEvent(0, version, null, Text(traceEventInfo, Int32(traceEventInfo, TaskNameOffsetOffset)),
            null, Text(traceEventInfo, Int32(traceEventInfo, OpcodeNameOffsetOffset)), opcode, null, [], 0, null, fields);
    }

    /// <summary>
    /// The class's layout fingerprint: its GUID, version and pointer width, and each described opcode's fields in order.
    /// Two opcodes with one layout, as ALPC's send and receive have, share every part a plan's fingerprint reads.
    /// </summary>
    public static string Fingerprint(Guid classGuid, int version, int pointerSize, IReadOnlyList<ProviderSchemaEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var canonical = new StringBuilder(256);
        canonical.Append("intercat-classic-schema-v1\n")
            .Append(classGuid.ToString("N", CultureInfo.InvariantCulture)).Append('\n')
            .Append(version.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(pointerSize.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (ProviderSchemaEvent described in events.OrderBy(item => item.OpcodeValue))
        {
            canonical.Append("opcode:").Append(described.OpcodeValue?.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (ProviderSchemaField field in described.Fields)
            {
                canonical.Append(field.Name).Append('|').Append(field.InType).Append('|')
                    .Append(((int)field.WidthKind).ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(field.FixedWidth.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    [SupportedOSPlatform("windows")]
    private static ProviderSchemaEvent? ReadEvent(Guid classGuid, int version, int opcode, int pointerSize)
    {
        nint record = Marshal.AllocHGlobal(EventRecordLength);
        nint body = Marshal.AllocHGlobal(SyntheticBodyLength);
        try
        {
            Zero(record, EventRecordLength);
            Zero(body, SyntheticBodyLength);
            Marshal.WriteInt16(record, 0, (short)(80 + SyntheticBodyLength));
            Marshal.WriteInt16(record, HeaderFlagsOffset, unchecked((short)(ClassicHeader | (pointerSize == 8 ? Header64Bit : Header32Bit))));
            Marshal.Copy(classGuid.ToByteArray(), 0, record + ProviderIdOffset, 16);
            Marshal.WriteByte(record, DescriptorVersionOffset, (byte)version);
            Marshal.WriteByte(record, DescriptorOpcodeOffset, (byte)opcode);
            Marshal.WriteInt16(record, UserDataLengthOffset, SyntheticBodyLength);
            Marshal.WriteIntPtr(record, UserDataOffset, body);

            uint size = 0;
            if (TdhGetEventInformation(record, 0, 0, 0, ref size) != ErrorInsufficientBuffer || size < PropertyArrayOffset)
            {
                return null;
            }

            byte[] info = new byte[size];
            nint buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (TdhGetEventInformation(record, 0, 0, buffer, ref size) != ErrorSuccess) return null;
                Marshal.Copy(buffer, info, 0, (int)size);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return Parse(info, opcode, version);
        }
        finally
        {
            Marshal.FreeHGlobal(body);
            Marshal.FreeHGlobal(record);
        }
    }

    private static string InTypeName(ushort inType) => inType switch
    {
        1 => "win:UnicodeString",
        2 => "win:AnsiString",
        3 => "win:Int8",
        4 => "win:UInt8",
        5 => "win:Int16",
        6 => "win:UInt16",
        7 => "win:Int32",
        8 => "win:UInt32",
        9 => "win:Int64",
        10 => "win:UInt64",
        11 => "win:Float",
        12 => "win:Double",
        13 => "win:Boolean",
        14 => "win:Binary",
        15 => "win:GUID",
        16 => "win:Pointer",
        17 => "win:FILETIME",
        18 => "win:SYSTEMTIME",
        19 => "win:SID",
        20 => "win:HexInt32",
        21 => "win:HexInt64",
        _ => string.Create(CultureInfo.InvariantCulture, $"classic:intype-{inType}"),
    };

    private static int Int32(ReadOnlySpan<byte> bytes, int offset) =>
        offset >= 0 && offset + 4 <= bytes.Length ? BitConverter.ToInt32(bytes.Slice(offset, 4)) : -1;

    private static ushort UInt16(ReadOnlySpan<byte> bytes, int offset) =>
        offset >= 0 && offset + 2 <= bytes.Length ? BitConverter.ToUInt16(bytes.Slice(offset, 2)) : (ushort)0;

    /// <summary>A NUL-terminated UTF-16 string at an offset TDH gives, or null when the offset is none or out of range.</summary>
    private static string? Text(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset <= 0 || offset >= bytes.Length) return null;
        ReadOnlySpan<byte> rest = bytes[offset..];
        int end = 0;
        while (end + 1 < rest.Length && (rest[end] != 0 || rest[end + 1] != 0)) end += 2;
        return end == 0 ? null : Encoding.Unicode.GetString(rest[..end]);
    }

    private static void Zero(nint memory, int length)
    {
        for (int offset = 0; offset < length; offset++) Marshal.WriteByte(memory, offset, 0);
    }

    [LibraryImport("tdh.dll")]
    private static partial uint TdhGetEventInformation(nint eventRecord, uint contextCount, nint context, nint buffer, ref uint bufferSize);
}
