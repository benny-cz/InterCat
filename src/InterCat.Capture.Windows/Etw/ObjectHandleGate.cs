namespace InterCat.Capture.Windows;

/// <summary>
/// One object manager handle record as a handle gate reads it (ADR-083, ADR-090): its opcode - a handle's creation, close or
/// duplication, or the rundown of a handle still open at the session's stop - the object's kernel address, the handle, the
/// object's type number, the process whose handle it is (a duplication's source), and a duplication's target process and
/// handle.
/// </summary>
public readonly record struct ObjectHandleRecord(
    int Opcode,
    ulong ObjectAddress,
    uint Handle,
    int ObjectType,
    int Process,
    int TargetProcess = 0,
    uint TargetHandle = 0);

/// <summary>What a handle gate decided of one handle record.</summary>
public enum HandleGateOutcome
{
    /// <summary>A record of a handle to an object of a type the gate follows, with the object it is of.</summary>
    Admitted = 1,

    /// <summary>A record of another type's handle - a file's, a key's, a timer's - which the gate never follows.</summary>
    OtherType = 2,

    /// <summary>A handle that would begin an object the gate does not follow, holding as many as it may.</summary>
    Untracked = 3,

    /// <summary>
    /// A kernel handle's record - its value's top bit set, a duplication's made handle's - which the system process holds while
    /// its creation and close are raised in whichever process the kernel ran for (FX-SYNC-002): the gate follows none.
    /// </summary>
    KernelHandle = 4,
}

/// <summary>Where a gate first met an object: at a handle the capture saw made, or at one made before the capture began.</summary>
public enum HandleFirstSeen
{
    /// <summary>A handle's creation the capture saw, at an address no followed object held.</summary>
    Creation = 1,

    /// <summary>A handle's duplication into a process the capture saw, at an address no followed object held.</summary>
    Duplication = 2,

    /// <summary>A handle's close, of a handle made before the capture began.</summary>
    Close = 3,

    /// <summary>The rundown at the session's stop, of a handle made before the capture began.</summary>
    Rundown = 4,
}

/// <summary>An object a gate numbered: its number in the capture, never its address, and where the gate first met it.</summary>
public readonly record struct ObjectIncarnation(long Number, HandleFirstSeen FirstSeen);

/// <summary>
/// Follows the objects of the types it is given through the object manager's handle records, numbering each in the capture
/// (ADR-083): the synchronization handle gate's types are an event's, a mutant's and a semaphore's, and the section handle
/// gate's a section's (ADR-090), each by the numbers measured for the capture's build. The handle group delivers every type's
/// handles and names an object only by its kernel address, which the kernel reuses once the object is gone. So an object is
/// followed from the first handle the capture sees made at its address - created or duplicated - to the close of the last
/// handle it saw made to it, the processes holding them and the duplications between them included; whatever the address held
/// before ended, seen or not. A close, or the stop's rundown, of a handle at an address no followed object holds is of a handle
/// made before the capture began: its object's other handles are unseen, so nothing says it is one object with any other
/// record, and it is numbered alone (R22). A kernel handle is the system process's, but its creation and close are raised in
/// whichever process the kernel ran for, so the gate follows none (FX-SYNC-002). Records are read as they arrive, and the gate
/// holds none back. It reads them on the capture's callback, so it allocates nothing per record (P12): a followed object is a
/// value in one table and each handle it holds an entry in another, both grown only as far as its bound.
/// </summary>
public sealed class ObjectHandleGate
{
    /// <summary>A handle's creation, its close, its duplication, and the rundown of one still open at the stop.</summary>
    public const int Create = 32;
    public const int Close = 33;
    public const int Duplicate = 34;
    public const int Rundown = 39;

    /// <summary>The type rundown at the stop: each type's number and name, which a capture holds the build's numbers to.</summary>
    public const int TypeRundown = 37;

    /// <summary>The bit a kernel handle's value has set, as the records carry a handle in 32 bits.</summary>
    public const uint KernelHandleBit = 0x8000_0000;

    /// <summary>
    /// How many handles the gate holds at most, and so how many objects it follows, each holding one at least: a handle that
    /// would begin an object beyond them is not followed, and a further handle of a followed object is its object's but not
    /// held, so the object may end before that handle's close, which is then numbered alone.
    /// </summary>
    public const int DefaultCapacity = 65_536;

    private readonly HashSet<int> types;

    // Each followed object by its address, with how many of the handles the gate holds are its.
    private readonly Dictionary<ulong, Followed> followed = [];

