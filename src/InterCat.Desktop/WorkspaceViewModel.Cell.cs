using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Desktop.Theme;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Desktop;

/// <summary>
/// The lane a timeline cell lies in (§6.8): the machine row when every part is null, or one mechanism's lane at the
/// machine rung, one process's lane among a group's, one source direction's row of a process, or one end of a channel.
/// </summary>
public sealed record TimelineCellLane(
    Mechanism? Mechanism = null, ProcessInstanceId? Owner = null, Direction? Direction = null, int? End = null,
    bool Folded = false);

/// <summary>
/// A timeline cell's own explanation (§6.8: every visual claim is one action from the rule, version and coverage that
/// produced it). A click on a cell makes the cell's interval the analysis interval - the lane's own, which in a group's
/// lanes counted coarser than the view is wider than the machine column beneath it - and the inspector then says,
/// beneath the time scope, which records the cell counts and by which rule, what its bar plots, how its column was cut
/// and what the capture covered of it. An interval a step or the interval table chose is explained as a cell of the lane
/// they follow, or of the machine row, and a brushed range, which is no cell, is not.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    private static readonly TimelineCellLane MachineRow = new();

    // The lane a click chose the analysis interval in, until another interval, another lane or another rung is chosen.
    private TimelineCellLane? chosenCell;
    private bool choosingCell;

    /// <summary>
    /// Chooses the cell a click landed on: its interval becomes the analysis interval, and the inspector explains it as a
    /// cell of its lane - a mechanism's, a process's among a group's, a source direction's or a channel end's, or else
    /// the machine row's.
    /// </summary>
    public void ChooseTimelineCell(TimelineBucket bucket, Mechanism? lane = null, ProcessNode? ownerLane = null,
        Direction? directionLane = null, ChannelEndTimelineLane? endLane = null, bool foldedLane = false)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        choosingCell = true;
        try
        {
            chosenCell = new TimelineCellLane(lane, ownerLane?.Id, directionLane, endLane?.End, foldedLane);
            SelectInterval(bucket.Interval);
        }
        finally
        {
            choosingCell = false;
        }

        RaiseCellExplanationChanged();
    }

    /// <summary>
    /// How the cell the analysis interval is was counted: the records of its lane it holds and the rule that bound or
    /// paired them, what its bar plots, the column it was counted in and the capture's coverage over it. Empty when the
    /// interval is no cell of the lane it is explained in - a brushed range - and in the tour, which no rule derived.
    /// </summary>
    public string CellExplanation => realOverview && selectedInterval is { } interval
        && CellOf(ExplainedLane, interval) is { } cell
            ? ExplainCell(ExplainedLane, cell.Bucket, cell.Zoomed)
            : string.Empty;

    public bool HasCellExplanation => CellExplanation.Length > 0;

    /// <summary>
    /// The lane the analysis interval is explained as a cell of: the one a click chose it in, until another interval or
    /// another lane is chosen; else the selected lane, which a step and the interval table follow; else the machine row.
    /// </summary>
    private TimelineCellLane ExplainedLane => chosenCell ?? SelectedCellLane ?? MachineRow;

    /// <summary>The lane selected where the rung draws it: a mechanism's, a process's, a source direction's or an end's.</summary>
    private TimelineCellLane? SelectedCellLane =>
        ShowsMechanismLanes && SelectedTimelineMechanism is { } mechanism ? new(Mechanism: mechanism)
        : ShowsProcessLanes && selectedProcess is { } process && HasProcessLane(process.Id) ? new(Owner: process.Id)
        : ShowsDirectionLanes && SelectedTimelineDirection is { } direction ? new(Direction: direction)
        : ShowsChannelEndLanes && selectedChannelEnd is { } end ? new(End: end)
        : null;

    private bool HasProcessLane(ProcessInstanceId process)
    {
        foreach (ProcessTimelineLane lane in processLaneDisplay)
        {
            if (lane.ProcessId == process) return true;
        }

        return false;
    }

    /// <summary>
    /// The lane's bucket whose half-open interval is <paramref name="interval"/>, as the timeline draws it, and whether it
    /// is the zoomed view's own count; null when the rung does not draw the lane or the interval is no cell of it.
    /// </summary>
    private (TimelineBucket Bucket, bool Zoomed)? CellOf(TimelineCellLane lane, TimeRange interval) =>
        CellWhere(lane, bucket => bucket.Interval == interval);

    /// <summary>The lane's bucket holding <paramref name="ticks"/>, as <see cref="CellOf"/> finds one by its interval.</summary>
    private (TimelineBucket Bucket, bool Zoomed)? CellAt(TimelineCellLane lane, long ticks) =>
        CellWhere(lane, bucket => bucket.Interval.StartTicks <= ticks && ticks < bucket.Interval.EndTicks);

    private (TimelineBucket Bucket, bool Zoomed)? CellWhere(TimelineCellLane lane, Func<TimelineBucket, bool> holds)
    {
        if (lane.Mechanism is { } mechanism)
        {
            // The lanes draw the zoom's own count only once it holds every lane, and the overview's until then.
            return !ShowsMechanismLanes ? null
                : Find(HasCompleteLaneDetail
                        ? timelineDetail!.MechanismLanes.FirstOrDefault(candidate => candidate.Mechanism == mechanism)?.Buckets
                        : null, holds) is { } zoomed
                    ? (zoomed, true)
                : Find(Snapshot.MechanismLanes.FirstOrDefault(candidate => candidate.Mechanism == mechanism)?.Buckets, holds) is { } whole
                    ? (whole, false)
                : null;
        }

        IReadOnlyList<TimelineBucket>? buckets = lane.Owner is { } owner
                ? ShowsProcessLanes ? processLaneDisplay.FirstOrDefault(candidate => candidate.ProcessId == owner)?.Buckets : null
            : lane.Folded
                ? FoldedLane?.Buckets
            : lane.Direction is { } direction
                ? ShowsDirectionLanes ? timelineDirectionLanes!.FirstOrDefault(candidate => candidate.Direction == direction)?.Buckets : null
            : lane.End is { } end
                ? ShowsChannelEndLanes ? timelineChannelEnds!.FirstOrDefault(candidate => candidate.End == end)?.Buckets : null
            : null;
        if (lane != MachineRow)
        {
            return Find(buckets, holds) is { } row ? (row, timelineDetail is not null) : null;
        }

        // The machine rung draws its mechanisms' lanes and no machine row beside them; every other rung draws one.
        return ShowsMechanismLanes ? null
            : Find(timelineDetail?.Buckets, holds) is { } machine ? (machine, true)
            : Find(Snapshot.Timeline, holds) is { } overview ? (overview, false)
            : null;

        static TimelineBucket? Find(IReadOnlyList<TimelineBucket>? buckets, Func<TimelineBucket, bool> holds) =>
            buckets?.FirstOrDefault(holds);
    }

    /// <summary>The explanation of one cell, in the words its hover card and the inspector's other explanations use.</summary>
    private string ExplainCell(TimelineCellLane lane, TimelineBucket bucket, bool zoomed)
    {
        int count = bucket.ObservationCount;
        string binding = WorkspaceRowBuilder.DescribeRule(RelationRule.Parse(ProcessInstanceIndex.BindingRule));
        string held;
        RankingMetric? bytes = null;
        if (lane.Mechanism is { } mechanism)
        {
            string name = MechanismText.InSentence(mechanism);
            held = count == 0
                ? $"No {name} record has a session time in this interval."
                : $"{Counted(count, name + " record", name + " records")} {Have(count)} a session time in this interval: "
                    + $"every {name} record, whichever process it is bound to.";
            bytes = timelineBytes?.Metric;
        }
        else if (lane.Owner is { } owner)
        {
            string process = wholeSnapshot.Processes.FirstOrDefault(candidate => candidate.Id == owner)?.NameWithPid
                ?? "instance " + owner.ToString()[..8];
            held = count == 0
                ? $"No record bound to {process} has a session time in this interval."
                : $"{Counted(count, "record", "records")} {Have(count)} a session time in this interval and "
                    + $"{(count == 1 ? "is" : "are")} bound to {process}, {(count == 1 ? "its" : "their")} canonical owner, "
                    + $"by {binding}.";
            bytes = processLaneBytes?.Metric;
        }
        else if (lane.Folded && FoldedLane is { } folded)
        {
            // Past the lane bound a group's least busy members are counted together (§6.2: collapse groups).
            string members = $"the {FoldedLaneLabel} of this group";
            held = (count == 0
                    ? $"No record bound to {members} has a session time in this interval."
                    : $"{Counted(count, "record", "records")} {Have(count)} a session time in this interval and "
                        + $"{(count == 1 ? "is" : "are")} bound to one of {members}, {(count == 1 ? "its" : "their")} canonical "
                        + $"owners, by {binding}.")
                + string.Create(CultureInfo.CurrentCulture,
                    $" They are folded into one lane past the {SessionTimelineQuery.MaximumProcessLanes:N0}-lane bound, ")
                + string.Create(CultureInfo.CurrentCulture,
                    $"its {folded.Processes.Count:N0} least busy members, so none of their records is dropped.");

            // A byte ranking plots each lane's own process: the folded lane's bar is its records'.
            bytes = null;
        }
        else if (lane.Direction is { } direction)
        {
            string process = timelineFocus is { OwnerProcesses: [var focused] }
                && wholeSnapshot.Processes.FirstOrDefault(candidate => candidate.Id == focused) is { } named
                    ? named.NameWithPid
                    : "the process";
            held = (count == 0
                    ? $"No record of {process} {SourceDirectionDomain(direction)} has a session time in this interval."
                    : $"{Counted(count, "record", "records")} {SourceDirectionDomain(direction)} {Have(count)} a session time in "
                        + $"this interval and {(count == 1 ? "is" : "are")} bound to {process}, "
                        + $"{(count == 1 ? "its" : "their")} canonical owner, by {binding}.")
                + $" {CellDirection(direction)}";
            bytes = directionBytes?.Metric;
        }
        else if (lane.End is { } end && timelineChannelEnds?.FirstOrDefault(candidate => candidate.End == end) is { } channelEnd)
        {
            int index = Enumerable.Range(0, channelEnd.Buckets.Count).FirstOrDefault(
                candidate => channelEnd.Buckets[candidate].Interval == bucket.Interval, -1);
            int outbound = index < 0 ? 0 : channelEnd.Outbound[index].ObservationCount;
            int inbound = index < 0 ? 0 : channelEnd.Inbound[index].ObservationCount;
            string place = $"made at {channelEnd.Endpoint}, the end of this paired TCP channel that {ChannelEndHolder(channelEnd)} holds";
            held = (count == 0
                    ? $"No record {place}, has a session time in this interval."
                    : $"{Counted(count, "record", "records")} {place}, {Have(count)} a session time in this interval: "
                        + string.Create(CultureInfo.CurrentCulture,
                            $"{outbound:N0} outbound, {inbound:N0} inbound and {count - outbound - inbound:N0} with no data direction."))
                + (FocusedRealChannel?.Rule is { } paired
                    ? $" The channel's ends were paired by {WorkspaceRowBuilder.DescribeRule(paired)}."
                    : string.Empty);
        }
        else
        {
            held = (count == 0
                    ? "No record has a session time in this interval."
                    : $"{Counted(count, "record", "records")} of any mechanism {Have(count)} a session time in this interval: "
                        + "the machine's own count, which no binding or pairing rule narrows.")
                + (timelineFocusDescription is { } focus && TimelineShowsFocus
                    && timelineFocusBuckets?.FirstOrDefault(candidate => candidate.Interval == bucket.Interval) is { } focused
                        ? $" {focus}: {Counted(focused.ObservationCount, "record", "records")} of them."
                        : string.Empty);
            bytes = ShowsProcessLanes ? processLaneBytes?.Metric : ShowsDirectionLanes ? directionBytes?.Metric : null;
        }

        // An empty cell draws no bar, and its coverage says what that means.
        string plots = count == 0 ? string.Empty
            : bytes is { } metric ? $" Its bar plots their {Phrase(metric)} per second, from those that recorded a size."
            : " Its bar plots their count per second.";
        string column = (lane.Owner is not null || lane.Folded) && CoarserLaneColumns is { } laneColumns
            ? string.Create(CultureInfo.CurrentCulture,
                $"one of the lanes' own {laneColumns:N0}, coarser than the view's so the group's {processLaneDisplay.Count:N0} ")
                + string.Create(CultureInfo.CurrentCulture, $"lanes stay within {SessionTimelineQuery.MaximumProcessLaneCells:N0} cells")
            : zoomed && timelineDetail is { } detail
            ? string.Create(CultureInfo.CurrentCulture, $"one of this view's own {detail.Buckets.Count:N0}")
                + (detail.Generation != DisplayedGeneration
                    ? string.Create(CultureInfo.CurrentCulture, $", counted from generation {detail.Generation:N0}")
                    : string.Empty)
            : string.Create(CultureInfo.CurrentCulture, $"one of the overview's {Snapshot.Timeline.Count:N0} over the whole session");
        string coverage = "Coverage over the cell: " + CoverageStateText.Value(bucket.Coverage)
            + (bucket.Coverage == CoverageState.Covered
                ? count == 0 ? ", so nothing the capture collects happened here." : "."
                : count == 0 ? ", so an empty cell is not proof of inactivity, and it is drawn hatched."
                : ", so it may hold fewer records than happened, and it is drawn hatched.");
        return $"{held}{plots} Each record counts in the one column whose half-open interval holds its session time, here "
            + $"{column}; a record without a usable session time counts in none. {coverage}";

        static string Have(int count) => count == 1 ? "has" : "have";
    }

    /// <summary>What a source direction's row holds, as a sentence: the source's marking, never who initiated a conversation.</summary>
    private static string CellDirection(Direction direction) => direction switch
    {
        Direction.Outbound => "The source marks sends, connection attempts and client calls outbound, which does not say who "
            + "initiated the conversation.",
        Direction.Inbound => "The source marks receives, accepted connections and server calls inbound, which does not say who "
            + "initiated the conversation.",
        Direction.Bidirectional => "The source marked these records bidirectional.",
        Direction.UnknownDirection => "The source stated no direction for these records, and none is inferred.",
        _ => "These are process lifecycle records, disconnects and other records that carry no data.",
    };

    /// <summary>
    /// E's step from the cell the analysis interval is, with nothing else selected (§6.4: selecting a timeline cell opens
    /// the exact contributing evidence): a process's lane's records are that process's, and a mechanism's lane, a source
    /// direction's row or a channel end's lane narrows the rung's own records by a visible filter its removal widens. Null
    /// for a brushed range, which is no cell, and for the machine row, whose cell draws the rung's own records over the
    /// machine's and so lists the rung's, as before.
    /// </summary>
    private LadderDescent? CellEvidenceDescent(TimeRange viewport)
    {
        TimelineCellLane lane = ExplainedLane;
        if (!realOverview || lane == MachineRow || selectedInterval is not { } interval || CellOf(lane, interval) is null)
        {
            return null;
        }

        string reason = $"Evidence was reached from the {NavigationState.Name(ladder.Current.Level).ToLowerInvariant()} rung "
            + "with a timeline cell chosen: its own records.";
        if (lane.Owner is { } owner)
        {
            string name = wholeSnapshot.Processes.FirstOrDefault(process => process.Id == owner)?.NameWithPid
                ?? "instance " + owner.ToString()[..8];
            return LadderProjection.EvidenceDescentFor(ladder.Current, viewport,
                new(DetailLevel.ProcessInstance, owner.ToString(), name), reason);
        }

        if (lane.Folded && FoldedLane is { } folded)
        {
            // The folded members are a set of their own, as a multi-selection is: E lists exactly theirs.
            return LadderProjection.EvidenceDescentFor(ladder.Current, viewport,
                new(DetailLevel.Group, ProcessSetFilter.KeyOf(folded.Processes),
                    $"{ladder.Current.Focus?.Label ?? "this group"}'s folded lane"), reason);
        }

        LadderDescent rung = LadderProjection.EvidenceDescentFor(ladder.Current, viewport);
        ImpliedFilter narrowing = lane.Mechanism is { } mechanism ? EvidenceScopes.MechanismFilter(mechanism, reason)
            : lane.Direction is { } direction ? EvidenceScopes.DirectionFilter(direction, reason)
            : EvidenceScopes.EndFilter(lane.End!.Value,
                timelineChannelEnds!.First(candidate => candidate.End == lane.End).Endpoint, reason);
        return rung with { AddedFilters = [.. rung.AddedFilters, narrowing] };
    }

    /// <summary>The records E lists from the cell the analysis interval is, as its step resolves them; null without one.</summary>
    private string? CellRecords(TimeRange viewport) => CellEvidenceDescent(viewport) is { } cell
        ? EvidenceScopes.Resolve(Snapshot, ladder.Current with
        {
            Viewport = viewport,
            Filters = [.. ladder.Current.Filters, .. cell.AddedFilters],
        }, TimeBase).Description
        : null;

    /// <summary>
    /// The inspector's title with an analysis interval chosen and nothing else: the timeline cell it is, or a time range
    /// that is no cell - a brush, or a step at the machine rung with no lane selected; null without one. A cell is a
    /// selection as a node or a row is (§6.4), so the inspector never calls itself empty beside one.
    /// </summary>
    private string? IntervalTitle => selectedInterval is not { } interval ? null
        : CellOf(ExplainedLane, interval) is not null ? "Timeline cell"
        : "Time range";

    /// <summary>Beneath the title: the cell's row and how many records it holds, or what the range scopes.</summary>
    private string? IntervalSubtitle => selectedInterval is not { } interval ? null
        : CellOf(ExplainedLane, interval) is { } cell
            ? CellLaneName(ExplainedLane) + " · " + Counted(cell.Bucket.ObservationCount, "record", "records")
        : "The analysis interval: the ranking, the graph and E count only what it holds";

    /// <summary>The row a cell lies in, in the words its label on the timeline uses.</summary>
    private string CellLaneName(TimelineCellLane lane) =>
        lane.Mechanism is { } mechanism ? EvidenceRowText.MechanismName(mechanism) + " lane"
        : lane.Owner is { } owner ? "Lane of " + (wholeSnapshot.Processes.FirstOrDefault(process => process.Id == owner)?.NameWithPid
            ?? "instance " + owner.ToString()[..8])
        : lane.Folded ? "Lane of " + FoldedLaneLabel
        : lane.Direction is { } direction ? DirectionLabel(direction) + " row"
        : lane.End is { } end
            ? timelineChannelEnds?.FirstOrDefault(candidate => candidate.End == end) is { } endLane
                ? "End at " + endLane.Endpoint
                : "End of this channel"
        : "Machine row";

    /// <summary>How many of a chosen cell's records the inspector lists beneath its explanation.</summary>
    internal const int CellRecordsListed = 5;

    private IReadOnlyList<CellRecordRow> cellRecordRows = [];
    private string cellRecordsNote = string.Empty;

    // The scope the inspector's list of a chosen cell's records was read for, how many records E lists from it where that
    // is known, and whether they are the rung's among a machine-row cell's rather than all of the cell's own.
    private (EvidenceScope Scope, int? Count, bool Rungs)? listedCellRecords;
    private CancellationTokenSource? cellRecordsQuery;

    /// <summary>Completes when the most recent read of a chosen cell's records has applied, been superseded or failed.</summary>
    public Task CellRecordsReady { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The first records of the cell the analysis interval is, exactly as E would list them, beneath the cell's
    /// explanation: §6.2's overlapping marks expanded to a detail list, and §6.4's chosen cell opening its contributing
    /// evidence without leaving the rung. Empty while they are read, and without such a cell.
    /// </summary>
    public IReadOnlyList<CellRecordRow> CellRecordRows => cellRecordRows;

    /// <summary>Whether the inspector lists a chosen cell's records: one holding any, whose records E would list.</summary>
    public bool ListsCellRecords => listedCellRecords is not null;

    /// <summary>
    /// What the list holds: the cell's records, or the first of them; in the machine row, whose cell counts the whole
    /// machine's, how many of them E lists - the rung's own.
    /// </summary>
    public string CellRecordsHeading => listedCellRecords is not { Count: var count } listed ? string.Empty
        : listed.Rungs
            ? count is { } rungs
                ? string.Create(CultureInfo.CurrentCulture, $"E lists {rungs:N0} of its records")
                : "Its records E lists"
        : cellRecordRows.Count == 0
            ? count is { } held ? "Its " + Counted(held, "record", "records") : "Its records"
        : count is { } total && total > cellRecordRows.Count
            ? string.Create(CultureInfo.CurrentCulture, $"Its first {cellRecordRows.Count:N0} of {total:N0} records")
        : count is null && cellRecordsMore
            ? string.Create(CultureInfo.CurrentCulture, $"Its first {cellRecordRows.Count:N0} records")
        : "Its " + Counted(cellRecordRows.Count, "record", "records");

    /// <summary>
    /// That they are being read, how many more E lists and how one opens there, or why they could not be read; empty
    /// without a list.
    /// </summary>
    public string CellRecordsNote => cellRecordsNote;

    private const string OpensThere = "Enter or a double click opens one there";

    public bool HasCellRecordsNote => cellRecordsNote.Length > 0;

    // Whether a read of a cell whose count is not E's found more records than the list holds.
    private bool cellRecordsMore;

    /// <summary>
    /// The records E would list from the cell the analysis interval is, with how many there are where that is known;
    /// null where the inspector lists none: no such cell, one holding no record E lists, something selected whose records
    /// E lists instead, several processes chosen, the evidence rung, and the tour.
    /// </summary>
    private (EvidenceScope Scope, int? Count, bool Rungs)? CellRecordsWanted()
    {
        if (disposed || evidenceSource is null || !realOverview || IsEvidenceRung || HasMultiSelection
            || selectedInterval is not { } interval)
        {
            return null;
        }

        TimelineCellLane lane = ExplainedLane;
        TimeRange viewport = ScopeInterval ?? ladder.Current.Viewport;
        if (CellOf(lane, interval) is not { } cell || cell.Bucket.ObservationCount == 0
            || SelectionEvidenceDescent(viewport) is not null)
        {
            return null;
        }

        // A lane's cell counts exactly the records E lists from it. The machine row counts the whole machine's, of which
        // E lists the rung's own, as its focus counts them where the timeline draws one.
        bool rungs = lane == MachineRow;
        int? count = !rungs ? cell.Bucket.ObservationCount
            : TimelineShowsFocus ? TimelineFocusBuckets?.FirstOrDefault(bucket => bucket.Interval == interval)?.ObservationCount
            : null;
        if (count == 0)
        {
            return null;
        }

        LadderDescent? descent = CellEvidenceDescent(viewport);
        EvidenceScope scope = EvidenceScopes.Resolve(Snapshot, ladder.Current with
        {
            Viewport = viewport,
            Filters = descent is null ? ladder.Current.Filters : [.. ladder.Current.Filters, .. descent.AddedFilters],
        }, TimeBase);
        return scope.Problem is null ? (scope, count, rungs) : null;
    }

    /// <summary>Reads the chosen cell's first records when the cell, its lane or its scope changed, and cancels an older read.</summary>
    private void FollowCellRecords()
    {
        (EvidenceScope Scope, int? Count, bool Rungs)? wanted = CellRecordsWanted();
        if (wanted is { } next && listedCellRecords is { } listed
                ? SameRecords(next.Scope, listed.Scope) && next.Count == listed.Count && next.Rungs == listed.Rungs
            : wanted is null && listedCellRecords is null)
        {
            return;
        }

        listedCellRecords = wanted;
        cellRecordsQuery?.Cancel();
        cellRecordsQuery?.Dispose();
        cellRecordsQuery = null;
        cellRecordRows = [];
        cellRecordsMore = false;
        cellRecordsNote = wanted is null ? string.Empty : "Reading them…";
        RaiseCellRecordsChanged();
        if (wanted is not { } read || evidenceSource is not { } source)
        {
            CellRecordsReady = Task.CompletedTask;
            return;
        }

        var query = new CancellationTokenSource();
        cellRecordsQuery = query;
        CellRecordsReady = LoadCellRecordsAsync(source, read.Scope, read.Count, query);
    }

    private async Task LoadCellRecordsAsync(SessionEvidenceSource source, EvidenceScope scope, int? count, CancellationTokenSource query)
    {
        try
        {
            // One more than the list holds says whether more remain where the cell's count is not E's.
            SessionEvidencePage page = await source.ReadScopeAsync(scope, CellRecordsListed + 1, query.Token).AnsweredLater();
            if (disposed || !ReferenceEquals(cellRecordsQuery, query))
            {
                return;
            }

            cellRecordRows = [.. page.Records.Take(CellRecordsListed).Select(record => CellRecord(record, TimeBase))];
            cellRecordsMore = page.Records.Count > CellRecordsListed;
            int? more = count is { } total ? total - cellRecordRows.Count : null;
            cellRecordsNote = more is > 0
                ? string.Create(CultureInfo.CurrentCulture, $"{more:N0} more, which E lists · ") + OpensThere
                : more is null && cellRecordsMore ? "More, which E lists · " + OpensThere
                : cellRecordRows.Count > 0 ? "Enter or a double click opens one in E"
                : string.Empty;
            RaiseCellRecordsChanged();
        }
        catch (OperationCanceledException) when (query.IsCancellationRequested)
        {
            // A newer cell, scope or rung superseded this read.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (!disposed && ReferenceEquals(cellRecordsQuery, query))
            {
                cellRecordRows = [];
                cellRecordsNote = "They could not be read: " + exception.Message;
                RaiseCellRecordsChanged();
            }
        }
    }

    private static readonly IReadOnlySet<string> NoKeys = new HashSet<string>(StringComparer.Ordinal);

    // What the graph highlights of the chosen cell, kept as it changes so a frame reads it rather than working it out.
    private CellHighlight cellGraph = CellHighlight.None;

    /// <summary>
    /// The drawn relationships a chosen cell's records belong to, which the graph highlights (§6.4: selecting a timeline
    /// cell highlights graph relationships): a mechanism's lane's, the edges of that mechanism holding records in its
    /// interval; a channel end's, its channel's edge; the machine row's, every edge holding any. Empty while the graph's
    /// counts are not yet the cell's interval's, and while anything else selected is what E lists.
    /// </summary>
    public IReadOnlySet<string> CellGraphEdges => cellGraph.Edges;

    /// <summary>
    /// The drawn nodes holding the processes whose records a chosen cell of a process's lane, the folded lane or a
    /// process's source-direction row counts, which the graph highlights in place of edges: a relationship's count holds
    /// both its ends' records, so it cannot say whether the process's own are among those in the cell. A node the
    /// selection already rings is not among them.
    /// </summary>
    public IReadOnlySet<string> CellGraphNodes => cellGraph.Nodes;

    /// <summary>What the graph's summary adds while it highlights a chosen cell's relationships or processes; empty otherwise.</summary>
    private string CellGraphNote => CellHighlightNote(cellGraph.Edges.Count, cellGraph.Nodes.Count, cellGraph.Processes,
        cellGraph.Nodes.Count == 1 && graphDisplay.Node(cellGraph.Nodes.First())?.Kind == GraphNodeKind.Process);

    /// <summary>
    /// What the graph's summary adds while it highlights a chosen cell's <paramref name="relationships"/>, or the
    /// <paramref name="nodes"/> holding its <paramref name="processes"/>, one a process's own node when
    /// <paramref name="processNode"/>: the node is named for the process it is, or for holding the cell's processes.
    /// </summary>
    internal static string CellHighlightNote(int relationships, int nodes, int processes, bool processNode) =>
        relationships == 1 ? " · the chosen cell's relationship highlighted"
        : relationships > 1 ? " · the chosen cell's " + Counted(relationships, "relationship", "relationships") + " highlighted"
        : nodes == 0 ? string.Empty
        : nodes == 1 && processes == 1 && processNode ? " · the chosen cell's process highlighted"
        : nodes == 1 ? " · the node holding the chosen cell's " + (processes == 1 ? "process" : "processes") + " highlighted"
        : " · the " + Counted(nodes, "node", "nodes") + " holding the chosen cell's processes highlighted";

    /// <summary>What the graph highlights of the cell the analysis interval is, as <see cref="CellGraphEdges"/> and <see cref="CellGraphNodes"/> say.</summary>
    private CellHighlight CellGraphWanted()
    {
        if (!realOverview || IsEvidenceRung || HasMultiSelection || selectedInterval is not { } interval
            || appliedInterval != interval || CellOf(ExplainedLane, interval) is not { Bucket.ObservationCount: > 0 }
            || SelectionEvidenceDescent(interval) is not null)
        {
            return CellHighlight.None;
        }

        TimelineCellLane lane = ExplainedLane;
        IReadOnlyList<ProcessInstanceId>? owners = lane.Owner is { } owner ? [owner]
            : lane.Folded ? FoldedLane?.Processes
            : lane.Direction is not null ? timelineFocus?.OwnerProcesses
            : null;
        if (owners is not null)
        {
            // A node the selection rings keeps that ring, which says more than the cell's fainter one could: the process a
            // rung was reached with is selected there, so a cell of its own rows highlights no node of it.
            (IReadOnlySet<string> whole, IReadOnlySet<string> part) = GraphSelection();
            HashSet<ProcessInstanceId> counted = [.. owners];
            HashSet<ProcessInstanceId> held = [];
            HashSet<string> nodes = new(StringComparer.Ordinal);
            foreach (GraphDisplayNode node in graphDisplay.Nodes)
            {
                if (whole.Contains(node.Key) || part.Contains(node.Key))
                {
                    continue;
                }

                foreach (ProcessInstanceId process in node.Members)
                {
                    if (counted.Contains(process))
                    {
                        held.Add(process);
                        nodes.Add(node.Key);
                    }
                }
            }

            return new(NoKeys, nodes, held.Count);
        }

        string? channel = lane.End is not null ? FocusedRealChannel?.EdgeKey : null;
        HashSet<string> edges = new(StringComparer.Ordinal);
        foreach (GraphDisplayEdge edge in graphDisplay.Edges)
        {
            if (edge.ObservationCount > 0
                && (lane.Mechanism is { } mechanism ? edge.Mechanism == mechanism
                    : lane.End is not null ? channel is not null && edge.Relationships.Contains(channel)
                    : true))
            {
                edges.Add(edge.Key);
            }
        }

        return new(edges, NoKeys, 0);
    }

    /// <summary>Works out the chosen cell's graph highlight again when the cell, its lane, the selection or the graph's counts changed.</summary>
    private void FollowCellGraph()
    {
        CellHighlight wanted = CellGraphWanted();
        if (wanted.Edges.SetEquals(cellGraph.Edges) && wanted.Nodes.SetEquals(cellGraph.Nodes) && wanted.Processes == cellGraph.Processes)
        {
            return;
        }

        cellGraph = wanted;
        OnPropertyChanged(nameof(CellGraphEdges));
        OnPropertyChanged(nameof(CellGraphNodes));
        OnPropertyChanged(nameof(GraphSummary));
        OnPropertyChanged(nameof(GraphScope));
    }

    /// <summary>
    /// What the graph highlights of a chosen cell: the relationships its records belong to, or the nodes holding the
    /// processes whose records it counts and how many of those processes they hold.
    /// </summary>
    private sealed record CellHighlight(IReadOnlySet<string> Edges, IReadOnlySet<string> Nodes, int Processes)
    {
        public static readonly CellHighlight None = new(NoKeys, NoKeys, 0);
    }

    /// <summary>A record of a chosen cell as the inspector lists it, in the evidence rung's words.</summary>
    private static CellRecordRow CellRecord(SessionEvidenceRecord record, SessionClock timeBase)
    {
        ObservationRowV1 row = record.Observation;
        FamilyTokens tokens = ThemePalette.TokensFor(ThemeResources.CurrentMode, ThemePalette.FamilyOf(row.Mechanism));
        string title = EvidenceRowText.Title(row);
        string? size = EvidenceRowText.Size(row, CultureInfo.CurrentCulture);
        string when = EvidenceRowText.When(row, timeBase, CultureInfo.CurrentCulture);
        string pid = EvidenceRowText.OwnerProcessId(row) is { } id ? string.Create(CultureInfo.CurrentCulture, $"PID {id}") : "no owner";
        string endpoints = EvidenceRowText.Endpoints(row) is { } pair ? ", " + pair : string.Empty;
        return new(size is null ? title : title + " · " + size, when + " · " + pid, tokens.Glyph, tokens.Label)
        {
            AccessibleName = $"{title}{(size is null ? string.Empty : ", " + size)}, at {when}{endpoints}, "
                + EvidenceRowText.Ownership(record, CultureInfo.CurrentCulture) + ".",
            Key = EvidenceKey(record),
        };
    }

    /// <summary>
    /// Opens E from the chosen cell with one of its listed records selected there, so the inspector describes that record
    /// (§6.4): what Enter or a double click on it in the list does. The list holds E's first records, so it is on E's
    /// first page. False when the record is no longer listed, or E does not open.
    /// </summary>
    public bool OpenCellRecord(CellRecordRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!cellRecordRows.Contains(row))
        {
            return false;
        }

        pendingEvidenceKey = row.Key;
        if (ShowEvidence())
        {
            return true;
        }

        pendingEvidenceKey = null;
        return false;
    }

    /// <summary>Whether two scopes read the same records: every part of each the same, their owners in the same order.</summary>
    private static bool SameRecords(EvidenceScope left, EvidenceScope right) =>
        left with { OwnerProcesses = Array.Empty<ProcessInstanceId>() } == right with { OwnerProcesses = Array.Empty<ProcessInstanceId>() }
        && left.OwnerProcesses.SequenceEqual(right.OwnerProcesses);

    private void RaiseCellRecordsChanged()
    {
        OnPropertyChanged(nameof(CellRecordRows));
        OnPropertyChanged(nameof(ListsCellRecords));
        OnPropertyChanged(nameof(CellRecordsHeading));
        OnPropertyChanged(nameof(CellRecordsNote));
        OnPropertyChanged(nameof(HasCellRecordsNote));
    }

    private void RaiseCellExplanationChanged()
    {
        OnPropertyChanged(nameof(CellExplanation));
        OnPropertyChanged(nameof(HasCellExplanation));
        OnPropertyChanged(nameof(EvidenceSummary));
        OnPropertyChanged(nameof(SelectionTitle));
        OnPropertyChanged(nameof(SelectionSubtitle));
    }

    /// <summary>What the cell's explanation reads, by the name its change is raised under.</summary>
    private static bool IsCellExplanationInput(string? propertyName) => propertyName is nameof(SelectedInterval)
        or nameof(SelectedProcess) or nameof(SelectedTimelineLane) or nameof(SelectedDirectionLane)
        or nameof(SelectedChannelEnd) or nameof(TimelineDetail) or nameof(ProcessLaneDisplay) or nameof(FoldedLane)
        or nameof(TimelineDirectionLanes) or nameof(TimelineChannelEndLanes) or nameof(TimelineFocusBuckets)
        or nameof(ShowsMechanismLanes) or nameof(ShowsProcessLanes) or nameof(ShowsDirectionLanes)
        or nameof(ShowsChannelEndLanes) or nameof(TimelineBytes) or nameof(ProcessLaneBytes) or nameof(DirectionLaneBytes)
        or nameof(Snapshot);
}
