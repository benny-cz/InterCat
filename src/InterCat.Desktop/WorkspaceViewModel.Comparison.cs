using System.Globalization;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// What the window offers for comparing the time scope with another interval (`contracts/comparison-v1.md`, plan M12):
/// whether it can, the time a baseline is chosen within, the way to a process a comparison's row names, and a comparison
/// shown on the graph (§8e), its edges marked by how they changed until it is cleared or another session is shown.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    // The comparison the graph marks its edges by, and its marks as last worked out for a drawing.
    private IntervalComparison? comparisonShown;
    private (GraphDisplay For, GraphComparisonOverlay Overlay)? overlaid;

    /// <summary>
    /// The comparison the graph marks its edges by (`contracts/comparison-v1.md` §8e), its marks worked out once for each
    /// drawing - a group opened, hosts drawn on their own, a scope - so every drawing of the session is marked alike; null
    /// while none is shown.
    /// </summary>
    public GraphComparisonOverlay? ComparisonOverlay
    {
        get
        {
            if (comparisonShown is not { } shown) return null;
            if (overlaid is { } known && ReferenceEquals(known.For, graphDisplay)) return known.Overlay;
            GraphComparisonOverlay overlay = GraphComparisonOverlay.Of(shown, graphDisplay, wholeSnapshot.Edges, wholeSnapshot.HostLinks);
            overlaid = (graphDisplay, overlay);
            return overlay;
        }
    }

    /// <summary>Whether the graph marks its edges by a comparison, which its line beneath the header states and Clear removes.</summary>
    public bool ShowsComparison => comparisonShown is not null;

    /// <summary>
    /// What the graph marks, in words, beneath its header (R15): the intervals and what the rows are, how many edges went
    /// each way, those unmarked and the rows no edge draws - and where the session has grown since, that what is new has no
    /// mark. Empty while it marks none.
    /// </summary>
    public string ComparisonShownSaid => ComparisonOverlay is not { } overlay ? string.Empty
        : overlay.Summary(TimeBase, CultureInfo.CurrentCulture)
            + (evidenceSource is { } source && overlay.Comparison.Generation != source.Generation
                ? " It was read at an earlier generation of the session than the window shows, so what the session holds since has "
                    + "no mark; Compare with… reads it again."
                : string.Empty);

    /// <summary>
    /// Marks the graph's edges by <paramref name="comparison"/>, until it is cleared or another session is shown. False for a
    /// comparison of another session than the one shown, or the tour, whose graph no comparison reads.
    /// </summary>
    public bool ShowComparison(IntervalComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        if (evidenceSource is not { } source || comparison.SessionId != source.SessionId) return false;
        comparisonShown = comparison;
        overlaid = null;
        RaiseComparisonShown();
        return true;
    }

    /// <summary>Takes the graph's marks away.</summary>
    public void ClearComparison()
    {
        if (comparisonShown is null) return;
        comparisonShown = null;
        overlaid = null;
        RaiseComparisonShown();
    }

    /// <summary>The comparison the graph marks, which a newer generation of the session takes on; null while none is shown.</summary>
    public IntervalComparison? CarryComparison() => comparisonShown;

    /// <summary>Marks the graph by the comparison an earlier generation of the session showed, before this one is shown.</summary>
    public void AdoptComparison(IntervalComparison? comparison)
    {
        if (comparison is null || evidenceSource is not { } source || comparison.SessionId != source.SessionId) return;
        comparisonShown = comparison;
        overlaid = null;
    }

    /// <summary>
    /// How the comparison shown on the graph says <paramref name="row"/> changed, as the relationship table's equivalent of
    /// the edge's mark (R15): its relationship's row, or its edge's to other hosts; null for one it compares in no row, and
    /// while none is shown.
    /// </summary>
    public GraphComparisonMarked? ComparedOf(RelationshipRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return ComparisonOverlay?.Of(row.Key);
    }

    /// <summary>
    /// What an edge's card says of the comparison shown on the graph: how what it draws changed, as its mark does, or that
    /// it draws no row, so an edge without a mark is not read as unchanged. Null while none is shown.
    /// </summary>
    private string? ComparedLine(GraphDisplayEdge edge) => ComparisonOverlay is not { } overlay ? null
        : overlay.Edges.GetValueOrDefault(edge.Key) is { } marked
            ? "Compared: " + overlay.Describe(marked, CultureInfo.CurrentCulture)
            : "Compared: no row, no record in either interval, so no mark";

    private void RaiseComparisonShown()
    {
        OnPropertyChanged(nameof(ComparisonOverlay));
        OnPropertyChanged(nameof(ShowsComparison));
        OnPropertyChanged(nameof(ComparisonShownSaid));
    }

    /// <summary>
    /// Whether the time scope can be compared with another interval: a session's, never the tour's, whose scope is an
    /// interval - the brush, or the visible range while the timeline is zoomed (§6.4).
    /// </summary>
    public bool CanCompareIntervals => evidenceSource is not null && ScopeInterval is not null;

    /// <summary>
    /// Whether a saved comparison can open on what the window shows: a session, never the tour, whatever its time scope,
    /// since the comparison names its own interval (`contracts/comparison-v1.md` §8c).
    /// </summary>
    public bool CanOpenSavedComparison => evidenceSource is not null;

    /// <summary>
    /// The time a comparison's baseline is chosen within: the session's recording where its capture recorded a stop, which
    /// its whole-session rates divide by (metrics-v1 §7), or else the span of its records.
    /// </summary>
    public TimeRange ComparableTime => wholeSnapshot.Recording ?? wholeSnapshot.Extent;

    /// <summary>Whether <see cref="ComparableTime"/> is the recording, rather than the span of its records.</summary>
    public bool ComparesWithinRecording => wholeSnapshot.Recording is not null;

    /// <summary>
    /// Opens a process's rung from wherever the ladder is, through its group, as a comparison's row of its connections to
    /// another host opens it: the breadcrumb states every rung, Esc climbs each, and the inspector lists the hosts it
    /// reached. False for a process the session does not hold.
    /// </summary>
    public bool OpenProcessRung(ProcessInstanceId processId) =>
        wholeSnapshot.Processes.FirstOrDefault(process => process.Id == processId) is { } process
        && DescendAlong([process.GroupKey, process.Id.ToString()], selectedInterval);

    /// <summary>
    /// Opens an executable's rung, its processes listed, from wherever the ladder is, as a comparison's row by executable
    /// opens it. False where the window groups processes otherwise, or no process of the session ran the executable.
    /// </summary>
    public bool OpenExecutableRung(string groupKey) =>
        WhyNoExecutableRung(groupKey) is null && DescendAlong([groupKey], selectedInterval);

    /// <summary>
    /// Opens what a comparison's row compares: a relationship as a double click on its edge opens it, a process's
    /// connections to other hosts by the process's rung, and an executable's by its rung - of a pair of executables, the
    /// first the session ran. False where the row names nothing the window can open.
    /// </summary>
    public bool OpenCompared(ComparedRelationship row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row switch
        {
            { Relationship: { } relationship } => OpenRelationship(relationship.Key),
            { Host: { } host } => OpenProcessRung(host.Link.Process),
            { Executables: { } pair } => OpenExecutableRung(WhyNoExecutableRung(pair.Source) is null ? pair.Source : pair.Target),
            { ExecutableHost: { } reach } => OpenExecutableRung(reach.Executable),
            _ => false,
        };
    }

    /// <summary>
    /// Why the window cannot open what a comparison's row compares, said where Show in the window is offered: an
    /// executable's rung where the window groups processes by terminal session, or where no process of this session ran
    /// it, as of another capture. Null where it can.
    /// </summary>
    public string? WhyNotShown(ComparedRelationship row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row switch
        {
            { Executables: { } pair } => WhyNoExecutableRung(pair.Source) is null ? null : WhyNoExecutableRung(pair.Target),
            { ExecutableHost: { } reach } => WhyNoExecutableRung(reach.Executable),
            _ => null,
        };
    }

    /// <summary>Why the ladder has no rung of the executable a group key names; null where it has.</summary>
    private string? WhyNoExecutableRung(string groupKey) =>
        Grouping != LaneGrouping.Executable
            ? "The window groups processes by terminal session, so it has no executable's rung to open; group them by "
                + "executable to open it."
            : wholeSnapshot.Groups.Any(group => group.Key == groupKey)
                ? null
                : "No process of the session shown ran this executable.";
}
