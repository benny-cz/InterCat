using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>
/// How the CLI says a session's times and instances. A native reading is shown as seconds after the capture epoch
/// only through the clock the session describes; without it, the reading stays a tick count (I8).
/// </summary>
internal static class SessionText
{
    /// <summary>A native reading as seconds after the capture epoch, invariant, or null when the clock is not described.</summary>
    public static string? Seconds(SourceClockDescriptor? clock, long nativeTicks)
    {
        if (clock is not { } described)
        {
            return null;
        }

        Int128 nanoseconds = SourceClockMath.SessionNanoseconds(described, nativeTicks);
        decimal seconds = (decimal)nanoseconds / SourceClockMath.SessionTicksPerSecond;
        return seconds.ToString("0.000000", CultureInfo.InvariantCulture);
    }

    /// <summary>A reading for a reader: seconds after capture start when the clock is known, ticks otherwise.</summary>
    public static string Instant(SourceClockDescriptor? clock, long nativeTicks) =>
        Seconds(clock, nativeTicks) is { } seconds
            ? $"{seconds} s"
            : string.Create(CultureInfo.CurrentCulture, $"{nativeTicks:N0} ticks");

    /// <summary>One instance as a reader names it: its PID, which of the PID's instances it is, and how it is witnessed.</summary>
    public static string Process(ProcessInstance instance, SourceClockDescriptor? clock, bool pidReused)
    {
        string pid = pidReused
            ? string.Create(CultureInfo.InvariantCulture, $"PID {instance.ProcessId} #{instance.LifecycleEpoch}")
            : string.Create(CultureInfo.InvariantCulture, $"PID {instance.ProcessId}");
        return $"{pid} - {Witness(instance, clock)}";
    }

    /// <summary>What the capture witnessed of an instance's life, in one short phrase.</summary>
    public static string Witness(ProcessInstance instance, SourceClockDescriptor? clock)
    {
        string exited = instance.ExitedNativeTicks is { } exit
            ? $", exited {Instant(clock, exit)}"
            : instance.LifetimeEndNativeTicks is { } end && instance.Gaps.HasFlag(ProcessEvidenceGaps.ExitNotWitnessed)
                ? $", replaced {Instant(clock, end)} with no exit seen"
                : string.Empty;
        return instance.Witness switch
        {
            ProcessWitness.Created => $"created {Instant(clock, instance.CreatedNativeTicks!.Value)}{exited}",
            ProcessWitness.Rundown => $"running at capture start{exited}",
            ProcessWitness.ExitOnly => $"running before its first record{exited}",
            ProcessWitness.ActivityOnly => "seen only in its own records",
            _ => instance.Witness.ToString(),
        };
    }

    /// <summary>Why a contribution could not be attributed to an instance, as a reader reads it.</summary>
    public static string Reason(ProcessBindingReason reason) => reason switch
    {
        ProcessBindingReason.NoOwner => "the record names no owner in its payload",
        ProcessBindingReason.BeforeFirstEvidence => "before the PID's first lifecycle record",
        ProcessBindingReason.BetweenInstances => "between one instance's exit and the next one's creation",
        ProcessBindingReason.AfterExit => "after the PID's last exit",
        ProcessBindingReason.NotAdmittedByPolicy => "a reused PID: a candidate the evidence policy does not admit",
        _ => reason.ToString(),
    };

    /// <summary>How the records of a group are bound, strongest first, e.g. "direct 2, correlated 78".</summary>
    public static string Bindings(IReadOnlyDictionary<RelationStrength, long> bindings) =>
        bindings.Count == 0
            ? "-"
            : string.Join(
                ", ",
                bindings
                    .OrderBy(entry => entry.Key)
                    .Select(entry => string.Create(
                        CultureInfo.CurrentCulture,
                        $"{entry.Key.ToString().ToLowerInvariant()} {entry.Value:N0}")));
}
