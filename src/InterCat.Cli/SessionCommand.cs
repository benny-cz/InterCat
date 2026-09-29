using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Capture.Journal;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>What opening a session found, as a document a tool can read without this CLI.</summary>
internal sealed record SessionDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required bool IsEmpty { get; init; }
    public required SessionGenerationDocument? Generation { get; init; }
    public required SessionRecoveryDocument Recovery { get; init; }
    public required SessionRederivationDocument Rederivation { get; init; }
    public required IReadOnlyList<SessionSegmentDocument> Segments { get; init; }
    public required IReadOnlyList<SessionFieldSegmentDocument> FieldSegments { get; init; }

    /// <summary>The policy a redacted session package was built under; null for an ordinary session.</summary>
    public required SessionRedaction? Redaction { get; init; }

    /// <summary>The restricted content the generation keeps beside its journal, in sum (ADR-036); null when it keeps none.</summary>
    public required SessionContentSummary? Content { get; init; }
    public required SessionCoverageDocument? Coverage { get; init; }
    public required SessionLedgerDocument? CoverageLedger { get; init; }

    /// <summary>The capture's clock against the wall clock, and its boot (`clock-calibration-v1`); null when it records none.</summary>
    public required ClockCalibrationV1? ClockCalibration { get; init; }

    /// <summary>How fast the wall clock ran against the source clock between the first and last sample; null when unknown.</summary>
    public required WallClockRate? WallClockRate { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// What the capture could observe (`coverage-v2`): each mechanism's state with the fact that decided it, and the
/// ledger's own facts. Absent from a legacy generation's ledger means coverage is unknown, never that it was complete.
/// </summary>
internal sealed record SessionLedgerDocument
{
    public required bool Published { get; init; }
    public required string Rule { get; init; }
    public required IReadOnlyList<SessionMechanismCoverageDocument> Mechanisms { get; init; }
    public required CoverageLedgerV1? Ledger { get; init; }
}

internal sealed record SessionMechanismCoverageDocument
{
    public required string Mechanism { get; init; }
    public required string State { get; init; }
    public required string Reason { get; init; }
}

internal sealed record SessionRederivationDocument
{
    public required bool CanAttempt { get; init; }
    public required string Explanation { get; init; }
    public required string? Command { get; init; }
}

internal sealed record SessionGenerationDocument
{
    public required long Generation { get; init; }
    public required long? PreviousGeneration { get; init; }
    public required string SessionId { get; init; }
    public required DateTimeOffset CommittedUtc { get; init; }
    public required string SourceIdentity { get; init; }
    public required string Digest { get; init; }
    public required SessionBoundaryDocument? Boundary { get; init; }
    public required IReadOnlyList<SessionDependencyDocument> Dependencies { get; init; }
}

/// <summary>
/// The committed boundary, and the journal it closes. A live recording's journal is a sequence of chunks and the boundary
/// names only the newest, so the chunks and their total size are stated beside it rather than left to the boundary alone.
/// </summary>
internal sealed record SessionBoundaryDocument
{
    public required string JournalName { get; init; }
    public required long CommittedBytes { get; init; }
    public required long CommittedRecords { get; init; }
    public required string Digest { get; init; }
    public required int JournalChunks { get; init; }
    public required long JournalBytes { get; init; }
}

internal sealed record SessionDependencyDocument
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required long LengthBytes { get; init; }
    public required string Digest { get; init; }
}

internal sealed record SessionRecoveryDocument
{
    public required bool RolledBackToLastKnownGood { get; init; }
    public required string? RollbackReason { get; init; }
    public required IReadOnlyList<string> RemovedStagingFiles { get; init; }
    public required IReadOnlyList<string> OrphanFiles { get; init; }
    public required long OrphanBytes { get; init; }
}

internal sealed record SessionSegmentDocument
{
    public required string Name { get; init; }
    public required string SegmentId { get; init; }
    public required string CaptureId { get; init; }
    public required string ClockId { get; init; }
    public required string TimestampEncoding { get; init; }
    public required uint Derivation { get; init; }
    public required int RowCount { get; init; }
    public required long MinNativeTicks { get; init; }
    public required long MaxNativeTicks { get; init; }
    public required int TimeBlocks { get; init; }
    public required IReadOnlyList<SessionColumnDocument> Columns { get; init; }
    public required IReadOnlyList<SessionMeasurementDocument> Measurements { get; init; }
}

internal sealed record SessionFieldSegmentDocument
{
    public required string Name { get; init; }
    public required int RowCount { get; init; }
    public required long MinNativeTicks { get; init; }
    public required long MaxNativeTicks { get; init; }
    public required IReadOnlyDictionary<string, long> Fields { get; init; }
}

internal sealed record SessionColumnDocument
{
    public required string Column { get; init; }
    public required string Type { get; init; }
    public required string Encoding { get; init; }
    public required bool Nullable { get; init; }
    public required int Known { get; init; }
    public required int Unknown { get; init; }
    public required long ValueBytes { get; init; }
}

