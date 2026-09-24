using System.Globalization;
using System.Text.Json;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>Read-only, manifest-bound pages of exact normalized source observations.</summary>
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

        string? directory = command.TakePositional();
        string? channel = command.TakeOption("--channel");
        string? ownerProcessText = command.TakeOption("--owner-process");
        string? cursor = command.TakeOption("--cursor");
        string? intervalText = command.TakeOption("--interval");
        string? size = command.TakeOption("--page-size");
        int pageSize = SessionEvidenceQuery.DefaultPageSize;
        bool invalidSize = size is not null &&
            (!int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize)
                || pageSize is < 1 or > SessionEvidenceQuery.MaximumPageSize);
        bool json = command.TryTakeFlag("--json");
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        bool invalidInterval = !TryParseInterval(intervalText, out TimeRange? interval);
        bool invalidOwner = ownerProcessText is not null
            && (!Guid.TryParse(ownerProcessText, out Guid ownerId) || ownerId == Guid.Empty);
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

        ProcessInstanceId? ownerScope = ownerProcessText is null ? null : new(Guid.Parse(ownerProcessText));
        SessionEvidencePage page;
        try
        {
            page = SessionEvidenceQuery.Read(SessionStore.OpenExisting(LocalOwnedDirectory.Open(path)),
                channel, interval, ownerScope, pageSize: pageSize, cursor: cursor,
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
                Contract = "evidence-page-v1", SessionPath = path, Page = page,
            }, JsonContracts.Indented));
            return page.RestartRequired ? InterCatExitCode.PartialResultSuccess : InterCatExitCode.Success;
        }

        ConsoleUi.Heading("Published source observations");
        ConsoleUi.Field("Session", path);
        ConsoleUi.Field("Generation", ConsoleUi.Count(page.Generation));
        ConsoleUi.Field("Rows on page", ConsoleUi.Count(page.Records.Count));
        if (channel is not null) ConsoleUi.Field("Paired TCP channel", channel);
        if (ownerScope is not null) ConsoleUi.Field("Canonical owner process", ownerScope.ToString()!);
        if (interval is { } range)
            ConsoleUi.Field("Session-time interval", $"[{range.StartTicks}, {range.EndTicks}) · 100 ns ticks");
        if (page.RestartRequired)
        {
            ConsoleUi.Warn(page.RestartReason!);
            return InterCatExitCode.PartialResultSuccess;
        }

        foreach (SessionEvidenceRecord record in page.Records)
        {
            ObservationRowV1 row = record.Observation;
            ConsoleUi.Line($"  {row.NativeTicks.ToString(CultureInfo.InvariantCulture)} · "
                + $"{row.Mechanism}/{row.Kind} · owner PID {row.OwnerProcessId?.ToString(CultureInfo.InvariantCulture) ?? "?"} "
                + $"· {row.ProviderId:N} event {row.EventId} v{row.DescriptorVersion} "
                + $"· raw {row.RawStreamId}/{row.RawSourceEpoch}/{row.RawRecordOrdinal} "
                + $"fact {row.FactKey} · {record.SegmentName} row {record.SegmentRow.ToString(CultureInfo.InvariantCulture)}");
        }

        ConsoleUi.Note(page.Caveat);
        if (page.NextCursor is not null)
            ConsoleUi.Note($"Next page: icat evidence <directory> --cursor {page.NextCursor}"
                + (channel is null ? string.Empty : $" --channel {channel}")
                + (ownerScope is null ? string.Empty : $" --owner-process {ownerScope}")
                + (interval is { } scope ? $" --interval {scope.StartTicks}:{scope.EndTicks}" : string.Empty));
        return InterCatExitCode.Success;
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat evidence <session-directory> [--channel <paired-tcp-key>] [--owner-process <instance-guid>]");
        ConsoleUi.Line("              [--interval <start:end>]");
        ConsoleUi.Line("              [--page-size <1-200>]");
        ConsoleUi.Line("              [--cursor <token>] [--json]");
        ConsoleUi.Line("  Read-only pages of admitted normalized source rows, tied to one manifest and query.");
        ConsoleUi.Line("  A stale cursor requests an explicit restart; --channel uses an overview channel key.");
        ConsoleUi.Line("  --owner-process selects rows canonically owned by that instance, not possible peer rows.");
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
