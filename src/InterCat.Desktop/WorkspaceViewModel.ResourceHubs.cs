using System.Globalization;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// The graph's resource hubs (§6.3, ADR-099): a resource two or more drawn processes held - a section by its views or by its
/// handles, or a named synchronization object - drawn as a diamond joined to each drawn node holding it by a membership,
/// which is undirected and carries no traffic value. Hovering one says what it is, who held it and that holding it is no
/// traffic (P27).
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>The resource a hub stands for, as the snapshot carries it; null for a key that names none.</summary>
    private ResourceLink? ResourceOf(string key) => wholeSnapshot.ResourceLinks.FirstOrDefault(link => link.Key == key);

    /// <summary>
    /// A hub's card: what the resource is, the processes holding it - the drawn ones by name - its capacity where its views
    /// mapped one, and that holding it proves shared access and never traffic.
    /// </summary>
    private HoverCard DescribeHubHover(GraphDisplayNode hub)
    {
        ResourceLink? resource = ResourceOf(hub.Key);
        string kind = ResourceLink.KindOf(hub.Key) ?? "Resource";
        var lines = new List<string>
        {
            resource?.Name is { } name ? $"{kind} {resource.Number.ToString(CultureInfo.CurrentCulture)}, named {name}" : kind,
        };
        if (resource is not null)
        {
            Dictionary<ProcessInstanceId, ProcessNode> processes = wholeSnapshot.Processes.ToDictionary(process => process.Id);
            string[] holders = [.. resource.Holders.Select(holder => processes.TryGetValue(holder, out ProcessNode? process)
                ? process.NameWithPid : holder.ToString())];
            lines.Add("Held by " + string.Join(", ", holders.Take(HubHoldersOnCard))
                + (holders.Length > HubHoldersOnCard
                    ? string.Create(CultureInfo.CurrentCulture, $" and {holders.Length - HubHoldersOnCard:N0} more")
                    : string.Empty));
            if (resource.LargestView is { } largest)
            {
                lines.Add("Capacity: the largest view of it a process mapped, " + ByteSizeText.Of(largest)
                    + " - what it can hold, never bytes moved through it");
            }
        }

        lines.Add(Counted(hub.ConnectedProcesses, "drawn process", "drawn processes") + " held it here; one outside this rung's "
            + "neighbourhood is left to its own rung");
        lines.Add("Holding it proves the processes shared it, never what moved through it: no record of it is traffic");
        lines.Add("Scope: " + TimeBase.HalfOpenRange(wholeSnapshot.Extent, CultureInfo.CurrentCulture) + " · whole session"
            + (appliedInterval is not null ? "; the brushed interval does not re-count it" : string.Empty));
        lines.Add("Edges: memberships, not relationships, and counted in no total; drawn thin and neutral, with no direction");
        lines.Add("Size: fixed; a resource, not a process");
        if (SearchedLine(hub) is { } searched)
        {
            lines.Add(searched);
        }

        return new(hub.Label, lines);
    }

    /// <summary>
    /// A membership's card: which drawn node held the hub's resource - how many of its processes - and that a membership has
    /// no direction and no traffic value.
    /// </summary>
    private static HoverCard DescribeMembershipHover(GraphDisplayEdge edge, GraphDisplay drawn)
    {
        string holder = drawn.Node(edge.SourceKey)?.Title ?? edge.SourceKey;
        string hub = drawn.Node(edge.TargetKey)?.Label ?? edge.TargetKey;
        var lines = new List<string>
        {
            edge.Relationships.Count == 1
                ? $"{holder} held {hub}"
                : $"{Counted(edge.Relationships.Count, "process", "processes")} of {holder} held {hub}",
            "A membership: holding a resource, never traffic through it, and with no direction",
            "Thickness: the same for every membership, which carries no traffic value in the graph",
        };
        return new($"{holder} — {hub}", lines);
    }

    /// <summary>
    /// A hub chosen: what the resource is, the processes holding it, that holding it proves shared access and never traffic,
    /// and that it stands for no process, so it selects no records of its own and E lists the rung's.
    /// </summary>
    private string DescribeHub(GraphDisplayNode hub)
    {
        string kind = ResourceLink.KindOf(hub.Key) ?? "Resource";
        string holders = ResourceOf(hub.Key) is { } resource
            ? string.Join(", ", resource.Holders.Take(HubHoldersOnCard).Select(holder =>
                wholeSnapshot.Processes.FirstOrDefault(process => process.Id == holder)?.NameWithPid ?? holder.ToString()))
                + (resource.Holders.Count > HubHoldersOnCard
                    ? string.Create(CultureInfo.CurrentCulture, $" and {resource.Holders.Count - HubHoldersOnCard:N0} more")
                    : string.Empty)
            : Counted(hub.ConnectedProcesses, "drawn process", "drawn processes");
        return $"{kind} held by {holders}: holding it proves they shared it, never what moved through it. E lists its records, "
            + "whichever process they bind to.";
    }

    /// <summary>A hub's evidence line: how many records its gate numbered it in - its views' or its handles' - none of them traffic.</summary>
    private string HubEvidence(GraphDisplayNode hub) => ResourceOf(hub.Key) is { } resource
        ? string.Create(CultureInfo.CurrentCulture, $"{resource.Records:N0} {(resource.Records == 1 ? "record" : "records")} of its ")
            + (resource.ByViews ? "views" : "handles") + " · holding a resource is no traffic"
        : "Its records are not in this generation";

    /// <summary>
    /// Draws the hub of the resource <paramref name="key"/> names first on the rung shown, whatever the budget would draw
    /// before it, and chooses it where the rung draws its holders apart; where it draws them in one node no hub joins them,
    /// and none is chosen.
    /// </summary>
    private void ChooseHub(string key)
    {
        keptHub = key;
        RefreshGraphDisplay(reproject: true);
        if (graphDisplay.Node(key) is { IsHub: true })
        {
            SelectGraphNode(key);
        }
    }

    /// <summary>How many of a hub's holders its card names before counting the rest.</summary>
    private const int HubHoldersOnCard = 6;
}
