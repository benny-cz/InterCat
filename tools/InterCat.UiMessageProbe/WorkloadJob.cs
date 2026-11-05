using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InterCat.UiMessageProbe;

/// <summary>
/// A job object holding a truth workload and the processes it starts, so that a trial reads the processor time all of them
/// spent - the sender's, the receiver's and their coordinator's, the records Win32k raised in their threads among it - apart
/// from whatever else the machine ran meanwhile. A process started by one in the job is in it too.
/// </summary>
internal sealed partial class WorkloadJob : IDisposable
{
    private const int BasicAccountingInformation = 1;
    private readonly nint handle;

    public WorkloadJob()
    {
        handle = CreateJobObjectW(0, null);
        if (handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>Adds a process just started, before it starts any of its own.</summary>
    public void Add(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>The user and kernel time of every process the job has held, and how many it has held.</summary>
    public (TimeSpan ProcessorTime, int Processes) Read()
    {
        if (!QueryInformationJobObject(handle, BasicAccountingInformation, out Accounting accounting, Marshal.SizeOf<Accounting>(), 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return (TimeSpan.FromTicks(accounting.TotalUserTime + accounting.TotalKernelTime), (int)accounting.TotalProcesses);
    }

    public void Dispose() => _ = CloseHandle(handle);

    // JOBOBJECT_BASIC_ACCOUNTING_INFORMATION: times in 100-nanosecond units, a TimeSpan's ticks.
    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(nint job, int informationClass, out Accounting information, int length, nint returnLength);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
