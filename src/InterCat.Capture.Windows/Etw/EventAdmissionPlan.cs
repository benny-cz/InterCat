using InterCat.Domain;

namespace InterCat.Capture.Windows;

/// <summary>
/// One admitted field, resolved to a fixed offset and width so the capture callback can copy it with a
/// bounded read and a shape check instead of a general-purpose decoder (R9, section 18.2).
/// </summary>
public sealed record AdmittedSlotPlan(
    string FieldName,
    FieldRole Role,
    int Offset,
    int Width,
    MeasurementUnit? Unit,
    ByteDomain? ByteDomain,
    SlotTransform Transform,
    AdmittedSlotKind Kind = AdmittedSlotKind.Numeric)
{
    /// <summary>
    /// The meaning the catalog gives this field when it is one of §7.3's source correlation or object fields. It
    /// decides which derived table the value is carried into, and it is not part of the admission fingerprint: it
    /// changes what a derivation does with a copied value, never what is copied or persisted.
    /// </summary>
    public SourceField? SourceField { get; init; }
}

/// <summary>What a slot holds. A name is copied into a bounded inline buffer, never into new memory (R9).</summary>
public enum AdmittedSlotKind
{
    Numeric = 1,

    /// <summary>A UTF-16 resource name, truncated to the bounded name length with truncation recorded.</summary>
    ResourceName = 2,

    /// <summary>A 16-byte identifier such as an interface UUID, copied whole into the record.</summary>
    Identifier = 3,

    /// <summary>
    /// An 8-bit resource name, such as the image name a process stop carries. Each byte is widened to one character,
    /// which is exact for ASCII and is recorded as the admission's reading of any other byte.
    /// </summary>
    AnsiResourceName = 4,

    /// <summary>
    /// A UTF-16 resource name that immediately follows one security identifier. The slot's offset is the identifier's;
    /// the name's offset is computed per record from the identifier's own sub-authority count, a one-byte shape read
    /// bounded by the record's length (R9).
    /// </summary>
    ResourceNameAfterSid = 5,

    /// <summary>
    /// A 16-byte IPv6 endpoint address, copied whole into one of the record's two address slots, in the order the
    /// descriptor's address slots are listed (revision 174).
    /// </summary>
    Address128 = 6,
}

/// <summary>A compiled admission plan for exactly one event descriptor version.</summary>
public sealed record AdmittedEventPlan
{
    public required int SourceIndex { get; init; }
    public required Guid ProviderGuid { get; init; }
    public required int EventId { get; init; }

    /// <summary>
    /// A classic descriptor's opcode, which with its class and version is its identity (ADR-035's addendum); null for a
    /// manifest descriptor, whose id and version are. Left out of a plan file when null, so manifest plans are unchanged.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? Opcode { get; init; }

    public required int Version { get; init; }
    public required string Name { get; init; }
    public required Mechanism Mechanism { get; init; }

    /// <summary>
    /// Which layer this descriptor's evidence belongs to, as its source contract declares it (§5.1, I11).
    /// It is not derived from the mechanism here, because one mechanism can carry evidence at more than one
    /// layer and guessing would let a transport metric absorb an application annotation.
    /// </summary>
    public required ObservationLayer Layer { get; init; }

    public required ObservationKind Kind { get; init; }
    public required Direction Direction { get; init; }

    /// <summary>Smallest body length that can contain every admitted slot; a shorter body is undecodable.</summary>
    public required int MinimumBodyLength { get; init; }

    /// <summary>
    /// The source pointer width the offsets were computed for. A record delivered with another width is
    /// undecodable rather than read at a guessed offset (section 18.3).
    /// </summary>
    public int PointerSize { get; init; } = 8;

    /// <summary>
    /// Whether every admitted field lies before any pointer-sized field and is not one, so a record raised by a process
    /// of the other pointer width decodes at the same offsets. A user-mode provider raises a 32-bit process's records at
    /// width 4; such a record is admitted only by a plan this holds for, and otherwise counted as undecodable.
    /// </summary>
    public bool PointerWidthIndependent { get; init; }

    /// <summary>
    /// SHA-256 identity of the saved provider schema, descriptor layout, admitted slots and body policy.
    /// A different projection therefore cannot reuse the same journal schema reference accidentally.
    /// </summary>
    public required string SchemaFingerprint { get; init; }

    /// <summary>The body policy compiled for this exact descriptor before capture starts.</summary>
    public required CompiledBodyAdmissionPolicy BodyPolicy { get; init; }

    public required IReadOnlyList<AdmittedSlotPlan> Slots { get; init; }

    /// <summary>What the capability report states about every intended field of this descriptor.</summary>
    public required IReadOnlyList<FieldCapability> FieldReport { get; init; }
}

/// <summary>The compiled plan for one source, plus the reasons any intended field was not admitted.</summary>
public sealed record SourceAdmissionPlan
{
    public required string SourceId { get; init; }
    public required Guid ProviderGuid { get; init; }
    public required int SourceIndex { get; init; }
    public required IReadOnlyList<AdmittedEventPlan> Events { get; init; }
    public required IReadOnlyList<string> Diagnostics { get; init; }
}

