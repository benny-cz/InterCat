using System.Diagnostics;
using InterCat.Domain;

namespace InterCat.Capture.Windows;

/// <summary>
/// Whether the capture's assumed timestamp encoding was confirmed against a record the session delivered.
/// An unconfirmed clock is usable for ordering but is never presented as a verified encoding (I8, R21).
/// </summary>
public enum SourceClockConfidence
{
    /// <summary>No record has been checked against the descriptor yet.</summary>
    Unconfirmed = 0,

    /// <summary>A delivered record's native reading sits within the plausibility window of this clock.</summary>
    Confirmed = 1,

    /// <summary>A delivered record's reading is not on this clock. The session's encoding stays unknown.</summary>
    Refused = 2,
}

/// <summary>
/// The persisted source clock of one capture, plus the evidence that the session actually used it. The
/// descriptor alone is an assumption; the first checked reading is what turns it into a measurement.
/// </summary>
public sealed record CaptureClockEvidence
{
    public required SourceClockDescriptor Descriptor { get; init; }
    public required SourceClockConfidence Confidence { get; init; }

    /// <summary>This process's own reading of the same clock, taken when the capture started.</summary>
    public required long ReferenceNativeTicks { get; init; }

    /// <summary>The first delivered record's native reading, or null when nothing has been checked.</summary>
    public required long? CheckedNativeTicks { get; init; }

    /// <summary>Why the reading was refused, stated field by field rather than as a generic failure.</summary>
    public required string? RefusalReason { get; init; }
}

/// <summary>
/// Builds and validates the identity-v1 source clock descriptor for an ETW capture. A real-time session
/// stamps records with QPC by default, but InterCat records that as a claim it checks rather than as a
/// fact it assumes: a session configured for system time or CPU cycles produces readings orders of
/// magnitude away from this process's own QPC, and those are refused instead of converted (section 18.3).
/// </summary>
public static class EtwSourceClock
{
    /// <summary>How far a delivered reading may sit from this process's reading and still be the same clock.</summary>
    public static readonly TimeSpan DefaultPlausibilityWindow = TimeSpan.FromDays(1);

    /// <summary>The largest session-relative distance a converted timestamp may reach before quarantine.</summary>
    public static readonly TimeSpan DefaultSessionBound = TimeSpan.FromDays(7);

    /// <summary>
    /// Describes the clock a capture starting now is expected to use, anchored to this process's own
    /// reading of it. The descriptor is complete enough to convert native ticks during offline replay.
    /// </summary>
    public static CaptureClockEvidence Describe(
        HostId hostId,
        long referenceNativeTicks,
        long ticksPerSecond) => new()
        {
            Descriptor = new(
                ClockId.New(),
                hostId,
                SourceClockKind.Monotonic,
                TimestampEncoding.Qpc,
                ticksPerSecond,
                referenceNativeTicks,
                TimestampRounding.NearestEven,
                (long)(DefaultSessionBound.TotalSeconds * SourceClockMath.SessionTicksPerSecond)),
            Confidence = SourceClockConfidence.Unconfirmed,
            ReferenceNativeTicks = referenceNativeTicks,
            CheckedNativeTicks = null,
            RefusalReason = null,
        };

    /// <summary>Describes the local performance counter this process reads, as the capture's clock anchor.</summary>
    public static CaptureClockEvidence DescribeLocal() =>
        Describe(HostId.ForLocalMachine(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);

    /// <summary>
    /// Checks one delivered native reading against the descriptor. A reading inside the window confirms the
    /// encoding; one outside it refuses the claim with the two values that disagree, so the refusal can be
    /// read without rerunning the capture.
    /// </summary>
    public static CaptureClockEvidence Check(
        CaptureClockEvidence evidence,
        long observedNativeTicks,
        TimeSpan? plausibilityWindow = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Confidence != SourceClockConfidence.Unconfirmed)
        {
            return evidence;
        }

        TimeSpan window = plausibilityWindow ?? DefaultPlausibilityWindow;
        long windowTicks = (long)Math.Min(
            window.TotalSeconds * evidence.Descriptor.TicksPerSecond,
            long.MaxValue / 2d);
        Int128 distance = Int128.Abs((Int128)observedNativeTicks - evidence.ReferenceNativeTicks);
        bool plausible = observedNativeTicks > 0 && distance <= windowTicks;
        return evidence with
        {
            Confidence = plausible ? SourceClockConfidence.Confirmed : SourceClockConfidence.Refused,
            CheckedNativeTicks = observedNativeTicks,
            RefusalReason = plausible
                ? null
                : $"A delivered record reads {observedNativeTicks} on a clock this process reads as "
                    + $"{evidence.ReferenceNativeTicks} at {evidence.Descriptor.TicksPerSecond} ticks/s. "
                    + "The session's timestamp encoding is therefore not the assumed QPC and stays unknown.",
        };
    }
}
