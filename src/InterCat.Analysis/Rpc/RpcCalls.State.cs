using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

public sealed partial class RpcCallIndex
{
    /// <summary>A call's bytes before its records: its first record's origin and ordinal, PID, side, state and flags.</summary>
    private const int CallHeadBytes = 4 + 8 + 4 + 1 + 1 + 1;

    /// <summary>An origin's bytes: the stream, epoch and fact key calls' first records share.</summary>
    private const int OriginBytes = 4 + 4 + 8 + 8;

    /// <summary>A start's bytes: the interface it named, its reading, segment and row.</summary>
    private const int StartBytes = 4 + 8 + 4 + 4;

    /// <summary>A stop's bytes: its reading, segment and row.</summary>
    private const int StopBytes = 8 + 4 + 4;

    /// <summary>
    /// The index's calls in the canonical order of their first records (§3): at each place in that order, the call's
    /// position in the index. It is the order an operation index holds them in, which no process binding changes.
    /// </summary>
    internal int[] InCanonicalOrder()
    {
        int[] ranks = new int[entries.Length];
        int[] order = new int[entries.Length];
        for (int index = 0; index < entries.Length; index++)
        {
            ranks[index] = entries[index].FirstRank;
            order[index] = index;
        }

        Array.Sort(ranks, order);
        return order;
    }

    /// <summary>How many bytes <see cref="WriteState"/> writes.</summary>
    internal long StateBytes()
    {
        int origins = entries.Select(entry => OriginOf(entry.FirstAddress)).Distinct().Count();
        long bytes = 8 + 8 + 4 + (16L * interfaces.Length) + 4 + ((long)OriginBytes * origins) + 4;
        foreach (Entry entry in entries)
        {
            bytes += CallHeadBytes
                + (entry.StartSegment >= 0 ? StartBytes : 0)
                + (entry.StopSegment >= 0 ? StopBytes : 0)
                + (entry.HasProcedure ? 8 : 0)
                + (entry.HasProtocol ? 8 : 0)
                + (entry.HasStatus ? 8 : 0);
        }

        return bytes;
    }

    /// <summary>
    /// Writes the calls as `contracts/operation-index-v1.md` §3 holds them: the records counted, the interfaces named, the
    /// origins their first records share, and each call in <paramref name="order"/> - the canonical order of
    /// <see cref="InCanonicalOrder"/> - without its process binding, which a reader binds again.
    /// </summary>
    internal void WriteState(IndexFileWriter writer, int[] order)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(order);
        writer.I64(CallRecords);
        writer.I64(OtherRpcRecords);
        writer.Count(interfaces.Length);
        foreach (Guid uuid in interfaces)
        {
            writer.Identity(uuid);
        }

        // A capture's call records share a few streams and epochs and one fact key, so each is written once, in the order
        // the calls first name them, and a call names its own by number.
        var numbered = new Dictionary<Origin, int>();
        foreach (int index in order)
        {
            _ = numbered.TryAdd(OriginOf(entries[index].FirstAddress), numbered.Count);
        }

        writer.Count(numbered.Count);
        foreach (Origin origin in numbered.Keys)
        {
            writer.U32(origin.Stream);
            writer.U32(origin.Epoch);
            writer.U64(origin.FactKey.High);
            writer.U64(origin.FactKey.Low);
        }