/// <summary>The pre-read decision for one descriptor; every rejected callback maps to one health ledger.</summary>
public enum DescriptorAdmissionOutcome
{
    Admitted = 1,
    UnrequestedProvider = 2,
    DescriptorNotAdmitted = 3,
    UnknownDescriptorVersion = 4,
}

public readonly record struct DescriptorAdmissionResolution(
    DescriptorAdmissionOutcome Outcome,
    AdmittedEventPlan? Plan);

/// <summary>
/// The lookup the capture callback uses. Keys are resolved before recording starts, so the callback
/// performs no allocation, no string work and no schema discovery (R9, R11).
/// </summary>
public sealed class EventAdmissionTable
{
    private readonly Dictionary<DescriptorKey, AdmittedEventPlan> plans;
    private readonly HashSet<Guid> knownProviders;

    public EventAdmissionTable(IReadOnlyList<SourceAdmissionPlan> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        plans = [];
        knownProviders = [];
        var sourceIds = new List<string>(sources.Count);
        foreach (SourceAdmissionPlan source in sources)
        {
            sourceIds.Add(source.SourceId);
            knownProviders.Add(source.ProviderGuid);
            foreach (AdmittedEventPlan plan in source.Events)
            {
                var key = new DescriptorKey(plan.ProviderGuid, plan.EventId, plan.Version, plan.Opcode ?? ManifestOpcode);
                if (!plans.TryAdd(key, plan))
                {
                    throw new InvalidOperationException(
                        $"Descriptor {plan.ProviderGuid:D}/{plan.EventId}/v{plan.Version} is compiled more "
                        + "than once. A capture cannot choose between ambiguous source plans.");
                }
            }
        }

        SourceIds = sourceIds;
    }

    public IReadOnlyList<string> SourceIds { get; }

    public int PlanCount => plans.Count;

    public bool IsKnownProvider(Guid providerGuid) => knownProviders.Contains(providerGuid);

    /// <summary>The opcode a manifest descriptor's key carries: a manifest event's opcode is not part of its identity.</summary>
    public const int ManifestOpcode = -1;

    /// <summary>
    /// The identity a delivered record is admitted and counted under. A manifest record is its provider and id. A classic
    /// record is the class its header carries, with id 0 and its opcode, because a class has one id for every event
    /// (ADR-035). That holds for a class the plan does not admit too: a decoder reports the generic kernel provider for
    /// such a record, or no provider, depending on which of its parsers the consumer registered, so only the header's
    /// class names it the same way every time.
    /// </summary>
    public static (Guid Provider, int EventId, int Opcode) RecordIdentity(bool classic, Guid providerGuid, Guid classGuid, int eventId, int opcode) =>
        classic ? (classGuid, 0, opcode) : (providerGuid, eventId, ManifestOpcode);

    /// <summary>A descriptor's plan; <paramref name="opcode"/> is a classic record's, or <see cref="ManifestOpcode"/>.</summary>
    public AdmittedEventPlan? Find(Guid providerGuid, int eventId, int version, int opcode = ManifestOpcode) =>
        plans.TryGetValue(new(providerGuid, eventId, version, opcode), out AdmittedEventPlan? plan) ? plan : null;

    /// <summary>
    /// Classifies a descriptor without touching its body. Unknown versions are decode failures because
    /// the descriptor was requested but its shape is unknown; entirely unrequested descriptors are
    /// policy omissions. The distinction feeds separate health counters (I13).
    /// </summary>
    public DescriptorAdmissionResolution Resolve(Guid providerGuid, int eventId, int version, int opcode = ManifestOpcode)
    {
        if (!IsKnownProvider(providerGuid))
        {
            return new(DescriptorAdmissionOutcome.UnrequestedProvider, null);
        }

        AdmittedEventPlan? plan = Find(providerGuid, eventId, version, opcode);
        if (plan is not null)
        {
            return new(DescriptorAdmissionOutcome.Admitted, plan);
        }

        return HasDescriptor(providerGuid, eventId, opcode)
            ? new(DescriptorAdmissionOutcome.UnknownDescriptorVersion, null)
            : new(DescriptorAdmissionOutcome.DescriptorNotAdmitted, null);
    }

    /// <summary>
    /// Resolves the plan an admitted record was produced by. Records carry the source index rather than a
    /// provider identity, so the decode stage resolves them without re-reading callback memory.
    /// </summary>
    public AdmittedEventPlan? FindBySourceIndex(int sourceIndex, int eventId, int version, int opcode)
    {
        foreach (AdmittedEventPlan plan in plans.Values)
        {
            if (plan.SourceIndex == sourceIndex && plan.EventId == eventId && plan.Version == version
                && (plan.Opcode is null || plan.Opcode == opcode))
            {
                return plan;
            }
        }

        return null;
    }

    /// <summary>
    /// True when the descriptor is admitted at some version. It separates an event the profile never
    /// wanted from one that arrived at a version the saved schema does not cover (section 18.3).
    /// </summary>
    public bool HasDescriptor(Guid providerGuid, int eventId, int opcode = ManifestOpcode)
    {
        foreach (DescriptorKey key in plans.Keys)
        {
            if (key.EventId == eventId && key.ProviderGuid == providerGuid && key.Opcode == opcode)
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct DescriptorKey(Guid ProviderGuid, int EventId, int Version, int Opcode);
}
