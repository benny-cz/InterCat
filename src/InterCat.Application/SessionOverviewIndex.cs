using System.Buffers;
using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// What the overview counts from a generation's rows (<see cref="SessionOverviewProjector.Count"/>): how many rows there
/// are, how many have no session time, the extent of the rest, and their counts in the overview's columns, per
/// mechanism, and in the minimap's. Null columns and extent mean no row is timed.
/// </summary>
internal sealed record OverviewCounts(
    long Rows,
    long WithoutTime,
    TimeRange? Extent,
    TimelineColumns? Main,
    TimelineColumns? Minimap)
{
    /// <summary>
    /// The RPC links between process instances, by pair and strength, when the capture collected ALPC and a persisted
    /// overview kept them (overview-index-v1 minor 1); null when they were not kept, and then they are derived when needed.
    /// </summary>
    public IReadOnlyList<RpcPeerLinkTotal>? RpcLinks { get; init; }

    /// <summary>
    /// Each overview column's bytes per mechanism, which a byte ranking's lanes plot, when a persisted overview kept them
    /// (overview-index-v1 minor 2); null when it did not, and then they are read from the segments when a view asks.
    /// </summary>
    public OverviewLaneBytes? LaneBytes { get; init; }

    /// <summary>
    /// Each process's and TCP channel end's bytes over the whole session, before any evidence policy, which the ranked
    /// table and the graph read under a byte ranking, when a persisted overview kept them (overview-index-v1 minor 3);
    /// null when it did not, and then they are read from the segments when a view asks.
    /// </summary>
    public OverviewProcessBytes? ProcessBytes { get; init; }

    /// <summary>
    /// The timed rows read before <see cref="Extent"/> begins: those a session that released its oldest interval kept from
    /// before its boundary, as the evidence of what came after, whose overview begins at the boundary (ADR-045,
    /// overview-index-v1 minor 4). Zero for every other session, and in an overview of an earlier minor.
    /// </summary>
    public long BeforeExtent { get; init; }
}

/// <summary>
/// What each overview column's records of one mechanism sent, received and stated on neither side, with the contributions
/// that measured each and those that declared a size they did not record (`TransportBytes`, overview-index-v1 minor 2).
/// Only a cell with a contribution is held, by column and then mechanism code; any other cell's bytes are none.
/// </summary>
internal sealed class OverviewLaneBytes
{
    private readonly Dictionary<(int Column, Mechanism Mechanism), TransportBytes> byCell;

    public OverviewLaneBytes(IEnumerable<(int Column, Mechanism Mechanism, TransportBytes Bytes)> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        Cells = Array.AsReadOnly([.. cells
            .Where(cell => cell.Bytes != TransportBytes.None)
            .OrderBy(cell => cell.Column)
            .ThenBy(cell => (int)cell.Mechanism)]);
        byCell = Cells.ToDictionary(cell => (cell.Column, cell.Mechanism), cell => cell.Bytes);
    }

    /// <summary>Every cell with a contribution, by column and then mechanism code.</summary>
    public IReadOnlyList<(int Column, Mechanism Mechanism, TransportBytes Bytes)> Cells { get; }

    /// <summary>One column's bytes of one mechanism: none where no record of it contributed any.</summary>
    public TransportBytes Of(int column, Mechanism mechanism) =>
        byCell.TryGetValue((column, mechanism), out TransportBytes? bytes) ? bytes : TransportBytes.None;
}

/// <summary>
/// What every record's transport-observed measurements came to over the whole session, before any evidence policy
/// (overview-index-v1 minor 3): for each process instance and each strength its records bind at, their bytes; for each
/// end of each TCP channel, the bytes of its holder's own records there and the one strength they bind at; and the
/// bytes of records bound to no instance at a strength a policy could admit. A policy applied to them gives exactly what
/// <see cref="SessionByteRanking.Measure"/> reads from every segment under it.
/// </summary>
internal sealed class OverviewProcessBytes
{
    public OverviewProcessBytes(
        TransportBytes unbound,
        IEnumerable<(ProcessInstanceId Instance, RelationStrength Strength, TransportBytes Bytes)> processes,
        IEnumerable<(string Channel, ProcessInstanceId Holder, RelationStrength Strength, TransportBytes Bytes)> ends)
    {
        ArgumentNullException.ThrowIfNull(unbound);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(ends);
        Unbound = unbound;
        Processes = Array.AsReadOnly([.. processes
            .OrderBy(entry => entry.Instance.ToString(), StringComparer.Ordinal)
            .ThenBy(entry => (int)entry.Strength)]);
        Ends = Array.AsReadOnly([.. ends
            .OrderBy(entry => entry.Channel, StringComparer.Ordinal)
            .ThenBy(entry => entry.Holder.ToString(), StringComparer.Ordinal)]);
    }

