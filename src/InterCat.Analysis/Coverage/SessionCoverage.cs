using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>One mechanism's coverage over a scope, and the fact that decided it (`coverage-v2` §4).</summary>
public sealed record MechanismCoverage(Mechanism Mechanism, CoverageState State, string Reason);

/// <summary>
/// Coverage states from a session's coverage ledger (`contracts/coverage-v2.md` §4): what an absence of records can and
/// cannot mean (R21). The rule reads the ledger's facts only. A legacy generation publishes no ledger, and every state
/// it is asked for is <see cref="CoverageState.UnknownCoverage"/>.
/// </summary>
public static class SessionCoverage
{
    /// <summary>The rule these states follow; results name it beside the states they carry.</summary>
    public const string Rule = "coverage-v2";

    /// <summary>Every mechanism's coverage over an interval, or over every epoch when none is given.</summary>
    public static IReadOnlyList<MechanismCoverage> ByMechanism(CoverageLedgerV1? ledger, TimeRange? interval = null) =>
        ForMechanisms(ledger, Enum.GetValues<Mechanism>(), interval);

    /// <summary>Several mechanisms over one scope, validating the ledger once and preserving the caller's order.</summary>
    public static IReadOnlyList<MechanismCoverage> ForMechanisms(
        CoverageLedgerV1? ledger,
        IEnumerable<Mechanism> mechanisms,
        TimeRange? interval = null)
    {
        ArgumentNullException.ThrowIfNull(mechanisms);
        Mechanism[] requested = [.. mechanisms];
        if (requested.Any(mechanism => !Enum.IsDefined(mechanism)))
        {
            throw new ArgumentOutOfRangeException(nameof(mechanisms));
        }

        ledger?.Validate();
        return [.. requested.Select(mechanism => OfValidated(ledger, mechanism, interval))];
    }

    /// <summary>One mechanism's coverage over an interval, or over every epoch when none is given.</summary>
    public static MechanismCoverage Of(CoverageLedgerV1? ledger, Mechanism mechanism, TimeRange? interval = null)
    {
        if (!Enum.IsDefined(mechanism))
        {
            throw new ArgumentOutOfRangeException(nameof(mechanism));
        }

        ledger?.Validate();
        return OfValidated(ledger, mechanism, interval);
    }

    private static MechanismCoverage OfValidated(CoverageLedgerV1? ledger, Mechanism mechanism, TimeRange? interval)
    {
        if (ledger is null)
        {
            return new(mechanism, CoverageState.UnknownCoverage, "this generation publishes no coverage ledger");
        }

        List<CoverageEpochV1> spanned =
        [
            .. ledger.Epochs.Where(epoch => interval is not { } range
                || (Bounds(epoch) is { } bounds && range.StartTicks <= bounds.Last && range.EndTicks > bounds.First)),
        ];
        if (spanned.Count == 0 || !Spans(spanned, interval))
        {
            return new(mechanism, CoverageState.UnknownCoverage, "outside the readings the capture's sources delivered");
        }

        // The worst epoch decides, and its fact is the reason: a state is never averaged across epochs (§10.3).
        return spanned
            .Select(epoch => StateIn(epoch, mechanism))
            .OrderByDescending(coverage => coverage.State)
            .First();
    }

