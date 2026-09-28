using InterCat.Storage;

namespace InterCat.Application;

/// <summary>How fast a capture's wall clock ran against its source clock between two calibration samples.</summary>
/// <param name="PartsPerMillion">Positive when the wall clock ran fast of the source clock.</param>
/// <param name="UncertaintyPartsPerMillion">The half-width the two samples' acquisition uncertainties allow.</param>
public sealed record WallClockRate(double PartsPerMillion, double UncertaintyPartsPerMillion);

/// <summary>What a capture's clock calibration says (`contracts/clock-calibration-v1.md`), read the same way everywhere.</summary>
public static class ClockCalibrationFacts
{
    /// <summary>
    /// How far the wall clock ran from the source clock between the first and the last sample, in parts per million of
    /// the source clock's interval, with the uncertainty the two samples' acquisition allows; null with fewer than two
    /// samples or none of the source clock's time between them. It measures the wall clock's steering and the counter's
    /// rate together, and says nothing of how right the wall clock was.
    /// </summary>
    public static WallClockRate? Rate(ClockCalibrationV1 calibration, long ticksPerSecond)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);
        if (calibration.Samples.Count < 2)
        {
            return null;
        }

        ClockCalibrationSampleV1 first = calibration.Samples[0];
        ClockCalibrationSampleV1 last = calibration.Samples[^1];
        double source = (last.NativeTicks - first.NativeTicks) * 1e9 / ticksPerSecond;
        if (source <= 0)
        {
            return null;
        }

        double wall = (last.Utc - first.Utc).Ticks * 100.0;
        return new(
            (wall - source) / source * 1e6,
            (first.AcquisitionUncertaintyNanoseconds + last.AcquisitionUncertaintyNanoseconds) / source * 1e6);
    }
}
