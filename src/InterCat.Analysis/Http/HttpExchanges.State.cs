using InterCat.Storage;

namespace InterCat.Analysis;

public sealed partial class HttpExchangeIndex
{
    /// <summary>An exchange's bytes before its times and parts: its number, PID, first buffer's origin and ordinal, and readings.</summary>
    private const int ExchangeHeadBytes = 8 + 4 + 4 + 8 + 8 + 8 + 1;

    /// <summary>A part's bytes: its buffers, their bytes, and whether they make it whole.</summary>
    private const int PartBytes = 4 + 8 + 1;

    private static readonly HttpPart[] Parts = [HttpPart.RequestHead, HttpPart.RequestBody, HttpPart.ResponseHead, HttpPart.ResponseBody];

    /// <summary>How many exchanges the index holds.</summary>
    internal int Count => exchanges.Length;

    /// <summary>
    /// The index's exchanges in the order of their first buffers - reading, then raw locator - which no process binding
    /// changes: the order an operation index holds them in, at each place the exchange's position in the index.
    /// </summary>
    internal int[] InCanonicalOrder()
    {
        int[] order = [.. Enumerable.Range(0, exchanges.Length)];
        Array.Sort(order, (left, right) => CompareFirstBuffers(exchanges[left], exchanges[right]));
        return order;
    }

    /// <summary>How many bytes <see cref="WriteState"/> writes.</summary>
    internal long StateBytes()
    {
        int origins = exchanges.Select(exchange => (exchange.First.Stream, exchange.First.Epoch)).Distinct().Count();
        long bytes = 8 + 4 + (8L * origins) + 4;
        for (int index = 0; index < exchanges.Length; index++)
        {
            HttpExchange exchange = exchanges[index];
            bytes += ExchangeHeadBytes
                + (exchange.FirstNanoseconds is null ? 0 : 8)
                + (exchange.LastNanoseconds is null ? 0 : 8)
                + (exchange.ResponseEndNanoseconds is null ? 0 : 8)
                + (4 * PartBytes)
                + 4 + (8L * records[index].Length);
        }

        return bytes;
    }

    /// <summary>
    /// Writes the exchanges as `contracts/operation-index-v1.md` §3 holds them from minor 1: the HTTP records that named
    /// no exchange, the origins of the exchanges' first buffers, and each exchange in the order of its first buffer, with
    /// its parts and its buffers' places, without its process binding, which a reader binds again.
    /// </summary>
    internal void WriteState(IndexFileWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        int[] order = InCanonicalOrder();
        writer.I64(WithoutExchange);
        var numbered = new Dictionary<(uint Stream, uint Epoch), int>();
        foreach (int index in order)
        {
            _ = numbered.TryAdd((exchanges[index].First.Stream, exchanges[index].First.Epoch), numbered.Count);
        }

        writer.Count(numbered.Count);
        foreach ((uint stream, uint epoch) in numbered.Keys)
        {
            writer.U32(stream);
            writer.U32(epoch);
        }

        writer.Count(order.Length);
        foreach (int index in order)
        {
            HttpExchange exchange = exchanges[index];
            writer.I64(exchange.Number);
            writer.I32(exchange.ProcessId);
            writer.I32(numbered[(exchange.First.Stream, exchange.First.Epoch)]);
            writer.U64(exchange.First.Ordinal);
            writer.I64(exchange.FirstTicks);
            writer.I64(exchange.LastTicks);
            writer.U8((byte)((exchange.FirstNanoseconds is null ? 0 : 1)
                | (exchange.LastNanoseconds is null ? 0 : 2)
                | (exchange.ResponseEndNanoseconds is null ? 0 : 4)));
            foreach (long? nanoseconds in new[] { exchange.FirstNanoseconds, exchange.LastNanoseconds, exchange.ResponseEndNanoseconds })
            {
                if (nanoseconds is { } value)
                {
                    writer.I64(value);
                }
            }

            foreach (HttpPart part in Parts)
            {
                HttpPartRecord recorded = exchange.Part(part);
                writer.I32(recorded.Buffers);
                writer.I64(recorded.Bytes);
                writer.Flag(recorded.Whole);
            }

            writer.Count(records[index].Length);
            foreach ((int segment, int row) in records[index])
            {
                writer.I32(segment);
                writer.I32(row);
            }
        }
    }

