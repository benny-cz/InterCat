namespace InterCat.Capture.Windows;

/// <summary>
/// Admits the object manager's handle records of the Section type, by the number measured for the capture's build (ADR-089's
/// third addendum), which each capture holds to its own type rundown at its stop, and numbers the section each is of
/// (ADR-090): a section is one section by its object's address within its lifetime (ADR-089), so it is followed from the first
/// handle the capture sees made at its address to the close of the last, as <see cref="ObjectHandleGate"/> follows any type it
/// is given, and a handle made before the capture, met at its close or the stop's rundown, is numbered alone. No product code
/// reads the object manager's type table (P20): the gate carries the number measured for the build, and a build none measured
/// has no gate. The handle group delivers every type's handles, a section's about one in thirty.
/// </summary>
public sealed class SectionHandleGate
{
    /// <summary>How many handles the gate holds at most, and so how many sections it follows.</summary>
    public const int DefaultCapacity = ObjectHandleGate.DefaultCapacity;

    /// <summary>The object type the gate follows, by the name an object type table or a type rundown gives it.</summary>
    public static readonly IReadOnlyList<string> SectionTypeNames = ["Section"];

    // The Section type's number on each build a fixture measured: the object type table read before and after a session and
    // the session's type rundown at its stop named it alike (ADR-089's third addendum). A build not here has no gate.
    private static readonly Dictionary<string, int[]> Measured = new(StringComparer.Ordinal)
    {
        ["10.0.26220.0-x64"] = [49],
    };

    private readonly ObjectHandleGate gate;

    /// <param name="sectionTypes">The Section type's number, measured for the capture's build.</param>
    /// <param name="capacity">How many handles the gate holds at most, and so how many sections it follows.</param>
    public SectionHandleGate(IEnumerable<int> sectionTypes, int capacity = DefaultCapacity) =>
        gate = new ObjectHandleGate(sectionTypes, capacity);

    /// <summary>How many sections the gate follows now.</summary>
    public int Following => gate.Following;

    /// <summary>How many handles the gate holds now: never more than its bound.</summary>
    public int Holding => gate.Holding;

    /// <summary>The Section type's number measured for a build; null for a build none measured.</summary>
    public static IReadOnlyList<int>? MeasuredTypes(string buildId)
    {
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        return Measured.TryGetValue(buildId, out int[]? numbers) ? numbers : null;
    }

    /// <summary>The Section type's number in a type table - a session's type rundown, which holds a build's measured number to its capture.</summary>
    public static IReadOnlyList<int> TypesIn(IReadOnlyDictionary<int, string> typeTable) => ObjectHandleGate.TypesIn(typeTable, SectionTypeNames);

    /// <summary>Reads one record as it arrives: what the gate decided of it, and the section it is of when it is admitted.</summary>
    public (HandleGateOutcome Outcome, ObjectIncarnation? Incarnation) Read(in ObjectHandleRecord record) => gate.Read(record);
}
