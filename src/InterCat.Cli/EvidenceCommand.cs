using System.Globalization;
using System.Text.Json;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>Read-only pages of exact normalized source observations, continued by row rather than by position.</summary>
internal static class EvidenceCommand
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

        // Options are taken before the positional directory, so a value such as a channel key is never mistaken
        // for the directory when options come first.
        string? channel = command.TakeOption("--channel");
        var ownerTexts = new List<string>();
        while (command.TakeOption("--owner-process") is { } ownerText) ownerTexts.Add(ownerText);
        string? cursor = command.TakeOption("--cursor");
        string? intervalText = command.TakeOption("--interval");
        string? size = command.TakeOption("--page-size");
        bool json = command.TryTakeFlag("--json");
        string? directory = command.TakePositional();
        int pageSize = SessionEvidenceQuery.DefaultPageSize;
        bool invalidSize = size is not null &&
            (!int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize)
                || pageSize is < 1 or > SessionEvidenceQuery.MaximumPageSize);
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        bool invalidInterval = !TryParseInterval(intervalText, out TimeRange? interval);
        bool invalidOwner = ownerTexts.Any(text => !Guid.TryParse(text, out Guid ownerId) || ownerId == Guid.Empty);
        if (directory is null || hasUnknown || invalidSize || invalidInterval || invalidOwner)
        {
            ConsoleUi.Failure(directory is null ? "A session directory is required: icat evidence <directory>."
                : hasUnknown ? $"Unknown or incomplete option: {unknown}"
                : invalidSize ? "--page-size must be an integer from 1 to 200."
                : invalidOwner ? "--owner-process must be a process-instance GUID from the overview."
                : "--interval must be start:end in 100-nanosecond session-relative ticks, with end > start.");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        string path = Path.GetFullPath(directory);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        ProcessInstanceId[] owners = [.. ownerTexts.Select(text => new ProcessInstanceId(Guid.Parse(text)))];
        SessionEvidencePage page;
        try
        {
            page = SessionEvidenceQuery.Read(SessionStore.OpenExisting(LocalOwnedDirectory.Open(path)),
                channel, interval, pageSize: pageSize, cursor: cursor,
                ownerProcesses: owners.Length == 0 ? null : owners, resolveOwners: true,
                cancellationToken: cancellationToken);
        }
        catch (ArgumentException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.InvalidInvocation;
        }
        catch (InvalidOperationException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                Contract = "evidence-page-v2", SessionPath = path, Page = page,
            }, JsonContracts.Indented));
            return page.RestartRequired ? InterCatExitCode.PartialResultSuccess : InterCatExitCode.Success;
        }

        ConsoleUi.Heading("Published source observations");
        ConsoleUi.Field("Session", path);
        ConsoleUi.Field("Session ID", page.SessionId.ToString("N"));
        ConsoleUi.Field("Generation", ConsoleUi.Count(page.Generation));
        ConsoleUi.Field("Rows on page", ConsoleUi.Count(page.Records.Count));
        if (channel is not null) ConsoleUi.Field("Paired TCP channel", channel);
        foreach (ProcessInstanceId owner in page.OwnerProcesses)
            ConsoleUi.Field("Canonical owner process", owner.ToString()!);
        if (interval is { } range)
            ConsoleUi.Field("Session-time interval", $"[{range.StartTicks}, {range.EndTicks}) · 100 ns ticks");
        if (page.RestartRequired)
        {
            ConsoleUi.Warn(page.RestartReason!);
            return InterCatExitCode.PartialResultSuccess;
        }

        if (page.ContinuedFromGeneration is { } earlier)
            ConsoleUi.Note($"Continued from generation {ConsoleUi.Count(earlier)} after its last row. Rows published "
                + "since then that sort before this page are not inserted; start again without --cursor to include them.");
        foreach (SessionEvidenceRecord record in page.Records)
        {
            ObservationRowV1 row = record.Observation;
            ConsoleUi.Line("  " + EvidenceRowText.Summary(record, CultureInfo.CurrentCulture));
            ConsoleUi.Note($"    {EvidenceRowText.Owner(record, CultureInfo.CurrentCulture)} · {row.ProviderId:N} event {row.EventId} "
                + $"v{row.DescriptorVersion} · raw {row.RawStreamId}/{row.RawSourceEpoch}/{row.RawRecordOrdinal} "
                + $"· {record.SegmentName} row {record.SegmentRow.ToString(CultureInfo.InvariantCulture)}");
        }

        ConsoleUi.Note(page.Caveat);
        if (page.Records.Count > 0)
            ConsoleUi.Note("To verify one original retained journal record, run icat raw <directory> "
                + $"--session-id {page.SessionId:N} --generation {page.Generation} "
                + "--segment <segment-name> --row <segment-row> using the locator printed above. "
                + "Body bytes stay hidden unless --reveal-bytes is supplied.");
        if (page.NextCursor is not null)
            ConsoleUi.Note($"Next page: icat evidence <directory> --cursor {page.NextCursor}"
                + (channel is null ? string.Empty : $" --channel {channel}")
                + string.Concat(page.OwnerProcesses.Select(owner => $" --owner-process {owner}"))
                + (interval is { } scope ? $" --interval {scope.StartTicks}:{scope.EndTicks}" : string.Empty));
        return InterCatExitCode.Success;
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat evidence <session-directory> [--channel <paired-tcp-key>] [--owner-process <instance-guid> ...]");
        ConsoleUi.Line("              [--interval <start:end>]");
        ConsoleUi.Line("              [--page-size <1-200>]");
        ConsoleUi.Line("              [--cursor <token>] [--json]");
        ConsoleUi.Line("  Read-only pages of admitted normalized source rows in native-reading order.");
        ConsoleUi.Line("  A cursor continues after its last row, in a newer generation too; a changed scope, policy");
        ConsoleUi.Line("  or derivation asks for an explicit restart. --channel uses an overview channel key.");
        ConsoleUi.Line("  --owner-process selects rows canonically owned by that instance, not possible peer rows;");
        ConsoleUi.Line("  repeat it to select a group's instances together.");
        ConsoleUi.Line("  --interval is a half-open range in 100-nanosecond session-relative presentation ticks.");
        ConsoleUi.Line("  This is not a logical-operation pairing or a raw payload export.");
    }

    private static bool TryParseInterval(string? raw, out TimeRange? interval)
    {
        interval = null;
        if (raw is null) return true;
        string[] parts = raw.Split(':');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long start)
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long end)
            || end <= start) return false;
        interval = new TimeRange(start, end);
        return true;
    }
}
