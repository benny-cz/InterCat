using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using InterCat.Domain;
using InterCat.Storage;
using Microsoft.Win32.SafeHandles;

namespace InterCat.Analysis;

public sealed partial class RpcCallIndex
{
    /// <summary>
    /// How many bytes of paired calls a streamed pairing holds before it writes them, sorted, to a file of its own: some
    /// 190,000 calls (revision 882).
    /// </summary>
    public const int StreamedCallBytes = 16 * 1024 * 1024;

    /// <summary>How many bytes of each run of spilled calls a streamed pairing reads back at once.</summary>
    private const int SpillReadBytes = 64 * 1024;

    /// <summary>No source field: a streamed pairing joins its records' fields as it takes them.</summary>
    private static readonly Dictionary<RecordAddress, FieldSlot> NoFields = [];

    /// <summary>
    /// The calls of <paramref name="segments"/> as <see cref="Derive"/> pairs them, with the procedure and protocol
    /// <paramref name="fieldSegments"/> carry for their starts, paired as their records are taken rather than once every
    /// record is held (revision 882). The records are taken in canonical order across the segments: a segment is read
    /// once the order reaches its earliest reading and given up once its last record is taken, so only the records of
    /// segments whose readings overlap are held at once - a live capture's overlap by about a delivery flush. The source
    /// fields are joined as they are taken, in the same order. Each call is paired as
    /// its records come, so only the calls in flight are held, and each call paired is held until it is written, up to
    /// <paramref name="heldBytes"/> of them: past that, those held are written, sorted, to a file of their own under
    /// <paramref name="spillDirectory"/>, by default the system's temporary directory, which is deleted when it is closed.
    /// Null when the segments hold an ALPC send or receive, or a COM call record: those calls' other ends are followed over
    /// calls held whole (<see cref="RpcPeerIndex.Derive"/>).
    /// </summary>
    internal static StreamedCalls? StreamCalls(
        IReadOnlyList<SegmentReaderV1> segments,
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        int heldBytes,
        string? spillDirectory,
        CancellationToken cancellationToken) =>
        StreamedCalls.Pair(segments, fieldSegments, heldBytes, spillDirectory, cancellationToken);

    /// <summary>
    /// How many of a segment's records are ALPC sends or receives, or COM call records, whose calls a streamed pairing does
    /// not pair: the other ends of those calls are followed through them, over calls held whole.
    /// </summary>
    private static long CountAlpcMessages(SegmentReaderV1 segment)
    {
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        SegmentColumnSlice kinds = default;
        bool opened = false;
        long count = 0;
        for (int row = 0; row < segment.RowCount; row++)
        {
            var mechanism = (Mechanism)mechanisms.UnsignedAt(row)!.Value;
            if (mechanism == Mechanism.ComCall)
            {
                count++;
                continue;
            }

            if (mechanism != Mechanism.Alpc)
            {
                continue;
            }

            if (!opened)
            {
                kinds = segment.Slice(SegmentColumnId.ObservationKind);
                opened = true;
            }

            count += (ObservationKind)kinds.UnsignedAt(row)!.Value is ObservationKind.Send or ObservationKind.Receive ? 1 : 0;
        }

        return count;
    }

