using System.Runtime.InteropServices;

namespace InterCat.Capture.Windows;

/// <summary>Kernel and user processor time of one thread, kept apart because they are different costs.</summary>
public readonly record struct ThreadCpuReading(long KernelTicks, long UserTicks)
{
    /// <summary>The two quantities added for a single "processor time" figure; both stay available.</summary>
    public long TotalTicks => KernelTicks + UserTicks;

    public TimeSpan Kernel => TimeSpan.FromTicks(KernelTicks);

    public TimeSpan User => TimeSpan.FromTicks(UserTicks);

    public TimeSpan Total => TimeSpan.FromTicks(TotalTicks);

    public static ThreadCpuReading operator -(ThreadCpuReading later, ThreadCpuReading earlier) =>
        new(later.KernelTicks - earlier.KernelTicks, later.UserTicks - earlier.UserTicks);

    public ThreadCpuReading Subtract(ThreadCpuReading earlier) => this - earlier;
}

/// <summary>
/// Per-thread processor time, so a callback's cost can be attributed to the callback rather than to the
/// whole process. .NET has no portable per-thread CPU clock, so this reads the Windows one directly and
/// reports unavailability rather than substituting a process-wide figure (section 12, R21).
/// </summary>
public static partial class ThreadCpuTime
{
    private const int TicksPerFileTimeUnit = 1;

    /// <summary>True when per-thread processor time can be read on this host.</summary>
    public static bool IsAvailable => OperatingSystem.IsWindows();

    /// <summary>
    /// Reads the calling thread's processor time. Returns false on a host without the counter; the caller
    /// then reports the stage's CPU as unmeasured instead of reporting zero.
    /// </summary>
    public static bool TryReadCurrentThread(out ThreadCpuReading reading)
    {
        reading = default;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (!GetThreadTimes(
            GetCurrentThread(),
            out long _,
            out long _,
            out long kernelTime,
            out long userTime))
        {
            return false;
        }

        // GetThreadTimes reports 100-nanosecond units, which is also the unit of TimeSpan ticks.
        reading = new(kernelTime * TicksPerFileTimeUnit, userTime * TicksPerFileTimeUnit);
        return true;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(
        nint thread,
        out long creationTime,
        out long exitTime,
        out long kernelTime,
        out long userTime);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();
}
