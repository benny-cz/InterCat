using InterCat.Domain;

namespace InterCat.Capture.Windows;

/// <summary>
/// A source's gate (ADR-074): reads each record of its source's admitted descriptors as it arrives, once its slots are
/// read, and admits it - with its gated slots rewritten to the gate's values - or omits it with the reason. It is called
/// from the capture callback, one record at a time, so its work is bounded.
/// </summary>
internal interface IRecordGate
{
    /// <summary>Null to admit <paramref name="admitted"/>, its gated slots rewritten; otherwise why it is omitted.</summary>
    GateOmission? Read(ref AdmittedEvent admitted, AdmittedEventPlan plan);
}

/// <summary>The gates a capture's sources admit through, by the source's index in its admission table.</summary>
internal static class RecordGates
{
    /// <summary>A gate for each source that has one, none for the rest.</summary>
    public static IRecordGate?[] For(EventAdmissionTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return [.. table.SourceIds.Select(sourceId => sourceId switch
        {
            WindowsSourceCatalog.WinsockAfdSourceId => new AfdRecordGate(),
            WindowsSourceCatalog.KernelObjectHandlesSourceId => new ObjectHandleRecordGate(SynchronizationHandleGate.MeasuredTypes(ThisBuild) ?? []),
            WindowsSourceCatalog.Win32kSourceId => new WindowMessageRecordGate(),
            WindowsSourceCatalog.KernelFileViewsSourceId => new SectionViewRecordGate(),
            _ => (IRecordGate?)null,
        })];
    }

    // This machine's build, by which the handle group's type numbers were measured (ADR-082's fourth addendum).
    private static string ThisBuild => CapabilityInventoryProbe.DescribeEnvironment().BuildId;

    /// <summary>
    /// Why a source's records could not be decided by the fields its compiled plan admits, or null when they can. A gated
    /// source's gate decides each record by fields of its descriptor (ADR-074): one the machine's schema does not admit
    /// would be read as zero, a value never observed (R3), and a gate reading its family as zero would follow no socket and
    /// say it saw none. A source whose records a derivation rule pairs or links by fields of its own - each field intent
    /// that says what it <see cref="AdmittedFieldIntent.Decides"/> - would have the rule state what was never observed: a
    /// COM call whose stop carries no trace number pairs with nothing, and one whose start names no other end reads as one
    /// whose ends disagree (ADR-079's fifth addendum). Either source is refused rather than admitted.
    /// </summary>
    public static string? Undecidable(SourceAdmissionPlan plan) => Undecidable(plan, ThisBuild);

    /// <summary>As <see cref="Undecidable(SourceAdmissionPlan)"/>, on a named build: the handle gate decides by its numbers.</summary>
    internal static string? Undecidable(SourceAdmissionPlan plan, string buildId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.SourceId switch
        {
            WindowsSourceCatalog.WinsockAfdSourceId => AfdRecordGate.Undecidable(plan),
            WindowsSourceCatalog.KernelObjectHandlesSourceId => ObjectHandleRecordGate.Undecidable(buildId) ?? WithoutDecidingField(plan),
            _ => WithoutDecidingField(plan),
        };
    }

    /// <summary>
    /// The first field a rule decides its source's records by that a compiled descriptor does not admit - or that no
    /// descriptor admits, as the schema registers no version of its event - said with what the rule decides by it; null
    /// when every one is admitted, or the source names none. Compiled once, before a capture starts, never on the callback path.
    /// </summary>
    private static string? WithoutDecidingField(SourceAdmissionPlan plan)
    {
        if (WindowsSourceCatalog.Find(plan.SourceId) is not { } definition)
        {
            return null;
        }

        foreach (AdmittedEventIntent intent in definition.AdmittedEvents)
        {
            // A classic class's events share event id 0 and are told apart by their opcode, as a manifest's by its id.
            AdmittedEventPlan[] descriptors = [.. plan.Events.Where(descriptor => descriptor.EventId == intent.EventId && descriptor.Opcode == intent.Opcode)];
            foreach (AdmittedFieldIntent field in intent.Fields.Where(field => field.Decides is not null))
            {
                AdmittedEventPlan? without = descriptors.Length == 0
                    ? null
                    : descriptors.FirstOrDefault(descriptor => !descriptor.Slots.Any(slot =>
                        string.Equals(slot.FieldName, field.FieldName, StringComparison.Ordinal)));
                if (descriptors.Length > 0 && without is null)
                {
                    continue;
                }

                string at = intent.Opcode is { } opcode
                    ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"opcode {opcode} v{without?.Version ?? intent.Version}")
                    : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"event {intent.EventId} v{without?.Version ?? intent.Version}");
                return $"This machine's schema admits no {field.FieldName} for {at} ({intent.Name}), by which {field.Decides}. "
                    + "Without it the rule would decide by a value never observed, so the source is refused rather than admitted (R3).";
            }
        }

        return null;
    }
}

