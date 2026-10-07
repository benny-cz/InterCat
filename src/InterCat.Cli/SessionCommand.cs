using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Capture.Journal;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>A capture's recording in native ticks of its clock, and how long it is.</summary>
internal sealed record SessionRecordingDocument
{
    public required long StartNativeTicks { get; init; }
    public required long EndNativeTicks { get; init; }
    public required string Seconds { get; init; }
}

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

    /// <summary>The processes that collected the capture (`collector-identities-v1`); null when it names none.</summary>
    public required CollectorIdentitiesV1? Collectors { get; init; }

    /// <summary>How fast the wall clock ran against the source clock between the first and last sample; null when unknown.</summary>
    public required WallClockRate? WallClockRate { get; init; }

    /// <summary>
    /// The capture's recording, which a whole session's rates divide by (`metrics-v1` §7): native bounds and seconds; null
    /// when its capture recorded no stop.
    /// </summary>
    public required SessionRecordingDocument? Recording { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>The generation's size on disk, with its tier, as the window states it (§12.1 S5); null before one is published.</summary>
    public required SessionSizeDocument? Size { get; init; }
}

/// <summary>A generation's size on disk: every file it names at its measured length, its journal's part, and its records.</summary>
internal sealed record SessionSizeDocument
{
    public required long Bytes { get; init; }
    public required int Files { get; init; }
    public required long JournalBytes { get; init; }
    public required long Records { get; init; }

    /// <summary>Bytes on disk for each record, to the nearest byte; null while it holds none.</summary>
    public required long? BytesPerRecord { get; init; }

    public required SessionSizeTier Tier { get; init; }

    /// <summary>All of it in the words the window uses.</summary>
    public required string Statement { get; init; }
}

/// <summary>
/// What the capture could observe (`coverage-v2`): each mechanism's state with the fact that decided it, and the
/// ledger's own facts. Absent from a legacy generation's ledger means coverage is unknown, never that it was complete.
/// </summary>
internal sealed record SessionLedgerDocument
{
    public required bool Published { get; init; }
    public required string Rule { get; init; }

    /// <summary>
    /// Whether <see cref="Mechanisms"/> hold within an interval package's interval, where its ledger speaks; over its whole
    /// time its coverage is unknown (redacted-session-v1 §11).
    /// </summary>
    public bool WithinInterval { get; init; }
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

    /// <summary>What this generation released, and why, when a retention published it (store-v1 §8); null otherwise.</summary>
    public required RetentionRecord? Retention { get; init; }

    /// <summary>
    /// The latest release of a journal prefix and of kept content that earlier generations published, which this one
    /// carries; null when there are none (store-v1 §8).
    /// </summary>
    public required IReadOnlyList<GenerationRelease>? EarlierReleases { get; init; }
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

