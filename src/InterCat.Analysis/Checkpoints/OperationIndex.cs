using System.Buffers;
using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// One generation's RPC calls and their other ends, and since minor 1 its HTTP exchanges, as derived from named segments,
/// published beside its derivation checkpoint so that a reopened session's first call ranking, listing or brush, or its
/// first HTTP listing, reads them rather than pairing every call record or grouping every buffer again
/// (`contracts/operation-index-v1.md`, P25). It is a derived index: it changes no observation, and the segments it covers
/// rebuild it (R1, R20).
/// </summary>
public sealed class OperationIndex
{
    /// <summary>The largest index a writer writes or a reader reads; a generation whose calls would take more keeps none.</summary>
    public const int MaximumBytes = 1024 * 1024 * 1024;

    private const string FilePrefix = "operation-index-";
    private const string FileSuffix = ".bin";
    private const ushort Major = 1;
    /// <summary>
    /// Minor 1 adds the HTTP exchanges (revision 441); a minor-0 index is read without them. Minor 2 (revision 917) says
    /// whether the generation holds COM call records and marks a call linked through one, under `rpc-call-peer-v2`, so
    /// every earlier index is of an earlier peer rule and derived again.
    /// </summary>
    private const ushort Minor = 2;
    private const byte ObservationRole = 1;
    private const byte FieldRole = 2;
    private const string What = "operation index";

    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    private OperationIndex(
        Guid sessionId,
        long derivedGeneration,
        IReadOnlyList<StoreDependency> segments,
        IReadOnlyList<StoreDependency> fieldSegments,
        RpcCallIndex? calls,
        RpcPeerIndex? peers,
        bool keepsExchanges,
        HttpExchangeIndex? exchanges)
    {
        SessionId = sessionId;
        DerivedGeneration = derivedGeneration;
        Segments = segments;
        FieldSegments = fieldSegments;
        Calls = calls;
        Peers = peers;
        KeepsExchanges = keepsExchanges;
        Exchanges = exchanges;
    }

    public Guid SessionId { get; }

    /// <summary>The generation whose segments the calls were derived from.</summary>
    public long DerivedGeneration { get; }

    /// <summary>The observation segments the calls were paired from, in the order the calls name them by.</summary>
    public IReadOnlyList<StoreDependency> Segments { get; }

    /// <summary>The source-field segments the calls' procedures, protocols and message ids were read from.</summary>
    public IReadOnlyList<StoreDependency> FieldSegments { get; }

    /// <summary>
    /// The calls, as a derivation of the covered segments with the instances they were read with gives them; null when
    /// the generation's calls were more than an index holds, and a reader pairs them from the segments.
    /// </summary>
    public RpcCallIndex? Calls { get; }

    /// <summary>The calls' other ends, as a derivation over <see cref="Calls"/> and the covered segments gives them.</summary>
    public RpcPeerIndex? Peers { get; }

    /// <summary>Whether the index was written with a section for the HTTP exchanges: from minor 1.</summary>
    public bool KeepsExchanges { get; }

    /// <summary>
    /// The HTTP exchanges, as a grouping of the covered segments with the instances they were read with gives them; null
    /// before minor 1, or when they were more than the index could hold besides the calls.
    /// </summary>
    public HttpExchangeIndex? Exchanges { get; }

    private static ReadOnlySpan<byte> Magic => "ICATOPIX"u8;

    /// <summary>The name an index published by <paramref name="generation"/> takes.</summary>
    public static string FileNameFor(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        return string.Create(CultureInfo.InvariantCulture, $"{FilePrefix}{generation:D10}{FileSuffix}");
    }

    /// <summary>
    /// The operation index <paramref name="manifest"/> names, or null when it names none. A generation naming two is
    /// refused, because a reader could not say which one describes it.
    /// </summary>
    public static StoreDependency? NamedBy(SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        StoreDependency[] named = [.. manifest.Dependencies.Where(IsOperationIndex)];
        return named.Length switch
        {
            0 => null,
            1 => named[0],
            _ => throw new InvalidDataException(
                $"Generation {manifest.Generation} names {named.Length} operation indexes; a generation names one."),
        };
    }

