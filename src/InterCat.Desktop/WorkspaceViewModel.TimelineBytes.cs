using System.Globalization;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Desktop.Theme;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>What the machine rung's mechanism lanes plot under a byte ranking (<see cref="WorkspaceViewModel.TimelineBytes"/>).</summary>
/// <param name="Metric">What each column's bar plots: the bytes the ranking measures - sent, received, or both.</param>
/// <param name="Overview">Each lane's bytes in the overview's columns, across the whole session.</param>
/// <param name="Zoomed">
/// Each lane's bytes in the zoomed view's own columns, which replace the overview's where they lie; null at the whole
/// extent and until they are read.
/// </param>
public sealed record TimelineByteLayer(
    RankingMetric Metric, SessionMechanismByteMeasures Overview, SessionMechanismByteMeasures? Zoomed)
{
    /// <summary>
    /// A lane's bytes over exactly <paramref name="interval"/>: the zoomed view's column of that interval where there is
    /// one, else the overview's; null when neither has a column of it.
    /// </summary>
    public TransportBytes? Of(Mechanism lane, TimeRange interval) =>
        Zoomed?.Of(lane)?.For(interval) ?? Overview.Of(lane)?.For(interval);
}

/// <summary>
/// What a group's process lanes and the machine row above them plot under a byte ranking
/// (<see cref="WorkspaceViewModel.ProcessLaneBytes"/>).
/// </summary>
/// <param name="Metric">What each column's bar plots: the bytes the ranking measures - sent, received, or both.</param>
/// <param name="Measures">Every record's bytes in the machine row's columns, and each lane's own in the lanes' columns.</param>
public sealed record ProcessLaneByteLayer(RankingMetric Metric, SessionOwnerByteMeasures Measures);

/// <summary>
/// What a process's direction rows and the machine row above them plot under a byte ranking
/// (<see cref="WorkspaceViewModel.DirectionLaneBytes"/>).
/// </summary>
/// <param name="Metric">What each column's bar plots: the bytes the ranking measures - sent, received, or both.</param>
/// <param name="Measures">Every record's bytes in the machine row's columns, and the process's own by source direction.</param>
public sealed record DirectionLaneByteLayer(RankingMetric Metric, SessionDirectionByteMeasures Measures);

