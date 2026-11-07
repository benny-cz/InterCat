using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// The session's resources in the inspector, by namespace (M6, `contracts/resources-v1.md`): while it describes nothing on
/// the machine rung, every resource the session's processes held but an image's section - named synchronization objects,
/// sections by their handles and by their views, AF_UNIX sockets - named ones by the object directory their names lie in,
/// then those no record named, and idle sockets last, apart from the traffic observed, as `icat resources` lists them. A
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

    // Whether every resource is listed, as asked, rather than the first few.
    private bool resourcesListedWhole;

    // The key of the resource chosen in the list, while the list shows and holds it; the list hidden lets it go.
    private string? chosenListedResource;

    // While the list restates its rows it lets its choice go for a moment, which is not the person's.
    private bool restatingResources;

    // The rows last built, for the snapshot and whether every resource was asked for.
    private (WorkspaceSnapshot Snapshot, bool Whole, IReadOnlyList<ResourceRow> Rows)? resourceRows;

    // Whether the list showed when it was last said, so a selection that leaves it as it was restates nothing; null until it
    // is first said, so the first selection always says it.
    private bool? resourcesShown;

    // The way down made by ChooseResource, which Esc comes back along.
    private ResourceHop? resourceHop;

    /// <summary>Whether the inspector lists the session's resources: on the machine rung while it describes nothing, where there are any.</summary>
    public bool ShowsResources => ladder.Current.Level == DetailLevel.Machine && DescribesNothing && wholeSnapshot.Resources.Count > 0;

    /// <summary>
    /// The resources the inspector lists, in the session's order: by namespace, those no record named after, idle sockets
    /// last. Built once for the snapshot and what was asked for; empty while the inspector lists none.
    /// </summary>
    public IReadOnlyList<ResourceRow> ResourceRows
    {
        get
        {
            if (!ShowsResources)
            {
                return [];
            }

            if (resourceRows is { } built && ReferenceEquals(built.Snapshot, wholeSnapshot) && built.Whole == resourcesListedWhole)
            {
                return built.Rows;
            }

            IReadOnlyList<ResourceRow> rows = [.. wholeSnapshot.Resources
                .Take(resourcesListedWhole ? ResourcesListed : ResourcesNamed)
                .Select(ResourceRowOf)];
            resourceRows = (wholeSnapshot, resourcesListedWhole, rows);
            return rows;
        }
    }

    /// <summary>
    /// The heading over them: how many, by namespace, and how many idle sockets come last - "12 resources by namespace",
    /// "5 resources by namespace, 2 idle listed last".
    /// </summary>
    public string ResourcesHeading
    {
        get
        {
            if (!ShowsResources)
            {
                return string.Empty;
            }

            int idle = wholeSnapshot.Resources.Count(resource => resource.Traffic is { Idle: true });
            return Spoken.Count(wholeSnapshot.Resources.Count, "resource") + " by namespace"
                + (idle > 0 ? string.Create(CultureInfo.CurrentCulture, $", {idle:N0} idle listed last") : string.Empty);
        }
    }

    /// <summary>The list as a screen reader names it: what it holds, and what E and Enter on a row do.</summary>
    public string ResourcesListName => ResourcesHeading + ": the resources this machine's processes held, by the object "
        + "directory each name lies in; E on one lists its records, and Enter chooses its hub or the processes holding it";

    /// <summary>Whether the inspector lists every resource, as asked, rather than the first few.</summary>
    public bool ListsAllResources => resourcesListedWhole && ShowsResources;

    /// <summary>Whether the inspector offers every resource, or the first few again: there are more than it names.</summary>
    public bool OffersResourcesToggle => ShowsResources && wholeSnapshot.Resources.Count > ResourcesNamed;

    /// <summary>What that button says: how many it would list, or that it names the first few again.</summary>
    public string ResourcesToggleLabel => ListsAllResources
        ? string.Create(CultureInfo.CurrentCulture, $"Show the first {ResourcesNamed}")
        : wholeSnapshot.Resources.Count is var count && count <= ResourcesListed
            ? string.Create(CultureInfo.CurrentCulture, $"Show all {count:N0} resources")
            : string.Create(CultureInfo.CurrentCulture, $"Show the first {ResourcesListed:N0} of {count:N0} resources");

    /// <summary>
    /// What the list leaves out with every resource asked for, past the most it lists, and where every one is read; empty
    /// otherwise, where the button that asks for them says how many there are.
    /// </summary>
    public string ResourcesMore => ListsAllResources && wholeSnapshot.Resources.Count - ResourcesListed is > 0 and var more
        ? string.Create(CultureInfo.CurrentCulture, $"{more:N0} more: icat resources --limit {wholeSnapshot.Resources.Count:N0} lists them all")
        : string.Empty;

    /// <summary>Whether the list says what it leaves out with every resource asked for.</summary>
    public bool ShowsResourcesMore => ResourcesMore.Length > 0;

    /// <summary>
    /// Lists every resource, at most <see cref="ResourcesListed"/>, or names the first few again, as its button asks; true
    /// where it now lists every one. A session with no more than the inspector names offers neither.
    /// </summary>
    public bool ToggleResources()
    {
        if (!OffersResourcesToggle)
        {
            return false;
        }

        resourcesListedWhole = !resourcesListedWhole;
        RaiseResourcesChanged();
        return ListsAllResources;
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
    /// or on its holders' program's rung where this one draws them as one node, first whatever the budget, as the search goes
    /// to it (ADR-099) - else the process holding it, or the processes holding it together. <see cref="ComeBackFromResource"/>
    /// comes back to its row. False for a resource the list does not hold or no process holds.
    /// </summary>
    public bool ChooseResource(ResourceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!ResourceRows.Any(listed => listed.Key == row.Key)
            || wholeSnapshot.Resources.FirstOrDefault(resource => resource.Key == row.Key) is not { Holders.Count: > 0 } resource)
        {
            return false;
        }

        if (!IsHub(resource))
        {
            if (!ChooseProcesses(resource.Holders))
            {
                return false;
            }

            resourceHop = new(resource.Key, ladder.Depth, Descended: false);
            return true;
        }

        ChooseHub(resource.Key);
        bool descended = false;
        if (SelectedCluster?.Key != resource.Key && ProgramsOf(resource) is [var program])
        {
            // Its holders are one program's, which this rung draws as one node: that program's rung draws them apart, and
            // the hub joining them.
            if (!DescendAlong([program], null))
            {
                return false;
            }

            ChooseHub(resource.Key);
            descended = true;
        }

        resourceHop = new(resource.Key, ladder.Depth, descended);
        return true;
    }

    /// <summary>
    /// Comes back along the way down made from the resources listed, as Esc does before it climbs the ladder: where what it
    /// chose - the hub, the process or the processes holding the resource - is still chosen on the rung it was chosen at,
    /// nothing is chosen, the machine rung is shown again where the hub was its holders' program's, and the resource is
    /// chosen in the list once more; the hop is returned, whose row the keyboard goes back to. A hop the selection or the
    /// ladder has since moved away from is let go, and null returned.
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

        if (hop.Descended)
        {
            ReturnTo(0);
        }

        ClearSelection();
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
        return new(resource.Key, resource.Label, $"{where} · {held} · {Spoken.Count(resource.Records, "record")}{carried}")
        {
            EnterChooses = resource.Holders.Count == 0 ? string.Empty
                : IsHub(resource) ? "its hub"
                : resource.Holders.Count == 1 ? "the process holding it"
                : "the processes holding it",
        };
    }

    /// <summary>
    /// Says again what the list holds where what the inspector describes showed or hid it; hidden, it lets its choice go, so
    /// nothing chosen later comes back chosen.
    /// </summary>
    private void FollowResources()
    {
        bool shows = ShowsResources;
        if (!shows)
        {
            chosenListedResource = null;
        }

        if (shows != resourcesShown)
        {
            RaiseResourcesChanged();
        }
    }

    /// <summary>Says again what the list of resources holds and whether it shows, its choice kept by its key.</summary>
    private void RaiseResourcesChanged()
    {
        resourcesShown = ShowsResources;
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
public sealed record ResourceHop(string Key, int Depth, bool Descended);
