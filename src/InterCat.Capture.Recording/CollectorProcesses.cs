using System.ComponentModel;
using System.Diagnostics;
using InterCat.Storage;

namespace InterCat.Capture.Recording;

/// <summary>
/// The processes that collect a capture, as `contracts/collector-identities-v1.md` names them: by PID and the moment the
/// operating system created each, which a lifecycle record of the same instance carries too.
/// </summary>
public static class CollectorProcesses
{
    /// <summary>This process, collecting in <paramref name="role"/>.</summary>
    public static CollectorProcessV1 Current(CollectorRole role)
    {
        using Process self = Process.GetCurrentProcess();
        return new() { Role = role, ProcessId = self.Id, CreatedUtc = CreatedUtc(self) };
    }

    /// <summary>
    /// Another process, by the PID it was named by, collecting in <paramref name="role"/>: its creation time is read when it
    /// can be opened, and left out when it has exited or cannot be, as the contract allows.
    /// </summary>
    public static CollectorProcessV1 Of(int processId, CollectorRole role)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        DateTimeOffset? created = null;
        try
        {
            using Process process = Process.GetProcessById(processId);
            created = CreatedUtc(process);
        }
        catch (ArgumentException)
        {
            // No process holds the PID now.
        }

        return new() { Role = role, ProcessId = processId, CreatedUtc = created };
    }

    /// <summary>When the operating system created <paramref name="process"/>, which .NET states in local time; null when unreadable.</summary>
    private static DateTimeOffset? CreatedUtc(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}
