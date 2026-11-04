namespace InterCat.Capture.Windows;

/// <summary>
/// One Winsock AFD record as the AF_UNIX gate reads it (ADR-073): its event, its phase - the provider's EnterExit, 0 on a
/// call's entry and 1 on its exit - and location, the endpoint address it names, an accept's new socket, a create's address
/// family and the process it was made for, and the process whose thread raised it.
/// </summary>
public readonly record struct AfdSocketRecord(
    int EventId,
    uint Phase,
    uint Location,
    ulong Endpoint,
    ulong AcceptEndpoint,
    uint AddressFamily,
    int MadeFor,
    int RaisedBy);

/// <summary>What the gate decided of one AFD record.</summary>
public enum AfdGateOutcome
{
    /// <summary>A record of an AF_UNIX socket the capture saw created that counts its life or one of its calls.</summary>
    Admitted = 1,

    /// <summary>A record of such a socket that counts nothing: a call's other records, an option, a transport's indication.</summary>
    NotCounted = 2,

    /// <summary>A record of no AF_UNIX socket the capture saw created: another family's, or one made before the capture.</summary>
    NoUnixSocket = 3,

    /// <summary>A create the gate did not follow, holding as many sockets as it may; its socket's records name none.</summary>
    Untracked = 4,
}

/// <summary>A socket the gate follows: its number in the capture, never its address, and the process its create named.</summary>
public readonly record struct UnixSocketIncarnation(long Number, int Owner);

/// <summary>
/// Admits Winsock AFD's records of the AF_UNIX sockets a capture saw created, one record a call (ADR-073). AFD delivers every
/// socket call of the machine, whatever its family, and names a socket only by its endpoint's kernel address, which the
/// kernel reuses: a socket is followed from the create that names its address as AF_UNIX to its close, or to the next
/// create at that address, owned by the process its create was made for - never the process whose thread raised a record,
/// as a completion is raised on whichever thread finished it. A record of an address no followed socket holds is of none.
/// Records are read as they arrive: FX-UNIX-001 delivered no record of a socket before its create, 0 of 36,088.
/// </summary>
public sealed class UnixSocketGate
{
    /// <summary>The address family of a Unix-domain socket.</summary>
    public const uint AfUnix = 1;

    /// <summary>How many sockets the gate follows at most; a create beyond them is not followed.</summary>
    public const int DefaultCapacity = 65_536;

    /// <summary>How many closed sockets the gate remembers, for the transport's indications that arrive after a close.</summary>
    public const int ClosedRemembered = 4_096;

    // The provider's events the gate reads (Microsoft-Windows-Winsock-AFD).
    private const int Create = 1000;
    private const int Close = 1001;
    private const int Send = 1003;
    private const int Receive = 1004;
    private const int Connect = 1017;
    private const int ConnectEx = 1020;
    private const int Accept = 1023;
    private const int AcceptWithAddress = 1024;
    private const int AcceptExWithAddress = 1027;
    private const int Bind = 1029;
    private const int BindWithAddress = 1030;
    private const int Listen = 1037;

    private const uint Entry = 0;
    private const uint Exit = 1;

    // A send's exit in its own process's context, one of the two exits each send raises, both with its length; measured on
    // build 10.0.26220.0-x64 (ADR-073). A build that raises it elsewhere is a finding, and its sends count none.
    private const uint SendReturned = 3051;

    private readonly Dictionary<ulong, UnixSocketIncarnation> followed = [];
    private readonly Dictionary<ulong, UnixSocketIncarnation> closed = [];
    private readonly Queue<(ulong Endpoint, long Number)> closing = new();
    private readonly int capacity;
    private long numbered;

    public UnixSocketGate(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.capacity = capacity;
    }

    /// <summary>How many sockets the gate follows now.</summary>
    public int Following => followed.Count;

    /// <summary>How many closed sockets the gate remembers now: never more than <see cref="ClosedRemembered"/>.</summary>
    public int Remembered => closed.Count;

    /// <summary>
    /// Whether <paramref name="record"/> counts its socket's life or one of its calls: a create's entry, a close's exit -
    /// a socket's last call, after its cleanup - the exit of a bind, a listen, a connect or an accept, a send's own exit,
    /// and every receive's exit, whose length is what the receive took; its entry's is what it asked for.
    /// </summary>
    public static bool Counts(in AfdSocketRecord record) => record.EventId switch
    {
        Create => record.Phase == Entry,
        Send => record.Phase == Exit && record.Location == SendReturned,
        Close or Receive or Bind or BindWithAddress or Listen or Connect or ConnectEx or Accept or AcceptWithAddress
            or AcceptExWithAddress => record.Phase == Exit,
        _ => false,
    };

    /// <summary>Reads one record as it arrives: what the gate decided of it, and the socket it belongs to when it has one.</summary>
    public (AfdGateOutcome Outcome, UnixSocketIncarnation? Socket) Read(in AfdSocketRecord record)
    {
        if (record.Endpoint == 0)
        {
            return (AfdGateOutcome.NoUnixSocket, null);
        }

        if (record.EventId == Create && record.Phase == Entry)
        {
            // A create begins a socket at its address: whatever the address held before ended, seen or not.
            followed.Remove(record.Endpoint);
            closed.Remove(record.Endpoint);
            if (record.AddressFamily != AfUnix)
            {
                return (AfdGateOutcome.NoUnixSocket, null);
            }

            if (followed.Count >= capacity)
            {
                return (AfdGateOutcome.Untracked, null);
            }

            var made = new UnixSocketIncarnation(++numbered, record.MadeFor != 0 ? record.MadeFor : record.RaisedBy);
            followed[record.Endpoint] = made;
            return (AfdGateOutcome.Admitted, made);
        }

        if (!followed.TryGetValue(record.Endpoint, out UnixSocketIncarnation socket))
        {
            // The transport can indicate a disconnect after the close: such a record is its socket's, and counts nothing.
            return closed.TryGetValue(record.Endpoint, out UnixSocketIncarnation ended)
                ? (AfdGateOutcome.NotCounted, ended)
                : (AfdGateOutcome.NoUnixSocket, null);
        }

        // An accept names the socket it made: one its process created ahead of the accept is followed already, and one
        // the accept made is its listener's owner's.
        if (record.EventId is Accept or AcceptWithAddress or AcceptExWithAddress && record.Phase == Exit
            && record.AcceptEndpoint != 0 && !followed.ContainsKey(record.AcceptEndpoint) && followed.Count < capacity)
        {
            followed[record.AcceptEndpoint] = new(++numbered, socket.Owner);
        }

        if (!Counts(record))
        {
            return (AfdGateOutcome.NotCounted, socket);
        }

        if (record.EventId == Close)
        {
            // Its close ends it; the gate remembers the last few closed, the oldest first forgotten.
            followed.Remove(record.Endpoint);
            closed[record.Endpoint] = socket;
            closing.Enqueue((record.Endpoint, socket.Number));
            while (closing.Count > ClosedRemembered)
            {
                (ulong endpoint, long number) = closing.Dequeue();
                if (closed.TryGetValue(endpoint, out UnixSocketIncarnation remembered) && remembered.Number == number)
                {
                    closed.Remove(endpoint);
                }
            }
        }

        return (AfdGateOutcome.Admitted, socket);
    }
}
