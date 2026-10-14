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

        string? sessionText = command.TakeOption("--session-id");
        string? generationText = command.TakeOption("--generation");
        string? segment = command.TakeOption("--segment");
        string? rowText = command.TakeOption("--row");
        string? directory = command.TakePositional();
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
                : hasUnknown ? CommandLine.Unknown(unknown!)
                : !validSession ? "--session-id must be the nonempty GUID printed by icat evidence."
                : !validGeneration ? "--generation must be the positive number printed by icat evidence."
                : string.IsNullOrWhiteSpace(segment) ? "--segment must name an evidence-page segment."
                : "--row must be a nonnegative evidence-page segment row.");
            ConsoleUi.Explain(PrintHelp);
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
            ConsoleUi.Failure(exception);
            return InterCatExitCode.InvalidInvocation;
        }
        catch (InvalidOperationException exception) when (exception is not NoSessionException)
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

        // A redacted package's journal holds synthetic records under its own policy; they are never called originals, found
        // or not. The record's facts are the window's, in its words (R18).
        bool synthetic = detail.Synthetic;
        ConsoleUi.Heading(synthetic ? "Synthetic record of a redacted package" : "Original admitted journal record");
        ConsoleUi.Field("Session", path);
        ConsoleUi.Field("Session ID", detail.SessionId.ToString("N"));
        ConsoleUi.Field("Generation", detail.Generation.ToString(CultureInfo.InvariantCulture));
        // The fact and normalizer derivation that name the row the locator found; its raw identity is the first fact below.
        ConsoleUi.Field("Normalized fact", string.Create(CultureInfo.InvariantCulture,
            $"fact {detail.ObservationId.FactKey} · observation-v{detail.ObservationId.NormalizerContractVersion.Value}"));
        if (detail.JournalName is { } journal)
        {
            ConsoleUi.Field("Journal", journal);
        }

        foreach ((string label, string value) in RawRecordText.Facts(detail))
        {
            ConsoleUi.Field(label, value);
        }

        if (!detail.Available)
        {
            ConsoleUi.Warn(detail.UnavailableReason!);
            return InterCatExitCode.PartialResultSuccess;
        }

        foreach (RawExtendedItemSummary item in detail.ExtendedItems)
            ConsoleUi.Line("  " + RawRecordText.ExtendedItem(item));
        if (detail.BodyPreview is { } bytes)
        {
            ConsoleUi.Line("  Retained body preview · inert hexadecimal:");
            foreach (string line in RawRecordText.PreviewRows(bytes))
                ConsoleUi.Line("  " + line);
            if (detail.BodyPreviewTruncated) ConsoleUi.Note(RawRecordText.PreviewStopped);
        }
        else if (detail.RetainedBodyLength > 0)
            ConsoleUi.Note("Body bytes are hidden. Add --reveal-bytes to print at most 256 inert hex bytes.");
        else if (!synthetic)
            ConsoleUi.Note(RawRecordText.NoBody);
        ConsoleUi.Note(synthetic
            ? RawRecordText.SyntheticDisclosure + " (redacted-session-v1)"
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
