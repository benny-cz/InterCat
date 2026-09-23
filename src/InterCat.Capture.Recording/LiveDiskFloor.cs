using System.Globalization;
using InterCat.Storage;

namespace InterCat.Capture.Recording;

/// <summary>Free space on the volume holding a recording's evidence, as one probe observed it.</summary>
/// <param name="AvailableBytes">Bytes this process may still allocate on the volume, after any per-user quota.</param>
/// <param name="AllocationUnitBytes">The volume's cluster size; every new file may round up by one of these.</param>
public readonly record struct VolumeSpace(long AvailableBytes, long AllocationUnitBytes);

/// <summary>
/// InterCat's own write-admission floor for a live recording (plan revision 76, §20.2). An append is admitted only if,
/// after it, the volume would still hold the configured minimum free bytes plus the bounded headroom the recording needs
/// to finish: the unwritten journal tail, the final coverage ledger and finalization marker, the last manifest and its
/// pointers, and one allocation unit per file those writes may create.
/// </summary>
/// <remarks>
/// This bounds what InterCat writes. It is not an exclusive reservation: another process can still consume the volume
/// between probes, which is why the probe is repeated and why finalization may use the reserved headroom but never
/// admits new records below the floor.
/// </remarks>
public sealed class LiveDiskFloor
{
    /// <summary>
    /// The staging stream may hold written bytes that have not reached the volume yet. FileStream buffers 4 KiB by
    /// default; this is that buffer rounded up generously so a changed default cannot silently undercount.
    /// </summary>
    internal const long StreamBufferBytes = 65_536;

    /// <summary>Files a final publication can create: the ledger, the marker, the manifest and two pointers.</summary>
    private const int FinalPublicationFiles = 5;

    /// <summary>Files an intermediate publication creates besides the next chunk: the manifest and two pointers.</summary>
    private const int IntermediatePublicationFiles = 3;

    private const long LargestAllocationUnitBytes = 2L * 1024 * 1024;

    private readonly Func<VolumeSpace> probe;

    public LiveDiskFloor(long minimumFreeBytes, Func<VolumeSpace> probe, TimeSpan? probeInterval = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumFreeBytes);
        ArgumentNullException.ThrowIfNull(probe);
        TimeSpan interval = probeInterval ?? TimeSpan.FromMilliseconds(250);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(probeInterval), "A free-space probe interval is positive.");
        }

        MinimumFreeBytes = minimumFreeBytes;
        ProbeInterval = interval;
        this.probe = probe;
    }

    /// <summary>The configured floor: free bytes InterCat's own writes never take the volume below.</summary>
    public long MinimumFreeBytes { get; }

    /// <summary>How long an observation stays current before the next admitted append probes again.</summary>
    public TimeSpan ProbeInterval { get; }

    /// <summary>
    /// The bytes a stopped recording still writes after its last admitted record, besides its journal's unwritten tail:
    /// a bounded coverage ledger and finalization marker, the last manifest naming
    /// <paramref name="dependenciesAfterFinal"/> files and its pointers, and allocation-unit rounding of each new file.
    /// </summary>
    public static long FinalizationHeadroom(int dependenciesAfterFinal, long allocationUnitBytes) => checked(
        CoverageLedgerV1.MaximumBytes
        + CaptureFinalizationV1.MaximumBytes
        + SessionStore.PublicationMetadataBound(dependenciesAfterFinal)
        + (FinalPublicationFiles + 1) * allocationUnitBytes
        + StreamBufferBytes);

    /// <summary>The bytes an intermediate publication writes besides the chunk it completes and the next chunk's header.</summary>
    internal static long IntermediatePublicationBytes(int dependenciesAfterPublication, long allocationUnitBytes) => checked(
        SessionStore.PublicationMetadataBound(dependenciesAfterPublication)
        + (IntermediatePublicationFiles + 1) * allocationUnitBytes);

    /// <summary>A byte count in binary units, for messages a person reads.</summary>
    public static string Describe(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.##} GiB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.##} MiB"),
        >= 1L << 10 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 10):0.##} KiB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes"),
    };

    /// <summary>Probes the volume, refusing a reading that could not describe a real one.</summary>
    internal VolumeSpace Observe()
    {
        VolumeSpace space = probe();
        if (space.AvailableBytes < 0
            || space.AllocationUnitBytes is < 512 or > LargestAllocationUnitBytes
            || (space.AllocationUnitBytes & (space.AllocationUnitBytes - 1)) != 0)
        {
            throw new InvalidDataException(
                $"The free-space probe returned an impossible reading ({space.AvailableBytes} bytes available, "
                + $"{space.AllocationUnitBytes}-byte allocation unit).");
        }

        return space;
    }
}
