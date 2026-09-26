using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

public sealed partial class TransportRelationIndex
{
    private const int PositionBytes = 40;
    private const int KeyBytes = 14;

    /// <summary>The process index the holders here are positions in.</summary>
    internal ProcessInstanceIndex Processes => processes;

    /// <summary>The published files these relations were derived from, or null when one had no published identity.</summary>
    internal IReadOnlyCollection<StoreDependency>? FilesRead => read;

    /// <summary>
    /// Writes every end's cuts, latest position and incarnations, and the related records that name no end, in canonical
    /// order (`contracts/derivation-checkpoint-v1.md` §3, <c>relations</c>). Pairing and channel numbers are not written:
    /// they are a function of these and are computed again when the relations are read.
    /// </summary>
    internal void WriteState(IndexFileWriter writer)
    {
        KeyValuePair<Mechanism, long>[] unmatched = [.. withoutEnd.Where(entry => entry.Value > 0)];
        Array.Sort(unmatched, static (left, right) => left.Key.CompareTo(right.Key));
        writer.Count(unmatched.Length);
        foreach ((Mechanism mechanism, long records) in unmatched)
        {
            writer.U16((ushort)mechanism);
            writer.I64(records);
        }

        KeyValuePair<EndKey, EndTimeline>[] ordered = [.. ends];
        Array.Sort(ordered, static (left, right) => left.Key.CompareTo(right.Key));
        writer.Count(ordered.Length);
        foreach ((EndKey key, EndTimeline timeline) in ordered)
        {
            writer.U8(key.Protocol);
            writer.U8(key.Family);
            writer.U32(key.LocalAddress);
            writer.U16(key.LocalPort);
            writer.U32(key.RemoteAddress);
            writer.U16(key.RemotePort);
            timeline.Write(writer);
        }
    }

    /// <summary>
    /// Rebuilds the relations a checkpoint holds over <paramref name="processes"/>, the instances rebuilt from the same
    /// checkpoint, whose positions its holders name.
    /// </summary>
    internal static TransportRelationIndex ReadState(
        IndexFileReader reader,
        ProcessInstanceIndex processes,
        HashSet<StoreDependency> read)
    {
        int count = reader.Count(2 + 8);
        var withoutEnd = new Dictionary<Mechanism, long>(count);
        Mechanism? previous = null;
        for (int index = 0; index < count; index++)
        {
            var mechanism = (Mechanism)reader.U16();
            long records = reader.I64();
            if (!Relates(mechanism) || records <= 0 || (previous is { } before && before >= mechanism))
            {
                throw reader.Invalid("its records without an end are not counted once per related mechanism.");
            }

            withoutEnd.Add(mechanism, records);
            previous = mechanism;
        }

        // An end holds its key, a cut count, whether it has a latest record and at least one incarnation's flags.
        count = reader.Count(KeyBytes + 4 + 1 + 1);
        var ends = new Dictionary<EndKey, EndTimeline>(count);
        EndKey? last = null;
        for (int index = 0; index < count; index++)
        {
            var key = new EndKey(reader.U8(), reader.U8(), reader.U32(), reader.U16(), reader.U32(), reader.U16());
            if (!Relates((Mechanism)key.Protocol)
                || key.LocalAddress == 0 || key.LocalPort == 0 || key.RemoteAddress == 0 || key.RemotePort == 0)
            {
                throw reader.Invalid("an end is not a TCP or UDP end with both endpoints.");
            }

            if (last is { } before && before.CompareTo(key) >= 0)
            {
                throw reader.Invalid("its ends are not in canonical order.");
            }

            ends.Add(key, EndTimeline.Read(reader, processes.Instances.Count));
            last = key;
        }

        return new(processes, ends, withoutEnd, read);
    }

    private static void WritePosition(IndexFileWriter writer, Position position)
    {
        writer.I64(position.Ticks);
        writer.U32(position.Stream);
        writer.U32(position.Epoch);
        writer.U64(position.Ordinal);
        writer.U64(position.FactHigh);
        writer.U64(position.FactLow);
    }

    private static Position ReadPosition(IndexFileReader reader) =>
        new(reader.I64(), reader.U32(), reader.U32(), reader.U64(), reader.U64(), reader.U64());

    private sealed partial class EndTimeline
    {
        public void Write(IndexFileWriter writer)
        {
            if (Incarnations.Length != cuts.Count + 1)
            {
                throw new InvalidOperationException("An end's incarnations do not follow from its cuts.");
            }

            writer.Count(cuts.Count);
            foreach (Cut cut in cuts)
            {
                WritePosition(writer, cut.At);
                writer.Flag(cut.AfterClose);
            }

            writer.Flag(LastPosition is not null);
            if (LastPosition is { } latest)
            {
                WritePosition(writer, latest);
            }

            foreach (Incarnation incarnation in Incarnations)
            {
                incarnation.Write(writer);
            }
        }