    /// <summary>The bytes of records bound to no instance, or at a strength no evidence policy admits.</summary>
    public TransportBytes Unbound { get; }

    /// <summary>Each instance's bytes at each strength its records bind at, by instance and then strength.</summary>
    public IReadOnlyList<(ProcessInstanceId Instance, RelationStrength Strength, TransportBytes Bytes)> Processes { get; }

    /// <summary>Each TCP channel end's bytes, its holder's own, and the strength they bind at, by channel and then holder.</summary>
    public IReadOnlyList<(string Channel, ProcessInstanceId Holder, RelationStrength Strength, TransportBytes Bytes)> Ends { get; }

    /// <summary>How many bytes they take in a persisted overview (overview-index-v1 §3), flag included.</summary>
    public long EncodedLength =>
        1 + Bytes + 4 + (Processes.Count * (16L + 1 + Bytes)) + 4
        + Ends.Sum(end => 1L + System.Text.Encoding.UTF8.GetByteCount(end.Channel) + 16 + 1 + Bytes);

    private const long Bytes = 9 * 8;
}

/// <summary>
/// The persisted whole-session overview (`contracts/overview-index-v1.md`), S4's top level: the counts a finished session's
/// first view draws, so opening it counts no row. It is published beside the derivation checkpoint and read only when
/// it covers exactly the observation segments its generation names.
/// </summary>
internal static class SessionOverviewIndex
{
    /// <summary>The largest overview a writer writes or a reader reads.</summary>
    public const int MaximumBytes = 16 * 1024 * 1024;

    private const string FilePrefix = "overview-";
    private const string FileSuffix = ".bin";
    private const string What = "persisted overview";
    private const ushort Major = 1;

    /// <summary>
    /// Minor 1 adds the RPC link totals after the counts, minor 2 each overview column's bytes per mechanism after them,
    /// minor 3 each process's and TCP channel end's bytes after those, and minor 4 how many timed rows were read before the
    /// extent begins (§3); an earlier overview holds what its minor did and is still read.
    /// </summary>
    private const ushort Minor = 4;

    /// <summary>The most RPC link totals an overview holds: far more instance pairs than a session draws.</summary>
    private const int MaximumRpcLinks = 1_000_000;

    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    private static ReadOnlySpan<byte> Magic => "ICATOVRV"u8;

    /// <summary>The name an overview published by <paramref name="generation"/> takes.</summary>
    public static string FileNameFor(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        return string.Create(CultureInfo.InvariantCulture, $"{FilePrefix}{generation:D10}{FileSuffix}");
    }

    /// <summary>The overview <paramref name="manifest"/> names, or null. A generation naming two is refused.</summary>
    public static StoreDependency? NamedBy(SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        StoreDependency[] named = [.. manifest.Dependencies.Where(dependency =>
            dependency.Kind == StoreDependencyKind.Index
            && dependency.Name.Length == FilePrefix.Length + 10 + FileSuffix.Length
            && dependency.Name.StartsWith(FilePrefix, StringComparison.Ordinal)
            && dependency.Name.EndsWith(FileSuffix, StringComparison.Ordinal))];
        return named.Length switch
        {
            0 => null,
            1 => named[0],
            _ => throw new InvalidDataException(
                $"Generation {manifest.Generation} names {named.Length} persisted overviews; a generation names one."),
        };
    }

    /// <summary>The observation segments a generation names, as its manifest records them.</summary>
    public static StoreDependency[] ObservationSegments(SessionManifestV1 manifest) => Named(manifest, SessionSegments.Names(manifest));

    /// <summary>The source-field segments a generation names, as its manifest records them.</summary>
    public static StoreDependency[] FieldSegments(SessionManifestV1 manifest) => Named(manifest, SessionSegments.FieldNames(manifest));

