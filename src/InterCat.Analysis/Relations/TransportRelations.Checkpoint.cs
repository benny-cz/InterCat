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
            WriteAddress(writer, key.Family, key.LocalAddress);
            writer.U16(key.LocalPort);
            WriteAddress(writer, key.Family, key.RemoteAddress);
            writer.U16(key.RemotePort);
            timeline.Write(writer);
        }
    }

    /// <summary>
    /// Writes what each incarnation holding records sent and received (`contracts/derivation-checkpoint-v1.md` §3,
    /// <c>transfers</c>, format 1.4): one entry per such incarnation, in the order <see cref="WriteState"/> wrote them, and
    /// then how many there are, so a checkpoint of format 1.3 is these bytes without them.
    /// </summary>
    internal void WriteTransfers(IndexFileWriter writer)
    {
        int count = 0;
        foreach (Incarnation incarnation in Ordered().Where(incarnation => incarnation.Records > 0))
        {
            incarnation.WriteTransfers(writer);
            count++;
        }

        writer.Count(count);
    }

    /// <summary>
    /// Reads what <see cref="WriteTransfers"/> wrote into the incarnations <see cref="ReadState"/> read, which then count
    /// what their records sent and received (<see cref="TransfersKept"/>).
    /// </summary>
    internal void ReadTransfers(IndexFileReader reader)
    {
        int count = 0;
        foreach (Incarnation incarnation in Ordered().Where(incarnation => incarnation.Records > 0))
        {
            incarnation.ReadTransfers(reader);
            count++;
        }

        if (reader.U32() != (uint)count)
        {
            throw reader.Invalid("its transfers are not one entry per incarnation holding records.");
        }

        TransfersKept = true;
    }

    /// <summary>Every end's incarnations, ends in canonical order and each end's in time order, as the state is written.</summary>
    private IEnumerable<Incarnation> Ordered() =>
        ends.OrderBy(entry => entry.Key).SelectMany(entry => entry.Value.Incarnations);

    /// <summary>
    /// An end's address in its family's width: four bytes for IPv4, as every format has written them, and from format 1.2
    /// sixteen bytes in network order for IPv6.
    /// </summary>
    private static void WriteAddress(IndexFileWriter writer, byte family, UInt128 address)
    {
        if (family == 6)
        {
            writer.Address128(address);
        }
        else
        {
            writer.U32((uint)address);
        }
    }

    /// <summary>
    /// Rebuilds the relations a checkpoint holds over <paramref name="processes"/>, the instances rebuilt from the same
    /// checkpoint, whose positions its holders name. <paramref name="ipv6Ends"/> says whether the checkpoint's format can
    /// hold an IPv6 end, which format 1.2 added; an earlier one that claims one is refused. <paramref name="earlierRule"/>
    /// says the relations were derived under <see cref="EarlierRelationRule"/>, which are this rule's only when no related
    /// record was left without an end: that rule left every IPv6 record without one. <paramref name="cutKinds"/> says
    /// whether the format says which record opened an incarnation, a connect or an accept, which format 1.3 added; an
    /// earlier one that holds an opening cut is refused, since its relations could not say how a connection opened.
    /// What each incarnation's records sent and received is read after the checkpoint's last section, where format 1.4
    /// holds it (<see cref="ReadTransfers"/>); until then the relations keep no such counts.
    /// </summary>
    internal static TransportRelationIndex ReadState(
        IndexFileReader reader,
        ProcessInstanceIndex processes,
        HashSet<StoreDependency> read,
        bool ipv6Ends,
        bool earlierRule,
        bool cutKinds)
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

        if (earlierRule && withoutEnd.Count > 0)
        {
            long unended = withoutEnd.Values.Sum();
            throw reader.Invalid(
                $"it was derived under {EarlierRelationRule}, which left {unended:N0} related "
                + $"{(unended == 1 ? "record" : "records")} without an end, and {RelationRule} gives an IPv6 record the "
                + "end that rule did not.");
        }

        // An end holds its key, a cut count, whether it has a latest record and at least one incarnation's flags.
        count = reader.Count(KeyBytes + 4 + 1 + 1);
        var ends = new Dictionary<EndKey, EndTimeline>(count);
        EndKey? last = null;
        for (int index = 0; index < count; index++)
        {
            byte protocol = reader.U8();
            byte family = reader.U8();
            if (family is not (4 or 6) || (family == 6 && !ipv6Ends))
            {
                throw reader.Invalid("an end's address family is not IPv4, or IPv6 in a format that holds one.");
            }

            var key = new EndKey(
                protocol,
                family,
                ReadAddress(reader, family),
                reader.U16(),
                ReadAddress(reader, family),
                reader.U16());
            if (!Relates((Mechanism)key.Protocol)
                || key.LocalAddress == UInt128.Zero || key.LocalPort == 0 || key.RemoteAddress == UInt128.Zero || key.RemotePort == 0)
            {
                throw reader.Invalid("an end is not a TCP or UDP end with both endpoints.");
            }

            if (last is { } before && before.CompareTo(key) >= 0)
            {
                throw reader.Invalid("its ends are not in canonical order.");
            }

            ends.Add(key, EndTimeline.Read(reader, processes.Instances.Count, cutKinds));
            last = key;
        }

        return new(processes, ends, withoutEnd, read, transfersKept: false, ownersKept: false);
    }

    private static UInt128 ReadAddress(IndexFileReader reader, byte family) =>
        family == 6 ? reader.Address128() : reader.U32();

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

    // What made a cut, as format 1.3 writes it; before 1.3 a cut said only whether it closed (1) or opened (0).
    private const byte ConnectCut = 0;
    private const byte DisconnectCut = 1;
    private const byte AcceptCut = 2;

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
                writer.U8(cut.AfterClose ? DisconnectCut : cut.Accepted ? AcceptCut : ConnectCut);
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

        /// <summary>
        /// Reads an end's cuts, latest position and incarnations. A cut says whether a connect, an accept or a disconnect
        /// made it where <paramref name="cutKinds"/> says the format holds that (1.3); an earlier one says only whether it
        /// closed, so one that opened an incarnation is refused: the relations could not say how that connection opened.
        /// </summary>
        public static EndTimeline Read(IndexFileReader reader, int instances, bool cutKinds)
        {
            int count = reader.Count(PositionBytes + 1);
            var timeline = new EndTimeline();
            for (int index = 0; index < count; index++)
            {
                Position at = ReadPosition(reader);
                byte kind = reader.U8();
                if (kind > (cutKinds ? AcceptCut : DisconnectCut))
                {
                    throw reader.Invalid($"an end's cut has kind {kind}.");
                }

                if (!cutKinds && kind == ConnectCut)
                {
                    throw reader.Invalid("its format, before 1.3, does not say whether a connect or an accept opened a "
                        + "connection, which this build's relations say; icat checkpoint writes one that does.");
                }

                var cut = new Cut(at, AfterClose: kind == DisconnectCut, Accepted: kind == AcceptCut);
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

        /// <summary>
        /// Writes what its records sent and received (format 1.4): whether a sum was refused, then its sends, the bytes they
        /// state and how many state none, and the same of its receives. A refused sum is written as none.
        /// </summary>
        public void WriteTransfers(IndexFileWriter writer)
        {
            writer.Flag(SumsRefused);
            writer.I64(Sends);
            writer.I64(SumsRefused ? 0 : SentBytes);
            writer.I64(UnsizedSends);
            writer.I64(Receives);
            writer.I64(SumsRefused ? 0 : ReceivedBytes);
            writer.I64(UnsizedReceives);
        }

        /// <summary>Reads what <see cref="WriteTransfers"/> wrote, refusing counts its records contradict.</summary>
        public void ReadTransfers(IndexFileReader reader)
        {
            bool refused = reader.Flag();
            (long sends, long sent, long unsizedSends, long receives, long received, long unsizedReceives) =
                (reader.I64(), reader.I64(), reader.I64(), reader.I64(), reader.I64(), reader.I64());
            if (sends < 0 || unsizedSends < 0 || unsizedSends > sends
                || receives < 0 || unsizedReceives < 0 || unsizedReceives > receives
                || sends > Records - receives
                || (refused && (sent != 0 || received != 0)))
            {
                throw reader.Invalid("an incarnation's sends and receives contradict its records.");
            }

            (Sends, SentBytes, UnsizedSends, Receives, ReceivedBytes, UnsizedReceives, SumsRefused) =
                (sends, sent, unsizedSends, receives, received, unsizedReceives, refused);
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

            return new(bounds.Start, bounds.End, bounds.OpenWitnessed, bounds.CloseWitnessed, bounds.Opening)
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
