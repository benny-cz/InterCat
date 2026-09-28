using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// One session of an investigation as its timeline shows it: its records counted over the investigation's columns once
/// placed in the investigation's time, with the uncertainty of that placement; or why it has no place.
/// </summary>
public sealed record InvestigationLane(
    Guid SessionId,
    TimeRange? Extent,
    IReadOnlyList<TimelineBucket> Buckets,
    TimeUncertainty? Uncertainty,
    WorkspaceTimeGap Gap,
    string? Unread)
{
    /// <summary>Whether its records have a place in the investigation's time.</summary>
    public bool Placed => Extent is not null && Buckets.Count > 0;

    /// <summary>Its records over the investigation's interval.</summary>
    public long Records => Buckets.Sum(bucket => (long)bucket.ObservationCount);
}

/// <summary>An investigation's merged time (§8.2): each session a lane on one axis, the investigation's own.</summary>
public sealed record InvestigationTimelineView(TimeRange? Interval, int Columns, IReadOnlyList<InvestigationLane> Lanes);

/// <summary>
/// Places an investigation's sessions on one time axis, the investigation's (§8.2, M4 "merged time navigation"). Each
/// session with a place - the time reference, or an aligned session - is counted over the same columns of the
/// investigation's time, mapped back into its own session time, so its bars are its own records where the alignment puts
/// them; its placement's uncertainty is stated beside it, and a session with no place is listed with the reason. Nothing
/// is rewritten: a session's timestamps stay its own (I9).
/// </summary>
public static class InvestigationTimeline
{
    /// <summary>Presentation ticks are 100 ns (`WorkspaceModels`); alignments are in nanoseconds.</summary>
    private const long NanosecondsPerTick = 100;

    public static InvestigationTimelineView Read(
        string workspacePath,
        int columns,
        TimeRange? interval = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, SessionTimelineQuery.MaximumColumns);
        string full = Path.GetFullPath(workspacePath);
        InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(full);
        IReadOnlyDictionary<Guid, ClockMapping> mappings = InvestigationWorkspace.Mappings(workspace);
        var placed = new List<(Guid Session, SessionStore Store, long OffsetTicks, TimeRange Extent, TimeUncertainty? Uncertainty)>();
        var lanes = new Dictionary<Guid, InvestigationLane>();
        foreach (WorkspaceMemberResolution resolution in InvestigationWorkspace.Resolve(full, workspace, cancellationToken))
        {
            Guid session = resolution.Member.SessionId;
            if (!resolution.HoldsItsCapture)
            {
                lanes[session] = new(session, null, [], null, WorkspaceTimeGap.None,
                    $"it is {resolution.State.ToString().ToLowerInvariant()}: {resolution.Reason}");
                continue;
            }

            if (!mappings.TryGetValue(session, out ClockMapping? mapping))
            {
                lanes[session] = new(session, null, [], null,
                    workspace.TimeReference is null ? WorkspaceTimeGap.NoTimeReference : WorkspaceTimeGap.NotAligned, null);
                continue;
            }

            try
            {
                SessionStore store = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(resolution.FullPath));
                if (SessionOverviewProjector.Project(store, cancellationToken: cancellationToken).Extent is not { } own)
                {
                    lanes[session] = new(session, null, [], null, WorkspaceTimeGap.None, "it holds no record with a session time");
                    continue;
                }

                // Alignments are offsets with no rate, so one uniform grid of the investigation's time is one uniform grid of
                // the session's own; the offset is placed to the presentation tick, finer than any column.
                long offset = (long)Math.Round(mapping.OffsetNanoseconds / (double)NanosecondsPerTick, MidpointRounding.ToEven);
                TimeUncertainty? start = mapping.UncertaintyAt(checked(own.StartTicks * NanosecondsPerTick));
                TimeUncertainty? end = mapping.UncertaintyAt(checked(own.EndTicks * NanosecondsPerTick));
                TimeUncertainty? widest = start is { } a && end is { } b ? (a.HalfWidthNanoseconds >= b.HalfWidthNanoseconds ? a : b) : null;
                placed.Add((session, store, offset, new TimeRange(checked(own.StartTicks + offset), checked(own.EndTicks + offset)), widest));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                lanes[session] = new(session, null, [], null, WorkspaceTimeGap.None, "it could not be read: " + exception.Message);
            }
        }

        TimeRange? whole = interval ?? (placed.Count == 0
            ? null
            : new TimeRange(placed.Min(lane => lane.Extent.StartTicks), placed.Max(lane => lane.Extent.EndTicks)));
        foreach ((Guid session, SessionStore store, long offset, TimeRange extent, TimeUncertainty? uncertainty) in placed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<TimelineBucket> buckets = [];
            if (whole is { } axis)
            {
                SessionTimelineDetail detail = SessionTimelineQuery.Detail(
                    store, new TimeRange(checked(axis.StartTicks - offset), checked(axis.EndTicks - offset)), columns, cancellationToken);
                buckets = [.. detail.Buckets.Select(bucket => bucket with
                {
                    Interval = new TimeRange(checked(bucket.Interval.StartTicks + offset), checked(bucket.Interval.EndTicks + offset)),
                })];
            }

            lanes[session] = new(session, extent, buckets, uncertainty,
                uncertainty is null ? WorkspaceTimeGap.DriftUnknown : WorkspaceTimeGap.None, null);
        }

        return new(whole, columns, [.. workspace.Members.Select(member => lanes[member.SessionId])]);
    }
}
