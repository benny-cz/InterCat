using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>One mechanism's coverage over a scope, and the fact that decided it (`coverage-v1` §4).</summary>
public sealed record MechanismCoverage(Mechanism Mechanism, CoverageState State, string Reason);

/// <summary>
/// Coverage states from a session's coverage ledger (`contracts/coverage-v1.md` §4): what an absence of records can and
/// cannot mean (R21). The rule reads the ledger's facts only. A legacy generation publishes no ledger, and every state
/// it is asked for is <see cref="CoverageState.UnknownCoverage"/>.
/// </summary>
public static class SessionCoverage
{
    /// <summary>The rule these states follow; results name it beside the states they carry.</summary>
    public const string Rule = "coverage-v1";

    /// <summary>Every mechanism's coverage over an interval, or over every epoch when none is given.</summary>
    public static IReadOnlyList<MechanismCoverage> ByMechanism(CoverageLedgerV1? ledger, TimeRange? interval = null) =>
        [.. Enum.GetValues<Mechanism>().Select(mechanism => Of(ledger, mechanism, interval))];

    /// <summary>One mechanism's coverage over an interval, or over every epoch when none is given.</summary>
    public static MechanismCoverage Of(CoverageLedgerV1? ledger, Mechanism mechanism, TimeRange? interval = null)
    {
        if (!Enum.IsDefined(mechanism))
        {
            throw new ArgumentOutOfRangeException(nameof(mechanism));
        }

        if (ledger is null)
        {
            return new(mechanism, CoverageState.UnknownCoverage, "this generation publishes no coverage ledger");
        }

        ledger.Validate();

        List<CoverageEpochV1> spanned =
        [
            .. ledger.Epochs.Where(epoch => interval is not { } range
                || (epoch.FirstDeliveredNativeTicks is { } first
                    && epoch.LastDeliveredNativeTicks is { } last
                    && range.StartTicks <= last
                    && range.EndTicks > first)),
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

    /// <summary>The worst of several states on the ordered lattice; nothing to span is unknown.</summary>
    public static CoverageState Worst(IEnumerable<CoverageState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        return states.DefaultIfEmpty(CoverageState.UnknownCoverage).Max();
    }

    private static MechanismCoverage StateIn(CoverageEpochV1 epoch, Mechanism mechanism)
    {
        HashSet<(Guid, int, int)> descriptors =
        [
            .. epoch.Collected
                .Where(descriptor => descriptor.Mechanism == mechanism)
                .Select(descriptor => (descriptor.ProviderId, descriptor.EventId, descriptor.Version)),
        ];
        if (descriptors.Count == 0)
        {
            return new(mechanism, CoverageState.NotCollected, "no admitted descriptor records it");
        }

        List<CoverageDeliveryV1> deliveries =
        [
            .. epoch.Deliveries.Where(delivery => delivery is { EventId: { } eventId, Version: { } version }
                && descriptors.Contains((delivery.ProviderId, eventId, version))),
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
            .. epoch.Losses.Where(loss => loss.Lost > 0).OrderBy(loss => loss.Layer).Select(loss => Describe(loss, epoch.Acquisition)),
        ];
        HashSet<(Guid, int, int)> allCollected =
        [
            .. epoch.Collected.Select(descriptor => (descriptor.ProviderId, descriptor.EventId, descriptor.Version)),
        ];
        long unassignedUndecodable = epoch.Deliveries
            .Where(delivery => delivery is { EventId: { } eventId, Version: { } version }
                && !allCollected.Contains((delivery.ProviderId, eventId, version)))
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
        foreach (CoverageEpochV1 epoch in epochs.OrderBy(epoch => epoch.FirstDeliveredNativeTicks))
        {
            if (epoch.FirstDeliveredNativeTicks > covered)
            {
                return false;
            }

            covered = Int128.Max(covered, (Int128)epoch.LastDeliveredNativeTicks!.Value + 1);
        }

        return covered >= range.EndTicks;
    }

    private static string Describe(CoverageLossV1 loss, CoverageAcquisition acquisition) => loss.Layer switch
    {
        LossLayer.SourceSession => acquisition == CoverageAcquisition.EtlImport
            ? $"the file reported {Count(loss.Lost, "lost event")}, which may be any mechanism's"
            : $"the session reported {Count(loss.Lost, "lost event")}, which may be any mechanism's",
        LossLayer.ConsumerBuffers => $"the consumer lost {Count(loss.Lost, "buffer")} of unknown size",
        LossLayer.CallbackQueue => $"InterCat's full queue dropped {Count(loss.Lost, "record")}",
        _ => $"{Count(loss.Lost, "admitted record")} could not be stored",
    };

    private static string Count(long count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count:N0} {noun}{(count == 1 ? string.Empty : "s")}");
}
