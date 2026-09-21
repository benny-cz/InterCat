namespace InterCat.Storage;

/// <summary>Immutable bounded policy used by the IC-009 experiment.</summary>
public sealed class JournalProbePolicy
{
    private readonly HashSet<JournalProbeDescriptor> permittedDescriptors;
    private readonly HashSet<ushort> permittedExtendedTypes;

    public JournalProbePolicy(
        string policyId,
        JournalProbeAdmissionMode mode,
        int maximumRetainedBodyBytes,
        int maximumExtendedItemBytes,
        IEnumerable<JournalProbeDescriptor> permittedDescriptors,
        IEnumerable<ushort> permittedExtendedTypes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRetainedBodyBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumExtendedItemBytes);
        ArgumentNullException.ThrowIfNull(permittedDescriptors);
        ArgumentNullException.ThrowIfNull(permittedExtendedTypes);
        if (policyId.Length > JournalProbeCodec.MaximumTextLength)
        {
            throw new ArgumentOutOfRangeException(nameof(policyId));
        }

        PolicyId = policyId;
        Mode = mode;
        MaximumRetainedBodyBytes = maximumRetainedBodyBytes;
        MaximumExtendedItemBytes = maximumExtendedItemBytes;
        this.permittedDescriptors = new(permittedDescriptors);
        this.permittedExtendedTypes = new(permittedExtendedTypes);
    }

    public string PolicyId { get; }
    public JournalProbeAdmissionMode Mode { get; }
    public int MaximumRetainedBodyBytes { get; }
    public int MaximumExtendedItemBytes { get; }

    public bool Permits(JournalProbeSourceRecord source) => permittedDescriptors.Contains(
        new(source.ProviderGuid, source.EventId, source.Version, source.SchemaFingerprint));

    public bool PermitsExtendedType(ushort type) => permittedExtendedTypes.Contains(type);
}

public static class JournalProbeAdmission
{
    public static JournalProbeAdmissionResult Admit(
        JournalProbeSourceRecord source,
        JournalProbePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(policy);
        ValidateSource(source);

        JournalProbeBodyEvidence body = AdmitBody(source, policy);
        var extended = new List<JournalProbeExtendedItem>(source.ExtendedItems.Count);
        int omittedExtended = 0;
        foreach (JournalProbeExtendedItem item in source.ExtendedItems)
        {
            if (!policy.PermitsExtendedType(item.Type)
                || item.Bytes.Length > policy.MaximumExtendedItemBytes)
            {
                omittedExtended++;
                continue;
            }

            extended.Add(new(item.Type, item.Flags, item.Bytes.ToArray()));
        }

        var envelope = new JournalProbeEnvelope
        {
            Id = source.Id,
            ProviderGuid = source.ProviderGuid,
            EventId = source.EventId,
            Version = source.Version,
            Opcode = source.Opcode,
            NativeTimestamp = source.NativeTimestamp,
            HeaderProcessId = source.HeaderProcessId,
            HeaderThreadId = source.HeaderThreadId,
            ProcessorNumber = source.ProcessorNumber,
            PointerSize = source.PointerSize,
            ActivityId = source.ActivityId,
            RelatedActivityId = source.RelatedActivityId,
            SchemaFingerprint = source.SchemaFingerprint,
            AdmissionPolicyId = policy.PolicyId,
            Body = body,
            ExtendedItems = extended,
            OmittedExtendedItemCount = omittedExtended,
        };

        bool policyOmission = IsOmission(body.Disposition) || omittedExtended > 0;
        return new(envelope, policyOmission);
    }

    public static JournalProbeAttributionReport Summarize(
        IReadOnlyCollection<JournalProbeAdmissionResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        int retained = 0;
        int omitted = 0;
        int noBody = 0;
        int omittedExtended = 0;
        foreach (JournalProbeAdmissionResult result in results)
        {
            omittedExtended += result.Envelope.OmittedExtendedItemCount;
            switch (result.Envelope.Body.Disposition)
            {
                case JournalProbeBodyDisposition.Retained:
                case JournalProbeBodyDisposition.TruncatedByPolicy:
                    retained++;
                    break;
                case JournalProbeBodyDisposition.NoBody:
                    noBody++;
                    break;
                default:
                    omitted++;
                    break;
            }
        }

        return new(results.Count, results.Count, retained, omitted, noBody, omittedExtended);
    }

    private static JournalProbeBodyEvidence AdmitBody(
        JournalProbeSourceRecord source,
        JournalProbePolicy policy)
    {
        int originalLength = source.Body.Length;
        if (originalLength == 0)
        {
            return BuildBody(source, JournalProbeBodyDisposition.NoBody, ReadOnlyMemory<byte>.Empty);
        }

        bool known = policy.Permits(source);
        if (!known)
        {
            return BuildBody(source, JournalProbeBodyDisposition.OmittedUnknownSchema, ReadOnlyMemory<byte>.Empty);
        }

        if (policy.Mode == JournalProbeAdmissionMode.MetadataOnly
            && source.BodyClassification != JournalProbeBodyClassification.ApprovedMetadata)
        {
            return BuildBody(source, JournalProbeBodyDisposition.OmittedUnapprovedClassification, ReadOnlyMemory<byte>.Empty);
        }

        if (policy.Mode == JournalProbeAdmissionMode.ScopedContent && !source.ScopeMatched)
        {
            return BuildBody(source, JournalProbeBodyDisposition.OmittedOutOfScope, ReadOnlyMemory<byte>.Empty);
        }

        if (originalLength > policy.MaximumRetainedBodyBytes)
        {
            if (policy.Mode != JournalProbeAdmissionMode.ScopedContent
                || policy.MaximumRetainedBodyBytes == 0)
            {
                return BuildBody(source, JournalProbeBodyDisposition.OmittedBudgetExceeded, ReadOnlyMemory<byte>.Empty);
            }

            return BuildBody(
                source,
                JournalProbeBodyDisposition.TruncatedByPolicy,
                source.Body[..policy.MaximumRetainedBodyBytes].ToArray());
        }

        return BuildBody(source, JournalProbeBodyDisposition.Retained, source.Body.ToArray());
    }

    private static JournalProbeBodyEvidence BuildBody(
        JournalProbeSourceRecord source,
        JournalProbeBodyDisposition disposition,
        ReadOnlyMemory<byte> retained) =>
        new()
        {
            Classification = source.BodyClassification,
            Disposition = disposition,
            OriginalLength = source.Body.Length,
            RetainedBytes = retained,
        };

    private static bool IsOmission(JournalProbeBodyDisposition disposition) =>
        disposition is not JournalProbeBodyDisposition.NoBody
            and not JournalProbeBodyDisposition.Retained
            and not JournalProbeBodyDisposition.TruncatedByPolicy;

    private static void ValidateSource(JournalProbeSourceRecord source)
    {
        if (source.PointerSize is not 4 and not 8)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "Pointer size must be 4 or 8 bytes.");
        }

        if (source.SchemaFingerprint.Length > JournalProbeCodec.MaximumTextLength)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "Schema fingerprint exceeds the probe bound.");
        }

        if (source.ExtendedItems.Count > JournalProbeCodec.MaximumExtendedItemCount)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "Extended item count exceeds the probe bound.");
        }
    }
}
