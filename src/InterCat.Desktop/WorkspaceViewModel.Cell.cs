using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// The lane a timeline cell lies in (§6.8): the machine row when every part is null, or one mechanism's lane at the
/// machine rung, one process's lane among a group's, one source direction's row of a process, or one end of a channel.
/// </summary>
public sealed record TimelineCellLane(
    Mechanism? Mechanism = null, ProcessInstanceId? Owner = null, Direction? Direction = null, int? End = null);

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
        Direction? directionLane = null, ChannelEndTimelineLane? endLane = null)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        choosingCell = true;
        try
        {
            chosenCell = new TimelineCellLane(lane, ownerLane?.Id, directionLane, endLane?.End);
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
    private (TimelineBucket Bucket, bool Zoomed)? CellOf(TimelineCellLane lane, TimeRange interval)
    {
        if (lane.Mechanism is { } mechanism)
        {
            return !ShowsMechanismLanes ? null
                : Find(timelineDetail?.MechanismLanes.FirstOrDefault(candidate => candidate.Mechanism == mechanism)?.Buckets, interval) is { } zoomed
                    ? (zoomed, true)
                : Find(Snapshot.MechanismLanes.FirstOrDefault(candidate => candidate.Mechanism == mechanism)?.Buckets, interval) is { } whole
                    ? (whole, false)
                : null;
        }

        IReadOnlyList<TimelineBucket>? buckets = lane.Owner is { } owner
                ? ShowsProcessLanes ? processLaneDisplay.FirstOrDefault(candidate => candidate.ProcessId == owner)?.Buckets : null
            : lane.Direction is { } direction
                ? ShowsDirectionLanes ? timelineDirectionLanes!.FirstOrDefault(candidate => candidate.Direction == direction)?.Buckets : null
            : lane.End is { } end
                ? ShowsChannelEndLanes ? timelineChannelEnds!.FirstOrDefault(candidate => candidate.End == end)?.Buckets : null
            : null;
        if (lane != MachineRow)
        {
            return Find(buckets, interval) is { } row ? (row, timelineDetail is not null) : null;
        }

        // The machine rung draws its mechanisms' lanes and no machine row beside them; every other rung draws one.
        return ShowsMechanismLanes ? null
            : Find(timelineDetail?.Buckets, interval) is { } machine ? (machine, true)
            : Find(Snapshot.Timeline, interval) is { } overview ? (overview, false)
            : null;

        static TimelineBucket? Find(IReadOnlyList<TimelineBucket>? buckets, TimeRange interval) =>
            buckets?.FirstOrDefault(bucket => bucket.Interval == interval);
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
        string column = lane.Owner is not null && CoarserLaneColumns is { } laneColumns
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
        }).Description
        : null;

    private void RaiseCellExplanationChanged()
    {
        OnPropertyChanged(nameof(CellExplanation));
        OnPropertyChanged(nameof(HasCellExplanation));
        OnPropertyChanged(nameof(EvidenceSummary));
    }

    /// <summary>What the cell's explanation reads, by the name its change is raised under.</summary>
    private static bool IsCellExplanationInput(string? propertyName) => propertyName is nameof(SelectedInterval)
        or nameof(SelectedProcess) or nameof(SelectedTimelineLane) or nameof(SelectedDirectionLane)
        or nameof(SelectedChannelEnd) or nameof(TimelineDetail) or nameof(ProcessLaneDisplay)
        or nameof(TimelineDirectionLanes) or nameof(TimelineChannelEndLanes) or nameof(TimelineFocusBuckets)
        or nameof(ShowsMechanismLanes) or nameof(ShowsProcessLanes) or nameof(ShowsDirectionLanes)
        or nameof(ShowsChannelEndLanes) or nameof(TimelineBytes) or nameof(ProcessLaneBytes) or nameof(DirectionLaneBytes)
        or nameof(Snapshot);
}