    /// <summary>Whether <paramref name="covered"/> is exactly the generation's observation segments, file for file.</summary>
    public static bool Covers(IReadOnlyCollection<StoreDependency> covered, SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(covered);
        StoreDependency[] named = ObservationSegments(manifest);
        return covered.Count == named.Length && covered.ToHashSet().SetEquals(named);
    }

    /// <summary>
    /// Writes <paramref name="counts"/>, counted from <paramref name="segments"/> of generation
    /// <paramref name="derivedGeneration"/>, in the canonical order of overview-index-v1 §3.
    /// </summary>
    /// <returns>How many bytes were written.</returns>
    public static long Write(
        Stream destination,
        Guid sessionId,
        long derivedGeneration,
        IReadOnlyList<StoreDependency> segments,
        OverviewCounts counts)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentOutOfRangeException.ThrowIfLessThan(derivedGeneration, 1);
        if (counts is { Main: null, LaneBytes.Cells.Count: > 0 })
        {
            throw new ArgumentException("Bytes are kept only in the columns of an extent, and these counts have none.", nameof(counts));
        }

        var writer = new IndexFileWriter(destination, MaximumBytes, What);
        writer.Raw(Magic);
        writer.U16(Major);
        writer.U16(Minor);
        writer.Identity(sessionId);
        writer.I64(derivedGeneration);
        writer.U16(SessionOverviewProjector.MaximumTimelineBuckets);
        writer.U16(SessionMinimap.MaximumColumns);
        StoreDependency[] covered = [.. segments];
        Array.Sort(covered, static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        writer.Count(covered.Length);
        foreach (StoreDependency segment in covered)
        {
            writer.Str8(segment.Name);
            writer.I64(segment.LengthBytes);
            writer.Str8(segment.Digest);
        }

        writer.I64(counts.Rows);
        writer.I64(counts.WithoutTime);
        writer.Flag(counts.Extent is not null);
        if (counts is { Extent: { } extent, Main: { } main, Minimap: { } minimap })
        {
            writer.I64(extent.StartTicks);
            writer.I64(extent.EndTicks);
            writer.Count(main.Counts.Count);
            (int Column, Mechanism Mechanism, int Count)[] tallies = [.. main.Tallies()];
            writer.Count(tallies.Length);
            foreach ((int column, Mechanism mechanism, int records) in tallies)
            {
                writer.Count(column);
                writer.U16((ushort)mechanism);
                writer.I32(records);
            }

            writer.I64(minimap.Interval.StartTicks);
            writer.I64(minimap.Interval.EndTicks);
            writer.Count(minimap.Counts.Count);
            int[] columns = [.. Enumerable.Range(0, minimap.Counts.Count).Where(column => minimap.Counts[column] > 0)];
            writer.Count(columns.Length);
            foreach (int column in columns)
            {
                writer.Count(column);
                writer.I32(minimap.Counts[column]);
            }
        }

        // Minor 1: the RPC links, kept only for a capture that collected ALPC, in their canonical order.
        writer.Flag(counts.RpcLinks is not null);
        if (counts.RpcLinks is { } links)
        {
            writer.Count(links.Count);
            foreach (RpcPeerLinkTotal link in links)
            {
                writer.Identity(link.First.Value);
                writer.Identity(link.Second.Value);
                writer.U8((byte)link.Strength);
                writer.I64(link.Records);
            }
        }

        // Minor 2: each overview column's bytes per mechanism, kept by a writer that summed them, in their canonical order.
        writer.Flag(counts.LaneBytes is not null);
        if (counts.LaneBytes is { } lanes)
        {
            writer.Count(lanes.Cells.Count);
            foreach ((int column, Mechanism mechanism, TransportBytes bytes) in lanes.Cells)
            {
                writer.Count(column);
                writer.U16((ushort)mechanism);
                WriteBytes(writer, bytes);
            }
        }

        // Minor 3: each process's and TCP channel end's bytes over the whole session, before any evidence policy, kept by a
        // writer that summed them, in their canonical order.
        writer.Flag(counts.ProcessBytes is not null);
        if (counts.ProcessBytes is { } kept)
        {
            WriteBytes(writer, kept.Unbound);
            writer.Count(kept.Processes.Count);
            foreach ((ProcessInstanceId instance, RelationStrength strength, TransportBytes bytes) in kept.Processes)
            {
                writer.Identity(instance.Value);
                writer.U8((byte)strength);
                WriteBytes(writer, bytes);
            }

            writer.Count(kept.Ends.Count);
            foreach ((string channel, ProcessInstanceId holder, RelationStrength strength, TransportBytes bytes) in kept.Ends)
            {
                writer.Str8(channel);
                writer.Identity(holder.Value);
                writer.U8((byte)strength);
                WriteBytes(writer, bytes);
            }
        }

        // Minor 4: the timed rows read before the extent begins, which a session that released an interval keeps.
        writer.I64(counts.BeforeExtent);
        writer.Flush();
        return writer.Written;
    }

