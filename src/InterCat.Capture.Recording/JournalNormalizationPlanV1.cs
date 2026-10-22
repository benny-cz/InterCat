using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Capture.Recording;

/// <summary>
/// The exact descriptor interpretation retained beside a session's admitted journal. A schema fingerprint
/// identifies the shape but cannot reconstruct slot roles, transforms, source-field meanings or body policy;
/// these are saved here so replay never borrows a later machine's mutable capability inventory.
/// </summary>
public sealed record JournalNormalizationPlanV1
{
    public const string ContractName = "normalizer-plan-v1";
    public const int MaximumBytes = 1_048_576;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    private readonly IReadOnlyList<AdmittedEventPlan> descriptors = [];

    // A replay resolves every record it reads, so the descriptors are indexed by what a record names of them once, when
    // they are set, rather than searched per record (R11).
    private readonly Dictionary<(int Source, Guid Provider, int Event, int Version), AdmittedEventPlan[]> byEvent = [];

    public required string Contract { get; init; }

    public required IReadOnlyList<AdmittedEventPlan> Descriptors
    {
        get => descriptors;
        init
        {
            descriptors = value;
            byEvent = ByEvent(value);
        }
    }

    public static JournalNormalizationPlanV1 FromSources(IReadOnlyList<SourceAdmissionPlan> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var plan = new JournalNormalizationPlanV1
        {
            Contract = ContractName,
            Descriptors =
            [
                .. sources.SelectMany(source => source.Events)
                    .OrderBy(descriptor => descriptor.SourceIndex)
                    .ThenBy(descriptor => descriptor.EventId)
                    .ThenBy(descriptor => descriptor.Version),
            ],
        };
        plan.Validate();
        return plan;
    }

    public byte[] Encode()
    {
        Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        return bytes.Length <= MaximumBytes
            ? bytes
            : throw new InvalidDataException(
                $"The compiled normalizer plan is {bytes.Length} bytes, beyond its {MaximumBytes}-byte bound.");
    }

