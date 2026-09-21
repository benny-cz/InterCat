using System.Globalization;
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
        int pointerSize = 8)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(schema);

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
                events.Add(CompileDescriptor(definition, intent, version, schema, sourceIndex, pointerSize, diagnostics));
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
            int resolvedWidth = resolved.Field.WidthKind switch
            {
                FieldWidthKind.Fixed => resolved.Field.FixedWidth,
                FieldWidthKind.PointerSized => pointerSize,
                _ => 0,
            };

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

            if (!isName && (resolved.Field.WidthKind == FieldWidthKind.Variable || resolvedWidth > MaximumSlotWidth))
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
                isName ? AdmittedSlotKind.ResourceName : AdmittedSlotKind.Numeric));
            nameSlotTaken |= isName;
            minimumLength = Math.Max(minimumLength, resolved.Offset + (isName ? 2 : resolvedWidth));
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

        return new()
        {
            SourceIndex = sourceIndex,
            ProviderGuid = schema.ProviderGuid,
            EventId = descriptor.EventId,
            Version = descriptor.Version,
            Name = intent.Name,
            Mechanism = intent.Mechanism,
            Kind = intent.Kind,
            Direction = intent.Direction,
            MinimumBodyLength = minimumLength,
            PointerSize = pointerSize,
            Slots = slots,
            FieldReport = report,
        };
    }
}