    /// <summary>
    /// The source fields of call starts and the ALPC message ids a source-field segment carries, read as a derivation reads
    /// them: a call start's field row whose value and availability disagree refuses the segment (`source-fields-v1`), and
    /// a message id without a value names none. Each row keeps the reading of the record it names, which it carries.
    /// </summary>
    private static List<FieldRow> FieldRowsOf(SegmentReaderV1 segment)
    {
        if (segment.Table != SegmentTableId.SourceFieldsV1)
        {
            throw new ArgumentException("A source-field segment must hold source-fields-v1.", nameof(segment));
        }

        SegmentColumnSlice codes = segment.Slice(SegmentColumnId.SourceField);
        var rows = new List<FieldRow>();
        bool opened = false;
        SegmentColumnSlice ticks = default;
        SegmentColumnSlice streams = default;
        SegmentColumnSlice epochs = default;
        SegmentColumnSlice ordinals = default;
        SegmentColumnSlice factHigh = default;
        SegmentColumnSlice factLow = default;
        SegmentColumnSlice values = default;
        SegmentColumnSlice availabilities = default;
        SegmentColumnPresence texts = default;
        for (int row = 0; row < segment.RowCount; row++)
        {
            var code = (SourceField)(ushort)codes.UnsignedAt(row)!.Value;
            if (code is not (SourceField.RpcProcedureNumber or SourceField.RpcProtocolSequence or SourceField.AlpcMessageId))
            {
                continue;
            }

            if (!opened)
            {
                ticks = segment.Slice(SegmentColumnId.NativeTicks);
                streams = segment.Slice(SegmentColumnId.RawStreamId);
                epochs = segment.Slice(SegmentColumnId.RawSourceEpoch);
                ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
                factHigh = segment.Slice(SegmentColumnId.FactKeyHigh);
                factLow = segment.Slice(SegmentColumnId.FactKeyLow);
                values = segment.Slice(SegmentColumnId.FieldValue);
                availabilities = segment.Slice(SegmentColumnId.FieldAvailability);
                texts = segment.Presence(SegmentColumnId.FieldText);
                opened = true;
            }

            long? value = values.SignedAt(row);
            if (code == SourceField.AlpcMessageId)
            {
                if (value is null)
                {
                    continue;
                }
            }
            else
            {
                bool text = texts.HasValue(row);
                var availability = (FieldAvailability)availabilities.UnsignedAt(row)!.Value;
                if (!Enum.IsDefined(availability)
                    || availability == FieldAvailability.NotApplicable
                    || (value is not null && text)
                    || (value is not null || text) != (availability == FieldAvailability.Present))
                {
                    throw new InvalidDataException(
                        $"Source-field row {row} is invalid: its {code} value and its availability ({availability}) "
                        + "disagree (source-fields-v1).");
                }
            }

            rows.Add(new(
                ticks.SignedAt(row)!.Value,
                new RecordAddress(
                    (uint)streams.UnsignedAt(row)!.Value,
                    (uint)epochs.UnsignedAt(row)!.Value,
                    ordinals.UnsignedAt(row)!.Value,
                    new FactKey(factHigh.UnsignedAt(row)!.Value, factLow.UnsignedAt(row)!.Value)),
                code,
                value ?? 0,
                value is not null));
        }

        Canonically(rows, CompareFields);
        return rows;
    }

    private static int CompareFields(FieldRow one, FieldRow other)
    {
        int order = CompareReadings(one.Ticks, one.Address, other.Ticks, other.Address);
        return order != 0 ? order : ((ushort)one.Code).CompareTo((ushort)other.Code);
    }

    /// <summary>
    /// Puts one segment's rows, read in row order, in canonical order: a segment holds them so (segment-v1 §4), which one
    /// pass confirms, and only a segment whose rows read at one instant are not in that order is sorted.
    /// </summary>
    private static void Canonically<T>(List<T> rows, Comparison<T> compare)
    {
        Span<T> span = CollectionsMarshal.AsSpan(rows);
        for (int index = 1; index < span.Length; index++)
        {
            if (compare(span[index - 1], span[index]) > 0)
            {
                span.Sort(compare);
                return;
            }
        }
    }

    /// <summary>
    /// A key with a call open in a streamed pairing: its first record and that record's rank, how many starts await their
    /// stop, and, once the id has been reused, every record of the run with its rank.
    /// </summary>
    private struct Pending(CallRecord first, int rank)
    {
        public CallRecord First = first;
        public int FirstRank = rank;
        public int Open = 1;
        public List<(int Rank, CallRecord Record)>? Run;
    }

    /// <summary>A call start's source field, or an ALPC record's message id: the record it names, by its reading and address, and its value.</summary>
    private readonly record struct FieldRow(long Ticks, RecordAddress Address, SourceField Code, long Value, bool HasValue);

