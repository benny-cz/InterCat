using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InterCat.Capture.Windows;

/// <summary>
/// One running service as the service control manager lists it: its name, the PID of the process hosting it, and when the
/// operating system created that process, which a lifecycle record of the same instance carries; null where the host could
/// not be opened to read it.
/// </summary>
public sealed record RunningService(string Name, int ProcessId, DateTimeOffset? HostCreatedUtc);

/// <summary>
/// The running services the service control manager lists, each with the process hosting it (ADR-095): its own documented
/// query of the Win32 services that are active (<c>EnumServicesStatusEx</c>), read with the right to enumerate services
/// alone, and each host's creation time as the operating system keeps it, read once a host. It starts, stops and configures no
/// service, and reads nothing of a host but when it was created. A service the manager lists without a process, as one
/// stopping can be, is left out.
/// </summary>
public static partial class RunningServices
{
    private const uint EnumerateService = 0x0004;
    private const int ProcessInformationLevel = 0;
    private const uint Win32Services = 0x30;
    private const uint Active = 0x1;
    private const int ErrorMoreData = 234;

    /// <summary>
    /// The services running now, each once, as the manager lists them; a manager that cannot be read refuses with the
    /// error it gave, as <see cref="Win32Exception"/>, rather than answering with no services (R3).
    /// </summary>
    public static IReadOnlyList<RunningService> Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The service control manager is read on Windows.");
        }

        nint manager = OpenSCManagerW(null, null, EnumerateService);
        if (manager == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The service control manager could not be opened to list its services.");
        }

        var listed = new List<(string Name, int ProcessId)>();
        try
        {
            uint resume = 0;
            byte[] buffer = new byte[64 * 1024];
            while (true)
            {
                if (List(manager, buffer, ref resume, listed, out uint needed))
                {
                    break;
                }

                if (needed > buffer.Length)
                {
                    buffer = new byte[needed];
                }
            }
        }
        finally
        {
            _ = CloseServiceHandle(manager);
        }

        // Each host's creation time, read once a host from one listing of the machine's processes.
        Process[] processes = Process.GetProcesses();
        try
        {
            Dictionary<int, Process> byId = processes.ToDictionary(static process => process.Id);
            var hosts = new Dictionary<int, DateTimeOffset?>();
            return [.. listed.Select(service => new RunningService(service.Name, service.ProcessId,
                hosts.TryGetValue(service.ProcessId, out DateTimeOffset? known) ? known
                    : hosts[service.ProcessId] = byId.TryGetValue(service.ProcessId, out Process? host) ? CreatedUtc(host) : null))];
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    // One call of the query from the resume point into the buffer: whether it listed the last services, and how many bytes
    // the next call needs where it did not.
    private static unsafe bool List(nint manager, byte[] buffer, ref uint resume, List<(string Name, int ProcessId)> listed, out uint needed)
    {
        fixed (byte* at = buffer)
        {
            bool done = EnumServicesStatusExW(manager, ProcessInformationLevel, Win32Services, Active, at, (uint)buffer.Length,
                out needed, out uint returned, ref resume, null);
            int error = done ? 0 : Marshal.GetLastPInvokeError();
            if (!done && error != ErrorMoreData)
            {
                throw new Win32Exception(error, "The service control manager's running services could not be listed.");
            }

            // The entries' names point into the buffer, so they are copied while it is held.
            var entries = (EnumServiceStatusProcess*)at;
            for (int index = 0; index < returned; index++)
            {
                if (entries[index].ProcessId is not 0 and <= int.MaxValue)
                {
                    listed.Add((new string(entries[index].ServiceName), (int)entries[index].ProcessId));
                }
            }

            return done;
        }
    }

    // When the operating system created a host, as a lifecycle record carries it: .NET states it in local time, which turns
    // back to the instant exactly; null where the host cannot be read or has exited.
    private static DateTimeOffset? CreatedUtc(Process host)
    {
        try
        {
            return new DateTimeOffset(host.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct EnumServiceStatusProcess
    {
        public char* ServiceName;
        public char* DisplayName;
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumServicesStatusExW(nint manager, int infoLevel, uint serviceType, uint serviceState, byte* services,
        uint bufferSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
