using System.Globalization;
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

    /// <summary>Whether a description on screen needs bytes: a selected process, group or set, or the relationship table.</summary>
    private bool DescribesBytes => selectedProcess is not null || SelectedGroup is not null || HasMultiSelection || showTables;

    /// <summary>Reads the bytes of the scope the rows count when a description needs them and none are known.</summary>
    private void FollowDescribedBytes()
    {
        if (!ReadsBytes || disposed || !DescribesBytes || DescribedBytes is not null)
        {
            return;
        }

        SelectionBytesReady = FollowAsync(selectionBytes);
    }

    /// <summary>Rebuilds what states bytes once they are read: the relationship table and the inspector's summary.</summary>
    private void RefreshDescribedBytes()
    {
        relationships = RelationshipRows();
        OnPropertyChanged(nameof(Relationships));
        OnPropertyChanged(nameof(EvidenceSummary));
    }

    /// <summary>The relationship table's rows, with the bytes sent across each once they are read.</summary>
    private IReadOnlyList<RelationshipRow> RelationshipRows() => WorkspaceRowBuilder.Relationships(
        Snapshot with { Edges = DescribedEdges(Snapshot.Edges) },
        ThemeResources.CurrentMode,
        ReadsBytes);

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

        ILookup<string, Channel> channels = Snapshot.Channels.ToLookup(channel => channel.EdgeKey, StringComparer.Ordinal);
        return [.. edges.Select(edge => edge with { KnownBytes = SentAcross(edge, channels[edge.Key], bytes) })];
    }

    private static long? SentAcross(CommunicationEdge edge, IEnumerable<Channel> channels, SessionByteMeasures bytes)
    {
        long sent = 0, measured = 0;
        foreach (Channel channel in channels)
        {
            foreach (ProcessInstanceId end in new[] { edge.SourceId, edge.TargetId }.Distinct())
            {
                if (bytes.ByChannelEnd.GetValueOrDefault(new ChannelEnd(channel.Key, end)) is { } ofEnd)
                {
                    sent = checked(sent + ofEnd.SentBytes);
                    measured += ofEnd.SentMeasured;
                }
            }
        }

        return measured > 0 ? sent : null;
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

        ProcessBytes sum = members.Aggregate(ProcessBytes.None,
            (total, member) => total.Plus(bytes.ByProcess.GetValueOrDefault(member.Id) ?? ProcessBytes.None));
        return Directional(sum.SentBytes, sum.SentMeasured, sum.SentUnmeasured, "sent", "sends") + " · "
            + Directional(sum.ReceivedBytes, sum.ReceivedMeasured, sum.ReceivedUnmeasured, "received", "receives");
    }

    /// <summary>One direction's bytes in words: a measured sum, sizes not recorded, or nothing in that direction (R21).</summary>
    private static string Directional(long value, long measured, long unmeasured, string verb, string records) =>
        measured > 0
            ? WorkspaceRowBuilder.DescribeSize(value) + " " + verb
                + (unmeasured > 0 ? string.Create(CultureInfo.CurrentCulture, $" ({unmeasured:N0} {records} unmeasured)") : string.Empty)
            : unmeasured > 0
                ? string.Create(CultureInfo.CurrentCulture, $"{unmeasured:N0} {records} unmeasured")
                : "nothing " + verb;
}
