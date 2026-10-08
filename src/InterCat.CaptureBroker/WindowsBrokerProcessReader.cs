using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using InterCat.Capture.Windows;
using Microsoft.Win32.SafeHandles;

namespace InterCat.CaptureBroker;

/// <summary>
/// Reads a running process for ADR-049 through the handle that holds it (<see cref="ProcessHolds"/>, which opens it with
/// limited query rights and refuses one that is not running or has exited): its token's user, logon session and
/// integrity, and when it started. A process that cannot be held or read is said to be, never guessed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsBrokerProcessReader : IBrokerProcessReader
{
    public BrokerProcessReading? Read(int processId, out string? problem)
    {
        using var holds = new ProcessHolds();
        if (!holds.TryHold([processId], out string? refusal))
        {
            problem = refusal;
            return null;
        }

        (BrokerProcessReading? reading, string? failure) = holds.Read(processId, handle => ReadThrough(processId, handle));
        problem = failure;
        return reading;
    }

    public IBrokerHeldProcesses? Hold(IReadOnlyList<int> processIds, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        var holds = new ProcessHolds();
        if (!holds.TryHold(processIds, out problem))
        {
            holds.Dispose();
            return null;
        }

        var readings = new Dictionary<int, BrokerProcessReading>(processIds.Count);
        foreach (int processId in processIds)
        {
            (BrokerProcessReading? reading, string? failure) = holds.Read(processId, handle => ReadThrough(processId, handle));
            if (reading is null)
            {
                holds.Dispose();
                problem = failure;
                return null;
            }

            readings[processId] = reading;
        }

        return new Held(holds, readings);
    }

    private static (BrokerProcessReading? Reading, string? Problem) ReadThrough(int processId, SafeProcessHandle process)
    {
        if (!GetProcessTimes(process, out long created, out _, out _, out _))
        {
            return (null, string.Create(CultureInfo.InvariantCulture,
                $"when process {processId} started could not be read (Windows error {Marshal.GetLastPInvokeError()})"));
        }

        try
        {
            return (new(WindowsBrokerTokenIdentity.ReadProcess(process), DateTimeOffset.FromFileTime(created).ToUniversalTime()), null);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidDataException or UnauthorizedAccessException)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"process {processId}'s token could not be read: ")
                + exception.Message.TrimEnd('.'));
        }
    }

    /// <summary>The holds a content capture keeps while it records, with what was read through them.</summary>
    private sealed class Held(ProcessHolds holds, IReadOnlyDictionary<int, BrokerProcessReading> readings) : IBrokerHeldProcesses
    {
        public IReadOnlyDictionary<int, BrokerProcessReading> Readings { get; } = readings;

        public void Dispose() => holds.Dispose();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(
        SafeProcessHandle process,
        out long creationTime,
        out long exitTime,
        out long kernelTime,
        out long userTime);
}
