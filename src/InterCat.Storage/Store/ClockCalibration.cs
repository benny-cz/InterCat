using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InterCat.Storage;

/// <summary>
/// One reading of a capture's source clock paired with a reading of the wall clock, bracketed by two source readings so
/// how far apart the pair may be is measured, not assumed (§8.1).
/// </summary>
public sealed record ClockCalibrationSampleV1
{
    /// <summary>The source clock's reading midway between the two that bracketed the wall-clock read.</summary>
    public required long NativeTicks { get; init; }

    /// <summary>The wall clock's reading, in UTC, at its own resolution.</summary>
    public required DateTimeOffset Utc { get; init; }

    /// <summary>
    /// How far from simultaneous the pair may be, in nanoseconds: half the bracketing interval plus the wall clock's
    /// resolution. It bounds only this reading; it says nothing of how right the wall clock was.
    /// </summary>
    public required long AcquisitionUncertaintyNanoseconds { get; init; }
}

/// <summary>
/// A live capture's source clock against the wall clock, and the boot it ran in (`contracts/clock-calibration-v1.md`):
/// evidence about the capture, which no journal holds and nothing can rebuild, kept with it like its coverage ledger.
/// </summary>
public sealed record ClockCalibrationV1
{
    public const string ContractName = "clock-calibration-v1";
    public const int MaximumBytes = 16_384;
    public const int MaximumSamples = 64;

    private static readonly JsonSerializerOptions Json = CreateOptions();

    public required string Contract { get; init; }

    public required Guid CaptureId { get; init; }

    /// <summary>The source clock the samples read: the capture's journal's.</summary>
    public required Guid ClockId { get; init; }

    /// <summary>
    /// A random identity of the boot the capture ran in, kept by the operating system only until it restarts, so every
    /// capture of one boot of one machine names the same one and no other capture does; null when none could be kept.
    /// </summary>
    public Guid? BootToken { get; init; }

    /// <summary>
    /// The operating system's count of its boots when the capture ran, for people; null when it could not be read. It is
    /// no identity: a clone counts as its original did.
    /// </summary>
    public long? BootCount { get; init; }

    /// <summary>What the wall-clock readings read, such as <c>GetSystemTimePreciseAsFileTime</c>.</summary>
    public required string WallClock { get; init; }

    /// <summary>The samples, in the order taken: one when the capture started and one when it stopped, or more.</summary>
    public required IReadOnlyList<ClockCalibrationSampleV1> Samples { get; init; }

    public byte[] Encode()
    {
        Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        return bytes.Length <= MaximumBytes
            ? bytes
            : throw new InvalidDataException($"The clock calibration is {bytes.Length} bytes, beyond its {MaximumBytes}-byte bound.");
    }

    public static ClockCalibrationV1 Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumBytes)
        {
            throw new InvalidDataException($"A {ContractName} file is empty or exceeds its {MaximumBytes}-byte bound.");
        }

        ClockCalibrationV1 calibration;
        try
        {
            calibration = JsonSerializer.Deserialize<ClockCalibrationV1>(bytes, Json)
                ?? throw new InvalidDataException("A clock calibration has no object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The clock calibration is not valid {ContractName} JSON.", exception);
        }

        calibration.Validate();
        return calibration;
    }

    /// <summary>
    /// The one clock calibration a generation carries, its bytes checked against the digest its generation records; null
    /// for a generation that carries none, as an import's, a redacted package's and every capture before it do.
    /// </summary>
    public static ClockCalibrationV1? Read(IOwnedDirectory directory, SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(manifest);
        StoreDependency[] calibrations =
        [
            .. manifest.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.ClockCalibration),
        ];
        if (calibrations.Length == 0)
        {
            return null;
        }

        if (calibrations.Length != 1)
        {
            throw new InvalidDataException(
                $"Generation {manifest.Generation} carries {calibrations.Length} clock calibrations; one capture has one.");
        }

        StoreDependency file = calibrations[0];
        if (file.LengthBytes is < 1 or > MaximumBytes)
        {
            throw new InvalidDataException($"Clock calibration '{file.Name}' is outside its {MaximumBytes}-byte bound.");
        }

        byte[] bytes = new byte[checked((int)file.LengthBytes)];
        using (FileStream stream = directory.OpenOwnedFile(
            file.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan))
        {
            if (stream.Length != file.LengthBytes)
            {
                throw new InvalidDataException(
                    $"Clock calibration '{file.Name}' changed length after generation {manifest.Generation} was published.");
            }

            stream.ReadExactly(bytes);
        }

        string measured = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        return string.Equals(measured, file.Digest, StringComparison.Ordinal)
            ? Decode(bytes)
            : throw new InvalidDataException(
                $"Clock calibration '{file.Name}' changed after generation {manifest.Generation} was published.");
    }

    public void Validate()
    {
        string? problem = !string.Equals(Contract, ContractName, StringComparison.Ordinal)
                ? $"it must name contract '{ContractName}', not '{Contract}'"
            : CaptureId == Guid.Empty || ClockId == Guid.Empty ? "it names no capture or no clock"
            : BootToken == Guid.Empty ? "its boot token is empty; a boot with no token names none"
            : BootCount < 0 ? "its boot count is negative"
            : string.IsNullOrWhiteSpace(WallClock) || WallClock.Length > 128 ? "it names no wall clock, or one beyond 128 characters"
            : Samples is null || Samples.Count is < 1 or > MaximumSamples ? $"it holds no sample, or more than {MaximumSamples}"
            : Samples.Any(sample => sample is null || sample.Utc == default || sample.AcquisitionUncertaintyNanoseconds < 0)
                ? "a sample has no wall-clock reading or a negative uncertainty"
            : Samples.Zip(Samples.Skip(1)).Any(pair => pair.Second.NativeTicks < pair.First.NativeTicks)
                ? "its samples are not in the order their source readings were taken"
            : null;
        if (problem is not null)
        {
            throw new InvalidDataException($"A clock calibration is refused: {problem}.");
        }
    }

    private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 8,
    };
}
