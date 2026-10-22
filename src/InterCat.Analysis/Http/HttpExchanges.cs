using System.Runtime.InteropServices;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>The four parts of an HTTP exchange, by WinINet's capture events 2001 to 2004 (ADR-037).</summary>
public enum HttpPart
{
    RequestHead = 0,
    RequestBody = 1,
    ResponseHead = 2,
    ResponseBody = 3,
}

/// <summary>
/// What the capture recorded of one part of an exchange: how many of its buffers, the bytes their records measured, and
/// whether they make the part whole - numbered from 0 in order, the first flagged first and the last flagged last.
/// </summary>
public readonly record struct HttpPartRecord(int Buffers, long Bytes, bool Whole)
{
    /// <summary>Whether any buffer of the part was recorded.</summary>
    public bool Recorded => Buffers > 0;
}

/// <summary>
/// One HTTP exchange (ADR-037): one use of an exchange number by its client process, from its first recorded buffer to its
/// last, with what was recorded of each of its four parts. It is read from the records' metadata and source fields alone,
/// never from their content, so it says how large each part was and whether it was recorded whole - never what it said.
/// </summary>
public sealed record HttpExchange
{
    /// <summary>The exchange's number: its client process's own count from 1, which names it only within that run.</summary>
    public required long Number { get; init; }

    public required int ProcessId { get; init; }

    public required ProcessBinding Process { get; init; }

    /// <summary>The source reading of its first recorded buffer.</summary>
    public required long FirstTicks { get; init; }

    /// <summary>The source reading of its last recorded buffer.</summary>
    public required long LastTicks { get; init; }

    /// <summary>Its first buffer's session time, in nanoseconds; null when the record has none.</summary>
    public long? FirstNanoseconds { get; init; }

    /// <summary>Its last buffer's session time, in nanoseconds; null when the record has none.</summary>
    public long? LastNanoseconds { get; init; }

    /// <summary>The session time of the buffer that ended its response, in nanoseconds; null when none was recorded or has none.</summary>
    public long? ResponseEndNanoseconds { get; init; }

    /// <summary>
    /// From its first recorded buffer to the one that ended its response, in nanoseconds: the time its client spent on it,
    /// as the client library held it. Null when the response's end was not recorded. WinINet can raise the empty buffer
    /// that ends a request body after the response has ended, so the last buffer of an exchange is not its end (ADR-037).
    /// </summary>
    public long? DurationNanoseconds => Ended && FirstNanoseconds is { } from && ResponseEndNanoseconds is { } to ? to - from : null;

    public required HttpPartRecord RequestHead { get; init; }

    public required HttpPartRecord RequestBody { get; init; }

    public required HttpPartRecord ResponseHead { get; init; }

    public required HttpPartRecord ResponseBody { get; init; }

    /// <summary>The first buffer's record, which names the exchange in every generation that holds it.</summary>
    public required (uint Stream, uint Epoch, ulong Ordinal) First { get; init; }

    /// <summary>How many buffer records it holds.</summary>
    public int Records => RequestHead.Buffers + RequestBody.Buffers + ResponseHead.Buffers + ResponseBody.Buffers;

    /// <summary>The bytes its request's records measured, head and body.</summary>
    public long RequestBytes => RequestHead.Bytes + RequestBody.Bytes;

    /// <summary>The bytes its response's records measured, head and body.</summary>
    public long ResponseBytes => ResponseHead.Bytes + ResponseBody.Bytes;

    /// <summary>
    /// Whether its request head and its response were recorded whole, and its request body too when it had one. WinINet
    /// raises no record for an empty request body (ADR-037), so a request without one is not missing a part.
    /// </summary>
    public bool Complete => RequestHead.Whole && ResponseHead.Whole && ResponseBody.Whole
        && (!RequestBody.Recorded || RequestBody.Whole);

    /// <summary>Whether its last recorded buffer ended its response: the buffer that ends an exchange was seen.</summary>
    public bool Ended => ResponseBody.Whole;

    public HttpPartRecord Part(HttpPart part) => part switch
    {
        HttpPart.RequestHead => RequestHead,
        HttpPart.RequestBody => RequestBody,
        HttpPart.ResponseHead => ResponseHead,
        HttpPart.ResponseBody => ResponseBody,
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };
}

