using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// How the window groups a session's processes (§6.3, §6's lane grouping): by the executable their records name, as the
/// overview is projected, by the terminal session their lifecycle records name, or by service container - each host of the
/// services its capture listed a group of its own, every other process its executable's (`metrics-v1` §6, ADR-096).
/// Regrouping reads no record; the measures a group's total cannot be summed from - its distinct peers and its call times -
/// are read for every grouping at once, under keys that never collide, so a change of grouping reads nothing again either.
/// </summary>
public static class WorkspaceGrouping
{
    /// <summary>The key of the group of processes whose lifecycle records named no terminal session.</summary>
    public const string UnrecordedSessionKey = "session:unknown";

    /// <summary>
    /// Grouped by service container, the key of the group of the processes a listed service may have run in, where no
    /// creation time tells: together, rather than in a container guessed by PID or an executable's group (ADR-096).
    /// </summary>
    public const string ServiceHostUnknownKey = "container:unknown";

    /// <summary>Every grouping the window can offer, in the order it lists them.</summary>
    public static IReadOnlyList<LaneGrouping> All { get; } =
        [LaneGrouping.Executable, LaneGrouping.UserSession, LaneGrouping.ServiceContainer];

    /// <summary>A terminal session's group key, or the unrecorded group's when no session was named.</summary>
    public static string SessionKey(uint? session) => session is { } named
        ? "session:" + named.ToString(CultureInfo.InvariantCulture)
        : UnrecordedSessionKey;

    /// <summary>A service container's group key: its host's identity.</summary>
    public static string ContainerKey(ProcessInstanceId host) => "container:" + host;

