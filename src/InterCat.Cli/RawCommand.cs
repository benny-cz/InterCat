using System.Globalization;
using System.Text.Json;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>Read one exact retained journal envelope named by a generation-bound evidence-page locator.</summary>
internal static class RawCommand
{
    public static Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken) =>
        Task.FromResult(Run(command, cancellationToken));

    private static InterCatExitCode Run(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? directory = command.TakePositional();
        string? sessionText = command.TakeOption("--session-id");
        string? generationText = command.TakeOption("--generation");
        string? segment = command.TakeOption("--segment");
        string? rowText = command.TakeOption("--row");
        bool reveal = command.TryTakeFlag("--reveal-bytes");
        bool json = command.TryTakeFlag("--json");
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        bool validSession = Guid.TryParse(sessionText, out Guid sessionId) && sessionId != Guid.Empty;
        bool validGeneration = long.TryParse(generationText, NumberStyles.None, CultureInfo.InvariantCulture,
            out long generation) && generation > 0;
        bool validRow = int.TryParse(rowText, NumberStyles.None, CultureInfo.InvariantCulture, out int row);
        if (directory is null || hasUnknown || !validSession || !validGeneration
            || string.IsNullOrWhiteSpace(segment) || !validRow)
        {
            ConsoleUi.Failure(directory is null ? "A session directory is required: icat raw <directory>."
                : hasUnknown ? $"Unknown or incomplete option: {unknown}"
                : !validSession ? "--session-id must be the nonempty GUID printed by icat evidence."
                : !validGeneration ? "--generation must be the positive number printed by icat evidence."
                : string.IsNullOrWhiteSpace(segment) ? "--segment must name an evidence-page segment."
                : "--row must be a nonnegative evidence-page segment row.");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        string path = Path.GetFullPath(directory);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        SessionRawRecordDetail detail;
        try
        {
            detail = SessionRawRecordQuery.ReadAt(SessionStore.OpenExisting(LocalOwnedDirectory.Open(path)),
                sessionId, generation, segment!, row, revealBodyBytes: reveal,
                cancellationToken: cancellationToken);
        }
        catch (ArgumentException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.InvalidInvocation;
        }
        catch (InvalidOperationException exception)
        {
            ConsoleUi.Warn(exception.Message);
            return InterCatExitCode.PartialResultSuccess;
        }

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                Contract = "raw-record-view-v1", SessionPath = path, Record = detail,
            }, JsonContracts.Indented));
            return detail.Available ? InterCatExitCode.Success : InterCatExitCode.PartialResultSuccess;
        }

        // A redacted package's journal holds synthetic records under its own policy; they are never called originals.
        bool synthetic = detail.AdmissionPolicyId == RedactedSessionPackage.Policy;
        ConsoleUi.Heading(synthetic ? "Synthetic record of a redacted package" : "Original admitted journal record");
        ConsoleUi.Field("Session", path);
        ConsoleUi.Field("Session ID", detail.SessionId.ToString("N"));
        ConsoleUi.Field("Generation", detail.Generation.ToString(CultureInfo.InvariantCulture));
        ConsoleUi.Field("Normalized fact", detail.ObservationId.ToString()!);
        if (!detail.Available)
        {
            ConsoleUi.Warn(detail.UnavailableReason!);
            return InterCatExitCode.PartialResultSuccess;
        }

        EventHeaderFieldsV1 header = detail.Header!.Value;
        ConsoleUi.Field("Journal", detail.JournalName!);
        ConsoleUi.Field("Source descriptor", $"{header.ProviderId:N} event {header.EventId} v{header.Version} opcode {header.Opcode}");
        ConsoleUi.Field("Native reading", $"{detail.NativeTicks} on {detail.ClockId} ({detail.TimestampEncoding})");
        ConsoleUi.Field("Header PID/TID", $"{header.ProcessId}/{header.ThreadId}");
        ConsoleUi.Field("Schema", detail.SchemaFingerprint ?? "unavailable");
        ConsoleUi.Field("Admission policy", detail.AdmissionPolicyId ?? "unavailable");
        ConsoleUi.Field("Body", $"{detail.BodyClassification}/{detail.BodyDisposition}; "
            + $"original {detail.OriginalBodyLength:N0} bytes, retained {detail.RetainedBodyLength:N0} bytes");
        ConsoleUi.Field("Extended items", $"retained {detail.ExtendedItems.Count:N0}, omitted {detail.OmittedExtendedItemCount:N0}");
        foreach (RawExtendedItemSummary item in detail.ExtendedItems)
            ConsoleUi.Line($"  type {item.Type}, flags {item.Flags}: original {item.OriginalLength:N0}, retained {item.RetainedLength:N0} bytes");
        if (detail.BodyPreview is { } bytes)
        {
            ConsoleUi.Line("  Retained body prefix · inert hexadecimal:");
            for (int offset = 0; offset < bytes.Length; offset += 16)
                ConsoleUi.Line($"  {offset:X4}: {Convert.ToHexString(bytes.AsSpan(offset, Math.Min(16, bytes.Length - offset)))}");
            if (detail.BodyPreviewTruncated) ConsoleUi.Note("Preview stopped at 256 bytes; more retained bytes exist.");
        }
        else if (detail.RetainedBodyLength > 0)
            ConsoleUi.Note("Body bytes are hidden. Add --reveal-bytes to print at most 256 inert hex bytes.");
        else if (!synthetic)
            ConsoleUi.Note("No body bytes were retained; the recorded disposition states why.");
        ConsoleUi.Note(synthetic
            ? "This package holds no original record. This synthetic entry carries the row's pseudonymous descriptor, "
                + "header and reading, and never a body or extended data (redacted-session-v1)."
            : "This is an original source envelope, not a decoded message, operation or payload export.");
        return InterCatExitCode.Success;
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat raw <session-directory> --session-id <guid> --generation <n> --segment <name> --row <n>");
        ConsoleUi.Line("         [--reveal-bytes] [--json]");
        ConsoleUi.Line("  Open one original retained journal record from an icat evidence page's exact row locator.");
        ConsoleUi.Line("  Session ID and generation are mandatory; a changed publication is refused, never followed.");
        ConsoleUi.Line("  Body bytes are hidden unless --reveal-bytes requests a 256-byte inert hex prefix.");
    }
}