    public static JournalNormalizationPlanV1 Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumBytes)
        {
            throw new InvalidDataException("A normalizer-plan-v1 file is empty or exceeds its 1 MiB bound.");
        }

        JournalNormalizationPlanV1 plan;
        try
        {
            plan = JsonSerializer.Deserialize<JournalNormalizationPlanV1>(bytes, Json)
                ?? throw new InvalidDataException("A normalizer plan has no object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The retained normalizer plan is not valid normalizer-plan-v1 JSON.", exception);
        }

        plan.Validate();
        return plan;
    }

    /// <summary>Refuses a plan whose descriptor fingerprints or policy IDs differ from the admitted journal.</summary>
    public void ValidateAgainst(JournalV1SchemaTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        Validate();
        // A classic class's descriptors at a version share one schema entry (revision 221), so the plan interprets as
        // many schemas as it has distinct provider/event/version keys, not one per descriptor.
        int interpreted = Descriptors.Select(descriptor => (descriptor.ProviderGuid, descriptor.EventId, descriptor.Version))
            .Distinct().Count();
        if (table.Schemas.Count != interpreted)
        {
            throw new InvalidDataException(
                $"The journal names {table.Schemas.Count} schemas but the retained plan interprets "
                + $"{interpreted}. Replay refuses an incomplete or extra interpretation.");
        }

        foreach (AdmittedEventPlan descriptor in Descriptors)
        {
            JournalSchemaV1 schema = table.Schemas.SingleOrDefault(candidate =>
                candidate.ProviderId == descriptor.ProviderGuid
                && candidate.EventId == descriptor.EventId
                && candidate.Version == descriptor.Version)
                ?? throw new InvalidDataException(
                    $"The journal names no schema for {descriptor.ProviderGuid:D}/{descriptor.EventId}/v{descriptor.Version}.");
            if (!string.Equals(schema.Fingerprint, descriptor.SchemaFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The retained interpretation of {descriptor.ProviderGuid:D}/{descriptor.EventId}/v{descriptor.Version} "
                    + "has a different fingerprint from the admitted journal. Replay refuses to reinterpret its bytes.");
            }

            if (!table.Policies.Any(policy =>
                string.Equals(policy.PolicyId, descriptor.BodyPolicy.PolicyId, StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    $"The journal names no policy '{descriptor.BodyPolicy.PolicyId}' for its retained descriptor.");
            }
        }
    }

    public AdmittedEventPlan Resolve(RecordEnvelopeV1 envelope, JournalV1SchemaTable table)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(table);
        JournalSchemaV1 schema = (envelope.SchemaReference is { } reference ? table.Find(reference) : null)
            ?? throw new InvalidDataException($"Record {envelope.RecordOrdinal} names an unknown schema reference.");
        JournalPolicyV1 policy = table.FindPolicy(envelope.AdmissionPolicyReference)
            ?? throw new InvalidDataException($"Record {envelope.RecordOrdinal} names an unknown policy reference.");
        // A stream past any source's index plus one names no descriptor, as one before the first does.
        int source = envelope.StreamId <= int.MaxValue ? (int)envelope.StreamId - 1 : -1;
        AdmittedEventPlan? plan = null;
        if (byEvent.TryGetValue(
            (source, envelope.Header.ProviderId, envelope.Header.EventId, envelope.Header.Version),
            out AdmittedEventPlan[]? candidates))
        {
            foreach (AdmittedEventPlan candidate in candidates)
            {
                if (candidate.Opcode is null || candidate.Opcode == envelope.Header.Opcode)
                {
                    plan = plan is null
                        ? candidate
                        : throw new InvalidDataException(
                            $"Record {envelope.RecordOrdinal} has more than one retained descriptor interpretation for its "
                            + "stream and event.");
                }
            }
        }

        if (plan is null)
        {
            throw new InvalidDataException(
                $"Record {envelope.RecordOrdinal} has no retained descriptor interpretation for its stream and event.");
        }
        if (schema.ProviderId != plan.ProviderGuid
            || schema.EventId != plan.EventId
            || schema.Version != plan.Version
            || !string.Equals(schema.Fingerprint, plan.SchemaFingerprint, StringComparison.Ordinal)
            || !string.Equals(policy.PolicyId, plan.BodyPolicy.PolicyId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Record {envelope.RecordOrdinal} does not match the schema and policy its retained plan interprets.");
        }

        return plan;
    }

    /// <summary>A plan is its contract and its descriptors; the index it keeps of them is not part of what it is.</summary>
    public bool Equals(JournalNormalizationPlanV1? other) =>
        other is not null
        && string.Equals(Contract, other.Contract, StringComparison.Ordinal)
        && EqualityComparer<IReadOnlyList<AdmittedEventPlan>>.Default.Equals(Descriptors, other.Descriptors);

    public override int GetHashCode() => HashCode.Combine(Contract, Descriptors);

    /// <summary>
    /// Groups descriptors by stream, provider, event and version, in the plan's order. A plan not yet validated may hold
    /// anything, so what no record could name is left out rather than refused here; validation refuses it.
    /// </summary>
    private static Dictionary<(int Source, Guid Provider, int Event, int Version), AdmittedEventPlan[]> ByEvent(
        IReadOnlyList<AdmittedEventPlan>? descriptors)
    {
        var index = new Dictionary<(int Source, Guid Provider, int Event, int Version), AdmittedEventPlan[]>();
        foreach (AdmittedEventPlan? descriptor in descriptors ?? [])
        {
            if (descriptor is null)
            {
                continue;
            }

            (int, Guid, int, int) key = (descriptor.SourceIndex, descriptor.ProviderGuid, descriptor.EventId, descriptor.Version);
            index[key] = index.TryGetValue(key, out AdmittedEventPlan[]? earlier) ? [.. earlier, descriptor] : [descriptor];
        }

        return index;
    }

    private void Validate()
    {
        if (!string.Equals(Contract, ContractName, StringComparison.Ordinal)
            || Descriptors is null or { Count: < 1 or > 1_024 })
        {
            throw new InvalidDataException("A normalizer-plan-v1 file names its contract and 1-1,024 descriptors.");
        }

        // A classic descriptor is its class, id 0, version and opcode (ADR-035's addendum); a manifest one has no opcode.
        var keys = new HashSet<(int Source, int Event, int Version, int Opcode)>();
        var schemas = new HashSet<(Guid Provider, int Event, int Version, int Opcode)>();
        var fingerprints = new Dictionary<(Guid Provider, int Event, int Version), string>();
        foreach (AdmittedEventPlan descriptor in Descriptors)
        {
            if (descriptor is null
                || descriptor.SourceIndex < 0
                || descriptor.ProviderGuid == Guid.Empty
                || descriptor.EventId is < 0 or > ushort.MaxValue
                || descriptor.Version is < 0 or > byte.MaxValue
                || descriptor.Opcode is < 0 or > byte.MaxValue
                || string.IsNullOrWhiteSpace(descriptor.SchemaFingerprint)
                || descriptor.Slots is null or { Count: > AdmissionPlanCompiler.MaximumSlots }
                || descriptor.Slots.Count(slot => slot?.Kind == AdmittedSlotKind.Address128) > AdmittedEvent.MaximumAddresses
                || descriptor.FieldReport is null
                || descriptor.BodyPolicy is null
                || !keys.Add((descriptor.SourceIndex, descriptor.EventId, descriptor.Version, descriptor.Opcode ?? -1))
                || !schemas.Add((descriptor.ProviderGuid, descriptor.EventId, descriptor.Version, descriptor.Opcode ?? -1))
                || (fingerprints.TryGetValue((descriptor.ProviderGuid, descriptor.EventId, descriptor.Version), out string? shared)
                    && !string.Equals(shared, descriptor.SchemaFingerprint, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("A retained normalizer descriptor is incomplete, out of bounds or duplicated.");
            }

            if (!Derivable(descriptor))
            {
                throw new InvalidDataException(
                    $"The retained descriptor {descriptor.ProviderGuid:D}/{descriptor.EventId}/v{descriptor.Version} carries a "
                    + "code §23 does not define, a byte field without its domain and unit, or both IPv4 and IPv6 "
                    + "addresses, so no row could be derived from its records.");
            }

            // One journal schema entry serves every opcode of a class at a version, so they must share its fingerprint.
            fingerprints[(descriptor.ProviderGuid, descriptor.EventId, descriptor.Version)] = descriptor.SchemaFingerprint;
            CaptureBodyAdmissionPolicies.EnsureSupported(descriptor.BodyPolicy);
        }
    }

    /// <summary>
    /// Whether every row a descriptor's records derive can be written: its codes are ones §23 defines, a byte field
    /// carries the domain and unit that label its value or its absence, a field it reports applies, and its endpoint
    /// addresses are of one family. A descriptor the catalog compiles is all of these; one that is not would have the
    /// segment refuse the first record it interprets, so the plan is refused before any is (R2, R3).
    /// </summary>
    private static bool Derivable(AdmittedEventPlan descriptor)
    {
        if (!Enum.IsDefined(descriptor.Mechanism)
            || !Enum.IsDefined(descriptor.Layer)
            || !Enum.IsDefined(descriptor.Kind)
            || !Enum.IsDefined(descriptor.Direction))
        {
            return false;
        }

        bool narrow = false;
        bool wide = false;
        foreach (AdmittedSlotPlan? slot in descriptor.Slots)
        {
            if (slot is null
                || !Enum.IsDefined(slot.Role)
                || !Enum.IsDefined(slot.Kind)
                || (slot.SourceField is { } field && !Enum.IsDefined(field))
                || (slot.Role == FieldRole.ByteCount
                    && (slot.ByteDomain is not { } domain || !Enum.IsDefined(domain) || slot.Unit is not { } unit || !Enum.IsDefined(unit))))
            {
                return false;
            }

            narrow |= slot is { Role: FieldRole.SourceEndpoint or FieldRole.DestinationEndpoint, Kind: AdmittedSlotKind.Numeric, Width: 4 };
            wide |= slot.Kind == AdmittedSlotKind.Address128;
        }

        foreach (FieldCapability? reported in descriptor.FieldReport)
        {
            if (reported is null
                || !Enum.IsDefined(reported.Availability)
                || reported.Availability == FieldAvailability.NotApplicable
                || (reported.ByteDomain is { } domain && !Enum.IsDefined(domain))
                || (reported.Unit is { } unit && !Enum.IsDefined(unit)))
            {
                return false;
            }
        }

        return !(narrow && wide);
    }
}
