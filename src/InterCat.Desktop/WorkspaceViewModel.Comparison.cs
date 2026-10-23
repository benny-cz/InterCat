using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// What the window offers for comparing the time scope with another interval (`contracts/comparison-v1.md`, plan M12):
/// whether it can, the time a baseline is chosen within, and the way to a process a comparison's row names.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>
    /// Whether the time scope can be compared with another interval: a session's, never the tour's, whose scope is an
    /// interval - the brush, or the visible range while the timeline is zoomed (§6.4).
    /// </summary>
    public bool CanCompareIntervals => evidenceSource is not null && ScopeInterval is not null;

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
}
