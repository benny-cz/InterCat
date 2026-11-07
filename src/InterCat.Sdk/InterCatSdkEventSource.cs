using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace InterCat.Sdk;

/// <summary>
/// The cooperative SDK's provider (`contracts/sdk-v1.md`, ADR-102): what an application says about its own channels,
/// messages and operations, raised through ETW as an EventSource in manifest format. Every event names the process that
/// raised it first, so a record's owner is in its own payload, as a kernel record's is (ADR-030); a channel and an
/// operation are named by identities the application chose, a message by its channel's epoch and sequence. An event's
/// text, where it has one, is its last field, so a capture reads every other field at a fixed offset, a bounded read (R9),
/// and an event carries one identity of its own in its payload: an operation's parent is its start's related activity,
/// ETW's own link from a start to what it began under. A start or a stop leaves the runtime's activity tracking alone, so
/// tracing the SDK never changes an application's activity identities. The capture's catalog reads this layout from this
/// type, so the SDK and a capture cannot disagree on it.
/// </summary>
[EventSource(Name = ProviderName)]
public sealed class InterCatSdkEventSource : EventSource
{
    /// <summary>The provider's name; its identity is derived from it, as every EventSource's is.</summary>
    public const string ProviderName = "InterCat-Sdk";

    /// <summary>The schema's major version: a change a reader of an earlier one could misread bumps it.</summary>
    public const int SchemaMajor = 1;

    /// <summary>
    /// The schema's minor version: fields added at the end of an event, which an earlier reader keeps unread, or events
    /// added, which it counts as records it cannot read, never misreads. Minor 1 adds the batches (ADR-106), minor 2 a
    /// message's content (ADR-107).
    /// </summary>
    public const int SchemaMinor = 2;

    /// <summary>The version every event is declared at, the schema's major one.</summary>
    public const byte EventVersion = SchemaMajor;

    /// <summary>The SDK announced its schema, as a session enabled the provider.</summary>
    public const int SdkStartedId = 1;

    /// <summary>A channel was opened, or joined, at an epoch.</summary>
    public const int ChannelOpenedId = 2;

    /// <summary>A channel's epoch was closed by the process.</summary>
    public const int ChannelClosedId = 3;

    /// <summary>A message was published - committed to its channel - at a sequence of an epoch.</summary>
    public const int MessagePublishedId = 4;

    /// <summary>A message was consumed by one reader.</summary>
    public const int MessageConsumedId = 5;

    /// <summary>An operation started.</summary>
    public const int OperationStartedId = 6;

    /// <summary>An operation completed, with its status.</summary>
    public const int OperationCompletedId = 7;

    /// <summary>A run of consecutive messages published together - a batch - from a sequence on (ADR-106).</summary>
    public const int MessagesPublishedId = 8;

    /// <summary>A run of consecutive messages consumed together by one reader - a batch (ADR-106).</summary>
    public const int MessagesConsumedId = 9;

    /// <summary>A message's content, its whole bytes, said only to a session that asks for content (ADR-107).</summary>
    public const int MessageContentId = 10;

    private InterCatSdkEventSource()
        : base(EventSourceSettings.EtwManifestEventFormat)
    {
    }

    /// <summary>The one instance a process raises the SDK's events through.</summary>
    public static InterCatSdkEventSource Log { get; } = new();

    /// <summary>
    /// The library's version, as the SDK announces it. It is read from a type of its own, so the announcement a session
    /// already listening hears as <see cref="Log"/> is made - before this type's own statics are - names it too.
    /// </summary>
    public static string Library => LibraryVersion.Value;

