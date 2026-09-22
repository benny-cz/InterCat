using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>
/// One row of the `source-fields-v1` table: one source correlation or object field of one observation, which
/// `observation-v1` has no column for (§7.3). It names its observation by the same raw-record locator and fact key
/// the observation's own row carries, so a reader joins the two without a side index, and it is as immutable as the
/// observation it belongs to (R1).
/// </summary>
public sealed record SourceFieldRowV1
{
    public required uint RawStreamId { get; init; }

    public required uint RawSourceEpoch { get; init; }

    public required ulong RawRecordOrdinal { get; init; }

    /// <summary>The fact key of the observation this field belongs to (§7.2).</summary>
    public required FactKey FactKey { get; init; }

    /// <summary>The observation's native reading, so the table sorts and blocks by time as every table does.</summary>
    public required long NativeTicks { get; init; }

    /// <summary>Which field this is, by meaning rather than by the provider's field name.</summary>
    public required SourceField Field { get; init; }

    /// <summary>The field's value as the source delivered it, or null when it delivered none.</summary>
    public long? Value { get; init; }

    /// <summary>The field's text, for a field whose value is text; null otherwise.</summary>
    public string? Text { get; init; }

    /// <summary>`Present` when the value is there, and why it is absent when it is not (R2, R3).</summary>
    public required FieldAvailability Availability { get; init; }

    /// <summary>The raw-record identity of the observation this field belongs to, given its capture.</summary>
    public RawRecordId RawRecordIdIn(CaptureId captureId) =>
        new(captureId, RawStreamId, RawSourceEpoch, RawRecordOrdinal);

    /// <summary>Returns the reason this row could not be written, or null when it can.</summary>
    public string? Validate()
    {
        if (!Enum.IsDefined(Field))
        {
            return $"A source field row names field {(ushort)Field}, which §23 does not define.";
        }

        if (!Enum.IsDefined(Availability) || Availability == FieldAvailability.NotApplicable)
        {
            return "A source field row states the field's availability, and a field that does not apply has no row.";
        }

        bool present = Value is not null || Text is not null;
        if (present != (Availability == FieldAvailability.Present))
        {
            return present
                ? $"A source field row carries a value and also reports it as {Availability}."
                : "A source field row with no value states why it has none; `Present` is not a reason (R3).";
        }

        if (Value is not null && Text is not null)
        {
            return "A source field is a number or a text, never both.";
        }

        if (Text is { Length: 0 })
        {
            return "A source field's text is null when the source named nothing, never the empty string.";
        }

        return Text is not null && System.Text.Encoding.UTF8.GetByteCount(Text) > SegmentFormatV1.MaximumTextBytes
            ? $"A source field's text is at most {SegmentFormatV1.MaximumTextBytes} UTF-8 bytes."
            : null;
    }
}
