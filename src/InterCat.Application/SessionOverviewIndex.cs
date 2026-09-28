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

    /// <summary>Minor 1 adds the RPC link totals after the counts (§3); a minor-0 overview holds none and is still read.</summary>
    private const ushort Minor = 1;

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

        writer.Flush();
        return writer.Written;
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
            reader.RequireEnd();
            return rows == withoutTime
                ? (new(rows, withoutTime, null, null, null) { RpcLinks = untimedLinks }, segments.AsReadOnly())
                : throw reader.Invalid("it has timed rows and no extent.");
        }

        long start = reader.I64();
        long end = reader.I64();
        if (end <= start)
        {
            throw reader.Invalid("its extent is empty.");
        }

        var extent = new TimeRange(start, end);
        var main = new TimelineColumns(extent, SessionOverviewProjector.MaximumTimelineBuckets, tallyMechanisms: true);
        if (reader.Count(1) != main.Counts.Count)
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
        if (reader.I64() != span.StartTicks || reader.I64() != span.EndTicks || reader.Count(1) != columns)
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
        reader.RequireEnd();
        return total == timed && mapped == timed
            ? (new(rows, withoutTime, extent, main, minimap) { RpcLinks = links }, segments.AsReadOnly())
            : throw reader.Invalid("its columns do not add up to its timed rows.");
    }

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