    /// <summary>One set of records' bytes: sent, received and on neither side, each with its measured and unmeasured counts.</summary>
    private static void WriteBytes(IndexFileWriter writer, TransportBytes bytes)
    {
        writer.I64(bytes.SentBytes);
        writer.I64(bytes.SentMeasured);
        writer.I64(bytes.SentUnmeasured);
        writer.I64(bytes.ReceivedBytes);
        writer.I64(bytes.ReceivedMeasured);
        writer.I64(bytes.ReceivedUnmeasured);
        writer.I64(bytes.OtherBytes);
        writer.I64(bytes.OtherMeasured);
        writer.I64(bytes.OtherUnmeasured);
    }

    /// <summary>
    /// Reads an overview of session <paramref name="sessionId"/>, with the segments it covers. Anything the bytes do not
    /// hold, hold in contradiction, or place in columns this build would not draw, is refused with
    /// <see cref="InvalidDataException"/> and the reason (§3).
    /// </summary>
    public static (OverviewCounts Counts, IReadOnlyList<StoreDependency> Segments) Read(ReadOnlyMemory<byte> bytes, Guid sessionId)
    {
        if (bytes.Length > MaximumBytes)
        {
            throw new InvalidDataException($"The {What} is not readable: it is larger than {MaximumBytes / (1024 * 1024)} MiB.");
        }

        // Counts are placed by the timeline's own rules, which refuse an extent or a count no session could hold with the
        // exceptions counting raises; here they say only that the overview is unreadable.
        try
        {
            return ReadCore(new IndexFileReader(bytes, What), sessionId);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"The {What} is not readable: {exception.Message}", exception);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException($"The {What} is not readable: {exception.Message}", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException($"The {What} is not readable: {exception.Message}", exception);
        }
    }