        writer.Count(order.Length);
        foreach (int index in order)
        {
            Entry entry = entries[index];
            writer.I32(numbered[OriginOf(entry.FirstAddress)]);
            writer.U64(entry.FirstAddress.Ordinal);
            writer.I32(entry.ProcessId);
            writer.U8((byte)entry.Side);
            writer.U8((byte)entry.State);
            writer.U8((byte)FlagsOf(entry));
            if (entry.StartSegment >= 0)
            {
                writer.I32(entry.Interface);
                writer.I64(entry.StartTicks);
                writer.I32(entry.StartSegment);
                writer.I32(entry.StartRow);
            }

            if (entry.StopSegment >= 0)
            {
                writer.I64(entry.StopTicks);
                writer.I32(entry.StopSegment);
                writer.I32(entry.StopRow);
            }

            if (entry.HasProcedure)
            {
                writer.I64(entry.Procedure);
            }

            if (entry.HasProtocol)
            {
                writer.I64(entry.Protocol);
            }

            if (entry.HasStatus)
            {
                writer.I64(entry.Status);
            }
        }
    }

    /// <summary>
    /// Reads the calls <see cref="WriteState"/> wrote, binds each to <paramref name="processes"/> by its first record's
    /// reading as a derivation binds it, and groups them as a derivation does, so the index answers as the one written.
    /// Anything the bytes cannot hold, or hold in contradiction of the pairing rule, is refused (operation-index-v1 §4).
    /// </summary>
    /// <param name="placed">Where each call read, by its place in the canonical order, is in the index returned.</param>
    internal static RpcCallIndex ReadState(
        IndexFileReader reader,
        ProcessInstanceIndex processes,
        SourceClockDescriptor clock,
        string[] segmentNames,
        CancellationToken cancellationToken,
        out int[] placed)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(segmentNames);
        long callRecords = reader.I64();
        long otherRecords = reader.I64();
        if (callRecords < 0 || otherRecords < 0)
        {
            throw reader.Invalid("it counts fewer than no RPC records.");
        }

        var interfaces = new Guid[reader.Count(16)];
        var named = new HashSet<Guid>();
        for (int index = 0; index < interfaces.Length; index++)
        {
            interfaces[index] = reader.Identity();
            if (!named.Add(interfaces[index]))
            {
                throw reader.Invalid($"interface {interfaces[index]} is named twice.");
            }
        }

        var origins = new Origin[reader.Count(OriginBytes)];
        var known = new HashSet<Origin>();
        for (int index = 0; index < origins.Length; index++)
        {
            origins[index] = new(reader.U32(), reader.U32(), new FactKey(reader.U64(), reader.U64()));
            if (!known.Add(origins[index]))
            {
                throw reader.Invalid("an origin of call records is named twice.");
            }
        }

        // The smallest call holds its head and one stop.
        var drafted = new Entry[reader.Count(CallHeadBytes + StopBytes)];
        long records = 0;
        for (int position = 0; position < drafted.Length; position++)
        {
            if ((position & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Entry entry = ReadEntry(reader, position, origins, interfaces.Length, segmentNames.Length);
            if (position > 0 && CompareFirstRecords(drafted[position - 1], entry) > 0)
            {
                throw reader.Invalid($"call {position:N0} is not in the canonical order of its first record.");
            }

            records += (entry.StartSegment >= 0 ? 1 : 0) + (entry.StopSegment >= 0 ? 1 : 0);
            drafted[position] = entry;
        }

        if (records != callRecords)
        {
            throw reader.Invalid($"its calls hold {records:N0} records, and it counts {callRecords:N0}.");
        }

        placed = new int[drafted.Length];
        return Assemble(drafted, processes, interfaces, segmentNames, clock, callRecords, otherRecords, cancellationToken, placed);
    }

    private static Entry ReadEntry(IndexFileReader reader, int position, Origin[] origins, int interfaces, int segments)
    {
        int originAt = reader.I32();
        if (originAt < 0 || originAt >= origins.Length)
        {
            throw reader.Invalid($"call {position:N0} names no origin the index holds.");
        }

        Origin origin = origins[originAt];
        ulong ordinal = reader.U64();
        int processId = reader.I32();
        var side = (RpcCallSide)reader.U8();
        var state = (RpcCallState)reader.U8();
        var flags = (StateFlags)reader.U8();
        bool start = (flags & StateFlags.Start) != 0;
        bool stop = (flags & StateFlags.Stop) != 0;

        // A call is its start and its stop, or one record alone: a start still open, or a stop without its start, or a
        // record that pairs with nothing. Only a start carries fields, and only a stop a status (operations-v1 §4).
        if (!Enum.IsDefined(side)
            || !Enum.IsDefined(state)
            || (flags & ~StateFlags.All) != 0
            || (state == RpcCallState.Completed ? !(start && stop) : start == stop)
            || (state == RpcCallState.OpenAtCaptureEnd && !start)
            || (state == RpcCallState.StartNotObserved && !stop)
            || ((flags & (StateFlags.Procedure | StateFlags.Protocol)) != 0 && !start)
            || ((flags & StateFlags.Status) != 0 && !stop))
        {
            throw reader.Invalid($"call {position:N0} holds a state its records contradict.");
        }

        // Each field is read once into its place, and the call is made once from them: an index holds millions of calls.
        int rpcInterface = -1;
        (long Ticks, int Segment, int Row) opened = (0, -1, -1);
        if (start)
        {
            rpcInterface = reader.I32();
            opened = (reader.I64(), reader.I32(), reader.I32());
            if (rpcInterface < -1 || rpcInterface >= interfaces || !IsRecord(opened.Segment, opened.Row, segments))
            {
                throw reader.Invalid($"call {position:N0}'s start names no interface or segment the index holds.");
            }
        }

        (long Ticks, int Segment, int Row) closed = (0, -1, -1);
        if (stop)
        {
            closed = (reader.I64(), reader.I32(), reader.I32());
            if (!IsRecord(closed.Segment, closed.Row, segments) || (start && closed.Ticks < opened.Ticks))
            {
                throw reader.Invalid($"call {position:N0}'s stop names no segment the index holds, or is read before its start.");
            }
        }

        bool hasProcedure = (flags & StateFlags.Procedure) != 0;
        long procedure = hasProcedure ? reader.I64() : 0;
        bool hasProtocol = (flags & StateFlags.Protocol) != 0;
        long protocol = hasProtocol ? reader.I64() : 0;
        bool hasStatus = (flags & StateFlags.Status) != 0;
        long status = hasStatus ? reader.I64() : 0;
        return new()
        {
            FirstAddress = new RecordAddress(origin.Stream, origin.Epoch, ordinal, origin.FactKey),
            FirstRank = position,
            ProcessId = processId,
            Side = side,
            State = state,
            Interface = rpcInterface,
            Procedure = procedure,
            HasProcedure = hasProcedure,
            Protocol = protocol,
            HasProtocol = hasProtocol,
            StartTicks = opened.Ticks,
            StartSegment = opened.Segment,
            StartRow = opened.Row,
            StopTicks = closed.Ticks,
            StopSegment = closed.Segment,
            StopRow = closed.Row,
            Status = status,
            HasStatus = hasStatus,
        };
    }

    private static bool IsRecord(int segment, int row, int segments) => segment >= 0 && segment < segments && row >= 0;

    /// <summary>Two calls by their first records' canonical order (§3): reading, then raw locator, then fact key.</summary>
    private static int CompareFirstRecords(in Entry one, in Entry other)
    {
        int order = one.FirstTicks.CompareTo(other.FirstTicks);
        order = order != 0 ? order : one.FirstAddress.Stream.CompareTo(other.FirstAddress.Stream);
        order = order != 0 ? order : one.FirstAddress.Epoch.CompareTo(other.FirstAddress.Epoch);
        order = order != 0 ? order : one.FirstAddress.Ordinal.CompareTo(other.FirstAddress.Ordinal);
        order = order != 0 ? order : one.FirstAddress.FactKey.High.CompareTo(other.FirstAddress.FactKey.High);
        return order != 0 ? order : one.FirstAddress.FactKey.Low.CompareTo(other.FirstAddress.FactKey.Low);
    }

    private static Origin OriginOf(RecordAddress address) => new(address.Stream, address.Epoch, address.FactKey);

    private static StateFlags FlagsOf(in Entry entry) =>
        (entry.StartSegment >= 0 ? StateFlags.Start : StateFlags.None)
        | (entry.StopSegment >= 0 ? StateFlags.Stop : StateFlags.None)
        | (entry.HasProcedure ? StateFlags.Procedure : StateFlags.None)
        | (entry.HasProtocol ? StateFlags.Protocol : StateFlags.None)
        | (entry.HasStatus ? StateFlags.Status : StateFlags.None);

    /// <summary>What the first records of calls share: their raw stream and epoch, and their fact key.</summary>
    private readonly record struct Origin(uint Stream, uint Epoch, FactKey FactKey);

    /// <summary>What a call holds in an operation index (operation-index-v1 §3).</summary>
    [Flags]
    private enum StateFlags : byte
    {
        None = 0,
        Start = 1,
        Stop = 2,
        Procedure = 4,
        Protocol = 8,
        Status = 16,
        All = Start | Stop | Procedure | Protocol | Status,
    }
}
