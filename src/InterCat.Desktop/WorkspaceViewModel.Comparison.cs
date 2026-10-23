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
