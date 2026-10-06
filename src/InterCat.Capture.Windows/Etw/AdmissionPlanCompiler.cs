using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InterCat.Domain;

namespace InterCat.Capture.Windows;

/// <summary>
/// Turns a catalog intent plus the schema TDH actually reports into a compiled admission plan.
/// A field the saved schema does not support is refused with a reason rather than guessed (section 18.3).
/// </summary>
public static class AdmissionPlanCompiler
{
    /// <summary>Widest field a bounded slot can carry in this milestone.</summary>
    public const int MaximumSlotWidth = 8;

    /// <summary>Slots per descriptor, bounded so callback work stays constant (R8).</summary>
    public const int MaximumSlots = 8;

    /// <summary>The shortest SID: revision, sub-authority count and a six-byte authority, with no sub-authority.</summary>
    public const int MinimumSidLength = 8;

    /// <summary>The most sub-authorities a SID carries (SID_MAX_SUB_AUTHORITIES); a record claiming more is undecodable.</summary>
    public const int MaximumSidSubAuthorities = 15;

    public static SourceAdmissionPlan Compile(
        WindowsSourceDefinition definition,
        ProviderSchema schema,
        int sourceIndex,
        int pointerSize = 8,
        CompiledBodyAdmissionPolicy? bodyPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(schema);

        bodyPolicy ??= CaptureBodyAdmissionPolicies.MetadataOnly;
        CaptureBodyAdmissionPolicies.EnsureSupported(bodyPolicy);

        var events = new List<AdmittedEventPlan>();
        var diagnostics = new List<string>();

        foreach (AdmittedEventIntent intent in definition.AdmittedEvents)
        {
            var versions = new List<ProviderSchemaEvent>();
            foreach (ProviderSchemaEvent candidate in schema.Events)
            {
                // A classic class's events share an id, 0, and are told apart by their opcode (ADR-035's addendum).
                if (candidate.EventId == intent.EventId && (intent.Opcode is null || candidate.OpcodeValue == intent.Opcode))
                {
                    versions.Add(candidate);
                }
            }

            if (versions.Count == 0)
            {
                diagnostics.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Event {intent.EventId} ({intent.Name}) is not declared by the saved schema of {schema.ProviderName}."));
                continue;
            }

            bool intendedVersionPresent = false;
            foreach (ProviderSchemaEvent version in versions)
            {
                intendedVersionPresent |= version.Version == intent.Version;
                events.Add(CompileDescriptor(
                    definition,
                    intent,
                    version,
                    schema,
                    sourceIndex,
                    pointerSize,
                    bodyPolicy,
                    diagnostics));
            }

            if (!intendedVersionPresent)
            {
                diagnostics.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Event {intent.EventId} ({intent.Name}) is declared, but not at the expected version {intent.Version}.")
                    + " Every declared version was compiled instead, and an unexpected version stays undecodable.");
            }
        }

