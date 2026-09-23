using System.Globalization;
using System.Text.Json;
using InterCat.Capture.Journal;
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
    public required SessionCoverageDocument? Coverage { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
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

internal sealed record SessionBoundaryDocument
{
    public required string JournalName { get; init; }
    public required long CommittedBytes { get; init; }
    public required long CommittedRecords { get; init; }
    public required string Digest { get; init; }
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
        SessionManifestV1? manifest = store.Current;
        var notes = new List<string>();
        var segments = new List<SessionSegmentDocument>();
        var fieldSegments = new List<SessionFieldSegmentDocument>();
        SessionCoverageDocument? coverage = null;

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
                SegmentReaderV1 segment = SessionSegments.Open(store.Root, manifest, name);
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
                SegmentReaderV1 segment = SessionSegments.Open(store.Root, manifest, name);
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
        ConsoleUi.Field("Manifest digest", generation.Digest);
        ConsoleUi.Field("Re-derivation", document.Rederivation.CanAttempt ? "can attempt" : "not available");
        ConsoleUi.Note(document.Rederivation.Explanation);
        if (document.Rederivation.Command is { } rederiveCommand)
        {
            ConsoleUi.Note(rederiveCommand);
        }

        ConsoleUi.Heading("Evidence this generation derives from");
        if (generation.Boundary is { } boundary)
        {
            ConsoleUi.Field("Journal", boundary.JournalName);
            ConsoleUi.Field("Durable extent", $"{ConsoleUi.Bytes(boundary.CommittedBytes)}, {ConsoleUi.Count(boundary.CommittedRecords)} records");
            ConsoleUi.Field("Prefix digest", boundary.Digest);
        }
        else
        {
            ConsoleUi.Field("Journal", "none declared");
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
                        row.OwnerProcessId?.ToString(CultureInfo.CurrentCulture) ?? "unknown",
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

    private static string Endpoint(ObservationRowV1 row) =>
        row.SourceEndpointAddress is { } source
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{Address(source)}:{row.SourceEndpointPort?.ToString(CultureInfo.InvariantCulture) ?? "?"} -> "
                + $"{(row.DestinationEndpointAddress is { } peer ? Address(peer) : "?")}:"
                + $"{row.DestinationEndpointPort?.ToString(CultureInfo.InvariantCulture) ?? "?"}")
            : row.ResourceName ?? "none named";

    private static string Address(uint value) => string.Create(
        CultureInfo.InvariantCulture,
        $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}");

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
