using System.Runtime.InteropServices;

namespace InterCat.Capture.Windows;

/// <summary>
/// Stops an ETW session by name and reads its loss counters from the stop's answer: ETW reports a session's final counters
/// only there, TraceEvent's own stop discards them, and a stopped session answers no later query.
/// </summary>
internal static partial class EtwSessionControl
{
    // EVENT_TRACE_PROPERTIES on 64-bit Windows: a 48-byte WNODE_HEADER, then its counters, the logger thread's handle and
    // the two name offsets, 120 bytes in all. Room for both names follows it.
    private const int PropertiesBytes = 120;
    private const int NameBytes = 1_024 * sizeof(char);
    private const int EventsLostAt = 88;
    private const int RealTimeBuffersLostAt = 100;
    private const int LogFileNameOffsetAt = 112;
    private const int LoggerNameOffsetAt = 116;
    private const uint StopControl = 1;

    /// <summary>
    /// Stops the session and returns the events it lost and the real-time buffers its consumer lost, as the stop reported
    /// them; null when the stop failed or this is not a 64-bit Windows process, and nothing was stopped.
    /// </summary>
    public static unsafe (long EventsLost, long RealTimeBuffersLost)? Stop(string sessionName)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionName);
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            return null;
        }

        const int Bytes = PropertiesBytes + (2 * NameBytes);
        byte* properties = stackalloc byte[Bytes];
        new Span<byte>(properties, Bytes).Clear();
        *(uint*)properties = Bytes;
        *(uint*)(properties + LoggerNameOffsetAt) = PropertiesBytes;
        *(uint*)(properties + LogFileNameOffsetAt) = PropertiesBytes + NameBytes;
        return ControlTrace(0, sessionName, properties, StopControl) == 0
            ? (*(uint*)(properties + EventsLostAt), *(uint*)(properties + RealTimeBuffersLostAt))
            : null;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint ControlTrace(ulong traceHandle, string instanceName, byte* properties, uint controlCode);
}