        return new()
        {
            SourceId = definition.SourceId,
            ProviderGuid = schema.ProviderGuid,
            SourceIndex = sourceIndex,
            Events = events,
            Diagnostics = diagnostics,
        };
    }

    private static AdmittedEventPlan CompileDescriptor(
        WindowsSourceDefinition definition,
        AdmittedEventIntent intent,
        ProviderSchemaEvent descriptor,
        ProviderSchema schema,
        int sourceIndex,
        int pointerSize,
        CompiledBodyAdmissionPolicy bodyPolicy,
        List<string> diagnostics)
    {
        // Each resolvable field's offset, and whether a pointer-sized field precedes it: an offset past a pointer moves
        // with the width of the process that raised the record, one before any does not.
        Dictionary<string, (int Offset, ProviderSchemaField Field, bool AfterPointer)> resolvable = new(StringComparer.OrdinalIgnoreCase);
        int offset = 0;
        bool afterPointer = false;
        string? blockedFrom = null;
        (int Offset, ProviderSchemaField Field, bool AfterPointer)? leadingSid = null;
        ProviderSchemaField? afterSid = null;
        bool followsSid = false;
        foreach (ProviderSchemaField field in descriptor.Fields)
        {
            if (blockedFrom is null)
            {
                resolvable[field.Name] = (offset, field, afterPointer);
            }
            else if (followsSid)
            {
                // The field right after the first variable-length field, when that field is a SID. A SID states its
                // own length in its second byte, so this one field is reachable with a bounded shape read.
                afterSid = field;
            }

            followsSid = false;
            if (field.WidthKind == FieldWidthKind.Fixed)
            {
                offset += field.FixedWidth;
                continue;
            }

            if (field.WidthKind == FieldWidthKind.PointerSized)
            {
                offset += pointerSize;
                afterPointer = true;
                continue;
            }

            // A variable-length field resolves at its own offset but makes every later offset unknowable - except the
            // one field after a leading SID, whose offset the SID itself determines.
            if (blockedFrom is null && string.Equals(field.InType, "win:SID", StringComparison.Ordinal))
            {
                leadingSid = (offset, field, afterPointer);
                followsSid = true;
            }

            blockedFrom ??= field.Name;
        }

        var slots = new List<AdmittedSlotPlan>();
        var report = new List<FieldCapability>();
        int minimumLength = 0;
        bool widthDependent = false;
        bool nameSlotTaken = false;
        bool identifierSlotTaken = false;
        bool contentSlotTaken = false;
        int addressSlots = 0;

        foreach (AdmittedFieldIntent fieldIntent in intent.Fields)
        {
            if (!resolvable.TryGetValue(fieldIntent.FieldName, out (int Offset, ProviderSchemaField Field, bool AfterPointer) resolved))
            {
                if (afterSid is { } named
                    && leadingSid is { } sid
                    && string.Equals(named.Name, fieldIntent.FieldName, StringComparison.OrdinalIgnoreCase)
                    && fieldIntent.Role == FieldRole.ResourceName
                    && string.Equals(named.InType, "win:UnicodeString", StringComparison.Ordinal)
                    && !nameSlotTaken
                    && slots.Count < MaximumSlots)
                {
                    slots.Add(new(
                        named.Name,
                        fieldIntent.Role,
                        sid.Offset,
                        0,
                        fieldIntent.Unit,
                        fieldIntent.ByteDomain,
                        fieldIntent.Transform,
                        AdmittedSlotKind.ResourceNameAfterSid)
                    {
                        SourceField = fieldIntent.SourceField,
                    });
                    nameSlotTaken = true;
                    widthDependent |= sid.AfterPointer;
                    minimumLength = Math.Max(minimumLength, sid.Offset + MinimumSidLength);
                    report.Add(new(
                        named.Name,
                        FieldAvailability.Present,
                        fieldIntent.Role,
                        named.InType,
                        fieldIntent.Unit,
                        fieldIntent.ByteDomain,
                        $"Follows the SID '{sid.Field.Name}', and is reached through that SID's own sub-authority count, "
                        + "bounded by the record's length."));
                    continue;
                }

                bool declaredButUnreachable = false;
                string? inType = null;
                foreach (ProviderSchemaField declared in descriptor.Fields)
                {
                    if (string.Equals(declared.Name, fieldIntent.FieldName, StringComparison.OrdinalIgnoreCase))
                    {
                        declaredButUnreachable = true;
                        inType = declared.InType;
                    }
                }

                report.Add(new(
                    fieldIntent.FieldName,
                    declaredButUnreachable ? FieldAvailability.SchemaUnknown : FieldAvailability.NotExposed,
                    fieldIntent.Role,
                    inType,
                    fieldIntent.Unit,
                    fieldIntent.ByteDomain,
                    declaredButUnreachable
                        ? $"Declared, but its offset follows the variable-length field '{blockedFrom}', which a bounded admission read cannot resolve."
                        : "Not declared by the saved schema of this descriptor version."));
                continue;
            }

            // A content field: a message's bytes sized by a fixed length field before them, copied beside the metadata
            // projection only under a scoped content policy (ADR-036).
            if (fieldIntent.Role == FieldRole.Content)
            {
                (FieldAvailability availability, string reason)? refusal = ContentRefusal(
                    definition.SourceId, fieldIntent, resolved.Offset, resolved.Field, resolvable, bodyPolicy, contentSlotTaken,
                    slots.Count);
                if (refusal is { } refused)
                {
                    report.Add(new(fieldIntent.FieldName, refused.availability, fieldIntent.Role, resolved.Field.InType,
                        fieldIntent.Unit, fieldIntent.ByteDomain, refused.reason));
                    continue;
                }

                (int Offset, ProviderSchemaField Field, bool AfterPointer) length = resolvable[resolved.Field.LengthField!];
                slots.Add(new(
                    resolved.Field.Name,
                    fieldIntent.Role,
                    resolved.Offset,
                    0,
                    null,
                    null,
                    SlotTransform.None,
                    AdmittedSlotKind.Content)
                {
                    LengthOffset = length.Offset,
                    LengthWidth = length.Field.FixedWidth,
                    ContentClassification = fieldIntent.ContentClassification,
                    ContentEncoding = fieldIntent.ContentEncoding,
                });
                contentSlotTaken = true;
                widthDependent |= resolved.AfterPointer || length.AfterPointer;
                minimumLength = Math.Max(minimumLength, resolved.Offset);
                report.Add(new(
                    resolved.Field.Name,
                    FieldAvailability.Present,
                    fieldIntent.Role,
                    resolved.Field.InType,
                    fieldIntent.Unit,
                    fieldIntent.ByteDomain,
                    string.Create(CultureInfo.InvariantCulture,
                        $"Content sized by '{length.Field.Name}', kept up to {bodyPolicy.ContentRecordLimit:N0} bytes a record beside the metadata projection, never in it.")));
                continue;
            }

            bool isVariableName = resolved.Field.WidthKind == FieldWidthKind.Variable
                && fieldIntent.Role == FieldRole.ResourceName;
            bool isAnsiName = isVariableName
                && string.Equals(resolved.Field.InType, "win:AnsiString", StringComparison.Ordinal);
            bool isName = isAnsiName
                || (isVariableName && string.Equals(resolved.Field.InType, "win:UnicodeString", StringComparison.Ordinal));
            bool isIdentifier = string.Equals(resolved.Field.InType, "win:GUID", StringComparison.Ordinal)
                && fieldIntent.Role is FieldRole.CorrelationKey or FieldRole.ResourceName;

            // An IPv6 endpoint address: 16 bytes the manifest itself declares binary, of that fixed length, read as IPv6.
            // Anything else under an IPv6 intent falls through to the width check below and is refused, not guessed.
            bool isAddress = fieldIntent.Transform == SlotTransform.NetworkOrderIpv6Address
                && fieldIntent.Role is FieldRole.SourceEndpoint or FieldRole.DestinationEndpoint
                && resolved.Field.WidthKind == FieldWidthKind.Fixed
                && resolved.Field.FixedWidth == 16
                && string.Equals(resolved.Field.InType, "win:Binary", StringComparison.Ordinal)
                && string.Equals(resolved.Field.OutType, "win:IPv6", StringComparison.Ordinal);
            int resolvedWidth = resolved.Field.WidthKind switch
            {
                FieldWidthKind.Fixed => resolved.Field.FixedWidth,
                FieldWidthKind.PointerSized => pointerSize,
                _ => 0,
            };

            if (isIdentifier && identifierSlotTaken)
            {
                report.Add(new(
                    fieldIntent.FieldName,
                    FieldAvailability.ProfileDisabled,
                    fieldIntent.Role,
                    resolved.Field.InType,
                    fieldIntent.Unit,
                    fieldIntent.ByteDomain,
                    "A bounded admission copies at most one identifier per descriptor."));
                continue;
            }

            if (isAddress && addressSlots == AdmittedEvent.MaximumAddresses)
            {
                report.Add(new(
                    fieldIntent.FieldName,
                    FieldAvailability.ProfileDisabled,
                    fieldIntent.Role,
                    resolved.Field.InType,
                    fieldIntent.Unit,
                    fieldIntent.ByteDomain,
                    $"A bounded admission copies at most {AdmittedEvent.MaximumAddresses} addresses per descriptor."));
                continue;
            }

            if (isName && nameSlotTaken)
            {
                report.Add(new(
                    fieldIntent.FieldName,
                    FieldAvailability.ProfileDisabled,
                    fieldIntent.Role,
                    resolved.Field.InType,
                    fieldIntent.Unit,
                    fieldIntent.ByteDomain,
                    "A bounded admission copies at most one resource name per descriptor."));
                continue;
            }

            if (!isName && !isIdentifier && !isAddress
                && (resolved.Field.WidthKind == FieldWidthKind.Variable || resolvedWidth > MaximumSlotWidth))
            {
                report.Add(new(
                    fieldIntent.FieldName,
                    FieldAvailability.SchemaUnknown,
                    fieldIntent.Role,
                    resolved.Field.InType,
                    fieldIntent.Unit,
                    fieldIntent.ByteDomain,
                    $"Input type {resolved.Field.InType} is not a fixed field of at most {MaximumSlotWidth} bytes, so this version does not admit it."));
                continue;
            }

            if (slots.Count == MaximumSlots)
            {
                report.Add(new(
                    fieldIntent.FieldName,
                    FieldAvailability.ProfileDisabled,
                    fieldIntent.Role,
                    resolved.Field.InType,
                    fieldIntent.Unit,
                    fieldIntent.ByteDomain,
                    $"The descriptor already admits the maximum of {MaximumSlots} fields."));
                continue;
            }

            slots.Add(new(
                resolved.Field.Name,
                fieldIntent.Role,
                resolved.Offset,
                resolvedWidth,
                fieldIntent.Unit,
                fieldIntent.ByteDomain,
                fieldIntent.Transform,
                isAnsiName
                    ? AdmittedSlotKind.AnsiResourceName
                    : isName
                        ? AdmittedSlotKind.ResourceName
                        : isIdentifier
                            ? AdmittedSlotKind.Identifier
                            : isAddress ? AdmittedSlotKind.Address128 : AdmittedSlotKind.Numeric)
            {
                SourceField = fieldIntent.SourceField,
            });
            nameSlotTaken |= isName;
            identifierSlotTaken |= isIdentifier;
            addressSlots += isAddress ? 1 : 0;
            widthDependent |= resolved.AfterPointer || resolved.Field.WidthKind == FieldWidthKind.PointerSized;
            minimumLength = Math.Max(
                minimumLength,
                resolved.Offset + (isAnsiName ? 1 : isName ? 2 : isIdentifier ? 16 : resolvedWidth));
            report.Add(new(
                resolved.Field.Name,
                FieldAvailability.Present,
                fieldIntent.Role,
                resolved.Field.InType,
                fieldIntent.Unit,
                fieldIntent.ByteDomain,
                fieldIntent.Notes));
        }

        if (slots.Count == 0 && intent.Fields.Count > 0)
        {
            diagnostics.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{definition.SourceId}: event {descriptor.EventId} v{descriptor.Version} admits no field; only its header is available."));
        }

        string fingerprint = ComputeFingerprint(
            schema.SchemaFingerprint,
            schema.ProviderGuid,
            descriptor.EventId,
            descriptor.Version,
            pointerSize,
            minimumLength,
            slots,
            bodyPolicy);

        return new()
        {
            SourceIndex = sourceIndex,
            ProviderGuid = schema.ProviderGuid,
            EventId = descriptor.EventId,
            Opcode = intent.Opcode,
            Version = descriptor.Version,
            Name = intent.Name,
            Mechanism = intent.Mechanism,
            Layer = intent.Layer,
            Kind = intent.Kind,
            Direction = intent.Direction,
            MinimumBodyLength = minimumLength,
            PointerSize = pointerSize,
            PointerWidthIndependent = !widthDependent,
            SchemaFingerprint = fingerprint,
            BodyPolicy = bodyPolicy,
            Slots = slots,
            FieldReport = report,
        };
    }

    /// <summary>
    /// Why a content field cannot be admitted, or null when it can: only under a scoped content policy, one a
    /// descriptor, a binary field sized by a fixed count of at most four bytes before it, and with the classification and
    /// encoding the catalog states for it (ADR-036).
    /// </summary>
    private static (FieldAvailability Availability, string Reason)? ContentRefusal(
        string sourceId,
        AdmittedFieldIntent intent,
        int offset,
        ProviderSchemaField field,
        Dictionary<string, (int Offset, ProviderSchemaField Field, bool AfterPointer)> resolvable,
        CompiledBodyAdmissionPolicy policy,
        bool taken,
        int slotCount)
    {
        if (!policy.KeepsContent)
        {
            return (FieldAvailability.ProfileDisabled,
                "Content is admitted only under a scoped content policy; this capture keeps metadata only.");
        }

        if (!policy.KeepsContentOf(sourceId))
        {
            return (FieldAvailability.ProfileDisabled,
                $"The capture's content policy '{policy.PolicyId}' keeps no content of this source; its metadata is kept.");
        }

        if (taken)
        {
            return (FieldAvailability.ProfileDisabled, "A bounded admission copies at most one content field per descriptor.");
        }

        if (slotCount == MaximumSlots)
        {
            return (FieldAvailability.ProfileDisabled, $"The descriptor already admits the maximum of {MaximumSlots} fields.");
        }

        if (field.WidthKind != FieldWidthKind.Variable
            || !string.Equals(field.InType, "win:Binary", StringComparison.Ordinal)
            || field.LengthField is not { } lengthName
            || !resolvable.TryGetValue(lengthName, out (int Offset, ProviderSchemaField Field, bool AfterPointer) length)
            || length.Field.WidthKind != FieldWidthKind.Fixed
            || length.Field.FixedWidth is not (1 or 2 or 4)
            || length.Offset + length.Field.FixedWidth > offset)
        {
            return (FieldAvailability.SchemaUnknown,
                "Content is a binary field sized by a fixed count of at most four bytes before it; this one is not.");
        }

        return intent.ContentClassification is null || intent.ContentEncoding is null
            ? (FieldAvailability.SchemaUnknown, "The catalog states no classification or encoding for this content field.")
            : null;
    }

    /// <summary>
    /// Computes a stable identity for everything that can affect bounded decoding or persistence. The
    /// canonical input is explicit so runtime or JSON formatting changes cannot alter the fingerprint.
    /// </summary>
    public static string ComputeFingerprint(
        string providerSchemaFingerprint,
        Guid providerGuid,
        int eventId,
        int version,
        int pointerSize,
        int minimumBodyLength,
        IReadOnlyList<AdmittedSlotPlan> slots,
        CompiledBodyAdmissionPolicy bodyPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerSchemaFingerprint);
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(bodyPolicy);

        var canonical = new StringBuilder(512);
        canonical.Append("intercat-admission-schema-v1\n")
            .Append(providerSchemaFingerprint).Append('\n')
            .Append(providerGuid.ToString("N", CultureInfo.InvariantCulture)).Append('\n')
            .Append(eventId.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(version.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(pointerSize.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(minimumBodyLength.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(bodyPolicy.PolicyId).Append('\n')
            .Append(((int)bodyPolicy.Mode).ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(((int)bodyPolicy.RetainedBody).ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(bodyPolicy.MaximumRetainedBodyBytes.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(bodyPolicy.RetainsOriginalSourceBytes ? '1' : '0').Append('\n');

        foreach (ushort type in bodyPolicy.PermittedExtendedDataTypes.Order())
        {
            canonical.Append("extended:").Append(type.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        // Content's terms are written only for a policy or slot that keeps content, so every metadata plan keeps the
        // fingerprint it always had.
        if (bodyPolicy.KeepsContent)
        {
            canonical.Append(CultureInfo.InvariantCulture,
                $"content:{bodyPolicy.ContentRecordLimit}|{bodyPolicy.ContentSessionLimit}|{(int?)bodyPolicy.ContentInspection}\n");
        }

        foreach (AdmittedSlotPlan slot in slots)
        {
            canonical.Append("slot:")
                .Append(slot.FieldName).Append('|')
                .Append(((int)slot.Role).ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(slot.Offset.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(slot.Width.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(slot.Unit?.ToString() ?? "-").Append('|')
                .Append(slot.ByteDomain?.ToString() ?? "-").Append('|')
                .Append(((int)slot.Transform).ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(((int)slot.Kind).ToString(CultureInfo.InvariantCulture));
            if (slot.Kind == AdmittedSlotKind.Content)
            {
                canonical.Append(CultureInfo.InvariantCulture,
                    $"|length:{slot.LengthOffset}:{slot.LengthWidth}|{(int?)slot.ContentClassification}|{(int?)slot.ContentEncoding}");
            }

            canonical.Append('\n');
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return "sha256:" + Convert.ToHexStringLower(digest);
    }
}
