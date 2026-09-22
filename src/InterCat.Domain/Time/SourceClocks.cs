namespace InterCat.Domain;

public enum TimestampEncoding
{
    Unknown = 0,
    Qpc = 1,
    FileTimeUtc = 2,
    EtwSystemTimeConverted = 3,
    CpuCycleCounter = 4,
}

public enum SourceClockKind
{
    Unknown = 0,
    Monotonic = 1,
    WallClock = 2,
    ProcessorCycle = 3,
}

public enum TimestampRounding
{
    TowardZero = 1,
    NearestEven = 2,
}

public enum TimestampQuarantineReason
{
    None = 0,
    ClockMismatch = 1,
    EncodingMismatch = 2,
    Overflow = 3,
    ImplausibleDistance = 4,
}

public readonly record struct NativeTimestamp(ClockId ClockId, TimestampEncoding Encoding, long Ticks);

public readonly record struct SessionTimestamp(long Nanoseconds);

public readonly record struct WorkspaceTimestamp(long Nanoseconds, uint AlignmentRevision);

public readonly record struct WallClockEstimate
{
    public WallClockEstimate(long utcTicks, long uncertaintyTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(uncertaintyTicks);
        UtcTicks = utcTicks;
        UncertaintyTicks = uncertaintyTicks;
    }

    public long UtcTicks { get; }
    public long UncertaintyTicks { get; }
}

/// <summary>Immutable source time. Workspace alignment is deliberately not a writable member.</summary>
public sealed record ObservationTime(
    NativeTimestamp Native,
    SessionTimestamp LocalRelative,
    WallClockEstimate? WallClock);

public readonly record struct ClockCalibrationSample
{
    public ClockCalibrationSample(ClockId clockId, long nativeTicks, long utcTicks, long acquisitionUncertaintyTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(acquisitionUncertaintyTicks);
        ClockId = clockId;
        NativeTicks = nativeTicks;
        UtcTicks = utcTicks;
        AcquisitionUncertaintyTicks = acquisitionUncertaintyTicks;
    }

    public ClockId ClockId { get; }
    public long NativeTicks { get; }
    public long UtcTicks { get; }
    public long AcquisitionUncertaintyTicks { get; }
}

public readonly record struct SourceClockDescriptor
{
    public SourceClockDescriptor(
        ClockId id,
        HostId hostId,
        SourceClockKind kind,
        TimestampEncoding encoding,
        long ticksPerSecond,
        long captureEpochNativeTicks,
        TimestampRounding rounding,
        long maximumAbsoluteSessionNanoseconds)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("A clock ID cannot be empty.", nameof(id));
        }

        if (hostId.Value == Guid.Empty)
        {
            throw new ArgumentException("A host ID cannot be empty.", nameof(hostId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAbsoluteSessionNanoseconds);
        if (kind == SourceClockKind.Unknown || encoding == TimestampEncoding.Unknown)
        {
            throw new ArgumentException("A persisted clock descriptor must declare its kind and timestamp encoding.");
        }

        Id = id;
        HostId = hostId;
        Kind = kind;
        Encoding = encoding;
        TicksPerSecond = ticksPerSecond;
        CaptureEpochNativeTicks = captureEpochNativeTicks;
        Rounding = rounding;
        MaximumAbsoluteSessionNanoseconds = maximumAbsoluteSessionNanoseconds;
    }

    public ClockId Id { get; }
    public HostId HostId { get; }
    public SourceClockKind Kind { get; }
    public TimestampEncoding Encoding { get; }
    public long TicksPerSecond { get; }
    public long CaptureEpochNativeTicks { get; }
    public TimestampRounding Rounding { get; }
    public long MaximumAbsoluteSessionNanoseconds { get; }
}

public readonly record struct TimestampConversionResult(
    SessionTimestamp? SessionTime,
    TimestampQuarantineReason QuarantineReason)
{
    public bool IsValid => SessionTime is not null && QuarantineReason == TimestampQuarantineReason.None;
}

public static class SourceClockMath
{
    public const long SessionTicksPerSecond = 1_000_000_000;