        public static EndTimeline Read(IndexFileReader reader, int instances)
        {
            int count = reader.Count(PositionBytes + 1);
            var timeline = new EndTimeline();
            for (int index = 0; index < count; index++)
            {
                var cut = new Cut(ReadPosition(reader), reader.Flag());
                if (index > 0 && timeline.cuts[^1].At.CompareTo(cut.At) > 0)
                {
                    throw reader.Invalid("an end's cuts are not in canonical order.");
                }

                timeline.cuts.Add(cut);
            }

            Position? latest = reader.Flag() ? ReadPosition(reader) : null;
            timeline.Incarnations = new Incarnation[count + 1];
            bool holdsRecords = false;
            for (int index = 0; index < timeline.Incarnations.Length; index++)
            {
                timeline.Incarnations[index] = Incarnation.Read(reader, timeline.Bounded(index), instances);
                holdsRecords |= timeline.Incarnations[index].Records > 0;
            }

            if (holdsRecords != latest.HasValue)
            {
                throw reader.Invalid("an end's latest record does not agree with the records its incarnations hold.");
            }

            timeline.LastPosition = latest;
            return timeline;
        }
    }

    private sealed partial class Incarnation
    {
        private const byte HoldsRecords = 1;
        private const byte NamesProcess = 2;
        private const byte NamesTwoProcesses = 4;
        private const byte BindsToTwo = 8;

        /// <summary>
        /// Writes what the incarnation's records say taken together. Which holder an ambiguous incarnation recorded
        /// first, and which PID one naming two recorded first, depend on the order its records were read in and are
        /// never read, so neither is written: the checkpoint is then a function of the records alone.
        /// </summary>
        public void Write(IndexFileWriter writer)
        {
            if (Records == 0)
            {
                writer.U8(0);
                return;
            }

            int? named = PidsDisagree ? null : processId;
            writer.U8((byte)(HoldsRecords
                | (named is null ? 0 : NamesProcess)
                | (PidsDisagree ? NamesTwoProcesses : 0)
                | (Ambiguous ? BindsToTwo : 0)));
            if (named is { } pid)
            {
                writer.I32(pid);
            }

            writer.I32(Ambiguous ? -1 : Holder);
            writer.U8((byte)Weakest);
            writer.I64(First);
            writer.I64(Last);
            WritePosition(writer, FirstPosition ?? throw new InvalidOperationException("An incarnation holds records and no first one."));
            writer.I64(Records);
            writer.I64(Untimed);
        }

        /// <summary>The incarnation within <paramref name="bounds"/> that a checkpoint describes.</summary>
        public static Incarnation Read(IndexFileReader reader, Incarnation bounds, int instances)
        {
            byte flags = reader.U8();
            if (flags == 0)
            {
                return bounds;
            }

            if ((flags & HoldsRecords) == 0 || flags > (HoldsRecords | NamesProcess | NamesTwoProcesses | BindsToTwo))
            {
                throw reader.Invalid($"an incarnation has flags {flags}.");
            }

            int? named = (flags & NamesProcess) != 0 ? reader.I32() : null;
            bool disagree = (flags & NamesTwoProcesses) != 0;
            bool ambiguous = (flags & BindsToTwo) != 0;
            int holder = reader.I32();
            var weakest = (RelationStrength)reader.U8();
            long first = reader.I64();
            long last = reader.I64();
            Position firstPosition = ReadPosition(reader);
            long records = reader.I64();
            long untimed = reader.I64();
            if ((disagree && (named is not null || !ambiguous))
                || (ambiguous && holder != -1)
                || holder < -1 || holder >= instances
                || !Enum.IsDefined(weakest)
                || first > last || firstPosition.Ticks != first
                || records <= 0 || untimed < 0 || untimed > records)
            {
                throw reader.Invalid("an incarnation's records, readings or holder contradict each other.");
            }

            return new(bounds.Start, bounds.End, bounds.OpenWitnessed, bounds.CloseWitnessed)
            {
                processId = named,
                PidsDisagree = disagree,
                Holder = holder,
                Ambiguous = ambiguous,
                Weakest = weakest,
                First = first,
                FirstPosition = firstPosition,
                Last = last,
                Records = records,
                Untimed = untimed,
            };
        }
    }
}
