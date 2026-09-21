namespace InterCat.Capture.Windows;

/// <summary>How wide a manifest field is in the event body, which decides whether a bounded
/// callback can read it without a general-purpose decoder (§18.2, R9).</summary>
public enum FieldWidthKind
{
    /// <summary>A fixed byte width known from the input type.</summary>
    Fixed = 1,

    /// <summary>Width follows the source architecture's pointer size.</summary>
    PointerSized = 2,

    /// <summary>Length is data dependent; a bounded shape check cannot compute later offsets.</summary>
    Variable = 3,
}

/// <summary>One declared field of an event template.</summary>
public sealed record ProviderSchemaField(string Name, string InType, FieldWidthKind WidthKind, int FixedWidth)
{
    public static ProviderSchemaField Create(string name, string inType)
    {
        (FieldWidthKind kind, int width) = WidthOf(inType);
        return new(name, inType, kind, width);
    }

    private static (FieldWidthKind Kind, int Width) WidthOf(string inType) => inType switch
    {
        "win:UInt8" or "win:Int8" or "win:Boolean8" => (FieldWidthKind.Fixed, 1),
        "win:UInt16" or "win:Int16" or "win:HexInt16" => (FieldWidthKind.Fixed, 2),
        "win:UInt32" or "win:Int32" or "win:HexInt32" or "win:Boolean" or "win:Float" => (FieldWidthKind.Fixed, 4),
        "win:UInt64" or "win:Int64" or "win:HexInt64" or "win:Double" or "win:FILETIME" => (FieldWidthKind.Fixed, 8),
        "win:GUID" or "win:SYSTEMTIME" => (FieldWidthKind.Fixed, 16),
        "win:Pointer" => (FieldWidthKind.PointerSized, 0),
        _ => (FieldWidthKind.Variable, 0),
    };
}

/// <summary>One declared event descriptor of a provider.</summary>
public sealed record ProviderSchemaEvent(
    int EventId,
    int Version,
    string? Symbol,
    string? TaskName,
    int? TaskValue,
    string? OpcodeName,
    int? OpcodeValue,
    string? Level,
    IReadOnlyList<string> Keywords,
    ulong KeywordMask,
    string? TemplateId,
    IReadOnlyList<ProviderSchemaField> Fields);

/// <summary>
/// A provider's schema as TDH reports it on this machine. The fingerprint identifies the exact
/// layout a decode was built against; a different fingerprint invalidates decode (§24).
/// </summary>
public sealed record ProviderSchema(
    string ProviderName,
    Guid ProviderGuid,
    string SchemaFingerprint,
    IReadOnlyDictionary<string, ulong> Keywords,
    IReadOnlyList<ProviderSchemaEvent> Events)
{
    public ProviderSchemaEvent? FindEvent(int eventId, int? version = null)
    {
        ProviderSchemaEvent? best = null;
        foreach (ProviderSchemaEvent candidate in Events)
        {
            if (candidate.EventId != eventId)
            {
                continue;
            }

            if (version is not null && candidate.Version != version)
            {
                continue;
            }

            if (best is null || candidate.Version > best.Version)
            {
                best = candidate;
            }
        }

        return best;
    }
}
