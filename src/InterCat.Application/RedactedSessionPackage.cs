using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>Where a redacted package is being made. Each stage reads every row once.</summary>
public enum RedactedPackageStage
{
    Inspecting = 1,
    Writing = 2,
    Verifying = 3,
}

/// <summary>How far one stage is: rows done of the rows it reads.</summary>
public sealed record RedactedPackageProgress(RedactedPackageStage Stage, long Done, long Total);

/// <summary>
/// What a session holds and what a redacted package would make of it, measured without writing anything. The source's
/// session ID and generation are shown to the person making the package; neither is written into it.
/// </summary>
public sealed record RedactedSessionPackagePreview(
    Guid SourceSessionId,
    long SourceGeneration,
    long Rows,
    long SourceFieldRows,
    long SourceFieldRowsRedacted,
    int SourceJournals,
    long SourceJournalBytes,
    bool CoverageLedger,
    bool NormalizerPlan,
    bool CaptureFinalization);

/// <summary>A published, verified package: where it is, its new identity, and what the verification covered.</summary>
public sealed record RedactedSessionPackageResult(
    string Directory,
    Guid SessionId,
    RedactedSessionPackagePreview Source,
    RedactedSessionCounts Counts,
    int FilesVerified,
    int IdentityNeedles,
    int NameNeedles);

/// <summary>
/// §11.3's redacted normalized session: a new, self-contained session that reopens in every InterCat reader, built from
/// an allowlisted projection of a verified session's rows and source fields (`contracts/redacted-session-v1.md`, I22).
/// </summary>
/// <remarks>
/// <para>
/// The package gets a fresh session, capture, clock and host identity. Its journal holds one synthetic metadata record per
/// row - no body, no extended item, the pseudonymous descriptor and header the row carries - so the original-record
/// drill-down resolves only into the package. Names, process and thread ids, addresses, ports, identifiers, providers,
/// schemas, start sequences and kernel object values are pseudonyms consistent only inside the package (<see
/// cref="RedactedSessionPseudonyms"/>). Native readings are moved so the capture epoch is a whole number of seconds from
/// zero: relative time is exact, while the boot-relative or wall-clock reading that would date the capture is gone.
/// </para>
/// <para>
/// Left out: the original journal chunks and any ETL, the normalizer plan (a package is not re-derivable), the capture
/// finalization marker, body and extended bytes, original raw locators, and wall-clock FILETIME fields. A coverage ledger
/// is rewritten under the same pseudonyms, so coverage and loss stay truthful.
/// </para>
/// <para>
/// The package is built in a private directory beside the destination, reopened and checked as a recipient would see it -
/// inventory, every reference, every value against the issued pseudonyms, the counts, sums and distinct values of the
/// source, and a byte scan for source identities and names - and only then renamed into place.
/// </para>
/// </remarks>
public static class RedactedSessionPackage
{
    public const string Contract = "intercat-redacted-normalized-session-v1";
    public const string Policy = "normalized-session-redaction-v1";

    /// <summary>
    /// The rows one package holds. Pseudonym tables and the source-field join grow with a session's distinct values, so
    /// a larger session is refused with this bound named rather than exhausting memory.
    /// </summary>
    public const long MaximumRows = 1_000_000;

    public const string Warning = "Pseudonymized, not anonymous. Relative times, sizes, counts, status codes and the "
        + "shape of the workload can still identify a system or its activity. Review the package before sharing it.";

    public const string PseudonymScope = "Pseudonyms are random and consistent only within this package. No mapping back "
        + "to an original value is kept, and a second package of the same session uses different ones.";

    private const int ProgressInterval = 16_384;
    private static readonly FactKey SyntheticFact = FactKey.Create("redacted-normalized-observation-v1");

    /// <summary>Measures what a package of this session would hold, reading every row once and writing nothing.</summary>
    public static RedactedSessionPackagePreview Preview(
        SessionStore source,
        IProgress<RedactedPackageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        using EvidenceLease lease = source.AcquireLease();
        SourceScan scan = Inspect(source.Root, lease.Manifest, new RedactedSessionPseudonyms(), progress, cancellationToken);
        return scan.Preview;
    }

