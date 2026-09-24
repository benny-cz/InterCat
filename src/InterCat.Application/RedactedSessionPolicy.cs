using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>How many of each kind of value a redacted session package carries or replaced.</summary>
public sealed record RedactedSessionCounts
{
    /// <summary>Normalized observation rows, one synthetic journal record each.</summary>
    public required long Rows { get; init; }

    /// <summary>Source-field rows carried, whether kept, pseudonymized or redacted.</summary>
    public required long SourceFieldRows { get; init; }

    /// <summary>Source-field rows whose value the policy withheld, each marked <c>Redacted</c>.</summary>
    public required long SourceFieldRowsRedacted { get; init; }

    /// <summary>Whether the source's coverage ledger was rewritten into the package.</summary>
    public required bool CoverageLedger { get; init; }

    /// <summary>Distinct executable, pipe, resource and folder pseudonyms issued.</summary>
    public required int Names { get; init; }

    /// <summary>Distinct PID and thread-id pseudonyms issued; the fixed points 0, 4 and -1 are not counted.</summary>
    public required int ProcessesAndThreads { get; init; }

    public required int Addresses { get; init; }

    public required int Ports { get; init; }

    /// <summary>Activity, related-activity and source identifiers.</summary>
    public required int Identifiers { get; init; }

    /// <summary>Providers given a pseudonym; public Microsoft providers InterCat's catalog admits keep their identity.</summary>
    public required int Providers { get; init; }

    public required int PublicProviders { get; init; }

    public required int Schemas { get; init; }

    public required int StartSequences { get; init; }

    /// <summary>Connection, request-packet, file-object and file-key values.</summary>
    public required int KernelObjects { get; init; }
}

/// <summary>
/// `redaction-policy-*.json`, the provenance a redacted session package publishes as its <see
/// cref="StoreDependencyKind.RedactionPolicy"/> dependency (`contracts/redacted-session-v1.md`). It is built only from
/// this type's constants and counts, never from a source value, so it can be read and shown as it is.
/// </summary>
public sealed record RedactedSessionPolicyV1
{
    public const int MaximumListEntries = 64;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public required string Contract { get; init; }

    public required string Policy { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required string Provenance { get; init; }

    public required string Warning { get; init; }

    public required string PseudonymScope { get; init; }

    public required bool OriginalSourcesIncluded { get; init; }

    public required bool OriginalRawLocatorsIncluded { get; init; }

    public required bool PayloadBytesIncluded { get; init; }

    public required bool Rederivable { get; init; }

    public required IReadOnlyList<string> Retained { get; init; }

    public required IReadOnlyList<string> Pseudonymized { get; init; }

    public required IReadOnlyList<string> Redacted { get; init; }

    public required IReadOnlyList<string> Omitted { get; init; }

    public required IReadOnlyList<string> FixedPoints { get; init; }

    public required RedactedSessionCounts Counts { get; init; }

    public byte[] Encode()
    {
        Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        return bytes.Length <= SessionSegments.MaximumRedactionPolicyBytes
            ? bytes
            : throw new InvalidOperationException("The redaction policy exceeds its 64 KiB bound.");
    }

    /// <summary>Reads a policy, refusing an unknown member, another contract or a claim this version never makes.</summary>
    public static RedactedSessionPolicyV1 Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > SessionSegments.MaximumRedactionPolicyBytes)
        {
            throw new InvalidDataException("A redaction policy is between 1 B and 64 KiB.");
        }

        RedactedSessionPolicyV1 policy;
        try
        {
            policy = JsonSerializer.Deserialize<RedactedSessionPolicyV1>(bytes, Json)
                ?? throw new InvalidDataException("The redaction policy holds no object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The redaction policy is not readable redacted-session-v1 JSON.", exception);
        }

        policy.Validate();
        return policy;
    }

    private void Validate()
    {
        if (!string.Equals(Contract, RedactedSessionPackage.Contract, StringComparison.Ordinal)
            || !string.Equals(Policy, RedactedSessionPackage.Policy, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"This package names contract '{Contract}' and policy '{Policy}'; this reader implements "
                + $"{RedactedSessionPackage.Contract} under {RedactedSessionPackage.Policy}.");
        }

        // A package that claims to include its original sources is not one this version produces, whatever else it says.
        if (OriginalSourcesIncluded || OriginalRawLocatorsIncluded || PayloadBytesIncluded || Rederivable)
        {
            throw new InvalidDataException(
                "A redacted-session-v1 package never includes original sources, raw locators or payload bytes, and is "
                + "never re-derivable; this policy claims otherwise.");
        }

        IReadOnlyList<string>?[] lists = [Retained, Pseudonymized, Redacted, Omitted, FixedPoints];
        if (lists.Any(list => list is null or { Count: > MaximumListEntries } || list.Any(string.IsNullOrWhiteSpace))
            || Counts is null
            || Counts.Rows < 0 || Counts.SourceFieldRows < 0
            || Counts.SourceFieldRowsRedacted is < 0 || Counts.SourceFieldRowsRedacted > Counts.SourceFieldRows
            || string.IsNullOrWhiteSpace(Provenance) || string.IsNullOrWhiteSpace(Warning)
            || string.IsNullOrWhiteSpace(PseudonymScope))
        {
            throw new InvalidDataException("The redaction policy's disclosures or counts are incomplete or out of range.");
        }
    }
}

/// <summary>
/// What a reader shows about a redacted session package: that it is one, when it was made, and what its values are.
/// Null on an ordinary session.
/// </summary>
public sealed record SessionRedaction(
    string Contract,
    string Policy,
    DateTimeOffset CreatedUtc,
    string Warning,
    string PseudonymScope,
    RedactedSessionCounts Counts)
{
    /// <summary>One sentence for a banner or a status line.</summary>
    public const string Summary =
        "Redacted session package: names, process and thread IDs, addresses, ports and identifiers are random "
        + "pseudonyms consistent only within this package, and its records are synthetic metadata with no original payload.";

    /// <summary>The package's policy, or null when the generation names none (an ordinary session).</summary>
    public static SessionRedaction? Read(IOwnedDirectory directory, SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(manifest);
        if (SessionSegments.RedactionPolicy(directory, manifest) is not { } bytes)
        {
            return null;
        }

        RedactedSessionPolicyV1 policy = RedactedSessionPolicyV1.Decode(bytes);
        return new(policy.Contract, policy.Policy, policy.CreatedUtc, policy.Warning, policy.PseudonymScope, policy.Counts);
    }
}
