using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using InterCat.Capture.Recording;

namespace InterCat.CaptureBroker;

/// <summary>
/// Reads the free space the broker may still allocate beneath a directory. GetDiskFreeSpaceEx answers for the calling
/// identity, so a per-user disk quota is honoured rather than reporting the volume's raw free space.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsVolumeSpace
{
    public static VolumeSpace Probe(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string path = directory.EndsWith('\\') ? directory : directory + '\\';
        if (!GetDiskFreeSpaceEx(path, out ulong available, out _, out _))
        {
            throw new IOException(
                $"Free space beneath '{directory}' could not be read.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        string root = Path.GetPathRoot(directory)
            ?? throw new IOException($"'{directory}' is not on a volume.");
        if (!GetDiskFreeSpace(root, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _))
        {
            throw new IOException(
                $"The allocation unit of '{root}' could not be read.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return new(
            available > long.MaxValue ? long.MaxValue : (long)available,
            checked((long)sectorsPerCluster * bytesPerSector));
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpace(
        string rootPathName,
        out uint sectorsPerCluster,
        out uint bytesPerSector,
        out uint numberOfFreeClusters,
        out uint totalNumberOfClusters);
}
