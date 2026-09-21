using System.Globalization;

namespace InterCat.Domain;

/// <summary>
/// A measured reading of one volume. §12 defines its reference device by sustained throughput rather than
/// by a model name, so a result measures the throughput instead of quoting a name it cannot verify.
/// Either figure may be null when the probe could not run; nothing is assumed from an absent one (R21).
/// </summary>
public sealed record StorageMeasurement
{
    public required string Volume { get; init; }
    public required string FileSystem { get; init; }
    public required long TotalBytes { get; init; }
    public required long AvailableBytes { get; init; }
    public required long ProbeBytes { get; init; }
    public required double? SequentialWriteBytesPerSecond { get; init; }
    public required double? SequentialReadBytesPerSecond { get; init; }
    public required string? UnavailableReason { get; init; }

    /// <summary>Whether the measured figures reach §12's reference device. Null when unmeasured.</summary>
    public bool? MeetsReferenceDevice =>
        SequentialWriteBytesPerSecond is not { } write || SequentialReadBytesPerSecond is not { } read
            ? null
            : write >= ReferenceMachine.WriteBytesPerSecond && read >= ReferenceMachine.ReadBytesPerSecond;

    public string Describe() => UnavailableReason is { } reason
        ? $"{Volume} ({FileSystem}): throughput not measured - {reason}"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{Volume} ({FileSystem}): {(SequentialWriteBytesPerSecond ?? 0) / 1e9:F2} GB/s write, "
            + $"{(SequentialReadBytesPerSecond ?? 0) / 1e9:F2} GB/s read");
}

/// <summary>
/// The machine a result was taken on. §12 requires it with every number, because a rate without its
/// machine is not a measurement.
/// </summary>
public sealed record MachineDescriptor
{
    public required string OperatingSystem { get; init; }
    public required string Architecture { get; init; }
    public required int LogicalProcessors { get; init; }
    public required long TotalPhysicalMemoryBytes { get; init; }
    public required StorageMeasurement Storage { get; init; }

    /// <summary>Every way this machine falls short of §12's reference. Empty means it is one.</summary>
    public IReadOnlyList<string> ReferenceGaps => ReferenceMachine.GapsOf(this);

    public bool MeetsReferenceMachine => ReferenceGaps.Count == 0;
}

/// <summary>
/// §12's reference machine, written down once so a result can be compared to it rather than described
/// against it in prose. A machine that falls short is not disqualified; its result says so.
/// </summary>
public static class ReferenceMachine
{
    /// <summary>At least 8 physical cores and 16 threads.</summary>
    public const int LogicalProcessors = 16;

    /// <summary>32 GiB of memory.</summary>
    public const long MemoryBytes = 32L * 1024 * 1024 * 1024;

    /// <summary>An NVMe SSD sustaining at least 1 GB/s sequential write.</summary>
    public const double WriteBytesPerSecond = 1_000_000_000d;

    /// <summary>An NVMe SSD sustaining at least 2 GB/s sequential read.</summary>
    public const double ReadBytesPerSecond = 2_000_000_000d;

    public static IReadOnlyList<string> GapsOf(MachineDescriptor machine)
    {
        ArgumentNullException.ThrowIfNull(machine);

        var gaps = new List<string>();
        if (machine.LogicalProcessors < LogicalProcessors)
        {
            gaps.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{machine.LogicalProcessors} logical processors, below the reference {LogicalProcessors}."));
        }

        if (machine.TotalPhysicalMemoryBytes < MemoryBytes)
        {
            gaps.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{machine.TotalPhysicalMemoryBytes / (1024d * 1024 * 1024):F0} GiB of memory, below the reference 32 GiB."));
        }

        switch (machine.Storage.MeetsReferenceDevice)
        {
            case null:
                gaps.Add(
                    machine.Storage.UnavailableReason is { } reason
                        ? $"Storage throughput was not measured: {reason}"
                        : "Storage throughput was not measured.");
                break;
            case false:
                string write = (machine.Storage.SequentialWriteBytesPerSecond / 1e9)?.ToString("F2", CultureInfo.InvariantCulture) ?? "?";
                string read = (machine.Storage.SequentialReadBytesPerSecond / 1e9)?.ToString("F2", CultureInfo.InvariantCulture) ?? "?";
                gaps.Add($"{write} GB/s write and {read} GB/s read, below the reference 1 GB/s and 2 GB/s.");
                break;
            default:
                break;
        }

        return gaps;
    }
}
