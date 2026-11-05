using System.Globalization;
using System.Runtime.InteropServices;

namespace InterCat.Capture.Windows;

/// <summary>
/// Adds kernel group masks to a private system logger (ADR-082's third addendum). A kernel group with no EVENT_TRACE_FLAG of
/// its own, as the object manager's handles, is enabled by a group mask, which Windows takes as
/// <c>TraceSystemTraceEnableFlagsInfo</c> by the session's handle. TraceEvent sets group masks only on the NT Kernel Logger,
/// which a capture never uses (ADR-035), and gives no session's handle, so the handle is the one a query of the session by its
/// name returns.
/// </summary>
internal static partial class KernelGroupMaskControl
{
    // EVENT_TRACE_PROPERTIES on 64-bit Windows, as EtwSessionControl reads it: on a query, Wnode.HistoricalContext, at
    // offset 8, returns the session's handle. Room for both names follows the structure.
    private const int PropertiesBytes = 120;
    private const int NameBytes = 1_024 * sizeof(char);
    private const int HistoricalContextAt = 8;
    private const int LogFileNameOffsetAt = 112;
    private const int LoggerNameOffsetAt = 116;
    private const uint QueryControl = 0;

    // TRACE_INFO_CLASS's TraceSystemTraceEnableFlagsInfo, whose information is PERFINFO_GROUPMASK: eight 32-bit masks, the
    // first of them the session's kernel flags.
    private const int TraceSystemTraceEnableFlagsInfo = 4;
    private const int MaskWords = 8;

    /// <summary>
    /// Adds the group masks to the named session's, keeping those it holds - its kernel flags among them. Null when they are
    /// set; otherwise why they are not, and nothing was changed past the failing step.
    /// </summary>
    public static unsafe string? Add(string sessionName, IReadOnlyList<uint> groupMasks)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionName);
        ArgumentNullException.ThrowIfNull(groupMasks);
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            return "Kernel group masks are set only by a 64-bit process on Windows.";
        }

        const int Bytes = PropertiesBytes + (2 * NameBytes);
        byte* properties = stackalloc byte[Bytes];
        new Span<byte>(properties, Bytes).Clear();
        *(uint*)properties = Bytes;
        *(uint*)(properties + LoggerNameOffsetAt) = PropertiesBytes;
        *(uint*)(properties + LogFileNameOffsetAt) = PropertiesBytes + NameBytes;
        uint status = ControlTrace(0, sessionName, properties, QueryControl);
        ulong handle = *(ulong*)(properties + HistoricalContextAt);
        if (status != 0 || handle == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"The session could not be queried for its handle: error {status}.");
        }

        uint* masks = stackalloc uint[MaskWords];
        var words = new Span<uint>(masks, MaskWords);
        words.Clear();
        status = TraceQueryInformation(handle, TraceSystemTraceEnableFlagsInfo, masks, MaskWords * sizeof(uint), out _);
        if (status != 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"The session's group masks could not be read: error {status}.");
        }

        Merge(words, groupMasks);
        status = TraceSetInformation(handle, TraceSystemTraceEnableFlagsInfo, masks, MaskWords * sizeof(uint));
        return status == 0
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"The session's group masks could not be set: error {status}.");
    }

    /// <summary>
    /// Sets each group mask's bits in the mask it names: a PERF_* value's top three bits are the index of one of the eight
    /// masks (PERF_GET_MASK_INDEX), and its other bits are the bits it sets there (PERF_MASK).
    /// </summary>
    public static void Merge(Span<uint> masks, IReadOnlyList<uint> groupMasks)
    {
        ArgumentNullException.ThrowIfNull(groupMasks);
        if (masks.Length != MaskWords)
        {
            throw new ArgumentException("A system logger has eight group masks.", nameof(masks));
        }

        foreach (uint group in groupMasks)
        {
            masks[(int)(group >> 29)] |= group & 0x1FFF_FFFF;
        }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint ControlTrace(ulong traceHandle, string instanceName, byte* properties, uint controlCode);

    [LibraryImport("advapi32.dll")]
    private static unsafe partial uint TraceQueryInformation(ulong sessionHandle, int informationClass, uint* information, uint length, out uint returnLength);

    [LibraryImport("advapi32.dll")]
    private static unsafe partial uint TraceSetInformation(ulong sessionHandle, int informationClass, uint* information, uint length);
}