        string? outputOption = command.TakeOption("--output");
        string? rowsOption = command.TakeOption("--rows");
        string? sessionPath = command.TakePositional();
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure(CommandLine.Unknown(unknown!));
            return InterCatExitCode.InvalidInvocation;
        }

        if (sessionPath is null)
        {
            ConsoleUi.Failure("A session directory is required: icat session <directory>");
            ConsoleUi.Explain(PrintHelp);
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
        SessionDocument document = Describe(store, full, opened, cancellationToken);
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
        Dictionary<string, SegmentReaderV1> opened,
        CancellationToken cancellationToken)
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
        CollectorIdentitiesV1? collectors = null;
        WallClockRate? rate = null;
        SessionRecordingDocument? recording = null;
        SessionRedaction? redaction = manifest is null ? null : SessionRedaction.Read(store.Root, manifest);

        // What the session holds that no export carries: its chunks are read and checked, and no message byte is kept.
        SessionContentSummary? content = manifest?.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.Content) == true
            ? SessionContentIndex.Read(store.Root, manifest, CancellationToken.None).Summarize()
            : null;
        if (redaction is not null)
        {
            notes.Add(redaction.Statement(CultureInfo.CurrentCulture));
        }

        if (manifest is not null && DemoInvestigation.IsDemo(manifest))
        {
            notes.Add(DemoInvestigation.Disclosure);
        }

        if (manifest is null)
        {
            notes.Add(SessionStore.NoGeneration);
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
            bool withinInterval = published is { HoldsRecordsOutsideItsEpochs: true };
            ledger = new()
            {
                Published = published is not null,
                Rule = SessionCoverage.Rule,
                WithinInterval = withinInterval,
                Mechanisms =
                [
                    .. SessionCoverage.ByMechanism(withinInterval ? published! with { HoldsRecordsOutsideItsEpochs = false } : published)
                        .Select(entry => new SessionMechanismCoverageDocument
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
            collectors = CollectorIdentitiesV1.Read(store.Root, manifest);
            if (calibration is not null && SessionSegments.SourceClock(store.Root, manifest) is { } clock)
            {
                rate = ClockCalibrationFacts.Rate(calibration, clock.TicksPerSecond);
                if (SessionRecording.NativeInterval(store, manifest, clock, cancellationToken) is { } interval)
                {
                    recording = new()
                    {
                        StartNativeTicks = interval.StartTicks,
                        EndNativeTicks = interval.EndTicks,
                        Seconds = (interval.SpanTicks / (double)clock.TicksPerSecond).ToString("0.000000", CultureInfo.InvariantCulture),
                    };
                }
            }
            else if (calibration is null && manifest.Boundary.IsDeclared)
            {
                notes.Add(
                    "This session records no clock calibration - it was imported, packaged, or captured by an earlier version of "
                    + "InterCat - so the wall-clock time of its readings and the boot it ran in are unknown.");
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
                Retention = manifest.Retention,
                EarlierReleases = manifest.EarlierReleases,
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
            Collectors = collectors,
            WallClockRate = rate,
            Recording = recording,
            Redaction = redaction,
            Content = content,
            Notes = notes,
            Size = manifest is null ? null : SizeOf(SessionGrowth.Measure(manifest, coverage!.RowCount)),
        };
    }

    private static SessionSizeDocument SizeOf(SessionSize size) => new()
    {
        Bytes = size.Bytes,
        Files = size.Files,
        JournalBytes = size.JournalBytes,
        Records = size.Records,
        BytesPerRecord = size.BytesPerRecord,
        Tier = size.Tier,
        Statement = SessionGrowth.Describe(size),
    };

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
                $"{redaction.Policy}, made {redaction.CreatedUtc:u}; pseudonymized, not anonymous")
                + (redaction.Interval is { } interval
                    ? string.Create(CultureInfo.CurrentCulture,
                        $"; an interval of its source, ticks {interval.StartTicks:N0} to {interval.EndTicks:N0}")
                    : string.Empty));
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

        if (generation.Retention is { } retention)
        {
            RenderRetention(retention, "this generation");
        }

        foreach (GenerationRelease earlier in generation.EarlierReleases ?? [])
        {
            RenderRetention(earlier.Record, string.Create(CultureInfo.CurrentCulture, $"generation {earlier.Generation:N0}"));
        }

        ConsoleUi.Heading("Published files");
        if (document.Size is { } size)
        {
            ConsoleUi.Field("On disk", size.Statement);
        }

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
            ConsoleUi.Field("Mechanisms", coverage.Mechanisms.Count == 0 ? "none" : string.Join(", ", coverage.Mechanisms.Select(MechanismLabel)));
            ConsoleUi.Field("Layers", coverage.Layers.Count == 0 ? "none" : string.Join(", ", coverage.Layers.Select(LayerLabel)));
        }

        if (document.CoverageLedger is { } ledger)
        {
            RenderCoverage(ledger);
        }

        if (document.ClockCalibration is { } calibration)
        {
            RenderCalibration(calibration, document.WallClockRate, document.Recording);
        }

        if (document.Collectors is { } collectors)
        {
            RenderCollectors(collectors);
        }

        if (document.FieldSegments.Count > 0)
        {
            ConsoleUi.Heading("Source correlation and object fields");
            foreach (SessionFieldSegmentDocument segment in document.FieldSegments)
            {
                ConsoleUi.Field("Segment", segment.Name);
                ConsoleUi.Field("Fields", CountText.Of(segment.RowCount, "row"));
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

    /// <summary>
    /// What a release gave up and why, as its retention record states it - this generation's, or an earlier one's that it
    /// carries (store-v1 §8): a release with no reader is indistinguishable from data loss, so the session says what went,
    /// when and why, and which generation released it.
    /// </summary>
    private static void RenderRetention(RetentionRecord retention, string publishedBy)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        ConsoleUi.Heading("Released by retention");
        ConsoleUi.Field("Released by", publishedBy);
        ConsoleUi.Field("What", retention.Kind switch
        {
            RetentionExtentKind.DerivedFiles => "derived files - segments, dictionaries or indexes - all rebuildable from the journal",
            RetentionExtentKind.JournalPrefix => string.Create(culture,
                $"{ConsoleUi.Count(retention.ReleasedRecords)} records of the admitted journal, which can no longer be re-derived"),
            RetentionExtentKind.Content => string.Create(culture,
                $"the content kept of {ConsoleUi.Count(retention.ReleasedRecords)} records: their bytes and each one's content facts; every record's metadata is kept"),
            _ => retention.Kind.ToString(),
        });
        ConsoleUi.Field("When", retention.ReleasedUtc.ToString("u", CultureInfo.InvariantCulture));
        ConsoleUi.Field("Why", retention.Reason);
        int listed = Math.Min(retention.ReleasedFiles.Count, 4);
        ConsoleUi.Field("Files", string.Create(culture,
            $"{ConsoleUi.Count(retention.ReleasedFiles.Count)}, {ConsoleUi.Bytes(retention.ReleasedBytes)}: {string.Join(", ", retention.ReleasedFiles.Take(listed))}")
            + (retention.ReleasedFiles.Count > listed
                ? string.Create(culture, $" and {ConsoleUi.Count(retention.ReleasedFiles.Count - listed)} more")
                : string.Empty));
    }

    /// <summary>
    /// The processes that collected the capture, each by its role, PID and creation time, which is what a lifecycle record of
    /// the same instance carries: never by a name (`collector-identities-v1` §3).
    /// </summary>
    private static void RenderCollectors(CollectorIdentitiesV1 collectors)
    {
        ConsoleUi.Heading("Collected by");
        foreach (CollectorProcessV1 process in collectors.Processes)
        {
            ConsoleUi.Field(CollectorText.RoleName(process.Role), string.Create(CultureInfo.CurrentCulture, $"PID {process.ProcessId}, ") + (process.CreatedUtc is { } created
                ? string.Create(CultureInfo.CurrentCulture, $"created {created.UtcDateTime:yyyy-MM-dd HH:mm:ss.fffffff} UTC")
                : "its creation time unread, so no instance of the capture can be shown to be it"));
        }
    }

    private static void RenderCalibration(ClockCalibrationV1 calibration, WallClockRate? rate, SessionRecordingDocument? recording)
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

        if (recording is not null)
        {
            // The document's seconds are invariant, for a tool; the text reads them in the reader's own culture.
            string seconds = double.Parse(recording.Seconds, CultureInfo.InvariantCulture).ToString("0.000000", CultureInfo.CurrentCulture);
            ConsoleUi.Field("Recording", $"{seconds} s, from capture start to its stop: a whole session's rates divide by it");
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
                    ? $"{CoverageLedgerText.Acquisition(epoch.Acquisition)}, delivered readings [{first:N0}, {epoch.LastDeliveredNativeTicks:N0}] source ticks"
                    : $"{CoverageLedgerText.Acquisition(epoch.Acquisition)}, nothing delivered")
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
        // Mechanisms and states named as the window names them (R5); the document keeps the enumeration's names for a tool.
        if (ledger.WithinInterval)
        {
            ConsoleUi.Line("  Within this package's interval, where its coverage speaks; outside it, coverage is unknown:");
        }

        ConsoleUi.Table(["Mechanism", "Coverage", "Why"],
            [.. collected.Select(entry => new[] { MechanismLabel(entry.Mechanism), StateValue(entry.State), entry.Reason })]);
        if (notCollected.Count > 0)
        {
            ConsoleUi.Line("  Not collected, so an absence of their records says nothing about their activity: "
                + $"{string.Join(", ", notCollected.Select(MechanismInSentence))}.");
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
                            CoverageLedgerText.Omission(delivery.Omission!.Value),
                        }),
                    ]);
            }

            long undecodable = epoch.Deliveries.Sum(delivery => delivery.Undecodable?.Values.Sum() ?? 0);
            // What each loss layer reported, a zero included, in the words a coverage reason says it in (R5, R21).
            string losses = string.Join("; ", epoch.Losses.OrderBy(loss => loss.Layer)
                .Select(loss => CoverageLedgerText.Loss(loss, epoch.Acquisition)));
            // Each epoch's losses are its own: with several, each line names the epoch it is of.
            string of = facts.Epochs.Count == 1 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"Epoch {epoch.Epoch} ");
            ConsoleUi.Field(of.Length == 0 ? "Losses" : of + "losses", losses.Length == 0 ? "nothing reported" : losses);
            ConsoleUi.Field(of.Length == 0 ? "Undecodable" : of + "undecodable", ConsoleUi.Count(undecodable));
        }
    }

    /// <summary>A mechanism the document names by its enumeration, as a lane names it: "TCP", "Process".</summary>
    private static string MechanismLabel(string name) => Enum.TryParse(name, out Mechanism mechanism) ? MechanismText.Name(mechanism) : name;

    /// <summary>A mechanism the document names by its enumeration, as a sentence names it: "TCP", "process lifecycle".</summary>
    private static string MechanismInSentence(string name) =>
        Enum.TryParse(name, out Mechanism mechanism) ? MechanismText.InSentence(mechanism) : name;

    /// <summary>A layer the document names by its enumeration, as a word: "transport", "lifecycle".</summary>
    private static string LayerLabel(string name) => Enum.TryParse(name, out ObservationLayer layer) ? ObservationText.Layer(layer) : name;

    /// <summary>A coverage state the document names by its enumeration, as the window says it: "partial gap, not extrapolated".</summary>
    private static string StateValue(string name) => Enum.TryParse(name, out CoverageState state) ? CoverageStateText.Value(state) : name;

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
                        MechanismText.Name(row.Mechanism),
                        ObservationText.Kind(row.Kind),
                        EvidenceRowText.OwnerProcessId(row)?.ToString(CultureInfo.CurrentCulture) ?? "unknown",
                        row.ByteValue is { } value
                            ? value.ToString("N0", CultureInfo.CurrentCulture)
                            : row.ByteAvailability == FieldAvailability.NotApplicable
                                ? "n/a"
                                : $"unknown ({ObservationText.Absence(row.ByteAvailability)})",
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
            document.Generation is null ? "nothing: no generation has been published here"
            : document.Recovery.RolledBackToLastKnownGood ? "the retained last-known-good generation"
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
