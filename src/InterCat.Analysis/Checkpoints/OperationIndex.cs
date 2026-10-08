using System.Buffers;
using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// One generation's RPC calls and their other ends as derived from named segments, published beside its derivation
/// checkpoint so that a reopened session's first call ranking, listing or brush reads them rather than pairing every
/// call record again (`contracts/operation-index-v1.md`, P25). It is a derived index: it changes no observation, and the
/// segments it covers rebuild it (R1, R20).
/// </summary>
public sealed class OperationIndex
{
    /// <summary>The largest index a writer writes or a reader reads; a generation whose calls would take more keeps none.</summary>
    public const int MaximumBytes = 1024 * 1024 * 1024;

    private const string FilePrefix = "operation-index-";
    private const string FileSuffix = ".bin";
    private const ushort Major = 1;
    private const ushort Minor = 0;
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
        RpcPeerIndex? peers)
    {
        SessionId = sessionId;
        DerivedGeneration = derivedGeneration;
        Segments = segments;
        FieldSegments = fieldSegments;
        Calls = calls;
        Peers = peers;
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
    /// Writes <paramref name="calls"/> and their other ends <paramref name="peers"/>, derived from
    /// <paramref name="segments"/> - in the order the calls were paired from - and <paramref name="fieldSegments"/> of
    /// generation <paramref name="derivedGeneration"/>. The calls are written in the canonical order of their first
    /// records (§3), so the same derivation always writes the same bytes. Calls that would take more than
    /// <paramref name="maximumBytes"/> are not kept: the index then says so, and a reader pairs them from the segments.
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
        long maximumBytes = MaximumBytes)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(fieldSegments);
        ArgumentNullException.ThrowIfNull(calls);
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentOutOfRangeException.ThrowIfLessThan(derivedGeneration, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBytes, MaximumBytes);
        if (!ReferenceEquals(peers.Calls, calls))
        {
            throw new ArgumentException("These other ends were followed over other calls.", nameof(peers));
        }

        if (!calls.SegmentNames.SequenceEqual(segments.Select(segment => segment.Name)))
        {
            throw new ArgumentException("The calls were paired from other segments, or in another order.", nameof(segments));
        }

        if (segments.Concat(fieldSegments).Any(file => file.Kind != StoreDependencyKind.Segment)
            || segments.Concat(fieldSegments).Select(file => file.Name).Distinct(StringComparer.Ordinal).Count()
                != segments.Count + fieldSegments.Count)
        {
            throw new ArgumentException("A covered file is not a segment, or is named twice.", nameof(fieldSegments));
        }

        var writer = new IndexFileWriter(destination, maximumBytes, What);
        writer.Raw(Magic);
        writer.U16(Major);
        writer.U16(Minor);
        writer.Str8(RpcCallIndex.OperationRule);
        writer.Str8(RpcPeerIndex.PeerRule);
        writer.Str8(ProcessInstanceIndex.BindingRule);
        writer.Identity(sessionId);
        writer.I64(derivedGeneration);
        writer.Identity(calls.Clock.Id.Value);
        writer.Identity(calls.Clock.HostId.Value);
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
        bool held = writer.Written + 1 + calls.StateBytes() + peers.StateBytes() <= maximumBytes;
        writer.Flag(held);
        if (held)
        {
            int[] order = calls.InCanonicalOrder();
            calls.WriteState(writer, order);
            peers.WriteState(writer, order);
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
        ArgumentNullException.ThrowIfNull(processes);
        if (processes.Clock != clock.Id)
        {
            throw new ArgumentException("The instances were derived on another clock than the calls are read on.", nameof(processes));
        }

        if (bytes.Length > MaximumBytes)
        {
            throw new InvalidDataException($"The {What} is not readable: it is larger than {MaximumBytes / (1024 * 1024)} MiB.");
        }

        // Calls are bound by the instances' own rules, which refuse a PID they were not derived from with the exceptions a
        // derivation raises; here they say only that the index is unreadable.
        try
        {
            return ReadCore(new IndexFileReader(bytes, What), sessionId, clock, processes, cancellationToken);
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

    private static OperationIndex ReadCore(
        IndexFileReader reader,
        Guid sessionId,
        SourceClockDescriptor clock,
        ProcessInstanceIndex processes,
        CancellationToken cancellationToken)
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

        RpcCallIndex? calls = null;
        RpcPeerIndex? peers = null;
        if (reader.Flag())
        {
            calls = RpcCallIndex.ReadState(
                reader, processes, clock, [.. segments.Select(segment => segment.Name)], cancellationToken, out int[] placed);
            peers = RpcPeerIndex.ReadState(reader, calls, placed);
        }

        reader.RequireEnd();
        return new(session, derivedGeneration, segments.AsReadOnly(), fields.AsReadOnly(), calls, peers);
    }

    private static bool IsDigest(string digest) =>
        digest.Length == 71
        && digest.StartsWith("sha256:", StringComparison.Ordinal)
        && !digest.AsSpan(7).ContainsAnyExcept(LowerHex);
}