    private static (OverviewCounts Counts, IReadOnlyList<StoreDependency> Segments) ReadCore(IndexFileReader reader, Guid sessionId)
    {
        if (!reader.Matches(Magic))
        {
            throw reader.Invalid("it does not begin as a persisted overview does.");
        }

        ushort major = reader.U16();
        ushort minor = reader.U16();
        if (major != Major || minor > Minor)
        {
            throw reader.Invalid($"it is format {major}.{minor}, and this build reads {Major}.0 to {Major}.{Minor}.");
        }

        Guid session = reader.Identity();
        if (session != sessionId)
        {
            throw reader.Invalid($"it belongs to session {session:N}, not {sessionId:N}.");
        }

        if (reader.I64() < 1)
        {
            throw reader.Invalid("it names no generation it was counted from.");
        }

        if (reader.U16() != SessionOverviewProjector.MaximumTimelineBuckets || reader.U16() != SessionMinimap.MaximumColumns)
        {
            throw reader.Invalid("it was counted into columns this build does not draw.");
        }

        int count = reader.Count(2 + 8 + 1);
        var segments = new List<StoreDependency>(count);
        for (int index = 0; index < count; index++)
        {
            string name = reader.Str8();
            long length = reader.I64();
            string digest = reader.Str8();
            if (OwnedFileName.Validate(name) is not null
                || length < 0
                || digest.Length != 71
                || !digest.StartsWith("sha256:", StringComparison.Ordinal)
                || digest.AsSpan(7).ContainsAnyExcept(LowerHex)
                || (segments.Count > 0 && string.CompareOrdinal(segments[^1].Name, name) >= 0))
            {
                throw reader.Invalid("a covered segment is not a named, measured segment in name order.");
            }

            segments.Add(new(name, StoreDependencyKind.Segment, length, digest));
        }

        long rows = reader.I64();
        long withoutTime = reader.I64();
        if (rows < 0 || withoutTime < 0 || withoutTime > rows)
        {
            throw reader.Invalid("its row counts contradict each other.");
        }

        if (!reader.Flag())
        {
            IReadOnlyList<RpcPeerLinkTotal>? untimedLinks = minor >= 1 ? ReadLinks(reader) : null;
            OverviewLaneBytes? untimedBytes = minor >= 2 ? ReadLaneBytes(reader, main: null) : null;
            OverviewProcessBytes? untimedKept = minor >= 3 ? ReadProcessBytes(reader) : null;
            long untimedBefore = minor >= 4 ? reader.I64() : 0;
            reader.RequireEnd();
            return rows == withoutTime && untimedBefore == 0
                ? (new(rows, withoutTime, null, null, null) { RpcLinks = untimedLinks, LaneBytes = untimedBytes, ProcessBytes = untimedKept },
                    segments.AsReadOnly())
                : throw reader.Invalid("it has timed rows and no extent.");
        }

        long start = reader.I64();
        long end = reader.I64();
        if (!TimeRange.TryCreate(start, end, out TimeRange extent))
        {
            throw reader.Invalid("its extent is empty, or longer than a tick count holds.");
        }

        // How many columns the extent is divided into is a width, not a count of the fields that follow, so it bounds
        // nothing about the bytes left: a few records over a long extent fill few of many columns (overview-index-v1 §3).
        var main = new TimelineColumns(extent, SessionOverviewProjector.MaximumTimelineBuckets, tallyMechanisms: true);
        if (reader.U32() != (uint)main.Counts.Count)
        {
            throw reader.Invalid("its overview columns are not the ones this build divides the extent into.");
        }

        count = reader.Count(4 + 2 + 4);
        long timed = rows - withoutTime;
        long total = 0;
        (int Column, int Code) previous = (-1, -1);
        for (int index = 0; index < count; index++)
        {
            uint column = reader.U32();
            var mechanism = (Mechanism)reader.U16();
            int records = reader.I32();
            if (column >= (uint)main.Counts.Count || !Enum.IsDefined(mechanism) || records <= 0
                || ((int)column, (int)mechanism).CompareTo(previous) <= 0)
            {
                throw reader.Invalid("an overview column's count is outside its columns, of an undefined mechanism, or out of order.");
            }

            main.Add((int)column, mechanism, records);
            total += records;
            previous = ((int)column, (int)mechanism);
        }

        (TimeRange span, int columns) = SessionMinimap.ColumnsFor(extent);
        if (reader.I64() != span.StartTicks || reader.I64() != span.EndTicks || reader.U32() != (uint)columns)
        {
            throw reader.Invalid("its minimap columns are not the ones this build derives from the extent.");
        }

        var minimap = new TimelineColumns(span, columns, tallyMechanisms: false);
        count = reader.Count(4 + 4);
        long mapped = 0;
        int before = -1;
        for (int index = 0; index < count; index++)
        {
            uint column = reader.U32();
            int records = reader.I32();
            if (column >= (uint)columns || (int)column <= before || records <= 0)
            {
                throw reader.Invalid("a minimap column's count is outside its columns or out of order.");
            }

            minimap.Add((int)column, Mechanism.UnknownMechanism, records);
            mapped += records;
            before = (int)column;
        }

        IReadOnlyList<RpcPeerLinkTotal>? links = minor >= 1 ? ReadLinks(reader) : null;
        OverviewLaneBytes? laneBytes = minor >= 2 ? ReadLaneBytes(reader, main) : null;
        OverviewProcessBytes? kept = minor >= 3 ? ReadProcessBytes(reader) : null;
        long beforeExtent = minor >= 4 ? reader.I64() : 0;
        reader.RequireEnd();
        if (beforeExtent < 0 || beforeExtent > timed)
        {
            throw reader.Invalid("it counts more rows before its extent than it has timed rows.");
        }

        return total + beforeExtent == timed && mapped + beforeExtent == timed
            ? (new(rows, withoutTime, extent, main, minimap)
                {
                    RpcLinks = links,
                    LaneBytes = laneBytes,
                    ProcessBytes = kept,
                    BeforeExtent = beforeExtent,
                },
                segments.AsReadOnly())
            : throw reader.Invalid("its columns do not add up to its timed rows.");
    }

