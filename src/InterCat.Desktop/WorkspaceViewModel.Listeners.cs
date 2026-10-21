using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// Listening ports in the inspector (§6.3): the session's while it describes nothing on the machine rung, and a chosen
/// program's processes' while it describes the program - each port they accepted TCP connections at, with the process that
/// accepted them, how many and who connected, the busiest first, as `icat ports` lists them. A machine's servers, or which
/// of a program's processes served at which port and for whom, otherwise took each process's card in turn. Enter on one
/// chooses the process that accepted its connections, whose inspector then says with whom, and Esc comes back to the list -
/// nothing chosen again, or the program - with the keyboard on that port.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>The most listening ports the inspector names, the busiest.</summary>
    internal const int ListenersNamed = 5;

    /// <summary>The most listening ports the inspector lists once every one is asked for.</summary>
    internal const int ListenersListed = 100;

    /// <summary>
    /// Whose listening ports the inspector lists whole, as asked: the machine's, an empty key, or a program's, by its
    /// group's key; null while it names the busiest.
    /// </summary>
    private string? listenersListedWhole;

    /// <summary>The way down made by <see cref="ChooseListener"/>, which Esc comes back along; kept for a newer generation.</summary>
    private ListenerHop? listenerHop;

    /// <summary>
    /// The scope the list was last said for, so a selection that leaves it where it was restates nothing; until it is first
    /// said, a scope no list has, so the first selection always says it.
    /// </summary>
    private string? raisedListenerScope = UnsaidListenerScope;

    /// <summary>The scope no list has, which the list has been said for before it is first said.</summary>
    private const string UnsaidListenerScope = "unsaid:";

    /// <summary>The rows last built, for the scope, the list asked for whole or not, and the candidate other ends read.</summary>
    private (string Scope, bool Whole, object Candidates, IReadOnlyList<ListenerRow> Rows)? listenerRows;

    /// <summary>The session's processes by instance, and its programs by key, built once to name the listening ports' ends.</summary>
    private (Dictionary<ProcessInstanceId, ProcessNode> Processes, Dictionary<string, ProcessGroup> Groups)? listenerNames;

    /// <summary>
    /// The listening ports the inspector lists, the busiest first: each by its port and the process that accepted
    /// connections at it, then how many and who connected - 8443, "files.exe · PID 5300", "1 connection accepted from
    /// 10.0.0.10". Built once for what they depend on, and empty while the inspector lists none.
    /// </summary>
    public IReadOnlyList<ListenerRow> ListenerRows
    {
        get
        {
            if (ListenerScope is not { } scope)
            {
                return [];
            }

            bool whole = ListsAllListeners;
            if (listenerRows is { } built && built.Scope == scope && built.Whole == whole
                && ReferenceEquals(built.Candidates, candidateListeners))
            {
                return built.Rows;
            }

            IReadOnlyList<ListenerRow> rows = [.. ScopedListeners(scope).Take(whole ? ListenersListed : ListenersNamed)
                .Select(ListenerRowOf)];
            listenerRows = (scope, whole, candidateListeners, rows);
            return rows;
        }
    }

    /// <summary>
    /// Whether the inspector lists them: on the machine rung while it describes nothing - no process, program, aggregate,
    /// relationship, set or cell chosen - or a program, whose processes accepted any connection the capture saw open.
    /// </summary>
    public bool ShowsListeners => ListenerScope is { } scope && ScopedListeners(scope).Count > 0;

    /// <summary>
    /// The heading over them: how many there are, the busiest first where there are several, and whose - "6 listening ports,
    /// the busiest first", "2 listening ports of its processes, the busiest first".
    /// </summary>
    public string ListenersHeading => ListenerScope is { } scope && ScopedListeners(scope).Count is var count
        ? (count == 1 ? "1 listening port" : string.Create(CultureInfo.CurrentCulture, $"{count:N0} listening ports"))
            + (scope.Length > 0 ? " of its processes" : string.Empty)
            + (count == 1 ? string.Empty : ", the busiest first")
        : string.Empty;

    /// <summary>The list as a screen reader names it: what it holds and what Enter on a row does.</summary>
    public string ListenersListName => ListenersHeading + ": the ports "
        + (ListenerScope is { Length: > 0 } ? "its processes" : "this machine's processes")
        + " accepted connections at; Enter on one chooses the process that accepted them";

    /// <summary>Whether the inspector lists every listening port of its scope, as asked, rather than the busiest few.</summary>
    public bool ListsAllListeners => ListenerScope is { } scope && string.Equals(listenersListedWhole, scope, StringComparison.Ordinal);

    /// <summary>
    /// What the list leaves out with every port asked for, past the most it lists, and where every one is read; empty
    /// otherwise, where the button that asks for them says how many there are.
    /// </summary>
    public string ListenersMore => ListsAllListeners && ListenerScope is { } scope
        && ScopedListeners(scope).Count - ListenersListed is > 0 and var more
        ? string.Create(CultureInfo.CurrentCulture,
            $"{more:N0} more, quieter: icat ports --limit {wholeSnapshot.Listeners.Count:N0} lists them all")
            + (scope.Length > 0 ? ", among the machine's" : string.Empty)
        : string.Empty;

    /// <summary>Whether the list says what it leaves out with every port asked for.</summary>
    public bool ShowsListenersMore => ListenersMore.Length > 0;

    /// <summary>
    /// What the candidate other ends its rows name are, beneath them, for a session opened from its investigation: never an
    /// established join; empty where none is named. The rows say which ports they are named for.
    /// </summary>
    public string ListenersNote => ListenerScope is { } scope && ScopedListeners(scope).Any(listener => CandidatesFor(listener) is not null)
        ? "The candidate other ends named here are processes of the investigation's other sessions whose connections mirror "
            + "those the ports accepted, never an established join."
        : string.Empty;

    /// <summary>Whether the list says what the candidate other ends it names are.</summary>
    public bool ShowsListenersNote => ListenersNote.Length > 0;

    /// <summary>Whether the inspector offers every listening port, or the busiest again: there are more than it names.</summary>
    public bool OffersListenersToggle => ListenerScope is { } scope && ScopedListeners(scope).Count > ListenersNamed;

    /// <summary>What that button says: how many it would list, or that it names the busiest again.</summary>
    public string ListenersToggleLabel => ListsAllListeners
        ? string.Create(CultureInfo.CurrentCulture, $"Show the busiest {ListenersNamed}")
        : ListenerScope is { } scope && ScopedListeners(scope).Count is var count
            ? count <= ListenersListed
                ? string.Create(CultureInfo.CurrentCulture, $"Show all {count:N0} listening ports")
                : string.Create(CultureInfo.CurrentCulture, $"Show the busiest {ListenersListed:N0} of {count:N0} listening ports")
            : string.Empty;

    /// <summary>
    /// Lists every listening port of the inspector's scope, at most <see cref="ListenersListed"/>, or names the busiest
    /// again, as its button asks; true where it now lists every one. A scope with no more than the inspector names offers
    /// neither.
    /// </summary>
    public bool ToggleListeners()
    {
        if (!OffersListenersToggle)
        {
            return false;
        }

        listenersListedWhole = ListsAllListeners ? null : ListenerScope;
        RaiseListenersChanged();
        return ListsAllListeners;
    }

    /// <summary>
    /// Chooses the process that accepted the connections of one of the listed ports, as a click on its node does: its
    /// inspector then says who connected to it, at which port, and <see cref="ComeBackFromListener"/> comes back to the
    /// list. False for a port the list does not hold.
    /// </summary>
    public bool ChooseListener(ListenerRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (ListenerScope is not { } scope || !ListenerRows.Any(listed => listed.Port == row.Port && listed.Process == row.Process))
        {
            return false;
        }

        SelectProcess(row.Process);
        listenerHop = new ListenerHop(row.Port, row.Process, ladder.Depth) { Program = scope.Length > 0 ? scope : null };
        return true;
    }

    /// <summary>
    /// Comes back along the way down made from the listening ports, as Esc does before it climbs the ladder: where the
    /// process it chose is still the one chosen, on the rung it was chosen at, what the list was listed for is chosen again
    /// - nothing, or the program, where this generation still holds it - and the hop is returned, whose row the keyboard
    /// goes back to. A hop the selection or the ladder has since moved away from is let go, and null returned.
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

        if (hop.Program is { } program)
        {
            if (Groups(program) is null)
            {
                return null;
            }

            SelectGroup(program, syncRow: true);
            return hop;
        }

        ClearSelection();
        return hop;
    }

    /// <summary>
    /// Whose listening ports the inspector lists: the machine's, an empty key, while it describes nothing on the machine
    /// rung, or a chosen program's, by its group's key, while it describes the program; null for any other selection.
    /// </summary>
    private string? ListenerScope => ladder.Current.Level != DetailLevel.Machine ? null
        : DescribesNothing ? string.Empty
        : GroupingExplanation.Length > 0 && SelectedGroup is { Kind: LaneGrouping.Executable } program ? program.Key
        : null;

    /// <summary>The listening ports of a scope, the busiest first: the machine's every one, or a program's processes'.</summary>
    private IReadOnlyList<PortListener> ScopedListeners(string scope) => scope.Length == 0 ? wholeSnapshot.Listeners
        : [.. wholeSnapshot.Listeners.Where(listener =>
            string.Equals(NodeOf(listener.Process)?.GroupKey, scope, StringComparison.Ordinal))];

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
    /// accepted · 4 from 4 processes of 4 programs · 1 from 10.0.0.6" - and, in a session opened from its investigation,
    /// the candidate other ends its connections from other hosts mirror: "candidate other ends: demo client: browser.exe ·
    /// PID 4100".
    /// </summary>
    private ListenerRow ListenerRowOf(PortListener listener)
    {
        string process = ProcessNamed(listener.Process);
        string port = listener.Port.ToString(CultureInfo.InvariantCulture);
        string accepted = Spoken.Count(listener.Accepted, "connection");
        IReadOnlyList<(long Connections, string Who)> from = ListenerText.From(listener, ProcessNamed, ProgramOf);
        string[] parts = from is [var only] ? ["from " + only.Who]
            : [.. from.Select(part => string.Create(CultureInfo.CurrentCulture, $"{part.Connections:N0} from {part.Who}"))];
        string candidates = CandidatesFor(listener) is { } ends ? "candidate other ends: " + CandidateEnd.Listed(ends) : string.Empty;
        // The row shows the process with its PID kept to its number, so a narrow inspector never breaks "PID" from it.
        return new ListenerRow(process.Replace("PID ", "PID\u00A0", StringComparison.Ordinal),
            (from.Count == 1 ? $"{accepted} accepted {parts[0]}" : string.Join(" · ", [accepted + " accepted", .. parts]))
                + (candidates.Length > 0 ? " · " + candidates : string.Empty),
            $"{process} accepted {accepted} at port {port}" + string.Concat(parts.Select(part => ", " + part))
                + (candidates.Length > 0 ? ", " + candidates : string.Empty))
        {
            Port = listener.Port,
            Process = listener.Process.Id,
        };
    }

    /// <summary>A process as the inspector's lists name it, "files.exe · PID 5300", or by its PID where the session holds no node of it.</summary>
    private string ProcessNamed(ProcessInstance process) => NodeOf(process)?.NameWithPid
        ?? string.Create(CultureInfo.InvariantCulture, $"PID {process.ProcessId}");

    /// <summary>The program a process ran, as the session groups it, or the process itself where it holds none.</summary>
    private string ProgramOf(ProcessInstance process) =>
        NodeOf(process) is { } node && Groups(node.GroupKey) is { } group ? group.Name : ProcessNamed(process);

    /// <summary>The session's node of a process; null where it holds none.</summary>
    private ProcessNode? NodeOf(ProcessInstance process) => ListenerNames().Processes.GetValueOrDefault(process.Id);

    /// <summary>The session's program of a key; null where it holds none.</summary>
    private ProcessGroup? Groups(string key) => ListenerNames().Groups.GetValueOrDefault(key);

    private (Dictionary<ProcessInstanceId, ProcessNode> Processes, Dictionary<string, ProcessGroup> Groups) ListenerNames() =>
        listenerNames ??= (wholeSnapshot.Processes.ToDictionary(node => node.Id),
            wholeSnapshot.Groups.ToDictionary(group => group.Key, StringComparer.Ordinal));

    /// <summary>Says again what the list of listening ports holds where what the inspector describes moved it to another scope.</summary>
    private void FollowListenerScope()
    {
        if (!string.Equals(ListenerScope, raisedListenerScope, StringComparison.Ordinal))
        {
            RaiseListenersChanged();
        }
    }

    /// <summary>Says again what the list of listening ports holds and whether it shows.</summary>
    private void RaiseListenersChanged()
    {
        raisedListenerScope = ListenerScope;
        OnPropertyChanged(nameof(ListenerRows));
        OnPropertyChanged(nameof(ShowsListeners));
        OnPropertyChanged(nameof(ListenersHeading));
        OnPropertyChanged(nameof(ListenersListName));
        OnPropertyChanged(nameof(ListsAllListeners));
        OnPropertyChanged(nameof(ListenersMore));
        OnPropertyChanged(nameof(ShowsListenersMore));
        OnPropertyChanged(nameof(ListenersNote));
        OnPropertyChanged(nameof(ShowsListenersNote));
        OnPropertyChanged(nameof(OffersListenersToggle));
        OnPropertyChanged(nameof(ListenersToggleLabel));
    }
}

/// <summary>
/// A way down made from the inspector's listening ports (§6.3): the port and the process chosen from it, on the rung it
/// was chosen at, which Esc comes back along to the list.
/// </summary>
public sealed record ListenerHop(ushort Port, ProcessInstanceId Process, int Depth)
{
    /// <summary>The program, by its group's key, whose processes' ports the list was listed for; null for the machine's.</summary>
    public string? Program { get; init; }
}
