using System.Runtime.InteropServices;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>What became of one cooperative message, as its records say (`contracts/cooperative-messages-v1.md` §2, ADR-104).</summary>
public enum CooperativeMessageState
{
    /// <summary>Published once, and consumed by at least one reader.</summary>
    Consumed = 1,

    /// <summary>Published once, and consumed by no reader as far as anything recorded says: open.</summary>
    Unconsumed = 2,

    /// <summary>Consumed, with no publication recorded: published before the capture, or its record lost.</summary>
    PublicationUnseen = 3,

    /// <summary>
    /// Published more than once, or consumed more than once by one reader: what the application said breaks the schema's
    /// word that a message is published once and consumed once a reader, so none of its records is chosen over another.
    /// </summary>
    Conflicting = 4,
}

/// <summary>One record of a cooperative message: the process that raised it, when, and the length it stated.</summary>
public sealed record CooperativeMessageRecord
{
    public required int ProcessId { get; init; }

    public required ProcessBinding Process { get; init; }

    /// <summary>The record's source reading.</summary>
    public required long Ticks { get; init; }

    /// <summary>Its session time, in nanoseconds; null when the record has none.</summary>
    public long? SessionNanoseconds { get; init; }

    /// <summary>The message's logical length as the record states it; null when the record lost it.</summary>
    public long? Length { get; init; }

    /// <summary>The raising process's own clock reading at it, as the record states it; null where it carries none.</summary>
    public long? SourceTicks { get; init; }

    /// <summary>The record, which names it in every generation that holds it.</summary>
    public required (uint Stream, uint Epoch, ulong Ordinal) Address { get; init; }
}

/// <summary>One reader's consumption of a cooperative message.</summary>
public sealed record CooperativeConsumption
{
    /// <summary>The reader, as the application numbers it.</summary>
    public required long Consumer { get; init; }

    public required CooperativeMessageRecord Record { get; init; }
}

/// <summary>
/// One cooperative message (ADR-104): its channel's epoch and sequence, its publication and each reader's consumption, as
/// the application said them through the SDK - each its own assertion, never an observation of the channel beneath.
/// </summary>
public sealed record CooperativeMessage
{
    public required Guid Channel { get; init; }

    public required long Epoch { get; init; }

    public required long Sequence { get; init; }

    /// <summary>Its first recorded publication; null where none was recorded.</summary>
    public CooperativeMessageRecord? Publication { get; init; }

    /// <summary>How many publications of it were recorded: one, or none, or - conflicting - more.</summary>
    public required int Publications { get; init; }

    /// <summary>The bytes its publisher copied for it, where the publisher said; null where it said it does not know.</summary>
    public long? CopiedLength { get; init; }

    /// <summary>Each reader's consumption, in the order recorded.</summary>
    public required IReadOnlyList<CooperativeConsumption> Consumptions { get; init; }

    public required CooperativeMessageState State { get; init; }
}

/// <summary>One channel's epoch, as its openings and its messages say it.</summary>
public sealed record CooperativeChannel
{
    public required Guid Channel { get; init; }

    public required long Epoch { get; init; }

    /// <summary>The name its first recorded opening gives it; null where no opening was recorded.</summary>
    public string? Name { get; init; }

    /// <summary>What its first recorded opening says it carries messages through (`contracts/sdk-v1.md` §2); null where none.</summary>
    public long? Kind { get; init; }

    /// <summary>How many openings of it were recorded, by any process.</summary>
    public required int Openings { get; init; }

    /// <summary>How many closings of it were recorded, by any process.</summary>
    public required int Closings { get; init; }

    public required int Messages { get; init; }

    public required int Consumed { get; init; }

    public required int Unconsumed { get; init; }

    public required int PublicationUnseen { get; init; }

    public required int Conflicting { get; init; }

    /// <summary>How many readers consumed any of its messages, by the numbers the application gives them.</summary>
    public required int Readers { get; init; }

    /// <summary>How many consumptions of its messages were recorded.</summary>
    public required long Consumptions { get; init; }

    /// <summary>The logical lengths its recorded publications state, summed.</summary>
    public required long PublishedBytes { get; init; }

    /// <summary>Where its messages begin in the index, in sequence order.</summary>
    internal int First { get; init; }
}

/// <summary>
/// The cooperative SDK's logical messages of one generation (ADR-104, M7): each publication and consumption the SDK's
/// provider raised, keyed by its channel's identity, epoch and sequence - one message, published once and consumed once by
/// each reader - and each channel's epoch with what its openings say of it. A record names a message only by values it
/// carries: one whose channel, epoch, sequence or reader the capture did not keep names none, and is counted apart rather
/// than keyed by a value never observed (R3). It is a derivation over published segments alone, rebuildable from retained
/// evidence (R20), and it changes no observation (R1).
/// </summary>
public sealed class CooperativeMessageIndex
{
    /// <summary>The rule that keys records into messages, named in every query identity that reads by it (R20).</summary>
    public const string Rule = "cooperative-message-v1";

