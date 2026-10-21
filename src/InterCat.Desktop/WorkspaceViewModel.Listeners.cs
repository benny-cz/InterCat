using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// The session's listening ports, in the inspector while it describes nothing on the machine rung (§6.3): each port its
/// processes accepted TCP connections at, with the process that accepted them, how many and who connected, the busiest
/// first, as `icat ports` lists them. A machine's servers - which process served at which port, and for whom - otherwise
/// took each process's card in turn. Enter on one chooses the process that accepted its connections, whose inspector then
/// says with whom, and Esc comes back to the list with the keyboard on that port.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>The most listening ports the inspector names, the busiest.</summary>
    internal const int ListenersNamed = 5;

    /// <summary>The most listening ports the inspector lists once every one is asked for.</summary>
    internal const int ListenersListed = 100;

    /// <summary>Whether the inspector lists every listening port, as asked, rather than the busiest few.</summary>
    private bool listenersListedWhole;

    /// <summary>The way down made by <see cref="ChooseListener"/>, which Esc comes back along; kept for a newer generation.</summary>
    private ListenerHop? listenerHop;

    /// <summary>
    /// The session's listening ports, the busiest first: each by its port and the process that accepted connections at it,
    /// then how many and who connected - 8443, "files.exe · PID 5300", "1 connection accepted from 10.0.0.10".
    /// </summary>
    public IReadOnlyList<ListenerRow> ListenerRows => [.. wholeSnapshot.Listeners
        .Take(listenersListedWhole ? ListenersListed : ListenersNamed)
        .Select(ListenerRowOf)];

    /// <summary>
    /// Whether the inspector lists them: on the machine rung while it describes nothing - no process, program, aggregate,
    /// relationship, set or cell chosen - in a session whose processes accepted any connection the capture saw open.
    /// </summary>
    public bool ShowsListeners => ladder.Current.Level == DetailLevel.Machine && DescribesNothing
        && wholeSnapshot.Listeners.Count > 0;

    /// <summary>The heading over them: how many there are, the busiest first where there are several.</summary>
    public string ListenersHeading => wholeSnapshot.Listeners.Count == 1 ? "1 listening port"
        : string.Create(CultureInfo.CurrentCulture, $"{wholeSnapshot.Listeners.Count:N0} listening ports, the busiest first");

    /// <summary>The list as a screen reader names it: what it holds and what Enter on a row does.</summary>
    public string ListenersListName => ListenersHeading + ": the ports this machine's processes accepted connections at; "
        + "Enter on one chooses the process that accepted them";

    /// <summary>Whether the inspector lists every listening port, as asked, rather than the busiest few.</summary>
    public bool ListsAllListeners => listenersListedWhole;

    /// <summary>
    /// What the list leaves out with every port asked for, past the most it lists, and where every one is read; empty
    /// otherwise, where the button that asks for them says how many there are.
    /// </summary>
    public string ListenersMore => listenersListedWhole && wholeSnapshot.Listeners.Count - ListenersListed is > 0 and var more
        ? string.Create(CultureInfo.CurrentCulture,
            $"{more:N0} more, quieter: icat ports --limit {wholeSnapshot.Listeners.Count:N0} lists them all")
        : string.Empty;

    /// <summary>Whether the list says what it leaves out with every port asked for.</summary>
    public bool ShowsListenersMore => ListenersMore.Length > 0;

    /// <summary>Whether the inspector offers every listening port, or the busiest again: there are more than it names.</summary>
    public bool OffersListenersToggle => wholeSnapshot.Listeners.Count > ListenersNamed;

    /// <summary>What that button says: how many it would list, or that it names the busiest again.</summary>
    public string ListenersToggleLabel => listenersListedWhole
        ? string.Create(CultureInfo.CurrentCulture, $"Show the busiest {ListenersNamed}")
        : wholeSnapshot.Listeners.Count is var count && count <= ListenersListed
            ? string.Create(CultureInfo.CurrentCulture, $"Show all {count:N0} listening ports")
            : string.Create(CultureInfo.CurrentCulture, $"Show the busiest {ListenersListed:N0} of {count:N0} listening ports");

    /// <summary>
    /// Lists every listening port, at most <see cref="ListenersListed"/>, or names the busiest again, as its button asks;
    /// true where it now lists every one. A session with no more than the inspector names offers neither.
    /// </summary>
    public bool ToggleListeners()
    {
        if (!OffersListenersToggle)
        {
            return false;
        }

        listenersListedWhole = !listenersListedWhole;
        RaiseListenersChanged();
        return listenersListedWhole;
    }

    /// <summary>
    /// Chooses the process that accepted the connections of one of the listed ports, as a click on its node does: its
    /// inspector then says who connected to it, at which port, and <see cref="ComeBackFromListener"/> comes back to the
    /// list. False for a port the list does not hold.
    /// </summary>
    public bool ChooseListener(ListenerRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!ShowsListeners || !ListenerRows.Any(listed => listed.Port == row.Port && listed.Process == row.Process))
        {
            return false;
        }

        SelectProcess(row.Process);
        listenerHop = new ListenerHop(row.Port, row.Process, ladder.Depth);
        return true;
    }

    /// <summary>
    /// Comes back along the way down made from the listening ports, as Esc does before it climbs the ladder: where the
    /// process it chose is still the one chosen, on the rung it was chosen at, nothing is chosen again and the hop is
    /// returned, whose row the keyboard goes back to. A hop the selection or the ladder has since moved away from is let go,
    /// and null returned.
    /// </summary>
    public ListenerHop? ComeBackFromListener()
    {
        if (listenerHop is not { } hop)
        {
            return null;
        }

        listenerHop = null;
        if (selectedProcess?.Id != hop.Process || ladder.Depth != hop.Depth)
        {
            return null;
        }

        ClearSelection();
        return hop;
    }

    /// <summary>
    /// Whether the inspector describes nothing, as its title says "Nothing selected": no relationship, row, set, aggregate,
    /// program, opened channel, process or interval chosen.
    /// </summary>
    private bool DescribesNothing => selectedRelationship is null && DescribedRow is null && !HasMultiSelection
        && SelectedCluster is null && SelectedGroup is null && !DescribesOpened && selectedProcess is null
        && IntervalTitle is null;

    /// <summary>
    /// One listening port as the inspector lists it: where and whose, then how many it accepted and from whom - one kind of
    /// end said with the count, "1 connection accepted from 10.0.0.10", several each with their own, "5 connections
    /// accepted · 4 from 4 processes of 4 programs · 1 from 10.0.0.6".
    /// </summary>
    private ListenerRow ListenerRowOf(PortListener listener)
    {
        string process = ProcessNamed(listener.Process);
        string port = listener.Port.ToString(CultureInfo.InvariantCulture);
        string accepted = Spoken.Count(listener.Accepted, "connection");
        IReadOnlyList<(long Connections, string Who)> from = ListenerText.From(listener, ProcessNamed, ProgramOf);
        string[] parts = from is [var only] ? ["from " + only.Who]
            : [.. from.Select(part => string.Create(CultureInfo.CurrentCulture, $"{part.Connections:N0} from {part.Who}"))];
        return new ListenerRow(process,
            from.Count == 1 ? $"{accepted} accepted {parts[0]}" : string.Join(" · ", [accepted + " accepted", .. parts]),
            $"{process} accepted {accepted} at port {port}" + string.Concat(parts.Select(part => ", " + part)))
        {
            Port = listener.Port,
            Process = listener.Process.Id,
        };
    }

    /// <summary>A process as the inspector's lists name it, "files.exe · PID 5300", or by its PID where the session holds no node of it.</summary>
    private string ProcessNamed(ProcessInstance process) =>
        wholeSnapshot.Processes.FirstOrDefault(node => node.Id == process.Id)?.NameWithPid
        ?? string.Create(CultureInfo.InvariantCulture, $"PID {process.ProcessId}");

    /// <summary>The program a process ran, as the session groups it, or the process itself where it holds none.</summary>
    private string ProgramOf(ProcessInstance process) =>
        wholeSnapshot.Processes.FirstOrDefault(node => node.Id == process.Id) is { } node
        && wholeSnapshot.Groups.FirstOrDefault(group => group.Key == node.GroupKey) is { } group
            ? group.Name
            : ProcessNamed(process);

    /// <summary>Says again what the list of listening ports holds and whether it shows.</summary>
    private void RaiseListenersChanged()
    {
        OnPropertyChanged(nameof(ListenerRows));
        OnPropertyChanged(nameof(ShowsListeners));
        OnPropertyChanged(nameof(ListsAllListeners));
        OnPropertyChanged(nameof(ListenersMore));
        OnPropertyChanged(nameof(ShowsListenersMore));
        OnPropertyChanged(nameof(OffersListenersToggle));
        OnPropertyChanged(nameof(ListenersToggleLabel));
    }
}

/// <summary>
/// A way down made from the inspector's listening ports (§6.3): the port and the process chosen from it, on the rung it
/// was chosen at, which Esc comes back along to the list.
/// </summary>
public sealed record ListenerHop(ushort Port, ProcessInstanceId Process, int Depth);