    /// <summary>
    /// Rows of segments, each segment's in canonical order, taken in canonical order across them. A segment is read once
    /// the order reaches its earliest reading, which its reader verified on opening it, since none of its rows can come
    /// before one read earlier; and given up once its last row is taken. So only the segments whose readings overlap the
    /// rows being taken are held at once.
    /// </summary>
    private abstract class ReadingMerge<T>
        where T : struct
    {
        private readonly IReadOnlyList<SegmentReaderV1> segments;
        private readonly int[] byEarliest;
        private readonly List<T>?[] held;
        private readonly int[] taken;
        private readonly int[] heap;
        private int count;
        private int read;

        protected ReadingMerge(IReadOnlyList<SegmentReaderV1> segments)
        {
            this.segments = segments;
            byEarliest = [.. Enumerable.Range(0, segments.Count).OrderBy(position => segments[position].MinNativeTicks)];
            held = new List<T>?[segments.Count];
            taken = new int[segments.Count];
            heap = new int[segments.Count];
        }

        /// <summary>Takes the earliest row not taken yet, false when every row is.</summary>
        public bool TryTake(out T row)
        {
            if (!TryPeek(out row))
            {
                return false;
            }

            Skip();
            return true;
        }

        /// <summary>The rows of the segment at <paramref name="position"/> the merge takes, in canonical order.</summary>
        protected abstract List<T> Read(SegmentReaderV1 segment, int position);

        protected abstract int Compare(in T one, in T other);

        protected abstract long ReadingOf(in T row);

        /// <summary>The earliest row not taken yet, false when every row is.</summary>
        protected bool TryPeek(out T row)
        {
            while (read < byEarliest.Length
                && (count == 0 || segments[byEarliest[read]].MinNativeTicks <= ReadingOf(Head(heap[0]))))
            {
                int position = byEarliest[read++];
                List<T> rows = Read(segments[position], position);
                if (rows.Count > 0)
                {
                    held[position] = rows;
                    heap[count++] = position;
                    SiftUp(count - 1);
                }
            }

            if (count == 0)
            {
                row = default;
                return false;
            }

            row = Head(heap[0]);
            return true;
        }

        /// <summary>Takes the row <see cref="TryPeek"/> gave.</summary>
        protected void Skip()
        {
            int position = heap[0];
            if (++taken[position] == held[position]!.Count)
            {
                held[position] = null;
                heap[0] = heap[--count];
            }

            if (count > 0)
            {
                SiftDown(0);
            }
        }

        private ref readonly T Head(int position) => ref CollectionsMarshal.AsSpan(held[position])[taken[position]];

        /// <summary>Whether one segment's next row comes before another's: rows that compare alike by their segments' places.</summary>
        private bool Before(int one, int other)
        {
            int order = Compare(Head(one), Head(other));
            return order < 0 || (order == 0 && one < other);
        }

        private void SiftUp(int at)
        {
            while (at > 0)
            {
                int parent = (at - 1) / 2;
                if (!Before(heap[at], heap[parent]))
                {
                    return;
                }

                (heap[at], heap[parent]) = (heap[parent], heap[at]);
                at = parent;
            }
        }

        private void SiftDown(int at)
        {
            while (true)
            {
                int least = at;
                int left = (2 * at) + 1;
                if (left < count && Before(heap[left], heap[least]))
                {
                    least = left;
                }

                if (left + 1 < count && Before(heap[left + 1], heap[least]))
                {
                    least = left + 1;
                }

                if (least == at)
                {
                    return;
                }

                (heap[at], heap[least]) = (heap[least], heap[at]);
                at = least;
            }
        }
    }

    /// <summary>
    /// The call records of segments in canonical order, read by <see cref="Collect"/> as a derivation reads them, with what
    /// pairing them counts: the call records, the other RPC records, the ALPC sends and receives, and the interfaces the
    /// starts name, each at the first segment position and row that names it.
    /// </summary>
    private sealed class RecordMerge(IReadOnlyList<SegmentReaderV1> segments) : ReadingMerge<CallRecord>(segments)
    {
        private readonly List<Guid> interfaces = [];
        private readonly Dictionary<Guid, int> interfaceIndex = [];
        private readonly List<(int Position, int Row)> firstNamed = [];

        public long CallRecords { get; private set; }

        public long OtherRecords { get; private set; }

        public long AlpcMessages { get; private set; }

        /// <summary>
        /// The interfaces named, in the order a derivation reads them first - by segment position, then row - and, by the
        /// number each was read under here, its place in that order.
        /// </summary>
        public Guid[] InterfacesInReadingOrder(out int[] placed)
        {
            int[] order = [.. Enumerable.Range(0, interfaces.Count).OrderBy(index => firstNamed[index])];
            placed = new int[order.Length];
            for (int place = 0; place < order.Length; place++)
            {
                placed[order[place]] = place;
            }

            return [.. order.Select(index => interfaces[index])];
        }