/// <summary>
/// The timeline under a byte ranking (§6.2: a density cell carries the selected count or a compatible byte sum). The
/// overview counts records and sums no bytes (`overview-index-v1` §4), so while a real session is ranked by bytes, the
/// lanes' bytes are read for the columns they draw: at the machine rung each mechanism lane's, over the overview's columns
/// and the zoomed view's own once it rests; at a group's rung each process lane's, and at a process's each direction row's,
/// with every record's in the machine row above them, over the columns the rows were counted in. Each lane plots what the
/// ranking measures, per second, and a column whose records declared sizes none of them recorded is drawn unmeasured, never
/// as zero (R3). Until the bytes arrive the lanes keep plotting records and the caption says their bytes are being read. A
/// live publication shows the previous one's bytes until its own arrive, as it does its zoomed counts.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    private ByteRead<LaneBytesRequest, SessionMechanismByteMeasures>? overviewLaneBytes;
    private ByteRead<LaneBytesRequest, SessionMechanismByteMeasures>? zoomedLaneBytes;
    private ByteRead<OwnerBytesRequest, SessionOwnerByteMeasures>? ownerLaneBytes;
    private ByteRead<DirectionBytesRequest, SessionDirectionByteMeasures>? directionLaneBytes;
    private IReadOnlyList<Mechanism>? laneMechanisms;
    private TimelineByteLayer? timelineBytes;
    private ProcessLaneByteLayer? processLaneBytes;
    private DirectionLaneByteLayer? directionBytes;
    private bool timelineDrawsUnmeasured;
    private string? timelineBytesNote;

    /// <summary>
    /// What the machine rung's mechanism lanes plot under a byte ranking once their bytes are read: the metric and each
    /// lane's bytes by column. Null while the lanes plot records: under any other ranking, at any other rung, and until the
    /// overview's bytes arrive.
    /// </summary>
    public TimelineByteLayer? TimelineBytes => timelineBytes;

    /// <summary>
    /// What a group's process lanes and their machine row plot under a byte ranking once their bytes are read; null while
    /// they plot records, as <see cref="TimelineBytes"/> is at the machine rung.
    /// </summary>
    public ProcessLaneByteLayer? ProcessLaneBytes => processLaneBytes;

    /// <summary>
    /// What a process's direction rows and their machine row plot under a byte ranking once their bytes are read; null
    /// while they plot records, as <see cref="TimelineBytes"/> is at the machine rung.
    /// </summary>
    public DirectionLaneByteLayer? DirectionLaneBytes => directionBytes;

    /// <summary>Whether the byte lanes draw a column in view whose size was not measured (§6.6).</summary>
    public bool TimelineDrawsUnmeasured => timelineDrawsUnmeasured;

    /// <summary>Whether a pane draws §6.6's unmeasured value, which the legend then keys: the graph, or the byte lanes.</summary>
    public bool DrawsUnmeasured => GraphDrawsUnmeasured || timelineDrawsUnmeasured;

    /// <summary>Completes when the timeline's latest byte reads have applied, been superseded or failed.</summary>
    public Task TimelineBytesReady =>
        Task.WhenAll(OverviewLaneBytes.Ready, ZoomedLaneBytes.Ready, OwnerLaneBytes.Ready, DirectionLaneBytesRead.Ready);

    /// <summary>The columns of one read of mechanism lanes: the interval they divide and how many.</summary>
    private sealed record LaneBytesRequest(TimeRange Interval, int Columns);

    /// <summary>
    /// The columns of one read of a group's lanes: the interval, the machine row's columns and the lanes' own, and the
    /// lanes' process instances, compared by value.
    /// </summary>
    private sealed record OwnerBytesRequest(TimeRange Interval, int Columns, int LaneColumns, IReadOnlyList<ProcessInstanceId> Owners)
    {
        public bool Equals(OwnerBytesRequest? other) => other is not null && Interval == other.Interval
            && Columns == other.Columns && LaneColumns == other.LaneColumns && Owners.SequenceEqual(other.Owners);

        public override int GetHashCode() => HashCode.Combine(Interval, Columns, LaneColumns, Owners.Count);
    }

    private ByteRead<LaneBytesRequest, SessionMechanismByteMeasures> OverviewLaneBytes => overviewLaneBytes ??= new(this,
        (source, request, cancellation) => source.LaneBytesAsync(request.Interval, request.Columns, LaneMechanisms, cancellation),
        measured => measured.SessionId);

    private ByteRead<LaneBytesRequest, SessionMechanismByteMeasures> ZoomedLaneBytes => zoomedLaneBytes ??= new(this,
        (source, request, cancellation) => source.LaneBytesAsync(request.Interval, request.Columns, LaneMechanisms, cancellation),
        measured => measured.SessionId);

    private ByteRead<OwnerBytesRequest, SessionOwnerByteMeasures> OwnerLaneBytes => ownerLaneBytes ??= new(this,
        (source, request, cancellation) => source.OwnerBytesAsync(
            request.Interval, request.Columns, request.LaneColumns, request.Owners, cancellation),
        measured => measured.SessionId);

    /// <summary>The columns of one read of a process's direction rows, which share the machine row's, and the process.</summary>
    private sealed record DirectionBytesRequest(TimeRange Interval, int Columns, ProcessInstanceId Owner);

    private ByteRead<DirectionBytesRequest, SessionDirectionByteMeasures> DirectionLaneBytesRead => directionLaneBytes ??= new(this,
        (source, request, cancellation) => source.DirectionBytesAsync(request.Interval, request.Columns, request.Owner, cancellation),
        measured => measured.SessionId);

    /// <summary>
    /// One set of columns the timeline reads bytes for: what it shows, the columns this generation read them for, the read
    /// under way and one that failed. A read of other columns than those asked for is cancelled; one that failed is not
    /// tried again until it is forgotten.
    /// </summary>
    private sealed class ByteRead<TRequest, TMeasures>(
        WorkspaceViewModel owner,
        Func<SessionEvidenceSource, TRequest, CancellationToken, Task<TMeasures>> read,
        Func<TMeasures, Guid> sessionOf)
        where TRequest : class
        where TMeasures : class
    {
        /// <summary>The bytes shown for these columns: this generation's, or a stand-in carried from an earlier one.</summary>
        public TMeasures? Measures { get; set; }

        /// <summary>The columns <see cref="Measures"/> answer when this generation read them; null for a stand-in.</summary>
        public TRequest? Answered { get; private set; }

        public (TRequest Request, CancellationTokenSource Cancellation)? Running { get; private set; }

        public (TRequest Request, string Problem)? Failed { get; private set; }

        public Task Ready { get; private set; } = Task.CompletedTask;

        /// <summary>Forgets a read that failed, so asking again tries once more.</summary>
        public void ForgetFailure() => Failed = null;

        /// <summary>Reads what <paramref name="wanted"/> asks for unless it has it, is reading it, or could not.</summary>
        public void Follow(TRequest? wanted)
        {
            if (Running is { } running && !EqualityComparer<TRequest?>.Default.Equals(running.Request, wanted))
            {
                Cancel();
            }

            if (owner.disposed || wanted is null || owner.evidenceSource is not { } source
                || EqualityComparer<TRequest?>.Default.Equals(Answered, wanted) || Running is not null
                || (Failed is { } failed && EqualityComparer<TRequest?>.Default.Equals(failed.Request, wanted)))
            {
                return;
            }

            var cancellation = new CancellationTokenSource();
            Running = (wanted, cancellation);
            Ready = ReadAsync(source, wanted, cancellation);
        }

        public void Cancel()
        {
            if (Running is { } running)
            {
                running.Cancellation.Cancel();
                running.Cancellation.Dispose();
                Running = null;
            }
        }

        private async Task ReadAsync(SessionEvidenceSource source, TRequest request, CancellationTokenSource cancellation)
        {
            try
            {
                TMeasures measured = await read(source, request, cancellation.Token).AnsweredLater();
                if (owner.disposed || Running?.Cancellation != cancellation)
                {
                    return;
                }

                Running = null;
                cancellation.Dispose();
                if (sessionOf(measured) == source.SessionId)
                {
                    Measures = measured;
                    Answered = request;
                    Failed = null;
                }
                else
                {
                    Failed = (request, "the session on disk is another one");
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Another view, ranking or rung, or a closed workspace, superseded this read.
                return;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException
                or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            {
                if (owner.disposed || Running?.Cancellation != cancellation)
                {
                    return;
                }

                Running = null;
                cancellation.Dispose();
                Failed = (request, exception.Message);
            }

            owner.UpdateTimelineBytes();
        }
    }

    /// <summary>The byte metric the mechanism lanes are asked to plot: a byte ranking at a real session's machine rung.</summary>
    private RankingMetric? LaneByteMetric => ReadsBytes && !disposed && ShowsMechanismLanes && Family == RankingFamily.Bytes
        ? rankBy : null;

    /// <summary>The byte metric a group's process lanes are asked to plot: a byte ranking while they are shown.</summary>
    private RankingMetric? ProcessLaneByteMetric => ReadsBytes && !disposed && ShowsProcessLanes && Family == RankingFamily.Bytes
        ? rankBy : null;

    /// <summary>The byte metric a process's direction rows are asked to plot: a byte ranking while they are shown.</summary>
    private RankingMetric? DirectionLaneByteMetric => ReadsBytes && !disposed && ShowsDirectionLanes
        && Family == RankingFamily.Bytes && timelineFocus is { ChannelKey: null, OwnerProcesses.Count: 1 }
        ? rankBy : null;

    /// <summary>The mechanisms whose lanes the overview draws, in its order.</summary>
    private IReadOnlyList<Mechanism> LaneMechanisms =>
        laneMechanisms ??= Array.AsReadOnly([.. wholeSnapshot.MechanismLanes.Select(lane => lane.Mechanism)]);

    /// <summary>Whether a drawn view shows the whole session, which the overview's own columns answer.</summary>
    private bool ShowsWholeExtent(TimeRange viewport) =>
        viewport.StartTicks <= wholeSnapshot.Extent.StartTicks && viewport.EndTicks >= wholeSnapshot.Extent.EndTicks;

    /// <summary>
    /// Reads the bytes the timeline's lanes are asked to plot and does not have, and cancels a read no longer asked for:
    /// the machine rung's mechanism lanes over the overview's columns and a zoomed view's own, and a group's process lanes
    /// or a process's direction rows over the columns they were counted in. Called when the ranking, the rung, the drawn
    /// view or the lanes change. Leaving the byte ranking forgets a failed read, so choosing it again tries once more.
    /// </summary>
    private void FollowTimelineBytes()
    {
        IReadOnlyList<TimelineBucket> overview = wholeSnapshot.Timeline;
        bool plots = LaneByteMetric is not null && overview.Count > 0;
        if (Family != RankingFamily.Bytes)
        {
            OverviewLaneBytes.ForgetFailure();
            ZoomedLaneBytes.ForgetFailure();
            OwnerLaneBytes.ForgetFailure();
            DirectionLaneBytesRead.ForgetFailure();
        }

        OverviewLaneBytes.Follow(plots
            ? new LaneBytesRequest(new TimeRange(overview[0].Interval.StartTicks, overview[^1].Interval.EndTicks), overview.Count)
            : null);
        ZoomedLaneBytes.Follow(plots && drawnTimeline is { } drawn && !ShowsWholeExtent(drawn.Viewport)
            ? new LaneBytesRequest(drawn.Viewport, drawn.Columns)
            : null);
        OwnerLaneBytes.Follow(ProcessLaneByteMetric is not null ? OwnerRequest() : null);
        DirectionLaneBytesRead.Follow(DirectionLaneByteMetric is not null ? DirectionRequest() : null);
        UpdateTimelineBytes();
    }

    /// <summary>
    /// The columns a process's direction rows were counted in, which the machine row above them shares, and the process;
    /// null without rows to read.
    /// </summary>
    private DirectionBytesRequest? DirectionRequest() =>
        timelineDirectionLanes is { Count: > 0 } lanes && lanes[0].Buckets is { Count: > 0 } row
        && timelineFocus is { ChannelKey: null, OwnerProcesses: [var owner] }
            ? new(new TimeRange(row[0].Interval.StartTicks, row[^1].Interval.EndTicks), row.Count, owner)
            : null;

    /// <summary>
    /// The columns a group's lanes were counted in, with the machine row's beside them: the zoomed detail's where it spans
    /// the lanes, else the overview's, else the lanes' own. Null without lanes to read.
    /// </summary>
    private OwnerBytesRequest? OwnerRequest()
    {
        if (processLaneDisplay.Count == 0 || processLaneDisplay[0].Buckets is not { Count: > 0 } lane)
        {
            return null;
        }

        var span = new TimeRange(lane[0].Interval.StartTicks, lane[^1].Interval.EndTicks);
        IReadOnlyList<TimelineBucket>? machine = timelineDetail?.Buckets is { Count: > 0 } detail && Spans(detail, span) ? detail
            : Spans(wholeSnapshot.Timeline, span) ? wholeSnapshot.Timeline
            : null;
        // The lanes' instances in an order of their own, not as drawn: pinning a lane moves it without reading anything anew.
        return new(span, machine?.Count ?? lane.Count, lane.Count,
            [.. processLaneDisplay.Select(owner => owner.ProcessId).OrderBy(owner => owner.Value)]);

        static bool Spans(IReadOnlyList<TimelineBucket> buckets, TimeRange span) => buckets.Count > 0
            && buckets[0].Interval.StartTicks == span.StartTicks && buckets[^1].Interval.EndTicks == span.EndTicks;
    }

    /// <summary>
    /// Shows an earlier publication's lane bytes, of the same session, until this generation's own arrive; a group's only
    /// for the same group.
    /// </summary>
    private void AdoptTimelineBytes(TimelineCarry carry, bool sameFocus)
    {
        if (evidenceSource is not { } source)
        {
            return;
        }

        if (carry.LaneBytes?.SessionId == source.SessionId && OverviewLaneBytes.Answered is null)
        {
            OverviewLaneBytes.Measures = carry.LaneBytes;
        }

        if (carry.ZoomedLaneBytes?.SessionId == source.SessionId && ZoomedLaneBytes.Answered is null)
        {
            ZoomedLaneBytes.Measures = carry.ZoomedLaneBytes;
        }

        if (sameFocus && carry.ProcessLaneBytes?.SessionId == source.SessionId && OwnerLaneBytes.Answered is null)
        {
            OwnerLaneBytes.Measures = carry.ProcessLaneBytes;
        }

        if (sameFocus && carry.DirectionLaneBytes?.SessionId == source.SessionId && DirectionLaneBytesRead.Answered is null)
        {
            DirectionLaneBytesRead.Measures = carry.DirectionLaneBytes;
        }

        UpdateTimelineBytes();
    }

    private void CancelTimelineBytes()
    {
        overviewLaneBytes?.Cancel();
        zoomedLaneBytes?.Cancel();
        ownerLaneBytes?.Cancel();
        directionLaneBytes?.Cancel();
    }

    /// <summary>Takes up what the lanes plot now, and says what changed: the bytes, the legend's key, the caption.</summary>
    private void UpdateTimelineBytes()
    {
        TimelineByteLayer? lanes = LaneByteMetric is { } metric && OverviewLaneBytes.Measures is { } overview
            ? new(metric, overview, drawnTimeline is { } drawn && !ShowsWholeExtent(drawn.Viewport) ? ZoomedLaneBytes.Measures : null)
            : null;
        if (lanes is null ? timelineBytes is not null
            : timelineBytes is null || lanes.Metric != timelineBytes.Metric
                || !ReferenceEquals(lanes.Overview, timelineBytes.Overview) || !ReferenceEquals(lanes.Zoomed, timelineBytes.Zoomed))
        {
            timelineBytes = lanes;
            OnPropertyChanged(nameof(TimelineBytes));
        }

        ProcessLaneByteLayer? owners = ProcessLaneByteMetric is { } ownerMetric && OwnerLaneBytes.Measures is { } measured
            ? new(ownerMetric, measured)
            : null;
        if (owners is null ? processLaneBytes is not null
            : processLaneBytes is null || owners.Metric != processLaneBytes.Metric
                || !ReferenceEquals(owners.Measures, processLaneBytes.Measures))
        {
            processLaneBytes = owners;
            OnPropertyChanged(nameof(ProcessLaneBytes));
        }

        DirectionLaneByteLayer? rows = DirectionLaneByteMetric is { } rowMetric && DirectionLaneBytesRead.Measures is { } read
            ? new(rowMetric, read)
            : null;
        if (rows is null ? directionBytes is not null
            : directionBytes is null || rows.Metric != directionBytes.Metric || !ReferenceEquals(rows.Measures, directionBytes.Measures))
        {
            directionBytes = rows;
            OnPropertyChanged(nameof(DirectionLaneBytes));
        }

        TimeRange view = drawnTimeline?.Viewport ?? wholeSnapshot.Extent;
        bool unmeasured = (timelineBytes is { } plotted
                && (Unmeasured(plotted.Overview.Lanes, plotted.Metric, view)
                    || (plotted.Zoomed is { } zoomed && Unmeasured(zoomed.Lanes, plotted.Metric, view))))
            || (processLaneBytes is { } group
                && (Unmeasured([group.Measures.Machine], group.Metric, view) || Unmeasured(group.Measures.Lanes, group.Metric, view)))
            || (directionBytes is { } directions
                && (Unmeasured([directions.Measures.Machine], directions.Metric, view)
                    || Unmeasured(directions.Measures.Lanes, directions.Metric, view)));
        if (unmeasured != timelineDrawsUnmeasured)
        {
            timelineDrawsUnmeasured = unmeasured;
            OnPropertyChanged(nameof(TimelineDrawsUnmeasured));
            OnPropertyChanged(nameof(DrawsUnmeasured));
        }

        // The caption states what the lanes plot, so a change of any of them, or of a read under way, restates it.
        string note = (LaneBytesNote ?? string.Empty) + "|" + ProcessLaneBytesNote + "|" + DirectionLaneBytesNote + "|"
            + timelineDrawsUnmeasured;
        if (note != timelineBytesNote)
        {
            timelineBytesNote = note;
            OnPropertyChanged(nameof(TimelineCaption));
        }
    }

    /// <summary>Whether any column of <paramref name="lanes"/> in <paramref name="view"/> plots §6.6's unmeasured value.</summary>
    private static bool Unmeasured(IReadOnlyList<SessionIntervalByteMeasures> lanes, RankingMetric metric, TimeRange view)
    {
        foreach (SessionIntervalByteMeasures lane in lanes)
        {
            for (int column = 0; column < lane.Columns.Count; column++)
            {
                TimeRange interval = lane.IntervalOf(column);
                if (interval.EndTicks > view.StartTicks && interval.StartTicks < view.EndTicks
                    && lane.Columns[column].ValueOf(metric) is (null, _, > 0))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// What the machine rung's caption says of its lanes' bytes under a byte ranking while it cannot plot them: that they
    /// are being read, or why they could not be. Null while they are plotted, and under any other ranking or rung.
    /// </summary>
    private string? LaneBytesNote =>
        LaneByteMetric is not { } metric || timelineBytes is not null ? null
        : OverviewLaneBytes.Failed is { } failed ? $"{Phrase(metric)} could not be read: {failed.Problem.TrimEnd('.')}"
        : $"reading {Phrase(metric)}…";

    /// <summary>
    /// What a group's caption says of its lanes' bytes under a byte ranking: that they plot them, per second, that they are
    /// being read, or why they could not be. Empty under any other ranking, and while the lanes are not shown.
    /// </summary>
    private string ProcessLaneBytesNote =>
        RowBytesNote(ProcessLaneByteMetric, processLaneBytes is not null, OwnerLaneBytes.Failed?.Problem);

    /// <summary>What a process's caption says of its direction rows' bytes under a byte ranking, as a group's does.</summary>
    private string DirectionLaneBytesNote =>
        RowBytesNote(DirectionLaneByteMetric, directionBytes is not null, DirectionLaneBytesRead.Failed?.Problem);

    /// <summary>A focused rung's rows' bytes in its caption: plotted per second, being read, or why they could not be.</summary>
    private string RowBytesNote(RankingMetric? asked, bool plotted, string? problem) =>
        asked is not { } metric ? string.Empty
        : plotted ? $" · {Phrase(metric)} per second"
            + (timelineDrawsUnmeasured ? ", cross-hatched where no size was recorded" : string.Empty)
        : problem is not null ? $" · {Phrase(metric)} could not be read: {problem.TrimEnd('.')}"
        : $" · reading {Phrase(metric)}…";

    /// <summary>
    /// What a rung adds to its caption under a byte ranking while its timeline counts records - its lanes could not be
    /// counted, or it has none - so what it draws is never read as a byte volume. While its lanes are being counted, the
    /// caption says that instead.
    /// </summary>
    private string LaneRecordsNote => ReadsBytes && ShowsRankingChoice && Family == RankingFamily.Bytes && !ShowsMechanismLanes
        && !ShowsProcessLanes && !ShowsDirectionLanes && !timelineFocusLoading
        ? " · the timeline counts records here, not bytes"
        : string.Empty;

    /// <summary>What a mechanism lane's card says under a byte ranking: its bucket's bytes against the busiest lane.</summary>
    private HoverCard DescribeLaneBytesHover(TimelineBucket bucket, double peakPerSecond, Mechanism lane, TimelineByteLayer plotted)
    {
        string name = EvidenceRowText.MechanismName(lane);
        SessionIntervalByteMeasures? zoomedLane = plotted.Zoomed?.Of(lane);
        bool fine = zoomedLane?.For(bucket.Interval) is not null;
        long generation = fine ? plotted.Zoomed!.Generation : plotted.Overview.Generation;
        string resolution = (fine
                ? string.Create(CultureInfo.CurrentCulture, $"Resolution: this view's own bytes, {zoomedLane!.Columns.Count:N0} buckets")
                : string.Create(CultureInfo.CurrentCulture,
                    $"Resolution: the overview's {plotted.Overview.Lanes[0].Columns.Count:N0} buckets over the whole session"))
            + GenerationNote(generation);
        string domain = plotted.Metric switch
        {
            RankingMetric.BytesSent => $"{name} send records with a session time",
            RankingMetric.BytesReceived => $"{name} receive records with a session time",
            _ => $"every {name} record with a session time, both directions",
        };
        return DescribeBytesHover(bucket, plotted.Metric, $"{name} lane", domain, plotted.Of(lane, bucket.Interval),
            peakPerSecond, "the busiest mechanism lane in this time view", resolution, new() { Mechanism = lane },
            selectionShown: true);
    }

    /// <summary>
    /// What a group's process lane's card says under a byte ranking: its bucket's bytes, from the process's own records,
    /// against the busiest lane including the machine row; its hue is its records' most frequent mechanism, as in records.
    /// </summary>
    private HoverCard DescribeOwnerBytesHover(TimelineBucket bucket, double peakPerSecond, ProcessNode owner, ProcessLaneByteLayer plotted)
    {
        SessionIntervalByteMeasures? lane = plotted.Measures.Of(owner.Id);
        string mechanism = ThemePalette.TokensFor(ThemeResources.CurrentMode, ThemePalette.FamilyOf(bucket.DominantMechanism)).Label;
        string resolution = string.Create(CultureInfo.CurrentCulture,
                $"Resolution: the lanes' own bytes, {plotted.Measures.Lanes[0].Columns.Count:N0} buckets")
            + (plotted.Measures.Lanes[0].Columns.Count < plotted.Measures.Machine.Columns.Count
                ? string.Create(CultureInfo.CurrentCulture,
                    $", coarser than the view's so the group's {plotted.Measures.Lanes.Count:N0} lanes stay within ")
                    + string.Create(CultureInfo.CurrentCulture, $"{SessionTimelineQuery.MaximumProcessLaneCells:N0} cells")
                : string.Empty)
            + GenerationNote(plotted.Measures.Generation);
        string domain = plotted.Metric switch
        {
            RankingMetric.BytesSent => $"send records with a session time canonically owned by {owner.NameWithPid}",
            RankingMetric.BytesReceived => $"receive records with a session time canonically owned by {owner.NameWithPid}",
            _ => $"every record with a session time canonically owned by {owner.NameWithPid}, both directions",
        };
        return DescribeBytesHover(bucket, plotted.Metric, $"{owner.NameWithPid} lane · mostly {mechanism} records", domain,
            lane?.For(bucket.Interval), peakPerSecond, "the busiest visible lane including machine context", resolution,
            new() { Owner = owner.Id }, selectionShown: false);
    }

    /// <summary>
    /// What a process's direction row's card says under a byte ranking: its bucket's bytes, from the process's own records
    /// of that source direction, against the busiest row including the machine row.
    /// </summary>
    private HoverCard DescribeDirectionBytesHover(TimelineBucket bucket, double peakPerSecond, Direction direction,
        DirectionLaneByteLayer plotted)
    {
        string owner = wholeSnapshot.Processes.FirstOrDefault(process => process.Id == plotted.Measures.Owner)?.NameWithPid
            ?? "instance " + plotted.Measures.Owner.ToString()[..8];
        string resolution = string.Create(CultureInfo.CurrentCulture,
                $"Resolution: the rows' own bytes, {plotted.Measures.Machine.Columns.Count:N0} buckets")
            + GenerationNote(plotted.Measures.Generation);
        string records = plotted.Metric switch
        {
            RankingMetric.BytesSent => "send records",
            RankingMetric.BytesReceived => "receive records",
            _ => "records",
        };
        string domain = $"{records} with a session time canonically owned by {owner}, {SourceDirectionDomain(direction)}";
        return DescribeBytesHover(bucket, plotted.Metric, $"{DirectionLabel(direction)} lane", domain,
            plotted.Measures.Of(direction)?.For(bucket.Interval), peakPerSecond,
            "the busiest visible lane including machine context", resolution,
            new() { Owner = plotted.Measures.Owner, Direction = direction }, selectionShown: false,
            meaning: DescribeSourceDirection(direction));
    }

    /// <summary>What the machine row's card says above a focused rung's byte rows: every record's bytes in its bucket.</summary>
    private HoverCard DescribeMachineBytesHover(TimelineBucket bucket, double peakPerSecond, RankingMetric metric,
        SessionIntervalByteMeasures machine, long generation)
    {
        string resolution = string.Create(CultureInfo.CurrentCulture,
                $"Resolution: the machine row's bytes, {machine.Columns.Count:N0} buckets")
            + GenerationNote(generation);
        string domain = metric switch
        {
            RankingMetric.BytesSent => "every send record with a session time",
            RankingMetric.BytesReceived => "every receive record with a session time",
            _ => "every record with a session time, both directions",
        };
        return DescribeBytesHover(bucket, metric, "machine context, not added to the lanes", domain,
            machine.For(bucket.Interval), peakPerSecond, "the busiest visible lane including machine context",
            resolution, IntervalByteScope.Whole, selectionShown: false);
    }

    /// <summary>What a card adds when the bytes it states were read from another generation than the one shown.</summary>
    private string GenerationNote(long generation) => generation != DisplayedGeneration
        ? string.Create(CultureInfo.CurrentCulture, $" · bytes from generation {generation:N0}")
        : string.Empty;

    /// <summary>
    /// A byte lane's card: the records the bucket holds, then what its bar plots - the ranking's bytes over the row's records
    /// in it, the records that measured them and those that recorded no size - and the byte rate its height reads against
    /// the busiest row, closing as every timeline card does.
    /// </summary>
    private HoverCard DescribeBytesHover(TimelineBucket bucket, RankingMetric metric, string row, string domain,
        TransportBytes? bytes, double peakPerSecond, string scale, string resolution, IntervalByteScope scope,
        bool selectionShown, string? meaning = null)
    {
        (string verb, string record, string accounting, string none) = metric switch
        {
            RankingMetric.BytesSent => ("sent", "send", "sender-accounted", "no send recorded"),
            RankingMetric.BytesReceived => ("received", "receive", "receiver-accounted", "no receive recorded"),
            _ => ("sent and received", "record", "endpoint activity, a local transfer at both of its ends", "no transfer recorded"),
        };
        var lines = new List<string>
        {
            bucket.ObservationCount == 0
                ? bucket.Coverage == CoverageState.Covered
                    ? "No record observed · the capture covered this interval, so nothing it collects happened here"
                    : "No record observed · an empty bucket is not proof of inactivity"
                : Counted(bucket.ObservationCount, "observed record", "observed records") + $" · {row}",
            $"Basis: source observations · unit: bytes · domain: transport-observed bytes of {domain} · accounting: {accounting}",
        };
        if (meaning is not null)
        {
            lines.Add(meaning);
        }

        string unmeasured;
        if (bytes is null)
        {
            lines.Add($"Plotted: {Phrase(metric)} not read for this interval yet · the bar here is the rate of a coarser or "
                + "earlier column around it");
            unmeasured = "Unmeasured: not known until this interval's bytes are read";
        }
        else
        {
            (long? value, long measured, long withoutSize) = bytes.ValueOf(metric);
            long span = Math.Max(1, bucket.Interval.SpanTicks);
            if (value is { } sum)
            {
                lines.Add($"Plotted: {WorkspaceRowBuilder.DescribeSize(sum)} {verb} on {Spoken.Count(measured, "measured " + record)}");
                lines.Add($"Rate: {WorkspaceRowBuilder.DescribeByteRate((double)sum * WorkspaceTime.TicksPerSecond / span)} · "
                    + HeightAgainst(scale, WorkspaceRowBuilder.DescribeByteRate(Math.Max(0, peakPerSecond))));
                unmeasured = withoutSize > 0
                    ? $"Unmeasured: {Spoken.Count(withoutSize, "more " + record)} recorded no size, so the bar is a lower bound"
                    : $"Unmeasured: none; every {record} here recorded its size";
            }
            else if (withoutSize > 0)
            {
                lines.Add($"Plotted: unmeasured, drawn cross-hatched · {Spoken.Count(withoutSize, record)} recorded no size, "
                    + "so the value is unknown, not zero");
                unmeasured = $"Unmeasured: {Spoken.Count(withoutSize, record)}, none with a size";
            }
            else
            {
                lines.Add($"Plotted: nothing · {none} in this interval");
                unmeasured = "Unmeasured: none";
            }
        }

        if (selectionShown && highlight is not null && highlightName is { } marked)
        {
            lines.Add($"Selection, {marked}: highlighted while the lanes plot records");
        }

        return FinishTimelineHover(bucket, zoomed: false, lines, scope, resolution, unmeasured, bytes);
    }
}
