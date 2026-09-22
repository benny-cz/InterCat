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
                if (candidate.EventId == intent.EventId)
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
        Dictionary<string, (int Offset, ProviderSchemaField Field)> resolvable = new(StringComparer.OrdinalIgnoreCase);
        int offset = 0;
        string? blockedFrom = null;
        foreach (ProviderSchemaField field in descriptor.Fields)
        {
            if (blockedFrom is null)
            {
                resolvable[field.Name] = (offset, field);
            }

            if (field.WidthKind == FieldWidthKind.Fixed)
            {
                offset += field.FixedWidth;
                continue;
            }

            if (field.WidthKind == FieldWidthKind.PointerSized)
            {
                offset += pointerSize;
                continue;
            }

            // A variable-length field resolves at its own offset but makes every later offset unknowable.
            blockedFrom ??= field.Name;
        }

        var slots = new List<AdmittedSlotPlan>();
        var report = new List<FieldCapability>();
        int minimumLength = 0;
        bool nameSlotTaken = false;
        bool identifierSlotTaken = false;

        foreach (AdmittedFieldIntent fieldIntent in intent.Fields)
        {
            if (!resolvable.TryGetValue(fieldIntent.FieldName, out (int Offset, ProviderSchemaField Field) resolved))
            {
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

            bool isName = resolved.Field.WidthKind == FieldWidthKind.Variable
                && fieldIntent.Role == FieldRole.ResourceName
                && string.Equals(resolved.Field.InType, "win:UnicodeString", StringComparison.Ordinal);
            bool isIdentifier = string.Equals(resolved.Field.InType, "win:GUID", StringComparison.Ordinal)
                && fieldIntent.Role is FieldRole.CorrelationKey or FieldRole.ResourceName;
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

            if (!isName && !isIdentifier && (resolved.Field.WidthKind == FieldWidthKind.Variable || resolvedWidth > MaximumSlotWidth))
            {
                report.Add(new(
                    fieldIntent.FieldName,
                    FieldAvailability.SchemaUnknown,
                    fieldIntent.Role,
                    resolved.Field.InType,
                    fieldIntent.Unit,
                    fieldIntent.ByteDomain,
                    $"Input type {resolved.Field.InType} is not a fixed field of at most {MaximumSlotWidth} bytes, so it is not admitted in this milestone."));
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
                isName
                    ? AdmittedSlotKind.ResourceName
                    : isIdentifier ? AdmittedSlotKind.Identifier : AdmittedSlotKind.Numeric));
            nameSlotTaken |= isName;
            identifierSlotTaken |= isIdentifier;
            minimumLength = Math.Max(
                minimumLength,
                resolved.Offset + (isName ? 2 : isIdentifier ? 16 : resolvedWidth));
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
            Version = descriptor.Version,
            Name = intent.Name,
            Mechanism = intent.Mechanism,
            Layer = intent.Layer,
            Kind = intent.Kind,
            Direction = intent.Direction,
            MinimumBodyLength = minimumLength,
            PointerSize = pointerSize,
            SchemaFingerprint = fingerprint,
            BodyPolicy = bodyPolicy,
            Slots = slots,
            FieldReport = report,
        };
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
                .Append(((int)slot.Kind).ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return "sha256:" + Convert.ToHexStringLower(digest);
    }
}
