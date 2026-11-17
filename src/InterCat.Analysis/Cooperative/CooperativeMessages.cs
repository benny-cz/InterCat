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

    /// <summary>
    /// The logical length the record states: its message's, or - a batch - its messages' together, never split among them;
    /// null when the record lost it.
    /// </summary>
    public long? Length { get; init; }

    /// <summary>
    /// How many consecutive messages the record said together, from its first sequence on: a batch (ADR-106); null where it
    /// said one message alone.
    /// </summary>
    public long? Batch { get; init; }

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

    /// <summary>
    /// How long after its message's publication was said this consumption was said, in session time (ADR-110): negative
    /// where the reader said it first, which the schema allows, each record said after its moment - so never the message's
    /// latency. Null where the message was not published exactly once, or a record has no session time.
    /// </summary>
    public long? LagNanoseconds { get; init; }
}

/// <summary>
/// How long after their publications a channel epoch's consumptions were said (ADR-110): the least, the median and the
/// most, over each consumption record once, and how many of them were said before their publication was.
/// </summary>
public sealed record CooperativeLag(long LeastNanoseconds, long MedianNanoseconds, long MostNanoseconds, long Consumptions, long SaidBefore);

/// <summary>
/// One cooperative message (ADR-104): its channel's epoch and sequence, its publication and each reader's consumption, as
/// the application said them through the SDK - each its own assertion, never an observation of the channel beneath. Or a
/// run of consecutive messages said in batches (ADR-106), every one of which the same records say: a run is never spread
/// into messages no record tells apart.
/// </summary>
public sealed record CooperativeMessage
{
    public required Guid Channel { get; init; }

    public required long Epoch { get; init; }

    /// <summary>Its sequence, or a run's first.</summary>
    public required long Sequence { get; init; }

    /// <summary>How many consecutive sequences it stands for, from <see cref="Sequence"/> on: 1, or a run's length.</summary>
    public long Count { get; init; } = 1;

    /// <summary>Its first recorded publication; null where none was recorded.</summary>
    public CooperativeMessageRecord? Publication { get; init; }

    /// <summary>How many publications of it were recorded: one, or none, or - conflicting - more.</summary>
    public required int Publications { get; init; }

    /// <summary>
    /// The bytes its publisher copied for it, where the publisher said; null where it said it does not know, or said them for
    /// a batch together.
    /// </summary>
    public long? CopiedLength { get; init; }

    /// <summary>Each reader's consumption, in the order recorded.</summary>
    public required IReadOnlyList<CooperativeConsumption> Consumptions { get; init; }

    public required CooperativeMessageState State { get; init; }

    /// <summary>
    /// The record its publisher said its content in, whose bytes a capture keeping the SDK's content keeps (ADR-112): the one
    /// content record naming it, raised by the process instance that published it once; null where none was, and where more
    /// than one names it, none of them chosen.
    /// </summary>
    public CooperativeMessageRecord? Content { get; init; }

    /// <summary>How many content records name it and were raised by its one publication's process: none, one, or - none chosen - more.</summary>
    public int ContentRecords { get; init; }
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

    /// <summary>Its messages, each of a run counted.</summary>
    public required long Messages { get; init; }

    public required long Consumed { get; init; }

    public required long Unconsumed { get; init; }

    public required long PublicationUnseen { get; init; }

    public required long Conflicting { get; init; }

    /// <summary>How many readers consumed any of its messages, by the numbers the application gives them.</summary>
    public required int Readers { get; init; }

    /// <summary>How many consumptions of its messages were recorded.</summary>
    public required long Consumptions { get; init; }

    /// <summary>
    /// The logical lengths its publications state, summed: each message's first publication's, and each batch's together,
    /// once.
    /// </summary>
    public required long PublishedBytes { get; init; }

    /// <summary>How long after their publications its consumptions were said; null where none has a lag.</summary>
    public CooperativeLag? Lag { get; init; }

    /// <summary>Where its messages begin in the index, in sequence order.</summary>
    internal int First { get; init; }

    /// <summary>How many entries of the index it holds: its messages, a run one entry.</summary>
    internal int Rows { get; init; }
}