    /// <summary>
    /// Reads the exchanges <see cref="WriteState"/> wrote, binds each to <paramref name="processes"/> by its first buffer's
    /// reading as a grouping binds it, and orders and counts them as a grouping does, so the index answers as the one
    /// written. Anything the bytes cannot hold, or hold in contradiction, is refused (operation-index-v1 §4).
    /// </summary>
    internal static HttpExchangeIndex ReadState(
        IndexFileReader reader,
        ProcessInstanceIndex processes,
        string[] segmentNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(segmentNames);
        long withoutExchange = reader.I64();
        if (withoutExchange < 0)
        {
            throw reader.Invalid("it counts fewer than no HTTP records without an exchange.");
        }

        var origins = new (uint Stream, uint Epoch)[reader.Count(8)];
        var known = new HashSet<(uint, uint)>();
        for (int index = 0; index < origins.Length; index++)
        {
            origins[index] = (reader.U32(), reader.U32());
            if (!known.Add(origins[index]))
            {
                throw reader.Invalid("an origin of exchanges is named twice.");
            }
        }

        // The smallest exchange holds its head, its four parts and its count of buffers.
        var drafts = new (HttpExchange Exchange, (int Segment, int Row)[] Records)[reader.Count(ExchangeHeadBytes + (4 * PartBytes) + 4)];
        for (int position = 0; position < drafts.Length; position++)
        {
            if ((position & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            drafts[position] = ReadExchange(reader, position, origins, segmentNames.Length, processes);
            if (position > 0 && CompareFirstBuffers(drafts[position - 1].Exchange, drafts[position].Exchange) >= 0)
            {
                throw reader.Invalid($"exchange {position:N0} is not in the order of its first buffer.");
            }
        }

        return Assemble(drafts, segmentNames, processes, withoutExchange);
    }

    private static (HttpExchange Exchange, (int Segment, int Row)[] Records) ReadExchange(
        IndexFileReader reader,
        int position,
        (uint Stream, uint Epoch)[] origins,
        int segments,
        ProcessInstanceIndex processes)
    {
        long number = reader.I64();
        int processId = reader.I32();
        int originAt = reader.I32();
        if (originAt < 0 || originAt >= origins.Length)
        {
            throw reader.Invalid($"exchange {position:N0} names no origin the index holds.");
        }

        ulong ordinal = reader.U64();
        long firstTicks = reader.I64();
        long lastTicks = reader.I64();
        byte times = reader.U8();
        if (lastTicks < firstTicks || (times & ~7) != 0)
        {
            throw reader.Invalid($"exchange {position:N0} ends before it begins, or holds an undefined time.");
        }

        long? firstNanoseconds = (times & 1) != 0 ? reader.I64() : null;
        long? lastNanoseconds = (times & 2) != 0 ? reader.I64() : null;
        long? responseEndNanoseconds = (times & 4) != 0 ? reader.I64() : null;
        var parts = new HttpPartRecord[Parts.Length];
        long buffers = 0;
        for (int part = 0; part < parts.Length; part++)
        {
            parts[part] = new(reader.I32(), reader.I64(), reader.Flag());
            if (parts[part].Buffers < 0 || parts[part].Bytes < 0 || (parts[part].Whole && parts[part].Buffers == 0))
            {
                throw reader.Invalid($"exchange {position:N0} holds a part its buffers could not make.");
            }

            buffers += parts[part].Buffers;
        }

        // An exchange is at least one buffer, each a record of the index's segments.
        var records = new (int Segment, int Row)[reader.Count(8)];
        if (records.Length == 0 || records.Length != buffers)
        {
            throw reader.Invalid($"exchange {position:N0} holds {records.Length:N0} buffers, and its parts {buffers:N0}.");
        }

        for (int index = 0; index < records.Length; index++)
        {
            records[index] = (reader.I32(), reader.I32());
            if (records[index].Segment < 0 || records[index].Segment >= segments || records[index].Row < 0)
            {
                throw reader.Invalid($"exchange {position:N0} names a buffer in no segment the index holds.");
            }
        }

        (uint stream, uint epoch) = origins[originAt];
        return (new HttpExchange
        {
            Number = number,
            ProcessId = processId,
            Process = processes.Bind(processId, firstTicks, isLifecycleRecord: false),
            FirstTicks = firstTicks,
            LastTicks = lastTicks,
            FirstNanoseconds = firstNanoseconds,
            LastNanoseconds = lastNanoseconds,
            ResponseEndNanoseconds = responseEndNanoseconds,
            RequestHead = parts[0],
            RequestBody = parts[1],
            ResponseHead = parts[2],
            ResponseBody = parts[3],
            First = (stream, epoch, ordinal),
        }, records);
    }

    /// <summary>Two exchanges by their first buffers: reading, then raw stream, epoch and ordinal.</summary>
    private static int CompareFirstBuffers(HttpExchange one, HttpExchange other)
    {
        int order = one.FirstTicks.CompareTo(other.FirstTicks);
        order = order != 0 ? order : one.First.Stream.CompareTo(other.First.Stream);
        order = order != 0 ? order : one.First.Epoch.CompareTo(other.First.Epoch);
        return order != 0 ? order : one.First.Ordinal.CompareTo(other.First.Ordinal);
    }
}