    /// <summary>
    /// The capture's own state over each native interval, whatever was observed in it: the worst state of every mechanism
    /// each spanned epoch collected, or <see cref="CoverageState.NotCollected"/> for an epoch that collected nothing. An
    /// interval outside the delivered readings, and every interval of a generation without a ledger, is unknown. Each
    /// epoch is judged once, so a fine grid of intervals costs one pass over the epochs per interval.
    /// </summary>
    public static IReadOnlyList<CoverageState> CaptureStates(CoverageLedgerV1? ledger, IReadOnlyList<TimeRange?> nativeIntervals)
    {
        ArgumentNullException.ThrowIfNull(nativeIntervals);
        var states = new CoverageState[nativeIntervals.Count];
        Array.Fill(states, CoverageState.UnknownCoverage);
        if (ledger is null)
        {
            return states;
        }

        ledger.Validate();
        CoverageState[] judged = [.. ledger.Epochs.Select(CaptureStateIn)];
        for (int index = 0; index < states.Length; index++)
        {
            if (nativeIntervals[index] is not { } range)
            {
                continue;
            }

            List<int> spanned = [];
            for (int epoch = 0; epoch < ledger.Epochs.Count; epoch++)
            {
                if (Bounds(ledger.Epochs[epoch]) is { } bounds && range.StartTicks <= bounds.Last && range.EndTicks > bounds.First)
                {
                    spanned.Add(epoch);
                }
            }

            if (spanned.Count > 0 && Spans([.. spanned.Select(epoch => ledger.Epochs[epoch])], range))
            {
                states[index] = Worst(spanned.Select(epoch => judged[epoch]));
            }
        }

        return states;
    }

    /// <summary>
    /// The capture's own state over every epoch, whatever was observed: the worst state of every mechanism each epoch
    /// collected, as <see cref="CaptureStates"/> judges one interval. A process can make any record the capture collects,
    /// so this is the coverage of its count over the whole session (§10.3). Without a ledger, or an epoch, it is unknown.
    /// </summary>
    public static CoverageState Capture(CoverageLedgerV1? ledger)
    {
        if (ledger is null)
        {
            return CoverageState.UnknownCoverage;
        }

        ledger.Validate();
        return Worst(ledger.Epochs.Select(CaptureStateIn));
    }

    /// <summary>One epoch's own state: the worst of every mechanism it collected, or not collected when it collected none.</summary>
    private static CoverageState CaptureStateIn(CoverageEpochV1 epoch) => Worst(epoch.Collected
        .Select(descriptor => descriptor.Mechanism)
        .Distinct()
        .Select(mechanism => StateIn(epoch, mechanism).State)
        .DefaultIfEmpty(CoverageState.NotCollected));