/// <summary>
/// One byte sum, with what it excluded. The exclusions are part of the result, not a footnote: a total is
/// only readable beside the contributions it did not take (I6, R2).
/// </summary>
internal sealed record SessionMeasurementDocument
{
    public required string ByteDomain { get; init; }
    public required string AccountingSide { get; init; }
    public required string? Unit { get; init; }
    public required long TotalBytes { get; init; }
    public required long KnownContributions { get; init; }
    public required long UnknownContributions { get; init; }
    public required double? MeasurementAvailability { get; init; }
    public required long ExcludedOtherDomain { get; init; }
    public required long ExcludedOtherSide { get; init; }
    public required long ExcludedNoDeclaredSlot { get; init; }
    public required IReadOnlyDictionary<string, long> UnknownReasons { get; init; }
}

internal sealed record SessionCoverageDocument
{
    public required int SegmentCount { get; init; }
    public required long RowCount { get; init; }
    public required long? MinNativeTicks { get; init; }
    public required long? MaxNativeTicks { get; init; }
    public required IReadOnlyList<string> Mechanisms { get; init; }
    public required IReadOnlyList<string> Layers { get; init; }
}

/// <summary>
/// Opens a published session and reports what it holds. It is read-only by construction: it acquires the
/// current generation, verifies every dependency the manifest names, and reports an interrupted publication
/// as a rollback with its reason rather than repairing anything.
/// </summary>
internal static class SessionCommand
{
    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? sessionPath = command.TakePositional();
        string? outputOption = command.TakeOption("--output");
        string? rowsOption = command.TakeOption("--rows");
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure($"Unknown or incomplete option: {unknown}");
            return InterCatExitCode.InvalidInvocation;
        }

        if (sessionPath is null)
        {
            ConsoleUi.Failure("A session directory is required: icat session <directory>");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        string full = Path.GetFullPath(sessionPath);
        if (!Directory.Exists(full))
        {
            ConsoleUi.Failure($"No session directory at {full}.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (!TryReadRows(rowsOption, out int rows, out string? rowProblem))
        {
            ConsoleUi.Failure(rowProblem!);
            return InterCatExitCode.InvalidInvocation;
        }

        string? outputPath = outputOption is null ? null : Path.GetFullPath(outputOption);
        if (outputPath is not null && File.Exists(outputPath) && !overwrite)
        {
            ConsoleUi.Failure($"{outputPath} exists. Pass --overwrite to replace it.");
            return InterCatExitCode.InvalidInvocation;
        }

        ConsoleUi.Progress($"Acquiring the current generation of {full} and verifying every dependency.");
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(full));
        Dictionary<string, SegmentReaderV1> opened = [];
        SessionDocument document = Describe(store, full, opened);
        string payload = JsonSerializer.Serialize(document, JsonContracts.Indented);
        if (json)
        {
            Console.Out.WriteLine(payload);
        }
        else
        {
            Render(document, opened, rows);
        }

        if (outputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, payload, cancellationToken).ConfigureAwait(false);
            ConsoleUi.Success($"Session report written to {outputPath}.");
        }

        return document.IsEmpty || document.Recovery.RolledBackToLastKnownGood
            ? InterCatExitCode.PartialResultSuccess
            : InterCatExitCode.Success;
    }

    private static SessionDocument Describe(
        SessionStore store,
        string path,
        Dictionary<string, SegmentReaderV1> opened)
    {
        SessionManifestV1? visible = store.Current;
        using EvidenceLease? lease = visible is null ? null : store.AcquireLease();
        SessionManifestV1? manifest = lease?.Manifest;
        var notes = new List<string>();
        var segments = new List<SessionSegmentDocument>();
        var fieldSegments = new List<SessionFieldSegmentDocument>();
        SessionCoverageDocument? coverage = null;
        SessionLedgerDocument? ledger = null;
        ClockCalibrationV1? calibration = null;
        WallClockRate? rate = null;
        SessionRedaction? redaction = manifest is null ? null : SessionRedaction.Read(store.Root, manifest);

        // What the session holds that no export carries: its chunks are read and checked, and no message byte is kept.
        SessionContentSummary? content = manifest?.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.Content) == true
            ? SessionContentIndex.Read(store.Root, manifest, CancellationToken.None).Summarize()
            : null;
        if (redaction is not null)
        {
            notes.Add(SessionRedaction.Summary + " " + redaction.Warning);
        }

        if (manifest is null)
        {
            notes.Add("This session has published no generation. That is an empty session, not a failure.");
        }
        else
        {
            var mechanisms = new SortedSet<string>(StringComparer.Ordinal);
            var layers = new SortedSet<string>(StringComparer.Ordinal);
            long rowCount = 0;
            long? minTicks = null;
            long? maxTicks = null;
            foreach (string name in SessionSegments.Names(manifest))
            {
                SegmentReaderV1 segment = SessionSegments.Open(store, manifest, name);
                opened[name] = segment;
                segments.Add(Describe(segment, name));
                rowCount += segment.RowCount;
                minTicks = minTicks is null ? segment.MinNativeTicks : Math.Min(minTicks.Value, segment.MinNativeTicks);
                maxTicks = maxTicks is null ? segment.MaxNativeTicks : Math.Max(maxTicks.Value, segment.MaxNativeTicks);
                SegmentColumnSlice mechanismColumn = segment.Slice(SegmentColumnId.Mechanism);
                SegmentColumnSlice layerColumn = segment.Slice(SegmentColumnId.Layer);
                for (int row = 0; row < segment.RowCount; row++)
                {
                    _ = mechanisms.Add(((Mechanism)mechanismColumn.UnsignedAt(row)!.Value).ToString());
                    _ = layers.Add(((ObservationLayer)layerColumn.UnsignedAt(row)!.Value).ToString());
                }
            }

            foreach (string name in SessionSegments.FieldNames(manifest))
            {
                SegmentReaderV1 segment = SessionSegments.Open(store, manifest, name);
                var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
                for (int row = 0; row < segment.RowCount; row++)
                {
                    SourceFieldRowV1 field = segment.FieldRow(row);
                    string key = field.Field.ToString();
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }

                fieldSegments.Add(new()
                {
                    Name = name,
                    RowCount = segment.RowCount,
                    MinNativeTicks = segment.MinNativeTicks,
                    MaxNativeTicks = segment.MaxNativeTicks,
                    Fields = counts,
                });
            }

            coverage = new()
            {
                SegmentCount = segments.Count,
                RowCount = rowCount,
                MinNativeTicks = minTicks,
                MaxNativeTicks = maxTicks,
                Mechanisms = [.. mechanisms],
                Layers = [.. layers],
            };

            CoverageLedgerV1? published = SessionSegments.CoverageLedger(store.Root, manifest);
            ledger = new()
            {
                Published = published is not null,
                Rule = SessionCoverage.Rule,
                Mechanisms =
                [
                    .. SessionCoverage.ByMechanism(published).Select(entry => new SessionMechanismCoverageDocument
                    {
                        Mechanism = entry.Mechanism.ToString(),
                        State = entry.State.ToString(),
                        Reason = entry.Reason,
                    }),
                ],
                Ledger = published,
            };
            if (published is null)
            {
                notes.Add(
                    "This generation publishes no coverage ledger, so coverage is unknown: a mechanism with no records "
                    + "may have been quiet or never collected, and nothing says whether the source lost events (R21).");
            }

            calibration = ClockCalibrationV1.Read(store.Root, manifest);
            if (calibration is not null && SessionSegments.SourceClock(store.Root, manifest) is { } clock)
            {
                rate = ClockCalibrationFacts.Rate(calibration, clock.TicksPerSecond);
            }
            else if (calibration is null && manifest.Boundary.IsDeclared)
            {
                notes.Add(
                    "This session records no clock calibration - it was imported, packaged, or captured before revision 255 - "
                    + "so the wall-clock time of its readings and the boot it ran in are unknown.");
            }

            notes.Add(
                "The interval above is in the source clock's own native ticks. It is not a wall-clock range, "
                + "and it is never converted into one here (I8).");
            if (segments.Count == 0)
            {
                notes.Add(
                    "This generation publishes evidence and no derived segments, so nothing above it can be "
                    + "queried yet.");
            }

            if (!manifest.Boundary.IsDeclared)
            {
                notes.Add(
                    "This generation declares no committed boundary, so it does not name the admitted evidence "
                    + "it derives from (ADR-010).");
            }
        }

        if (store.Recovery.RolledBackToLastKnownGood)
        {
            notes.Add(
                "The newest generation did not verify, so this is the retained last-known-good one. Nothing was "
                + "repaired and nothing was removed.");
        }

        if (store.Recovery.OrphanFiles.Count > 0)
        {
            notes.Add(
                "Unreferenced files are reported and kept: one may belong to a generation whose publication was "
                + "interrupted, and deleting it would turn a recoverable interruption into lost evidence.");
            if (store.Recovery.OrphanFiles.Any(name =>
                name.StartsWith(SessionStore.StagingPrefix, StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(SessionStore.StagingSuffix, StringComparison.OrdinalIgnoreCase)))
            {
                notes.Add(
                    "Staging files are also kept: another process may have completed them and not yet committed. "
                    + "Use icat staging to preview ownership and clean only files whose owner has let go. "
                    + "Unmarked legacy files still need manual review.");
            }
        }

        JournalRederivationReadiness readiness = JournalRederivation.Assess(manifest);
        return new()
        {
            Contract = "store-v1",
            Path = path,
            IsEmpty = manifest is null,
            Rederivation = new()
            {
                CanAttempt = readiness.CanAttempt,
                Explanation = readiness.Explanation,
                Command = readiness.CanAttempt
                    ? $"icat rederive \"{path}\""
                    : null,
            },
            Generation = manifest is null ? null : new()
            {
                Generation = manifest.Generation,
                PreviousGeneration = manifest.PreviousGeneration,
                SessionId = manifest.SessionId.ToString("N"),
                CommittedUtc = manifest.CommittedUtc,
                SourceIdentity = manifest.SourceIdentity,
                Digest = manifest.Digest,
                Boundary = manifest.Boundary.IsDeclared
                    ? new()
                    {
                        JournalName = manifest.Boundary.JournalName,
                        CommittedBytes = manifest.Boundary.CommittedBytes,
                        CommittedRecords = manifest.Boundary.CommittedRecords,
                        Digest = manifest.Boundary.Digest,
                        JournalChunks = manifest.Dependencies.Count(dependency =>
                            dependency.Kind == StoreDependencyKind.Journal),
                        JournalBytes = manifest.Dependencies
                            .Where(dependency => dependency.Kind == StoreDependencyKind.Journal)
                            .Sum(dependency => dependency.LengthBytes),
                    }
                    : null,
                Dependencies =
                [
                    .. manifest.Dependencies.Select(dependency => new SessionDependencyDocument
                    {
                        Name = dependency.Name,
                        Kind = dependency.Kind.ToString(),
                        LengthBytes = dependency.LengthBytes,
                        Digest = dependency.Digest,
                    }),
                ],
            },
            Recovery = new()
            {
                RolledBackToLastKnownGood = store.Recovery.RolledBackToLastKnownGood,
                RollbackReason = store.Recovery.RollbackReason,
                RemovedStagingFiles = store.Recovery.RemovedStagingFiles,
                OrphanFiles = store.Recovery.OrphanFiles,
                OrphanBytes = store.Recovery.OrphanBytes,
            },
            Segments = segments,
            FieldSegments = fieldSegments,
            Coverage = coverage,
            CoverageLedger = ledger,
            ClockCalibration = calibration,
            WallClockRate = rate,
            Redaction = redaction,
            Content = content,
            Notes = notes,
        };
    }

    private static SessionSegmentDocument Describe(SegmentReaderV1 segment, string name) => new()
    {
        Name = name,
        SegmentId = segment.SegmentId.ToString("N"),
        CaptureId = segment.CaptureId.ToString(),
        ClockId = segment.ClockId.ToString(),
        TimestampEncoding = segment.TimestampEncoding.ToString(),
        Derivation = segment.Derivation.Value,
        RowCount = segment.RowCount,
        MinNativeTicks = segment.MinNativeTicks,
        MaxNativeTicks = segment.MaxNativeTicks,
        TimeBlocks = segment.TimeBlocks.Count,
        Columns =
        [
            .. segment.Columns.Select(column => new SessionColumnDocument
            {
                Column = column.Id.ToString(),
                Type = column.Type.ToString(),
                Encoding = column.Encoding.ToString(),
                Nullable = column.Nullable,
                Known = column.KnownCount,
                Unknown = column.UnknownCount,
                ValueBytes = column.ValueLength,
            }),
        ],
        Measurements = [.. Measure(segment)],
    };

    /// <summary>
    /// Sums each byte domain and side separately. They are never combined, and a pair with no contribution at
    /// all is left out rather than reported as a zero it summed (P1, P3).
    /// </summary>
    private static IEnumerable<SessionMeasurementDocument> Measure(SegmentReaderV1 segment)
    {
        foreach (ByteDomain domain in Enum.GetValues<ByteDomain>())
        {
            foreach (AccountingSide side in Enum.GetValues<AccountingSide>())
            {
                ByteSumResult sum = SegmentMeasurement.SumBytes(segment, new() { Domain = domain, Side = side });
                if (sum.KnownContributions + sum.UnknownContributions == 0)
                {
                    continue;
                }

                yield return new()
                {
                    ByteDomain = domain.ToString(),
                    AccountingSide = side.ToString(),
                    Unit = sum.Unit?.ToString(),
                    TotalBytes = sum.TotalBytes,
                    KnownContributions = sum.KnownContributions,
                    UnknownContributions = sum.UnknownContributions,
                    MeasurementAvailability = sum.MeasurementAvailability,
                    ExcludedOtherDomain = sum.ExcludedOtherDomain,
                    ExcludedOtherSide = sum.ExcludedOtherSide,
                    ExcludedNoDeclaredSlot = sum.ExcludedNoDeclaredSlot,
                    UnknownReasons = sum.UnknownReasons.ToDictionary(
                        entry => entry.Key.ToString(),
                        entry => entry.Value),
                };
            }
        }
    }

    private static void Render(
        SessionDocument document,
        Dictionary<string, SegmentReaderV1> opened,
        int rows)
    {
        ConsoleUi.Heading("Session");
        ConsoleUi.Field("Path", document.Path);
        if (document.Generation is not { } generation)
        {
            ConsoleUi.Field("Generation", "none published yet");
            ConsoleUi.Field("Re-derivation", document.Rederivation.Explanation);
            RenderRecovery(document);
            RenderNotes(document);
            return;
        }

        ConsoleUi.Field("Generation", ConsoleUi.Count(generation.Generation));
        ConsoleUi.Field("Committed", generation.CommittedUtc.ToString("u", CultureInfo.InvariantCulture));
        ConsoleUi.Field("Session", generation.SessionId);
        ConsoleUi.Field("Derived from", generation.SourceIdentity.Length == 0 ? "not stated" : generation.SourceIdentity);
        if (document.Redaction is { } redaction)
        {
            ConsoleUi.Field("Redacted package", string.Create(CultureInfo.InvariantCulture,
                $"{redaction.Policy}, made {redaction.CreatedUtc:u}; pseudonymized, not anonymous"));
        }

        ConsoleUi.Field("Manifest digest", generation.Digest);
        ConsoleUi.Field("Re-derivation", document.Rederivation.CanAttempt ? "can attempt" : "not available");
        ConsoleUi.Note(document.Rederivation.Explanation);
        if (document.Rederivation.Command is { } rederiveCommand)
        {
            ConsoleUi.Note(rederiveCommand);
        }

        ConsoleUi.Heading("Evidence this generation derives from");
        if (generation.Boundary is { JournalChunks: > 1 } chunked)
        {
            // The boundary names only the newest chunk; its extent alone would read as the whole recording's (ADR-022).
            ConsoleUi.Field("Journal", $"{chunked.JournalChunks} chunks of one recording, {ConsoleUi.Bytes(chunked.JournalBytes)} in all");
            ConsoleUi.Field("Newest chunk", $"{chunked.JournalName}: {ConsoleUi.Bytes(chunked.CommittedBytes)}, {ConsoleUi.Count(chunked.CommittedRecords)} records");
            ConsoleUi.Field("Newest digest", chunked.Digest);
        }
        else if (generation.Boundary is { } boundary)
        {
            ConsoleUi.Field("Journal", boundary.JournalName);
            ConsoleUi.Field("Durable extent", $"{ConsoleUi.Bytes(boundary.CommittedBytes)}, {ConsoleUi.Count(boundary.CommittedRecords)} records");
            ConsoleUi.Field("Prefix digest", boundary.Digest);
        }
        else
        {
            ConsoleUi.Field("Journal", "none declared");
        }

        if (document.Content is { } content)
        {
            RenderContent(content);
        }

        ConsoleUi.Heading("Published files");
        ConsoleUi.Table(
            ["File", "Kind", "Bytes"],
            [
                .. document.Generation.Dependencies.Select(dependency => new[]
                {
                    dependency.Name,
                    dependency.Kind,
                    dependency.LengthBytes.ToString("N0", CultureInfo.CurrentCulture),
                }),
            ]);

        if (document.Coverage is { } coverage)
        {
            ConsoleUi.Heading("What the derived segments hold");
            ConsoleUi.Field("Segments", ConsoleUi.Count(coverage.SegmentCount));
            ConsoleUi.Field("Observations", ConsoleUi.Count(coverage.RowCount));
            ConsoleUi.Field("Source fields", ConsoleUi.Count(document.FieldSegments.Sum(segment => (long)segment.RowCount)));
            ConsoleUi.Field(
                "Native interval",
                coverage.MinNativeTicks is null
                    ? "none"
                    : $"[{coverage.MinNativeTicks:N0}, {coverage.MaxNativeTicks:N0}] source ticks");
            ConsoleUi.Field("Mechanisms", coverage.Mechanisms.Count == 0 ? "none" : string.Join(", ", coverage.Mechanisms));
            ConsoleUi.Field("Layers", coverage.Layers.Count == 0 ? "none" : string.Join(", ", coverage.Layers));
        }

        if (document.CoverageLedger is { } ledger)
        {
            RenderCoverage(ledger);
        }

        if (document.ClockCalibration is { } calibration)
        {
            RenderCalibration(calibration, document.WallClockRate);
        }

        if (document.FieldSegments.Count > 0)
        {
            ConsoleUi.Heading("Source correlation and object fields");
            foreach (SessionFieldSegmentDocument segment in document.FieldSegments)
            {
                ConsoleUi.Field("Segment", segment.Name);
                ConsoleUi.Field("Fields", $"{segment.RowCount:N0} rows");
                ConsoleUi.Table(
                    ["Field", "Rows"],
                    [.. segment.Fields.Select(entry => new[] { entry.Key, entry.Value.ToString("N0", CultureInfo.CurrentCulture) })]);
            }
        }

        foreach (SessionSegmentDocument segment in document.Segments)
        {
            ConsoleUi.Heading($"Segment {segment.Name}");
            ConsoleUi.Field(
                "Rows",
                $"{segment.RowCount:N0} in {segment.TimeBlocks:N0} time block{(segment.TimeBlocks == 1 ? string.Empty : "s")}");
            ConsoleUi.Field("Clock", segment.ClockId);
            ConsoleUi.Field("Derivation", $"normalizer v{segment.Derivation}, {segment.TimestampEncoding} ticks");

            IReadOnlyList<SessionColumnDocument> incomplete =
                [.. segment.Columns.Where(column => column.Unknown > 0).OrderByDescending(column => column.Unknown)];
            ConsoleUi.Line();
            ConsoleUi.Line("  Columns with unknown values (availability, not absence):");
            if (incomplete.Count == 0)
            {
                ConsoleUi.Bullet("none; every column has a value in every row");
            }
            else
            {
                ConsoleUi.Table(
                    ["Column", "Known", "Unknown", "Encoding"],
                    [
                        .. incomplete.Select(column => new[]
                        {
                            column.Column,
                            column.Known.ToString("N0", CultureInfo.CurrentCulture),
                            column.Unknown.ToString("N0", CultureInfo.CurrentCulture),
                            column.Encoding,
                        }),
                    ]);
            }

            if (segment.Measurements.Count > 0)
            {
                ConsoleUi.Line();
                ConsoleUi.Line("  Byte metrics, one domain and one side each - never summed together:");
                ConsoleUi.Table(
                    ["Domain", "Side", "Total", "Measured on"],
                    [
                        .. segment.Measurements.Select(measurement => new[]
                        {
                            measurement.ByteDomain,
                            measurement.AccountingSide,
                            $"{measurement.TotalBytes:N0} {measurement.Unit ?? "unknown unit"}",
                            measurement.MeasurementAvailability is { } availability
                                ? $"{availability:P1} of {measurement.KnownContributions + measurement.UnknownContributions:N0}"
                                : "no declared slot",
                        }),
                    ]);
            }

            RenderRows(opened[segment.Name], rows);
        }

        RenderRecovery(document);
        RenderNotes(document);
    }

    /// <summary>
    /// What the capture could observe: collected mechanisms by state, the rest named as not collected, and what the
    /// policy chose not to admit, which is not a loss (`coverage-v2` §3, R21).
    /// </summary>
    /// <summary>
    /// The restricted content a session keeps, in sum and without a byte of it (ADR-036), so a session is never shared
    /// without its holder knowing it holds messages.
    /// </summary>
    private static void RenderContent(SessionContentSummary content)
    {
        ConsoleUi.Heading("Restricted content");
        ConsoleUi.Field("Kept", string.Create(CultureInfo.CurrentCulture,
            $"{ConsoleUi.Bytes(content.KeptBytes)} in {ConsoleUi.Count(content.Chunks)} {(content.Chunks == 1 ? "chunk" : "chunks")} beside the journal"));
        ConsoleUi.Field("Messages", string.Create(CultureInfo.CurrentCulture,
            $"{ConsoleUi.Count(content.Records)}: {ConsoleUi.Count(content.Whole)} kept whole, {ConsoleUi.Count(content.Cut)} cut to the record limit, {ConsoleUi.Count(content.Omitted)} not kept once the content limit was reached"));
        foreach (SessionContentPolicy policy in content.Policies)
        {
            string inspection = policy.Inspection == ContentInspectionV1.HexAndText
                ? "its bytes may be shown as hex and text"
                : "its bytes are never shown";
            ConsoleUi.Field("Policy", $"{policy.PolicyId}: at most {ConsoleUi.Bytes(policy.RecordLimit)} a record; {inspection}");
        }

        if (content.Problem is { } problem)
        {
            ConsoleUi.Warn($"Some content could not be read, so these sums leave it out: {problem}");
        }

        ConsoleUi.Note("Content is restricted evidence (ADR-036). icat export and a redacted package never carry it; an "
            + "original evidence package discloses it. icat raw states what each record kept.");
    }

    private static void RenderCalibration(ClockCalibrationV1 calibration, WallClockRate? rate)
    {
        ConsoleUi.Heading("Clock calibration");
        ConsoleUi.Field("Wall clock", calibration.WallClock);
        ConsoleUi.Field("Boot", calibration.BootToken is { } token
            ? $"token {token:N}" + (calibration.BootCount is { } count
                ? string.Create(CultureInfo.CurrentCulture, $", which Windows counts as its boot {count:N0}")
                : string.Empty)
            : "unknown: no boot token could be kept, so no other capture can be shown to share this boot");
        foreach ((ClockCalibrationSampleV1 sample, int index) in calibration.Samples.Select((sample, index) => (sample, index)))
        {
            string when = calibration.Samples.Count <= 2 ? (index == 0 ? "At start" : "At stop") : $"Sample {index + 1}";
            ConsoleUi.Field(when, string.Create(CultureInfo.CurrentCulture,
                $"{sample.Utc.UtcDateTime:yyyy-MM-dd HH:mm:ss.fffffff} UTC at source tick {sample.NativeTicks:N0}, ")
                + "±" + OperationText.DurationAtLeast(sample.AcquisitionUncertaintyNanoseconds, CultureInfo.CurrentCulture));
        }

        if (calibration.Samples.Count == 1)
        {
            // Published with the capture's first chunk; its last replaces it with the stop beside the start.
            ConsoleUi.Field("At stop", "not in this generation: a capture records its stop with its last publication, and one "
                + "that ended before it never does");
        }

        if (rate is not null)
        {
            ConsoleUi.Field("Wall clock rate", string.Create(CultureInfo.CurrentCulture,
                $"{rate.PartsPerMillion:+0.0;-0.0;0.0} ppm ±{rate.UncertaintyPartsPerMillion:0.0##} against the source clock between the samples"));
        }

        ConsoleUi.Note("A sample bounds only how far apart its two readings were taken; how right the wall clock was is not known "
            + "from it.");
    }

    private static void RenderCoverage(SessionLedgerDocument ledger)
    {
        ConsoleUi.Heading("What the capture could observe");
        if (ledger.Ledger is not { } facts)
        {
            ConsoleUi.Field("Coverage", "unknown: this generation publishes no coverage ledger");
            return;
        }

        foreach (CoverageEpochV1 epoch in facts.Epochs)
        {
            ConsoleUi.Field(
                facts.Epochs.Count == 1 ? "Epoch" : $"Epoch {epoch.Epoch}",
                (epoch.FirstDeliveredNativeTicks is { } first
                    ? $"{Words(epoch.Acquisition.ToString())}, delivered readings [{first:N0}, {epoch.LastDeliveredNativeTicks:N0}] source ticks"
                    : $"{Words(epoch.Acquisition.ToString())}, nothing delivered")
                + (epoch.RecordedFromNativeTicks is { } from
                    ? $"; recorded from {from:N0} to {epoch.RecordedToNativeTicks:N0}, and it speaks for every reading between"
                    : string.Empty));
        }

        List<SessionMechanismCoverageDocument> collected =
        [
            .. ledger.Mechanisms.Where(entry => entry.State != nameof(CoverageState.NotCollected)),
        ];
        List<string> notCollected =
        [
            .. ledger.Mechanisms.Where(entry => entry.State == nameof(CoverageState.NotCollected)).Select(entry => entry.Mechanism),
        ];
        ConsoleUi.Table(["Mechanism", "Coverage", "Why"], [.. collected.Select(entry => new[] { entry.Mechanism, Words(entry.State), entry.Reason })]);
        if (notCollected.Count > 0)
        {
            ConsoleUi.Line(
                $"  Not collected, so an absence of their records says nothing about their activity: {string.Join(", ", notCollected)}.");
        }

        Dictionary<Guid, string> names = facts.Epochs
            .SelectMany(epoch => epoch.Collected)
            .GroupBy(descriptor => descriptor.ProviderId)
            .ToDictionary(group => group.Key, group => group.First().ProviderName);
        foreach (CoverageEpochV1 epoch in facts.Epochs)
        {
            List<CoverageDeliveryV1> omitted = [.. epoch.Deliveries.Where(delivery => delivery.Omitted > 0)];
            if (omitted.Count > 0)
            {
                ConsoleUi.Line();
                ConsoleUi.Line("  Delivered and not admitted by the policy - not lost, and a wider policy could admit them:");
                ConsoleUi.Table(
                    ["Provider", "Event", "Version", "Records", "Why"],
                    [
                        .. omitted.Select(delivery => new[]
                        {
                            delivery.ProviderId == Guid.Empty
                                ? "Unidentified provider"
                                : names.GetValueOrDefault(delivery.ProviderId)
                                    ?? WindowsSourceCatalog.ClassicEventClassName(delivery.ProviderId)
                                    ?? delivery.ProviderId.ToString("D"),
                            delivery switch
                            {
                                { EventId: { } id, Opcode: { } opcode } => string.Create(CultureInfo.InvariantCulture, $"{id}, opcode {opcode}"),
                                { EventId: { } id } => id.ToString(CultureInfo.InvariantCulture),
                                _ => "any",
                            },
                            delivery.Version?.ToString(CultureInfo.InvariantCulture) ?? "any",
                            ConsoleUi.Count(delivery.Omitted),
                            Words(delivery.Omission!.Value.ToString()),
                        }),
                    ]);
            }

            long undecodable = epoch.Deliveries.Sum(delivery => delivery.Undecodable?.Values.Sum() ?? 0);
            string losses = string.Join(
                "; ",
                epoch.Losses.Select(loss => loss.Layer == LossLayer.ConsumerBuffers
                    ? $"{Words(loss.Layer.ToString())} {ConsoleUi.Count(loss.Lost)} buffers"
                    : $"{Words(loss.Layer.ToString())} {ConsoleUi.Count(loss.Lost)} records"));
            ConsoleUi.Field("Reported lost", losses.Length == 0 ? "nothing reported" : losses);
            ConsoleUi.Field("Undecodable", ConsoleUi.Count(undecodable));
        }
    }

    /// <summary>Splits a PascalCase name into lower-case words, e.g. "etl import".</summary>
    private static string Words(string identifier)
    {
        var builder = new System.Text.StringBuilder(identifier.Length + 8);
        for (int index = 0; index < identifier.Length; index++)
        {
            char character = identifier[index];
            if (index > 0 && char.IsUpper(character) && !char.IsUpper(identifier[index - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(index == 0 ? char.ToUpperInvariant(character) : char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static void RenderRows(SegmentReaderV1 reader, int rows)
    {
        if (rows == 0)
        {
            return;
        }

        int shown = Math.Min(rows, reader.RowCount);
        ConsoleUi.Line();
        ConsoleUi.Line($"  First {shown:N0} of {reader.RowCount:N0} observations, in the segment's own order:");
        ConsoleUi.Table(
            ["Native ticks", "Mechanism", "Kind", "Owner PID", "Bytes", "Endpoint"],
            [
                .. Enumerable.Range(0, shown).Select(index =>
                {
                    ObservationRowV1 row = reader.Row(index);
                    return new[]
                    {
                        row.NativeTicks.ToString("N0", CultureInfo.CurrentCulture),
                        row.Mechanism.ToString(),
                        row.Kind.ToString(),
                        EvidenceRowText.OwnerProcessId(row)?.ToString(CultureInfo.CurrentCulture) ?? "unknown",
                        row.ByteValue is { } value
                            ? value.ToString("N0", CultureInfo.CurrentCulture)
                            : row.ByteAvailability == FieldAvailability.NotApplicable
                                ? "n/a"
                                : $"unknown ({row.ByteAvailability})",
                        Endpoint(row),
                    };
                }),
            ]);
    }

    private static string Endpoint(ObservationRowV1 row)
    {
        (string? source, string? peer) = row.EndpointAddressFamily == 6
            ? (Address(row.SourceEndpointAddressV6), Address(row.DestinationEndpointAddressV6))
            : (Address(row.SourceEndpointAddress), Address(row.DestinationEndpointAddress));
        return source is not null
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{source}:{row.SourceEndpointPort?.ToString(CultureInfo.InvariantCulture) ?? "?"} -> "
                + $"{peer ?? "?"}:{row.DestinationEndpointPort?.ToString(CultureInfo.InvariantCulture) ?? "?"}")
            : row.ResourceName ?? "none named";
    }

    private static string? Address(uint? value) => value is { } address ? EndpointText.Ipv4(address) : null;

    /// <summary>An IPv6 address bracketed, so the port that follows cannot read as one of its groups.</summary>
    private static string? Address(UInt128? value) => value is { } address ? "[" + EndpointText.Ipv6(address) + "]" : null;

    private static void RenderRecovery(SessionDocument document)
    {
        ConsoleUi.Heading("What opening this session found");
        ConsoleUi.Field(
            "Acquired",
            document.Recovery.RolledBackToLastKnownGood
                ? "the retained last-known-good generation"
                : "the generation the pointer names");
        if (document.Recovery.RollbackReason is { } reason)
        {
            ConsoleUi.Field("Rollback reason", reason);
        }

        ConsoleUi.Field(
            "Staging files kept",
            ConsoleUi.Count(document.Recovery.OrphanFiles.Count(name =>
                name.StartsWith(SessionStore.StagingPrefix, StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(SessionStore.StagingSuffix, StringComparison.OrdinalIgnoreCase))));
        ConsoleUi.Field(
            "Staging owner markers",
            ConsoleUi.Count(document.Recovery.OrphanFiles.Count(name =>
                name.StartsWith(SessionStore.StagingPrefix, StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(SessionStore.StagingOwnershipSuffix, StringComparison.OrdinalIgnoreCase))));
        ConsoleUi.Field(
            "Unreferenced files",
            document.Recovery.OrphanFiles.Count == 0
                ? "none"
                : $"{document.Recovery.OrphanFiles.Count:N0} kept, {document.Recovery.OrphanBytes:N0} B");
    }

    private static void RenderNotes(SessionDocument document)
    {
        if (document.Notes.Count == 0)
        {
            return;
        }

        ConsoleUi.Line();
        foreach (string note in document.Notes)
        {
            ConsoleUi.Note(note);
        }
    }

    private static bool TryReadRows(string? option, out int value, out string? problem)
    {
        value = 10;
        problem = null;
        if (option is null)
        {
            return true;
        }

        if (!int.TryParse(option, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value > 10_000)
        {
            problem = $"--rows expects a whole number up to 10,000; '{option}' is not one.";
            return false;
        }

        return true;
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("  icat session <directory> [--rows <n>] [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("      Opens a published session, verifies every dependency the current generation");
        ConsoleUi.Line("      names, and reports its evidence boundary, its segments, their column");
        ConsoleUi.Line("      availability and their byte metrics. Read-only; repairs nothing.");
    }
}
