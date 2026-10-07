using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// What an evidence rung reads from a published session: one paired channel, the rows canonically owned by a set of
/// process instances, an RPC channel's or one call's records, or every admitted row, optionally within a deliberate
/// time range, and narrowed to one mechanism's records, one source direction's or one channel end's as a timeline lane
/// counts them (§6.4). A scope that cannot be read states its problem rather than widening itself to the whole session.
/// </summary>
public sealed record EvidenceScope(
    string Description,
    string? ChannelKey,
    IReadOnlyList<ProcessInstanceId> OwnerProcesses,
    TimeRange? Interval,
    string? ContributingEdgeKey,
    string? Problem = null)
{
    /// <summary>
    /// The RPC channel or call (<see cref="RpcChannelKeys"/>), or the process's HTTP exchanges or one exchange
    /// (<see cref="HttpExchangeKeys"/>), whose records the scope reads; null otherwise.
    /// </summary>
    public string? OperationKey { get; init; }

    /// <summary>The one mechanism whose records the scope reads, as a mechanism's lane counts them; null for every one.</summary>
    public Mechanism? Mechanism { get; init; }

    /// <summary>The one source direction whose records the scope reads, as a direction row counts them; null for every one.</summary>
    public Direction? Direction { get; init; }

    /// <summary>
    /// The one end of its paired channel whose records the scope reads, as an end's lane counts them: 0 the first end, 1
    /// the second; null for both.
    /// </summary>
    public int? End { get; init; }

    /// <summary>Whether the scope narrows its records to a lane's: one mechanism's, one source direction's or one end's.</summary>
    public bool IsNarrowed => Mechanism is not null || Direction is not null || End is not null;

    public bool IsWholeSession => ChannelKey is null && OwnerProcesses.Count == 0 && OperationKey is null && !IsNarrowed;
}

/// <summary>
/// An explicit set of process instances as one filter: §6.7's multi-selection, which <c>Enter</c> turns into a filter.
/// Its key names every member, so the scope it reads is exactly the processes chosen and never a group's current
/// membership or a guessed PID.
/// </summary>
public static class ProcessSetFilter
{
    public const string Prefix = "processes:";

