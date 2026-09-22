using System.Globalization;
using System.Text.Json;
using InterCat.Capture.Journal;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

internal sealed record RederiveDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required long SourceGeneration { get; init; }
    public required long PublishedGeneration { get; init; }
    public required string ManifestDigest { get; init; }
    public required string JournalName { get; init; }
    public required long ReplayedRecords { get; init; }
    public required long ObservationRows { get; init; }
    public required long SourceFieldRows { get; init; }
    public required int ObservationSegments { get; init; }
    public required int SourceFieldSegments { get; init; }
    public required string Derivation { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>Rebuilds one published generation from its own admitted journal, without rereading an ETL.</summary>
internal static class RederiveCommand
{
    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? pathOption = command.TakePositional();
        string? rowsOption = command.TakeOption("--rows-per-segment");
        string? outputOption = command.TakeOption("--output");
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure($"Unknown or incomplete option: {unknown}");
            return InterCatExitCode.InvalidInvocation;
        }

        if (pathOption is null)
        {
            ConsoleUi.Failure("A published session directory is required: icat rederive <directory>.");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        if (rowsOption is not null
            && (!int.TryParse(rowsOption, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedRows)
                || parsedRows is < 1 or > SegmentFormatV1.MaximumRowsPerSegment))
        {
            ConsoleUi.Failure($"--rows-per-segment must be between 1 and {SegmentFormatV1.MaximumRowsPerSegment:N0}.");
            return InterCatExitCode.InvalidInvocation;
        }

        string full = Path.GetFullPath(pathOption);
        if (!Directory.Exists(full))
        {
            ConsoleUi.Failure($"No session directory at {full}.");
            return InterCatExitCode.InvalidInvocation;
        }

        string? output = outputOption is null ? null : Path.GetFullPath(outputOption);
        if (output is not null && File.Exists(output) && !overwrite)
        {
            ConsoleUi.Failure($"{output} exists. Pass --overwrite to replace the report.");
            return InterCatExitCode.InvalidInvocation;
        }

        ConsoleUi.Progress($"Opening {full} and verifying the published generation and every dependency.");
        SessionStore inspected = SessionStore.OpenExisting(LocalOwnedDirectory.Open(full));
        if (inspected.Current is not { } current)
        {
            ConsoleUi.Failure("This session has no published generation to re-derive.");
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        if (inspected.Recovery.RolledBackToLastKnownGood)
        {
            ConsoleUi.Warn(
                $"The newest generation did not verify ({inspected.Recovery.RollbackReason}); re-deriving the "
                + $"retained last-known-good generation {current.Generation}.");
        }

        var options = new DerivedGenerationOptions
        {
            RowsPerSegment = rowsOption is null
                ? DerivedGenerationOptions.Default.RowsPerSegment
                : int.Parse(rowsOption, CultureInfo.InvariantCulture),
        };
        SessionStore writable = SessionStore.Open(LocalOwnedDirectory.Open(full), current.SessionId, current.SourceIdentity);
        ConsoleUi.Progress(
            $"Replaying generation {current.Generation}'s retained journal in bounded batches; the current "
            + "generation stays published until the replacement is complete.");
        JournalRederivationResult result;
        try
        {
            result = JournalRederivation.Rebuild(writable, DateTimeOffset.UtcNow, options, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        var document = new RederiveDocument
        {
            Contract = "rederive-v1",
            Path = full,
            SourceGeneration = result.SourceGeneration,
            PublishedGeneration = result.Generation.Manifest.Generation,
            ManifestDigest = result.Generation.Manifest.Digest,
            JournalName = result.Generation.JournalName,
            ReplayedRecords = result.ReplayedRecords,
            ObservationRows = result.Generation.RowCount,
            SourceFieldRows = result.Generation.FieldRowCount,
            ObservationSegments = result.Generation.Segments.Count,
            SourceFieldSegments = result.Generation.FieldSegments.Count,
            Derivation = $"observation-v{ObservationNormalizerV1.ContractVersion.Value}",
            Notes =
            [
                "Only the admitted journal and its saved descriptor plan were read; no original ETL or current "
                + "machine schema was consulted.",
                "The previous generation remains last-known-good. It is not counted alongside this replacement.",
                "Segment bytes repeat exactly only when the normalizer and segment partition bounds are the same.",
            ],
        };
        string payload = JsonSerializer.Serialize(document, JsonContracts.Indented);
        if (json)
        {
            Console.Out.WriteLine(payload);
        }
        else
        {
            ConsoleUi.Heading("Re-derived session");
            ConsoleUi.Field("Session", full);
            ConsoleUi.Field("Generation", $"{result.SourceGeneration} → {document.PublishedGeneration}");
            ConsoleUi.Field("Admitted journal", document.JournalName);
            ConsoleUi.Field("Replayed records", ConsoleUi.Count(document.ReplayedRecords));
            ConsoleUi.Field("Observations", ConsoleUi.Count(document.ObservationRows));
            ConsoleUi.Field("Source fields", ConsoleUi.Count(document.SourceFieldRows));
            ConsoleUi.Field("Segments", $"{document.ObservationSegments} observations, {document.SourceFieldSegments} fields");
            foreach (string note in document.Notes)
            {
                ConsoleUi.Note(note);
            }
        }

        if (output is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, payload, cancellationToken).ConfigureAwait(false);
            ConsoleUi.Success($"Re-derivation report written to {output}.");
        }

        return inspected.Recovery.RolledBackToLastKnownGood
            ? InterCatExitCode.PartialResultSuccess
            : InterCatExitCode.Success;
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("  icat rederive <directory> [--rows-per-segment <n>] [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("      Replays the session's admitted journal and saved normalizer plan into a new generation.");
        ConsoleUi.Line("      The old generation stays current until publication and remains last-known-good afterwards.");
        ConsoleUi.Line("      Legacy sessions without a saved plan are refused until a verified plan migration exists.");
    }
}