    /// <summary>
    /// Grouped by service container, the key of the group of an executable's processes that host none: the executable's key,
    /// marked, since its members are not the executable grouping's - a host of services is a container of its own.
    /// </summary>
    public static string ContainerExecutableKey(string executableKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableKey);
        return ContainerExecutablePrefix + executableKey;
    }

    private const string ContainerExecutablePrefix = "container|";

    /// <summary>
    /// Whether <paramref name="group"/> is a service container's: a group of that kind that gathers neither an executable's
    /// processes hosting none nor the processes a listed service may have run in - regrouped, one process hosting the
    /// services its capture listed, which <see cref="ProcessGroup.Services"/> names.
    /// </summary>
    public static bool IsContainer(ProcessGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.Kind == LaneGrouping.ServiceContainer && ExecutableKeyOf(group) is null
            && !string.Equals(group.Key, ServiceHostUnknownKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// The key of the executable <paramref name="group"/> gathers the processes of: its own grouped by executable, and the
    /// executable's under a grouping by service container; null for a session's, a container's, or the processes a listed
    /// service may have run in.
    /// </summary>
    public static string? ExecutableKeyOf(ProcessGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.Kind switch
        {
            LaneGrouping.Executable => group.Key,
            LaneGrouping.ServiceContainer when group.Key.StartsWith(ContainerExecutablePrefix, StringComparison.Ordinal) =>
                group.Key[ContainerExecutablePrefix.Length..],
            _ => null,
        };
    }

    /// <summary>
    /// The key of the group <paramref name="instance"/> belongs to under <paramref name="grouping"/>; a grouping by service
    /// container reads <paramref name="services"/>, the capture's services bound to its instances.
    /// </summary>
    public static string KeyOf(ProcessInstance instance, LaneGrouping grouping, ServiceMatch? services = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return grouping switch
        {
            LaneGrouping.Executable => SessionOverviewProjector.GroupKey(instance),
            LaneGrouping.UserSession => SessionKey(instance.SessionId),
            LaneGrouping.ServiceContainer => (services ?? ServiceMatch.NotRecorded).ContainerOf(instance.Id) is not null
                ? ContainerKey(instance.Id)
                : (services ?? ServiceMatch.NotRecorded).MayHaveHosted(instance)
                    ? ServiceHostUnknownKey
                    : ContainerExecutableKey(SessionOverviewProjector.GroupKey(instance)),
            _ => throw new ArgumentOutOfRangeException(nameof(grouping), grouping, Offers),
        };
    }

    /// <summary>
    /// The identity of a projected graph grouped by <paramref name="grouping"/>: the projection's own by executable, and
    /// marked by any other, since its group nodes and their relationships differ, so no layout made for one is applied to
    /// another.
    /// </summary>
    public static string Identity(string graphIdentity, LaneGrouping grouping)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphIdentity);
        return grouping switch
        {
            LaneGrouping.Executable => graphIdentity,
            LaneGrouping.UserSession => graphIdentity + "|grouped-by:session",
            LaneGrouping.ServiceContainer => graphIdentity + "|grouped-by:service-container",
            _ => throw new ArgumentOutOfRangeException(nameof(grouping), grouping, Offers),
        };
    }

    /// <summary>
    /// The groupings a snapshot's processes can be grouped by: by executable always, by terminal session once one of its
    /// processes' lifecycle records names one, and by service container once its capture listed the services running - a
    /// grouping every process of which is unrecorded tells nothing.
    /// </summary>
    public static IReadOnlyList<LaneGrouping> Offered(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return
        [
            .. All.Where(grouping => grouping switch
            {
                LaneGrouping.UserSession => snapshot.Processes.Any(process => process.TerminalSession is not null),
                LaneGrouping.ServiceContainer => snapshot.Services.Recorded,
                _ => true,
            }),
        ];
    }

    /// <summary>
    /// How a snapshot's processes are grouped now, as its groups say: by session when they are sessions, by service container
    /// when every one is of that grouping, as a regrouping makes them, and otherwise by executable.
    /// </summary>
    public static LaneGrouping Of(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Groups.Any(group => group.Kind == LaneGrouping.UserSession) ? LaneGrouping.UserSession
            : snapshot.Groups.Count > 0 && snapshot.Groups.All(group => group.Kind == LaneGrouping.ServiceContainer)
                ? LaneGrouping.ServiceContainer
            : LaneGrouping.Executable;
    }

    /// <summary>
    /// A snapshot projected by executable, grouped by <paramref name="grouping"/>: unchanged by executable; by session one
    /// group per terminal session its processes name, in the session's order, then the processes naming none; and by
    /// service container one group per host of the services listed, then each executable's processes hosting none as it
    /// was projected, then the processes a listed service may have run in.
    /// </summary>
    public static WorkspaceSnapshot Regroup(WorkspaceSnapshot projected, LaneGrouping grouping)
    {
        ArgumentNullException.ThrowIfNull(projected);
        if (Of(projected) != LaneGrouping.Executable)
        {
            throw new ArgumentException("Only a snapshot grouped by executable, as projected, is regrouped.", nameof(projected));
        }

        return grouping switch
        {
            LaneGrouping.Executable => projected,
            LaneGrouping.UserSession => BySession(projected),
            LaneGrouping.ServiceContainer => ByContainer(projected),
            _ => throw new ArgumentOutOfRangeException(nameof(grouping), grouping, Offers),
        };
    }

    private const string Offers = "The window groups by executable, terminal session or service container.";

    private static WorkspaceSnapshot BySession(WorkspaceSnapshot projected)
    {
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

    // Each container is its own group, named as icat metric names it; every other process keeps its projected executable's
    // group under a key of this grouping's own, its name and path as projected; the processes no creation time tells about
    // are one group, never a guess (ADR-096).
    private static WorkspaceSnapshot ByContainer(WorkspaceSnapshot projected)
    {
        ProcessNode[] processes = [.. projected.Processes.Select(process => process with { GroupKey = ContainerKeyOf(process) })];
        ProcessGroup[] containers =
        [
            .. projected.Processes
                .Where(process => process.Services is { Count: > 0 })
                .OrderBy(process => process.ProcessId)
                .ThenBy(process => process.PidHolder)
                .Select(process => new ProcessGroup(
                    ContainerKey(process.Id),
                    GroupingText.Container(
                        process.ProcessId,
                        string.Equals(process.Name, ProcessNode.PidName(process.ProcessId), StringComparison.Ordinal) ? null : process.Name,
                        process.Services!),
                    LaneGrouping.ServiceContainer)
                {
                    Services = process.Services,
                }),
        ];
        HashSet<string> holding = [.. processes.Select(process => process.GroupKey)];
        ProcessGroup[] executables =
        [
            .. projected.Groups
                .Where(group => holding.Contains(ContainerExecutableKey(group.Key)))
                .Select(group => group with
                {
                    Key = ContainerExecutableKey(group.Key),
                    Kind = LaneGrouping.ServiceContainer,
                    Peers = null,
                    CallTimes = null,
                }),
        ];
        ProcessGroup[] unknown = holding.Contains(ServiceHostUnknownKey)
            ? [new(ServiceHostUnknownKey, GroupingText.ServiceHostNotKnown, LaneGrouping.ServiceContainer)]
            : [];
        return projected with { Groups = [.. containers, .. executables, .. unknown], Processes = processes };
    }

    private static string ContainerKeyOf(ProcessNode process) =>
        process.Services is { Count: > 0 } ? ContainerKey(process.Id)
        : process.MayHaveHostedService ? ServiceHostUnknownKey
        : ContainerExecutableKey(process.GroupKey);
}