/// <summary>
/// The cooperative SDK's logical messages of one generation (ADR-104, M7): each publication and consumption the SDK's
/// provider raised, keyed by its channel's identity, epoch and sequence - one message, published once and consumed once by
/// each reader - and each channel's epoch with what its openings say of it. A record names a message only by values it
/// carries: one whose channel, epoch, sequence or reader the capture did not keep names none, and is counted apart rather
/// than keyed by a value never observed (R3). A batch's record says a run of consecutive messages (ADR-106), which is kept a
/// run: the sequence space of each channel's epoch is swept once, each stretch every record says alike one entry, so the
/// index grows with the records, never with the messages a batch says. It is a derivation over published segments alone,
/// rebuildable from retained evidence (R20), and it changes no observation (R1).
/// </summary>
public sealed class CooperativeMessageIndex
{
    /// <summary>
    /// The rule that keys records into messages, named in every query identity that reads by it (R20): since ADR-106 a batch
    /// is keyed too, as a run.
    /// </summary>
    public const string Rule = "cooperative-message-v2";

    /// <summary>
    /// The rule that binds a content record to the message it names (ADR-112, R20): the message of its channel's epoch and
    /// sequence, said alone and published once, by the process instance that raised its content.
    /// </summary>
    public const string ContentRule = "cooperative-message-content-v1";

    private readonly CooperativeMessage[] messages;

    private CooperativeMessageIndex(
        CooperativeMessage[] messages,
        CooperativeChannel[] channels,
        ProcessInstanceIndex processes,
        long withoutMessage,
        long contentApart)
    {
        this.messages = messages;
        Channels = channels;
        Processes = processes;
        WithoutMessage = withoutMessage;
        ContentApart = contentApart;
    }

    /// <summary>Every channel's epoch, by identity and then epoch.</summary>
    public IReadOnlyList<CooperativeChannel> Channels { get; }

    public ProcessInstanceIndex Processes { get; }

    /// <summary>Channel and message records of the SDK that name no message or channel: one value they are keyed by was not kept.</summary>
    public long WithoutMessage { get; }

    /// <summary>
    /// Content records of the SDK bound to no message (ADR-112): no message of their channel's epoch and sequence was said
    /// alone and published once by their own process instance. A content record is never a message of its own; one of
    /// several naming one message is that message's, which then chooses none.
    /// </summary>
    public long ContentApart { get; }

    /// <summary>Every message and run, by channel, epoch and sequence.</summary>
    public IReadOnlyList<CooperativeMessage> Messages => messages;

    /// <summary>A channel's messages, a run one entry, in sequence order.</summary>
    public IReadOnlyList<CooperativeMessage> MessagesOf(CooperativeChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new ArraySegment<CooperativeMessage>(messages, channel.First, channel.Rows);
    }

