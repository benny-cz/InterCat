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
        return [.. table.SourceIds.Select(sourceId => sourceId == WindowsSourceCatalog.WinsockAfdSourceId ? new AfdRecordGate() : (IRecordGate?)null)];
    }

    /// <summary>
    /// Why a source's records could not be decided by the fields its compiled plan admits, or null when they can. A gated
    /// source's gate decides each record by fields of its descriptor (ADR-074): one the machine's schema does not admit
    /// would be read as zero, a value never observed (R3), and a gate reading its family as zero would follow no socket and
    /// say it saw none. A source whose records a derivation rule pairs or links by fields of its own - each field intent
    /// that says what it <see cref="AdmittedFieldIntent.Decides"/> - would have the rule state what was never observed: a
    /// COM call whose stop carries no trace number pairs with nothing, and one whose start names no other end reads as one
    /// whose ends disagree (ADR-079's fifth addendum). Either source is refused rather than admitted.
    /// </summary>
    public static string? Undecidable(SourceAdmissionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.SourceId == WindowsSourceCatalog.WinsockAfdSourceId ? AfdRecordGate.Undecidable(plan) : WithoutDecidingField(plan);
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
            AdmittedEventPlan[] descriptors = [.. plan.Events.Where(descriptor => descriptor.EventId == intent.EventId)];
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

                string at = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"event {intent.EventId} v{without?.Version ?? intent.Version}");
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