    /// <summary>
    /// Minor 2's lane bytes (§3): none when they were not kept; otherwise each cell an overview column counts records of
    /// a mechanism in, once and in order, with no count below zero, no sum without a contribution that measured it, and
    /// some contribution. An overview with no extent has no columns, so it keeps no cell.
    /// </summary>
    private static OverviewLaneBytes? ReadLaneBytes(IndexFileReader reader, TimelineColumns? main)
    {
        if (!reader.Flag())
        {
            return null;
        }

        int count = reader.Count(4 + 2 + (9 * 8));
        var cells = new List<(int Column, Mechanism Mechanism, TransportBytes Bytes)>(count);
        (int Column, int Code) previous = (-1, -1);
        for (int index = 0; index < count; index++)
        {
            uint column = reader.U32();
            var mechanism = (Mechanism)reader.U16();
            TransportBytes bytes = ReadBytes(reader);
            if (main is null || column >= (uint)main.Counts.Count || !Enum.IsDefined(mechanism)
                || main.CountOf((int)column, mechanism) == 0
                || ((int)column, (int)mechanism).CompareTo(previous) <= 0
                || !Consistent(bytes))
            {
                throw reader.Invalid("a lane's bytes lie outside the overview's columns or where it counts no record of "
                    + "their mechanism, are out of order, or contradict themselves.");
            }

            cells.Add(((int)column, mechanism, bytes));
            previous = ((int)column, (int)mechanism);
        }

        return new(cells);
    }

    /// <summary>
    /// Minor 3's process bytes (§3): none when they were not kept; otherwise the bytes of records bound to no instance, then
    /// each instance's at each strength a policy can admit, and each TCP channel end's by its holder, each once and in
    /// order, with no count below zero, no sum without a contribution that measured it, and some contribution.
    /// </summary>
    private static OverviewProcessBytes? ReadProcessBytes(IndexFileReader reader)
    {
        if (!reader.Flag())
        {
            return null;
        }

        TransportBytes unbound = ReadBytes(reader);
        if (!Consistent(unbound) && unbound != TransportBytes.None)
        {
            throw reader.Invalid("the bytes of records bound to no instance contradict themselves.");
        }

        int count = reader.Count(16 + 1 + (9 * 8));
        var processes = new List<(ProcessInstanceId Instance, RelationStrength Strength, TransportBytes Bytes)>(count);
        for (int index = 0; index < count; index++)
        {
            var instance = new ProcessInstanceId(reader.Identity());
            var strength = (RelationStrength)reader.U8();
            TransportBytes bytes = ReadBytes(reader);
            if (instance.Value == Guid.Empty || !Admissible(strength) || !Consistent(bytes)
                || (processes.Count > 0 && CompareKept(processes[^1].Instance, (int)processes[^1].Strength, instance, (int)strength) >= 0))
            {
                throw reader.Invalid("a process's bytes name no instance, a strength no policy admits, are out of order, or "
                    + "contradict themselves.");
            }

            processes.Add((instance, strength, bytes));
        }

        count = reader.Count(1 + 16 + 1 + (9 * 8));
        var ends = new List<(string Channel, ProcessInstanceId Holder, RelationStrength Strength, TransportBytes Bytes)>(count);
        for (int index = 0; index < count; index++)
        {
            string channel = reader.Str8();
            var holder = new ProcessInstanceId(reader.Identity());
            var strength = (RelationStrength)reader.U8();
            TransportBytes bytes = ReadBytes(reader);
            int order = ends.Count == 0 ? -1 : string.CompareOrdinal(ends[^1].Channel, channel);
            if (channel.Length == 0 || holder.Value == Guid.Empty || !Admissible(strength) || !Consistent(bytes)
                || order > 0
                || (order == 0 && string.CompareOrdinal(ends[^1].Holder.ToString(), holder.ToString()) >= 0))
            {
                throw reader.Invalid("a channel end's bytes name no channel or holder, a strength no policy admits, are out of "
                    + "order, or contradict themselves.");
            }

            ends.Add((channel, holder, strength, bytes));
        }

        return new(unbound, processes, ends);
    }

