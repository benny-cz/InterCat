using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>IC-009 experiment only. Version 0 is deliberately not the RecordEnvelopeV1 wire contract.</summary>
public enum JournalProbeAdmissionMode
{
    MetadataOnly = 1,
    ScopedContent = 2,
    OriginalEvidence = 3,
}

public enum JournalProbeBodyClassification
{
    None = 0,
    ApprovedMetadata = 1,
    Content = 2,
    OpaqueUnknown = 3,
}

public enum JournalProbeBodyDisposition
{
    NoBody = 0,
    Retained = 1,
    TruncatedByPolicy = 2,
    OmittedUnknownSchema = 3,
    OmittedUnapprovedClassification = 4,
    OmittedOutOfScope = 5,
    OmittedBudgetExceeded = 6,
}

public readonly record struct JournalProbeDescriptor(
    Guid ProviderGuid,
    int EventId,
    int Version,
    string SchemaFingerprint);

public readonly record struct JournalProbeExtendedItem(
    ushort Type,
    ushort Flags,
    ReadOnlyMemory<byte> Bytes);

/// <summary>A callback-lifetime view used as input to the admission experiment.</summary>
public sealed record JournalProbeSourceRecord
{
    public required RawRecordId Id { get; init; }
    public required Guid ProviderGuid { get; init; }
    public required int EventId { get; init; }
    public required int Version { get; init; }
    public required int Opcode { get; init; }
    public required NativeTimestamp NativeTimestamp { get; init; }
    public required int HeaderProcessId { get; init; }
    public required int HeaderThreadId { get; init; }
    public required int ProcessorNumber { get; init; }
    public required int PointerSize { get; init; }
    public required Guid ActivityId { get; init; }
    public required Guid RelatedActivityId { get; init; }
    public required string SchemaFingerprint { get; init; }
    public required JournalProbeBodyClassification BodyClassification { get; init; }
    public required ReadOnlyMemory<byte> Body { get; init; }
    public required IReadOnlyList<JournalProbeExtendedItem> ExtendedItems { get; init; }
    public required bool ScopeMatched { get; init; }
}

public sealed record JournalProbeBodyEvidence
{
    public required JournalProbeBodyClassification Classification { get; init; }
    public required JournalProbeBodyDisposition Disposition { get; init; }
    public required int OriginalLength { get; init; }
    public required ReadOnlyMemory<byte> RetainedBytes { get; init; }
}

/// <summary>
/// Semantic envelope used to test IC-009 decisions. Its binary codec is a disposable probe format and
/// cannot be opened as a production InterCat journal.
/// </summary>
public sealed record JournalProbeEnvelope
{
    public required RawRecordId Id { get; init; }
    public required Guid ProviderGuid { get; init; }
    public required int EventId { get; init; }
    public required int Version { get; init; }
    public required int Opcode { get; init; }
    public required NativeTimestamp NativeTimestamp { get; init; }
    public required int HeaderProcessId { get; init; }
    public required int HeaderThreadId { get; init; }
    public required int ProcessorNumber { get; init; }
    public required int PointerSize { get; init; }
    public required Guid ActivityId { get; init; }
    public required Guid RelatedActivityId { get; init; }
    public required string SchemaFingerprint { get; init; }
    public required string AdmissionPolicyId { get; init; }
    public required JournalProbeBodyEvidence Body { get; init; }
    public required IReadOnlyList<JournalProbeExtendedItem> ExtendedItems { get; init; }
    public required int OmittedExtendedItemCount { get; init; }
}

public sealed record JournalProbeAdmissionResult(
    JournalProbeEnvelope Envelope,
    bool HasPolicyOmission);

public sealed record JournalProbeAttributionReport(
    int InputRecords,
    int EnvelopeRecords,
    int RetainedBodies,
    int ExplicitBodyOmissions,
    int RecordsWithoutBodies,
    int OmittedExtendedItems)
{
    public bool IsComplete =>
        InputRecords == EnvelopeRecords
        && InputRecords == RetainedBodies + ExplicitBodyOmissions + RecordsWithoutBodies;
}