/// <summary>One process's HTTP exchanges, as bound to one instance, or to none, under process binding.</summary>
public sealed record HttpExchangeGroup
{
    public required int ProcessId { get; init; }

    public required ProcessBinding Process { get; init; }

    public required int Exchanges { get; init; }

    /// <summary>How many of its exchanges were recorded whole (<see cref="HttpExchange.Complete"/>).</summary>
    public required int Complete { get; init; }

    public required long Records { get; init; }

    public required long RequestBytes { get; init; }

    public required long ResponseBytes { get; init; }

    /// <summary>Where the group's exchanges begin in the index, in reading order.</summary>
    internal int First { get; init; }
}

/// <summary>
/// The HTTP exchanges of one generation (ADR-037, M8): WinINet capture records grouped by the exchange number and the
/// raising process their source fields and headers name, a number's buffers told apart in time into its uses, and each use
/// bound to a process instance. It is a derivation over published segments alone, rebuildable from retained evidence (R20),
/// and it changes no observation (R1).
/// </summary>
/// <remarks>
/// An exchange number is its client process's own count from 1, so the same number and process ID can name two exchanges
/// in one session - a process ID used again, or WinINet loaded again. In time order, a buffer opens another use of its
/// number when it is a request head flagged first, or when the use so far already holds a buffer of the same part at the
/// same place, as a use whose request head was lost would (R22). Nothing else ends a use: WinINet raises the empty buffer
/// that ends a request body after its response has ended, so a use is not over when its response is.
/// </remarks>
public sealed partial class HttpExchangeIndex
{
    /// <summary>The rule that groups buffers into exchanges, named in every query identity that reads by it (R20).</summary>
    public const string GroupingRule = "http-exchange-v1";

    /// <summary>
    /// How far behind the latest buffer read a number's last use is kept, once its response has ended, for a later
    /// generation to extend it (<see cref="Extend"/>): WinINet raises the buffer that ends a request body after the
    /// response it belongs to, by about a delivery flush, far less than this. A later buffer of a number not kept is
    /// grouped again in full.
    /// </summary>
    public const long LateWindowNanoseconds = 15_000_000_000;

    private readonly HttpExchange[] exchanges;
    private readonly (int Segment, int Row)[][] records;

    /// <summary>
    /// The published identities of the segments and source-field segments the exchanges were read from, for an extension
    /// to tell they still begin a later generation's; null where nothing extends.
    /// </summary>
    private readonly (StoreDependency?[] Segments, StoreDependency?[] Fields)? read;

    /// <summary>What a later generation's buffers can still change, for <see cref="Extend"/>; null where nothing extends.</summary>
    private readonly ExchangeTail? tail;

    private HttpExchangeIndex(
        HttpExchange[] exchanges,
        (int Segment, int Row)[][] records,
        HttpExchangeGroup[] groups,
        IReadOnlyList<string?> segmentNames,
        ProcessInstanceIndex processes,
        long withoutExchange,
        (StoreDependency?[] Segments, StoreDependency?[] Fields)? read = null,
        ExchangeTail? tail = null)
    {
        this.exchanges = exchanges;
        this.records = records;
        this.read = read;
        this.tail = tail;
        Groups = groups;
        SegmentNames = segmentNames;
        Processes = processes;
        WithoutExchange = withoutExchange;
    }

    /// <summary>Every process's exchanges, bound instances in index order, then the unbound by process ID.</summary>
    public IReadOnlyList<HttpExchangeGroup> Groups { get; }

    /// <summary>The published names of the segments the exchanges were read from, in the order read.</summary>
    public IReadOnlyList<string?> SegmentNames { get; }

    public ProcessInstanceIndex Processes { get; }

    /// <summary>HTTP records whose source fields name no exchange, and so belong to none.</summary>
    public long WithoutExchange { get; }

    /// <summary>
    /// Reads every HTTP record's exchange, place and ends from <paramref name="fieldSegments"/>, groups the records of
    /// <paramref name="segments"/> into exchanges, and binds each exchange by its first buffer.
    /// </summary>
    public static HttpExchangeIndex Derive(
        IReadOnlyList<SegmentReaderV1> segments,
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        ProcessInstanceIndex processes,
        CancellationToken cancellationToken = default) =>
        Derive(segments, fieldSegments, processes, clock: null, cancellationToken);