    /// <summary>
    /// Builds, verifies and publishes a package at <paramref name="destination"/>, which must not exist and must not
    /// overlap the source. Nothing appears under that name unless the complete package verified.
    /// </summary>
    public static RedactedSessionPackageResult Create(
        SessionStore source,
        string destination,
        DateTimeOffset createdUtc,
        IProgress<RedactedPackageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        string sourcePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Root.Path));
        StringComparison paths = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (target.Equals(sourcePath, paths)
            || target.StartsWith(sourcePath + Path.DirectorySeparatorChar, paths)
            || sourcePath.StartsWith(target + Path.DirectorySeparatorChar, paths))
        {
            throw new ArgumentException(
                "A redacted package is written to its own new directory, outside the session it is made from.",
                nameof(destination));
        }

        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException($"{target} already exists. A redacted package is written only to a new directory.");
        }

        string parent = Path.GetDirectoryName(target)
            ?? throw new ArgumentException("The destination has no parent directory.", nameof(destination));

        using EvidenceLease lease = source.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        var pseudonyms = new RedactedSessionPseudonyms();
        SourceScan scan = Inspect(source.Root, manifest, pseudonyms, progress, cancellationToken);
        (RedactedPackageLeakScanner identities, RedactedPackageLeakScanner names) = Needles(manifest, scan, pseudonyms);

        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, $"{Path.GetFileName(target)}.partial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        bool published = false;
        try
        {
            Written written = Write(source.Root, manifest, scan, pseudonyms, staging, createdUtc, progress,
                cancellationToken);
            int files = Verify(staging, written, scan, pseudonyms, identities, names, progress, cancellationToken);

            // The package takes its requested name only once a complete, verified generation exists.
            Directory.Move(staging, target);
            published = true;
            return new(target, written.SessionId, scan.Preview, written.Counts, files, identities.Count, names.Count);
        }
        finally
        {
            if (!published) RemoveStaging(staging);
        }
    }

    /// <summary>
    /// Removes an unpublished package. Only files directly under the randomly named stage are removed; anything nested,
    /// or a stage replaced by a link, is left for inspection rather than traversed.
    /// </summary>
    private static void RemoveStaging(string staging)
    {
        try
        {
            if (!Directory.Exists(staging) || (File.GetAttributes(staging) & FileAttributes.ReparsePoint) != 0) return;
            foreach (string file in Directory.EnumerateFiles(staging)) File.Delete(file);
            if (!Directory.EnumerateFileSystemEntries(staging).Any()) Directory.Delete(staging);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A scanner or indexer may still hold a file for a moment. The stage keeps its ".partial-" name, never the
            // package's, so nothing can mistake it for a finished package.
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Inspection: every source value the package will replace, and what the package must reproduce.
    // ---------------------------------------------------------------------------------------------------------------

    private readonly record struct RecordAddress(uint Stream, uint Epoch, ulong Ordinal, FactKey FactKey);

    private readonly record struct Descriptor(Guid Provider, ushort EventId, byte Version, string Fingerprint);

    private enum FieldTransform
    {
        Keep = 1,
        Number = 2,
        Sequence = 3,
        Pointer = 4,
        Redact = 5,
    }

    /// <summary>
    /// The source-field allowlist. A field not named here - a wall-clock FILETIME, or any field a later version adds - is
    /// carried with its value withheld and marked <see cref="FieldAvailability.Redacted"/>, never passed through.
    /// </summary>
    private static FieldTransform TransformOf(SourceField field) => field switch
    {
        SourceField.ProcessSessionId or SourceField.RpcProcedureNumber or SourceField.RpcProtocolSequence
            or SourceField.FileByteOffset => FieldTransform.Keep,
        SourceField.ParentProcessId or SourceField.IssuingThreadId => FieldTransform.Number,
        SourceField.ProcessStartSequence or SourceField.ParentStartSequence => FieldTransform.Sequence,
        SourceField.ConnectionId or SourceField.IoRequestPacket or SourceField.FileObject
            or SourceField.FileKey => FieldTransform.Pointer,
        _ => FieldTransform.Redact,
    };

    private static FieldTransform TransformOf(SourceFieldRowV1 field) =>
        field.Availability != FieldAvailability.Present ? FieldTransform.Keep
            : field.Text is not null ? FieldTransform.Redact
            : TransformOf(field.Field);

    private sealed class SourceScan
    {
        public required SourceClockDescriptor Clock { get; init; }

        public required CaptureId Capture { get; init; }

        public required NormalizerContractVersion Derivation { get; init; }

        public required IReadOnlyList<string> Segments { get; init; }

        public required IReadOnlyList<string> FieldSegments { get; init; }

        public required long Rows { get; init; }

        public required HashSet<RecordAddress> FieldAddresses { get; init; }

        public required IReadOnlyList<Descriptor> Descriptors { get; init; }

        /// <summary>The package's capture epoch: whole seconds from zero, so every timed reading is non-negative.</summary>
        public required long PackageEpoch { get; init; }

        public required CoverageLedgerV1? Ledger { get; init; }

        public required PackageTally Tally { get; init; }

        public required RedactedSessionPackagePreview Preview { get; init; }

        /// <summary>Moves a source reading onto the package's clock, keeping its distance from the capture epoch exactly.</summary>
        public long Shift(long nativeTicks)
        {
            Int128 moved = (Int128)nativeTicks - Clock.CaptureEpochNativeTicks + PackageEpoch;
            return moved >= long.MinValue && moved <= long.MaxValue
                ? (long)moved
                : throw new InvalidDataException(
                    $"A native reading of {nativeTicks} cannot be placed relative to the capture epoch; its timestamp is "
                    + "outside what the source clock can express. The package refuses rather than alter it.");
        }
    }

    private static SourceScan Inspect(
        IOwnedDirectory root,
        SessionManifestV1 manifest,
        RedactedSessionPseudonyms pseudonyms,
        IProgress<RedactedPackageProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (manifest.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.RedactionPolicy))
        {
            // A package of a package would be fine to build, but it would read as twice-redacted evidence of an unknown
            // source. Sharing the existing package is the same disclosure with none of the confusion.
            throw new InvalidOperationException(
                "This session is already a redacted package. Share it as it is; a package is not built from a package.");
        }

        SourceClockDescriptor clock = SessionSegments.SourceClock(root, manifest)
            ?? throw new InvalidDataException(
                "This generation names no source clock, so its readings cannot be moved onto a package clock.");
        IReadOnlyList<string> segmentNames = SessionSegments.Names(manifest);
        IReadOnlyList<string> fieldNames = SessionSegments.FieldNames(manifest);
        if (segmentNames.Count == 0)
        {
            throw new InvalidOperationException(
                "This session holds no normalized rows to package. An evidence-only session is derived first "
                + "(icat follow), and its derived session is what a package is made from.");
        }

        // The bound is checked from the segment headers alone, so a session too large to package is refused at once.
        long total = segmentNames.Sum(name => (long)SessionSegments.DeclaredRowCount(root, manifest, name));
        if (total > MaximumRows)
        {
            throw new InvalidOperationException(
                $"This session has {total:N0} rows; a redacted package holds at most {MaximumRows:N0} in this version. "
                + "Nothing was written.");
        }

        CaptureId? capture = null;
        NormalizerContractVersion? derivation = null;
        var tally = new PackageTally();
        var descriptors = new HashSet<Descriptor>();
        var names = new HashSet<(RedactedNameKind Kind, string Key)>();
        Int128? earliest = null;
        long rows = 0;
        foreach (string name in segmentNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentReaderV1 segment = SessionSegments.Open(root, manifest, name);
            RequireOneCapture(segment, clock, ref capture, ref derivation);
            for (int index = 0; index < segment.RowCount; index++)
            {
                if ((rows & 4_095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (rows % ProgressInterval == 0) progress?.Report(new(RedactedPackageStage.Inspecting, rows, total));
                rows++;
                ObservationRowV1 row = segment.Row(index);
                descriptors.Add(new(row.ProviderId, row.EventId, row.DescriptorVersion, row.SchemaFingerprint));
                pseudonyms.SeeProvider(row.ProviderId);
                pseudonyms.SeeFingerprint(row.SchemaFingerprint);
                pseudonyms.SeeNumber(row.HeaderProcessId);
                pseudonyms.SeeNumber(row.HeaderThreadId);
                if (row.OwnerProcessId is { } owner) pseudonyms.SeeNumber(owner);
                pseudonyms.SeeIdentifier(row.ActivityId);
                pseudonyms.SeeIdentifier(row.RelatedActivityId);
                pseudonyms.SeeIdentifier(row.SourceIdentifier);
                pseudonyms.SeeAddress(row.SourceEndpointAddress);
                pseudonyms.SeeAddress(row.DestinationEndpointAddress);
                pseudonyms.SeePort(row.SourceEndpointPort);
                pseudonyms.SeePort(row.DestinationEndpointPort);
                if (row.ResourceName is { } resource)
                {
                    pseudonyms.SeeName(resource);
                    RedactedNameKind kind = RedactedSessionPseudonyms.NameKindOf(row.Mechanism);
                    names.Add((kind, kind == RedactedNameKind.Executable ? resource.ToUpperInvariant() : resource));
                }

                if (row.SessionRelativeTicks is not null)
                {
                    Int128 relative = (Int128)row.NativeTicks - clock.CaptureEpochNativeTicks;
                    earliest = earliest is { } seen && seen <= relative ? seen : relative;
                }

                tally.Row(row);
            }
        }

        var fieldAddresses = new HashSet<RecordAddress>();
        long fieldRows = 0;
        long redactedFields = 0;
        foreach (string name in fieldNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentReaderV1 segment = SessionSegments.Open(root, manifest, name);
            RequireOneCapture(segment, clock, ref capture, ref derivation);
            for (int index = 0; index < segment.RowCount; index++)
            {
                if ((index & 4_095) == 0) cancellationToken.ThrowIfCancellationRequested();
                SourceFieldRowV1 field = segment.FieldRow(index);
                fieldAddresses.Add(new(field.RawStreamId, field.RawSourceEpoch, field.RawRecordOrdinal, field.FactKey));
                fieldRows++;
                FieldTransform transform = TransformOf(field);
                switch (transform)
                {
                    case FieldTransform.Number:
                        pseudonyms.SeeNumber(unchecked((int)field.Value!.Value));
                        break;
                    case FieldTransform.Sequence:
                        pseudonyms.SeeSequence(unchecked((ulong)field.Value!.Value));
                        break;
                    case FieldTransform.Pointer:
                        pseudonyms.SeePointer(field.Value!.Value);
                        break;
                    case FieldTransform.Redact:
                        redactedFields++;
                        break;
                }

                tally.Field(field, transform == FieldTransform.Redact ? FieldAvailability.Redacted : field.Availability,
                    transform == FieldTransform.Keep ? field.Value : null);
            }
        }

        CoverageLedgerV1? ledger = SessionSegments.CoverageLedger(root, manifest);
        foreach (CoverageEpochV1 epoch in ledger?.Epochs ?? [])
        {
            foreach (long? reading in new[] { epoch.FirstDeliveredNativeTicks, epoch.LastDeliveredNativeTicks })
            {
                if (reading is not { } value) continue;
                Int128 relative = (Int128)value - clock.CaptureEpochNativeTicks;
                earliest = earliest is { } seen && seen <= relative ? seen : relative;
            }

            foreach (CoverageCollectedV1 collected in epoch.Collected) pseudonyms.SeeProvider(collected.ProviderId);
            foreach (CoverageDeliveryV1 delivery in epoch.Deliveries) pseudonyms.SeeProvider(delivery.ProviderId);
        }

        tally.Distinct("names", names.Count);
        StoreDependency[] journals = [.. manifest.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Journal)];
        progress?.Report(new(RedactedPackageStage.Inspecting, rows, total));
        return new()
        {
            Clock = clock,
            Capture = capture!.Value,
            Derivation = derivation!.Value,
            Segments = segmentNames,
            FieldSegments = fieldNames,
            Rows = rows,
            FieldAddresses = fieldAddresses,
            Descriptors = [.. descriptors
                .OrderBy(descriptor => descriptor.Provider)
                .ThenBy(descriptor => descriptor.EventId)
                .ThenBy(descriptor => descriptor.Version)
                .ThenBy(descriptor => descriptor.Fingerprint, StringComparer.Ordinal)],
            PackageEpoch = PackageEpochFor(earliest, clock.TicksPerSecond),
            Ledger = ledger,
            Tally = tally,
            Preview = new(
                manifest.SessionId,
                manifest.Generation,
                rows,
                fieldRows,
                redactedFields,
                journals.Length,
                journals.Sum(journal => journal.LengthBytes),
                ledger is not null,
                manifest.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.DerivationPlan),
                manifest.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.CaptureFinalization)),
        };
    }

    private static void RequireOneCapture(
        SegmentReaderV1 segment,
        SourceClockDescriptor clock,
        ref CaptureId? capture,
        ref NormalizerContractVersion? derivation)
    {
        if (segment.ClockId != clock.Id || segment.TimestampEncoding != clock.Encoding)
        {
            throw new InvalidDataException(
                "A segment is on another clock than the generation's journal. A package with more than one clock would "
                + "need an alignment policy this version does not have.");
        }

        capture ??= segment.CaptureId;
        derivation ??= segment.Derivation;
        if (segment.CaptureId != capture || segment.Derivation != derivation)
        {
            throw new InvalidDataException(
                "The generation's segments name more than one capture or derivation; one package holds one capture.");
        }
    }

    /// <summary>
    /// The package's capture epoch: zero, unless a timed reading lies before the source's epoch, in which case the
    /// smallest whole number of seconds that keeps every timed reading non-negative. It never depends on the source's
    /// epoch itself, which would date the capture.
    /// </summary>
    private static long PackageEpochFor(Int128? earliestRelative, long ticksPerSecond)
    {
        if (earliestRelative is not { } earliest || earliest >= 0) return 0;
        Int128 seconds = (-earliest + ticksPerSecond - 1) / ticksPerSecond;
        Int128 epoch = seconds * ticksPerSecond;
        return epoch <= long.MaxValue
            ? (long)epoch
            : throw new InvalidDataException("The session's readings span more than a package clock can express.");
    }

    /// <summary>The source values the byte scan looks for, and the scanner for each kind of file.</summary>
    private static (RedactedPackageLeakScanner Identities, RedactedPackageLeakScanner Names) Needles(
        SessionManifestV1 manifest,
        SourceScan scan,
        RedactedSessionPseudonyms pseudonyms)
    {
        var identities = new RedactedPackageLeakScanner();
        identities.AddIdentifier(manifest.SessionId, "the source session's identity");
        identities.AddIdentifier(scan.Capture.Value, "the source capture's identity");
        identities.AddIdentifier(scan.Clock.Id.Value, "the source clock's identity");
        identities.AddIdentifier(scan.Clock.HostId.Value, "the source host's identity");
        identities.AddDigest(manifest.Digest, "the source manifest's digest");

        // The evidence a package never carries. A rewritten companion - a ledger whose epoch and providers did not change -
        // can be byte-identical to its source and so share its digest without disclosing anything, so it is not a needle.
        foreach (StoreDependency dependency in manifest.Dependencies.Where(dependency => dependency.Kind
            is StoreDependencyKind.Journal or StoreDependencyKind.DerivationPlan or StoreDependencyKind.CaptureFinalization))
        {
            identities.AddDigest(dependency.Digest, $"the digest of source file {dependency.Name}");
        }

        if (manifest.SourceIdentity.Length > 0) identities.AddText(manifest.SourceIdentity, "the source's identity text");
        foreach (Guid provider in pseudonyms.PseudonymizedSourceProviders)
        {
            identities.AddIdentifier(provider, "a pseudonymized provider's identity");
        }

        foreach (string fingerprint in pseudonyms.SourceFingerprints)
        {
            identities.AddText(fingerprint, "a source schema fingerprint");
        }

        var names = new RedactedPackageLeakScanner();
        foreach (string name in pseudonyms.SourceNames)
        {
            names.AddText(name, "a source resource or executable name");
        }

        return (identities, names);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Writing: one generation, built as any derivation is, from the projected rows.
    // ---------------------------------------------------------------------------------------------------------------

    private sealed record Written(
        Guid SessionId,
        CaptureId Capture,
        SourceClockDescriptor Clock,
        NormalizerContractVersion Derivation,
        byte[]? LedgerBytes,
        byte[] PolicyBytes,
        RedactedSessionCounts Counts);

    private static Written Write(
        IOwnedDirectory root,
        SessionManifestV1 manifest,
        SourceScan scan,
        RedactedSessionPseudonyms pseudonyms,
        string staging,
        DateTimeOffset createdUtc,
        IProgress<RedactedPackageProgress>? progress,
        CancellationToken cancellationToken)
    {
        Guid sessionId = Guid.NewGuid();
        CaptureId capture = CaptureId.New();
        SourceClockDescriptor source = scan.Clock;
        var clock = new SourceClockDescriptor(ClockId.New(), HostId.New(), source.Kind, source.Encoding,
            source.TicksPerSecond, scan.PackageEpoch, source.Rounding, source.MaximumAbsoluteSessionNanoseconds);
        var identity = new SegmentIdentityV1
        {
            CaptureId = capture,
            ClockId = clock.Id,
            TimestampEncoding = clock.Encoding,
            Derivation = scan.Derivation,
        };

        SessionStore package = SessionStore.Open(LocalOwnedDirectory.Open(staging), sessionId, Contract);
        byte[]? ledgerBytes = null;
        byte[] policyBytes;
        RedactedSessionCounts counts;
        using (DerivedGenerationBuilder builder = DerivedGenerationBuilder.Begin(package, identity, clock, createdUtc))
        {
            // Every descriptor is interned before the first batch: a journal's schema table precedes its records.
            var schemas = new JournalV1SchemaTable();
            var references = new Dictionary<Descriptor, uint>();
            foreach (Descriptor descriptor in scan.Descriptors)
            {
                references[descriptor] = schemas.Intern(pseudonyms.Provider(descriptor.Provider), descriptor.EventId,
                    descriptor.Version, pseudonyms.Fingerprint(descriptor.Fingerprint));
            }

            uint policy = schemas.InternPolicy(Policy);
            builder.Journal.WriteSchemas(schemas);

            var ordinals = new Dictionary<RecordAddress, ulong>(scan.FieldAddresses.Count);
            ulong ordinal = 0;
            foreach (string name in scan.Segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SegmentReaderV1 segment = SessionSegments.Open(root, manifest, name);
                for (int index = 0; index < segment.RowCount; index++)
                {
                    if ((ordinal & 4_095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    if ((long)ordinal % ProgressInterval == 0)
                    {
                        progress?.Report(new(RedactedPackageStage.Writing, (long)ordinal, scan.Rows));
                    }

                    ObservationRowV1 row = segment.Row(index);
                    ordinal++;
                    var address = new RecordAddress(row.RawStreamId, row.RawSourceEpoch, row.RawRecordOrdinal, row.FactKey);
                    if (scan.FieldAddresses.Contains(address) && !ordinals.TryAdd(address, ordinal))
                    {
                        throw new InvalidDataException(
                            "Two rows of this generation share one observation identity; the package refuses to guess "
                            + "which one its source fields belong to.");
                    }

                    ObservationRowV1 projected = Project(row, ordinal, scan, pseudonyms);
                    builder.Journal.Append(Envelope(projected, references[new(row.ProviderId, row.EventId,
                        row.DescriptorVersion, row.SchemaFingerprint)], policy, capture, clock));
                    builder.AddRow(projected);
                }
            }

            if ((long)ordinal != scan.Rows)
            {
                throw new InvalidDataException("The source generation's rows changed while the package was written.");
            }

            foreach (string name in scan.FieldSegments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SegmentReaderV1 segment = SessionSegments.Open(root, manifest, name);
                for (int index = 0; index < segment.RowCount; index++)
                {
                    if ((index & 4_095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    SourceFieldRowV1 field = segment.FieldRow(index);
                    if (!ordinals.TryGetValue(new(field.RawStreamId, field.RawSourceEpoch, field.RawRecordOrdinal,
                            field.FactKey), out ulong owner))
                    {
                        throw new InvalidDataException(
                            "A source field names an observation this generation does not hold, so the package cannot "
                            + "attach it to a row.");
                    }

                    builder.AddFieldRow(Project(field, owner, scan, pseudonyms));
                }
            }

            if (scan.Ledger is { } ledger)
            {
                ledgerBytes = Project(ledger, scan, pseudonyms).Encode();
                builder.StageCoverageLedger(ledgerBytes);
            }

            counts = new()
            {
                Rows = scan.Rows,
                SourceFieldRows = scan.Preview.SourceFieldRows,
                SourceFieldRowsRedacted = scan.Preview.SourceFieldRowsRedacted,
                CoverageLedger = scan.Ledger is not null,
                Names = pseudonyms.NameCount,
                ProcessesAndThreads = pseudonyms.NumberCount,
                Addresses = pseudonyms.AddressCount,
                Ports = pseudonyms.PortCount,
                Identifiers = pseudonyms.IdentifierCount,
                Providers = pseudonyms.ProviderCount,
                PublicProviders = pseudonyms.PublicProviderCount,
                Schemas = pseudonyms.SchemaCount,
                StartSequences = pseudonyms.SequenceCount,
                KernelObjects = pseudonyms.PointerCount,
            };
            policyBytes = PolicyFor(createdUtc, counts).Encode();
            builder.StageRedactionPolicy(policyBytes);
            builder.Complete(createdUtc, cancellationToken);
        }

        progress?.Report(new(RedactedPackageStage.Writing, scan.Rows, scan.Rows));
        return new(sessionId, capture, clock, scan.Derivation, ledgerBytes, policyBytes, counts);
    }

    /// <summary>
    /// One row as the package holds it. Every member is set here by name, so a column a later version adds is left out
    /// until a policy revision admits it, rather than copied through.
    /// </summary>
    private static ObservationRowV1 Project(ObservationRowV1 row, ulong ordinal, SourceScan scan,
        RedactedSessionPseudonyms pseudonyms) => new()
    {
        RawStreamId = 1,
        RawSourceEpoch = 1,
        RawRecordOrdinal = ordinal,
        JournalRecordIndex = ordinal - 1,
        FactKey = SyntheticFact,
        ProviderId = pseudonyms.Provider(row.ProviderId),
        EventId = row.EventId,
        DescriptorVersion = row.DescriptorVersion,
        SchemaFingerprint = pseudonyms.Fingerprint(row.SchemaFingerprint),
        Opcode = row.Opcode,
        NativeTicks = scan.Shift(row.NativeTicks),
        SessionRelativeTicks = row.SessionRelativeTicks,
        HeaderProcessId = pseudonyms.Number(row.HeaderProcessId),
        HeaderThreadId = pseudonyms.Number(row.HeaderThreadId),
        ProcessorNumber = 0,
        ActivityId = pseudonyms.Identifier(row.ActivityId),
        RelatedActivityId = pseudonyms.Identifier(row.RelatedActivityId),
        Mechanism = row.Mechanism,
        Layer = row.Layer,
        Kind = row.Kind,
        Direction = row.Direction,
        OwnerProcessId = pseudonyms.Number(row.OwnerProcessId),
        ResourceName = row.ResourceName is { } name
            ? pseudonyms.Name(RedactedSessionPseudonyms.NameKindOf(row.Mechanism), name)
            : null,
        SourceIdentifier = pseudonyms.Identifier(row.SourceIdentifier),
        EndpointAddressFamily = row.EndpointAddressFamily,
        SourceEndpointAddress = pseudonyms.Address(row.SourceEndpointAddress),
        SourceEndpointPort = pseudonyms.Port(row.SourceEndpointPort),
        DestinationEndpointAddress = pseudonyms.Address(row.DestinationEndpointAddress),
        DestinationEndpointPort = pseudonyms.Port(row.DestinationEndpointPort),
        ByteValue = row.ByteValue,
        ByteDomain = row.ByteDomain,
        AccountingSide = row.AccountingSide,
        MeasurementUnit = row.MeasurementUnit,
        ByteAvailability = row.ByteAvailability,
        StatusCode = row.StatusCode,
        StatusAvailability = row.StatusAvailability,
        AttributionQuality = row.AttributionQuality,
        CorrelationQuality = row.CorrelationQuality,
        MeasurementQuality = row.MeasurementQuality,
        TimingQuality = row.TimingQuality,

        // A pseudonym of a truncated name is still a prefix's pseudonym, so the marker stays. The extended-data and body
        // markers describe evidence the package does not carry, so they would claim what its synthetic record cannot show.
        Markers = row.Markers & SegmentRowMarkers.ResourceNameTruncated,
    };

    private static SourceFieldRowV1 Project(SourceFieldRowV1 field, ulong ordinal, SourceScan scan,
        RedactedSessionPseudonyms pseudonyms)
    {
        FieldTransform transform = TransformOf(field);
        long? value = field.Value is not { } source ? null : transform switch
        {
            FieldTransform.Keep => source,
            FieldTransform.Number => pseudonyms.Number(unchecked((int)source)),
            FieldTransform.Sequence => unchecked((long)pseudonyms.Sequence(unchecked((ulong)source))),
            FieldTransform.Pointer => pseudonyms.Pointer(source),
            _ => null,
        };
        return new()
        {
            RawStreamId = 1,
            RawSourceEpoch = 1,
            RawRecordOrdinal = ordinal,
            FactKey = SyntheticFact,
            NativeTicks = scan.Shift(field.NativeTicks),
            Field = field.Field,
            Value = value,
            Text = null,
            Availability = transform == FieldTransform.Redact ? FieldAvailability.Redacted : field.Availability,
        };
    }

    /// <summary>The coverage ledger under the package's pseudonyms and clock. Counts and states are unchanged.</summary>
    private static CoverageLedgerV1 Project(CoverageLedgerV1 ledger, SourceScan scan, RedactedSessionPseudonyms pseudonyms) => new()
    {
        Contract = CoverageLedgerV1.ContractName,
        Epochs =
        [
            .. ledger.Epochs.Select(epoch => new CoverageEpochV1
            {
                Epoch = epoch.Epoch,
                Acquisition = epoch.Acquisition,
                FirstDeliveredNativeTicks = epoch.FirstDeliveredNativeTicks is { } first ? scan.Shift(first) : null,
                LastDeliveredNativeTicks = epoch.LastDeliveredNativeTicks is { } last ? scan.Shift(last) : null,
                Collected =
                [
                    .. epoch.Collected.Select(collected => new CoverageCollectedV1
                    {
                        ProviderId = pseudonyms.Provider(collected.ProviderId),
                        ProviderName = pseudonyms.ProviderName(collected.ProviderId),
                        EventId = collected.EventId,
                        Version = collected.Version,
                        Mechanism = collected.Mechanism,
                    }),
                ],
                Deliveries =
                [
                    .. epoch.Deliveries.Select(delivery => new CoverageDeliveryV1
                    {
                        ProviderId = pseudonyms.Provider(delivery.ProviderId),
                        EventId = delivery.EventId,
                        Version = delivery.Version,
                        Delivered = delivery.Delivered,
                        Admitted = delivery.Admitted,
                        Omission = delivery.Omission,
                        Omitted = delivery.Omitted,
                        Undecodable = delivery.Undecodable is { } undecodable
                            ? new Dictionary<UndecodableReason, long>(undecodable)
                            : null,
                    }),
                ],
                Losses = [.. epoch.Losses.Select(loss => new CoverageLossV1 { Layer = loss.Layer, Lost = loss.Lost })],
            }),
        ],
    };

    /// <summary>
    /// The synthetic record behind one package row: the row's pseudonymous descriptor, header and reading, and nothing
    /// else. It is evidence that the row exists in this package, never a copy of an original record.
    /// </summary>
    private static RecordEnvelopeV1 Envelope(ObservationRowV1 row, uint schema, uint policy, CaptureId capture,
        SourceClockDescriptor clock) => new()
    {
        CaptureId = capture,
        StreamId = row.RawStreamId,
        SourceEpoch = row.RawSourceEpoch,
        RecordOrdinal = row.RawRecordOrdinal,
        Header = new(row.ProviderId, row.EventId, row.DescriptorVersion, 0, 0, row.Opcode, 0, 0, 0, 0,
            row.HeaderProcessId, row.HeaderThreadId, row.ActivityId ?? Guid.Empty, row.RelatedActivityId ?? Guid.Empty),
        BufferContext = new(0, 0),
        ClockId = clock.Id,
        TimestampEncoding = clock.Encoding,
        NativeTicks = row.NativeTicks,
        PointerSize = 8,
        SchemaReference = schema,
        AdmissionPolicyReference = policy,
        ExtendedItems = [],
        OmittedExtendedItemCount = 0,
        Body = BodyV1.None,
    };

    internal static RedactedSessionPolicyV1 PolicyFor(DateTimeOffset createdUtc, RedactedSessionCounts counts) => new()
    {
        Contract = Contract,
        Policy = Policy,
        CreatedUtc = createdUtc,
        Provenance = "Made from a verified InterCat session. That session's identity, capture, host, clock and files are "
            + "not recorded here, and nothing in this package refers back to them.",
        Warning = Warning,
        PseudonymScope = PseudonymScope,
        OriginalSourcesIncluded = false,
        OriginalRawLocatorsIncluded = false,
        PayloadBytesIncluded = false,
        Rederivable = false,
        Retained =
        [
            "Mechanism, layer, kind and direction of every normalized row",
            "Session-relative time, exactly, and native readings relative to a package capture epoch",
            "Byte values with their domain, accounting side, unit and availability",
            "Status codes and their availability; the four quality levels",
            "Event id, descriptor version and opcode of each row's descriptor",
            "Process terminal session, RPC procedure number and protocol sequence, and file byte offset fields",
            "Coverage and loss facts, when the source published a coverage ledger",
            "Whether a resource name was truncated",
        ],
        Pseudonymized =
        [
            "Executable, folder, pipe and resource names, a path component at a time",
            "Process and thread ids, including parent and issuing-thread fields",
            "IPv4 addresses and ports",
            "Activity, related-activity and source identifiers",
            "Provider identities other than the public Microsoft providers InterCat admits, and every schema fingerprint",
            "Process start sequence numbers and kernel object values (connection, request packet, file object, file key)",
        ],
        Redacted =
        [
            "Process creation and exit wall-clock times",
            "Any source-field text, and any source field this policy does not name",
        ],
        Omitted =
        [
            "The original admitted journal and any ETL, body bytes and extended data",
            "Original raw record locators, processor numbers and event header context",
            "The normalizer plan and the capture finalization marker",
            "The source session, capture, host and clock identities and the source's absolute clock readings",
        ],
        FixedPoints =
        [
            "Process and thread ids 0, 4 and -1",
            "Addresses 0.0.0.0, 127.0.0.0/8 and 255.255.255.255, and port 0",
            "The empty identifier and zero-valued start sequences and kernel object values",
        ],
        Counts = counts,
    };

    // ---------------------------------------------------------------------------------------------------------------
    // Verification: the package reopened and read as a recipient would, before it is published.
    // ---------------------------------------------------------------------------------------------------------------

    private static int Verify(
        string staging,
        Written written,
        SourceScan scan,
        RedactedSessionPseudonyms pseudonyms,
        RedactedPackageLeakScanner identities,
        RedactedPackageLeakScanner nameScanner,
        IProgress<RedactedPackageProgress>? progress,
        CancellationToken cancellationToken)
    {
        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(staging));
        using EvidenceLease lease = reopened.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        Require(!reopened.Recovery.RolledBackToLastKnownGood && reopened.Recovery.OrphanFiles.Count == 0,
            "The package holds a file its generation does not name.");
        Require(manifest.SessionId == written.SessionId && manifest.SourceIdentity == Contract
            && manifest.Generation == 1 && manifest.PreviousGeneration is null && manifest.Retention is null,
            "The package does not carry its own new identity.");

        string[] allowed =
        [
            SessionManifestV1.FileNameFor(1),
            SessionPointerV1.FileName,
            SessionPointerV1.PreviousFileName,
            SessionStore.PublicationLockFileName,
            SessionStore.EvidenceLeaseLockFileName,
            .. manifest.Dependencies.Select(dependency => dependency.Name),
        ];
        Require(!Directory.EnumerateDirectories(staging).Any()
            && Directory.EnumerateFiles(staging).Select(Path.GetFileName)
                .All(file => allowed.Contains(file, StringComparer.OrdinalIgnoreCase)),
            "The package directory holds a file outside its published inventory.");

        StoreDependency[] journals = [.. Of(manifest, StoreDependencyKind.Journal)];
        Require(journals.Length == 1 && journals[0].Name == SegmentFormatV1.JournalFileName(1)
            && manifest.Boundary.JournalName == journals[0].Name,
            "The package names other than its one synthetic journal.");
        Require(Of(manifest, StoreDependencyKind.RedactionPolicy).Select(dependency => dependency.Name)
                .SequenceEqual([DerivedGenerationBuilder.RedactionPolicyFileName(1)])
            && Of(manifest, StoreDependencyKind.CoverageLedger).Count() == (written.LedgerBytes is null ? 0 : 1)
            && manifest.Dependencies.All(dependency => dependency.Kind is StoreDependencyKind.Journal
                or StoreDependencyKind.Segment or StoreDependencyKind.Dictionary or StoreDependencyKind.RedactionPolicy
                or StoreDependencyKind.CoverageLedger),
            "The package's companion files are not exactly its policy and, when the source had one, its ledger.");

        SourceClockDescriptor clock = SessionSegments.SourceClock(reopened.Root, manifest)
            ?? throw new InvalidDataException("The package names no clock.");
        Require(clock == written.Clock, "The package's journal describes another clock than its rows are on.");

        // The journal: synthetic records only, in ordinal order, under the package's schemas and policy.
        long[] readings = new long[scan.Rows];
        Descriptor[] descriptors = new Descriptor[scan.Rows];
        long records = 0;
        using (FileStream stream = reopened.Root.OpenOwnedFile(journals[0].Name, FileMode.Open, FileAccess.Read,
            FileShare.Read, FileOptions.SequentialScan))
        {
            JournalV1Reader.ReplayBatches(stream, (capture, journalClock, table, batch) =>
            {
                Require(capture == written.Capture && journalClock == written.Clock
                    && table.Policies.Count == 1 && table.Policies.Single().PolicyId == Policy
                    && table.Schemas.All(schema => pseudonyms.IsIssuedOrPublicProvider(schema.ProviderId)
                        && pseudonyms.IsIssuedFingerprint(schema.Fingerprint)),
                    "The package journal names a source capture, clock, schema or policy.");
                foreach (RecordEnvelopeV1 record in batch.Records)
                {
                    EventHeaderFieldsV1 header = record.Header;
                    JournalSchemaV1? schema = record.SchemaReference is { } reference ? table.Find(reference) : null;
                    Require(record.CaptureId == written.Capture && record.StreamId == 1 && record.SourceEpoch == 1
                        && record.RecordOrdinal == (ulong)records + 1
                        && record.Body.Disposition == BodyDispositionV1.NoBody && record.Body.RetainedLength == 0
                        && record.Body.OriginalLength == 0
                        && record.ExtendedItems.Count == 0 && record.OmittedExtendedItemCount == 0
                        && record.ClockId == clock.Id && record.PointerSize == 8
                        && table.FindPolicy(record.AdmissionPolicyReference)?.PolicyId == Policy
                        && schema is not null && schema.ProviderId == header.ProviderId
                        && schema.EventId == header.EventId && schema.Version == header.Version
                        && header is { Channel: 0, Level: 0, Task: 0, Keyword: 0, Flags: 0, EventProperty: 0 }
                        && record.BufferContext == new BufferContextFieldsV1(0, 0)
                        && pseudonyms.IsIssuedOrFixedNumber(header.ProcessId)
                        && pseudonyms.IsIssuedOrFixedNumber(header.ThreadId)
                        && pseudonyms.IsIssuedOrFixedIdentifier(header.ActivityId)
                        && pseudonyms.IsIssuedOrFixedIdentifier(header.RelatedActivityId),
                        "A package journal record is not a synthetic metadata record.");
                    Require(records < scan.Rows, "The package journal holds more records than rows.");
                    readings[records] = record.NativeTicks;
                    descriptors[records] = new(header.ProviderId, header.EventId, header.Version, schema!.Fingerprint);
                    records++;
                }
            }, cancellationToken);
        }

        Require(records == scan.Rows, "The package journal and its rows disagree.");

        // The rows: every value a pseudonym, a fixed point or a kept value, reproducing the source's tallies.
        var tally = new PackageTally();
        var packageNames = new HashSet<string>(StringComparer.Ordinal);
        var seen = new bool[scan.Rows];
        long read = 0;
        foreach (string name in SessionSegments.Names(manifest))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentReaderV1 segment = SessionSegments.Open(reopened, manifest, name);
            RequirePackageSegment(segment, written);
            for (int index = 0; index < segment.RowCount; index++)
            {
                if (read % ProgressInterval == 0) progress?.Report(new(RedactedPackageStage.Verifying, read, scan.Rows));
                read++;
                ObservationRowV1 row = segment.Row(index);
                long at = checked((long)row.RawRecordOrdinal - 1);
                Require(at >= 0 && at < scan.Rows && !seen[at] && row.RawStreamId == 1 && row.RawSourceEpoch == 1
                    && row.JournalRecordIndex == row.RawRecordOrdinal - 1 && row.FactKey == SyntheticFact
                    && row.NativeTicks == readings[at]
                    && descriptors[at] == new Descriptor(row.ProviderId, row.EventId, row.DescriptorVersion,
                        row.SchemaFingerprint)
                    && row.ProcessorNumber == 0
                    && pseudonyms.IsIssuedOrFixedNumber(row.HeaderProcessId)
                    && pseudonyms.IsIssuedOrFixedNumber(row.HeaderThreadId)
                    && (row.OwnerProcessId is not { } owner || pseudonyms.IsIssuedOrFixedNumber(owner))
                    && (row.ActivityId is not { } activity || pseudonyms.IsIssuedOrFixedIdentifier(activity))
                    && (row.RelatedActivityId is not { } related || pseudonyms.IsIssuedOrFixedIdentifier(related))
                    && (row.SourceIdentifier is not { } identifier || pseudonyms.IsIssuedOrFixedIdentifier(identifier))
                    && (row.ResourceName is not { } resource || pseudonyms.IsIssuedName(resource))
                    && (row.SourceEndpointAddress is not { } sourceAddress || pseudonyms.IsIssuedOrFixedAddress(sourceAddress))
                    && (row.DestinationEndpointAddress is not { } remoteAddress || pseudonyms.IsIssuedOrFixedAddress(remoteAddress))
                    && (row.SourceEndpointPort is not { } sourcePort || pseudonyms.IsIssuedOrFixedPort(sourcePort))
                    && (row.DestinationEndpointPort is not { } remotePort || pseudonyms.IsIssuedOrFixedPort(remotePort))
                    && (row.Markers & ~SegmentRowMarkers.ResourceNameTruncated) == 0,
                    $"Package row {row.RawRecordOrdinal} carries a value that is not a pseudonym, a fixed point or kept.");
                seen[at] = true;
                if (row.ResourceName is { } kept) packageNames.Add(kept);
                tally.Row(row);
            }
        }

        Require(read == scan.Rows, "The package does not hold every row.");
        tally.Distinct("names", packageNames.Count);
        foreach (string name in SessionSegments.FieldNames(manifest))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentReaderV1 segment = SessionSegments.Open(reopened, manifest, name);
            RequirePackageSegment(segment, written);
            for (int index = 0; index < segment.RowCount; index++)
            {
                SourceFieldRowV1 field = segment.FieldRow(index);
                long at = checked((long)field.RawRecordOrdinal - 1);
                FieldTransform transform = field.Availability == FieldAvailability.Present
                    ? TransformOf(field.Field)
                    : FieldTransform.Keep;
                Require(at >= 0 && at < scan.Rows && field.RawStreamId == 1 && field.RawSourceEpoch == 1
                    && field.FactKey == SyntheticFact && field.NativeTicks == readings[at] && field.Text is null
                    && (field.Value is not { } value || transform switch
                    {
                        FieldTransform.Keep => true,
                        FieldTransform.Number => pseudonyms.IsIssuedOrFixedNumber(unchecked((int)value)),
                        FieldTransform.Sequence => pseudonyms.IsIssuedOrFixedSequence(unchecked((ulong)value)),
                        FieldTransform.Pointer => pseudonyms.IsIssuedOrFixedPointer(value),
                        _ => false,
                    }),
                    $"A package source field of row {field.RawRecordOrdinal} carries a value its policy does not allow.");
                tally.Field(field, field.Availability, transform == FieldTransform.Keep ? field.Value : null);
            }
        }

        // Dictionaries hold only the package's pseudonyms and its schema entries.
        foreach (StoreDependency dictionary in Of(manifest, StoreDependencyKind.Dictionary))
        {
            byte[] bytes = ReadAll(reopened.Root, dictionary.Name);
            foreach (string entry in SegmentDictionaryV1.Decode(bytes).Entries)
            {
                Require(pseudonyms.IsIssuedName(entry) || IsPackageSchemaEntry(entry, pseudonyms),
                    $"Dictionary {dictionary.Name} holds a value that is not one of the package's pseudonyms.");
            }
        }

        if (written.LedgerBytes is { } ledgerBytes)
        {
            CoverageLedgerV1 ledger = SessionSegments.CoverageLedger(reopened.Root, manifest)
                ?? throw new InvalidDataException("The package's coverage ledger is missing.");
            Require(ledger.Encode().AsSpan().SequenceEqual(ledgerBytes)
                && ledger.Epochs.All(epoch => epoch.Collected.All(collected =>
                    pseudonyms.IsIssuedOrPublicProvider(collected.ProviderId)
                    && pseudonyms.IsIssuedOrPublicProviderName(collected.ProviderName))
                    && epoch.Deliveries.All(delivery => pseudonyms.IsIssuedOrPublicProvider(delivery.ProviderId))),
                "The package's coverage ledger names a source provider.");
        }

        RedactedSessionPolicyV1 policy = RedactedSessionPolicyV1.Decode(
            SessionSegments.RedactionPolicy(reopened.Root, manifest)
                ?? throw new InvalidDataException("The package's redaction policy is missing."));
        Require(policy.Counts == written.Counts
            && ReadAll(reopened.Root, DerivedGenerationBuilder.RedactionPolicyFileName(1)).AsSpan()
                .SequenceEqual(written.PolicyBytes),
            "The package's redaction policy is not the one it was built under.");

        if (tally.FirstDifference(scan.Tally) is { } difference)
        {
            throw new InvalidDataException("The package does not reproduce its source: " + difference);
        }

        // Last, every published byte is searched for the source's identities and names.
        int files = 0;
        foreach (string path in Directory.EnumerateFiles(staging).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string file = Path.GetFileName(path);
            files++;
            if (file.Equals(DerivedGenerationBuilder.RedactionPolicyFileName(1), StringComparison.Ordinal))
            {
                // Built only from this type's constants and counts, and compared byte for byte above.
                continue;
            }

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
                FileOptions.SequentialScan);
            if (identities.Find(stream, cancellationToken) is { } identity)
            {
                throw new InvalidDataException($"The package file {file} contains {identity}. Nothing was published.");
            }

            if (IsBinary(file))
            {
                stream.Position = 0;
                if (nameScanner.Find(stream, cancellationToken) is { } name)
                {
                    throw new InvalidDataException($"The package file {file} contains {name}. Nothing was published.");
                }
            }
        }

        progress?.Report(new(RedactedPackageStage.Verifying, scan.Rows, scan.Rows));
        return files;
    }

    /// <summary>
    /// The files whose text is only the package's pseudonyms. JSON files name enumerations and members, so they are
    /// checked by structure and for identities, not searched for names that could be ordinary words.
    /// </summary>
    private static bool IsBinary(string file) =>
        file.EndsWith(".icatj", StringComparison.Ordinal) || file.EndsWith(".icats", StringComparison.Ordinal)
        || file.EndsWith(".icatd", StringComparison.Ordinal);

    private static bool IsPackageSchemaEntry(string entry, RedactedSessionPseudonyms pseudonyms)
    {
        // `<provider GUID in D form>|<event id>|<version>|<fingerprint>` (segment-v1 §8).
        string[] parts = entry.Split('|');
        return parts.Length == 4
            && Guid.TryParseExact(parts[0], "D", out Guid provider) && pseudonyms.IsIssuedOrPublicProvider(provider)
            && ushort.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && byte.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && pseudonyms.IsIssuedFingerprint(parts[3]);
    }

    private static void RequirePackageSegment(SegmentReaderV1 segment, Written written) =>
        Require(segment.CaptureId == written.Capture && segment.ClockId == written.Clock.Id
            && segment.TimestampEncoding == written.Clock.Encoding && segment.Derivation == written.Derivation,
            "A package segment names another capture, clock or derivation.");

    private static IEnumerable<StoreDependency> Of(SessionManifestV1 manifest, StoreDependencyKind kind) =>
        manifest.Dependencies.Where(dependency => dependency.Kind == kind);

    private static byte[] ReadAll(IOwnedDirectory root, string name)
    {
        using FileStream stream = root.OpenOwnedFile(name, FileMode.Open, FileAccess.Read, FileShare.Read,
            FileOptions.SequentialScan);
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void Require(bool condition, string failure)
    {
        if (!condition) throw new InvalidDataException(failure + " Nothing was published.");
    }

    /// <summary>
    /// What a package must reproduce of its source, measured the same way on both: counts by classification, sums of
    /// every kept number, presence of every pseudonymized one, and how many distinct values each namespace holds.
    /// </summary>
    private sealed class PackageTally
    {
        private readonly SortedDictionary<string, Int128> values = new(StringComparer.Ordinal);
        private readonly HashSet<int> numbers = [];
        private readonly HashSet<uint> addresses = [];
        private readonly HashSet<ushort> ports = [];
        private readonly HashSet<Guid> identifiers = [];
        private readonly HashSet<(SourceField Field, long Value)> fieldValues = [];

        public void Row(ObservationRowV1 row)
        {
            Count($"row|{row.Mechanism}|{row.Layer}|{row.Kind}|{row.Direction}|{row.EventId}|{row.DescriptorVersion}|{row.Opcode}");
            Count($"bytes|{row.ByteDomain}|{row.AccountingSide}|{row.MeasurementUnit}|{row.ByteAvailability}");
            Sum($"byte-sum|{row.ByteDomain}|{row.AccountingSide}", row.ByteValue ?? 0);
            Count($"status|{row.StatusAvailability}|{row.StatusCode is not null}");
            Sum("status-sum", row.StatusCode ?? 0);
            Count($"quality|{row.AttributionQuality}|{row.CorrelationQuality}|{row.MeasurementQuality}|{row.TimingQuality}");
            Count($"time|{row.SessionRelativeTicks is not null}");
            Sum("time-sum", row.SessionRelativeTicks ?? 0);

            // Whether a pseudonymized value is present is kept; a journal index is not tallied, because every package row
            // has one, including a source row whose record retention released.
            Count($"present|{row.OwnerProcessId is not null}|{row.ResourceName is not null}|{row.EndpointAddressFamily}"
                + $"|{row.ActivityId is not null}|{row.RelatedActivityId is not null}|{row.SourceIdentifier is not null}"
                + $"|{row.SourceEndpointAddress is not null}|{row.SourceEndpointPort is not null}"
                + $"|{row.DestinationEndpointAddress is not null}|{row.DestinationEndpointPort is not null}");
            Count($"marker|{(row.Markers & SegmentRowMarkers.ResourceNameTruncated) != 0}");

            // Fixed points are kept exactly, so their values - not only their presence - must match per row.
            Count($"fixed|{Fixed(row.HeaderProcessId)}|{Fixed(row.HeaderThreadId)}|{Fixed(row.OwnerProcessId)}"
                + $"|{Fixed(row.SourceEndpointAddress)}|{Fixed(row.SourceEndpointPort)}"
                + $"|{Fixed(row.DestinationEndpointAddress)}|{Fixed(row.DestinationEndpointPort)}"
                + $"|{Fixed(row.ActivityId)}|{Fixed(row.RelatedActivityId)}|{Fixed(row.SourceIdentifier)}");
            numbers.Add(row.HeaderProcessId);
            numbers.Add(row.HeaderThreadId);
            if (row.OwnerProcessId is { } owner) numbers.Add(owner);
            foreach (uint? address in new[] { row.SourceEndpointAddress, row.DestinationEndpointAddress })
            {
                if (address is { } value) addresses.Add(value);
            }

            foreach (ushort? port in new[] { row.SourceEndpointPort, row.DestinationEndpointPort })
            {
                if (port is { } value) ports.Add(value);
            }

            foreach (Guid? identifier in new[] { row.ActivityId, row.RelatedActivityId, row.SourceIdentifier })
            {
                if (identifier is { } value) identifiers.Add(value);
            }
        }

        public void Field(SourceFieldRowV1 field, FieldAvailability availability, long? keptValue)
        {
            // A pseudonymized field keeps its fixed points exactly: PIDs 0, 4 and -1, and zero start keys and objects.
            string fixedPoint = availability != FieldAvailability.Present || field.Value is not { } value ? "none"
                : TransformOf(field.Field) switch
                {
                    FieldTransform.Number => Fixed(unchecked((int)value)),
                    FieldTransform.Sequence or FieldTransform.Pointer => value == 0 ? "0" : "mapped",
                    _ => "kept",
                };
            Count($"field|{field.Field}|{availability}|{fixedPoint}");
            Sum($"field-sum|{field.Field}", keptValue ?? 0);
            if (availability == FieldAvailability.Present && keptValue is null && field.Value is { } pseudonymized)
            {
                fieldValues.Add((field.Field, pseudonymized));
            }
        }

        public void Distinct(string what, long count) => Sum("distinct|" + what, count);

        public string? FirstDifference(PackageTally source)
        {
            Close();
            source.Close();
            foreach (string key in values.Keys.Union(source.values.Keys).Order(StringComparer.Ordinal))
            {
                Int128 package = values.GetValueOrDefault(key);
                Int128 expected = source.values.GetValueOrDefault(key);
                if (package != expected) return $"{key}: the source has {expected}, the package {package}.";
            }

            return null;
        }

        private bool closed;

        private void Close()
        {
            if (closed) return;
            closed = true;
            Distinct("numbers", numbers.Count);
            Distinct("addresses", addresses.Count);
            Distinct("ports", ports.Count);
            Distinct("identifiers", identifiers.Count);
            Distinct("field-values", fieldValues.Count);
        }

        private static string Fixed(int? number) => number is not { } value ? "none"
            : RedactedSessionPseudonyms.IsFixedNumber(value) ? value.ToString(CultureInfo.InvariantCulture) : "mapped";

        private static string Fixed(uint? address) => address is not { } value ? "none"
            : RedactedSessionPseudonyms.IsFixedAddress(value) ? value.ToString(CultureInfo.InvariantCulture) : "mapped";

        private static string Fixed(ushort? port) => port is not { } value ? "none" : value == 0 ? "0" : "mapped";

        private static string Fixed(Guid? identifier) => identifier is not { } value ? "none"
            : value == Guid.Empty ? "empty" : "mapped";

        private void Count(string key) => Sum(key, 1);

        private void Sum(string key, Int128 amount) => values[key] = values.GetValueOrDefault(key) + amount;
    }
}
