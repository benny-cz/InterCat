using System.Globalization;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// The other hosts a chosen process reached, in the inspector (§6.3): by address, the busiest first, as `icat processes
/// --pid` lists them. Most of a busy machine's connections run to other hosts, which the graph draws to one node; here a
/// process's own are read, and Enter on one finds every process that reached it, as a search for its address does.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>The most other hosts the inspector names for a chosen process, the busiest.</summary>
    internal const int ReachedHostsNamed = 5;

    /// <summary>The most other hosts the inspector lists for a chosen process once every one is asked for.</summary>
    internal const int ReachedHostsListed = 100;

    /// <summary>The process whose other hosts the inspector lists whole, as asked; null while it names the busiest.</summary>
    private ProcessInstanceId? reachedHostsListedWhole;

    /// <summary>The other hosts the process last asked about reached, summed once for it.</summary>
    private (ProcessInstanceId Process, IReadOnlyList<OtherHost> Hosts)? reachedHosts;

    /// <summary>
    /// The other hosts the selected process reached over the whole session, the busiest first: each by its address, then
    /// its scope where one is known, what reached it, the ports at the address, those it accepted from it said apart at
    /// its own ports, and the records - "203.0.113.1", "2 TCP connections · port 443 · 6 records". Empty for any other
    /// selection, and for a process that reached none.
    /// </summary>
    public IReadOnlyList<ReachedHostRow> ReachedHostRows => [.. HostsOfSelection()
        .Take(ListsAllReachedHosts ? ReachedHostsListed : ReachedHostsNamed)
        .Select(host => new ReachedHostRow(host.Address, string.Join(" · ", new[]
        {
            AddressScope.Of(host.Address),
            HostLinkText.Reached(host, countsProcesses: false),
            host.Ports.Count == 0 ? null : HostLinkText.Ports(host.Ports),
            HostLinkText.Accepted(host) is { Length: > 0 } accepted ? accepted : null,
            CountText.Of(host.Records, "record"),
        }.OfType<string>())))];

    /// <summary>Whether the inspector lists the other hosts the selected process reached.</summary>
    public bool ShowsReachedHosts => HostsOfSelection().Count > 0;

    /// <summary>The heading over them, saying under a brush that they are the whole session's.</summary>
    public string ReachedHostsHeading => "Other hosts it reached, the busiest first"
        + (appliedInterval is null ? string.Empty : " · whole session");

    /// <summary>
    /// The list as a screen reader names it: what it holds and what Enter on a row does. Each row says itself whole as the
    /// keyboard reaches it.
    /// </summary>
    public string ReachedHostsListName => ReachedHostsHeading.Replace(" · ", ", over the ", StringComparison.Ordinal)
        + "; Enter on one finds the processes that reached it";

    /// <summary>Whether the inspector lists every other host the selected process reached, as asked, rather than the busiest few.</summary>
    public bool ListsAllReachedHosts => selectedProcess is { } selected && reachedHostsListedWhole == selected.Id;

    /// <summary>
    /// What the list leaves out with every host asked for, past the most it lists, and where every one is read; empty
    /// otherwise, where the button that asks for them says how many there are.
    /// </summary>
    public string ReachedHostsMore => ListsAllReachedHosts && selectedProcess is { } selected
        && HostsOfSelection().Count - ReachedHostsListed is > 0 and var more
        ? string.Create(CultureInfo.CurrentCulture,
            $"{more:N0} more, quieter: icat processes --pid {selected.ProcessId} --json lists them all")
        : string.Empty;

    /// <summary>Whether the list says what it leaves out with every host asked for.</summary>
    public bool ShowsReachedHostsMore => ReachedHostsMore.Length > 0;

    /// <summary>Whether the inspector offers every host, or the busiest again: the process reached more than it names.</summary>
    public bool OffersReachedHostsToggle => HostsOfSelection().Count > ReachedHostsNamed;

    /// <summary>What that button says: how many it would list, or that it names the busiest again.</summary>
    public string ReachedHostsToggleLabel => ListsAllReachedHosts
        ? string.Create(CultureInfo.CurrentCulture, $"Show the busiest {ReachedHostsNamed}")
        : HostsOfSelection().Count is var count && count <= ReachedHostsListed
            ? string.Create(CultureInfo.CurrentCulture, $"Show all {count:N0} hosts")
            : string.Create(CultureInfo.CurrentCulture, $"Show the busiest {ReachedHostsListed:N0} of {count:N0} hosts");

    /// <summary>
    /// Lists every other host the selected process reached, at most <see cref="ReachedHostsListed"/>, or names the busiest
    /// again, as its button asks; true where it now lists every one. A process that reached no more hosts than the
    /// inspector names offers neither.
    /// </summary>
    public bool ToggleReachedHosts()
    {
        if (!OffersReachedHostsToggle)
        {
            return false;
        }

        reachedHostsListedWhole = ListsAllReachedHosts ? null : selectedProcess?.Id;
        RaiseReachedHostsChanged();
        return ListsAllReachedHosts;
    }

    /// <summary>
    /// Finds every process that reached one of the hosts the selected process reached, as a search for its address does:
    /// the search box takes the address, the rail lists the processes and the graph marks them, while the process stays
    /// chosen. False for a host the list does not hold.
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

    /// <summary>The other hosts the selected process reached, the busiest first, while its own are what the inspector describes.</summary>
    private IReadOnlyList<OtherHost> HostsOfSelection()
    {
        if (!ShowsLineage || selectedProcess is not { } selected)
        {
            return [];
        }

        if (reachedHosts is { } known && known.Process == selected.Id)
        {
            return known.Hosts;
        }

        IReadOnlyList<OtherHost> hosts = OtherHost.Of(wholeSnapshot.HostLinks.Where(link => link.Process == selected.Id));
        reachedHosts = (selected.Id, hosts);
        return hosts;
    }

    /// <summary>Says again what the list of the other hosts the selected process reached holds.</summary>
    private void RaiseReachedHostsChanged()
    {
        OnPropertyChanged(nameof(ReachedHostRows));
        OnPropertyChanged(nameof(ShowsReachedHosts));
        OnPropertyChanged(nameof(ListsAllReachedHosts));
        OnPropertyChanged(nameof(ReachedHostsMore));
        OnPropertyChanged(nameof(ShowsReachedHostsMore));
        OnPropertyChanged(nameof(OffersReachedHostsToggle));
        OnPropertyChanged(nameof(ReachedHostsToggleLabel));
    }
}