    /// <summary>
    /// <see cref="Derive(IReadOnlyList{SegmentReaderV1}, IReadOnlyList{SegmentReaderV1}, ProcessInstanceIndex, CancellationToken)"/>,
    /// keeping, when <paramref name="clock"/> is the segments' clock, what a later generation's buffers can still change,
    /// so that generation's exchanges extend these (<see cref="Extend"/>) rather than group every buffer again.
    /// </summary>
    public static HttpExchangeIndex Derive(
        IReadOnlyList<SegmentReaderV1> segments,
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        ProcessInstanceIndex processes,
        SourceClockDescriptor? clock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(fieldSegments);
        ArgumentNullException.ThrowIfNull(processes);
        Dictionary<RecordAddress, Place> places = ReadPlaces(fieldSegments, cancellationToken);
        var buffers = new List<Buffer>();
        long withoutExchange = 0;
        for (int position = 0; position < segments.Count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            withoutExchange += Collect(segments[position], position, places, buffers);
        }

        // Each number's buffers in time, then by place: a use of the number is its buffers from one that opens it to the
        // next that opens another (ADR-037, R22).
        var drafts = new List<(HttpExchange Exchange, (int Segment, int Row)[] Records)>();
        var lastUses = new Dictionary<(int ProcessId, long Number), LastUse>();
        foreach (IGrouping<(int ProcessId, long Number), Buffer> number in buffers.GroupBy(buffer => (buffer.ProcessId, buffer.Number)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<Buffer>? last = null;
            int uses = 0;
            foreach ((List<Buffer> use, bool _) in Uses(number))
            {
                drafts.Add(Exchange(use, processes));
                last = use;
                uses++;
            }

            lastUses[number.Key] = new([.. last!], uses == 1);
        }

        ExchangeTail? kept = clock is { } known && processes.Clock == known.Id
            ? Kept(known, buffers.Count == 0 ? null : buffers.Max(buffer => buffer.Ticks), lastUses, [.. lastUses.Keys])
            : null;
        return Assemble(
            drafts,
            [.. segments.Select(segment => segment.Published?.Name)],
            processes,
            withoutExchange,
            kept is null ? null : (RpcCallIndex.IdentitiesOf(segments), RpcCallIndex.IdentitiesOf(fieldSegments)),
            kept);
    }

    /// <summary>
    /// The exchanges of <paramref name="segments"/>, extended from these rather than grouped again, when the segments and the
    /// source-field segments begin with every one these were grouped from, in the same order, as a live capture's next
    /// generation's do (I14). Only the added segments and fields are read. Each number the added buffers name is told
    /// apart again from its kept last use on, where it was kept - from its first buffer, where that use holds every buffer
    /// of the number, and otherwise only when every added buffer reads after that use began - or from nothing, where no
    /// buffer named it before; every other exchange stands, bound again to <paramref name="processes"/>. The result is
    /// exactly
    /// <see cref="Derive(IReadOnlyList{SegmentReaderV1}, IReadOnlyList{SegmentReaderV1}, ProcessInstanceIndex, SourceClockDescriptor?, CancellationToken)"/>'s
    /// over the same segments. Null, for a full grouping to answer, when a segment read here is missing or another (after a
    /// compaction or a retention), when an added buffer names a number read before and no longer kept, or reads before its
    /// number's last use began while an earlier use holds some of the number's buffers, or when an added source field names
    /// no added HTTP record: a record's fields are published with it, so such a field could move a buffer these grouped.
    /// </summary>
    public HttpExchangeIndex? Extend(
        IReadOnlyList<SegmentReaderV1> segments,
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        ProcessInstanceIndex processes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(fieldSegments);
        ArgumentNullException.ThrowIfNull(processes);
        if (tail is null || read is not { } before || processes.Clock != tail.Clock.Id)
        {
            return null;
        }

        StoreDependency?[] offered = RpcCallIndex.IdentitiesOf(segments);
        StoreDependency?[] offeredFields = RpcCallIndex.IdentitiesOf(fieldSegments);
        if (!RpcCallIndex.Begins(offered, before.Segments) || !RpcCallIndex.Begins(offeredFields, before.Fields))
        {
            return null;
        }

        Dictionary<RecordAddress, Place> places = ReadPlaces(
            [.. Enumerable.Range(before.Fields.Length, fieldSegments.Count - before.Fields.Length).Select(position => fieldSegments[position])],
            cancellationToken);
        var added = new List<Buffer>();
        var named = new HashSet<RecordAddress>();
        long withoutExchange = WithoutExchange;
        for (int position = before.Segments.Length; position < segments.Count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            withoutExchange += Collect(segments[position], position, places, added, named);
        }

        foreach (RecordAddress address in places.Keys)
        {
            if (!named.Contains(address))
            {
                return null;
            }
        }

        var drafts = new List<(HttpExchange Exchange, (int Segment, int Row)[] Records)>(exchanges.Length + added.Count);
        var lastUses = new Dictionary<(int ProcessId, long Number), LastUse>(tail.Open);
        var numbers = new HashSet<(int ProcessId, long Number)>(tail.Numbers);
        var continued = new HashSet<(uint Stream, uint Epoch, ulong Ordinal)>();
        foreach (IGrouping<(int ProcessId, long Number), Buffer> number in added.GroupBy(buffer => (buffer.ProcessId, buffer.Number)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<Buffer> buffers = number;
            bool whole = true;
            if (tail.Open.TryGetValue(number.Key, out LastUse? kept))
            {
                // A use that holds every buffer of its number is told apart again with the added ones from nothing, as a
                // full grouping would. Otherwise an earlier use holds some, and an added buffer read before the last use
                // began could belong to it, or split the uses otherwise than a full grouping would.
                if (!kept.Whole)
                {
                    foreach (Buffer buffer in number)
                    {
                        if (CompareBuffers(buffer, kept.Buffers[0]) <= 0)
                        {
                            return null;
                        }
                    }
                }

                Buffer first = kept.Buffers[0];
                _ = continued.Add((first.Address.Stream, first.Address.Epoch, first.Address.Ordinal));
                buffers = kept.Buffers.Concat(number);
                whole = kept.Whole;
            }
            else if (!numbers.Add(number.Key))
            {
                return null;
            }

            List<Buffer>? lastUse = null;
            int uses = 0;
            foreach ((List<Buffer> use, bool _) in Uses(buffers))
            {
                drafts.Add(Exchange(use, processes));
                lastUse = use;
                uses++;
            }

            lastUses[number.Key] = new([.. lastUse!], whole && uses == 1);
        }

        // Every other exchange stands, bound again: a later generation's lifecycle records can bind its first buffer to an
        // instance it did not have.
        for (int index = 0; index < exchanges.Length; index++)
        {
            HttpExchange exchange = exchanges[index];
            if (!continued.Contains(exchange.First))
            {
                drafts.Add((exchange with { Process = processes.Bind(exchange.ProcessId, exchange.FirstTicks, isLifecycleRecord: false) },
                    records[index]));
            }
        }

        long? latest = tail.LatestTicks;
        foreach (Buffer buffer in added)
        {
            latest = latest is { } newest && newest >= buffer.Ticks ? newest : buffer.Ticks;
        }

        return Assemble(
            drafts,
            [.. offered.Select(identity => identity!.Name)],
            processes,
            withoutExchange,
            (offered, offeredFields),
            Kept(tail.Clock, latest, lastUses, numbers));
    }

    /// <summary>
    /// What a later generation can still change: every number's last use whose response has not ended, or whose last buffer
    /// reads within <see cref="LateWindowNanoseconds"/> of the latest read, and every number read so far.
    /// </summary>
    private static ExchangeTail Kept(
        SourceClockDescriptor clock,
        long? latest,
        Dictionary<(int ProcessId, long Number), LastUse> lastUses,
        HashSet<(int ProcessId, long Number)> numbers)
    {
        long watermark = long.MinValue;
        if (latest is { } newest)
        {
            Int128 ticks = (Int128)LateWindowNanoseconds * clock.TicksPerSecond / 1_000_000_000;
            long window = ticks > long.MaxValue ? long.MaxValue : (long)ticks;
            watermark = newest < long.MinValue + window ? long.MinValue : newest - window;
        }

        var open = new Dictionary<(int ProcessId, long Number), LastUse>();
        foreach (((int ProcessId, long Number) key, LastUse use) in lastUses)
        {
            if (!PartOf(use.Buffers, HttpPart.ResponseBody).Whole || use.Buffers.Max(buffer => buffer.Ticks) >= watermark)
            {
                open[key] = use;
            }
        }

        return new(clock, latest, open, numbers);
    }

    /// <summary>The order a number's buffers are told apart in: by reading, then by place, then by ordinal.</summary>
    private static int CompareBuffers(Buffer left, Buffer right)
    {
        int compared = left.Ticks.CompareTo(right.Ticks);
        compared = compared != 0 ? compared : left.Sequence.CompareTo(right.Sequence);
        return compared != 0 ? compared : left.Address.Ordinal.CompareTo(right.Address.Ordinal);
    }

    /// <summary>
    /// Orders bound exchanges into their groups and counts each group: what a derivation does once it has told the uses
    /// apart and bound them, and a reader of an operation index once it has read and bound them.
    /// </summary>
    private static HttpExchangeIndex Assemble(
        IEnumerable<(HttpExchange Exchange, (int Segment, int Row)[] Records)> drafts,
        string?[] segmentNames,
        ProcessInstanceIndex processes,
        long withoutExchange,
        (StoreDependency?[] Segments, StoreDependency?[] Fields)? read = null,
        ExchangeTail? tail = null)
    {
        // Grouped by instance, the bound first in index order and then the unbound by process ID; each group's exchanges in
        // reading order, by first buffer and then by its record.
        (HttpExchange Exchange, (int Segment, int Row)[] Records)[] ordered =
        [
            .. drafts
                .OrderBy(draft => draft.Exchange.Process.IsBound ? 0 : 1)
                .ThenBy(draft => draft.Exchange.Process.IsBound ? draft.Exchange.Process.Instance : draft.Exchange.ProcessId)
                .ThenBy(draft => draft.Exchange.FirstTicks)
                .ThenBy(draft => draft.Exchange.First.Stream)
                .ThenBy(draft => draft.Exchange.First.Epoch)
                .ThenBy(draft => draft.Exchange.First.Ordinal),
        ];
        var groups = new List<HttpExchangeGroup>();
        for (int start = 0; start < ordered.Length;)
        {
            HttpExchange first = ordered[start].Exchange;
            int end = start + 1;
            while (end < ordered.Length && SameGroup(ordered[end].Exchange, first))
            {
                end++;
            }

            ArraySegment<(HttpExchange Exchange, (int Segment, int Row)[] Records)> members = new(ordered, start, end - start);
            groups.Add(new()
            {
                ProcessId = first.ProcessId,
                Process = first.Process,
                Exchanges = members.Count,
                Complete = members.Count(member => member.Exchange.Complete),
                Records = members.Sum(member => (long)member.Exchange.Records),
                RequestBytes = members.Aggregate(0L, (sum, member) => SegmentMeasurement.AddBytes(sum, member.Exchange.RequestBytes)),
                ResponseBytes = members.Aggregate(0L, (sum, member) => SegmentMeasurement.AddBytes(sum, member.Exchange.ResponseBytes)),
                First = start,
            });
            start = end;
        }

        return new(
            [.. ordered.Select(draft => draft.Exchange)],
            [.. ordered.Select(draft => draft.Records)],
            [.. groups],
            segmentNames,
            processes,
            withoutExchange,
            read,
            tail);
    }

    /// <summary>
    /// One number's uses, in time and then by place (ADR-037, R22): a buffer opens another use when it is a request head
    /// flagged first, or when the use so far already holds a buffer of its part at its place. Each use says whether it was
    /// opened by such a repetition, which only the use before it decides.
    /// </summary>
    private static IEnumerable<(List<Buffer> Use, bool Repeated)> Uses(IEnumerable<Buffer> number)
    {
        var use = new List<Buffer>();
        var held = new HashSet<(HttpPart, long)>();
        bool repeated = false;
        foreach (Buffer buffer in number.OrderBy(buffer => buffer.Ticks).ThenBy(buffer => buffer.Sequence).ThenBy(buffer => buffer.Address.Ordinal))
        {
            bool opens = buffer.Part == HttpPart.RequestHead && (buffer.Flags & 1) != 0;
            if (use.Count > 0 && (opens || !held.Add((buffer.Part, buffer.Sequence))))
            {
                yield return (use, repeated);
                repeated = !opens;
                use = [];
                held.Clear();
                held.Add((buffer.Part, buffer.Sequence));
            }
            else if (use.Count == 0)
            {
                held.Add((buffer.Part, buffer.Sequence));
            }

            use.Add(buffer);
        }

        yield return (use, repeated);
    }

    /// <summary>The exchanges bound to <paramref name="instance"/>; null when it made none.</summary>
    public HttpExchangeGroup? GroupOf(ProcessInstanceId instance) =>
        Groups.FirstOrDefault(group => group.Process.IsBound && Processes.Instances[group.Process.Instance].Id == instance);

    /// <summary>The group's exchanges in reading order, from <paramref name="offset"/>, at most <paramref name="limit"/>.</summary>
    public IReadOnlyList<HttpExchange> ExchangesOf(HttpExchangeGroup group, int offset = 0, int limit = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        int from = group.First + Math.Min(offset, group.Exchanges);
        int to = group.First + (int)Math.Min((long)offset + limit, group.Exchanges);
        return new ArraySegment<HttpExchange>(exchanges, from, to - from);
    }

    /// <summary>Where in its group the exchange whose first buffer is the named record is; null when the group has none.</summary>
    public int? PositionOf(HttpExchangeGroup group, uint stream, uint epoch, ulong ordinal)
    {
        ArgumentNullException.ThrowIfNull(group);
        for (int index = 0; index < group.Exchanges; index++)
        {
            if (exchanges[group.First + index].First == (stream, epoch, ordinal))
            {
                return index;
            }
        }

        return null;
    }

    /// <summary>
    /// The records of one exchange of the group, or of all of them when <paramref name="position"/> is null, by segment
    /// position and row in the segments the index was read from.
    /// </summary>
    public IEnumerable<(int Segment, int Row)> RecordsOf(HttpExchangeGroup group, int? position = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (position is { } one)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(one);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(one, group.Exchanges);
            return records[group.First + one];
        }

        return records.Skip(group.First).Take(group.Exchanges).SelectMany(exchange => exchange);
    }

    private static bool SameGroup(HttpExchange candidate, HttpExchange first) =>
        candidate.Process.IsBound == first.Process.IsBound
        && (first.Process.IsBound ? candidate.Process.Instance == first.Process.Instance : candidate.ProcessId == first.ProcessId);

    private static (HttpExchange Exchange, (int Segment, int Row)[] Records) Exchange(List<Buffer> use, ProcessInstanceIndex processes)
    {
        Buffer first = use[0];
        Buffer last = use.MaxBy(buffer => buffer.Ticks)!;
        Buffer? end = use.LastOrDefault(buffer => buffer.Part == HttpPart.ResponseBody && (buffer.Flags & 2) != 0);
        return (new HttpExchange
        {
            Number = first.Number,
            ProcessId = first.ProcessId,
            Process = processes.Bind(first.ProcessId, first.Ticks, isLifecycleRecord: false),
            FirstTicks = first.Ticks,
            LastTicks = last.Ticks,
            FirstNanoseconds = first.SessionNanoseconds,
            LastNanoseconds = last.SessionNanoseconds,
            ResponseEndNanoseconds = end?.SessionNanoseconds,
            RequestHead = PartOf(use, HttpPart.RequestHead),
            RequestBody = PartOf(use, HttpPart.RequestBody),
            ResponseHead = PartOf(use, HttpPart.ResponseHead),
            ResponseBody = PartOf(use, HttpPart.ResponseBody),
            First = (first.Address.Stream, first.Address.Epoch, first.Address.Ordinal),
        }, [.. use.Select(buffer => (buffer.Segment, buffer.Row))]);
    }

    /// <summary>A part's buffers, whole when numbered from 0 in order with the first flagged first and the last flagged last.</summary>
    private static HttpPartRecord PartOf(IEnumerable<Buffer> use, HttpPart part)
    {
        Buffer[] buffers = [.. use.Where(buffer => buffer.Part == part).OrderBy(buffer => buffer.Sequence)];
        bool whole = buffers.Length > 0
            && (buffers[0].Flags & 1) != 0
            && (buffers[^1].Flags & 2) != 0;
        for (int index = 0; whole && index < buffers.Length; index++)
        {
            whole = buffers[index].Sequence == index;
        }

        return new(buffers.Length, buffers.Aggregate(0L, (sum, buffer) => SegmentMeasurement.AddBytes(sum, buffer.Bytes)), whole);
    }

    /// <summary>
    /// Reads each HTTP record's exchange, place and ends, a column at a time. A field repeated across source-field segments
    /// refuses the derivation, since a duplicated field could move a buffer to another exchange.
    /// </summary>
    private static Dictionary<RecordAddress, Place> ReadPlaces(
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        CancellationToken cancellationToken)
    {
        var places = new Dictionary<RecordAddress, Place>();
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
                if (code is not (SourceField.HttpExchangeId or SourceField.ContentBufferSequence or SourceField.ContentBufferFlags))
                {
                    continue;
                }

                // Only a segment holding HTTP fields opens the columns they need.
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
                ref Place place = ref CollectionsMarshal.GetValueRefOrAddDefault(places, address, out _);
                long? value = values.SignedAt(row);
                bool repeated = code switch
                {
                    SourceField.HttpExchangeId => place.Number is not null,
                    SourceField.ContentBufferSequence => place.Sequence is not null,
                    _ => place.Flags is not null,
                };
                if (repeated)
                {
                    throw new InvalidDataException(
                        $"Observation {address} carries {code} in more than one source-field segment; a duplicated field "
                        + "could move its buffer to another exchange.");
                }

                place = code switch
                {
                    SourceField.HttpExchangeId => place with { Number = value },
                    SourceField.ContentBufferSequence => place with { Sequence = value },
                    _ => place with { Flags = value },
                };
            }
        }