    /// <summary>
    /// One mechanism's coverage over a scope as a sentence states it, in the words every layer uses (R5, R21): "TCP was
    /// covered over the selected interval: …", "RPC has a partial gap over this scope, not extrapolated: …", "UDP's
    /// coverage over this scope is unknown: …", each ending with the fact that decided it.
    /// </summary>
    public static string Sentence(MechanismCoverage coverage, string scope)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentException.ThrowIfNullOrEmpty(scope);
        string name = MechanismText.InSentence(coverage.Mechanism);
        string said = coverage.State switch
        {
            CoverageState.Covered => $"{name} was covered over {scope}",
            CoverageState.ReducedFidelity => $"{name} was captured at reduced fidelity over {scope}",
            CoverageState.PartialGap => $"{name} has a partial gap over {scope}, not extrapolated",
            CoverageState.NotCollected => $"{name} was not collected over {scope}",
            _ => $"{name}'s coverage over {scope} is unknown",
        };
        return string.Concat(char.ToUpperInvariant(said[0]).ToString(), said[1..], ": ", coverage.Reason, ".");
    }

    /// <summary>The worst of several states on the ordered lattice; nothing to span is unknown.</summary>
    public static CoverageState Worst(IEnumerable<CoverageState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        return states.DefaultIfEmpty(CoverageState.UnknownCoverage).Max();
    }

    private static MechanismCoverage StateIn(CoverageEpochV1 epoch, Mechanism mechanism)
    {
        HashSet<(Guid, int, int, int?)> descriptors =
        [
            .. epoch.Collected
                .Where(descriptor => descriptor.Mechanism == mechanism)
                .Select(descriptor => (descriptor.ProviderId, descriptor.EventId, descriptor.Version, descriptor.Opcode)),
        ];
        if (descriptors.Count == 0)
        {
            return new(mechanism, CoverageState.NotCollected, "no admitted descriptor records it");
        }

        List<CoverageDeliveryV1> deliveries =
        [
            .. epoch.Deliveries.Where(delivery => delivery is { EventId: { } eventId, Version: { } version }
                && descriptors.Contains((delivery.ProviderId, eventId, version, delivery.Opcode))),
        ];
        long delivered = deliveries.Sum(delivery => delivery.Delivered);
        string admitted = Count(descriptors.Count, "admitted descriptor");
        if (delivered == 0 && epoch.Acquisition == CoverageAcquisition.EtlImport)
        {
            return new(
                mechanism,
                CoverageState.UnknownCoverage,
                $"its {admitted} delivered nothing, and a file cannot show whether its session recorded them");
        }

        List<string> losses =
        [
            .. epoch.Losses.Where(loss => loss.Lost > 0).OrderBy(loss => loss.Layer)
                .Select(loss => CoverageLedgerText.Loss(loss, epoch.Acquisition)),
        ];
        HashSet<(Guid, int, int, int?)> allCollected =
        [
            .. epoch.Collected.Select(descriptor => (descriptor.ProviderId, descriptor.EventId, descriptor.Version, descriptor.Opcode)),
        ];
        long unassignedUndecodable = epoch.Deliveries
            .Where(delivery => delivery is { EventId: { } eventId, Version: { } version }
                && !allCollected.Contains((delivery.ProviderId, eventId, version, delivery.Opcode)))
            .Sum(delivery => delivery.Undecodable?.Values.Sum() ?? 0);
        if (unassignedUndecodable > 0)
        {
            losses.Add($"{Count(unassignedUndecodable, "undecodable record")} had no admitted mechanism; it may affect this one");
        }
        long undecodable = deliveries.Sum(delivery => delivery.Undecodable?.Values.Sum() ?? 0);
        if (undecodable > 0)
        {
            losses.Add(string.Create(CultureInfo.InvariantCulture, $"{undecodable:N0} of its records could not be decoded"));
        }

        if (losses.Count > 0)
        {
            return new(mechanism, CoverageState.PartialGap, string.Join("; ", losses));
        }

        return new(
            mechanism,
            CoverageState.Covered,
            delivered == 0
                ? $"its {admitted} delivered nothing while the session recorded them, and nothing was reported lost"
                : $"{Count(delivered, "record")} from its {admitted}, and nothing was reported lost");
    }

    /// <summary>Whether the epochs' readings hold the whole interval; with no interval, the epochs are the scope.</summary>
    private static bool Spans(IReadOnlyList<CoverageEpochV1> epochs, TimeRange? interval)
    {
        if (interval is not { } range)
        {
            return true;
        }

        Int128 covered = range.StartTicks;
        foreach ((long first, long last) in epochs.Select(Bounds).OfType<(long First, long Last)>().OrderBy(bounds => bounds.First))
        {
            if (first > covered)
            {
                return false;
            }

            covered = Int128.Max(covered, (Int128)last + 1);
        }

        return covered >= range.EndTicks;
    }

    /// <summary>
    /// The readings an epoch speaks for (`coverage-v2` §2): from the earlier of its first delivered and its recorded
    /// start to the later of its last delivered and its recorded stop; null when it states neither.
    /// </summary>
    private static (long First, long Last)? Bounds(CoverageEpochV1 epoch) =>
        (epoch.FirstDeliveredNativeTicks, epoch.RecordedFromNativeTicks) switch
        {
            (null, null) => null,
            _ => (Math.Min(epoch.FirstDeliveredNativeTicks ?? long.MaxValue, epoch.RecordedFromNativeTicks ?? long.MaxValue),
                Math.Max(epoch.LastDeliveredNativeTicks ?? long.MinValue, epoch.RecordedToNativeTicks ?? long.MinValue)),
        };

    private static string Count(long count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count:N0} {noun}{(count == 1 ? string.Empty : "s")}");
}