        protected override List<CallRecord> Read(SegmentReaderV1 segment, int position)
        {
            // A segment's call records are read at once, and nothing after reads its columns.
            var records = new List<CallRecord>(CountRpcRecords([segment], CancellationToken.None));
            OtherRecords += Collect(segment, position, NoFields, interfaces, interfaceIndex, records);
            AlpcMessages += CountAlpcMessages(segment);
            _ = segment.GiveBackColumns();
            CallRecords += records.Count;
            foreach (CallRecord record in records)
            {
                if (record.IsStart && record.Interface >= 0)
                {
                    while (firstNamed.Count <= record.Interface)
                    {
                        firstNamed.Add((int.MaxValue, int.MaxValue));
                    }

                    if ((position, record.Row).CompareTo(firstNamed[record.Interface]) < 0)
                    {
                        firstNamed[record.Interface] = (position, record.Row);
                    }
                }
            }

            Canonically(records, static (one, other) => CompareRecords(one, other));
            return records;
        }

        protected override int Compare(in CallRecord one, in CallRecord other) => CompareRecords(one, other);

        protected override long ReadingOf(in CallRecord row) => row.Ticks;
    }

    /// <summary>
    /// The source fields of call starts and the ALPC message ids of source-field segments in canonical order, each joined
    /// to the call record it names as that record is taken: a field row carries its record's reading (segment-v1), so the
    /// two orders meet. A field named twice is refused, as a derivation refuses it: two rows naming one record with one
    /// meaning compare alike, so one is taken right after the other.
    /// </summary>
    private sealed class FieldMerge(IReadOnlyList<SegmentReaderV1> fieldSegments) : ReadingMerge<FieldRow>(fieldSegments)
    {
        private FieldRow last;
        private bool any;

        /// <summary>
        /// <paramref name="record"/>, with the procedure and protocol it carried when it is a call start; every field of a
        /// record before it, or of it, is checked and let go.
        /// </summary>
        public CallRecord Joined(in CallRecord record)
        {
            FieldSlot carried = default;
            while (TryPeek(out FieldRow field))
            {
                int order = CompareReadings(field.Ticks, field.Address, record.Ticks, record.Address);
                if (order > 0)
                {
                    break;
                }

                Skip();
                Check(field);
                if (order == 0 && record.IsStart && field.Code != SourceField.AlpcMessageId)
                {
                    _ = carried.Name(field.Code, field.HasValue ? field.Value : null);
                }
            }

            return !record.IsStart
                ? record
                : record with
                {
                    Procedure = carried.Procedure ?? 0,
                    Protocol = carried.Protocol ?? 0,
                    Flags = record.Flags
                        | (carried.Procedure is null ? CallFlags.None : CallFlags.Procedure)
                        | (carried.Protocol is null ? CallFlags.None : CallFlags.Protocol),
                };
        }