/// <summary>
/// Winsock AFD's gate: reads its records through <see cref="UnixSocketGate"/>, and writes into an admitted record, in place
/// of the source's kernel addresses, the gate's values - the socket's number in the capture for its endpoint, and the
/// process its create named for its process - so that no record keeps an address (ADR-073). An accept's new socket follows
/// a variable-length address in its record, so none is read: a socket accepted is followed from its own create.
/// </summary>
internal sealed class AfdRecordGate(int capacity = UnixSocketGate.DefaultCapacity) : IRecordGate
{
    private const int CreateEvent = 1000;
    private const int SendEvent = 1003;

    // The fields the gate decides every call's record by, and what it could not tell without each.
    private static readonly (string Field, string Decides)[] EveryCall =
    [
        ("EnterExit", "a call's entry from its exit"),
        ("Endpoint", "which socket a record is of"),
        ("Process", "where a record's owner is written"),
    ];

    private static readonly (string Field, string Decides)[] Create =
    [
        .. EveryCall,
        ("AddressFamily", "an AF_UNIX socket from another family's"),
        ("ProcessId", "the process a socket was made for"),
    ];

    private static readonly (string Field, string Decides)[] Send = [.. EveryCall, ("Location", "a send's own exit from its other")];

    private readonly UnixSocketGate gate = new(capacity);
    private readonly Dictionary<AdmittedEventPlan, Slots> slots = new(ReferenceEqualityComparer.Instance);

    public GateOmission? Read(ref AdmittedEvent admitted, AdmittedEventPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!slots.TryGetValue(plan, out Slots at))
        {
            slots[plan] = at = Slots.Of(plan);
        }

        var record = new AfdSocketRecord(
            admitted.EventId,
            (uint)Value(in admitted, at.Phase),
            (uint)Value(in admitted, at.Location),
            (ulong)Value(in admitted, at.Endpoint),
            0,
            (uint)Value(in admitted, at.AddressFamily),
            (int)Value(in admitted, at.ProcessId),
            admitted.HeaderProcessId);
        (AfdGateOutcome outcome, UnixSocketIncarnation? socket) = gate.Read(in record);
        switch (outcome)
        {
            case AfdGateOutcome.Admitted:
                if (at.Endpoint >= 0) admitted.SetSlot(at.Endpoint, socket!.Value.Number);
                if (at.Process >= 0) admitted.SetSlot(at.Process, socket!.Value.Owner);
                return null;
            case AfdGateOutcome.NotCounted:
                return GateOmission.NotCounted;
            case AfdGateOutcome.Untracked:
                return GateOmission.BeyondBound;
            default:
                return GateOmission.NoFollowedResource;
        }
    }

    private static long Value(in AdmittedEvent admitted, int slot) => slot >= 0 && admitted.TryGetSlot(slot, out long value) ? value : 0;

    /// <summary>
    /// The first field a descriptor of <paramref name="plan"/> does not admit that the gate decides its records by, said
    /// with what the gate could not tell without it; null when each descriptor admits every one. Compiled once, before a
    /// capture starts, never on the callback path.
    /// </summary>
    internal static string? Undecidable(SourceAdmissionPlan plan)
    {
        foreach (AdmittedEventPlan descriptor in plan.Events)
        {
            (string Field, string Decides)[] deciding = descriptor.EventId switch
            {
                CreateEvent => Create,
                SendEvent => Send,
                _ => EveryCall,
            };
            foreach ((string field, string decides) in deciding)
            {
                if (Slots.Find(descriptor, field) < 0)
                {
                    string at = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"event {descriptor.EventId} v{descriptor.Version}");
                    return $"This machine's schema admits no {field} for {at} ({descriptor.Name}), by which the source's gate "
                        + $"tells {decides}. Read as zero, it would be decided by a value never observed, so the source is "
                        + "refused rather than admitted (R3).";
                }
            }
        }

        return null;
    }

    /// <summary>Where a descriptor's admitted fields the gate reads and writes are, by name; -1 for one it does not admit.</summary>
    private readonly record struct Slots(int Phase, int Location, int Endpoint, int AddressFamily, int ProcessId, int Process)
    {
        public static Slots Of(AdmittedEventPlan plan) => new(
            Find(plan, "EnterExit"), Find(plan, "Location"), Find(plan, "Endpoint"), Find(plan, "AddressFamily"),
            Find(plan, "ProcessId"), Find(plan, "Process"));

        public static int Find(AdmittedEventPlan plan, string field)
        {
            for (int index = 0; index < plan.Slots.Count; index++)
            {
                if (string.Equals(plan.Slots[index].FieldName, field, StringComparison.Ordinal)) return index;
            }

            return -1;
        }
    }
}

