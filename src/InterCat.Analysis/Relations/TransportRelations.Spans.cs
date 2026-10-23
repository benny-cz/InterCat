using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>A record's place in the canonical order (`entities-v1` §3): its native reading, raw locator and fact key.</summary>
public readonly record struct TransportPosition(long Ticks, uint Stream, uint Epoch, ulong Ordinal, ulong FactHigh, ulong FactLow)
    : IComparable<TransportPosition>
{
    public static bool operator <(TransportPosition left, TransportPosition right) => left.CompareTo(right) < 0;

    public static bool operator <=(TransportPosition left, TransportPosition right) => left.CompareTo(right) <= 0;

    public static bool operator >(TransportPosition left, TransportPosition right) => left.CompareTo(right) > 0;

    public static bool operator >=(TransportPosition left, TransportPosition right) => left.CompareTo(right) >= 0;

    public int CompareTo(TransportPosition other)
    {
        int compared = Ticks.CompareTo(other.Ticks);
        compared = compared != 0 ? compared : Stream.CompareTo(other.Stream);
        compared = compared != 0 ? compared : Epoch.CompareTo(other.Epoch);
        compared = compared != 0 ? compared : Ordinal.CompareTo(other.Ordinal);
        compared = compared != 0 ? compared : FactHigh.CompareTo(other.FactHigh);
        return compared != 0 ? compared : FactLow.CompareTo(other.FactLow);
    }
}

/// <summary>
/// The records of one connection or datagram-flow end that a chunk's tiles hold within one incarnation of it, as the
/// derivation that tiled them bound them (`contracts/tile-index-v1.md` §6): the end - its protocol, address family, own
/// endpoint and the remote one it names, as a relation index keys it - and the first and last of those records in the
/// canonical order. A later derivation binds them alike, to one channel or to none, wherever one incarnation of the end
/// holds both.
/// </summary>
public readonly record struct TransportEndSpan(
    Mechanism Protocol,
    byte Family,
    UInt128 LocalAddress,
    ushort LocalPort,
    UInt128 RemoteAddress,
    ushort RemotePort,
    TransportPosition First,
    TransportPosition Last);

public sealed partial class TransportRelationIndex
{
    /// <summary>Gathers, under this derivation, the end spans of the rows a chunk's tiles hold (<see cref="EndSpans"/>).</summary>
    public EndSpans GatherEndSpans() => new(this);

    /// <summary>
    /// The channel the records of <paramref name="span"/> belong to under this derivation, where one incarnation of its end
    /// holds both its first and its last record, and so every record between: true with that incarnation's channel, or -1
    /// where its partner is undecided and it is counted as none. False where this derivation holds no such end, or where
    /// a lifecycle record it holds - a connect, an accept or a disconnect read later than the records it falls among -
    /// divides the span.
    /// </summary>
    public bool TryChannelOf(TransportEndSpan span, out int channel)
    {
        channel = -1;
        if (!ends.TryGetValue(KeyOf(span), out EndTimeline? end))
        {
            return false;
        }

        // An end no cut divides holds every record in one incarnation; otherwise both of the span's ends must fall in one.
        Incarnation incarnation = end.Whole ?? end.At(Of(span.First));
        if (end.Whole is null && !ReferenceEquals(incarnation, end.At(Of(span.Last))))
        {
            return false;
        }

        channel = incarnation.Channel;
        return true;
    }

    private static EndKey KeyOf(TransportEndSpan span) =>
        new((byte)span.Protocol, span.Family, span.LocalAddress, span.LocalPort, span.RemoteAddress, span.RemotePort);

    private static Position Of(TransportPosition at) => new(at.Ticks, at.Stream, at.Epoch, at.Ordinal, at.FactHigh, at.FactLow);

    /// <summary>
    /// The end spans of a chunk's records, gathered segment by segment as its tiles are written (`contracts/tile-index-v1.md`
    /// §6): each row the tiles hold that names an end is given its span - its end, and the incarnation of it the row
    /// belongs to under the derivation that gathers them - numbered from 1 in the order the rows first meet them, and each
    /// span stretches over its rows' first and last place in the canonical order.
    /// </summary>
    public sealed class EndSpans
    {
        private readonly TransportRelationIndex relations;
        private readonly Dictionary<Incarnation, int> places = new(ReferenceEqualityComparer.Instance);
        private readonly List<(EndKey Key, Position First, Position Last)> spans = [];

        internal EndSpans(TransportRelationIndex relations) => this.relations = relations;

        /// <summary>How many spans the rows given so far meet.</summary>
        public int Count => spans.Count;

        /// <summary>The spans, in the order the rows given so far first met them.</summary>
        public TransportEndSpan[] Spans() =>
        [
            .. spans.Select(span => new TransportEndSpan((Mechanism)span.Key.Protocol, span.Key.Family, span.Key.LocalAddress,
                span.Key.LocalPort, span.Key.RemoteAddress, span.Key.RemotePort, Of(span.First), Of(span.Last))),
        ];

        /// <summary>
        /// Gives each row of <paramref name="segment"/> that <paramref name="held"/> marks its span's number in
        /// <paramref name="keys"/>, and 0 to a row naming no end - of another mechanism, or with an incomplete endpoint - and
        /// to a row not held. Every row named must have an end this derivation holds: the derivation read it.
        /// </summary>
        public void Add(SegmentReaderV1 segment, ReadOnlySpan<bool> held, Span<uint> keys)
        {
            ArgumentNullException.ThrowIfNull(segment);
            RequireRows(segment, keys.Length);
            RequireRows(segment, held.Length);
            var columns = new EndColumns(segment);
            for (int row = 0; row < segment.RowCount; row++)
            {
                keys[row] = 0;
                if (!held[row] || !Relates(columns.MechanismAt(row)) || columns.KeyAt(row) is not { } key)
                {
                    continue;
                }

                if (!relations.ends.TryGetValue(key, out EndTimeline? end))
                {
                    throw new InvalidOperationException("A row names an end the derivation gathering its span does not hold.");
                }

                Position position = columns.PositionAt(row);
                Incarnation incarnation = end.Whole ?? end.At(position);
                if (places.TryGetValue(incarnation, out int place))
                {
                    (EndKey named, Position first, Position last) = spans[place];
                    spans[place] = (named, position.CompareTo(first) < 0 ? position : first, position.CompareTo(last) > 0 ? position : last);
                }
                else
                {
                    place = spans.Count;
                    places[incarnation] = place;
                    spans.Add((key, position, position));
                }

                keys[row] = (uint)place + 1;
            }
        }

        private static TransportPosition Of(Position at) => new(at.Ticks, at.Stream, at.Epoch, at.Ordinal, at.FactHigh, at.FactLow);
    }
}