    /// <summary>
    /// Reads each SDK record's epoch, kind, sequence, reader, copied length and clock reading from
    /// <paramref name="fieldSegments"/>, keys the channel and message records of <paramref name="segments"/> into channels
    /// and messages, binds each record's process, and binds each content record to the message it names (ADR-112).
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
        var spans = new Dictionary<(Guid Channel, long Epoch), List<Span>>();
        var openings = new Dictionary<(Guid Channel, long Epoch), Opening>();
        var contents = new Dictionary<(Guid Channel, long Epoch), List<Said>>();
        long withoutMessage = 0;
        long ordinal = 0;
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            withoutMessage += Collect(segment, carried, processes, spans, openings, contents, ref ordinal);
        }

        // Each channel's epoch swept by sequence into its messages and runs, its content bound to the messages it names;
        // each from its messages and its openings.
        var keyed = new List<CooperativeMessage>();
        var channels = new List<CooperativeChannel>();
        long contentApart = 0;
        foreach (((Guid Channel, long Epoch) key, List<Span> held) in spans.OrderBy(pair => pair.Key.Channel).ThenBy(pair => pair.Key.Epoch))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int first = keyed.Count;
            Sweep(key, held, keyed);
            if (contents.Remove(key, out List<Said>? said))
            {
                contentApart += Bind(said, keyed, first);
            }

            channels.Add(Channel(key, openings.GetValueOrDefault(key), keyed.GetRange(first, keyed.Count - first), first));
        }

        // Content naming a channel's epoch of which no message was recorded names no message.
        contentApart += contents.Values.Sum(said => (long)said.Count);

        // A channel opened with no message recorded is a channel of none.
        foreach (((Guid, long) key, Opening opening) in openings)
        {
            if (!spans.ContainsKey(key))
            {
                channels.Add(Channel(key, opening, [], keyed.Count));
            }
        }

        return new(
            [.. keyed],
            [.. channels.OrderBy(channel => channel.Channel).ThenBy(channel => channel.Epoch)],
            processes,
            withoutMessage,
            contentApart);
    }

    /// <summary>
    /// Binds each content record of one channel's epoch, whose messages begin at <paramref name="first"/> in
    /// <paramref name="keyed"/>, to the message of its sequence (ADR-112): one said alone and published once, by the process
    /// instance that raised the content. A message several such records name takes none of them. Returns how many name no
    /// such message.
    /// </summary>
    private static long Bind(List<Said> said, List<CooperativeMessage> keyed, int first)
    {
        long apart = 0;
        var named = new Dictionary<int, List<CooperativeMessageRecord>>();
        foreach (Said content in said)
        {
            int at = EntryOf(keyed, first, content.Sequence);
            if (at < 0 || keyed[at] is not { Count: 1, Publications: 1, Publication: { Batch: null } publication }
                || publication.ProcessId != content.Record.ProcessId || publication.Process.Instance != content.Record.Process.Instance)
            {
                apart++;
                continue;
            }

            ref List<CooperativeMessageRecord>? records = ref CollectionsMarshal.GetValueRefOrAddDefault(named, at, out _);
            records ??= [];
            records.Add(content.Record);
        }

        foreach ((int at, List<CooperativeMessageRecord> records) in named)
        {
            keyed[at] = keyed[at] with { Content = records.Count == 1 ? records[0] : null, ContentRecords = records.Count };
        }

        return apart;
    }

    /// <summary>The entry of <paramref name="keyed"/> from <paramref name="first"/> on whose sequences hold <paramref name="sequence"/>, or -1.</summary>
    private static int EntryOf(List<CooperativeMessage> keyed, int first, long sequence)
    {
        int low = first, high = keyed.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            CooperativeMessage entry = keyed[middle];
            if (sequence < entry.Sequence)
            {
                high = middle - 1;
            }
            else if (sequence > entry.Sequence + (entry.Count - 1))
            {
                low = middle + 1;
            }
            else
            {
                return middle;
            }
        }

        return -1;
    }

    /// <summary>
    /// Sweeps one channel's epoch by sequence: every stretch its records say alike - covered by the same publications and
    /// consumptions - one entry, its state as one message's; a stretch no record covers is no message. A message said alone
    /// is a stretch of one; a batch's run is split only where another record begins or ends within it.
    /// </summary>
    private static void Sweep((Guid Channel, long Epoch) key, List<Span> spans, List<CooperativeMessage> keyed)
    {
        // The boundaries: each record's first sequence, and the sequence past its last - which past the last a long holds is
        // still a boundary.
        Int128[] bounds = [.. spans.SelectMany(span => new[] { (Int128)span.First, (Int128)span.Last + 1 }).Distinct().Order()];
        Span[] byFirst = [.. spans.OrderBy(span => span.First).ThenBy(span => span.Ordinal)];
        var active = new List<Span>();
        int next = 0;
        for (int at = 0; at + 1 < bounds.Length; at++)
        {
            Int128 from = bounds[at];
            _ = active.RemoveAll(span => span.Last < from);
            while (next < byFirst.Length && byFirst[next].First == from)
            {
                active.Add(byFirst[next++]);
            }

            if (active.Count > 0)
            {
                keyed.Add(Entry(key, (long)from, (long)(bounds[at + 1] - from), active));
            }
        }
    }

    /// <summary>
    /// One stretch's entry: its first recorded publication the one it names, its consumptions in the order recorded, and what
    /// became of it - conflicting where it was published twice or read twice by one reader, none of its records chosen.
    /// </summary>
    private static CooperativeMessage Entry((Guid Channel, long Epoch) key, long sequence, long count, List<Span> covering)
    {
        Span[] recorded = [.. covering.OrderBy(span => span.Ordinal)];
        Span[] publications = [.. recorded.Where(span => span.Consumer is null)];

        // A consumption's lag is from the one publication its message has; with none, or two, it has none (ADR-110).
        long? published = publications.Length == 1 ? publications[0].Record.SessionNanoseconds : null;
        CooperativeConsumption[] consumptions =
        [
            .. recorded.Where(span => span.Consumer is not null).Select(span => new CooperativeConsumption
            {
                Consumer = span.Consumer!.Value,
                Record = span.Record,
                LagNanoseconds = published is { } from && span.Record.SessionNanoseconds is { } to ? to - from : null,
            }),
        ];
        bool twice = publications.Length > 1 || consumptions.GroupBy(consumption => consumption.Consumer).Any(reader => reader.Count() > 1);
        Span? publication = publications.FirstOrDefault();
        return new()
        {
            Channel = key.Channel,
            Epoch = key.Epoch,
            Sequence = sequence,
            Count = count,
            Publication = publication?.Record,
            Publications = publications.Length,
            CopiedLength = publication is { Record.Batch: null } ? publication.Copied : null,
            Consumptions = consumptions,
            State = twice ? CooperativeMessageState.Conflicting
                : publications.Length == 0 ? CooperativeMessageState.PublicationUnseen
                : consumptions.Length == 0 ? CooperativeMessageState.Unconsumed
                : CooperativeMessageState.Consumed,
        };
    }

    private static CooperativeChannel Channel((Guid Channel, long Epoch) key, Opening? opening, List<CooperativeMessage> held, int first)
    {
        var readers = new HashSet<long>();
        var batches = new HashSet<CooperativeMessageRecord>(ReferenceEqualityComparer.Instance);
        var lagged = new HashSet<CooperativeMessageRecord>(ReferenceEqualityComparer.Instance);
        var lags = new List<long>();
        long consumptions = 0;
        long bytes = 0;
        foreach (CooperativeMessage message in held)
        {
            foreach (CooperativeConsumption consumption in message.Consumptions)
            {
                _ = readers.Add(consumption.Consumer);

                // Each consumption record's lag once, however its batch's run splits.
                if (consumption.LagNanoseconds is { } lag && lagged.Add(consumption.Record))
                {
                    lags.Add(lag);
                }
            }

            consumptions += message.Count * message.Consumptions.Count;

            // A message's length is its first publication's; a batch's, said together, is counted once however its run splits.
            if (message.Publication is { Batch: null } alone)
            {
                bytes += alone.Length ?? 0;
            }
            else if (message.Publication is { } batch && batches.Add(batch))
            {
                bytes += batch.Length ?? 0;
            }
        }

        long Of(CooperativeMessageState state) => held.Where(message => message.State == state).Sum(message => message.Count);
        lags.Sort();
        return new()
        {
            Channel = key.Channel,
            Epoch = key.Epoch,
            Name = opening?.Name,
            Kind = opening?.Kind,
            Openings = opening?.Openings ?? 0,
            Closings = opening?.Closings ?? 0,
            Messages = held.Sum(message => message.Count),
            Consumed = Of(CooperativeMessageState.Consumed),
            Unconsumed = Of(CooperativeMessageState.Unconsumed),
            PublicationUnseen = Of(CooperativeMessageState.PublicationUnseen),
            Conflicting = Of(CooperativeMessageState.Conflicting),
            Readers = readers.Count,
            Consumptions = consumptions,
            PublishedBytes = bytes,
            Lag = lags.Count == 0 ? null : new(lags[0], lags[lags.Count / 2], lags[^1], lags.Count, lags.Count(lag => lag < 0)),
            First = first,
            Rows = held.Count,
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
                    or SourceField.ConsumerNumber or SourceField.CopiedLength or SourceField.SourceTicks or SourceField.MessageCount))
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
                    SourceField.MessageCount => fields with { Count = value },
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
        SourceField.MessageCount => fields.Count is not null,
        _ => fields.SourceTicks is not null,
    };

    private static long Collect(
        SegmentReaderV1 segment,
        Dictionary<RecordAddress, Carried> carried,
        ProcessInstanceIndex processes,
        Dictionary<(Guid Channel, long Epoch), List<Span>> spans,
        Dictionary<(Guid Channel, long Epoch), Opening> openings,
        Dictionary<(Guid Channel, long Epoch), List<Said>> contents,
        ref long ordinal)
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
                    or CooperativeSdk.MessageConsumed or CooperativeSdk.MessagesPublished or CooperativeSdk.MessagesConsumed
                    or CooperativeSdk.MessageContent))
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

            // A message's content is an inventory of its bytes beside its publication (ADR-112): kept to be bound to the
            // message it names, never a message of its own.
            if (eventId == CooperativeSdk.MessageContent)
            {
                if (fields.Sequence is not { } named)
                {
                    without++;
                    continue;
                }

                long when = ticks.SignedAt(row)!.Value;
                ref List<Said>? said = ref CollectionsMarshal.GetValueRefOrAddDefault(contents, (channel, epoch), out _);
                said ??= [];
                said.Add(new(named, new CooperativeMessageRecord
                {
                    ProcessId = processId,
                    Process = processes.Bind(processId, when, isLifecycleRecord: false),
                    Ticks = when,
                    SessionNanoseconds = sessionTimes.SignedAt(row),
                    SourceTicks = fields.SourceTicks,
                    Address = (address.Stream, address.Epoch, address.Ordinal),
                }));
                continue;
            }

            // A batch says its run by its first sequence and its count; one whose count no SDK raises, or whose run would run
            // past the last sequence a long holds, names no message.
            bool consumed = eventId is CooperativeSdk.MessageConsumed or CooperativeSdk.MessagesConsumed;
            bool batch = eventId is CooperativeSdk.MessagesPublished or CooperativeSdk.MessagesConsumed;
            long count = batch ? fields.Count ?? 0 : 1;
            if (fields.Sequence is not { } sequence || (consumed && fields.Consumer is null)
                || count is < 1 or > CooperativeSdk.MaximumBatch || sequence > long.MaxValue - (count - 1))
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
                Batch = batch ? count : null,
                SourceTicks = fields.SourceTicks,
                Address = (address.Stream, address.Epoch, address.Ordinal),
            };
            ref List<Span>? held = ref CollectionsMarshal.GetValueRefOrAddDefault(spans, (channel, epoch), out _);
            held ??= [];
            held.Add(new(
                sequence,
                sequence + (count - 1),
                consumed ? fields.Consumer!.Value : null,
                record,
                fields.Copied is { } copied && copied >= 0 ? copied : null,
                ordinal++));
        }

        return without;
    }

    private readonly record struct RecordAddress(uint Stream, uint Epoch, ulong Ordinal, FactKey FactKey);

    /// <summary>The SDK's source fields one record carries, each null where it carries none.</summary>
    private readonly record struct Carried(long? Epoch, long? Kind, long? Sequence, long? Consumer, long? Copied, long? SourceTicks, long? Count);

    /// <summary>What a channel's epoch's openings and closings say, gathered as read.</summary>
    private sealed class Opening
    {
        public int Openings { get; set; }

        public int Closings { get; set; }

        public string? Name { get; set; }

        public long? Kind { get; set; }
    }

    /// <summary>
    /// One publication or consumption as read: the sequences it says, from its first to its last - one, or a batch's run -
    /// its reader where it is a consumption, its record, the bytes it says it copied, and the order it was read in.
    /// </summary>
    private sealed record Span(long First, long Last, long? Consumer, CooperativeMessageRecord Record, long? Copied, long Ordinal);

    /// <summary>One content record as read: the sequence of the message it names, and the record.</summary>
    private sealed record Said(long Sequence, CooperativeMessageRecord Record);
}
