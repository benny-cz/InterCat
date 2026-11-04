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
}

/// <summary>
/// Winsock AFD's gate: reads its records through <see cref="UnixSocketGate"/>, and writes into an admitted record, in place
/// of the source's kernel addresses, the gate's values - the socket's number in the capture for its endpoint, and the
/// process its create named for its process - so that no record keeps an address (ADR-073). An accept's new socket follows
/// a variable-length address in its record, so none is read: a socket accepted is followed from its own create.
/// </summary>
internal sealed class AfdRecordGate(int capacity = UnixSocketGate.DefaultCapacity) : IRecordGate
{
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

    /// <summary>Where a descriptor's admitted fields the gate reads and writes are, by name; -1 for one it does not admit.</summary>
    private readonly record struct Slots(int Phase, int Location, int Endpoint, int AddressFamily, int ProcessId, int Process)
    {
        public static Slots Of(AdmittedEventPlan plan) => new(
            Find(plan, "EnterExit"), Find(plan, "Location"), Find(plan, "Endpoint"), Find(plan, "AddressFamily"),
            Find(plan, "ProcessId"), Find(plan, "Process"));

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
