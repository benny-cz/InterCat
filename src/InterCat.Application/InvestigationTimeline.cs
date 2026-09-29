using System.Globalization;
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

/// <summary>What two captures of one host are to each other in the investigation's time (§8.4).</summary>
public enum OverlapKind
{
    /// <summary>They ran at once, beyond their uncertainty: records of one event may be in both.</summary>
    Concurrent = 1,

    /// <summary>They are nearer than their uncertainty, so they may have run at once.</summary>
    Possible = 2,

    /// <summary>They recorded two boots of their host, yet overlap beyond their uncertainty, which they cannot: an alignment is wrong.</summary>
    Contradictory = 3,

    /// <summary>Not both have a place with a known uncertainty, so whether they overlap is unknown.</summary>
    Unknown = 4,
}

/// <summary>
/// Two captures of one host identity, and what their extents in the investigation's time say of each other (§8.4): a
/// partial overlap is flagged, never deduplicated by time, so no count across the two is summed.
/// </summary>
public sealed record WorkspaceOverlap(Guid First, Guid Second, OverlapKind Kind, TimeRange? Shared)
{
    /// <summary>The overlap in words.</summary>
    public string Statement(IFormatProvider? culture = null)
    {
        IFormatProvider format = culture ?? CultureInfo.CurrentCulture;
        string pair = $"Sessions {First.ToString("N")[..8]} and {Second.ToString("N")[..8]}";
        string span = Shared is { } shared
            ? " for " + OperationText.Duration(checked((shared.EndTicks - shared.StartTicks) * 100), format)
            : string.Empty;
        return Kind switch
        {
            OverlapKind.Concurrent => $"{pair} of one host ran at once{span}: records of one event may be in both, so no count "
                + "across them is summed.",
            OverlapKind.Possible => $"{pair} of one host may have run at once: they are nearer than their uncertainty.",
            OverlapKind.Contradictory => $"{pair} recorded two boots of one host, yet overlap{span} in the investigation's time, "
                + "which two boots cannot: one of their alignments is wrong.",
            _ => $"{pair} were recorded on one host, but not both have a place with a known uncertainty, so whether they ran "
                + "at once is unknown.",
        };
    }
}

/// <summary>An investigation's merged time (§8.2): each session a lane on one axis, the investigation's own.</summary>
public sealed record InvestigationTimelineView(TimeRange? Interval, int Columns, IReadOnlyList<InvestigationLane> Lanes)
{
    /// <summary>Two captures of one host that ran, or may have run, at once, or whose overlap is unknown (§8.4).</summary>
    public IReadOnlyList<WorkspaceOverlap> Overlaps { get; init; } = [];
}

