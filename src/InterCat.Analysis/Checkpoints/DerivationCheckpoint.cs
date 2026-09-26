using System.Buffers;
using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// The state of one generation's process instances and transport relations as derived from named segments, published
/// so that opening the session builds both from it and reads only the segments it does not cover
/// (`contracts/derivation-checkpoint-v1.md`). It is a derived index: it changes no observation, and the segments it
/// covers rebuild it (R1, R20).
/// </summary>
public sealed class DerivationCheckpoint
{
    /// <summary>The largest checkpoint a writer writes or a reader reads.</summary>
    public const int MaximumBytes = 512 * 1024 * 1024;

    private const string FilePrefix = "derivation-checkpoint-";
    private const string FileSuffix = ".bin";
    private const ushort Major = 1;
    private const ushort Minor = 0;
    private const byte ObservationRole = 1;
    private const byte FieldRole = 2;

    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    private DerivationCheckpoint(
        Guid sessionId,
        long derivedGeneration,
        IReadOnlyList<StoreDependency> segments,
        IReadOnlyList<StoreDependency> fieldSegments,
        ProcessInstanceIndex processes,
        TransportRelationIndex relations)
    {
        SessionId = sessionId;
        DerivedGeneration = derivedGeneration;
        Segments = segments;
        FieldSegments = fieldSegments;
        Processes = processes;
        Relations = relations;
    }

    public Guid SessionId { get; }

    /// <summary>The generation whose segments the state was derived from.</summary>
    public long DerivedGeneration { get; }

    /// <summary>The observation segments the state covers.</summary>
    public IReadOnlyList<StoreDependency> Segments { get; }

    /// <summary>The source-field segments the state covers.</summary>
    public IReadOnlyList<StoreDependency> FieldSegments { get; }

    /// <summary>The instances, as a derivation of the covered segments gives them.</summary>
    public ProcessInstanceIndex Processes { get; }

    /// <summary>The relations, as a derivation of the covered segments with <see cref="Processes"/> gives them.</summary>
    public TransportRelationIndex Relations { get; }

    private static ReadOnlySpan<byte> Magic => "ICATDCKP"u8;

    /// <summary>The name a checkpoint published by <paramref name="generation"/> takes.</summary>
    public static string FileNameFor(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        return string.Create(CultureInfo.InvariantCulture, $"{FilePrefix}{generation:D10}{FileSuffix}");
    }

    /// <summary>
    /// The checkpoint <paramref name="manifest"/> names, or null when it names none. A generation naming two is refused,
    /// because a reader could not say which one describes it.
    /// </summary>
    public static StoreDependency? NamedBy(SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        StoreDependency[] named = [.. manifest.Dependencies.Where(IsCheckpoint)];
        return named.Length switch
        {
            0 => null,
            1 => named[0],
            _ => throw new InvalidDataException(
                $"Generation {manifest.Generation} names {named.Length} derivation checkpoints; a generation names one."),
        };
    }