/// <summary>
/// Win32k's message pump gate: reads a message's ends through <see cref="WindowMessageGate"/> and writes into an admitted
/// record, in place of the queue message's kernel pointer, the message's number in the capture, so no record keeps a pointer
/// (ADR-087). The gate numbers by the order records were raised in, which a session was measured delivering them in
/// (ADR-086's third addendum): a record delivered out of it, which the gate numbers alone, is omitted as such, counted by its
/// reason, rather than admitted as a message of its own (ADR-087's addendum). A schema without the pointer is refused before
/// a capture starts, as the field's intent says the gate decides by it (<see cref="RecordGates.Undecidable(SourceAdmissionPlan)"/>).
/// </summary>
internal sealed class WindowMessageRecordGate(int capacity = WindowMessageGate.DefaultCapacity) : IRecordGate
{
    private readonly WindowMessageGate gate = new(capacity);
    private readonly Dictionary<AdmittedEventPlan, int> pointers = new(ReferenceEqualityComparer.Instance);

    public GateOmission? Read(ref AdmittedEvent admitted, AdmittedEventPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!pointers.TryGetValue(plan, out int pointer))
        {
            pointers[plan] = pointer = PointerSlot(plan);
        }

        long queueMessage = pointer >= 0 && admitted.TryGetSlot(pointer, out long value) ? value : 0;
        WindowMessageIncarnation message = gate.Read(new WindowMessageRecord(admitted.EventId, unchecked((ulong)queueMessage), admitted.TimestampQpc));
        if (message.FirstSeen == MessageFirstSeen.OutOfOrder)
        {
            return GateOmission.OutOfOrder;
        }

        if (pointer >= 0)
        {
            admitted.SetSlot(pointer, message.Number);
        }