    private readonly CooperativeMessage[] messages;

    private CooperativeMessageIndex(CooperativeMessage[] messages, CooperativeChannel[] channels, ProcessInstanceIndex processes, long withoutMessage)
    {
        this.messages = messages;
        Channels = channels;
        Processes = processes;
        WithoutMessage = withoutMessage;
    }

    /// <summary>Every channel's epoch, by identity and then epoch.</summary>
    public IReadOnlyList<CooperativeChannel> Channels { get; }

    public ProcessInstanceIndex Processes { get; }

    /// <summary>Channel and message records of the SDK that name no message or channel: one value they are keyed by was not kept.</summary>
    public long WithoutMessage { get; }

    /// <summary>Every message, by channel, epoch and sequence.</summary>
    public IReadOnlyList<CooperativeMessage> Messages => messages;

    /// <summary>A channel's messages, in sequence order.</summary>
    public IReadOnlyList<CooperativeMessage> MessagesOf(CooperativeChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new ArraySegment<CooperativeMessage>(messages, channel.First, channel.Messages);
    }

    /// <summary>
    /// Reads each SDK record's epoch, kind, sequence, reader, copied length and clock reading from
    /// <paramref name="fieldSegments"/>, keys the channel and message records of <paramref name="segments"/> into channels
    /// and messages, and binds each record's process.
    /// </summary>
    public static CooperativeMessageIndex Derive(
        IReadOnlyList<SegmentReaderV1> segments,
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        ProcessInstanceIndex processes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(fieldSegments);
        ArgumentNullException.ThrowIfNull(processes);
        Dictionary<RecordAddress, Carried> carried = ReadFields(fieldSegments, cancellationToken);
        var drafts = new Dictionary<(Guid Channel, long Epoch, long Sequence), Draft>();
        var openings = new Dictionary<(Guid Channel, long Epoch), Opening>();
        long withoutMessage = 0;
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            withoutMessage += Collect(segment, carried, processes, drafts, openings);
        }

        // Each message in its channel's epoch, by sequence; each channel's epoch from its messages and its openings.
        CooperativeMessage[] keyed =
        [
            .. drafts.OrderBy(draft => draft.Key.Channel).ThenBy(draft => draft.Key.Epoch).ThenBy(draft => draft.Key.Sequence)
                .Select(draft => draft.Value.Message(draft.Key.Channel, draft.Key.Epoch, draft.Key.Sequence)),
        ];
        var channels = new List<CooperativeChannel>();
        var seen = new HashSet<(Guid, long)>();
        for (int first = 0; first < keyed.Length;)
        {
            int end = first;
            while (end < keyed.Length && keyed[end].Channel == keyed[first].Channel && keyed[end].Epoch == keyed[first].Epoch)
            {
                end++;
            }

            (Guid, long) key = (keyed[first].Channel, keyed[first].Epoch);
            _ = seen.Add(key);
            channels.Add(Channel(key, openings.GetValueOrDefault(key), new ArraySegment<CooperativeMessage>(keyed, first, end - first), first));
            first = end;
        }

        // A channel opened with no message recorded is a channel of none.
        foreach (((Guid, long) key, Opening opening) in openings)
        {
            if (!seen.Contains(key))
            {
                channels.Add(Channel(key, opening, ArraySegment<CooperativeMessage>.Empty, keyed.Length));
            }
        }