    /// <summary>Whether a dependency is a derivation checkpoint: an index under this contract's name.</summary>
    public static bool IsCheckpoint(StoreDependency dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        return dependency.Kind == StoreDependencyKind.Index
            && dependency.Name.Length == FilePrefix.Length + 10 + FileSuffix.Length
            && dependency.Name.StartsWith(FilePrefix, StringComparison.Ordinal)
            && dependency.Name.EndsWith(FileSuffix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the state covers exactly these segments: each one covered, and no other, told apart by the file its
    /// generation publishes it as. The derivations are then the checkpoint's as they stand.
    /// </summary>
    public bool Covers(IReadOnlyList<SegmentReaderV1> segments, IReadOnlyList<SegmentReaderV1> fieldSegments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(fieldSegments);
        return Same(Segments, segments) && Same(FieldSegments, fieldSegments);

        static bool Same(IReadOnlyList<StoreDependency> covered, IReadOnlyList<SegmentReaderV1> offered) =>
            covered.Count == offered.Count
            && offered.All(segment => segment.Published is not null)
            && covered.ToHashSet().SetEquals(offered.Select(segment => segment.Published!));
    }

    /// <summary>
    /// Writes the state of <paramref name="processes"/> and <paramref name="relations"/>, derived from the segments of
    /// generation <paramref name="derivedGeneration"/>, in the canonical order of §3. The same derivations always write
    /// the same bytes.
    /// </summary>
    /// <returns>How many bytes were written.</returns>
    public static long Write(
        Stream destination,
        Guid sessionId,
        long derivedGeneration,
        ProcessInstanceIndex processes,
        TransportRelationIndex relations)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(relations);
        ArgumentOutOfRangeException.ThrowIfLessThan(derivedGeneration, 1);
        if (!ReferenceEquals(relations.Processes, processes))
        {
            throw new ArgumentException(
                "These relations were derived with other instances, so their holders name positions in another index.",
                nameof(relations));
        }

        IReadOnlyCollection<StoreDependency> read = processes.FilesRead
            ?? throw new ArgumentException(
                "A segment these instances read has no published identity, so no reader could tell what they cover.",
                nameof(processes));
        IReadOnlyCollection<StoreDependency> related = relations.FilesRead
            ?? throw new ArgumentException(
                "A segment these relations read has no published identity, so no reader could tell what they cover.",
                nameof(relations));
        if (!related.All(read.Contains))
        {
            throw new ArgumentException("The relations read a segment the instances did not.", nameof(relations));
        }

        StoreDependency[] covered = [.. read];
        Array.Sort(covered, static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        if (covered.Any(file => file.Kind != StoreDependencyKind.Segment))
        {
            throw new ArgumentException("The instances were read from a file that is not a segment.", nameof(processes));
        }

        (CaptureId? capture, NormalizerContractVersion? derivation) = processes.Provenance;
        var writer = new CheckpointWriter(destination);
        foreach (byte value in Magic)
        {
            writer.U8(value);
        }

        writer.U16(Major);
        writer.U16(Minor);
        writer.Str8(ProcessInstanceIndex.BindingRule);
        writer.Str8(TransportRelationIndex.RelationRule);
        writer.Identity(sessionId);
        writer.I64(derivedGeneration);
        writer.Identity(processes.Clock.Value);
        writer.Identity(processes.Host.Value);
        writer.Identity(capture?.Value ?? Guid.Empty);
        writer.U32(derivation?.Value ?? 0);
        writer.Count(covered.Length);
        foreach (StoreDependency file in covered)
        {
            writer.U8(related.Contains(file) ? ObservationRole : FieldRole);
            writer.Str8(file.Name);
            writer.I64(file.LengthBytes);
            writer.Str8(file.Digest);
        }

        processes.WriteState(writer);
        relations.WriteState(writer);
        writer.Flush();
        return writer.Written;
    }

    /// <summary>
    /// Reads a checkpoint of session <paramref name="sessionId"/> on <paramref name="clock"/>. Anything the bytes do not
    /// hold, or hold in contradiction, is refused with <see cref="InvalidDataException"/> and the reason (§4); a
    /// checkpoint is rebuildable, so nothing is guessed from one.
    /// </summary>
    public static DerivationCheckpoint Read(ReadOnlyMemory<byte> bytes, Guid sessionId, SourceClockDescriptor clock)
    {
        if (bytes.Length > MaximumBytes)
        {
            throw CheckpointReader.Invalid($"it is larger than {MaximumBytes / (1024 * 1024)} MiB.");
        }

        // Instances are rebuilt by the derivation's own rules, which refuse what no segment could have held with the
        // exceptions a derivation raises; here they say only that the checkpoint is unreadable.
        try
        {
            return ReadCore(new CheckpointReader(bytes), sessionId, clock);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The derivation checkpoint is not readable: " + exception.Message, exception);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("The derivation checkpoint is not readable: " + exception.Message, exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("The derivation checkpoint is not readable: " + exception.Message, exception);
        }
    }

    private static DerivationCheckpoint ReadCore(CheckpointReader reader, Guid sessionId, SourceClockDescriptor clock)
    {
        foreach (byte expected in Magic)
        {
            if (reader.U8() != expected)
            {
                throw CheckpointReader.Invalid("it does not begin as a derivation checkpoint does.");
            }
        }

        ushort major = reader.U16();
        ushort minor = reader.U16();
        if (major != Major || minor != Minor)
        {
            throw CheckpointReader.Invalid($"it is format {major}.{minor}, and this build reads {Major}.{Minor}.");
        }

        string binding = reader.Str8();
        string relation = reader.Str8();
        if (binding != ProcessInstanceIndex.BindingRule || relation != TransportRelationIndex.RelationRule)
        {
            throw CheckpointReader.Invalid(
                $"it was derived under {binding} and {relation}, and this build derives under "
                + $"{ProcessInstanceIndex.BindingRule} and {TransportRelationIndex.RelationRule}.");
        }

        Guid session = reader.Identity();
        if (session != sessionId)
        {
            throw CheckpointReader.Invalid($"it belongs to session {session:N}, not {sessionId:N}.");
        }

        long derivedGeneration = reader.I64();
        Guid clockId = reader.Identity();
        Guid host = reader.Identity();
        if (derivedGeneration < 1 || clockId != clock.Id.Value || host != clock.HostId.Value)
        {
            throw CheckpointReader.Invalid("it was derived on another clock or host, or from no generation.");
        }

        Guid captured = reader.Identity();
        uint normalizer = reader.U32();
        if ((captured == Guid.Empty) != (normalizer == 0))
        {
            throw CheckpointReader.Invalid("it names a capture without its normalizer derivation, or one without the other.");
        }

        CaptureId? capture = normalizer == 0 ? null : new CaptureId(captured);
        NormalizerContractVersion? derivation = normalizer == 0 ? null : new NormalizerContractVersion(normalizer);

        // A covered file holds its role, a name of at least one character, its length and its digest.
        int count = reader.Count(1 + 2 + 8 + 1);
        var segments = new List<StoreDependency>();
        var fields = new List<StoreDependency>();
        string? previous = null;
        for (int index = 0; index < count; index++)
        {
            byte role = reader.U8();
            string name = reader.Str8();
            long length = reader.I64();
            string digest = reader.Str8();
            if (role is not (ObservationRole or FieldRole)
                || OwnedFileName.Validate(name) is not null
                || length < 0
                || !IsDigest(digest)
                || (previous is not null && string.CompareOrdinal(previous, name) >= 0))
            {
                throw CheckpointReader.Invalid("a covered file is not a named, measured segment in name order.");
            }

            (role == ObservationRole ? segments : fields).Add(new(name, StoreDependencyKind.Segment, length, digest));
            previous = name;
        }

        if ((count > 0) != capture.HasValue)
        {
            throw CheckpointReader.Invalid("it covers segments without the capture they belong to, or names a capture and covers none.");
        }

        ProcessInstanceIndex processes = ProcessInstanceIndex.ReadState(reader, clock, capture, derivation, [.. segments, .. fields]);
        TransportRelationIndex relations = TransportRelationIndex.ReadState(reader, processes, [.. segments]);
        reader.RequireEnd();
        return new(session, derivedGeneration, segments.AsReadOnly(), fields.AsReadOnly(), processes, relations);
    }

    private static bool IsDigest(string digest) =>
        digest.Length == 71
        && digest.StartsWith("sha256:", StringComparison.Ordinal)
        && !digest.AsSpan(7).ContainsAnyExcept(LowerHex);
}
