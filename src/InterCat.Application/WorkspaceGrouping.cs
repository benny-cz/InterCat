using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// How the window groups a session's processes (§6.3, §6's lane grouping): by the executable their records name, as the
/// overview is projected, or by the terminal session their lifecycle records name (`metrics-v1` §6). Regrouping reads no
/// record; the measures a group's total cannot be summed from - its distinct peers and its call times - are read for every
/// grouping at once, under keys that never collide, so a change of grouping reads nothing again either.
/// </summary>
public static class WorkspaceGrouping
{
    /// <summary>The key of the group of processes whose lifecycle records named no terminal session.</summary>
    public const string UnrecordedSessionKey = "session:unknown";

    /// <summary>Every grouping the window can offer, in the order it lists them.</summary>
    public static IReadOnlyList<LaneGrouping> All { get; } = [LaneGrouping.Executable, LaneGrouping.UserSession];

    /// <summary>A terminal session's group key, or the unrecorded group's when no session was named.</summary>
    public static string SessionKey(uint? session) => session is { } named
        ? "session:" + named.ToString(CultureInfo.InvariantCulture)
        : UnrecordedSessionKey;

    /// <summary>The key of the group <paramref name="instance"/> belongs to under <paramref name="grouping"/>.</summary>
    public static string KeyOf(ProcessInstance instance, LaneGrouping grouping)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return grouping switch
        {
            LaneGrouping.Executable => SessionOverviewProjector.GroupKey(instance),
            LaneGrouping.UserSession => SessionKey(instance.SessionId),
            _ => throw new ArgumentOutOfRangeException(nameof(grouping), grouping, "The window groups by executable or session."),
        };
    }

    /// <summary>
    /// The identity of a projected graph grouped by <paramref name="grouping"/>: the projection's own by executable, and
    /// marked by session, since its group nodes and their relationships differ, so no layout made for one is applied to
    /// the other.
    /// </summary>
    public static string Identity(string graphIdentity, LaneGrouping grouping)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphIdentity);
        return grouping switch
        {
            LaneGrouping.Executable => graphIdentity,
            LaneGrouping.UserSession => graphIdentity + "|grouped-by:session",
            _ => throw new ArgumentOutOfRangeException(nameof(grouping), grouping, "The window groups by executable or session."),
        };
    }

    /// <summary>
    /// The groupings a snapshot's processes can be grouped by: by executable always, and by terminal session once one of
    /// its processes' lifecycle records names one - a grouping every process of which is unrecorded tells nothing.
    /// </summary>
    public static IReadOnlyList<LaneGrouping> Offered(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Processes.Any(process => process.TerminalSession is not null) ? All : [LaneGrouping.Executable];
    }

    /// <summary>How a snapshot's processes are grouped now: by session when its groups are sessions, else by executable.</summary>
    public static LaneGrouping Of(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Groups.Any(group => group.Kind == LaneGrouping.UserSession) ? LaneGrouping.UserSession : LaneGrouping.Executable;
    }

    /// <summary>
    /// A snapshot projected by executable, grouped by <paramref name="grouping"/>: unchanged by executable, and by session
    /// one group per terminal session its processes name, in the session's order, then the processes naming none.
    /// </summary>
    public static WorkspaceSnapshot Regroup(WorkspaceSnapshot projected, LaneGrouping grouping)
    {
        ArgumentNullException.ThrowIfNull(projected);
        if (Of(projected) != LaneGrouping.Executable)
        {
            throw new ArgumentException("Only a snapshot grouped by executable, as projected, is regrouped.", nameof(projected));
        }

        if (grouping == LaneGrouping.Executable)
        {
            return projected;
        }

        if (grouping != LaneGrouping.UserSession)
        {
            throw new ArgumentOutOfRangeException(nameof(grouping), grouping, "The window groups by executable or session.");
        }

        ProcessNode[] processes = [.. projected.Processes.Select(process => process with { GroupKey = SessionKey(process.TerminalSession) })];
        ProcessGroup[] groups = [.. projected.Processes
            .Select(process => process.TerminalSession)
            .Distinct()
            .OrderBy(session => session is null)
            .ThenBy(session => session)
            .Select(session => new ProcessGroup(
                SessionKey(session),
                session is { } named ? GroupingText.Session(named) : GroupingText.SessionNotRecorded,
                LaneGrouping.UserSession))];
        return projected with { Groups = groups, Processes = processes };
    }
}
