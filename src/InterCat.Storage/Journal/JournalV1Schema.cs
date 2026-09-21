using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>
/// One schema a journal's records were admitted against. The fingerprint identifies the descriptor shape
/// the recording machine saw, so a reader can tell that two records share a layout without having the
/// recording machine's manifests (§18.3).
/// </summary>
public sealed record JournalSchemaV1(
    uint Reference,
    Guid ProviderId,
    ushort EventId,
    byte Version,
    string Fingerprint);

/// <summary>One admission policy identifier, referenced by every record it decided.</summary>
public sealed record JournalPolicyV1(uint Reference, string PolicyId);

/// <summary>
/// The schema and policy tables a journal carries. Records reference them by number rather than
/// repeating the text, and the tables are written before the first batch that needs them, so a reader
/// that stops at a truncated batch has still read every reference that batch could use.
/// </summary>
public sealed class JournalV1SchemaTable
{
    private readonly Dictionary<(Guid Provider, ushort EventId, byte Version), JournalSchemaV1> schemas = [];
    private readonly Dictionary<string, JournalPolicyV1> policies = new(StringComparer.Ordinal);

    public IReadOnlyCollection<JournalSchemaV1> Schemas => schemas.Values;

    public IReadOnlyCollection<JournalPolicyV1> Policies => policies.Values;

    /// <summary>Adds a schema if it is new and returns its reference. References start at one; zero is none.</summary>
    public uint Intern(Guid providerId, ushort eventId, byte version, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        (Guid, ushort, byte) key = (providerId, eventId, version);
        if (schemas.TryGetValue(key, out JournalSchemaV1? existing))
        {
            return existing.Fingerprint.Equals(fingerprint, StringComparison.Ordinal)
                ? existing.Reference
                : throw new InvalidOperationException(
                    $"Descriptor {providerId:D}/{eventId}/v{version} already has fingerprint "
                    + $"'{existing.Fingerprint}' in this journal. A descriptor cannot change shape inside "
                    + "one capture without a new reference.");
        }

        var added = new JournalSchemaV1((uint)schemas.Count + 1, providerId, eventId, version, fingerprint);
        schemas.Add(key, added);
        return added.Reference;
    }

    /// <summary>Adds a policy identifier if it is new and returns its reference.</summary>
    public uint InternPolicy(string policyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        if (policies.TryGetValue(policyId, out JournalPolicyV1? existing))
        {
            return existing.Reference;
        }

        var added = new JournalPolicyV1((uint)policies.Count + 1, policyId);
        policies.Add(policyId, added);
        return added.Reference;
    }

    /// <summary>Rebuilds a table from what a reader decoded, so a replayed journal resolves its references.</summary>
    public static JournalV1SchemaTable FromDecoded(
        IEnumerable<JournalSchemaV1> schemas,
        IEnumerable<JournalPolicyV1> policies)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(policies);

        var table = new JournalV1SchemaTable();
        foreach (JournalSchemaV1 schema in schemas)
        {
            table.schemas[(schema.ProviderId, schema.EventId, schema.Version)] = schema;
        }

        foreach (JournalPolicyV1 policy in policies)
        {
            table.policies[policy.PolicyId] = policy;
        }

        return table;
    }

    public JournalSchemaV1? Find(uint reference) =>
        schemas.Values.FirstOrDefault(schema => schema.Reference == reference);

    public JournalPolicyV1? FindPolicy(uint reference) =>
        policies.Values.FirstOrDefault(policy => policy.Reference == reference);
}

/// <summary>The frames journal-v1 defines. A reader that meets an unknown kind refuses the file (§20.1).</summary>
public enum JournalFrameKind : uint
{
    SourceClock = 1,
    SchemaTable = 2,
    RecordBatch = 3,
    Terminal = 4,
}

/// <summary>
/// One batch as journal-v1 frames it: a record count, the first and last source identities it covers,
/// and the records themselves. §20.1 requires the identities so a reader can tell what a batch covers
/// without decoding it, and a partial trailing batch is never committed.
/// </summary>
public sealed record JournalBatchV1(
    RawRecordId First,
    RawRecordId Last,
    IReadOnlyList<RecordEnvelopeV1> Records) : IDisposable
{
    public void Dispose()
    {
        foreach (RecordEnvelopeV1 record in Records)
        {
            record.Dispose();
        }
    }
}
