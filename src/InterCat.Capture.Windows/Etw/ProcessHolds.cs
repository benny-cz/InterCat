using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace InterCat.Capture.Windows;

/// <summary>
/// The processes a session's process filter names, held open while the session lives. A process filter holds process
/// IDs, and Windows gives no process an ID while a handle to the process that had it is still open; so, held, the IDs a
/// filter names stay the processes it named. A named process that exits keeps its ID until the session ends, and no
/// record of another process can enter through it (R22, ADR-037). A process that is not running, or has already exited,
/// is refused: its ID could be anyone's.
/// </summary>
public sealed partial class ProcessHolds : IDisposable
{
    private const uint Synchronize = 0x0010_0000;
    private const uint QueryLimitedInformation = 0x1000;
    private const int ErrorInvalidParameter = 87;
    private const uint WaitObject0 = 0;

    private readonly Dictionary<int, SafeProcessHandle> held = [];
    private readonly Lock gate = new();
    private bool disposed;

    /// <summary>The process IDs held open, in order.</summary>
    public IReadOnlyList<int> Held
    {
        get
        {
            lock (gate)
            {
                return [.. held.Keys.Order()];
            }
        }
    }

    /// <summary>
    /// Holds each of <paramref name="processIds"/> open. False, with the reason in words, when one is not running, has
    /// already exited or cannot be opened; any held before it stay held until the holds are disposed.
    /// </summary>
    public bool TryHold(IReadOnlyList<int> processIds, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        if (!OperatingSystem.IsWindows())
        {
            refusal = "holding a process open needs Windows";
            return false;
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach (int processId in processIds)
            {
                if (held.ContainsKey(processId))
                {
                    continue;
                }

                SafeProcessHandle handle = OpenProcess(Synchronize | QueryLimitedInformation, false, processId);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    refusal = error == ErrorInvalidParameter
                        ? string.Create(CultureInfo.InvariantCulture, $"process {processId} is not running, so its ID could be given to any process")
                        : string.Create(CultureInfo.InvariantCulture,
                            $"process {processId} could not be held open (Windows error {error}), so its ID could pass to another process");
                    return false;
                }

                if (WaitForSingleObject(handle, 0) == WaitObject0)
                {
                    handle.Dispose();
                    refusal = string.Create(CultureInfo.InvariantCulture,
                        $"process {processId} has already exited, so nothing it could record is the named process's");
                    return false;
                }

                held[processId] = handle;
            }
        }

        refusal = null;
        return true;
    }

    /// <summary>Lets every held process go: only once nothing can deliver a record through its ID any more.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (SafeProcessHandle handle in held.Values)
            {
                handle.Dispose();
            }

            held.Clear();
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