    // Each handle the gate saw made to a followed object and holds until its close: the object's address, the process whose
    // handle it is, and its value there.
    private readonly HashSet<(ulong Address, int Process, uint Handle)> held = [];
    private readonly int capacity;
    private long numbered;

    /// <param name="types">The type numbers of the objects the gate follows, measured for the capture's build.</param>
    /// <param name="capacity">How many handles the gate holds at most, and so how many objects it follows.</param>
    public ObjectHandleGate(IEnumerable<int> types, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.types = [.. types];
        this.capacity = capacity;
    }

    /// <summary>How many objects the gate follows now.</summary>
    public int Following => followed.Count;

    /// <summary>How many handles the gate holds now: never more than its bound.</summary>
    public int Holding => held.Count;

    /// <summary>The numbers a type table - a session's type rundown - gives the types named: each name's, where the table has it.</summary>
    public static IReadOnlyList<int> TypesIn(IReadOnlyDictionary<int, string> typeTable, IReadOnlyList<string> typeNames)
    {
        ArgumentNullException.ThrowIfNull(typeTable);
        ArgumentNullException.ThrowIfNull(typeNames);
        return [.. typeTable.Where(type => typeNames.Contains(type.Value, StringComparer.Ordinal)).Select(type => type.Key).Order()];
    }

    /// <summary>Reads one record as it arrives: what the gate decided of it, and the object it is of when it is admitted.</summary>
    public (HandleGateOutcome Outcome, ObjectIncarnation? Incarnation) Read(in ObjectHandleRecord record)
    {
        if (!types.Contains(record.ObjectType))
        {
            return (HandleGateOutcome.OtherType, null);
        }

        // A kernel handle, made or closed in whichever process the kernel ran for, is never followed, nor a duplication into
        // the system process's table; a duplication from one into a process makes that process a handle of its own.
        if (((record.Opcode == Duplicate ? record.TargetHandle : record.Handle) & KernelHandleBit) != 0)
        {
            return (HandleGateOutcome.KernelHandle, null);
        }

        bool isFollowed = followed.TryGetValue(record.ObjectAddress, out Followed found);
        switch (record.Opcode)
        {
            case Create or Duplicate:
            {
                // A handle made: to the followed object at its address, or the first of an object the capture saw begin
                // there. A duplication makes its handle in its target process.
                if (!isFollowed)
                {
                    if (held.Count >= capacity)
                    {
                        return (HandleGateOutcome.Untracked, null);
                    }

                    found = new(new(++numbered, record.Opcode == Create ? HandleFirstSeen.Creation : HandleFirstSeen.Duplication), 0);
                }

                // Within its bound the gate holds the handle until its close; beyond it the handle is still its object's.
                (int process, uint handle) = record.Opcode == Create ? (record.Process, record.Handle) : (record.TargetProcess, record.TargetHandle);
                if (held.Count < capacity && held.Add((record.ObjectAddress, process, handle)))
                {
                    found = found with { Handles = found.Handles + 1 };
                }

                followed[record.ObjectAddress] = found;
                return (HandleGateOutcome.Admitted, found.Incarnation);
            }

            case Close:
            {
                if (!isFollowed)
                {
                    // A handle made before the capture: its object's other handles are unseen, so it is numbered alone.
                    return (HandleGateOutcome.Admitted, new(++numbered, HandleFirstSeen.Close));
                }

                // The close of the last handle the gate holds of the object ends it: the address may hold another next.
                if (held.Remove((record.ObjectAddress, record.Process, record.Handle)))
                {
                    if (found.Handles == 1)
                    {
                        followed.Remove(record.ObjectAddress);
                    }
                    else
                    {
                        followed[record.ObjectAddress] = found with { Handles = found.Handles - 1 };
                    }
                }

                return (HandleGateOutcome.Admitted, found.Incarnation);
            }

            case Rundown:
                // A handle still open at the stop: the followed object's, or one made before the capture, numbered alone.
                return (HandleGateOutcome.Admitted, isFollowed ? found.Incarnation : new(++numbered, HandleFirstSeen.Rundown));

            default:
                throw new ArgumentOutOfRangeException(nameof(record), record.Opcode, "The gate reads a handle's creation, close, duplication and rundown.");
        }
    }

    /// <summary>A followed object: its incarnation, and how many of the handles the gate holds are its.</summary>
    private readonly record struct Followed(ObjectIncarnation Incarnation, int Handles);
}
