namespace InterCat.Capture.Windows;

/// <summary>
/// One view record as the section view gate reads it (ADR-090): its opcode - a view's mapping, its unmapping, or a view mapped
/// at the session's start or stop by their rundowns - the view's base in its process, the value its file object field carries,
/// which every view of one section carries in any process (ADR-089), and the process it is in.
/// </summary>
public readonly record struct SectionViewRecord(int Opcode, ulong ViewBase, ulong SectionValue, int Process);

/// <summary>What the section view gate decided of one view record.</summary>
public enum ViewGateOutcome
{
    /// <summary>A view's record, with the view and the section it is of.</summary>
    Admitted = 1,

    /// <summary>A view that would begin beyond the views the gate follows, holding as many as it may.</summary>
    Untracked = 2,
}

/// <summary>
/// Where the gate first met a view: at its mapping or in the start's rundown, or - for a view mapped before the capture and
/// absent from the start's rundown - at its unmapping or in the stop's rundown.
/// </summary>
public enum ViewFirstSeen
{
    /// <summary>A view's mapping the capture saw.</summary>
    Mapping = 1,

    /// <summary>The rundown at the session's start, of a view mapped before the capture began.</summary>
    StartRundown = 2,

    /// <summary>A view's unmapping, of a view the gate never saw begin.</summary>
    Unmapping = 3,

    /// <summary>The rundown at the session's stop, of a view the gate never saw begin.</summary>
    StopRundown = 4,
}

/// <summary>
/// A view the gate numbered: its number in the capture, never its base; its section's number in the capture, never the value its
/// views carry, or zero for a view whose record names no section; and where the gate first met it.
/// </summary>
public readonly record struct ViewIncarnation(long View, long Section, ViewFirstSeen FirstSeen);

/// <summary>
/// Admits the kernel's file view records - each view's mapping and unmapping and the rundowns of the views mapped at a session's
/// start and stop - and numbers the view each is of and the section it maps (ADR-090). A view names its section by no handle
/// and no name, but every view of one section, in any process, carries one value in its file object field, a kernel address
/// the kernel reuses once the section is gone (ADR-089). So a view is followed by its process and base from its mapping, or
/// the start's rundown, to its unmapping: a mapping at a base its process's followed view held ends that view, unseen. A section
/// is followed by the value from the first followed view that carries it to the unmapping of the last: while a followed view
/// holds the value, the section cannot be gone and the value is its alone, so every view carrying it is the section's; once
/// none does, the next view carrying it begins another section. An unmapping, or the stop's rundown, of a view the gate never
/// saw begin is numbered alone, and joins the section its value names only where a followed view holds that value - else its
/// section is numbered alone too, never joined to another by a value nothing held between them (R22). A view whose value is
/// zero names no section and joins none. Records are read as they arrive, and the gate holds none back. It reads them on the
/// capture's callback, so it allocates nothing per record (P12): a followed view is a value in one table and a followed
/// section a value in another, both grown only as far as its bound.
/// </summary>
public sealed class SectionViewGate
{
    /// <summary>A view's mapping, its unmapping, and the rundowns of the views mapped at the session's start and stop.</summary>
    public const int Map = 37;
    public const int Unmap = 38;
    public const int StartRundown = 39;
    public const int StopRundown = 40;

    /// <summary>
    /// How many views the gate follows at most, and so how many sections, each held by one view at least: the start's rundown
    /// alone began 61,016 to 61,146 views machine-wide in FX-SECT-001's runs (ADR-089), so the bound is four times the handle
    /// gate's. A view that would begin beyond it is not followed. Growing to it, with every view of a section of its own, the
    /// gate's tables allocate at most 64 MiB, the arrays they outgrow included.
    /// </summary>
    public const int DefaultCapacity = 262_144;

    // Each followed view by its process and base: its incarnation and the value it carries.
    private readonly Dictionary<(int Process, ulong Base), (ViewIncarnation Incarnation, ulong Value)> views = [];

    // Each followed section by the value its views carry: its number, and how many followed views carry it.
    private readonly Dictionary<ulong, (long Section, int Views)> sections = [];
    private readonly int capacity;
    private long viewsNumbered;
    private long sectionsNumbered;

    /// <param name="capacity">How many views the gate follows at most, and so how many sections.</param>
    public SectionViewGate(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.capacity = capacity;
    }

    /// <summary>How many views the gate follows now: never more than its bound.</summary>
    public int Following => views.Count;

    /// <summary>How many sections the gate follows now, each held by a followed view.</summary>
    public int Sections => sections.Count;

    /// <summary>Reads one record as it arrives: what the gate decided of it, and the view and section it is of when it is admitted.</summary>
    public (ViewGateOutcome Outcome, ViewIncarnation? Incarnation) Read(in SectionViewRecord record)
    {
        (int, ulong) key = (record.Process, record.ViewBase);
        switch (record.Opcode)
        {
            case Map or StartRundown:
            {
                // A view mapped at a base its process's followed view held: that view ended, its unmapping unseen.
                if (views.Remove(key, out (ViewIncarnation Incarnation, ulong Value) ended))
                {
                    Release(ended.Value);
                }

                if (views.Count >= capacity)
                {
                    return (ViewGateOutcome.Untracked, null);
                }

                var incarnation = new ViewIncarnation(++viewsNumbered, Hold(record.SectionValue),
                    record.Opcode == Map ? ViewFirstSeen.Mapping : ViewFirstSeen.StartRundown);
                views[key] = (incarnation, record.SectionValue);
                return (ViewGateOutcome.Admitted, incarnation);
            }

            case Unmap:
            {
                if (views.Remove(key, out (ViewIncarnation Incarnation, ulong Value) view))
                {
                    Release(view.Value);
                    return (ViewGateOutcome.Admitted, view.Incarnation);
                }

                // A view mapped before the capture and absent from the start's rundown: numbered alone.
                return (ViewGateOutcome.Admitted, new(++viewsNumbered, SectionOf(record.SectionValue), ViewFirstSeen.Unmapping));
            }

            case StopRundown:
                // A view still mapped at the stop: the followed view's, or one the gate never saw begin, numbered alone.
                return (ViewGateOutcome.Admitted, views.TryGetValue(key, out (ViewIncarnation Incarnation, ulong Value) mapped)
                    ? mapped.Incarnation
                    : new(++viewsNumbered, SectionOf(record.SectionValue), ViewFirstSeen.StopRundown));

            default:
                throw new ArgumentOutOfRangeException(nameof(record), record.Opcode, "The gate reads a view's mapping, unmapping and rundowns.");
        }
    }

    // The section a followed view joins: the followed section its value names, or one it begins; zero for no value.
    private long Hold(ulong value)
    {
        if (value == 0)
        {
            return 0;
        }

        (long section, int count) = sections.TryGetValue(value, out (long Section, int Views) found) ? found : (++sectionsNumbered, 0);
        sections[value] = (section, count + 1);
        return section;
    }

    // A followed view's value released: the section ends with its last followed view, and its value may be another's next.
    private void Release(ulong value)
    {
        if (value != 0 && sections.TryGetValue(value, out (long Section, int Views) found))
        {
            if (found.Views == 1)
            {
                sections.Remove(value);
            }
            else
            {
                sections[value] = (found.Section, found.Views - 1);
            }
        }
    }

    // The section of a view the gate never saw begin: the followed section its value names, or a number of its own; zero for
    // no value. It holds nothing, as the view is not followed.
    private long SectionOf(ulong value) =>
        value == 0 ? 0 : sections.TryGetValue(value, out (long Section, int Views) found) ? found.Section : ++sectionsNumbered;
}