    /// <summary>The filter key for these members, in a stable order.</summary>
    public static string KeyOf(IEnumerable<ProcessInstanceId> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return Prefix + string.Join(",", members.Select(member => member.Value.ToString("N")).Distinct().Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// How the set reads in a breadcrumb or a caption: "3 selected processes", or, for an aggregate drawn in the graph and
    /// chosen by its name, "the 3 processes of Rest of the machine".
    /// </summary>
    public static string Label(int count, string? of = null) => of is null
        ? count == 1 ? "1 selected process" : string.Create(CultureInfo.CurrentCulture, $"{count:N0} selected processes")
        : count == 1 ? $"the 1 process of {of}" : string.Create(CultureInfo.CurrentCulture, $"the {count:N0} processes of {of}");

    /// <summary>The members a set's key names; false for a key that names no set.</summary>
    public static bool TryParse(string? key, out ProcessInstanceId[] members)
    {
        members = [];
        if (key is null || !key.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parsed = new List<ProcessInstanceId>();
        foreach (string part in key[Prefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Guid.TryParseExact(part, "N", out Guid id) || id == Guid.Empty)
            {
                return false;
            }

            parsed.Add(new(id));
        }

        members = [.. parsed];
        return members.Length > 0;
    }
}

/// <summary>
/// Resolves an evidence rung's scope from the filters its breadcrumb shows. The latest filter that names an entity
/// decides: the rung evidence was reached from, or - once the user removes that filter - the next one out. Removing
/// filters therefore widens the scope one visible step at a time, and a rung with none reads the whole session.
/// A group is its member instances in this snapshot, never a guessed PID.
/// </summary>
public static class EvidenceScopes
{
    /// <summary>The field of a filter narrowing a scope to one mechanism's records, keyed by the mechanism's name.</summary>
    public const string MechanismField = "mechanism";

    /// <summary>The field of a filter narrowing a scope to one source direction's records, keyed by the direction's name.</summary>
    public const string DirectionField = "direction";

    /// <summary>The field of a filter narrowing a paired channel's scope to one end's records, keyed 0 or 1.</summary>
    public const string EndField = "end";

    /// <summary>A filter narrowing the scope to one mechanism's records, as its timeline lane counts them.</summary>
    public static ImpliedFilter MechanismFilter(Mechanism mechanism, string reason) =>
        new(MechanismField, MechanismText.Name(mechanism), reason) { Key = mechanism.ToString() };

    /// <summary>A filter narrowing the scope to one source direction's records, as a process's direction row counts them.</summary>
    public static ImpliedFilter DirectionFilter(Direction direction, string reason) =>
        new(DirectionField, Capitalized(ObservationText.DirectionOf(direction)), reason) { Key = direction.ToString() };

    /// <summary>A filter narrowing a paired channel's scope to the records made at one end, named by its endpoint.</summary>
    public static ImpliedFilter EndFilter(int end, string endpoint, string reason) => end is 0 or 1
        ? new(EndField, endpoint, reason) { Key = end.ToString(CultureInfo.InvariantCulture) }
        : throw new ArgumentOutOfRangeException(nameof(end), "A channel has two ends, 0 and 1.");

    public static EvidenceScope Resolve(WorkspaceSnapshot snapshot, NavigationState rung)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rung);
        TimeRange? interval = rung.Viewport == snapshot.Extent ? null : rung.Viewport;
        return Narrowed(ResolveEntity(snapshot, rung, interval), rung.Filters);
    }

    /// <summary>
    /// The scope the latest filter that names an entity decides: a process set, a group, a process, a channel, an
    /// operation, or else the whole session.
    /// </summary>
    private static EvidenceScope ResolveEntity(WorkspaceSnapshot snapshot, NavigationState rung, TimeRange? interval)
    {
        string time = interval is { } range ? " · " + WorkspaceTime.FormatRange(range, CultureInfo.CurrentCulture) : string.Empty;

        for (int index = rung.Filters.Count - 1; index >= 0; index--)
        {
            ImpliedFilter filter = rung.Filters[index];
            if (ProcessSetFilter.TryParse(filter.Key, out ProcessInstanceId[] chosen))
            {
                // A multi-selection turned into a filter reads exactly its members that this generation still holds; an
                // aggregate chosen by its name keeps the name its filter shows.
                ProcessInstanceId[] present = [.. snapshot.Processes.Where(node => chosen.Contains(node.Id)).Select(node => node.Id)];
                string? named = string.Equals(filter.Value, ProcessSetFilter.Label(chosen.Length), StringComparison.Ordinal)
                    ? null
                    : filter.Value;
                return present.Length == 0
                    ? Unreadable("None of the selected processes is in this generation.", interval)
                    : new($"Records owned by {ProcessSetFilter.Label(present.Length, named)}{time}", null, present, interval, null);
            }

            switch (filter.Level)
            {
                case DetailLevel.Machine:
                    return Whole(interval, time);
                case DetailLevel.Channel when RpcChannelKeys.IsRpc(filter.Key) || HttpExchangeKeys.IsHttp(filter.Key):
                    return new($"Records of {filter.Value}{time}", null, [], interval, null) { OperationKey = filter.Key };
                case DetailLevel.Operation when RpcChannelKeys.IsRpc(filter.Key) || HttpExchangeKeys.IsHttp(filter.Key):
                    // A call or an exchange is one entity: its records are all of its own wherever the view is zoomed.
                    return new($"Records of {filter.Value}", null, [], null, null) { OperationKey = filter.Key };
                case DetailLevel.Channel when TransportConnection.IsKey(filter.Key):
                    // A one-sided connection is no paired channel of the overview: it is read by its own key.
                    return new($"Records of {filter.Value}{time}", filter.Key, [], interval, null);
                case DetailLevel.Channel when filter.Key is { } channelKey:
                    Channel? channel = snapshot.Channels.FirstOrDefault(candidate => candidate.Key == channelKey);
                    return new($"Paired TCP channel {channel?.Name ?? filter.Value}{time}", channelKey, [], interval,
                        channel?.EdgeKey);
                case DetailLevel.ProcessInstance when filter.Key is { } processKey:
                    if (!Guid.TryParse(processKey, out Guid id) || id == Guid.Empty)
                        return Unreadable($"The process filter '{filter.Value}' names no process instance.", interval);
                    ProcessNode? process = snapshot.Processes.FirstOrDefault(node => node.Id.Value == id);
                    string name = process is null ? filter.Value : process.NameWithPid;
                    return new($"Records owned by {name}{time}", null, [new(id)], interval, null);
                case DetailLevel.Group when filter.Key is { } groupKey:
                    ProcessInstanceId[] members = [.. snapshot.Processes
                        .Where(node => string.Equals(node.GroupKey, groupKey, StringComparison.Ordinal))
                        .Select(node => node.Id)];
                    return members.Length == 0
                        ? Unreadable($"The group '{filter.Value}' has no process instance in this generation.", interval)
                        : new(string.Create(CultureInfo.CurrentCulture,
                                $"Records owned by the {members.Length:N0} "
                                + $"{(members.Length == 1 ? "instance" : "instances")} of {filter.Value}{time}"),
                            null, members, interval, null);
                case DetailLevel.Operation:
                    return Unreadable("This session projects no logical operations, so an operation cannot scope its "
                        + "records. Remove the operation filter to read the channel's records.", interval);
            }
        }

        return Whole(interval, time);
    }

    /// <summary>
    /// The scope narrowed by the filters a timeline cell's lane implies (§6.4): one mechanism's records, one source
    /// direction's, or those made at one end of a paired channel. Each narrows whatever entity the scope names, and one
    /// that cannot narrow it - an end of anything but a paired channel - is a problem stated, never ignored.
    /// </summary>
    private static EvidenceScope Narrowed(EvidenceScope scope, IReadOnlyList<ImpliedFilter> filters)
    {
        Mechanism? mechanism = null;
        Direction? direction = null;
        int? end = null;
        foreach (ImpliedFilter filter in filters)
        {
            switch (filter.Field)
            {
                case MechanismField when Enum.TryParse(filter.Key, out Mechanism named) && Enum.IsDefined(named):
                    mechanism = named;
                    break;
                case DirectionField when Enum.TryParse(filter.Key, out Direction marked) && Enum.IsDefined(marked):
                    direction = marked;
                    break;
                case EndField when filter.Key is "0" or "1":
                    end = filter.Key == "0" ? 0 : 1;
                    break;
                case MechanismField or DirectionField or EndField:
                    return Unreadable($"The {filter.Field} filter '{filter.Value}' names none this session can read.", scope.Interval);
            }
        }

        if (scope.Problem is not null || (mechanism is null && direction is null && end is null))
        {
            return scope;
        }

        if (end is not null && (scope.ChannelKey is null || TransportConnection.IsKey(scope.ChannelKey)))
        {
            return Unreadable("An end filter names one end of a paired TCP channel, and this scope reads none. Remove it to "
                + "read the scope's records.", scope.Interval);
        }

        string only = string.Join(", ", new[]
        {
            mechanism is { } kind ? $"{MechanismText.InSentence(kind)} records" : null,
            direction is { } way ? DirectionWords(way) : null,
            end is not null ? $"those made at {filters.Last(filter => filter.Field == EndField).Value}" : null,
        }.OfType<string>());
        return scope with
        {
            Description = $"{scope.Description} · {only} only",
            Mechanism = mechanism,
            Direction = direction,
            End = end,
        };
    }

    /// <summary>A source direction's records as a scope names them: "records marked outbound", "records with no data direction".</summary>
    private static string DirectionWords(Direction direction) => direction switch
    {
        Direction.UnknownDirection => "records of unknown direction",
        Direction.DirectionNotApplicable => "records with no data direction",
        _ => "records marked " + ObservationText.DirectionOf(direction),
    };

    private static string Capitalized(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    private static EvidenceScope Whole(TimeRange? interval, string time) =>
        new($"Every admitted record in this session{time}", null, [], interval, null);

    private static EvidenceScope Unreadable(string problem, TimeRange? interval) =>
        new("No readable scope", null, [], interval, null, problem);
}
