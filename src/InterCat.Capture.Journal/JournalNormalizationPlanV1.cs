using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Capture.Windows;
using InterCat.Storage;

namespace InterCat.Capture.Journal;

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

    public required string Contract { get; init; }

    public required IReadOnlyList<AdmittedEventPlan> Descriptors { get; init; }

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
        if (table.Schemas.Count != Descriptors.Count)
        {
            throw new InvalidDataException(
                $"The journal names {table.Schemas.Count} schemas but the retained plan interprets "
                + $"{Descriptors.Count}. Replay refuses an incomplete or extra interpretation.");
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
        AdmittedEventPlan plan = Descriptors.SingleOrDefault(descriptor =>
            descriptor.SourceIndex == checked((int)envelope.StreamId - 1)
            && descriptor.ProviderGuid == envelope.Header.ProviderId
            && descriptor.EventId == envelope.Header.EventId
            && descriptor.Version == envelope.Header.Version)
            ?? throw new InvalidDataException(
                $"Record {envelope.RecordOrdinal} has no retained descriptor interpretation for its stream and event.");
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

    private void Validate()
    {
        if (!string.Equals(Contract, ContractName, StringComparison.Ordinal)
            || Descriptors is null or { Count: < 1 or > 1_024 })
        {
            throw new InvalidDataException("A normalizer-plan-v1 file names its contract and 1-1,024 descriptors.");
        }

        var keys = new HashSet<(int Source, int Event, int Version)>();
        var schemas = new HashSet<(Guid Provider, int Event, int Version)>();
        foreach (AdmittedEventPlan descriptor in Descriptors)
        {
            if (descriptor is null
                || descriptor.SourceIndex < 0
                || descriptor.ProviderGuid == Guid.Empty
                || descriptor.EventId is < 0 or > ushort.MaxValue
                || descriptor.Version is < 0 or > byte.MaxValue
                || string.IsNullOrWhiteSpace(descriptor.SchemaFingerprint)
                || descriptor.Slots is null or { Count: > AdmissionPlanCompiler.MaximumSlots }
                || descriptor.FieldReport is null
                || descriptor.BodyPolicy is null
                || !keys.Add((descriptor.SourceIndex, descriptor.EventId, descriptor.Version))
                || !schemas.Add((descriptor.ProviderGuid, descriptor.EventId, descriptor.Version)))
            {
                throw new InvalidDataException("A retained normalizer descriptor is incomplete, out of bounds or duplicated.");
            }

            CaptureBodyAdmissionPolicies.EnsureSupported(descriptor.BodyPolicy);
        }
    }
}
