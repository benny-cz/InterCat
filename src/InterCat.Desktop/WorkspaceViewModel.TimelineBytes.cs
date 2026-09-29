using System.Globalization;
using InterCat.Application;
using InterCat.Desktop.Presentation;
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
/// The machine rung's timeline under a byte ranking (§6.2: a density cell carries the selected count or a compatible byte
/// sum). The overview counts records and sums no bytes (`overview-index-v1` §4), so while a real session's machine rung is
/// ranked by bytes, its mechanism lanes' bytes are read for the columns drawn - the overview's across the whole session,
/// and the zoomed view's own once it rests - and each lane plots what the ranking measures, per second. A column whose
/// records declared sizes none of them recorded is drawn unmeasured, never as zero (R3). Until the overview's bytes arrive
/// the lanes keep plotting records and the caption says their bytes are being read. A live publication shows the previous
/// one's bytes until its own arrive, as it does its zoomed counts.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    private readonly LaneByteRead overviewLaneBytes = new();
    private readonly LaneByteRead zoomedLaneBytes = new();
    private IReadOnlyList<Mechanism>? laneMechanisms;
    private TimelineByteLayer? timelineBytes;
    private bool timelineDrawsUnmeasured;
    private string? laneBytesNote;

    /// <summary>
    /// What the timeline's mechanism lanes plot under a byte ranking once their bytes are read: the metric and each lane's
    /// bytes by column. Null while the lanes plot records: under any other ranking, at any other rung, and until the
    /// overview's bytes arrive.
    /// </summary>
    public TimelineByteLayer? TimelineBytes => timelineBytes;

    /// <summary>Whether the byte lanes draw a column in view whose size was not measured (§6.6).</summary>
    public bool TimelineDrawsUnmeasured => timelineDrawsUnmeasured;

    /// <summary>Whether a pane draws §6.6's unmeasured value, which the legend then keys: the graph, or the byte lanes.</summary>
    public bool DrawsUnmeasured => GraphDrawsUnmeasured || timelineDrawsUnmeasured;

    /// <summary>Completes when the timeline's latest byte reads have applied, been superseded or failed.</summary>
    public Task TimelineBytesReady => Task.WhenAll(overviewLaneBytes.Ready, zoomedLaneBytes.Ready);

    /// <summary>The columns of one read: the interval they divide and how many.</summary>
    private readonly record struct LaneBytesRequest(TimeRange Interval, int Columns);

    /// <summary>One set of lane columns the timeline reads bytes for: what it shows, the read under way, a failed one.</summary>
    private sealed class LaneByteRead
    {
        /// <summary>The bytes shown for these columns: this generation's, or a stand-in carried from an earlier one.</summary>
        public SessionMechanismByteMeasures? Measures { get; set; }

        /// <summary>The columns <see cref="Measures"/> answer when this generation read them; null for a stand-in.</summary>
        public LaneBytesRequest? Answered { get; set; }

        public (LaneBytesRequest Request, CancellationTokenSource Cancellation)? Running { get; set; }

        public (LaneBytesRequest Request, string Problem)? Failed { get; set; }

        public Task Ready { get; set; } = Task.CompletedTask;

        public void Cancel()
        {
            if (Running is { } running)
            {
                running.Cancellation.Cancel();
                running.Cancellation.Dispose();
                Running = null;
            }
        }
    }

    /// <summary>The byte metric the lanes are asked to plot: a byte ranking at a real session's machine rung, else null.</summary>
    private RankingMetric? LaneByteMetric => ReadsBytes && !disposed && ShowsMechanismLanes && Family == RankingFamily.Bytes
        ? rankBy : null;

    /// <summary>The mechanisms whose lanes the overview draws, in its order.</summary>
    private IReadOnlyList<Mechanism> LaneMechanisms =>
        laneMechanisms ??= Array.AsReadOnly([.. wholeSnapshot.MechanismLanes.Select(lane => lane.Mechanism)]);

    /// <summary>Whether a drawn view shows the whole session, which the overview's own columns answer.</summary>
    private bool ShowsWholeExtent(TimeRange viewport) =>
        viewport.StartTicks <= wholeSnapshot.Extent.StartTicks && viewport.EndTicks >= wholeSnapshot.Extent.EndTicks;

    /// <summary>
    /// Reads the lane bytes the timeline is asked to plot and does not have - the overview's columns, and the zoomed view's
    /// own while it is zoomed - and cancels a read no longer asked for. Called when the ranking, the rung or the drawn view
    /// changes. Leaving the byte ranking forgets a failed read, so choosing it again tries once more.
    /// </summary>
    private void FollowLaneBytes()
    {
        IReadOnlyList<TimelineBucket> overview = wholeSnapshot.Timeline;
        bool plots = LaneByteMetric is not null && overview.Count > 0;
        if (!plots)
        {
            overviewLaneBytes.Failed = null;
            zoomedLaneBytes.Failed = null;
        }

        Follow(overviewLaneBytes, plots
            ? new LaneBytesRequest(new TimeRange(overview[0].Interval.StartTicks, overview[^1].Interval.EndTicks), overview.Count)
            : null);
        Follow(zoomedLaneBytes, plots && drawnTimeline is { } drawn && !ShowsWholeExtent(drawn.Viewport)
            ? new LaneBytesRequest(drawn.Viewport, drawn.Columns)
            : null);
        UpdateTimelineBytes();
    }

    private void Follow(LaneByteRead read, LaneBytesRequest? wanted)
    {
        if (read.Running is { } running && running.Request != wanted)
        {
            read.Cancel();
        }

        if (disposed || wanted is not { } request || evidenceSource is not { } source || read.Answered == request
            || read.Running is not null || read.Failed?.Request == request)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        read.Running = (request, cancellation);
        read.Ready = ReadLaneBytesAsync(read, source, request, cancellation);
    }

    private async Task ReadLaneBytesAsync(
        LaneByteRead read, SessionEvidenceSource source, LaneBytesRequest request, CancellationTokenSource cancellation)
    {
        try
        {
            SessionMechanismByteMeasures measured = await source.LaneBytesAsync(
                request.Interval, request.Columns, LaneMechanisms, cancellation.Token);
            if (disposed || read.Running?.Cancellation != cancellation)
            {
                return;
            }

            read.Running = null;
            cancellation.Dispose();
            if (measured.SessionId == source.SessionId)
            {
                read.Measures = measured;
                read.Answered = request;
                read.Failed = null;
            }
            else
            {
                read.Failed = (request, "the session on disk is another one");
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
            if (disposed || read.Running?.Cancellation != cancellation)
            {
                return;
            }

            read.Running = null;
            cancellation.Dispose();
            read.Failed = (request, exception.Message);
        }

        UpdateTimelineBytes();
    }

    /// <summary>Shows an earlier publication's lane bytes, of the same session, until this generation's own arrive.</summary>
    private void AdoptLaneBytes(SessionMechanismByteMeasures? overview, SessionMechanismByteMeasures? zoomed)
    {
        if (evidenceSource is not { } source)
        {
            return;
        }

        if (overview?.SessionId == source.SessionId && overviewLaneBytes.Answered is null)
        {
            overviewLaneBytes.Measures = overview;
        }

        if (zoomed?.SessionId == source.SessionId && zoomedLaneBytes.Answered is null)
        {
            zoomedLaneBytes.Measures = zoomed;
        }

        UpdateTimelineBytes();
    }

    private void CancelLaneBytes()
    {
        overviewLaneBytes.Cancel();
        zoomedLaneBytes.Cancel();
    }

    /// <summary>Takes up what the lanes plot now, and says what changed: the bytes, the legend's key, the caption.</summary>
    private void UpdateTimelineBytes()
    {
        TimelineByteLayer? layer = LaneByteMetric is { } metric && overviewLaneBytes.Measures is { } overview
            ? new(metric, overview, drawnTimeline is { } drawn && !ShowsWholeExtent(drawn.Viewport) ? zoomedLaneBytes.Measures : null)
            : null;
        bool changed = layer is null
            ? timelineBytes is not null
            : timelineBytes is null || layer.Metric != timelineBytes.Metric
                || !ReferenceEquals(layer.Overview, timelineBytes.Overview) || !ReferenceEquals(layer.Zoomed, timelineBytes.Zoomed);
        if (changed)
        {
            timelineBytes = layer;
            OnPropertyChanged(nameof(TimelineBytes));
        }

        bool unmeasured = layer is not null && DrawsUnmeasuredColumn(layer, drawnTimeline?.Viewport ?? wholeSnapshot.Extent);
        if (unmeasured != timelineDrawsUnmeasured)
        {
            timelineDrawsUnmeasured = unmeasured;
            OnPropertyChanged(nameof(TimelineDrawsUnmeasured));
            OnPropertyChanged(nameof(DrawsUnmeasured));
        }

        string? note = LaneBytesNote;
        if (changed || note != laneBytesNote)
        {
            laneBytesNote = note;
            OnPropertyChanged(nameof(TimelineCaption));
        }
    }

    /// <summary>Whether any lane column in <paramref name="view"/> plots §6.6's unmeasured value.</summary>
    private static bool DrawsUnmeasuredColumn(TimelineByteLayer layer, TimeRange view) =>
        Unmeasured(layer.Overview, layer.Metric, view) || (layer.Zoomed is { } zoomed && Unmeasured(zoomed, layer.Metric, view));

    private static bool Unmeasured(SessionMechanismByteMeasures measures, RankingMetric metric, TimeRange view)
    {
        foreach (SessionIntervalByteMeasures lane in measures.Lanes)
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
        : overviewLaneBytes.Failed is { } failed ? $"{Phrase(metric)} could not be read: {failed.Problem.TrimEnd('.')}"
        : $"reading {Phrase(metric)}…";

    /// <summary>
    /// What a rung whose lanes count records adds to its caption under a byte ranking: that they count records, and where
    /// bytes are plotted, so a lane is never read as a byte volume.
    /// </summary>
    private string LaneRecordsNote => ReadsBytes && ShowsRankingChoice && Family == RankingFamily.Bytes && !ShowsMechanismLanes
        ? " · lanes count records; bytes are plotted at the machine rung"
        : string.Empty;

    /// <summary>
    /// A mechanism lane's card while the lanes plot bytes: the records the bucket holds, then what its bar plots - the
    /// ranking's bytes over the lane's records in it, the records that measured them and those that recorded no size - and
    /// the byte rate its height reads against the busiest lane, closing as every timeline card does.
    /// </summary>
    private HoverCard DescribeLaneBytesHover(TimelineBucket bucket, double peakPerSecond, Mechanism lane, TimelineByteLayer plotted)
    {
        string name = EvidenceRowText.MechanismName(lane);
        (string verb, string record, string domain, string accounting, string none) = plotted.Metric switch
        {
            RankingMetric.BytesSent => ("sent", "send", $"{name} send records", "sender-accounted", "no send recorded"),
            RankingMetric.BytesReceived => ("received", "receive", $"{name} receive records", "receiver-accounted",
                "no receive recorded"),
            _ => ("sent and received", "record", $"every {name} record, both directions",
                "endpoint activity, a local transfer at both of its ends", "no transfer recorded"),
        };
        var lines = new List<string>
        {
            bucket.ObservationCount == 0
                ? bucket.Coverage == CoverageState.Covered
                    ? "No record observed · the capture covered this interval, so nothing it collects happened here"
                    : "No record observed · an empty bucket is not proof of inactivity"
                : Counted(bucket.ObservationCount, "observed record", "observed records") + $" · {name} lane",
            $"Basis: source observations · unit: bytes · domain: transport-observed bytes of {domain} with a session time · "
                + $"accounting: {accounting}",
        };

        TransportBytes? bytes = plotted.Of(lane, bucket.Interval);
        string unmeasured;
        if (bytes is null)
        {
            lines.Add($"Plotted: {Phrase(plotted.Metric)} not read for this interval yet · the bar here is the rate of a "
                + "coarser or earlier column around it");
            unmeasured = "Unmeasured: not known until this interval's bytes are read";
        }
        else
        {
            (long? value, long measured, long withoutSize) = bytes.ValueOf(plotted.Metric);
            long span = Math.Max(1, bucket.Interval.SpanTicks);
            if (value is { } sum)
            {
                lines.Add($"Plotted: {WorkspaceRowBuilder.DescribeSize(sum)} {verb} on {Spoken.Count(measured, "measured " + record)}");
                lines.Add($"Rate: {WorkspaceRowBuilder.DescribeByteRate((double)sum * WorkspaceTime.TicksPerSecond / span)} · height "
                    + "against the busiest mechanism lane in this time view, "
                    + $"{WorkspaceRowBuilder.DescribeByteRate(Math.Max(0, peakPerSecond))} (shared scale)");
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

        if (highlight is not null && highlightName is { } marked)
        {
            lines.Add($"Selection, {marked}: highlighted while the lanes plot records");
        }

        SessionIntervalByteMeasures? zoomedLane = plotted.Zoomed?.Of(lane);
        bool fine = zoomedLane?.For(bucket.Interval) is not null;
        long generation = fine ? plotted.Zoomed!.Generation : plotted.Overview.Generation;
        string resolution = (fine
                ? string.Create(CultureInfo.CurrentCulture, $"Resolution: this view's own bytes, {zoomedLane!.Columns.Count:N0} buckets")
                : string.Create(CultureInfo.CurrentCulture,
                    $"Resolution: the overview's {plotted.Overview.Lanes[0].Columns.Count:N0} buckets over the whole session"))
            + (generation != DisplayedGeneration
                ? string.Create(CultureInfo.CurrentCulture, $" · bytes from generation {generation:N0}")
                : string.Empty);
        return FinishTimelineHover(bucket, fine, lines, new() { Mechanism = lane }, resolution, unmeasured, bytes);
    }
}