        return null;
    }

    /// <summary>Where a descriptor admits the queue message's pointer; -1 for one that does not.</summary>
    private static int PointerSlot(AdmittedEventPlan plan)
    {
        for (int index = 0; index < plan.Slots.Count; index++)
        {
            if (string.Equals(plan.Slots[index].FieldName, "pqmsg", StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}

/// <summary>
/// The file view flag's gate: reads a view's mapping and unmapping and the rundowns at the session's start and stop through
/// <see cref="SectionViewGate"/>, and writes into an admitted record, in place of the view's base, the view's number in the
/// capture, and in place of the value its file object field carries, its section's number, so no record keeps an address
/// (ADR-090). A view is the process's its record names: a mapping's and an unmapping's always the process that raised it
/// (ADR-089), and a rundown's where no rundown record's header names one (ADR-089's fourth addendum). A view that would begin
/// beyond the gate's bound is omitted as such, counted.
/// </summary>
internal sealed class SectionViewRecordGate(int capacity = SectionViewGate.DefaultCapacity) : IRecordGate
{
    private readonly SectionViewGate gate = new(capacity);
    private readonly Dictionary<AdmittedEventPlan, Slots> slots = new(ReferenceEqualityComparer.Instance);

    public GateOmission? Read(ref AdmittedEvent admitted, AdmittedEventPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!slots.TryGetValue(plan, out Slots at))
        {
            slots[plan] = at = Slots.Of(plan);
        }

        var record = new SectionViewRecord(
            admitted.Opcode,
            unchecked((ulong)Value(in admitted, at.ViewBase)),
            unchecked((ulong)Value(in admitted, at.Section)),
            (int)Value(in admitted, at.ProcessId));
        (ViewGateOutcome outcome, ViewIncarnation? view) = gate.Read(in record);
        if (outcome != ViewGateOutcome.Admitted)
        {
            return GateOmission.BeyondBound;
        }

        if (at.ViewBase >= 0) admitted.SetSlot(at.ViewBase, view!.Value.View);
        if (at.Section >= 0) admitted.SetSlot(at.Section, view!.Value.Section);
        return null;
    }

    private static long Value(in AdmittedEvent admitted, int slot) => slot >= 0 && admitted.TryGetSlot(slot, out long value) ? value : 0;

    /// <summary>Where a descriptor's admitted fields the gate reads and writes are, by name; -1 for one it does not admit.</summary>
    private readonly record struct Slots(int ViewBase, int Section, int ProcessId)
    {
        public static Slots Of(AdmittedEventPlan plan) => new(Find(plan, "ViewBase"), Find(plan, "FileObject"), Find(plan, "ProcessId"));

        private static int Find(AdmittedEventPlan plan, string field)
        {
            for (int index = 0; index < plan.Slots.Count; index++)
            {
                if (string.Equals(plan.Slots[index].FieldName, field, StringComparison.Ordinal)) return index;
            }

            return -1;
        }
    }
}

/// <summary>
/// The object manager handle group's gate: reads its handle records through <see cref="SynchronizationHandleGate"/> and writes
/// into an admitted record, in place of the object's kernel address, the object's number in the capture, so no record keeps
/// an address (ADR-083). A handle is the record's own process's - the header's - but at the stop's rundown, which names it in
/// its record, and a duplication's is made in its target process, which its record names. A kernel handle's records, raised
/// wherever the kernel ran, are omitted as of no object it follows. The type rundown at the stop admits the synchronization
/// types' own entries, by which a capture holds the build's measured numbers to its session.
/// </summary>
internal sealed class ObjectHandleRecordGate(IReadOnlyList<int> types, int capacity = SynchronizationHandleGate.DefaultCapacity) : IRecordGate
{
    private readonly SynchronizationHandleGate gate = new(types, capacity);
    private readonly HashSet<int> synchronization = [.. types];
    private readonly Dictionary<AdmittedEventPlan, Slots> slots = new(ReferenceEqualityComparer.Instance);

    public GateOmission? Read(ref AdmittedEvent admitted, AdmittedEventPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!slots.TryGetValue(plan, out Slots at))
        {
            slots[plan] = at = Slots.Of(plan);
        }

        int type = (int)Value(in admitted, at.ObjectType);
        if (admitted.Opcode == SynchronizationHandleGate.TypeRundown)
        {
            return synchronization.Contains(type) ? null : GateOmission.NoFollowedResource;
        }

        bool duplicate = admitted.Opcode == SynchronizationHandleGate.Duplicate;
        var record = new ObjectHandleRecord(
            admitted.Opcode,
            (ulong)Value(in admitted, at.Object),
            (uint)Value(in admitted, duplicate ? at.SourceHandle : at.Handle),
            type,
            admitted.Opcode == SynchronizationHandleGate.Rundown ? (int)Value(in admitted, at.ProcessId) : admitted.HeaderProcessId,
            duplicate ? (int)Value(in admitted, at.TargetProcessId) : 0,
            duplicate ? (uint)Value(in admitted, at.TargetHandle) : 0);
        (HandleGateOutcome outcome, ObjectIncarnation? incarnation) = gate.Read(in record);
        switch (outcome)
        {
            case HandleGateOutcome.Admitted:
                if (at.Object >= 0) admitted.SetSlot(at.Object, incarnation!.Value.Number);
                return null;
            case HandleGateOutcome.Untracked:
                return GateOmission.BeyondBound;
            default:
                return GateOmission.NoFollowedResource;
        }
    }

    /// <summary>
    /// Why the gate could not decide its records on <paramref name="buildId"/>: no type numbers measured for it, by which it
    /// tells a synchronization object's handle; null when the build's are measured. A gate deciding by numbers never
    /// measured would admit another type's handles as an event's, so the source is refused instead.
    /// </summary>
    internal static string? Undecidable(string buildId) =>
        SynchronizationHandleGate.MeasuredTypes(buildId) is null
            ? $"No synchronization type numbers are measured for build {buildId}, by which the source's gate tells an event's, a "
                + "mutant's or a semaphore's handle from another type's. Decided by numbers never measured, it would admit another "
                + "type's handles as theirs, so the source is refused rather than admitted (R3)."
            : null;

    private static long Value(in AdmittedEvent admitted, int slot) => slot >= 0 && admitted.TryGetSlot(slot, out long value) ? value : 0;

    /// <summary>Where a descriptor's admitted fields the gate reads and writes are, by name; -1 for one it does not admit.</summary>
    private readonly record struct Slots(int Object, int Handle, int ObjectType, int SourceHandle, int TargetHandle, int TargetProcessId, int ProcessId)
    {
        public static Slots Of(AdmittedEventPlan plan) => new(
            Find(plan, "Object"), Find(plan, "Handle"), Find(plan, "ObjectType"), Find(plan, "SourceHandle"), Find(plan, "TargetHandle"),
            Find(plan, "TargetProcessId"), Find(plan, "ProcessId"));

        private static int Find(AdmittedEventPlan plan, string field)
        {
            for (int index = 0; index < plan.Slots.Count; index++)
            {
                if (string.Equals(plan.Slots[index].FieldName, field, StringComparison.Ordinal)) return index;
            }

            return -1;
        }
    }
}