using System.Globalization;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// The other hosts a chosen process, or a chosen program's processes, reached, in the inspector (§6.3): by address, the
/// busiest first, as `icat processes --pid` lists one process's. Most of a busy machine's connections run to other hosts,
/// which the graph draws to one node; here a process's or a program's own are read, and Enter on one finds every process
/// that reached it, as a search for its address does.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>The most other hosts the inspector names for a chosen process or program, the busiest.</summary>
    internal const int ReachedHostsNamed = 5;

    /// <summary>The most other hosts the inspector lists for a chosen process or program once every one is asked for.</summary>
    internal const int ReachedHostsListed = 100;

    /// <summary>
    /// The process, by its instance, or the program, by its group's key, whose other hosts the inspector lists whole, as
    /// asked; null while it names the busiest.
    /// </summary>
    private string? reachedHostsListedWhole;

    /// <summary>The other hosts the selection last asked about reached, and who reached each, summed once for it.</summary>
    private (string Selection, IReadOnlyList<OtherHost> Hosts, IReadOnlyDictionary<string, string> ReachedBy, bool Several)?
        reachedHosts;

    /// <summary>
    /// The other hosts the selected process, or the selected program's processes, reached over the whole session, the
    /// busiest first: each by its address, then its scope where one is known, what reached it - for a program whose
    /// processes several reached them, the busiest of them and how many more - the ports at the address, those accepted
    /// from it said apart at the processes' own ports, and the records: "203.0.113.1", "2 TCP connections · port 443 · 6
    /// records". Empty for any other selection, and for one that reached none.
    /// </summary>
    public IReadOnlyList<ReachedHostRow> ReachedHostRows
    {
        get
        {
            (IReadOnlyList<OtherHost> hosts, IReadOnlyDictionary<string, string> reachedBy, bool several) = HostsOfSelection();
            return [.. hosts.Take(ListsAllReachedHosts ? ReachedHostsListed : ReachedHostsNamed)
                .Select(host => new ReachedHostRow(host.Address, string.Join(" · ", new[]
                {
                    AddressScope.Of(host.Address),
                    ReachedFrom(host, several, reachedBy),
                    host.Ports.Count == 0 ? null : HostLinkText.Ports(host.Ports),
                    HostLinkText.Accepted(host) is { Length: > 0 } accepted ? accepted : null,
                    CountText.Of(host.Records, "record"),
                }.OfType<string>())))];
        }
    }

    /// <summary>Whether the inspector lists the other hosts the selection reached.</summary>
    public bool ShowsReachedHosts => HostsOfSelection().Hosts.Count > 0;

    /// <summary>
    /// The heading over them, a process's or a program's processes' - those that connected to it where it accepted every
    /// connection - saying under a brush that they are the whole session's.
    /// </summary>
    public string ReachedHostsHeading
    {
        get
        {
            IReadOnlyList<OtherHost> hosts = HostsOfSelection().Hosts;
            return HostLinkText.HostsHeading(ShowsLineage ? "it" : "its processes", hosts.Sum(host => (long)host.Accepted),
                    hosts.Sum(host => (long)host.TcpConnections + host.UdpFlows))
                + ", the busiest first" + (appliedInterval is null ? string.Empty : " · whole session");
        }
    }

    /// <summary>
    /// The list as a screen reader names it: what it holds and what Enter on a row does. Each row says itself whole as the
    /// keyboard reaches it.
    /// </summary>
    public string ReachedHostsListName => ReachedHostsHeading.Replace(" · ", ", over the ", StringComparison.Ordinal)
        + "; Enter on one finds the processes that reached it";

    /// <summary>Whether the inspector lists every other host the selection reached, as asked, rather than the busiest few.</summary>
    public bool ListsAllReachedHosts => SelectionHostsKey is { } selection
        && string.Equals(reachedHostsListedWhole, selection, StringComparison.Ordinal);

    /// <summary>
    /// What the list leaves out with every host asked for, past the most it lists, and where every one is read: a
    /// process's in `icat processes --pid --json`, a program's in `icat hosts --program`, which says how to name one of
    /// two programs of one name. Empty otherwise, where the button that asks for them says how many there are.
    /// </summary>
    public string ReachedHostsMore => ListsAllReachedHosts && HostsOfSelection().Hosts.Count - ReachedHostsListed is > 0 and var more
        ? string.Create(CultureInfo.CurrentCulture, $"{more:N0} more, quieter: ")
            + (ShowsLineage && selectedProcess is { } selected
                ? string.Create(CultureInfo.CurrentCulture, $"icat processes --pid {selected.ProcessId} --json lists them all")
                : $"icat hosts --program {CommandArgument.Of(DescribedGroup?.Name ?? string.Empty)} lists them all")
        : string.Empty;

    /// <summary>Whether the list says what it leaves out with every host asked for.</summary>
    public bool ShowsReachedHostsMore => ReachedHostsMore.Length > 0;

    /// <summary>Whether the inspector offers every host, or the busiest again: the selection reached more than it names.</summary>
    public bool OffersReachedHostsToggle => HostsOfSelection().Hosts.Count > ReachedHostsNamed;

    /// <summary>What that button says: how many it would list, or that it names the busiest again.</summary>
    public string ReachedHostsToggleLabel => ListsAllReachedHosts
        ? string.Create(CultureInfo.CurrentCulture, $"Show the busiest {ReachedHostsNamed}")
        : HostsOfSelection().Hosts.Count is var count && count <= ReachedHostsListed
            ? string.Create(CultureInfo.CurrentCulture, $"Show all {count:N0} hosts")
            : string.Create(CultureInfo.CurrentCulture, $"Show the busiest {ReachedHostsListed:N0} of {count:N0} hosts");

    /// <summary>
    /// Lists every other host the selection reached, at most <see cref="ReachedHostsListed"/>, or names the busiest again,
    /// as its button asks; true where it now lists every one. A selection that reached no more hosts than the inspector
    /// names offers neither.
    /// </summary>
    public bool ToggleReachedHosts()
    {
        if (!OffersReachedHostsToggle)
        {
            return false;
        }

        reachedHostsListedWhole = ListsAllReachedHosts ? null : SelectionHostsKey;
        RaiseReachedHostsChanged();
        return ListsAllReachedHosts;
    }

    /// <summary>
    /// Finds every process that reached one of the hosts the selection reached, as a search for its address does: the
    /// search box takes the address, the rail lists the processes and the graph marks them, while the selection stays.
    /// False for a host the list does not hold.
    /// </summary>
    public bool FindReachedHost(ReachedHostRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!ReachedHostRows.Any(listed => string.Equals(listed.Address, row.Address, StringComparison.Ordinal)))
        {
            return false;
        }

        SearchText = row.Address;
        return true;
    }

    /// <summary>
    /// What the inspector lists the other hosts of: the selected process, by its instance, while its own are described, or
    /// the described program, chosen or opened, by its group's key, while its grouping is; null for any other selection.
    /// </summary>
    private string? SelectionHostsKey => ShowsLineage && selectedProcess is { } selected ? selected.Id.ToString()
        : GroupingExplanation.Length > 0 && DescribedGroup is { } group ? group.Key
        : null;

    /// <summary>
    /// The other hosts the selection reached, the busiest first, who reached each by its address, and whether several of
    /// its processes reached them.
    /// </summary>
    private (IReadOnlyList<OtherHost> Hosts, IReadOnlyDictionary<string, string> ReachedBy, bool Several) HostsOfSelection()
    {
        if (SelectionHostsKey is not { } selection)
        {
            return ([], new Dictionary<string, string>(), false);
        }

        if (reachedHosts is { } known && string.Equals(known.Selection, selection, StringComparison.Ordinal))
        {
            return (known.Hosts, known.ReachedBy, known.Several);
        }

        HashSet<ProcessInstanceId> members = ShowsLineage && selectedProcess is { } process ? [process.Id]
            : [.. wholeSnapshot.Processes.Where(member => member.GroupKey == selection).Select(member => member.Id)];
        HostLink[] links = [.. wholeSnapshot.HostLinks.Where(link => members.Contains(link.Process))];
        bool several = links.Select(link => link.Process).Distinct().Skip(1).Any();
        reachedHosts = (selection, OtherHost.Of(links), ReachedByAddress(links), several);
        return (reachedHosts.Value.Hosts, reachedHosts.Value.ReachedBy, several);
    }

    /// <summary>Says again what the list of the other hosts the selection reached holds.</summary>
    private void RaiseReachedHostsChanged()
    {
        OnPropertyChanged(nameof(ReachedHostRows));
        OnPropertyChanged(nameof(ShowsReachedHosts));
        OnPropertyChanged(nameof(ReachedHostsHeading));
        OnPropertyChanged(nameof(ReachedHostsListName));
        OnPropertyChanged(nameof(ListsAllReachedHosts));
        OnPropertyChanged(nameof(ReachedHostsMore));
        OnPropertyChanged(nameof(ShowsReachedHostsMore));
        OnPropertyChanged(nameof(OffersReachedHostsToggle));
        OnPropertyChanged(nameof(ReachedHostsToggleLabel));
    }
}
