using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InterCat.CaptureComparison;

/// <summary>
/// A measured reading of the volume a run writes to. Section 12 defines its reference device by sustained
/// throughput, not by a model string, so the harness measures the throughput rather than quoting a name
/// it cannot verify. Either figure may be null when the probe could not run; nothing is assumed.
/// </summary>
internal sealed record StorageMeasurement
{
    public required string Volume { get; init; }
    public required string FileSystem { get; init; }
    public required long TotalBytes { get; init; }
    public required long AvailableBytes { get; init; }
    public required long ProbeBytes { get; init; }
    public required double? SequentialWriteBytesPerSecond { get; init; }
    public required double? SequentialReadBytesPerSecond { get; init; }

    /// <summary>Whether the measured figures reach the section 12 reference device. Null when unmeasured.</summary>
    public required bool? MeetsReferenceDevice { get; init; }

    public required string? UnavailableReason { get; init; }
}

/// <summary>
/// The machine a result was taken on. Section 12 requires it with every number, because a rate without
/// its machine is not a measurement.
/// </summary>
internal sealed record MachineDescriptor
{
    public required string OperatingSystem { get; init; }
    public required string Architecture { get; init; }
    public required int LogicalProcessors { get; init; }
    public required long TotalPhysicalMemoryBytes { get; init; }
    public required StorageMeasurement Storage { get; init; }
    public required bool MeetsReferenceMachine { get; init; }
    public required IReadOnlyList<string> ReferenceGaps { get; init; }
}

internal static class StorageProbe
{
    /// <summary>Section 12's reference device sustains at least this much sequential write throughput.</summary>
    public const double ReferenceWriteBytesPerSecond = 1_000_000_000d;

    /// <summary>Section 12's reference device sustains at least this much sequential read throughput.</summary>
    public const double ReferenceReadBytesPerSecond = 2_000_000_000d;

    /// <summary>Section 12's reference machine has at least this many logical processors.</summary>
    public const int ReferenceLogicalProcessors = 16;

    /// <summary>Section 12's reference machine has at least this much memory.</summary>
    public const long ReferenceMemoryBytes = 32L * 1024 * 1024 * 1024;

    private const int ProbeBytes = 128 * 1024 * 1024;
    private const int ChunkBytes = 1024 * 1024;

    /// <summary>
    /// Describes the machine and measures the volume the run writes to. The probe writes a bounded file,
    /// forces it to the device, reads it back with no buffering hint, then deletes it.
    /// </summary>
    public static MachineDescriptor Describe(string outputDirectory)
    {
        StorageMeasurement storage = MeasureVolume(outputDirectory);
        long memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var gaps = new List<string>();
        if (Environment.ProcessorCount < ReferenceLogicalProcessors)
        {
            gaps.Add(
                $"{Environment.ProcessorCount} logical processors, below the reference "
                + $"{ReferenceLogicalProcessors}.");
        }

        if (memory < ReferenceMemoryBytes)
        {
            gaps.Add($"{memory / (1024 * 1024 * 1024)} GiB of memory, below the reference 32 GiB.");
        }

        if (storage.MeetsReferenceDevice != true)
        {
            gaps.Add(
                storage.UnavailableReason is { } reason
                    ? $"Storage throughput was not measured: {reason}"
                    : "Measured storage throughput is below the reference device.");
        }

        return new()
        {
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            LogicalProcessors = Environment.ProcessorCount,
            TotalPhysicalMemoryBytes = memory,
            Storage = storage,
            MeetsReferenceMachine = gaps.Count == 0,
            ReferenceGaps = gaps,
        };
    }

    private static StorageMeasurement MeasureVolume(string outputDirectory)
    {
        string full = Path.GetFullPath(outputDirectory);
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
                    PreallocationSize = ProbeBytes,
                }))
            {
                for (long written = 0; written < ProbeBytes; written += ChunkBytes)
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
            double? writeRate = Rate(ProbeBytes, writeElapsed);
            double? readRate = Rate(read, readElapsed);
            return new()
            {
                Volume = drive.Name,
                FileSystem = drive.DriveFormat,
                TotalBytes = drive.TotalSize,
                AvailableBytes = drive.AvailableFreeSpace,
                ProbeBytes = ProbeBytes,
                SequentialWriteBytesPerSecond = writeRate,
                SequentialReadBytesPerSecond = readRate,
                MeetsReferenceDevice = writeRate is null || readRate is null
                    ? null
                    : writeRate >= ReferenceWriteBytesPerSecond && readRate >= ReferenceReadBytesPerSecond,
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
                MeetsReferenceDevice = null,
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
    internal static double? Rate(long bytes, TimeSpan elapsed) =>
        bytes <= 0 || elapsed <= TimeSpan.Zero ? null : bytes / elapsed.TotalSeconds;
}
