using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;

namespace InterCat.SectionProbe;

/// <summary>
/// A private system logger's kernel group masks (<c>TraceSystemTraceEnableFlagsInfo</c>). The object manager's handle and
/// object groups have no <c>EVENT_TRACE_FLAG</c>; TraceEvent sets them only on the NT Kernel Logger, which this probe never
/// uses (ADR-035). Windows takes a system logger's masks by the session's handle, which a query of the session by its
/// name returns, so the probe adds the groups to its own session and to no other.
/// </summary>
internal static partial class GroupMask
{
    /// <summary><c>PERF_OB_HANDLE</c>, 0x80000040: a handle's creation, close and duplication. Group 4, bit 0x40.</summary>
    public const uint ObjectHandles = 0x8000_0040;

    /// <summary><c>PERF_OB_OBJECT</c>, 0x80000080: an object's creation and deletion. Group 4, bit 0x80.</summary>
    public const uint ObjectLifetimes = 0x8000_0080;

    private const int TraceSystemTraceEnableFlagsInfo = 4;
    private const int MaskBytes = 8 * sizeof(uint);

    /// <summary>Adds the groups to the named session's masks; null when they are set, otherwise why they are not.</summary>
    public static string? Add(string sessionName, params uint[] groups)
    {
        if (Handle(sessionName) is not { } handle)
        {
            return $"The session {sessionName} could not be queried for its handle.";
        }

        byte[] masks = new byte[MaskBytes];
        uint status = TraceQueryInformation(handle, TraceSystemTraceEnableFlagsInfo, masks, MaskBytes, out _);
        if (status != 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Its group masks could not be read: error {status}.");
        }

        foreach (uint group in groups)
        {
            // PERF_GET_MASK_INDEX: the top three bits name the mask, the rest are its bits.
            int index = (int)((group & 0xE000_0000) >> 29);
            Span<byte> mask = masks.AsSpan(index * sizeof(uint), sizeof(uint));
            BinaryPrimitives.WriteUInt32LittleEndian(mask, BinaryPrimitives.ReadUInt32LittleEndian(mask) | (group & 0x1FFF_FFFF));
        }

        status = TraceSetInformation(handle, TraceSystemTraceEnableFlagsInfo, masks, MaskBytes);
        return status == 0 ? null : string.Create(CultureInfo.InvariantCulture, $"Its group masks could not be set: error {status}.");
    }

    /// <summary>The masks the named session holds, as eight hexadecimal words; null when they cannot be read.</summary>
    public static string[]? Read(string sessionName)
    {
        byte[] masks = new byte[MaskBytes];
        return Handle(sessionName) is { } handle && TraceQueryInformation(handle, TraceSystemTraceEnableFlagsInfo, masks, MaskBytes, out _) == 0
            ? [.. Enumerable.Range(0, 8).Select(index =>
                "0x" + BinaryPrimitives.ReadUInt32LittleEndian(masks.AsSpan(index * sizeof(uint))).ToString("X8", CultureInfo.InvariantCulture))]
            : null;
    }

    // EVENT_TRACE_PROPERTIES on 64-bit Windows, queried by name: Wnode.HistoricalContext, at offset 8, returns the session's
    // handle; room for both names follows the structure, as the product's EtwSessionControl leaves it.
    private static ulong? Handle(string sessionName)
    {
        const int PropertiesBytes = 120;
        const int NameBytes = 1_024 * sizeof(char);
        const int Bytes = PropertiesBytes + (2 * NameBytes);
        byte[] properties = new byte[Bytes];
        BinaryPrimitives.WriteUInt32LittleEndian(properties, Bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(116), PropertiesBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(112), PropertiesBytes + NameBytes);
        if (ControlTrace(0, sessionName, properties, 0) != 0)
        {
            return null;
        }

        ulong handle = BinaryPrimitives.ReadUInt64LittleEndian(properties.AsSpan(8));
        return handle == 0 ? null : handle;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ControlTrace(ulong traceHandle, string instanceName, [In, Out] byte[] properties, uint controlCode);

    [LibraryImport("advapi32.dll")]
    private static partial uint TraceQueryInformation(ulong sessionHandle, int informationClass, [In, Out] byte[] information, uint length, out uint returnLength);

    [LibraryImport("advapi32.dll")]
    private static partial uint TraceSetInformation(ulong sessionHandle, int informationClass, [In] byte[] information, uint length);
}
