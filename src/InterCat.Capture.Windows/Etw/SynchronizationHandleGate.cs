namespace InterCat.Capture.Windows;

/// <summary>
/// Admits the object manager's handle records of the synchronization types - events, mutants and semaphores, by the numbers
/// measured for the capture's build (ADR-082's fourth addendum), which each capture holds to its own type rundown at its
/// stop - and numbers the object each is of (ADR-083), following each from the first handle the capture sees made at its
/// address to the close of the last, as <see cref="ObjectHandleGate"/> follows any type it is given. No product code asks the
/// object manager for its type table: the query that reads it is the one that names a duplicated handle's object, which P20's
/// fitness check keeps out of the product, so only the probe reads it, to measure a build. The handle group delivers every
/// type's handles, a synchronization object's about one in thirty.
/// </summary>
public sealed class SynchronizationHandleGate
{
    /// <summary>A handle's creation, its close, its duplication, and the rundown of one still open at the stop.</summary>
    public const int Create = ObjectHandleGate.Create;
    public const int Close = ObjectHandleGate.Close;
    public const int Duplicate = ObjectHandleGate.Duplicate;
    public const int Rundown = ObjectHandleGate.Rundown;

    /// <summary>The type rundown at the stop: each type's number and name, which a capture holds the build's numbers to.</summary>
    public const int TypeRundown = ObjectHandleGate.TypeRundown;

    /// <summary>The bit a kernel handle's value has set, as the records carry a handle in 32 bits.</summary>
    public const uint KernelHandleBit = ObjectHandleGate.KernelHandleBit;

    /// <summary>How many handles the gate holds at most, and so how many objects it follows.</summary>
    public const int DefaultCapacity = ObjectHandleGate.DefaultCapacity;

    /// <summary>The object types the gate follows, by the names an object type table or a type rundown gives them.</summary>
    public static readonly IReadOnlyList<string> SynchronizationTypeNames = ["Event", "Mutant", "Semaphore"];

    // The synchronization types' numbers on each build a fixture measured: the object type table read before a session and
    // the session's type rundown at its stop named them alike, and FX-SYNC-001's records carried them (ADR-082's fourth
    // addendum). A build not here has no gate, as its numbers are unmeasured.
    private static readonly Dictionary<string, int[]> Measured = new(StringComparer.Ordinal)
    {
        ["10.0.26220.0-x64"] = [21, 22, 24],
    };

    private readonly ObjectHandleGate gate;

    /// <param name="synchronizationTypes">The type numbers of the synchronization types, measured for the capture's build.</param>
    /// <param name="capacity">How many handles the gate holds at most, and so how many objects it follows.</param>
    public SynchronizationHandleGate(IEnumerable<int> synchronizationTypes, int capacity = DefaultCapacity) =>
        gate = new ObjectHandleGate(synchronizationTypes, capacity);

    /// <summary>How many objects the gate follows now.</summary>
    public int Following => gate.Following;

    /// <summary>How many handles the gate holds now: never more than its bound.</summary>
    public int Holding => gate.Holding;

    /// <summary>The synchronization types' numbers measured for a build, Event's, Mutant's and Semaphore's; null for a build none measured.</summary>
    public static IReadOnlyList<int>? MeasuredTypes(string buildId)
    {
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        return Measured.TryGetValue(buildId, out int[]? numbers) ? numbers : null;
    }

    /// <summary>
    /// The synchronization types' numbers in a type table - a session's type rundown, which holds a build's measured numbers
    /// to the capture it ran in: each name's, where the table has it.
    /// </summary>
    public static IReadOnlyList<int> TypesIn(IReadOnlyDictionary<int, string> typeTable) => ObjectHandleGate.TypesIn(typeTable, SynchronizationTypeNames);

    /// <summary>Reads one record as it arrives: what the gate decided of it, and the object it is of when it is admitted.</summary>
    public (HandleGateOutcome Outcome, ObjectIncarnation? Incarnation) Read(in ObjectHandleRecord record) => gate.Read(record);
}
