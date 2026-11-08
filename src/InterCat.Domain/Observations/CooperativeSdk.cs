namespace InterCat.Domain;

/// <summary>
/// The cooperative SDK's provider as a capture records it (`contracts/sdk-v1.md`, ADR-102): its identity, which EventSource
/// derives from its name, and its events' identities. The SDK's own type declares them, and a test holds these to it, so a
/// derivation reads the SDK's records without referencing the library an application ships. Another application provider
/// is recorded under the same mechanism - InterCat's content fixture, whose events are numbered from 1 too - so a record
/// is the SDK's by its provider, never by its mechanism or its number alone.
/// </summary>
public static class CooperativeSdk
{
    /// <summary>The provider's name, from which EventSource derives its identity.</summary>
    public const string ProviderName = "InterCat-Sdk";

    /// <summary>The provider's identity, as EventSource derives it from <see cref="ProviderName"/>.</summary>
    public static readonly Guid ProviderId = new("82c69482-41a7-5887-ff73-c02a5012c044");

    /// <summary>The SDK announced its schema and clock to a session enabling it.</summary>
    public const ushort SdkStarted = 1;

    /// <summary>A process opened, or joined, a channel at an epoch.</summary>
    public const ushort ChannelOpened = 2;

    /// <summary>A process closed its use of a channel's epoch.</summary>
    public const ushort ChannelClosed = 3;

    /// <summary>A message was published at a sequence of its channel's epoch.</summary>
    public const ushort MessagePublished = 4;

    /// <summary>A message was consumed by one reader.</summary>
    public const ushort MessageConsumed = 5;

    /// <summary>An operation started.</summary>
    public const ushort OperationStarted = 6;

    /// <summary>An operation completed.</summary>
    public const ushort OperationCompleted = 7;

    /// <summary>
    /// A run of consecutive messages published together, from a sequence on, their lengths said in total: a batch (ADR-106).
    /// </summary>
    public const ushort MessagesPublished = 8;

    /// <summary>A run of consecutive messages consumed together by one reader: a batch (ADR-106).</summary>
    public const ushort MessagesConsumed = 9;

    /// <summary>
    /// A message's content, its whole bytes, said right after its publication only to a session asking for it, and kept only
    /// by a capture keeping the SDK's content under a request (ADR-107).
    /// </summary>
    public const ushort MessageContent = 10;

    /// <summary>
    /// The most messages one batch says: a record saying more names no message, as no SDK raises one, and its run is never
    /// spread past what the schema bounds.
    /// </summary>
    public const long MaximumBatch = 65_536;

    /// <summary>The schema's major this InterCat reads: every event's version (`contracts/sdk-v1.md` §5).</summary>
    public const int SchemaMajor = 1;

    /// <summary>
    /// The newest minor of the schema this InterCat knows: a process announcing a later one may raise events no capture of
    /// this InterCat admits, and no reading of it counts (ADR-111).
    /// </summary>
    public const int SchemaMinor = 2;

    /// <summary>
    /// The events a minor added to the schema: minor 0 every event it began with, minor 1 the batches (ADR-106), minor 2 a
    /// message's content (ADR-107); none for a minor this InterCat does not know.
    /// </summary>
    public static IReadOnlyList<ushort> AddedBy(int minor) => minor switch
    {
        0 => [SdkStarted, ChannelOpened, ChannelClosed, MessagePublished, MessageConsumed, OperationStarted, OperationCompleted],
        1 => [MessagesPublished, MessagesConsumed],
        2 => [MessageContent],
        _ => [],
    };

    /// <summary>
    /// Whether an event is said only to a session keeping the SDK's content (ADR-107): a capture keeping none leaves it out by
    /// choice, never for want of knowing it.
    /// </summary>
    public static bool ContentOnly(ushort eventId) => eventId == MessageContent;
}