/// <summary>
/// Places an investigation's sessions on one time axis, the investigation's (§8.2, M4 "merged time navigation"). Each
/// session with a place - the time reference, or an aligned session - is counted over the same columns of the
/// investigation's time, mapped back into its own session time, so its bars are its own records where the alignment puts
/// them; its placement's uncertainty is stated beside it, and a session with no place is listed with the reason. Nothing
/// is rewritten: a session's timestamps stay its own (I9). Two captures of one host are compared for overlap (§8.4).
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
        List<Placement> placements = Placements(workspacePath, cancellationToken);
        Placement[] placed = [.. placements.Where(placement => placement.Extent is not null)];
        TimeRange? whole = interval ?? (placed.Length == 0
            ? null
            : new TimeRange(placed.Min(lane => lane.Extent!.Value.StartTicks), placed.Max(lane => lane.Extent!.Value.EndTicks)));
        var lanes = new List<InvestigationLane>(placements.Count);
        foreach (Placement placement in placements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (placement is not { Extent: { } extent, Store: { } store })
            {
                lanes.Add(new(placement.SessionId, null, [], null, placement.Gap, placement.Unread));
                continue;
            }

            IReadOnlyList<TimelineBucket> buckets = [];
            if (whole is { } axis)
            {
                long offset = placement.OffsetTicks;
                SessionTimelineDetail detail = SessionTimelineQuery.Detail(
                    store, new TimeRange(checked(axis.StartTicks - offset), checked(axis.EndTicks - offset)), columns, cancellationToken);
                buckets = [.. detail.Buckets.Select(bucket => bucket with
                {
                    Interval = new TimeRange(checked(bucket.Interval.StartTicks + offset), checked(bucket.Interval.EndTicks + offset)),
                })];
            }

            lanes.Add(new(placement.SessionId, extent, buckets, placement.Uncertainty,
                placement.Uncertainty is null ? WorkspaceTimeGap.DriftUnknown : WorkspaceTimeGap.None, null));
        }

        return new(whole, columns, lanes) { Overlaps = Overlaps(placements) };
    }

    /// <summary>
    /// Every pair of an investigation's captures of one host identity that ran at once, may have, cannot have but seem to,
    /// or cannot be compared (§8.4, `contracts/workspace-v4.md` §5). Pairs of two hosts are never compared: their records
    /// are of two machines' events.
    /// </summary>
    public static IReadOnlyList<WorkspaceOverlap> Overlaps(string workspacePath, CancellationToken cancellationToken = default)
    {
        List<Placement> placements = Placements(workspacePath, cancellationToken);
        return Overlaps(placements);
    }

    private static WorkspaceOverlap[] Overlaps(List<Placement> placements)
    {
        var overlaps = new List<WorkspaceOverlap>();
        for (int first = 0; first < placements.Count; first++)
        {
            for (int second = first + 1; second < placements.Count; second++)
            {
                (Placement a, Placement b) = (placements[first], placements[second]);
                if (a.HostId != b.HostId)
                {
                    continue;
                }

                if (a is not { Extent: { } x, Uncertainty: { } ux } || b is not { Extent: { } y, Uncertainty: { } uy })
                {
                    overlaps.Add(new(a.SessionId, b.SessionId, OverlapKind.Unknown, null));
                    continue;
                }

                // Each extent may lie as far off as its placement's uncertainty: they certainly overlap only when each reaches
                // past the other's start by more than the pair's uncertainty, and may overlap when their gap is within it.
                double pair = TimeUncertainty.Pair(ux, uy).HalfWidthNanoseconds / NanosecondsPerTick;
                double reach = Math.Min(x.EndTicks - y.StartTicks, y.EndTicks - x.StartTicks);
                TimeRange? shared = reach > 0
                    ? new TimeRange(Math.Max(x.StartTicks, y.StartTicks), Math.Min(x.EndTicks, y.EndTicks))
                    : null;
                bool twoBoots = a.BootToken is { } bootA && b.BootToken is { } bootB && bootA != bootB;
                if (reach > pair)
                {
                    overlaps.Add(new(a.SessionId, b.SessionId, twoBoots ? OverlapKind.Contradictory : OverlapKind.Concurrent, shared));
                }
                else if (reach > -pair && !twoBoots)
                {
                    overlaps.Add(new(a.SessionId, b.SessionId, OverlapKind.Possible, shared));
                }
            }
        }

        return [.. overlaps];
    }

    /// <summary>
    /// Where each member falls in the investigation's time: its session's record extent shifted by its alignment, with the
    /// widest uncertainty at its ends and the boot its capture recorded; or why it has no place.
    /// </summary>
    private static List<Placement> Placements(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        string full = Path.GetFullPath(workspacePath);
        InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(full);
        IReadOnlyDictionary<Guid, ClockMapping> mappings = InvestigationWorkspace.Mappings(workspace);
        var placements = new List<Placement>(workspace.Members.Count);
        foreach (WorkspaceMemberResolution resolution in InvestigationWorkspace.Resolve(full, workspace, cancellationToken))
        {
            WorkspaceMember member = resolution.Member;
            var none = new Placement(member.SessionId, member.HostId, null, null, 0, null, null, WorkspaceTimeGap.None, null);
            if (!resolution.HoldsItsCapture)
            {
                placements.Add(none with { Unread = $"it is {resolution.State.ToString().ToLowerInvariant()}: {resolution.Reason}" });
                continue;
            }

            if (!mappings.TryGetValue(member.SessionId, out ClockMapping? mapping))
            {
                placements.Add(none with
                {
                    Gap = workspace.TimeReference is null ? WorkspaceTimeGap.NoTimeReference : WorkspaceTimeGap.NotAligned,
                });
                continue;
            }

            try
            {
                SessionStore store = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(resolution.FullPath));
                Guid? boot = store.Current is { } manifest ? ClockCalibrationV1.Read(store.Root, manifest)?.BootToken : null;
                if (SessionOverviewProjector.Project(store, cancellationToken: cancellationToken).Extent is not { } own)
                {
                    placements.Add(none with { Unread = "it holds no record with a session time", BootToken = boot });
                    continue;
                }

                // Alignments are offsets with no rate, so one uniform grid of the investigation's time is one uniform grid of
                // the session's own; the offset is placed to the presentation tick, finer than any column.
                long offset = (long)Math.Round(mapping.OffsetNanoseconds / (double)NanosecondsPerTick, MidpointRounding.ToEven);
                TimeUncertainty? start = mapping.UncertaintyAt(checked(own.StartTicks * NanosecondsPerTick));
                TimeUncertainty? end = mapping.UncertaintyAt(checked(own.EndTicks * NanosecondsPerTick));
                TimeUncertainty? widest = start is { } a && end is { } b ? (a.HalfWidthNanoseconds >= b.HalfWidthNanoseconds ? a : b) : null;
                placements.Add(new(member.SessionId, member.HostId, store,
                    new TimeRange(checked(own.StartTicks + offset), checked(own.EndTicks + offset)), offset, widest, boot,
                    widest is null ? WorkspaceTimeGap.DriftUnknown : WorkspaceTimeGap.None, null));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                placements.Add(none with { Unread = "it could not be read: " + exception.Message });
            }
        }

        return placements;
    }

    private sealed record Placement(
        Guid SessionId,
        Guid HostId,
        SessionStore? Store,
        TimeRange? Extent,
        long OffsetTicks,
        TimeUncertainty? Uncertainty,
        Guid? BootToken,
        WorkspaceTimeGap Gap,
        string? Unread);
}
