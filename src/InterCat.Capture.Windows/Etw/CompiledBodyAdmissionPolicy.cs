using InterCat.Domain;

namespace InterCat.Capture.Windows;

/// <summary>
/// The body representation a callback is allowed to retain. This is deliberately smaller than the
/// journal-v1 classification set: a capture mode must not become available merely because the storage
/// format has a wire code for it (R17, section 18.2).
/// </summary>
public enum RetainedBodyShape
{
    ApprovedMetadataProjection = 1,
}

/// <summary>
/// Immutable body-admission rules compiled before a session starts. Adapters consume this object; they
/// do not infer a policy from a profile name or silently substitute a safer-looking mode at runtime.
/// </summary>
public sealed record CompiledBodyAdmissionPolicy
{
    public required string PolicyId { get; init; }
    public required AdmissionMode Mode { get; init; }
    public required RetainedBodyShape RetainedBody { get; init; }
    public required int MaximumRetainedBodyBytes { get; init; }
    public required bool RetainsOriginalSourceBytes { get; init; }
    public required IReadOnlyList<ushort> PermittedExtendedDataTypes { get; init; }
    public required string Summary { get; init; }

    /// <summary>
    /// Under a scoped content policy, the most bytes of one record's content kept (ADR-036): a longer message keeps its
    /// prefix and the length it had. Zero under a metadata-only policy, which keeps no content.
    /// </summary>
    public int ContentRecordLimit { get; init; }

    /// <summary>Under a scoped content policy, the most content bytes the capture keeps before it stops (stop-at-limit).</summary>
    public long ContentSessionLimit { get; init; }

    /// <summary>Under a scoped content policy, the inspection consent its content is kept under; null otherwise.</summary>
    public ContentInspectionMode? ContentInspection { get; init; }

    /// <summary>
    /// Under a scoped content policy, the sources whose content it keeps; every other source of the capture keeps its
    /// metadata only. Empty under a metadata-only policy.
    /// </summary>
    public IReadOnlyList<string> ContentSourceIds { get; init; } = [];

    /// <summary>Whether this policy keeps content beside the metadata projection.</summary>
    public bool KeepsContent => Mode == AdmissionMode.ScopedContent;

    /// <summary>Whether this policy keeps the content of <paramref name="sourceId"/>: a scoped policy that names it.</summary>
    public bool KeepsContentOf(string sourceId) => KeepsContent && ContentSourceIds.Contains(sourceId, StringComparer.Ordinal);

    public bool PermitsExtendedDataType(ushort type)
    {
        foreach (ushort permitted in PermittedExtendedDataTypes)
        {
            if (permitted == type)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Reviewed policies that the production admission path can currently enforce.</summary>
public static class CaptureBodyAdmissionPolicies
{
    public const string MetadataOnlyPolicyId = "metadata-only-admitted-projection-v1";

    /// <summary>
    /// Keeps only the bounded, schema-approved field projection and selected correlation metadata. It
    /// never retains the source event's original user-data bytes.
    /// </summary>
    public static CompiledBodyAdmissionPolicy MetadataOnly { get; } = new()
    {
        PolicyId = MetadataOnlyPolicyId,
        Mode = AdmissionMode.MetadataOnly,
        RetainedBody = RetainedBodyShape.ApprovedMetadataProjection,
        MaximumRetainedBodyBytes = 1024,
        RetainsOriginalSourceBytes = false,
        PermittedExtendedDataTypes = [.. EtwExtendedDataTypes.PermittedMetadata.Order()],
        Summary =
            "Schema-approved metadata projection only; unknown or unapproved source bodies are omitted "
            + "before persistence, and original source bytes are never retained.",
    };

    /// <summary>The reviewed scoped content policy: the controlled fixture's, the one content source admitted (ADR-036).</summary>
    public const string ScopedContentFixturePolicyId = "scoped-content-fixture-v1";

    /// <summary>The largest per-record content limit a policy names (`content-v1`'s record limit bound).</summary>
    public const int MaximumContentRecordLimit = 1024 * 1024;

    /// <summary>The largest session content limit a policy names.</summary>
    public const long MaximumContentSessionLimit = 64L * 1024 * 1024 * 1024;

    /// <summary>
    /// The metadata projection of <see cref="MetadataOnly"/>, and beside it each content field's bytes up to
    /// <paramref name="recordLimit"/>, kept as restricted evidence under <paramref name="inspection"/> until
    /// <paramref name="sessionLimit"/> stops the capture (ADR-036). The journal body is still the projection; no source
    /// body is ever retained.
    /// </summary>
    public static CompiledBodyAdmissionPolicy ScopedContentFixture(int recordLimit, long sessionLimit, ContentInspectionMode inspection)
    {
        CompiledBodyAdmissionPolicy policy = MetadataOnly with
        {
            PolicyId = ScopedContentFixturePolicyId,
            Mode = AdmissionMode.ScopedContent,
            ContentRecordLimit = recordLimit,
            ContentSessionLimit = sessionLimit,
            ContentInspection = inspection,
            ContentSourceIds = [WindowsSourceCatalog.ContentFixtureSourceId],
            Summary = "Schema-approved metadata projection, and the controlled fixture's message bytes kept beside it as "
                + "restricted evidence, bounded per record and per session; original source bodies are never retained.",
        };
        EnsureSupported(policy);
        return policy;
    }

    public static void EnsureSupported(CompiledBodyAdmissionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        bool exactExtendedDataAllowlist =
            policy.PermittedExtendedDataTypes.Count == EtwExtendedDataTypes.PermittedMetadata.Count
            && policy.PermittedExtendedDataTypes.Distinct().Count() == policy.PermittedExtendedDataTypes.Count
            && policy.PermittedExtendedDataTypes.All(EtwExtendedDataTypes.PermittedMetadata.Contains);
        bool metadataOnly = string.Equals(policy.PolicyId, MetadataOnlyPolicyId, StringComparison.Ordinal)
            && policy.Mode == AdmissionMode.MetadataOnly
            && policy.ContentRecordLimit == 0
            && policy.ContentSessionLimit == 0
            && policy.ContentInspection is null
            && policy.ContentSourceIds.Count == 0;

        // The one scoped content policy reviewed: its limits inside their bounds and its consent stated.
        bool scopedContent = string.Equals(policy.PolicyId, ScopedContentFixturePolicyId, StringComparison.Ordinal)
            && policy.Mode == AdmissionMode.ScopedContent
            && policy.ContentRecordLimit is >= 1 and <= MaximumContentRecordLimit
            && policy.ContentSessionLimit >= policy.ContentRecordLimit
            && policy.ContentSessionLimit <= MaximumContentSessionLimit
            && policy.ContentInspection is { } inspection && Enum.IsDefined(inspection)
            && policy.ContentSourceIds is [WindowsSourceCatalog.ContentFixtureSourceId];
        if (!(metadataOnly || scopedContent)
            || policy.RetainedBody != RetainedBodyShape.ApprovedMetadataProjection
            || policy.RetainsOriginalSourceBytes
            || policy.MaximumRetainedBodyBytes != MetadataOnly.MaximumRetainedBodyBytes
            || !exactExtendedDataAllowlist)
        {
            throw new NotSupportedException(
                $"Admission policy '{policy.PolicyId}' is not enforceable by the bounded metadata mapper. "
                + "Only the reviewed metadata policy and the controlled fixture's scoped content policy, with their byte "
                + "bounds and extended-data allowlist, are accepted. The capture was refused instead of being downgraded "
                + "or retaining unreviewed content.");
        }
    }
}
