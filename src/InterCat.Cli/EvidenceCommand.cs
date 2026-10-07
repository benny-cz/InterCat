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
        string? mechanismText = command.TakeOption("--mechanism");
        string? directionText = command.TakeOption("--direction");
        string? endText = command.TakeOption("--end");
        bool json = command.TryTakeFlag("--json");
        string? directory = command.TakePositional();
        int pageSize = SessionEvidenceQuery.DefaultPageSize;
        bool invalidSize = size is not null &&
            (!int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize)
                || pageSize is < 1 or > SessionEvidenceQuery.MaximumPageSize);
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        TimeRange? interval = TickInterval.Read(intervalText, out string? tooWide);
        bool invalidInterval = intervalText is not null && interval is null;
        bool invalidOwner = ownerTexts.Any(text => !Guid.TryParse(text, out Guid ownerId) || ownerId == Guid.Empty);

        // A timeline lane's records, as the window's E lists a chosen cell's (§6.4): one mechanism's, one source
        // direction's, or those made at one end of a paired channel.
        bool mechanismRead = MetricCommand.TryParseOptional(mechanismText, "--mechanism", out Mechanism? mechanism, out string? mechanismProblem);
        bool directionRead = MetricCommand.TryParseOptional(directionText, "--direction", out Direction? direction, out string? directionProblem);
        string? narrowingProblem = !mechanismRead ? mechanismProblem
            : !directionRead ? directionProblem
            : endText is not (null or "0" or "1") ? "--end takes 0, a paired channel's first end, or 1, its second."
            : null;
        int? end = endText is null ? null : endText == "0" ? 0 : 1;
        bool invalidNarrowing = narrowingProblem is not null;
        if (directory is null || hasUnknown || invalidSize || invalidInterval || invalidOwner || invalidNarrowing)
        {
            ConsoleUi.Failure(directory is null ? "A session directory is required: icat evidence <directory>."
                : hasUnknown ? CommandLine.Unknown(unknown!)
                : invalidSize ? "--page-size must be an integer from 1 to 200."
                : invalidOwner ? "--owner-process must be a process-instance GUID from the overview."
                : invalidNarrowing ? narrowingProblem!
                : tooWide ?? "--interval must be start:end in 100-nanosecond session-relative ticks, with end > start.");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        string path = Path.GetFullPath(directory);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        ProcessInstanceId[] owners = [.. ownerTexts.Select(text => new ProcessInstanceId(Guid.Parse(text)))];
        // A relationship's evidence key opens its records at both ends (R4): a paired TCP channel's, or an RPC
        // relationship's, channel's or call's, whose records the RPC calls name.
        bool rpc = RpcChannelKeys.IsRpc(channel);
        SessionEvidencePage page;
        try
        {
            page = SessionEvidenceQuery.Read(SessionStore.OpenExisting(LocalOwnedDirectory.Open(path)),
                rpc ? null : channel, interval, pageSize: pageSize, cursor: cursor,
                ownerProcesses: owners.Length == 0 ? null : owners, resolveOwners: true,
                operationKey: rpc ? channel : null, mechanism: mechanism, direction: direction, end: end,
                cancellationToken: cancellationToken);
        }
        catch (ArgumentException exception)
        {
            ConsoleUi.Failure(exception);
            return InterCatExitCode.InvalidInvocation;
        }
        catch (InvalidOperationException exception) when (exception is not NoSessionException)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                Contract = "evidence-page-v4", SessionPath = path, Page = page,
            }, JsonContracts.Indented));
            return page.RestartRequired ? InterCatExitCode.PartialResultSuccess : InterCatExitCode.Success;
        }

        ConsoleUi.Heading("Published source observations");
        ConsoleUi.Field("Session", path);
        ConsoleUi.Field("Session ID", page.SessionId.ToString("N"));
        ConsoleUi.Field("Generation", ConsoleUi.Count(page.Generation));
        ConsoleUi.Field("Rows on page", ConsoleUi.Count(page.Records.Count));
        if (channel is not null) ConsoleUi.Field(rpc ? "RPC key" : "Paired TCP channel", channel);
        if (page.End is { } side) ConsoleUi.Field("Channel end", side == 0 ? "0, its first end" : "1, its second end");
        if (page.Mechanism is { } kind) ConsoleUi.Field("Mechanism", MechanismText.Name(kind));
        if (page.Direction is { } way) ConsoleUi.Field("Source direction", ObservationText.DirectionOf(way));
        foreach (ProcessInstanceId owner in page.OwnerProcesses)
            ConsoleUi.Field("Canonical owner process", owner.ToString()!);
        if (interval is { } range)
            ConsoleUi.Field("Session-time interval", $"[{range.StartTicks}, {range.EndTicks}) · 100 ns ticks");
        if (page.RestartRequired)
        {
            ConsoleUi.Warn(page.RestartReason!);
            return InterCatExitCode.PartialResultSuccess;
        }

        // What the capture covered over the page's time scope, in the inspector's words: a page that lists no record is
        // not a quiet scope unless the capture covered it (R21).
        if (CoverageText.Describe(page.Coverage) is { Length: > 0 } coverage)
            ConsoleUi.Note(coverage);

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
                + (interval is { } scope ? $" --interval {scope.StartTicks}:{scope.EndTicks}" : string.Empty)
                + (page.Mechanism is { } named ? $" --mechanism {Kebab(named.ToString())}" : string.Empty)
                + (page.Direction is { } marked ? $" --direction {Kebab(marked.ToString())}" : string.Empty)
                + (page.End is { } narrowed ? string.Create(CultureInfo.InvariantCulture, $" --end {narrowed}") : string.Empty));
        return InterCatExitCode.Success;
    }

    /// <summary>An enumeration's name as a person types it: "ProcessLifecycle" as "process-lifecycle".</summary>
    private static string Kebab(string name) => string.Concat(name.Select((character, index) =>
        char.IsUpper(character) && index > 0 ? "-" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat evidence <session-directory> [--channel <key>] [--owner-process <instance-guid> ...]");
        ConsoleUi.Line("              [--interval <start:end>] [--mechanism <name>] [--direction <name>] [--end <0|1>]");
        ConsoleUi.Line("              [--page-size <1-200>]");
        ConsoleUi.Line("              [--cursor <token>] [--json]");
        ConsoleUi.Line("  Read-only pages of admitted normalized source rows in native-reading order.");
        ConsoleUi.Line("  A cursor continues after its last row, in a newer generation too; a changed scope, policy");
        ConsoleUi.Line("  or derivation asks for an explicit restart. --channel takes a relationship's evidence key: a");
        ConsoleUi.Line("  paired TCP channel's, or an RPC relationship's, which opens the calls its links join at both");
        ConsoleUi.Line("  ends; an RPC channel's or call's key opens its own records, and no RPC key takes --owner-process.");
        ConsoleUi.Line("  --owner-process selects rows canonically owned by that instance, not possible peer rows;");
        ConsoleUi.Line("  repeat it to select a group's instances together.");
        ConsoleUi.Line("  --interval is a half-open range in 100-nanosecond session-relative presentation ticks.");
        ConsoleUi.Line("  --mechanism and --direction keep one mechanism's or one source direction's rows, and --end the");
        ConsoleUi.Line("  rows made at one end of a paired --channel: a timeline lane's records, as the window lists a");
        ConsoleUi.Line("  chosen cell's.");

        ConsoleUi.Line("  Each page states what the capture covered over its time scope, mechanism by mechanism, so a");
        ConsoleUi.Line("  page without a row is not read as a quiet scope.");
        ConsoleUi.Line("  This is not a logical-operation pairing or a raw payload export.");
    }
}