        /// <summary>Checks every field after the last record taken.</summary>
        public void Finish(CancellationToken cancellationToken)
        {
            for (int index = 0; TryTake(out FieldRow field); index++)
            {
                if ((index & 0xFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                Check(field);
            }
        }

        protected override List<FieldRow> Read(SegmentReaderV1 segment, int position)
        {
            List<FieldRow> rows = FieldRowsOf(segment);
            _ = segment.GiveBackColumns();
            return rows;
        }

        protected override int Compare(in FieldRow one, in FieldRow other) => CompareFields(one, other);

        protected override long ReadingOf(in FieldRow row) => row.Ticks;

        private void Check(in FieldRow field)
        {
            if (any && field.Code == last.Code && field.Ticks == last.Ticks && field.Address == last.Address)
            {
                throw new InvalidDataException(field.Code == SourceField.AlpcMessageId
                    ? $"Observation {field.Address} carries its ALPC message id in more than one source-field segment; a "
                        + "duplicated field could change which call it links."
                    : $"Observation {field.Address} carries {field.Code} in more than one source-field segment; a "
                        + "duplicated field could change what its call is.");
            }

            (last, any) = (field, true);
        }
    }

    /// <summary>
    /// The calls a streamed pairing paired (<see cref="StreamCalls"/>) and what an operation index's calls state holds of
    /// them, each call held until it is written: up to the bytes the pairing was given in memory, and past them in sorted
    /// runs of a file of its own, which is deleted once it is closed.
    /// </summary>
    internal sealed class StreamedCalls : IDisposable
    {
        private readonly int capacity;
        private readonly string? spillDirectory;
        private readonly List<(long Offset, int Count)> runs = [];
        private readonly Dictionary<Origin, int> originAt = [];
        private readonly List<Origin> origins = [];
        private readonly List<int> firstNaming = [];
        private HeldCall[] held = [];
        private int buffered;
        private SafeFileHandle? spill;
        private long spilledBytes;
        private long callBytes;
        private Guid[] interfaces = [];
        private int[] interfacePlace = [];
        private bool written;

        private StreamedCalls(int heldBytes, string? spillDirectory)
        {
            capacity = Math.Max(1, heldBytes / Unsafe.SizeOf<HeldCall>());
            this.spillDirectory = spillDirectory;
        }

        /// <summary>How many call records were read.</summary>
        public long CallRecords { get; private set; }

        /// <summary>How many RPC records were no call record.</summary>
        public long OtherRecords { get; private set; }

        /// <summary>How many calls were paired.</summary>
        public int Count { get; private set; }

        /// <summary>How many sorted runs of calls were written to the spill file; none when every call was held.</summary>
        public int Runs => runs.Count;

        /// <summary>The pairing <see cref="StreamCalls"/> describes.</summary>
        internal static StreamedCalls? Pair(
            IReadOnlyList<SegmentReaderV1> segments,
            IReadOnlyList<SegmentReaderV1> fieldSegments,
            int heldBytes,
            string? spillDirectory,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(segments);
            ArgumentNullException.ThrowIfNull(fieldSegments);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heldBytes);
            var records = new RecordMerge(segments);
            var fields = new FieldMerge(fieldSegments);
            var calls = new StreamedCalls(heldBytes, spillDirectory);
            try
            {
                // Every key's records pair as Walk pairs them, in canonical order: a start opens a call and the next stop
                // of its key closes it, and a start while its key has a call open makes every record of the key, from the
                // earlier start until as many stops as starts have been read, ambiguous.
                var open = new Dictionary<CallKey, Pending>();
                for (int rank = 0; records.TryTake(out CallRecord taken); rank++)
                {
                    if ((rank & 0xFFFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (records.AlpcMessages > 0)
                    {
                        calls.Dispose();
                        return null;
                    }

                    CallRecord record = fields.Joined(taken);
                    if (!record.HasActivity)
                    {
                        calls.Add(Single(record, RpcCallState.NoActivityId, rank));
                        continue;
                    }

                    CallKey key = KeyOf(record);
                    ref Pending state = ref CollectionsMarshal.GetValueRefOrNullRef(open, key);
                    if (Unsafe.IsNullRef(ref state))
                    {
                        if (record.IsStart)
                        {
                            open.Add(key, new Pending(record, rank));
                        }
                        else
                        {
                            calls.Add(Single(record, RpcCallState.StartNotObserved, rank));
                        }

                        continue;
                    }

                    if (record.IsStart || state.Run is not null)
                    {
                        (state.Run ??= [(state.FirstRank, state.First)]).Add((rank, record));
                    }

                    state.Open += record.IsStart ? 1 : -1;
                    if (state.Open > 0)
                    {
                        continue;
                    }

                    Pending closed = state;
                    _ = open.Remove(key);
                    if (closed.Run is { } run)
                    {
                        calls.AddAmbiguous(run);
                    }
                    else
                    {
                        calls.Add(Paired(closed.First, record, closed.FirstRank));
                    }
                }

                if (records.AlpcMessages > 0)
                {
                    calls.Dispose();
                    return null;
                }

                fields.Finish(cancellationToken);
                foreach (Pending still in open.Values)
                {
                    if (still.Run is { } run)
                    {
                        calls.AddAmbiguous(run);
                    }
                    else
                    {
                        calls.Add(Single(still.First, RpcCallState.OpenAtCaptureEnd, still.FirstRank));
                    }
                }

                calls.CallRecords = records.CallRecords;
                calls.OtherRecords = records.OtherRecords;
                calls.interfaces = records.InterfacesInReadingOrder(out calls.interfacePlace);
                return calls;
            }
            catch
            {
                calls.Dispose();
                throw;
            }
        }

        private void AddAmbiguous(List<(int Rank, CallRecord Record)> run)
        {
            foreach ((int rank, CallRecord record) in run)
            {
                Add(Single(record, RpcCallState.Ambiguous, rank));
            }
        }

        /// <summary>Holds one call paired, writing those held, sorted, to the spill file when they fill the bytes given.</summary>
        private void Add(in Entry call)
        {
            Origin origin = OriginOf(call.FirstAddress);
            if (!originAt.TryGetValue(origin, out int at))
            {
                at = origins.Count;
                originAt.Add(origin, at);
                origins.Add(origin);
                firstNaming.Add(call.FirstRank);
            }
            else if (call.FirstRank < firstNaming[at])
            {
                firstNaming[at] = call.FirstRank;
            }

            callBytes += CallBytes(call);
            Count = checked(Count + 1);
            if (buffered == held.Length)
            {
                if (held.Length < capacity)
                {
                    Array.Resize(ref held, (int)Math.Min(capacity, Math.Max(1024L, 2L * held.Length)));
                }
                else
                {
                    Spill();
                }
            }

            held[buffered++] = HeldCall.Of(call, at);
        }

        /// <summary>How many bytes <see cref="WriteState"/> writes.</summary>
        public long StateBytes() => HeadBytes(interfaces.Length, origins.Count) + callBytes;

        /// <summary>
        /// Writes the calls as <see cref="RpcCallIndex.WriteState"/> writes the calls a derivation paired from the same
        /// segments: the same records counted, interfaces, origins in the order the calls first name them, and calls in
        /// the canonical order of their first records. They are written once.
        /// </summary>
        public void WriteState(IndexFileWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);
            if (written)
            {
                throw new InvalidOperationException("These calls were written already.");
            }

            written = true;
            int[] byFirst = [.. Enumerable.Range(0, origins.Count).OrderBy(origin => firstNaming[origin])];
            int[] numbered = new int[byFirst.Length];
            for (int place = 0; place < byFirst.Length; place++)
            {
                numbered[byFirst[place]] = place;
            }

            WriteHead(writer, CallRecords, OtherRecords, interfaces, [.. byFirst.Select(origin => origins[origin])], Count);
            foreach (HeldCall call in InRankOrder())
            {
                WriteCall(writer, numbered[call.Origin], call.ToEntry(origins[call.Origin], interfacePlace));
            }
        }

        public void Dispose()
        {
            spill?.Dispose();
            spill = null;
        }

        private static int ByRank(HeldCall one, HeldCall other) => one.FirstRank.CompareTo(other.FirstRank);

        /// <summary>The calls by the ranks of their first records: those held, sorted, or every run merged.</summary>
        private IEnumerable<HeldCall> InRankOrder()
        {
            if (runs.Count == 0)
            {
                Array.Sort(held, 0, buffered, Comparer<HeldCall>.Create(ByRank));
                for (int index = 0; index < buffered; index++)
                {
                    yield return held[index];
                }

                yield break;
            }

            if (buffered > 0)
            {
                Spill();
            }

            held = [];
            int perRead = Math.Max(1, SpillReadBytes / Unsafe.SizeOf<HeldCall>());
            var cursors = new RunCursor[runs.Count];
            var next = new PriorityQueue<int, int>(runs.Count);
            for (int run = 0; run < runs.Count; run++)
            {
                cursors[run] = new RunCursor(runs[run].Offset, runs[run].Count, new HeldCall[Math.Min(perRead, runs[run].Count)]);
                Fill(cursors[run]);
                next.Enqueue(run, cursors[run].Current.FirstRank);
            }

            while (next.TryDequeue(out int run, out _))
            {
                RunCursor cursor = cursors[run];
                yield return cursor.Current;
                if (++cursor.At == cursor.Filled)
                {
                    if (cursor.Left == 0)
                    {
                        continue;
                    }

                    Fill(cursor);
                }

                next.Enqueue(run, cursor.Current.FirstRank);
            }
        }

        /// <summary>Writes the calls held, sorted by the ranks of their first records, as one run of the spill file.</summary>
        private void Spill()
        {
            Array.Sort(held, 0, buffered, Comparer<HeldCall>.Create(ByRank));
            if (spill is null)
            {
                string directory = spillDirectory ?? Path.Combine(Path.GetTempPath(), "InterCat.Checkpoint");
                _ = Directory.CreateDirectory(directory);
                spill = File.OpenHandle(
                    Path.Combine(directory, $"calls-{Guid.NewGuid():N}.run"),
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    FileOptions.DeleteOnClose);
            }

            RandomAccess.Write(spill, MemoryMarshal.AsBytes(held.AsSpan(0, buffered)), spilledBytes);
            runs.Add((spilledBytes, buffered));
            spilledBytes += (long)buffered * Unsafe.SizeOf<HeldCall>();
            buffered = 0;
        }

        private void Fill(RunCursor cursor)
        {
            int take = Math.Min(cursor.Left, cursor.Buffer.Length);
            Span<byte> bytes = MemoryMarshal.AsBytes(cursor.Buffer.AsSpan(0, take));
            for (int done = 0; done < bytes.Length;)
            {
                int read = RandomAccess.Read(spill!, bytes[done..], cursor.Offset + done);
                if (read == 0)
                {
                    throw new IOException("The spilled calls ended before every call written there was read back.");
                }

                done += read;
            }

            cursor.Offset += bytes.Length;
            cursor.Left -= take;
            cursor.Filled = take;
            cursor.At = 0;
        }

        /// <summary>Where a merge is in one run of the spill file: the calls read back and not yet taken, and where the rest are.</summary>
        private sealed class RunCursor(long offset, int count, HeldCall[] buffer)
        {
            public long Offset = offset;
            public int Left = count;
            public int Filled;
            public int At;

            public HeldCall[] Buffer { get; } = buffer;

            public HeldCall Current => Buffer[At];
        }
    }

    /// <summary>
    /// A call held or spilled until it is written: what an operation index writes of it, with its first record's origin by
    /// its number among the origins held and its interface by its number while pairing.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct HeldCall
    {
        public ulong Ordinal;
        public long StartTicks;
        public long StopTicks;
        public long Procedure;
        public long Protocol;
        public long Status;
        public int FirstRank;
        public int Origin;
        public int ProcessId;
        public int Interface;
        public int StartSegment;
        public int StartRow;
        public int StopSegment;
        public int StopRow;
        public byte Side;
        public byte State;
        public bool HasProcedure;
        public bool HasProtocol;
        public bool HasStatus;

        public static HeldCall Of(in Entry call, int origin) => new()
        {
            Ordinal = call.FirstAddress.Ordinal,
            StartTicks = call.StartTicks,
            StopTicks = call.StopTicks,
            Procedure = call.Procedure,
            Protocol = call.Protocol,
            Status = call.Status,
            FirstRank = call.FirstRank,
            Origin = origin,
            ProcessId = call.ProcessId,
            Interface = call.Interface,
            StartSegment = call.StartSegment,
            StartRow = call.StartRow,
            StopSegment = call.StopSegment,
            StopRow = call.StopRow,
            Side = (byte)call.Side,
            State = (byte)call.State,
            HasProcedure = call.HasProcedure,
            HasProtocol = call.HasProtocol,
            HasStatus = call.HasStatus,
        };

        public readonly Entry ToEntry(Origin origin, int[] interfacePlace) => new()
        {
            FirstAddress = new RecordAddress(origin.Stream, origin.Epoch, Ordinal, origin.FactKey),
            FirstRank = FirstRank,
            ProcessId = ProcessId,
            Side = (RpcCallSide)Side,
            State = (RpcCallState)State,
            Interface = Interface < 0 ? -1 : interfacePlace[Interface],
            Procedure = Procedure,
            HasProcedure = HasProcedure,
            Protocol = Protocol,
            HasProtocol = HasProtocol,
            StartTicks = StartTicks,
            StartSegment = StartSegment,
            StartRow = StartRow,
            StopTicks = StopTicks,
            StopSegment = StopSegment,
            StopRow = StopRow,
            Status = Status,
            HasStatus = HasStatus,
            StartThread = -1,
        };
    }
}
