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
    /// The most messages one batch says: a record saying more names no message, as no SDK raises one, and its run is never
    /// spread past what the schema bounds.
    /// </summary>
    public const long MaximumBatch = 65_536;
}