    /// <summary>A session enabling the provider is told which schema this SDK raises, before any other event.</summary>
    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command is { Command: EventCommand.Enable })
        {
            SdkStarted(Environment.ProcessId, SchemaMajor, SchemaMinor, Stopwatch.Frequency, Library);
        }
    }

    /// <summary>
    /// Announces the schema the process raises, the frequency of the clock its readings are in, and its library's version.
    /// </summary>
    [Event(SdkStartedId, Level = EventLevel.Informational, Opcode = EventOpcode.Info, Version = EventVersion)]
    public unsafe void SdkStarted(int processId, int schemaMajor, int schemaMinor, long ticksPerSecond, string library)
    {
        library ??= string.Empty;
        fixed (char* text = library)
        {
            EventData* data = stackalloc EventData[5];
            Set(data, 0, &processId, sizeof(int));
            Set(data, 1, &schemaMajor, sizeof(int));
            Set(data, 2, &schemaMinor, sizeof(int));
            Set(data, 3, &ticksPerSecond, sizeof(long));
            Set(data, 4, text, (library.Length + 1) * sizeof(char));
            WriteEventCore(SdkStartedId, 5, data);
        }
    }

    /// <summary>
    /// A channel opened or joined at <paramref name="epoch"/>: its identity, which every process using it names alike, its
    /// kind, and its name.
    /// </summary>
    [Event(ChannelOpenedId, Level = EventLevel.Informational, Opcode = EventOpcode.Start, Version = EventVersion, ActivityOptions = EventActivityOptions.Disable)]
    public unsafe void ChannelOpened(int processId, Guid channel, long epoch, int kind, string name)
    {
        name ??= string.Empty;
        fixed (char* text = name)
        {
            EventData* data = stackalloc EventData[5];
            Set(data, 0, &processId, sizeof(int));
            Set(data, 1, &channel, sizeof(Guid));
            Set(data, 2, &epoch, sizeof(long));
            Set(data, 3, &kind, sizeof(int));
            Set(data, 4, text, (name.Length + 1) * sizeof(char));
            WriteEventCore(ChannelOpenedId, 5, data);
        }
    }

    /// <summary>The process closed its use of a channel's epoch.</summary>
    [Event(ChannelClosedId, Level = EventLevel.Informational, Opcode = EventOpcode.Stop, Version = EventVersion, ActivityOptions = EventActivityOptions.Disable)]
    public unsafe void ChannelClosed(int processId, Guid channel, long epoch)
    {
        EventData* data = stackalloc EventData[3];
        Set(data, 0, &processId, sizeof(int));
        Set(data, 1, &channel, sizeof(Guid));
        Set(data, 2, &epoch, sizeof(long));
        WriteEventCore(ChannelClosedId, 3, data);
    }

    /// <summary>
    /// A message published - committed where its readers can take it - at <paramref name="sequence"/> of a channel's epoch:
    /// its logical length, the bytes the publisher copied for it where it knows them (-1 where not), and the process's clock
    /// reading at the commit.
    /// </summary>
    [Event(MessagePublishedId, Level = EventLevel.Informational, Opcode = EventOpcode.Send, Version = EventVersion)]
    public unsafe void MessagePublished(int processId, Guid channel, long epoch, long sequence, long logicalLength, long copiedLength, long sourceTicks)
    {
        EventData* data = stackalloc EventData[7];
        Set(data, 0, &processId, sizeof(int));
        Set(data, 1, &channel, sizeof(Guid));
        Set(data, 2, &epoch, sizeof(long));
        Set(data, 3, &sequence, sizeof(long));
        Set(data, 4, &logicalLength, sizeof(long));
        Set(data, 5, &copiedLength, sizeof(long));
        Set(data, 6, &sourceTicks, sizeof(long));
        WriteEventCore(MessagePublishedId, 7, data);
    }

    /// <summary>
    /// A message consumed by one reader, <paramref name="consumer"/>, at <paramref name="sequence"/> of a channel's epoch:
    /// one message three readers consume is three of these and one publication, never three writes.
    /// </summary>
    [Event(MessageConsumedId, Level = EventLevel.Informational, Opcode = EventOpcode.Receive, Version = EventVersion)]
    public unsafe void MessageConsumed(int processId, Guid channel, long epoch, long sequence, long consumer, long logicalLength, long sourceTicks)
    {
        EventData* data = stackalloc EventData[7];
        Set(data, 0, &processId, sizeof(int));
        Set(data, 1, &channel, sizeof(Guid));
        Set(data, 2, &epoch, sizeof(long));
        Set(data, 3, &sequence, sizeof(long));
        Set(data, 4, &consumer, sizeof(long));
        Set(data, 5, &logicalLength, sizeof(long));
        Set(data, 6, &sourceTicks, sizeof(long));
        WriteEventCore(MessageConsumedId, 7, data);
    }

    /// <summary>
    /// An operation started: its identity, the process's clock reading at its start, and its name; its parent, where it has
    /// one, is the event's related activity, <paramref name="relatedActivityId"/> (none where it is the empty identity).
    /// </summary>
    [Event(OperationStartedId, Level = EventLevel.Informational, Opcode = EventOpcode.Start, Version = EventVersion, ActivityOptions = EventActivityOptions.Disable)]
    public unsafe void OperationStarted(Guid relatedActivityId, int processId, Guid operation, long sourceTicks, string name)
    {
        name ??= string.Empty;
        fixed (char* text = name)
        {
            EventData* data = stackalloc EventData[4];
            Set(data, 0, &processId, sizeof(int));
            Set(data, 1, &operation, sizeof(Guid));
            Set(data, 2, &sourceTicks, sizeof(long));
            Set(data, 3, text, (name.Length + 1) * sizeof(char));
            if (relatedActivityId == Guid.Empty)
            {
                WriteEventCore(OperationStartedId, 4, data);
            }
            else
            {
                WriteEventWithRelatedActivityIdCore(OperationStartedId, &relatedActivityId, 4, data);
            }
        }
    }

    /// <summary>
    /// An operation completed with <paramref name="status"/> - 0 its success, any other value a failure in the application's
    /// own code - and the process's clock reading at its completion.
    /// </summary>
    [Event(OperationCompletedId, Level = EventLevel.Informational, Opcode = EventOpcode.Stop, Version = EventVersion, ActivityOptions = EventActivityOptions.Disable)]
    public unsafe void OperationCompleted(int processId, Guid operation, long status, long sourceTicks)
    {
        EventData* data = stackalloc EventData[4];
        Set(data, 0, &processId, sizeof(int));
        Set(data, 1, &operation, sizeof(Guid));
        Set(data, 2, &status, sizeof(long));
        Set(data, 3, &sourceTicks, sizeof(long));
        WriteEventCore(OperationCompletedId, 4, data);
    }

    /// <summary>
    /// A batch published: <paramref name="count"/> consecutive messages of a channel's epoch from
    /// <paramref name="firstSequence"/> on, each committed where its readers can take it, their logical lengths and the bytes
    /// copied for them together (-1 where the publisher does not know them), and the process's clock reading at the commit.
    /// Each message is still its own sequence, published once.
    /// </summary>
    [Event(MessagesPublishedId, Level = EventLevel.Informational, Opcode = EventOpcode.Send, Version = EventVersion)]
    public unsafe void MessagesPublished(int processId, Guid channel, long epoch, long firstSequence, long count, long logicalLength, long copiedLength, long sourceTicks)
    {
        EventData* data = stackalloc EventData[8];
        Set(data, 0, &processId, sizeof(int));
        Set(data, 1, &channel, sizeof(Guid));
        Set(data, 2, &epoch, sizeof(long));
        Set(data, 3, &firstSequence, sizeof(long));
        Set(data, 4, &count, sizeof(long));
        Set(data, 5, &logicalLength, sizeof(long));
        Set(data, 6, &copiedLength, sizeof(long));
        Set(data, 7, &sourceTicks, sizeof(long));
        WriteEventCore(MessagesPublishedId, 8, data);
    }

    /// <summary>
    /// A batch consumed by one reader, <paramref name="consumer"/>: <paramref name="count"/> consecutive messages of a
    /// channel's epoch from <paramref name="firstSequence"/> on, their logical lengths together - one consumption of each.
    /// </summary>
    [Event(MessagesConsumedId, Level = EventLevel.Informational, Opcode = EventOpcode.Receive, Version = EventVersion)]
    public unsafe void MessagesConsumed(int processId, Guid channel, long epoch, long firstSequence, long count, long consumer, long logicalLength, long sourceTicks)
    {
        EventData* data = stackalloc EventData[8];
        Set(data, 0, &processId, sizeof(int));
        Set(data, 1, &channel, sizeof(Guid));
        Set(data, 2, &epoch, sizeof(long));
        Set(data, 3, &firstSequence, sizeof(long));
        Set(data, 4, &count, sizeof(long));
        Set(data, 5, &consumer, sizeof(long));
        Set(data, 6, &logicalLength, sizeof(long));
        Set(data, 7, &sourceTicks, sizeof(long));
        WriteEventCore(MessagesConsumedId, 8, data);
    }

    /// <summary>
    /// The content of the message at <paramref name="sequence"/> of a channel's epoch, as the application holds it: its whole
    /// bytes, raised right after its publication, which says its length, and only to a session that asks for
    /// <see cref="Keywords.Content"/> (ADR-107). A message's content is never said in part.
    /// </summary>
    [Event(MessageContentId, Level = EventLevel.Informational, Keywords = Keywords.Content, Version = EventVersion)]
    public void MessageContent(int processId, Guid channel, long epoch, long sequence, byte[] content) =>
        Content(processId, channel, epoch, sequence, content);

    /// <summary>
    /// Raises <see cref="MessageContent"/> from the bytes where the application holds them, copying none: the count of them,
    /// then the bytes, as the manifest declares a binary field sized by the field before it.
    /// </summary>
    [NonEvent]
    internal unsafe void Content(int processId, Guid channel, long epoch, long sequence, ReadOnlySpan<byte> content)
    {
        int size = content.Length;
        byte none = 0;
        fixed (byte* bytes = content)
        {
            EventData* data = stackalloc EventData[6];
            Set(data, 0, &processId, sizeof(int));
            Set(data, 1, &channel, sizeof(Guid));
            Set(data, 2, &epoch, sizeof(long));
            Set(data, 3, &sequence, sizeof(long));
            Set(data, 4, &size, sizeof(int));
            Set(data, 5, size == 0 ? &none : bytes, size);
            WriteEventCore(MessageContentId, 6, data);
        }
    }

    private static unsafe void Set(EventData* data, int index, void* value, int size)
    {
        data[index].DataPointer = (IntPtr)value;
        data[index].Size = size;
    }

    /// <summary>
    /// The provider's keywords (`contracts/sdk-v1.md` §2). Every event but a message's content has none, so every session
    /// enabling the provider hears it; a session hears content only by asking for <see cref="Content"/>.
    /// </summary>
    public static class Keywords
    {
        /// <summary>
        /// A message's content: the one event under it, raised only where a session asks for it and the application gives the
        /// message's bytes (ADR-107). A capture that keeps no content asks for every keyword but this one.
        /// </summary>
        public const EventKeywords Content = (EventKeywords)0x1;
    }

    /// <summary>
    /// The library's version, held apart from the provider's statics: a session listening when a process first uses the
    /// SDK enables the provider as <see cref="Log"/> is constructed, and the announcement it hears is made then.
    /// </summary>
    private static class LibraryVersion
    {
        public static readonly string Value = typeof(InterCatSdkEventSource).Assembly.GetName().Version?.ToString() ?? string.Empty;
    }
}