        return new(
            keyed,
            [.. channels.OrderBy(channel => channel.Channel).ThenBy(channel => channel.Epoch)],
            processes,
            withoutMessage);
    }

    private static CooperativeChannel Channel((Guid Channel, long Epoch) key, Opening? opening, ArraySegment<CooperativeMessage> held, int first)
    {
        var readers = new HashSet<long>();
        long consumptions = 0;
        long bytes = 0;
        foreach (CooperativeMessage message in held)
        {
            foreach (CooperativeConsumption consumption in message.Consumptions)
            {
                _ = readers.Add(consumption.Consumer);
                consumptions++;
            }

            bytes += message.Publication?.Length ?? 0;
        }

        return new()
        {
            Channel = key.Channel,
            Epoch = key.Epoch,
            Name = opening?.Name,
            Kind = opening?.Kind,
            Openings = opening?.Openings ?? 0,
            Closings = opening?.Closings ?? 0,
            Messages = held.Count,
            Consumed = held.Count(message => message.State == CooperativeMessageState.Consumed),
            Unconsumed = held.Count(message => message.State == CooperativeMessageState.Unconsumed),
            PublicationUnseen = held.Count(message => message.State == CooperativeMessageState.PublicationUnseen),
            Conflicting = held.Count(message => message.State == CooperativeMessageState.Conflicting),
            Readers = readers.Count,
            Consumptions = consumptions,
            PublishedBytes = bytes,
            First = first,
        };
    }

    private static Dictionary<RecordAddress, Carried> ReadFields(IReadOnlyList<SegmentReaderV1> fieldSegments, CancellationToken cancellationToken)
    {
        var carried = new Dictionary<RecordAddress, Carried>();
        foreach (SegmentReaderV1 segment in fieldSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segment.Table != SegmentTableId.SourceFieldsV1)
            {
                throw new ArgumentException("A source-field segment must hold source-fields-v1.", nameof(fieldSegments));
            }

            SegmentColumnSlice codes = segment.Slice(SegmentColumnId.SourceField);
            bool opened = false;
            SegmentColumnSlice streams = default, epochs = default, ordinals = default, factHigh = default, factLow = default,
                values = default;
            for (int row = 0; row < segment.RowCount; row++)
            {
                var code = (SourceField)(ushort)codes.UnsignedAt(row)!.Value;
                if (code is not (SourceField.ChannelEpoch or SourceField.ChannelKind or SourceField.MessageSequence
                    or SourceField.ConsumerNumber or SourceField.CopiedLength or SourceField.SourceTicks))
                {
                    continue;
                }

                // Only a segment holding the SDK's fields opens the columns they need.
                if (!opened)
                {
                    streams = segment.Slice(SegmentColumnId.RawStreamId);
                    epochs = segment.Slice(SegmentColumnId.RawSourceEpoch);
                    ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
                    factHigh = segment.Slice(SegmentColumnId.FactKeyHigh);
                    factLow = segment.Slice(SegmentColumnId.FactKeyLow);
                    values = segment.Slice(SegmentColumnId.FieldValue);
                    opened = true;
                }

                var address = new RecordAddress(
                    (uint)streams.UnsignedAt(row)!.Value,
                    (uint)epochs.UnsignedAt(row)!.Value,
                    ordinals.UnsignedAt(row)!.Value,
                    new FactKey(factHigh.UnsignedAt(row)!.Value, factLow.UnsignedAt(row)!.Value));
                ref Carried fields = ref CollectionsMarshal.GetValueRefOrAddDefault(carried, address, out _);
                long? value = values.SignedAt(row);
                if (Repeated(fields, code))
                {
                    throw new InvalidDataException(
                        $"Observation {address} carries {code} in more than one source-field segment; a duplicated field could "
                        + "move its record to another message.");
                }

                fields = code switch
                {
                    SourceField.ChannelEpoch => fields with { Epoch = value },
                    SourceField.ChannelKind => fields with { Kind = value },
                    SourceField.MessageSequence => fields with { Sequence = value },
                    SourceField.ConsumerNumber => fields with { Consumer = value },
                    SourceField.CopiedLength => fields with { Copied = value },
                    _ => fields with { SourceTicks = value },
                };
            }
        }

        return carried;
    }

    private static bool Repeated(Carried fields, SourceField code) => code switch
    {
        SourceField.ChannelEpoch => fields.Epoch is not null,
        SourceField.ChannelKind => fields.Kind is not null,
        SourceField.MessageSequence => fields.Sequence is not null,
        SourceField.ConsumerNumber => fields.Consumer is not null,
        SourceField.CopiedLength => fields.Copied is not null,
        _ => fields.SourceTicks is not null,
    };

    private static long Collect(
        SegmentReaderV1 segment,
        Dictionary<RecordAddress, Carried> carried,
        ProcessInstanceIndex processes,
        Dictionary<(Guid Channel, long Epoch, long Sequence), Draft> drafts,
        Dictionary<(Guid Channel, long Epoch), Opening> openings)
    {
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        long without = 0;
        bool opened = false;
        SegmentColumnSlice ticks = default, streams = default, epochs = default, ordinals = default, factHigh = default,
            factLow = default, bytes = default, sessionTimes = default, identifiers = default;
        var owners = new RecordOwnerColumns(segment);
        for (int row = 0; row < segment.RowCount; row++)
        {
            if ((Mechanism)mechanisms.UnsignedAt(row)!.Value != Mechanism.ApplicationSdk)
            {
                continue;
            }

            // The content fixture's records share the mechanism, and its events are numbered from 1 too: a record is the
            // SDK's by its provider.
            (Guid provider, ushort eventId, _, _) = segment.SchemaOf(row);
            if (provider != CooperativeSdk.ProviderId
                || eventId is not (CooperativeSdk.ChannelOpened or CooperativeSdk.ChannelClosed or CooperativeSdk.MessagePublished
                    or CooperativeSdk.MessageConsumed))
            {
                continue;
            }

            // Only a segment holding the SDK's records opens the columns a message needs.
            if (!opened)
            {
                ticks = segment.Slice(SegmentColumnId.NativeTicks);
                streams = segment.Slice(SegmentColumnId.RawStreamId);
                epochs = segment.Slice(SegmentColumnId.RawSourceEpoch);
                ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
                factHigh = segment.Slice(SegmentColumnId.FactKeyHigh);
                factLow = segment.Slice(SegmentColumnId.FactKeyLow);
                bytes = segment.Slice(SegmentColumnId.ByteValue);
                sessionTimes = segment.Slice(SegmentColumnId.SessionRelativeTicks);
                identifiers = segment.Slice(SegmentColumnId.SourceIdentifier);
                opened = true;
            }

            var address = new RecordAddress(
                (uint)streams.UnsignedAt(row)!.Value,
                (uint)epochs.UnsignedAt(row)!.Value,
                ordinals.UnsignedAt(row)!.Value,
                new FactKey(factHigh.UnsignedAt(row)!.Value, factLow.UnsignedAt(row)!.Value));
            Carried fields = carried.GetValueOrDefault(address);
            if (identifiers.GuidAt(row) is not { } channel || fields.Epoch is not { } epoch
                || owners.At(row, Mechanism.ApplicationSdk) is not { } processId)
            {
                without++;
                continue;
            }

            if (eventId is CooperativeSdk.ChannelOpened or CooperativeSdk.ChannelClosed)
            {
                ref Opening? opening = ref CollectionsMarshal.GetValueRefOrAddDefault(openings, (channel, epoch), out _);
                opening ??= new();
                if (eventId == CooperativeSdk.ChannelOpened)
                {
                    // The first opening names the channel and says its kind; a later one adds its count.
                    if (opening.Openings++ == 0)
                    {
                        opening.Name = segment.TextValue(SegmentColumnId.ResourceName, row);
                        opening.Kind = fields.Kind;
                    }
                }
                else
                {
                    opening.Closings++;
                }

                continue;
            }

            bool consumed = eventId == CooperativeSdk.MessageConsumed;
            if (fields.Sequence is not { } sequence || (consumed && fields.Consumer is null))
            {
                without++;
                continue;
            }

            long at = ticks.SignedAt(row)!.Value;
            var record = new CooperativeMessageRecord
            {
                ProcessId = processId,
                Process = processes.Bind(processId, at, isLifecycleRecord: false),
                Ticks = at,
                SessionNanoseconds = sessionTimes.SignedAt(row),
                Length = bytes.SignedAt(row),
                SourceTicks = fields.SourceTicks,
                Address = (address.Stream, address.Epoch, address.Ordinal),
            };
            ref Draft? draft = ref CollectionsMarshal.GetValueRefOrAddDefault(drafts, (channel, epoch, sequence), out _);
            draft ??= new();
            if (consumed)
            {
                draft.Consumptions.Add(new() { Consumer = fields.Consumer!.Value, Record = record });
            }
            else
            {
                draft.Publications++;
                if (draft.Publication is null)
                {
                    draft.Publication = record;
                    draft.Copied = fields.Copied is { } copied && copied >= 0 ? copied : null;
                }
            }
        }

        return without;
    }

    private readonly record struct RecordAddress(uint Stream, uint Epoch, ulong Ordinal, FactKey FactKey);

    /// <summary>The SDK's source fields one record carries, each null where it carries none.</summary>
    private readonly record struct Carried(long? Epoch, long? Kind, long? Sequence, long? Consumer, long? Copied, long? SourceTicks);

    /// <summary>What a channel's epoch's openings and closings say, gathered as read.</summary>
    private sealed class Opening
    {
        public int Openings { get; set; }

        public int Closings { get; set; }

        public string? Name { get; set; }

        public long? Kind { get; set; }
    }

    /// <summary>One message's records, gathered as read.</summary>
    private sealed class Draft
    {
        public int Publications { get; set; }

        public CooperativeMessageRecord? Publication { get; set; }

        public long? Copied { get; set; }

        public List<CooperativeConsumption> Consumptions { get; } = [];

        public CooperativeMessage Message(Guid channel, long epoch, long sequence)
        {
            bool twice = Publications > 1
                || Consumptions.GroupBy(consumption => consumption.Consumer).Any(reader => reader.Count() > 1);
            return new()
            {
                Channel = channel,
                Epoch = epoch,
                Sequence = sequence,
                Publication = Publication,
                Publications = Publications,
                CopiedLength = Copied,
                Consumptions = [.. Consumptions],
                State = twice ? CooperativeMessageState.Conflicting
                    : Publications == 0 ? CooperativeMessageState.PublicationUnseen
                    : Consumptions.Count == 0 ? CooperativeMessageState.Unconsumed
                    : CooperativeMessageState.Consumed,
            };
        }
    }
}
