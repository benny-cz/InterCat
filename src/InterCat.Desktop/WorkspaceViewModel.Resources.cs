using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// The session's resources in the inspector, by namespace (M6, `contracts/resources-v1.md`): while it describes nothing on
/// the machine rung, every resource the session's processes held but an image's section - named synchronization objects,
/// sections by their handles and by their views, AF_UNIX sockets - and while it describes a program, chosen or opened,
/// those its processes held; named ones by the object directory their names lie in, then those no record named, and idle
/// sockets last, apart from the traffic observed, as `icat resources` lists them. A
/// resource chosen there is its own records: the timeline highlights them, the card counts them and E lists them, as for a
/// hub chosen in the graph. Enter on one chooses where it is held - its hub, or the processes holding it - and Esc comes
/// back to its row.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>The most resources the inspector names before every one is asked for.</summary>
    internal const int ResourcesNamed = 8;

    /// <summary>The most resources the inspector lists once every one is asked for.</summary>
    internal const int ResourcesListed = 100;

    /// <summary>How many of a resource's holders its row names before counting the rest.</summary>
    private const int ResourceHoldersOnRow = 3;

    // Whose resources the inspector lists whole, as asked: the machine's, an empty key, or a program's, by its group's key;
    // null while it names the first few.
    private string? resourcesListedWhole;

    // The key of the resource chosen in the list, while the list shows and holds it; another scope lets it go.
    private string? chosenListedResource;

    // While the list restates its rows it lets its choice go for a moment, which is not the person's.
    private bool restatingResources;

    // The rows last built, for the scope, the snapshot and whether every resource was asked for.
    private (string Scope, WorkspaceSnapshot Snapshot, bool Whole, IReadOnlyList<ResourceRow> Rows)? resourceRows;

    // A scope's resources, worked out once for the snapshot.
    private (string Scope, WorkspaceSnapshot Snapshot, IReadOnlyList<ResourceLink> Resources)? scopedResources;

    // The scope the list was last said for, so a selection that leaves it where it was restates nothing; until it is first
    // said, a scope no list has, so the first selection always says it.
    private string? raisedResourceScope = UnsaidListenerScope;

    // The way down made by ChooseResource, which Esc comes back along.
    private ResourceHop? resourceHop;

    /// <summary>
    /// Whether the inspector lists resources: the session's on the machine rung while it describes nothing, or a program's
    /// processes' while it describes the program, chosen or opened, where there are any.
    /// </summary>
    public bool ShowsResources => ResourceScope is { } scope && ScopedResources(scope).Count > 0;

    /// <summary>
    /// The resources the inspector lists, in the session's order: by namespace, those no record named after, idle sockets
    /// last. Built once for the scope, the snapshot and what was asked for; empty while the inspector lists none.
    /// </summary>
    public IReadOnlyList<ResourceRow> ResourceRows
    {
        get
        {
            if (ResourceScope is not { } scope || ScopedResources(scope) is not { Count: > 0 } resources)
            {
                return [];
            }

            bool whole = ListsAllResources;
            if (resourceRows is { } built && built.Scope == scope && ReferenceEquals(built.Snapshot, wholeSnapshot) && built.Whole == whole)
            {
                return built.Rows;
            }

            IReadOnlyList<ResourceRow> rows = [.. resources.Take(whole ? ResourcesListed : ResourcesNamed).Select(ResourceRowOf)];
            resourceRows = (scope, wholeSnapshot, whole, rows);
            return rows;
        }
    }

    /// <summary>
    /// The heading over them: how many, whose, by namespace, and how many idle sockets come last - "12 resources by
    /// namespace", "2 resources its processes held, by namespace, 1 idle listed last".
    /// </summary>
    public string ResourcesHeading
    {
        get
        {
            if (ResourceScope is not { } scope || ScopedResources(scope) is not { Count: > 0 } resources)
            {
                return string.Empty;
            }

            int idle = resources.Count(resource => resource.Traffic is { Idle: true });
            return Spoken.Count(resources.Count, "resource") + (scope.Length > 0 ? " its processes held," : string.Empty) + " by namespace"
                + (idle > 0 ? string.Create(CultureInfo.CurrentCulture, $", {idle:N0} idle listed last") : string.Empty);
        }
    }

    /// <summary>The list as a screen reader names it: what it holds, and what E and Enter on a row do.</summary>
    public string ResourcesListName => ResourcesHeading + ": the resources "
        + (ResourceScope is { Length: > 0 } ? "its processes" : "this machine's processes")
        + " held, by the object directory each name lies in; E on one lists its records, and Enter chooses its hub or the "
        + "processes holding it";

    /// <summary>Whether the inspector lists every resource of its scope, as asked, rather than the first few.</summary>
    public bool ListsAllResources => ResourceScope is { } scope && string.Equals(resourcesListedWhole, scope, StringComparison.Ordinal);

    /// <summary>Whether the inspector offers every resource, or the first few again: there are more than it names.</summary>
    public bool OffersResourcesToggle => ResourceScope is { } scope && ScopedResources(scope).Count > ResourcesNamed;

    /// <summary>What that button says: how many it would list, or that it names the first few again.</summary>
    public string ResourcesToggleLabel => ListsAllResources
        ? string.Create(CultureInfo.CurrentCulture, $"Show the first {ResourcesNamed}")
        : ResourceScope is { } scope && ScopedResources(scope).Count is var count
            ? count <= ResourcesListed
                ? string.Create(CultureInfo.CurrentCulture, $"Show all {count:N0} resources")
                : string.Create(CultureInfo.CurrentCulture, $"Show the first {ResourcesListed:N0} of {count:N0} resources")
            : string.Empty;

    /// <summary>
    /// What the list leaves out with every resource asked for, past the most it lists, and where every one is read; empty
    /// otherwise, where the button that asks for them says how many there are.
    /// </summary>
    public string ResourcesMore => ListsAllResources && ResourceScope is { } scope
        && ScopedResources(scope).Count - ResourcesListed is > 0 and var more
        ? string.Create(CultureInfo.CurrentCulture,
            $"{more:N0} more: icat resources --limit {wholeSnapshot.Resources.Count:N0} lists every one the session's processes held")
        : string.Empty;

    /// <summary>Whether the list says what it leaves out with every resource asked for.</summary>
    public bool ShowsResourcesMore => ResourcesMore.Length > 0;

    /// <summary>
    /// Lists every resource of the inspector's scope, at most <see cref="ResourcesListed"/>, or names the first few again,
    /// as its button asks; true where it now lists every one. A scope with no more than the inspector names offers neither.
    /// </summary>
    public bool ToggleResources()
    {
        if (!OffersResourcesToggle)
        {
            return false;
        }

        resourcesListedWhole = ListsAllResources ? null : ResourceScope;
        RaiseResourcesChanged();
        return ListsAllResources;
    }

    /// <summary>
    /// Whose resources the inspector lists, as whose listening ports it lists: a program's, by its group's key, while it
    /// describes the program, chosen or opened, or the machine's, an empty key, while it describes nothing on the machine
    /// rung; null for any other selection.
    /// </summary>
    private string? ResourceScope => ListenerScope;

    /// <summary>
    /// The resources of a scope, in the session's order: the machine's every one, or those a program's processes held -
    /// shared with other programs' too - worked out once for the snapshot.
    /// </summary>
    private IReadOnlyList<ResourceLink> ScopedResources(string scope)
    {
        if (scope.Length == 0)
        {
            return wholeSnapshot.Resources;
        }

        if (scopedResources is { } known && known.Scope == scope && ReferenceEquals(known.Snapshot, wholeSnapshot))
        {
            return known.Resources;
        }

        Dictionary<ProcessInstanceId, ProcessNode> processes = ListenerNames().Processes;
        IReadOnlyList<ResourceLink> resources = [.. wholeSnapshot.Resources.Where(resource => resource.Holders.Any(holder =>
            string.Equals(processes.GetValueOrDefault(holder)?.GroupKey, scope, StringComparison.Ordinal)))];
        scopedResources = (scope, wholeSnapshot, resources);
        return resources;
    }

    /// <summary>
    /// The resource chosen in the inspector's list: what the card counts, the timeline highlights and E lists, as for a hub
    /// chosen in the graph. Null while none is, or the list does not show it. When the list restates its rows, the resource
    /// chosen is chosen again among them by its key.
    /// </summary>
    public ResourceRow? ChosenResourceRow
    {
        get => ChosenResource is { } chosen ? ResourceRows.FirstOrDefault(row => row.Key == chosen.Key) : null;
        set
        {
            if (restatingResources)
            {
                return;
            }

            string? key = value is not null && ResourceRows.Any(row => row.Key == value.Key) ? value.Key : null;
            if (key == chosenListedResource)
            {
                return;
            }

            chosenListedResource = key;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EvidenceHeading));
            OnPropertyChanged(nameof(EvidenceSummary));
            UpdateHighlight();
        }
    }

    /// <summary>
    /// Chooses where one of the listed resources is held, as Enter on its row asks: its hub where the graph draws one - here,
    /// or, from the machine rung, on its holders' program's rung where this one draws them as one node, first whatever the
    /// budget, as the search goes to it (ADR-099) - else the process holding it, or the processes holding it together.
    /// <see cref="ComeBackFromResource"/> comes back to its row, and to the program the list was for. False for a resource
    /// the list does not hold or no process holds.
    /// </summary>
    public bool ChooseResource(ResourceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!ResourceRows.Any(listed => listed.Key == row.Key)
            || wholeSnapshot.Resources.FirstOrDefault(resource => resource.Key == row.Key) is not { Holders.Count: > 0 } resource)
        {
            return false;
        }

        string? program = ResourceScope is { Length: > 0 } scope ? scope : null;
        bool descended = false;
        if (IsHub(resource))
        {
            ChooseHub(resource.Key);
            if (SelectedCluster?.Key != resource.Key && ladder.Depth == 0 && ProgramsOf(resource) is [var holders])
            {
                // Its holders are one program's, which the machine rung draws as one node: that program's rung draws them
                // apart, and the hub joining them.
                if (!DescendAlong([holders], null))
                {
                    return false;
                }

                ChooseHub(resource.Key);
                descended = true;
            }

            if (SelectedCluster?.Key == resource.Key)
            {
                resourceHop = new(resource.Key, ladder.Depth, descended) { Program = program };
                return true;
            }
        }

        // No hub, or none this rung can draw apart from its holders: the processes holding it.
        if (!ChooseProcesses(resource.Holders))
        {
            return false;
        }

        resourceHop = new(resource.Key, ladder.Depth, descended) { Program = program };
        return true;
    }

    /// <summary>
    /// Comes back along the way down made from the resources listed, as Esc does before it climbs the ladder: where what it
    /// chose - the hub, the process or the processes holding the resource - is still chosen on the rung it was chosen at,
    /// the machine rung is shown again where the hub was its holders' program's, what the list was listed for is described
    /// again - nothing, or the program, where this generation still holds it - and the resource is chosen in the list once
    /// more; the hop is returned, whose row the keyboard goes back to. A hop the selection or the ladder has since moved away
    /// from is let go, and null returned.
    /// </summary>
    public ResourceHop? ComeBackFromResource()
    {
        if (resourceHop is not { } hop)
        {
            return null;
        }

        resourceHop = null;
        if (ladder.Depth != hop.Depth
            || wholeSnapshot.Resources.FirstOrDefault(resource => resource.Key == hop.Key) is not { } resource
            || !(SelectedCluster?.Key == hop.Key
                || (resource.Holders.Count == 1 && selectedProcess?.Id == resource.Holders[0])
                || (HasMultiSelection && chosenProcesses.SetEquals(resource.Holders))))
        {
            return null;
        }

        if (hop.Program is { } program && Groups(program) is null)
        {
            return null;
        }

        if (hop.Descended)
        {
            ReturnTo(0);
        }

        if (hop.Program is { } listedFor)
        {
            ReturnToGroup(listedFor);
        }
        else
        {
            ClearSelection();
        }

        ChosenResourceRow = ResourceRows.FirstOrDefault(row => row.Key == hop.Key);
        return hop;
    }

    /// <summary>Whether the graph draws a resource as a hub: a section or a synchronization object two or more processes held.</summary>
    private bool IsHub(ResourceLink resource) => wholeSnapshot.ResourceLinks.Any(link => link.Key == resource.Key);

    /// <summary>The programs, by their groups' keys, whose processes held a resource.</summary>
    private string[] ProgramsOf(ResourceLink resource) =>
    [
        .. resource.Holders.Select(holder => ListenerNames().Processes.GetValueOrDefault(holder)?.GroupKey)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>The resource chosen in the inspector's list while the list shows it; null otherwise.</summary>
    private ResourceLink? ChosenResource => chosenListedResource is { } key && ResourceRows.Any(row => row.Key == key)
        ? wholeSnapshot.Resources.FirstOrDefault(resource => resource.Key == key)
        : null;

    /// <summary>
    /// A resource's evidence line: how many records its gate numbered it in - its views', its handles' or its socket's -
    /// and that holding a section or an object is no traffic, or what a socket carried.
    /// </summary>
    private static string ResourceEvidence(ResourceLink resource) =>
        string.Create(CultureInfo.CurrentCulture, $"{resource.Records:N0} {(resource.Records == 1 ? "record" : "records")} of its ")
        + (resource.Mechanism == Mechanism.UnixDomainSocket
            ? "socket · " + (resource.Traffic is { Idle: false } carried ? SocketTraffic(carried) : "idle: no send or receive seen")
            : (resource.ByViews ? "views" : "handles") + " · holding a resource is no traffic");

    /// <summary>
    /// What a resource's records say of its life that a row names: its notable ends alone - made before the capture, held at
    /// the stop, its close or unmapping unseen - and nothing for one made and ended in the capture.
    /// </summary>
    private static string NotableLife(ResourceLink resource) => string.Join(", ", new List<string?>
    {
        resource.MadeInCapture ? null : "made before the capture",
        resource.End switch
        {
            ResourceEnd.HeldAtStop => "held at the stop",
            ResourceEnd.Unseen => resource.ByViews ? "its unmapping unseen" : "its close unseen",
            _ => null,
        },
    }.OfType<string>());

    /// <summary>
    /// A resource's life as its card says it: when its first and last records were read, in the window's time words, and how
    /// it began and ended - "Seen from 1.0 µs to 3.1 µs: made in the capture, unmapped".
    /// </summary>
    private string LifeLine(ResourceLink resource)
    {
        string began = resource.MadeInCapture ? "made in the capture" : "made before the capture";
        string ended = resource.End switch
        {
            ResourceEnd.HeldAtStop => "held at the stop",
            ResourceEnd.Closed => resource.ByViews ? "unmapped" : "closed",
            _ => resource.ByViews ? "its unmapping unseen" : "its close unseen",
        };
        return (resource.FirstNanoseconds is { } first && resource.LastNanoseconds is { } last
                ? $"Seen from {TimeBase.Moment(first, CultureInfo.CurrentCulture)} to {TimeBase.Moment(last, CultureInfo.CurrentCulture)}: "
                : "Seen: ")
            + began + ", " + ended;
    }

    /// <summary>What a socket's records carried, its traffic: "2 sends, 1 receive".</summary>
    private static string SocketTraffic(ResourceTraffic traffic) =>
        Spoken.Count(traffic.Sends, "send") + ", " + Spoken.Count(traffic.Receives, "receive");

    /// <summary>
    /// One resource as the inspector lists it: its label, then what it is and the object directory its name lies in - or
    /// that no record named it - who held it, its records and, for a socket, what it carried or that it was idle.
    /// </summary>
    private ResourceRow ResourceRowOf(ResourceLink resource)
    {
        string where = resource.Name is null ? "unnamed"
            : (ResourceLink.KindOf(resource.Key) ?? "Resource") + " · " + (resource.Namespace ?? "a name in no directory");
        Dictionary<ProcessInstanceId, ProcessNode> processes = ListenerNames().Processes;
        string held = resource.Holders.Count == 0 ? "bound to no process"
            : "held by " + string.Join(", ", resource.Holders.Take(ResourceHoldersOnRow)
                .Select(holder => processes.GetValueOrDefault(holder)?.NameWithPid ?? holder.ToString()))
                + (resource.Holders.Count > ResourceHoldersOnRow
                    ? string.Create(CultureInfo.CurrentCulture, $" and {resource.Holders.Count - ResourceHoldersOnRow:N0} more")
                    : string.Empty);
        string carried = resource.Traffic is { } traffic ? " · " + (traffic.Idle ? "idle" : SocketTraffic(traffic)) : string.Empty;
        string life = NotableLife(resource) is { Length: > 0 } notable ? " · " + notable : string.Empty;
        return new(resource.Key, resource.Label, $"{where} · {held} · {Spoken.Count(resource.Records, "record")}{carried}{life}")
        {
            EnterChooses = resource.Holders.Count == 0 ? string.Empty
                : IsHub(resource) ? "its hub"
                : resource.Holders.Count == 1 ? "the process holding it"
                : "the processes holding it",
        };
    }

    /// <summary>
    /// Says again what the list holds where what the inspector describes moved it to another scope - shown, hidden, or a
    /// program's for the machine's - and lets its choice go, so nothing chosen later comes back chosen.
    /// </summary>
    private void FollowResources()
    {
        if (!string.Equals(ResourceScope, raisedResourceScope, StringComparison.Ordinal))
        {
            chosenListedResource = null;
            RaiseResourcesChanged();
        }
    }

    /// <summary>Says again what the list of resources holds and whether it shows, its choice kept by its key.</summary>
    private void RaiseResourcesChanged()
    {
        raisedResourceScope = ResourceScope;
        restatingResources = true;
        try
        {
            OnPropertyChanged(nameof(ResourceRows));
        }
        finally
        {
            restatingResources = false;
        }

        OnPropertyChanged(nameof(ChosenResourceRow));
        OnPropertyChanged(nameof(ShowsResources));
        OnPropertyChanged(nameof(ResourcesHeading));
        OnPropertyChanged(nameof(ResourcesListName));
        OnPropertyChanged(nameof(ListsAllResources));
        OnPropertyChanged(nameof(OffersResourcesToggle));
        OnPropertyChanged(nameof(ResourcesToggleLabel));
        OnPropertyChanged(nameof(ResourcesMore));
        OnPropertyChanged(nameof(ShowsResourcesMore));
    }
}

/// <summary>
/// A way down made from the inspector's resources (M6): the resource whose hub or holders were chosen from its row, the
/// rung they were chosen at, and whether that rung is its holders' program's, gone down to from the machine rung. Esc comes
/// back along it to the resource's row.
/// </summary>
public sealed record ResourceHop(string Key, int Depth, bool Descended)
{
    /// <summary>The program, by its group's key, whose processes' resources the list was listed for; null for the machine's.</summary>
    public string? Program { get; init; }
}
