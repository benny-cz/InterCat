using System.Diagnostics;
using InterCat.Capture.Windows;
using InterCat.Storage;

namespace InterCat.Capture.Recording;

/// <summary>
/// Where a live recording reads its clock calibration (`contracts/clock-calibration-v1.md`): samples pairing its source
/// clock with the wall clock, and the boot it runs in.
/// </summary>
public sealed class ClockCalibrationSource
{
    /// <summary>What the wall-clock readings read.</summary>
    public required string WallClock { get; init; }

    /// <summary>One bracketed reading of the source clock paired with the wall clock.</summary>
    public required Func<ClockCalibrationSampleV1> Sample { get; init; }

    /// <summary>The boot's token and count, either unknown as null.</summary>
    public required Func<(Guid? Token, long? Count)> Boot { get; init; }

    /// <summary>
    /// This machine's: its performance counter, which every live capture here reads (<see cref="EtwSourceClock"/>), against
    /// its precise wall clock, and its boot, minting the boot's token when this process may write the machine's registry.
    /// </summary>
    public static ClockCalibrationSource Local { get; } = new()
    {
        WallClock = LocalClockReading.WallClock,
        Sample = () =>
        {
            (long ticks, long fileTime, long halfInterval) = LocalClockReading.Read();
            long frequency = Stopwatch.Frequency;
            return new()
            {
                NativeTicks = ticks,
                Utc = new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero),
                AcquisitionUncertaintyNanoseconds = checked(((halfInterval * 1_000_000_000L) + frequency - 1) / frequency)
                    + LocalClockReading.WallClockResolutionNanoseconds,
            };
        },
        Boot = () => (LocalBootIdentity.Token(mint: true), LocalBootIdentity.Count()),
    };
}
