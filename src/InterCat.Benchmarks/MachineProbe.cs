using System.Diagnostics;
using System.Runtime.InteropServices;
using InterCat.Domain;

namespace InterCat.Benchmarks;

/// <summary>
/// Measures the machine a benchmark runs on. §12 requires the machine with every number and defines its
/// reference device by sustained throughput, so this measures the throughput rather than quoting a model
/// name it cannot verify. Everything it cannot measure it reports as unmeasured (R21).
/// </summary>
public static class MachineProbe
{
    private const int DefaultProbeBytes = 128 * 1024 * 1024;
    private const int ChunkBytes = 1024 * 1024;

    /// <summary>
    /// Describes this machine and measures the volume the caller will write to. The probe writes a
    /// bounded file with write-through, forces it to the device, reads it back and deletes it.
    /// </summary>
    public static MachineDescriptor Describe(string directory, int probeBytes = DefaultProbeBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(probeBytes, ChunkBytes);

        return new()
        {
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            LogicalProcessors = Environment.ProcessorCount,
            TotalPhysicalMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            Storage = MeasureVolume(directory, probeBytes),
        };
    }

    /// <summary>Describes the machine without touching the disk, for a caller that only needs the host.</summary>
    public static MachineDescriptor DescribeWithoutStorage(string volumeHint, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(volumeHint)) ?? volumeHint);
        return new()
        {
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            LogicalProcessors = Environment.ProcessorCount,
            TotalPhysicalMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            Storage = new()
            {
                Volume = drive.Name,
                FileSystem = drive.DriveFormat,
                TotalBytes = drive.TotalSize,
                AvailableBytes = drive.AvailableFreeSpace,
                ProbeBytes = 0,
                SequentialWriteBytesPerSecond = null,
                SequentialReadBytesPerSecond = null,
                UnavailableReason = reason,
            },
        };
    }

    private static StorageMeasurement MeasureVolume(string directory, int probeBytes)
    {
        string full = Path.GetFullPath(directory);
        var drive = new DriveInfo(Path.GetPathRoot(full) ?? full);
        string probePath = Path.Combine(full, $"storage-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(full);
            byte[] chunk = new byte[ChunkBytes];
            Random.Shared.NextBytes(chunk);

            long writeStarted = Stopwatch.GetTimestamp();
            using (var stream = new FileStream(
                probePath,
                new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = FileMode.CreateNew,
                    Share = FileShare.None,
                    BufferSize = 0,
                    Options = FileOptions.WriteThrough | FileOptions.SequentialScan,
                    PreallocationSize = probeBytes,
                }))
            {
                for (long written = 0; written < probeBytes; written += ChunkBytes)
                {
                    stream.Write(chunk);
                }

                stream.Flush(flushToDisk: true);
            }

            TimeSpan writeElapsed = Stopwatch.GetElapsedTime(writeStarted);

            long readStarted = Stopwatch.GetTimestamp();
            long read = 0;
            using (var stream = new FileStream(
                probePath,
                new FileStreamOptions
                {
                    Access = FileAccess.Read,
                    Mode = FileMode.Open,
                    Share = FileShare.None,
                    BufferSize = 0,
                    Options = FileOptions.SequentialScan,
                }))
            {
                int taken;
                while ((taken = stream.Read(chunk)) > 0)
                {
                    read += taken;
                }
            }

            TimeSpan readElapsed = Stopwatch.GetElapsedTime(readStarted);
            return new()
            {
                Volume = drive.Name,
                FileSystem = drive.DriveFormat,
                TotalBytes = drive.TotalSize,
                AvailableBytes = drive.AvailableFreeSpace,
                ProbeBytes = probeBytes,
                SequentialWriteBytesPerSecond = Rate(probeBytes, writeElapsed),
                SequentialReadBytesPerSecond = Rate(read, readElapsed),
                UnavailableReason = null,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new()
            {
                Volume = drive.Name,
                FileSystem = drive.DriveFormat,
                TotalBytes = drive.TotalSize,
                AvailableBytes = drive.AvailableFreeSpace,
                ProbeBytes = 0,
                SequentialWriteBytesPerSecond = null,
                SequentialReadBytesPerSecond = null,
                UnavailableReason = exception.Message,
            };
        }
        finally
        {
            try
            {
                if (File.Exists(probePath))
                {
                    File.Delete(probePath);
                }
            }
            catch (IOException)
            {
                // A probe file left behind is a nuisance, not a measurement error.
            }
        }
    }

    /// <summary>Bytes per second, or null when the interval is too short to divide by honestly.</summary>
    public static double? Rate(long bytes, TimeSpan elapsed) =>
        bytes <= 0 || elapsed <= TimeSpan.Zero ? null : bytes / elapsed.TotalSeconds;
}