        return places;
    }

    /// <summary>
    /// Collects a segment's HTTP records that name an exchange; returns how many named none. Every HTTP record's address is
    /// added to <paramref name="named"/>, when one is given.
    /// </summary>
    private static long Collect(
        SegmentReaderV1 segment,
        int position,
        Dictionary<RecordAddress, Place> places,
        List<Buffer> buffers,
        HashSet<RecordAddress>? named = null)
    {
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        long without = 0;
        bool opened = false;
        SegmentColumnSlice ticks = default, streams = default, epochs = default, ordinals = default,
            factHigh = default, factLow = default, bytes = default, sessionTimes = default;
        var owners = new RecordOwnerColumns(segment);
        for (int row = 0; row < segment.RowCount; row++)
        {
            if ((Mechanism)mechanisms.UnsignedAt(row)!.Value != Mechanism.Http)
            {
                continue;
            }

            // Only a segment holding HTTP records opens the columns an exchange needs.
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
                opened = true;
            }

            var address = new RecordAddress(
                (uint)streams.UnsignedAt(row)!.Value,
                (uint)epochs.UnsignedAt(row)!.Value,
                ordinals.UnsignedAt(row)!.Value,
                new FactKey(factHigh.UnsignedAt(row)!.Value, factLow.UnsignedAt(row)!.Value));
            _ = named?.Add(address);
            ushort eventId = segment.SchemaOf(row).EventId;
            if (eventId is < 2001 or > 2004
                || !places.TryGetValue(address, out Place place) || place.Number is not { } number
                || owners.At(row, Mechanism.Http) is not { } processId)
            {
                without++;
                continue;
            }

            buffers.Add(new(
                address,
                (HttpPart)(eventId - 2001),
                number,
                place.Sequence ?? long.MaxValue,
                place.Flags ?? 0,
                ticks.SignedAt(row)!.Value,
                sessionTimes.SignedAt(row),
                bytes.SignedAt(row) ?? 0,
                processId,
                position,
                row));
        }

        return without;
    }

    private readonly record struct RecordAddress(uint Stream, uint Epoch, ulong Ordinal, FactKey FactKey);

    private readonly record struct Place(long? Number, long? Sequence, long? Flags);

    /// <summary>
    /// What a later generation's buffers can still change: the clock its window is read on, the latest buffer read, each
    /// kept number's last use, and every number read so far, kept or not.
    /// </summary>
    private sealed record ExchangeTail(
        SourceClockDescriptor Clock,
        long? LatestTicks,
        Dictionary<(int ProcessId, long Number), LastUse> Open,
        HashSet<(int ProcessId, long Number)> Numbers);

    /// <summary>A number's last use, its buffers in reading order, and whether it holds every buffer of the number.</summary>
    private sealed record LastUse(Buffer[] Buffers, bool Whole);

    private sealed record Buffer(
        RecordAddress Address,
        HttpPart Part,
        long Number,
        long Sequence,
        long Flags,
        long Ticks,
        long? SessionNanoseconds,
        long Bytes,
        int ProcessId,
        int Segment,
        int Row);
}
