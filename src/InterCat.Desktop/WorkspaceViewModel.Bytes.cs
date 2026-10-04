using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Desktop.Theme;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// What a session's bytes are where a selection and its relationships are described (§5, R21). The overview sums no
/// bytes, so "bytes unknown" said nothing true of a real session: its records carry sizes that no view had read. Once a
/// selection or the relationship table needs them, the bytes of the scope the rows count are read through the same
/// per-scope read a byte ranking uses, and each relationship carries the bytes sent across it, each transfer counted once
/// at its sender. Until they arrive the inspector says they are being read.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    // Bytes read for a description when no byte ranking has read them for the scope; a byte ranking's own are preferred.
    private readonly RankingReads<SessionByteMeasures> selectionBytes = new((source, scope, cancellation) =>
        source.ByteMeasuresAsync(scope, cancellation));

    // The drawn graph sized by bytes, for the display and the bytes it was sized from, so a repaint never re-sums it.
    private (GraphDisplay Source, SessionByteMeasures Bytes, GraphDisplay Drawn)? weightedGraph;

    /// <summary>Completes when the most recent read of the bytes a description needs has applied, been superseded or failed.</summary>
    public Task SelectionBytesReady { get; private set; } = Task.CompletedTask;

    /// <summary>This generation's bytes for the scope the rows count, from a byte ranking or a description's own read.</summary>
    private SessionByteMeasures? DescribedBytes =>
        byteReads.For(CountedScope) is { } ranked && !byteReads.IsCarried(ranked) ? ranked
        : selectionBytes.For(CountedScope) is { } read && !selectionBytes.IsCarried(read) ? read
        : null;

    /// <summary>
    /// Whether this workspace reads its bytes when a description needs them: a real session with the directory to read
    /// them from. A view of a session without one keeps saying its bytes are unknown, since it has no way to know them.
    /// </summary>
    private bool ReadsBytes => realOverview && evidenceSource is not null;

    /// <summary>
    /// Whether a description on screen needs bytes: a selected process, group or set, the relationship table, or the
    /// channel a channel rung is focused on.
    /// </summary>
    private bool DescribesBytes => selectedProcess is not null || SelectedGroup is not null || HasMultiSelection || showTables
        || FocusedRealChannel is not null;

    /// <summary>
    /// What a description says where the scope's bytes are not known: that they are being read, that they could not be,
    /// or, while nothing on screen has asked for them, that they have not been read.
    /// </summary>
    private string UnreadBytes =>
        selectionBytes.Problem is not null || byteReads.Problem is not null ? "bytes could not be read"
        : selectionBytes.Reads(CountedScope) || byteReads.Reads(CountedScope) ? "reading bytes…"
        : "bytes not read";

    /// <summary>Reads the bytes of the scope the rows count when a description needs them and none are known.</summary>
    private void FollowDescribedBytes()
    {
        if (!ReadsBytes || disposed || !DescribesBytes || DescribedBytes is not null)
        {
            return;
        }

        SelectionBytesReady = FollowAsync(selectionBytes, ranks: false);
    }

    /// <summary>
    /// Rebuilds what states bytes once they are read, or once a read starts or fails: the relationship table, the
    /// inspector's summary and a channel rung's total.
    /// </summary>
    private void RefreshDescribedBytes()
    {
        relationships = RelationshipRows();
        OnPropertyChanged(nameof(Relationships));
        OnPropertyChanged(nameof(RelationshipTableScope));
        OnPropertyChanged(nameof(EvidenceSummary));
        OnPropertyChanged(nameof(LevelSummary));
    }

    /// <summary>
    /// The relationship table's rows, those the graph draws at this rung, with the bytes sent across each once they are
    /// read, or that none was sent or none of its sends measured a size; until the bytes are read, each says why they are
    /// not known yet.
    /// </summary>
    private IReadOnlyList<RelationshipRow> RelationshipRows()
    {
        WorkspaceSnapshot listed = ListedRelationships();
        if (!ReadsBytes)
        {
            return WorkspaceRowBuilder.Relationships(listed, ThemeResources.CurrentMode);
        }

        if (DescribedBytes is not { } bytes)
        {
            string unread = UnreadBytes;
            return WorkspaceRowBuilder.Relationships(listed, ThemeResources.CurrentMode, _ => unread);
        }

        ILookup<string, Channel> channels = listed.Channels.ToLookup(channel => channel.EdgeKey, StringComparer.Ordinal);
        return WorkspaceRowBuilder.Relationships(listed, ThemeResources.CurrentMode,
            edge => SentAcross(edge, channels[edge.Key], bytes).Phrase);
    }

    /// <summary>
    /// A channel rung's bytes, as its total states them: on a session whose bytes are read, what its two ends sent across
    /// it over the scope the rows count, each transfer counted once at its sender, as its relationship's row counts them.
    /// </summary>
    private string FocusedChannelBytes(Channel channel)
    {
        if (!ReadsBytes)
        {
            return "bytes unknown";
        }

        if (DescribedBytes is not { } bytes)
        {
            return UnreadBytes;
        }

        return Snapshot.Edges.FirstOrDefault(edge => edge.Key == channel.EdgeKey) is { } relationship
            ? SentAcross(relationship, [channel], bytes).Phrase
            : "nothing sent across";
    }

    /// <summary>
    /// The edges as a description states them: on a real session whose bytes are read, each carries the bytes sent across
    /// its channels by either end, each transfer counted once at its sender; otherwise as projected.
    /// </summary>
    private IReadOnlyList<CommunicationEdge> DescribedEdges(IEnumerable<CommunicationEdge> edges)
    {
        if (!ReadsBytes || DescribedBytes is not { } bytes)
        {
            return [.. edges];
        }

        Dictionary<string, SentAcrossTally> sent = SentAcrossEdges(bytes);
        return [.. edges.Select(edge => edge with { KnownBytes = sent.GetValueOrDefault(edge.Key).Known })];
    }

    /// <summary>
    /// What was sent across each relationship's channels by either end, each transfer counted once at its sender: the
    /// measured bytes, and how many sends measured a size or recorded none.
    /// </summary>
    private Dictionary<string, SentAcrossTally> SentAcrossEdges(SessionByteMeasures bytes)
    {
        ILookup<string, Channel> channels = Snapshot.Channels.ToLookup(channel => channel.EdgeKey, StringComparer.Ordinal);
        return Snapshot.Edges.ToDictionary(edge => edge.Key, edge => SentAcross(edge, channels[edge.Key], bytes),
            StringComparer.Ordinal);
    }

    /// <summary>What both ends of a relationship's channels sent across them, each transfer counted once at its sender.</summary>
    private static SentAcrossTally SentAcross(CommunicationEdge edge, IEnumerable<Channel> channels, SessionByteMeasures bytes)
    {
        long sent = 0, measured = 0, unmeasured = 0;
        foreach (Channel channel in channels)
        {
            foreach (ProcessInstanceId end in new[] { edge.SourceId, edge.TargetId }.Distinct())
            {
                if (bytes.ByChannelEnd.GetValueOrDefault(new ChannelEnd(channel.Key, end)) is { } ofEnd)
                {
                    sent = checked(sent + ofEnd.SentBytes);
                    measured += ofEnd.SentMeasured;
                    unmeasured += ofEnd.SentUnmeasured;
                }
            }
        }

        return new(sent, measured, unmeasured);
    }

    /// <summary>
    /// Sends across a relationship or channel: the bytes the measured ones carried, and how many recorded no size. None
    /// sent is not a zero-byte total, and sends that recorded no size are not nothing sent (R21).
    /// </summary>
    private readonly record struct SentAcrossTally(long Sent, long Measured, long Unmeasured)
    {
        public long? Known => Measured > 0 ? Sent : null;

        /// <summary>Several relationships' sends together, as one drawn mark carries them.</summary>
        public SentAcrossTally Plus(SentAcrossTally other) =>
            new(checked(Sent + other.Sent), Measured + other.Measured, Unmeasured + other.Unmeasured);

        /// <summary>
        /// What a drawing sized by these sends reads: their measured bytes; unknown (null) when none measured a size and some
        /// recorded none; zero when nothing was sent. A partly measured total is its measured part, a lower bound.
        /// </summary>
        public long? Magnitude => Measured > 0 ? Sent : Unmeasured > 0 ? null : 0;

        public string Phrase => Measured > 0 ? WorkspaceRowBuilder.DescribeSize(Sent) + " sent across"
            : Unmeasured > 0 ? "no size measured"
            : "nothing sent across";
    }

    /// <summary>
    /// A selection's own bytes, sent and received, as the inspector states them on a real session: what its records
    /// measured over the scope the rows count, "reading bytes…" until that is read, or why it could not be.
    /// </summary>
    private string SelectionBytes(IReadOnlyCollection<ProcessNode> members)
    {
        if (DescribedBytes is not { } bytes)
        {
            return selectionBytes.Problem is not null || byteReads.Problem is not null ? "bytes could not be read" : "reading bytes…";
        }

        TransportBytes sum = members.Aggregate(TransportBytes.None,
            (total, member) => total.Plus(bytes.ByProcess.GetValueOrDefault(member.Id) ?? TransportBytes.None));
        return WorkspaceRowBuilder.Directional(sum.SentBytes, sum.SentMeasured, sum.SentUnmeasured, "sent", "sends") + " · "
            + WorkspaceRowBuilder.Directional(sum.ReceivedBytes, sum.ReceivedMeasured, sum.ReceivedUnmeasured, "received", "receives");
    }

    /// <summary>
    /// The graph sized by the ranking's metric (§6.3): under a byte ranking whose bytes are shown, an edge carries the bytes
    /// sent across the relationships it draws, and a node the bytes its members' relationships carry, each relationship
    /// once; otherwise the graph as projected, sized by records.
    /// </summary>
    private GraphDisplay Weighted(GraphDisplay display)
    {
        if (!ReadsBytes || ShownMeasures is not SessionByteMeasures bytes || !RanksThisRung)
        {
            return display;
        }

        if (weightedGraph is { } cached && ReferenceEquals(cached.Source, display) && ReferenceEquals(cached.Bytes, bytes))
        {
            return cached.Drawn;
        }

        GraphDisplay drawn = WeighedBy(display, bytes);
        weightedGraph = (display, bytes, drawn);
        return drawn;
    }

    /// <summary>
    /// The graph sized by the bytes shown. Kept out of <see cref="Weighted"/>, which every repaint of the graph reads: a
    /// lambda's captured locals are allocated where the method begins, whether or not the lambda runs, so that read once
    /// allocated them on every frame (R11).
    /// </summary>
    private GraphDisplay WeighedBy(GraphDisplay display, SessionByteMeasures bytes)
    {
        // The bytes shown size the graph, an earlier publication's standing in included, as they rank the rows. A mark none
        // of whose sends measured a size, though some recorded none, is unmeasured and drawn so (§6.6), never as zero.
        Dictionary<string, SentAcrossTally> sent = SentAcrossEdges(bytes);
        return display.WithMagnitudes(
            edge => edge.Relationships.Aggregate(default(SentAcrossTally), (sum, key) => sum.Plus(sent.GetValueOrDefault(key)))
                .Magnitude,
            node =>
            {
                HashSet<ProcessInstanceId> members = [.. node.Members];
                return Snapshot.Edges
                    .Where(edge => members.Contains(edge.SourceId) || members.Contains(edge.TargetId))
                    .Aggregate(default(SentAcrossTally), (sum, edge) => sum.Plus(sent.GetValueOrDefault(edge.Key)))
                    .Magnitude;
            });
    }

}
