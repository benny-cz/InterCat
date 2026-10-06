using InterCat.Application;

namespace InterCat.Desktop;

/// <summary>
/// §6.2's normalization scope: whether the timeline's lanes share one scale, so their heights compare, or each lane's
/// bars are read against its own busiest bar, so a quiet lane's shape shows beside a busy one.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    private bool scalesEachLane;

    /// <summary>
    /// Whether each timeline lane's bars are read against that lane's own busiest visible bar rather than one scale every
    /// lane shares (§6.2). Shared is the default, since only it lets heights compare across lanes; each lane's own shows a
    /// quiet lane's shape beside a busy one, which the legend, the axis and every card then say.
    /// </summary>
    public bool ScalesEachLane
    {
        get => scalesEachLane;
        set
        {
            if (scalesEachLane == value || disposed) return;
            scalesEachLane = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LaneScaleText));
        }
    }

    /// <summary>
    /// Whether the timeline draws lanes whose heights the scale's scope changes: the machine's mechanism lanes, or a rung's
    /// rows beneath its machine context. An operation lane's calls are drawn on a density scale of their own.
    /// </summary>
    public bool OffersLaneScale => ShowsMechanismLanes || ShowsProcessLanes || ShowsDirectionLanes || ShowsChannelEndLanes;

    /// <summary>
    /// The legend's key to the lanes' heights (§6.2, §6.8): what they plot, per second, and the scale they are read
    /// against - "records per second, one scale for every lane".
    /// </summary>
    public string LaneScaleText => (PlottedByteMetric is { } metric ? Phrase(metric) : "records") + " per second, "
        + (scalesEachLane ? "each lane to its own peak" : "one scale for every lane");

    /// <summary>The byte sum the drawn lanes plot in place of their records, when a byte ranking has them plot one.</summary>
    private RankingMetric? PlottedByteMetric => ShowsMechanismLanes ? timelineBytes?.Metric
        : ShowsProcessLanes ? processLaneBytes?.Metric
        : ShowsDirectionLanes ? directionBytes?.Metric
        : null;

    /// <summary>
    /// The end of a lane card's rate line: what its height is read against. Under one scale, <paramref name="shared"/>,
    /// the busiest bar every lane is read against; under each lane's own, the lane's own busiest bar, whose heights no
    /// other lane's compare with.
    /// </summary>
    private string HeightAgainst(string shared, string peak) => scalesEachLane && OffersLaneScale
        ? $"height against this lane's own busiest visible bar, {peak} (each lane on its own scale: compare lanes by their numbers, not their heights)"
        : $"height against {shared}, {peak} (shared scale)";
}