    public static TimestampConversionResult ConvertToSession(
        SourceClockDescriptor clock,
        NativeTimestamp timestamp)
    {
        if (timestamp.ClockId != clock.Id)
        {
            return new(null, TimestampQuarantineReason.ClockMismatch);
        }

        if (timestamp.Encoding != clock.Encoding)
        {
            return new(null, TimestampQuarantineReason.EncodingMismatch);
        }

        try
        {
            Int128 nativeDelta = (Int128)timestamp.Ticks - clock.CaptureEpochNativeTicks;
            Int128 scaled = Scale(nativeDelta, SessionTicksPerSecond, clock.TicksPerSecond, clock.Rounding);
            if (scaled > long.MaxValue || scaled < long.MinValue)
            {
                return new(null, TimestampQuarantineReason.Overflow);
            }

            long nanoseconds = (long)scaled;
            if (nanoseconds > clock.MaximumAbsoluteSessionNanoseconds
                || nanoseconds < -clock.MaximumAbsoluteSessionNanoseconds)
            {
                return new(null, TimestampQuarantineReason.ImplausibleDistance);
            }

            return new(new SessionTimestamp(nanoseconds), TimestampQuarantineReason.None);
        }
        catch (OverflowException)
        {
            return new(null, TimestampQuarantineReason.Overflow);
        }
    }

    public static WorkspaceTimestamp Align(
        SessionTimestamp localTime,
        long scaleNumerator,
        long scaleDenominator,
        long offsetNanoseconds,
        uint alignmentRevision,
        TimestampRounding rounding = TimestampRounding.NearestEven)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleNumerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleDenominator);
        Int128 scaled = Scale(localTime.Nanoseconds, scaleNumerator, scaleDenominator, rounding);
        Int128 aligned = scaled + offsetNanoseconds;
        return aligned <= long.MaxValue && aligned >= long.MinValue
            ? new((long)aligned, alignmentRevision)
            : throw new OverflowException("The workspace-aligned timestamp is outside Int64 range.");
    }

    /// <summary>
    /// The first native reading whose session-relative instant is at or after <paramref name="sessionTime"/>, under
    /// exactly the conversion <see cref="ConvertToSession"/> applies. It turns a half-open session-time interval
    /// into the half-open native interval holding the same readings: the readings <c>n</c> with
    /// <c>a &lt;= session(n) &lt; b</c> are exactly <c>[FirstNativeAtOrAfter(a), FirstNativeAtOrAfter(b))</c>,
    /// because the conversion never decreases (I3, I8). Nothing is rounded twice and nothing is clamped.
    /// </summary>
    public static long FirstNativeAtOrAfter(SourceClockDescriptor clock, SessionTimestamp sessionTime)
    {
        // An estimate from the inverse scale, then the exact boundary found by applying the forward conversion:
        // the forward conversion is what every stored session instant was produced by, so it is the one that
        // decides which readings a boundary admits.
        Int128 estimate = (Int128)clock.CaptureEpochNativeTicks
            + Scale(sessionTime.Nanoseconds, clock.TicksPerSecond, SessionTicksPerSecond, TimestampRounding.TowardZero);
        if (estimate > long.MaxValue || estimate < long.MinValue)
        {
            throw new OverflowException("The session instant is outside the native reading range of this clock.");
        }

        long candidate = (long)estimate;
        while (SessionNanoseconds(clock, candidate) < sessionTime.Nanoseconds)
        {
            candidate = checked(candidate + 1);
        }

        while (SessionNanoseconds(clock, checked(candidate - 1)) >= sessionTime.Nanoseconds)
        {
            candidate--;
        }

        return candidate;
    }

    /// <summary>A native reading's session-relative instant, unbounded by the plausibility window.</summary>
    public static Int128 SessionNanoseconds(SourceClockDescriptor clock, long nativeTicks) =>
        Scale((Int128)nativeTicks - clock.CaptureEpochNativeTicks, SessionTicksPerSecond, clock.TicksPerSecond, clock.Rounding);

    private static Int128 Scale(Int128 value, long numerator, long denominator, TimestampRounding rounding)
    {
        Int128 product = checked(value * numerator);
        if (rounding == TimestampRounding.TowardZero)
        {
            return product / denominator;
        }

        if (rounding != TimestampRounding.NearestEven)
        {
            throw new ArgumentOutOfRangeException(nameof(rounding));
        }

        bool negative = product < 0;
        Int128 magnitude = Int128.Abs(product);
        Int128 quotient = magnitude / denominator;
        Int128 remainder = magnitude % denominator;
        Int128 doubled = remainder * 2;
        if (doubled > denominator || (doubled == denominator && (quotient & 1) != 0))
        {
            quotient++;
        }

        return negative ? -quotient : quotient;
    }
}