    /// <summary>Whether a dependency is an operation index: an index under this contract's name.</summary>
    public static bool IsOperationIndex(StoreDependency dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        return dependency.Kind == StoreDependencyKind.Index
            && dependency.Name.Length == FilePrefix.Length + 10 + FileSuffix.Length
            && dependency.Name.StartsWith(FilePrefix, StringComparison.Ordinal)
            && dependency.Name.EndsWith(FileSuffix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the calls cover exactly the segments a generation names, as its manifest records them and in its order:
    /// the calls name segments by their positions, so they serve only the segments in the order they were paired from.
    /// </summary>
    public bool Covers(IReadOnlyList<StoreDependency> segments, IReadOnlyList<StoreDependency> fieldSegments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(fieldSegments);
        return Segments.SequenceEqual(segments) && FieldSegments.SequenceEqual(fieldSegments);
    }

    /// <summary>
    /// Writes <paramref name="calls"/>, their other ends <paramref name="peers"/> and the exchanges
    /// <paramref name="exchanges"/>, derived from <paramref name="segments"/> - in the order they were read - and
    /// <paramref name="fieldSegments"/> of generation <paramref name="derivedGeneration"/>. Calls and exchanges are written
    /// in the canonical order of their first records (§3), so the same derivations always write the same bytes. Calls, or
    /// exchanges after them, that would take more than <paramref name="maximumBytes"/> are not kept: the index then says
    /// so, and a reader derives them from the segments.
    /// </summary>
    /// <returns>How many bytes were written.</returns>
    public static long Write(
        Stream destination,
        Guid sessionId,
        long derivedGeneration,
        IReadOnlyList<StoreDependency> segments,
        IReadOnlyList<StoreDependency> fieldSegments,
        RpcCallIndex calls,
        RpcPeerIndex peers,
        HttpExchangeIndex exchanges,
        long maximumBytes = MaximumBytes)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(fieldSegments);
        ArgumentNullException.ThrowIfNull(calls);
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentNullException.ThrowIfNull(exchanges);
        ArgumentOutOfRangeException.ThrowIfLessThan(derivedGeneration, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBytes, MaximumBytes);
        if (!ReferenceEquals(peers.Calls, calls))
        {
            throw new ArgumentException("These other ends were followed over other calls.", nameof(peers));
        }

        if (!calls.SegmentNames.SequenceEqual(segments.Select(segment => segment.Name))
            || !exchanges.SegmentNames.SequenceEqual(segments.Select(segment => segment.Name)))
        {
            throw new ArgumentException(
                "The calls or the exchanges were read from other segments, or in another order.", nameof(segments));
        }

        RequireCoveredSegments(segments, fieldSegments);
        return WriteCovering(
            destination,
            sessionId,
            derivedGeneration,
            segments,
            fieldSegments,
            calls.Clock,
            calls.StateBytes() + peers.StateBytes(),
            writer =>
            {
                int[] order = calls.InCanonicalOrder();
                calls.WriteState(writer, order);
                peers.WriteState(writer, order);
            },
            exchanges,
            maximumBytes);
    }

    /// <summary>
    /// Writes what <see cref="Write"/> writes of the calls <see cref="RpcCallIndex.Derive"/> pairs from
    /// <paramref name="observations"/> and <paramref name="fields"/> - the segments <paramref name="segments"/> and
    /// <paramref name="fieldSegments"/> name, in that order - their other ends and <paramref name="exchanges"/>: the same
    /// bytes, with the calls paired as they are written rather than held (revision 882). It holds the calls in flight,
    /// the call records of the segments whose readings overlap, and up to <paramref name="heldBytes"/> of calls paired;
    /// past that they wait, sorted, in a file under <paramref name="spillDirectory"/>, by default the system's temporary
    /// directory, deleted once they are written. Segments holding an ALPC send or receive, or a COM call record, are not
    /// written here, since those calls' other ends are followed over calls held whole: <see cref="Write"/> writes those.
    /// </summary>
    /// <returns>
    /// How many bytes were written; null, with nothing written, when the segments hold an ALPC send or receive or a COM
    /// call record.
    /// </returns>
    public static long? WriteFromSegments(
        Stream destination,
        Guid sessionId,
        long derivedGeneration,
        IReadOnlyList<StoreDependency> segments,
        IReadOnlyList<StoreDependency> fieldSegments,
        IReadOnlyList<SegmentReaderV1> observations,
        IReadOnlyList<SegmentReaderV1> fields,
        SourceClockDescriptor clock,
        HttpExchangeIndex exchanges,
        long maximumBytes = MaximumBytes,
        int heldBytes = RpcCallIndex.StreamedCallBytes,
        string? spillDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(fieldSegments);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(exchanges);
        ArgumentOutOfRangeException.ThrowIfLessThan(derivedGeneration, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBytes, MaximumBytes);
        if (!observations.Select(segment => segment.Published?.Name).SequenceEqual(segments.Select(segment => segment.Name))
            || !fields.Select(segment => segment.Published?.Name).SequenceEqual(fieldSegments.Select(segment => segment.Name))
            || !exchanges.SegmentNames.SequenceEqual(segments.Select(segment => segment.Name)))
        {
            throw new ArgumentException(
                "The segments, the source-field segments or the exchanges' segments are not the files named, in their order.",
                nameof(segments));
        }

        RequireCoveredSegments(segments, fieldSegments);
        using RpcCallIndex.StreamedCalls? calls =
            RpcCallIndex.StreamCalls(observations, fields, heldBytes, spillDirectory, cancellationToken);
        return calls is null
            ? null
            : WriteCovering(
                destination,
                sessionId,
                derivedGeneration,
                segments,
                fieldSegments,
                clock,
                calls.StateBytes() + RpcPeerIndex.NoneReadBytes,
                writer =>
                {
                    calls.WriteState(writer);
                    RpcPeerIndex.WriteNoneRead(writer);
                },
                exchanges,
                maximumBytes);
    }

    private static void RequireCoveredSegments(IReadOnlyList<StoreDependency> segments, IReadOnlyList<StoreDependency> fieldSegments)
    {
        if (segments.Concat(fieldSegments).Any(file => file.Kind != StoreDependencyKind.Segment)
            || segments.Concat(fieldSegments).Select(file => file.Name).Distinct(StringComparer.Ordinal).Count()
                != segments.Count + fieldSegments.Count)
        {
            throw new ArgumentException("A covered file is not a segment, or is named twice.", nameof(fieldSegments));
        }
    }

    /// <summary>
    /// Writes the index's header and covered files, the calls and their other ends by <paramref name="writeCalls"/> when
    /// their <paramref name="callsBytes"/> fit, and the exchanges when they fit after them.
    /// </summary>
    private static long WriteCovering(
        Stream destination,
        Guid sessionId,
        long derivedGeneration,
        IReadOnlyList<StoreDependency> segments,
        IReadOnlyList<StoreDependency> fieldSegments,
        SourceClockDescriptor clock,
        long callsBytes,
        Action<IndexFileWriter> writeCalls,
        HttpExchangeIndex exchanges,
        long maximumBytes)
    {
        var writer = new IndexFileWriter(destination, maximumBytes, What);
        writer.Raw(Magic);
        writer.U16(Major);
        writer.U16(Minor);
        writer.Str8(RpcCallIndex.OperationRule);
        writer.Str8(RpcPeerIndex.PeerRule);
        writer.Str8(ProcessInstanceIndex.BindingRule);
        writer.Identity(sessionId);
        writer.I64(derivedGeneration);
        writer.Identity(clock.Id.Value);
        writer.Identity(clock.HostId.Value);
        writer.Count(segments.Count + fieldSegments.Count);
        foreach ((StoreDependency file, byte role) in segments.Select(file => (file, ObservationRole))
            .Concat(fieldSegments.Select(file => (file, FieldRole))))
        {
            writer.U8(role);
            writer.Str8(file.Name);
            writer.I64(file.LengthBytes);
            writer.Str8(file.Digest);
        }

        // Whether the calls fit is known before any of them is written: one flag byte says, after what is written so far.
        bool held = writer.Written + 1 + callsBytes <= maximumBytes;
        writer.Flag(held);
        if (held)
        {
            writeCalls(writer);
        }

        // The exchanges follow under their own rule, kept when they fit after what is written.
        writer.Str8(HttpExchangeIndex.GroupingRule);
        bool exchangesHeld = writer.Written + 1 + exchanges.StateBytes() <= maximumBytes;
        writer.Flag(exchangesHeld);
        if (exchangesHeld)
        {
            exchanges.WriteState(writer);
        }

        writer.Flush();
        return writer.Written;
    }

    /// <summary>
    /// Reads an operation index of session <paramref name="sessionId"/> on <paramref name="clock"/>, binding its calls to
    /// <paramref name="processes"/> - the instances of the segments it covers. Anything the bytes do not hold, or hold in
    /// contradiction, is refused with <see cref="InvalidDataException"/> and the reason (§4); an index is rebuildable, so
    /// nothing is guessed from one.
    /// </summary>
    public static OperationIndex Read(
        ReadOnlyMemory<byte> bytes,
        Guid sessionId,
        SourceClockDescriptor clock,
        ProcessInstanceIndex processes,
        CancellationToken cancellationToken = default)
    {
        RequireClock(clock, processes);
        return Readable(Held(bytes), reader => ReadWhole(reader, sessionId, clock, processes, cancellationToken));
    }

    /// <summary>
    /// Reads the operation index <paramref name="dependency"/> names in <paramref name="directory"/> as
    /// <see cref="Read(ReadOnlyMemory{byte}, Guid, SourceClockDescriptor, ProcessInstanceIndex, CancellationToken)"/> reads
    /// its bytes, every refusal the same, as it streams the file rather than holding it whole beside its calls: at a busy
    /// hour's two million calls the file is 158 MB (revision 885). Bytes other than those published are refused as
    /// `SessionSegments.ReadVerified` refuses them.
    /// </summary>
    public static OperationIndex Read(
        IOwnedDirectory directory,
        StoreDependency dependency,
        Guid sessionId,
        SourceClockDescriptor clock,
        ProcessInstanceIndex processes,
        CancellationToken cancellationToken = default)
    {
        RequireClock(clock, processes);
        return SessionSegments.ReadVerified(directory, dependency, MaximumBytes, (stream, length) =>
            Readable(new IndexFileReader(stream, length, What), reader => ReadWhole(reader, sessionId, clock, processes, cancellationToken)));
    }

    /// <summary>
    /// What an operation index <see cref="Read"/> would read covers and keeps, the bytes checked as it checks them - every
    /// refusal the same, each call bound to <paramref name="processes"/> - without holding its calls: each is checked and
    /// let go. A writer asking whether its generation's index is current reads every call of it, two million at a busy
    /// hour's 4.46 million rows, which read whole would take more than its analysis budget (revision 882).
    /// </summary>
    public static VerifiedOperationIndex Verify(
        ReadOnlyMemory<byte> bytes,
        Guid sessionId,
        SourceClockDescriptor clock,
        ProcessInstanceIndex processes,
        CancellationToken cancellationToken = default)
    {
        RequireClock(clock, processes);
        return Readable(Held(bytes), reader => VerifyWhole(reader, sessionId, clock, processes, cancellationToken));
    }

    /// <summary>
    /// What <see cref="Read(IOwnedDirectory, StoreDependency, Guid, SourceClockDescriptor, ProcessInstanceIndex, CancellationToken)"/>
    /// would read of the index <paramref name="dependency"/> names, checked call by call as it streams, holding neither its
    /// calls nor the file.
    /// </summary>
    public static VerifiedOperationIndex Verify(
        IOwnedDirectory directory,
        StoreDependency dependency,
        Guid sessionId,
        SourceClockDescriptor clock,
        ProcessInstanceIndex processes,
        CancellationToken cancellationToken = default)
    {
        RequireClock(clock, processes);
        return SessionSegments.ReadVerified(directory, dependency, MaximumBytes, (stream, length) =>
            Readable(new IndexFileReader(stream, length, What), reader => VerifyWhole(reader, sessionId, clock, processes, cancellationToken)));
    }

    private static OperationIndex ReadWhole(
        IndexFileReader reader, Guid sessionId, SourceClockDescriptor clock, ProcessInstanceIndex processes, CancellationToken cancellationToken)
    {
        Head head = ReadHead(reader, sessionId, clock);
        RpcCallIndex? calls = null;
        RpcPeerIndex? peers = null;
        if (reader.Flag())
        {
            calls = RpcCallIndex.ReadState(reader, processes, clock, head.Positions, cancellationToken, out int[] placed);
            peers = RpcPeerIndex.ReadState(reader, calls, placed);
        }

        HttpExchangeIndex? exchanges = ReadExchanges(reader, head, processes, cancellationToken);
        reader.RequireEnd();
        return new OperationIndex(
            head.SessionId, head.DerivedGeneration, head.Segments, head.FieldSegments, calls, peers, head.Minor >= 1, exchanges);
    }

    private static VerifiedOperationIndex VerifyWhole(
        IndexFileReader reader, Guid sessionId, SourceClockDescriptor clock, ProcessInstanceIndex processes, CancellationToken cancellationToken)
    {
        Head head = ReadHead(reader, sessionId, clock);
        bool keepsCalls = reader.Flag();
        if (keepsCalls)
        {
            RpcPeerIndex.VerifyState(reader, RpcCallIndex.VerifyState(reader, processes, head.Positions.Length, cancellationToken));
        }

        _ = ReadExchanges(reader, head, processes, cancellationToken);
        reader.RequireEnd();
        return new VerifiedOperationIndex(
            head.SessionId, head.DerivedGeneration, head.Segments, head.FieldSegments, keepsCalls, head.Minor >= 1);
    }

    private static void RequireClock(SourceClockDescriptor clock, ProcessInstanceIndex processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        if (processes.Clock != clock.Id)
        {
            throw new ArgumentException("The instances were derived on another clock than the calls are read on.", nameof(processes));
        }
    }

    /// <summary>A reader of an index's bytes held whole, refusing them past the bound.</summary>
    private static IndexFileReader Held(ReadOnlyMemory<byte> bytes) => bytes.Length > MaximumBytes
        ? throw new InvalidDataException($"The {What} is not readable: it is larger than {MaximumBytes / (1024 * 1024)} MiB.")
        : new IndexFileReader(bytes, What);

    /// <summary>
    /// Reads an index with <paramref name="read"/>. Calls are bound by the instances' own rules, which refuse a PID they were
    /// not derived from with the exceptions a derivation raises; here they say only that the index is unreadable.
    /// </summary>
    private static T Readable<T>(IndexFileReader opened, Func<IndexFileReader, T> read)
    {
        try
        {
            return read(opened);
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

    /// <summary>The exchanges an index of <paramref name="head"/>'s minor keeps under its rule, or null where it keeps none.</summary>
    private static HttpExchangeIndex? ReadExchanges(
        IndexFileReader reader, Head head, ProcessInstanceIndex processes, CancellationToken cancellationToken)
    {
        if (head.Minor < 1)
        {
            return null;
        }

        string grouping = reader.Str8();
        if (grouping != HttpExchangeIndex.GroupingRule)
        {
            throw reader.Invalid(
                $"its exchanges were grouped under {grouping}, and this build groups under {HttpExchangeIndex.GroupingRule}.");
        }

        return reader.Flag() ? HttpExchangeIndex.ReadState(reader, processes, head.Positions, cancellationToken) : null;
    }

    /// <summary>An index's head: its format's minor, the session and generation it was derived at, and the files it covers.</summary>
    private static Head ReadHead(IndexFileReader reader, Guid sessionId, SourceClockDescriptor clock)
    {
        if (!reader.Matches(Magic))
        {
            throw reader.Invalid("it does not begin as an operation index does.");
        }

        ushort major = reader.U16();
        ushort minor = reader.U16();
        if (major != Major || minor > Minor)
        {
            throw reader.Invalid($"it is format {major}.{minor}, and this build reads {Major}.0 to {Major}.{Minor}.");
        }

        string operation = reader.Str8();
        string peer = reader.Str8();
        string binding = reader.Str8();
        if (operation != RpcCallIndex.OperationRule || peer != RpcPeerIndex.PeerRule || binding != ProcessInstanceIndex.BindingRule)
        {
            throw reader.Invalid(
                $"it was derived under {operation}, {peer} and {binding}, and this build derives under "
                + $"{RpcCallIndex.OperationRule}, {RpcPeerIndex.PeerRule} and {ProcessInstanceIndex.BindingRule}.");
        }

        Guid session = reader.Identity();
        if (session != sessionId)
        {
            throw reader.Invalid($"it belongs to session {session:N}, not {sessionId:N}.");
        }

        long derivedGeneration = reader.I64();
        Guid clockId = reader.Identity();
        Guid host = reader.Identity();
        if (derivedGeneration < 1 || clockId != clock.Id.Value || host != clock.HostId.Value)
        {
            throw reader.Invalid("it was derived on another clock or host, or from no generation.");
        }

        // A covered file holds its role, a name of at least one character, its length and its digest. The observation
        // segments come first, in the order the calls name them by.
        int count = reader.Count(1 + 2 + 8 + 1);
        var segments = new List<StoreDependency>();
        var fields = new List<StoreDependency>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            byte role = reader.U8();
            string name = reader.Str8();
            long length = reader.I64();
            string digest = reader.Str8();
            if (role is not (ObservationRole or FieldRole)
                || (role == ObservationRole && fields.Count > 0)
                || OwnedFileName.Validate(name) is not null
                || length < 0
                || !IsDigest(digest)
                || !names.Add(name))
            {
                throw reader.Invalid("a covered file is not a named, measured segment, once, with the observation segments first.");
            }

            (role == ObservationRole ? segments : fields).Add(new(name, StoreDependencyKind.Segment, length, digest));
        }

        return new(minor, session, derivedGeneration, segments.AsReadOnly(), fields.AsReadOnly(), [.. segments.Select(segment => segment.Name)]);
    }

    private static bool IsDigest(string digest) =>
        digest.Length == 71
        && digest.StartsWith("sha256:", StringComparison.Ordinal)
        && !digest.AsSpan(7).ContainsAnyExcept(LowerHex);

    /// <summary>An index's head as read: its minor, session, generation and covered files, and the observation segments' names by position.</summary>
    private sealed record Head(
        ushort Minor,
        Guid SessionId,
        long DerivedGeneration,
        IReadOnlyList<StoreDependency> Segments,
        IReadOnlyList<StoreDependency> FieldSegments,
        string[] Positions);
}

/// <summary>
/// What an operation index covers and keeps, as <see cref="OperationIndex.Verify"/> found it: readable as
/// <see cref="OperationIndex.Read"/> reads it, without its calls held.
/// </summary>
/// <param name="SessionId">The session it belongs to.</param>
/// <param name="DerivedGeneration">The generation its calls and exchanges were derived at.</param>
/// <param name="Segments">The observation segments it covers, in the order its calls name them by.</param>
/// <param name="FieldSegments">The source-field segments it covers.</param>
/// <param name="KeepsCalls">Whether it keeps its calls, which it does unless they would not fit.</param>
/// <param name="KeepsExchanges">Whether its minor keeps exchanges, as since minor 1.</param>
public sealed record VerifiedOperationIndex(
    Guid SessionId,
    long DerivedGeneration,
    IReadOnlyList<StoreDependency> Segments,
    IReadOnlyList<StoreDependency> FieldSegments,
    bool KeepsCalls,
    bool KeepsExchanges)
{
    /// <summary>Whether it covers exactly <paramref name="segments"/>, in that order, and <paramref name="fieldSegments"/>.</summary>
    public bool Covers(IReadOnlyList<StoreDependency> segments, IReadOnlyList<StoreDependency> fieldSegments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(fieldSegments);
        return Segments.SequenceEqual(segments) && FieldSegments.SequenceEqual(fieldSegments);
    }
}
