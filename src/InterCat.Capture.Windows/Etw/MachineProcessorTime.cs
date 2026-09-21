using System.Runtime.InteropServices;

namespace InterCat.Capture.Windows;

/// <summary>
/// One GetSystemTimes reading. Kernel time includes idle time; all values are cumulative 100 ns ticks
/// across the machine's logical processors.
/// </summary>
public readonly record struct MachineProcessorTimeReading(long IdleTicks, long KernelTicks, long UserTicks);

/// <summary>A validated interval between two total-machine processor-time readings.</summary>
public readonly record struct MachineProcessorTimeInterval(long IdleTicks, long KernelTicks, long UserTicks)
{
    public long TotalTicks => KernelTicks + UserTicks;

    public long BusyTicks => TotalTicks - IdleTicks;

    public double BusyPercentage => TotalTicks == 0 ? 0 : 100d * BusyTicks / TotalTicks;
}

/// <summary>
/// Reads Windows' total-machine processor clock. This is deliberately not process CPU: section 12's
/// capture-impact budget is a percentage of the whole machine and must include the provider and kernel.
/// </summary>
public static partial class MachineProcessorTime
{
    public static bool IsAvailable => OperatingSystem.IsWindows();

    public static bool TryRead(out MachineProcessorTimeReading reading, out string? unavailableReason)
    {
        reading = default;
        if (!OperatingSystem.IsWindows())
        {
            unavailableReason = "GetSystemTimes is available only on Windows.";
            return false;
        }

        if (!GetSystemTimes(out long idle, out long kernel, out long user))
        {
            unavailableReason = $"GetSystemTimes failed with Win32 error {Marshal.GetLastPInvokeError()}.";
            return false;
        }

        reading = new(idle, kernel, user);
        unavailableReason = null;
        return true;
    }

    /// <summary>
    /// Builds an interval only when every cumulative clock advanced monotonically and idle remained a
    /// subset of kernel time. A reset or malformed reading is unavailable, never a zero-cost interval.
    /// </summary>
    public static bool TryMeasure(
        MachineProcessorTimeReading earlier,
        MachineProcessorTimeReading later,
        out MachineProcessorTimeInterval interval)
    {
        interval = default;
        if (later.IdleTicks < earlier.IdleTicks
            || later.KernelTicks < earlier.KernelTicks
            || later.UserTicks < earlier.UserTicks)
        {
            return false;
        }

        long idle = later.IdleTicks - earlier.IdleTicks;
        long kernel = later.KernelTicks - earlier.KernelTicks;
        long user = later.UserTicks - earlier.UserTicks;
        if (idle > kernel || kernel > long.MaxValue - user)
        {
            return false;
        }

        interval = new(idle, kernel, user);
        return interval.TotalTicks > 0;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