    /// <summary>Whether a binding at <paramref name="strength"/> is one some evidence policy admits.</summary>
    private static bool Admissible(RelationStrength strength) =>
        strength is RelationStrength.Direct or RelationStrength.Correlated or RelationStrength.Candidate or RelationStrength.Conflicting;

    private static int CompareKept(ProcessInstanceId before, int beforeStrength, ProcessInstanceId instance, int strength)
    {
        int order = string.CompareOrdinal(before.ToString(), instance.ToString());
        return order != 0 ? order : beforeStrength.CompareTo(strength);
    }

    private static TransportBytes ReadBytes(IndexFileReader reader) =>
        new(reader.I64(), reader.I64(), reader.I64(), reader.I64(), reader.I64(), reader.I64())
        {
            OtherBytes = reader.I64(),
            OtherMeasured = reader.I64(),
            OtherUnmeasured = reader.I64(),
        };

    /// <summary>No count is below zero, a side no contribution measured sums nothing, and some contribution is there.</summary>
    private static bool Consistent(TransportBytes bytes) =>
        bytes is
        {
            SentBytes: >= 0, SentMeasured: >= 0, SentUnmeasured: >= 0,
            ReceivedBytes: >= 0, ReceivedMeasured: >= 0, ReceivedUnmeasured: >= 0,
            OtherBytes: >= 0, OtherMeasured: >= 0, OtherUnmeasured: >= 0,
        }
        && (bytes.SentMeasured > 0 || bytes.SentBytes == 0)
        && (bytes.ReceivedMeasured > 0 || bytes.ReceivedBytes == 0)
        && (bytes.OtherMeasured > 0 || bytes.OtherBytes == 0)
        && bytes != TransportBytes.None;

    /// <summary>
    /// Minor 1's RPC links (§3): none when they were not kept; otherwise each pair of distinct instances, the first in
    /// display order, at a strength a link can have, with the records it holds, each pair and strength once and in order.
    /// </summary>
    private static System.Collections.ObjectModel.ReadOnlyCollection<RpcPeerLinkTotal>? ReadLinks(IndexFileReader reader)
    {
        if (!reader.Flag())
        {
            return null;
        }

        int count = reader.Count(16 + 16 + 1 + 8);
        if (count > MaximumRpcLinks)
        {
            throw reader.Invalid($"it keeps more than {MaximumRpcLinks:N0} RPC links.");
        }

        var links = new List<RpcPeerLinkTotal>(count);
        for (int index = 0; index < count; index++)
        {
            var first = new ProcessInstanceId(reader.Identity());
            var second = new ProcessInstanceId(reader.Identity());
            var strength = (RelationStrength)reader.U8();
            long records = reader.I64();
            if (first.Value == Guid.Empty || second.Value == Guid.Empty
                || string.CompareOrdinal(first.ToString(), second.ToString()) >= 0
                || strength is not (RelationStrength.Correlated or RelationStrength.Candidate or RelationStrength.Conflicting)
                || records <= 0
                || (links.Count > 0 && CompareLinks(links[^1], first, second, strength) >= 0))
            {
                throw reader.Invalid("an RPC link names no pair of distinct instances in order, a strength a link cannot have, "
                    + "or no record, or is out of order.");
            }

            links.Add(new(first, second, strength, records));
        }

        return links.AsReadOnly();
    }

    private static int CompareLinks(RpcPeerLinkTotal before, ProcessInstanceId first, ProcessInstanceId second, RelationStrength strength)
    {
        int order = string.CompareOrdinal(before.First.ToString(), first.ToString());
        if (order == 0)
        {
            order = string.CompareOrdinal(before.Second.ToString(), second.ToString());
        }

        return order != 0 ? order : before.Strength.CompareTo(strength);
    }

    private static StoreDependency[] Named(SessionManifestV1 manifest, IReadOnlyList<string> names)
    {
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        return [.. manifest.Dependencies.Where(dependency =>
            dependency.Kind == StoreDependencyKind.Segment && wanted.Contains(dependency.Name))];
    }
}
