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

    /// <summary>
    /// Each column's interval in the session's own time, as its records were counted there: what opening the column shows
    /// of the session. Parallel to <see cref="Buckets"/>.
    /// </summary>
    public IReadOnlyList<TimeRange> OwnIntervals { get; init; } = [];
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
    /// <summary>Whether the two are of one host only by a person's confirmation: their own identities differ (§8.3).</summary>
    public bool ByConfirmation { get; init; }

    /// <summary>The overlap in words.</summary>
    public string Statement(IFormatProvider? culture = null)
    {
        IFormatProvider format = culture ?? CultureInfo.CurrentCulture;
        string pair = $"Sessions {First.ToString("N")[..8]} and {Second.ToString("N")[..8]}";
        string host = ByConfirmation ? "one host, by a person's confirmation," : "one host";
        string span = Shared is { } shared
            ? " for " + OperationText.Duration(checked((shared.EndTicks - shared.StartTicks) * 100), format)
            : string.Empty;
        return Kind switch
        {
            OverlapKind.Concurrent => $"{pair} of {host} ran at once{span}: records of one event may be in both, so no count "
                + "across them is summed.",
            OverlapKind.Possible => $"{pair} of {host} may have run at once: they are nearer than their uncertainty.",
            OverlapKind.Contradictory => $"{pair} recorded two boots of {host.TrimEnd(',')}, yet overlap{span} in the investigation's time, "
                + "which two boots cannot: one of their alignments is wrong.",
            _ => $"{pair} were recorded on {host.TrimEnd(',')}, but not both have a place with a known uncertainty, so whether they ran "
                + "at once is unknown.",
        };
    }
}

/// <summary>
/// A note on the merged time: pinned at an instant of a session, that instant's lane and place in the investigation's time
/// with its uncertainty - or none, when its session has no place - or about the whole investigation.
/// </summary>
public sealed record InvestigationNote(WorkspaceNote Note, int? Lane, long? Ticks, TimeUncertainty? Uncertainty);

/// <summary>An investigation's merged time (§8.2): each session a lane on one axis, the investigation's own.</summary>
public sealed record InvestigationTimelineView(TimeRange? Interval, int Columns, IReadOnlyList<InvestigationLane> Lanes)
{
    /// <summary>Two captures of one host that ran, or may have run, at once, or whose overlap is unknown (§8.4).</summary>
    public IReadOnlyList<WorkspaceOverlap> Overlaps { get; init; } = [];

    /// <summary>The investigation's notes in force, each where it is pinned (§8.4).</summary>
    public IReadOnlyList<InvestigationNote> Notes { get; init; } = [];
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
            if (placement is not { Extent: { } extent, Store: { } store, Chain: { } chain })
            {
                lanes.Add(new(placement.SessionId, null, [], null, placement.Gap, placement.Unread));
                continue;
            }

            IReadOnlyList<TimelineBucket> buckets = [];
            IReadOnlyList<TimeRange> own = [];
            if (whole is { } axis)
            {
                // A mapping is affine, so the axis's uniform grid is a uniform grid of the session's own time: its columns
                // are read there and placed back through the mapping.
                SessionTimelineDetail detail = SessionTimelineQuery.Detail(
                    store, new TimeRange(Back(chain, axis.StartTicks), Back(chain, axis.EndTicks)), columns, cancellationToken);
                buckets = [.. detail.Buckets.Select(bucket => bucket with
                {
                    Interval = new TimeRange(Forward(chain, bucket.Interval.StartTicks), Forward(chain, bucket.Interval.EndTicks)),
                })];
                own = [.. detail.Buckets.Select(bucket => bucket.Interval)];
            }

