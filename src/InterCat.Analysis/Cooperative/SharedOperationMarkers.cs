using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// An operation's start as a capture recorded it (`contracts/sdk-v1.md` §3): its identity, the operation it was started under
/// where it names one - the record's related activity - and the record's instant in its own session time.
/// </summary>
public readonly record struct CooperativeOperationStart(Guid Operation, Guid? Parent, long SessionNanoseconds);

/// <summary>
/// The markers two captures share through the cooperative SDK's operations (§8.2, ADR-108): an operation one capture recorded
/// starting under an operation the other recorded starting. The SDK makes an operation's identity in the call that says its
/// start, before returning it, so no process holds the identity before its start was said, and a start under it is said
/// after its holder received it: each such pair is a record of one capture known to be taken after a record of the other,
/// by the SDK's own word rather than a measured delay. A message is no marker: its publication is said after its commit, so
/// a reader may consume it first.
/// </summary>
public static class SharedOperationMarkers
{
    /// <summary>Every operation start a session's records hold, each record's instant read in its session time.</summary>
    public static IReadOnlyList<CooperativeOperationStart> Starts(
        IReadOnlyList<SegmentReaderV1> segments,
        SourceClockDescriptor clock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var starts = new List<CooperativeOperationStart>();
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
            bool opened = false;
            SegmentColumnSlice identifiers = default, related = default, ticks = default;
            for (int row = 0; row < segment.RowCount; row++)
            {
                if ((Mechanism)mechanisms.UnsignedAt(row)!.Value != Mechanism.ApplicationSdk)
                {
                    continue;
                }

                // A record is the SDK's by its provider, as every derivation of the SDK's reads it.
                (Guid provider, ushort eventId, _, _) = segment.SchemaOf(row);
                if (provider != CooperativeSdk.ProviderId || eventId != CooperativeSdk.OperationStarted)
                {
                    continue;
                }

                if (!opened)
                {
                    identifiers = segment.Slice(SegmentColumnId.SourceIdentifier);
                    related = segment.Slice(SegmentColumnId.RelatedActivityId);
                    ticks = segment.Slice(SegmentColumnId.NativeTicks);
                    opened = true;
                }

                // A start that names no operation, or that its capture's clock cannot place, orders nothing.
                if (identifiers.GuidAt(row) is not { } operation || operation == Guid.Empty || ticks.SignedAt(row) is not { } native
                    || SourceClockMath.ConvertToSession(clock, new NativeTimestamp(clock.Id, clock.Encoding, native)).SessionTime is not { } at)
                {
                    continue;
                }

                Guid? parent = related.GuidAt(row) is { } named && named != Guid.Empty ? named : null;
                starts.Add(new(operation, parent, at.Nanoseconds));
            }
        }

        return starts;
    }

    /// <summary>
    /// The orderings two captures' starts give their clocks: each start of the member's under an operation the reference
    /// recorded starting followed that start, and each of the reference's under one the member recorded followed the member's.
    /// Where a capture recorded an operation's start more than once, its earliest is the one each pairing is held to, which
    /// no start under it can precede; pairs within one capture order nothing between two.
    /// </summary>
    public static IReadOnlyList<MarkerOrdering> Between(
        IReadOnlyList<CooperativeOperationStart> member,
        IReadOnlyList<CooperativeOperationStart> reference)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(reference);
        Dictionary<Guid, long> memberEarliest = Earliest(member);
        Dictionary<Guid, long> referenceEarliest = Earliest(reference);
        var orderings = new List<MarkerOrdering>();
        foreach (CooperativeOperationStart start in member)
        {
            if (start.Parent is { } parent && referenceEarliest.TryGetValue(parent, out long parentStart))
            {
                orderings.Add(new(start.SessionNanoseconds, parentStart, MarkerOrder.MemberAfter));
            }
        }

        foreach (CooperativeOperationStart start in reference)
        {
            if (start.Parent is { } parent && memberEarliest.TryGetValue(parent, out long parentStart))
            {
                orderings.Add(new(parentStart, start.SessionNanoseconds, MarkerOrder.ReferenceAfter));
            }
        }

        return orderings;
    }

    private static Dictionary<Guid, long> Earliest(IReadOnlyList<CooperativeOperationStart> starts)
    {
        var earliest = new Dictionary<Guid, long>();
        foreach (CooperativeOperationStart start in starts)
        {
            earliest[start.Operation] = earliest.TryGetValue(start.Operation, out long known) ? Math.Min(known, start.SessionNanoseconds)
                : start.SessionNanoseconds;
        }

        return earliest;
    }
}
