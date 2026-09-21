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

    public static void EnsureSupported(CompiledBodyAdmissionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        bool exactExtendedDataAllowlist =
            policy.PermittedExtendedDataTypes.Count == EtwExtendedDataTypes.PermittedMetadata.Count
            && policy.PermittedExtendedDataTypes.Distinct().Count() == policy.PermittedExtendedDataTypes.Count
            && policy.PermittedExtendedDataTypes.All(EtwExtendedDataTypes.PermittedMetadata.Contains);
        if (!string.Equals(policy.PolicyId, MetadataOnlyPolicyId, StringComparison.Ordinal)
            || policy.Mode != AdmissionMode.MetadataOnly
            || policy.RetainedBody != RetainedBodyShape.ApprovedMetadataProjection
            || policy.RetainsOriginalSourceBytes
            || policy.MaximumRetainedBodyBytes != MetadataOnly.MaximumRetainedBodyBytes
            || !exactExtendedDataAllowlist)
        {
            throw new NotSupportedException(
                $"Admission policy '{policy.PolicyId}' is not enforceable by the bounded metadata mapper. "
                + "Only the reviewed metadata policy, byte bound and extended-data allowlist are accepted. "
                + "The capture was refused instead of being downgraded or retaining unreviewed content.");
        }
    }
}
