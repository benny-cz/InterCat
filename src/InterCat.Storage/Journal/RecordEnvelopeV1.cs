using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>
/// What a body is, as admission classified it. Frozen by journal-v1: a value here is a wire code.
/// </summary>
public enum BodyClassificationV1 : byte
{
    None = 0,
    ApprovedMetadata = 1,
    Content = 2,
    OpaqueUnknown = 3,
}

/// <summary>
/// What happened to a body. An omission is one of these, never an absent field: §18.2 requires
/// unknown-body suppression to be a visible policy omission rather than unexplained parser loss (I13).
/// </summary>
public enum BodyDispositionV1 : byte
{
    NoBody = 0,
    Retained = 1,
    TruncatedByPolicy = 2,
    OmittedUnknownSchema = 3,
    OmittedUnapprovedClassification = 4,
    OmittedOutOfScope = 5,
    OmittedBudgetExceeded = 6,
}

/// <summary>
/// The event header fields journal-v1 keeps. Windows defines the header, the buffer context, the
/// extended data and the user data separately, and serializing only the user data would lose the
/// correlation and decoding context the rest carries (§18.1).
/// </summary>
public readonly record struct EventHeaderFieldsV1(
    Guid ProviderId,
    ushort EventId,
    byte Version,
    byte Channel,
    byte Level,
    byte Opcode,
    ushort Task,
    ulong Keyword,
    ushort Flags,
    ushort EventProperty,
    int ProcessId,
    int ThreadId,
    Guid ActivityId,
    Guid RelatedActivityId);

/// <summary>The buffer context: which processor delivered the record, and from which logger.</summary>
public readonly record struct BufferContextFieldsV1(ushort ProcessorNumber, ushort LoggerId);

/// <summary>
/// One extended-data item as journal-v1 stores it: its type, its linkage bit, how long it was, and the
/// bytes that were kept. A truncated item keeps its original length, so a prefix is never mistaken for
/// the whole item (I21).
/// </summary>
public sealed record ExtendedItemV1 : IDisposable
{
    public required ushort Type { get; init; }
    public required ushort Flags { get; init; }
    public required int OriginalLength { get; init; }
    public required EnvelopeBuffer Bytes { get; init; }

    public bool Truncated => Bytes.Length < OriginalLength;

    public void Dispose() => Bytes.Dispose();
}

/// <summary>
/// The body and what policy did with it. An omitted body keeps its original length and its disposition,
/// because "nothing was kept" and "there was nothing" are different facts (R3, I13).
/// </summary>
public sealed record BodyV1 : IDisposable
{
    public static BodyV1 None { get; } = new()
    {
        Classification = BodyClassificationV1.None,
        Disposition = BodyDispositionV1.NoBody,
        OriginalLength = 0,
        Bytes = EnvelopeBuffer.Empty,
    };

    public required BodyClassificationV1 Classification { get; init; }
    public required BodyDispositionV1 Disposition { get; init; }
    public required int OriginalLength { get; init; }
    public required EnvelopeBuffer Bytes { get; init; }

    public int RetainedLength => Bytes.Length;

    public void Dispose() => Bytes.Dispose();
}

/// <summary>
/// The journal-v1 record envelope, frozen by IC-011 on the evidence of ADR-008. Every field here is part
/// of the wire contract of `contracts/journal-v1.md`; adding, removing or reordering one is a format
/// change that needs a new major version and an ADR.
/// </summary>
/// <remarks>
/// An envelope owns its buffers. It is disposable because those buffers are pooled and have exactly one
/// owner at a time: the callback copies into them, the queue carries them, the writer serializes them,
/// and disposing returns them (§18.1). Nothing in an envelope is a pointer into callback memory, and
/// <c>UserContext</c> and other callback-lifetime values are never serialized.
/// </remarks>
public sealed record RecordEnvelopeV1 : IDisposable
{
    /// <summary>The capture this record belongs to. It is written once in the file header, not per record.</summary>
    public required CaptureId CaptureId { get; init; }

    public required uint StreamId { get; init; }
    public required uint SourceEpoch { get; init; }
    public required ulong RecordOrdinal { get; init; }

    public required EventHeaderFieldsV1 Header { get; init; }
    public required BufferContextFieldsV1 BufferContext { get; init; }

    /// <summary>The clock this record's native reading is on, named by the file's clock frame.</summary>
    public required ClockId ClockId { get; init; }

    public required TimestampEncoding TimestampEncoding { get; init; }

    /// <summary>The source clock reading in its original encoding. It is never replaced by a conversion (I8).</summary>
    public required long NativeTicks { get; init; }

    /// <summary>
    /// The pointer width the record's fields were laid out for. A reader on another architecture needs
    /// it to interpret them, so it travels with the record rather than with the machine (§18.1).
    /// </summary>
    public required byte PointerSize { get; init; }

    /// <summary>
    /// The schema this record was admitted against, by reference into the file's schema table, or null
    /// when it was admitted without one. The table travels with the journal, because the viewing machine
    /// may not have the recording machine's manifests (§18.3).
    /// </summary>
    public required uint? SchemaReference { get; init; }

    /// <summary>The admission policy that decided this record's body, by reference into the file's table.</summary>
    public required uint AdmissionPolicyReference { get; init; }

    public required IReadOnlyList<ExtendedItemV1> ExtendedItems { get; init; }

    /// <summary>
    /// Items the source carried that this envelope does not. They are counted so a reader can tell a
    /// record that had none from a record whose items were refused or did not fit (I13).
    /// </summary>
    public required int OmittedExtendedItemCount { get; init; }

    public required BodyV1 Body { get; init; }

    /// <summary>This record's raw identity, which replay preserves unchanged (I1, §18.4).</summary>
    public RawRecordId Id => new(CaptureId, StreamId, SourceEpoch, RecordOrdinal);

    public void Dispose()
    {
        Body.Dispose();
        foreach (ExtendedItemV1 item in ExtendedItems)
        {
            item.Dispose();
        }
    }
}