            lanes.Add(new(placement.SessionId, extent, buckets, placement.Uncertainty,
                placement.Uncertainty is null ? WorkspaceTimeGap.DriftUnknown : WorkspaceTimeGap.None, null)
            {
                OwnIntervals = own,
            });
        }

        return new(whole, columns, lanes) { Overlaps = Overlaps(placements), Notes = Notes(workspacePath, placements) };
    }

    /// <summary>Each note in force where it is pinned: its session's lane and its instant placed through the session's chain.</summary>
    private static InvestigationNote[] Notes(string workspacePath, List<Placement> placements)
    {
        InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(Path.GetFullPath(workspacePath));
        return [.. InvestigationWorkspace.NotesInForce(workspace).Select(note =>
        {
            if (note.At is not { } at)
            {
                return new InvestigationNote(note, null, null, null);
            }

            int lane = placements.FindIndex(placement => placement.SessionId == at.SessionId);
            return lane >= 0 && placements[lane].Chain is { } chain
                ? new InvestigationNote(note, lane, Ticks(chain.ToWorkspace(at.Nanoseconds)), chain.UncertaintyAt(at.Nanoseconds).Uncertainty)
                : new InvestigationNote(note, lane >= 0 ? lane : null, null, null);
        })];
    }

    /// <summary>
    /// Every pair of an investigation's captures of one host identity that ran at once, may have, cannot have but seem to,
    /// or cannot be compared (§8.4, `contracts/workspace-v9.md` §5). Pairs of two hosts are never compared: their records
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

                // Each end is compared with the other's start as their chains allow - what they are aligned through counts
                // once: they certainly overlap only when each reaches past the other's start by more than that pair's
                // uncertainty, and may overlap when a gap between them is within it.
                if (a is not { Extent: { } x, Own: { } ownA, Chain: { } chainA, Uncertainty: not null }
                    || b is not { Extent: { } y, Own: { } ownB, Chain: { } chainB, Uncertainty: not null }
                    || ClockChain.Compare(chainA, ownA.EndTicks * NanosecondsPerTick, chainB, ownB.StartTicks * NanosecondsPerTick)
                        is not { DifferenceNanoseconds: { } endA, Uncertainty: { } pairA }
                    || ClockChain.Compare(chainB, ownB.EndTicks * NanosecondsPerTick, chainA, ownA.StartTicks * NanosecondsPerTick)
                        is not { DifferenceNanoseconds: { } endB, Uncertainty: { } pairB })
                {
                    overlaps.Add(new(a.SessionId, b.SessionId, OverlapKind.Unknown, null) { ByConfirmation = a.Identity != b.Identity });
                    continue;
                }

                // Each difference runs from an end to the other's start, so a negative one is that end reaching past it.
                (double reachA, double reachB) = (-endA, -endB);
                TimeRange? shared = reachA > 0 && reachB > 0
                    ? new TimeRange(Math.Max(x.StartTicks, y.StartTicks), Math.Min(x.EndTicks, y.EndTicks))
                    : null;
                bool twoBoots = a.BootToken is { } bootA && b.BootToken is { } bootB && bootA != bootB;
                if (reachA > pairA.HalfWidthNanoseconds && reachB > pairB.HalfWidthNanoseconds)
                {
                    overlaps.Add(new(a.SessionId, b.SessionId, twoBoots ? OverlapKind.Contradictory : OverlapKind.Concurrent, shared)
                    {
                        ByConfirmation = a.Identity != b.Identity,
                    });
                }
                else if (reachA > -pairA.HalfWidthNanoseconds && reachB > -pairB.HalfWidthNanoseconds && !twoBoots)
                {
                    overlaps.Add(new(a.SessionId, b.SessionId, OverlapKind.Possible, shared) { ByConfirmation = a.Identity != b.Identity });
                }
            }
        }

        return [.. overlaps];
    }

    /// <summary>
    /// Where each member falls in the investigation's time: its session's record extent placed by its alignment, with the
    /// widest uncertainty over it and the boot its capture recorded; or why it has no place.
    /// </summary>
    private static List<Placement> Placements(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        string full = Path.GetFullPath(workspacePath);
        InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(full);
        IReadOnlyDictionary<Guid, ClockChain> chains = InvestigationWorkspace.Chains(workspace);
        var placements = new List<Placement>(workspace.Members.Count);
        foreach (WorkspaceMemberResolution resolution in InvestigationWorkspace.Resolve(full, workspace, cancellationToken))
        {
            WorkspaceMember member = resolution.Member;
            var none = new Placement(member.SessionId, InvestigationWorkspace.HostKey(workspace, member.HostId), null, null, null, null,
                null, null, WorkspaceTimeGap.None, null)
            {
                Identity = member.HostId,
            };
            if (!resolution.HoldsItsCapture)
            {
                placements.Add(none with { Unread = $"it is {resolution.State.ToString().ToLowerInvariant()}: {resolution.Reason}" });
                continue;
            }

            if (!chains.TryGetValue(member.SessionId, out ClockChain? chain))
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

                // The extent is placed through the member's chain to the presentation tick, finer than any column.
                TimeUncertainty? widest = chain.WidestUncertainty(
                    checked(own.StartTicks * NanosecondsPerTick), checked(own.EndTicks * NanosecondsPerTick)).Uncertainty;
                placements.Add(none with
                {
                    Store = store,
                    Extent = new TimeRange(Forward(chain, own.StartTicks), Forward(chain, own.EndTicks)),
                    Own = own,
                    Chain = chain,
                    Uncertainty = widest,
                    BootToken = boot,
                    Gap = widest is null ? WorkspaceTimeGap.DriftUnknown : WorkspaceTimeGap.None,
                });
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                placements.Add(none with { Unread = "it could not be read: " + exception.Message });
            }
        }

        return placements;
    }

    /// <summary>A session tick placed in the investigation's time; offsets alone move it by whole ticks, as they always did.</summary>
    private static long Forward(ClockChain chain, long ticks) => chain.Links.All(link => link.Mapping.Scale == 1)
        ? checked(ticks + Ticks(Offset(chain)))
        : Ticks(chain.ToWorkspace(checked(ticks * NanosecondsPerTick)));

    /// <summary>The session tick an investigation tick maps from.</summary>
    private static long Back(ClockChain chain, long ticks) => chain.Links.All(link => link.Mapping.Scale == 1)
        ? checked(ticks - Ticks(Offset(chain)))
        : Ticks(chain.FromWorkspace(checked(ticks * NanosecondsPerTick)));

    private static long Offset(ClockChain chain) => chain.Links.Aggregate(0L, (sum, link) => checked(sum + link.Mapping.OffsetNanoseconds));

    private static long Ticks(long nanoseconds) => (long)Math.Round(nanoseconds / (double)NanosecondsPerTick, MidpointRounding.ToEven);

    private sealed record Placement(
        Guid SessionId,
        Guid HostId,
        SessionStore? Store,
        TimeRange? Extent,
        TimeRange? Own,
        ClockChain? Chain,
        TimeUncertainty? Uncertainty,
        Guid? BootToken,
        WorkspaceTimeGap Gap,
        string? Unread)
    {
        /// <summary>Its own host identity; <see cref="HostId"/> stands for every identity confirmed one host with it.</summary>
        public Guid Identity { get; init; }
    }
}
